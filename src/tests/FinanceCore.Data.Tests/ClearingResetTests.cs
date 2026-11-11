using FinanceCore.Core.Commands;
using FinanceCore.Core.Posting;

namespace FinanceCore.Data.Tests;

/// <summary>clearings:reset (03, 5.4, 06, 7.1): the mirrored items, the CLEARING_RESET document that mirrors the difference document,
/// the item list of B2 and the rules of the command.</summary>
[Collection("db")]
public class ClearingResetTests
{
    private static string Describe<T>(CommandResult<T> r) =>
        r.Status + ": " + string.Join(", ", r.Errors.Select(e => $"{e.Code} {e.Path}"));

    private static string[] Codes<T>(CommandResult<T> r) => r.Errors.Select(e => e.Code).ToArray();

    private sealed record Setup(TestTenant Tenant, Guid GlOpen);

    private static async Task<Setup> CreateAsync(bool resetSeries = true)
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid acc = Guid.NewGuid(), s1 = Guid.NewGuid(), s2 = Guid.NewGuid();
        await t.ExecuteAsync(
            "insert into core.gl_account (tenant_id, company_id, account_id, account_no, name, account_type, tax_code_rule, open_item_mode) " +
            "values (@t, @c, @a, '1900', 'Bank clearing', 'ASSET', 'FORBIDDEN', 'GL_OPEN_ITEM'); " +
            "insert into core.exchange_rate (tenant_id, rate_type, from_currency, to_currency, valid_from, rate) values " +
            "(@t, 'SPOT', 'USD', 'DKK', date '2026-03-20', 7.2000000000), (@t, 'SPOT', 'USD', 'EUR', date '2026-03-20', 0.9200000000); " +
            "insert into core.number_series (tenant_id, company_id, series_id, series_code, prefix, include_fiscal_year, separator, number_length, is_legal) " +
            "values (@t, @c, @s1, 'GL-CLR', 'CL', true, '-', 6, true); " +
            "insert into core.number_series_assignment (tenant_id, company_id, document_type, series_id, effective_from, created_by) values (@t, @c, 'CLEARING', @s1, date '2026-01-01', 'test'); " +
            "insert into core.number_range_counter (tenant_id, company_id, series_id, fiscal_year, last_number) values (@t, @c, @s1, 2026, 0)",
            ("c", t.CompanyId), ("a", acc), ("s1", s1));
        if (resetSeries)
            await t.ExecuteAsync(
                "insert into core.number_series (tenant_id, company_id, series_id, series_code, prefix, include_fiscal_year, separator, number_length, is_legal) " +
                "values (@t, @c, @s2, 'GL-CLRR', 'CR', true, '-', 6, true); " +
                "insert into core.number_series_assignment (tenant_id, company_id, document_type, series_id, effective_from, created_by) values (@t, @c, 'CLEARING_RESET', @s2, date '2026-01-01', 'test'); " +
                "insert into core.number_range_counter (tenant_id, company_id, series_id, fiscal_year, last_number) values (@t, @c, @s2, 2026, 0)",
                ("c", t.CompanyId), ("s2", s2));
        return new Setup(t, acc);
    }

    private static async Task<Guid> PostOpen(Setup s, string key, decimal amount, string date = "2026-03-15", string currency = "DKK")
    {
        var req = s.Tenant.Journal(date, currency, amount) with
        {
            Lines = [new RequestLine("1", s.GlOpen, amount, null, new OpenItemRequest()), new RequestLine("2", s.Tenant.Revenue, -amount)]
        };
        var r = await new JournalDocumentPoster(s.Tenant.App).PostAsync(s.Tenant.TenantId, s.Tenant.Context(key), req);
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        return r.Value!.DocumentId;
    }

    private static Task<CommandResult<PostedClearing>> Clear(Setup s, string key, Guid a, Guid b, decimal? partialA = null) =>
        new ClearingPoster(s.Tenant.App).PostAsync(s.Tenant.TenantId, s.Tenant.Context(key, "clearing.post"),
            new PostClearingRequest(s.Tenant.CompanyId, s.GlOpen, null, new DateOnly(2026, 3, 31), "MANUAL", null, null,
                [new ClearingItemRequest(a, 1, partialA), new ClearingItemRequest(b, 1)]));

    private static Task<CommandResult<PostedClearing>> Reset(
        Setup s, string key, Guid clearing, string date = "2026-04-05", string reason = "CORRECTION", string? text = null, params string[] permissions) =>
        new ClearingPoster(s.Tenant.App).ResetAsync(s.Tenant.TenantId, s.Tenant.Context(key, ["clearing.reset", .. permissions]),
            new ResetClearingRequest(s.Tenant.CompanyId, clearing, DateOnly.Parse(date), reason, text));

    private static async Task<string> Text(TestTenant t, string sql, params (string, object)[] p)
    {
        string q = sql.TrimStart().StartsWith("select", StringComparison.Ordinal) ? sql : "select " + sql;
        return await t.ScalarAsync<string>("select coalesce((" + q + ")::text, '<null>')", p);
    }

    private static Task<string> Item(Setup s, Guid doc, string column, int line = 1) => Text(s.Tenant,
        $"{column} from core.journal_line_open_item where tenant_id = @t and document_id = @d and line_no = @n", ("d", doc), ("n", line));

    private static async Task OpenApril(Setup s) => await s.Tenant.SetPeriodStatusAsync(4, "OPEN");

    [Fact]
    public async Task AResetOfACleanMatchMirrorsTheItemsAndPostsNothing()
    {
        var s = await CreateAsync();
        Guid a = await PostOpen(s, "a", 1000m), b = await PostOpen(s, "b", -1000m);
        var c = (await Clear(s, "c1", a, b)).Value!;

        var r = await Reset(s, "r1", c.ClearingId, text: "Wrong payment");

        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        PostedClearing reset = r.Value!;
        Assert.Equal(c.ClearingId, reset.ResetOfClearingId);
        Assert.Equal(2L, reset.ChainSeq);
        Assert.Null(reset.DocumentId);
        Assert.Equal((-1000m, -1000m, -134m, -134m, "OPEN"), (reset.Items[0].AmountTc, reset.Items[0].AmountLc, reset.Items[0].AmountGc, reset.Items[0].AmountRc, reset.Items[0].StatusAfter));
        Assert.Equal((1000m, 134m, "OPEN"), (reset.Items[1].AmountTc, reset.Items[1].AmountGc, reset.Items[1].StatusAfter));
        Assert.Equal("OPEN", await Item(s, a, "clearing_status"));
        Assert.Equal("0.0000", await Item(s, a, "cleared_amount_tc"));
        Assert.Equal(reset.ClearingId.ToString(), (await Item(s, b, "last_clearing_id")).ToLowerInvariant());
        Assert.Equal("RESET|MANUAL|clearings:reset|Wrong payment", await Text(s.Tenant,
            "clearing_type || '|' || clearing_method || '|' || command_name || '|' || note from core.clearing where tenant_id = @t and clearing_id = @c", ("c", reset.ClearingId)));
        // the original is never changed, and a clean match posts no document
        Assert.Equal("CLEAR", await Text(s.Tenant, "clearing_type from core.clearing where tenant_id = @t and clearing_id = @c", ("c", c.ClearingId)));
        Assert.Equal(2L, await s.Tenant.ScalarAsync<long>("select count(*) from core.journal_document where tenant_id = @t"));
        Assert.Equal(c.RowHash, reset.PrevHash);
        Assert.Equal(reset.RowHash, await ClearingDifferenceTests.RecomputeAsync(s.Tenant, reset.ClearingId, null));
        Assert.Equal(2L, await s.Tenant.ScalarAsync<long>("select count(*) from core.outbox_event where tenant_id = @t and event_type = 'open_item.reset'"));
        Assert.Equal(1L, await s.Tenant.ScalarAsync<long>("select count(*) from core.outbox_event where tenant_id = @t and event_type = 'clearing.reset' and is_audit"));
    }

    [Fact]
    public async Task AResetWithADifferenceDocumentFollowsTheItemListOfB2()
    {
        var s = await CreateAsync();
        Guid invoice = await PostOpen(s, "inv", 100m, "2026-01-05", "USD"), payment = await PostOpen(s, "pay", -100m, "2026-03-25", "USD");
        var c = (await Clear(s, "c1", invoice, payment)).Value!;
        Guid d = c.DocumentId!.Value;

        var r = await Reset(s, "r1", c.ClearingId);

        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        PostedClearing reset = r.Value!;
        // D' is the mirror of D: type CLEARING_RESET, its own number, reversal_of_document_id = D with the reason, reversed_line_no on both lines
        Guid mirror = reset.DocumentId!.Value;
        Assert.Equal("CR-2026-000001", reset.DocumentNumber);
        Assert.Equal($"CLEARING_RESET|true|SYSTEM|2026-04-05|{d}|CORRECTION|USD", await Text(s.Tenant,
            "document_type || '|' || is_system_generated || '|' || origin || '|' || posting_date || '|' || reversal_of_document_id || '|' || reversal_reason_code || '|' || transaction_currency " +
            "from core.journal_document where tenant_id = @t and document_id = @d", ("d", mirror)));
        Assert.Equal("FX_REALISED|20.0000|2.0000|2.0000|1", await Text(s.Tenant,
            "line_type || '|' || amount_lc || '|' || amount_gc || '|' || amount_rc || '|' || reversed_line_no from core.journal_line where tenant_id = @t and document_id = @d and line_no = 1", ("d", mirror)));
        Assert.Equal("CLEARING_DIFF|-20.0000|-2.0000|-2.0000|2", await Text(s.Tenant,
            "line_type || '|' || amount_lc || '|' || amount_gc || '|' || amount_rc || '|' || reversed_line_no from core.journal_line where tenant_id = @t and document_id = @d and line_no = 2", ("d", mirror)));
        Assert.Equal("reversed|" + mirror, await Text(s.Tenant,
            "status || '|' || reversed_by_document_id from core.journal_document where tenant_id = @t and document_id = @d", ("d", d)));

        // the items: the mirror of the invoice and the payment, none on D's offset line, one on the offset line of D' (03, 5.4)
        Assert.Equal(3, reset.Items.Count);
        Assert.Equal((invoice, -100m, -700m, -90m, "OPEN"), (reset.Items[0].DocumentId, reset.Items[0].AmountTc, reset.Items[0].AmountLc, reset.Items[0].AmountGc, reset.Items[0].StatusAfter));
        Assert.Equal((payment, 100m, 720m, 92m, "OPEN"), (reset.Items[1].DocumentId, reset.Items[1].AmountTc, reset.Items[1].AmountLc, reset.Items[1].AmountGc, reset.Items[1].StatusAfter));
        Assert.Equal((mirror, 0m, -20m, -2m, -2m, "CLEARED"), (reset.Items[2].DocumentId, reset.Items[2].AmountTc, reset.Items[2].AmountLc, reset.Items[2].AmountGc, reset.Items[2].AmountRc, reset.Items[2].StatusAfter));
        Assert.Equal(0m, reset.Items.Sum(i => i.AmountLc));
        Assert.Equal(0m, reset.Items.Sum(i => i.AmountGc));
        Assert.Equal(0L, await s.Tenant.ScalarAsync<long>(
            "select count(*) from core.clearing_item where tenant_id = @t and clearing_id = @c and open_item_document_id = @d", ("c", reset.ClearingId), ("d", d)));

        // D's offset line stays CLEARED by C, the offset line of D' is cleared by the reset, the invoice and the payment are open again
        Assert.Equal("CLEARED|" + c.ClearingId, await Text(s.Tenant,
            "clearing_status || '|' || last_clearing_id from core.journal_line_open_item where tenant_id = @t and document_id = @d and line_no = 2", ("d", d)));
        Assert.Equal("20.0000", await Item(s, d, "cleared_amount_lc", 2));
        Assert.Equal("CLEARED|" + reset.ClearingId, await Text(s.Tenant,
            "clearing_status || '|' || last_clearing_id from core.journal_line_open_item where tenant_id = @t and document_id = @d and line_no = 2", ("d", mirror)));
        Assert.Equal("OPEN", await Item(s, invoice, "clearing_status"));
        Assert.Equal("OPEN", await Item(s, payment, "clearing_status"));

        // the ledger chain (invoice, payment, D, D'), the clearing chain and the clearing hash with the row hash of D'
        Assert.Equal(4L, await s.Tenant.ScalarAsync<long>("select last_seq from core.chain_head where tenant_id = @t and chain_type = 'LEDGER'"));
        var v = await new ChainVerifier(s.Tenant.App).VerifyLedgerAsync(s.Tenant.TenantId, s.Tenant.CompanyId, s.Tenant.LedgerId);
        Assert.True(v.Ok, string.Join("; ", v.Problems.Select(p => p.Code + " " + p.Detail)));
        string mirrorHash = await Text(s.Tenant, "encode(row_hash, 'hex') from core.journal_document where tenant_id = @t and document_id = @d", ("d", mirror));
        Assert.Equal(reset.RowHash, await ClearingDifferenceTests.RecomputeAsync(s.Tenant, reset.ClearingId, Convert.FromHexString(mirrorHash)));
        Assert.Equal(1L, await s.Tenant.ScalarAsync<long>("select count(*) from core.outbox_event where tenant_id = @t and event_type = 'ledger.document_reversed' and entity_key like '%' || @d::text", ("d", d)));

        // the same two documents clear again, with a new difference document
        await OpenApril(s);
        var again = await Clear(s, "c2", invoice, payment);
        Assert.True(again.Status == CommandStatus.Ok, Describe(again));
        Assert.Equal("CL-2026-000002", again.Value!.DocumentNumber);
        Assert.Equal("CLEARED", await Item(s, invoice, "clearing_status"));

        // a document of a clearing is derived: the reversal command points at the owning command
        var viaReverse = await new DocumentReverser(s.Tenant.App).ReverseAsync(s.Tenant.TenantId, s.Tenant.Context("rev", "journal.reverse"),
            new(c.Items[2].DocumentId, null, "CORRECTION", null));
        Assert.Equal(["REVERSAL.TYPE_NOT_REVERSIBLE"], Codes(viaReverse));
        Assert.Equal("clearings:reset", viaReverse.Errors[0].Params!["owning_command"]);
    }

    [Fact]
    public async Task ResettingOneClearingOfAnItemLeavesTheOtherInPlace()
    {
        var s = await CreateAsync();
        Guid a = await PostOpen(s, "a", 1000m), b = await PostOpen(s, "b", -400m), c = await PostOpen(s, "c", -600m);
        var first = (await Clear(s, "c1", a, b, 400m)).Value!;
        var second = (await Clear(s, "c2", a, c)).Value!;
        Assert.Equal("CLEARED", await Item(s, a, "clearing_status"));

        var r = await Reset(s, "r1", first.ClearingId);

        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        Assert.Equal("PARTIAL", await Item(s, a, "clearing_status"));
        Assert.Equal("600.0000", await Item(s, a, "cleared_amount_tc"));
        Assert.Equal("80.4000", await Item(s, a, "cleared_amount_gc"));
        Assert.Equal("OPEN", await Item(s, b, "clearing_status"));
        Assert.Equal("CLEARED", await Item(s, c, "clearing_status"));
        Assert.Equal(r.Value!.ClearingId.ToString(), (await Item(s, a, "last_clearing_id")).ToLowerInvariant());
        Assert.Equal(second.ChainSeq + 1, r.Value.ChainSeq);
    }

    [Fact]
    public async Task WhatCannotBeResetIsRejectedAndNothingIsWritten()
    {
        var s = await CreateAsync();
        Guid a = await PostOpen(s, "a", 1000m), b = await PostOpen(s, "b", -1000m);
        var c = (await Clear(s, "c1", a, b)).Value!;

        Assert.Equal(["CLEARING.NOT_FOUND"], Codes(await Reset(s, "e1", Guid.NewGuid())));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await Reset(s, "e2", c.ClearingId, date: "2026-03-30")));   // before the clearing
        Assert.Equal(["REVERSAL.REASON_NOT_ALLOWED"], Codes(await Reset(s, "e3", c.ClearingId, reason: "RERUN")));
        Assert.Equal(["REVERSAL.REASON_REQUIRED"], Codes(await Reset(s, "e4", c.ClearingId, reason: "")));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await new ClearingPoster(s.Tenant.App).ResetAsync(s.Tenant.TenantId, s.Tenant.Context("e5"),
            new ResetClearingRequest(s.Tenant.CompanyId, c.ClearingId, new DateOnly(2026, 4, 5), "ERROR", null))));
        Assert.Equal(["PERIOD.NOT_FOUND"], Codes(await Reset(s, "e6", c.ClearingId, date: "2027-02-01")));
        Assert.Equal(1L, await s.Tenant.ScalarAsync<long>("select count(*) from core.clearing where tenant_id = @t"));
        Assert.Equal("CLEARED", await Item(s, a, "clearing_status"));

        var reset = await Reset(s, "ok", c.ClearingId, reason: "ERROR");
        Assert.True(reset.Status == CommandStatus.Ok, Describe(reset));
        Assert.Equal(["CLEARING.ALREADY_RESET"], Codes(await Reset(s, "e7", c.ClearingId)));
        var ofReset = await Reset(s, "e8", reset.Value!.ClearingId);
        Assert.Equal(["CLEARING.NOT_RESETTABLE"], Codes(ofReset));
        Assert.Equal(2L, await s.Tenant.ScalarAsync<long>("select count(*) from core.clearing where tenant_id = @t"));
    }

    [Fact]
    public async Task TheMirrorNeedsItsNumberSeriesAndThePeriodDecides()
    {
        var noSeries = await CreateAsync(resetSeries: false);
        Guid inv = await PostOpen(noSeries, "inv", 100m, "2026-01-05", "USD"), pay = await PostOpen(noSeries, "pay", -100m, "2026-03-25", "USD");
        var c = (await Clear(noSeries, "c1", inv, pay)).Value!;
        Assert.Equal(["SERIES.NO_ASSIGNMENT"], Codes(await Reset(noSeries, "r1", c.ClearingId)));
        Assert.Equal(1L, await noSeries.Tenant.ScalarAsync<long>("select count(*) from core.clearing where tenant_id = @t"));
        Assert.Equal("CLEARED", await Item(noSeries, inv, "clearing_status"));
        Assert.Equal("posted", await Text(noSeries.Tenant, "status from core.journal_document where tenant_id = @t and document_id = @d", ("d", c.DocumentId!.Value)));

        var s = await CreateAsync();
        Guid a = await PostOpen(s, "a", 1000m), b = await PostOpen(s, "b", -1000m);
        var c2 = (await Clear(s, "c1", a, b)).Value!;
        await s.Tenant.SetPeriodStatusAsync(4, "SOFT_CLOSED");
        Assert.Equal(["PERIOD.SOFT_CLOSED_NOT_AUTHORISED"], Codes(await Reset(s, "e1", c2.ClearingId)));
        await s.Tenant.SetPeriodStatusAsync(4, "HARD_CLOSED");
        Assert.Equal(["PERIOD.HARD_CLOSED"], Codes(await Reset(s, "e3", c2.ClearingId, permissions: ["post.soft_closed"])));
        await s.Tenant.SetPeriodStatusAsync(4, "SOFT_CLOSED");
        var ok = await Reset(s, "e2", c2.ClearingId, permissions: ["post.soft_closed"]);
        Assert.True(ok.Status == CommandStatus.Ok, Describe(ok));
        Assert.Contains(ok.Warnings, w => w.Code == "POSTING.SOFT_CLOSE_OVERRIDE");
    }

    [Fact]
    public async Task ARepeatedKeyReturnsTheFirstReset()
    {
        var s = await CreateAsync();
        Guid inv = await PostOpen(s, "inv", 100m, "2026-01-05", "USD"), pay = await PostOpen(s, "pay", -100m, "2026-03-25", "USD");
        var c = (await Clear(s, "c1", inv, pay)).Value!;
        var first = await Reset(s, "same", c.ClearingId);
        var again = await Reset(s, "same", c.ClearingId);
        Assert.True(again.IdempotentReplay, Describe(again));
        Assert.Equal(first.Value!.ClearingId, again.Value!.ClearingId);
        Assert.Equal(first.Value.DocumentId, again.Value.DocumentId);
        Assert.Equal(c.ClearingId, again.Value.ResetOfClearingId);
        Assert.Equal(3, again.Value.Items.Count);
        Assert.Equal(["IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST"], Codes(await Reset(s, "same", c.ClearingId, reason: "ERROR")));
        Assert.Equal(2L, await s.Tenant.ScalarAsync<long>("select count(*) from core.clearing where tenant_id = @t"));
    }
}
