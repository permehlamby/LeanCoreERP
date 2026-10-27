using System.Net;
using System.Text.Json;
using FinanceCore.Data.Tests;

namespace FinanceCore.Api.Tests;

/// <summary>The master data and configuration commands over HTTP (A3).</summary>
public class ConfigApiTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private HttpClient Client => api.CreateClient();

    private static string Code(JsonElement problem, int index = 0) => problem.GetProperty("errors")[index].GetProperty("code").GetString()!;

    private async Task<(HttpResponseMessage Response, JsonElement Body)> Send(TestTenant t, string command, object body, string key, params string[] permissions)
    {
        var response = await Client.SendAsync(api.Post(command, body, ApiFixture.Token(tenant: t.TenantId, subject: "config-user", permissions: permissions), key));
        return (response, await ApiFixture.Json(response));
    }

    private static Dictionary<string, object?> D(params (string, object?)[] f) => f.ToDictionary(x => x.Item1, x => x.Item2);

    [Fact]
    public async Task ACompanyIsCheckedOpenedAndActivatedOverHttp()
    {
        var t = await TestTenant.CreateAsync(bare: true);
        var company = D(("company_id", t.CompanyId));

        var notReady = await Send(t, "companies:check-readiness", company, "r1", "read.config");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, notReady.Response.StatusCode);
        Assert.Equal("CONFIG.COMPANY_NOT_READY", Code(notReady.Body));
        Assert.Equal("FISCAL_YEAR_OPEN", notReady.Body.GetProperty("errors")[0].GetProperty("params").GetProperty("check").GetString());

        var year = await Send(t, "fiscal-years:open", D(("company_id", t.CompanyId), ("fiscal_year", 2026)), "y1", "period.admin");
        Assert.Equal(HttpStatusCode.OK, year.Response.StatusCode);
        Assert.Equal(12, year.Body.GetProperty("result").GetProperty("periods").GetArrayLength());

        var ready = await Send(t, "companies:check-readiness", company, "r2", "read.config");
        Assert.Equal(HttpStatusCode.OK, ready.Response.StatusCode);
        Assert.True(ready.Body.GetProperty("result").GetProperty("ready").GetBoolean());

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(t, "companies:activate", company, "a0", "read.config")).Response.StatusCode);
        var activated = await Send(t, "companies:activate", company, "a1", "company.activate");
        Assert.Equal(HttpStatusCode.OK, activated.Response.StatusCode);
        Assert.Equal("ACTIVE", activated.Body.GetProperty("result").GetProperty("status").GetString());
        var twice = await Send(t, "companies:activate", company, "a2", "company.activate");
        Assert.Equal("COMPANY.NOT_DRAFT", Code(twice.Body));
    }

    [Fact]
    public async Task APartnerIsCreatedUpdatedWithTypedFieldsAndDeactivated()
    {
        var t = await TestTenant.CreateReadyAsync();
        var create = await Send(t, "business-partners:create", D(
            ("partner", D(("partner_code", "ACME"), ("partner_kind", "ORGANISATION"), ("is_customer", true))),
            ("pii", D(("legal_name", "ACME A/S"), ("country_code", "DK"))),
            ("relations", new object[] { D(("company_id", t.CompanyId), ("fields", D(("is_customer", true), ("default_currency", "DKK")))) })), "p1", "write.master");
        Assert.Equal(HttpStatusCode.OK, create.Response.StatusCode);
        string id = create.Body.GetProperty("result").GetProperty("entity_id").GetString()!;
        Assert.Equal(1, create.Body.GetProperty("result").GetProperty("row_version").GetInt32());

        // a credit limit as a string and an id as a string are read as an amount and a Guid
        var update = await Send(t, "business-partners:update", D(
            ("partner_id", id), ("expected_version", 1),
            ("partner", D(("tenant_credit_limit", "5000.50"), ("tenant_credit_limit_currency", "DKK"))),
            ("pii", D(("city", "København K"))),
            ("relations", Array.Empty<object>())), "p2", "write.master");
        Assert.Equal(HttpStatusCode.OK, update.Response.StatusCode);
        Assert.Equal("5000.5000", await t.ScalarAsync<string>("select tenant_credit_limit::text from core.business_partner where tenant_id = @t"));
        Assert.Equal("København K", await t.ScalarAsync<string>("select city from core.business_partner_pii where tenant_id = @t"));

        var stale = await Send(t, "business-partners:update", D(
            ("partner_id", id), ("expected_version", 1), ("partner", D(("is_supplier", true))), ("relations", Array.Empty<object>())), "p3", "write.master");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, stale.Response.StatusCode);

        var unknown = await Send(t, "business-partners:update", D(
            ("partner_id", id), ("expected_version", 2), ("partner", D(("not_a_field", "x"))), ("relations", Array.Empty<object>())), "p4", "write.master");
        Assert.NotEqual(HttpStatusCode.OK, unknown.Response.StatusCode);

        var wrongType = await Send(t, "business-partners:update", D(
            ("partner_id", id), ("expected_version", 2), ("partner", D(("is_customer", "yes"))), ("relations", Array.Empty<object>())), "p5", "write.master");
        Assert.Equal(HttpStatusCode.BadRequest, wrongType.Response.StatusCode);

        var deactivate = await Send(t, "business-partners:deactivate", D(("id", id), ("expected_version", 2), ("reason_code", "CLOSED")), "p6", "write.master");
        Assert.Equal(HttpStatusCode.OK, deactivate.Response.StatusCode);
        Assert.Equal("BLOCKED", await t.ScalarAsync<string>("select status from core.business_partner where tenant_id = @t"));
        var reactivate = await Send(t, "business-partners:reactivate", D(("id", id), ("expected_version", 3), ("reason_code", "REOPENED")), "p7", "write.master");
        Assert.Equal(HttpStatusCode.OK, reactivate.Response.StatusCode);
        Assert.Equal("ACTIVE", await t.ScalarAsync<string>("select status from core.business_partner where tenant_id = @t"));

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(t, "business-partners:deactivate", D(("id", id), ("expected_version", 4), ("reason_code", "X")), "p8", "read.master")).Response.StatusCode);
    }

    [Fact]
    public async Task PaymentTermsAreCreatedAndUpdatedWithNumbersAsNumbers()
    {
        var t = await TestTenant.CreateReadyAsync();
        var create = await Send(t, "payment-terms:create", D(("fields", D(("code", "N30"), ("description", "30 days"), ("baseline_rule", "DOCUMENT_DATE"), ("net_days", 30)))), "t1", "write.config");
        Assert.True(create.Response.StatusCode == HttpStatusCode.OK, create.Body.ToString());
        string id = create.Body.GetProperty("result").GetProperty("entity_id").GetString()!;
        Assert.Equal("30", await t.ScalarAsync<string>("select net_days::text from core.payment_terms where tenant_id = @t"));

        var update = await Send(t, "payment-terms:update", D(("payment_terms_id", id), ("expected_version", 1), ("fields", D(("net_days", 45), ("discount_days", 10), ("discount_pct", "2.5")))), "t2", "write.config");
        Assert.True(update.Response.StatusCode == HttpStatusCode.OK, update.Body.ToString());
        Assert.Equal("45", await t.ScalarAsync<string>("select net_days::text from core.payment_terms where tenant_id = @t"));
        Assert.Equal("2.5000000000", await t.ScalarAsync<string>("select discount_pct::text from core.payment_terms where tenant_id = @t"));

        var deactivated = await Send(t, "payment-terms:deactivate", D(("id", id), ("expected_version", 2), ("reason_code", "OBSOLETE")), "t3", "write.config");
        Assert.Equal(HttpStatusCode.OK, deactivated.Response.StatusCode);
    }

    [Fact]
    public async Task AccountRolesAndRetentionRulesAreSetThroughTheApi()
    {
        var t = await TestTenant.CreateAsync(withRoles: false, withRetention: false);
        Guid account = Guid.NewGuid();
        await t.ExecuteAsync(
            "insert into core.gl_account (tenant_id, company_id, account_id, account_no, name, account_type, tax_code_rule) values (@t, @c, @a, '7900', 'Rounding', 'EXPENSE', 'FORBIDDEN')",
            ("a", account));

        var role = await Send(t, "account-role-assignments:create", D(("company_id", t.CompanyId), ("role_code", "ROUNDING"), ("account_id", account)), "ro1", "write.config");
        Assert.Equal(HttpStatusCode.OK, role.Response.StatusCode);
        Assert.Equal("ROUNDING", role.Body.GetProperty("result").GetProperty("role_code").GetString());
        var badRole = await Send(t, "account-role-assignments:create", D(("company_id", t.CompanyId), ("role_code", "NOT_A_ROLE"), ("account_id", account)), "ro2", "write.config");
        Assert.Equal(HttpStatusCode.BadRequest, badRole.Response.StatusCode);   // P1: a value that is not in the list

        var rule = await Send(t, "retention-rules:create-version", D(("company_id", t.CompanyId), ("record_class", "LEDGER"), ("country_code", "DK"),
            ("retention_years", 5), ("start_event", "FISCAL_YEAR_END"), ("legal_basis", "Bogføringsloven")), "rr1", "retention.manage");
        Assert.Equal(HttpStatusCode.OK, rule.Response.StatusCode);
        Assert.True(rule.Body.GetProperty("result").GetProperty("new_rule").GetBoolean());
        var second = await Send(t, "retention-rules:create-version", D(("company_id", t.CompanyId), ("record_class", "LEDGER"), ("country_code", "DK"),
            ("retention_years", 10), ("start_event", "FISCAL_YEAR_END"), ("effective_from", "2099-01-01T00:00:00Z")), "rr2", "retention.manage");
        Assert.Equal(HttpStatusCode.OK, second.Response.StatusCode);
        Assert.False(second.Body.GetProperty("result").GetProperty("new_rule").GetBoolean());
        var back = await Send(t, "retention-rules:create-version", D(("company_id", t.CompanyId), ("record_class", "LEDGER"), ("country_code", "DK"),
            ("retention_years", 3), ("start_event", "FISCAL_YEAR_END"), ("effective_from", "2000-01-01T00:00:00Z")), "rr3", "retention.manage");
        Assert.Equal("CONFIG.VALID_FROM_NOT_LATER", Code(back.Body));
    }

    [Fact]
    public async Task APeriodIsSoftClosedHardClosedAndReopenedOverHttp()
    {
        var t = await TestTenant.CreateReadyAsync();
        var period = D(("company_id", t.CompanyId), ("fiscal_year", 2026), ("period_no", 1));
        var soft = await Send(t, "periods:soft-close", period, "s1", "period.admin");
        Assert.Equal(HttpStatusCode.OK, soft.Response.StatusCode);
        Assert.Equal("SOFT_CLOSED", await t.ScalarAsync<string>("select status from core.fiscal_period where tenant_id = @t and period_no = 1"));
        var hard = await Send(t, "periods:hard-close", period, "h1", "period.hard_close");
        Assert.Equal(HttpStatusCode.OK, hard.Response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(t, "periods:reopen", D(("period", period), ("target_status", "OPEN"), ("reason_code", "CORRECTION"), ("reason_text", "x")), "o0", "period.admin")).Response.StatusCode);
        var reopen = await Send(t, "periods:reopen", D(("period", period), ("target_status", "OPEN"), ("reason_code", "CORRECTION"), ("reason_text", "A late invoice")), "o1", "period.reopen");
        Assert.Equal(HttpStatusCode.OK, reopen.Response.StatusCode);
        Assert.Equal("OPEN", await t.ScalarAsync<string>("select status from core.fiscal_period where tenant_id = @t and period_no = 1"));
    }
}
