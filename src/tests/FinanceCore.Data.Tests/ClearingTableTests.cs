using FinanceCore.Core.Commands;
using FinanceCore.Core.Hashing;
using FinanceCore.Core.Posting;
using Npgsql;

namespace FinanceCore.Data.Tests;

/// <summary>
/// The clearing tables, the chain and the cache rule of the open item S columns (03, 4 and 6, migration 0015). No command writes
/// them yet, so these tests write as the clearing role would: one transaction with the clearing, its items, the cache update and
/// the chain head. The database checks run at commit.
/// </summary>
[Collection("db")]
public class ClearingTableTests
{
    private static string Describe<T>(CommandResult<T> r) =>
        r.Status + ": " + string.Join(", ", r.Errors.Select(e => $"{e.Code} {e.Path}"));

    private sealed record Setup(TestTenant Tenant, Guid GlOpen, Guid GlOpen2);
    private sealed record Ln(Guid Doc, int No, string Currency, decimal Tc, decimal Lc, decimal Gc, decimal? Rc, Guid Account);
    private sealed record It(Ln Line, decimal Tc, decimal Lc, decimal Gc, decimal? Rc);

    private static async Task<Setup> CreateAsync()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        await t.ExecuteAsync(
            "insert into core.gl_account (tenant_id, company_id, account_id, account_no, name, account_type, tax_code_rule, open_item_mode) values " +
            "(@t, @c, @a, '1900', 'Bank clearing', 'ASSET', 'FORBIDDEN', 'GL_OPEN_ITEM'), (@t, @c, @b, '1910', 'Other clearing', 'ASSET', 'FORBIDDEN', 'GL_OPEN_ITEM')",
            ("c", t.CompanyId), ("a", a), ("b", b));
        return new Setup(t, a, b);
    }

    /// <summary>Posts a document with one open item line (line 1) on the account, and returns that line as stored.</summary>
    private static async Task<Ln> PostOpen(Setup s, string key, decimal amount, Guid? account = null)
    {
        var req = s.Tenant.Journal(amount: amount) with
        {
            Lines = [new RequestLine("1", account ?? s.GlOpen, amount, null, new OpenItemRequest()), new RequestLine("2", s.Tenant.Revenue, -amount)]
        };
        var r = await new JournalDocumentPoster(s.Tenant.App).PostAsync(s.Tenant.TenantId, s.Tenant.Context(key), req);
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        await using var admin = await s.Tenant.OpenAdminAsync();
        await using var cmd = new NpgsqlCommand(
            "select l.amount_tc, l.amount_lc, l.amount_gc, l.amount_rc, d.transaction_currency, l.account_id from core.journal_line l " +
            "join core.journal_document d on d.tenant_id = l.tenant_id and d.company_id = l.company_id and d.document_id = l.document_id " +
            "where l.tenant_id = @t and l.document_id = @d and l.line_no = 1", admin);
        cmd.Parameters.AddWithValue("t", s.Tenant.TenantId);
        cmd.Parameters.AddWithValue("d", r.Value!.DocumentId);
        await using var rd = await cmd.ExecuteReaderAsync();
        await rd.ReadAsync();
        return new Ln(r.Value.DocumentId, 1, rd.GetString(4), rd.GetDecimal(0), rd.GetDecimal(1), rd.GetDecimal(2),
            rd.IsDBNull(3) ? null : rd.GetDecimal(3), rd.GetGuid(5));
    }

    private static It Full(Ln l) => new(l, l.Tc, l.Lc, l.Gc, l.Rc);
    private static decimal R(decimal v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);
    private static It Part(Ln l, decimal share) => new(l, R(l.Tc * share), R(l.Lc * share), R(l.Gc * share), l.Rc is null ? null : R(l.Rc.Value * share));
    private static It Mirror(It i) => new(i.Line, -i.Tc, -i.Lc, -i.Gc, -i.Rc);

    private sealed record Written(Guid ClearingId, long ChainSeq, byte[] RowHash);

    /// <summary>One transaction as the clearing role would run it. The deferred checks of the database run at the commit.</summary>
    private static async Task<Written> Clear(
        Setup s, string key, It[] items, Guid account, string type = "CLEAR", Guid? resetOf = null, byte[]? prevOverride = null,
        bool updateCache = true, bool moveHead = true, string role = "cmd_clearing", string? statusOverride = null)
    {
        var t = s.Tenant;
        await using NpgsqlConnection c = await t.OpenAdminAsync();
        await using NpgsqlTransaction tx = await c.BeginTransactionAsync();

        async Task Exec(string sql, params (string, object?)[] p)
        {
            await using var cmd = new NpgsqlCommand(sql, c, tx);
            cmd.Parameters.AddWithValue("t", t.TenantId);
            foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }

        await Exec("select set_config('app.tenant_id', @t::text, true)");
        await Exec($"set local role {role}");

        long headSeq; byte[] headHash;
        await using (var cmd = new NpgsqlCommand(
            "select last_seq, last_hash from core.chain_head where tenant_id = @t and company_id = @c and chain_type = 'CLEARING' and scope_key = ''", c, tx))
        {
            cmd.Parameters.AddWithValue("t", t.TenantId);
            cmd.Parameters.AddWithValue("c", t.CompanyId);
            await using var rd = await cmd.ExecuteReaderAsync();
            await rd.ReadAsync();
            headSeq = rd.GetInt64(0); headHash = (byte[])rd[1];
        }
        long seq = headSeq + 1;
        byte[] prev = prevOverride ?? headHash;
        Guid id = Guid.NewGuid();
        var date = new DateOnly(2026, 3, 15);
        DateTime now = DateTime.UtcNow; var at = new DateTime(now.Ticks - now.Ticks % 10, DateTimeKind.Utc);
        string command = type == "CLEAR" ? "clearings:post" : "clearings:reset";

        var body = new Dictionary<string, object?>
        {
            ["clearing_id"] = id, ["clearing_type"] = type, ["clearing_date"] = date, ["fiscal_year"] = 2026, ["fiscal_period"] = 3,
            ["cleared_at"] = at, ["account_id"] = account, ["partner_id"] = null, ["document_id"] = null, ["reset_of_clearing_id"] = resetOf,
            ["clearing_method"] = "MANUAL", ["matching_ref"] = null, ["source_system"] = "TEST", ["idempotency_key"] = key,
            ["command_name"] = command, ["cleared_by"] = "tester", ["correlation_id"] = "corr-" + key
        };
        var itemRows = items.Select((i, n) => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["tenant_id"] = t.TenantId, ["company_id"] = t.CompanyId, ["clearing_id"] = id, ["item_no"] = n + 1, ["clearing_date"] = date,
            ["open_item_document_id"] = i.Line.Doc, ["open_item_line_no"] = i.Line.No, ["currency_tc"] = i.Line.Currency,
            ["cleared_amount_tc"] = i.Tc, ["cleared_amount_lc"] = i.Lc, ["cleared_amount_gc"] = i.Gc, ["cleared_amount_rc"] = i.Rc
        }).ToList();
        byte[] rowHash = ClearingHash2.RowHash(t.TenantId, t.CompanyId, seq, prev, body, ClearingHash2.ItemsDigest(itemRows), null);

        await Exec(
            "insert into core.clearing (tenant_id, company_id, clearing_id, chain_seq, clearing_type, clearing_date, fiscal_year, fiscal_period, " +
            "cleared_at, account_id, reset_of_clearing_id, clearing_method, source_system, idempotency_key, command_name, cleared_by, correlation_id, " +
            "prev_hash, row_hash, hash_version) values (@t, @c, @id, @seq, @type, @date, 2026, 3, @at, @acc, @reset, 'MANUAL', 'TEST', @key, @cmd, " +
            "'tester', @corr, @prev, @hash, 2)",
            ("c", t.CompanyId), ("id", id), ("seq", seq), ("type", type), ("date", date), ("at", at), ("acc", account), ("reset", resetOf),
            ("key", key), ("cmd", command), ("corr", "corr-" + key), ("prev", prev), ("hash", rowHash));
        for (int n = 0; n < items.Length; n++)
        {
            It i = items[n];
            await Exec(
                "insert into core.clearing_item (tenant_id, company_id, clearing_id, item_no, clearing_date, open_item_document_id, open_item_line_no, " +
                "currency_tc, cleared_amount_tc, cleared_amount_lc, cleared_amount_gc, cleared_amount_rc) " +
                "values (@t, @c, @id, @n, @date, @doc, @line, @ccy, @tc, @lc, @gc, @rc)",
                ("c", t.CompanyId), ("id", id), ("n", n + 1), ("date", date), ("doc", i.Line.Doc), ("line", i.Line.No), ("ccy", i.Line.Currency),
                ("tc", i.Tc), ("lc", i.Lc), ("gc", i.Gc), ("rc", i.Rc));
        }

        if (updateCache)
            foreach (var g in items.GroupBy(i => (i.Line.Doc, i.Line.No)))
            {
                Ln l = g.First().Line;
                decimal tc = 0, lc = 0, gc = 0, rc = 0;
                await using (var cmd = new NpgsqlCommand(
                    "select cleared_amount_tc, cleared_amount_lc, cleared_amount_gc, coalesce(cleared_amount_rc, 0) from core.journal_line_open_item " +
                    "where tenant_id = @t and document_id = @d and line_no = @n", c, tx))
                {
                    cmd.Parameters.AddWithValue("t", t.TenantId); cmd.Parameters.AddWithValue("d", l.Doc); cmd.Parameters.AddWithValue("n", l.No);
                    await using var rd = await cmd.ExecuteReaderAsync();
                    await rd.ReadAsync();
                    tc = rd.GetDecimal(0); lc = rd.GetDecimal(1); gc = rd.GetDecimal(2); rc = rd.GetDecimal(3);
                }
                tc += g.Sum(i => i.Tc); lc += g.Sum(i => i.Lc); gc += g.Sum(i => i.Gc); rc += g.Sum(i => i.Rc ?? 0m);
                string status = statusOverride ?? (tc == 0 && lc == 0 ? "OPEN" : tc == l.Tc && lc == l.Lc ? "CLEARED" : "PARTIAL");
                await Exec(
                    "update core.journal_line_open_item set clearing_status = @s, cleared_amount_tc = @tc, cleared_amount_lc = @lc, cleared_amount_gc = @gc, " +
                    "cleared_amount_rc = case when @hasrc then @rc else null end, last_clearing_id = @id where tenant_id = @t and document_id = @d and line_no = @n",
                    ("s", status), ("tc", tc), ("lc", lc), ("gc", gc), ("rc", rc), ("hasrc", l.Rc is not null), ("id", id), ("d", l.Doc), ("n", l.No));
            }

        if (moveHead)
            await Exec(
                "update core.chain_head set last_seq = @seq, last_hash = @h, hash_version = 2, updated_at = now() " +
                "where tenant_id = @t and company_id = @c and chain_type = 'CLEARING' and scope_key = ''",
                ("c", t.CompanyId), ("seq", seq), ("h", rowHash));

        await tx.CommitAsync();
        return new Written(id, seq, rowHash);
    }

    private static async Task<string> Fail(Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(action);
        return ex.MessageText;
    }

    private static async Task<string> Status(Setup s, Ln l) => await s.Tenant.ScalarAsync<string>(
        "select clearing_status from core.journal_line_open_item where tenant_id = @t and document_id = @d and line_no = 1", ("d", l.Doc));

    private static async Task<string> Dec(Setup s, Ln l, string column) => await s.Tenant.ScalarAsync<string>(
        $"select {column}::text from core.journal_line_open_item where tenant_id = @t and document_id = @d and line_no = 1", ("d", l.Doc));

    [Fact]
    public async Task AFullClearingCommitsWithItsCacheAndItsChainNode()
    {
        var s = await CreateAsync();
        Ln a = await PostOpen(s, "a", 1000m), b = await PostOpen(s, "b", -1000m);

        var w = await Clear(s, "c1", [Full(a), Full(b)], s.GlOpen);

        Assert.Equal(1L, w.ChainSeq);
        Assert.Equal("CLEARED", await Status(s, a));
        Assert.Equal("CLEARED", await Status(s, b));
        Assert.Equal("1000.0000", await Dec(s, a, "cleared_amount_tc"));
        Assert.Equal("-134.0000", await Dec(s, b, "cleared_amount_gc"));
        Assert.Equal(w.ClearingId, await s.Tenant.ScalarAsync<Guid>("select last_clearing_id from core.journal_line_open_item where tenant_id = @t and document_id = @d and line_no = 1", ("d", a.Doc)));
        Assert.Equal(1L, await s.Tenant.ScalarAsync<long>("select last_seq from core.chain_head where tenant_id = @t and chain_type = 'CLEARING'"));
        Assert.Equal(2L, await s.Tenant.ScalarAsync<long>("select count(*) from core.clearing_item where tenant_id = @t and clearing_id = @c", ("c", w.ClearingId)));
        // the ledger chain is untouched: a clean match posts no document (03, 3)
        Assert.Equal(2L, await s.Tenant.ScalarAsync<long>("select count(*) from core.journal_document where tenant_id = @t"));
    }

    [Fact]
    public async Task APartialClearingLeavesTheRestOpen()
    {
        var s = await CreateAsync();
        Ln a = await PostOpen(s, "a", 1000m), b = await PostOpen(s, "b", -400m);

        await Clear(s, "c1", [Part(a, 0.4m), Full(b)], s.GlOpen);

        Assert.Equal("PARTIAL", await Status(s, a));
        Assert.Equal("CLEARED", await Status(s, b));
        Assert.Equal("400.0000", await Dec(s, a, "cleared_amount_tc"));
        Assert.Equal("53.6000", await Dec(s, a, "cleared_amount_gc"));

        // the rest of A clears against another document later
        Ln c = await PostOpen(s, "c", -600m);
        await Clear(s, "c2", [Part(a, 0.6m), Full(c)], s.GlOpen);
        Assert.Equal("CLEARED", await Status(s, a));
        Assert.Equal("1000.0000", await Dec(s, a, "cleared_amount_tc"));
        Assert.Equal(2L, await s.Tenant.ScalarAsync<long>("select last_seq from core.chain_head where tenant_id = @t and chain_type = 'CLEARING'"));
    }

    [Fact]
    public async Task AResetMirrorsTheItemsAndReopensThem()
    {
        var s = await CreateAsync();
        Ln a = await PostOpen(s, "a", 1000m), b = await PostOpen(s, "b", -1000m);
        var c1 = await Clear(s, "c1", [Full(a), Full(b)], s.GlOpen);

        var reset = await Clear(s, "r1", [Mirror(Full(a)), Mirror(Full(b))], s.GlOpen, "RESET", c1.ClearingId);

        Assert.Equal(2L, reset.ChainSeq);
        Assert.Equal("OPEN", await Status(s, a));
        Assert.Equal("OPEN", await Status(s, b));
        Assert.Equal("0.0000", await Dec(s, a, "cleared_amount_tc"));
        Assert.Equal(reset.ClearingId, await s.Tenant.ScalarAsync<Guid>("select last_clearing_id from core.journal_line_open_item where tenant_id = @t and document_id = @d and line_no = 1", ("d", a.Doc)));

        // a clearing is reset at most once, and a reset is not reset (a reset undoes a CLEAR)
        Assert.Contains("duplicate key", await Fail(() => Clear(s, "r2", [Mirror(Full(a)), Mirror(Full(b))], s.GlOpen, "RESET", c1.ClearingId)));
        Assert.Contains("a reset undoes a CLEAR", await Fail(() => Clear(s, "r3", [Full(a), Full(b)], s.GlOpen, "RESET", reset.ClearingId)));
    }

    [Fact]
    public async Task TheDatabaseRejectsWhatIsNotAClearing()
    {
        var s = await CreateAsync();
        Ln a = await PostOpen(s, "a", 1000m), b = await PostOpen(s, "b", -1000m), c = await PostOpen(s, "c", -400m);
        Ln other = await PostOpen(s, "o", -1000m, s.GlOpen2);

        Assert.Contains("differ from the sum of the clearing items", await Fail(() => Clear(s, "e1", [Full(a), Full(b)], s.GlOpen, updateCache: false)));
        Assert.Contains("clearing_status is", await Fail(() => Clear(s, "e2", [Full(a), Full(b)], s.GlOpen, statusOverride: "PARTIAL")));
        Assert.Contains("do not net to zero", await Fail(() => Clear(s, "e3", [Full(a), Full(c)], s.GlOpen)));
        Assert.Contains("has 1 item(s)", await Fail(() => Clear(s, "e4", [Full(a)], s.GlOpen)));
        Assert.Contains("not on the account and partner", await Fail(() => Clear(s, "e5", [Full(a), Full(other)], s.GlOpen)));
        Assert.Contains("does not link to its predecessor", await Fail(() => Clear(s, "e6", [Full(a), Full(b)], s.GlOpen, prevOverride: new byte[32])));
        Assert.Contains("does not match the newest clearing", await Fail(() => Clear(s, "e7", [Full(a), Full(b)], s.GlOpen, moveHead: false)));
        Assert.Equal(0L, await s.Tenant.ScalarAsync<long>("select count(*) from core.clearing where tenant_id = @t"));
        Assert.Equal("OPEN", await Status(s, a));

        // over-clearing: A is cleared once, a second clearing of A would take it past its amount
        var d = await PostOpen(s, "d", -1000m);
        await Clear(s, "ok", [Full(a), Full(b)], s.GlOpen);
        Assert.Contains("outside the line amount", await Fail(() => Clear(s, "e8", [Full(a), Full(d)], s.GlOpen)));
        Assert.Equal("CLEARED", await Status(s, a));
        Assert.Equal("OPEN", await Status(s, d));
        Assert.Equal(1L, await s.Tenant.ScalarAsync<long>("select count(*) from core.clearing where tenant_id = @t"));
    }

    [Fact]
    public async Task OnlyTheClearingRoleWritesAndTheRowsStayAsWritten()
    {
        var s = await CreateAsync();
        Ln a = await PostOpen(s, "a", 1000m), b = await PostOpen(s, "b", -1000m);

        Assert.Contains("permission denied", await Fail(() => Clear(s, "p1", [Full(a), Full(b)], s.GlOpen, role: "cmd_post")));
        Assert.Contains("permission denied", await Fail(() => Clear(s, "p2", [Full(a), Full(b)], s.GlOpen, role: "cmd_config")));
        Assert.Equal(0L, await s.Tenant.ScalarAsync<long>("select count(*) from core.clearing where tenant_id = @t"));

        var w = await Clear(s, "ok", [Full(a), Full(b)], s.GlOpen);
        Assert.Contains("append-only", await Fail(() => s.Tenant.ExecuteAsync("update core.clearing set note = 'x' where tenant_id = @t")));
        Assert.Contains("append-only", await Fail(() => s.Tenant.ExecuteAsync("delete from core.clearing_item where tenant_id = @t")));
        Assert.Contains("append-only", await Fail(() => s.Tenant.ExecuteAsync("truncate core.clearing_item")));

        // the clearing role changes the S columns of an open item and nothing else of it
        string asClearing = "select set_config('app.tenant_id', @t::text, true); set local role cmd_clearing; ";
        Assert.Contains("permission denied", await Fail(() => s.Tenant.ExecuteAsync(asClearing + "update core.journal_line_open_item set due_date = date '2026-05-01' where tenant_id = @t")));
        Assert.Contains("permission denied", await Fail(() => s.Tenant.ExecuteAsync(asClearing + "update core.journal_line_open_item set partner_id = gen_random_uuid() where tenant_id = @t")));
        Assert.Equal("CLEARED", await Status(s, a));
        Assert.True(w.ChainSeq == 1);
    }
}
