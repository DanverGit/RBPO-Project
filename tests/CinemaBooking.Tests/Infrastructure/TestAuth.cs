using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CinemaBooking.Tests.Infrastructure;

/// <summary>
/// Выпускает токены в формате Keycloak (sub, realm_access.roles, aud), подписанные тестовым ключом.
/// API проверяет их тем же кодом, что и настоящие токены; заменяется только источник ключей и издатель.
/// </summary>
public static class TestAuth
{
    public const string Issuer = "http://test-keycloak/realms/cinema";
    public const string Audience = "cinema-api";

    public static readonly RsaSecurityKey SigningKey = new(RSA.Create(2048)) { KeyId = "test-key" };
    public static readonly RsaSecurityKey ForeignKey = new(RSA.Create(2048)) { KeyId = "test-key" };

    public static string CreateToken(
        string userId,
        string[] roles,
        string audience = Audience,
        DateTime? expires = null,
        SecurityKey? key = null)
    {
        var now = DateTime.UtcNow;
        var exp = expires ?? now.AddMinutes(5);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = audience,
            IssuedAt = exp.AddMinutes(-10),
            NotBefore = exp.AddMinutes(-10),
            Expires = exp,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = userId,
                ["realm_access"] = new Dictionary<string, object> { ["roles"] = roles },
            },
            SigningCredentials = new SigningCredentials(key ?? SigningKey, SecurityAlgorithms.RsaSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    public static HttpClient WithToken(this HttpClient client, string token)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
