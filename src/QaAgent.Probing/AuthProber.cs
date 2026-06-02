using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using QaAgent.Core;

namespace QaAgent.Probing;

/// <summary>
/// Емпірично визначає матрицю доступу API: реєструє двох користувачів
/// (другий гарантовано Client), і за кодами 401/403 класифікує кожен ендпоінт.
/// Неруйнівне: dummy-id для path-параметрів, admin-токен у запитах НЕ використовується.
/// </summary>
public sealed class AuthProber
{
    private const string DummyId = "999999999";

    private readonly HttpClient _http;
    private readonly AuthConfig _config;

    public AuthProber(string baseUrl, AuthConfig? config = null)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
        _config = config ?? new AuthConfig();
    }

    /// <summary>
    /// Реєструє/логінить тестових користувачів і заповнює <see cref="EndpointSpec.Auth"/>
    /// у переданому <paramref name="api"/>. Повертає контекст із токенами.
    /// </summary>
    public async Task<AuthContext> ProbeAsync(ApiSpec api, CancellationToken ct = default)
    {
        var ctx = await EstablishUsersAsync(ct);

        if (!ctx.HasClient)
            throw new InvalidOperationException(
                "Не вдалося отримати client-токен — авторизацію класифікувати неможливо. " +
                "Перевір, що register/login працюють.");

        var clientToken = ctx.Client!.Token!;

        foreach (var ep in api.Endpoints)
            ep.Auth = await ClassifyAsync(ep, clientToken, ct);

        return ctx;
    }

    private async Task<AuthContext> EstablishUsersAsync(CancellationToken ct)
    {
        // Двоє користувачів: userB реєструється другим → гарантовано Client.
        var userA = await RegisterAndLoginAsync(ct);
        var userB = await RegisterAndLoginAsync(ct);

        var ctx = new AuthContext();

        // Client = той, чия роль Client (зазвичай userB). Admin = якщо такий зʼявився (порожня БД).
        ctx.Client = PickByRole(userB, "Client") ?? PickByRole(userA, "Client") ?? userB;
        ctx.Admin = PickByRole(userA, "Admin") ?? PickByRole(userB, "Admin");

        return ctx;
    }

    private static ProbeUser? PickByRole(ProbeUser user, string role) =>
        string.Equals(user.Role, role, StringComparison.OrdinalIgnoreCase) ? user : null;

    private async Task<ProbeUser> RegisterAndLoginAsync(CancellationToken ct)
    {
        var user = new ProbeUser
        {
            Email = $"qa.agent+{Guid.NewGuid():N}@example.com",
            Password = _config.Password
        };

        var credentials = new Dictionary<string, string>
        {
            [_config.EmailField] = user.Email,
            [_config.PasswordField] = user.Password
        };

        await _http.PostAsJsonAsync(_config.RegisterPath, credentials, ct);

        var loginResp = await _http.PostAsJsonAsync(_config.LoginPath, credentials, ct);
        if (loginResp.IsSuccessStatusCode)
        {
            var json = await loginResp.Content.ReadAsStringAsync(ct);
            user.Token = ExtractToken(json, _config.TokenField);
            user.Role = user.Token is null ? null : JwtUtil.GetRole(user.Token);
        }

        return user;
    }

    private async Task<AuthRequirement> ClassifyAsync(EndpointSpec ep, string clientToken, CancellationToken ct)
    {
        // Реєстрацію/логін не зондуємо — це завжди анонімні точки входу.
        if (ep.Path.Equals(_config.RegisterPath, StringComparison.OrdinalIgnoreCase) ||
            ep.Path.Equals(_config.LoginPath, StringComparison.OrdinalIgnoreCase))
            return AuthRequirement.Anonymous;

        var anonStatus = await SendProbeAsync(ep, token: null, ct);
        if (anonStatus != 401)
            return AuthRequirement.Anonymous;

        var clientStatus = await SendProbeAsync(ep, clientToken, ct);
        return clientStatus == 403 ? AuthRequirement.Admin : AuthRequirement.Authenticated;
    }

    private async Task<int> SendProbeAsync(EndpointSpec ep, string? token, CancellationToken ct)
    {
        var path = ep.Path;
        foreach (var p in ep.Parameters.Where(p => p.In == ParamLocation.Path))
            path = path.Replace("{" + p.Name + "}", DummyId);

        using var request = new HttpRequestMessage(new HttpMethod(ep.Method), path);

        if (ep.RequestBody is not null)
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        if (token is not null)
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var response = await _http.SendAsync(request, ct);
        return (int)response.StatusCode;
    }

    private static string? ExtractToken(string json, string field)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty(field, out var v) ? v.GetString() : null;
    }
}
