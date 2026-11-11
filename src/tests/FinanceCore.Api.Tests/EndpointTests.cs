using System.Net;
using System.Text.Json;
using FinanceCore.Api.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace FinanceCore.Api.Tests;

/// <summary>The API shell (A1): authentication, the envelope from identity and headers, payload checking and the answer for every kind of result.</summary>
public class EndpointTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private HttpClient Client => api.CreateClient();

    private static object Echo(string name = "x", object? amount = null, string date = "2026-03-15") =>
        new Dictionary<string, object?> { ["name"] = name, ["amount"] = amount ?? "1000.50", ["date"] = date };

    private static string Code(JsonElement problem, int index = 0) => problem.GetProperty("errors")[index].GetProperty("code").GetString()!;

    // ---------------------------------------------------------------------------------------------------------
    // Identity
    // ---------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task ARequestWithoutAValidTokenIsAnsweredWithTheErrorModel()
    {
        var none = await Client.SendAsync(api.Post("echo:run", Echo(), token: null));
        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Equal("application/problem+json", none.Content.Headers.ContentType!.MediaType);
        Assert.Equal("AUTH.TOKEN_INVALID", Code(await ApiFixture.Json(none)));

        foreach (string bad in new[]
        {
            ApiFixture.Token(key: "another-signing-key-of-32-characters-or-more!!"),
            ApiFixture.Token(issuer: "somebody-else"),
            ApiFixture.Token(audience: "another-api"),
            ApiFixture.Token(lifetime: TimeSpan.FromMinutes(-10)),
            "not.a.token"
        })
        {
            var r = await Client.SendAsync(api.Post("echo:run", Echo(), token: bad));
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
            Assert.Equal("AUTH.TOKEN_INVALID", Code(await ApiFixture.Json(r)));
        }
    }

    [Fact]
    public async Task TheTenantComesFromTheTokenOnly()
    {
        var noTenant = await Client.SendAsync(api.Post("echo:run", Echo(), ApiFixture.Token(withTenant: false)));
        Assert.Equal(HttpStatusCode.Forbidden, noTenant.StatusCode);
        Assert.Equal("AUTH.TENANT_MISSING", Code(await ApiFixture.Json(noTenant)));

        var other = await Client.SendAsync(api.Post("echo:run", Echo(), ApiFixture.Token(), tweak: r => r.Headers.Add("X-Tenant-Id", Guid.NewGuid().ToString())));
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        Assert.Equal("AUTH.TENANT_MISMATCH", Code(await ApiFixture.Json(other)));
        var garbage = await Client.SendAsync(api.Post("echo:run", Echo(), ApiFixture.Token(), tweak: r => r.Headers.Add("X-Tenant-Id", "not-a-guid")));
        Assert.Equal(HttpStatusCode.Forbidden, garbage.StatusCode);

        var same = await Client.SendAsync(api.Post("echo:run", Echo(), ApiFixture.Token(), tweak: r => r.Headers.Add("X-Tenant-Id", ApiFixture.Tenant.ToString())));
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
    }

    [Fact]
    public async Task TheEnvelopeIsBuiltFromTheIdentityAndTheHeaders()
    {
        var response = await Client.SendAsync(api.Post("echo:run", Echo(), ApiFixture.Token(subject: "bob", permissions: ["journal.post", "read.ledger"], scope: "write.config journal.post"),
            idempotencyKey: "idem-42", tweak: r => { r.Headers.Add("X-Correlation-Id", "corr-7"); r.Headers.Add("X-Source-System", "erp-bridge"); }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("corr-7", response.Headers.GetValues("X-Correlation-Id").Single());
        var result = (await ApiFixture.Json(response)).GetProperty("result");
        Assert.Equal("bob", result.GetProperty("actor").GetString());
        Assert.Equal(new[] { "journal.post", "read.ledger", "write.config" }, result.GetProperty("permissions").EnumerateArray().Select(x => x.GetString()!).ToArray());
        Assert.Equal("idem-42", result.GetProperty("key").GetString());
        Assert.Equal("corr-7", result.GetProperty("correlation").GetString());
        Assert.Equal("erp-bridge", result.GetProperty("source").GetString());
        Assert.Equal(ApiFixture.Tenant.ToString(), result.GetProperty("tenant").GetString());
    }

    [Fact]
    public async Task ACorrelationIdIsGeneratedWhenTheCallerSendsNoneAndTheSourceDefaultsToApi()
    {
        var response = await Client.SendAsync(api.Post("echo:run", Echo(), ApiFixture.Token()));
        string correlation = response.Headers.GetValues("X-Correlation-Id").Single();
        Assert.False(string.IsNullOrWhiteSpace(correlation));
        var result = (await ApiFixture.Json(response)).GetProperty("result");
        Assert.Equal(correlation, result.GetProperty("correlation").GetString());
        Assert.Equal("API", result.GetProperty("source").GetString());
    }

    [Fact]
    public async Task TheIdempotencyKeyIsMandatoryAndAtMostOneHundredCharacters()
    {
        var missing = await Client.SendAsync(api.Post("echo:run", Echo(), ApiFixture.Token(), idempotencyKey: null));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        var problem = await ApiFixture.Json(missing);
        Assert.Equal("SCHEMA.REQUIRED_FIELD_MISSING", Code(problem));
        Assert.Equal("Idempotency-Key", problem.GetProperty("errors")[0].GetProperty("path").GetString());

        var tooLong = await Client.SendAsync(api.Post("echo:run", Echo(), ApiFixture.Token(), idempotencyKey: new string('k', 101)));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal("SCHEMA.INVALID_VALUE", Code(await ApiFixture.Json(tooLong)));
        Assert.Equal(HttpStatusCode.OK, (await Client.SendAsync(api.Post("echo:run", Echo(), ApiFixture.Token(), idempotencyKey: new string('k', 100)))).StatusCode);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Routing and payload
    // ---------------------------------------------------------------------------------------------------------
    [Theory]
    [InlineData("nothing:here")]
    [InlineData("echo")]
    [InlineData("Echo:Run")]
    [InlineData("echo:run:again")]
    public async Task AnUnknownOrMalformedCommandIsNotFound(string command)
    {
        var r = await Client.SendAsync(api.Post(command, Echo(), ApiFixture.Token()));
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal("API.COMMAND_NOT_FOUND", Code(await ApiFixture.Json(r)));
    }

    [Fact]
    public async Task ThePermissionOfACommandIsCheckedBeforeTheIdempotencyKeyAndThePayload()
    {
        // P0a (06, 3): no permission, no key and a payload that is not even JSON: the caller learns only that the permission is missing
        var denied = await Client.SendAsync(api.Post("guarded:run", "{ not json", ApiFixture.Token(permissions: ["read.ledger"]), idempotencyKey: null));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var problem = await ApiFixture.Json(denied);
        Assert.Equal("AUTH.PERMISSION_MISSING", Code(problem));
        Assert.Equal("P0", problem.GetProperty("errors")[0].GetProperty("phase").GetString());
        Assert.Equal(TestModule.GuardedPermission, problem.GetProperty("errors")[0].GetProperty("params").GetProperty("permission").GetString());

        // with the permission, P1 speaks: the missing key first, then the payload
        var noKey = await Client.SendAsync(api.Post("guarded:run", Echo(), ApiFixture.Token(permissions: [TestModule.GuardedPermission]), idempotencyKey: null));
        Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);
        Assert.Equal("SCHEMA.REQUIRED_FIELD_MISSING", Code(await ApiFixture.Json(noKey)));
        var badBody = await Client.SendAsync(api.Post("guarded:run", "{ not json", ApiFixture.Token(permissions: [TestModule.GuardedPermission])));
        Assert.Equal(HttpStatusCode.BadRequest, badBody.StatusCode);
        Assert.Equal("SCHEMA.INVALID_TYPE", Code(await ApiFixture.Json(badBody)));

        // the permission may come in the scope claim, as for every command
        var viaScope = await Client.SendAsync(api.Post("guarded:run", Echo(), ApiFixture.Token(scope: "openid " + TestModule.GuardedPermission)));
        Assert.Equal(HttpStatusCode.OK, viaScope.StatusCode);
    }

    [Fact]
    public async Task APayloadThatDoesNotFitItsTypeIsRejectedWithThePathOfTheField()
    {
        async Task<(HttpStatusCode, string, string)> Try(string raw)
        {
            var r = await Client.SendAsync(api.Post("echo:run", raw, ApiFixture.Token()));
            var problem = await ApiFixture.Json(r);
            return (r.StatusCode, Code(problem), problem.GetProperty("errors")[0].GetProperty("path").GetString()!);
        }

        var unknown = await Try("""{"name":"x","amount":"1","date":"2026-03-15","extra":1}""");
        Assert.Equal((HttpStatusCode.BadRequest, "SCHEMA.UNKNOWN_FIELD"), (unknown.Item1, unknown.Item2));
        Assert.Contains("extra", unknown.Item3);

        var missing = await Try("""{"name":"x","date":"2026-03-15"}""");
        Assert.Equal((HttpStatusCode.BadRequest, "SCHEMA.REQUIRED_FIELD_MISSING"), (missing.Item1, missing.Item2));

        var nullName = await Try("""{"name":null,"amount":"1","date":"2026-03-15"}""");
        Assert.Equal("SCHEMA.REQUIRED_FIELD_MISSING", nullName.Item2);

        var wrongType = await Try("""{"name":"x","amount":"abc","date":"2026-03-15"}""");
        Assert.Equal((HttpStatusCode.BadRequest, "SCHEMA.INVALID_TYPE"), (wrongType.Item1, wrongType.Item2));
        Assert.Contains("amount", wrongType.Item3);

        var badDate = await Try("""{"name":"x","amount":"1","date":"15-03-2026"}""");
        Assert.Equal("SCHEMA.INVALID_TYPE", badDate.Item2);

        var wrongCase = await Try("""{"Name":"x","amount":"1","date":"2026-03-15"}""");
        Assert.Equal("SCHEMA.UNKNOWN_FIELD", wrongCase.Item2);

        var broken = await Client.SendAsync(api.Post("echo:run", "{not json", ApiFixture.Token()));
        Assert.Equal(HttpStatusCode.BadRequest, broken.StatusCode);
        Assert.Equal("SCHEMA.INVALID_TYPE", Code(await ApiFixture.Json(broken)));

        var empty = await Client.SendAsync(api.Post("echo:run", null, ApiFixture.Token()));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal("SCHEMA.REQUIRED_FIELD_MISSING", Code(await ApiFixture.Json(empty)));
    }

    [Fact]
    public async Task DecimalsAreAcceptedAsStringsOrNumbersAndAnsweredAsStrings()
    {
        foreach (string amount in new[] { "\"1000.50\"", "1000.50", "\"1000.5000\"" })
        {
            var r = await Client.SendAsync(api.Post("echo:run", $$"""{"name":"x","amount":{{amount}},"date":"2026-03-15"}""", ApiFixture.Token()));
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var value = (await ApiFixture.Json(r)).GetProperty("result").GetProperty("amount");
            Assert.Equal(JsonValueKind.String, value.ValueKind);
            Assert.Equal(1000.5m, decimal.Parse(value.GetString()!, System.Globalization.CultureInfo.InvariantCulture));
        }
        var exact = await Client.SendAsync(api.Post("echo:run", """{"name":"x","amount":"0.1234567890","date":"2026-03-15"}""", ApiFixture.Token()));
        Assert.Equal("0.1234567890", (await ApiFixture.Json(exact)).GetProperty("result").GetProperty("amount").GetString());
    }

    // ---------------------------------------------------------------------------------------------------------
    // Results
    // ---------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task ASuccessIsTheEnvelopeOfTheContract()
    {
        var r = await Client.SendAsync(api.Post("echo:run", Echo(name: "replay"), ApiFixture.Token()));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("application/json", r.Content.Headers.ContentType!.MediaType);
        var body = await ApiFixture.Json(r);
        Assert.Equal("OK", body.GetProperty("status").GetString());
        Assert.True(body.GetProperty("idempotent_replay").GetBoolean());
        Assert.Equal("00000000-0000-0000-0000-000000000001", body.GetProperty("command_log_id").GetString());
        var warning = body.GetProperty("warnings")[0];
        Assert.Equal("TEST.WARNED", warning.GetProperty("code").GetString());
        Assert.Equal("WARNING", warning.GetProperty("severity").GetString());
        Assert.Equal("2026-03-15", body.GetProperty("result").GetProperty("date").GetString());
    }

    [Theory]
    [InlineData("reject:run", "BALANCE.UNBALANCED_TC", HttpStatusCode.UnprocessableEntity)]
    [InlineData("reject:run", "AUTH.PERMISSION_MISSING", HttpStatusCode.Forbidden)]
    [InlineData("reject:run", "IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", HttpStatusCode.Conflict)]
    [InlineData("reject:run", "COMPANY.NOT_FOUND", HttpStatusCode.NotFound)]
    [InlineData("reject:run", "SCHEMA.INVALID_VALUE", HttpStatusCode.UnprocessableEntity)]
    [InlineData("fail:run", "SYSTEM.CONFLICT", HttpStatusCode.Conflict)]
    [InlineData("fail:run", "SYSTEM.CHAIN_HEAD_MISSING", HttpStatusCode.InternalServerError)]
    [InlineData("fail:run", "SYSTEM.UNAVAILABLE", HttpStatusCode.ServiceUnavailable)]
    public async Task ARefusalIsProblemJsonWithTheStatusOfItsFirstError(string command, string code, HttpStatusCode status)
    {
        var r = await Client.SendAsync(api.Post(command, Echo(name: code), ApiFixture.Token()));
        Assert.Equal(status, r.StatusCode);
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType!.MediaType);
        var problem = await ApiFixture.Json(r);
        Assert.Equal((int)status, problem.GetProperty("status").GetInt32());
        Assert.Equal(code, Code(problem));
    }

    [Fact]
    public async Task AnErrorCarriesItsPathPhaseParametersAndRetryFlag()
    {
        var r = await Client.SendAsync(api.Post("reject:run", Echo(name: "BALANCE.UNBALANCED_TC"), ApiFixture.Token(), tweak: x => x.Headers.Add("X-Correlation-Id", "c-9")));
        var problem = await ApiFixture.Json(r);
        Assert.Equal("c-9", problem.GetProperty("correlation_id").GetString());
        var e = problem.GetProperty("errors")[0];
        Assert.Equal("lines[0]", e.GetProperty("path").GetString());
        Assert.Equal("P2", e.GetProperty("phase").GetString());
        Assert.False(e.GetProperty("retryable").GetBoolean());
        Assert.Equal(1, e.GetProperty("params").GetProperty("n").GetInt32());
        Assert.Equal("2026-01-02", e.GetProperty("params").GetProperty("when").GetString());

        var conflict = await ApiFixture.Json(await Client.SendAsync(api.Post("fail:run", Echo(name: "SYSTEM.CONFLICT"), ApiFixture.Token())));
        Assert.True(conflict.GetProperty("errors")[0].GetProperty("retryable").GetBoolean());
    }

    [Fact]
    public async Task HealthIsAnonymousAndOnlyPostRunsCommands()
    {
        var health = await Client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await Client.GetAsync("/v1/echo:run")).StatusCode);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Start-up
    // ---------------------------------------------------------------------------------------------------------
    [Fact]
    public void TheDevelopmentKeyIsRefusedInProductionAndAnAuthorityIsRequired()
    {
        Assert.Throws<InvalidOperationException>(() =>
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Production");
                b.UseSetting("Auth:DevSigningKey", ApiFixture.Key);
            });
            factory.CreateClient();
        });
        Assert.Throws<InvalidOperationException>(() =>
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
            factory.CreateClient();
        });
        Assert.Throws<InvalidOperationException>(() =>
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Development");
                b.UseSetting("Auth:DevSigningKey", "short");
            });
            factory.CreateClient();
        });
    }
}
