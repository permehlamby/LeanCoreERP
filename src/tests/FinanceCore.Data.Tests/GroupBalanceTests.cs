using FinanceCore.Core.Commands;
using FinanceCore.Core.Posting;

namespace FinanceCore.Data.Tests;

/// <summary>balances:query with group_account_id and ic_counterpart_company_id as keys and filters (23, 7.1; CT-05): the mapping at the reporting date, unmapped lines in a null row.</summary>
[Collection("db")]
public class GroupBalanceTests
{
    private static string Describe<T>(CommandResult<T> r) => r.Status + ": " + string.Join(", ", r.Errors.Select(e => $"{e.Code} {e.Path}"));
    private static CommandContext Read(TestTenant t) => t.Context("rd", "read.ledger");

    private static async Task<Guid> GroupAccount(TestTenant t, string code, string type)
    {
        Guid group = Guid.Parse(await t.ScalarAsync<string>("select group_id::text from core.group_membership where tenant_id = @t and company_id = @c", ("c", t.CompanyId)));
        Guid id = Guid.NewGuid();
        await t.ExecuteAsync("insert into core.group_chart_account (tenant_id, group_account_id, group_id, code, name, account_type, created_by, updated_by) values (@t, @id, @g, @code, @name, @type, 'test', 'test')",
            ("id", id), ("g", group), ("code", code), ("name", "Group " + code), ("type", type));
        return id;
    }

    private static Task Map(TestTenant t, Guid account, Guid groupAccount, string from, string? to = null) =>
        t.ExecuteAsync("insert into core.account_group_mapping (tenant_id, company_id, account_id, valid_from, valid_to, group_account_id, created_by) values (@t, @c, @a, @d::date, @e::date, @g, 'test')",
            ("c", t.CompanyId), ("a", account), ("g", groupAccount), ("d", from), ("e", (object?)to ?? DBNull.Value));

    [Fact]
    public async Task AnUnmappedAccountFormsTheNullRowAndTheRowCarriesTheGroupAccount()
    {
        var t = await TestTenant.CreateReadyAsync();
        var post = await new JournalDocumentPoster(t.App).PostAsync(t.TenantId, t.Context("p"), new PostJournalDocumentRequest(t.CompanyId, null, "JOURNAL", new DateOnly(2026, 3, 10), new DateOnly(2026, 3, 10),
            "DKK", "Test", null, null, [new RequestLine("1", t.Bank, 1000m, null), new RequestLine("2", t.Revenue, -1000m)]));
        Assert.True(post.Status == CommandStatus.Ok, Describe(post));
        Guid cash = await GroupAccount(t, "G100", "ASSET");
        await Map(t, t.Bank, cash, "2026-01-01");
        var reads = new LedgerReads(t.App);

        var byGroup = await reads.BalancesAsync(t.TenantId, Read(t), new(t.CompanyId, "CUMULATIVE", AsOfDate: new DateOnly(2026, 6, 30), GroupBy: ["group_account_id"]));
        Assert.True(byGroup.Status == CommandStatus.Ok, Describe(byGroup));
        Assert.Equal(2, byGroup.Value!.Rows.Count);
        var mapped = byGroup.Value.Rows.Single(r => r.Keys["group_account_id"] is not null);
        Assert.Equal(cash.ToString(), mapped.Keys["group_account_id"]);
        Assert.Equal("G100", mapped.Keys["group_account_code"]);
        Assert.Equal("Group G100", mapped.Keys["group_account_name"]);
        Assert.Equal("ASSET", mapped.Keys["group_account_type"]);
        Assert.Equal("CLOSING", mapped.Keys["translation_rate_basis"]);
        Assert.Equal(1000m, mapped.Debit.Lc);
        var unmapped = byGroup.Value.Rows.Single(r => r.Keys["group_account_id"] is null);
        Assert.Equal(-1000m, unmapped.Credit.Lc);
        Assert.Null(unmapped.Keys["group_account_code"]);

        // the filter, and the mapping resolved at the reporting date: before the mapping started the account is unmapped
        var filtered = await reads.BalancesAsync(t.TenantId, Read(t), new(t.CompanyId, "CUMULATIVE", AsOfDate: new DateOnly(2026, 6, 30), GroupAccountId: cash));
        Assert.Equal(1000m, filtered.Value!.TotalDebit.Lc);
        Assert.Equal(0m, filtered.Value.TotalCredit.Lc);
    }

    [Fact]
    public async Task TheMappingOfTheReportingDateCountsAndTheIntercompanyKeysWorkWithoutMarkedLines()
    {
        var t = await TestTenant.CreateReadyAsync();
        await new JournalDocumentPoster(t.App).PostAsync(t.TenantId, t.Context("p"), new PostJournalDocumentRequest(t.CompanyId, null, "JOURNAL", new DateOnly(2026, 3, 10), new DateOnly(2026, 3, 10),
            "DKK", "Test", null, null, [new RequestLine("1", t.Bank, 500m, null), new RequestLine("2", t.Revenue, -500m)]));
        Guid old = await GroupAccount(t, "G100", "ASSET"), renamed = await GroupAccount(t, "G200", "ASSET");
        await Map(t, t.Bank, old, "2026-01-01", "2026-07-01");
        await Map(t, t.Bank, renamed, "2026-07-01");
        var reads = new LedgerReads(t.App);

        async Task<string?> GroupAt(string date)
        {
            var r = await reads.BalancesAsync(t.TenantId, Read(t), new(t.CompanyId, "CUMULATIVE", AsOfDate: DateOnly.Parse(date), GroupBy: ["group_account_code"]));
            return r.Status == CommandStatus.Ok ? "ok" : Describe(r);
        }
        Assert.StartsWith("Rejected", await GroupAt("2026-06-30") ?? "");   // group_account_code is not a key of its own: only group_account_id

        var june = await reads.BalancesAsync(t.TenantId, Read(t), new(t.CompanyId, "CUMULATIVE", AsOfDate: new DateOnly(2026, 6, 30), GroupBy: ["group_account_id"]));
        var august = await reads.BalancesAsync(t.TenantId, Read(t), new(t.CompanyId, "CUMULATIVE", AsOfDate: new DateOnly(2026, 8, 31), GroupBy: ["group_account_id"]));
        Assert.Contains(june.Value!.Rows, r => r.Keys["group_account_code"] == "G100");
        Assert.Contains(august.Value!.Rows, r => r.Keys["group_account_code"] == "G200");
        Assert.DoesNotContain(august.Value.Rows, r => r.Keys["group_account_code"] == "G100");

        // no line carries the marker yet: one null row, and is_intercompany keeps nothing
        var ic = await reads.BalancesAsync(t.TenantId, Read(t), new(t.CompanyId, "CUMULATIVE", AsOfDate: new DateOnly(2026, 6, 30), GroupBy: ["ic_counterpart_company_id"]));
        var row = Assert.Single(ic.Value!.Rows);
        Assert.Null(row.Keys["ic_counterpart_company_id"]);
        Assert.Equal(0L, (await reads.BalancesAsync(t.TenantId, Read(t), new(t.CompanyId, "CUMULATIVE", AsOfDate: new DateOnly(2026, 6, 30), IsIntercompany: true))).Value!.Rows.Sum(r => r.LineCount));
        Assert.Equal(2L, (await reads.BalancesAsync(t.TenantId, Read(t), new(t.CompanyId, "CUMULATIVE", AsOfDate: new DateOnly(2026, 6, 30), IsIntercompany: false))).Value!.Rows.Sum(r => r.LineCount));
        Assert.Equal(0L, (await reads.BalancesAsync(t.TenantId, Read(t), new(t.CompanyId, "CUMULATIVE", AsOfDate: new DateOnly(2026, 6, 30), IcCounterpartCompanyId: Guid.NewGuid()))).Value!.Rows.Sum(r => r.LineCount));
    }
}
