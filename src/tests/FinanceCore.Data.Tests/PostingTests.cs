using FinanceCore.Core.Commands;
using FinanceCore.Core.Hashing;
using FinanceCore.Core.Posting;

namespace FinanceCore.Data.Tests;

[Collection("db")]
public class PostingTests
{
    private static string[] Codes(CommandResult<PostedDocument> r) => r.Errors.Select(e => e.Code).ToArray();

    private static string Describe(CommandResult<PostedDocument> r) =>
        r.Status + ": " + string.Join(", ", r.Errors.Select(e => $"{e.Code} {e.Path}"));

    [Fact]
    public async Task FirstDocumentGetsNumberOneAndLinksToGenesis()
    {
        var t = await TestTenant.CreateAsync();
        var result = await new JournalDocumentPoster(t.App).PostAsync(t.TenantId, t.Context("k1"), t.Journal());

        Assert.True(result.Status == CommandStatus.Ok, string.Join(", ", result.Errors.Select(e => e.Code + " " + e.Path)));
        PostedDocument d = result.Value!;
        Assert.Equal("GL-2026-000001", d.DocumentNumber);
        Assert.Equal(1L, d.ChainSeq);
        Assert.Equal(3, d.FiscalPeriod);
        Assert.Equal(Canonical.FormatHex32(LedgerHash2.Genesis(t.TenantId, t.CompanyId, "LEDGER", t.LedgerId.ToString("D"))), d.PrevHash);
        Assert.Equal(2, d.Lines.Count);

        // DKK document: local amount equals transaction amount, group and reporting currency are EUR at 0.134
        Assert.All(d.Lines, l => { Assert.Equal(1m, l.RateLc); Assert.Equal(0.134m, l.RateGc); Assert.Equal(0.134m, l.RateRc); });
        Assert.Equal(1000m, d.Lines[0].AmountLc);
        Assert.Equal(134m, d.Lines[0].AmountGc);
        Assert.Equal(-134m, d.Lines[1].AmountRc);

        Assert.Equal(1L, await t.ScalarAsync<long>("select last_seq from core.chain_head where tenant_id = @t and chain_type = 'LEDGER'"));
        Assert.Equal(1L, await t.ScalarAsync<long>("select last_position from core.outbox_head where tenant_id = @t"));
        Assert.Equal("ledger.document_posted", await t.ScalarAsync<string>("select event_type from core.outbox_event where tenant_id = @t"));
    }

    [Fact]
    public async Task NumbersAreGaplessAndTheChainVerifies()
    {
        var t = await TestTenant.CreateAsync();
        var poster = new JournalDocumentPoster(t.App);
        for (int i = 1; i <= 5; i++)
        {
            var r = await poster.PostAsync(t.TenantId, t.Context("k" + i), t.Journal(amount: 100m * i));
            Assert.Equal(CommandStatus.Ok, r.Status);
            Assert.Equal($"GL-2026-{i:000000}", r.Value!.DocumentNumber);
            Assert.Equal(i, r.Value.ChainSeq);
        }
        var v = await new ChainVerifier(t.App).VerifyLedgerAsync(t.TenantId, t.CompanyId, t.LedgerId);
        Assert.True(v.Ok, string.Join("; ", v.Problems.Select(p => $"{p.Code} {p.ChainSeq} {p.Detail}")));
        Assert.Equal(5L, v.DocumentsChecked);
    }

    [Fact]
    public async Task SameKeyAndRequestIsAReplayAndPostsNothingNew()
    {
        var t = await TestTenant.CreateAsync();
        var poster = new JournalDocumentPoster(t.App);
        var first = await poster.PostAsync(t.TenantId, t.Context("same"), t.Journal());
        var second = await poster.PostAsync(t.TenantId, t.Context("same"), t.Journal());

        Assert.Equal(CommandStatus.Ok, second.Status);
        Assert.True(second.IdempotentReplay);
        Assert.Equal(first.Value!.DocumentId, second.Value!.DocumentId);
        Assert.Equal(first.Value.RowHash, second.Value.RowHash);
        Assert.Equal(first.CommandLogId, second.CommandLogId);
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.journal_document where tenant_id = @t"));
        Assert.Equal(1L, await t.ScalarAsync<long>("select last_number from core.number_range_counter where tenant_id = @t"));
    }

    [Fact]
    public async Task SameKeyWithAnotherRequestIsRejected()
    {
        var t = await TestTenant.CreateAsync();
        var poster = new JournalDocumentPoster(t.App);
        await poster.PostAsync(t.TenantId, t.Context("reuse"), t.Journal(amount: 100m));
        var second = await poster.PostAsync(t.TenantId, t.Context("reuse"), t.Journal(amount: 200m));

        Assert.Equal(CommandStatus.Rejected, second.Status);
        Assert.Contains("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", Codes(second));
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.journal_document where tenant_id = @t"));
    }

    [Fact]
    public async Task UnbalancedDocumentIsRejectedBeforeAnyNumberIsTaken()
    {
        var t = await TestTenant.CreateAsync();
        var req = t.Journal() with { Lines = [new RequestLine("1", t.Bank, 100m), new RequestLine("2", t.Revenue, -90m)] };
        var r = await new JournalDocumentPoster(t.App).PostAsync(t.TenantId, t.Context("unbal"), req);

        Assert.Equal(CommandStatus.Rejected, r.Status);
        Assert.Contains("DOCUMENT.NOT_BALANCED", Codes(r));
        Assert.Equal(0L, await t.ScalarAsync<long>("select last_number from core.number_range_counter where tenant_id = @t"));
        Assert.Equal(0L, await t.ScalarAsync<long>("select count(*) from core.command_log where tenant_id = @t"));
    }

    [Fact]
    public async Task SeveralIndependentErrorsAreReportedTogether()
    {
        var t = await TestTenant.CreateAsync();
        var req = t.Journal(postingDate: "2027-01-05") with
        {
            Lines = [new RequestLine("1", t.Blocked, 100m), new RequestLine("2", Guid.NewGuid(), -90m)]
        };
        var r = await new JournalDocumentPoster(t.App).PostAsync(t.TenantId, t.Context("many"), req);

        Assert.Equal(CommandStatus.Rejected, r.Status);
        var codes = Codes(r);
        Assert.Contains("ACCOUNT.POSTING_BLOCKED", codes);
        Assert.Contains("ACCOUNT.NOT_FOUND", codes);
        Assert.Contains("PERIOD.NOT_FOUND", codes);
        Assert.Contains("DOCUMENT.NOT_BALANCED", codes);
        Assert.Contains("lines[1].account_id", r.Errors.Select(e => e.Path));
    }

    [Fact]
    public async Task MissingPermissionAndMissingSeriesAreRejected()
    {
        var t = await TestTenant.CreateAsync();
        var poster = new JournalDocumentPoster(t.App);

        var denied = await poster.PostAsync(t.TenantId, t.Context("p", "read.ledger"), t.Journal());
        Assert.Equal(["AUTH.PERMISSION_DENIED"], Codes(denied));

        var payment = await poster.PostAsync(t.TenantId, t.Context("s"), t.Journal() with { DocumentType = "PAYMENT" });
        Assert.Contains("SERIES.NO_ASSIGNMENT", Codes(payment));

        var system = await poster.PostAsync(t.TenantId, t.Context("x"), t.Journal() with { DocumentType = "YEAR_END" });
        Assert.Contains("DOCUMENT.TYPE_NOT_ALLOWED", Codes(system));
    }

    [Fact]
    public async Task ClosedPeriodsAreHonoured()
    {
        var t = await TestTenant.CreateAsync();
        var poster = new JournalDocumentPoster(t.App);
        await t.SetPeriodStatusAsync(2, "HARD_CLOSED");
        await t.SetPeriodStatusAsync(3, "SOFT_CLOSED");

        var hard = await poster.PostAsync(t.TenantId, t.Context("h"), t.Journal(postingDate: "2026-02-10"));
        Assert.Contains("PERIOD.HARD_CLOSED", Codes(hard));

        var soft = await poster.PostAsync(t.TenantId, t.Context("s1"), t.Journal(postingDate: "2026-03-10"));
        Assert.Contains("PERIOD.SOFT_CLOSED_NOT_AUTHORISED", Codes(soft));

        var allowed = await poster.PostAsync(t.TenantId, t.Context("s2", "journal.post", "post.soft_closed"), t.Journal(postingDate: "2026-03-10"));
        Assert.Equal(CommandStatus.Ok, allowed.Status);
        Assert.True(allowed.Value!.SoftCloseOverride);
        Assert.Contains(allowed.Warnings, w => w.Code == "POSTING.SOFT_CLOSE_OVERRIDE");
    }

    [Fact]
    public async Task PostingWindowAndClosedYearAreHonoured()
    {
        var t = await TestTenant.CreateAsync();
        var poster = new JournalDocumentPoster(t.App);
        await t.ExecuteAsync("""
            insert into core.posting_window (tenant_id, company_id, effective_from, earliest_posting_date, latest_posting_date, created_by)
            values (@t, @c, now() - interval '1 day', date '2026-03-01', date '2026-03-31', 'test')
            """);
        var outside = await poster.PostAsync(t.TenantId, t.Context("w"), t.Journal(postingDate: "2026-04-02"));
        Assert.Contains("POSTING.OUTSIDE_WINDOW", Codes(outside));
        Assert.Equal(CommandStatus.Ok, (await poster.PostAsync(t.TenantId, t.Context("w2"), t.Journal(postingDate: "2026-03-02"))).Status);
    }

    [Fact]
    public async Task ForeignCurrencyUsesTheRateTableWithFallbackAndMarksRatesUsed()
    {
        var t = await TestTenant.CreateAsync();
        var poster = new JournalDocumentPoster(t.App);

        // USD 100.00 on 2026-01-05: the rates are valid from 2026-01-01 (4 days back, inside the 7 days of fallback)
        var r = await poster.PostAsync(t.TenantId, t.Context("usd"), t.Journal(postingDate: "2026-01-05", currency: "USD", amount: 100m));
        Assert.Equal(CommandStatus.Ok, r.Status);
        Assert.Equal(7.0m, r.Value!.Lines[0].RateLc);
        Assert.Equal(700m, r.Value.Lines[0].AmountLc);
        Assert.Equal(90m, r.Value.Lines[0].AmountGc);
        Assert.Contains(r.Warnings, w => w.Code == "RATE.FALLBACK_USED");
        // the two rows that were used (USD to DKK, USD to EUR) are locked, the 365 DKK to EUR rows are not
        Assert.Equal(2L, await t.ScalarAsync<long>("select count(*) from core.exchange_rate where tenant_id = @t and first_used_at is not null"));

        // 2026-02-01 is 31 days after the last rate row
        var old = await poster.PostAsync(t.TenantId, t.Context("usd2"), t.Journal(postingDate: "2026-02-01", currency: "USD", amount: 100m));
        Assert.Contains("RATE.RATE_NOT_FOUND", Codes(old));
    }

    [Fact]
    public async Task InverseRateIsDerived()
    {
        var t = await TestTenant.CreateAsync(dkkToEurRate: false);
        // no DKK to EUR row: the derivation takes EUR to DKK instead
        await t.ExecuteAsync("insert into core.exchange_rate (tenant_id, rate_type, from_currency, to_currency, valid_from, rate) " +
                             "select @t, 'SPOT', 'EUR', 'DKK', d::date, 8.0000000000 " +
                             "from generate_series(timestamp '2026-01-01', timestamp '2026-12-31', interval '1 day') d");
        var r = await new JournalDocumentPoster(t.App).PostAsync(t.TenantId, t.Context("inv"), t.Journal(amount: 800m));
        Assert.Equal(CommandStatus.Ok, r.Status);
        Assert.Equal(0.125m, r.Value!.Lines[0].RateGc);
        Assert.Equal(100m, r.Value.Lines[0].AmountGc);
    }

    [Fact]
    public async Task RoundingDifferenceIsReportedNotHidden()
    {
        var t = await TestTenant.CreateAsync();
        // USD 0.03, 0.03 and -0.06 at 0.9 to EUR: 0.027, 0.027 and -0.054 round to 0.03, 0.03 and -0.05
        var req = t.Journal(postingDate: "2026-01-02", currency: "USD") with
        {
            Lines = [new RequestLine("1", t.Bank, 0.03m), new RequestLine("2", t.Expense, 0.03m), new RequestLine("3", t.Revenue, -0.06m)]
        };
        var r = await new JournalDocumentPoster(t.App).PostAsync(t.TenantId, t.Context("rnd"), req);
        Assert.Equal(CommandStatus.Rejected, r.Status);
        Assert.Contains("LIMIT.ROUNDING_DIFFERENCE", Codes(r));
    }

    [Fact]
    public async Task TamperingWithAStoredLineIsDetected()
    {
        var t = await TestTenant.CreateAsync();
        var poster = new JournalDocumentPoster(t.App);
        for (int i = 1; i <= 3; i++) await poster.PostAsync(t.TenantId, t.Context("k" + i), t.Journal(amount: 100m * i));
        Assert.True((await new ChainVerifier(t.App).VerifyLedgerAsync(t.TenantId, t.CompanyId, t.LedgerId)).Ok);

        // The protection triggers stop this for every application role. The migration role can switch one off,
        // which is exactly the attack the hash chain is there to reveal.
        await t.ExecuteAsync("""
            alter table core.journal_line disable trigger journal_line_guard;
            update core.journal_line set amount_lc = amount_lc + 1
             where tenant_id = @t and document_id = (select document_id from core.journal_document where tenant_id = @t and chain_seq = 2) and line_no = 1;
            alter table core.journal_line enable trigger journal_line_guard;
            """);

        var v = await new ChainVerifier(t.App).VerifyLedgerAsync(t.TenantId, t.CompanyId, t.LedgerId);
        Assert.False(v.Ok);
        Assert.Contains(v.Problems, p => p.Code == "CHAIN.ROW_HASH_MISMATCH" && p.ChainSeq == 2);
        Assert.Contains(v.Problems, p => p.Code == "DOCUMENT.NOT_BALANCED" && p.ChainSeq == 2);
    }

    [Fact]
    public async Task PostingRoleCanLockPeriodRowsButNotChangeThem()
    {
        var t = await TestTenant.CreateAsync();
        await using var tx = await TenantTransaction.BeginAsync(t.App, t.TenantId, CommandRole.Post);

        await using (var cmd = tx.Command("select 1 from core.fiscal_period where tenant_id = @t and period_no = 3 for share"))
        {
            cmd.Parameters.AddWithValue("t", t.TenantId);
            Assert.Equal(1, await cmd.ExecuteScalarAsync());
        }
        // the status column has no grant for this role
        await using (var cmd = tx.Command("update core.fiscal_period set status = 'HARD_CLOSED' where tenant_id = @t"))
        {
            cmd.Parameters.AddWithValue("t", t.TenantId);
            var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => cmd.ExecuteNonQueryAsync());
            Assert.Equal("42501", ex.SqlState);
        }
    }

    [Fact]
    public async Task PostingRoleCannotPointAPeriodAtAnotherEvent()
    {
        var t = await TestTenant.CreateAsync();
        await using var tx = await TenantTransaction.BeginAsync(t.App, t.TenantId, CommandRole.Post);
        // the guard trigger of 0006 rejects a last_event_id that is not the matching, latest period_event
        await using var cmd = tx.Command("update core.fiscal_period set last_event_id = gen_random_uuid() where tenant_id = @t and period_no = 3");
        cmd.Parameters.AddWithValue("t", t.TenantId);
        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("23000", ex.SqlState);
    }

    [Fact]
    public async Task ConcurrentPostsGetDistinctNumbersWithoutGaps()
    {
        var t = await TestTenant.CreateAsync();
        if (t.Settings.IsLocalTestDouble) return;   // PGlite serves one connection at a time: real PostgreSQL only

        var poster = new JournalDocumentPoster(t.App);
        var results = await Task.WhenAll(Enumerable.Range(1, 12).Select(i =>
            poster.PostAsync(t.TenantId, t.Context("c" + i), t.Journal(amount: i))));
        Assert.All(results, r => Assert.True(r.Status == CommandStatus.Ok || r.Errors.All(e => e.Retryable)));

        var v = await new ChainVerifier(t.App).VerifyLedgerAsync(t.TenantId, t.CompanyId, t.LedgerId);
        Assert.True(v.Ok, string.Join("; ", v.Problems.Select(p => $"{p.Code} {p.ChainSeq} {p.Detail}")));
        Assert.Equal(results.Count(r => r.Status == CommandStatus.Ok), (int)v.DocumentsChecked);
    }
}
