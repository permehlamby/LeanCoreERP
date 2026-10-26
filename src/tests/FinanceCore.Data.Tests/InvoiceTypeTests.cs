using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;
using FinanceCore.Core.Posting;

namespace FinanceCore.Data.Tests;

/// <summary>The header partner and the invoice types (06, 5.2): the partner is required for AR and AP types, checked like the partner of an open item
/// plus the role of its relation, and the legal snapshot of the document takes the partner's data as on the document (02, 4).</summary>
[Collection("db")]
public class InvoiceTypeTests
{
    private static string Describe<T>(CommandResult<T> r) =>
        r.Status + ": " + string.Join(", ", r.Errors.Select(e => $"{e.Code} {e.Path}"));

    private static Dictionary<string, object?> D(params (string, object?)[] f) => f.ToDictionary(x => x.Item1, x => x.Item2);

    private static async Task<Guid> NewPartner(TestTenant t, string key, string code, bool customer, bool supplier, string? relationStatus = null)
    {
        var relation = D(("is_customer", customer), ("is_supplier", supplier));
        if (relationStatus is not null) relation["status"] = relationStatus;
        var req = new CreatePartnerRequest(
            D(("partner_code", code), ("partner_kind", "ORGANISATION"), ("is_customer", customer), ("is_supplier", supplier)),
            D(("legal_name", code + " A/S"), ("address_line_1", "Havnegade 1"), ("postal_code", "1058"), ("city", "København K"), ("country_code", "DK"),
              ("electronic_address", "5790000436057"), ("electronic_address_scheme", "0088"), ("legal_registration_id", "12345678"),
              ("email", "post@" + code.ToLowerInvariant() + ".dk")),
            [new PartnerRelationInput(t.CompanyId, relation)]);
        var r = await new PartnerCommands(t.App).CreateAsync(t.TenantId, t.Context(key, "write.master"), req);
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        return r.Value!.EntityId;
    }

    private static PostJournalDocumentRequest Invoice(TestTenant t, string type, Guid? partner, string date = "2026-03-15") =>
        t.Journal(date) with { DocumentType = type, PartnerId = partner };

    private static Task<CommandResult<PostedDocument>> Post(TestTenant t, string key, PostJournalDocumentRequest req) =>
        new JournalDocumentPoster(t.App).PostAsync(t.TenantId, t.Context(key), req);

    private static string[] Codes<T>(CommandResult<T> r) => r.Errors.Select(e => e.Code).ToArray();

    [Theory]
    [InlineData("AR_INVOICE")]
    [InlineData("AR_CREDIT")]
    [InlineData("AP_INVOICE")]
    [InlineData("AP_CREDIT")]
    public async Task AnInvoiceTypeNeedsAHeaderPartner(string type)
    {
        var t = await TestTenant.CreateReadyAsync();
        var r = await Post(t, "p", Invoice(t, type, null));
        Assert.Equal(["SCHEMA.REQUIRED_FIELD_MISSING"], Codes(r));
        Assert.Equal("partner_id", r.Errors[0].Path);
        Assert.Equal(0L, await t.ScalarAsync<long>("select count(*) from core.journal_document where tenant_id = @t"));
    }

    [Fact]
    public async Task TheSnapshotTakesThePartnersDataAsOnTheDocumentAndLaterChangesDoNotReachIt()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid partner = await NewPartner(t, "pa", "ACME", customer: true, supplier: false);
        var r = await Post(t, "inv", Invoice(t, "AR_INVOICE", partner));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        Guid doc = r.Value!.DocumentId;

        async Task<string> Snap(string column) => await t.ScalarAsync<string>(
            $"select coalesce({column}::text, '<null>') from core.document_legal_snapshot where tenant_id = @t and document_id = @d", ("d", doc));
        Assert.Equal(partner.ToString(), await Snap("partner_id"));
        Assert.Equal("ACME A/S", await Snap("legal_name"));
        Assert.Equal("Havnegade 1", await Snap("address_line_1"));
        Assert.Equal("<null>", await Snap("address_line_2"));
        Assert.Equal("1058", await Snap("postal_code"));
        Assert.Equal("København K", await Snap("city"));
        Assert.Equal("DK", await Snap("country_code"));
        Assert.Equal("5790000436057", await Snap("electronic_address"));
        Assert.Equal("0088", await Snap("electronic_address_scheme"));
        Assert.Equal("12345678", await Snap("legal_registration_id"));
        Assert.Equal("<null>", await Snap("partner_vat_id"));
        Assert.Equal("2036-12-31", await Snap("retention_until"));

        // the master changes, the document keeps what was on it
        await t.ExecuteAsync("update core.business_partner_pii set legal_name = 'ACME Holding A/S' where tenant_id = @t and partner_id = @p", ("p", partner));
        Assert.Equal("ACME A/S", await Snap("legal_name"));
        // the header partner is not in the hash: the chain still verifies
        Assert.True((await new ChainVerifier(t.App).VerifyLedgerAsync(t.TenantId, t.CompanyId, t.LedgerId)).Ok);
    }

    [Fact]
    public async Task TheRoleOfTheRelationMustFitTheType()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid customer = await NewPartner(t, "c", "CUST", customer: true, supplier: false);
        Guid supplier = await NewPartner(t, "s", "SUPP", customer: false, supplier: true);
        Guid both = await NewPartner(t, "b", "BOTH", customer: true, supplier: true);

        Assert.Equal(["MASTER.PARTNER_INACTIVE"], Codes(await Post(t, "1", Invoice(t, "AR_INVOICE", supplier))));
        Assert.Equal(["MASTER.PARTNER_INACTIVE"], Codes(await Post(t, "2", Invoice(t, "AP_INVOICE", customer))));
        Assert.Equal(CommandStatus.Ok, (await Post(t, "3", Invoice(t, "AR_INVOICE", customer))).Status);
        Assert.Equal(CommandStatus.Ok, (await Post(t, "4", Invoice(t, "AP_CREDIT", supplier))).Status);
        Assert.Equal(CommandStatus.Ok, (await Post(t, "5", Invoice(t, "AR_CREDIT", both))).Status);
        Assert.Equal(CommandStatus.Ok, (await Post(t, "6", Invoice(t, "AP_INVOICE", both))).Status);
    }

    [Fact]
    public async Task APartnerThatIsNotUsableIsRejectedWithTheCodeOfItsState()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid unknown = Guid.NewGuid();
        Guid blocked = await NewPartner(t, "b", "BLK", true, false);
        Guid anonymised = await NewPartner(t, "a", "ANO", true, false);
        Guid restricted = await NewPartner(t, "r", "RES", true, false);
        Guid relBlocked = await NewPartner(t, "rb", "RELBLK", true, false, "BLOCKED");
        Guid noRelation = Guid.NewGuid();
        await t.ExecuteAsync("update core.business_partner set status = 'BLOCKED' where tenant_id = @t and partner_id = @p", ("p", blocked));
        await t.ExecuteAsync("update core.business_partner set status = 'ANONYMISED', anonymised_at = now() where tenant_id = @t and partner_id = @p", ("p", anonymised));
        await t.ExecuteAsync("update core.business_partner set restriction_status = 'RESTRICTED' where tenant_id = @t and partner_id = @p", ("p", restricted));
        await t.ExecuteAsync(
            "insert into core.business_partner (tenant_id, partner_id, partner_code, partner_kind, is_customer, created_by, updated_by) values (@t, @p, 'NOREL', 'ORGANISATION', true, 'x', 'x')",
            ("p", noRelation));

        Assert.Equal(["MASTER.PARTNER_NOT_FOUND"], Codes(await Post(t, "1", Invoice(t, "AR_INVOICE", unknown))));
        Assert.Equal(["MASTER.PARTNER_INACTIVE"], Codes(await Post(t, "2", Invoice(t, "AR_INVOICE", blocked))));
        Assert.Equal(["MASTER.PARTNER_ANONYMISED"], Codes(await Post(t, "3", Invoice(t, "AR_INVOICE", anonymised))));
        Assert.Equal(["MASTER.PARTNER_RESTRICTED"], Codes(await Post(t, "4", Invoice(t, "AR_INVOICE", restricted))));
        Assert.Equal(["MASTER.PARTNER_INACTIVE"], Codes(await Post(t, "5", Invoice(t, "AR_INVOICE", relBlocked))));
        Assert.Equal(["MASTER.PARTNER_NOT_FOUND"], Codes(await Post(t, "6", Invoice(t, "AR_INVOICE", noRelation))));
        Assert.Equal(0L, await t.ScalarAsync<long>("select count(*) from core.journal_document where tenant_id = @t"));
    }

    [Fact]
    public async Task AJournalOrAPaymentMayCarryAPartnerAndItsSnapshotThenHoldsIt()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid supplier = await NewPartner(t, "s", "SUPP", customer: false, supplier: true);
        await t.ExecuteAsync("insert into core.number_series_assignment (tenant_id, company_id, document_type, series_id, effective_from, created_by) values (@t, @c, 'PAYMENT', @s, date '2026-01-01', 'test')", ("s", t.SeriesId));
        var payment = await Post(t, "pay", t.Journal() with { DocumentType = "PAYMENT", PartnerId = supplier });
        Assert.True(payment.Status == CommandStatus.Ok, Describe(payment));
        Assert.Equal("SUPP A/S", await t.ScalarAsync<string>(
            "select legal_name from core.document_legal_snapshot where tenant_id = @t and document_id = @d", ("d", payment.Value!.DocumentId)));
        var plain = await Post(t, "j", t.Journal());
        Assert.Equal("<null>", await t.ScalarAsync<string>(
            "select coalesce(legal_name, '<null>') from core.document_legal_snapshot where tenant_id = @t and document_id = @d", ("d", plain.Value!.DocumentId)));
    }

    [Fact]
    public async Task TheMirrorOfAnInvoiceCarriesTheSnapshotOfTheInvoice()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid partner = await NewPartner(t, "pa", "ACME", true, false);
        var inv = await Post(t, "inv", Invoice(t, "AR_INVOICE", partner));
        await t.ExecuteAsync("update core.business_partner_pii set legal_name = 'Renamed A/S' where tenant_id = @t and partner_id = @p", ("p", partner));
        var rev = await new DocumentReverser(t.App).ReverseAsync(t.TenantId, t.Context("rev", "journal.reverse"), new(inv.Value!.DocumentId, null, "ERROR", null));
        Assert.True(rev.Status == CommandStatus.Ok, Describe(rev));
        Assert.Equal("ACME A/S", await t.ScalarAsync<string>(
            "select legal_name from core.document_legal_snapshot where tenant_id = @t and document_id = @d", ("d", rev.Value!.DocumentId)));
        Assert.Equal(partner.ToString(), await t.ScalarAsync<string>(
            "select partner_id::text from core.document_legal_snapshot where tenant_id = @t and document_id = @d", ("d", rev.Value.DocumentId)));
    }

    [Fact]
    public async Task TheHeaderPartnerIsPartOfTheRequestFingerprint()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid a = await NewPartner(t, "a", "AAA", true, false);
        Guid b = await NewPartner(t, "b", "BBB", true, false);
        var first = await Post(t, "same-key", Invoice(t, "AR_INVOICE", a));
        Assert.Equal(CommandStatus.Ok, first.Status);
        var replay = await Post(t, "same-key", Invoice(t, "AR_INVOICE", a));
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(["IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST"], Codes(await Post(t, "same-key", Invoice(t, "AR_INVOICE", b))));
        Assert.NotEqual(RequestFingerprint.Compute(t.Journal()), RequestFingerprint.Compute(t.Journal() with { PartnerId = a }));
    }
}
