namespace QaAgent.Probing;

/// <summary>
/// Контракт авторизації цільового API (специфічний для застосунку).
/// Дефолти налаштовані під OnlineLibrary.
/// </summary>
public sealed class AuthConfig
{
    public string RegisterPath { get; set; } = "/api/Auth/register";
    public string LoginPath { get; set; } = "/api/Auth/login";
    public string EmailField { get; set; } = "email";
    public string PasswordField { get; set; } = "password";
    public string TokenField { get; set; } = "token";
    public string Password { get; set; } = "Passw0rd!";
}

/// <summary>Облікові дані одного тестового користувача та його роль/токен.</summary>
public sealed class ProbeUser
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? Token { get; set; }
    public string? Role { get; set; }
}

/// <summary>
/// Результат auth-probing: тестові користувачі та чи доступний admin.
/// Токени короткоживучі — у snapshot НЕ зберігаються.
/// </summary>
public sealed class AuthContext
{
    public ProbeUser? Client { get; set; }
    public ProbeUser? Admin { get; set; }

    public bool HasClient => !string.IsNullOrEmpty(Client?.Token);
    public bool HasAdmin => !string.IsNullOrEmpty(Admin?.Token);
}
