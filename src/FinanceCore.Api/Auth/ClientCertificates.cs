using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using FinanceCore.Api.Http;
using FinanceCore.Core.Commands;
using Microsoft.IdentityModel.Tokens;

namespace FinanceCore.Api.Auth;

/// <summary>Mutual TLS for service identities (20, 7: optional). `Transport:ClientCertificate` is None (default), Allow or Require.</summary>
public enum MutualTlsMode { None, Allow, Require }

public sealed class TransportOptions
{
    public MutualTlsMode ClientCertificate { get; set; } = MutualTlsMode.None;
}

/// <summary>
/// Mutual TLS as the transport proof of a service identity. The token stays the identity (tenant, actor, permissions); the certificate is bound to it
/// the way RFC 8705 does: a token with `cnf` → `x5t#S256` is accepted only on a connection whose client certificate has that SHA-256 thumbprint, so a stolen
/// token is useless without the private key. With Require, a request with a token and no client certificate is refused, and so is a client certificate that
/// is not valid by the TLS handshake of the server (Kestrel chain validation). TLS 1.2 or higher is set for the HTTPS endpoints of the server itself.
/// A gateway that terminates TLS does the handshake instead; this binding then does not see a certificate, and Require must not be used.
/// </summary>
public static class ClientCertificates
{
    public const string ThumbprintClaim = "x5t#S256";

    public static TransportOptions AddApiTransport(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection("Transport").Get<TransportOptions>() ?? new TransportOptions();
        builder.Services.AddSingleton(options);
        builder.WebHost.ConfigureKestrel(k => k.ConfigureHttpsDefaults(https =>
        {
            https.SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13;
            https.ClientCertificateMode = options.ClientCertificate switch
            {
                MutualTlsMode.Require => Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode.RequireCertificate,
                MutualTlsMode.Allow => Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode.AllowCertificate,
                _ => Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode.NoCertificate
            };
        }));
        return options;
    }

    /// <summary>The SHA-256 thumbprint of a certificate as the unpadded base64url of RFC 8705.</summary>
    public static string Thumbprint(X509Certificate2 certificate) => Base64UrlEncoder.Encode(SHA256.HashData(certificate.RawData));

    /// <summary>The `x5t#S256` of the `cnf` claim of the token, or null when the token is not bound to a certificate.</summary>
    public static string? BoundThumbprint(System.Security.Claims.ClaimsPrincipal principal)
    {
        string? cnf = principal.FindFirst("cnf")?.Value;
        if (string.IsNullOrEmpty(cnf)) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(cnf);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(ThumbprintClaim, out JsonElement t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : "";   // a cnf that names no thumbprint is bound to something this server cannot check: refused below as a mismatch
        }
        catch (JsonException)
        {
            return "";
        }
    }

    /// <summary>After authentication: refuses a request whose token is bound to a certificate that the connection does not present, and, with Require, any authenticated request without one.</summary>
    public static void UseClientCertificateBinding(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<TransportOptions>();
        if (options.ClientCertificate == MutualTlsMode.None)
        {
            // a bound token is still honoured when the connection does show a certificate; without mTLS configured it cannot be proven, so it is refused
            app.Use(async (http, next) =>
            {
                if (http.User.Identity?.IsAuthenticated == true && BoundThumbprint(http.User) is not null)
                {
                    await Problems.WriteAsync(http, 401, "UNAUTHENTICATED", [new CommandError("AUTH.CLIENT_CERTIFICATE_MISMATCH", "P0", "")], http.RequestAborted);
                    return;
                }
                await next(http);
            });
            return;
        }
        app.Use(async (http, next) =>
        {
            if (http.User.Identity?.IsAuthenticated != true)
            {
                await next(http);
                return;
            }
            X509Certificate2? certificate = await http.Connection.GetClientCertificateAsync(http.RequestAborted);
            string? bound = BoundThumbprint(http.User);
            if (certificate is null)
            {
                if (options.ClientCertificate == MutualTlsMode.Require || bound is not null)
                {
                    await Problems.WriteAsync(http, 401, "UNAUTHENTICATED", [new CommandError("AUTH.CLIENT_CERTIFICATE_REQUIRED", "P0", "")], http.RequestAborted);
                    return;
                }
            }
            else if (bound is not null && !CryptographicOperations.FixedTimeEquals(
                         System.Text.Encoding.ASCII.GetBytes(bound), System.Text.Encoding.ASCII.GetBytes(Thumbprint(certificate))))
            {
                await Problems.WriteAsync(http, 401, "UNAUTHENTICATED", [new CommandError("AUTH.CLIENT_CERTIFICATE_MISMATCH", "P0", "")], http.RequestAborted);
                return;
            }
            await next(http);
        });
    }
}
