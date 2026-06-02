using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;
using QaAgent.Core;

namespace QaAgent.Swagger;

/// <summary>
/// Завантажує OpenAPI-документ (з URL або файлу) і мапить його у наш <see cref="ApiSpec"/>.
/// </summary>
public sealed class SwaggerParser
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public async Task<ApiSpec> LoadFromUrlAsync(string url, CancellationToken ct = default)
    {
        await using var stream = await Http.GetStreamAsync(url, ct);
        var spec = Parse(stream);
        spec.SourceUrl = url;
        ResolveServerUrl(spec, url);
        return spec;
    }

    /// <summary>Перетворює (можливо відносний) servers[0].url на абсолютний базовий URL.</summary>
    private static void ResolveServerUrl(ApiSpec spec, string swaggerUrl)
    {
        var authority = new Uri(swaggerUrl).GetLeftPart(UriPartial.Authority);
        var server = spec.ServerUrl;

        if (string.IsNullOrWhiteSpace(server) || server == "/")
            spec.ServerUrl = authority;
        else if (server.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            spec.ServerUrl = server.TrimEnd('/');
        else
            spec.ServerUrl = authority + "/" + server.Trim('/');
    }

    public ApiSpec LoadFromFile(string path)
    {
        using var stream = File.OpenRead(path);
        var spec = Parse(stream);
        spec.SourceUrl = path;
        return spec;
    }

    public ApiSpec Parse(Stream stream)
    {
        var doc = new OpenApiStreamReader().Read(stream, out var diagnostic);
        if (diagnostic.Errors.Count > 0)
        {
            var errors = string.Join("; ", diagnostic.Errors.Select(e => e.Message));
            throw new InvalidOperationException($"OpenAPI parse errors: {errors}");
        }
        return Map(doc);
    }

    private static ApiSpec Map(OpenApiDocument doc)
    {
        var spec = new ApiSpec
        {
            Title = doc.Info?.Title ?? string.Empty,
            Version = doc.Info?.Version ?? string.Empty,
            ServerUrl = doc.Servers?.FirstOrDefault()?.Url
        };

        if (doc.Components?.Schemas is { } schemas)
            foreach (var (name, schema) in schemas)
                spec.Schemas[name] = MapSchemaBody(schema);   // повне тіло (не самопосилання)

        foreach (var (path, item) in doc.Paths)
        {
            foreach (var (opType, op) in item.Operations)
            {
                var ep = new EndpointSpec
                {
                    Method = opType.ToString().ToUpperInvariant(),
                    Path = path,
                    OperationId = op.OperationId,
                    Tag = op.Tags?.FirstOrDefault()?.Name,
                    Summary = op.Summary
                };

                if (op.Parameters is { } parameters)
                    foreach (var p in parameters)
                        ep.Parameters.Add(new ParameterSpec
                        {
                            Name = p.Name,
                            In = MapLocation(p.In),
                            Required = p.Required,
                            Schema = p.Schema is null ? new SchemaSpec() : MapSchema(p.Schema)
                        });

                if (op.RequestBody?.Content is { Count: > 0 } content)
                {
                    var media = content.TryGetValue("application/json", out var json)
                        ? new KeyValuePair<string, OpenApiMediaType>("application/json", json)
                        : content.First();

                    ep.RequestBody = new RequestBodySpec
                    {
                        Required = op.RequestBody.Required,
                        ContentType = media.Key,
                        Schema = media.Value.Schema is null ? new SchemaSpec() : MapSchema(media.Value.Schema)
                    };
                }

                foreach (var (code, resp) in op.Responses)
                {
                    var rs = new ResponseSpec { StatusCode = code, Description = resp.Description };
                    if (resp.Content is { Count: > 0 } rc)
                    {
                        var media = rc.TryGetValue("application/json", out var mt) ? mt : rc.First().Value;
                        if (media.Schema is not null) rs.Schema = MapSchema(media.Schema);
                    }
                    ep.Responses.Add(rs);
                }

                spec.Endpoints.Add(ep);
            }
        }

        spec.Endpoints.Sort((a, b) => string.CompareOrdinal(a.Signature, b.Signature));
        spec.Hash = ComputeHash(spec);
        return spec;
    }

    private static SchemaSpec MapSchema(OpenApiSchema s)
    {
        // Якщо це $ref на компонент — лишаємо лише вказівник; повна схема є в ApiSpec.Schemas.
        // Це також рятує від нескінченної рекурсії на самопосильних схемах.
        if (s.Reference?.Id is { } refId)
            return new SchemaSpec { Reference = refId };

        return MapSchemaBody(s);
    }

    /// <summary>Мапить ТІЛО схеми (type/properties/items) без короткого замикання на власному $ref.
    /// Використовується для визначень компонентів, бо Microsoft.OpenApi ставить Reference на сам компонент.</summary>
    private static SchemaSpec MapSchemaBody(OpenApiSchema s)
    {
        var spec = new SchemaSpec
        {
            Type = s.Type,
            Format = s.Format,
            MinLength = s.MinLength,
            MaxLength = s.MaxLength,
            Minimum = s.Minimum.HasValue ? (double)s.Minimum.Value : null,
            Maximum = s.Maximum.HasValue ? (double)s.Maximum.Value : null
        };

        if (s.Required is { Count: > 0 })
            spec.Required = s.Required.ToList();

        if (s.Enum is { Count: > 0 })
            spec.Enum = s.Enum.Select(e => e?.ToString() ?? string.Empty).ToList();

        if (s.Properties is { Count: > 0 })
            foreach (var (name, ps) in s.Properties)
                spec.Properties[name] = MapSchema(ps);

        if (s.Items is not null)
            spec.Items = MapSchema(s.Items);

        return spec;
    }

    private static ParamLocation MapLocation(ParameterLocation? loc) => loc switch
    {
        ParameterLocation.Query => ParamLocation.Query,
        ParameterLocation.Header => ParamLocation.Header,
        ParameterLocation.Cookie => ParamLocation.Cookie,
        _ => ParamLocation.Path
    };

    private static string ComputeHash(ApiSpec spec)
    {
        var canonical = JsonSerializer.Serialize(
            new { spec.Title, spec.Version, spec.Endpoints, spec.Schemas });
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes);
    }
}
