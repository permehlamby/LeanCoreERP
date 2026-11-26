using FinanceCore.Core.Commands;
using FinanceCore.Core.Hashing;
using FinanceCore.Core.Posting;
using FinanceCore.Core.Reads;

namespace FinanceCore.Data.Tests;

/// <summary>intercompany-documents:post (06, 5.5, ADR-016, ADR-226): two documents in one transaction, the marker on the base lines, the same group amounts, atomic.</summary>
[Collection("db")]
public class IntercompanyTests
{
    private static string Describe<T>(CommandResult<T> r) => r.Status + ": " + string.Join(", ", r.Errors.Select(e => $"{e.Code} {e.Path}"));
    private static string[] Codes<T>(CommandResult<T> r) => r.Errors.Select(e => e.Code).ToArray();
    private static CommandContext Ctx(TestTenant t, string key) => t.Context(key, IntercompanyRules.PostPermission);

    internal sealed record Second(Guid CompanyId, Guid Expense, Guid IcPayable, Guid IcReceivable, Guid Bank = default);

    private static async Task<(Guid Receivable, Guid Payable)> IcAccounts(TestTenant t, Guid company, Guid counterpart, bool payableRole = true)
    {
        Guid rec = Guid.NewGuid(), pay = Guid.NewGuid();
        await t.ExecuteAsync(
            "insert into core.gl_account (tenant_id, company_id, account_id, account_no, name, account_type, tax_code_rule) values (@t, @co, @r, '1500', 'IC receivable', 'ASSET', 'FORBIDDEN'), (@t, @co, @p, '2500', 'IC payable', 'LIABILITY', 'FORBIDDEN'); " +
            "insert into core.account_role_assignment (tenant_id, company_id, role_code, account_id, counterpart_company_id, effective_from) select @t, @co, v.role, v.acc, @cp, timestamptz '2026-01-01 00:00:00+00' from (values ('IC_RECEIVABLE', @r), ('IC_PAYABLE', @p)) v(role, acc) where v.role = 'IC_RECEIVABLE' or @pr",
            ("co", company), ("r", rec), ("p", pay), ("cp", counterpart), ("pr", payableRole));
        return (rec, pay);
    }

    /// <summary>A second company in the same group, with the chart, series, year, ledger and chain head that a posting needs, and the IC accounts of both companies.</summary>
    internal static async Task<Second> AddSecond(TestTenant t, bool icPayableRole = true)
    {
        Guid c2 = Guid.NewGuid(), ledger = Guid.NewGuid(), series = Guid.NewGuid(), expense = Guid.NewGuid(), bank = Guid.NewGuid();
        await t.ExecuteAsync(
            "insert into core.company (tenant_id, company_id, company_code, legal_name, country_code, local_currency, reporting_currency, fiscal_calendar_id, status) " +
            "select tenant_id, @n, 'C2', 'Other A/S', country_code, local_currency, reporting_currency, fiscal_calendar_id, 'ACTIVE' from core.company where tenant_id = @t and company_id = @c; " +
            "insert into core.company_setting_version (tenant_id, company_id, effective_from) values (@t, @n, timestamptz '2026-01-01 00:00:00+00'); " +
            "insert into core.retention_rule (tenant_id, company_id, rule_id, effective_from, record_class, country_code, retention_years, start_event, created_by) " +
            "select @t, @n, gen_random_uuid(), timestamptz '2000-01-01 00:00:00+00', v.cls, 'DK', v.years, v.start, 'test' from (values ('LEDGER', 10, 'FISCAL_YEAR_END'), ('LEGAL_SNAPSHOT', 5, 'RECORD_DATE')) v(cls, years, start); " +
            "insert into core.ledger (tenant_id, company_id, ledger_id, ledger_code) values (@t, @n, @l, 'L1'); " +
            "insert into core.group_membership (tenant_id, company_id, valid_from, group_id) select tenant_id, @n, date '2026-01-01', group_id from core.group_membership where tenant_id = @t and company_id = @c; " +
            "insert into core.gl_account (tenant_id, company_id, account_id, account_no, name, account_type, tax_code_rule) values (@t, @n, @bank, '1000', 'Bank', 'ASSET', 'FORBIDDEN'), (@t, @n, @e, '4000', 'Expenses', 'EXPENSE', 'FORBIDDEN'); " +
            "insert into core.number_series (tenant_id, company_id, series_id, series_code, prefix, include_fiscal_year, separator, number_length, is_legal) values (@t, @n, @s, 'GL-JRN', 'GL', true, '-', 6, true); " +
            "insert into core.number_series_assignment (tenant_id, company_id, document_type, series_id, effective_from, created_by) values (@t, @n, 'JOURNAL', @s, date '2026-01-01', 'test'); " +
            "insert into core.fiscal_year (tenant_id, company_id, fiscal_year, calendar_id, start_date, end_date, opened_by) select @t, @n, 2026, fiscal_calendar_id, date '2026-01-01', date '2026-12-31', 'test' from core.company where tenant_id = @t and company_id = @n; " +
            "insert into core.fiscal_period (tenant_id, company_id, fiscal_year, period_no, period_type, start_date, end_date) select @t, @n, 2026, m, 'REGULAR', make_date(2026, m, 1), (make_date(2026, m, 1) + interval '1 month - 1 day')::date from generate_series(1, 12) m; " +
            "insert into core.number_range_counter (tenant_id, company_id, series_id, fiscal_year, last_number) values (@t, @n, @s, 2026, 0); " +
            "insert into core.chain_head (tenant_id, company_id, chain_type, scope_key, last_seq, last_hash, hash_version) values (@t, @n, 'LEDGER', @l::text, 0, @genesis, 2)",
            ("n", c2), ("c", t.CompanyId), ("l", ledger), ("s", series), ("e", expense), ("bank", bank), ("genesis", LedgerHash2.Genesis(t.TenantId, c2, "LEDGER", ledger.ToString("D"))));
        var (rec2, pay2) = await IcAccounts(t, c2, t.CompanyId, icPayableRole);
        var (rec1, _) = await IcAccounts(t, t.CompanyId, c2);
        return new Second(c2, expense, pay2, rec1, bank);
    }

    private static PostIntercompanyDocumentRequest Pair(TestTenant t, Second s, decimal amount = 1000m, string date = "2026-03-15") =>
        new(DateOnly.Parse(date), DateOnly.Parse(date), "DKK", "IC-2026-001",
        [
            new IntercompanySide(t.CompanyId, "JOURNAL", [new RequestLine("1", t.Revenue, -amount, "Recharge")], HeaderText: "Sender"),
            new IntercompanySide(s.CompanyId, "JOURNAL", [new RequestLine("1", s.Expense, amount, "Recharge")], HeaderText: "Receiver")
        ]);

    [Fact]
    public async Task APairIsPostedInOneTransactionWithTheMarkerOnTheBaseLinesAndEqualGroupAmounts()
    {
        var t = await TestTenant.CreateReadyAsync();
        var s = await AddSecond(t);
        var r = await new IntercompanyCommands(t.App).PostAsync(t.TenantId, Ctx(t, "ic1"), Pair(t, s));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        Assert.Equal(2, r.Value!.Documents.Count);
        Assert.Equal(t.CompanyId, r.Value.Documents[0].CompanyId);
        Assert.Equal(s.CompanyId, r.Value.Documents[1].CompanyId);

        // one transaction id on both documents, the other company as counterpart, the derived keys
        Assert.Equal(2L, await t.ScalarAsync<long>("select count(*) from core.journal_document where tenant_id = @t and ic_transaction_id = @i", ("i", r.Value.IcTransactionId)));
        Assert.Equal(s.CompanyId.ToString(), await t.ScalarAsync<string>("select ic_counterpart_company_id::text from core.journal_document where tenant_id = @t and document_id = @d", ("d", r.Value.Documents[0].DocumentId)));
        Assert.Equal("ic1#1", await t.ScalarAsync<string>("select idempotency_key from core.journal_document where tenant_id = @t and document_id = @d", ("d", r.Value.Documents[0].DocumentId)));
        Assert.Equal("ic1#2", await t.ScalarAsync<string>("select idempotency_key from core.journal_document where tenant_id = @t and document_id = @d", ("d", r.Value.Documents[1].DocumentId)));

        // the generated line: a receivable on the counterpart for the sender, a payable for the receiver; both BASE lines carry the marker
        var a = r.Value.Documents[0].Lines; var b = r.Value.Documents[1].Lines;
        Assert.Equal(2, a.Count); Assert.Equal(2, b.Count);
        Assert.Equal(s.IcReceivable, a.Single(l => l.AmountTc > 0m).AccountId);
        Assert.Equal(s.IcPayable, b.Single(l => l.AmountTc < 0m).AccountId);
        Assert.Equal(4L, await t.ScalarAsync<long>("select count(*) from core.journal_line where tenant_id = @t and ic_counterpart_company_id is not null"));
        Assert.Equal(0L, await t.ScalarAsync<long>("select count(*) from core.journal_line where tenant_id = @t and line_type <> 'BASE' and ic_counterpart_company_id is not null"));
        // the group amount is the same on both sides and eliminates to zero
        Assert.Equal(0m, a.Sum(l => l.AmountGc) + b.Sum(l => l.AmountGc));
        Assert.Equal(a.Single(l => l.AmountTc > 0m).AmountGc, -b.Single(l => l.AmountTc < 0m).AmountGc);

        // the ledger hash is verifiable with the marker in it
        var verify = await new ChainVerification(t.App).VerifyAsync(t.TenantId, t.Context("v", ChainVerification.Permission), new(t.CompanyId, "LEDGER", t.LedgerId.ToString("D")));
        Assert.True(verify.Status == CommandStatus.Ok, Describe(verify));
        Assert.True(verify.Value!.Ok);

        // balances:query sees the marker as a key and as a filter (23, 7.1)
        var reads = new LedgerReads(t.App);
        var byIc = await reads.BalancesAsync(t.TenantId, t.Context("rd", "read.ledger"), new(t.CompanyId, "CUMULATIVE", AsOfDate: new DateOnly(2026, 6, 30), GroupBy: ["ic_counterpart_company_id"]));
        Assert.Equal(s.CompanyId.ToString(), Assert.Single(byIc.Value!.Rows).Keys["ic_counterpart_company_id"]);
        Assert.Equal(2L, (await reads.BalancesAsync(t.TenantId, t.Context("rd2", "read.ledger"), new(t.CompanyId, "CUMULATIVE", AsOfDate: new DateOnly(2026, 6, 30), IsIntercompany: true))).Value!.Rows.Sum(x => x.LineCount));

        // one side alone is never reversed by the generic command
        var rev = await new DocumentReverser(t.App).ReverseAsync(t.TenantId, t.Context("rev", "journal.reverse"), new(r.Value.Documents[0].DocumentId, null, "CORRECTION", "x"));
        Assert.Equal(["REVERSAL.TYPE_NOT_REVERSIBLE"], Codes(rev));
    }

    [Fact]
    public async Task ThePairIsReversedAtomicallyWithMirrorsOnTheSameTransactionId()
    {
        var t = await TestTenant.CreateReadyAsync();
        var s = await AddSecond(t);
        var cmds = new IntercompanyCommands(t.App);
        var posted = (await cmds.PostAsync(t.TenantId, Ctx(t, "p"), Pair(t, s))).Value!;

        var r = await cmds.ReverseAsync(t.TenantId, Ctx(t, "rev1"), new(posted.IcTransactionId, "CORRECTION", ReasonText: "Wrong recharge"));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        Assert.Equal(2, r.Value!.Documents.Count);
        Assert.Equal(new[] { t.CompanyId, s.CompanyId }.Order(), r.Value.Documents.Select(d => d.CompanyId).Order());

        // both originals are reversed, the mirrors carry the id and the marker, the pair nets to zero per company
        Assert.Equal(2L, await t.ScalarAsync<long>("select count(*) from core.journal_document where tenant_id = @t and ic_transaction_id = @i and status = 'reversed'", ("i", posted.IcTransactionId)));
        Assert.Equal(4L, await t.ScalarAsync<long>("select count(*) from core.journal_document where tenant_id = @t and ic_transaction_id = @i", ("i", posted.IcTransactionId)));
        Assert.Equal(4L, await t.ScalarAsync<long>("select count(*) from core.journal_line l join core.journal_document d on d.tenant_id = l.tenant_id and d.document_id = l.document_id where l.tenant_id = @t and d.reversal_of_document_id is not null and l.ic_counterpart_company_id is not null"));
        Assert.Equal(0m, decimal.Parse(await t.ScalarAsync<string>("select coalesce(sum(amount_gc), 0)::text from core.journal_line where tenant_id = @t"), System.Globalization.CultureInfo.InvariantCulture));
        var reads = new LedgerReads(t.App);
        var net = await reads.BalancesAsync(t.TenantId, t.Context("rd", "read.ledger"), new(t.CompanyId, "CUMULATIVE", AsOfDate: new DateOnly(2026, 6, 30), IsIntercompany: true));
        Assert.Equal(0m, net.Value!.TotalDebit!.Lc + net.Value.TotalCredit!.Lc);
        foreach (Guid company in new[] { t.CompanyId, s.CompanyId })
        {
            Guid ledger = Guid.Parse(await t.ScalarAsync<string>("select ledger_id::text from core.ledger where tenant_id = @t and company_id = @co", ("co", company)));
            var verify = await new ChainVerification(t.App).VerifyAsync(t.TenantId, t.Context("v" + company, ChainVerification.Permission), new(company, "LEDGER", ledger.ToString("D")));
            Assert.True(verify.Value!.Ok, Describe(verify));
        }

        // replay, a second reversal, the reason, an unknown id
        var replay = await cmds.ReverseAsync(t.TenantId, Ctx(t, "rev1"), new(posted.IcTransactionId, "CORRECTION", ReasonText: "Wrong recharge"));
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(r.Value.Documents.Select(d => d.DocumentId), replay.Value!.Documents.Select(d => d.DocumentId));
        Assert.Equal(["REVERSAL.ALREADY_REVERSED"], Codes(await cmds.ReverseAsync(t.TenantId, Ctx(t, "rev2"), new(posted.IcTransactionId, "ERROR"))));
        Assert.Equal(["REVERSAL.REASON_NOT_ALLOWED"], Codes(await cmds.ReverseAsync(t.TenantId, Ctx(t, "rev3"), new(posted.IcTransactionId, "RERUN"))));
        Assert.Equal(["REVERSAL.NOT_FOUND"], Codes(await cmds.ReverseAsync(t.TenantId, Ctx(t, "rev4"), new(Guid.NewGuid(), "ERROR"))));
        Assert.Equal(["IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST"], Codes(await cmds.ReverseAsync(t.TenantId, Ctx(t, "rev1"), new(posted.IcTransactionId, "ERROR"))));
        // the post still answers with its two documents
        Assert.Equal(2, (await cmds.PostAsync(t.TenantId, Ctx(t, "p"), Pair(t, s))).Value!.Documents.Count);
    }

    private static CommandContext RecCtx(TestTenant t, string key = "rec") => t.Context(key, "read.ledger", IcReconciliationRules.CrossCompanyPermission);

    [Fact]
    public async Task APostedPairReconcilesToZeroInBothViews()
    {
        var t = await TestTenant.CreateReadyAsync();
        var s = await AddSecond(t);
        await new IntercompanyCommands(t.App).PostAsync(t.TenantId, Ctx(t, "p"), Pair(t, s));
        var reads = new IcReconciliationReads(t.App);
        var asOf = new DateOnly(2026, 6, 30);

        var balances = await reads.QueryAsync(t.TenantId, RecCtx(t), new(asOf, CompanyIds: [t.CompanyId, s.CompanyId]));
        Assert.True(balances.Status == CommandStatus.Ok, Describe(balances));
        var pair = Assert.Single(balances.Value!.Pairs!);
        Guid first = new[] { t.CompanyId, s.CompanyId }.Min();
        Assert.Equal(first, pair.CompanyA);
        // the sender holds a receivable of 1000 DKK, the receiver a payable: 134 EUR each way, opposite signs
        decimal senderGc = pair.CompanyA == t.CompanyId ? pair.AToB.Gc : pair.BToA.Gc, receiverGc = pair.CompanyA == t.CompanyId ? pair.BToA.Gc : pair.AToB.Gc;
        Assert.Equal(134m, senderGc);
        Assert.Equal(-134m, receiverGc);
        Assert.Equal(0m, pair.DifferenceGc);
        Assert.Equal(0m, pair.DifferenceTc["DKK"]);
        Assert.Equal(0m, pair.PnlDifferenceGc);
        Assert.Empty(pair.Flags);
        Assert.Equal(2, balances.Value.Positions.Count);

        var viaGroup = await reads.QueryAsync(t.TenantId, RecCtx(t, "g"), new(asOf, GroupId: Guid.Parse(await t.ScalarAsync<string>("select group_id::text from core.group_membership where tenant_id = @t and company_id = @c", ("c", t.CompanyId)))));
        Assert.Equal(2, viaGroup.Value!.Companies.Count);
        Assert.Single(viaGroup.Value.Pairs!);

        var documents = await reads.QueryAsync(t.TenantId, RecCtx(t, "d"), new(asOf, CompanyIds: [t.CompanyId, s.CompanyId], View: "DOCUMENTS"));
        Assert.Empty(documents.Value!.Findings!);
        // before the posting date the pair is empty
        Assert.Empty((await reads.QueryAsync(t.TenantId, RecCtx(t, "e"), new(new DateOnly(2026, 2, 1), CompanyIds: [t.CompanyId, s.CompanyId]))).Value!.Pairs!);

        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await reads.QueryAsync(t.TenantId, t.Context("x", "read.ledger"), new(asOf, CompanyIds: [t.CompanyId, s.CompanyId]))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await reads.QueryAsync(t.TenantId, RecCtx(t, "y"), new(asOf, CompanyIds: [t.CompanyId]))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await reads.QueryAsync(t.TenantId, RecCtx(t, "z"), new(asOf, CompanyIds: [t.CompanyId, s.CompanyId], View: "OTHER"))));
        Assert.Equal(["READ.NOT_FOUND"], Codes(await reads.QueryAsync(t.TenantId, RecCtx(t, "w"), new(asOf, CompanyIds: [t.CompanyId, Guid.NewGuid()]))));
    }

    [Fact]
    public async Task ADifferenceBeyondTheToleranceIsFlaggedAndADocumentFindingNamesIt()
    {
        var t = await TestTenant.CreateReadyAsync();
        var s = await AddSecond(t);
        var posted = (await new IntercompanyCommands(t.App).PostAsync(t.TenantId, Ctx(t, "p"), Pair(t, s))).Value!;
        // a defect or migrated data: the group amount of the sender's IC line differs by 5, and the receiver's document is marked reversed alone (the commands cannot produce either)
        await t.ExecuteAsync(
            "set session_replication_role = replica; " +
            "update core.journal_line set amount_gc = amount_gc + 5 where tenant_id = @t and document_id = @d and amount_tc > 0; " +
            "update core.journal_document set status = 'reversed' where tenant_id = @t and document_id = @e; " +
            "set session_replication_role = origin",
            ("d", posted.Documents[0].DocumentId), ("e", posted.Documents[1].DocumentId));
        var reads = new IcReconciliationReads(t.App);
        var asOf = new DateOnly(2026, 6, 30);

        var strict = await reads.QueryAsync(t.TenantId, RecCtx(t), new(asOf, CompanyIds: [t.CompanyId, s.CompanyId]));
        var pair = Assert.Single(strict.Value!.Pairs!);
        Assert.Equal(5m, Math.Abs(pair.DifferenceGc));
        Assert.Contains("BALANCE_DIFFERENCE", pair.Flags);
        // within the tolerance nothing is flagged
        var loose = await reads.QueryAsync(t.TenantId, RecCtx(t, "l"), new(asOf, CompanyIds: [t.CompanyId, s.CompanyId], ToleranceGc: 5m));
        Assert.DoesNotContain("BALANCE_DIFFERENCE", loose.Value!.Pairs![0].Flags);

        var findings = (await reads.QueryAsync(t.TenantId, RecCtx(t, "d"), new(asOf, CompanyIds: [t.CompanyId, s.CompanyId], View: "DOCUMENTS"))).Value!.Findings!;
        Assert.Equal(["GC_DIFFERENT", "ONE_SIDE_REVERSED"], findings.Select(f => f.Finding).Order().ToArray());
        Assert.All(findings, f => Assert.Equal(posted.IcTransactionId, f.IcTransactionId));
        Assert.Equal(2, findings[0].Documents.Count);
    }

    [Fact]
    public void TheFlagsFollowTheToleranceInAbsoluteGroupAmount()
    {
        Assert.Empty(IcReconciliationRules.FlagsOf(0m, 0m, 0m));
        Assert.Equal(["BALANCE_DIFFERENCE"], IcReconciliationRules.FlagsOf(-3m, 0m, 2m));
        Assert.Equal(["BALANCE_DIFFERENCE", "PNL_DIFFERENCE"], IcReconciliationRules.FlagsOf(3m, -4m, 2m));
        Assert.Empty(IcReconciliationRules.FlagsOf(2m, -2m, 2m));
    }

    [Fact]
    public async Task AClosedPeriodInOneCompanyKeepsBothSidesPosted()
    {
        var t = await TestTenant.CreateReadyAsync();
        var s = await AddSecond(t);
        var cmds = new IntercompanyCommands(t.App);
        var posted = (await cmds.PostAsync(t.TenantId, Ctx(t, "p"), Pair(t, s))).Value!;
        await t.SetPeriodStatusAsync(3, "HARD_CLOSED");

        var r = await cmds.ReverseAsync(t.TenantId, Ctx(t, "rev"), new(posted.IcTransactionId, "CORRECTION"));
        Assert.Equal(CommandStatus.Rejected, r.Status);
        Assert.StartsWith("PERIOD.", r.Errors[0].Code);
        Assert.Equal(0L, await t.ScalarAsync<long>("select count(*) from core.journal_document where tenant_id = @t and status = 'reversed'"));
        Assert.Equal(2L, await t.ScalarAsync<long>("select count(*) from core.journal_document where tenant_id = @t"));
        // a date in an open period works for both
        var later = await cmds.ReverseAsync(t.TenantId, Ctx(t, "rev-later"), new(posted.IcTransactionId, "CORRECTION", ReversalDate: new DateOnly(2026, 4, 2)));
        Assert.True(later.Status == CommandStatus.Ok, Describe(later));
    }

    [Fact]
    public async Task TheSameKeyReplaysAndAChangedRequestIsRefused()
    {
        var t = await TestTenant.CreateReadyAsync();
        var s = await AddSecond(t);
        var cmds = new IntercompanyCommands(t.App);
        var first = await cmds.PostAsync(t.TenantId, Ctx(t, "ic2"), Pair(t, s));
        var replay = await cmds.PostAsync(t.TenantId, Ctx(t, "ic2"), Pair(t, s));
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(first.Value!.IcTransactionId, replay.Value!.IcTransactionId);
        Assert.Equal(first.Value.Documents.Select(d => d.DocumentId), replay.Value.Documents.Select(d => d.DocumentId));
        Assert.Equal(["IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST"], Codes(await cmds.PostAsync(t.TenantId, Ctx(t, "ic2"), Pair(t, s, amount: 900m))));
        Assert.Equal(2L, await t.ScalarAsync<long>("select count(*) from core.journal_document where tenant_id = @t"));
    }

    [Fact]
    public async Task TheShapeIsCheckedAndASideThatFailsRollsBackTheOther()
    {
        var t = await TestTenant.CreateReadyAsync();
        var s = await AddSecond(t, icPayableRole: false);
        var cmds = new IntercompanyCommands(t.App);
        var pair = Pair(t, s);

        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await cmds.PostAsync(t.TenantId, t.Context("a", "journal.post"), pair)));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await cmds.PostAsync(t.TenantId, Ctx(t, "b"), pair with { Sides = [pair.Sides[0]] })));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await cmds.PostAsync(t.TenantId, Ctx(t, "c"), pair with { Sides = [pair.Sides[0], pair.Sides[0]] })));
        Assert.Equal(["BALANCE.UNBALANCED_TC"], Codes(await cmds.PostAsync(t.TenantId, Ctx(t, "d"),
            pair with { Sides = [pair.Sides[0], pair.Sides[1] with { Lines = [new RequestLine("1", s.Expense, 900m)] }] })));

        // the receiver has no IC_PAYABLE role: the sender's document is not kept
        var missing = await cmds.PostAsync(t.TenantId, Ctx(t, "e"), pair);
        Assert.Equal(["CONFIG.ROLE_NOT_ASSIGNED"], Codes(missing));
        Assert.Equal("sides[1]", missing.Errors[0].Path);
        Assert.Equal(0L, await t.ScalarAsync<long>("select count(*) from core.journal_document where tenant_id = @t"));
        Assert.Equal(0L, await t.ScalarAsync<long>("select count(*) from core.command_log where tenant_id = @t and command_name = 'intercompany-documents:post'"));
    }
}
