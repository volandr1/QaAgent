using System.Text.Json;
using QaAgent.Core;

namespace QaAgent.Generation;

/// <summary>
/// Будує приклад JSON-тіла зі схеми (резолвить $ref). Подається LLM як ЖОРСТКА структура:
/// модель лише заповнює значення, не змінюючи набір ключів — це різко підвищує дотримання схеми.
/// </summary>
public static class SchemaSkeleton
{
    public static string Build(SchemaSpec schema, ApiSpec api)
    {
        var node = BuildNode(schema, api, 0);
        return JsonSerializer.Serialize(node);
    }

    /// <summary>Граф валідного прикладу тіла як словник (для мутацій у negative-сценаріях).</summary>
    public static Dictionary<string, object?>? BuildObject(SchemaSpec schema, ApiSpec api) =>
        BuildNode(schema, api, 0) as Dictionary<string, object?>;

    private static object? BuildNode(SchemaSpec schema, ApiSpec api, int depth)
    {
        if (depth > 6) return null;

        var target = schema;
        if (schema.Reference is { } refName && api.Schemas.TryGetValue(refName, out var resolved))
            target = resolved;

        if (target.Properties.Count > 0)
        {
            var obj = new Dictionary<string, object?>();
            foreach (var (key, prop) in target.Properties)
                obj[key] = BuildNode(prop, api, depth + 1);
            return obj;
        }

        if (target.Type == "array")
            return new[] { target.Items is null ? (object?)"value" : BuildNode(target.Items, api, depth + 1) };

        return target.Type switch
        {
            "integer" => 0,
            "number" => 0,
            "boolean" => false,
            _ => FormatValue(target.Format)
        };
    }

    /// <summary>Валідне значення-зразок для рядкових форматів (дати, guid, email тощо).</summary>
    private static string FormatValue(string? format) => format switch
    {
        "email" => "user@example.com",
        "date-time" => "2024-01-01T00:00:00Z",
        "date" => "2024-01-01",
        "uuid" or "guid" => "00000000-0000-0000-0000-000000000000",
        "uri" or "url" => "https://example.com",
        "byte" => "U3RyaW5n",
        "binary" => "U3RyaW5n",
        _ => "string"
    };
}
