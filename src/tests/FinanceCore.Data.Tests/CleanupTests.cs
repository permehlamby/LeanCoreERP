using FinanceCore.Core.Commands;
using FinanceCore.Core.Posting;
using Npgsql;

namespace FinanceCore.Data.Tests;

/// <summary>Proves db/tools/cleanup-test-tenants.sql on a tenant with every kind of row, and that it leaves other tenants alone.</summary>
[Collection("db")]
public class CleanupTests
{
    private static readonly string Script = File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "db-tools", "cleanup-test-tenants.sql"));

    private static readonly string[] Tables =
    [
        "outbox_event", "command_log_result", "command_log", "journal_line", "journal_document", "number_range_counter", "fiscal_period",
        "fiscal_year", "chain_head", "period_event", "posting_window", "number_series_assignment", "number_series", "gl_account",
        "exchange_rate", "company_setting_version", "group_membership", "ledger", "company", "company_group", "fiscal_calendar",
        "tenant_currency", "tenant_setting", "outbox_head"
    ];

    private static async Task<TestTenant> TenantWithEverything()
    {
        var t = await TestTenant.CreateReadyAsync();
        var poster = new JournalDocumentPoster(t.App);
        var doc = await poster.PostAsync(t.TenantId, t.Context("p1"), t.Journal());
        await poster.PostAsync(t.TenantId, t.Context("p2"), t.Journal(amount: 5m));
        Assert.Equal(CommandStatus.Ok, (await new DocumentReverser(t.App).ReverseAsync(
            t.TenantId, t.Context("r", "journal.reverse"), new(doc.Value!.DocumentId, null, "ERROR", null))).Status);
        await new PeriodStatusCommands(t.App).HardCloseAsync(
            t.TenantId, t.Context("h", "period.hard_close"), new Core.Periods.PeriodRef(t.CompanyId, 2026, 1));
        return t;
    }

    [Fact]
    public async Task RemovesTheNamedTenantCompletelyAndNothingElse()
    {
        var doomed = await TenantWithEverything();
        var kept = await TenantWithEverything();

        await using (var admin = await doomed.OpenAdminAsync())
        {
            await using (var set = new NpgsqlCommand("select set_config('cleanup.only_tenant', @t, false)", admin))
            {
                set.Parameters.AddWithValue("t", doomed.TenantId.ToString("D"));
                await set.ExecuteNonQueryAsync();
            }
            await using var run = new NpgsqlCommand(Script, admin);
            await run.ExecuteNonQueryAsync();
        }

        Assert.Equal(0L, await doomed.ScalarAsync<long>("select count(*) from platform.tenant where tenant_id = @t"));
        foreach (string table in Tables)
        {
            Assert.Equal(0L, await doomed.ScalarAsync<long>($"select count(*) from core.{table} where tenant_id = @t"));
            Assert.True(await kept.ScalarAsync<long>($"select count(*) from core.{table} where tenant_id = @t") > 0 || table is "posting_window",
                $"{table} of the other tenant is gone");
        }
        Assert.Equal(1L, await kept.ScalarAsync<long>("select count(*) from platform.tenant where tenant_id = @t"));

        // the guards are back on: the posting tables reject a change again
        await using var verify = await kept.OpenAdminAsync();
        await using var upd = new NpgsqlCommand("update core.journal_line set amount_lc = 1 where tenant_id = @t", verify);
        upd.Parameters.AddWithValue("t", kept.TenantId);
        await Assert.ThrowsAsync<PostgresException>(() => upd.ExecuteNonQueryAsync());
    }
}
