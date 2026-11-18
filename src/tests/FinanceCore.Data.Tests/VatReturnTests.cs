using FinanceCore.Core.Chains;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;
using FinanceCore.Core.Posting;
using FinanceCore.Core.Roles;

namespace FinanceCore.Data.Tests;

/// <summary>VAT returns and their settlement (08, 6.3 to 7.3a; built choices in 07, 13.2n).</summary>
[Collection("db")]
public class VatReturnTests
{
    private static string Describe<T>(CommandResult<T> r) => r.Status + ": " + string.Join(", ", r.Errors.Select(e => $"{e.Code} {e.Path}"));
    private static string[] Codes<T>(CommandResult<T> r) => r.Errors.Select(e => e.Code).ToArray();
    private static Dictionary<string, object?> D(params (string, object?)[] f) => f.ToDictionary(x => x.Item1, x => x.Item2);
    private static CommandContext Cfg(TestTenant t, string key) => t.Context(key, "write.config");

    private sealed record World(Guid Registration, Guid Payable, Guid Receivable, Guid Settlement, Guid U25, Guid I25);

    /// <summary>A quarterly registration in DKK, an output code and an input code that map to the boxes B1 (base of sales), B2 (output tax) and B3 (input tax),
    /// the rules that choose them by the account of the line, the role VAT_SETTLEMENT and a series for the VAT_SETTLE documents.</summary>
    private static async Task<World> Setup(TestTenant t, string correction = "ADJUST_NEXT", bool series = true, bool role = true)
    {
        bool series_ = series;
        Guid payable = Guid.NewGuid(), receivable = Guid.NewGuid(), settlement = Guid.NewGuid(); Guid ser0 = Guid.NewGuid();
        await t.ExecuteAsync(
            "insert into core.gl_account (tenant_id, company_id, account_id, account_no, name, account_type, tax_code_rule) values " +
            "(@t, @c, @p, '2700', 'VAT payable', 'LIABILITY', 'FORBIDDEN'), (@t, @c, @r, '2710', 'VAT receivable', 'ASSET', 'FORBIDDEN'), (@t, @c, @s, '2790', 'VAT settlement', 'LIABILITY', 'FORBIDDEN'); " +
            "update core.gl_account set tax_code_rule = 'MANDATORY' where tenant_id = @t and account_id in (@rev, @exp); " +
            "insert into core.number_series (tenant_id, company_id, series_id, series_code, prefix, include_fiscal_year, separator, number_length, is_legal) select @t, @c, @ser, 'GL-VAT', 'VS', true, '-', 6, true where @series; " +
            "insert into core.number_series_assignment (tenant_id, company_id, document_type, series_id, effective_from, created_by) select @t, @c, 'VAT_SETTLE', @ser, date '2026-01-01', 'test' where @series; " +
            "insert into core.number_range_counter (tenant_id, company_id, series_id, fiscal_year, last_number) select @t, @c, @ser, 2026, 0 where @series",
            ("c", t.CompanyId), ("p", payable), ("r", receivable), ("s", settlement), ("ser", ser0), ("series", series_), ("rev", t.Revenue), ("exp", t.Expense));
        var tax = new TaxCommands(t.App);
        foreach (string box in new[] { "B1", "B2", "B3" })
            Assert.Equal(CommandStatus.Ok, (await tax.CreateBoxAsync(t.TenantId, Cfg(t, "box-" + box), new("DK", "STANDARD", box, D(("name", "Box " + box))))).Status);
        var reg = await tax.CreateRegistrationAsync(t.TenantId, Cfg(t, "w-reg"), new(D(("company_id", t.CompanyId), ("country_code", "DK"), ("vat_id", "DK12345678"), ("scheme", "STANDARD"),
            ("filing_frequency", "QUARTERLY"), ("correction_method", correction), ("return_currency", "DKK"), ("valid_from", "2026-01-01"))));
        Assert.True(reg.Status == CommandStatus.Ok, Describe(reg));
        async Task<Guid> Code(string code, string type, Guid account, string key, Guid ruleAccount, params TaxBoxMapping[] boxes)
        {
            var c = await tax.CreateCodeAsync(t.TenantId, Cfg(t, "w-c-" + key), new(D(("company_id", t.CompanyId), ("code", code), ("description", code), ("tax_type", type), ("vat_registration_id", reg.Value!.EntityId))));
            Assert.True(c.Status == CommandStatus.Ok, Describe(c));
            var v = await tax.CreateVersionAsync(t.TenantId, Cfg(t, "w-v-" + key), new(c.Value!.EntityId, D(("valid_from", "2026-01-01"), ("vat_account_id", account), ("rate", 25m)), boxes));
            Assert.True(v.Status == CommandStatus.Ok, Describe(v));
            var rule = await new TaxDeterminationCommands(t.App).CreateRuleAsync(t.TenantId, Cfg(t, "w-r-" + key),
                new(t.CompanyId, D(("valid_from", "2026-01-01"), ("priority", 10), ("tax_code_id", c.Value.EntityId), ("account_id", ruleAccount), ("origin_country", "DK"), ("destination_country", "DK")), []));
            Assert.True(rule.Status == CommandStatus.Ok, Describe(rule));
            return c.Value.EntityId;
        }
        Guid u25 = await Code("U25", "OUTPUT", payable, "u25", t.Revenue, new TaxBoxMapping("PRIMARY", "STANDARD", "B1", "BASE", -1), new TaxBoxMapping("PRIMARY", "STANDARD", "B2", "TAX", -1));
        Guid i25 = await Code("I25", "INPUT", receivable, "i25", t.Expense, new TaxBoxMapping("PRIMARY", "STANDARD", "B3", "TAX", 1));
        if (role)
        {
            var assigned = await new AccountRoleAssigner(t.App).AssignAsync(t.TenantId, Cfg(t, "w-role"), new(t.CompanyId, "VAT_SETTLEMENT", settlement, VatRegistrationId: reg.Value!.EntityId));
            Assert.True(assigned.Status == CommandStatus.Ok, Describe(assigned));
        }
        return new World(reg.Value!.EntityId, payable, receivable, settlement, u25, i25);
    }

    private static PostJournalDocumentRequest Sale(TestTenant t, decimal net, string date, string? taxDate = null) =>
        t.Journal(date) with
        {
            OriginCountry = "DK", DestinationCountry = "DK", TaxDate = taxDate is null ? null : DateOnly.Parse(taxDate),
            Lines = [new RequestLine("1", t.Bank, net * 1.25m), new RequestLine("2", t.Revenue, -net)]
        };

    private static PostJournalDocumentRequest Purchase(TestTenant t, decimal net, string date) =>
        t.Journal(date) with
        {
            OriginCountry = "DK", DestinationCountry = "DK",
            Lines = [new RequestLine("1", t.Expense, net), new RequestLine("2", t.Bank, -net * 1.25m)]
        };

    private static async Task<Guid> Post(TestTenant t, string key, PostJournalDocumentRequest req)
    {
        var r = await new JournalDocumentPoster(t.App).PostAsync(t.TenantId, t.Context(key), req);
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        return r.Value!.DocumentId;
    }

    private static async Task CloseQuarter(TestTenant t, int quarter)
    {
        for (int m = (quarter - 1) * 3 + 1; m <= quarter * 3; m++) await t.SetPeriodStatusAsync(m, "SOFT_CLOSED");
    }

    private static Task<CommandResult<VatReturnResult>> Prepare(TestTenant t, World w, string key, int period = 1) =>
        new VatReturnCommands(t.App).PrepareAsync(t.TenantId, t.Context(key, "vat.prepare"), new(w.Registration, "STANDARD", 2026, period));

    private static Task<CommandResult<SettledVatReturn>> Settle(TestTenant t, string key, Guid returnId, DateOnly? date = null) =>
        new VatSettlement(t.App).SettleAsync(t.TenantId, t.Context(key, "vat.settle"), new(returnId, date));

    private static Task<CommandResult<SettledVatReturn>> ReverseSettlement(TestTenant t, string key, Guid returnId, string date = "2026-04-10", string reason = "ERROR") =>
        new VatSettlement(t.App).ReverseAsync(t.TenantId, t.Context(key, "vat.settle"), new(returnId, DateOnly.Parse(date), reason, "wrong return"));

    private static async Task AssertChains(TestTenant t)
    {
        var verify = await new ChainVerification(t.App).VerifyAsync(t.TenantId, t.Context("verify", ChainVerification.Permission), new(t.CompanyId, "LEDGER"));
        Assert.True(verify.Status == CommandStatus.Ok && verify.Value!.Ok, Describe(verify) + " " + string.Join(";", verify.Value?.Breaks.Select(p => p.Code + " " + p.Detail) ?? []));
    }

    private static decimal Box(VatReturnResult r, string box, bool tax) => r.Lines.Single(l => l.BoxCode == box) is { } l ? (tax ? l.TaxAmount : l.BaseAmount) : 0m;

    // ---------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task AReturnFreezesTheTaxLinesOfThePeriodByBoxAndIsAChainedSnapshot()
    {
        var t = await TestTenant.CreateReadyAsync();
        var w = await Setup(t);
        await Post(t, "s1", Sale(t, 1000m, "2026-01-10"));
        await Post(t, "s2", Sale(t, 2000m, "2026-02-10"));
        await Post(t, "p1", Purchase(t, 400m, "2026-03-15"));
        await Post(t, "s-q2", Sale(t, 800m, "2026-05-05"));   // another quarter
        await CloseQuarter(t, 1);

        var r = await Prepare(t, w, "prep-1");
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        var ret = r.Value!;
        Assert.Equal(("ORIGINAL", 1, 3, 0, 1L), (ret.ReturnKind, ret.ReturnSeq, ret.TaxLineCount, ret.LateLineCount, ret.ChainSeq));
        Assert.Equal((new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31)), (ret.PeriodStartDate, ret.PeriodEndDate));
        Assert.Equal(["B1", "B2", "B3"], ret.Lines.Select(l => l.BoxCode!).ToArray());
        Assert.Equal((3000m, 750m, 100m), (Box(ret, "B1", false), Box(ret, "B2", true), Box(ret, "B3", true)));
        Assert.Equal(2, ret.Lines.Single(l => l.BoxCode == "B2").LineCount);
        Assert.All(ret.TaxLines, l => Assert.True(l.IsCurrent && !l.IsLate));

        // the same request again is a replay, and the snapshot reads back as it was
        var replay = await Prepare(t, w, "prep-1");
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(ret.RowHash, replay.Value!.RowHash);
        var read = await new VatReturnCommands(t.App).GetAsync(t.TenantId, t.Context("get", "vat.read"), ret.ReturnId);
        Assert.Equal(ret.SnapshotDigest, read.Value!.SnapshotDigest);
        Assert.Equal(3, read.Value.TaxLines.Count);

        // the second quarter is the next node of the chain and takes only its own line
        await CloseQuarter(t, 2);
        var q2 = await Prepare(t, w, "prep-2", 2);
        Assert.True(q2.Status == CommandStatus.Ok, Describe(q2));
        Assert.Equal((2L, 1, ret.RowHash), (q2.Value!.ChainSeq, q2.Value.TaxLineCount, q2.Value.PrevHash));
        Assert.Equal(800m * 0.25m, Box(q2.Value, "B2", true));
        var chain = await new ChainVerification(t.App).VerifyAsync(t.TenantId, t.Context("verify-vat", ChainVerification.Permission), new(t.CompanyId, "VAT_RETURN"));
        Assert.True(chain.Status == CommandStatus.Ok && chain.Value!.Ok, Describe(chain) + " " + string.Join(";", chain.Value?.Breaks.Select(b => b.Code + " " + b.Detail) ?? []));
        Assert.Equal(2L, chain.Value!.Checked);
    }

    [Fact]
    public async Task APreparationNeedsAClosedPeriodAnExistingPeriodAndTheEarlierReturn()
    {
        var t = await TestTenant.CreateReadyAsync();
        var w = await Setup(t);
        await Post(t, "s1", Sale(t, 1000m, "2026-01-10"));
        var vat = new VatReturnCommands(t.App);

        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await vat.PrepareAsync(t.TenantId, t.Context("x0", "journal.post"), new(w.Registration, "STANDARD", 2026, 1))));
        Assert.Equal(["VAT.NO_REGISTRATION"], Codes(await vat.PrepareAsync(t.TenantId, t.Context("x1", "vat.prepare"), new(Guid.NewGuid(), "STANDARD", 2026, 1))));
        Assert.Equal(["VAT.PERIOD_NOT_FOUND"], Codes(await Prepare(t, w, "x2", 5)));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await vat.PrepareAsync(t.TenantId, t.Context("x3", "vat.prepare"), new(w.Registration, "EC_LIST", 2026, 1))));   // the registration files no EC list
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await vat.PrepareAsync(t.TenantId, t.Context("x4", "vat.prepare"), new(w.Registration, "OSS", 2026, 1))));
        Assert.Equal(["VAT.PERIOD_NOT_COMPLETE"], Codes(await Prepare(t, w, "x5")));          // all three months are open
        await t.SetPeriodStatusAsync(1, "SOFT_CLOSED"); await t.SetPeriodStatusAsync(2, "HARD_CLOSED");
        Assert.Equal(["VAT.PERIOD_NOT_COMPLETE"], Codes(await Prepare(t, w, "x6")));          // March is still open

        await CloseQuarter(t, 2);
        Assert.Equal(["VAT.PREVIOUS_PERIOD_NOT_PREPARED"], Codes(await Prepare(t, w, "x7", 2)));
        await t.SetPeriodStatusAsync(3, "SOFT_CLOSED");
        Assert.Equal(CommandStatus.Ok, (await Prepare(t, w, "ok-1")).Status);
        Assert.Equal(["VAT.ALREADY_PREPARED"], Codes(await Prepare(t, w, "x8")));              // ADJUST_NEXT
        Assert.Equal(CommandStatus.Ok, (await Prepare(t, w, "ok-2", 2)).Status);
    }

    [Fact]
    public async Task ALateLineReachesTheNextReturnUnderAdjustNext()
    {
        var t = await TestTenant.CreateReadyAsync();
        var w = await Setup(t);
        await Post(t, "s1", Sale(t, 1000m, "2026-01-10"));
        await CloseQuarter(t, 1);
        Assert.Equal(CommandStatus.Ok, (await Prepare(t, w, "q1")).Status);

        await Post(t, "late", Sale(t, 400m, "2026-04-05", taxDate: "2026-02-20"));    // posted in April for a supply of February
        await Post(t, "q2doc", Sale(t, 100m, "2026-04-06"));
        await CloseQuarter(t, 2);
        var q2 = await Prepare(t, w, "q2", 2);
        Assert.True(q2.Status == CommandStatus.Ok, Describe(q2));
        Assert.Equal((2, 1), (q2.Value!.TaxLineCount, q2.Value.LateLineCount));
        Assert.Equal(1, q2.Value.TaxLines.Count(l => l.IsLate));
        Assert.Equal(500m * 0.25m, Box(q2.Value, "B2", true));
    }

    [Fact]
    public async Task ASupplementaryReturnTakesOnlyTheLinesNotYetReported()
    {
        var t = await TestTenant.CreateReadyAsync();
        var w = await Setup(t, "SUPPLEMENTARY");
        await Post(t, "s1", Sale(t, 1000m, "2026-01-10"));
        await CloseQuarter(t, 1);
        var original = await Prepare(t, w, "q1");
        Assert.Equal("ORIGINAL", original.Value!.ReturnKind);
        Assert.Equal(["VAT.NOTHING_TO_REPORT"], Codes(await Prepare(t, w, "q1-nothing")));

        await Post(t, "late", Sale(t, 400m, "2026-04-05", taxDate: "2026-03-20"));
        var supp = await Prepare(t, w, "q1-supp");
        Assert.True(supp.Status == CommandStatus.Ok, Describe(supp));
        Assert.Equal(("SUPPLEMENTARY", 2, 1, 2L), (supp.Value!.ReturnKind, supp.Value.ReturnSeq, supp.Value.TaxLineCount, supp.Value.ChainSeq));
        Assert.Equal(100m, Box(supp.Value, "B2", true));
        Assert.Null(supp.Value.ReplacesReturnId);
        Assert.Null((await new VatReturnCommands(t.App).GetAsync(t.TenantId, t.Context("g", "vat.read"), original.Value.ReturnId)).Value!.SupersededByReturnId);
    }

    [Fact]
    public async Task AReplacementTakesTheWholePeriodAndSupersedesTheReplacedReturn()
    {
        var t = await TestTenant.CreateReadyAsync();
        var w = await Setup(t, "REPLACEMENT");
        await Post(t, "s1", Sale(t, 1000m, "2026-01-10"));
        await CloseQuarter(t, 1);
        var original = (await Prepare(t, w, "q1")).Value!;
        await Post(t, "late", Sale(t, 400m, "2026-04-05", taxDate: "2026-03-20"));

        var replacement = await Prepare(t, w, "q1-repl");
        Assert.True(replacement.Status == CommandStatus.Ok, Describe(replacement));
        var r = replacement.Value!;
        Assert.Equal(("REPLACEMENT", 2, original.ReturnId, 2), (r.ReturnKind, r.ReturnSeq, r.ReplacesReturnId, r.TaxLineCount));
        Assert.Equal(1400m * 0.25m, Box(r, "B2", true));
        Assert.All(r.TaxLines, l => Assert.True(l.IsCurrent));
        var old = (await new VatReturnCommands(t.App).GetAsync(t.TenantId, t.Context("g", "vat.read"), original.ReturnId)).Value!;
        Assert.Equal(r.ReturnId, old.SupersededByReturnId);
        Assert.All(old.TaxLines, l => Assert.False(l.IsCurrent));
        Assert.Equal(250m, Box(old, "B2", true));          // the snapshot itself is frozen
        // a third preparation replaces the replacement, never the replaced one
        var third = (await Prepare(t, w, "q1-repl-2")).Value!;
        Assert.Equal(r.ReturnId, third.ReplacesReturnId);
    }

    [Fact]
    public async Task AFilingIsRecordedOnceAndFreezesTheSettlement()
    {
        var t = await TestTenant.CreateReadyAsync();
        var w = await Setup(t);
        await Post(t, "s1", Sale(t, 1000m, "2026-01-10"));
        await CloseQuarter(t, 1);
        var ret = (await Prepare(t, w, "q1")).Value!;
        var vat = new VatReturnCommands(t.App);
        var at = new DateTime(2026, 4, 20, 9, 30, 0, DateTimeKind.Utc);

        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await vat.RecordFilingAsync(t.TenantId, t.Context("f0", "vat.prepare"), new(ret.ReturnId, "SKAT-1", at))));
        Assert.Equal(["VAT.RETURN_NOT_FOUND"], Codes(await vat.RecordFilingAsync(t.TenantId, t.Context("f1", "vat.file"), new(Guid.NewGuid(), "SKAT-1", at))));
        var settled = await Settle(t, "settle", ret.ReturnId);
        Assert.True(settled.Status == CommandStatus.Ok, Describe(settled));
        var filed = await vat.RecordFilingAsync(t.TenantId, t.Context("f2", "vat.file"), new(ret.ReturnId, "SKAT-1", at));
        Assert.True(filed.Status == CommandStatus.Ok, Describe(filed));
        Assert.Equal(("SKAT-1", at), (filed.Value!.FilingReference, filed.Value.FiledAt));
        Assert.True((await vat.RecordFilingAsync(t.TenantId, t.Context("f2", "vat.file"), new(ret.ReturnId, "SKAT-1", at))).IdempotentReplay);
        Assert.Equal(["VAT.ALREADY_FILED"], Codes(await vat.RecordFilingAsync(t.TenantId, t.Context("f3", "vat.file"), new(ret.ReturnId, "SKAT-2", at))));
        Assert.Equal(["VAT.ALREADY_FILED"], Codes(await ReverseSettlement(t, "rev", ret.ReturnId)));
        // the database holds the same line: a filing cannot be changed, a return cannot be deleted
        await Assert.ThrowsAnyAsync<Exception>(() => t.ExecuteAsync("update core.vat_return set filing_reference = 'X' where tenant_id = @t", ("t", t.TenantId)));
        await Assert.ThrowsAnyAsync<Exception>(() => t.ExecuteAsync("delete from core.vat_return where tenant_id = @t", ("t", t.TenantId)));
    }

    [Fact]
    public async Task ASettlementClearsTheVatAccountsAgainstTheSettlementRoleAndIsReversedOnlyByItsOwnCommand()
    {
        var t = await TestTenant.CreateReadyAsync();
        var w = await Setup(t);
        await Post(t, "s1", Sale(t, 1000m, "2026-01-10"));
        await Post(t, "p1", Purchase(t, 400m, "2026-03-15"));
        await CloseQuarter(t, 1);
        var ret = (await Prepare(t, w, "q1")).Value!;

        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await new VatSettlement(t.App).SettleAsync(t.TenantId, t.Context("s0", "vat.prepare"), new(ret.ReturnId, null))));
        Assert.Equal(["VAT.SETTLEMENT_DATE_NOT_POSTABLE"], Codes(await Settle(t, "s-closed", ret.ReturnId, new DateOnly(2026, 3, 31))));
        var settled = await Settle(t, "settle-a", ret.ReturnId);
        Assert.True(settled.Status == CommandStatus.Ok, Describe(settled));
        var doc = settled.Value!.Document;
        Assert.Equal(("VAT_SETTLE", new DateOnly(2026, 4, 1)), (doc.DocumentType, doc.PostingDate));      // the first postable date after the period end
        Assert.Equal(new[] { w.Payable, w.Receivable, w.Settlement }.Order().ToArray(), doc.Lines.Select(l => l.AccountId).Distinct().Order().ToArray());
        Assert.Equal(250m, doc.Lines.Single(l => l.AccountId == w.Payable).AmountLc);        // the output tax was a credit: the settlement debits it
        Assert.Equal(-100m, doc.Lines.Single(l => l.AccountId == w.Receivable).AmountLc);
        Assert.Equal(-150m, doc.Lines.Single(l => l.AccountId == w.Settlement).AmountLc);    // net payable to the authority
        Assert.All(doc.Lines, l => Assert.Equal("VAT_SETTLEMENT", l.LineType));
        Assert.Equal(0m, await t.ScalarAsync<decimal>("select coalesce(sum(amount_lc), 0) from core.journal_line where tenant_id = @t and account_id in (select account_id from core.gl_account where account_no in ('2700', '2710') and tenant_id = @t)", ("t", t.TenantId)));
        Assert.Equal(doc.DocumentId, await t.ScalarAsync<Guid>("select settled_document_id from core.vat_return where tenant_id = @t", ("t", t.TenantId)));
        Assert.Equal(ret.SnapshotDigest, Core.Hashing.Canonical.FormatHex32(await t.ScalarAsync<byte[]>("select detail_digest from core.journal_document where tenant_id = @t and document_id = @d", ("t", t.TenantId), ("d", doc.DocumentId))));

        Assert.Equal(["VAT.ALREADY_SETTLED"], Codes(await Settle(t, "s2", ret.ReturnId)));
        Assert.True((await Settle(t, "settle-a", ret.ReturnId)).IdempotentReplay);
        // the journal command does not reverse it
        var viaJournal = await new DocumentReverser(t.App).ReverseAsync(t.TenantId, t.Context("jr", "journal.reverse"), new(doc.DocumentId, null, "ERROR", null));
        Assert.Equal(["REVERSAL.TYPE_NOT_REVERSIBLE"], Codes(viaJournal));
        Assert.Equal("vat-returns:reverse-settlement", viaJournal.Errors[0].Params!["owning_command"]);
        await AssertChains(t);
    }

    [Fact]
    public async Task ASettlementIsReversedBeforeFilingAndThenSettledAgain()
    {
        var t = await TestTenant.CreateReadyAsync();
        var w = await Setup(t);
        await Post(t, "s1", Sale(t, 1000m, "2026-01-10"));
        await CloseQuarter(t, 1);
        var ret = (await Prepare(t, w, "q1")).Value!;

        Assert.Equal(["VAT.NOT_SETTLED"], Codes(await ReverseSettlement(t, "r0", ret.ReturnId)));
        var first = (await Settle(t, "settle-1", ret.ReturnId)).Value!.Document;
        Assert.Equal(["REVERSAL.REASON_NOT_ALLOWED"], Codes(await ReverseSettlement(t, "r1", ret.ReturnId, reason: "RETURN")));
        Assert.Equal(["VAT.SETTLEMENT_DATE_NOT_POSTABLE"], Codes(await ReverseSettlement(t, "r2", ret.ReturnId, "2026-03-31")));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await new VatSettlement(t.App).ReverseAsync(t.TenantId, t.Context("r3", "vat.prepare"), new(ret.ReturnId, new DateOnly(2026, 4, 10), "ERROR", "x"))));

        var reversed = await ReverseSettlement(t, "r4", ret.ReturnId);
        Assert.True(reversed.Status == CommandStatus.Ok, Describe(reversed));
        var mirror = reversed.Value!.Document;
        Assert.Equal(("VAT_SETTLE", new DateOnly(2026, 4, 10)), (mirror.DocumentType, mirror.PostingDate));
        Assert.Equal(first.Lines.Select(l => -l.AmountLc).ToArray(), mirror.Lines.Select(l => l.AmountLc).ToArray());
        Assert.Equal(-250m, await t.ScalarAsync<decimal>("select coalesce(sum(amount_lc), 0) from core.journal_line where tenant_id = @t and account_id = @a", ("t", t.TenantId), ("a", w.Payable)));   // the tax is open again
        Assert.Equal(0L, await t.ScalarAsync<long>("select count(*) from core.vat_return where tenant_id = @t and settled_document_id is not null", ("t", t.TenantId)));
        Assert.Equal(("reversed", mirror.DocumentId), (await t.ScalarAsync<string>("select status from core.journal_document where tenant_id = @t and document_id = @d", ("t", t.TenantId), ("d", first.DocumentId)),
            await t.ScalarAsync<Guid>("select reversed_by_document_id from core.journal_document where tenant_id = @t and document_id = @d", ("t", t.TenantId), ("d", first.DocumentId))));
        Assert.True((await ReverseSettlement(t, "r4", ret.ReturnId)).IdempotentReplay);

        var again = await Settle(t, "settle-2", ret.ReturnId);
        Assert.True(again.Status == CommandStatus.Ok, Describe(again));
        Assert.NotEqual(first.DocumentId, again.Value!.Document.DocumentId);
        await AssertChains(t);
    }

    [Fact]
    public async Task AReplacementSettlesTheDifferenceToTheLastSettledReturnOfItsChain()
    {
        var t = await TestTenant.CreateReadyAsync();
        var w = await Setup(t, "REPLACEMENT");
        await Post(t, "s1", Sale(t, 1000m, "2026-01-10"));
        await CloseQuarter(t, 1);
        var original = (await Prepare(t, w, "q1")).Value!;
        var settledOriginal = (await Settle(t, "settle-o", original.ReturnId)).Value!.Document;
        Assert.Equal(-250m, settledOriginal.Lines.Single(l => l.AccountId == w.Settlement).AmountLc);

        await Post(t, "late", Sale(t, 400m, "2026-04-05", taxDate: "2026-03-20"));
        var replacement = (await Prepare(t, w, "q1-repl")).Value!;
        Assert.Equal(["VAT.ALREADY_SETTLED"], Codes(await Settle(t, "settle-again", original.ReturnId)));

        // reversing the settlement of the replaced return waits until the successor is not settled (B9)
        var settledReplacement = await Settle(t, "settle-r", replacement.ReturnId);
        Assert.True(settledReplacement.Status == CommandStatus.Ok, Describe(settledReplacement));
        var diff = settledReplacement.Value!.Document;
        Assert.Equal(100m, diff.Lines.Single(l => l.AccountId == w.Payable).AmountLc);          // only the new line: 400 x 25 %
        Assert.Equal(-100m, diff.Lines.Single(l => l.AccountId == w.Settlement).AmountLc);
        Assert.Equal(["VAT.SUPERSEDED"], Codes(await ReverseSettlement(t, "rev-o", original.ReturnId)));
        Assert.Equal(CommandStatus.Ok, (await ReverseSettlement(t, "rev-r", replacement.ReturnId)).Status);
        Assert.Equal(CommandStatus.Ok, (await ReverseSettlement(t, "rev-o2", original.ReturnId, "2026-04-11")).Status);

        // an unsettled superseded return cannot be settled, and a replacement with nothing settled before it settles in full
        Assert.Equal(["VAT.SUPERSEDED"], Codes(await Settle(t, "settle-o2", original.ReturnId)));
        var full = (await Settle(t, "settle-r2", replacement.ReturnId)).Value!.Document;
        Assert.Equal(-350m, full.Lines.Single(l => l.AccountId == w.Settlement).AmountLc);
        await AssertChains(t);
    }

    [Fact]
    public async Task ANilReturnHasNothingToSettleAndTheSettlementAccountTakesOnlyTaxAndSettlementLines()
    {
        var t = await TestTenant.CreateReadyAsync();
        var w = await Setup(t);
        await CloseQuarter(t, 1);
        var nil = await Prepare(t, w, "q1");
        Assert.True(nil.Status == CommandStatus.Ok, Describe(nil));
        Assert.Equal((0, 0), (nil.Value!.TaxLineCount, nil.Value.Lines.Count));
        Assert.Equal(["VAT.NOTHING_TO_SETTLE"], Codes(await Settle(t, "settle-nil", nil.Value.ReturnId)));

        Assert.True(await t.ScalarAsync<bool>("select tax_account from core.gl_account where tenant_id = @t and account_id = @a", ("t", t.TenantId), ("a", w.Settlement)));
        var direct = await new JournalDocumentPoster(t.App).PostAsync(t.TenantId, t.Context("direct"),
            t.Journal("2026-04-15") with { Lines = [new RequestLine("1", w.Settlement, 100m), new RequestLine("2", t.Bank, -100m)] });
        Assert.Contains("ACCOUNT_RULE.TAX_ACCOUNT_DIRECT_POSTING", Codes(direct));
        // the role needs an existing registration of the company
        var role = await new AccountRoleAssigner(t.App).AssignAsync(t.TenantId, Cfg(t, "bad-role"), new(t.CompanyId, "VAT_SETTLEMENT", t.Bank, VatRegistrationId: Guid.NewGuid()));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(role));
    }

    [Fact]
    public async Task ASettlementNeedsItsRoleAndItsSeries()
    {
        foreach (var (series, role, expected) in new[] { (false, true, "SERIES.NO_ASSIGNMENT"), (true, false, "CONFIG.ROLE_NOT_ASSIGNED") })
        {
            var t = await TestTenant.CreateReadyAsync();
            var w = await Setup(t, series: series, role: role);
            await Post(t, "s1", Sale(t, 1000m, "2026-01-10"));
            await CloseQuarter(t, 1);
            var ret = (await Prepare(t, w, "q1")).Value!;
            Assert.Equal([expected], Codes(await Settle(t, "settle", ret.ReturnId)));
        }
    }
}
