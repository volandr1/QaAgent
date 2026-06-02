using System.Text;
using QaAgent.Probing;

namespace QaAgent.App;

/// <summary>
/// Опис цільового API: ім'я (= папка/неймспейс тестів), Swagger URL та модель авторизації.
/// </summary>
public sealed class TargetConfig
{
    public required string Name { get; init; }
    public required string SwaggerUrl { get; init; }

    /// <summary>Чи робити auth-probing (register/login). Для публічних API — false.</summary>
    public bool ProbeAuth { get; init; }

    /// <summary>Контракт авторизації (коли ProbeAuth=true).</summary>
    public AuthConfig? Auth { get; init; }

    /// <summary>Чи генерувати negative-тести (API має валідувати ввід).</summary>
    public bool GenerateNegatives { get; init; } = true;

    /// <summary>Чи покривати write-ендпоінти (POST create / PUT negative / DELETE smoke). МУТУЄ дані.</summary>
    public bool CoverWrites { get; init; }

    public string BaseUrl => new Uri(SwaggerUrl).GetLeftPart(UriPartial.Authority);

    public static readonly IReadOnlyList<TargetConfig> BuiltIn = new[]
    {
        new TargetConfig
        {
            Name = "Library",
            SwaggerUrl = "http://localhost:5234/swagger/v1/swagger.json",
            ProbeAuth = true,
            Auth = new AuthConfig(),       // дефолти OnlineLibrary
            GenerateNegatives = true,
            CoverWrites = true             // CRUD round-trip (з admin/client-токенами)
        },
        new TargetConfig
        {
            Name = "Petstore",
            SwaggerUrl = "https://petstore3.swagger.io/api/v3/openapi.json",
            ProbeAuth = false,             // демо не вимагає авторизації
            GenerateNegatives = false      // демо слабо валідує — negative дали б шум
        }
    };

    public static TargetConfig Get(string name) =>
        BuiltIn.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? BuiltIn[0];

    /// <summary>
    /// Створює таргет для ДОВІЛЬНОГО Swagger/OpenAPI URL. Auth і negative вимкнені за
    /// замовчуванням (контракт невідомий). Ім'я виводиться з хоста, якщо не задане.
    /// </summary>
    public static TargetConfig FromUrl(string swaggerUrl, string? name = null) => new()
    {
        Name = string.IsNullOrWhiteSpace(name) ? DeriveName(swaggerUrl) : Sanitize(name!),
        SwaggerUrl = swaggerUrl,
        ProbeAuth = false,
        GenerateNegatives = true,   // повне покриття: і negative для write
        CoverWrites = true          // positive-create / PUT-negative / DELETE-smoke
    };

    private static string DeriveName(string url)
    {
        try { return Sanitize(new Uri(url).Host); }
        catch { return "Api"; }
    }

    /// <summary>Перетворює рядок на валідний ідентифікатор (для папки/неймспейсу тестів).</summary>
    public static string Sanitize(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw) sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        var s = sb.ToString().Trim('_');
        while (s.Contains("__")) s = s.Replace("__", "_");
        if (s.Length == 0) s = "Api";
        if (char.IsDigit(s[0])) s = "Api_" + s;
        return char.ToUpperInvariant(s[0]) + s[1..];
    }
}
