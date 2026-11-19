using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;

namespace FinanceCore.Data.Tests;

/// <summary>The account determination of inventory and order events (10, 3.9): versions, the role's open-item mode, the overlap rule and the resolution.</summary>
[Collection("db")]
public class AccountDeterminationTests
{
    private static string Describe<T>(CommandResult<T> r) => RevaluationTests.Describe(r);
    private static string[] Codes<T>(CommandResult<T> r) => RevaluationTests.Codes(r);
    private static CommandContext Write(TestTenant t, string key) => t.Context(key, "write.config");
    private static Dictionary<string, object?> D(params (string, object?)[] f) => f.ToDictionary(x => x.Item1, x => x.Item2);

    private static async Task<Guid> AccountAsync(TestTenant t, string no, string mode)
    {
        Guid id = Guid.NewGuid();
        await t.ExecuteAsync("insert into core.gl_account (tenant_id, company_id, account_id, account_no, name, account_type, tax_code_rule, open_item_mode) values (@t, @c, @a, @no, 'X', 'ASSET', 'FORBIDDEN', @m)",
            ("a", id), ("no", no), ("m", mode));
        return id;
    }

    private static async Task<Guid> GroupAsync(TestTenant t, string key, string code)
    {
        var r = await new InventoryMasterCommands(t.App).CreateAsync("item-groups", t.TenantId, Write(t, key), new(D(("code", code), ("name", code))));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        return r.Value!.EntityId;
    }

    private static async Task<DeterminationMatch?> ResolveAsync(TestTenant t, string role, Guid? group, string date, Guid? warehouse = null)
    {
        await using var tx = await TenantTransaction.BeginAsync(t.App, t.TenantId, CommandRole.Read);
        return await AccountDeterminationResolver.ResolveAsync(tx, t.TenantId, t.CompanyId, role, group, warehouse, null, null, DateOnly.Parse(date), default);
    }

    [Fact]
    public async Task TheMostSpecificVersionThatHoldsAtTheDateWinsThenThePriority()
    {
        var t = await TestTenant.CreateReadyAsync();
        var cmds = new AccountDeterminationCommands(t.App);
        Guid general = await AccountAsync(t, "1410", "NONE"), foodAccount = await AccountAsync(t, "1411", "NONE"), later = await AccountAsync(t, "1412", "NONE");
        Guid food = await GroupAsync(t, "g1", "FOOD"), other = await GroupAsync(t, "g2", "OTHER");

        var all = await cmds.CreateAsync(t.TenantId, Write(t, "d1"), new(t.CompanyId, D(("role_code", "INVENTORY"), ("valid_from", "2026-01-01"), ("priority", 10), ("account_id", general))));
        Assert.True(all.Status == CommandStatus.Ok, Describe(all));
        var foodRule = await cmds.CreateAsync(t.TenantId, Write(t, "d2"), new(t.CompanyId, D(("role_code", "INVENTORY"), ("item_group_id", food), ("valid_from", "2026-01-01"), ("priority", 10), ("account_id", foodAccount))));
        Assert.Equal(CommandStatus.Ok, foodRule.Status);

        Assert.Equal(general, (await ResolveAsync(t, "INVENTORY", other, "2026-03-01"))!.AccountId);
        Assert.Equal(foodAccount, (await ResolveAsync(t, "INVENTORY", food, "2026-03-01"))!.AccountId);
        Assert.Equal(general, (await ResolveAsync(t, "INVENTORY", null, "2026-03-01"))!.AccountId);
        Assert.Null(await ResolveAsync(t, "INVENTORY", food, "2025-12-31"));   // before the first version
        Assert.Null(await ResolveAsync(t, "COGS", food, "2026-03-01"));         // no rule for the role

        // a version replaces the one before it from its valid_from on, and a version needs a later valid_from
        var v2 = await cmds.CreateVersionAsync(t.TenantId, Write(t, "d3"), new(foodRule.Value!.DeterminationId, D(("role_code", "INVENTORY"), ("item_group_id", food), ("valid_from", "2026-07-01"), ("priority", 10), ("account_id", later))));
        Assert.True(v2.Status == CommandStatus.Ok, Describe(v2));
        Assert.Equal(foodAccount, (await ResolveAsync(t, "INVENTORY", food, "2026-06-30"))!.AccountId);
        Assert.Equal(later, (await ResolveAsync(t, "INVENTORY", food, "2026-07-01"))!.AccountId);
        Assert.Equal(["CONFIG.VALID_FROM_NOT_LATER"], Codes(await cmds.CreateVersionAsync(t.TenantId, Write(t, "d4"), new(foodRule.Value.DeterminationId, D(("role_code", "INVENTORY"), ("item_group_id", food), ("valid_from", "2026-07-01"), ("priority", 10), ("account_id", later))))));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await cmds.CreateVersionAsync(t.TenantId, Write(t, "d5"), new(Guid.NewGuid(), D(("role_code", "INVENTORY"), ("valid_from", "2026-08-01"), ("priority", 10), ("account_id", later))))));

        // the same keys and priority over an overlapping time are refused, another priority is not, and a lower priority number wins
        Assert.Equal(["CONFIG.OVERLAPPING_RULE"], Codes(await cmds.CreateAsync(t.TenantId, Write(t, "d6"), new(t.CompanyId, D(("role_code", "INVENTORY"), ("valid_from", "2026-05-01"), ("priority", 10), ("account_id", general))))));
        Guid preferred = await AccountAsync(t, "1413", "NONE");
        Assert.Equal(CommandStatus.Ok, (await cmds.CreateAsync(t.TenantId, Write(t, "d7"), new(t.CompanyId, D(("role_code", "INVENTORY"), ("valid_from", "2026-01-01"), ("priority", 5), ("account_id", preferred))))).Status);
        Assert.Equal(preferred, (await ResolveAsync(t, "INVENTORY", other, "2026-03-01"))!.AccountId);
        Assert.Equal(foodAccount, (await ResolveAsync(t, "INVENTORY", food, "2026-03-01"))!.AccountId);   // specificity before priority

        // the rows are never changed or deleted
        await Assert.ThrowsAnyAsync<Exception>(() => t.ExecuteAsync("update core.account_determination set priority = 1 where tenant_id = @t"));
        await Assert.ThrowsAnyAsync<Exception>(() => t.ExecuteAsync("delete from core.account_determination where tenant_id = @t"));
    }

    [Fact]
    public async Task TheAccountOfARoleHasTheOpenItemModeTheRoleNeeds()
    {
        var t = await TestTenant.CreateReadyAsync();
        var cmds = new AccountDeterminationCommands(t.App);
        Guid plain = await AccountAsync(t, "1420", "NONE"), grir = await AccountAsync(t, "2420", "GL_OPEN_ITEM"), control = await AccountAsync(t, "1300", "CONTROL");

        static Dictionary<string, object?> Rule(string role, Guid account) => D(("role_code", role), ("valid_from", "2026-01-01"), ("priority", 1), ("account_id", account));
        Assert.Equal(["CONFIG.ROLE_ACCOUNT_RULE_VIOLATED"], Codes(await cmds.CreateAsync(t.TenantId, Write(t, "m1"), new(t.CompanyId, Rule("GR_IR", plain)))));
        Assert.Equal(["CONFIG.ROLE_ACCOUNT_RULE_VIOLATED"], Codes(await cmds.CreateAsync(t.TenantId, Write(t, "m2"), new(t.CompanyId, Rule("INVENTORY", grir)))));
        Assert.Equal(["CONFIG.ROLE_ACCOUNT_RULE_VIOLATED"], Codes(await cmds.CreateAsync(t.TenantId, Write(t, "m3"), new(t.CompanyId, Rule("AR_CONTROL", plain)))));
        Assert.Equal(CommandStatus.Ok, (await cmds.CreateAsync(t.TenantId, Write(t, "m4"), new(t.CompanyId, Rule("GR_IR", grir)))).Status);
        Assert.Equal(CommandStatus.Ok, (await cmds.CreateAsync(t.TenantId, Write(t, "m5"), new(t.CompanyId, Rule("AR_CONTROL", control)))).Status);

        // dropship clearing falls back to the rules of GR_IR until it has its own
        var viaGrir = await ResolveAsync(t, "DROPSHIP_CLEARING", null, "2026-03-01");
        Assert.Equal(grir, viaGrir!.AccountId);
        Guid dropship = await AccountAsync(t, "2430", "GL_OPEN_ITEM");
        Assert.Equal(CommandStatus.Ok, (await cmds.CreateAsync(t.TenantId, Write(t, "m6"), new(t.CompanyId, Rule("DROPSHIP_CLEARING", dropship)))).Status);
        Assert.Equal(dropship, (await ResolveAsync(t, "DROPSHIP_CLEARING", null, "2026-03-01"))!.AccountId);
    }

    [Fact]
    public async Task ReferencesRepeatsPermissionAndTheListAreChecked()
    {
        var t = await TestTenant.CreateReadyAsync();
        var cmds = new AccountDeterminationCommands(t.App);
        Guid plain = await AccountAsync(t, "1440", "NONE");
        var fields = D(("role_code", "COGS"), ("valid_from", "2026-01-01"), ("priority", 1), ("account_id", plain));

        var first = await cmds.CreateAsync(t.TenantId, Write(t, "r1"), new(t.CompanyId, fields));
        var replay = await cmds.CreateAsync(t.TenantId, Write(t, "r1"), new(t.CompanyId, fields));
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(first.Value!.DeterminationId, replay.Value!.DeterminationId);
        Assert.Equal(["IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST"], Codes(await cmds.CreateAsync(t.TenantId, Write(t, "r1"), new(t.CompanyId, D(("role_code", "COGS"), ("valid_from", "2026-02-01"), ("priority", 1), ("account_id", plain))))));

        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await cmds.CreateAsync(t.TenantId, t.Context("r2", "read.config"), new(t.CompanyId, fields))));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await cmds.CreateAsync(t.TenantId, Write(t, "r3"), new(Guid.NewGuid(), fields))));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await cmds.CreateAsync(t.TenantId, Write(t, "r4"), new(t.CompanyId, D(("role_code", "COGS"), ("valid_from", "2026-01-01"), ("priority", 2), ("account_id", Guid.NewGuid()))))));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await cmds.CreateAsync(t.TenantId, Write(t, "r5"), new(t.CompanyId, D(("role_code", "COGS"), ("valid_from", "2026-01-01"), ("priority", 2), ("account_id", plain), ("warehouse_id", Guid.NewGuid()))))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await cmds.CreateAsync(t.TenantId, Write(t, "r6"), new(t.CompanyId, D(("role_code", "NOPE"), ("valid_from", "2026-01-01"), ("priority", 2), ("account_id", plain))))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await cmds.CreateAsync(t.TenantId, Write(t, "r7"), new(t.CompanyId, D(("role_code", "COGS"), ("valid_from", "2026-03-01"), ("valid_to", "2026-02-01"), ("priority", 2), ("account_id", plain))))));
        Assert.Equal(["SCHEMA.REQUIRED_FIELD_MISSING"], Codes(await cmds.CreateAsync(t.TenantId, Write(t, "r8"), new(t.CompanyId, D(("role_code", "COGS"))))).Distinct().ToArray());

        var list = await new MasterReads(t.App).ListAsync(t.TenantId, t.Context("l", "read.config"), MasterReads.InventorySpecs.Single(s => s.Resource == "account-determinations"),
            new(new Dictionary<string, string> { ["role_code"] = "COGS" }));
        Assert.True(list.Status == CommandStatus.Ok, Describe(list));
        Assert.Equal("2026-01-01", Assert.Single(list.Value!.Items).GetProperty("valid_from").GetString());
    }
}
