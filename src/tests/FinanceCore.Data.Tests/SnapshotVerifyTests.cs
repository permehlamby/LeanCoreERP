using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;
using FinanceCore.Core.Posting;

namespace FinanceCore.Data.Tests;

/// <summary>chains:verify of the LEDGER chain also checks the legal snapshots (02, 4, 12, 4.1): they are outside the hash, so these checks are the
/// evidence that every document has one, that its retention was not shortened and that its personal columns fit its partner.</summary>
[Collection("db")]
public class SnapshotVerifyTests
{
    private static Dictionary<string, object?> D(params (string, object?)[] f) => f.ToDictionary(x => x.Item1, x => x.Item2);

    private static async Task<(TestTenant T, Guid Invoice, Guid Journal)> SetupAsync()
    {
        var t = await TestTenant.CreateReadyAsync();
        var partner = await new PartnerCommands(t.App).CreateAsync(t.TenantId, t.Context("pa", "write.master"), new CreatePartnerRequest(
            D(("partner_code", "ACME"), ("partner_kind", "ORGANISATION"), ("is_customer", true)),
            D(("legal_name", "ACME A/S"), ("country_code", "DK")),
            [new PartnerRelationInput(t.CompanyId, D(("is_customer", true)))]));
        var poster = new JournalDocumentPoster(t.App);
        var journal = await poster.PostAsync(t.TenantId, t.Context("j"), t.Journal());
        var invoice = await poster.PostAsync(t.TenantId, t.Context("i"), t.Journal() with { DocumentType = "AR_INVOICE", PartnerId = partner.Value!.EntityId });
        Assert.Equal(CommandStatus.Ok, invoice.Status);
        return (t, invoice.Value!.DocumentId, journal.Value!.DocumentId);
    }

    private static Task<VerificationResult> Verify(TestTenant t) => new ChainVerifier(t.App).VerifyLedgerAsync(t.TenantId, t.CompanyId, t.LedgerId);

    // The protection triggers stop these changes for every application role; the migration role can switch one off, which is the attack to reveal
    private static Task Tamper(TestTenant t, string trigger, string sql, params (string, object)[] p) =>
        t.ExecuteAsync($"alter table core.document_legal_snapshot disable trigger {trigger}; {sql}; alter table core.document_legal_snapshot enable trigger {trigger};", p);

    [Fact]
    public async Task AnUntouchedLedgerWithSnapshotsVerifies()
    {
        var (t, _, _) = await SetupAsync();
        var v = await Verify(t);
        Assert.True(v.Ok, string.Join("; ", v.Problems.Select(p => p.Code + " " + p.Detail)));
        Assert.Equal(2, v.DocumentsChecked);
    }

    [Fact]
    public async Task AMissingSnapshotIsDetected()
    {
        var (t, invoice, _) = await SetupAsync();
        await Tamper(t, "document_legal_snapshot_append_only", "delete from core.document_legal_snapshot where tenant_id = @t and document_id = @d", ("d", invoice));
        var v = await Verify(t);
        Assert.False(v.Ok);
        Assert.Equal(["SNAPSHOT.MISSING"], v.Problems.Select(p => p.Code));
        Assert.Equal(2L, v.Problems[0].ChainSeq);
    }

    [Fact]
    public async Task AShortenedRetentionIsDetectedAndALongerOneIsNot()
    {
        var (t, invoice, journal) = await SetupAsync();   // the rules give 2036-12-31
        await Tamper(t, "document_legal_snapshot_append_only",
            "update core.document_legal_snapshot set retention_until = date '2036-12-30' where tenant_id = @t and document_id = @d", ("d", invoice));
        var shortened = await Verify(t);
        Assert.Equal(["SNAPSHOT.RETENTION_SHORTER_THAN_RULE"], shortened.Problems.Select(p => p.Code));
        Assert.Equal(2L, shortened.Problems[0].ChainSeq);
        Assert.Contains("2036-12-30", shortened.Problems[0].Detail);

        await Tamper(t, "document_legal_snapshot_append_only",
            "update core.document_legal_snapshot set retention_until = date '2036-12-30' where tenant_id = @t and document_id = @d", ("d", journal));
        var both = await Verify(t);
        Assert.Equal(new long[] { 1, 2 }, both.Problems.Select(p => p.ChainSeq!.Value).Order().ToArray());

        // an extension (retention:extend, later) lengthens: that is not a break
        await Tamper(t, "document_legal_snapshot_append_only",
            "update core.document_legal_snapshot set retention_until = date '2050-12-31' where tenant_id = @t and document_id in (@d, @e)", ("d", invoice), ("e", journal));
        Assert.True((await Verify(t)).Ok);
    }

    [Fact]
    public async Task PersonalColumnsThatDoNotFitThePartnerAreDetected()
    {
        var (t, invoice, journal) = await SetupAsync();
        await Tamper(t, "document_legal_snapshot_append_only",
            "update core.document_legal_snapshot set legal_name = null where tenant_id = @t and document_id = @d", ("d", invoice));
        await Tamper(t, "document_legal_snapshot_append_only",
            "update core.document_legal_snapshot set legal_name = 'Ghost A/S' where tenant_id = @t and document_id = @d", ("d", journal));
        var v = await Verify(t);
        Assert.Equal(["SNAPSHOT.PARTNER_DATA_INCONSISTENT", "SNAPSHOT.PARTNER_DATA_INCONSISTENT"], v.Problems.Select(p => p.Code));
        Assert.Equal([1L, 2L], v.Problems.Select(p => p.ChainSeq!.Value).Order().ToArray());

        // an anonymised snapshot (retention job, later) has no name and keeps its partner: not a break
        await Tamper(t, "document_legal_snapshot_append_only",
            "update core.document_legal_snapshot set legal_name = null, anonymised_at = now() where tenant_id = @t and document_id = @d", ("d", invoice));
        await Tamper(t, "document_legal_snapshot_append_only",
            "update core.document_legal_snapshot set legal_name = null where tenant_id = @t and document_id = @d", ("d", journal));
        Assert.True((await Verify(t)).Ok);
    }

    [Fact]
    public async Task ARuleThatCameAfterTheDocumentDoesNotMakeItsSnapshotTooShort()
    {
        var (t, _, _) = await SetupAsync();
        // a longer rule from now on (Q13): the documents posted before it keep the retention they were given
        var made = await new RetentionRuleCommands(t.App).CreateVersionAsync(t.TenantId, t.Context("r", "retention.manage"),
            new(t.CompanyId, "LEDGER", "SE", 30, "RECORD_DATE", null, null));
        Assert.Equal(CommandStatus.Ok, made.Status);
        Assert.True((await Verify(t)).Ok);
    }
}
