using System.Diagnostics;
using QaAgent.Core;
using QaAgent.Execution;
using QaAgent.Generation;
using QaAgent.Healing;
using QaAgent.Llm;
using QaAgent.Probing;
using QaAgent.Reporting;
using QaAgent.Swagger;

Console.OutputEncoding = System.Text.Encoding.UTF8;

const string DefaultSwaggerUrl = "http://localhost:5234/swagger/v1/swagger.json";

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "analyze";

switch (command)
{
    case "smoke":
        await RunSmokeAsync();
        break;
    case "analyze":
        await RunAnalyzeAsync(args.Length > 1 ? args[1] : DefaultSwaggerUrl);
        break;
    case "generate":
        await RunGenerateAsync(args.Length > 1 ? args[1] : DefaultSwaggerUrl);
        break;
    case "probe":
        await RunProbeAsync(args.Length > 1 ? args[1] : DefaultSwaggerUrl);
        break;
    case "run":
        await RunTestsAsync();
        break;
    case "heal":
        await RunHealAsync(args.Length > 1 ? args[1] : DefaultSwaggerUrl);
        break;
    case "agent":
        await RunAgentAsync(args.Length > 1 ? args[1] : DefaultSwaggerUrl);
        break;
    case "seed":
        await RunSeedAsync(args.Length > 1 ? args[1] : DefaultSwaggerUrl);
        break;
    default:
        Console.WriteLine($"Невідома команда: {command}");
        Console.WriteLine("Доступні: smoke | analyze | probe | generate | run | heal | agent | seed [url]");
        break;
}

// ---------------------------------------------------------------------------

static async Task RunSmokeAsync()
{
    Console.WriteLine("=== QaAgent · smoke-тест Semantic Kernel ↔ Ollama ===\n");
    var options = new OllamaOptions();
    Console.WriteLine($"Model: {options.Model} @ {options.Endpoint}\n");

    var client = new LlmClient(options);
    var sw = Stopwatch.StartNew();
    var answer = await client.AskAsync(
        "You are a senior QA automation engineer. Be concise.",
        "Answer with a single word: PONG.");
    sw.Stop();

    Console.WriteLine($"Відповідь ({sw.ElapsedMilliseconds} ms): {answer.Trim()}");
    Console.WriteLine("✅ Зв'язок із Ollama працює.");
}

static async Task RunAnalyzeAsync(string swaggerUrl)
{
    Console.WriteLine("=== QaAgent · Етап 1 · аналіз Swagger ===\n");
    Console.WriteLine($"Джерело: {swaggerUrl}\n");

    var parser = new SwaggerParser();
    ApiSpec current;
    try
    {
        current = swaggerUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? await parser.LoadFromUrlAsync(swaggerUrl)
            : parser.LoadFromFile(swaggerUrl);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Не вдалося завантажити/розпарсити схему: {ex.Message}");
        Console.WriteLine("Підказка: переконайся, що OnlineLibrary запущено в Development.");
        Environment.ExitCode = 1;
        return;
    }

    Console.WriteLine($"API: {current.Title} v{current.Version}");
    Console.WriteLine($"Ендпоінтів: {current.Endpoints.Count}, схем: {current.Schemas.Count}");
    Console.WriteLine($"Hash: {current.Hash[..12]}…\n");

    Console.WriteLine("Ендпоінти:");
    foreach (var ep in current.Endpoints)
    {
        var pars = ep.Parameters.Count > 0
            ? " {" + string.Join(", ", ep.Parameters.Select(p => $"{p.Name}:{p.In}")) + "}"
            : "";
        var body = ep.RequestBody is { } b ? $" body={b.Schema.Reference ?? b.Schema.Type}" : "";
        var codes = string.Join("/", ep.Responses.Select(r => r.StatusCode));
        Console.WriteLine($"  {ep.Method,-6} {ep.Path,-28}{pars}{body}  →[{codes}]  auth={ep.Auth}");
    }

    // ---- snapshot + diff ----
    var store = new SnapshotStore(System.IO.Path.Combine(ArtifactsDir(), "schema-snapshot.json"));
    var previous = await store.LoadAsync();

    Console.WriteLine();
    if (previous is null)
    {
        Console.WriteLine("Попереднього знімка немає — це базова лінія (baseline).");
    }
    else if (previous.Hash == current.Hash)
    {
        Console.WriteLine("✅ Схема не змінилася з останнього запуску (hash збігається).");
    }
    else
    {
        var diff = DiffEngine.Diff(previous, current);
        Console.WriteLine("⚠️  Схема ЗМІНИЛАСЯ:");
        foreach (var s in diff.AddedEndpoints) Console.WriteLine($"   + новий ендпоінт: {s}");
        foreach (var s in diff.RemovedEndpoints) Console.WriteLine($"   - видалено ендпоінт: {s}");
        foreach (var c in diff.ChangedEndpoints)
            Console.WriteLine($"   ~ змінено {c.Signature}: {string.Join(", ", c.Changes)}");
        foreach (var s in diff.SchemaChanges) Console.WriteLine($"   schema {s}");
    }

    await store.SaveAsync(current);
    Console.WriteLine($"\nЗнімок збережено: {store.Path}");
}

static async Task RunProbeAsync(string swaggerUrl)
{
    Console.WriteLine("=== QaAgent · Етап 3 · auth-probing ===\n");

    var parser = new SwaggerParser();
    ApiSpec api;
    try
    {
        api = await parser.LoadFromUrlAsync(swaggerUrl);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Не вдалося завантажити схему: {ex.Message}");
        Environment.ExitCode = 1;
        return;
    }

    var baseUrl = new Uri(swaggerUrl).GetLeftPart(UriPartial.Authority);
    Console.WriteLine($"Base URL: {baseUrl}");
    Console.WriteLine("Реєструю двох тестових користувачів і зондую доступ...\n");

    var prober = new AuthProber(baseUrl);
    AuthContext ctx;
    try
    {
        ctx = await prober.ProbeAsync(api);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Probing не вдався: {ex.Message}");
        Environment.ExitCode = 1;
        return;
    }

    Console.WriteLine($"Client: {ctx.Client?.Email} (роль: {ctx.Client?.Role ?? "?"})");
    Console.WriteLine(ctx.HasAdmin
        ? $"Admin : {ctx.Admin?.Email} (роль: {ctx.Admin?.Role}) — БД була порожня, перший юзер став Admin"
        : "Admin : не отримано (БД не порожня). Для класифікації не потрібен — Admin визначається по 403 від client.");
    Console.WriteLine();

    Console.WriteLine("Матриця доступу (емпірична):");
    foreach (var ep in api.Endpoints.OrderBy(e => e.Path).ThenBy(e => e.Method))
    {
        var icon = ep.Auth switch
        {
            AuthRequirement.Anonymous => "🌐",
            AuthRequirement.Authenticated => "🔑",
            AuthRequirement.Admin => "👑",
            _ => "❓"
        };
        Console.WriteLine($"  {icon} {ep.Method,-6} {ep.Path,-30} → {ep.Auth}");
    }

    // Звіряємо зі Swagger: глобальний Bearer бреше на анонімних ендпоінтах.
    var falseAuth = api.Endpoints.Count(e => e.Auth == AuthRequirement.Anonymous);
    Console.WriteLine();
    Console.WriteLine($"⚠️  Знахідка: Swagger декларує Bearer на ВСІХ {api.Endpoints.Count} ендпоінтах, " +
                      $"але {falseAuth} з них насправді анонімні — документація вводить в оману.");

    var store = new SnapshotStore(System.IO.Path.Combine(ArtifactsDir(), "schema-snapshot.json"));
    await store.SaveAsync(api);
    Console.WriteLine($"\nМатрицю збережено у знімок: {store.Path}");
}

static async Task RunGenerateAsync(string swaggerUrl)
{
    Console.WriteLine("=== QaAgent · Етап 3b · генерація тестів (positive + auth + boundary) ===\n");

    // Читаємо знімок із матрицею доступу (заповнюється командою `probe`).
    var store = new SnapshotStore(System.IO.Path.Combine(ArtifactsDir(), "schema-snapshot.json"));
    var api = await store.LoadAsync();
    if (api is null)
    {
        Console.WriteLine("❌ Немає знімка. Спершу виконай: probe (щоб визначити матрицю доступу).");
        Environment.ExitCode = 1;
        return;
    }
    if (api.Endpoints.All(e => e.Auth == AuthRequirement.Unknown))
        Console.WriteLine("⚠️  Матриця доступу не визначена (усі Unknown). Рекомендую спершу `probe`.\n");

    var (files, total) = await GenerateSuiteAsync(api, GeneratedDir(), overwrite: true, verbose: true);
    Console.WriteLine($"\nЗгенеровано {total} тест(ів) у {files} файлах: {GeneratedDir()}");
    Console.WriteLine("Запусти тести:  dotnet test generated/ApiTests");
}

static async Task<(int Files, int Total)> GenerateSuiteAsync(
    ApiSpec api, string outDir, bool overwrite, bool verbose)
{
    // LLM-positive для безпечних анонімних GET, що стабільно дають 200.
    var positiveAllowlist = new HashSet<string> { "GET /api/Books", "GET /api/Books/search" };
    // LLM-positive для Admin-ендпоінтів із тілом (потребує засіяного admin + мутує БД).
    var adminPositiveAllowlist = new HashSet<string> { "POST /api/Books" };

    var generator = new TestGenerator(new LlmClient(new OllamaOptions()));
    var renderer = new TestRenderer();
    int files = 0, total = 0;

    foreach (var ep in api.Endpoints)
    {
        var file = System.IO.Path.Combine(outDir, renderer.ClassName(ep) + ".cs");
        if (!overwrite && File.Exists(file))
            continue; // зберігаємо наявні (можливо, полагоджені) тести

        var scenarios = new List<TestScenario>();

        if (positiveAllowlist.Contains(ep.Signature))
        {
            try { scenarios.AddRange(await generator.GeneratePositiveAsync(ep, api)); }
            catch (Exception ex) { if (verbose) Console.WriteLine($"  ⚠️ LLM positive {ep.Signature}: {ex.Message}"); }
        }

        // Negative: невалідні дані для анонімних ендпоінтів із тілом (register/login).
        if (ep.RequestBody is not null && ep.Auth == AuthRequirement.Anonymous)
        {
            try { scenarios.AddRange(await generator.GenerateNegativeAsync(ep, api)); }
            catch (Exception ex) { if (verbose) Console.WriteLine($"  ⚠️ LLM negative {ep.Signature}: {ex.Message}"); }
        }

        // Positive-admin: дія Admin-ендпоінта з тілом під admin-токеном (потребує seed).
        if (adminPositiveAllowlist.Contains(ep.Signature))
        {
            try { scenarios.AddRange(await generator.GeneratePositiveAsync(ep, api, authMode: "admin")); }
            catch (Exception ex) { if (verbose) Console.WriteLine($"  ⚠️ LLM admin-positive {ep.Signature}: {ex.Message}"); }
        }

        scenarios.AddRange(ScenarioBuilder.AdminPositiveScenarios(ep));
        scenarios.AddRange(ScenarioBuilder.AuthScenarios(ep));
        scenarios.AddRange(ScenarioBuilder.BoundaryScenarios(ep));

        if (scenarios.Count == 0) continue;

        await File.WriteAllTextAsync(file, renderer.Render(ep, scenarios));

        if (verbose)
        {
            var byType = scenarios.GroupBy(s => s.Type).Select(g => $"{g.Count()} {g.Key}");
            Console.WriteLine($"• {ep.Signature,-34} → {string.Join(", ", byType)}");
        }
        files++;
        total += scenarios.Count;
    }

    return (files, total);
}

static async Task RunHealAsync(string swaggerUrl)
{
    Console.WriteLine("=== QaAgent · Етап 5 · self-healing ===\n");

    var store = new SnapshotStore(System.IO.Path.Combine(ArtifactsDir(), "schema-snapshot.json"));
    var previous = await store.LoadAsync();
    if (previous is null)
    {
        Console.WriteLine("❌ Немає попереднього знімка. Спершу: probe.");
        Environment.ExitCode = 1;
        return;
    }

    ApiSpec current;
    try
    {
        current = await new SwaggerParser().LoadFromUrlAsync(swaggerUrl);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Не вдалося завантажити поточну схему: {ex.Message}");
        Environment.ExitCode = 1;
        return;
    }

    var diff = DiffEngine.Diff(previous, current);
    if (diff.HasChanges)
    {
        Console.WriteLine("Виявлені зміни API:");
        foreach (var r in diff.Renames) Console.WriteLine($"   ✎ перейменовано: {r.From} → {r.To}");
        foreach (var s in diff.AddedEndpoints) Console.WriteLine($"   + {s}");
        foreach (var s in diff.RemovedEndpoints) Console.WriteLine($"   - {s}");
        foreach (var c in diff.ChangedEndpoints) Console.WriteLine($"   ~ {c.Signature}: {string.Join(", ", c.Changes)}");
        foreach (var s in diff.SchemaChanges) Console.WriteLine($"   schema {s}");
    }
    else
    {
        Console.WriteLine("Схема не змінилася — лікуватимемо суто за фактом падінь тестів.");
    }
    Console.WriteLine();

    var healer = new SelfHealer(
        new LlmClient(new OllamaOptions()),
        new TestRunner(),
        System.IO.Path.Combine(SolutionRoot(), "generated", "ApiTests", "ApiTests.csproj"),
        GeneratedDir(),
        System.IO.Path.Combine(ArtifactsDir(), "test-results"));

    Console.WriteLine("Прогін тестів і самовідновлення...\n");
    var report = await healer.HealAsync(diff, current);

    if (report.StartedGreen)
    {
        Console.WriteLine("✅ Усі тести зелені — лікувати нічого.");
        return;
    }

    Console.WriteLine($"Спроб лікування: {report.Attempts}");
    if (report.HealedFiles.Count > 0)
        Console.WriteLine($"🩹 Полагоджено файлів: {string.Join(", ", report.HealedFiles)}");

    if (report.FinalGreen)
    {
        Console.WriteLine("\n✅ Self-healing успішний — усі тести знову зелені.");
    }
    else
    {
        Console.WriteLine("\n⚠️ Залишились непролагоджені падіння — ймовірно РЕАЛЬНІ баги:");
        foreach (var f in report.UnresolvedFailures)
            Console.WriteLine($"   ❌ {f.Name}: {f.ErrorMessage?.Trim().Replace("\n", " ")}");
        Environment.ExitCode = 1;
    }
}

static async Task RunSeedAsync(string swaggerUrl)
{
    Console.WriteLine("=== QaAgent · seed admin ===\n");

    var baseUrl = new Uri(swaggerUrl).GetLeftPart(UriPartial.Authority);
    var email = Environment.GetEnvironmentVariable("QA_ADMIN_EMAIL") ?? "qa.admin@example.com";
    var password = Environment.GetEnvironmentVariable("QA_ADMIN_PASSWORD") ?? "Passw0rd!";
    var apiProject = Environment.GetEnvironmentVariable("QA_API_PROJECT")
        ?? @"G:\Rider sulutions\OnlineLibrary\OnlineLibrary";

    // 1. Реєстрація (ідемпотентно — якщо існує, отримаємо 4xx і просто йдемо далі).
    using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
    var body = new StringContent($"{{\"email\":\"{email}\",\"password\":\"{password}\"}}",
        System.Text.Encoding.UTF8, "application/json");
    var reg = await http.PostAsync("/api/Auth/register", body);
    Console.WriteLine($"register {email}: {(int)reg.StatusCode}");

    // 2. Підвищення до Admin через консольну команду API (--no-build, щоб не чіпати запущений інстанс).
    var psi = new System.Diagnostics.ProcessStartInfo("dotnet")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    foreach (var a in new[] { "run", "--no-build", "--project", apiProject, "--", "setrole", email, "Admin" })
        psi.ArgumentList.Add(a);

    using var proc = System.Diagnostics.Process.Start(psi)!;
    var stdout = await proc.StandardOutput.ReadToEndAsync();
    await proc.WaitForExitAsync();

    var line = stdout.Split('\n').LastOrDefault(l => l.Contains("Успех") || l.Contains("Ошибка"))?.Trim();
    Console.WriteLine($"setrole: {line ?? $"exit {proc.ExitCode}"}");
    Console.WriteLine($"\n✅ Admin готовий: {email}. Тепер positive-admin тести зможуть логінитись.");
}

static async Task RunAgentAsync(string swaggerUrl)
{
    Console.WriteLine("=== QaAgent · повний цикл (agent) ===\n");

    var store = new SnapshotStore(System.IO.Path.Combine(ArtifactsDir(), "schema-snapshot.json"));
    var previous = await store.LoadAsync();

    ApiSpec current;
    try { current = await new SwaggerParser().LoadFromUrlAsync(swaggerUrl); }
    catch (Exception ex) { Console.WriteLine($"❌ Схема недоступна: {ex.Message}"); Environment.ExitCode = 1; return; }

    var baseUrl = new Uri(swaggerUrl).GetLeftPart(UriPartial.Authority);

    var diff = DiffEngine.Diff(previous, current);
    Console.WriteLine(previous is null
        ? "1) Baseline (перший запуск).\n"
        : diff.HasChanges ? "1) Виявлено зміни API.\n" : "1) Змін API немає.\n");

    Console.WriteLine("2) Auth-probing...");
    try { await new AuthProber(baseUrl).ProbeAsync(current); }
    catch (Exception ex) { Console.WriteLine($"   ⚠️ probing: {ex.Message}"); }

    Console.WriteLine("3) Генерація тестів...");
    var (files, total) = await GenerateSuiteAsync(current, GeneratedDir(), overwrite: previous is null, verbose: false);
    Console.WriteLine($"   файлів: {files}, тестів: {total}");

    Console.WriteLine("4) Прогін тестів" + (diff.HasChanges && previous is not null ? " + self-healing..." : "..."));
    var runner = new TestRunner();
    var testProject = System.IO.Path.Combine(SolutionRoot(), "generated", "ApiTests", "ApiTests.csproj");
    var resultsDir = System.IO.Path.Combine(ArtifactsDir(), "test-results");

    TestRun finalRun;
    HealReport? heal = null;
    if (diff.HasChanges && previous is not null)
    {
        heal = await new SelfHealer(new LlmClient(new OllamaOptions()), runner, testProject, GeneratedDir(), resultsDir)
            .HealAsync(diff, current);
        finalRun = heal.FinalRun!;
    }
    else
    {
        finalRun = (await runner.RunAsync(testProject, resultsDir)).Run;
    }

    Console.WriteLine("5) Звіт...\n");
    var report = BuildReport(current, diff, finalRun, heal);
    var log = await new ReportDispatcher(System.IO.Path.Combine(ArtifactsDir(), "reports")).DispatchAsync(report);

    Console.WriteLine(ReportRenderer.ToShortText(report));
    Console.WriteLine();
    foreach (var l in log) Console.WriteLine(l);

    await store.SaveAsync(current);
    Environment.ExitCode = report.Success ? 0 : 1;
}

static RunReport BuildReport(ApiSpec api, ApiDiff diff, TestRun run, HealReport? heal)
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
    if (anon > 0)
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

static async Task RunTestsAsync()
{
    Console.WriteLine("=== QaAgent · Етап 4 · запуск тестів ===\n");

    var testProject = System.IO.Path.Combine(SolutionRoot(), "generated", "ApiTests", "ApiTests.csproj");
    var resultsDir = System.IO.Path.Combine(ArtifactsDir(), "test-results");

    if (!File.Exists(testProject))
    {
        Console.WriteLine($"❌ Тест-проєкт не знайдено: {testProject}");
        Environment.ExitCode = 1;
        return;
    }

    Console.WriteLine("Запускаю dotnet test (може зайняти трохи)...\n");
    var runner = new TestRunner();

    TestRunner.RunOutput output;
    try
    {
        output = await runner.RunAsync(testProject, resultsDir);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ {ex.Message}");
        Environment.ExitCode = 1;
        return;
    }

    var run = output.Run;
    Console.WriteLine($"Результат: {run.Passed}/{run.Total} passed, {run.Failed} failed, {run.Skipped} skipped " +
                      $"({run.Duration.TotalSeconds:F1}s)");

    if (run.Failed > 0)
    {
        Console.WriteLine("\nПровалені тести:");
        foreach (var f in run.Failures)
        {
            Console.WriteLine($"  ❌ {f.Name}");
            if (!string.IsNullOrWhiteSpace(f.ErrorMessage))
                Console.WriteLine($"     {f.ErrorMessage.Trim().Replace("\n", "\n     ")}");
        }
    }
    else
    {
        Console.WriteLine("✅ Усі тести пройшли.");
    }

    Console.WriteLine($"\nTRX: {resultsDir}");
    Environment.ExitCode = run.Success ? 0 : 1;
}

static string GeneratedDir()
{
    var path = System.IO.Path.Combine(SolutionRoot(), "generated", "ApiTests", "Generated");
    Directory.CreateDirectory(path);
    return path;
}

static string SolutionRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && dir.GetFiles("QaAgent.slnx").Length == 0)
        dir = dir.Parent;
    return dir?.FullName ?? Directory.GetCurrentDirectory();
}

static string ArtifactsDir()
{
    var path = System.IO.Path.Combine(SolutionRoot(), "artifacts");
    Directory.CreateDirectory(path);
    return path;
}
