using System.Diagnostics;
using QaAgent.App;
using QaAgent.Core;
using QaAgent.Llm;
using QaAgent.Reporting;
using QaAgent.Swagger;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "agent";
var arg2 = args.Length > 1 ? args[1] : "Library";   // ім'я таргета АБО будь-який Swagger URL

var paths = new WorkspacePaths();
var target = arg2.StartsWith("http", StringComparison.OrdinalIgnoreCase)
    ? TargetConfig.FromUrl(arg2)            // довільний API за URL
    : TargetConfig.Get(arg2);               // вбудований таргет за іменем
var agent = new QaAgentService(paths, target);
void Log(string line) => Console.WriteLine(line);

Console.WriteLine($"Таргет: {target.Name} ({target.SwaggerUrl})\n");

switch (command)
{
    case "smoke":
        await RunSmokeAsync();
        break;

    case "analyze":
    {
        var (current, _) = await agent.AnalyzeAsync(Log);
        await new SnapshotStore(paths.SnapshotFor(target.Name)).SaveAsync(current);
        break;
    }

    case "probe":
        await agent.ProbeAsync(Log);
        break;

    case "generate":
    {
        var api = await agent.LoadSnapshotAsync();
        if (api is null) { Console.WriteLine("❌ Немає знімка. Спершу: probe."); Environment.ExitCode = 1; break; }
        await agent.GenerateAsync(api, overwrite: true, log: Log);
        break;
    }

    case "run":
    {
        var run = await agent.RunAsync(Log);
        Environment.ExitCode = run.Success ? 0 : 1;
        break;
    }

    case "heal":
    {
        var (current, diff) = await agent.AnalyzeAsync(Log);
        var report = await agent.HealAsync(diff, current, Log);
        Environment.ExitCode = report.FinalGreen ? 0 : 1;
        break;
    }

    case "agent":
    {
        var report = await agent.FullCycleAsync(Log);
        Console.WriteLine();
        Console.WriteLine(ReportRenderer.ToShortText(report));
        Environment.ExitCode = report.Success ? 0 : 1;
        break;
    }

    case "seed":
        await RunSeedAsync();
        break;
    case "monitor":
    {
        var interval = args.Length > 2 && int.TryParse(args[2], out var s) ? Math.Max(15, s) : 60;
        await RunMonitorAsync(interval);
        break;
    }

    default:
        Console.WriteLine($"Невідома команда: {command}");
        Console.WriteLine("Доступні: smoke | analyze | probe | generate | run | heal | agent | seed | monitor [target|url] [interval]");
        Console.WriteLine($"Таргети: {string.Join(", ", TargetConfig.BuiltIn.Select(t => t.Name))} — або будь-який Swagger URL");
        break;
}

// ---------------------------------------------------------------------------

async Task RunMonitorAsync(int intervalSec)
{
    Console.WriteLine($"=== Моніторинг {target.Name} кожні {intervalSec}с (Ctrl+C — зупинка) ===\n");
    while (true)
    {
        var stamp = DateTime.Now.ToString("HH:mm:ss");
        try
        {
            var current = await agent.LoadCurrentAsync();
            var snapshot = await agent.LoadSnapshotAsync();
            if (snapshot is null)
            {
                Console.WriteLine($"[{stamp}] baseline — знімка немає, тестую…");
                await agent.FullCycleAsync(Log);
            }
            else if (snapshot.Hash != current.Hash)
            {
                var diff = DiffEngine.Diff(snapshot, current);
                Console.WriteLine($"[{stamp}] ⚠️ ЗМІНА схеми: +{diff.AddedEndpoints.Count} -{diff.RemovedEndpoints.Count} ~{diff.ChangedEndpoints.Count} rename {diff.Renames.Count} → тестую…");
                await agent.FullCycleAsync(Log);
            }
            else
            {
                Console.WriteLine($"[{stamp}] без змін.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{stamp}] помилка: {ex.Message}");
        }
        await Task.Delay(intervalSec * 1000);
    }
}

async Task RunSmokeAsync()
{
    var opts = new OllamaOptions();
    Console.WriteLine($"=== smoke · {opts.Model} @ {opts.Endpoint} ===");
    var sw = Stopwatch.StartNew();
    var answer = await new LlmClient(opts).AskAsync("You are a QA engineer. Be concise.", "Answer one word: PONG.");
    Console.WriteLine($"Відповідь ({sw.ElapsedMilliseconds} ms): {answer.Trim()}");
    Console.WriteLine("✅ Звʼязок із Ollama працює.");
}

async Task RunSeedAsync()
{
    Console.WriteLine("=== seed admin ===");
    var email = Environment.GetEnvironmentVariable("QA_ADMIN_EMAIL") ?? "qa.admin@example.com";
    var password = Environment.GetEnvironmentVariable("QA_ADMIN_PASSWORD") ?? "Passw0rd!";
    var apiProject = Environment.GetEnvironmentVariable("QA_API_PROJECT")
        ?? @"G:\Rider sulutions\OnlineLibrary\OnlineLibrary";

    using var http = new HttpClient { BaseAddress = new Uri(agent.BaseUrl) };
    var body = new StringContent($"{{\"email\":\"{email}\",\"password\":\"{password}\"}}",
        System.Text.Encoding.UTF8, "application/json");
    var reg = await http.PostAsync("/api/Auth/register", body);
    Console.WriteLine($"register {email}: {(int)reg.StatusCode}");

    var psi = new ProcessStartInfo("dotnet")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    foreach (var a in new[] { "run", "--no-build", "--project", apiProject, "--", "setrole", email, "Admin" })
        psi.ArgumentList.Add(a);

    using var proc = Process.Start(psi)!;
    var stdout = await proc.StandardOutput.ReadToEndAsync();
    await proc.WaitForExitAsync();
    var line = stdout.Split('\n').LastOrDefault(l => l.Contains("Успех") || l.Contains("Ошибка"))?.Trim();
    Console.WriteLine($"setrole: {line ?? $"exit {proc.ExitCode}"}");
}
