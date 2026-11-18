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
    public async Task DimensionsAreCreatedRuledAndUsedInAPostingOverHttp()
    {
        var t = await TestTenant.CreateReadyAsync();
        var type = await Send(t, "dimension-types:create", D(("fields", D(("type_code", "REGION"), ("name", "Region")))), "dt1", "write.master");
        Assert.Equal(HttpStatusCode.OK, type.Response.StatusCode);
        string typeId = type.Body.GetProperty("result").GetProperty("entity_id").GetString()!;

        // a date is text in JSON, an id is a string, a bad date is a field error with its path
        var badDate = await Send(t, "dimension-values:create",
            D(("fields", D(("dimension_type_id", typeId), ("value_code", "N"), ("name", "North"), ("valid_from", "15/03/2026")))), "dv0", "write.master");
        Assert.Equal(HttpStatusCode.BadRequest, badDate.Response.StatusCode);
        Assert.Equal("/fields/valid_from", badDate.Body.GetProperty("errors")[0].GetProperty("path").GetString());
        var value = await Send(t, "dimension-values:create",
            D(("fields", D(("dimension_type_id", typeId), ("value_code", "N"), ("name", "North"), ("valid_from", "2026-01-01")))), "dv1", "write.master");
        Assert.Equal(HttpStatusCode.OK, value.Response.StatusCode);
        string valueId = value.Body.GetProperty("result").GetProperty("entity_id").GetString()!;

        var rule = await Send(t, "account-dimension-rules:create", D(("company_id", t.CompanyId), ("account_id", t.Revenue), ("dimension_type_id", typeId), ("rule", "MANDATORY")), "ar1", "write.config");
        Assert.True(rule.Response.StatusCode == HttpStatusCode.OK, rule.Body.ToString());
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(t, "account-dimension-rules:create", D(("company_id", t.CompanyId), ("account_id", t.Revenue), ("dimension_type_id", typeId), ("rule", "FORBIDDEN")), "ar2", "write.master")).Response.StatusCode);

        object Lines(object? revenueDimensions) => new object[]
        {
            D(("line_ref", "1"), ("account_id", t.Bank), ("amount_tc", "100.00")),
            D(("line_ref", "2"), ("account_id", t.Revenue), ("amount_tc", "-100.00"), ("dimensions", revenueDimensions))
        };
        object Post(object? dimensions) => D(("company_id", t.CompanyId), ("document_type", "JOURNAL"), ("document_date", "2026-03-15"), ("posting_date", "2026-03-15"),
            ("transaction_currency", "DKK"), ("lines", Lines(dimensions)));

        var missing = await Send(t, "journal-documents:post", Post(null), "p1", "journal.post");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.Response.StatusCode);
        Assert.Equal("ACCOUNT_RULE.DIMENSION_REQUIRED", Code(missing.Body));
        Assert.Equal("lines[1].dimensions", missing.Body.GetProperty("errors")[0].GetProperty("path").GetString());

        var posted = await Send(t, "journal-documents:post", Post(D(("free", new object[] { D(("dimension_type_id", typeId), ("dimension_value_id", valueId)) }))), "p2", "journal.post");
        Assert.Equal(HttpStatusCode.OK, posted.Response.StatusCode);
        string documentId = posted.Body.GetProperty("result").GetProperty("document_id").GetString()!;

        var change = await Send(t, "journal-documents:change-open-fields", D(("document_id", documentId), ("reason_code", "CORRECTION"),
            ("changes", new object[] { D(("entity_type", "LINE_DIMENSION"), ("line_no", 2), ("field_name", "dimension_value_id"), ("sub_key", typeId), ("expected_old_value", valueId), ("new_value", null)) })), "c1", "journal.change");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, change.Response.StatusCode);
        Assert.Equal("ACCOUNT_RULE.DIMENSION_REQUIRED", Code(change.Body));
    }

    [Fact]
    public async Task ATaxedDocumentIsPostedWithTheRuleOfItsDestinationOverHttp()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid vatAccount = Guid.NewGuid();
        await t.ExecuteAsync(
            "insert into core.gl_account (tenant_id, company_id, account_id, account_no, name, account_type, tax_code_rule) values (@t, @c, @v, '2700', 'VAT', 'LIABILITY', 'FORBIDDEN'); " +
            "update core.gl_account set tax_code_rule = 'MANDATORY' where tenant_id = @t and account_id = @r", ("c", t.CompanyId), ("v", vatAccount), ("r", t.Revenue));
        var registration = await Send(t, "company-vat-registrations:create", D(("fields", D(("company_id", t.CompanyId), ("country_code", "DK"), ("vat_id", "DK12345678"), ("scheme", "STANDARD"),
            ("filing_frequency", "QUARTERLY"), ("correction_method", "ADJUST_NEXT"), ("return_currency", "DKK"), ("valid_from", "2026-01-01")))), "vr1", "write.config");
        string registrationId = registration.Body.GetProperty("result").GetProperty("entity_id").GetString()!;
        var code = await Send(t, "tax-codes:create", D(("fields", D(("company_id", t.CompanyId), ("code", "U25"), ("description", "Output 25"), ("tax_type", "OUTPUT"), ("vat_registration_id", registrationId)))), "tc1", "write.config");
        string codeId = code.Body.GetProperty("result").GetProperty("entity_id").GetString()!;
        var version = await Send(t, "tax-codes:create-version", D(("tax_code_id", codeId), ("fields", D(("valid_from", "2026-01-01"), ("rate", 25), ("vat_account_id", vatAccount))), ("boxes", Array.Empty<object>())), "tv1", "write.config");
        Assert.Equal(HttpStatusCode.OK, version.Response.StatusCode);

        // a rule is created with its first version and its alternatives, and answers its id
        var rule = await Send(t, "tax-determination-rules:create", D(("company_id", t.CompanyId), ("fields", D(("valid_from", "2026-01-01"), ("priority", 10), ("tax_code_id", codeId), ("destination_country", "DK"))),
            ("alternatives", Array.Empty<object>())), "tr1", "write.config");
        Assert.True(rule.Response.StatusCode == HttpStatusCode.OK, rule.Body.ToString());
        Assert.Equal(0, rule.Body.GetProperty("result").GetProperty("alternative_count").GetInt32());
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(t, "tax-determination-rules:create", D(("company_id", t.CompanyId), ("fields", D())), "tr2", "write.master")).Response.StatusCode);

        object Post(string? tax) => D(("company_id", t.CompanyId), ("document_type", "JOURNAL"), ("document_date", "2026-03-15"), ("posting_date", "2026-03-15"), ("transaction_currency", "DKK"),
            ("origin_country", "DK"), ("destination_country", tax), ("lines", new object[]
            {
                D(("line_ref", "1"), ("account_id", t.Bank), ("amount_tc", "1250.00")),
                D(("line_ref", "2"), ("account_id", t.Revenue), ("amount_tc", "-1000.00"), ("tax_code_id", codeId))
            }));
        var posted = await Send(t, "journal-documents:post", Post("DK"), "p1", "journal.post");
        Assert.Equal(HttpStatusCode.OK, posted.Response.StatusCode);
        var lines = posted.Body.GetProperty("result").GetProperty("lines");
        Assert.Equal(3, lines.GetArrayLength());
        Assert.Equal("TAX", lines[2].GetProperty("line_type").GetString());
        Assert.Equal(-250m, decimal.Parse(lines[2].GetProperty("amount_tc").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(codeId, lines[2].GetProperty("tax_code_id").GetString());

        var noRule = await Send(t, "journal-documents:post", Post("SE"), "p2", "journal.post");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noRule.Response.StatusCode);
        Assert.Equal("TAX.NO_RULE_MATCHED", Code(noRule.Body));
    }

    [Fact]
    public async Task VatConfigurationIsCreatedWithItsBoxesOverHttp()
    {
        var t = await TestTenant.CreateReadyAsync();
        var registration = await Send(t, "company-vat-registrations:create", D(("fields", D(("company_id", t.CompanyId), ("country_code", "DK"), ("vat_id", "DK12345678"), ("scheme", "STANDARD"),
            ("filing_frequency", "QUARTERLY"), ("correction_method", "ADJUST_NEXT"), ("return_currency", "DKK"), ("valid_from", "2026-01-01")))), "vr1", "write.config");
        Assert.Equal(HttpStatusCode.OK, registration.Response.StatusCode);
        string registrationId = registration.Body.GetProperty("result").GetProperty("entity_id").GetString()!;

        var box = await Send(t, "tax-return-boxes:create", D(("country_code", "DK"), ("return_type", "STANDARD"), ("box_code", "A"), ("fields", D(("name", "Box A")))), "tb1", "write.config");
        Assert.Equal(HttpStatusCode.OK, box.Response.StatusCode);

        var code = await Send(t, "tax-codes:create", D(("fields", D(("company_id", t.CompanyId), ("code", "U25"), ("description", "Output 25"), ("tax_type", "OUTPUT"), ("vat_registration_id", registrationId)))), "tc1", "write.config");
        Assert.Equal(HttpStatusCode.OK, code.Response.StatusCode);
        string codeId = code.Body.GetProperty("result").GetProperty("entity_id").GetString()!;

        // a rate is a number or a string, a date is text, the boxes are a list of objects
        var version = await Send(t, "tax-codes:create-version", D(("tax_code_id", codeId), ("fields", D(("valid_from", "2026-01-01"), ("rate", 25), ("vat_account_id", t.Bank))),
            ("boxes", new object[] { D(("line_side", "PRIMARY"), ("return_type", "STANDARD"), ("box_code", "A"), ("amount_kind", "BASE"), ("sign", 1)) })), "tv1", "write.config");
        Assert.Equal(HttpStatusCode.OK, version.Response.StatusCode);
        Assert.Equal(1, version.Body.GetProperty("result").GetProperty("box_count").GetInt32());

        var again = await Send(t, "tax-codes:create-version", D(("tax_code_id", codeId), ("fields", D(("valid_from", "2026-01-01"), ("rate", 25), ("vat_account_id", t.Bank))), ("boxes", Array.Empty<object>())), "tv2", "write.config");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.Response.StatusCode);
        Assert.Equal("CONFIG.VALID_FROM_NOT_LATER", Code(again.Body));
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(t, "tax-codes:create-version", D(("tax_code_id", codeId), ("fields", D())), "tv3", "write.master")).Response.StatusCode);
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
        Assert.True(rule.Response.StatusCode == HttpStatusCode.OK, rule.Body.ToString());
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
