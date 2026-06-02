using System.Text;
using System.Text.Json;

namespace QaAgent.Probing;

/// <summary>Мінімальне декодування JWT-payload для читання ролі (без перевірки підпису).</summary>
public static class JwtUtil
{
    private const string RoleClaimUri = "http://schemas.microsoft.com/ws/2008/06/identity/claims/role";

    public static string? GetRole(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) return null;

        try
        {
            var json = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
            using var doc = JsonDocument.Parse(json);
            foreach (var key in new[] { "role", RoleClaimUri })
                if (doc.RootElement.TryGetProperty(key, out var v))
                    return v.ValueKind == JsonValueKind.Array ? v[0].GetString() : v.GetString();
        }
        catch
        {
            // Невалідний payload — роль невідома.
        }
        return null;
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        s += (s.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(s);
    }
}
