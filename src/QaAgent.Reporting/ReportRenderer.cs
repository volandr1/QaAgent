using System.Text;
using QaAgent.Core;

namespace QaAgent.Reporting;

/// <summary>Рендерить <see cref="RunReport"/> у Markdown, HTML та короткий текст (для Telegram).</summary>
public static class ReportRenderer
{
    public static string ToMarkdown(RunReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# QA-агент · звіт");
        sb.AppendLine();
        sb.AppendLine($"**API:** {r.ApiTitle} v{r.ApiVersion}  ");
        sb.AppendLine($"**Час:** {r.GeneratedAt:yyyy-MM-dd HH:mm:ss}  ");
        sb.AppendLine($"**Статус:** {(r.Success ? "✅ PASS" : "❌ FAIL")}");
        sb.AppendLine();

        if (r.TestRun is { } tr)
        {
            sb.AppendLine("## Тести");
            sb.AppendLine($"- Усього: **{tr.Total}**");
            sb.AppendLine($"- ✅ Passed: {tr.Passed}");
            sb.AppendLine($"- ❌ Failed: {tr.Failed}");
            sb.AppendLine($"- ⏭ Skipped: {tr.Skipped}");
            sb.AppendLine($"- ⏱ {tr.Duration.TotalSeconds:F1}s");
            sb.AppendLine();

            if (tr.Failed > 0)
            {
                sb.AppendLine("### Провалені тести");
                foreach (var f in tr.Failures)
                {
                    sb.AppendLine($"- **{f.Name}**");
                    sb.AppendLine("  ```");
                    sb.AppendLine("  " + (f.ErrorMessage?.Trim().Replace("\n", "\n  ") ?? "(без повідомлення)"));
                    sb.AppendLine("  ```");
                }
                sb.AppendLine();
            }
        }

        if (!string.IsNullOrWhiteSpace(r.AiAnalysis))
        {
            sb.AppendLine("## 🧠 AI-аналіз");
            sb.AppendLine(r.AiAnalysis);
            sb.AppendLine();
        }

        if (r.SchemaChanges.Count > 0)
        {
            sb.AppendLine("## Зміни схеми API");
            foreach (var s in r.SchemaChanges) sb.AppendLine($"- {s}");
            sb.AppendLine();
        }

        if (r.SelfHealingApplied)
        {
            sb.AppendLine("## Self-healing");
            sb.AppendLine(r.HealedFiles.Count > 0
                ? $"🩹 Полагоджено файлів: {string.Join(", ", r.HealedFiles)}"
                : "Спроби лікування не дали результату.");
            sb.AppendLine();
        }

        if (r.Findings.Count > 0)
        {
            sb.AppendLine("## Знахідки");
            foreach (var f in r.Findings)
                sb.AppendLine($"- {Icon(f.Severity)} **{f.Title}** — {f.Detail}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static string ToShortText(RunReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{(r.Success ? "✅" : "❌")} QA-агент — {r.ApiTitle} v{r.ApiVersion}");
        if (r.TestRun is { } tr)
            sb.AppendLine($"Тести: {tr.Passed}/{tr.Total} passed, {tr.Failed} failed ({tr.Duration.TotalSeconds:F1}s)");
        if (r.SchemaChanges.Count > 0)
            sb.AppendLine($"Зміни схеми: {r.SchemaChanges.Count}");
        if (r.SelfHealingApplied && r.HealedFiles.Count > 0)
            sb.AppendLine($"🩹 Полагоджено: {r.HealedFiles.Count} файл(ів)");
        if (r.BugCount > 0)
            sb.AppendLine($"🐞 Баги: {r.BugCount}");
        return sb.ToString().Trim();
    }

    public static string ToHtml(RunReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html lang=\"uk\"><head><meta charset=\"utf-8\">");
        sb.AppendLine("<title>QA-агент · звіт</title><style>");
        sb.AppendLine("body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#222}");
        sb.AppendLine("h1{font-size:20px}h2{font-size:16px;margin-top:20px;border-bottom:1px solid #eee}");
        sb.AppendLine(".pass{color:#1a7f37}.fail{color:#cf222e}");
        sb.AppendLine("pre{background:#f6f8fa;padding:8px;border-radius:6px;overflow:auto}");
        sb.AppendLine("table{border-collapse:collapse}td,th{border:1px solid #ddd;padding:4px 8px}");
        sb.AppendLine("</style></head><body>");
        sb.AppendLine($"<h1>QA-агент · звіт <span class=\"{(r.Success ? "pass" : "fail")}\">{(r.Success ? "PASS" : "FAIL")}</span></h1>");
        sb.AppendLine($"<p><b>API:</b> {Esc(r.ApiTitle)} v{Esc(r.ApiVersion)}<br><b>Час:</b> {r.GeneratedAt:yyyy-MM-dd HH:mm:ss}</p>");

        if (r.TestRun is { } tr)
        {
            sb.AppendLine("<h2>Тести</h2>");
            sb.AppendLine($"<p>Усього: <b>{tr.Total}</b> · <span class=\"pass\">{tr.Passed} passed</span> · " +
                          $"<span class=\"fail\">{tr.Failed} failed</span> · {tr.Skipped} skipped · {tr.Duration.TotalSeconds:F1}s</p>");
            if (tr.Failed > 0)
            {
                sb.AppendLine("<h2>Провалені тести</h2>");
                foreach (var f in tr.Failures)
                    sb.AppendLine($"<p><b>{Esc(f.Name)}</b></p><pre>{Esc(f.ErrorMessage?.Trim() ?? "")}</pre>");
            }
        }

        if (!string.IsNullOrWhiteSpace(r.AiAnalysis))
            sb.AppendLine($"<h2>🧠 AI-аналіз</h2><p>{Esc(r.AiAnalysis).Replace("\n", "<br>")}</p>");

        if (r.SchemaChanges.Count > 0)
            sb.AppendLine("<h2>Зміни схеми API</h2><ul>" +
                          string.Concat(r.SchemaChanges.Select(s => $"<li>{Esc(s)}</li>")) + "</ul>");

        if (r.SelfHealingApplied)
            sb.AppendLine("<h2>Self-healing</h2><p>" +
                          (r.HealedFiles.Count > 0 ? $"🩹 Полагоджено: {Esc(string.Join(", ", r.HealedFiles))}" : "Без результату") + "</p>");

        if (r.Findings.Count > 0)
            sb.AppendLine("<h2>Знахідки</h2><ul>" +
                          string.Concat(r.Findings.Select(f => $"<li>{Icon(f.Severity)} <b>{Esc(f.Title)}</b> — {Esc(f.Detail)}</li>")) + "</ul>");

        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private static string Icon(FindingSeverity s) => s switch
    {
        FindingSeverity.Bug => "🐞",
        FindingSeverity.Warning => "⚠️",
        _ => "ℹ️"
    };

    private static string Esc(string s) => System.Net.WebUtility.HtmlEncode(s);
}
