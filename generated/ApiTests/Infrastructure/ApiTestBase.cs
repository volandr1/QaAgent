using Microsoft.Playwright;

namespace ApiTests.Infrastructure;

/// <summary>
/// Базовий клас для згенерованих API-тестів.
/// Піднімає Playwright APIRequestContext із базовим URL (browser-binaries НЕ потрібні).
/// Надає рантайм-токени (client/admin) для auth-залежних тестів.
/// </summary>
public abstract class ApiTestBase
{
    protected IPlaywright PlaywrightInstance = null!;
    protected IAPIRequestContext Api = null!;

    private string? _clientToken;
    private string? _adminToken;

    protected static string BaseUrl =>
        Environment.GetEnvironmentVariable("API_BASE_URL") ?? "http://localhost:5234";

    // Фіксовані креди admin (для positive-тестів Admin-ендпоінтів).
    // Адмін має бути засіяний заздалегідь (команда `seed`); тут лише логін.
    private static string AdminEmail =>
        Environment.GetEnvironmentVariable("QA_ADMIN_EMAIL") ?? "qa.admin@example.com";
    private static string AdminPassword =>
        Environment.GetEnvironmentVariable("QA_ADMIN_PASSWORD") ?? "Passw0rd!";

    [SetUp]
    public async Task SetUpApi()
    {
        PlaywrightInstance = await Playwright.CreateAsync();
        Api = await PlaywrightInstance.APIRequest.NewContextAsync(new APIRequestNewContextOptions
        {
            // Кінцевий "/" обовʼязковий: інакше відносний шлях скидає базовий шлях (напр. /api/v3).
            BaseURL = BaseUrl.TrimEnd('/') + "/",
            IgnoreHTTPSErrors = true
        });
    }

    [TearDown]
    public async Task TearDownApi()
    {
        await Api.DisposeAsync();
        PlaywrightInstance.Dispose();
    }

    /// <summary>Токен свіжозареєстрованого звичайного користувача (роль Client).</summary>
    protected async Task<string> GetClientTokenAsync()
    {
        if (_clientToken is not null) return _clientToken;

        var email = $"qa.client+{Guid.NewGuid():N}@example.com";
        _clientToken = await RegisterAndLoginAsync(email, "Passw0rd!", register: true);
        return _clientToken;
    }

    /// <summary>Токен адміністратора (логін фіксованими seed-кредами).</summary>
    protected async Task<string> GetAdminTokenAsync()
    {
        if (_adminToken is not null) return _adminToken;

        _adminToken = await RegisterAndLoginAsync(AdminEmail, AdminPassword, register: false);
        return _adminToken;
    }

    private async Task<string> RegisterAndLoginAsync(string email, string password, bool register)
    {
        var creds = new { email, password };

        if (register)
            await Api.PostAsync("/api/Auth/register", new APIRequestContextOptions { DataObject = creds });

        var resp = await Api.PostAsync("/api/Auth/login", new APIRequestContextOptions { DataObject = creds });
        Assert.That(resp.Ok, Is.True, $"Логін {email} не вдався: {(int)resp.Status}");

        var json = await resp.JsonAsync();
        return json!.Value.GetProperty("token").GetString()!;
    }
}
