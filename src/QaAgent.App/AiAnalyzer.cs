using System.Text;
using QaAgent.Core;
using QaAgent.Llm;

namespace QaAgent.App;

/// <summary>
/// AI-аналіз результатів прогону: згодовує LLM зведення (тести, падіння, зміни схеми,
/// self-healing) і повертає стислий висновок із ймовірними причинами та рекомендаціями.
/// </summary>
public sealed class AiAnalyzer
{
    private const string SystemPrompt =
        "Ти — senior QA-аналітик. Відповідай УКРАЇНСЬКОЮ, стисло (3-5 речень), без markdown-заголовків і без нумерованих списків. " +
        "Якщо невалідні запити повертають 2xx — це означає, що САМЕ API не валідує ввід (не звинувачуй тести). " +
        "Якщо запит неіснуючого ресурсу повертає 200 замість 404 — це проблема контракту API.";

    private readonly LlmClient _llm;
    public AiAnalyzer(LlmClient llm) => _llm = llm;

    public async Task<string> AnalyzeAsync(RunReport r, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"API: {r.ApiTitle} v{r.ApiVersion}");

        if (r.TestRun is { } tr)
        {
            sb.AppendLine($"Tests: {tr.Passed}/{tr.Total} passed, {tr.Failed} failed, {tr.Skipped} skipped.");
            foreach (var f in tr.Failures)
                sb.AppendLine($"FAIL {f.Name}: {f.ErrorMessage?.Trim().Replace("\n", " ")}");
        }
        if (r.SchemaChanges.Count > 0)
            sb.AppendLine("Schema changes: " + string.Join("; ", r.SchemaChanges));
        if (r.SelfHealingApplied)
            sb.AppendLine($"Self-healing: healed {r.HealedFiles.Count} file(s) ({string.Join(", ", r.HealedFiles)}).");
        foreach (var f in r.Findings)
            sb.AppendLine($"Finding [{f.Severity}]: {f.Title} — {f.Detail}");

        var prompt =
            "Analyze this API test run. In 3-6 sentences (Ukrainian): overall health, " +
            "likely root causes of any failures, and concrete recommended next actions.\n\n" + sb;

        return (await _llm.AskAsync(SystemPrompt, prompt, ct)).Trim();
    }
}
