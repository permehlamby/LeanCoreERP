using FinanceCore.Core.Commands;
using FinanceCore.Core.Hashing;
using FinanceCore.Core.Posting;
using Npgsql;

namespace FinanceCore.Data.Tests;

/// <summary>All database tests share one server, so they run one after the other.</summary>
[CollectionDefinition("db", DisableParallelization = true)]
public class DbCollection;

/// <summary>
/// A fresh tenant for one test: a DKK company (group and reporting currency EUR) with a chart of accounts, a general
/// journal series, the fiscal year 2026 with twelve periods, the LEDGER chain head at its genesis value and a few rates.
/// It is created as the migration role, because the commands that would create it (fiscal-years:open and others)
/// are not written yet. Needs the schema and the reference data of db/seed/dev_seed.sql.
/// </summary>
public sealed class TestTenant
{
    public Guid TenantId { get; } = Guid.NewGuid();
    public Guid CompanyId { get; } = Guid.NewGuid();
    public Guid LedgerId { get; } = Guid.NewGuid();
    public Guid SeriesId { get; } = Guid.NewGuid();
    public Guid Bank { get; } = Guid.NewGuid();
    public Guid Revenue { get; } = Guid.NewGuid();
    public Guid Expense { get; } = Guid.NewGuid();
    public Guid Blocked { get; } = Guid.NewGuid();

    public DbSettings Settings { get; } = DbSettings.FromEnvironment();
    public NpgsqlDataSource App { get; private set; } = null!;

    public static async Task<TestTenant> CreateAsync(bool dkkToEurRate = true)
    {
        var t = new TestTenant();
        t.App = NpgsqlDataSource.Create(t.Settings.AppConnection);

        Guid calendar = Guid.NewGuid(), group = Guid.NewGuid();
        byte[] genesis = LedgerHash2.Genesis(t.TenantId, t.CompanyId, "LEDGER", t.LedgerId.ToString("D"));

        await using NpgsqlConnection admin = await t.OpenAdminAsync();
        await using var cmd = new NpgsqlCommand("""
            insert into platform.tenant (tenant_id, tenant_code, name, status, purpose)
            values (@t, 'T' || substr(@t::text, 1, 8), 'Test tenant', 'ACTIVE', 'TEST');
            insert into core.tenant_setting (tenant_id) values (@t);
            insert into core.outbox_head (tenant_id) values (@t);
            insert into core.tenant_currency (tenant_id, currency_code) values (@t, 'DKK'), (@t, 'EUR'), (@t, 'USD');
            insert into core.fiscal_calendar (tenant_id, calendar_id, calendar_code, first_month, special_period_count, year_label_rule)
              values (@t, @cal, 'CAL', 1, 0, 'START');
            insert into core.company_group (tenant_id, group_id, group_code, name, group_currency) values (@t, @grp, 'GRP', 'Group', 'EUR');
            insert into core.company (tenant_id, company_id, company_code, legal_name, country_code, local_currency, reporting_currency,
                                      fiscal_calendar_id, status)
              values (@t, @c, 'C1', 'Test A/S', 'DK', 'DKK', 'EUR', @cal, 'ACTIVE');
            insert into core.ledger (tenant_id, company_id, ledger_id, ledger_code) values (@t, @c, @l, 'L1');
            insert into core.group_membership (tenant_id, company_id, valid_from, group_id) values (@t, @c, date '2026-01-01', @grp);
            insert into core.gl_account (tenant_id, company_id, account_id, account_no, name, account_type, tax_code_rule, posting_blocked) values
              (@t, @c, @a1, '1000', 'Bank', 'ASSET', 'FORBIDDEN', false),
              (@t, @c, @a2, '3000', 'Revenue', 'REVENUE', 'FORBIDDEN', false),
              (@t, @c, @a3, '4000', 'Expenses', 'EXPENSE', 'FORBIDDEN', false),
              (@t, @c, @a4, '9999', 'Blocked', 'EXPENSE', 'FORBIDDEN', true);
            insert into core.number_series (tenant_id, company_id, series_id, series_code, prefix, include_fiscal_year, separator, number_length, is_legal)
              values (@t, @c, @s, 'GL-JRN', 'GL', true, '-', 6, true);
            insert into core.number_series_assignment (tenant_id, company_id, document_type, series_id, effective_from, created_by)
              values (@t, @c, 'JOURNAL', @s, date '2026-01-01', 'test');
            insert into core.fiscal_year (tenant_id, company_id, fiscal_year, calendar_id, start_date, end_date, opened_by)
              values (@t, @c, 2026, @cal, date '2026-01-01', date '2026-12-31', 'test');
            insert into core.fiscal_period (tenant_id, company_id, fiscal_year, period_no, period_type, start_date, end_date)
              select @t, @c, 2026, m, 'REGULAR', make_date(2026, m, 1), (make_date(2026, m, 1) + interval '1 month - 1 day')::date
              from generate_series(1, 12) m;
            insert into core.number_range_counter (tenant_id, company_id, series_id, fiscal_year, last_number)
              values (@t, @c, @s, 2026, 0);
            insert into core.chain_head (tenant_id, company_id, chain_type, scope_key, last_seq, last_hash, hash_version)
              values (@t, @c, 'LEDGER', @l::text, 0, @genesis, 2);
            insert into core.exchange_rate (tenant_id, rate_type, from_currency, to_currency, valid_from, rate) values
              (@t, 'SPOT', 'USD', 'DKK', date '2026-01-01', 7.0000000000),
              (@t, 'SPOT', 'USD', 'EUR', date '2026-01-01', 0.9000000000);
            insert into core.exchange_rate (tenant_id, rate_type, from_currency, to_currency, valid_from, rate)
              select @t, 'SPOT', 'DKK', 'EUR', d::date, 0.1340000000
              from generate_series(timestamp '2026-01-01', timestamp '2026-12-31', interval '1 day') d where @dkkEur;
            """, admin);
        cmd.Parameters.AddWithValue("dkkEur", dkkToEurRate);
        cmd.Parameters.AddWithValue("t", t.TenantId);
        cmd.Parameters.AddWithValue("c", t.CompanyId);
        cmd.Parameters.AddWithValue("l", t.LedgerId);
        cmd.Parameters.AddWithValue("s", t.SeriesId);
        cmd.Parameters.AddWithValue("cal", calendar);
        cmd.Parameters.AddWithValue("grp", group);
        cmd.Parameters.AddWithValue("a1", t.Bank);
        cmd.Parameters.AddWithValue("a2", t.Revenue);
        cmd.Parameters.AddWithValue("a3", t.Expense);
        cmd.Parameters.AddWithValue("a4", t.Blocked);
        cmd.Parameters.AddWithValue("genesis", genesis);
        await cmd.ExecuteNonQueryAsync();
        return t;
    }

    /// <summary>A connection as the migration role with the tenant set, for set-up and for tampering.</summary>
    public async Task<NpgsqlConnection> OpenAdminAsync()
    {
        var connection = new NpgsqlConnection(Settings.AdminConnection);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand("select set_config('app.tenant_id', @t, false)", connection);
        cmd.Parameters.AddWithValue("t", TenantId.ToString("D"));
        await cmd.ExecuteNonQueryAsync();
        return connection;
    }

    public async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using NpgsqlConnection admin = await OpenAdminAsync();
        await using var cmd = new NpgsqlCommand(sql, admin);
        cmd.Parameters.AddWithValue("t", TenantId);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        return (T)Convert.ChangeType((await cmd.ExecuteScalarAsync())!, typeof(T));
    }

    public async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using NpgsqlConnection admin = await OpenAdminAsync();
        await using var cmd = new NpgsqlCommand(sql, admin);
        cmd.Parameters.AddWithValue("t", TenantId);
        cmd.Parameters.AddWithValue("c", CompanyId);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Changes a period status the way the period command will: with the matching, latest period_event.</summary>
    public async Task SetPeriodStatusAsync(int period, string status)
    {
        Guid ev = Guid.NewGuid();
        await ExecuteAsync("""
            insert into core.period_event (tenant_id, event_id, company_id, event_seq, fiscal_year, period_no, event_type, to_status,
                                           event_at, event_by, correlation_id, idempotency_key, prev_hash, row_hash, hash_version)
            values (@t, @ev, @c, (select coalesce(max(event_seq), 0) + 1 from core.period_event where tenant_id = @t and company_id = @c),
                    2026, @p, @type, @status, now(), 'test', 'test', 'ev-' || @ev::text, @h, @h, 2);
            update core.fiscal_period set status = @status, last_event_id = @ev
             where tenant_id = @t and company_id = @c and fiscal_year = 2026 and period_no = @p;
            """,
            ("ev", ev), ("p", period), ("status", status),
            ("type", status == "HARD_CLOSED" ? "PERIOD_HARD_CLOSE" : "PERIOD_SOFT_CLOSE"), ("h", new byte[32]));
    }

    public CommandContext Context(string key, params string[] permissions) =>
        new(key, "corr-" + key, "tester", new HashSet<string>(permissions.Length == 0 ? ["journal.post"] : permissions));

    public PostJournalDocumentRequest Journal(
        string postingDate = "2026-03-15", string currency = "DKK", decimal amount = 1000m, string? ledgerText = null) =>
        new(CompanyId, null, "JOURNAL", DateOnly.Parse(postingDate), DateOnly.Parse(postingDate), currency,
            "Test posting", null, null,
            [new RequestLine("1", Bank, amount, ledgerText), new RequestLine("2", Revenue, -amount)]);
}
