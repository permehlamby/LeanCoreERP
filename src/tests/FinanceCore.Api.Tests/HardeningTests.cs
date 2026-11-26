using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FinanceCore.Api.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;

namespace FinanceCore.Api.Tests;

/// <summary>The hardening of the API shell (20, 7): the rate limit per tenant, the response headers, and mutual TLS bound to the token.</summary>
public class HardeningTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private static object Echo() => new Dictionary<string, object?> { ["name"] = "x", ["amount"] = "1.00", ["date"] = "2026-03-15" };

    private static string Code(System.Text.Json.JsonElement problem) => problem.GetProperty("errors")[0].GetProperty("code").GetString()!;

    private HttpClient Client(Action<IWebHostBuilder> configure) => api.WithWebHostBuilder(configure).CreateClient();

    // ---------------------------------------------------------------------------------------------------------
    // Rate limit
    // ---------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task ATenantOverItsRateLimitIsAnsweredWith429AndRetryAfterWhileAnotherTenantIsNot()
    {
        using HttpClient client = Client(b => { b.UseSetting("RateLimit:PermitLimit", "3"); b.UseSetting("RateLimit:WindowSeconds", "60"); });
        for (int i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(api.Post("echo:run", Echo(), ApiFixture.Token(), idempotencyKey: "k" + i))).StatusCode);

        var limited = await client.SendAsync(api.Post("echo:run", Echo(), ApiFixture.Token(), idempotencyKey: "k9"));
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("application/problem+json", limited.Content.Headers.ContentType!.MediaType);
        Assert.True(int.Parse(limited.Headers.GetValues("Retry-After").Single()) >= 1);
        var problem = await ApiFixture.Json(limited);
        Assert.Equal("API.RATE_LIMITED", Code(problem));
        Assert.True(problem.GetProperty("errors")[0].GetProperty("retryable").GetBoolean());

        var other = await client.SendAsync(api.Post("echo:run", Echo(), ApiFixture.Token(tenant: Guid.NewGuid()), idempotencyKey: "k1"));
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        // the health check and the OpenAPI document are not limited
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/openapi.json")).StatusCode);
    }

    [Fact]
    public async Task ARequestWithoutATokenIsCountedPerAddressSoRefusalsCannotBeUsedToProbeWithoutALimit()
    {
        using HttpClient client = Client(b => { b.UseSetting("RateLimit:PermitLimit", "2"); b.UseSetting("RateLimit:WindowSeconds", "60"); });
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(api.Post("echo:run", Echo(), token: null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(api.Post("echo:run", Echo(), token: "bad"))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(api.Post("echo:run", Echo(), token: null))).StatusCode);
    }

    [Fact]
    public async Task TheRateLimitCanBeSwitchedOff()
    {
        using HttpClient client = Client(b => { b.UseSetting("RateLimit:Enabled", "false"); b.UseSetting("RateLimit:PermitLimit", "1"); });
        for (int i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(api.Post("echo:run", Echo(), ApiFixture.Token(), idempotencyKey: "k" + i))).StatusCode);
    }

    [Fact]
    public void AWindowShorterThanItsSegmentsOrALimitBelowOneIsRefusedAtStart()
    {
        Assert.Throws<InvalidOperationException>(() => Client(b => b.UseSetting("RateLimit:PermitLimit", "0")));
        Assert.Throws<InvalidOperationException>(() => Client(b => b.UseSetting("RateLimit:WindowSeconds", "3")));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Headers
    // ---------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task EveryAnswerCarriesTheSecurityHeadersAndACommandAnswerIsNotCached()
    {
        using HttpClient client = api.CreateClient();
        foreach (var response in new[] { await client.SendAsync(api.Post("echo:run", Echo(), ApiFixture.Token())), await client.SendAsync(api.Post("echo:run", Echo(), token: null)) })
        {
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
            Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
            Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        }
        var health = await client.GetAsync("/healthz");
        Assert.Equal("nosniff", health.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.False(health.Headers.CacheControl?.NoStore ?? false);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Mutual TLS
    // ---------------------------------------------------------------------------------------------------------
    private sealed class FakeTls(X509Certificate2? certificate) : ITlsConnectionFeature
    {
        public X509Certificate2? ClientCertificate { get; set; } = certificate;
        public Task<X509Certificate2?> GetClientCertificateAsync(CancellationToken cancellationToken) => Task.FromResult(ClientCertificate);
    }

    /// <summary>Puts the client certificate of a TLS handshake on the request, in front of the authentication, as the server does.</summary>
    private sealed class CertificateFilter(X509Certificate2? certificate) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((http, n) => { http.Features.Set<ITlsConnectionFeature>(new FakeTls(certificate)); return n(http); });
            next(app);
        };
    }

    private static X509Certificate2 NewCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=erp-bridge", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
    }

    private HttpClient Mtls(string mode, X509Certificate2? certificate) => Client(b =>
    {
        b.UseSetting("Transport:ClientCertificate", mode);
        b.ConfigureServices(s => s.AddSingleton<IStartupFilter>(new CertificateFilter(certificate)));
    });

    [Fact]
    public async Task WithRequireAnAuthenticatedRequestWithoutAClientCertificateIsRefused()
    {
        using HttpClient client = Mtls("Require", certificate: null);
        var response = await client.SendAsync(api.Post("echo:run", Echo(), ApiFixture.Token()));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("AUTH.CLIENT_CERTIFICATE_REQUIRED", Code(await ApiFixture.Json(response)));
        // a request without a token is still answered by the authentication
        Assert.Equal("AUTH.TOKEN_INVALID", Code(await ApiFixture.Json(await client.SendAsync(api.Post("echo:run", Echo(), token: null)))));
    }

    [Fact]
    public async Task ATokenBoundToACertificateIsAcceptedOnlyOnAConnectionWithThatCertificate()
    {
        using X509Certificate2 cert = NewCertificate();
        string thumbprint = ClientCertificates.Thumbprint(cert);
        using HttpClient client = Mtls("Require", cert);

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(api.Post("echo:run", Echo(), ApiFixture.Token(certificateThumbprint: thumbprint)))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(api.Post("echo:run", Echo(), ApiFixture.Token()))).StatusCode);   // an unbound token needs only a certificate

        foreach (string wrong in new[] { ClientCertificates.Thumbprint(NewCertificate()), "", "AAAA" })
        {
            var response = await client.SendAsync(api.Post("echo:run", Echo(), ApiFixture.Token(certificateThumbprint: wrong)));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("AUTH.CLIENT_CERTIFICATE_MISMATCH", Code(await ApiFixture.Json(response)));
        }
    }

    [Fact]
    public async Task WithAllowACertificateIsNotNeededUnlessTheTokenIsBoundToOne()
    {
        using HttpClient without = Mtls("Allow", certificate: null);
        Assert.Equal(HttpStatusCode.OK, (await without.SendAsync(api.Post("echo:run", Echo(), ApiFixture.Token()))).StatusCode);
        var bound = await without.SendAsync(api.Post("echo:run", Echo(), ApiFixture.Token(certificateThumbprint: "abc")));
        Assert.Equal(HttpStatusCode.Unauthorized, bound.StatusCode);
        Assert.Equal("AUTH.CLIENT_CERTIFICATE_REQUIRED", Code(await ApiFixture.Json(bound)));
    }

    [Fact]
    public async Task WithoutMutualTlsATokenBoundToACertificateIsRefusedBecauseTheBindingCannotBeProved()
    {
        using HttpClient client = api.CreateClient();
        var response = await client.SendAsync(api.Post("echo:run", Echo(), ApiFixture.Token(certificateThumbprint: "abc")));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("AUTH.CLIENT_CERTIFICATE_MISMATCH", Code(await ApiFixture.Json(response)));
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(api.Post("echo:run", Echo(), ApiFixture.Token()))).StatusCode);
    }
}
