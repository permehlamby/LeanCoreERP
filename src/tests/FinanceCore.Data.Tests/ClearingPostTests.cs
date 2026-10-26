using FinanceCore.Core.Commands;
using FinanceCore.Core.Hashing;
using FinanceCore.Core.Posting;
using Npgsql;

namespace FinanceCore.Data.Tests;

/// <summary>clearings:post for a clean match (03, 5, 06, 7.1) on GL_OPEN_ITEM accounts.</summary>
[Collection("db")]
public class ClearingPostTests
{
    private static string Describe<T>(CommandResult<T> r) =>
        r.Status + ": " + string.Join(", ", r.Errors.Select(e => $"{e.Code} {e.Path}"));

    private static string[] Codes<T>(CommandResult<T> r) => r.Errors.Select(e => e.Code).ToArray();

    private sealed record Setup(TestTenant Tenant, Guid GlOpen, Guid GlOpen2, Guid Control);

    private static async Task<Setup> CreateAsync()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), k = Guid.NewGuid();
        await t.ExecuteAsync(
            "insert into core.gl_account (tenant_id, company_id, account_id, account_no, name, account_type, tax_code_rule, open_item_mode) values " +
            "(@t, @c, @a, '1900', 'Bank clearing', 'ASSET', 'FORBIDDEN', 'GL_OPEN_ITEM'), (@t, @c, @b, '1910', 'Other clearing', 'ASSET', 'FORBIDDEN', 'GL_OPEN_ITEM'), " +
            "(@t, @c, @k, '1300', 'Receivables', 'ASSET', 'FORBIDDEN', 'CONTROL')",
            ("c", t.CompanyId), ("a", a), ("b", b), ("k", k));
        return new Setup(t, a, b, k);
    }

    private static async Task<Guid> PostOpen(Setup s, string key, decimal amount, Guid? account = null, string date = "2026-03-15", string currency = "DKK")
    {
        var req = s.Tenant.Journal(date, currency, amount) with
        {
            Lines = [new RequestLine("1", account ?? s.GlOpen, amount, null, new OpenItemRequest()), new RequestLine("2", s.Tenant.Revenue, -amount)]
        };
        var r = await new JournalDocumentPoster(s.Tenant.App).PostAsync(s.Tenant.TenantId, s.Tenant.Context(key), req);
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        return r.Value!.DocumentId;
    }

    private static CommandContext Ctx(Setup s, string key, params string[] more) => s.Tenant.Context(key, ["clearing.post", .. more]);

    private static ClearingItemRequest I(Guid doc, decimal? tc = null, int line = 1) => new(doc, line, tc);

    private static Task<CommandResult<PostedClearing>> Clear(
        Setup s, string key, ClearingItemRequest[] items, Guid? account = null, string date = "2026-03-31", string method = "MANUAL",
        Guid? partner = null, string? reference = null, string? note = null, params string[] permissions) =>
        new ClearingPoster(s.Tenant.App).PostAsync(s.Tenant.TenantId, Ctx(s, key, permissions),
            new PostClearingRequest(s.Tenant.CompanyId, account ?? s.GlOpen, partner, DateOnly.Parse(date), method, reference, note, items));

    private static async Task<string> Status(Setup s, Guid doc, int line = 1) => await s.Tenant.ScalarAsync<string>(
        "select clearing_status from core.journal_line_open_item where tenant_id = @t and document_id = @d and line_no = @n", ("d", doc), ("n", line));

    private static Task<string> Cleared(Setup s, Guid doc, string column) => s.Tenant.ScalarAsync<string>(
        $"select {column}::text from core.journal_line_open_item where tenant_id = @t and document_id = @d and line_no = 1", ("d", doc));

    [Fact]
    public async Task AFullMatchClearsBothItemsAndExtendsTheChain()
    {
        var s = await CreateAsync();
        Guid a = await PostOpen(s, "a", 1000m), b = await PostOpen(s, "b", -1000m);

        var r = await Clear(s, "c1", [I(a), I(b)], reference: "BANK-1", note: "March");

        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        PostedClearing c = r.Value!;
        Assert.Equal(1L, c.ChainSeq);
        Assert.Equal((2026, 3), (c.FiscalYear, c.FiscalPeriod));
        Assert.Equal(2, c.Items.Count);
        Assert.Equal((1000m, 1000m, 134m, 134m, "CLEARED"), (c.Items[0].AmountTc, c.Items[0].AmountLc, c.Items[0].AmountGc, c.Items[0].AmountRc, c.Items[0].StatusAfter));
        Assert.Equal((-1000m, -134m, "CLEARED"), (c.Items[1].AmountTc, c.Items[1].AmountGc, c.Items[1].StatusAfter));

        Assert.Equal("CLEARED", await Status(s, a));
        Assert.Equal("CLEARED", await Status(s, b));
        Assert.Equal(c.ClearingId.ToString(), (await Cleared(s, a, "last_clearing_id")).ToLowerInvariant());
        Assert.Equal(1L, await s.Tenant.ScalarAsync<long>("select last_seq from core.chain_head where tenant_id = @t and chain_type = 'CLEARING'"));
        Assert.Equal("March", await s.Tenant.ScalarAsync<string>("select note from core.clearing where tenant_id = @t"));
        // A clean match posts no document and takes no number (03, 3)
        Assert.Equal(2L, await s.Tenant.ScalarAsync<long>("select count(*) from core.journal_document where tenant_id = @t"));
        Assert.Equal(c.Items.Count, (int)await s.Tenant.ScalarAsync<long>("select count(*) from core.clearing_item where tenant_id = @t"));
    }

    [Fact]
    public async Task TheStoredRowsGiveTheHashThatTheCommandWrote()
    {
        var s = await CreateAsync();
        Guid a = await PostOpen(s, "a", 1000m), b = await PostOpen(s, "b", -1000m);
        var first = await Clear(s, "c1", [I(a), I(b)]);
        Guid c = await PostOpen(s, "c", 500m), d = await PostOpen(s, "d", -500m);
        var second = await Clear(s, "c2", [I(c), I(d)], method: "AUTO_MATCH");
        Assert.True(first.Status == CommandStatus.Ok && second.Status == CommandStatus.Ok, Describe(second));
        Assert.Equal(first.Value!.RowHash, second.Value!.PrevHash);

        // recompute every node of the chain from the stored columns (03, 6.1) and compare with the stored hash and link
        var t = s.Tenant;
        await using var admin = await t.OpenAdminAsync();
        var nodes = new List<(Guid Id, long Seq, byte[] Prev, byte[] Hash, Dictionary<string, object?> Body)>();
        await using (var cmd = new NpgsqlCommand(
            "select clearing_id, chain_seq, prev_hash, row_hash, clearing_type, clearing_date, fiscal_year, fiscal_period, cleared_at, account_id, partner_id, " +
            "document_id, reset_of_clearing_id, clearing_method, matching_ref, source_system, idempotency_key, command_name, cleared_by, correlation_id " +
            "from core.clearing where tenant_id = @t order by chain_seq", admin))
        {
            cmd.Parameters.AddWithValue("t", t.TenantId);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                nodes.Add((r.GetGuid(0), r.GetInt64(1), (byte[])r[2], (byte[])r[3], new Dictionary<string, object?>
                {
                    ["clearing_id"] = r.GetGuid(0), ["clearing_type"] = r.GetString(4), ["clearing_date"] = r.GetFieldValue<DateOnly>(5),
                    ["fiscal_year"] = r.GetInt32(6), ["fiscal_period"] = r.GetInt32(7), ["cleared_at"] = DateTime.SpecifyKind(r.GetDateTime(8), DateTimeKind.Utc),
                    ["account_id"] = r.GetGuid(9), ["partner_id"] = r.IsDBNull(10) ? null : r.GetGuid(10), ["document_id"] = r.IsDBNull(11) ? null : r.GetGuid(11),
                    ["reset_of_clearing_id"] = r.IsDBNull(12) ? null : r.GetGuid(12), ["clearing_method"] = r.GetString(13),
                    ["matching_ref"] = r.IsDBNull(14) ? null : r.GetString(14), ["source_system"] = r.GetString(15), ["idempotency_key"] = r.GetString(16),
                    ["command_name"] = r.GetString(17), ["cleared_by"] = r.GetString(18), ["correlation_id"] = r.GetString(19)
                }));
        }
        byte[] prev = LedgerHash2.Genesis(t.TenantId, t.CompanyId, ClearingHash2.ChainType, "");
        foreach (var n in nodes)
        {
            var items = new List<IReadOnlyDictionary<string, object?>>();
            await using (var cmd = new NpgsqlCommand(
                "select item_no, clearing_date, open_item_document_id, open_item_line_no, currency_tc, cleared_amount_tc, cleared_amount_lc, cleared_amount_gc, " +
                "cleared_amount_rc from core.clearing_item where tenant_id = @t and clearing_id = @c order by item_no", admin))
            {
                cmd.Parameters.AddWithValue("t", t.TenantId);
                cmd.Parameters.AddWithValue("c", n.Id);
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                    items.Add(new Dictionary<string, object?>
                    {
                        ["tenant_id"] = t.TenantId, ["company_id"] = t.CompanyId, ["clearing_id"] = n.Id, ["item_no"] = r.GetInt32(0),
                        ["clearing_date"] = r.GetFieldValue<DateOnly>(1), ["open_item_document_id"] = r.GetGuid(2), ["open_item_line_no"] = r.GetInt32(3),
                        ["currency_tc"] = r.GetString(4), ["cleared_amount_tc"] = r.GetDecimal(5), ["cleared_amount_lc"] = r.GetDecimal(6),
                        ["cleared_amount_gc"] = r.GetDecimal(7), ["cleared_amount_rc"] = r.IsDBNull(8) ? null : r.GetDecimal(8)
                    });
            }
            Assert.Equal(Convert.ToHexString(prev), Convert.ToHexString(n.Prev));
            byte[] expected = ClearingHash2.RowHash(t.TenantId, t.CompanyId, n.Seq, prev, n.Body, ClearingHash2.ItemsDigest(items), null);
            Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(n.Hash));
            prev = n.Hash;
        }
        Assert.Equal(2, nodes.Count);
    }

    [Fact]
    public async Task APartialClearingDerivesTheOtherAmountsAndLeavesTheRestOpen()
    {
        var s = await CreateAsync();
        Guid a = await PostOpen(s, "a", 1000m), b = await PostOpen(s, "b", -333.33m), c = await PostOpen(s, "c", -666.67m);

        var first = await Clear(s, "c1", [I(a, 333.33m), I(b)]);
        Assert.True(first.Status == CommandStatus.Ok, Describe(first));
        Assert.Equal((333.33m, 333.33m, 44.67m, 44.67m, "PARTIAL"), (first.Value!.Items[0].AmountTc, first.Value.Items[0].AmountLc, first.Value.Items[0].AmountGc,
            first.Value.Items[0].AmountRc, first.Value.Items[0].StatusAfter));
        Assert.Equal("PARTIAL", await Status(s, a));
        Assert.Equal("333.3300", await Cleared(s, a, "cleared_amount_tc"));

        // no amount: the full remaining amount, so the two clearings of A add up to A exactly
        var second = await Clear(s, "c2", [I(a), I(c)]);
        Assert.True(second.Status == CommandStatus.Ok, Describe(second));
        Assert.Equal((666.67m, 89.33m, "CLEARED"), (second.Value!.Items[0].AmountTc, second.Value.Items[0].AmountGc, second.Value.Items[0].StatusAfter));
        Assert.Equal("1000.0000", await Cleared(s, a, "cleared_amount_tc"));
        Assert.Equal("134.0000", await Cleared(s, a, "cleared_amount_gc"));
        Assert.Equal(2L, second.Value.ChainSeq);
        Assert.Equal(second.Value.ClearingId.ToString(), (await Cleared(s, a, "last_clearing_id")).ToLowerInvariant());
    }

    [Fact]
    public async Task WhatIsNotAnOpenItemOrNotAllowedIsRejectedAndNothingIsWritten()
    {
        var s = await CreateAsync();
        Guid a = await PostOpen(s, "a", 1000m), b = await PostOpen(s, "b", -1000m), c = await PostOpen(s, "c", -400m);
        Guid other = await PostOpen(s, "o", -1000m, s.GlOpen2);
        async Task<string[]> Try(string key, ClearingItemRequest[] items, Guid? account = null) => Codes(await Clear(s, key, items, account));

        Assert.Equal(["CLEARING.NOT_AN_OPEN_ITEM"], await Try("e1", [I(a), I(Guid.NewGuid())]));
        Assert.Equal(["CLEARING.NOT_AN_OPEN_ITEM"], await Try("e2", [I(a), I(b, line: 2)]));   // line 2 is revenue, no open item
        Assert.Equal(["CLEARING.ITEMS_NOT_SAME_ACCOUNT"], await Try("e3", [I(a), I(other)]));
        Assert.Equal(["CLEARING.OVER_CLEARING"], await Try("e4", [I(a, 1500m), I(b)]));
        Assert.Equal(["CLEARING.OVER_CLEARING"], await Try("e5", [I(a, -10m), I(b)]));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], await Try("e6", [I(a, 0m), I(b)]));
        Assert.Equal(["CLEARING.TC_DIFFERENCE"], await Try("e7", [I(a), I(c)]));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], await Try("e8", [I(a)]));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], await Try("e9", [I(a), I(a)]));
        Assert.Equal(["CLEARING.ITEMS_NOT_SAME_ACCOUNT", "CLEARING.ITEMS_NOT_SAME_ACCOUNT"], await Try("e10", [I(a), I(b)], s.GlOpen2));

        var empty = await Clear(s, "e11", [I(a), I(b)], s.Tenant.Bank);   // an account that is not open-item managed
        Assert.Contains("CLEARING.NOT_AN_OPEN_ITEM", Codes(empty));
        Assert.Equal(["ACCOUNT_RULE.PARTNER_FORBIDDEN"], Codes(await Clear(s, "e12", [I(a), I(b)], partner: Guid.NewGuid())));
        Assert.Equal(["ACCOUNT_RULE.PARTNER_REQUIRED"], Codes(await Clear(s, "e13", [I(a), I(b)], s.Control)));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await Clear(s, "e14", [I(a), I(b)], method: "REVERSAL")));

        Assert.Equal(0L, await s.Tenant.ScalarAsync<long>("select count(*) from core.clearing where tenant_id = @t"));
        Assert.Equal("OPEN", await Status(s, a));
        Assert.Equal(0L, await s.Tenant.ScalarAsync<long>("select count(*) from core.command_log where tenant_id = @t and command_name = 'clearings:post'"));

        // an item that is already cleared in full
        Assert.True((await Clear(s, "ok", [I(a), I(b)])).Status == CommandStatus.Ok);
        Guid d = await PostOpen(s, "d", -1000m);
        Assert.Equal(["CONFLICT.ITEM_ALREADY_CLEARED"], await Try("e15", [I(a), I(d)]));
    }

    [Fact]
    public async Task ASetThatNeedsADifferenceDocumentNeedsTheNumberSeriesOfIt()
    {
        var s = await CreateAsync();
        await s.Tenant.ExecuteAsync(
            "insert into core.exchange_rate (tenant_id, rate_type, from_currency, to_currency, valid_from, rate) values " +
            "(@t, 'SPOT', 'USD', 'DKK', date '2026-03-20', 7.2000000000), (@t, 'SPOT', 'USD', 'EUR', date '2026-03-20', 0.9200000000)");
        Guid invoice = await PostOpen(s, "inv", 100m, currency: "USD", date: "2026-01-05");     // LC 700
        Guid payment = await PostOpen(s, "pay", -100m, currency: "USD", date: "2026-03-25");    // LC -720

        // TC nets to zero, LC does not: realised FX (03, 3.1). This tenant has no number series for the document type CLEARING,
        // so the difference document cannot be posted and nothing is written. ClearingDifferenceTests has the case with a series.
        var r = await Clear(s, "c1", [I(invoice), I(payment)]);
        Assert.Equal(["SERIES.NO_ASSIGNMENT"], Codes(r));
        Assert.Equal("OPEN", await Status(s, invoice));
        Assert.Equal(0L, await s.Tenant.ScalarAsync<long>("select count(*) from core.clearing where tenant_id = @t"));
    }

    [Fact]
    public async Task ThePeriodOfTheClearingDateDecides()
    {
        var s = await CreateAsync();
        Guid a = await PostOpen(s, "a", 1000m), b = await PostOpen(s, "b", -1000m);

        Assert.Equal(["PERIOD.NOT_FOUND"], Codes(await Clear(s, "e1", [I(a), I(b)], date: "2027-01-15")));

        await s.Tenant.SetPeriodStatusAsync(3, "SOFT_CLOSED");
        var denied = await Clear(s, "e2", [I(a), I(b)]);
        Assert.Equal(["PERIOD.SOFT_CLOSED_NOT_AUTHORISED"], Codes(denied));
        Assert.Equal("clearing_date", denied.Errors[0].Path);

        await s.Tenant.SetPeriodStatusAsync(3, "HARD_CLOSED");
        Assert.Equal(["PERIOD.HARD_CLOSED"], Codes(await Clear(s, "e3", [I(a), I(b)], permissions: ["post.soft_closed"])));
        Assert.Equal("OPEN", await Status(s, a));

        // the items themselves may lie in a hard-closed period: only the clearing date counts (03, 5.1)
        await s.Tenant.SetPeriodStatusAsync(3, "OPEN");
        await s.Tenant.SetPeriodStatusAsync(4, "OPEN");
        var ok = await Clear(s, "ok", [I(a), I(b)], date: "2026-04-10");
        Assert.True(ok.Status == CommandStatus.Ok, Describe(ok));
        Assert.Equal(4, ok.Value!.FiscalPeriod);
    }

    [Fact]
    public async Task ARepeatedKeyReturnsTheFirstClearingAndAnotherRequestIsRejected()
    {
        var s = await CreateAsync();
        Guid a = await PostOpen(s, "a", 1000m), b = await PostOpen(s, "b", -1000m), c = await PostOpen(s, "c", -1000m);

        var first = await Clear(s, "same", [I(a), I(b)]);
        var again = await Clear(s, "same", [I(a), I(b)]);
        Assert.True(again.Status == CommandStatus.Ok && again.IdempotentReplay, Describe(again));
        Assert.Equal(first.Value!.ClearingId, again.Value!.ClearingId);
        Assert.Equal(first.Value.RowHash, again.Value.RowHash);
        Assert.Equal(2, again.Value.Items.Count);
        Assert.Equal(1L, await s.Tenant.ScalarAsync<long>("select count(*) from core.clearing where tenant_id = @t"));

        Assert.Equal(["IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST"], Codes(await Clear(s, "same", [I(a), I(c)])));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await new ClearingPoster(s.Tenant.App).PostAsync(s.Tenant.TenantId, s.Tenant.Context("p"),
            new PostClearingRequest(s.Tenant.CompanyId, s.GlOpen, null, new DateOnly(2026, 3, 31), "MANUAL", null, null, [I(a), I(b)]))));
    }

    [Fact]
    public async Task TheEventsSayWhatWasCleared()
    {
        var s = await CreateAsync();
        Guid a = await PostOpen(s, "a", 1000m), b = await PostOpen(s, "b", -1000m);
        var r = await Clear(s, "c1", [I(a), I(b)]);
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));

        Assert.Equal(1L, await s.Tenant.ScalarAsync<long>("select count(*) from core.outbox_event where tenant_id = @t and event_type = 'clearing.posted' and is_audit"));
        Assert.Equal(2L, await s.Tenant.ScalarAsync<long>("select count(*) from core.outbox_event where tenant_id = @t and event_type = 'open_item.cleared' and not is_audit"));
        string chainRef = await s.Tenant.ScalarAsync<string>("select chain_ref::text from core.outbox_event where tenant_id = @t and event_type = 'clearing.posted'");
        Assert.Contains("\"CLEARING\"", chainRef);
        Assert.Contains(r.Value!.RowHash, chainRef);
    }
}
