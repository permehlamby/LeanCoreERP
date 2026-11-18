using FinanceCore.Core.Chains;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;
using FinanceCore.Core.Posting;

namespace FinanceCore.Data.Tests;

/// <summary>Partial and non-deductible input VAT (19, 07, 8.6): deduction keys and rates, the split of an input tax into the deductible part and the cost lines of the rest.</summary>
[Collection("db")]
public class TaxDeductionTests
{
    private static string Describe<T>(CommandResult<T> r) => r.Status + ": " + string.Join(", ", r.Errors.Select(e => $"{e.Code} {e.Path}"));
    private static string[] Codes<T>(CommandResult<T> r) => r.Errors.Select(e => e.Code).ToArray();
    private static Dictionary<string, object?> D(params (string, object?)[] f) => f.ToDictionary(x => x.Item1, x => x.Item2);
    private static CommandContext Cfg(TestTenant t, string key) => t.Context(key, "write.config");

    private sealed record World(Guid Registration, Guid Receivable, Guid Payable, Guid RcCounter);

    private static async Task<World> Setup(TestTenant t)
    {
        Guid payable = Guid.NewGuid(), receivable = Guid.NewGuid(), counter = Guid.NewGuid();
        await t.ExecuteAsync(
            "insert into core.gl_account (tenant_id, company_id, account_id, account_no, name, account_type, tax_code_rule) values " +
            "(@t, @c, @p, '2700', 'VAT payable', 'LIABILITY', 'FORBIDDEN'), (@t, @c, @r, '2710', 'VAT receivable', 'ASSET', 'FORBIDDEN'), (@t, @c, @k, '2720', 'VAT reverse charge', 'ASSET', 'FORBIDDEN'); " +
            "update core.gl_account set tax_code_rule = 'MANDATORY' where tenant_id = @t and account_id = @exp",
            ("c", t.CompanyId), ("p", payable), ("r", receivable), ("k", counter), ("exp", t.Expense));
        var reg = await new TaxCommands(t.App).CreateRegistrationAsync(t.TenantId, Cfg(t, "w-reg"), new(D(("company_id", t.CompanyId), ("country_code", "DK"), ("vat_id", "DK12345678"), ("scheme", "STANDARD"),
            ("filing_frequency", "QUARTERLY"), ("correction_method", "ADJUST_NEXT"), ("return_currency", "DKK"), ("valid_from", "2026-01-01"))));
        Assert.True(reg.Status == CommandStatus.Ok, Describe(reg));
        return new World(reg.Value!.EntityId, receivable, payable, counter);
    }

    private static async Task<Guid> Key(TestTenant t, World w, string code, string kind, string key)
    {
        var r = await new DeductionCommands(t.App).CreateKeyAsync(t.TenantId, Cfg(t, "k-" + key), new(D(("company_id", t.CompanyId), ("registration_id", w.Registration), ("key_code", code), ("kind", kind))));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        return r.Value!.EntityId;
    }

    private static async Task Rate(TestTenant t, Guid key, decimal pct, string from, string idem, string? to = null)
    {
        var fields = D(("valid_from", from), ("rate_pct", pct), ("rate_status", "PROVISIONAL"));
        if (to is not null) fields["valid_to"] = to;
        var r = await new DeductionCommands(t.App).CreateRateAsync(t.TenantId, Cfg(t, "r-" + idem), new(key, fields));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
    }

    /// <summary>An input code with a version: the share, an optional key, a cap, reverse charge.</summary>
    private static async Task<Guid> Input(TestTenant t, World w, string code, string key, decimal pct = 100m, string? deductionKey = null, string cap = "NONE", bool reverse = false, int priority = 10)
    {
        var tax = new TaxCommands(t.App);
        var c = await tax.CreateCodeAsync(t.TenantId, Cfg(t, "c-" + key), new(D(("company_id", t.CompanyId), ("code", code), ("description", code), ("tax_type", "INPUT"), ("vat_registration_id", w.Registration))));
        Assert.True(c.Status == CommandStatus.Ok, Describe(c));
        var fields = D(("valid_from", "2026-01-01"), ("rate", 25m), ("vat_account_id", reverse ? w.Payable : w.Receivable), ("deductible_pct", pct), ("deduction_cap", cap));
        if (deductionKey is not null) fields["deduction_key"] = deductionKey;
        if (reverse) { fields["reverse_charge"] = true; fields["reverse_charge_account_id"] = w.RcCounter; fields["en16931_vat_category"] = "AE"; }
        var v = await tax.CreateVersionAsync(t.TenantId, Cfg(t, "v-" + key), new(c.Value!.EntityId, fields, []));
        Assert.True(v.Status == CommandStatus.Ok, Describe(v));
        var rule = await new TaxDeterminationCommands(t.App).CreateRuleAsync(t.TenantId, Cfg(t, "ru-" + key), new(t.CompanyId, D(("valid_from", "2026-01-01"), ("priority", priority), ("tax_code_id", c.Value.EntityId)), []));
        Assert.True(rule.Status == CommandStatus.Ok, Describe(rule));
        return c.Value.EntityId;
    }

    private static PostJournalDocumentRequest Purchase(TestTenant t, decimal gross, params RequestLine[] expenses) =>
        t.Journal() with { OriginCountry = "DK", DestinationCountry = "DK", Lines = [.. expenses, new RequestLine("g", t.Bank, -gross)] };

    private static Task<CommandResult<PostedDocument>> Post(TestTenant t, string key, PostJournalDocumentRequest req) =>
        new JournalDocumentPoster(t.App).PostAsync(t.TenantId, t.Context(key), req);

    private static async Task AssertChainIsOk(TestTenant t)
    {
        var verify = await new ChainVerification(t.App).VerifyAsync(t.TenantId, t.Context("verify", ChainVerification.Permission), new(t.CompanyId, "LEDGER"));
        Assert.True(verify.Status == CommandStatus.Ok && verify.Value!.Ok, Describe(verify) + " " + string.Join(";", verify.Value?.Breaks.Select(p => p.Code + " " + p.Detail) ?? []));
    }

    // ---------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task AFixedShareTimesAProRataRateSplitsTheTaxAndTheRestIsCostOnTheBaseAccount()
    {
        var t = await TestTenant.CreateReadyAsync();
        var w = await Setup(t);
        Guid key = await Key(t, w, "PRORATA", "PRO_RATA", "a");
        await Rate(t, key, 80m, "2026-01-01", "a");
        await Input(t, w, "K050", "a", 50m, "PRORATA");

        // the example of 19, 4.2: 1,000.00 net, 25 percent, share 40 percent, supplier invoice 1,250.00
        var r = await Post(t, "p1", Purchase(t, 1250m, new RequestLine("1", t.Expense, 1000m)));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        var lines = r.Value!.Lines;
        Assert.Equal(["BASE", "BASE", "TAX", "TAX_NONDEDUCTIBLE"], lines.Select(l => l.LineType).ToArray());
        Assert.Equal((t.Expense, 150m), (lines[3].AccountId, lines[3].AmountTc));
        Assert.Equal((w.Receivable, 100m), (lines[2].AccountId, lines[2].AmountTc));
        Assert.Null(lines[3].TaxCodeId);
        Assert.Equal(0m, lines.Sum(l => l.AmountTc));
        Assert.Equal(0m, lines.Sum(l => l.AmountLc));
        Assert.Equal(0m, lines.Sum(l => l.AmountGc));
        Guid doc = r.Value.DocumentId;

        async Task<T> Tax<T>(string column) => await t.ScalarAsync<T>($"select {column} from core.journal_line_tax where tenant_id = @t and document_id = @d and line_no = 3", ("d", doc));
        Assert.Equal(250m, await Tax<decimal>("gross_tax_amount_tc"));
        Assert.Equal(50m, await Tax<decimal>("deductible_pct"));
        Assert.Equal("PRORATA", await Tax<string>("deduction_key"));
        Assert.Equal(80m, await Tax<decimal>("deduction_key_rate"));
        Assert.Equal(40m, await Tax<decimal>("deduction_share"));
        Assert.Equal(100m, await Tax<decimal>("vat_tax_amount"));
        Assert.Equal(150m, await t.ScalarAsync<decimal>("select nondeductible_tax_tc from core.journal_line_tax_base where tenant_id = @t and document_id = @d and tax_line_no = 3 and base_line_no = 1", ("d", doc)));
        Assert.Equal(4, await t.ScalarAsync<int>("select nondeductible_line_no from core.journal_line_tax_base where tenant_id = @t and document_id = @d and tax_line_no = 3", ("d", doc)));
        await AssertChainIsOk(t);

        // a reversal copies the lines and the rows as they are (19, 7)
        var mirror = await new DocumentReverser(t.App).ReverseAsync(t.TenantId, t.Context("rev", "journal.reverse"), new(doc, null, "CORRECTION", "Wrong"));
        Assert.True(mirror.Status == CommandStatus.Ok, Describe(mirror));
        Assert.Equal(["BASE", "BASE", "TAX", "TAX_NONDEDUCTIBLE"], mirror.Value!.Lines.Select(l => l.LineType).ToArray());
        Assert.Equal(-150m, mirror.Value.Lines[3].AmountTc);
        Assert.Equal(-150m, await t.ScalarAsync<decimal>("select nondeductible_tax_tc from core.journal_line_tax_base where tenant_id = @t and document_id = @d", ("d", mirror.Value.DocumentId)));
        await AssertChainIsOk(t);
    }

    [Fact]
    public async Task ReverseChargeSplitsTheDeductibleSideOnlyAndTheOutputSideStaysFull()
    {
        var t = await TestTenant.CreateReadyAsync();
        var w = await Setup(t);
        await Input(t, w, "RC60", "a", 60m, null, "NONE", reverse: true);
        var r = await Post(t, "rc", Purchase(t, 1000m, new RequestLine("1", t.Expense, 1000m)));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        var lines = r.Value!.Lines;
        Assert.Equal(["BASE", "BASE", "TAX", "TAX", "TAX_NONDEDUCTIBLE"], lines.Select(l => l.LineType).ToArray());
        Assert.Equal((w.Payable, -250m), (lines[2].AccountId, lines[2].AmountTc));
        Assert.Equal((w.RcCounter, 150m), (lines[3].AccountId, lines[3].AmountTc));
        Assert.Equal(100m, lines[4].AmountTc);
        Assert.Equal(0m, lines.Sum(l => l.AmountTc));
        Assert.Equal(60m, await t.ScalarAsync<decimal>("select deduction_share from core.journal_line_tax where tenant_id = @t and document_id = @d and line_no = 4", ("d", r.Value.DocumentId)));
        Assert.Equal(100m, await t.ScalarAsync<decimal>("select deduction_share from core.journal_line_tax where tenant_id = @t and document_id = @d and line_no = 3", ("d", r.Value.DocumentId)));
        await AssertChainIsOk(t);
    }

    [Fact]
    public async Task ACodeWithoutDeductionMakesAZeroTaxLineAndTheFullTaxIsCost()
    {
        var t = await TestTenant.CreateReadyAsync();
        var w = await Setup(t);
        await Input(t, w, "K230", "a", 0m);
        var r = await Post(t, "z", Purchase(t, 1250m, new RequestLine("1", t.Expense, 1000m)));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        Assert.Equal(["BASE", "BASE", "TAX", "TAX_NONDEDUCTIBLE"], r.Value!.Lines.Select(l => l.LineType).ToArray());
        Assert.Equal(0m, r.Value.Lines[2].AmountTc);
        Assert.Equal(250m, r.Value.Lines[3].AmountTc);
        await AssertChainIsOk(t);
    }

    [Fact]
    public async Task ASuppliedDeductibleAmountIsCheckedAgainstTheLimitAndTakesTheKeyRate()
    {
        var t = await TestTenant.CreateReadyAsync();
        var w = await Setup(t);
        Guid key = await Key(t, w, "PRORATA", "PRO_RATA", "a");
        await Rate(t, key, 80m, "2026-01-01", "a");
        Guid capped = await Input(t, w, "K090", "a", 25m, "PRORATA", "SUPPLIED_AMOUNT");

        PostJournalDocumentRequest Req(decimal? supplied) => Purchase(t, 1250m, new RequestLine("1", t.Expense, 1000m, DeductibleTaxAmountTc: supplied));
        Assert.Equal(["TAX.DEDUCTION_AMOUNT_REQUIRED"], Codes(await Post(t, "c1", Req(null))));
        // the limit is 250 x 25 percent = 62.50
        Assert.Equal(["TAX.DEDUCTION_AMOUNT_EXCEEDS_LIMIT"], Codes(await Post(t, "c2", Req(70m))));
        Assert.Equal(["TAX.DEDUCTION_AMOUNT_EXCEEDS_LIMIT"], Codes(await Post(t, "c3", Req(-1m))));
        var ok = await Post(t, "c4", Req(50m));
        Assert.True(ok.Status == CommandStatus.Ok, Describe(ok));
        // D = 50 x 80 percent = 40, N = 250 - 40
        Assert.Equal(40m, ok.Value!.Lines.Single(l => l.LineType == "TAX").AmountTc);
        Assert.Equal(210m, ok.Value.Lines.Single(l => l.LineType == "TAX_NONDEDUCTIBLE").AmountTc);
        Assert.Equal(50m, await t.ScalarAsync<decimal>("select deduction_cap_amount_tc from core.journal_line_tax where tenant_id = @t and document_id = @d", ("d", ok.Value.DocumentId)));
        Assert.Equal(16m, await t.ScalarAsync<decimal>("select deduction_share from core.journal_line_tax where tenant_id = @t and document_id = @d and line_no = 3", ("d", ok.Value.DocumentId)));
        await AssertChainIsOk(t);

        // a code without a cap takes none
        Guid plain = await Input(t, w, "I25", "b", priority: 5);
        Assert.Equal(["TAX.DEDUCTION_AMOUNT_NOT_ALLOWED"], Codes(await Post(t, "c5", Req(10m))));
        Assert.NotEqual(Guid.Empty, capped);
        Assert.NotEqual(Guid.Empty, plain);
    }

    [Fact]
    public async Task TheNondeductibleTaxIsAllocatedToTheBaseLinesByLargestRemainderAndCarriesTheirDimensions()
    {
        var t = await TestTenant.CreateReadyAsync();
        var w = await Setup(t);
        await Input(t, w, "K070", "a", 50m);
        Guid cc = await t.ScalarAsync<Guid>("select dimension_type_id from core.dimension_type where tenant_id = @t and standard_role = 'COST_CENTER'");
        var value = await new DimensionCommands(t.App).CreateValueAsync(t.TenantId, t.Context("dv", "write.master"), new(D(("dimension_type_id", cc), ("value_code", "CC1"), ("name", "CC1"), ("valid_from", "2026-01-01"))));
        Assert.True(value.Status == CommandStatus.Ok, Describe(value));
        Guid cc1 = value.Value!.EntityId;

        // 0.30 net in three lines, tax 0.075 rounds to 0.08, D = 0.04, N = 0.04: 0.02, 0.01, 0.01, the odd cent to the first line
        var r = await Post(t, "a1", Purchase(t, 0.38m, new RequestLine("1", t.Expense, 0.10m, Dimensions: new LineDimensions(CostCenterId: cc1)), new RequestLine("2", t.Expense, 0.10m), new RequestLine("3", t.Expense, 0.10m)));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        var nondeductible = r.Value!.Lines.Where(l => l.LineType == "TAX_NONDEDUCTIBLE").Select(l => l.AmountTc).ToArray();
        Assert.Equal([0.02m, 0.01m, 0.01m], nondeductible);
        Assert.Equal(0.04m, r.Value.Lines.Single(l => l.LineType == "TAX").AmountTc);
        Assert.Equal(0m, r.Value.Lines.Sum(l => l.AmountTc));
        // the cost line of the first base line has its cost centre
        Assert.Equal(cc1, await t.ScalarAsync<Guid>("select cost_center_id from core.journal_line where tenant_id = @t and document_id = @d and line_type = 'TAX_NONDEDUCTIBLE' order by line_no limit 1", ("d", r.Value.DocumentId)));
        Assert.Equal(2L, await t.ScalarAsync<long>("select count(*) from core.journal_line where tenant_id = @t and document_id = @d and cost_center_id = @c", ("d", r.Value.DocumentId), ("c", cc1)));
        await AssertChainIsOk(t);

        // unequal bases: 100, 200, 300 at 25 percent and a share of 50: tax 150, D 75, N 75 to 12.50, 25.00, 37.50
        var big = await Post(t, "a2", Purchase(t, 750m, new RequestLine("1", t.Expense, 100m), new RequestLine("2", t.Expense, 200m), new RequestLine("3", t.Expense, 300m)));
        Assert.True(big.Status == CommandStatus.Ok, Describe(big));
        Assert.Equal([12.5m, 25m, 37.5m], big.Value!.Lines.Where(l => l.LineType == "TAX_NONDEDUCTIBLE").Select(l => l.AmountTc).ToArray());
        await AssertChainIsOk(t);
    }

    [Fact]
    public async Task AKeyWithoutARateAtTheTaxDateRejectsThePosting()
    {
        var t = await TestTenant.CreateReadyAsync();
        var w = await Setup(t);
        Guid key = await Key(t, w, "PRORATA", "PRO_RATA", "a");
        await Rate(t, key, 80m, "2026-06-01", "a");
        await Input(t, w, "K110", "a", 100m, "PRORATA");
        var early = await Post(t, "e", Purchase(t, 1250m, new RequestLine("1", t.Expense, 1000m)));
        Assert.Equal(["TAX.DEDUCTION_RATE_NOT_FOUND"], Codes(early));
        // the rate is read at the tax date, not at the posting date (19, A2)
        var late = await Post(t, "l", Purchase(t, 1250m, new RequestLine("1", t.Expense, 1000m, TaxDate: new DateOnly(2026, 6, 15))));
        Assert.True(late.Status == CommandStatus.Ok, Describe(late));
        Assert.Equal(200m, late.Value!.Lines.Single(l => l.LineType == "TAX").AmountTc);
        Assert.Equal(50m, late.Value.Lines.Single(l => l.LineType == "TAX_NONDEDUCTIBLE").AmountTc);
        await AssertChainIsOk(t);
    }

    // ---------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task KeysAndRatesAreCreatedVersionedAndGuarded()
    {
        var t = await TestTenant.CreateReadyAsync();
        var w = await Setup(t);
        var commands = new DeductionCommands(t.App);
        Guid key = await Key(t, w, "PRORATA", "PRO_RATA", "a");
        Assert.True((await commands.CreateKeyAsync(t.TenantId, Cfg(t, "k-a"), new(D(("company_id", t.CompanyId), ("registration_id", w.Registration), ("key_code", "PRORATA"), ("kind", "PRO_RATA"))))).IdempotentReplay);
        Assert.Equal(["CONFIG.DUPLICATE_CODE"], Codes(await commands.CreateKeyAsync(t.TenantId, Cfg(t, "k-b"), new(D(("company_id", t.CompanyId), ("registration_id", w.Registration), ("key_code", "PRORATA"), ("kind", "AREA"))))));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await commands.CreateKeyAsync(t.TenantId, Cfg(t, "k-c"), new(D(("company_id", t.CompanyId), ("registration_id", Guid.NewGuid()), ("key_code", "X"), ("kind", "AREA"))))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await commands.CreateKeyAsync(t.TenantId, Cfg(t, "k-d"), new(D(("company_id", t.CompanyId), ("registration_id", w.Registration), ("key_code", "X"), ("kind", "OTHER"))))));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await commands.CreateKeyAsync(t.TenantId, t.Context("k-e", "write.master"), new(D(("company_id", t.CompanyId))))));

        // a kind can change until a rate exists
        Assert.True((await commands.UpdateKeyAsync(t.TenantId, Cfg(t, "u1"), new(key, 1, D(("kind", "ESTIMATE"))))).Status == CommandStatus.Ok);
        await Rate(t, key, 80m, "2026-01-01", "a");
        Assert.Equal(["CONFIG.DEDUCTION_KEY_REFERENCED"], Codes(await commands.UpdateKeyAsync(t.TenantId, Cfg(t, "u2"), new(key, 2, D(("kind", "SECTOR"))))));
        Assert.True((await commands.UpdateKeyAsync(t.TenantId, Cfg(t, "u3"), new(key, 2, D(("description", "Pro rata"))))).Status == CommandStatus.Ok);

        // a rate version starts after the latest, is replayed and cannot be changed
        var replay = await commands.CreateRateAsync(t.TenantId, Cfg(t, "r-a"), new(key, D(("valid_from", "2026-01-01"), ("rate_pct", 80m), ("rate_status", "PROVISIONAL"))));
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(["CONFIG.VALID_FROM_NOT_LATER"], Codes(await commands.CreateRateAsync(t.TenantId, Cfg(t, "r-b"), new(key, D(("valid_from", "2026-01-01"), ("rate_pct", 70m), ("rate_status", "FINAL"))))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await commands.CreateRateAsync(t.TenantId, Cfg(t, "r-c"), new(key, D(("valid_from", "2026-07-01"), ("rate_pct", 101m), ("rate_status", "FINAL"))))));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await commands.CreateRateAsync(t.TenantId, Cfg(t, "r-d"), new(Guid.NewGuid(), D(("valid_from", "2026-07-01"), ("rate_pct", 70m), ("rate_status", "FINAL"))))));
        await Rate(t, key, 0m, "2026-07-01", "e");
        Assert.Equal(2L, await t.ScalarAsync<long>("select count(*) from core.vat_deduction_rate where tenant_id = @t"));

        // a version of a code needs an active key of its registration, and a key an active code version names stays
        var tax = new TaxCommands(t.App);
        var code = await tax.CreateCodeAsync(t.TenantId, Cfg(t, "c1"), new(D(("company_id", t.CompanyId), ("code", "K1"), ("description", "k"), ("tax_type", "INPUT"), ("vat_registration_id", w.Registration))));
        Assert.Equal(["CONFIG.DEDUCTION_KEY_UNKNOWN"], Codes(await tax.CreateVersionAsync(t.TenantId, Cfg(t, "v1"), new(code.Value!.EntityId,
            D(("valid_from", "2026-01-01"), ("rate", 25m), ("vat_account_id", w.Receivable), ("deduction_key", "NOPE")), []))));
        Assert.True((await tax.CreateVersionAsync(t.TenantId, Cfg(t, "v2"), new(code.Value.EntityId,
            D(("valid_from", "2026-01-01"), ("rate", 25m), ("vat_account_id", w.Receivable), ("deduction_key", "PRORATA")), []))).Status == CommandStatus.Ok);
        Assert.Equal(["CONFIG.DEDUCTION_KEY_REFERENCED"], Codes(await commands.DeactivateKeyAsync(t.TenantId, Cfg(t, "d1"), new(key, 3, "RETIRED"))));
        Assert.True((await tax.DeactivateCodeAsync(t.TenantId, Cfg(t, "d2"), new(code.Value.EntityId, 1, "RETIRED"))).Status == CommandStatus.Ok);
        Assert.True((await commands.DeactivateKeyAsync(t.TenantId, Cfg(t, "d3"), new(key, 3, "RETIRED"))).Status == CommandStatus.Ok);
        Assert.True((await commands.ReactivateKeyAsync(t.TenantId, Cfg(t, "d4"), new(key, 4, "BACK"))).Status == CommandStatus.Ok);
    }
}
