using System.Text.Json;
using QaAgent.Core;

namespace QaAgent.Swagger;

public sealed class EndpointChange
{
    public string Signature { get; set; } = string.Empty;
    public List<string> Changes { get; set; } = new();
}

public sealed class EndpointRename
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
}

/// <summary>Результат порівняння двох знімків API.</summary>
public sealed class ApiDiff
{
    public List<string> AddedEndpoints { get; set; } = new();
    public List<string> RemovedEndpoints { get; set; } = new();
    public List<EndpointChange> ChangedEndpoints { get; set; } = new();
    public List<EndpointRename> Renames { get; set; } = new();
    public List<string> SchemaChanges { get; set; } = new();

    public bool HasChanges =>
        AddedEndpoints.Count > 0 || RemovedEndpoints.Count > 0 ||
        ChangedEndpoints.Count > 0 || Renames.Count > 0 || SchemaChanges.Count > 0;
}

/// <summary>
/// Структурний diff між попереднім і поточним <see cref="ApiSpec"/>.
/// Саме він тригерить таргетний self-healing: лагодимо лише змінені ендпоінти.
/// </summary>
public static class DiffEngine
{
    public static ApiDiff Diff(ApiSpec? previous, ApiSpec current)
    {
        var diff = new ApiDiff();

        var oldEps = (previous?.Endpoints ?? new()).ToDictionary(e => e.Signature);
        var curEps = current.Endpoints.ToDictionary(e => e.Signature);

        foreach (var sig in curEps.Keys.Where(k => !oldEps.ContainsKey(k)))
            diff.AddedEndpoints.Add(sig);

        foreach (var sig in oldEps.Keys.Where(k => !curEps.ContainsKey(k)))
            diff.RemovedEndpoints.Add(sig);

        foreach (var sig in curEps.Keys.Where(oldEps.ContainsKey))
        {
            var changes = CompareEndpoint(oldEps[sig], curEps[sig]);
            if (changes.Count > 0)
                diff.ChangedEndpoints.Add(new EndpointChange { Signature = sig, Changes = changes });
        }

        DetectRenames(diff);

        var oldSchemas = previous?.Schemas ?? new();
        foreach (var (name, schema) in current.Schemas)
        {
            if (!oldSchemas.TryGetValue(name, out var prev))
                diff.SchemaChanges.Add($"+ {name} (added)");
            else if (Canon(schema) != Canon(prev))
                diff.SchemaChanges.Add($"~ {name} (modified)");
        }
        foreach (var name in oldSchemas.Keys.Where(n => !current.Schemas.ContainsKey(n)))
            diff.SchemaChanges.Add($"- {name} (removed)");

        return diff;
    }

    /// <summary>
    /// Евристика перейменування: якщо для одного HTTP-методу рівно один ендпоінт зник
    /// і рівно один зʼявився — вважаємо це перейменуванням шляху (підказка для self-healing).
    /// </summary>
    private static void DetectRenames(ApiDiff diff)
    {
        var removedByMethod = diff.RemovedEndpoints.GroupBy(s => s.Split(' ')[0]);
        var addedByMethod = diff.AddedEndpoints.ToLookup(s => s.Split(' ')[0]);

        foreach (var group in removedByMethod)
        {
            var added = addedByMethod[group.Key].ToList();
            if (group.Count() == 1 && added.Count == 1)
            {
                var from = group.Single();
                var to = added.Single();
                diff.Renames.Add(new EndpointRename { From = from, To = to });
                diff.RemovedEndpoints.Remove(from);
                diff.AddedEndpoints.Remove(to);
            }
        }
    }

    private static List<string> CompareEndpoint(EndpointSpec a, EndpointSpec b)
    {
        var changes = new List<string>();

        var ap = a.Parameters.ToDictionary(p => p.Name);
        var bp = b.Parameters.ToDictionary(p => p.Name);
        foreach (var n in bp.Keys.Where(k => !ap.ContainsKey(k))) changes.Add($"параметр +{n}");
        foreach (var n in ap.Keys.Where(k => !bp.ContainsKey(k))) changes.Add($"параметр -{n}");
        foreach (var n in bp.Keys.Where(ap.ContainsKey))
            if (Canon(ap[n]) != Canon(bp[n])) changes.Add($"параметр ~{n}");

        if (Canon(a.RequestBody) != Canon(b.RequestBody))
            changes.Add("тіло запиту змінено");

        var ar = a.Responses.Select(r => r.StatusCode).OrderBy(x => x);
        var br = b.Responses.Select(r => r.StatusCode).OrderBy(x => x);
        if (!ar.SequenceEqual(br))
            changes.Add("коди відповідей змінено");

        return changes;
    }

    private static string Canon(object? o) => o is null ? string.Empty : JsonSerializer.Serialize(o);
}
