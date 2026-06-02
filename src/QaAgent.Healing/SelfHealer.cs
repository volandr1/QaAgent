using System.Text;
using QaAgent.Core;
using QaAgent.Execution;
using QaAgent.Llm;
using QaAgent.Swagger;

namespace QaAgent.Healing;

public sealed class HealReport
{
    public bool StartedGreen { get; set; }
    public bool FinalGreen { get; set; }
    public int Attempts { get; set; }
    public List<string> HealedFiles { get; set; } = new();
    public List<TestCaseResult> UnresolvedFailures { get; set; } = new();

    /// <summary>Підсумковий прогін тестів після (можливого) лікування.</summary>
    public TestRun? FinalRun { get; set; }
}

/// <summary>
/// Таргетне самовідновлення: для впалих тестів дає LLM (старий файл + diff + поточна схема),
/// отримує патч, перекомпільовує та реранить. Guardrails: ліміт спроб; нерозвʼязані падіння
/// вважаються РЕАЛЬНИМИ багами, а зіпсовані моделлю файли відкочуються.
/// </summary>
public sealed class SelfHealer
{
    private const string SystemPrompt =
        "You are a senior C# SDET maintaining Playwright + NUnit API tests. " +
        "When an API changes, you fix the affected test file MINIMALLY so it matches the new contract. " +
        "Return ONLY the full corrected C# file content — no markdown, no prose, no code fences.";

    private readonly LlmClient _llm;
    private readonly TestRunner _runner;
    private readonly string _testProject;
    private readonly string _generatedDir;
    private readonly string _resultsDir;

    public SelfHealer(LlmClient llm, TestRunner runner, string testProject, string generatedDir, string resultsDir)
    {
        _llm = llm;
        _runner = runner;
        _testProject = testProject;
        _generatedDir = generatedDir;
        _resultsDir = resultsDir;
    }

    public async Task<HealReport> HealAsync(
        ApiDiff diff, ApiSpec currentApi, int maxAttempts = 2, CancellationToken ct = default)
    {
        var report = new HealReport();

        var run = (await _runner.RunAsync(_testProject, _resultsDir, ct)).Run;
        if (run.Success)
        {
            report.StartedGreen = true;
            report.FinalGreen = true;
            report.FinalRun = run;
            return report;
        }

        var backups = new Dictionary<string, string>();

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            report.Attempts = attempt;

            var failingNames = run.Failures.Select(f => f.Name).ToHashSet();
            var affected = MapFailuresToFiles(failingNames, run.Failures.ToList());
            if (affected.Count == 0) break;

            foreach (var (file, fails) in affected)
            {
                var original = await File.ReadAllTextAsync(file, ct);
                backups.TryAdd(file, original);

                var prompt = BuildPrompt(original, fails, diff, currentApi);
                var patched = CodeExtractor.Extract(await _llm.AskAsync(SystemPrompt, prompt, ct));
                await File.WriteAllTextAsync(file, patched, ct);
            }

            run = (await _runner.RunAsync(_testProject, _resultsDir, ct)).Run;
            if (run.Success) break;
        }

        report.FinalGreen = run.Success;
        report.FinalRun = run;
        report.UnresolvedFailures = run.Failures.ToList();

        // Відкочуємо файли, які досі падають (щоб не лишати зіпсований моделлю код).
        // Healed = ті, що ми чіпали і вони більше не падають.
        var stillFailingNames = run.Failures.Select(f => f.Name).ToHashSet();
        foreach (var (file, original) in backups)
        {
            if (FileContainsAnyTest(original, stillFailingNames) || FileContainsAnyTest(File.ReadAllText(file), stillFailingNames))
                await File.WriteAllTextAsync(file, original, ct);
            else
                report.HealedFiles.Add(Path.GetFileName(file));
        }

        return report;
    }

    private Dictionary<string, List<TestCaseResult>> MapFailuresToFiles(
        HashSet<string> failingNames, List<TestCaseResult> failures)
    {
        var map = new Dictionary<string, List<TestCaseResult>>();
        foreach (var file in Directory.GetFiles(_generatedDir, "*.cs"))
        {
            var content = File.ReadAllText(file);
            var matched = failures.Where(f => ContainsMethod(content, f.Name)).ToList();
            if (matched.Count > 0)
                map[file] = matched;
        }
        return map;
    }

    private static bool FileContainsAnyTest(string content, HashSet<string> names) =>
        names.Any(n => ContainsMethod(content, n));

    private static bool ContainsMethod(string content, string methodName) =>
        content.Contains($"public async Task {methodName}(") ||
        content.Contains($"Task {methodName}(");

    private static string BuildPrompt(string fileContent, List<TestCaseResult> fails, ApiDiff diff, ApiSpec api)
    {
        var sb = new StringBuilder();
        sb.AppendLine("An API change broke the test file below. Fix it to match the NEW API contract.");
        sb.AppendLine();

        if (diff.Renames.Count > 0)
        {
            sb.AppendLine("RENAMED endpoints (old -> new):");
            foreach (var r in diff.Renames) sb.AppendLine($"  {r.From}  ->  {r.To}");
        }
        if (diff.ChangedEndpoints.Count > 0)
        {
            sb.AppendLine("CHANGED endpoints:");
            foreach (var c in diff.ChangedEndpoints) sb.AppendLine($"  {c.Signature}: {string.Join(", ", c.Changes)}");
        }
        if (diff.RemovedEndpoints.Count > 0)
            sb.AppendLine("REMOVED endpoints: " + string.Join(", ", diff.RemovedEndpoints));

        sb.AppendLine();
        sb.AppendLine("Current API endpoints (method path):");
        foreach (var ep in api.Endpoints) sb.AppendLine($"  {ep.Method} {ep.Path}");

        sb.AppendLine();
        sb.AppendLine("Failing tests in this file and their errors:");
        foreach (var f in fails)
            sb.AppendLine($"  - {f.Name}: {f.ErrorMessage?.Trim().Replace("\n", " ")}");

        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Change ONLY what is required (usually the URL path/params).");
        sb.AppendLine("- Keep the class name, namespace, ApiTestBase, helper calls and asserts intact.");
        sb.AppendLine("- Output the COMPLETE corrected .cs file, nothing else.");
        sb.AppendLine();
        sb.AppendLine("--- FILE START ---");
        sb.AppendLine(fileContent);
        sb.AppendLine("--- FILE END ---");
        return sb.ToString();
    }
}
