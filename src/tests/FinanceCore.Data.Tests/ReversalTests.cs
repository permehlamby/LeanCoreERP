using FinanceCore.Core.Commands;
using FinanceCore.Core.Periods;
using FinanceCore.Core.Posting;

namespace FinanceCore.Data.Tests;

[Collection("db")]
public class ReversalTests
{
    private static string Describe<T>(CommandResult<T> r) =>
        r.Status + ": " + string.Join(", ", r.Errors.Select(e => $"{e.Code} {e.Path}"));

    private static string[] Codes<T>(CommandResult<T> r) => r.Errors.Select(e => e.Code).ToArray();

    private static CommandContext Rev(TestTenant t, string key) => t.Context(key, "journal.reverse");

    private static async Task<PostedDocument> Post(TestTenant t, string key, string date = "2026-03-15", string currency = "DKK", decimal amount = 1000m)
    {
        var r = await new JournalDocumentPoster(t.App).PostAsync(t.TenantId, t.Context(key), t.Journal(date, currency, amount));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        return r.Value!;
    }

    [Fact]
    public async Task MirrorHasEveryLineWithTheOppositeSignAndTheOriginalRates()
    {
        var t = await TestTenant.CreateReadyAsync();
        var original = await Post(t, "orig", "2026-01-05", "USD", 100m);   // USD with rate fallback: LC 700, GC 90, RC 90
        var r = await new DocumentReverser(t.App).ReverseAsync(t.TenantId, Rev(t, "rev"), new(original.DocumentId, null, "CORRECTION", "Wrong account"));

        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        PostedDocument m = r.Value!;
        Assert.Equal("REVERSAL", m.DocumentType);
        Assert.Equal("RV-2026-000001", m.DocumentNumber);
        Assert.Equal(original.ChainSeq + 1, m.ChainSeq);
        Assert.Equal(original.RowHash, m.PrevHash);
        Assert.Equal(original.PostingDate, m.PostingDate);   // default: the original posting date
        Assert.Equal(original.Lines.Count, m.Lines.Count);
        for (int i = 0; i < m.Lines.Count; i++)
        {
            PostedLine o = original.Lines[i], x = m.Lines[i];
            Assert.Equal(o.LineNo, x.LineNo);
            Assert.Equal(-o.AmountTc, x.AmountTc);
            Assert.Equal(-o.AmountLc, x.AmountLc);
            Assert.Equal(-o.AmountGc, x.AmountGc);
            Assert.Equal(-o.AmountRc, x.AmountRc);
            Assert.Equal((o.RateLc, o.RateGc, o.RateRc, o.RateDate), (x.RateLc, x.RateGc, x.RateRc, x.RateDate));
            Assert.Equal(o.AccountId, x.AccountId);
        }

        Assert.Equal("reversed", await t.ScalarAsync<string>("select status from core.journal_document where tenant_id = @t and document_id = @d", ("d", original.DocumentId)));
        Assert.Equal(m.DocumentId, await t.ScalarAsync<Guid>("select reversed_by_document_id from core.journal_document where tenant_id = @t and document_id = @d", ("d", original.DocumentId)));
        Assert.Equal("CORRECTION", await t.ScalarAsync<string>("select reversal_reason_code from core.journal_document where tenant_id = @t and document_id = @d", ("d", m.DocumentId)));
        Assert.Equal(2L, await t.ScalarAsync<long>("select count(*) from core.journal_line where tenant_id = @t and document_id = @d and reversed_line_no = line_no", ("d", m.DocumentId)));
        Assert.Equal(0m, await t.ScalarAsync<decimal>("select sum(amount_lc) from core.journal_line where tenant_id = @t and document_id in (@a, @b)", ("a", original.DocumentId), ("b", m.DocumentId)));

        var v = await new ChainVerifier(t.App).VerifyLedgerAsync(t.TenantId, t.CompanyId, t.LedgerId);
        Assert.True(v.Ok, string.Join("; ", v.Problems.Select(p => p.Code + " " + p.Detail)));
        Assert.Equal("ledger.document_reversed", await t.ScalarAsync<string>(
            "select event_type from core.outbox_event where tenant_id = @t order by position desc limit 1"));
    }

    [Fact]
    public async Task ReversalDateIsUsedAndTheCoreNeverShiftsIt()
    {
        var t = await TestTenant.CreateReadyAsync();
        var original = await Post(t, "orig", "2026-03-15");
        var reverser = new DocumentReverser(t.App);

        await new PeriodStatusCommands(t.App).HardCloseAsync(t.TenantId, t.Context("h1", "period.hard_close"), new(t.CompanyId, 2026, 1));
        await new PeriodStatusCommands(t.App).HardCloseAsync(t.TenantId, t.Context("h2", "period.hard_close"), new(t.CompanyId, 2026, 2));
        await new PeriodStatusCommands(t.App).HardCloseAsync(t.TenantId, t.Context("h3", "period.hard_close"), new(t.CompanyId, 2026, 3));

        // the original posting date lies in a hard-closed period: rejected, nothing written, the caller picks a later date
        var closed = await reverser.ReverseAsync(t.TenantId, Rev(t, "r1"), new(original.DocumentId, null, "ERROR", null));
        Assert.Contains("PERIOD.HARD_CLOSED", Codes(closed));
        Assert.Equal("reversal_date", closed.Errors[0].Path);
        Assert.Equal("posted", await t.ScalarAsync<string>("select status from core.journal_document where tenant_id = @t and document_id = @d", ("d", original.DocumentId)));
        Assert.Equal(0L, await t.ScalarAsync<long>("select last_number from core.number_range_counter where tenant_id = @t and series_id = @s", ("s", t.ReversalSeriesId)));

        var later = await reverser.ReverseAsync(t.TenantId, Rev(t, "r2"), new(original.DocumentId, new DateOnly(2026, 4, 2), "ERROR", "Moved"));
        Assert.True(later.Status == CommandStatus.Ok, Describe(later));
        Assert.Equal(new DateOnly(2026, 4, 2), later.Value!.PostingDate);
        Assert.Equal(4, later.Value.FiscalPeriod);
    }

    [Fact]
    public async Task ADocumentIsReversedOnceAndAMirrorCannotBeReversed()
    {
        var t = await TestTenant.CreateReadyAsync();
        var original = await Post(t, "orig");
        var reverser = new DocumentReverser(t.App);
        var first = await reverser.ReverseAsync(t.TenantId, Rev(t, "r1"), new(original.DocumentId, null, "CORRECTION", null));

        var again = await reverser.ReverseAsync(t.TenantId, Rev(t, "r2"), new(original.DocumentId, null, "CORRECTION", null));
        Assert.Equal(["REVERSAL.ALREADY_REVERSED"], Codes(again));

        var mirror = await reverser.ReverseAsync(t.TenantId, Rev(t, "r3"), new(first.Value!.DocumentId, null, "CORRECTION", null));
        Assert.Equal(["REVERSAL.TYPE_NOT_REVERSIBLE"], Codes(mirror));
        Assert.Equal("REVERSAL", mirror.Errors[0].Params!["document_type"]);
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.journal_document where tenant_id = @t and document_type = 'REVERSAL'"));
    }

    [Fact]
    public async Task ReasonPermissionAndDocumentAreChecked()
    {
        var t = await TestTenant.CreateReadyAsync();
        var original = await Post(t, "orig");
        var reverser = new DocumentReverser(t.App);

        Assert.Equal(["REVERSAL.REASON_REQUIRED"], Codes(await reverser.ReverseAsync(t.TenantId, Rev(t, "a"), new(original.DocumentId, null, "", null))));
        foreach (string system in new[] { "AUTO_REVERSAL", "RERUN", "REOPEN", "RETURN", "SOMETHING" })
        {
            var r = await reverser.ReverseAsync(t.TenantId, Rev(t, "b-" + system), new(original.DocumentId, null, system, null));
            Assert.Equal(["REVERSAL.REASON_NOT_ALLOWED"], Codes(r));
            Assert.Equal(["CORRECTION", "ERROR"], (string[])r.Errors[0].Params!["allowed_values"]!);
        }
        Assert.Equal(["AUTH.PERMISSION_DENIED"], Codes(await reverser.ReverseAsync(t.TenantId, t.Context("c", "journal.post"), new(original.DocumentId, null, "ERROR", null))));
        Assert.Equal(["REVERSAL.NOT_FOUND"], Codes(await reverser.ReverseAsync(t.TenantId, Rev(t, "d"), new(Guid.NewGuid(), null, "ERROR", null))));
        Assert.Equal("posted", await t.ScalarAsync<string>("select status from core.journal_document where tenant_id = @t and document_id = @d", ("d", original.DocumentId)));
    }

    [Fact]
    public async Task SameKeyIsAReplayAndAnotherRequestWithTheKeyIsRejected()
    {
        var t = await TestTenant.CreateReadyAsync();
        var a = await Post(t, "a");
        var b = await Post(t, "b", amount: 50m);
        var reverser = new DocumentReverser(t.App);
        var first = await reverser.ReverseAsync(t.TenantId, Rev(t, "same"), new(a.DocumentId, null, "ERROR", null));
        var replay = await reverser.ReverseAsync(t.TenantId, Rev(t, "same"), new(a.DocumentId, null, "ERROR", null));

        Assert.True(replay.IdempotentReplay);
        Assert.Equal(first.Value!.DocumentId, replay.Value!.DocumentId);
        Assert.Equal(first.Value.RowHash, replay.Value.RowHash);
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.journal_document where tenant_id = @t and document_type = 'REVERSAL'"));

        var other = await reverser.ReverseAsync(t.TenantId, Rev(t, "same"), new(b.DocumentId, null, "ERROR", null));
        Assert.Contains("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", Codes(other));
        Assert.Equal("posted", await t.ScalarAsync<string>("select status from core.journal_document where tenant_id = @t and document_id = @d", ("d", b.DocumentId)));
    }

    [Fact]
    public async Task ReversalNeedsASeriesForTheMirrorType()
    {
        var t = await TestTenant.CreateReadyAsync();
        var original = await Post(t, "orig");
        // the assignment is insert-only, so remove it the only way that exists: as the migration role with the guard off
        await t.ExecuteAsync("""
            alter table core.number_series_assignment disable trigger user;
            delete from core.number_series_assignment where tenant_id = @t and document_type = 'REVERSAL';
            alter table core.number_series_assignment enable trigger user;
            """);
        var r = await new DocumentReverser(t.App).ReverseAsync(t.TenantId, Rev(t, "r"), new(original.DocumentId, null, "ERROR", null));
        Assert.Contains("SERIES.NO_ASSIGNMENT", Codes(r));
    }

    [Fact]
    public async Task ConcurrentReversalsOfOneDocumentAllowExactlyOne()
    {
        var t = await TestTenant.CreateReadyAsync();
        if (t.Settings.IsLocalTestDouble) return;   // PGlite serves one connection at a time: real PostgreSQL only

        var original = await Post(t, "orig");
        var reverser = new DocumentReverser(t.App);
        var results = await Task.WhenAll(Enumerable.Range(1, 4).Select(i =>
            reverser.ReverseAsync(t.TenantId, Rev(t, "c" + i), new(original.DocumentId, null, "ERROR", null))));

        Assert.Equal(1, results.Count(r => r.Status == CommandStatus.Ok));
        Assert.All(results.Where(r => r.Status != CommandStatus.Ok),
            r => Assert.True(r.Errors.All(e => e.Code == "REVERSAL.ALREADY_REVERSED" || e.Retryable), Describe(r)));
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.journal_document where tenant_id = @t and document_type = 'REVERSAL'"));
        var v = await new ChainVerifier(t.App).VerifyLedgerAsync(t.TenantId, t.CompanyId, t.LedgerId);
        Assert.True(v.Ok, string.Join("; ", v.Problems.Select(p => p.Code + " " + p.Detail)));
    }
}
