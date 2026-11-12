using System.Text.Json;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;
using FinanceCore.Core.Migration;

namespace FinanceCore.Data.Tests;

/// <summary>The migration batch (13): opening balances and open items of a DRAFT company, from the open to the seal.</summary>
[Collection("db")]
public class MigrationTests
{
    private static string Describe<T>(CommandResult<T> r) => RevaluationTests.Describe(r);
    private static string[] Codes<T>(CommandResult<T> r) => RevaluationTests.Codes(r);
    private static CommandContext Manage(TestTenant t, string key) => t.Context(key, "migration.manage");
    private static CommandContext Stage(TestTenant t, string key) => t.Context(key, "migration.stage");
    private static CommandContext Commit(TestTenant t, string key) => t.Context(key, "migration.commit");
    private static JsonElement J(object o) => JsonSerializer.SerializeToElement(o);

    private sealed record Setup(TestTenant T, MigrationCommands Cmds);

    /// <summary>A DRAFT DKK company (group and reporting currency EUR) with its accounts, the two migration roles, the series of the migration documents, the year 2026 and a partner.</summary>
    private static async Task<Setup> SetupAsync(bool series = true, bool roles = true)
    {
        var t = await TestTenant.CreateAsync(bare: true);
        await t.ExecuteAsync("""
            insert into core.gl_account (tenant_id, company_id, account_id, account_no, name, account_type, tax_code_rule, open_item_mode) values
              (@t, @c, gen_random_uuid(), '1300', 'Receivables', 'ASSET', 'FORBIDDEN', 'CONTROL'),
              (@t, @c, gen_random_uuid(), '1900', 'Bank clearing', 'ASSET', 'FORBIDDEN', 'GL_OPEN_ITEM'),
              (@t, @c, gen_random_uuid(), '1800', 'Migration clearing', 'ASSET', 'FORBIDDEN', 'NONE'),
              (@t, @c, gen_random_uuid(), '1810', 'History result', 'EQUITY', 'FORBIDDEN', 'NONE'),
              (@t, @c, gen_random_uuid(), '2000', 'Share capital', 'EQUITY', 'FORBIDDEN', 'NONE');
            insert into core.account_role_assignment (tenant_id, company_id, role_code, account_id, effective_from)
              select @t, @c, v.role, g.account_id, timestamptz '2026-01-01 00:00:00+00' from (values ('MIGRATION_CLEARING', '1800'), ('HISTORY_RESULT', '1810')) v(role, no)
              join core.gl_account g on g.tenant_id = @t and g.company_id = @c and g.account_no = v.no where @roles;
            """, ("roles", roles));
        if (series)
            await t.ExecuteAsync("""
                insert into core.number_series (tenant_id, company_id, series_id, series_code, prefix, include_fiscal_year, separator, number_length, is_legal)
                  values (@t, @c, gen_random_uuid(), 'MIG', 'MG', true, '-', 6, false);
                insert into core.number_series_assignment (tenant_id, company_id, document_type, series_id, effective_from, created_by)
                  select @t, @c, ty, (select series_id from core.number_series where tenant_id = @t and series_code = 'MIG'), date '2026-01-01', 'test'
                  from unnest(array['OPENING', 'OPENING_ITEMS']) ty;
                """);
        var year = await new FiscalYearOpener(t.App).OpenAsync(t.TenantId, t.Context("year", "period.admin"), new(t.CompanyId, 2026));
        Assert.True(year.Status == CommandStatus.Ok, Describe(year));
        var partner = await new PartnerCommands(t.App).CreateAsync(t.TenantId, t.Context("partner", "write.master"), new CreatePartnerRequest(
            new Dictionary<string, object?> { ["partner_code"] = "ACME", ["partner_kind"] = "ORGANISATION", ["is_customer"] = true },
            new Dictionary<string, object?> { ["legal_name"] = "Acme A/S", ["country_code"] = "DK" },
            [new PartnerRelationInput(t.CompanyId, new Dictionary<string, object?> { ["is_customer"] = true })]));
        Assert.True(partner.Status == CommandStatus.Ok, Describe(partner));
        return new Setup(t, new MigrationCommands(t.App));
    }

    private static OpenBatchRequest OpenReq(TestTenant t, string date = "2026-04-01") => new(t.CompanyId, DateOnly.Parse(date), "LEGACY");

    private static StageRecordInput Bal(string account, decimal lc, decimal gc, string? @ref = null) =>
        new(RecordTypes.OpeningBalance, @ref ?? "BAL-" + account, J(new { account_no = account, balance_lc = lc, balance_gc = gc, balance_rc = gc }));

    private static StageRecordInput Item(string @ref, string account, string? partner, string ccy, decimal tc, decimal lc, decimal gc, string due = "2026-04-15") =>
        new(partner is null ? RecordTypes.GlOpenItem : RecordTypes.OpenItem, @ref, J(new
        {
            account_no = account, partner_code = partner, document_date = "2026-03-01", posting_date = "2026-03-02", due_date = due, currency = ccy,
            amount_tc = tc, amount_lc = lc, amount_gc = gc, amount_rc = gc, legacy_document_ref = @ref
        }));

    private static ControlTotalInput Tb(string account, decimal lc, decimal gc) => new(ControlKinds.TbByAccount, account, null, lc, gc, gc);
    private static ControlTotalInput Items(string account, decimal lc, decimal gc) => new(ControlKinds.OpenItemsByAccount, account, null, lc, gc, gc);

    /// <summary>A consistent opening: bank 8000, receivables 1900 (DKK 1200 and USD 100 at 7.00), a GL open item of 100 and share capital of 10000.</summary>
    private static StageRecordInput[] GoodRecords() =>
    [
        Bal("1000", 8000m, 1072m), Bal("2000", -10000m, -1340.2m),
        Item("INV-1", "1300", "ACME", "DKK", 1200m, 1200m, 160.8m), Item("INV-2", "1300", "ACME", "USD", 100m, 700m, 94m), Item("GL-1", "1900", null, "DKK", 100m, 100m, 13.4m)
    ];

    private static ControlTotalInput[] GoodTotals() =>
        [Tb("1000", 8000m, 1072m), Tb("2000", -10000m, -1340.2m), Tb("1300", 1900m, 254.8m), Tb("1900", 100m, 13.4m), Items("1300", 1900m, 254.8m), Items("1900", 100m, 13.4m)];

    private static async Task<Guid> ReadyBatchAsync(Setup s, StageRecordInput[]? records = null, ControlTotalInput[]? totals = null)
    {
        var t = s.T;
        var open = await s.Cmds.OpenAsync(t.TenantId, Manage(t, "open"), OpenReq(t));
        Assert.True(open.Status == CommandStatus.Ok, Describe(open));
        Guid batch = open.Value!.BatchId;
        Assert.Equal(CommandStatus.Ok, (await s.Cmds.DeclareTotalsAsync(t.TenantId, Manage(t, "totals"), new(batch, totals ?? GoodTotals()))).Status);
        var staged = await s.Cmds.StageAsync(t.TenantId, Stage(t, "stage"), new(batch, records ?? GoodRecords()));
        Assert.True(staged.Status == CommandStatus.Ok, Describe(staged));
        Assert.True(staged.Value!.Records!.All(r => r.Status == "VALID"), string.Join("; ", staged.Value.Records!.SelectMany(r => r.Errors.Select(e => r.LegacyRef + " " + e.Code + " " + e.Path))));
        Assert.Equal(CommandStatus.Ok, (await s.Cmds.ValidateAsync(t.TenantId, Manage(t, "validate"), new(batch))).Status);
        var rec = await s.Cmds.ReconcileAsync(t.TenantId, Manage(t, "reconcile"), new(batch));
        Assert.True(rec.Status == CommandStatus.Ok && rec.Value!.Status == "RECONCILED", Describe(rec) + " " + string.Join("; ", rec.Value?.Reconciliation?.Where(r => r.Result == "FAIL").Select(r => r.CheckCode + " " + r.Key) ?? []));
        return batch;
    }

    private static async Task AssertLedgerOk(TestTenant t)
    {
        var v = await new ChainVerification(t.App).VerifyAsync(t.TenantId, t.Context("verify", "chain.verify"), new(t.CompanyId, "LEDGER"));
        Assert.True(v.Status == CommandStatus.Ok && v.Value!.Ok, Describe(v) + " " + string.Join("; ", v.Value?.Breaks.Select(b => $"{b.Code} {b.ChainSeq}") ?? []));
    }

    [Fact]
    public async Task ABatchGoesFromOpenToSealedAndLeavesTheLedgerAtTheDeclaredTrialBalance()
    {
        var s = await SetupAsync();
        var t = s.T;
        Guid batch = await ReadyBatchAsync(s);

        var committed = await s.Cmds.CommitAsync(t.TenantId, Commit(t, "commit"), new(batch));
        Assert.True(committed.Status == CommandStatus.Ok, Describe(committed));
        Assert.Equal(("COMMITTED", 3), (committed.Value!.Status, committed.Value.Documents));   // the opening, the DKK items and the USD items
        Assert.Equal([("OPENING_GL", "DONE"), ("OPEN_ITEMS", "DONE")], committed.Value.Chunks!.Select(c => (c.Area, c.Status)).ToArray());

        Assert.Equal(3L, await t.ScalarAsync<long>("select count(*) from core.journal_document where tenant_id = @t and origin = 'MIGRATION' and migration_batch_id = @b", ("b", batch)));
        Assert.Equal(new[] { "OPENING", "OPENING_ITEMS", "OPENING_ITEMS" }, await Types(t));
        Assert.Equal("USD", await t.ScalarAsync<string>("select transaction_currency from core.journal_document where tenant_id = @t and document_type = 'OPENING_ITEMS' and transaction_currency <> 'DKK'"));
        Assert.Equal(3L, await t.ScalarAsync<long>("select count(*) from core.journal_line_open_item where tenant_id = @t"));
        Assert.Equal("ACME", await t.ScalarAsync<string>("select p.partner_code from core.journal_line_open_item o join core.business_partner p on p.tenant_id = o.tenant_id and p.partner_id = o.partner_id where o.tenant_id = @t and o.legacy_document_ref = 'INV-1'"));
        Assert.Equal(0m, await Balance(t, "1800"));                   // MIGRATION_CLEARING nets to zero
        Assert.Equal(8000m, await Balance(t, "1000"));
        Assert.Equal(1900m, await Balance(t, "1300"));
        Assert.Equal(-10000m, await Balance(t, "2000"));
        Assert.Equal("DRAFT", await t.ScalarAsync<string>("select status from core.company where tenant_id = @t and company_id = @c", ("c", t.CompanyId)));
        Assert.Equal(0L, await t.ScalarAsync<long>("select count(*) from core.outbox_event where tenant_id = @t and event_type like 'ledger.%'"));   // no event per record or document
        await AssertLedgerOk(t);

        var sealedBatch = await s.Cmds.SealAsync(t.TenantId, Commit(t, "seal"), new(batch));
        Assert.True(sealedBatch.Status == CommandStatus.Ok, Describe(sealedBatch));
        Assert.Equal("SEALED", sealedBatch.Value!.Status);
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.outbox_event where tenant_id = @t and event_type = 'migration.sealed'"));
        Assert.Equal(5L, await t.ScalarAsync<long>("select count(*) from core.outbox_event where tenant_id = @t and event_type = 'migration.batch_status_changed'"));   // open, validated, reconciled, committing, committed
        Assert.True(await t.ScalarAsync<long>("select count(*) from core.chain_anchor where tenant_id = @t and anchor_state = 'PENDING'") >= 1);
        Assert.Equal(["MIGRATION.BATCH_STATUS_INVALID"], Codes(await s.Cmds.SealAsync(t.TenantId, Commit(t, "seal2"), new(batch))));
        Assert.Equal(["MIGRATION.ABORT_NOT_ALLOWED"], Codes(await s.Cmds.AbortAsync(t.TenantId, Manage(t, "abort"), new(batch))));
        // the replay of the seal
        Assert.True((await s.Cmds.SealAsync(t.TenantId, Commit(t, "seal"), new(batch))).IdempotentReplay);
    }

    private static async Task<string[]> Types(TestTenant t)
    {
        await using var admin = await t.OpenAdminAsync();
        await using var cmd = new Npgsql.NpgsqlCommand("select document_type from core.journal_document where tenant_id = @t order by chain_seq", admin);
        cmd.Parameters.AddWithValue("t", t.TenantId);
        var list = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(r.GetString(0));
        return list.ToArray();
    }

    private static Task<decimal> Balance(TestTenant t, string account) => t.ScalarAsync<decimal>(
        "select coalesce(sum(l.amount_lc), 0) from core.journal_line l join core.gl_account a on a.tenant_id = l.tenant_id and a.account_id = l.account_id where l.tenant_id = @t and a.account_no = @n", ("n", account));

    [Fact]
    public async Task RecordsAreValidatedOnReceiptAndAFixSupersedesTheBadRecord()
    {
        var s = await SetupAsync();
        var t = s.T;
        var batch = (await s.Cmds.OpenAsync(t.TenantId, Manage(t, "open"), OpenReq(t))).Value!.BatchId;
        var bad = new[]
        {
            Bal("8888", 1m, 1m),                                            // no such account
            Bal("1300", 1m, 1m),                                            // a subledger account takes no balance
            Item("INV-1", "1300", "NOBODY", "DKK", 10m, 10m, 1.3m),         // no such partner
            Item("INV-2", "1300", "ACME", "XXX", 10m, 10m, 1.3m),           // currency not enabled
            Item("INV-3", "1900", "ACME", "DKK", 10m, 10m, 1.3m),           // a GL open item has no partner (shape)
            Item("INV-4", "1300", "ACME", "DKK", 10m, -10m, 1.3m),          // the signs disagree
            new(RecordTypes.OpeningBalance, "BAL-X", J(new { account_no = "1000", surprise = 1 }))   // an unknown field
        };
        var staged = await s.Cmds.StageAsync(t.TenantId, Stage(t, "stage"), new(batch, bad));
        Assert.True(staged.Status == CommandStatus.Ok, Describe(staged));
        Assert.All(staged.Value!.Records!, r => Assert.Equal("INVALID", r.Status));
        Assert.Equal(7, staged.Value.InvalidCount);
        Assert.Contains("ACCOUNT.NOT_FOUND", staged.Value.Records![0].Errors.Select(e => e.Code));
        Assert.Contains("MASTER.PARTNER_NOT_FOUND", staged.Value.Records![2].Errors.Select(e => e.Code));
        Assert.Contains("CONFIG.CURRENCY_NOT_ENABLED", staged.Value.Records![3].Errors.Select(e => e.Code));
        Assert.Equal(["MIGRATION.RECORDS_INVALID"], Codes(await s.Cmds.ValidateAsync(t.TenantId, Manage(t, "validate"), new(batch))));

        // a record with the same type and legacy_ref supersedes the earlier one
        var fixes = bad.Select(b => b.RecordType == RecordTypes.OpeningBalance ? Bal("1000", 1m, 1m, b.LegacyRef) : Item(b.LegacyRef, "1300", "ACME", "DKK", 10m, 10m, 1.3m)).ToArray();
        var fixedRecords = await s.Cmds.StageAsync(t.TenantId, Stage(t, "fix"), new(batch, fixes));
        Assert.Equal(0, fixedRecords.Value!.InvalidCount);
        Assert.Equal(CommandStatus.Ok, (await s.Cmds.ValidateAsync(t.TenantId, Manage(t, "validate2"), new(batch))).Status);

        Assert.Equal(["MIGRATION.RECORD_TYPE_INVALID"], Codes(await s.Cmds.StageAsync(t.TenantId, Stage(t, "t"), new(batch, [new("STOCK_OPENING_LINE", "x", J(new { }))]))));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await s.Cmds.StageAsync(t.TenantId, Manage(t, "p"), new(batch, GoodRecords()))));
        Assert.Equal(["MIGRATION.CONTROL_KIND_INVALID"], Codes(await s.Cmds.DeclareTotalsAsync(t.TenantId, Manage(t, "k"), new(batch, [new("STOCK_QTY_BY_ITEM", "x", null, 1m, 1m, 1m)]))));
    }

    [Fact]
    public async Task AReconciliationThatFailsBlocksTheCommitAndAChangedRecordNeedsANewOne()
    {
        var s = await SetupAsync();
        var t = s.T;
        var batch = (await s.Cmds.OpenAsync(t.TenantId, Manage(t, "open"), OpenReq(t))).Value!.BatchId;
        // the open items of 1300 are 1 DKK short of the declared balance, and the trial balance does not net to zero
        var totals = GoodTotals().Select(x => x.Key1 == "1300" && x.ControlKind == ControlKinds.TbByAccount ? x with { AmountLc = 1901m } : x).ToArray();
        await s.Cmds.DeclareTotalsAsync(t.TenantId, Manage(t, "totals"), new(batch, totals));
        await s.Cmds.StageAsync(t.TenantId, Stage(t, "stage"), new(batch, GoodRecords()));
        await s.Cmds.ValidateAsync(t.TenantId, Manage(t, "validate"), new(batch));

        var failed = await s.Cmds.ReconcileAsync(t.TenantId, Manage(t, "r1"), new(batch));
        Assert.Equal("VALIDATED", failed.Value!.Status);
        var failing = failed.Value.Reconciliation!.Where(r => r.Result == "FAIL").Select(r => r.CheckCode).ToArray();
        Assert.Contains("TB_BALANCES", failing);
        Assert.Contains("OPEN_ITEMS_VS_TB", failing);
        Assert.Contains("ZERO_TOLERANCE", failing);
        Assert.Equal(["MIGRATION.RECONCILIATION_MISSING"], Codes(await s.Cmds.CommitAsync(t.TenantId, Commit(t, "c1"), new(batch))));

        // the corrected total passes, and a record staged afterwards takes the batch back to OPEN
        await s.Cmds.DeclareTotalsAsync(t.TenantId, Manage(t, "totals2"), new(batch, [Tb("1300", 1900m, 254.8m)]));
        Assert.Equal("RECONCILED", (await s.Cmds.ReconcileAsync(t.TenantId, Manage(t, "r2"), new(batch))).Value!.Status);
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.migration_batch where tenant_id = @t and status = 'RECONCILED' and accepted_reconciliation_id is not null"));
        var restaged = await s.Cmds.StageAsync(t.TenantId, Stage(t, "stage2"), new(batch, [Bal("1000", 8000m, 1072m)]));
        Assert.Equal("OPEN", restaged.Value!.Status);
        Assert.Equal(["MIGRATION.RECONCILIATION_MISSING"], Codes(await s.Cmds.CommitAsync(t.TenantId, Commit(t, "c2"), new(batch))));
    }

    [Fact]
    public async Task AChunkThatFailsIsResumedAfterTheFix()
    {
        var s = await SetupAsync(series: false);
        var t = s.T;
        Guid batch = await ReadyBatchAsync(s);

        var failed = await s.Cmds.CommitAsync(t.TenantId, Commit(t, "c1"), new(batch));
        Assert.Equal(["MIGRATION.CHUNK_FAILED"], Codes(failed));
        Assert.Equal("SERIES.NO_ASSIGNMENT", failed.Errors[0].Params!["failure_codes"]);
        Assert.Equal("COMMITTING", await t.ScalarAsync<string>("select status from core.migration_batch where tenant_id = @t and batch_id = @b", ("b", batch)));
        Assert.Equal("FAILED", await t.ScalarAsync<string>("select status from core.migration_commit_chunk where tenant_id = @t and batch_id = @b and chunk_no = 1", ("b", batch)));
        Assert.Equal(0L, await t.ScalarAsync<long>("select count(*) from core.journal_document where tenant_id = @t"));   // nothing was posted: a chunk is atomic

        await t.ExecuteAsync("""
            insert into core.number_series (tenant_id, company_id, series_id, series_code, prefix, include_fiscal_year, separator, number_length, is_legal)
              values (@t, @c, gen_random_uuid(), 'MIG', 'MG', true, '-', 6, false);
            insert into core.number_series_assignment (tenant_id, company_id, document_type, series_id, effective_from, created_by)
              select @t, @c, ty, (select series_id from core.number_series where tenant_id = @t and series_code = 'MIG'), date '2026-01-01', 'test' from unnest(array['OPENING', 'OPENING_ITEMS']) ty;
            insert into core.number_range_counter (tenant_id, company_id, series_id, fiscal_year, last_number)
              select @t, @c, series_id, 2026, 0 from core.number_series where tenant_id = @t and series_code = 'MIG';
            """);
        var resumed = await s.Cmds.CommitAsync(t.TenantId, Commit(t, "c2"), new(batch));
        Assert.True(resumed.Status == CommandStatus.Ok, Describe(resumed));
        Assert.Equal(("COMMITTED", 3), (resumed.Value!.Status, resumed.Value.Documents));
        Assert.Equal(CommandStatus.Ok, (await s.Cmds.SealAsync(t.TenantId, Commit(t, "seal"), new(batch))).Status);
        await AssertLedgerOk(t);
    }

    [Fact]
    public async Task OpeningABatchChecksTheCompanyTheCutoverTheRolesAndTheOtherBatches()
    {
        var s = await SetupAsync();
        var t = s.T;
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await s.Cmds.OpenAsync(t.TenantId, t.Context("a"), OpenReq(t))));
        Assert.Equal(["MIGRATION.CUTOVER_NOT_PERIOD_START"], Codes(await s.Cmds.OpenAsync(t.TenantId, Manage(t, "b"), OpenReq(t, "2026-04-15"))));
        Assert.Equal(["MIGRATION.CUTOVER_YEAR_NOT_OPEN"], Codes(await s.Cmds.OpenAsync(t.TenantId, Manage(t, "c"), OpenReq(t, "2030-01-01"))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await s.Cmds.OpenAsync(t.TenantId, Manage(t, "d"), new(t.CompanyId, new DateOnly(2026, 4, 1), " "))));

        var open = await s.Cmds.OpenAsync(t.TenantId, Manage(t, "open"), OpenReq(t));
        Assert.Equal(("OPEN", 0), (open.Value!.Status, open.Value.RecordCount));
        Assert.True((await s.Cmds.OpenAsync(t.TenantId, Manage(t, "open"), OpenReq(t))).IdempotentReplay);
        Assert.Equal(["MIGRATION.BATCH_EXISTS"], Codes(await s.Cmds.OpenAsync(t.TenantId, Manage(t, "open2"), OpenReq(t))));

        // an aborted batch frees the company, and nothing but the S columns of a batch ever changes
        Assert.Equal("ABORTED", (await s.Cmds.AbortAsync(t.TenantId, Manage(t, "abort"), new(open.Value.BatchId))).Value!.Status);
        Assert.Equal(CommandStatus.Ok, (await s.Cmds.OpenAsync(t.TenantId, Manage(t, "open3"), OpenReq(t))).Status);
        await Assert.ThrowsAnyAsync<Exception>(() => t.ExecuteAsync("update core.migration_batch set cutover_date = date '2026-05-01' where tenant_id = @t"));

        var ready = await TestTenant.CreateReadyAsync();
        Assert.Equal(["MIGRATION.COMPANY_NOT_DRAFT"], Codes(await new MigrationCommands(ready.App).OpenAsync(ready.TenantId, Manage(ready, "o"), OpenReq(ready))));

        var noRole = await SetupAsync(roles: false);
        Assert.Equal(["CONFIG.ROLE_NOT_ASSIGNED"], Codes(await noRole.Cmds.OpenAsync(noRole.T.TenantId, Manage(noRole.T, "o"), OpenReq(noRole.T))));
    }

    [Fact]
    public async Task AProductionTenantNeedsTheFourEyesThatAreNotBuilt()
    {
        var s = await SetupAsync();
        var t = s.T;
        Guid batch = await ReadyBatchAsync(s);
        await t.ExecuteAsync("update platform.tenant set purpose = 'PRODUCTION' where tenant_id = @t");
        Assert.Equal(["LIMIT.MIGRATION_FOUR_EYES_NOT_BUILT"], Codes(await s.Cmds.CommitAsync(t.TenantId, Commit(t, "c"), new(batch))));
        Assert.Equal("RECONCILED", await t.ScalarAsync<string>("select status from core.migration_batch where tenant_id = @t and batch_id = @b", ("b", batch)));
    }
}
