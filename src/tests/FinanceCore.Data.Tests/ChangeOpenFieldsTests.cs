using FinanceCore.Core.Commands;
using FinanceCore.Core.Posting;

namespace FinanceCore.Data.Tests;

[Collection("db")]
public class ChangeOpenFieldsTests
{
    private static string Describe<T>(CommandResult<T> r) =>
        r.Status + ": " + string.Join(", ", r.Errors.Select(e => $"{e.Code} {e.Path}"));

    private const string Initial = "Test posting";   // the header text of TestTenant.Journal

    private static string[] Codes<T>(CommandResult<T> r) => r.Errors.Select(e => e.Code).ToArray();

    private static CommandContext Change(TestTenant t, string key, params string[] more) =>
        t.Context(key, ["journal.change", .. more]);

    private static async Task<PostedDocument> Post(TestTenant t, string key = "post", string date = "2026-03-15")
    {
        var r = await new JournalDocumentPoster(t.App).PostAsync(t.TenantId, t.Context(key), t.Journal(date, "DKK", 1000m));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        return r.Value!;
    }

    private static Task<CommandResult<ChangedDocument>> Run(
        TestTenant t, CommandContext ctx, Guid document, string reason, params FieldChange[] changes) =>
        new DocumentChanger(t.App).ChangeAsync(t.TenantId, ctx, new ChangeOpenFieldsRequest(document, changes, reason, null));

    private static FieldChange Header(string field, string? old, string? value) => new("DOCUMENT", null, field, old, value);
    private static FieldChange Line(int no, string field, string? old, string? value) => new("LINE", no, field, old, value);

    /// <summary>ScalarAsync turns NULL into an empty string, so a nullable column is read through text.</summary>
    private static async Task<string?> Nullable(TestTenant t, string sql, params (string Name, object Value)[] parameters)
    {
        string v = await t.ScalarAsync<string>("select coalesce((" + sql + ")::text, '<null>')", parameters);
        return v == "<null>" ? null : v;
    }

    private static Task<string?> HeaderText(TestTenant t, Guid d) => Nullable(t, 
        "select header_text from core.journal_document where tenant_id = @t and document_id = @d", ("d", d));

    [Fact]
    public async Task ChangesHeaderAndLineAndLogsEveryField()
    {
        var t = await TestTenant.CreateReadyAsync();
        var doc = await Post(t);
        string hashBefore = doc.RowHash;

        var r = await Run(t, Change(t, "c1"), doc.DocumentId, "CORRECTION",
            Header("header_text", Initial, "Hello"), Header("external_reference", null, "PO-7"), Line(1, "line_text", null, "First line"));

        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        Assert.Equal("OPEN", r.Value!.PeriodStatus);
        Assert.Equal(3, r.Value.Changed.Count);
        Assert.Equal("Hello", await HeaderText(t, doc.DocumentId));
        Assert.Equal("PO-7", await Nullable(t, "select external_reference from core.journal_document where tenant_id = @t and document_id = @d", ("d", doc.DocumentId)));
        Assert.Equal("First line", await Nullable(t, "select line_text from core.journal_line where tenant_id = @t and document_id = @d and line_no = 1", ("d", doc.DocumentId)));
        Assert.Null(await Nullable(t, "select line_text from core.journal_line where tenant_id = @t and document_id = @d and line_no = 2", ("d", doc.DocumentId)));

        // one log row per field, one change set, with the command's key and reason
        Assert.Equal(3L, await t.ScalarAsync<long>(
            "select count(*) from core.change_log where tenant_id = @t and change_set_id = @s and idempotency_key = 'c1' and reason_code = 'CORRECTION' " +
            "and lock_class = 'L2' and period_status = 'OPEN' and changed_by = 'tester'", ("s", r.Value.ChangeSetId)));
        Assert.Equal("ledger.line_changed", await t.ScalarAsync<string>("select event_type from core.outbox_event where tenant_id = @t order by position desc limit 1"));
        Assert.True(await t.ScalarAsync<bool>("select is_audit from core.outbox_event where tenant_id = @t order by position desc limit 1"));
        string summary = await t.ScalarAsync<string>("select summary::text from core.outbox_event where tenant_id = @t order by position desc limit 1");
        Assert.DoesNotContain("Hello", summary);   // field names only, no values (15, 6)
        Assert.Contains("header_text", summary);

        // L2 is outside every hash: the chain is as it was
        Assert.Equal(hashBefore.ToLowerInvariant(), await t.ScalarAsync<string>("select lower(encode(row_hash, 'hex')) from core.journal_document where tenant_id = @t and document_id = @d", ("d", doc.DocumentId)));
        var v = await new ChainVerifier(t.App).VerifyLedgerAsync(t.TenantId, t.CompanyId, t.LedgerId);
        Assert.True(v.Ok, string.Join("; ", v.Problems.Select(p => p.Code + " " + p.Detail)));
    }

    [Fact]
    public async Task EachChangeNeedsTheValueTheCallerSaw()
    {
        var t = await TestTenant.CreateReadyAsync();
        var doc = await Post(t);
        Assert.Equal(CommandStatus.Ok, (await Run(t, Change(t, "c1"), doc.DocumentId, "CORRECTION", Header("header_text", Initial, "A"))).Status);

        // the first change is stale (the field is "A", not empty), so the second, correct one is not applied either
        var r = await Run(t, Change(t, "c2"), doc.DocumentId, "CORRECTION",
            Header("external_reference", null, "PO-1"), Header("header_text", Initial, "B"));
        Assert.Equal(CommandStatus.Rejected, r.Status);
        Assert.Equal(["CONFLICT.EXPECTED_VALUE_MISMATCH"], Codes(r));
        Assert.Equal("A", r.Errors[0].Params!["current_value"]);
        Assert.Equal("A", await HeaderText(t, doc.DocumentId));
        Assert.Null(await Nullable(t, "select external_reference from core.journal_document where tenant_id = @t and document_id = @d", ("d", doc.DocumentId)));
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.change_log where tenant_id = @t"));

        var ok = await Run(t, Change(t, "c3"), doc.DocumentId, "CORRECTION", Header("header_text", "A", "B"));
        Assert.True(ok.Status == CommandStatus.Ok, Describe(ok));
        Assert.Equal("A", await t.ScalarAsync<string>("select old_value from core.change_log where tenant_id = @t and idempotency_key = 'c3'"));
        Assert.Equal("B", await HeaderText(t, doc.DocumentId));
    }

    [Fact]
    public async Task NullClearsAFieldAndAnUnchangedFieldIsIgnoredWithAWarning()
    {
        var t = await TestTenant.CreateReadyAsync();
        var doc = await Post(t);
        await Run(t, Change(t, "c1"), doc.DocumentId, "CORRECTION", Header("header_text", Initial, "A"));

        var r = await Run(t, Change(t, "c2"), doc.DocumentId, "CORRECTION",
            Header("header_text", "A", null), Header("external_reference", null, null));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        Assert.Single(r.Value!.Changed);
        Assert.Equal(["SCHEMA.NO_EFFECTIVE_CHANGE"], r.Warnings.Select(w => w.Code).ToArray());
        Assert.Null(await HeaderText(t, doc.DocumentId));
        Assert.Null(await Nullable(t, "select new_value from core.change_log where tenant_id = @t and idempotency_key = 'c2'"));

        var none = await Run(t, Change(t, "c3"), doc.DocumentId, "CORRECTION", Header("header_text", null, null));
        Assert.Equal(CommandStatus.Rejected, none.Status);
        Assert.Equal(["SCHEMA.NO_EFFECTIVE_CHANGE"], Codes(none));
    }

    [Fact]
    public async Task HardClosedPeriodRejectsAndSoftClosedNeedsItsOwnAuthorisation()
    {
        var t = await TestTenant.CreateReadyAsync();
        var doc = await Post(t);

        await t.SetPeriodStatusAsync(3, "SOFT_CLOSED");
        var denied = await Run(t, Change(t, "c1"), doc.DocumentId, "CORRECTION", Header("header_text", Initial, "A"));
        Assert.Equal(["PERIOD.SOFT_CLOSED_NOT_AUTHORISED"], Codes(denied));
        Assert.Equal(Initial, await HeaderText(t, doc.DocumentId));

        var ok = await Run(t, Change(t, "c2", "change.soft_closed"), doc.DocumentId, "CORRECTION", Header("header_text", Initial, "A"));
        Assert.True(ok.Status == CommandStatus.Ok, Describe(ok));
        Assert.Equal("SOFT_CLOSED", ok.Value!.PeriodStatus);
        Assert.Equal("SOFT_CLOSED", await t.ScalarAsync<string>("select period_status from core.change_log where tenant_id = @t"));

        await t.SetPeriodStatusAsync(3, "HARD_CLOSED");
        var locked = await Run(t, Change(t, "c3", "change.soft_closed"), doc.DocumentId, "CORRECTION", Header("header_text", "A", "B"));
        Assert.Equal(["LOCK.FIELD_LOCKED_L3"], Codes(locked));
        Assert.Equal("A", await HeaderText(t, doc.DocumentId));
    }

    [Fact]
    public async Task OnlyTheOpenFieldsChange()
    {
        var t = await TestTenant.CreateReadyAsync();
        var doc = await Post(t);

        var r = await Run(t, Change(t, "c1"), doc.DocumentId, "CORRECTION",
            Header("document_type", "JOURNAL", "AR_INVOICE"),   // L1
            Header("status", "posted", "reversed"),              // S
            Header("nope", null, "x"),                           // not a column
            Header("line_text", null, "x"),                      // a line field on the header
            Line(1, "amount_lc", "1", "2"),                      // L1
            Line(1, "cost_center_id", null, Guid.NewGuid().ToString()),   // L2, but the value must exist
            Line(9, "line_text", null, "x"));                    // no such line
        Assert.Equal(CommandStatus.Rejected, r.Status);
        Assert.Equal(
            ["LOCK.FIELD_LOCKED_L1", "LOCK.FIELD_LOCKED_L1", "LOCK.FIELD_UNKNOWN", "LOCK.FIELD_UNKNOWN", "LOCK.FIELD_LOCKED_L1",
             "DOCUMENT.LINE_NOT_FOUND", "ACCOUNT_RULE.DIMENSION_VALUE_INVALID"], Codes(r));
        Assert.Equal(0L, await t.ScalarAsync<long>("select count(*) from core.change_log where tenant_id = @t"));
        Assert.Equal("JOURNAL", await t.ScalarAsync<string>("select document_type from core.journal_document where tenant_id = @t and document_id = @d", ("d", doc.DocumentId)));

        var dim = await Run(t, Change(t, "c2"), doc.DocumentId, "CORRECTION", new FieldChange("TAX_EVIDENCE", 1, "evidence_no", null, "x"));
        Assert.Equal(["LIMIT.ENTITY_TYPE_NOT_BUILT"], Codes(dim));
    }

    [Fact]
    public async Task TheShapeOfTheRequestIsChecked()
    {
        var t = await TestTenant.CreateReadyAsync();
        var doc = await Post(t);

        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await Run(t, t.Context("c0"), doc.DocumentId, "CORRECTION", Header("header_text", Initial, "A"))));
        Assert.Equal(["SCHEMA.REQUIRED_FIELD_MISSING"], Codes(await Run(t, Change(t, "c1"), doc.DocumentId, "", Header("header_text", Initial, "A"))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await Run(t, Change(t, "c2"), doc.DocumentId, "SYSTEM", Header("header_text", Initial, "A"))));
        Assert.Equal(["SCHEMA.REQUIRED_FIELD_MISSING"], Codes(await Run(t, Change(t, "c3"), doc.DocumentId, "CORRECTION")));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await Run(t, Change(t, "c4"), doc.DocumentId, "CORRECTION", Header("header_text", Initial, ""))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await Run(t, Change(t, "c5"), doc.DocumentId, "CORRECTION", Header("header_text", Initial, new string('x', 201)))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await Run(t, Change(t, "c6"), doc.DocumentId, "CORRECTION",
            Header("header_text", Initial, "a"), Header("header_text", Initial, "b"))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await Run(t, Change(t, "c7"), doc.DocumentId, "CORRECTION", new FieldChange("LINE", null, "line_text", null, "a"))));
        Assert.Equal(["DOCUMENT.NOT_FOUND"], Codes(await Run(t, Change(t, "c8"), Guid.NewGuid(), "CORRECTION", Header("header_text", Initial, "A"))));
        Assert.Equal(0L, await t.ScalarAsync<long>("select count(*) from core.change_log where tenant_id = @t"));
    }

    [Fact]
    public async Task ARepeatedKeyReturnsTheFirstResultAndAnotherRequestWithTheKeyIsRejected()
    {
        var t = await TestTenant.CreateReadyAsync();
        var doc = await Post(t);
        var first = await Run(t, Change(t, "same"), doc.DocumentId, "CORRECTION",
            Header("header_text", Initial, "A"), Line(2, "line_text", null, "B"));
        var again = await Run(t, Change(t, "same"), doc.DocumentId, "CORRECTION",
            Header("header_text", Initial, "A"), Line(2, "line_text", null, "B"));

        Assert.True(again.Status == CommandStatus.Ok, Describe(again));
        Assert.True(again.IdempotentReplay);
        Assert.Equal(first.Value!.ChangeSetId, again.Value!.ChangeSetId);
        Assert.Equal(2, again.Value.Changed.Count);
        Assert.Equal(2L, await t.ScalarAsync<long>("select count(*) from core.change_log where tenant_id = @t"));

        var other = await Run(t, Change(t, "same"), doc.DocumentId, "CORRECTION", Header("header_text", "A", "C"));
        Assert.Equal(["IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST"], Codes(other));
        Assert.Equal("A", await HeaderText(t, doc.DocumentId));
    }

    [Fact]
    public async Task AReversedDocumentKeepsItsOpenFieldsOpen()
    {
        var t = await TestTenant.CreateReadyAsync();
        var doc = await Post(t);
        var rev = await new DocumentReverser(t.App).ReverseAsync(t.TenantId, t.Context("rev", "journal.reverse"), new(doc.DocumentId, null, "CORRECTION", null));
        Assert.True(rev.Status == CommandStatus.Ok, Describe(rev));

        var r = await Run(t, Change(t, "c1"), doc.DocumentId, "CORRECTION", Header("header_text", Initial, "Reversed because of PO-9"));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        var v = await new ChainVerifier(t.App).VerifyLedgerAsync(t.TenantId, t.CompanyId, t.LedgerId);
        Assert.True(v.Ok, string.Join("; ", v.Problems.Select(p => p.Code + " " + p.Detail)));
    }
}
