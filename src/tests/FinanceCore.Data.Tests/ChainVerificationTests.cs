using FinanceCore.Core.Chains;
using FinanceCore.Core.Commands;

namespace FinanceCore.Data.Tests;

/// <summary>chains:verify as a command (06, 7.4): permission, chain types, scope, the kinds of break and that nothing is written.</summary>
[Collection("db")]
public class ChainVerificationTests
{
    private static CommandContext Ctx(TestTenant t, string key = "v") => t.Context(key, ChainVerification.Permission);

    private static string Describe<T>(CommandResult<T> r) => r.Status + ": " + string.Join(", ", r.Errors.Select(e => e.Code + " " + e.Path));

    [Fact]
    public async Task AnIntactChainIsOkForEveryBuiltChainTypeAndWritesNothing()
    {
        var t = await TestTenant.CreateReadyAsync();
        var poster = new JournalDocumentPoster(t.App);
        await poster.PostAsync(t.TenantId, t.Context("p1"), t.Journal());
        await poster.PostAsync(t.TenantId, t.Context("p2"), t.Journal(amount: 5m));
        long logBefore = await t.ScalarAsync<long>("select count(*) from core.command_log where tenant_id = @t");
        long eventsBefore = await t.ScalarAsync<long>("select count(*) from core.outbox_event where tenant_id = @t");

        var verify = new ChainVerification(t.App);
        var ledger = await verify.VerifyAsync(t.TenantId, Ctx(t), new(t.CompanyId, "LEDGER"));
        Assert.True(ledger.Status == CommandStatus.Ok, Describe(ledger));
        Assert.True(ledger.Value!.Ok);
        Assert.Equal(2, ledger.Value.Checked);
        Assert.Equal(t.LedgerId.ToString("D"), ledger.Value.ScopeKey);
        Assert.Null(ledger.Value.FirstBreakSeq);
        Assert.Null(ledger.CommandLogId);

        var named = await verify.VerifyAsync(t.TenantId, Ctx(t), new(t.CompanyId, "LEDGER", t.LedgerId.ToString("D").ToUpperInvariant()));
        Assert.True(named.Value!.Ok);
        Assert.True((await verify.VerifyAsync(t.TenantId, Ctx(t), new(t.CompanyId, "PERIOD_EVENT"))).Value!.Ok);
        Assert.True((await verify.VerifyAsync(t.TenantId, Ctx(t), new(t.CompanyId, "CLEARING"))).Value!.Ok);

        Assert.Equal(logBefore, await t.ScalarAsync<long>("select count(*) from core.command_log where tenant_id = @t"));
        Assert.Equal(eventsBefore, await t.ScalarAsync<long>("select count(*) from core.outbox_event where tenant_id = @t"));
    }

    [Fact]
    public async Task ABrokenChainReportsTheKindOfEveryBreakAndTheFirstBrokenSequence()
    {
        var t = await TestTenant.CreateReadyAsync();
        var poster = new JournalDocumentPoster(t.App);
        for (int i = 1; i <= 3; i++) await poster.PostAsync(t.TenantId, t.Context("p" + i), t.Journal(amount: 100m * i));
        await t.ExecuteAsync("""
            alter table core.journal_line disable trigger journal_line_guard;
            update core.journal_line set amount_lc = amount_lc + 1
             where tenant_id = @t and document_id = (select document_id from core.journal_document where tenant_id = @t and chain_seq = 2) and line_no = 1;
            alter table core.journal_line enable trigger journal_line_guard;
            alter table core.document_legal_snapshot disable trigger document_legal_snapshot_append_only;
            delete from core.document_legal_snapshot where tenant_id = @t and document_id = (select document_id from core.journal_document where tenant_id = @t and chain_seq = 3);
            alter table core.document_legal_snapshot enable trigger document_legal_snapshot_append_only;
            """);

        var r = await new ChainVerification(t.App).VerifyAsync(t.TenantId, Ctx(t), new(t.CompanyId, "LEDGER"));
        Assert.Equal(CommandStatus.Ok, r.Status);   // the breaks are the result of a successful verification
        Assert.False(r.Value!.Ok);
        Assert.Equal(2L, r.Value.FirstBreakSeq);
        Assert.Contains(r.Value.Breaks, b => b is { Kind: "hash", Code: "CHAIN.ROW_HASH_MISMATCH", ChainSeq: 2 });
        Assert.Contains(r.Value.Breaks, b => b is { Kind: "balance", Code: "DOCUMENT.NOT_BALANCED", ChainSeq: 2 });
        Assert.Contains(r.Value.Breaks, b => b is { Kind: "snapshot", Code: "SNAPSHOT.MISSING", ChainSeq: 3 });
    }

    [Fact]
    public async Task PermissionTypeScopeAndCompanyAreChecked()
    {
        var t = await TestTenant.CreateReadyAsync();
        var verify = new ChainVerification(t.App);
        string[] Codes(CommandResult<ChainVerificationResult> r) => r.Errors.Select(e => e.Code).ToArray();

        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await verify.VerifyAsync(t.TenantId, t.Context("a", "read.audit"), new(t.CompanyId, "LEDGER"))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await verify.VerifyAsync(t.TenantId, Ctx(t), new(t.CompanyId, "NOT_A_CHAIN"))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await verify.VerifyAsync(t.TenantId, Ctx(t), new(Guid.Empty, "LEDGER"))));
        Assert.Equal(["LIMIT.CHAIN_TYPE_NOT_BUILT"], Codes(await verify.VerifyAsync(t.TenantId, Ctx(t), new(t.CompanyId, "STOCK"))));
        Assert.Equal(["LIMIT.CHAIN_RANGE_NOT_BUILT"], Codes(await verify.VerifyAsync(t.TenantId, Ctx(t), new(t.CompanyId, "LEDGER", null, 1, 5))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await verify.VerifyAsync(t.TenantId, Ctx(t), new(t.CompanyId, "LEDGER", null, 5, 1))));
        Assert.Equal(["COMPANY.NOT_FOUND"], Codes(await verify.VerifyAsync(t.TenantId, Ctx(t), new(Guid.NewGuid(), "LEDGER"))));
        Assert.Equal(["LEDGER.NOT_FOUND"], Codes(await verify.VerifyAsync(t.TenantId, Ctx(t), new(t.CompanyId, "LEDGER", Guid.NewGuid().ToString("D")))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await verify.VerifyAsync(t.TenantId, Ctx(t), new(t.CompanyId, "CLEARING", "x"))));
    }

    [Fact]
    public void EveryBreakCodeOfTheVerifierHasAKind()
    {
        string[] codes =
        [
            "CHAIN.SEQUENCE_GAP", "CHAIN.PREV_HASH_MISMATCH", "CHAIN.HASH_VERSION_UNSUPPORTED", "CHAIN.ROW_HASH_MISMATCH", "CHAIN.HEAD_MISMATCH",
            "CHAIN.HEAD_HASH_MISMATCH", "CHAIN.HEAD_MISSING", "PERIOD.STATUS_MISMATCH", "CLEARING.DOCUMENT_MISSING", "CLEARING.DOCUMENT_MISMATCH",
            "CLEARING.NO_ITEMS", "CLEARING.NOT_BALANCED", "OPEN_ITEM.OVER_CLEARED", "OPEN_ITEM.CACHE_MISMATCH", "CHAIN.DOCUMENT_WITHOUT_LINES",
            "DOCUMENT.NOT_BALANCED", "NUMBER.GAP", "SNAPSHOT.MISSING", "SNAPSHOT.RETENTION_SHORTER_THAN_RULE", "SNAPSHOT.PARTNER_DATA_INCONSISTENT"
        ];
        var kinds = codes.Select(ChainRules.KindOf).Distinct().Order().ToArray();
        Assert.Equal(new[] { "balance", "digest_mismatch", "gap", "hash", "head", "link", "number_continuity", "s_column_mismatch", "snapshot", "structure" }, kinds);
    }
}
