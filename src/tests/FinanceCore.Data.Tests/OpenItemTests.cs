using FinanceCore.Core.Commands;
using FinanceCore.Core.Posting;
using Npgsql;

namespace FinanceCore.Data.Tests;

/// <summary>Open items on GL_OPEN_ITEM accounts (02, 5A): posting, the hash, the guards of the table and the change of its L2 fields.</summary>
[Collection("db")]
public class OpenItemTests
{
    private static string Describe<T>(CommandResult<T> r) =>
        r.Status + ": " + string.Join(", ", r.Errors.Select(e => $"{e.Code} {e.Path}"));

    private static string[] Codes<T>(CommandResult<T> r) => r.Errors.Select(e => e.Code).ToArray();

    private sealed record Setup(TestTenant Tenant, Guid GlOpen, Guid Control);

    private static async Task<Setup> CreateAsync()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid glOpen = Guid.NewGuid(), control = Guid.NewGuid();
        await t.ExecuteAsync(
            "insert into core.gl_account (tenant_id, company_id, account_id, account_no, name, account_type, tax_code_rule, open_item_mode) values " +
            "(@t, @c, @g, '1900', 'Bank clearing', 'ASSET', 'FORBIDDEN', 'GL_OPEN_ITEM'), " +
            "(@t, @c, @k, '1300', 'Receivables', 'ASSET', 'FORBIDDEN', 'CONTROL')",
            ("c", t.CompanyId), ("g", glOpen), ("k", control));
        return new Setup(t, glOpen, control);
    }

    private static PostJournalDocumentRequest Journal(Setup s, OpenItemRequest? openItem, Guid? account = null, Guid? other = null, decimal amount = 1000m) =>
        s.Tenant.Journal() with
        {
            Lines =
            [
                new RequestLine("1", account ?? s.GlOpen, amount, null, openItem),
                new RequestLine("2", other ?? s.Tenant.Revenue, -amount)
            ]
        };

    private static async Task<PostedDocument> Post(Setup s, string key, OpenItemRequest? openItem)
    {
        var r = await new JournalDocumentPoster(s.Tenant.App).PostAsync(s.Tenant.TenantId, s.Tenant.Context(key), Journal(s, openItem));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        return r.Value!;
    }

    private static OpenItemRequest Item() => new(DueDate: new DateOnly(2026, 4, 15), PaymentReference: "REF-1", DiscountAmountTc: 10m);

    private static async Task<string> Nullable(TestTenant t, string sql, params (string Name, object Value)[] p)
    {
        string v = await t.ScalarAsync<string>("select coalesce((" + sql + ")::text, '<null>')", p);
        return v;
    }

    private static string OpenItemSql(string column, Guid doc, int line) =>
        $"select {column} from core.journal_line_open_item where tenant_id = @t and document_id = '{doc}' and line_no = {line}";

    [Fact]
    public async Task AnOpenItemAccountGetsItsRowAndTheHashCoversIt()
    {
        var s = await CreateAsync();
        var doc = await Post(s, "p1", Item());

        Assert.Equal(1L, await s.Tenant.ScalarAsync<long>("select count(*) from core.journal_line_open_item where tenant_id = @t"));
        Assert.Equal("2026-04-15", await Nullable(s.Tenant, OpenItemSql("due_date", doc.DocumentId, 1)));
        Assert.Equal("REF-1", await Nullable(s.Tenant, OpenItemSql("payment_reference", doc.DocumentId, 1)));
        Assert.Equal("10.0000", await Nullable(s.Tenant, OpenItemSql("discount_amount_tc", doc.DocumentId, 1)));
        Assert.Equal("<null>", await Nullable(s.Tenant, OpenItemSql("partner_id", doc.DocumentId, 1)));
        Assert.Equal("OPEN", await Nullable(s.Tenant, OpenItemSql("clearing_status", doc.DocumentId, 1)));
        Assert.Equal("0.0000", await Nullable(s.Tenant, OpenItemSql("cleared_amount_tc", doc.DocumentId, 1)));
        Assert.Equal("0.0000", await Nullable(s.Tenant, OpenItemSql("cleared_amount_rc", doc.DocumentId, 1)));   // the line has a reporting amount
        Assert.Equal(0L, await s.Tenant.ScalarAsync<long>("select count(*) from core.journal_line_open_item where tenant_id = @t and line_no = 2"));

        // The verifier recomputes the row hash with the open item block (02, 9.3a): the posting and the check agree
        var v = await new ChainVerifier(s.Tenant.App).VerifyLedgerAsync(s.Tenant.TenantId, s.Tenant.CompanyId, s.Tenant.LedgerId);
        Assert.True(v.Ok, string.Join("; ", v.Problems.Select(p => p.Code + " " + p.Detail)));

        // The same document without an open item row has another hash: the row is covered
        var plain = await new JournalDocumentPoster(s.Tenant.App).PostAsync(s.Tenant.TenantId, s.Tenant.Context("p2"),
            Journal(s, null, account: s.Tenant.Bank));
        Assert.True(plain.Status == CommandStatus.Ok, Describe(plain));
        Assert.Equal(0L, await s.Tenant.ScalarAsync<long>("select count(*) from core.journal_line_open_item where tenant_id = @t and document_id = @d", ("d", plain.Value!.DocumentId)));
    }

    [Fact]
    public async Task TheOpenItemRulesOfThePostingCommand()
    {
        var s = await CreateAsync();
        async Task<string[]> Try(string key, PostJournalDocumentRequest r) =>
            Codes(await new JournalDocumentPoster(s.Tenant.App).PostAsync(s.Tenant.TenantId, s.Tenant.Context(key), r));

        Assert.Equal(["ACCOUNT_RULE.OPEN_ITEM_REQUIRED"], await Try("a", Journal(s, null)));
        Assert.Equal(["ACCOUNT_RULE.OPEN_ITEM_FORBIDDEN"], await Try("b", Journal(s, new OpenItemRequest(), account: s.Tenant.Bank)));
        Assert.Equal(["ACCOUNT_RULE.PARTNER_FORBIDDEN"], await Try("c", Journal(s, new OpenItemRequest(PartnerId: Guid.NewGuid()))));
        Assert.Equal(["LIMIT.OPEN_ITEM_FIELD_NOT_BUILT", "LIMIT.OPEN_ITEM_FIELD_NOT_BUILT"],
            await Try("d", Journal(s, new OpenItemRequest(PaymentTermsId: Guid.NewGuid(), BankAccountId: Guid.NewGuid()))));
        Assert.Equal(["LIMIT.CONTROL_ACCOUNT_NEEDS_PARTNER"], await Try("e", Journal(s, new OpenItemRequest(DueDate: new DateOnly(2026, 4, 1)), account: s.Control)));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], await Try("f", Journal(s, new OpenItemRequest(DiscountAmountTc: -1m))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], await Try("g", Journal(s, new OpenItemRequest(DiscountAmountTc: 0.001m))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], await Try("h", Journal(s, new OpenItemRequest(PaymentBlock: ""))));
        Assert.Equal(0L, await s.Tenant.ScalarAsync<long>("select count(*) from core.journal_document where tenant_id = @t"));

        // An empty open_item object is a valid open item: every attribute is optional (02, 5A)
        await Post(s, "ok", new OpenItemRequest());
    }

    [Fact]
    public async Task TheOpenItemIsPartOfTheRequestOfAnIdempotentCommand()
    {
        var s = await CreateAsync();
        var first = await Post(s, "same", Item());
        var again = await new JournalDocumentPoster(s.Tenant.App).PostAsync(s.Tenant.TenantId, s.Tenant.Context("same"), Journal(s, Item()));
        Assert.True(again.IdempotentReplay, Describe(again));
        Assert.Equal(first.DocumentId, again.Value!.DocumentId);

        var other = await new JournalDocumentPoster(s.Tenant.App).PostAsync(s.Tenant.TenantId, s.Tenant.Context("same"),
            Journal(s, Item() with { DueDate = new DateOnly(2026, 5, 1) }));
        Assert.Equal(["IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST"], Codes(other));
        Assert.Equal(1L, await s.Tenant.ScalarAsync<long>("select count(*) from core.journal_line_open_item where tenant_id = @t"));
    }

    [Fact]
    public async Task TheTableGuardsItsColumns()
    {
        var s = await CreateAsync();
        var doc = await Post(s, "p1", Item());
        var t = s.Tenant;
        string key = $"tenant_id = @t and document_id = '{doc.DocumentId}' and line_no = 1";

        async Task<string> Error(string sql)
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => t.ExecuteAsync(sql));
            return ex.MessageText;
        }
        Assert.Contains("L1 column cannot change", await Error($"update core.journal_line_open_item set partner_id = gen_random_uuid() where {key}"));
        Assert.Contains("L1 column cannot change", await Error($"update core.journal_line_open_item set legacy_document_ref = 'x' where {key}"));
        Assert.Contains("only with a clearing", await Error($"update core.journal_line_open_item set clearing_status = 'CLEARED', last_clearing_id = gen_random_uuid() where {key}"));
        Assert.Contains("only with a clearing", await Error($"update core.journal_line_open_item set cleared_amount_tc = 1 where {key}"));
        Assert.Contains("without a matching change_log row", await Error($"update core.journal_line_open_item set due_date = date '2026-06-01' where {key}"));
        Assert.Contains("no hard delete", await Error($"delete from core.journal_line_open_item where {key}"));

        // a row on a line of an account that is not open-item managed is rejected at commit
        Assert.Contains("not open-item managed", await Error(
            $"insert into core.journal_line_open_item (tenant_id, company_id, document_id, line_no) select tenant_id, company_id, document_id, 2 from core.journal_line_open_item where {key}"));
        Assert.Equal("2026-04-15", await Nullable(t, OpenItemSql("due_date", doc.DocumentId, 1)));
    }

    [Fact]
    public async Task ADocumentWithOpenItemsCannotBeReversedYet()
    {
        var s = await CreateAsync();
        var doc = await Post(s, "p1", Item());
        var r = await new DocumentReverser(s.Tenant.App).ReverseAsync(s.Tenant.TenantId, s.Tenant.Context("rev", "journal.reverse"), new(doc.DocumentId, null, "CORRECTION", null));
        Assert.Equal(["LIMIT.REVERSAL_OF_OPEN_ITEMS_NOT_BUILT"], Codes(r));
        Assert.Equal("posted", await s.Tenant.ScalarAsync<string>("select status from core.journal_document where tenant_id = @t and document_id = @d", ("d", doc.DocumentId)));
    }

    // ---------------------------------------------------------------------------------------------------------
    // journal-documents:change-open-fields on the entity type LINE_OPEN_ITEM
    // ---------------------------------------------------------------------------------------------------------
    private static Task<CommandResult<ChangedDocument>> Change(Setup s, string key, Guid doc, params FieldChange[] changes) =>
        new DocumentChanger(s.Tenant.App).ChangeAsync(s.Tenant.TenantId, s.Tenant.Context(key, "journal.change"),
            new ChangeOpenFieldsRequest(doc, changes, "CORRECTION", null));

    private static FieldChange OI(string field, string? old, string? value, int line = 1) => new("LINE_OPEN_ITEM", line, field, old, value);

    [Fact]
    public async Task TheL2FieldsOfAnOpenItemChangeWithTheirKindsAndCanonicalText()
    {
        var s = await CreateAsync();
        var doc = await Post(s, "p1", Item());
        var t = s.Tenant;

        var r = await Change(s, "c1", doc.DocumentId,
            OI("due_date", "2026-04-15", "2026-05-01"), OI("discount_amount_tc", "10.0000", "12.5"),
            OI("payment_block", null, "DISPUTED"), OI("payment_reference", "REF-1", null), OI("baseline_date", null, "2026-03-15"));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        Assert.Equal(5, r.Value!.Changed.Count);
        Assert.Equal("2026-05-01", await Nullable(t, OpenItemSql("due_date", doc.DocumentId, 1)));
        Assert.Equal("12.5000", await Nullable(t, OpenItemSql("discount_amount_tc", doc.DocumentId, 1)));   // canonical text, also in the log
        Assert.Equal("DISPUTED", await Nullable(t, OpenItemSql("payment_block", doc.DocumentId, 1)));
        Assert.Equal("<null>", await Nullable(t, OpenItemSql("payment_reference", doc.DocumentId, 1)));
        Assert.Equal("2026-03-15", await Nullable(t, OpenItemSql("baseline_date", doc.DocumentId, 1)));

        Assert.Equal("12.5000", await t.ScalarAsync<string>("select new_value from core.change_log where tenant_id = @t and field_name = 'discount_amount_tc'"));
        Assert.Equal("AMOUNT", await t.ScalarAsync<string>("select value_type from core.change_log where tenant_id = @t and field_name = 'discount_amount_tc'"));
        Assert.Equal("DATE", await t.ScalarAsync<string>("select value_type from core.change_log where tenant_id = @t and field_name = 'due_date'"));
        Assert.Equal("CODE", await t.ScalarAsync<string>("select value_type from core.change_log where tenant_id = @t and field_name = 'payment_block'"));
        Assert.Equal(5L, await t.ScalarAsync<long>("select count(*) from core.change_log where tenant_id = @t and entity_type = 'LINE_OPEN_ITEM' and line_no = 1 and lock_class = 'L2'"));

        // compare-and-set on the canonical text: the caller must give the amount as stored
        var stale = await Change(s, "c2", doc.DocumentId, OI("discount_amount_tc", "12.5", "20"));
        Assert.Equal(["CONFLICT.EXPECTED_VALUE_MISMATCH"], Codes(stale));
        Assert.Equal("12.5000", stale.Errors[0].Params!["current_value"]);

        // L2 is outside the hash
        var v = await new ChainVerifier(t.App).VerifyLedgerAsync(t.TenantId, t.CompanyId, t.LedgerId);
        Assert.True(v.Ok, string.Join("; ", v.Problems.Select(p => p.Code + " " + p.Detail)));
    }

    [Fact]
    public async Task TheOpenItemFieldsThatCannotChange()
    {
        var s = await CreateAsync();
        var doc = await Post(s, "p1", Item());

        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await Change(s, "a", doc.DocumentId, OI("due_date", "2026-04-15", "15/04/2026"))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await Change(s, "b", doc.DocumentId, OI("discount_amount_tc", "10.0000", "-1"))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await Change(s, "c", doc.DocumentId, OI("discount_amount_tc", "10.0000", "1.00001"))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await Change(s, "d", doc.DocumentId, OI("payment_reference", "REF-1", ""))));
        Assert.Equal(["LIMIT.FIELD_NOT_BUILT"], Codes(await Change(s, "e", doc.DocumentId, OI("payment_terms_id", null, Guid.NewGuid().ToString()))));
        Assert.Equal(["LOCK.FIELD_LOCKED_L1"], Codes(await Change(s, "f", doc.DocumentId, OI("partner_id", null, Guid.NewGuid().ToString()))));
        Assert.Equal(["LOCK.FIELD_LOCKED_L1"], Codes(await Change(s, "g", doc.DocumentId, OI("clearing_status", "OPEN", "CLEARED"))));
        Assert.Equal(["LOCK.FIELD_UNKNOWN"], Codes(await Change(s, "h", doc.DocumentId, OI("nope", null, "x"))));
        Assert.Equal(["DOCUMENT.OPEN_ITEM_NOT_FOUND"], Codes(await Change(s, "i", doc.DocumentId, OI("due_date", null, "2026-05-01", line: 2))));
        Assert.Equal(["DOCUMENT.LINE_NOT_FOUND"], Codes(await Change(s, "j", doc.DocumentId, OI("due_date", null, "2026-05-01", line: 9))));
        Assert.Equal(0L, await s.Tenant.ScalarAsync<long>("select count(*) from core.change_log where tenant_id = @t"));
        Assert.Equal("2026-04-15", await Nullable(s.Tenant, OpenItemSql("due_date", doc.DocumentId, 1)));
    }

    [Fact]
    public async Task AHardClosedPeriodLocksTheOpenItemToo()
    {
        var s = await CreateAsync();
        var doc = await Post(s, "p1", Item());
        await s.Tenant.SetPeriodStatusAsync(3, "HARD_CLOSED");
        var r = await Change(s, "c1", doc.DocumentId, OI("due_date", "2026-04-15", "2026-05-01"));
        Assert.Equal(["LOCK.FIELD_LOCKED_L3"], Codes(r));
        Assert.Equal("2026-04-15", await Nullable(s.Tenant, OpenItemSql("due_date", doc.DocumentId, 1)));
    }
}
