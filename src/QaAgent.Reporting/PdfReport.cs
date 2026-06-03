using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using QaAgent.Core;

namespace QaAgent.Reporting;

/// <summary>
/// Рендерить <see cref="RunReport"/> у PDF (QuestPDF) — детальний звіт-документ,
/// який можна завантажити й відкрити на будь-якому пристрої.
/// </summary>
public static class PdfReport
{
    static PdfReport() => QuestPDF.Settings.License = LicenseType.Community;

    private static readonly Color Pass = Color.FromHex("#1a7f37");
    private static readonly Color Fail = Color.FromHex("#cf222e");
    private static readonly Color Muted = Color.FromHex("#57606a");
    private static readonly Color Line = Color.FromHex("#d0d7de");
    private static readonly Color Box = Color.FromHex("#f6f8fa");

    public static byte[] Build(RunReport r) =>
        Document.Create(doc =>
        {
            doc.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(36);
                page.DefaultTextStyle(t => t.FontFamily("Arial").FontSize(10).FontColor(Color.FromHex("#1f2328")));

                page.Header().Element(h => Header(h, r));
                page.Content().PaddingVertical(10).Element(c => Content(c, r));
                page.Footer().AlignCenter().Text(t =>
                {
                    t.Span("QA-агент · ").FontColor(Muted).FontSize(8);
                    t.Span($"{r.GeneratedAt:yyyy-MM-dd HH:mm:ss}").FontColor(Muted).FontSize(8);
                    t.Span("   ·   стор. ").FontColor(Muted).FontSize(8);
                    t.CurrentPageNumber().FontColor(Muted).FontSize(8);
                    t.Span(" / ").FontColor(Muted).FontSize(8);
                    t.TotalPages().FontColor(Muted).FontSize(8);
                });
            });
        }).GeneratePdf();

    private static void Header(IContainer c, RunReport r) =>
        c.PaddingBottom(8).BorderBottom(1).BorderColor(Line).Row(row =>
        {
            row.RelativeItem().Column(col =>
            {
                col.Item().Text("QA-агент · звіт").FontSize(18).Bold();
                col.Item().Text($"{r.ApiTitle}  v{r.ApiVersion}").FontColor(Muted);
            });
            row.ConstantItem(110).AlignRight().AlignMiddle().Text(r.Success ? "PASS" : "FAIL")
                .FontSize(20).Bold().FontColor(r.Success ? Pass : Fail);
        });

    private static void Content(IContainer c, RunReport r) =>
        c.Column(col =>
        {
            col.Spacing(14);

            // Тести
            if (r.TestRun is { } tr)
            {
                Section(col, "Тести");
                col.Item().Row(row =>
                {
                    Stat(row, tr.Total.ToString(), "усього", null);
                    Stat(row, tr.Passed.ToString(), "passed", Pass);
                    Stat(row, tr.Failed.ToString(), "failed", tr.Failed > 0 ? Fail : (Color?)null);
                    Stat(row, tr.Skipped.ToString(), "skipped", null);
                    Stat(row, $"{tr.Duration.TotalSeconds:F1}s", "час", null);
                });

                if (tr.Failed > 0)
                {
                    Section(col, "Провалені тести");
                    foreach (var f in tr.Failures)
                    {
                        col.Item().Text(f.Name).Bold().FontColor(Fail);
                        col.Item().Background(Box).Padding(8).Text(
                            (f.ErrorMessage?.Trim() ?? "(без повідомлення)"))
                            .FontFamily("Consolas").FontSize(9);
                    }
                }
            }

            // Покриття
            if (r.Coverage is { } cov)
            {
                Section(col, "Покриття");
                col.Item().Row(row =>
                {
                    Stat(row, $"{cov.CoveredEndpoints}/{cov.TotalEndpoints}", "ендпоінтів", cov.Percent >= 80 ? Pass : (Color?)null);
                    Stat(row, $"{cov.Percent}%", "покриття", cov.Percent >= 80 ? Pass : cov.Percent >= 50 ? (Color?)null : Fail);
                    Stat(row, cov.Uncovered.Count.ToString(), "без тестів", cov.Uncovered.Count > 0 ? Fail : (Color?)null);
                });
                if (cov.ByType.Count > 0)
                    col.Item().PaddingTop(6).Text("Сценарії: " +
                        string.Join(" · ", cov.ByType.OrderByDescending(k => k.Value).Select(k => $"{k.Value} {k.Key}")))
                        .FontColor(Muted).FontSize(10);
                if (cov.Uncovered.Count > 0)
                    col.Item().PaddingTop(4).Text("Без тестів: " + string.Join(", ", cov.Uncovered))
                        .FontColor(Muted).FontSize(9);
            }

            // AI-аналіз
            if (!string.IsNullOrWhiteSpace(r.AiAnalysis))
            {
                Section(col, "AI-аналіз");
                col.Item().Background(Box).Padding(10).Text(r.AiAnalysis.Trim());
            }

            // Зміни схеми
            if (r.SchemaChanges.Count > 0)
            {
                Section(col, "Зміни схеми API");
                foreach (var s in r.SchemaChanges)
                    col.Item().Text(t => { t.Span("•  ").FontColor(Muted); t.Span(s); });
            }

            // Self-healing
            if (r.SelfHealingApplied)
            {
                Section(col, "Self-healing");
                col.Item().Text(r.HealedFiles.Count > 0
                    ? $"Полагоджено файлів: {string.Join(", ", r.HealedFiles)}"
                    : "Спроби лікування не дали результату.");
            }

            // Знахідки
            if (r.Findings.Count > 0)
            {
                Section(col, "Знахідки");
                foreach (var f in r.Findings)
                    col.Item().Text(t =>
                    {
                        t.Span($"[{Sev(f.Severity)}] ").Bold().FontColor(SevColor(f.Severity));
                        t.Span(f.Title).Bold();
                        t.Span($" — {f.Detail}").FontColor(Muted);
                    });
            }
        });

    private static void Section(ColumnDescriptor col, string title) =>
        col.Item().PaddingTop(4).BorderBottom(1).BorderColor(Line).PaddingBottom(3)
            .Text(title).FontSize(13).Bold();

    private static void Stat(RowDescriptor row, string value, string label, Color? color) =>
        row.RelativeItem().Column(c =>
        {
            c.Item().Text(value).FontSize(18).Bold().FontColor(color ?? Color.FromHex("#1f2328"));
            c.Item().Text(label).FontColor(Muted).FontSize(9);
        });

    private static string Sev(FindingSeverity s) => s switch
    {
        FindingSeverity.Bug => "BUG",
        FindingSeverity.Warning => "WARN",
        _ => "INFO"
    };

    private static Color SevColor(FindingSeverity s) => s switch
    {
        FindingSeverity.Bug => Fail,
        FindingSeverity.Warning => Color.FromHex("#9a6700"),
        _ => Muted
    };
}
