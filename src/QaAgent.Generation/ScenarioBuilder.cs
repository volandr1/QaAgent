using QaAgent.Core;

namespace QaAgent.Generation;

/// <summary>
/// Детерміновані сценарії, які НЕ потребують LLM: auth (401/403) та boundary (404).
/// Будуються напряму з матриці доступу та схеми — максимально надійно.
/// Неруйнівні: для path-параметрів використовується неіснуючий id.
/// </summary>
public static class ScenarioBuilder
{
    private const string DummyId = "999999999";

    public static IEnumerable<TestScenario> AuthScenarios(EndpointSpec ep)
    {
        if (ep.Auth is AuthRequirement.Anonymous or AuthRequirement.Unknown)
            yield break;

        var baseName = BaseName(ep);

        // Будь-який захищений ендпоінт без токена → 401.
        yield return new TestScenario
        {
            Name = $"{baseName}_NoToken_Returns401",
            Description = "Без токена доступ заборонено (401).",
            Method = ep.Method,
            Path = ep.Path,
            PathParams = DummyPathParams(ep),
            Auth = "none",
            ExpectedStatus = 401,
            Type = "auth"
        };

        // Admin-ендпоінт із токеном звичайного користувача → 403.
        if (ep.Auth == AuthRequirement.Admin)
            yield return new TestScenario
            {
                Name = $"{baseName}_ClientToken_Returns403",
                Description = "Client не має прав на Admin-ендпоінт (403).",
                Method = ep.Method,
                Path = ep.Path,
                PathParams = DummyPathParams(ep),
                Auth = "client",
                ExpectedStatus = 403,
                Type = "auth"
            };
    }

    /// <summary>
    /// Positive-доступ Admin до read-only GET-ендпоінтів (без тіла й path-параметрів) → 200.
    /// Потребує засіяного admin (команда `seed`). Детерміновано, без LLM.
    /// </summary>
    public static IEnumerable<TestScenario> AdminPositiveScenarios(EndpointSpec ep)
    {
        if (ep.Auth == AuthRequirement.Admin && ep.Method == "GET" && ep.RequestBody is null &&
            ep.Parameters.All(p => p.In != ParamLocation.Path))
        {
            yield return new TestScenario
            {
                Name = $"{BaseName(ep)}_AsAdmin_Returns200",
                Description = "Admin має доступ до ендпоінта (200).",
                Method = ep.Method,
                Path = ep.Path,
                Auth = "admin",
                ExpectedStatus = 200,
                Type = "positive"
            };
        }
    }

    public static IEnumerable<TestScenario> BoundaryScenarios(EndpointSpec ep)
    {
        // GET за неіснуючим числовим id (анонімний) → 404.
        var intPathParam = ep.Parameters.FirstOrDefault(p =>
            p.In == ParamLocation.Path &&
            (p.Schema.Type == "integer" || p.Schema.Format is "int32" or "int64"));

        if (ep.Method == "GET" && ep.Auth == AuthRequirement.Anonymous && intPathParam is not null)
        {
            yield return new TestScenario
            {
                Name = $"{BaseName(ep)}_NonExistentId_Returns404",
                Description = "Запит неіснуючого ресурсу повертає 404.",
                Method = ep.Method,
                Path = ep.Path,
                PathParams = DummyPathParams(ep),
                Auth = "none",
                ExpectedStatus = 404,
                Type = "boundary"
            };
        }
    }

    private static Dictionary<string, string> DummyPathParams(EndpointSpec ep) =>
        ep.Parameters
            .Where(p => p.In == ParamLocation.Path)
            .ToDictionary(p => p.Name, _ => DummyId);

    private static string BaseName(EndpointSpec ep)
    {
        var lastSegment = ep.Path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(s => !s.StartsWith('{')) ?? "Endpoint";
        return $"{Capitalize(ep.Method.ToLowerInvariant())}_{Capitalize(lastSegment)}";
    }

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
