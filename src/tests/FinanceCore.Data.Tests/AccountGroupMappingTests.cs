using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;

namespace FinanceCore.Data.Tests;

/// <summary>account-group-mappings:create and :list (07, 6.4, 23, 4; CT-02): the group account must belong to the group of the company at valid_from, one mapping holds at a time.</summary>
[Collection("db")]
public class AccountGroupMappingTests
{
    private static string Describe<T>(CommandResult<T> r) => r.Status + ": " + string.Join(", ", r.Errors.Select(e => $"{e.Code} {e.Path}"));
    private static string[] Codes<T>(CommandResult<T> r) => r.Errors.Select(e => e.Code).ToArray();
    private static CommandContext Write(TestTenant t, string key) => t.Context(key, AccountGroupMappingCommands.Permission);

    private static async Task<Guid> Guid1(TestTenant t, string sql, params (string, object)[] p) => Guid.Parse(await t.ScalarAsync<string>(sql, p));

    private static async Task<Guid> GroupAccount(TestTenant t, Guid group, string code, bool active = true)
    {
        Guid id = Guid.NewGuid();
        await t.ExecuteAsync("insert into core.group_chart_account (tenant_id, group_account_id, group_id, code, name, account_type, active, created_by, updated_by) values (@t, @id, @g, @code, 'Cash', 'ASSET', @a, 'test', 'test')",
            ("id", id), ("g", group), ("code", code), ("a", active));
        return id;
    }

    private static Task<CommandResult<AccountGroupMappingResult>> Map(TestTenant t, string key, Guid account, Guid groupAccount, string from, Guid? company = null) =>
        new AccountGroupMappingCommands(t.App).CreateAsync(t.TenantId, Write(t, key), new(company ?? t.CompanyId, account, groupAccount, DateOnly.Parse(from)));

    [Fact]
    public async Task ARemappingEndsTheOpenRowAndTheListShowsBothVersions()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid group = await Guid1(t, "select group_id::text from core.group_membership where tenant_id = @t and company_id = @c", ("c", t.CompanyId));
        Guid account = await Guid1(t, "select account_id::text from core.gl_account where tenant_id = @t and company_id = @c and account_no = '1000'", ("c", t.CompanyId));
        Guid g1 = await GroupAccount(t, group, "G100"), g2 = await GroupAccount(t, group, "G110");

        var first = await Map(t, "a", account, g1, "2026-01-01");
        Assert.True(first.Status == CommandStatus.Ok, Describe(first));
        Assert.Null(first.Value!.ClosedPreviousFrom);
        var second = await Map(t, "b", account, g2, "2026-07-01");
        Assert.True(second.Status == CommandStatus.Ok, Describe(second));
        Assert.Equal(new DateOnly(2026, 1, 1), second.Value!.ClosedPreviousFrom);
        Assert.Equal("2026-07-01", await t.ScalarAsync<string>("select valid_to::text from core.account_group_mapping where tenant_id = @t and account_id = @a and valid_from = date '2026-01-01'", ("a", account)));

        var list = await new MasterReads(t.App).ListAsync(t.TenantId, t.Context("l", "read.config"), AccountGroupMappingCommands.ReadSpec, new());
        Assert.Equal(2, list.Value!.Items.Count);

        Assert.Equal(["CONFIG.VALID_FROM_NOT_LATER"], Codes(await Map(t, "c", account, g1, "2026-07-01")));
        Assert.Equal(["CONFIG.VALID_FROM_NOT_LATER"], Codes(await Map(t, "d", account, g1, "2026-03-01")));
        // only valid_to of an open row may change
        await Assert.ThrowsAnyAsync<Exception>(() => t.ExecuteAsync("update core.account_group_mapping set group_account_id = @g where tenant_id = @t and account_id = @a", ("g", g1), ("a", account)));
        await Assert.ThrowsAnyAsync<Exception>(() => t.ExecuteAsync("update core.account_group_mapping set valid_to = date '2026-12-31' where tenant_id = @t and account_id = @a and valid_from = date '2026-01-01'", ("a", account)));
    }

    [Fact]
    public async Task TheGroupAccountMustBelongToTheGroupOfTheCompanyAtValidFrom()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid group = await Guid1(t, "select group_id::text from core.group_membership where tenant_id = @t and company_id = @c", ("c", t.CompanyId));
        Guid account = await Guid1(t, "select account_id::text from core.gl_account where tenant_id = @t and company_id = @c and account_no = '1000'", ("c", t.CompanyId));
        Guid other = Guid.NewGuid();
        await t.ExecuteAsync("insert into core.company_group (tenant_id, group_id, group_code, name, group_currency) values (@t, @g, 'GRP2', 'Other group', 'EUR')", ("g", other));
        Guid foreign = await GroupAccount(t, other, "X100"), own = await GroupAccount(t, group, "G100"), off = await GroupAccount(t, group, "G900", active: false);

        Assert.Equal(["CONFIG.GROUP_ACCOUNT_OTHER_GROUP"], Codes(await Map(t, "a", account, foreign, "2026-01-01")));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await Map(t, "b", account, off, "2026-01-01")));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await Map(t, "c", account, Guid.NewGuid(), "2026-01-01")));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await Map(t, "d", Guid.NewGuid(), own, "2026-01-01")));
        // before the membership started
        Assert.Equal(["CONFIG.NOT_A_GROUP_MEMBER"], Codes(await Map(t, "e", account, own, "2025-12-31")));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await new AccountGroupMappingCommands(t.App).CreateAsync(t.TenantId, t.Context("f", "journal.post"), new(t.CompanyId, account, own, new DateOnly(2026, 1, 1)))));
        Assert.Equal(0L, await t.ScalarAsync<long>("select count(*) from core.account_group_mapping where tenant_id = @t"));

        var ok = await Map(t, "g", account, own, "2026-01-01");
        Assert.True(ok.Status == CommandStatus.Ok, Describe(ok));
        var replay = await Map(t, "g", account, own, "2026-01-01");
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(["IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST"], Codes(await Map(t, "g", account, own, "2026-02-01")));
    }
}
