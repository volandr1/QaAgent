using QaAgent.App;
using QaAgent.Core;
using QaAgent.Swagger;

namespace QaAgent.Web.Services;

public sealed record MonitorEvent(DateTimeOffset Time, string Target, string Message, bool Important);

/// <summary>
/// Фоновий моніторинг Swagger: періодично опитує кожен таргет, дешево порівнює hash схеми,
/// і ЛИШЕ при зміні (новий/змінений/перейменований ендпоінт) запускає повний цикл
/// (probe→generate→run→heal→report). Працює, доки живе веб-додаток.
/// </summary>
public sealed class SchemaMonitor : BackgroundService
{
    private readonly WorkspacePaths _paths;
    private readonly AgentSettings _settings;
    private readonly object _gate = new();
    private readonly List<MonitorEvent> _events = new();

    public bool Enabled { get; set; }
    public int IntervalSeconds { get; set; } = 900;
    public DateTimeOffset? LastCheck { get; private set; }
    public DateTimeOffset? NextCheckAt { get; private set; }
    public bool Busy { get; private set; }

    /// <summary>Перемикачі з UI.</summary>
    public bool RunTestsOnChange { get; set; } = true;
    public bool NotifyTelegram { get; set; } = true;
    public bool AutoHeal { get; set; } = true;

    /// <summary>Останній результат прогону (для картки «останній результат»).</summary>
    public RunReport? LastResult { get; private set; }

    public SchemaMonitor(WorkspacePaths paths, AgentSettings settings)
    {
        _paths = paths;
        _settings = settings;
    }

    public IReadOnlyList<MonitorEvent> Events
    {
        get { lock (_gate) return _events.AsEnumerable().Reverse().ToList(); }
    }

    /// <summary>Вмикає/вимикає моніторинг і одразу планує наступну перевірку (для UI).</summary>
    public void SetEnabled(bool on)
    {
        Enabled = on;
        NextCheckAt = on ? DateTimeOffset.Now.AddSeconds(Math.Max(15, IntervalSeconds)) : null;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        Log("monitor", "Монітор запущено (очікує ввімкнення).", false);
        // Тикаємо часто й порівнюємо з NextCheckAt — так відлік на UI і реальний
        // запуск завжди синхронні (не «застрягає» на 0, коли час вийшов).
        while (!ct.IsCancellationRequested)
        {
            if (Enabled)
            {
                NextCheckAt ??= DateTimeOffset.Now.AddSeconds(Math.Max(15, IntervalSeconds));

                if (!Busy && DateTimeOffset.Now >= NextCheckAt)
                {
                    Busy = true;
                    try { await CheckAllAsync(ct); }
                    catch (Exception ex) { Log("monitor", $"помилка циклу: {ex.Message}", true); }
                    finally
                    {
                        Busy = false;
                        NextCheckAt = DateTimeOffset.Now.AddSeconds(Math.Max(15, IntervalSeconds));
                    }
                }
            }
            else NextCheckAt = null;

            try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task CheckAllAsync(CancellationToken ct)
    {
        LastCheck = DateTimeOffset.Now;
        _settings.ApplyEnv();

        foreach (var target in _settings.Targets.ToList())
        {
            ct.ThrowIfCancellationRequested();
            var svc = new QaAgentService(_paths, target,
                new OllamaOptions { Model = _settings.OllamaModel, Endpoint = _settings.OllamaEndpoint });

            try
            {
                var current = await svc.LoadCurrentAsync(ct);
                var snapshot = await svc.LoadSnapshotAsync(ct);

                if (snapshot is null)
                {
                    Log(target.Name, "baseline — знімка немає, встановлюю та тестую…", true);
                    var r = await svc.FullCycleAsync(null, ct, AutoHeal, NotifyTelegram);
                    LastResult = r;
                    Log(target.Name, $"baseline готово: {Summary(r)}", true);
                }
                else if (snapshot.Hash != current.Hash)
                {
                    var diff = DiffEngine.Diff(snapshot, current);
                    if (RunTestsOnChange)
                    {
                        Log(target.Name, $"⚠️ ЗМІНА схеми: {DescribeDiff(diff)} → запускаю тести…", true);
                        var r = await svc.FullCycleAsync(null, ct, AutoHeal, NotifyTelegram);
                        LastResult = r;
                        Log(target.Name, $"результат: {Summary(r)}", true);
                    }
                    else
                    {
                        Log(target.Name, $"⚠️ ЗМІНА схеми: {DescribeDiff(diff)} (автозапуск тестів вимкнено)", true);
                    }
                }
                else
                {
                    Log(target.Name, "перевірка схеми: без змін", false);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log(target.Name, $"недоступний: {ex.Message}", false);
            }
        }
    }

    private static string DescribeDiff(ApiDiff d) =>
        $"+{d.AddedEndpoints.Count} нових, -{d.RemovedEndpoints.Count} видалених, ~{d.ChangedEndpoints.Count} змінених, {d.Renames.Count} перейменувань";

    private static string Summary(RunReport r) =>
        r.TestRun is { } tr ? $"{(r.Success ? "✅ PASS" : "❌ FAIL")} {tr.Passed}/{tr.Total}" : "немає прогону";

    private void Log(string target, string message, bool important)
    {
        lock (_gate)
        {
            _events.Add(new MonitorEvent(DateTimeOffset.Now, target, message, important));
            if (_events.Count > 200) _events.RemoveRange(0, _events.Count - 200);
        }
    }
}
