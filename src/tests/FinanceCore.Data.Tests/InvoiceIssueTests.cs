using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;
using FinanceCore.Core.Posting;

namespace FinanceCore.Data.Tests;

/// <summary>invoices:record-issue (12, 11, 22 delta 1; FT-03, FT-04): the evidence of an issued file, for a direct invoice by its journal document.</summary>
[Collection("db")]
public class InvoiceIssueTests
{
    private const string Sha = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    private static string Describe<T>(CommandResult<T> r) => r.Status + ": " + string.Join(", ", r.Errors.Select(e => $"{e.Code} {e.Path}"));
    private static string[] Codes<T>(CommandResult<T> r) => r.Errors.Select(e => e.Code).ToArray();
    private static CommandContext Ctx(TestTenant t, string key) => t.Context(key, InvoiceIssueRules.Permission);

    private static async Task<Guid> Partner(TestTenant t)
    {
        var req = new CreatePartnerRequest(
            new Dictionary<string, object?> { ["partner_code"] = "ACME", ["partner_kind"] = "ORGANISATION", ["is_customer"] = true, ["is_supplier"] = false },
            new Dictionary<string, object?> { ["legal_name"] = "ACME A/S", ["address_line_1"] = "Havnegade 1", ["postal_code"] = "1058", ["city"] = "København K", ["country_code"] = "DK", ["email"] = "post@acme.dk" },
            [new PartnerRelationInput(t.CompanyId, new Dictionary<string, object?> { ["is_customer"] = true, ["is_supplier"] = false })]);
        var r = await new PartnerCommands(t.App).CreateAsync(t.TenantId, t.Context("pa", "write.master"), req);
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        return r.Value!.EntityId;
    }

    private static async Task<Guid> Posted(TestTenant t, string key, string type, Guid? partner)
    {
        var r = await new JournalDocumentPoster(t.App).PostAsync(t.TenantId, t.Context(key), t.Journal() with { DocumentType = type, PartnerId = partner });
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        return r.Value!.DocumentId;
    }

    private static RecordInvoiceIssueRequest Issue(TestTenant t, Guid? document, string? kind = null, Guid? invoice = null, string sha = Sha) =>
        new(t.CompanyId, "XML", "archive://2026/inv-1.xml", sha, "PEPPOL", kind, invoice, document, new DateTimeOffset(2026, 3, 16, 9, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task ADirectInvoiceGetsSeveralIssueRecordsAndAnEvent()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid doc = await Posted(t, "inv", "AR_INVOICE", await Partner(t));
        var commands = new InvoiceIssueCommands(t.App);

        var first = await commands.RecordAsync(t.TenantId, Ctx(t, "iss:1:1"), Issue(t, doc));
        Assert.True(first.Status == CommandStatus.Ok, Describe(first));
        Assert.Equal("SALES", first.Value!.InvoiceKind);
        Assert.Equal(doc, first.Value.DocumentId);
        Assert.Null(first.Value.InvoiceId);

        var again = await commands.RecordAsync(t.TenantId, Ctx(t, "iss:1:2"), Issue(t, doc) with { DeliveryChannel = "EMAIL", Format = "PDF" });
        Assert.True(again.Status == CommandStatus.Ok, Describe(again));
        Assert.NotEqual(first.Value.IssueId, again.Value!.IssueId);
        Assert.Equal(2L, await t.ScalarAsync<long>("select count(*) from core.invoice_issue_record where tenant_id = @t and document_id = @d", ("d", doc)));
        Assert.Equal(2L, await t.ScalarAsync<long>("select count(*) from core.outbox_event where tenant_id = @t and event_type = 'invoice.issued'"));
        Assert.Equal(Sha, await t.ScalarAsync<string>("select encode(document_sha256, 'hex') from core.invoice_issue_record where tenant_id = @t and issue_id = @i", ("i", first.Value.IssueId)));
    }

    [Fact]
    public async Task TheSameKeyReplaysAndAChangedRequestIsRefused()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid doc = await Posted(t, "inv", "AR_INVOICE", await Partner(t));
        var commands = new InvoiceIssueCommands(t.App);
        var first = await commands.RecordAsync(t.TenantId, Ctx(t, "iss:2"), Issue(t, doc));
        var replay = await commands.RecordAsync(t.TenantId, Ctx(t, "iss:2"), Issue(t, doc));
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(first.Value!.IssueId, replay.Value!.IssueId);
        var changed = await commands.RecordAsync(t.TenantId, Ctx(t, "iss:2"), Issue(t, doc) with { Format = "PDF" });
        Assert.Equal(["IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST"], Codes(changed));
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.invoice_issue_record where tenant_id = @t"));
    }

    [Fact]
    public async Task TheTargetIsExactlyOneAndTheKindFollowsTheDocument()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid doc = await Posted(t, "inv", "AR_INVOICE", await Partner(t));
        Guid journal = await Posted(t, "jrn", "JOURNAL", null);
        var commands = new InvoiceIssueCommands(t.App);

        Assert.Equal(["ORDER.ISSUE_TARGET_INVALID"], Codes(await commands.RecordAsync(t.TenantId, Ctx(t, "a"), Issue(t, null))));
        Assert.Equal(["ORDER.ISSUE_TARGET_INVALID"], Codes(await commands.RecordAsync(t.TenantId, Ctx(t, "b"), Issue(t, doc, "SALES", Guid.NewGuid()))));
        Assert.Equal(["ORDER.ISSUE_TARGET_INVALID"], Codes(await commands.RecordAsync(t.TenantId, Ctx(t, "c"), Issue(t, doc, "PURCHASE"))));
        Assert.Equal(["ORDER.ISSUE_DOCUMENT_TYPE_INVALID"], Codes(await commands.RecordAsync(t.TenantId, Ctx(t, "d"), Issue(t, journal))));
        Assert.Equal(["ORDER.ISSUE_TARGET_NOT_FOUND"], Codes(await commands.RecordAsync(t.TenantId, Ctx(t, "e"), Issue(t, Guid.NewGuid()))));
        Assert.Equal(["ORDER.ISSUE_TARGET_NOT_FOUND"], Codes(await commands.RecordAsync(t.TenantId, Ctx(t, "f"), Issue(t, null, "SALES", Guid.NewGuid()))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await commands.RecordAsync(t.TenantId, Ctx(t, "g"), Issue(t, doc, sha: "abc"))));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await commands.RecordAsync(t.TenantId, t.Context("h", "journal.post"), Issue(t, doc))));
        Assert.Equal(0L, await t.ScalarAsync<long>("select count(*) from core.invoice_issue_record where tenant_id = @t"));
    }

    [Fact]
    public async Task AMirrorDocumentIsNoTargetAndTheRecordsCannotBeChanged()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid doc = await Posted(t, "inv", "AR_INVOICE", await Partner(t));
        var rev = await new DocumentReverser(t.App).ReverseAsync(t.TenantId, t.Context("rev", "journal.reverse"), new(doc, null, "CORRECTION", "Wrong"));
        Assert.True(rev.Status == CommandStatus.Ok, Describe(rev));
        var commands = new InvoiceIssueCommands(t.App);
        // the generic mirror has the type REVERSAL; the mirror of an owning command (AR_INVOICE with reversal_of_document_id) is ISSUE_TARGET_INVALID
        Assert.Equal(["ORDER.ISSUE_DOCUMENT_TYPE_INVALID"], Codes(await commands.RecordAsync(t.TenantId, Ctx(t, "m"), Issue(t, rev.Value!.DocumentId))));

        // the original may still get evidence, and the record is insert-only
        var ok = await commands.RecordAsync(t.TenantId, Ctx(t, "o"), Issue(t, doc));
        Assert.True(ok.Status == CommandStatus.Ok, Describe(ok));
        await Assert.ThrowsAnyAsync<Exception>(() => t.ExecuteAsync("update core.invoice_issue_record set format = 'PDF' where tenant_id = @t"));
        await Assert.ThrowsAnyAsync<Exception>(() => t.ExecuteAsync("delete from core.invoice_issue_record where tenant_id = @t"));
    }
}
