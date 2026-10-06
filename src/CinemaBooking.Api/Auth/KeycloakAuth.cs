using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;

namespace CinemaBooking.Api.Auth;

public sealed class AuthOptions
{
    /// <summary>Адрес realm Keycloak, откуда API получает метаданные и ключи подписи.</summary>
    public string Authority { get; set; } = "";

    /// <summary>Ожидаемый получатель токена (claim <c>aud</c>).</summary>
    public string Audience { get; set; } = "";

    /// <summary>
    /// Ожидаемый издатель. Задаётся явно, когда API обращается к Keycloak по внутреннему адресу,
    /// а токены выдаются по внешнему (docker compose). Если не задан — берётся из метаданных.
    /// </summary>
    public string? ValidIssuer { get; set; }

    public bool RequireHttpsMetadata { get; set; } = true;
}

public static class Roles
{
    public const string Viewer = "viewer";
    public const string Staff = "staff";
}

public static class Policies
{
    public const string Viewer = "viewer";
    public const string Staff = "staff";
}

public static class KeycloakAuth
{
    public const string UserIdClaim = "sub";
    public const string RoleClaim = "role";

    /// <summary>D-01: принимаются только токены Keycloak с проверенной подписью, издателем, получателем и сроком.</summary>
    public static IServiceCollection AddKeycloakJwt(this IServiceCollection services, IConfiguration config)
    {
        var options = config.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(o =>
            {
                o.Authority = options.Authority;
                o.Audience = options.Audience;
                o.RequireHttpsMetadata = options.RequireHttpsMetadata;
                // Имена claim не переименовываются: роли и идентификатор берутся только из sub и realm_access.
                o.MapInboundClaims = false;
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = options.ValidIssuer,
                    ValidateAudience = true,
                    ValidAudience = options.Audience,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    RequireSignedTokens = true,
                    ValidateIssuerSigningKey = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = UserIdClaim,
                    RoleClaimType = RoleClaim,
                };
                o.Events = new JwtBearerEvents
                {
                    OnTokenValidated = ctx =>
                    {
                        if (ctx.Principal?.FindFirst(UserIdClaim) is null)
                        {
                            ctx.Fail("Токен не содержит sub");
                            return Task.CompletedTask;
                        }
                        MapRealmRoles(ctx.Principal);
                        return Task.CompletedTask;
                    },
                };
            });

        services.AddAuthorizationBuilder()
            // Запрет по умолчанию: любая конечная точка без явного AllowAnonymous требует аутентификации.
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(Policies.Viewer, p => p.RequireRole(Roles.Viewer))
            .AddPolicy(Policies.Staff, p => p.RequireRole(Roles.Staff));

        return services;
    }

    /// <summary>
    /// Keycloak кладёт роли realm в JSON-claim <c>realm_access.roles</c>; ASP.NET Core их не понимает.
    /// Роли берутся только оттуда: любые другие claim <c>role</c> удаляются.
    /// </summary>
    public static void MapRealmRoles(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity identity) return;

        foreach (var claim in identity.FindAll(RoleClaim).ToList())
            identity.RemoveClaim(claim);

        var realmAccess = identity.FindFirst("realm_access")?.Value;
        if (string.IsNullOrEmpty(realmAccess)) return;

        using var doc = JsonDocument.Parse(realmAccess);
        if (!doc.RootElement.TryGetProperty("roles", out var roles) || roles.ValueKind != JsonValueKind.Array) return;

        foreach (var role in roles.EnumerateArray())
            if (role.ValueKind == JsonValueKind.String)
                identity.AddClaim(new Claim(RoleClaim, role.GetString()!));
    }

    public static string GetUserId(this ClaimsPrincipal user) =>
        user.FindFirstValue(UserIdClaim) ?? throw new InvalidOperationException("Пользователь не аутентифицирован");
}
