using System.Text.Json;
using System.Text.RegularExpressions;
using FinanceCore.Api.Auth;
using FinanceCore.Core.Commands;

namespace FinanceCore.Api.Http;

/// <summary>
/// `POST /v1/{resource}:{verb}` (20, 7). The identity gives the tenant, the actor and the permissions, the headers give the idempotency key
/// (`Idempotency-Key`), the correlation id (`X-Correlation-Id`) and the calling system (`X-Source-System`), the body is the payload of the command.
/// The endpoint checks the permission of the command in P0a before it reads the idempotency header and the payload, and the command checks it again in its own P0. The response is the envelope of 06, 2.2, or problem+json.
/// </summary>
public static class CommandEndpoint
{
    public const int MaxBodyBytes = 4 * 1024 * 1024;
    private static readonly Regex CommandName = new("^[a-z0-9]+(-[a-z0-9]+)*:[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);

    public sealed record SuccessBody(string Status, bool IdempotentReplay, Guid? CommandLogId, object? Result, IReadOnlyList<Problems.ErrorBody> Warnings);

    public static void Map(WebApplication app)
    {
        app.MapPost("/v1/{command}", HandleAsync).RequireAuthorization().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxBodyBytes));
        app.MapGet("/v1/openapi.json", (CommandCatalog catalog) => Results.Text(OpenApiBuilder.ToText(OpenApiBuilder.Build(catalog)), "application/json")).AllowAnonymous();
        app.MapGet("/healthz", () => Results.Json(new { status = "ok" }, ApiJson.Options)).AllowAnonymous();
    }

    private static async Task HandleAsync(HttpContext http, string command, CommandCatalog catalog, AuthOptions auth, IServiceProvider services)
    {
        CancellationToken ct = http.RequestAborted;
        string correlation = http.Request.Headers["X-Correlation-Id"].FirstOrDefault() is { Length: > 0 and <= 100 } c ? c : Guid.NewGuid().ToString("N");
        http.Response.Headers["X-Correlation-Id"] = correlation;

        if (!CommandName.IsMatch(command) || catalog.Find(command) is not { } registration)
        {
            await Problems.WriteAsync(http, 404, "NOT_FOUND", [new CommandError("API.COMMAND_NOT_FOUND", "P0", "", new Dictionary<string, object?> { ["command"] = command })], ct);
            return;
        }

        Caller caller = Caller.From(http.User, auth);
        if (caller.TenantId is not { } tenantId)
        {
            await Problems.WriteAsync(http, 403, "FORBIDDEN", [new CommandError("AUTH.TENANT_MISSING", "P0", "")], ct);
            return;
        }
        // a request that names another tenant than its token's is refused before any query (20, 7)
        if (http.Request.Headers["X-Tenant-Id"].FirstOrDefault() is { Length: > 0 } named && !Guid.TryParse(named, out Guid namedId) | namedId != tenantId)
        {
            await Problems.WriteAsync(http, 403, "FORBIDDEN", [new CommandError("AUTH.TENANT_MISMATCH", "P0", "")], ct);
            return;
        }

        // P0a (06, 3): the permission that the command needs whatever the payload, from the token alone. A caller without it learns nothing about
        // the payload, the idempotency header or the schema. P1 follows; the command checks the same permission again and the company access (P0b, which
        // needs the company of the payload) in its own P0 after the payload is read.
        if (registration.Permission is { } needed && !caller.Permissions.Contains(needed))
        {
            await Problems.WriteAsync(http, 403, "FORBIDDEN", [new CommandError("AUTH.PERMISSION_MISSING", "P0", "", new Dictionary<string, object?> { ["permission"] = needed })], ct);
            return;
        }

        string? key = http.Request.Headers["Idempotency-Key"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(key))
        {
            await Problems.WriteAsync(http, 400, "REJECTED", [new CommandError("SCHEMA.REQUIRED_FIELD_MISSING", "P1", "Idempotency-Key")], ct);
            return;
        }
        if (key.Length > 100)
        {
            await Problems.WriteAsync(http, 400, "REJECTED", [new CommandError("SCHEMA.INVALID_VALUE", "P1", "Idempotency-Key", new Dictionary<string, object?> { ["reason"] = "at most 100 characters" })], ct);
            return;
        }

        JsonElement? body;
        try
        {
            body = http.Request.ContentLength == 0 ? null : await JsonSerializer.DeserializeAsync<JsonElement?>(http.Request.Body, ApiJson.Options, ct);
        }
        catch (JsonException e) when (e.Message.Contains("does not contain any JSON tokens", StringComparison.Ordinal))
        {
            body = null;   // no body at all: the handler answers that a payload is required
        }
        catch (JsonException e)
        {
            await Problems.WriteAsync(http, 400, "REJECTED", [new CommandError("SCHEMA.INVALID_TYPE", "P1", "", new Dictionary<string, object?> { ["reason"] = e.Message.Split(" Path:")[0] })], ct);
            return;
        }

        string source = http.Request.Headers["X-Source-System"].FirstOrDefault() is { Length: > 0 and <= 50 } s ? s : caller.ClientId ?? "API";
        var context = new CommandContext(key, correlation, caller.Actor, caller.Permissions, source, caller.AppCode);
        CommandOutcome outcome = await registration.Handler(new CommandInvocation(tenantId, context, body, services), ct);

        if (outcome.Status == CommandStatus.Ok)
        {
            http.Response.StatusCode = 200;
            http.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(http.Response.Body,
                new SuccessBody("OK", outcome.Replay, outcome.LogId, outcome.Result, outcome.Warnings.Select(Problems.ToBody).ToList()), ApiJson.Options, ct);
            return;
        }
        await Problems.WriteAsync(http, Problems.StatusOf(outcome.Status, outcome.Errors), outcome.Status == CommandStatus.Rejected ? "REJECTED" : "FAILED", outcome.Errors, ct);
    }
}
