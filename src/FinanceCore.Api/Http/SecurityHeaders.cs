namespace FinanceCore.Api.Http;

/// <summary>
/// The response headers of an API that only answers JSON (20, 7): no sniffing, no framing, no scripts or other content, and nothing cached by a
/// shared cache, because every answer is for one tenant. The OpenAPI document and the health check are public and may be cached by the client.
/// </summary>
public static class SecurityHeaders
{
    public static void UseSecurityHeaders(this WebApplication app) => app.Use((http, next) =>
    {
        http.Response.OnStarting(() =>
        {
            IHeaderDictionary h = http.Response.Headers;
            h["X-Content-Type-Options"] = "nosniff";
            h["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
            h["Referrer-Policy"] = "no-referrer";
            if (http.Request.Path.Value is not ("/healthz" or "/v1/openapi.json")) h["Cache-Control"] = "no-store";
            return Task.CompletedTask;
        });
        return next(http);
    });
}
