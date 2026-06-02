namespace QaAgent.Core;

/// <summary>
/// Рівень авторизації ендпоінта. Зі Swagger-схеми зазвичай НЕвідомий
/// (глобальний security вішається на всі операції), тож заповнюється
/// пізніше через auth-probing або аналіз вихідного коду.
/// </summary>
public enum AuthRequirement
{
    Unknown,
    Anonymous,
    Authenticated,
    Admin
}

public enum ParamLocation
{
    Path,
    Query,
    Header,
    Cookie
}

/// <summary>
/// Нормалізоване представлення JSON-схеми (тіло запиту, параметр, властивість).
/// Містить обмеження, потрібні для negative/boundary-тестів.
/// </summary>
public sealed class SchemaSpec
{
    public string? Type { get; set; }
    public string? Format { get; set; }

    /// <summary>Ім'я компонента, якщо це посилання ($ref) на components/schemas.</summary>
    public string? Reference { get; set; }

    public List<string> Required { get; set; } = new();
    public Dictionary<string, SchemaSpec> Properties { get; set; } = new();
    public SchemaSpec? Items { get; set; }

    public int? MinLength { get; set; }
    public int? MaxLength { get; set; }
    public double? Minimum { get; set; }
    public double? Maximum { get; set; }
    public List<string>? Enum { get; set; }
}

public sealed class ParameterSpec
{
    public string Name { get; set; } = string.Empty;
    public ParamLocation In { get; set; }
    public bool Required { get; set; }
    public SchemaSpec Schema { get; set; } = new();
}

public sealed class RequestBodySpec
{
    public bool Required { get; set; }
    public string ContentType { get; set; } = "application/json";
    public SchemaSpec Schema { get; set; } = new();
}

public sealed class ResponseSpec
{
    public string StatusCode { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>Схема тіла відповіді (для розрізнення об'єкт vs масив/колекція).</summary>
    public SchemaSpec? Schema { get; set; }
}

/// <summary>
/// Один ендпоінт API (HTTP-метод + шлях) з усіма деталями для генерації тестів.
/// </summary>
public sealed class EndpointSpec
{
    public string Method { get; set; } = string.Empty;   // GET, POST, PUT, DELETE...
    public string Path { get; set; } = string.Empty;      // /api/Books/{id}
    public string? OperationId { get; set; }
    public string? Tag { get; set; }
    public string? Summary { get; set; }

    public List<ParameterSpec> Parameters { get; set; } = new();
    public RequestBodySpec? RequestBody { get; set; }
    public List<ResponseSpec> Responses { get; set; } = new();

    public AuthRequirement Auth { get; set; } = AuthRequirement.Unknown;

    /// <summary>Унікальний підпис ендпоінта для зіставлення під час diff.</summary>
    public string Signature => $"{Method} {Path}";
}

/// <summary>
/// Повний знімок API: метадані, список ендпоінтів, компоненти-схеми та hash.
/// Серіалізується у snapshot для подальшого diff і self-healing.
/// </summary>
public sealed class ApiSpec
{
    public string Title { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string? SourceUrl { get; set; }

    /// <summary>Базовий URL сервера (з OpenAPI `servers`), напр. https://host/api/v3.</summary>
    public string? ServerUrl { get; set; }
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<EndpointSpec> Endpoints { get; set; } = new();
    public Dictionary<string, SchemaSpec> Schemas { get; set; } = new();

    /// <summary>SHA-256 від канонічного вмісту (без CapturedAt) — швидкий "змінилось/ні".</summary>
    public string Hash { get; set; } = string.Empty;
}
