using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace FinanceCore.Api.Auth;

/// <summary>
/// The authentication settings (20, 7). The identity provider is any OpenID Connect authority: with `Auth:Authority` the keys come from its
/// discovery document. `Auth:DevSigningKey` is a shared secret for local development and tests and is refused in the Production environment.
/// </summary>
public sealed class AuthOptions
{
    public const string DevIssuer = "financecore-dev";

    public string? Authority { get; set; }
    public string Audience { get; set; } = "financecore-api";
    public string? DevSigningKey { get; set; }
    /// <summary>The claim that carries the tenant: the core takes `tenant_id` from the identity, never from the payload (06, 2, ADR-002).</summary>
    public string TenantClaim { get; set; } = "tenant_id";
    /// <summary>The permission names of 06, 8: a multi-valued claim, and the space-separated `scope` claim of OAuth.</summary>
    public string PermissionsClaim { get; set; } = "permissions";
}

/// <summary>What the core takes from the token: the tenant, the actor and the permissions of the caller, and the app code of a registered app (15, 3.1).</summary>
public sealed record Caller(Guid? TenantId, string Actor, IReadOnlySet<string> Permissions, string? AppCode, string? ClientId)
{
    public static Caller From(ClaimsPrincipal principal, AuthOptions options)
    {
        string? tenant = principal.FindFirstValue(options.TenantClaim);
        var permissions = new HashSet<string>(StringComparer.Ordinal);
        foreach (Claim c in principal.FindAll(options.PermissionsClaim)) permissions.Add(c.Value);
        foreach (Claim c in principal.FindAll("scope"))
            foreach (string s in c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)) permissions.Add(s);
        return new Caller(
            Guid.TryParse(tenant, out Guid id) ? id : null,
            principal.FindFirstValue("sub") ?? "unknown",
            permissions,
            principal.FindFirstValue("app_code"),
            principal.FindFirstValue("azp") ?? principal.FindFirstValue("client_id"));
    }
}

public static class AuthSetup
{
    public static AuthOptions AddApiAuthentication(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();
        bool dev = !string.IsNullOrWhiteSpace(options.DevSigningKey);
        if (dev && builder.Environment.IsProduction())
            throw new InvalidOperationException("Auth:DevSigningKey is for development and tests. Production needs Auth:Authority.");
        if (!dev && string.IsNullOrWhiteSpace(options.Authority))
            throw new InvalidOperationException("Set Auth:Authority (an OpenID Connect authority), or Auth:DevSigningKey for development.");
        if (dev && options.DevSigningKey!.Length < 32)
            throw new InvalidOperationException("Auth:DevSigningKey is at least 32 characters.");

        builder.Services.AddSingleton(options);
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
        {
            o.MapInboundClaims = false;
            o.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
            if (dev)
            {
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = AuthOptions.DevIssuer,
                    ValidAudience = options.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.DevSigningKey!)),
                    ClockSkew = TimeSpan.FromSeconds(30)
                };
            }
            else
            {
                o.Authority = options.Authority;
                o.Audience = options.Audience;
                o.TokenValidationParameters.ClockSkew = TimeSpan.FromSeconds(30);
            }
            o.Events = new JwtBearerEvents
            {
                // a missing or invalid token answers with the error model of 06, not an empty 401
                OnChallenge = async context =>
                {
                    context.HandleResponse();
                    await Http.Problems.WriteAsync(context.HttpContext, 401, "UNAUTHENTICATED",
                        [new Core.Commands.CommandError("AUTH.TOKEN_INVALID", "P0", "")], ct: context.HttpContext.RequestAborted);
                }
            };
        });
        builder.Services.AddAuthorization();
        return options;
    }
}
