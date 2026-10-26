using FinanceCore.Core.Commands;
using FinanceCore.Core.Retention;

namespace FinanceCore.Data.Tests;

/// <summary>The legal snapshot of every document (02, 4) with the retention date of 12, 4.1, and RETENTION.NO_RULE (06, 13.9).</summary>
[Collection("db")]
public class LegalSnapshotTests
{
    private static async Task<string> UntilAsync(TestTenant t, Guid documentId) =>
        await t.ScalarAsync<string>("select retention_until::text from core.document_legal_snapshot where tenant_id = @t and document_id = @d", ("d", documentId));

    [Fact]
    public async Task EveryDocumentGetsASnapshotWithTheLaterOfTheLedgerAndTheSnapshotRule()
    {
        var t = await TestTenant.CreateReadyAsync();    // LEDGER 10 years from the fiscal year end, LEGAL_SNAPSHOT 5 years from the posting date
        var poster = new JournalDocumentPoster(t.App);
        var doc = await poster.PostAsync(t.TenantId, t.Context("p1"), t.Journal("2026-03-15"));
        Assert.Equal(CommandStatus.Ok, doc.Status);
        Assert.Equal("2036-12-31", await UntilAsync(t, doc.Value!.DocumentId));
        Assert.Equal("1", await t.ScalarAsync<string>(
            "select count(*)::text from core.document_legal_snapshot where tenant_id = @t and partner_id is null and legal_name is null and anonymised_at is null"));

        var reversal = await new DocumentReverser(t.App).ReverseAsync(
            t.TenantId, t.Context("r1", "journal.reverse"), new(doc.Value.DocumentId, null, "ERROR", null));
        Assert.Equal(CommandStatus.Ok, reversal.Status);
        Assert.Equal("2036-12-31", await UntilAsync(t, reversal.Value!.DocumentId));
        Assert.Equal(await t.ScalarAsync<long>("select count(*) from core.journal_document where tenant_id = @t"),
                     await t.ScalarAsync<long>("select count(*) from core.document_legal_snapshot where tenant_id = @t"));
    }

    [Fact]
    public async Task ALongerSnapshotRuleWinsAndALaterRuleVersionOnlyReachesLaterDocuments()
    {
        var t = await TestTenant.CreateReadyAsync();
        var rules = new RetentionRuleCommands(t.App);
        var poster = new JournalDocumentPoster(t.App);
        var manage = (string key) => t.Context(key, RetentionRules.Permission);

        var first = await poster.PostAsync(t.TenantId, t.Context("p1"), t.Journal());
        // the snapshot rule of a second country, 15 years from the posting date (2026-03-15): the longest rule applies
        Assert.Equal(CommandStatus.Ok, (await rules.CreateVersionAsync(t.TenantId, manage("se"),
            new(t.CompanyId, "LEGAL_SNAPSHOT", "SE", 15, "RECORD_DATE", null, null))).Status);
        var second = await poster.PostAsync(t.TenantId, t.Context("p2"), t.Journal());
        Assert.Equal("2036-12-31", await UntilAsync(t, first.Value!.DocumentId));   // unchanged (Q13)
        Assert.Equal("2041-03-15", await UntilAsync(t, second.Value!.DocumentId));
    }

    [Fact]
    public async Task WithoutARuleTheDocumentIsRejectedAndNothingIsWritten()
    {
        foreach (var (withRetention, expected) in new[] { (false, new[] { "LEDGER", "LEGAL_SNAPSHOT" }) })
        {
            var t = await TestTenant.CreateAsync(withRetention: withRetention);
            var r = await new JournalDocumentPoster(t.App).PostAsync(t.TenantId, t.Context("p1"), t.Journal());
            Assert.Equal(["RETENTION.NO_RULE"], r.Errors.Select(e => e.Code));
            Assert.Equal(expected, ((string[])r.Errors[0].Params!["record_classes"]!).Order().ToArray());
            Assert.Equal(0L, await t.ScalarAsync<long>("select count(*) from core.journal_document where tenant_id = @t"));
            Assert.Equal(0L, await t.ScalarAsync<long>("select count(*) from core.document_legal_snapshot where tenant_id = @t"));
            Assert.Equal(0L, await t.ScalarAsync<long>("select coalesce(sum(last_number), 0) from core.number_range_counter where tenant_id = @t"));
            Assert.Equal(0L, await t.ScalarAsync<long>("select last_seq from core.chain_head where tenant_id = @t and chain_type = 'LEDGER'"));
        }
    }

    [Fact]
    public async Task OnlyOneOfTheTwoRulesIsStillNoRuleAndTheMissingClassIsNamed()
    {
        var t = await TestTenant.CreateAsync(withRetention: false);
        var rules = new RetentionRuleCommands(t.App);
        Assert.Equal(CommandStatus.Ok, (await rules.CreateVersionAsync(t.TenantId, t.Context("l", RetentionRules.Permission),
            new(t.CompanyId, "LEDGER", "DK", 10, "FISCAL_YEAR_END", null, null))).Status);
        var r = await new JournalDocumentPoster(t.App).PostAsync(t.TenantId, t.Context("p1"), t.Journal());
        Assert.Equal(["RETENTION.NO_RULE"], r.Errors.Select(e => e.Code));
        Assert.Equal(["LEGAL_SNAPSHOT"], (string[])r.Errors[0].Params!["record_classes"]!);

        Assert.Equal(CommandStatus.Ok, (await rules.CreateVersionAsync(t.TenantId, t.Context("s", RetentionRules.Permission),
            new(t.CompanyId, "LEGAL_SNAPSHOT", "DK", 5, "RECORD_DATE", null, null))).Status);
        Assert.Equal(CommandStatus.Ok, (await new JournalDocumentPoster(t.App).PostAsync(t.TenantId, t.Context("p2"), t.Journal())).Status);
    }

    [Fact]
    public async Task ReadinessNeedsTheLedgerAndTheSnapshotRule()
    {
        var t = await TestTenant.CreateAsync(bare: true, withRetention: false);
        var activator = new CompanyActivator(t.App);
        var check = await activator.CheckReadinessAsync(t.TenantId, t.Context("r", "read.config"), t.CompanyId);
        Assert.Equal(CommandStatus.Rejected, check.Status);
        var failed = check.Errors.Where(e => (string?)e.Params?["check"] == "RETENTION_RULES").ToList();
        Assert.Single(failed);
        Assert.Contains("LEDGER, LEGAL_SNAPSHOT", (string)failed[0].Params!["detail"]!);
        Assert.DoesNotContain("RETENTION_RULES", CompanyActivator.NotChecked);
    }
}
