using System.Net;
using System.Text.Json;
using FinanceCore.Data;
using FinanceCore.Data.Tests;

namespace FinanceCore.Api.Tests;

/// <summary>The transaction commands over HTTP (A2): the same commands as the library tests, reached through identity, headers and JSON.</summary>
public class PostingApiTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private HttpClient Client => api.CreateClient();

    private static string Code(JsonElement problem, int index = 0) => problem.GetProperty("errors")[index].GetProperty("code").GetString()!;

    private async Task<(HttpResponseMessage Response, JsonElement Body)> Send(TestTenant t, string command, object body, string key, params string[] permissions)
    {
        var response = await Client.SendAsync(api.Post(command, body, ApiFixture.Token(tenant: t.TenantId, subject: "api-user", permissions: permissions), key));
        return (response, await ApiFixture.Json(response));
    }

    private static object Journal(TestTenant t, string text = "Via API", string amount = "1000.00", string type = "JOURNAL", Guid? partner = null, object[]? lines = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["company_id"] = t.CompanyId, ["document_type"] = type, ["document_date"] = "2026-03-15", ["posting_date"] = "2026-03-15",
            ["transaction_currency"] = "DKK", ["header_text"] = text,
            ["lines"] = lines ?? new object[]
            {
                new Dictionary<string, object?> { ["line_ref"] = "1", ["account_id"] = t.Bank, ["amount_tc"] = amount },
                new Dictionary<string, object?> { ["line_ref"] = "2", ["account_id"] = t.Revenue, ["amount_tc"] = "-" + amount }
            }
        };
        if (partner is not null) body["partner_id"] = partner;
        return body;
    }

    [Fact]
    public async Task ADocumentIsPostedOverHttpAndTheChainVerifies()
    {
        var t = await TestTenant.CreateReadyAsync();
        var (response, body) = await Send(t, "journal-documents:post", Journal(t), "post-1", "journal.post");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("OK", body.GetProperty("status").GetString());
        Assert.False(body.GetProperty("idempotent_replay").GetBoolean());
        var result = body.GetProperty("result");
        Assert.Equal("GL-2026-000001", result.GetProperty("document_number").GetString());
        Assert.Equal("posted", result.GetProperty("status").GetString());
        Assert.Equal(2, result.GetProperty("lines").GetArrayLength());
        Assert.Equal(64, result.GetProperty("row_hash").GetString()!.Length);
        Assert.Equal("1000.00", result.GetProperty("lines")[0].GetProperty("amount_tc").GetString());
        Assert.Equal("api-user", await t.ScalarAsync<string>("select posted_by from core.journal_document where tenant_id = @t"));
        Assert.Equal("post-1", await t.ScalarAsync<string>("select idempotency_key from core.journal_document where tenant_id = @t"));
        Assert.True((await new ChainVerifier(t.App).VerifyLedgerAsync(t.TenantId, t.CompanyId, t.LedgerId)).Ok);
    }

    [Fact]
    public async Task TheLedgerChainIsVerifiedOverHttpAndABrokenOneNamesItsBreaks()
    {
        var t = await TestTenant.CreateReadyAsync();
        await Send(t, "journal-documents:post", Journal(t), "p1", "journal.post");
        var verify = new Dictionary<string, object?> { ["company_id"] = t.CompanyId, ["chain_type"] = "LEDGER" };

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(t, "chains:verify", verify, "v0", "read.audit")).Response.StatusCode);
        var ok = await Send(t, "chains:verify", verify, "v1", "chain.verify");
        Assert.Equal(HttpStatusCode.OK, ok.Response.StatusCode);
        Assert.True(ok.Body.GetProperty("result").GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, ok.Body.GetProperty("command_log_id").ValueKind);

        await t.ExecuteAsync("""
            alter table core.journal_line disable trigger journal_line_guard;
            update core.journal_line set amount_lc = amount_lc + 1 where tenant_id = @t and line_no = 1;
            alter table core.journal_line enable trigger journal_line_guard;
            """);
        var broken = await Send(t, "chains:verify", verify, "v2", "chain.verify");
        Assert.Equal(HttpStatusCode.OK, broken.Response.StatusCode);
        var result = broken.Body.GetProperty("result");
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Equal(1, result.GetProperty("first_break_seq").GetInt32());
        Assert.Contains(result.GetProperty("breaks").EnumerateArray(), b => b.GetProperty("kind").GetString() == "hash" && b.GetProperty("code").GetString() == "CHAIN.ROW_HASH_MISMATCH");

        var notBuilt = await Send(t, "chains:verify", new Dictionary<string, object?> { ["company_id"] = t.CompanyId, ["chain_type"] = "STOCK" }, "v3", "chain.verify");
        Assert.Equal("LIMIT.CHAIN_TYPE_NOT_BUILT", Code(notBuilt.Body));
    }

    [Fact]
    public async Task ARepeatedKeyReplaysAndAChangedRequestWithTheSameKeyIsRefused()
    {
        var t = await TestTenant.CreateReadyAsync();
        var first = await Send(t, "journal-documents:post", Journal(t), "same", "journal.post");
        var replay = await Send(t, "journal-documents:post", Journal(t), "same", "journal.post");
        Assert.Equal(HttpStatusCode.OK, replay.Response.StatusCode);
        Assert.True(replay.Body.GetProperty("idempotent_replay").GetBoolean());
        Assert.Equal(first.Body.GetProperty("result").GetProperty("document_id").GetString(), replay.Body.GetProperty("result").GetProperty("document_id").GetString());
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.journal_document where tenant_id = @t"));

        var changed = await Send(t, "journal-documents:post", Journal(t, amount: "999.00"), "same", "journal.post");
        Assert.Equal(HttpStatusCode.Conflict, changed.Response.StatusCode);
        Assert.Equal("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", Code(changed.Body));
    }

    [Fact]
    public async Task RefusalsKeepTheirCodesPathsAndStatuses()
    {
        var t = await TestTenant.CreateReadyAsync();

        var denied = await Send(t, "journal-documents:post", Journal(t), "k1", "read.ledger");
        Assert.Equal(HttpStatusCode.Forbidden, denied.Response.StatusCode);
        Assert.Equal("AUTH.PERMISSION_MISSING", Code(denied.Body));
        Assert.Equal("journal.post", denied.Body.GetProperty("errors")[0].GetProperty("params").GetProperty("permission").GetString());

        var unbalanced = await Send(t, "journal-documents:post", Journal(t, lines:
        [
            new Dictionary<string, object?> { ["line_ref"] = "1", ["account_id"] = t.Bank, ["amount_tc"] = "100.00" },
            new Dictionary<string, object?> { ["line_ref"] = "2", ["account_id"] = t.Revenue, ["amount_tc"] = "-90.00" }
        ]), "k2", "journal.post");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unbalanced.Response.StatusCode);
        Assert.Equal("BALANCE.UNBALANCED_TC", Code(unbalanced.Body));
        Assert.Equal("P6", unbalanced.Body.GetProperty("errors")[0].GetProperty("phase").GetString());

        var unknownCompany = Journal(t);
        ((Dictionary<string, object?>)unknownCompany)["company_id"] = Guid.NewGuid();
        var missing = await Send(t, "journal-documents:post", unknownCompany, "k3", "journal.post");
        Assert.Equal(HttpStatusCode.NotFound, missing.Response.StatusCode);
        Assert.Equal("COMPANY.NOT_FOUND", Code(missing.Body));

        var notInTheSlice = await Send(t, "journal-documents:post", Journal(t, type: "YEAR_END"), "k4", "journal.post");
        Assert.Equal("DOCUMENT.TYPE_NOT_ALLOWED", Code(notInTheSlice.Body));
        Assert.Equal(0L, await t.ScalarAsync<long>("select count(*) from core.journal_document where tenant_id = @t"));

        var badField = await Send(t, "journal-documents:post", """{"company_id":"%s","origin":"SYSTEM"}""".Replace("%s", t.CompanyId.ToString()), "k5", "journal.post");
        Assert.Equal(HttpStatusCode.BadRequest, badField.Response.StatusCode);
        Assert.Contains(badField.Body.GetProperty("errors").EnumerateArray(), e => e.GetProperty("code").GetString() is "SCHEMA.UNKNOWN_FIELD" or "SCHEMA.REQUIRED_FIELD_MISSING");
    }

    [Fact]
    public async Task ATokenOfAnotherTenantNeverReachesTheDataOfThisOne()
    {
        var a = await TestTenant.CreateReadyAsync();
        var b = await TestTenant.CreateReadyAsync();
        // tenant B's token with tenant A's company: the company does not exist for B
        var (response, body) = await Send(b, "journal-documents:post", Journal(a), "cross", "journal.post");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("COMPANY.NOT_FOUND", Code(body));
        Assert.Equal(0L, await a.ScalarAsync<long>("select count(*) from core.journal_document where tenant_id = @t"));
    }

    [Fact]
    public async Task AnInvoiceCarriesItsHeaderPartnerIntoTheSnapshot()
    {
        var t = await TestTenant.CreateReadyAsync();
        var partner = await new PartnerCommands(t.App).CreateAsync(t.TenantId, t.Context("pa", "write.master"), new(
            new Dictionary<string, object?> { ["partner_code"] = "ACME", ["partner_kind"] = "ORGANISATION", ["is_customer"] = true },
            new Dictionary<string, object?> { ["legal_name"] = "ACME A/S", ["country_code"] = "DK" },
            [new(t.CompanyId, new Dictionary<string, object?> { ["is_customer"] = true })]));

        var missing = await Send(t, "journal-documents:post", Journal(t, type: "AR_INVOICE"), "i1", "journal.post");
        Assert.Equal("SCHEMA.REQUIRED_FIELD_MISSING", Code(missing.Body));
        Assert.Equal("partner_id", missing.Body.GetProperty("errors")[0].GetProperty("path").GetString());

        var ok = await Send(t, "journal-documents:post", Journal(t, type: "AR_INVOICE", partner: partner.Value!.EntityId), "i2", "journal.post");
        Assert.Equal(HttpStatusCode.OK, ok.Response.StatusCode);
        Assert.Equal("ACME A/S", await t.ScalarAsync<string>("select legal_name from core.document_legal_snapshot where tenant_id = @t and partner_id is not null"));
    }

    [Fact]
    public async Task ADocumentIsReversedOnceAndItsHeaderTextIsChangedWithTheOldValueChecked()
    {
        var t = await TestTenant.CreateReadyAsync();
        string id = (await Send(t, "journal-documents:post", Journal(t), "p", "journal.post")).Body.GetProperty("result").GetProperty("document_id").GetString()!;

        var change = await Send(t, "journal-documents:change-open-fields", new Dictionary<string, object?>
        {
            ["document_id"] = id, ["reason_code"] = "CORRECTION",
            ["changes"] = new object[] { new Dictionary<string, object?> { ["entity_type"] = "DOCUMENT", ["field_name"] = "header_text", ["expected_old_value"] = "Via API", ["new_value"] = "Corrected" } }
        }, "c1", "journal.change");
        Assert.Equal(HttpStatusCode.OK, change.Response.StatusCode);
        Assert.Equal("Corrected", await t.ScalarAsync<string>("select header_text from core.journal_document where tenant_id = @t"));
        var stale = await Send(t, "journal-documents:change-open-fields", new Dictionary<string, object?>
        {
            ["document_id"] = id, ["reason_code"] = "CORRECTION",
            ["changes"] = new object[] { new Dictionary<string, object?> { ["entity_type"] = "DOCUMENT", ["field_name"] = "header_text", ["expected_old_value"] = "Via API", ["new_value"] = "Again" } }
        }, "c2", "journal.change");
        Assert.NotEqual(HttpStatusCode.OK, stale.Response.StatusCode);

        var reverse = await Send(t, "journal-documents:reverse", new Dictionary<string, object?> { ["document_id"] = id, ["reason_code"] = "ERROR" }, "r1", "journal.reverse");
        Assert.Equal(HttpStatusCode.OK, reverse.Response.StatusCode);
        Assert.Equal("RV-2026-000001", reverse.Body.GetProperty("result").GetProperty("document_number").GetString());
        var again = await Send(t, "journal-documents:reverse", new Dictionary<string, object?> { ["document_id"] = id, ["reason_code"] = "ERROR" }, "r2", "journal.reverse");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.Response.StatusCode);
        Assert.Equal("REVERSAL.ALREADY_REVERSED", Code(again.Body));
        Assert.True((await new ChainVerifier(t.App).VerifyLedgerAsync(t.TenantId, t.CompanyId, t.LedgerId)).Ok);
    }

    [Fact]
    public async Task AClearingIsPostedAndResetOverHttp()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid open = Guid.NewGuid();
        await t.ExecuteAsync(
            "insert into core.gl_account (tenant_id, company_id, account_id, account_no, name, account_type, tax_code_rule, open_item_mode) " +
            "values (@t, @c, @a, '1900', 'Bank clearing', 'ASSET', 'FORBIDDEN', 'GL_OPEN_ITEM')", ("a", open));
        async Task<string> Open(string key, string amount) => (await Send(t, "journal-documents:post", Journal(t, lines:
        [
            new Dictionary<string, object?> { ["line_ref"] = "1", ["account_id"] = open, ["amount_tc"] = amount, ["open_item"] = new Dictionary<string, object?>() },
            new Dictionary<string, object?> { ["line_ref"] = "2", ["account_id"] = t.Revenue, ["amount_tc"] = amount.StartsWith('-') ? amount[1..] : "-" + amount }
        ]), key, "journal.post")).Body.GetProperty("result").GetProperty("document_id").GetString()!;
        string a = await Open("a", "1000.00"), b = await Open("b", "-1000.00");

        var clearing = await Send(t, "clearings:post", new Dictionary<string, object?>
        {
            ["company_id"] = t.CompanyId, ["account_id"] = open, ["clearing_date"] = "2026-03-31", ["clearing_method"] = "MANUAL",
            ["items"] = new object[]
            {
                new Dictionary<string, object?> { ["document_id"] = a, ["line_no"] = 1 },
                new Dictionary<string, object?> { ["document_id"] = b, ["line_no"] = 1 }
            }
        }, "c1", "clearing.post");
        Assert.Equal(HttpStatusCode.OK, clearing.Response.StatusCode);
        var result = clearing.Body.GetProperty("result");
        Assert.Equal(2, result.GetProperty("items").GetArrayLength());
        Assert.Equal("CLEARED", result.GetProperty("items")[0].GetProperty("status_after").GetString());

        var denied = await Send(t, "clearings:reset", new Dictionary<string, object?>
        {
            ["company_id"] = t.CompanyId, ["clearing_id"] = result.GetProperty("clearing_id").GetString(), ["clearing_date"] = "2026-04-05", ["reason_code"] = "CORRECTION"
        }, "x0", "clearing.post");
        Assert.Equal(HttpStatusCode.Forbidden, denied.Response.StatusCode);
        var reset = await Send(t, "clearings:reset", new Dictionary<string, object?>
        {
            ["company_id"] = t.CompanyId, ["clearing_id"] = result.GetProperty("clearing_id").GetString(), ["clearing_date"] = "2026-04-05", ["reason_code"] = "CORRECTION"
        }, "x1", "clearing.reset");
        Assert.Equal(HttpStatusCode.OK, reset.Response.StatusCode);
        Assert.True((await new ChainVerifier(t.App).VerifyClearingsAsync(t.TenantId, t.CompanyId)).Ok);
    }
}
