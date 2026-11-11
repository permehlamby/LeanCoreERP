using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FinanceCore.Api.Auth;
using FinanceCore.Api.Http;
using FinanceCore.Core.Commands;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace FinanceCore.Api.Tests;

public sealed record EchoRequest(string Name, decimal Amount, DateOnly Date, Guid? Id = null);

public sealed record EchoResult(string Name, decimal Amount, DateOnly Date, string Actor, string[] Permissions, string Key, string Correlation, string Source, Guid Tenant);

/// <summary>Commands that exist only in the tests: they show what the endpoint hands to a handler and how every kind of result is answered.</summary>
public sealed class TestModule : ICommandModule
{
    public const string GuardedPermission = "test.guarded";

    public void Register(CommandCatalog catalog)
    {
        catalog.Add<EchoRequest, EchoResult>("echo:run", (_, tenant, ctx, req, _) => Task.FromResult(
            CommandResult<EchoResult>.Ok(new EchoResult(req.Name, req.Amount, req.Date, ctx.Actor, ctx.Permissions.Order().ToArray(), ctx.IdempotencyKey,
                ctx.CorrelationId, ctx.SourceSystem, tenant), Guid.Parse("00000000-0000-0000-0000-000000000001"),
                [new CommandError("TEST.WARNED", "P4", "", null, false, "WARNING")], replay: req.Name == "replay")));
        catalog.Add<EchoRequest, EchoResult>("guarded:run", GuardedPermission, (_, tenant, ctx, req, _) => Task.FromResult(
            CommandResult<EchoResult>.Ok(new EchoResult(req.Name, req.Amount, req.Date, ctx.Actor, ctx.Permissions.Order().ToArray(), ctx.IdempotencyKey,
                ctx.CorrelationId, ctx.SourceSystem, tenant), Guid.Parse("00000000-0000-0000-0000-000000000001"))));
        catalog.Add<EchoRequest, EchoResult>("reject:run", (_, _, ctx, req, _) => Task.FromResult(CommandResult<EchoResult>.Rejected(
            [new CommandError(req.Name, req.Name == "AUTH.PERMISSION_MISSING" ? "P0" : "P2", "lines[0]", new Dictionary<string, object?> { ["n"] = 1, ["when"] = new DateOnly(2026, 1, 2) })])));
        catalog.Add<EchoRequest, EchoResult>("fail:run", (_, _, _, req, _) => Task.FromResult(CommandResult<EchoResult>.Failed(
            new CommandError(req.Name, "P7", "", null, Retryable: req.Name == "SYSTEM.CONFLICT"))));
    }
}

public sealed class ApiFixture : WebApplicationFactory<Program>
{
    public const string Key = "a-development-signing-key-of-32-characters-or-more";
    public static readonly Guid Tenant = Guid.Parse("11111111-1111-4111-8111-111111111111");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("Auth:DevSigningKey", Key);
        builder.ConfigureServices(services => services.AddSingleton<ICommandModule, TestModule>());
    }

    public static string Token(Guid? tenant = null, string subject = "alice", string[]? permissions = null, string? scope = null, string? appCode = null,
        string issuer = AuthOptions.DevIssuer, string audience = "financecore-api", string? key = null, TimeSpan? lifetime = null, bool withTenant = true)
    {
        var claims = new Dictionary<string, object> { ["sub"] = subject };
        if (withTenant) claims["tenant_id"] = (tenant ?? Tenant).ToString("D");
        if (permissions is not null) claims["permissions"] = permissions;
        if (scope is not null) claims["scope"] = scope;
        if (appCode is not null) claims["app_code"] = appCode;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer, Audience = audience, Claims = claims,
            Expires = DateTime.UtcNow.Add(lifetime ?? TimeSpan.FromMinutes(5)),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key ?? Key)), SecurityAlgorithms.HmacSha256)
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    public HttpRequestMessage Post(string command, object? body, string? token = null, string? idempotencyKey = "key-1", Action<HttpRequestMessage>? tweak = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/" + command)
        {
            Content = body is null ? null : body is string raw
                ? new StringContent(raw, Encoding.UTF8, "application/json")
                : new StringContent(JsonSerializer.Serialize(body, ApiJson.Options), Encoding.UTF8, "application/json")
        };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        tweak?.Invoke(request);
        return request;
    }

    public static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
}
