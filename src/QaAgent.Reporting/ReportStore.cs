using System.Text.Json;
using System.Text.Json.Serialization;
using QaAgent.Core;

namespace QaAgent.Reporting;

public sealed record RunRecord(string Stamp, RunReport Report, string Target = "");

/// <summary>
/// Зберігає/читає структуровані звіти (RunReport) у JSON — основа для History/Compare/Detail.
/// </summary>
public static class ReportStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static void Save(string reportsDir, RunReport report, string stamp)
    {
        Directory.CreateDirectory(reportsDir);
        File.WriteAllText(Path.Combine(reportsDir, $"report-{stamp}.json"),
            JsonSerializer.Serialize(report, Json));
    }

    public static List<RunRecord> List(string reportsDir)
    {
        var list = new List<RunRecord>();
        if (!Directory.Exists(reportsDir)) return list;

        foreach (var f in Directory.GetFiles(reportsDir, "report-*.json").OrderByDescending(x => x))
        {
            try
            {
                var report = JsonSerializer.Deserialize<RunReport>(File.ReadAllText(f), Json);
                if (report is not null)
                {
                    var stamp = Path.GetFileNameWithoutExtension(f).Replace("report-", "");
                    list.Add(new RunRecord(stamp, report));
                }
            }
            catch { /* пошкоджений файл — пропускаємо */ }
        }
        return list;
    }

    /// <summary>Усі звіти з УСІХ таргетів (artifacts/&lt;target&gt;/reports/), найновіші перші.</summary>
    public static List<RunRecord> ListAll(string artifactsRoot)
    {
        var all = new List<RunRecord>();
        if (!Directory.Exists(artifactsRoot)) return all;

        foreach (var targetDir in Directory.GetDirectories(artifactsRoot))
        {
            var reportsDir = Path.Combine(targetDir, "reports");
            if (!Directory.Exists(reportsDir)) continue;
            var target = Path.GetFileName(targetDir);
            foreach (var rec in List(reportsDir))
                all.Add(rec with { Target = target });
        }
        return all.OrderByDescending(r => r.Report.GeneratedAt).ToList();
    }

    public static RunReport? Load(string reportsDir, string stamp)
    {
        var f = Path.Combine(reportsDir, $"report-{stamp}.json");
        if (!File.Exists(f)) return null;
        try { return JsonSerializer.Deserialize<RunReport>(File.ReadAllText(f), Json); }
        catch { return null; }
    }
}
