using FinanceCore.Core.Closing;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Periods;
using FinanceCore.Core.Posting;

namespace FinanceCore.Data.Tests;

/// <summary>revaluation:run, :preview, the rerun and the stale check at hard-close (05, 3). The basis is the balance of a revalued USD bank account in a DKK company.</summary>
[Collection("db")]
public class RevaluationTests
{
    internal static string Describe<T>(CommandResult<T> r) => r.Status + ": " + string.Join(", ", r.Errors.Select(e => $"{e.Code} {e.Path}"));
    internal static string[] Codes<T>(CommandResult<T> r) => r.Errors.Select(e => e.Code).ToArray();

    /// <summary>A ready company with the closing series, the bank account flagged for revaluation and closing rates for 2026-03-31.</summary>
    internal static async Task<TestTenant> ReadyAsync(int special = 0, bool closingRates = true, bool revalueBank = true)
    {
        var t = await TestTenant.CreateReadyAsync(specialPeriods: special);
        await t.ExecuteAsync("""
            insert into core.number_series (tenant_id, company_id, series_id, series_code, prefix, include_fiscal_year, separator, number_length, is_legal)
              select @t, @c, gen_random_uuid(), v.code, v.prefix, true, '-', 6, true from (values ('GL-RVL', 'RV'), ('GL-RVR', 'RR'), ('GL-YE', 'YE')) v(code, prefix);
            insert into core.number_series_assignment (tenant_id, company_id, document_type, series_id, effective_from, created_by)
              select @t, @c, case series_code when 'GL-RVL' then 'REVALUATION' when 'GL-RVR' then 'REVAL_REVERSAL' else 'YEAR_END' end, series_id, date '2026-01-01', 'test'
              from core.number_series where tenant_id = @t and company_id = @c and series_code in ('GL-RVL', 'GL-RVR', 'GL-YE');
            insert into core.number_range_counter (tenant_id, company_id, series_id, fiscal_year, last_number)
              select @t, @c, series_id, 2026, 0 from core.number_series where tenant_id = @t and company_id = @c and series_code in ('GL-RVL', 'GL-RVR', 'GL-YE');
            insert into core.exchange_rate (tenant_id, rate_type, from_currency, to_currency, valid_from, rate)
              select @t, 'SPOT', v.f, v.o, d::date, v.r
              from generate_series(timestamp '2026-01-01', timestamp '2026-12-31', interval '1 day') d, (values ('USD', 'DKK', 7.0), ('USD', 'EUR', 0.9)) v(f, o, r)
              where d::date <> date '2026-01-01';
            update core.gl_account set revalue_flag = @reval where tenant_id = @t and company_id = @c and account_id = @bank;
            insert into core.exchange_rate (tenant_id, rate_type, from_currency, to_currency, valid_from, rate)
              select @t, 'CLOSING', v.f, v.o, date '2026-03-31', v.r from (values ('USD', 'DKK', 7.2), ('USD', 'EUR', 0.95)) v(f, o, r) where @rates;
            """, ("reval", revalueBank), ("bank", t.Bank), ("rates", closingRates));
        return t;
    }

    internal static CommandContext Run(TestTenant t, string key) => t.Context(key, "revaluation.run");

    internal static async Task PostUsdAsync(TestTenant t, string key, string date, decimal amount)
    {
        var r = await new JournalDocumentPoster(t.App).PostAsync(t.TenantId, t.Context(key), t.Journal(postingDate: date, currency: "USD", amount: amount));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
    }

    private static async Task AssertChainsOk(TestTenant t)
    {
        var v = await new ChainVerification(t.App).VerifyAsync(t.TenantId, t.Context("verify", "chain.verify"), new(t.CompanyId, "LEDGER"));
        Assert.True(v.Status == CommandStatus.Ok && v.Value!.Ok, Describe(v) + " " + string.Join("; ", v.Value?.Breaks.Select(b => $"{b.Code} {b.ChainSeq}") ?? []));
        var p = await new ChainVerifier(t.App).VerifyPeriodEventsAsync(t.TenantId, t.CompanyId);
        Assert.True(p.Ok, string.Join("; ", p.Problems.Select(x => $"{x.Code} {x.ChainSeq} {x.Detail}")));
    }

    [Fact]
    public async Task ARunPostsTheRevaluationAndItsAutomaticReversalAndRecordsTheItems()
    {
        var t = await ReadyAsync();
        await PostUsdAsync(t, "usd1", "2026-03-15", 1000m);
        var cmds = new RevaluationCommands(t.App);

        var preview = await cmds.PreviewAsync(t.TenantId, t.Context("pv", "revaluation.preview"), new(t.CompanyId, 2026, 3));
        Assert.True(preview.Status == CommandStatus.Ok, Describe(preview));
        Assert.Null(preview.Value!.RunId);
        Assert.Equal(1, preview.Value.ItemCount);

        var run = await cmds.RunAsync(t.TenantId, Run(t, "r1"), new(t.CompanyId, 2026, 3));
        Assert.True(run.Status == CommandStatus.Ok, Describe(run));
        RevaluationResult v = run.Value!;
        Assert.Equal(preview.Value.ItemsDigest, v.ItemsDigest);
        Assert.Equal(1, v.RunNo);
        Assert.Equal(new DateOnly(2026, 3, 31), v.ValuationDate);

        Assert.Equal("REVALUATION", v.Document!.DocumentType);
        Assert.Equal(new DateOnly(2026, 3, 31), v.Document.PostingDate);
        Assert.Equal("REVAL_REVERSAL", v.ReversalDocument!.DocumentType);
        Assert.Equal(new DateOnly(2026, 4, 1), v.ReversalDocument.PostingDate);
        Assert.Equal(4, v.ReversalDocument.FiscalPeriod);

        // one adjustment line on the bank account (TC 0, +200 DKK, +50 EUR group and reporting) and one gain line against it
        var adj = v.Document.Lines.Single(l => l.LineType == "REVAL_ADJUSTMENT");
        Assert.Equal((t.Bank, 0m, 200m, 50m, 50m), (adj.AccountId, adj.AmountTc, adj.AmountLc, adj.AmountGc, adj.AmountRc));
        var fx = v.Document.Lines.Single(l => l.LineType == "FX_UNREALISED");
        Assert.Equal((-200m, -50m, -50m), (fx.AmountLc, fx.AmountGc, fx.AmountRc));
        Assert.All(v.ReversalDocument.Lines, l => Assert.Equal(-v.Document.Lines.Single(o => o.LineNo == l.LineNo).AmountLc, l.AmountLc));

        // the item of the run, the rates, and the link of the documents
        var item = v.Items.Single();
        Assert.Equal(("ACCOUNT_BALANCE", 1000m, 7000m, 7200m, 200m, 1), (item.BasisType, item.RemainingTc, item.CarriedLc, item.RevaluedLc, item.AdjustmentLc, item.AdjustmentLineNo));
        Assert.Equal(2, v.Rates.Count);
        Assert.Equal("reversed", await t.ScalarAsync<string>("select status from core.journal_document where tenant_id = @t and document_id = @d", ("d", v.Document.DocumentId)));
        Assert.Equal(v.ReversalDocument.DocumentId, await t.ScalarAsync<Guid>("select reversed_by_document_id from core.journal_document where tenant_id = @t and document_id = @d", ("d", v.Document.DocumentId)));
        Assert.Equal(Core.Hashing.Canonical.FormatHex32(await t.ScalarAsync<byte[]>("select detail_digest from core.journal_document where tenant_id = @t and document_id = @d", ("d", v.Document.DocumentId))), v.ItemsDigest);
        Assert.Equal(new DateOnly(2026, 4, 1), DateOnly.FromDateTime(await t.ScalarAsync<DateTime>("select auto_reversal_date from core.journal_document where tenant_id = @t and document_id = @d", ("d", v.Document.DocumentId))));

        // the balance at closing rate in March includes the adjustment, from 1 April it is gone again (3.8)
        Assert.Equal(7200m, await t.ScalarAsync<decimal>("select sum(amount_lc) from core.journal_line where tenant_id = @t and account_id = @b and posting_date <= date '2026-03-31'", ("b", t.Bank)));
        Assert.Equal(7000m, await t.ScalarAsync<decimal>("select sum(amount_lc) from core.journal_line where tenant_id = @t and account_id = @b and posting_date <= date '2026-04-30'", ("b", t.Bank)));
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.outbox_event where tenant_id = @t and event_type = 'revaluation.run_completed'"));
        await AssertChainsOk(t);

        // the same request again is a replay
        var replay = await cmds.RunAsync(t.TenantId, Run(t, "r1"), new(t.CompanyId, 2026, 3));
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(v.RunId, replay.Value!.RunId);
        Assert.Equal(["IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST"], Codes(await cmds.RunAsync(t.TenantId, Run(t, "r1"), new(t.CompanyId, 2026, 4))));
    }

    [Fact]
    public async Task ARerunNeutralisesTheOldPairAndPostsANewOne()
    {
        var t = await ReadyAsync();
        await PostUsdAsync(t, "usd1", "2026-03-15", 1000m);
        var cmds = new RevaluationCommands(t.App);
        var first = (await cmds.RunAsync(t.TenantId, Run(t, "r1"), new(t.CompanyId, 2026, 3))).Value!;

        await PostUsdAsync(t, "usd2", "2026-03-20", 500m);   // a later posting into the valuated period
        var rerun = await cmds.RunAsync(t.TenantId, Run(t, "r2"), new(t.CompanyId, 2026, 3));
        Assert.True(rerun.Status == CommandStatus.Ok, Describe(rerun));
        RevaluationResult v = rerun.Value!;
        Assert.Equal((2, first.RunId), (v.RunNo, v.SupersedesRunId));
        Assert.Equal(2, v.RerunMirrors.Count);
        Assert.Equal([new DateOnly(2026, 3, 31), new DateOnly(2026, 4, 1)], v.RerunMirrors.Select(m => m.PostingDate).ToArray());
        Assert.All(v.RerunMirrors, m => Assert.Equal("REVAL_REVERSAL", m.DocumentType));

        // R0, A0, M1, M2, R1, A1
        Assert.Equal(6L, await t.ScalarAsync<long>("select count(*) from core.journal_document where tenant_id = @t and document_type in ('REVALUATION', 'REVAL_REVERSAL')"));
        Assert.Equal(["RERUN", "RERUN"], await Reasons(t, first));
        Assert.Equal("SUPERSEDED", await t.ScalarAsync<string>("select status from core.revaluation_run where tenant_id = @t and run_id = @r", ("r", first.RunId!.Value)));
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.revaluation_run where tenant_id = @t and status = 'ACTIVE'"));
        // R0 keeps A0 as its first mirror
        Assert.Equal(first.ReversalDocument!.DocumentId, await t.ScalarAsync<Guid>("select reversed_by_document_id from core.journal_document where tenant_id = @t and document_id = @d", ("d", first.Document!.DocumentId)));

        // the net adjustment on the bank at 31 March is the new one (1500 USD: 10800 - 10500 = 300), and nothing is left from 1 April
        Assert.Equal(300m, await t.ScalarAsync<decimal>(
            "select sum(l.amount_lc) from core.journal_line l where l.tenant_id = @t and l.account_id = @b and l.line_type = 'REVAL_ADJUSTMENT' and l.posting_date <= date '2026-03-31'", ("b", t.Bank)));
        Assert.Equal(0m, await t.ScalarAsync<decimal>(
            "select sum(l.amount_lc) from core.journal_line l where l.tenant_id = @t and l.account_id = @b and l.line_type = 'REVAL_ADJUSTMENT'", ("b", t.Bank)));
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.outbox_event where tenant_id = @t and event_type = 'revaluation.rerun'"));
        await AssertChainsOk(t);
    }

    private static async Task<string[]> Reasons(TestTenant t, RevaluationResult first)
    {
        await using var admin = await t.OpenAdminAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            "select reversal_reason_code from core.journal_document where tenant_id = @t and document_type = 'REVAL_REVERSAL' and reversal_reason_code = 'RERUN' order by chain_seq", admin);
        cmd.Parameters.AddWithValue("t", t.TenantId);
        var list = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(r.GetString(0));
        return list.ToArray();
    }

    [Fact]
    public async Task ARunWithNothingToAdjustIsStillRecorded()
    {
        var t = await ReadyAsync(revalueBank: false);
        await PostUsdAsync(t, "usd1", "2026-03-15", 1000m);
        var run = await new RevaluationCommands(t.App).RunAsync(t.TenantId, Run(t, "r1"), new(t.CompanyId, 2026, 3));
        Assert.True(run.Status == CommandStatus.Ok, Describe(run));
        Assert.Null(run.Value!.Document);
        Assert.Equal(0, run.Value.ItemCount);
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.revaluation_run where tenant_id = @t and document_id is null"));
        Assert.Equal(0L, await t.ScalarAsync<long>("select count(*) from core.journal_document where tenant_id = @t and document_type = 'REVALUATION'"));
    }

    [Fact]
    public async Task TheRulesOfARunAreChecked()
    {
        var t = await ReadyAsync(closingRates: false);
        await PostUsdAsync(t, "usd1", "2026-03-15", 1000m);
        var cmds = new RevaluationCommands(t.App);

        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await cmds.RunAsync(t.TenantId, t.Context("a"), new(t.CompanyId, 2026, 3))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await cmds.RunAsync(t.TenantId, Run(t, "b"), new(t.CompanyId, 2026, 13))));
        Assert.Equal(["REVAL.CLOSING_RATE_MISSING"], Codes(await cmds.RunAsync(t.TenantId, Run(t, "c"), new(t.CompanyId, 2026, 3))));

        await t.ExecuteAsync("insert into core.exchange_rate (tenant_id, rate_type, from_currency, to_currency, valid_from, rate) values (@t, 'CLOSING', 'USD', 'DKK', date '2026-03-31', 7.2), (@t, 'CLOSING', 'USD', 'EUR', date '2026-03-31', 0.95)");
        await t.SetPeriodStatusAsync(3, "SOFT_CLOSED");
        Assert.Equal(["REVAL.SOFT_CLOSE_NOT_AUTHORISED"], Codes(await cmds.RunAsync(t.TenantId, Run(t, "d"), new(t.CompanyId, 2026, 3))));
        await t.SetPeriodStatusAsync(3, "OPEN");
        await t.SetPeriodStatusAsync(4, "SOFT_CLOSED");
        Assert.Equal(["REVAL.SOFT_CLOSE_NOT_AUTHORISED"], Codes(await cmds.RunAsync(t.TenantId, Run(t, "e"), new(t.CompanyId, 2026, 3))));
        await t.SetPeriodStatusAsync(4, "OPEN");
        await t.SetPeriodStatusAsync(3, "HARD_CLOSED");
        Assert.Equal(["REVAL.PERIOD_NOT_OPEN"], Codes(await cmds.RunAsync(t.TenantId, Run(t, "f"), new(t.CompanyId, 2026, 3))));
        await t.SetPeriodStatusAsync(3, "OPEN");

        // December: the next year has not been opened
        await t.ExecuteAsync("insert into core.exchange_rate (tenant_id, rate_type, from_currency, to_currency, valid_from, rate) values (@t, 'CLOSING', 'USD', 'DKK', date '2026-12-31', 7.2), (@t, 'CLOSING', 'USD', 'EUR', date '2026-12-31', 0.95)");
        Assert.Equal(["REVAL.NEXT_YEAR_NOT_OPENED"], Codes(await cmds.RunAsync(t.TenantId, Run(t, "g"), new(t.CompanyId, 2026, 12))));
        Assert.Equal(0L, await t.ScalarAsync<long>("select count(*) from core.revaluation_run where tenant_id = @t"));
    }

    [Fact]
    public async Task HardCloseRejectsAStaleRevaluationAndAcceptsARerun()
    {
        var t = await ReadyAsync();
        await PostUsdAsync(t, "usd1", "2026-03-15", 1000m);
        var hard = new PeriodStatusCommands(t.App);
        await hard.HardCloseAsync(t.TenantId, t.Context("h1", "period.hard_close"), new PeriodRef(t.CompanyId, 2026, 1));
        await hard.HardCloseAsync(t.TenantId, t.Context("h2", "period.hard_close"), new PeriodRef(t.CompanyId, 2026, 2));

        // no run, and the preview is not empty
        var noRun = await hard.HardCloseAsync(t.TenantId, t.Context("h3", "period.hard_close"), new PeriodRef(t.CompanyId, 2026, 3));
        Assert.Equal(["CLOSE.REVALUATION_STALE"], Codes(noRun));
        Assert.Equal("OPEN", await t.ScalarAsync<string>("select status from core.fiscal_period where tenant_id = @t and fiscal_year = 2026 and period_no = 3"));

        var cmds = new RevaluationCommands(t.App);
        Assert.Equal(CommandStatus.Ok, (await cmds.RunAsync(t.TenantId, Run(t, "r1"), new(t.CompanyId, 2026, 3))).Status);

        // a posting after the run makes it stale
        await PostUsdAsync(t, "usd2", "2026-03-20", 500m);
        Assert.Equal(["CLOSE.REVALUATION_STALE"], Codes(await hard.HardCloseAsync(t.TenantId, t.Context("h4", "period.hard_close"), new PeriodRef(t.CompanyId, 2026, 3))));

        Assert.Equal(CommandStatus.Ok, (await cmds.RunAsync(t.TenantId, Run(t, "r2"), new(t.CompanyId, 2026, 3))).Status);
        long anchorsBefore = await t.ScalarAsync<long>("select count(*) from core.chain_anchor where tenant_id = @t");
        var closed = await hard.HardCloseAsync(t.TenantId, t.Context("h5", "period.hard_close"), new PeriodRef(t.CompanyId, 2026, 3));
        Assert.True(closed.Status == CommandStatus.Ok, Describe(closed));

        // the close wrote an anchor request per chain head, PENDING, with the digest of the head (02, 9.5)
        Assert.True(await t.ScalarAsync<long>("select count(*) from core.chain_anchor where tenant_id = @t") > anchorsBefore);
        Assert.Equal(0L, await t.ScalarAsync<long>("select count(*) from core.chain_anchor where tenant_id = @t and anchor_state <> 'PENDING'"));
        Assert.Equal(await t.ScalarAsync<long>("select last_seq from core.chain_head where tenant_id = @t and company_id = @c and chain_type = 'PERIOD_EVENT'", ("c", t.CompanyId)),
            await t.ScalarAsync<long>("select max(chain_seq) from core.chain_anchor where tenant_id = @t and chain_type = 'PERIOD_EVENT'"));
        await AssertChainsOk(t);
    }

    [Fact]
    public async Task ThePeriodRoleLocksTheHeadsOnlyThroughTheFunction()
    {
        var t = await ReadyAsync();
        await using var tx = await TenantTransaction.BeginAsync(t.App, t.TenantId, CommandRole.Period);
        // direct: the restrictive policy hides the head from FOR UPDATE (0006). Through the function it works.
        await using (var cmd = tx.Command("select core.lock_chain_heads(@c)")) { cmd.Parameters.AddWithValue("c", t.CompanyId); await cmd.ExecuteScalarAsync(); }
        await using (var cmd = tx.Command("select core.lock_anchor_heads(@c)")) { cmd.Parameters.AddWithValue("c", t.CompanyId); await cmd.ExecuteScalarAsync(); }
        await using var denied = tx.Command("update core.chain_head set last_seq = last_seq + 1 where tenant_id = @t and chain_type = 'LEDGER'");
        denied.Parameters.AddWithValue("t", t.TenantId);
        Assert.Equal(0, await denied.ExecuteNonQueryAsync());
    }
}
