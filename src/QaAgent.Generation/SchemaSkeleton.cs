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
            _ => target.Format == "email" ? "user@example.com" : "string"
        };
    }
}
