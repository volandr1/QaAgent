using System.Text;
using System.Text.RegularExpressions;
using QaAgent.Core;
using QaAgent.Generation;
using QaAgent.Probing;

namespace QaAgent.App;

/// <summary>
/// Рахує покриття ендпоінтів тестами: скільки з усіх мають згенерований файл/сценарій,
/// які лишились без тестів, і розбивку тест-кейсів за типом сценарію ([Category]).
/// Детерміновано — повторює правила іменування генератора.
/// </summary>
public static class CoverageAnalyzer
{
    private static readonly TestRenderer Renderer = new();

    public static CoverageInfo Compute(ApiSpec api, string generatedDir, TargetConfig target)
    {
        var files = Directory.Exists(generatedDir)
            ? Directory.GetFiles(generatedDir, "*.cs").Select(p => Path.GetFileName(p)!).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var info = new CoverageInfo { TotalEndpoints = api.Endpoints.Count };

        foreach (var ep in api.Endpoints)
        {
            if (IsCovered(ep, files, target)) info.CoveredEndpoints++;
            else info.Uncovered.Add(ep.Signature);
        }

        // Розбивка тест-кейсів за типом сценарію.
        foreach (var file in files)
        {
            string text;
            try { text = File.ReadAllText(Path.Combine(generatedDir, file)); }
            catch { continue; }
            foreach (Match m in Regex.Matches(text, "\\[Category\\(\"([^\"]+)\"\\)\\]"))
            {
                var cat = m.Groups[1].Value;
                info.ByType[cat] = info.ByType.GetValueOrDefault(cat) + 1;
            }
        }

        return info;
    }

    private static bool IsCovered(EndpointSpec ep, HashSet<string> files, TargetConfig target)
    {
        // 1) Власний файл сценаріїв ендпоінта.
        if (files.Contains(Renderer.ClassName(ep) + ".cs")) return true;

        // 2) Auth login/register → спільний Auth_Tests.cs.
        if (target.Auth is { } ac && ep.Method == "POST")
        {
            if ((Norm(ep.Path).Equals(Norm(ac.LoginPath), StringComparison.OrdinalIgnoreCase) ||
                 Norm(ep.Path).Equals(Norm(ac.RegisterPath), StringComparison.OrdinalIgnoreCase)) &&
                files.Contains("Auth_Tests.cs"))
                return true;
        }

        // 3) CRUD round-trip / security за ресурсом.
        var basePath = IsByIdPath(ep.Path) ? StripId(ep.Path) : ep.Path;
        var resource = LastSegment(basePath);
        var hasRt = files.Contains($"{resource}_RoundTrip_Tests.cs") || files.Contains($"{resource}_Security_Tests.cs");
        if (hasRt)
        {
            if (ep.Method == "POST" && !IsByIdPath(ep.Path)) return true;           // create
            if (IsByIdPath(ep.Path) && ep.Method is "GET" or "PUT" or "DELETE") return true; // by-id
        }
        return false;
    }

    private static string Norm(string p) => "/" + p.Trim('/');
    private static bool IsByIdPath(string path) => path.TrimEnd('/').EndsWith("}");

    private static string StripId(string path)
    {
        var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (segs.Count > 0 && segs[^1].StartsWith('{')) segs.RemoveAt(segs.Count - 1);
        return "/" + string.Join("/", segs);
    }

    private static string LastSegment(string path) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(s => !s.StartsWith('{')) ?? "Resource";
}
