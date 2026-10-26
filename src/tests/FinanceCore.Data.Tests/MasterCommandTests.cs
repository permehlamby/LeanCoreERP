using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;
using Npgsql;

namespace FinanceCore.Data.Tests;

/// <summary>The first master commands (07, 11.1): business-partners:* with personal data and relations, payment-terms:*. The rows of
/// master_change_log, the events and the guards of the tables.</summary>
[Collection("db")]
public class MasterCommandTests
{
    private static string Describe<T>(CommandResult<T> r) =>
        r.Status + ": " + string.Join(", ", r.Errors.Select(e => $"{e.Code} {e.Path}"));

    private static string[] Codes<T>(CommandResult<T> r) => r.Errors.Select(e => e.Code).ToArray();

    private static Dictionary<string, object?> D(params (string, object?)[] f) => f.ToDictionary(x => x.Item1, x => x.Item2);

    private static CommandContext Master(TestTenant t, string key) => t.Context(key, "write.master");
    private static CommandContext Config(TestTenant t, string key) => t.Context(key, "write.config");

    private static async Task<string> Text(TestTenant t, string sql, params (string, object)[] p)
    {
        string q = sql.TrimStart().StartsWith("select", StringComparison.Ordinal) ? sql : "select " + sql;
        return await t.ScalarAsync<string>("select coalesce((" + q + ")::text, '<null>')", p);
    }

    private static async Task<Guid> SecondCompany(TestTenant t)
    {
        Guid id = Guid.NewGuid();
        await t.ExecuteAsync(
            "insert into core.company (tenant_id, company_id, company_code, legal_name, country_code, local_currency, reporting_currency, fiscal_calendar_id, status) " +
            "select tenant_id, @n, 'C2', 'Other A/S', country_code, local_currency, reporting_currency, fiscal_calendar_id, 'DRAFT' from core.company where tenant_id = @t and company_id = @c",
            ("n", id), ("c", t.CompanyId));
        return id;
    }

    private static CreatePartnerRequest Acme(TestTenant t, Guid? terms = null, string kind = "ORGANISATION", string code = "ACME") => new(
        D(("partner_code", code), ("partner_kind", kind), ("is_customer", true), ("is_supplier", false), ("tenant_credit_limit", 50000m), ("tenant_credit_limit_currency", "EUR")),
        D(("legal_name", "Acme A/S"), ("country_code", "DK"), ("email", "ap@acme.example"), ("city", "Aarhus")),
        [new PartnerRelationInput(t.CompanyId, D(("is_customer", true), ("default_payment_terms_id", terms), ("credit_limit", 10000m), ("credit_limit_currency", "DKK")))]);

    private static async Task<Guid> Terms(TestTenant t, string key = "terms", string code = "N30")
    {
        var r = await new PaymentTermsCommands(t.App).CreateAsync(t.TenantId, Config(t, key),
            new(D(("code", code), ("description", "30 days net"), ("baseline_rule", "DOCUMENT_DATE"), ("net_days", 30))));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        return r.Value!.EntityId;
    }

    private static async Task<MasterResult> Create(TestTenant t, string key, CreatePartnerRequest req)
    {
        var r = await new PartnerCommands(t.App).CreateAsync(t.TenantId, Master(t, key), req);
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        return r.Value!;
    }

    // ---------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task CreateWritesThePartnerItsPersonalDataAndItsRelationsWithOneChangeSet()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid terms = await Terms(t);
        var r = await Create(t, "c1", Acme(t, terms));

        Assert.Equal(1L, r.RowVersion);
        Assert.Equal("ACME|ORGANISATION|true|false|ACTIVE|NONE|50000.0000|EUR", await Text(t,
            "partner_code || '|' || partner_kind || '|' || is_customer || '|' || is_supplier || '|' || status || '|' || restriction_status || '|' || tenant_credit_limit || '|' || tenant_credit_limit_currency from core.business_partner where tenant_id = @t and partner_id = @p", ("p", r.EntityId)));
        Assert.Equal("Acme A/S|DK|Aarhus", await Text(t, "legal_name || '|' || country_code || '|' || city from core.business_partner_pii where tenant_id = @t and partner_id = @p", ("p", r.EntityId)));
        Assert.Equal($"true|{terms}|10000.0000|DKK|ACTIVE", await Text(t,
            "is_customer || '|' || default_payment_terms_id || '|' || credit_limit || '|' || credit_limit_currency || '|' || status from core.partner_company_relation where tenant_id = @t and partner_id = @p and company_id = @c",
            ("p", r.EntityId), ("c", t.CompanyId)));

        // three CREATE rows with one change set, the personal one without values, all with the command log row
        Assert.Equal(3L, await t.ScalarAsync<long>("select count(*) from core.master_change_log where tenant_id = @t and action = 'CREATE' and change_set_id = @s and entity_type like '%PARTNER%' and command_log_id is not null", ("s", r.ChangeSetId)));
        Assert.Equal("BUSINESS_PARTNER=false,BUSINESS_PARTNER_PII=true,PARTNER_COMPANY_RELATION=false", await Text(t,
            "string_agg(entity_type || '=' || is_pii, ',' order by entity_type) from core.master_change_log where tenant_id = @t and change_set_id = @s", ("s", r.ChangeSetId)));
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.outbox_event where tenant_id = @t and event_type = 'master.created' and entity_type = 'BUSINESS_PARTNER' and is_audit"));
        // no personal value is in the log or in the event
        Assert.Equal(0L, await t.ScalarAsync<long>("select count(*) from core.master_change_log where tenant_id = @t and (old_value like '%Acme%' or new_value like '%Acme%' or new_value like '%acme.example%')"));
        Assert.Equal(0L, await t.ScalarAsync<long>("select count(*) from core.outbox_event where tenant_id = @t and summary::text like '%Acme%'"));
    }

    [Fact]
    public async Task UpdateLogsOldAndNewValuesAndHidesPersonalOnes()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid second = await SecondCompany(t);
        var created = await Create(t, "c1", Acme(t));

        var r = await new PartnerCommands(t.App).UpdateAsync(t.TenantId, Master(t, "u1"), new UpdatePartnerRequest(
            created.EntityId, 1, D(("partner_code", "ACME2"), ("is_supplier", true)), D(("email", "new@acme.example"), ("city", "Aarhus")),
            [new PartnerRelationInput(t.CompanyId, D(("credit_limit", 12000m))), new PartnerRelationInput(second, D(("is_supplier", true)))]));

        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        Assert.Equal(2L, r.Value!.RowVersion);
        Assert.Equal("ACME2|true", await Text(t, "partner_code || '|' || is_supplier from core.business_partner where tenant_id = @t and partner_id = @p", ("p", created.EntityId)));
        Assert.Equal("new@acme.example|2", await Text(t, "email || '|' || row_version from core.business_partner_pii where tenant_id = @t and partner_id = @p", ("p", created.EntityId)));
        Assert.Equal("12000.0000|2", await Text(t, "credit_limit || '|' || row_version from core.partner_company_relation where tenant_id = @t and partner_id = @p and company_id = @c", ("p", created.EntityId), ("c", t.CompanyId)));
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.partner_company_relation where tenant_id = @t and partner_id = @p and company_id = @c and is_supplier", ("p", created.EntityId), ("c", second)));

        // the city did not change, so it is not logged. A field change names old and new value, a personal field has neither
        Assert.Equal("ACME|ACME2", await Text(t, "old_value || '|' || new_value from core.master_change_log where tenant_id = @t and change_set_id = @s and field_name = 'partner_code'", ("s", r.Value.ChangeSetId)));
        Assert.Equal("true|<null>|<null>", await Text(t, "is_pii || '|' || coalesce(old_value, '<null>') || '|' || coalesce(new_value, '<null>') from core.master_change_log where tenant_id = @t and change_set_id = @s and field_name = 'email'", ("s", r.Value.ChangeSetId)));
        Assert.Equal(0L, await t.ScalarAsync<long>("select count(*) from core.master_change_log where tenant_id = @t and change_set_id = @s and field_name = 'city'", ("s", r.Value.ChangeSetId)));
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.master_change_log where tenant_id = @t and change_set_id = @s and entity_type = 'PARTNER_COMPANY_RELATION' and action = 'CREATE'", ("s", r.Value.ChangeSetId)));
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.outbox_event where tenant_id = @t and event_type = 'master.changed' and entity_key = @p", ("p", created.EntityId.ToString("D"))));

        // compare and set on the version of the partner
        var stale = await new PartnerCommands(t.App).UpdateAsync(t.TenantId, Master(t, "u2"), new UpdatePartnerRequest(created.EntityId, 1, D(("is_supplier", false)), null, []));
        Assert.Equal(["CONFIG.ROW_VERSION_MISMATCH"], Codes(stale));
        Assert.Equal(2L, (long)stale.Errors[0].Params!["current_version"]!);
        var none = await new PartnerCommands(t.App).UpdateAsync(t.TenantId, Master(t, "u3"), new UpdatePartnerRequest(created.EntityId, 2, D(("is_supplier", true)), null, []));
        Assert.Equal(["SCHEMA.NO_EFFECTIVE_CHANGE"], Codes(none));
    }

    [Fact]
    public async Task TheCreditLimitsOfAPersonArePersonalData()
    {
        var t = await TestTenant.CreateReadyAsync();
        var created = await Create(t, "c1", Acme(t, kind: "PERSON", code: "JANE"));
        var r = await new PartnerCommands(t.App).UpdateAsync(t.TenantId, Master(t, "u1"), new UpdatePartnerRequest(
            created.EntityId, 1, D(("tenant_credit_limit", 60000m)), null, [new PartnerRelationInput(t.CompanyId, D(("credit_limit", 11000m)))]));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        Assert.Equal("true|true", await Text(t, "string_agg(is_pii::text, '|' order by entity_type) from core.master_change_log where tenant_id = @t and change_set_id = @s", ("s", r.Value!.ChangeSetId)));
        Assert.Equal(0L, await t.ScalarAsync<long>("select count(*) from core.master_change_log where tenant_id = @t and change_set_id = @s and (old_value is not null or new_value is not null)", ("s", r.Value.ChangeSetId)));
    }

    [Fact]
    public async Task WhatIsNotAValidPartnerIsRejectedAndNothingIsWritten()
    {
        var t = await TestTenant.CreateReadyAsync();
        var commands = new PartnerCommands(t.App);
        async Task<string[]> Try(string key, CreatePartnerRequest req) => Codes(await commands.CreateAsync(t.TenantId, Master(t, key), req));

        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await commands.CreateAsync(t.TenantId, t.Context("p"), Acme(t))));
        Assert.Equal(["SCHEMA.REQUIRED_FIELD_MISSING", "SCHEMA.REQUIRED_FIELD_MISSING", "SCHEMA.REQUIRED_FIELD_MISSING", "SCHEMA.REQUIRED_FIELD_MISSING"],
            await Try("e1", new CreatePartnerRequest(D(), D(), [])));
        Assert.Equal(["SCHEMA.UNKNOWN_FIELD"], await Try("e2", Acme(t) with { Partner = D(("partner_code", "X"), ("partner_kind", "ORGANISATION"), ("status", "ANONYMISED")) }));
        Assert.Equal(["SCHEMA.UNKNOWN_FIELD"], await Try("e3", Acme(t) with { Partner = D(("partner_code", "X"), ("partner_kind", "ORGANISATION"), ("restriction_status", "RESTRICTED")) }));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], await Try("e4", Acme(t) with { Partner = D(("partner_code", "X"), ("partner_kind", "COMPANY")) }));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], await Try("e5", Acme(t) with { Pii = D(("legal_name", "A"), ("country_code", "dk")) }));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], await Try("e6", Acme(t) with { Partner = D(("partner_code", "X"), ("partner_kind", "ORGANISATION"), ("tenant_credit_limit", 5m)) }));   // no currency
        Assert.Equal(["SCHEMA.INVALID_VALUE"], await Try("e7", Acme(t) with { Partner = D(("partner_code", "X"), ("partner_kind", "ORGANISATION"), ("is_customer", "yes")) }));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], await Try("e8", Acme(t) with { Pii = D(("legal_name", "A"), ("country_code", "ZZ")) }));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], await Try("e9", Acme(t) with { Relations = [new PartnerRelationInput(Guid.NewGuid(), D())] }));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], await Try("e10", Acme(t, Guid.NewGuid())));   // payment terms that do not exist
        Assert.Equal(["CONFIG.CURRENCY_NOT_ENABLED"], await Try("e11", Acme(t) with { Partner = D(("partner_code", "X"), ("partner_kind", "ORGANISATION"), ("tenant_credit_limit", 5m), ("tenant_credit_limit_currency", "JPY")) }));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], await Try("e12", Acme(t) with { Relations = [new(t.CompanyId, D()), new(t.CompanyId, D())] }));
        Assert.Equal(0L, await t.ScalarAsync<long>("select count(*) from core.business_partner where tenant_id = @t"));

        await Create(t, "ok", Acme(t));
        Assert.Equal(["CONFIG.DUPLICATE_CODE"], await Try("e13", Acme(t)));
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.business_partner where tenant_id = @t"));
    }

    [Fact]
    public async Task DeactivateBlocksAndReactivateOpensThePartnerAgain()
    {
        var t = await TestTenant.CreateReadyAsync();
        var created = await Create(t, "c1", Acme(t));
        var commands = new PartnerCommands(t.App);

        Assert.Equal(["SCHEMA.REQUIRED_FIELD_MISSING"], Codes(await commands.DeactivateAsync(t.TenantId, Master(t, "d0"), new(created.EntityId, 1, ""))));
        var d = await commands.DeactivateAsync(t.TenantId, Master(t, "d1"), new(created.EntityId, 1, "CREDIT"));
        Assert.True(d.Status == CommandStatus.Ok, Describe(d));
        Assert.Equal(2L, d.Value!.RowVersion);
        Assert.Equal("BLOCKED", await Text(t, "status from core.business_partner where tenant_id = @t and partner_id = @p", ("p", created.EntityId)));
        Assert.Equal("DEACTIVATE|<null>|CREDIT", await Text(t, "action || '|' || coalesce(field_name, '<null>') || '|' || reason_code from core.master_change_log where tenant_id = @t and change_set_id = @s", ("s", d.Value.ChangeSetId)));
        Assert.Equal(["SCHEMA.NO_EFFECTIVE_CHANGE"], Codes(await commands.DeactivateAsync(t.TenantId, Master(t, "d2"), new(created.EntityId, 2, "CREDIT"))));
        Assert.Equal(["CONFIG.ROW_VERSION_MISMATCH"], Codes(await commands.ReactivateAsync(t.TenantId, Master(t, "r0"), new(created.EntityId, 1, "OK"))));

        var r = await commands.ReactivateAsync(t.TenantId, Master(t, "r1"), new(created.EntityId, 2, "PAID"));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        Assert.Equal("ACTIVE", await Text(t, "status from core.business_partner where tenant_id = @t and partner_id = @p", ("p", created.EntityId)));
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.outbox_event where tenant_id = @t and event_type = 'master.deactivated'"));
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.outbox_event where tenant_id = @t and event_type = 'master.reactivated' and is_audit"));
    }

    [Fact]
    public async Task ARepeatedKeyReturnsTheFirstResultAndAnotherRequestIsRejected()
    {
        var t = await TestTenant.CreateReadyAsync();
        var first = await Create(t, "same", Acme(t));
        var again = await new PartnerCommands(t.App).CreateAsync(t.TenantId, Master(t, "same"), Acme(t));
        Assert.True(again.IdempotentReplay, Describe(again));
        Assert.Equal(first.EntityId, again.Value!.EntityId);
        Assert.Equal(first.ChangeSetId, again.Value.ChangeSetId);
        Assert.Equal(["IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST"], Codes(await new PartnerCommands(t.App).CreateAsync(t.TenantId, Master(t, "same"), Acme(t, code: "OTHER"))));
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.business_partner where tenant_id = @t"));
    }

    [Fact]
    public async Task TheGuardsOfTheTablesHoldWhatTheCommandsDoNotReach()
    {
        var t = await TestTenant.CreateReadyAsync();
        var created = await Create(t, "c1", Acme(t));
        string asConfig = "select set_config('app.tenant_id', @t::text, true); set local role cmd_config; ";
        async Task<string> Error(string sql)
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => t.ExecuteAsync(sql, ("p", created.EntityId)));
            return ex.MessageText;
        }
        // S columns have no grant, the configuration role cannot anonymise, nobody deletes
        Assert.Contains("permission denied", await Error(asConfig + "update core.business_partner set restriction_status = 'RESTRICTED' where tenant_id = @t and partner_id = @p"));
        Assert.Contains("permission denied", await Error(asConfig + "update core.business_partner set retention_until = date '2030-01-01' where tenant_id = @t and partner_id = @p"));
        Assert.Contains("only the retention job anonymises", await Error(asConfig + "update core.business_partner set status = 'ANONYMISED' where tenant_id = @t and partner_id = @p"));
        Assert.Contains("no hard delete", await Error("delete from core.business_partner where tenant_id = @t and partner_id = @p"));
        Assert.Contains("no hard delete", await Error("delete from core.business_partner_pii where tenant_id = @t and partner_id = @p"));
        Assert.Contains("permission denied", await Error("select set_config('app.tenant_id', @t::text, true); set local role cmd_post; insert into core.business_partner (tenant_id, partner_code, partner_kind, created_by, updated_by) values (@t, 'X', 'ORGANISATION', 'a', 'a')"));
        Assert.Contains("permission denied", await Error("select set_config('app.tenant_id', @t::text, true); set local role cmd_post; select email from core.business_partner_pii"));   // the posting role reads the snapshot columns only (0021)

        // an anonymised partner and its data change no more, not even by the commands
        await t.ExecuteAsync("update core.business_partner set status = 'ANONYMISED', anonymised_at = now() where tenant_id = @t and partner_id = @p", ("p", created.EntityId));
        var update = await new PartnerCommands(t.App).UpdateAsync(t.TenantId, Master(t, "u1"), new UpdatePartnerRequest(created.EntityId, 1, D(("is_supplier", true)), null, []));
        Assert.Equal(["MASTER.PARTNER_ANONYMISED"], Codes(update));
        Assert.Equal(["MASTER.PARTNER_ANONYMISED"], Codes(await new PartnerCommands(t.App).ReactivateAsync(t.TenantId, Master(t, "r1"), new(created.EntityId, 1, "X"))));
    }

    // ---------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task PaymentTermsAreCreatedChangedAndDeactivated()
    {
        var t = await TestTenant.CreateReadyAsync();
        var commands = new PaymentTermsCommands(t.App);
        var created = await commands.CreateAsync(t.TenantId, Config(t, "c1"), new(D(("code", "N30D2"), ("description", "30 days, 2 % in 10"), ("baseline_rule", "END_OF_MONTH"),
            ("net_days", 30), ("discount_days", 10), ("discount_pct", 2.5m))));
        Assert.True(created.Status == CommandStatus.Ok, Describe(created));
        Guid id = created.Value!.EntityId;
        Assert.Equal("N30D2|END_OF_MONTH|30|10|2.5000000000|true|1", await Text(t,
            "code || '|' || baseline_rule || '|' || net_days || '|' || discount_days || '|' || discount_pct || '|' || active || '|' || row_version from core.payment_terms where tenant_id = @t and payment_terms_id = @p", ("p", id)));

        var u = await commands.UpdateAsync(t.TenantId, Config(t, "u1"), new(id, 1, D(("net_days", 45), ("description", "45 days"))));
        Assert.True(u.Status == CommandStatus.Ok, Describe(u));
        Assert.Equal("30|45", await Text(t, "old_value || '|' || new_value from core.master_change_log where tenant_id = @t and change_set_id = @s and field_name = 'net_days'", ("s", u.Value!.ChangeSetId)));
        Assert.Equal(["CONFIG.ROW_VERSION_MISMATCH"], Codes(await commands.UpdateAsync(t.TenantId, Config(t, "u2"), new(id, 1, D(("net_days", 60))))));
        Assert.Equal(["SCHEMA.NO_EFFECTIVE_CHANGE"], Codes(await commands.UpdateAsync(t.TenantId, Config(t, "u3"), new(id, 2, D(("net_days", 45))))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await commands.UpdateAsync(t.TenantId, Config(t, "u4"), new(id, 2, D(("discount_pct", null))))));   // days stay, so the pair breaks
        var cleared = await commands.UpdateAsync(t.TenantId, Config(t, "u5"), new(id, 2, D(("discount_pct", null), ("discount_days", null))));
        Assert.True(cleared.Status == CommandStatus.Ok, Describe(cleared));

        Assert.Equal(["CONFIG.DUPLICATE_CODE"], Codes(await commands.CreateAsync(t.TenantId, Config(t, "c2"), new(D(("code", "N30D2"), ("description", "x"), ("baseline_rule", "POSTING_DATE"), ("net_days", 1))))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await commands.CreateAsync(t.TenantId, Config(t, "c3"), new(D(("code", "Z"), ("description", "x"), ("baseline_rule", "TOMORROW"), ("net_days", 1))))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await commands.CreateAsync(t.TenantId, Config(t, "c4"), new(D(("code", "Z"), ("description", "x"), ("baseline_rule", "POSTING_DATE"), ("net_days", -1))))));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await commands.CreateAsync(t.TenantId, Master(t, "c5"), new(D()))));   // write.master does not write terms

        var d = await commands.DeactivateAsync(t.TenantId, Config(t, "d1"), new(id, 3, "OBSOLETE"));
        Assert.True(d.Status == CommandStatus.Ok, Describe(d));
        Assert.Equal("false", await Text(t, "active from core.payment_terms where tenant_id = @t and payment_terms_id = @p", ("p", id)));
        // terms that are not active cannot be named by a relation
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await new PartnerCommands(t.App).CreateAsync(t.TenantId, Master(t, "p1"), Acme(t, id))));
        var r = await commands.ReactivateAsync(t.TenantId, Config(t, "r1"), new(id, 4, "AGAIN"));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        Assert.True((await new PartnerCommands(t.App).CreateAsync(t.TenantId, Master(t, "p2"), Acme(t, id))).Status == CommandStatus.Ok);
    }
}
