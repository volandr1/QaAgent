using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using QaAgent.Core;
using QaAgent.Llm;

namespace QaAgent.Generation;

/// <summary>
/// Генерує тест-сценарії (як JSON) для ендпоінта за допомогою LLM.
/// На Етапі 2 — лише positive (happy path).
/// </summary>
public sealed class TestGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private const string SystemPrompt =
        "You are a senior QA automation engineer. " +
        "You design API test scenarios and return STRICTLY valid JSON only — no markdown, no prose, no code fences. " +
        "Output must match the requested JSON schema exactly.";

    private readonly LlmClient _llm;

    public TestGenerator(LlmClient llm) => _llm = llm;

    public Task<List<TestScenario>> GeneratePositiveAsync(
        EndpointSpec endpoint, ApiSpec api, string authMode = "none", CancellationToken ct = default) =>
        RequestScenariosAsync(BuildPositivePrompt(endpoint, api), endpoint, "positive", authMode, ct);

    public Task<List<TestScenario>> GenerateNegativeAsync(
        EndpointSpec endpoint, ApiSpec api, CancellationToken ct = default) =>
        RequestScenariosAsync(BuildNegativePrompt(endpoint, api), endpoint, "negative", "none", ct);

    private async Task<List<TestScenario>> RequestScenariosAsync(
        string prompt, EndpointSpec endpoint, string defaultType, string authMode, CancellationToken ct)
    {
        var raw = await _llm.AskAsync(SystemPrompt, prompt, ct);
        var json = JsonExtractor.Extract(raw);
        var set = JsonSerializer.Deserialize<ScenarioSet>(json, JsonOptions) ?? new ScenarioSet();

        var index = 1;
        foreach (var s in set.Scenarios)
        {
            // Метод, шлях і ІМʼЯ — детерміновані з ендпоінта (модель лише постачає дані).
            // Стабільні імена прибирають галюцинації (напр. "GetBooks" для Petstore) і
            // дають коректний матчинг тестів у Compare між прогонами.
            s.Method = endpoint.Method;
            s.Path = endpoint.Path;
            if (string.IsNullOrWhiteSpace(s.Type)) s.Type = defaultType;
            s.Auth = authMode;
            s.Name = $"{NameBase(endpoint)}_{Cap(s.Type)}_{index++}";

            // Заповнюємо відсутні path-параметри dummy-значенням (для PUT/{id} тощо).
            foreach (var p in endpoint.Parameters.Where(p => p.In == ParamLocation.Path))
                if (!s.PathParams.ContainsKey(p.Name))
                    s.PathParams[p.Name] = "999999999";

            // Для negative точний код валідації непередбачуваний → асертимо 4xx-діапазон.
            if (defaultType == "negative") s.ClientErrorRange = true;
        }
        return set.Scenarios;
    }

    private static string BuildNegativePrompt(EndpointSpec ep, ApiSpec api)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Generate NEGATIVE API test scenarios: INVALID request bodies that the API MUST REJECT.");
        sb.AppendLine();
        sb.AppendLine($"Endpoint: {ep.Method} {ep.Path}");
        if (ep.RequestBody is { } body)
        {
            sb.AppendLine($"Request body ({body.ContentType}):");
            sb.AppendLine(DescribeSchema(body.Schema, api, "  "));
            sb.AppendLine("Valid body template (these are the ONLY valid keys — keep key names, make VALUES invalid):");
            sb.AppendLine(SchemaSkeleton.Build(body.Schema, api));
        }
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Each scenario MUST violate a validation rule OR use invalid credentials.");
        sb.AppendLine("- Validation failures (missing required field, value too short, wrong format like bad email)");
        sb.AppendLine("  => expectedStatus = 400.");
        sb.AppendLine("- Invalid login credentials (authentication endpoints) => expectedStatus = 401.");
        sb.AppendLine("- Put the concrete invalid JSON in \"body\". Make it CLEARLY invalid (do not produce valid data).");
        sb.AppendLine("- Do NOT rely on existing data (no duplicate-record scenarios).");
        sb.AppendLine("- 2 to 4 scenarios. Name must be a valid C# method identifier. type = \"negative\".");
        sb.AppendLine();
        sb.AppendLine("Return ONLY this JSON shape:");
        sb.AppendLine("""
        {
          "scenarios": [
            {
              "name": "Register_ShortPassword_Returns400",
              "description": "short text",
              "method": "POST",
              "path": "/api/Auth/register",
              "pathParams": {},
              "queryParams": {},
              "body": { "email": "user@example.com", "password": "123" },
              "expectedStatus": 400,
              "type": "negative"
            }
          ]
        }
        """);
        return sb.ToString();
    }

    private static string BuildPositivePrompt(EndpointSpec ep, ApiSpec api)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Generate POSITIVE (happy-path) API test scenarios for the endpoint below.");
        sb.AppendLine();
        sb.AppendLine($"Endpoint: {ep.Method} {ep.Path}");
        if (!string.IsNullOrWhiteSpace(ep.Tag)) sb.AppendLine($"Tag: {ep.Tag}");

        if (ep.Parameters.Count > 0)
        {
            sb.AppendLine("Parameters:");
            foreach (var p in ep.Parameters)
                sb.AppendLine($"  - {p.Name} (in={p.In}, required={p.Required}, type={p.Schema.Type ?? p.Schema.Reference})");
        }

        if (ep.RequestBody is { } body)
        {
            sb.AppendLine($"Request body ({body.ContentType}):");
            sb.AppendLine(DescribeSchema(body.Schema, api, "  "));
            sb.AppendLine("Body JSON template — use EXACTLY these keys (keep arrays as arrays, fill realistic valid values):");
            sb.AppendLine(SchemaSkeleton.Build(body.Schema, api));
        }

        var codes = string.Join(", ", ep.Responses.Select(r => r.StatusCode));
        sb.AppendLine($"Documented response codes: {codes}");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Only valid, realistic inputs that should SUCCEED (2xx).");
        sb.AppendLine("- Use ONLY the documented body fields above — do NOT invent or rename fields.");
        sb.AppendLine("- Provide concrete values for every required parameter and body field.");
        sb.AppendLine("- 1 to 2 scenarios is enough.");
        sb.AppendLine("- expectedStatus must be the success code (usually 200).");
        sb.AppendLine("- Name must be a valid C# method identifier (letters/digits/underscore).");
        sb.AppendLine();
        sb.AppendLine("Return ONLY this JSON shape:");
        sb.AppendLine("""
        {
          "scenarios": [
            {
              "name": "GetBooks_ReturnsOk",
              "description": "short text",
              "method": "GET",
              "path": "/api/Books",
              "pathParams": {},
              "queryParams": {},
              "body": null,
              "expectedStatus": 200,
              "type": "positive"
            }
          ]
        }
        """);
        return sb.ToString();
    }

    private static string NameBase(EndpointSpec ep)
    {
        var seg = ep.Path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(s => !s.StartsWith('{')) ?? "Endpoint";
        return Cap(ep.Method.ToLowerInvariant()) + "_" + Cap(seg);
    }

    private static string Cap(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string DescribeSchema(SchemaSpec schema, ApiSpec api, string indent)
    {
        var target = schema;
        if (schema.Reference is { } refName && api.Schemas.TryGetValue(refName, out var resolved))
            target = resolved;

        var sb = new StringBuilder();
        if (target.Properties.Count == 0)
        {
            sb.Append(indent).Append("type: ").Append(target.Type ?? "object");
            return sb.ToString();
        }

        foreach (var (name, prop) in target.Properties)
        {
            var req = target.Required.Contains(name) ? "required" : "optional";
            var constraints = new List<string>();
            if (prop.MinLength is { } ml) constraints.Add($"minLength={ml}");
            if (prop.MaxLength is { } xl) constraints.Add($"maxLength={xl}");
            if (!string.IsNullOrEmpty(prop.Format)) constraints.Add($"format={prop.Format}");
            var c = constraints.Count > 0 ? $" [{string.Join(", ", constraints)}]" : "";
            sb.Append(indent).AppendLine($"- {name} ({prop.Type ?? prop.Reference}, {req}){c}");
        }
        return sb.ToString().TrimEnd();
    }
}
