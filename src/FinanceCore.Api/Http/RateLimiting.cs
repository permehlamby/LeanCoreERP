using System.Globalization;
using System.Threading.RateLimiting;
using FinanceCore.Api.Auth;
using FinanceCore.Core.Commands;

namespace FinanceCore.Api.Http;

/// <summary>
/// The rate limit of the core (20, 7): a sliding window per tenant, in addition to the limits of the gateway, so one tenant cannot starve the others
/// when the gateway is bypassed or misconfigured. `RateLimit:PermitLimit` requests per `RateLimit:WindowSeconds` (default 600 per 60 seconds);
/// `RateLimit:Enabled` false switches it off. A request without a tenant in its token (no token, or a token that is refused later) is counted per remote
/// address with the same limit, so the refusals of the authentication cannot be used to probe without a limit. The health check and the OpenAPI document are not limited.
/// An answer of 429 carries `Retry-After` and the error model of 06, with the retryable code `API.RATE_LIMITED`.
/// </summary>
public sealed class RateLimitOptions
{
    public bool Enabled { get; set; } = true;
    public int PermitLimit { get; set; } = 600;
    public int WindowSeconds { get; set; } = 60;
}

public static class RateLimiting
{
    private const int Segments = 6;

    public static RateLimitOptions AddApiRateLimit(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection("RateLimit").Get<RateLimitOptions>() ?? new RateLimitOptions();
        if (options.Enabled && (options.PermitLimit < 1 || options.WindowSeconds < Segments))
            throw new InvalidOperationException($"RateLimit:PermitLimit is at least 1 and RateLimit:WindowSeconds at least {Segments}.");
        builder.Services.AddSingleton(options);
        return options;
    }

    public static void UseApiRateLimit(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<RateLimitOptions>();
        if (!options.Enabled) return;
        var auth = app.Services.GetRequiredService<AuthOptions>();
        var limiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
        {
            string path = http.Request.Path.Value ?? "";
            if (path is "/healthz" or "/v1/openapi.json") return RateLimitPartition.GetNoLimiter("open");
            string key = http.User.FindFirst(auth.TenantClaim)?.Value is { Length: > 0 } tenant ? "t:" + tenant : "ip:" + http.Connection.RemoteIpAddress;
            return RateLimitPartition.GetSlidingWindowLimiter(key, _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = options.PermitLimit,
                Window = TimeSpan.FromSeconds(options.WindowSeconds),
                SegmentsPerWindow = Segments,
                QueueLimit = 0,
                AutoReplenishment = true
            });
        });
        app.Use(async (http, next) =>
        {
            using RateLimitLease lease = await limiter.AcquireAsync(http, 1, http.RequestAborted);
            if (lease.IsAcquired)
            {
                await next(http);
                return;
            }
            // the window is the longest wait; the lease says how long when it knows
            double seconds = lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan wait) ? Math.Ceiling(wait.TotalSeconds) : options.WindowSeconds;
            http.Response.Headers["Retry-After"] = Math.Max(1, seconds).ToString("0", CultureInfo.InvariantCulture);
            await Problems.WriteAsync(http, 429, "REJECTED", [new CommandError("API.RATE_LIMITED", "P0", "", null, Retryable: true)], http.RequestAborted);
        });
    }
}
