using System.Text.Json;
using System.Text.Json.Serialization;
using QaAgent.Core;

namespace QaAgent.Swagger;

/// <summary>
/// Зберігає/читає знімок <see cref="ApiSpec"/> у JSON-файл — основа для diff і self-healing.
/// </summary>
public sealed class SnapshotStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;

    public SnapshotStore(string path) => _path = path;

    public string Path => _path;
    public bool Exists => File.Exists(_path);

    public async Task SaveAsync(ApiSpec spec, CancellationToken ct = default)
    {
        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(_path, JsonSerializer.Serialize(spec, Json), ct);
    }

    public async Task<ApiSpec?> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path)) return null;
        var text = await File.ReadAllTextAsync(_path, ct);
        return JsonSerializer.Deserialize<ApiSpec>(text, Json);
    }
}
