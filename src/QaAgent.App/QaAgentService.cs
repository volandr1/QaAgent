using QaAgent.Core;
using QaAgent.Execution;
using QaAgent.Generation;
using QaAgent.Healing;
using QaAgent.Llm;
using QaAgent.Probing;
using QaAgent.Reporting;
using QaAgent.Swagger;

namespace QaAgent.App;

/// <summary>
/// Єдина оркестрація агента для конкретного таргета (API). Використовується CLI і веб.
/// Усі довгі операції приймають логер (Action&lt;string&gt;) для живого виводу.
/// </summary>
public sealed class QaAgentService
{
    private readonly WorkspacePaths _paths;
    private readonly TargetConfig _target;
    private readonly OllamaOptions _ollama;

    public string Name => _target.Name;
    public string SwaggerUrl => _target.SwaggerUrl;
    public string BaseUrl => _target.BaseUrl;

    private string Snapshot => _paths.SnapshotFor(Name);
    private string GeneratedDir => _paths.GeneratedFor(Name);
    private string ResultsDir => _paths.TestResultsFor(Name);
    private string Reports => _paths.ReportsFor(Name);
    private string Namespace => _paths.TestNamespace(Name);
    // Крапка в кінці — точна межа неймспейсу (щоб "Petstore" не матчив "Petstore3_swagger_io").
    private string Filter => $"FullyQualifiedName~{Namespace}.";

    public QaAgentService(WorkspacePaths paths, TargetConfig target, OllamaOptions? ollama = null)
    {
        _paths = paths;
        _target = target;
        _ollama = ollama ?? new OllamaOptions();
    }

    private static Action<string> Safe(Action<string>? log) => log ?? (_ => { });

    // Базовий URL для запитів = servers[] зі схеми (напр. /api/v3), інакше host зі Swagger URL.
    private string EffectiveBaseUrl(ApiSpec spec) =>
        string.IsNullOrWhiteSpace(spec.ServerUrl) ? BaseUrl : spec.ServerUrl!;

    // Узагальнене правило positive: ідемпотентний анонімний GET без path-параметрів.
    private static bool IsSafePositiveGet(EndpointSpec ep) =>
        ep.Method == "GET" && ep.Auth == AuthRequirement.Anonymous &&
        ep.Parameters.All(p => p.In != ParamLocation.Path);

    public async Task<ApiSpec> LoadCurrentAsync(CancellationToken ct = default) =>
        await new SwaggerParser().LoadFromUrlAsync(SwaggerUrl, ct);

    public Task<ApiSpec?> LoadSnapshotAsync(CancellationToken ct = default) =>
        new SnapshotStore(Snapshot).LoadAsync(ct);

    public async Task<(ApiSpec Current, ApiDiff Diff)> AnalyzeAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        var logf = Safe(log);
        var current = await LoadCurrentAsync(ct);
        logf($"[{Name}] {current.Title} v{current.Version} — {current.Endpoints.Count} ендпоінтів, {current.Schemas.Count} схем");

        var previous = await LoadSnapshotAsync(ct);
        var diff = DiffEngine.Diff(previous, current);
        if (previous is null) logf("Baseline (знімка немає).");
        else if (!diff.HasChanges) logf("Змін немає.");
        else logf($"Змін: +{diff.AddedEndpoints.Count} -{diff.RemovedEndpoints.Count} ~{diff.ChangedEndpoints.Count}, перейменувань {diff.Renames.Count}.");
        return (current, diff);
    }

    public async Task<ApiSpec> ProbeAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        var logf = Safe(log);
        var api = await LoadCurrentAsync(ct);

        if (_target.ProbeAuth)
        {
            logf("Auth-probing (реєстрація користувачів + зондування)...");
            await new AuthProber(EffectiveBaseUrl(api), _target.Auth).ProbeAsync(api, ct);
            foreach (var ep in api.Endpoints.OrderBy(e => e.Path))
                logf($"  {ep.Method,-6} {ep.Path,-28} → {ep.Auth}");
        }
        else
        {
            logf("API без авторизації — probing пропущено (усі ендпоінти Anonymous).");
            foreach (var ep in api.Endpoints) ep.Auth = AuthRequirement.Anonymous;
        }

        await new SnapshotStore(Snapshot).SaveAsync(api, ct);
        logf("Знімок збережено.");
        return api;
    }

    public async Task<(int Files, int Tests)> GenerateAsync(ApiSpec api, bool overwrite, Action<string>? log = null,
        CancellationToken ct = default, IReadOnlyCollection<string>? skipSignatures = null)
    {
        var logf = Safe(log);
        var generator = new TestGenerator(new LlmClient(_ollama));
        var renderer = new TestRenderer();
        int files = 0, total = 0;

        // Повна генерація = чистий аркуш (прибираємо застарілі/перейменовані файли таргета).
        if (overwrite && Directory.Exists(GeneratedDir))
            foreach (var old in Directory.GetFiles(GeneratedDir, "*.cs"))
                File.Delete(old);

        foreach (var ep in api.Endpoints)
        {
            // Перейменовані ендпоінти не генеруємо заново — їх лагодить self-heal (старий файл).
            if (skipSignatures is not null && skipSignatures.Contains(ep.Signature)) continue;

            var file = Path.Combine(GeneratedDir, renderer.ClassName(ep) + ".cs");
            if (!overwrite && File.Exists(file)) continue;

            var scenarios = new List<TestScenario>();

            if (IsSafePositiveGet(ep))
                scenarios.AddRange(await SafeGen(() => generator.GeneratePositiveAsync(ep, api, "none", ct), ep, "positive", logf));

            // Створення покриває CRUD round-trip (нижче), а не окремий positive-create —
            // щоб не плутати дії-ендпоінти (login/logout) зі створенням ресурсу.

            // Negative — на правилах зі схеми (детерміновано, без LLM-галюцинацій).
            if (_target.GenerateNegatives && ep.RequestBody is not null && ep.Auth == AuthRequirement.Anonymous)
                scenarios.AddRange(ScenarioBuilder.NegativeScenarios(ep, api));

            scenarios.AddRange(ScenarioBuilder.AdminPositiveScenarios(ep));
            scenarios.AddRange(ScenarioBuilder.AuthScenarios(ep));
            scenarios.AddRange(ScenarioBuilder.BoundaryScenarios(ep));
            if (_target.CoverWrites) scenarios.AddRange(ScenarioBuilder.WriteSmokeScenarios(ep));

            if (scenarios.Count == 0) continue;

            await File.WriteAllTextAsync(file, renderer.Render(ep, scenarios, Namespace), ct);
            logf($"• {ep.Signature,-34} → {string.Join(", ", scenarios.GroupBy(s => s.Type).Select(g => $"{g.Count()} {g.Key}"))}");
            files++;
            total += scenarios.Count;
        }

        // Auth-ендпоінти (login/register) — це ДІЇ, а не ресурси: окреме покриття
        // (positive: register+login → 2xx + токен; negative: невірний пароль/юзер → 401).
        // Лише коли контракт авторизації відомий (ProbeAuth + AuthConfig).
        if (_target is { ProbeAuth: true, Auth: not null })
        {
            var ac = _target.Auth;
            static string Norm(string p) => "/" + p.Trim('/');
            var loginEp = api.Endpoints.FirstOrDefault(e =>
                e.Method == "POST" && Norm(e.Path).Equals(Norm(ac.LoginPath), StringComparison.OrdinalIgnoreCase));

            if (loginEp is not null)
            {
                var file = Path.Combine(GeneratedDir, "Auth_Tests.cs");
                if (overwrite || !File.Exists(file))
                {
                    var hasRegister = api.Endpoints.Any(e =>
                        e.Method == "POST" && Norm(e.Path).Equals(Norm(ac.RegisterPath), StringComparison.OrdinalIgnoreCase));
                    var code = renderer.RenderAuthTests(Namespace,
                        Norm(ac.RegisterPath).TrimStart('/'), Norm(ac.LoginPath).TrimStart('/'),
                        ac.EmailField, ac.PasswordField, ac.TokenField, ac.Password, hasRegister);
                    await File.WriteAllTextAsync(file, code, ct);
                    var n = hasRegister ? 4 : 2;
                    logf($"• auth login/register → {n} тест(ів)");
                    files++; total += n;
                }
            }
        }

        // CRUD round-trip (стейтовий): для ресурсів POST + GET/{id} (+DELETE/{id}).
        if (_target.CoverWrites)
        {
            foreach (var createEp in api.Endpoints.Where(e =>
                e.Method == "POST" && e.RequestBody is not null && e.Parameters.All(p => p.In != ParamLocation.Path)))
            {
                var basePath = createEp.Path;
                var byId = api.Endpoints.FirstOrDefault(e => e.Method == "GET" && IsByIdOf(e.Path, basePath));
                if (byId is null) continue;

                var file = Path.Combine(GeneratedDir, $"{LastSegment(basePath)}_RoundTrip_Tests.cs");
                if (!overwrite && File.Exists(file)) continue;

                var deleteEp = api.Endpoints.FirstOrDefault(e => e.Method == "DELETE" && IsByIdOf(e.Path, basePath));
                var updateEp = api.Endpoints.FirstOrDefault(e => e.Method == "PUT" && IsByIdOf(e.Path, basePath));
                var body = SchemaSkeleton.Build(createEp.RequestBody!.Schema, api);

                var (updateBody, marker) = BuildUpdateBody(updateEp ?? createEp, createEp, api);

                var code = renderer.RenderRoundTrip(Namespace, LastSegment(basePath),
                    basePath.TrimStart('/'), ToInterpolatedById(byId), body, deleteEp is not null,
                    TokenFor(createEp.Auth), TokenFor(byId.Auth), TokenFor(deleteEp?.Auth ?? AuthRequirement.Anonymous),
                    hasUpdate: updateEp is not null, updateBody: updateBody,
                    updateAuth: TokenFor((updateEp ?? createEp).Auth), marker: marker);
                await File.WriteAllTextAsync(file, code, ct);
                logf($"• round-trip {LastSegment(basePath)}");
                files++; total++;

                // IDOR/BOLA: ресурс створюється під авторизацією (має власника) і читання за id
                // вимагає авторизації (owner-scoped). Публічні (anon) і суто рольові (Admin) — пропускаємо.
                if (createEp.Auth != AuthRequirement.Anonymous && byId.Auth == AuthRequirement.Authenticated)
                {
                    var idorFile = Path.Combine(GeneratedDir, $"{LastSegment(basePath)}_Security_Tests.cs");
                    if (overwrite || !File.Exists(idorFile))
                    {
                        var idorCode = renderer.RenderIdor(Namespace, LastSegment(basePath),
                            basePath.TrimStart('/'), ToInterpolatedById(byId), body, TokenFor(createEp.Auth));
                        await File.WriteAllTextAsync(idorFile, idorCode, ct);
                        logf($"• security(IDOR) {LastSegment(basePath)}");
                        files++; total++;
                    }
                }
            }
        }

        logf($"Згенеровано {total} тест(ів) у {files} файлах.");
        return (files, total);
    }

    /// <summary>Видаляє файли тестів для ВИДАЛЕНИХ ендпоінтів (перейменування сюди не входять — їх лікує heal).</summary>
    private int RemoveObsoleteTests(ApiDiff diff, ApiSpec previous)
    {
        var renderer = new TestRenderer();
        var removed = 0;
        foreach (var sig in diff.RemovedEndpoints)
        {
            var ep = previous.Endpoints.FirstOrDefault(e => e.Signature == sig);
            if (ep is null) continue;
            var file = Path.Combine(GeneratedDir, renderer.ClassName(ep) + ".cs");
            if (File.Exists(file)) { File.Delete(file); removed++; }
        }
        return removed;
    }

    private static bool IsByIdOf(string path, string basePath) =>
        path.StartsWith(basePath + "/{", StringComparison.OrdinalIgnoreCase) &&
        path.EndsWith("}") &&
        path.Count(c => c == '/') == basePath.Count(c => c == '/') + 1;

    private static SchemaSpec ResolveSchema(SchemaSpec s, ApiSpec api) =>
        s.Reference is { } r && api.Schemas.TryGetValue(r, out var t) ? t : s;

    /// <summary>Будує тіло для PUT: скелет із одним рядковим полем, зміненим на маркер (для перевірки, що оновлення збереглося).</summary>
    private static (string Body, string? Marker) BuildUpdateBody(EndpointSpec bodyEp, EndpointSpec fallback, ApiSpec api)
    {
        var bodySchema = bodyEp.RequestBody?.Schema ?? fallback.RequestBody!.Schema;
        var obj = SchemaSkeleton.BuildObject(bodySchema, api);
        if (obj is null) return (SchemaSkeleton.Build(bodySchema, api), null);

        var schema = ResolveSchema(bodySchema, api);
        var field = schema.Properties.FirstOrDefault(p =>
        {
            var t = ResolveSchema(p.Value, api);
            return (t.Type == "string" || t.Type is null) && t.Format is null && obj.TryGetValue(p.Key, out var v) && v is string;
        });

        string? marker = null;
        if (field.Key is not null) { obj[field.Key] = "QaAgentUpdated"; marker = "QaAgentUpdated"; }

        return (System.Text.Json.JsonSerializer.Serialize(obj), marker);
    }

    private static string TokenFor(AuthRequirement auth) => auth switch
    {
        AuthRequirement.Admin => "admin",
        AuthRequirement.Authenticated => "client",
        _ => "none"
    };

    private static string LastSegment(string path) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "Resource";

    private static string ToInterpolatedById(EndpointSpec byIdEp)
    {
        var path = byIdEp.Path.TrimStart('/');
        var p = byIdEp.Parameters.FirstOrDefault(x => x.In == ParamLocation.Path);
        return p is null ? path : path.Replace("{" + p.Name + "}", "{id}");
    }

    public async Task<TestRun> RunAsync(Action<string>? log = null, string? baseUrl = null, CancellationToken ct = default)
    {
        var logf = Safe(log);
        var url = baseUrl ?? (await LoadSnapshotAsync(ct))?.ServerUrl ?? BaseUrl;
        logf($"dotnet test (база: {url})...");
        var output = await new TestRunner().RunAsync(_paths.TestProject, ResultsDir, url, Filter, ct);
        var run = output.Run;
        logf($"{run.Passed}/{run.Total} passed, {run.Failed} failed ({run.Duration.TotalSeconds:F1}s)");
        foreach (var f in run.Failures) logf($"  ❌ {f.Name}: {f.ErrorMessage?.Trim().Replace("\n", " ")}");
        return run;
    }

    public async Task<HealReport> HealAsync(ApiDiff diff, ApiSpec current, Action<string>? log = null, CancellationToken ct = default)
    {
        var logf = Safe(log);
        var healer = new SelfHealer(new LlmClient(_ollama), new TestRunner(),
            _paths.TestProject, GeneratedDir, ResultsDir, EffectiveBaseUrl(current), Filter);
        var report = await healer.HealAsync(diff, current, ct: ct);
        if (report.StartedGreen) logf("Усе зелене — лікувати нічого.");
        else if (report.FinalGreen) logf($"🩹 Полагоджено: {string.Join(", ", report.HealedFiles)}");
        else logf($"⚠️ Залишились падіння: {report.UnresolvedFailures.Count}");
        return report;
    }

    public async Task<RunReport> FullCycleAsync(Action<string>? log = null, CancellationToken ct = default,
        bool autoHeal = true, bool notifyTelegram = true)
    {
        var logf = Safe(log);
        var store = new SnapshotStore(Snapshot);
        var previous = await store.LoadAsync(ct);
        var current = await LoadCurrentAsync(ct);
        var diff = DiffEngine.Diff(previous, current);

        logf(previous is null ? $"1) [{Name}] Baseline." : diff.HasChanges ? $"1) [{Name}] Виявлено зміни API." : $"1) [{Name}] Змін API немає.");

        logf("2) Auth...");
        if (_target.ProbeAuth)
        {
            try { await new AuthProber(EffectiveBaseUrl(current), _target.Auth).ProbeAsync(current, ct); }
            catch (Exception ex) { logf($"   ⚠️ probing: {ex.Message}"); }
        }
        else
        {
            foreach (var ep in current.Endpoints) ep.Auth = AuthRequirement.Anonymous;
            logf("   API без авторизації — пропущено.");
        }

        if (previous is null)
        {
            logf("3) Генерація тестів (baseline)...");
            var (files, tests) = await GenerateAsync(current, overwrite: true, log: null, ct);
            logf($"   baseline: {files} файлів / {tests} тестів");
        }
        else
        {
            logf("3) Адаптація тестів (зберігаю наявні для self-heal)...");
            var removed = RemoveObsoleteTests(diff, previous);
            var renameTargets = diff.Renames.Select(r => r.To).ToHashSet();
            var (files, tests) = await GenerateAsync(current, overwrite: false, log: null, ct, skipSignatures: renameTargets);
            logf($"   нових файлів: {files}, прибрано застарілих: {removed}");
        }

        var doHeal = autoHeal && diff.HasChanges && previous is not null;
        logf("4) Прогін" + (doHeal ? " + self-healing..." : "..."));
        TestRun finalRun;
        HealReport? heal = null;
        if (doHeal)
        {
            heal = await HealAsync(diff, current, logf, ct);
            finalRun = heal.FinalRun!;
        }
        else
        {
            finalRun = await RunAsync(logf, EffectiveBaseUrl(current), ct);
        }

        // На baseline не засмічуємо звіт «усі ендпоінти додані».
        var reportDiff = previous is null ? new ApiDiff() : diff;
        var report = BuildReport(current, reportDiff, finalRun, heal);
        report.Coverage = CoverageAnalyzer.Compute(current, GeneratedDir, _target);

        logf("5) AI-аналіз результатів...");
        try
        {
            report.AiAnalysis = await new AiAnalyzer(new LlmClient(_ollama)).AnalyzeAsync(report, ct);
            logf("   " + report.AiAnalysis.Replace("\n", "\n   "));
        }
        catch (Exception ex) { logf($"   ⚠️ AI-аналіз недоступний: {ex.Message}"); }

        logf("6) Звіт...");
        foreach (var l in await new ReportDispatcher(Reports).DispatchAsync(report, ct, notifyTelegram)) logf(l);

        await store.SaveAsync(current, ct);
        return report;
    }

    public static RunReport BuildReport(ApiSpec api, ApiDiff diff, TestRun run, HealReport? heal)
    {
        var report = new RunReport
        {
            ApiTitle = api.Title,
            ApiVersion = api.Version,
            TestRun = run,
            Success = run.Success,
            SelfHealingApplied = heal is { StartedGreen: false },
            HealedFiles = heal?.HealedFiles ?? new()
        };

        foreach (var r in diff.Renames) report.SchemaChanges.Add($"перейменовано: {r.From} → {r.To}");
        report.SchemaChanges.AddRange(diff.AddedEndpoints.Select(s => $"+ {s}"));
        report.SchemaChanges.AddRange(diff.RemovedEndpoints.Select(s => $"- {s}"));
        report.SchemaChanges.AddRange(diff.ChangedEndpoints.Select(c => $"~ {c.Signature}: {string.Join(", ", c.Changes)}"));
        report.SchemaChanges.AddRange(diff.SchemaChanges.Select(s => $"schema {s}"));

        var anon = api.Endpoints.Count(e => e.Auth == AuthRequirement.Anonymous);
        if (anon > 0 && anon < api.Endpoints.Count)
            report.Findings.Add(new ReportFinding
            {
                Severity = FindingSeverity.Warning,
                Title = "Документація auth вводить в оману",
                Detail = $"Swagger декларує Bearer на всіх ендпоінтах, але {anon} насправді анонімні."
            });

        foreach (var f in run.Failures)
            report.Findings.Add(new ReportFinding
            {
                Severity = FindingSeverity.Bug,
                Title = f.Name,
                Detail = f.ErrorMessage?.Trim().Replace("\n", " ") ?? "тест впав"
            });

        return report;
    }

    private static async Task<List<TestScenario>> SafeGen(
        Func<Task<List<TestScenario>>> gen, EndpointSpec ep, string kind, Action<string> log)
    {
        try { return await gen(); }
        catch (Exception ex) { log($"  ⚠️ LLM {kind} {ep.Signature}: {ex.Message}"); return new(); }
    }
}
