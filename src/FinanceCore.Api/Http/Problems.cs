using FinanceCore.Core.Commands;

namespace FinanceCore.Api.Http;

/// <summary>The error response (20, 7): `application/problem+json`, one entry per error with the stable code, the path, the parameters, the phase and the retryable flag.</summary>
public static class Problems
{
    public sealed record ErrorBody(string Code, string Path, IReadOnlyDictionary<string, object?>? Params, string Phase, bool Retryable, string Severity);

    public sealed record ProblemBody(string Type, string Title, int Status, string? CorrelationId, IReadOnlyList<ErrorBody> Errors);

    public static ErrorBody ToBody(CommandError e) => new(e.Code, e.Path, e.Params, e.Phase, e.Retryable, e.Severity);

    /// <summary>The HTTP status of a refusal: the first error decides. The codes of the error index (06, 2.3) stay the contract, the status is a convenience.</summary>
    public static int StatusOf(CommandStatus status, IReadOnlyList<CommandError> errors)
    {
        CommandError? first = errors.FirstOrDefault();
        if (first is null) return status == CommandStatus.Ok ? 200 : 500;
        string code = first.Code;
        if (code.StartsWith("AUTH.", StringComparison.Ordinal)) return code == "AUTH.TOKEN_INVALID" ? 401 : 403;
        if (code.StartsWith("IDEMPOTENCY.", StringComparison.Ordinal)) return 409;
        if (code.EndsWith("_NOT_FOUND", StringComparison.Ordinal) || code.EndsWith(".NOT_FOUND", StringComparison.Ordinal)) return 404;
        if (code == "SYSTEM.UNAVAILABLE") return 503;
        if (code.StartsWith("SYSTEM.", StringComparison.Ordinal)) return first.Retryable ? 409 : 500;
        if (status == CommandStatus.Failed) return 500;
        return first.Phase is "P0" or "P1" ? 400 : 422;
    }

    public static Task WriteAsync(HttpContext http, int status, string title, IEnumerable<CommandError> errors, CancellationToken ct = default)
    {
        string? correlation = http.Response.Headers["X-Correlation-Id"].FirstOrDefault() ?? http.Request.Headers["X-Correlation-Id"].FirstOrDefault();
        http.Response.StatusCode = status;
        http.Response.ContentType = "application/problem+json";
        var body = new ProblemBody("urn:financecore:problem", title, status, correlation, errors.Take(100).Select(ToBody).ToList());
        return System.Text.Json.JsonSerializer.SerializeAsync(http.Response.Body, body, ApiJson.Options, ct);
    }
}
