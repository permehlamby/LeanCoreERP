using FinanceCore.Core.Commands;
using FinanceCore.Core.Posting;

namespace FinanceCore.Data.Tests;

/// <summary>The reversal of documents with open items and its automatic clearing (03, 5.7, B3, 06, 5.3), and chains:verify for the
/// clearing chain (03, 5.5 and 6.1).</summary>
[Collection("db")]
public class OpenItemReversalAndVerifyTests
{
    private static string Describe<T>(CommandResult<T> r) =>
        r.Status + ": " + string.Join(", ", r.Errors.Select(e => $"{e.Code} {e.Path}"));

    private static string[] Codes<T>(CommandResult<T> r) => r.Errors.Select(e => e.Code).ToArray();

    private sealed record Setup(TestTenant Tenant, Guid GlOpen, Guid GlOpen2);

    private static async Task<Setup> CreateAsync()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), s1 = Guid.NewGuid(), s2 = Guid.NewGuid();
        await t.ExecuteAsync(
            "insert into core.gl_account (tenant_id, company_id, account_id, account_no, name, account_type, tax_code_rule, open_item_mode) values " +
            "(@t, @c, @a, '1900', 'Bank clearing', 'ASSET', 'FORBIDDEN', 'GL_OPEN_ITEM'), (@t, @c, @b, '1910', 'Other clearing', 'ASSET', 'FORBIDDEN', 'GL_OPEN_ITEM'); " +
            "insert into core.exchange_rate (tenant_id, rate_type, from_currency, to_currency, valid_from, rate) values " +
            "(@t, 'SPOT', 'USD', 'DKK', date '2026-03-20', 7.2000000000), (@t, 'SPOT', 'USD', 'EUR', date '2026-03-20', 0.9200000000); " +
            "insert into core.number_series (tenant_id, company_id, series_id, series_code, prefix, include_fiscal_year, separator, number_length, is_legal) values " +
            "(@t, @c, @s1, 'GL-CLR', 'CL', true, '-', 6, true), (@t, @c, @s2, 'GL-CLRR', 'CR', true, '-', 6, true); " +
            "insert into core.number_series_assignment (tenant_id, company_id, document_type, series_id, effective_from, created_by) values " +
            "(@t, @c, 'CLEARING', @s1, date '2026-01-01', 'test'), (@t, @c, 'CLEARING_RESET', @s2, date '2026-01-01', 'test'); " +
            "insert into core.number_range_counter (tenant_id, company_id, series_id, fiscal_year, last_number) values (@t, @c, @s1, 2026, 0), (@t, @c, @s2, 2026, 0)",
            ("c", t.CompanyId), ("a", a), ("b", b), ("s1", s1), ("s2", s2));
        return new Setup(t, a, b);
    }

    private static async Task<Guid> PostOpen(Setup s, string key, decimal amount, string date = "2026-03-15", string currency = "DKK", bool second = false)
    {
        var lines = new List<RequestLine> { new("1", s.GlOpen, amount, null, new OpenItemRequest()) };
        if (second) lines.Add(new RequestLine("2", s.GlOpen2, amount * 2, null, new OpenItemRequest { }));
        lines.Add(new RequestLine((lines.Count + 1).ToString(), s.Tenant.Revenue, -lines.Sum(l => l.AmountTc)));
        var req = s.Tenant.Journal(date, currency, amount) with { Lines = lines };
        var r = await new JournalDocumentPoster(s.Tenant.App).PostAsync(s.Tenant.TenantId, s.Tenant.Context(key), req);
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        return r.Value!.DocumentId;
    }

    private static Task<CommandResult<PostedDocument>> Reverse(Setup s, string key, Guid doc) =>
        new DocumentReverser(s.Tenant.App).ReverseAsync(s.Tenant.TenantId, s.Tenant.Context(key, "journal.reverse"),
            new(doc, null, "CORRECTION", "wrong"));

    private static Task<CommandResult<PostedClearing>> Clear(Setup s, string key, Guid a, Guid b, string date = "2026-03-31", decimal? partialA = null) =>
        new ClearingPoster(s.Tenant.App).PostAsync(s.Tenant.TenantId, s.Tenant.Context(key, "clearing.post"),
            new PostClearingRequest(s.Tenant.CompanyId, s.GlOpen, null, DateOnly.Parse(date), "MANUAL", null, null,
                [new ClearingItemRequest(a, 1, partialA), new ClearingItemRequest(b, 1)]));

    private static async Task<string> Text(TestTenant t, string sql, params (string, object)[] p)
    {
        string q = sql.TrimStart().StartsWith("select", StringComparison.Ordinal) ? sql : "select " + sql;
        return await t.ScalarAsync<string>("select coalesce((" + q + ")::text, '<null>')", p);
    }

    private static Task<string> Item(Setup s, Guid doc, string column, int line = 1) => Text(s.Tenant,
        $"{column} from core.journal_line_open_item where tenant_id = @t and document_id = @d and line_no = @n", ("d", doc), ("n", line));

    private static async Task AssertChainsOk(Setup s)
    {
        var l = await new ChainVerifier(s.Tenant.App).VerifyLedgerAsync(s.Tenant.TenantId, s.Tenant.CompanyId, s.Tenant.LedgerId);
        Assert.True(l.Ok, "ledger: " + string.Join("; ", l.Problems.Select(p => p.Code + " " + p.Detail)));
        var c = await new ChainVerifier(s.Tenant.App).VerifyClearingsAsync(s.Tenant.TenantId, s.Tenant.CompanyId);
        Assert.True(c.Ok, "clearing: " + string.Join("; ", c.Problems.Select(p => p.Code + " " + p.Detail)));
    }

    // ---------------------------------------------------------------------------------------------------------
    // The reversal
    // ---------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task TheMirrorOpenItemsAreClearedAgainstTheirOriginalsInTheSameTransaction()
    {
        var s = await CreateAsync();
        Guid doc = await PostOpen(s, "a", 1000m);

        var r = await Reverse(s, "rev", doc);

        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        Guid mirror = r.Value!.DocumentId;
        Assert.Equal("reversed", await Text(s.Tenant, "status from core.journal_document where tenant_id = @t and document_id = @d", ("d", doc)));
        // the mirror line is an open item of its own, and both are CLEARED with the amounts of their lines
        Assert.Equal("CLEARED|1000.0000|134.0000", await Text(s.Tenant,
            "clearing_status || '|' || cleared_amount_tc || '|' || cleared_amount_gc from core.journal_line_open_item where tenant_id = @t and document_id = @d and line_no = 1", ("d", doc)));
        Assert.Equal("CLEARED|-1000.0000|-134.0000", await Text(s.Tenant,
            "clearing_status || '|' || cleared_amount_tc || '|' || cleared_amount_gc from core.journal_line_open_item where tenant_id = @t and document_id = @d and line_no = 1", ("d", mirror)));
        Assert.Equal(0L, await s.Tenant.ScalarAsync<long>("select count(*) from core.journal_line_open_item where tenant_id = @t and document_id = @d and line_no = 2", ("d", mirror)));

        // one clearing of method REVERSAL, no document, items for the original and the mirror line
        Assert.Equal(1L, await s.Tenant.ScalarAsync<long>("select count(*) from core.clearing where tenant_id = @t"));
        Assert.Equal("CLEAR|REVERSAL|journal-documents:reverse|rev#1|<null>|2026-03-15", await Text(s.Tenant,
            "clearing_type || '|' || clearing_method || '|' || command_name || '|' || idempotency_key || '|' || coalesce(document_id::text, '<null>') || '|' || clearing_date from core.clearing where tenant_id = @t"));
        Assert.Equal("1000.0000|-1000.0000", await Text(s.Tenant,
            "string_agg(cleared_amount_tc::text, '|' order by item_no) from core.clearing_item where tenant_id = @t"));
        Assert.Equal(2L, await s.Tenant.ScalarAsync<long>("select count(*) from core.outbox_event where tenant_id = @t and event_type = 'open_item.cleared'"));
        Assert.Equal(1L, await s.Tenant.ScalarAsync<long>("select count(*) from core.outbox_event where tenant_id = @t and event_type = 'clearing.posted'"));
        Assert.Equal(1L, await s.Tenant.ScalarAsync<long>("select count(*) from core.outbox_event where tenant_id = @t and event_type = 'ledger.document_reversed'"));
        await AssertChainsOk(s);

        // a reset of such a clearing is rejected: the originals stay reversed (03, 5.7)
        Guid clearing = Guid.Parse(await Text(s.Tenant, "clearing_id from core.clearing where tenant_id = @t"));
        var reset = await new ClearingPoster(s.Tenant.App).ResetAsync(s.Tenant.TenantId, s.Tenant.Context("reset", "clearing.reset"),
            new ResetClearingRequest(s.Tenant.CompanyId, clearing, new DateOnly(2026, 4, 5), "ERROR", null));
        Assert.Equal(["CLEARING.NOT_RESETTABLE"], Codes(reset));
    }

    [Fact]
    public async Task OneClearingPerAccountOfTheOpenItemLines()
    {
        var s = await CreateAsync();
        Guid doc = await PostOpen(s, "a", 500m, second: true);   // open items on two accounts

        var r = await Reverse(s, "rev", doc);

        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        Assert.Equal(2L, await s.Tenant.ScalarAsync<long>("select count(*) from core.clearing where tenant_id = @t and clearing_method = 'REVERSAL'"));
        Assert.Equal("rev#1|rev#2", await Text(s.Tenant, "string_agg(idempotency_key, '|' order by chain_seq) from core.clearing where tenant_id = @t"));
        Assert.Equal("2|1", await Text(s.Tenant, "(select count(*) from core.clearing_item i where i.tenant_id = @t and i.clearing_id = (select clearing_id from core.clearing where tenant_id = @t and chain_seq = 1))::text || '|' || " +
            "(select count(*) from core.clearing where tenant_id = @t and chain_seq = 2 and account_id <> (select account_id from core.clearing where tenant_id = @t and chain_seq = 1))::text"));
        Assert.Equal(2L, await s.Tenant.ScalarAsync<long>("select last_seq from core.chain_head where tenant_id = @t and chain_type = 'CLEARING'"));
        await AssertChainsOk(s);
    }

    [Fact]
    public async Task ADocumentWithClearedOrPartlyClearedLinesCannotBeReversed()
    {
        var s = await CreateAsync();
        Guid a = await PostOpen(s, "a", 1000m), b = await PostOpen(s, "b", -1000m), c = await PostOpen(s, "c", 1000m), d = await PostOpen(s, "d", -400m);
        Assert.True((await Clear(s, "c1", a, b)).Status == CommandStatus.Ok);
        Assert.True((await Clear(s, "c2", c, d, partialA: 400m)).Status == CommandStatus.Ok);

        var cleared = await Reverse(s, "r1", a);
        Assert.Equal(["REVERSAL.HAS_CLEARED_LINES"], Codes(cleared));
        Assert.Single((int[])cleared.Errors[0].Params!["line_nos"]!);
        Assert.Equal(["REVERSAL.HAS_CLEARED_LINES"], Codes(await Reverse(s, "r2", c)));   // partly cleared
        Assert.Equal("posted", await Text(s.Tenant, "status from core.journal_document where tenant_id = @t and document_id = @d", ("d", a)));
        Assert.Equal(2L, await s.Tenant.ScalarAsync<long>("select count(*) from core.clearing where tenant_id = @t"));
        Assert.Equal(0L, await s.Tenant.ScalarAsync<long>("select count(*) from core.journal_document where tenant_id = @t and document_type = 'REVERSAL'"));

        // after a reset of the clearing the document is reversible
        Guid clearing = Guid.Parse(await Text(s.Tenant, "clearing_id from core.clearing where tenant_id = @t and chain_seq = 1"));
        Assert.True((await new ClearingPoster(s.Tenant.App).ResetAsync(s.Tenant.TenantId, s.Tenant.Context("reset", "clearing.reset"),
            new ResetClearingRequest(s.Tenant.CompanyId, clearing, new DateOnly(2026, 4, 5), "ERROR", null))).Status == CommandStatus.Ok);
        await s.Tenant.SetPeriodStatusAsync(4, "OPEN");
        var ok = await new DocumentReverser(s.Tenant.App).ReverseAsync(s.Tenant.TenantId, s.Tenant.Context("r3", "journal.reverse"),
            new(a, new DateOnly(2026, 4, 6), "CORRECTION", null));
        Assert.True(ok.Status == CommandStatus.Ok, Describe(ok));
        await AssertChainsOk(s);
    }

    [Fact]
    public async Task ARepeatedReversalKeyReturnsTheMirrorAndWritesNoSecondClearing()
    {
        var s = await CreateAsync();
        Guid doc = await PostOpen(s, "a", 1000m);
        var first = await Reverse(s, "same", doc);
        var again = await Reverse(s, "same", doc);
        Assert.True(again.IdempotentReplay, Describe(again));
        Assert.Equal(first.Value!.DocumentId, again.Value!.DocumentId);
        Assert.Equal(1L, await s.Tenant.ScalarAsync<long>("select count(*) from core.clearing where tenant_id = @t"));
    }

    // ---------------------------------------------------------------------------------------------------------
    // chains:verify for the clearing chain
    // ---------------------------------------------------------------------------------------------------------
    /// <summary>A state with every kind of clearing: a clean match, one with a difference document, its reset and a reversal.</summary>
    private static async Task<Setup> FullStateAsync()
    {
        var s = await CreateAsync();
        Guid a = await PostOpen(s, "a", 1000m), b = await PostOpen(s, "b", -1000m);
        Assert.True((await Clear(s, "c1", a, b)).Status == CommandStatus.Ok);
        Guid inv = await PostOpen(s, "inv", 100m, "2026-01-05", "USD"), pay = await PostOpen(s, "pay", -100m, "2026-03-25", "USD");
        var c2 = await Clear(s, "c2", inv, pay);
        Assert.True(c2.Status == CommandStatus.Ok, Describe(c2));
        await s.Tenant.SetPeriodStatusAsync(4, "OPEN");
        var reset = await new ClearingPoster(s.Tenant.App).ResetAsync(s.Tenant.TenantId, s.Tenant.Context("reset", "clearing.reset"),
            new ResetClearingRequest(s.Tenant.CompanyId, c2.Value!.ClearingId, new DateOnly(2026, 4, 5), "CORRECTION", null));
        Assert.True(reset.Status == CommandStatus.Ok, Describe(reset));
        Guid e = await PostOpen(s, "e", 250m);
        Assert.True((await Reverse(s, "rev", e)).Status == CommandStatus.Ok);
        return s;
    }

    [Fact]
    public async Task EveryKindOfClearingVerifies()
    {
        var s = await FullStateAsync();
        var v = await new ChainVerifier(s.Tenant.App).VerifyClearingsAsync(s.Tenant.TenantId, s.Tenant.CompanyId);
        Assert.True(v.Ok, string.Join("; ", v.Problems.Select(p => p.Code + " " + p.Detail)));
        Assert.Equal(4L, v.DocumentsChecked);   // clean match, difference clearing, its reset, the reversal clearing
        await AssertChainsOk(s);
    }

    private static async Task<string[]> Tamper(Setup s, string script)
    {
        await s.Tenant.ExecuteAsync(script);
        var v = await new ChainVerifier(s.Tenant.App).VerifyClearingsAsync(s.Tenant.TenantId, s.Tenant.CompanyId);
        Assert.False(v.Ok);
        return v.Problems.Select(p => p.Code).Distinct().ToArray();
    }

    [Fact]
    public async Task ATamperedItemIsDetectedByTheHashTheBalanceAndTheCache()
    {
        var s = await FullStateAsync();
        string[] codes = await Tamper(s, """
            alter table core.clearing_item disable trigger user;
            update core.clearing_item set cleared_amount_lc = cleared_amount_lc + 1
             where tenant_id = @t and clearing_id = (select clearing_id from core.clearing where tenant_id = @t and chain_seq = 1) and item_no = 1;
            alter table core.clearing_item enable trigger user;
            """);
        Assert.Contains("CHAIN.ROW_HASH_MISMATCH", codes);
        Assert.Contains("CLEARING.NOT_BALANCED", codes);
        Assert.Contains("OPEN_ITEM.CACHE_MISMATCH", codes);
    }

    [Fact]
    public async Task ABrokenLinkOrHeadIsDetected()
    {
        var s = await FullStateAsync();
        string[] codes = await Tamper(s, """
            alter table core.clearing disable trigger user;
            update core.clearing set prev_hash = sha256('x') where tenant_id = @t and chain_seq = 2;
            alter table core.clearing enable trigger user;
            """);
        Assert.Contains("CHAIN.PREV_HASH_MISMATCH", codes);
        Assert.Contains("CHAIN.ROW_HASH_MISMATCH", codes);

        var s2 = await FullStateAsync();
        string[] heads = await Tamper(s2, """
            alter table core.chain_head disable trigger user;
            update core.chain_head set last_hash = sha256('y') where tenant_id = @t and chain_type = 'CLEARING';
            alter table core.chain_head enable trigger user;
            """);
        Assert.Equal(["CHAIN.HEAD_HASH_MISMATCH"], heads);
    }

    [Fact]
    public async Task ATamperedCacheOfAnOpenItemIsDetected()
    {
        var s = await FullStateAsync();
        string[] codes = await Tamper(s, """
            alter table core.journal_line_open_item disable trigger user;
            update core.journal_line_open_item set cleared_amount_tc = 0, cleared_amount_lc = 0, cleared_amount_gc = 0, cleared_amount_rc = 0, clearing_status = 'OPEN'
             where tenant_id = @t and document_id = (select i.open_item_document_id from core.clearing_item i join core.clearing c on c.tenant_id = i.tenant_id and c.clearing_id = i.clearing_id where c.tenant_id = @t and c.chain_seq = 1 and i.item_no = 1) and line_no = 1;
            alter table core.journal_line_open_item enable trigger user;
            """);
        Assert.Equal(["OPEN_ITEM.CACHE_MISMATCH"], codes);
    }

    [Fact]
    public async Task ATamperedDifferenceDocumentBreaksTheLinkedHash()
    {
        var s = await FullStateAsync();
        string[] codes = await Tamper(s, """
            alter table core.journal_document disable trigger user;
            update core.journal_document set row_hash = sha256('z')
             where tenant_id = @t and document_id = (select document_id from core.clearing where tenant_id = @t and chain_seq = 2);
            alter table core.journal_document enable trigger user;
            """);
        Assert.Contains("CHAIN.ROW_HASH_MISMATCH", codes);   // the clearing hash covers the row hash of its document (03, 6)
    }
}
