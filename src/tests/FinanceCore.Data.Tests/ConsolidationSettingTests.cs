using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;

namespace FinanceCore.Data.Tests;

/// <summary>company-consolidation-settings:create-version (23, 3.1; CT-03, CT-04): ownership, parent chain, methods and the translation method against the currencies.</summary>
[Collection("db")]
public class ConsolidationSettingTests
{
    private static string Describe<T>(CommandResult<T> r) => r.Status + ": " + string.Join(", ", r.Errors.Select(e => $"{e.Code} {e.Path}"));
    private static string[] Codes<T>(CommandResult<T> r) => r.Errors.Select(e => e.Code).ToArray();
    private static CommandContext Ctx(TestTenant t, string key) => t.Context(key, ConsolidationSettingCommands.Permission);

    private static Dictionary<string, object?> F(string from, Guid? parent = null, decimal? pct = null, string method = "FULL", string translation = "CURRENT_RATE")
    {
        var f = new Dictionary<string, object?> { ["effective_from"] = from, ["consolidation_method"] = method, ["translation_method"] = translation };
        if (parent is not null) f["parent_company_id"] = parent;
        if (pct is not null) f["ownership_pct"] = pct;
        return f;
    }

    private static async Task<Guid> Company(TestTenant t, string code, string currency, bool member)
    {
        Guid id = Guid.NewGuid();
        await t.ExecuteAsync(
            "insert into core.company (tenant_id, company_id, company_code, legal_name, country_code, local_currency, reporting_currency, fiscal_calendar_id, status) " +
            "select tenant_id, @n, @code, 'Other A/S', country_code, @cur, reporting_currency, fiscal_calendar_id, 'DRAFT' from core.company where tenant_id = @t and company_id = @c",
            ("n", id), ("c", t.CompanyId), ("code", code), ("cur", currency));
        if (member)
            await t.ExecuteAsync("insert into core.group_membership (tenant_id, company_id, valid_from, group_id) select tenant_id, @n, date '2026-01-01', group_id from core.group_membership where tenant_id = @t and company_id = @c",
                ("n", id), ("c", t.CompanyId));
        return id;
    }

    private static Task<CommandResult<ConsolidationSettingVersionResult>> Create(TestTenant t, string key, Guid company, Dictionary<string, object?> fields) =>
        new ConsolidationSettingCommands(t.App).CreateVersionAsync(t.TenantId, Ctx(t, key), new(company, fields));

    [Fact]
    public async Task AParentWithItsShareIsStoredAndTheNextVersionMustBeLater()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid parent = await Company(t, "P1", "DKK", member: true);
        var ok = await Create(t, "a", t.CompanyId, F("2026-01-01", parent, 80m));
        Assert.True(ok.Status == CommandStatus.Ok, Describe(ok));
        Assert.Equal(new DateOnly(2026, 1, 1), ok.Value!.EffectiveFrom);
        string stored = await t.ScalarAsync<string>("select ownership_pct::text from core.company_consolidation_setting_version where tenant_id = @t and company_id = @c", ("c", t.CompanyId));
        Assert.Equal(80m, decimal.Parse(stored, System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(["CONFIG.VALID_FROM_NOT_LATER"], Codes(await Create(t, "b", t.CompanyId, F("2026-01-01"))));
        var later = await Create(t, "c", t.CompanyId, F("2026-07-01", method: "NOT_CONSOLIDATED"));
        Assert.True(later.Status == CommandStatus.Ok, Describe(later));
        Assert.Equal(2L, await t.ScalarAsync<long>("select count(*) from core.company_consolidation_setting_version where tenant_id = @t and company_id = @c", ("c", t.CompanyId)));

        // the list shows every version, filtered by method
        var reads = new MasterReads(t.App);
        var all = await reads.ListAsync(t.TenantId, t.Context("l1", "read.config"), ConsolidationSettingCommands.ReadSpec, new());
        Assert.Equal(2, all.Value!.Items.Count);
        var notConsolidated = await reads.ListAsync(t.TenantId, t.Context("l2", "read.config"), ConsolidationSettingCommands.ReadSpec, new(new Dictionary<string, string> { ["consolidation_method"] = "NOT_CONSOLIDATED" }));
        Assert.Single(notConsolidated.Value!.Items);
    }

    [Fact]
    public async Task TheSameKeyReplaysAndAChangedRequestIsRefused()
    {
        var t = await TestTenant.CreateReadyAsync();
        var first = await Create(t, "k", t.CompanyId, F("2026-01-01"));
        var replay = await Create(t, "k", t.CompanyId, F("2026-01-01"));
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(first.Value!.EffectiveFrom, replay.Value!.EffectiveFrom);
        Assert.Equal(["IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST"], Codes(await Create(t, "k", t.CompanyId, F("2026-01-01", method: "EQUITY"))));
    }

    [Fact]
    public async Task OwnershipRulesOfCt03()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid parent = await Company(t, "P1", "DKK", member: true);
        Guid outsider = await Company(t, "X1", "DKK", member: false);
        Guid p = parent;

        Assert.Equal(["CONFIG.OWNERSHIP_INVALID"], Codes(await Create(t, "a", t.CompanyId, F("2026-01-01", p, 0m))));
        // above 100 is refused by the rate type of the field registry before the rule runs
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await Create(t, "b", t.CompanyId, F("2026-01-01", p, 101m))));
        Assert.Equal(["CONFIG.OWNERSHIP_INVALID"], Codes(await Create(t, "c", t.CompanyId, F("2026-01-01", p))));
        Assert.Equal(["CONFIG.OWNERSHIP_INVALID"], Codes(await Create(t, "d", t.CompanyId, F("2026-01-01", null, 50m))));
        Assert.Equal(["CONFIG.OWNERSHIP_INVALID"], Codes(await Create(t, "e", t.CompanyId, F("2026-01-01", outsider, 50m))));
        Assert.Equal(["CONFIG.OWNERSHIP_INVALID"], Codes(await Create(t, "f", t.CompanyId, F("2026-01-01", t.CompanyId, 50m))));

        // the cycle: C1 owned by P1, then P1 owned by C1
        Assert.True((await Create(t, "g", t.CompanyId, F("2026-01-01", p, 60m))).Status == CommandStatus.Ok);
        Assert.Equal(["CONFIG.OWNERSHIP_CYCLE"], Codes(await Create(t, "h", parent, F("2026-02-01", t.CompanyId, 40m))));
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.company_consolidation_setting_version where tenant_id = @t"));
    }

    [Fact]
    public async Task MembershipAndTranslationMethodOfCt04()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid eur = await Company(t, "E1", "EUR", member: true);
        Guid loose = await Company(t, "X1", "DKK", member: false);

        // DKK company, EUR group: NONE is invalid. EUR company: only NONE is valid
        Assert.Equal(["CONFIG.TRANSLATION_METHOD_INVALID"], Codes(await Create(t, "a", t.CompanyId, F("2026-01-01", translation: "NONE"))));
        Assert.True((await Create(t, "b", t.CompanyId, F("2026-01-01", translation: "TEMPORAL"))).Status == CommandStatus.Ok);
        Assert.Equal(["CONFIG.TRANSLATION_METHOD_INVALID"], Codes(await Create(t, "c", eur, F("2026-01-01", translation: "CURRENT_RATE"))));
        Assert.True((await Create(t, "d", eur, F("2026-01-01", translation: "NONE"))).Status == CommandStatus.Ok);

        // not a member at the date, and before the membership started
        Assert.Equal(["CONFIG.NOT_A_GROUP_MEMBER"], Codes(await Create(t, "e", loose, F("2026-01-01"))));
        Guid late = await Company(t, "L1", "DKK", member: true);
        Assert.Equal(["CONFIG.NOT_A_GROUP_MEMBER"], Codes(await Create(t, "f", late, F("2025-12-31"))));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await Create(t, "g", Guid.NewGuid(), F("2026-01-01"))));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await new ConsolidationSettingCommands(t.App).CreateVersionAsync(t.TenantId, t.Context("h", "journal.post"), new(t.CompanyId, F("2026-08-01")))));
    }
}
