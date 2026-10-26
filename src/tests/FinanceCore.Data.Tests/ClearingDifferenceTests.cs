using FinanceCore.Core.Commands;
using FinanceCore.Core.Hashing;
using FinanceCore.Core.Posting;
using Npgsql;

namespace FinanceCore.Data.Tests;

/// <summary>clearings:post with a difference document for realised FX (03, 3.1 and 5.6): the two lines, the offset open item, the number,
/// the LEDGER chain, the pre-lock and its restart path.</summary>
[Collection("db")]
public class ClearingDifferenceTests
{
    private static string Describe<T>(CommandResult<T> r) =>
        r.Status + ": " + string.Join(", ", r.Errors.Select(e => $"{e.Code} {e.Path}"));

    private static string[] Codes<T>(CommandResult<T> r) => r.Errors.Select(e => e.Code).ToArray();

    private sealed record Setup(TestTenant Tenant, Guid GlOpen);

    private static async Task<Setup> CreateAsync(bool withSeries = true)
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid acc = Guid.NewGuid(), series = Guid.NewGuid();
        await t.ExecuteAsync(
            "insert into core.gl_account (tenant_id, company_id, account_id, account_no, name, account_type, tax_code_rule, open_item_mode) " +
            "values (@t, @c, @a, '1900', 'Bank clearing', 'ASSET', 'FORBIDDEN', 'GL_OPEN_ITEM'); " +
            "insert into core.exchange_rate (tenant_id, rate_type, from_currency, to_currency, valid_from, rate) values " +
            "(@t, 'SPOT', 'USD', 'DKK', date '2026-03-20', 7.2000000000), (@t, 'SPOT', 'USD', 'EUR', date '2026-03-20', 0.9200000000)",
            ("c", t.CompanyId), ("a", acc));
        if (withSeries)
            await t.ExecuteAsync(
                "insert into core.number_series (tenant_id, company_id, series_id, series_code, prefix, include_fiscal_year, separator, number_length, is_legal) " +
                "values (@t, @c, @s, 'GL-CLR', 'CL', true, '-', 6, true); " +
                "insert into core.number_series_assignment (tenant_id, company_id, document_type, series_id, effective_from, created_by) " +
                "values (@t, @c, 'CLEARING', @s, date '2026-01-01', 'test'); " +
                "insert into core.number_range_counter (tenant_id, company_id, series_id, fiscal_year, last_number) values (@t, @c, @s, 2026, 0)",
                ("c", t.CompanyId), ("s", series));
        return new Setup(t, acc);
    }

    private static async Task<Guid> PostOpen(Setup s, string key, decimal amount, string date, string currency)
    {
        var req = s.Tenant.Journal(date, currency, amount) with
        {
            Lines = [new RequestLine("1", s.GlOpen, amount, null, new OpenItemRequest()), new RequestLine("2", s.Tenant.Revenue, -amount)]
        };
        var r = await new JournalDocumentPoster(s.Tenant.App).PostAsync(s.Tenant.TenantId, s.Tenant.Context(key), req);
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        return r.Value!.DocumentId;
    }

    private static async Task<(Guid Invoice, Guid Payment)> UsdPair(Setup s)
    {
        Guid invoice = await PostOpen(s, "inv", 100m, "2026-01-05", "USD");    // LC 700, GC 90, RC 90
        Guid payment = await PostOpen(s, "pay", -100m, "2026-03-25", "USD");   // LC -720, GC -92, RC -92
        return (invoice, payment);
    }

    private static Task<CommandResult<PostedClearing>> Clear(Setup s, string key, Guid a, Guid b, bool disablePreLock = false, string date = "2026-03-31") =>
        new ClearingPoster(s.Tenant.App) { DisablePreLock = disablePreLock }.PostAsync(s.Tenant.TenantId, s.Tenant.Context(key, "clearing.post"),
            new PostClearingRequest(s.Tenant.CompanyId, s.GlOpen, null, DateOnly.Parse(date), "MANUAL", null, null,
                [new ClearingItemRequest(a, 1), new ClearingItemRequest(b, 1)]));

    private static async Task<string> Text(TestTenant t, string sql, params (string, object)[] p)
    {
        string q = sql.TrimStart().StartsWith("select", StringComparison.Ordinal) ? sql : "select " + sql;
        string v = await t.ScalarAsync<string>("select coalesce((" + q + ")::text, '<null>')", p);
        return v;
    }

    [Fact]
    public async Task RealisedFxIsPostedAsADifferenceDocumentWithItsOffsetOpenItem()
    {
        var s = await CreateAsync();
        var (invoice, payment) = await UsdPair(s);

        var r = await Clear(s, "c1", invoice, payment);

        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        PostedClearing c = r.Value!;
        Assert.NotNull(c.DocumentId);
        Assert.Equal("CL-2026-000001", c.DocumentNumber);
        // the two items of the caller and the offset line as the last item, which nets the cleared set to zero (03, 3.1)
        Assert.Equal(3, c.Items.Count);
        Assert.Equal((100m, 700m, 90m, 90m, "CLEARED"), (c.Items[0].AmountTc, c.Items[0].AmountLc, c.Items[0].AmountGc, c.Items[0].AmountRc, c.Items[0].StatusAfter));
        Assert.Equal((-100m, -720m, -92m, -92m), (c.Items[1].AmountTc, c.Items[1].AmountLc, c.Items[1].AmountGc, c.Items[1].AmountRc));
        Assert.Equal((0m, 20m, 2m, 2m, "CLEARED"), (c.Items[2].AmountTc, c.Items[2].AmountLc, c.Items[2].AmountGc, c.Items[2].AmountRc, c.Items[2].StatusAfter));
        Assert.Equal(c.DocumentId, c.Items[2].DocumentId);   // the offset item points at a line of the clearing's own document
        Assert.Equal(0m, c.Items.Sum(i => i.AmountLc));
        Assert.Equal(0m, c.Items.Sum(i => i.AmountGc));

        // the document: type CLEARING, system generated, origin SYSTEM, posting date = clearing date, FX_REALISED first, TC 0 on both lines
        var t = s.Tenant;
        Guid doc = c.DocumentId!.Value;
        Assert.Equal("CLEARING|true|SYSTEM|2026-03-31|USD", await Text(t,
            "select document_type || '|' || is_system_generated || '|' || origin || '|' || posting_date || '|' || transaction_currency from core.journal_document where tenant_id = @t and document_id = @d", ("d", doc)));
        Assert.Equal("FX_REALISED|7920|0.0000|-20.0000|-2.0000|-2.0000|DERIVED", await Text(t,
            "select l.line_type || '|' || a.account_no || '|' || l.amount_tc || '|' || l.amount_lc || '|' || l.amount_gc || '|' || l.amount_rc || '|' || l.rate_type " +
            "from core.journal_line l join core.gl_account a on a.tenant_id = l.tenant_id and a.company_id = l.company_id and a.account_id = l.account_id " +
            "where l.tenant_id = @t and l.document_id = @d and l.line_no = 1", ("d", doc)));
        Assert.Equal("CLEARING_DIFF|1900|0.0000|20.0000|2.0000|2.0000|DERIVED", await Text(t,
            "select l.line_type || '|' || a.account_no || '|' || l.amount_tc || '|' || l.amount_lc || '|' || l.amount_gc || '|' || l.amount_rc || '|' || l.rate_type " +
            "from core.journal_line l join core.gl_account a on a.tenant_id = l.tenant_id and a.company_id = l.company_id and a.account_id = l.account_id " +
            "where l.tenant_id = @t and l.document_id = @d and l.line_no = 2", ("d", doc)));
        Assert.Equal("1|1|1.0000000000", await Text(t,
            "select (rate_lc = 1)::int || '|' || (rate_gc = 1)::int || '|' || rate_rc from core.journal_line where tenant_id = @t and document_id = @d and line_no = 2", ("d", doc)));

        // the offset line is an open item that was created cleared, by this clearing
        Assert.Equal("CLEARED|20.0000|0.0000", await Text(t,
            "select clearing_status || '|' || cleared_amount_lc || '|' || cleared_amount_tc from core.journal_line_open_item where tenant_id = @t and document_id = @d and line_no = 2", ("d", doc)));
        Assert.Equal(c.ClearingId.ToString(), (await Text(t,
            "select last_clearing_id from core.journal_line_open_item where tenant_id = @t and document_id = @d and line_no = 2", ("d", doc))).ToLowerInvariant());
        Assert.Equal(doc.ToString(), (await Text(t, "select document_id from core.clearing where tenant_id = @t and clearing_id = @c", ("c", c.ClearingId))).ToLowerInvariant());
        Assert.Equal("CLEARED", await Text(t, "select clearing_status from core.journal_line_open_item where tenant_id = @t and document_id = @d and line_no = 1", ("d", invoice)));

        // the number, the ledger chain (invoice, payment, difference document) and the clearing chain
        Assert.Equal(1L, await t.ScalarAsync<long>("select last_number from core.number_range_counter where tenant_id = @t and series_id = (select series_id from core.number_series where series_code = 'GL-CLR' and tenant_id = @t)"));
        Assert.Equal(3L, await t.ScalarAsync<long>("select last_seq from core.chain_head where tenant_id = @t and chain_type = 'LEDGER'"));
        var v = await new ChainVerifier(t.App).VerifyLedgerAsync(t.TenantId, t.CompanyId, t.LedgerId);
        Assert.True(v.Ok, string.Join("; ", v.Problems.Select(p => p.Code + " " + p.Detail)));
        Assert.Equal(1L, await t.ScalarAsync<long>("select last_seq from core.chain_head where tenant_id = @t and chain_type = 'CLEARING'"));

        // the clearing hash binds the difference document: recompute it with the row hash of the document (03, 6.1)
        string docHash = await Text(t, "encode(row_hash, 'hex') from core.journal_document where tenant_id = @t and document_id = @d", ("d", doc));
        Assert.Equal(c.RowHash, await RecomputeAsync(t, c.ClearingId, Convert.FromHexString(docHash)));
        Assert.NotEqual(c.RowHash, await RecomputeAsync(t, c.ClearingId, null));

        // events: the document, the clearing and three cleared items
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.outbox_event where tenant_id = @t and event_type = 'ledger.document_posted' and entity_key like '%' || @d::text", ("d", doc)));
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.outbox_event where tenant_id = @t and event_type = 'clearing.posted'"));
        Assert.Equal(3L, await t.ScalarAsync<long>("select count(*) from core.outbox_event where tenant_id = @t and event_type = 'open_item.cleared'"));
    }

    private static async Task<string> RecomputeAsync(TestTenant t, Guid clearingId, byte[]? documentRowHash)
    {
        await using var admin = await t.OpenAdminAsync();
        Dictionary<string, object?> body; long seq; byte[] prev;
        await using (var cmd = new NpgsqlCommand(
            "select chain_seq, prev_hash, clearing_type, clearing_date, fiscal_year, fiscal_period, cleared_at, account_id, partner_id, document_id, reset_of_clearing_id, " +
            "clearing_method, matching_ref, source_system, idempotency_key, command_name, cleared_by, correlation_id from core.clearing where tenant_id = @t and clearing_id = @c", admin))
        {
            cmd.Parameters.AddWithValue("t", t.TenantId); cmd.Parameters.AddWithValue("c", clearingId);
            await using var r = await cmd.ExecuteReaderAsync(); await r.ReadAsync();
            seq = r.GetInt64(0); prev = (byte[])r[1];
            body = new Dictionary<string, object?>
            {
                ["clearing_id"] = clearingId, ["clearing_type"] = r.GetString(2), ["clearing_date"] = r.GetFieldValue<DateOnly>(3), ["fiscal_year"] = r.GetInt32(4),
                ["fiscal_period"] = r.GetInt32(5), ["cleared_at"] = DateTime.SpecifyKind(r.GetDateTime(6), DateTimeKind.Utc), ["account_id"] = r.GetGuid(7),
                ["partner_id"] = r.IsDBNull(8) ? null : r.GetGuid(8), ["document_id"] = r.IsDBNull(9) ? null : r.GetGuid(9),
                ["reset_of_clearing_id"] = r.IsDBNull(10) ? null : r.GetGuid(10), ["clearing_method"] = r.GetString(11),
                ["matching_ref"] = r.IsDBNull(12) ? null : r.GetString(12), ["source_system"] = r.GetString(13), ["idempotency_key"] = r.GetString(14),
                ["command_name"] = r.GetString(15), ["cleared_by"] = r.GetString(16), ["correlation_id"] = r.GetString(17)
            };
        }
        var items = new List<IReadOnlyDictionary<string, object?>>();
        await using (var cmd = new NpgsqlCommand(
            "select item_no, clearing_date, open_item_document_id, open_item_line_no, currency_tc, cleared_amount_tc, cleared_amount_lc, cleared_amount_gc, cleared_amount_rc " +
            "from core.clearing_item where tenant_id = @t and clearing_id = @c order by item_no", admin))
        {
            cmd.Parameters.AddWithValue("t", t.TenantId); cmd.Parameters.AddWithValue("c", clearingId);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                items.Add(new Dictionary<string, object?>
                {
                    ["tenant_id"] = t.TenantId, ["company_id"] = t.CompanyId, ["clearing_id"] = clearingId, ["item_no"] = r.GetInt32(0),
                    ["clearing_date"] = r.GetFieldValue<DateOnly>(1), ["open_item_document_id"] = r.GetGuid(2), ["open_item_line_no"] = r.GetInt32(3),
                    ["currency_tc"] = r.GetString(4), ["cleared_amount_tc"] = r.GetDecimal(5), ["cleared_amount_lc"] = r.GetDecimal(6),
                    ["cleared_amount_gc"] = r.GetDecimal(7), ["cleared_amount_rc"] = r.IsDBNull(8) ? null : r.GetDecimal(8)
                });
        }
        return Canonical.FormatHex32(ClearingHash2.RowHash(t.TenantId, t.CompanyId, seq, prev, body, ClearingHash2.ItemsDigest(items), documentRowHash));
    }

    [Fact]
    public async Task TheRestartPathOfThePreLockLeavesNoTraceOfTheFirstAttempt()
    {
        var s = await CreateAsync();
        var (invoice, payment) = await UsdPair(s);

        // no pre-lock at the first attempt: the difference is found after the locks, the transaction starts again with the pre-lock (03, 5.6)
        var r = await Clear(s, "c1", invoice, payment, disablePreLock: true);

        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        Assert.Equal("CL-2026-000001", r.Value!.DocumentNumber);
        Assert.Equal(1L, await s.Tenant.ScalarAsync<long>("select count(*) from core.journal_document where tenant_id = @t and document_type = 'CLEARING'"));
        Assert.Equal(1L, await s.Tenant.ScalarAsync<long>("select count(*) from core.clearing where tenant_id = @t"));
        Assert.Equal(1L, await s.Tenant.ScalarAsync<long>("select count(*) from core.command_log where tenant_id = @t and command_name = 'clearings:post'"));
    }

    [Fact]
    public async Task APreLockThatTurnsOutToBeUnneededConsumesNoNumber()
    {
        var s = await CreateAsync();
        // two DKK documents that net to zero: a partial amount triggers the pre-lock, but no difference arises
        Guid a = await PostOpen(s, "a", 1000m, "2026-03-15", "DKK"), b = await PostOpen(s, "b", -400m, "2026-03-15", "DKK");
        var r = await new ClearingPoster(s.Tenant.App).PostAsync(s.Tenant.TenantId, s.Tenant.Context("c1", "clearing.post"),
            new PostClearingRequest(s.Tenant.CompanyId, s.GlOpen, null, new DateOnly(2026, 3, 31), "MANUAL", null, null,
                [new ClearingItemRequest(a, 1, 400m), new ClearingItemRequest(b, 1)]));

        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        Assert.Null(r.Value!.DocumentId);
        Assert.Equal(0L, await s.Tenant.ScalarAsync<long>("select count(*) from core.journal_document where tenant_id = @t and document_type = 'CLEARING'"));
        Assert.Equal(0L, await s.Tenant.ScalarAsync<long>("select last_number from core.number_range_counter where tenant_id = @t and series_id = (select series_id from core.number_series where series_code = 'GL-CLR' and tenant_id = @t)"));
        Assert.Equal(2L, await s.Tenant.ScalarAsync<long>("select last_seq from core.chain_head where tenant_id = @t and chain_type = 'LEDGER'"));
    }

    [Fact]
    public async Task ADifferenceThatIsOnlyInGroupCurrencyClearsToo()
    {
        var s = await CreateAsync();
        // DKK 700 and USD -100 net to zero in local currency but not in group and reporting currency (700 x 0.134 = 93.80, 100 x 0.9 = 90.00)
        Guid dkk = await PostOpen(s, "dkk", 700m, "2026-01-05", "DKK"), usd = await PostOpen(s, "usd", -100m, "2026-01-05", "USD");

        var r = await Clear(s, "c1", dkk, usd);

        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        PostedClearing c = r.Value!;
        Assert.Equal(0m, c.Items.Take(2).Sum(i => i.AmountLc));
        Assert.Equal(3.8m, c.Items.Take(2).Sum(i => i.AmountGc));
        Assert.Equal((0m, 0m, -3.8m, -3.8m, "CLEARED"), (c.Items[2].AmountTc, c.Items[2].AmountLc, c.Items[2].AmountGc, c.Items[2].AmountRc, c.Items[2].StatusAfter));
        // the items are in two currencies, so the document is in the local currency (F-IMPL-20)
        Assert.Equal("DKK", await Text(s.Tenant, "transaction_currency from core.journal_document where tenant_id = @t and document_id = @d", ("d", c.DocumentId!.Value)));
        Assert.Equal("CLEARED", await Text(s.Tenant, "clearing_status from core.journal_line_open_item where tenant_id = @t and document_id = @d and line_no = 2", ("d", c.DocumentId.Value)));
        var v = await new ChainVerifier(s.Tenant.App).VerifyLedgerAsync(s.Tenant.TenantId, s.Tenant.CompanyId, s.Tenant.LedgerId);
        Assert.True(v.Ok, string.Join("; ", v.Problems.Select(p => p.Code + " " + p.Detail)));
    }

    [Fact]
    public async Task WhatTheDifferenceDocumentNeedsIsCheckedAndNothingIsWrittenWithoutIt()
    {
        var s = await CreateAsync(withSeries: false);
        var (invoice, payment) = await UsdPair(s);
        var noSeries = await Clear(s, "c1", invoice, payment);
        Assert.Equal(["SERIES.NO_ASSIGNMENT"], Codes(noSeries));

        var s2 = await CreateAsync();
        var (inv2, pay2) = await UsdPair(s2);
        await s2.Tenant.ExecuteAsync("update core.gl_account set posting_blocked = true where tenant_id = @t and account_no = '7920'");
        var blocked = await Clear(s2, "c2", inv2, pay2);
        Assert.Equal(["ACCOUNT.ROLE_ACCOUNT_NOT_USABLE"], Codes(blocked));

        foreach (var (setup, a) in new[] { (s, invoice), (s2, inv2) })
        {
            Assert.Equal(0L, await setup.Tenant.ScalarAsync<long>("select count(*) from core.clearing where tenant_id = @t"));
            Assert.Equal("OPEN", await Text(setup.Tenant, "clearing_status from core.journal_line_open_item where tenant_id = @t and document_id = @d and line_no = 1", ("d", a)));
        }
        Assert.Equal(2L, await s2.Tenant.ScalarAsync<long>("select last_seq from core.chain_head where tenant_id = @t and chain_type = 'LEDGER'"));
        Assert.Equal(0L, await s2.Tenant.ScalarAsync<long>("select last_number from core.number_range_counter where tenant_id = @t and series_id = (select series_id from core.number_series where series_code = 'GL-CLR' and tenant_id = @t)"));
    }

    [Fact]
    public async Task ARepeatedKeyReturnsTheDocumentToo()
    {
        var s = await CreateAsync();
        var (invoice, payment) = await UsdPair(s);
        var first = await Clear(s, "same", invoice, payment);
        var again = await Clear(s, "same", invoice, payment);
        Assert.True(again.IdempotentReplay, Describe(again));
        Assert.Equal(first.Value!.DocumentId, again.Value!.DocumentId);
        Assert.Equal("CL-2026-000001", again.Value.DocumentNumber);
        Assert.Equal(3, again.Value.Items.Count);
        Assert.Equal(1L, await s.Tenant.ScalarAsync<long>("select count(*) from core.journal_document where tenant_id = @t and document_type = 'CLEARING'"));
    }
}
