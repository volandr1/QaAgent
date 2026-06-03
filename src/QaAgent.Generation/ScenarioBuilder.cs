using System.Text.Json;
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

    /// <summary>
    /// Smoke для write-ендпоінтів: DELETE неіснуючого ресурсу — ендпоінт існує і не падає (не 5xx).
    /// Неруйнівне (id не існує). Точний код (404 vs 204) у різних API різний — тому лише «не 5xx».
    /// </summary>
    public static IEnumerable<TestScenario> WriteSmokeScenarios(EndpointSpec ep)
    {
        if (ep.Method == "DELETE" && ep.Auth == AuthRequirement.Anonymous &&
            ep.Parameters.Any(p => p.In == ParamLocation.Path))
        {
            yield return new TestScenario
            {
                Name = $"{BaseName(ep)}_NonExistent_NoServerError",
                Description = "DELETE неіснуючого ресурсу: ендпоінт існує, без серверної помилки.",
                Method = ep.Method,
                Path = ep.Path,
                PathParams = DummyPathParams(ep),
                Auth = "none",
                NoServerError = true,
                Type = "smoke"
            };
        }
    }

    public static IEnumerable<TestScenario> BoundaryScenarios(EndpointSpec ep)
    {
        // GET за неіснуючим числовим id (анонімний) → 404.
        var intPathParam = ep.Parameters.FirstOrDefault(p =>
            p.In == ParamLocation.Path &&
            (p.Schema.Type == "integer" || p.Schema.Format is "int32" or "int64"));

        // Колекційні ендпоінти (2xx повертає масив) на неіснуючий батьк. id дають 200+[], а не 404.
        var success = ep.Responses.FirstOrDefault(r => r.StatusCode.StartsWith("2"));
        var returnsArray = success?.Schema?.Type == "array";

        if (ep.Method == "GET" && ep.Auth == AuthRequirement.Anonymous && intPathParam is not null && !returnsArray)
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

    /// <summary>
    /// Negative-сценарії НА ПРАВИЛАХ зі схеми (без LLM): порушуємо лише реальні обмеження —
    /// відсутнє required-поле, поганий format=email, закоротке значення (minLength).
    /// Якщо обмежень немає — нічого не генеруємо (уникаємо шуму на API без валідації).
    /// </summary>
    public static IEnumerable<TestScenario> NegativeScenarios(EndpointSpec ep, ApiSpec api)
    {
        if (ep.RequestBody is null) yield break;

        var schema = Resolve(ep.RequestBody.Schema, api);
        if (schema.Properties.Count == 0) yield break;

        var baseline = SchemaSkeleton.BuildObject(ep.RequestBody.Schema, api);
        if (baseline is null) yield break;

        var pathParams = DummyPathParams(ep);

        // 1) Відсутнє обовʼязкове поле (до 2-х).
        foreach (var req in schema.Required.Take(2))
        {
            var body = new Dictionary<string, object?>(baseline);
            body.Remove(req);
            yield return Negative(ep, pathParams, body, $"Missing_{Clean(req)}", $"Відсутнє обовʼязкове поле '{req}'");
        }

        // 2) Невалідний email (тільки якщо поле справді email).
        var email = schema.Properties.FirstOrDefault(p => Resolve(p.Value, api).Format == "email");
        if (email.Key is not null)
        {
            var body = new Dictionary<string, object?>(baseline) { [email.Key] = "not-an-email" };
            yield return Negative(ep, pathParams, body, $"InvalidEmail_{Clean(email.Key)}", $"Невалідний формат email у '{email.Key}'");
        }

        // 3) Закоротке значення (minLength).
        var shortField = schema.Properties.FirstOrDefault(p => Resolve(p.Value, api).MinLength is > 0);
        if (shortField.Key is not null)
        {
            var body = new Dictionary<string, object?>(baseline) { [shortField.Key] = "x" };
            yield return Negative(ep, pathParams, body, $"TooShort_{Clean(shortField.Key)}", $"Закоротке значення '{shortField.Key}'");
        }

        // 4) Невірний ТИП даних (рядок замість числа/булеана тощо) — до 2-х полів.
        var typed = schema.Properties
            .Where(p => baseline.ContainsKey(p.Key))
            .Select(p => (p.Key, Type: Resolve(p.Value, api).Type))
            .Where(p => p.Type is "integer" or "number" or "boolean" or "string")
            .Take(2);
        foreach (var (key, type) in typed)
        {
            object wrong = type switch
            {
                "integer" or "number" => "not-a-number", // рядок замість числа
                "boolean" => "not-a-bool",               // рядок замість булеана
                _ => 123456                              // число замість рядка
            };
            var body = new Dictionary<string, object?>(baseline) { [key] = wrong };
            yield return Negative(ep, pathParams, body, $"WrongType_{Clean(key)}", $"Невірний тип даних у полі '{key}'");
        }

        // 5) null у обовʼязковому полі — до 2-х полів (відрізняється від «поле відсутнє»).
        foreach (var req in schema.Required.Where(baseline.ContainsKey).Take(2))
        {
            var body = new Dictionary<string, object?>(baseline) { [req] = null };
            yield return Negative(ep, pathParams, body, $"NullRequired_{Clean(req)}", $"null у обовʼязковому полі '{req}'");
        }

        // 6) Вихід за числові межі minimum/maximum.
        foreach (var p in schema.Properties.Where(p => baseline.ContainsKey(p.Key)))
        {
            var t = Resolve(p.Value, api);
            if (t.Minimum is { } min)
            {
                var body = new Dictionary<string, object?>(baseline) { [p.Key] = min - 1 };
                yield return Negative(ep, pathParams, body, $"BelowMin_{Clean(p.Key)}", $"Значення нижче minimum у '{p.Key}'");
            }
            if (t.Maximum is { } max)
            {
                var body = new Dictionary<string, object?>(baseline) { [p.Key] = max + 1 };
                yield return Negative(ep, pathParams, body, $"AboveMax_{Clean(p.Key)}", $"Значення вище maximum у '{p.Key}'");
            }
        }
    }

    private static TestScenario Negative(EndpointSpec ep, Dictionary<string, string> pathParams,
        Dictionary<string, object?> body, string name, string description) => new()
    {
        Name = name,
        Description = description,
        Method = ep.Method,
        Path = ep.Path,
        PathParams = new Dictionary<string, string>(pathParams),
        Body = JsonSerializer.SerializeToElement(body),
        ClientErrorRange = true,
        Auth = "none",
        Type = "negative"
    };

    private static SchemaSpec Resolve(SchemaSpec s, ApiSpec api) =>
        s.Reference is { } r && api.Schemas.TryGetValue(r, out var t) ? t : s;

    private static string Clean(string s) =>
        new string(s.Where(char.IsLetterOrDigit).ToArray()) is { Length: > 0 } c
            ? char.ToUpperInvariant(c[0]) + c[1..] : "Field";

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
