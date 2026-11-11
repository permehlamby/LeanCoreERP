using FinanceCore.Core.Chains;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;
using FinanceCore.Core.Posting;

namespace FinanceCore.Data.Tests;

/// <summary>The determination rules, the partner registrations and the tax lines of a posting (07, 8.4, 9.4, 02, 7, 08, 5.2, 6.7; built choices in 07, 13.2l).</summary>
[Collection("db")]
public class TaxPostingTests
{
    private static string Describe<T>(CommandResult<T> r) => r.Status + ": " + string.Join(", ", r.Errors.Select(e => $"{e.Code} {e.Path}"));
    private static string[] Codes<T>(CommandResult<T> r) => r.Errors.Select(e => e.Code).ToArray();
    private static Dictionary<string, object?> D(params (string, object?)[] f) => f.ToDictionary(x => x.Item1, x => x.Item2);
    private static CommandContext Cfg(TestTenant t, string key) => t.Context(key, "write.config");

    private sealed record World(Guid Registration, Guid Payable, Guid Receivable, Guid RcCounter, Guid U25, Guid I25, Guid Z0, Guid Rc25);

    /// <summary>A registration in DKK, the accounts of the VAT, four codes with a version each, and Revenue and Expense that need a tax code.</summary>
    private static async Task<World> Setup(TestTenant t)
    {
        Guid payable = Guid.NewGuid(), receivable = Guid.NewGuid(), counter = Guid.NewGuid();
        await t.ExecuteAsync(
            "insert into core.gl_account (tenant_id, company_id, account_id, account_no, name, account_type, tax_code_rule) values " +
            "(@t, @c, @p, '2700', 'VAT payable', 'LIABILITY', 'FORBIDDEN'), (@t, @c, @r, '2710', 'VAT receivable', 'ASSET', 'FORBIDDEN'), (@t, @c, @k, '2720', 'VAT reverse charge', 'ASSET', 'FORBIDDEN'); " +
            "update core.gl_account set tax_code_rule = 'MANDATORY' where tenant_id = @t and account_id in (@rev, @exp)",
            ("c", t.CompanyId), ("p", payable), ("r", receivable), ("k", counter), ("rev", t.Revenue), ("exp", t.Expense));
        var tax = new TaxCommands(t.App);
        var reg = await tax.CreateRegistrationAsync(t.TenantId, Cfg(t, "w-reg"), new(D(("company_id", t.CompanyId), ("country_code", "DK"), ("vat_id", "DK12345678"), ("scheme", "STANDARD"),
            ("filing_frequency", "QUARTERLY"), ("correction_method", "ADJUST_NEXT"), ("return_currency", "DKK"), ("valid_from", "2026-01-01"))));
        Assert.True(reg.Status == CommandStatus.Ok, Describe(reg));
        async Task<Guid> Code(string code, string type, Guid vatAccount, string key, params (string, object?)[] extra)
        {
            var c = await tax.CreateCodeAsync(t.TenantId, Cfg(t, "w-c-" + key), new(D(("company_id", t.CompanyId), ("code", code), ("description", code), ("tax_type", type), ("vat_registration_id", reg.Value!.EntityId))));
            Assert.True(c.Status == CommandStatus.Ok, Describe(c));
            var fields = D(("valid_from", "2026-01-01"), ("vat_account_id", vatAccount), ("rate", type is "ZERO_RATED" ? 0m : 25m));
            foreach (var (k, v) in extra) fields[k] = v;
            var v1 = await tax.CreateVersionAsync(t.TenantId, Cfg(t, "w-v-" + key), new(c.Value!.EntityId, fields, []));
            Assert.True(v1.Status == CommandStatus.Ok, Describe(v1));
            return c.Value.EntityId;
        }
        return new World(reg.Value!.EntityId, payable, receivable, counter,
            await Code("U25", "OUTPUT", payable, "u25"), await Code("I25", "INPUT", receivable, "i25"),
            await Code("Z0", "ZERO_RATED", payable, "z0", ("en16931_vat_category", "Z")),
            await Code("RC25", "INPUT", payable, "rc25", ("reverse_charge", true), ("reverse_charge_account_id", counter), ("en16931_vat_category", "AE")));
    }

    private static async Task<Guid> Rule(TestTenant t, string key, Guid code, short priority, Guid[]? alternatives = null, params (string, object?)[] keys)
    {
        var fields = D(("valid_from", "2026-01-01"), ("priority", (int)priority), ("tax_code_id", code));
        foreach (var (k, v) in keys) fields[k] = v;
        var r = await new TaxDeterminationCommands(t.App).CreateRuleAsync(t.TenantId, Cfg(t, key), new(t.CompanyId, fields, alternatives ?? []));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        return r.Value!.RuleId;
    }

    private static PostJournalDocumentRequest Sale(TestTenant t, decimal gross, decimal net, Guid? code = null, string date = "2026-03-15") =>
        t.Journal(date) with
        {
            OriginCountry = "DK", DestinationCountry = "DK",
            Lines = [new RequestLine("1", t.Bank, gross), new RequestLine("2", t.Revenue, -net, TaxCodeId: code)]
        };

    private static Task<CommandResult<PostedDocument>> Post(TestTenant t, string key, PostJournalDocumentRequest req) =>
        new JournalDocumentPoster(t.App).PostAsync(t.TenantId, t.Context(key), req);

    private static async Task AssertChainIsOk(TestTenant t)
    {
        var verify = await new ChainVerification(t.App).VerifyAsync(t.TenantId, t.Context("verify", ChainVerification.Permission), new(t.CompanyId, "LEDGER"));
        Assert.True(verify.Status == CommandStatus.Ok && verify.Value!.Ok, Describe(verify) + " " + string.Join(";", verify.Value?.Breaks.Select(p => p.Code + " " + p.Detail) ?? []));
    }

    // ---------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task ASaleIsTaxedByTheRuleItsTaxLineIsPostedAndTheChainVerifies()
    {
        var t = await TestTenant.CreateReadyAsync();
        var w = await Setup(t);
        await Rule(t, "r1", w.U25, 10, null, ("origin_country", "DK"), ("destination_country", "DK"));

        var r = await Post(t, "p1", Sale(t, 1250m, 1000m));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        var lines = r.Value!.Lines;
        Assert.Equal(["BASE", "BASE", "TAX"], lines.Select(l => l.LineType).ToArray());
        Assert.Equal(w.U25, lines[1].TaxCodeId);
        Assert.Equal(w.U25, lines[2].TaxCodeId);
        Assert.Equal(-250m, lines[2].AmountTc);
        Assert.Equal(w.Payable, lines[2].AccountId);
        Assert.Equal(0m, lines.Sum(l => l.AmountTc));
        Guid doc = r.Value.DocumentId;

        async Task<T> Tax<T>(string column) => await t.ScalarAsync<T>($"select {column} from core.journal_line_tax where tenant_id = @t and document_id = @d and line_no = 3", ("d", doc));
        Assert.Equal("OUTPUT", await Tax<string>("tax_type"));
        Assert.Equal(25m, await Tax<decimal>("tax_rate"));
        Assert.Equal(-1000m, await Tax<decimal>("base_amount_tc"));
        Assert.Equal(-1000m, await Tax<decimal>("base_amount_lc"));
        Assert.Equal("DKK", (await Tax<string>("vat_currency")).Trim());
        Assert.Equal(-1000m, await Tax<decimal>("vat_base_amount"));
        Assert.Equal(-250m, await Tax<decimal>("vat_tax_amount"));
        Assert.Equal(1m, await Tax<decimal>("vat_rate"));
        Assert.True(await Tax<bool>("origin_eu"));
        Assert.True(await Tax<bool>("destination_eu"));
        Assert.False(await Tax<bool>("is_override"));
        Assert.Equal("PER_DOCUMENT", await Tax<string>("rounding_mode"));
        Assert.Equal(100m, await Tax<decimal>("deduction_share"));
        Assert.Equal(-250m, await Tax<decimal>("gross_tax_amount_tc"));
        Assert.Equal("2026-03-15", await Tax<string>("tax_date::text"));
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.journal_line_tax_base where tenant_id = @t and document_id = @d and tax_line_no = 3 and base_line_no = 2", ("d", doc)));
        await AssertChainIsOk(t);

        // the posted amounts of the tax line in the other currencies
        Assert.Equal(-33.5m, lines[2].AmountRc);
        Assert.True((await Post(t, "p1", Sale(t, 1250m, 1000m))).IdempotentReplay);
    }

    [Fact]
    public async Task TheLowestPriorityWinsThenTheMostSpecificRule()
    {
        var t = await TestTenant.CreateReadyAsync();
        var w = await Setup(t);
        var partner = await NewPartner(t, "pa", "ACME", "DK", customer: true);
        await Rule(t, "r1", w.Z0, 50);
        await Rule(t, "r2", w.U25, 50, null, ("transaction_type", "SALES"), ("destination_country", "DK"));

        var invoice = t.Journal() with { DocumentType = "AR_INVOICE", PartnerId = partner, Lines = [new RequestLine("1", t.Bank, 1250m), new RequestLine("2", t.Revenue, -1000m)] };
        var first = await Post(t, "i1", invoice);
        Assert.True(first.Status == CommandStatus.Ok, Describe(first));
        Assert.Equal(-250m, first.Value!.Lines.Single(l => l.LineType == "TAX").AmountTc);   // the more specific rule: no origin or destination was named, the defaults of an AR document were used

        // an explicit rank of 40 beats both, a zero-rated code makes a tax line of 0 (02, 7.1)
        await Rule(t, "r3", w.Z0, 40);
        var second = await Post(t, "i2", invoice with { Lines = [new RequestLine("1", t.Bank, 1000m), new RequestLine("2", t.Revenue, -1000m)] });
        Assert.True(second.Status == CommandStatus.Ok, Describe(second));
        var tax = second.Value!.Lines.Single(l => l.LineType == "TAX");
        Assert.Equal(0m, tax.AmountTc);
        Assert.Equal(w.Z0, tax.TaxCodeId);
        await AssertChainIsOk(t);
    }

    [Fact]
    public async Task ADocumentWithoutAMatchingRuleOrWithoutOriginAndDestinationIsRejected()
    {
        var t = await TestTenant.CreateReadyAsync();
        var w = await Setup(t);
        var none = await Post(t, "n1", Sale(t, 1250m, 1000m));
        Assert.Equal(["TAX.NO_RULE_MATCHED"], Codes(none));
        Assert.Equal("lines[1]", none.Errors[0].Path);

        await Rule(t, "r1", w.U25, 10);
        var partnerless = await Post(t, "n2", Sale(t, 1250m, 1000m) with { OriginCountry = null, DestinationCountry = null });
        Assert.Equal(["TAX.ORIGIN_DESTINATION_REQUIRED"], Codes(partnerless));
        Assert.Equal(0L, await t.ScalarAsync<long>("select count(*) from core.journal_document where tenant_id = @t"));
        Assert.Equal(0L, await t.ScalarAsync<long>("select count(*) from core.journal_line_tax where tenant_id = @t"));
    }

    [Fact]
    public async Task ASuppliedCodeMustBeThePermittedAlternativeAndAPostingWithOneIsMarkedAsOverride()
    {
        var t = await TestTenant.CreateReadyAsync();
        var w = await Setup(t);
        await Rule(t, "r1", w.U25, 10, [w.Z0]);

        var alt = await Post(t, "a1", Sale(t, 1000m, 1000m, w.Z0));
        Assert.True(alt.Status == CommandStatus.Ok, Describe(alt));
        Assert.Contains("TAX.CODE_DIFFERS_FROM_DETERMINED", alt.Warnings.Select(x => x.Code));
        Assert.True(await t.ScalarAsync<bool>("select is_override from core.journal_line_tax where tenant_id = @t and document_id = @d", ("d", alt.Value!.DocumentId)));
        Assert.Equal(w.U25, await t.ScalarAsync<Guid>("select determined_tax_code_id from core.journal_line_tax where tenant_id = @t and document_id = @d", ("d", alt.Value.DocumentId)));

        var notAllowed = await Post(t, "a2", Sale(t, 1250m, 1000m, w.I25));
        Assert.Equal(["TAX.CODE_NOT_PERMITTED"], Codes(notAllowed));
        // the account of the bank line forbids a tax code
        var forbidden = await Post(t, "a3", Sale(t, 1250m, 1000m) with { Lines = [new RequestLine("1", t.Bank, 1250m, TaxCodeId: w.U25), new RequestLine("2", t.Revenue, -1000m)] });
        Assert.Equal(["TAX.CODE_NOT_PERMITTED"], Codes(forbidden));
    }

    [Fact]
    public async Task TheTaxIsRoundedPerDocumentOrPerLineAsTheCompanySettingSays()
    {
        var t = await TestTenant.CreateReadyAsync();
        var w = await Setup(t);
        await Rule(t, "r1", w.U25, 10);
        PostJournalDocumentRequest Three(decimal gross) => Sale(t, 0m, 0m) with
        {
            Lines = [new RequestLine("1", t.Bank, gross), new RequestLine("2", t.Revenue, -33.33m), new RequestLine("3", t.Revenue, -33.33m), new RequestLine("4", t.Revenue, -33.33m)]
        };

        var doc = await Post(t, "d1", Three(124.99m));
        Assert.True(doc.Status == CommandStatus.Ok, Describe(doc));
        Assert.Equal([25m], doc.Value!.Lines.Where(l => l.LineType == "TAX").Select(l => -l.AmountTc).ToArray());
        Assert.Equal(3L, await t.ScalarAsync<long>("select count(*) from core.journal_line_tax_base where tenant_id = @t and document_id = @d", ("d", doc.Value.DocumentId)));

        await t.ExecuteAsync("update core.company_setting_version set tax_rounding_mode = 'PER_LINE' where tenant_id = @t");
        var lineMode = await Post(t, "d2", Three(124.98m));
        Assert.True(lineMode.Status == CommandStatus.Ok, Describe(lineMode));
        Assert.Equal([8.33m, 8.33m, 8.33m], lineMode.Value!.Lines.Where(l => l.LineType == "TAX").Select(l => -l.AmountTc).ToArray());
        Assert.Equal("PER_LINE", await t.ScalarAsync<string>("select rounding_mode from core.journal_line_tax where tenant_id = @t and document_id = @d and line_no = 5", ("d", lineMode.Value.DocumentId)));
        // the gross of the other mode does not balance
        Assert.Equal(["BALANCE.UNBALANCED_TC"], Codes(await Post(t, "d3", Three(124.99m))));
        await AssertChainIsOk(t);
    }

    [Fact]
    public async Task ASuppliedTaxAmountWithinTheToleranceLeavesItsDifferenceOnTheTaxRoundingAccount()
    {
        var t = await TestTenant.CreateReadyAsync();
        var w = await Setup(t);
        await Rule(t, "r1", w.U25, 10);
        PostJournalDocumentRequest Invoice(decimal gross) => Sale(t, gross, 1000m) with
        {
            Lines = [new RequestLine("1", t.Bank, gross), new RequestLine("2", t.Revenue, -1000m, TaxAmountTc: -250.40m)]
        };

        var out0 = await Post(t, "s1", Invoice(1250.40m));
        Assert.Equal(["TAX.AMOUNT_OUT_OF_TOLERANCE"], Codes(out0));
        await t.ExecuteAsync("update core.company_setting_version set tax_amount_tolerance = 0.50 where tenant_id = @t");
        var ok = await Post(t, "s2", Invoice(1250.40m));
        Assert.True(ok.Status == CommandStatus.Ok, Describe(ok));
        Assert.Equal(["BASE", "BASE", "TAX", "TAX_ROUNDING"], ok.Value!.Lines.Select(l => l.LineType).ToArray());
        Assert.Equal(-250m, ok.Value.Lines[2].AmountTc);
        Assert.Equal(-0.40m, ok.Value.Lines[3].AmountTc);
        Assert.Equal(-250.40m, await t.ScalarAsync<decimal>("select tax_amount_supplied_tc from core.journal_line_tax where tenant_id = @t and document_id = @d", ("d", ok.Value.DocumentId)));
        Assert.Equal("7910", await t.ScalarAsync<string>("select account_no from core.gl_account where tenant_id = @t and account_id = @a", ("a", ok.Value.Lines[3].AccountId)));
        Assert.Equal(0m, ok.Value.Lines.Sum(l => l.AmountTc));
        await AssertChainIsOk(t);
    }

    [Fact]
    public async Task AReverseChargeCodePostsAnOutputAndADeductibleLineOverTheSameBase()
    {
        var t = await TestTenant.CreateReadyAsync();
        var w = await Setup(t);
        await Rule(t, "r1", w.Rc25, 10);
        var r = await Post(t, "rc", Sale(t, 0m, 0m) with { Lines = [new RequestLine("1", t.Expense, 1000m), new RequestLine("2", t.Bank, -1000m)] });
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        var lines = r.Value!.Lines;
        Assert.Equal(["BASE", "BASE", "TAX", "TAX"], lines.Select(l => l.LineType).ToArray());
        Assert.Equal((w.Payable, -250m), (lines[2].AccountId, lines[2].AmountTc));
        Assert.Equal((w.RcCounter, 250m), (lines[3].AccountId, lines[3].AmountTc));
        Assert.Equal(["OUTPUT", "INPUT"], [await t.ScalarAsync<string>("select tax_type from core.journal_line_tax where tenant_id = @t and document_id = @d and line_no = 3", ("d", r.Value.DocumentId)),
            await t.ScalarAsync<string>("select tax_type from core.journal_line_tax where tenant_id = @t and document_id = @d and line_no = 4", ("d", r.Value.DocumentId))]);
        Assert.Equal(2L, await t.ScalarAsync<long>("select count(*) from core.journal_line_tax_base where tenant_id = @t and document_id = @d and base_line_no = 1", ("d", r.Value.DocumentId)));
        await AssertChainIsOk(t);
    }

    [Fact]
    public async Task TheMirrorOfATaxDocumentCarriesTheTaxRowsWithOppositeAmountsAndTheChainVerifies()
    {
        var t = await TestTenant.CreateReadyAsync();
        var w = await Setup(t);
        await Rule(t, "r1", w.U25, 10);
        var original = await Post(t, "p1", Sale(t, 1250m, 1000m));
        Assert.True(original.Status == CommandStatus.Ok, Describe(original));
        var mirror = await new DocumentReverser(t.App).ReverseAsync(t.TenantId, t.Context("rev", "journal.reverse"), new(original.Value!.DocumentId, null, "CORRECTION", "Wrong"));
        Assert.True(mirror.Status == CommandStatus.Ok, Describe(mirror));
        Assert.Equal(250m, mirror.Value!.Lines.Single(l => l.LineType == "TAX").AmountTc);
        Assert.Equal(1000m, await t.ScalarAsync<decimal>("select base_amount_tc from core.journal_line_tax where tenant_id = @t and document_id = @d", ("d", mirror.Value.DocumentId)));
        Assert.Equal(250m, await t.ScalarAsync<decimal>("select vat_tax_amount from core.journal_line_tax where tenant_id = @t and document_id = @d", ("d", mirror.Value.DocumentId)));
        Assert.Equal(0m, await t.ScalarAsync<decimal>("select coalesce(sum(vat_tax_amount), 0) from core.journal_line_tax where tenant_id = @t", ("d", Guid.Empty)));
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.journal_line_tax_base where tenant_id = @t and document_id = @d and base_amount_tc = 1000", ("d", mirror.Value.DocumentId)));
        await AssertChainIsOk(t);
    }

    [Fact]
    public async Task ThePartnerVatStatusInTheDestinationCountryIsAKeyOfTheDetermination()
    {
        var t = await TestTenant.CreateReadyAsync();
        var w = await Setup(t);
        await Rule(t, "r1", w.Z0, 10, null, ("transaction_type", "SALES"), ("destination_country", "DE"), ("partner_vat_status", "REGISTERED"));
        await Rule(t, "r2", w.U25, 20, null, ("transaction_type", "SALES"), ("destination_country", "DE"));
        Guid registered = await NewPartner(t, "pr", "REG", "DE", customer: true), unregistered = await NewPartner(t, "pu", "CONSUMER", "DE", customer: true);
        var registration = await new TaxDeterminationCommands(t.App).CreateRegistrationAsync(t.TenantId, t.Context("reg", "write.master"),
            new(D(("partner_id", registered), ("country_code", "DE"), ("valid_from", "2026-01-01"), ("vat_status", "REGISTERED"), ("vat_id", "DE123456789"))));
        Assert.True(registration.Status == CommandStatus.Ok, Describe(registration));

        PostJournalDocumentRequest Invoice(Guid partner) => t.Journal() with { DocumentType = "AR_INVOICE", PartnerId = partner, Lines = [new RequestLine("1", t.Bank, 1250m), new RequestLine("2", t.Revenue, -1000m)] };
        var b2b = await Post(t, "b2b", Invoice(registered) with { Lines = [new RequestLine("1", t.Bank, 1000m), new RequestLine("2", t.Revenue, -1000m)] });
        Assert.True(b2b.Status == CommandStatus.Ok, Describe(b2b));
        Assert.Equal(w.Z0, b2b.Value!.Lines.Single(l => l.LineType == "TAX").TaxCodeId);
        Assert.Equal("REGISTERED", await t.ScalarAsync<string>("select partner_vat_status from core.journal_line_tax where tenant_id = @t and document_id = @d", ("d", b2b.Value.DocumentId)));
        Assert.Equal("DE", (await t.ScalarAsync<string>("select destination_country from core.journal_line_tax where tenant_id = @t and document_id = @d", ("d", b2b.Value.DocumentId))).Trim());

        var b2c = await Post(t, "b2c", Invoice(unregistered));
        Assert.True(b2c.Status == CommandStatus.Ok, Describe(b2c));
        Assert.Equal(w.U25, b2c.Value!.Lines.Single(l => l.LineType == "TAX").TaxCodeId);
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.journal_line_tax where tenant_id = @t and document_id = @d and partner_vat_status is null", ("d", b2c.Value.DocumentId)));

        // an inactive registration is none (08, 3.1)
        Assert.True((await new TaxDeterminationCommands(t.App).DeactivateRegistrationAsync(t.TenantId, t.Context("deact", "write.master"), new(registration.Value!.EntityId, 1, "ENDED"))).Status == CommandStatus.Ok);
        var after = await Post(t, "b2b2", Invoice(registered));
        Assert.Equal(w.U25, after.Value!.Lines.Single(l => l.LineType == "TAX").TaxCodeId);
        await AssertChainIsOk(t);
    }

    [Fact]
    public async Task TheVatAmountsAreTheReportingAmountsWhenTheRegistrationReportsInTheReportingCurrency()
    {
        var t = await TestTenant.CreateReadyAsync();
        var w = await Setup(t);
        var tax = new TaxCommands(t.App);
        var reg = await tax.CreateRegistrationAsync(t.TenantId, Cfg(t, "e-reg"), new(D(("company_id", t.CompanyId), ("country_code", "DK"), ("vat_id", "DK1"), ("scheme", "OSS_UNION"),
            ("filing_frequency", "QUARTERLY"), ("correction_method", "ADJUST_NEXT"), ("return_currency", "EUR"), ("valid_from", "2026-01-01"))));
        var code = await tax.CreateCodeAsync(t.TenantId, Cfg(t, "e-c"), new(D(("company_id", t.CompanyId), ("code", "UE"), ("description", "EUR"), ("tax_type", "OUTPUT"), ("vat_registration_id", reg.Value!.EntityId))));
        Assert.True((await tax.CreateVersionAsync(t.TenantId, Cfg(t, "e-v"), new(code.Value!.EntityId, D(("valid_from", "2026-01-01"), ("rate", 25m), ("vat_account_id", w.Payable)), []))).Status == CommandStatus.Ok);
        await Rule(t, "r1", code.Value.EntityId, 10);

        var r = await Post(t, "e1", Sale(t, 1250m, 1000m));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        Guid doc = r.Value!.DocumentId;
        Assert.Equal("EUR", (await t.ScalarAsync<string>("select vat_currency from core.journal_line_tax where tenant_id = @t and document_id = @d", ("d", doc))).Trim());
        Assert.Equal(-33.5m, await t.ScalarAsync<decimal>("select vat_tax_amount from core.journal_line_tax where tenant_id = @t and document_id = @d", ("d", doc)));
        Assert.Equal(-134m, await t.ScalarAsync<decimal>("select vat_base_amount from core.journal_line_tax where tenant_id = @t and document_id = @d", ("d", doc)));
        Assert.Equal(0.134m, await t.ScalarAsync<decimal>("select vat_rate from core.journal_line_tax where tenant_id = @t and document_id = @d", ("d", doc)));
        await AssertChainIsOk(t);
    }

    [Fact]
    public async Task WhatIsNotBuiltAnswersWithALimitCode()
    {
        var t = await TestTenant.CreateReadyAsync();
        var w = await Setup(t);
        var tax = new TaxCommands(t.App);
        var partial = await tax.CreateCodeAsync(t.TenantId, Cfg(t, "l-c"), new(D(("company_id", t.CompanyId), ("code", "I80"), ("description", "80"), ("tax_type", "INPUT"), ("vat_registration_id", w.Registration))));
        Assert.True((await tax.CreateVersionAsync(t.TenantId, Cfg(t, "l-v"), new(partial.Value!.EntityId, D(("valid_from", "2026-01-01"), ("rate", 25m), ("vat_account_id", w.Receivable), ("deductible_pct", 80m)), []))).Status == CommandStatus.Ok);
        await Rule(t, "r1", partial.Value.EntityId, 10);
        var r = await Post(t, "l1", Sale(t, 1250m, 1000m));
        Assert.Equal(["LIMIT.PARTIAL_DEDUCTION_NOT_BUILT"], Codes(r));

        var t2 = await TestTenant.CreateReadyAsync();
        var w2 = await Setup(t2);
        Assert.True((await tax.UpdateRegistrationAsync(t2.TenantId, Cfg(t2, "l-u"), new(w2.Registration, 1, D(("vat_rate_type", "VAT"))))).Status == CommandStatus.Ok);
        await Rule(t2, "r1", w2.U25, 10);
        Assert.Equal(["LIMIT.VAT_RATE_TYPE_NOT_BUILT"], Codes(await Post(t2, "l2", Sale(t2, 1250m, 1000m))));
    }

    [Fact]
    public async Task ATaxAccountAcceptsTaxLinesOnly()
    {
        var t = await TestTenant.CreateReadyAsync();
        var w = await Setup(t);
        await t.ExecuteAsync("update core.gl_account set tax_code_rule = 'OPTIONAL' where tenant_id = @t and account_id = @a", ("a", t.Revenue));
        var direct = await Post(t, "d1", t.Journal() with { Lines = [new RequestLine("1", w.Payable, 100m), new RequestLine("2", t.Revenue, -100m)] });
        Assert.Contains("ACCOUNT_RULE.TAX_ACCOUNT_DIRECT_POSTING", Codes(direct));
        Assert.True((await Post(t, "d2", t.Journal())).Status == CommandStatus.Ok);
        // the database refuses it too, whatever the command checks
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => t.ExecuteAsync(
            "insert into core.journal_line (tenant_id, company_id, document_id, line_no, ledger_id, line_type, account_id, posting_date, fiscal_year, fiscal_period, amount_tc, amount_lc, currency_lc, rate_lc, " +
            "amount_gc, currency_gc, rate_gc, rate_type, rate_date) select tenant_id, company_id, document_id, 99, ledger_id, 'BASE', @a, posting_date, fiscal_year, fiscal_period, 0, 0, 'DKK', 1, 0, 'EUR', 1, 'SPOT', posting_date " +
            "from core.journal_document where tenant_id = @t limit 1", ("a", w.Payable)));
        Assert.True(true, ex.Message);
    }

    // ---------------------------------------------------------------------------------------------------------
    // the commands
    // ---------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task ARuleVersionIsInsertOnlyStartsAfterTheLatestOneAndRefusesAnOverlap()
    {
        var t = await TestTenant.CreateReadyAsync();
        var w = await Setup(t);
        var commands = new TaxDeterminationCommands(t.App);
        Guid rule = await Rule(t, "r1", w.U25, 10, [w.Z0], ("destination_country", "DK"));
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.tax_determination_alternative where tenant_id = @t and rule_id = @r", ("r", rule)));

        var replay = await commands.CreateRuleAsync(t.TenantId, Cfg(t, "r1"), new(t.CompanyId, D(("valid_from", "2026-01-01"), ("priority", 10), ("tax_code_id", w.U25), ("destination_country", "DK")), [w.Z0]));
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(rule, replay.Value!.RuleId);
        Assert.Equal(["IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST"], Codes(await commands.CreateRuleAsync(t.TenantId, Cfg(t, "r1"), new(t.CompanyId, D(("valid_from", "2026-01-01"), ("priority", 11), ("tax_code_id", w.U25)), []))));

        // the same key values at the same priority and an overlapping time
        var overlap = await commands.CreateRuleAsync(t.TenantId, Cfg(t, "r2"), new(t.CompanyId, D(("valid_from", "2026-01-01"), ("priority", 10), ("tax_code_id", w.Z0), ("destination_country", "DK")), []));
        Assert.Equal(["CONFIG.OVERLAPPING_RULE"], Codes(overlap));
        // other values, or another priority, are fine
        await Rule(t, "r3", w.Z0, 10, null, ("destination_country", "SE"));
        await Rule(t, "r4", w.Z0, 11, null, ("destination_country", "DK"));

        var later = await commands.CreateRuleVersionAsync(t.TenantId, Cfg(t, "v1"), new(rule, D(("valid_from", "2026-07-01"), ("priority", 10), ("tax_code_id", w.Z0), ("destination_country", "DK")), []));
        Assert.True(later.Status == CommandStatus.Ok, Describe(later));
        Assert.Equal(rule, later.Value!.RuleId);
        Assert.Equal(["CONFIG.VALID_FROM_NOT_LATER"], Codes(await commands.CreateRuleVersionAsync(t.TenantId, Cfg(t, "v2"), new(rule, D(("valid_from", "2026-07-01"), ("priority", 10), ("tax_code_id", w.Z0)), []))));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await commands.CreateRuleVersionAsync(t.TenantId, Cfg(t, "v3"), new(Guid.NewGuid(), D(("valid_from", "2026-07-01"), ("priority", 10), ("tax_code_id", w.Z0)), []))));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await commands.CreateRuleAsync(t.TenantId, Cfg(t, "v4"), new(t.CompanyId, D(("valid_from", "2026-01-01"), ("priority", 1), ("tax_code_id", Guid.NewGuid())), []))));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await commands.CreateRuleAsync(t.TenantId, t.Context("v5", "write.master"), new(t.CompanyId, D(("valid_from", "2026-01-01"), ("priority", 1), ("tax_code_id", w.U25)), []))));
        Assert.Equal(["SCHEMA.REQUIRED_FIELD_MISSING"], Codes(await commands.CreateRuleAsync(t.TenantId, Cfg(t, "v6"), new(t.CompanyId, D(("valid_from", "2026-01-01")), []))).Distinct().ToArray());

        // the version in force at the posting date is the one that decides
        var march = await Post(t, "p-march", Sale(t, 1250m, 1000m));
        Assert.Equal(w.U25, march.Value!.Lines.Single(l => l.LineType == "TAX").TaxCodeId);
        var august = await Post(t, "p-aug", Sale(t, 1000m, 1000m, null, "2026-08-15"));
        Assert.Equal(w.Z0, august.Value!.Lines.Single(l => l.LineType == "TAX").TaxCodeId);
        await AssertChainIsOk(t);
    }

    [Fact]
    public async Task APartnerRegistrationIsCreatedChangedAndDeactivatedAndAPrivatePersonHasNoVatId()
    {
        var t = await TestTenant.CreateReadyAsync();
        var commands = new TaxDeterminationCommands(t.App);
        Guid org = await NewPartner(t, "o", "ORG", "DE", customer: true);
        CommandContext Ctx(string key) => t.Context(key, "write.master");
        var created = await commands.CreateRegistrationAsync(t.TenantId, Ctx("c1"), new(D(("partner_id", org), ("country_code", "DE"), ("valid_from", "2026-01-01"), ("vat_status", "REGISTERED"), ("vat_id", "DE1"))));
        Assert.True(created.Status == CommandStatus.Ok, Describe(created));
        Guid id = created.Value!.EntityId;
        Assert.True((await commands.CreateRegistrationAsync(t.TenantId, Ctx("c1"), new(D(("partner_id", org), ("country_code", "DE"), ("valid_from", "2026-01-01"), ("vat_status", "REGISTERED"), ("vat_id", "DE1"))))).IdempotentReplay);
        Assert.Equal(["CONFIG.DUPLICATE_CODE"], Codes(await commands.CreateRegistrationAsync(t.TenantId, Ctx("c2"), new(D(("partner_id", org), ("country_code", "DE"), ("valid_from", "2026-01-01"), ("vat_status", "NOT_REGISTERED"))))));
        Assert.Equal(["MASTER.PARTNER_NOT_FOUND"], Codes(await commands.CreateRegistrationAsync(t.TenantId, Ctx("c3"), new(D(("partner_id", Guid.NewGuid()), ("country_code", "DE"), ("valid_from", "2026-01-01"), ("vat_status", "REGISTERED"))))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await commands.CreateRegistrationAsync(t.TenantId, Ctx("c4"), new(D(("partner_id", org), ("country_code", "SE"), ("valid_from", "2026-02-01"), ("vat_status", "MAYBE"))))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await commands.CreateRegistrationAsync(t.TenantId, Ctx("c5"), new(D(("partner_id", org), ("country_code", "SE"), ("valid_from", "2026-02-01"), ("vat_status", "REGISTERED"), ("valid_to", "2026-01-01"))))));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await commands.CreateRegistrationAsync(t.TenantId, t.Context("c6", "write.config"), new(D(("partner_id", org), ("country_code", "SE"), ("valid_from", "2026-02-01"), ("vat_status", "REGISTERED"))))));

        var update = await commands.UpdateRegistrationAsync(t.TenantId, Ctx("u1"), new(id, 1, D(("vat_status", "NOT_REGISTERED"), ("valid_to", "2026-12-31"))));
        Assert.True(update.Status == CommandStatus.Ok, Describe(update));
        Assert.Equal(["SCHEMA.UNKNOWN_FIELD"], Codes(await commands.UpdateRegistrationAsync(t.TenantId, Ctx("u2"), new(id, 2, D(("country_code", "SE"))))));
        Assert.Equal(["CONFIG.ROW_VERSION_MISMATCH"], Codes(await commands.UpdateRegistrationAsync(t.TenantId, Ctx("u3"), new(id, 1, D(("vat_status", "REGISTERED"))))));
        Assert.True((await commands.DeactivateRegistrationAsync(t.TenantId, Ctx("d1"), new(id, 2, "ENDED"))).Status == CommandStatus.Ok);
        Assert.True((await commands.ReactivateRegistrationAsync(t.TenantId, Ctx("r1"), new(id, 3, "BACK"))).Status == CommandStatus.Ok);

        Guid person = await NewPartner(t, "p", "PERSON1", "DE", customer: true, kind: "PERSON");
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await commands.CreateRegistrationAsync(t.TenantId, Ctx("p1"), new(D(("partner_id", person), ("country_code", "DE"), ("valid_from", "2026-01-01"), ("vat_status", "REGISTERED"), ("vat_id", "DE9"))))));
        Assert.True((await commands.CreateRegistrationAsync(t.TenantId, Ctx("p2"), new(D(("partner_id", person), ("country_code", "DE"), ("valid_from", "2026-01-01"), ("vat_status", "REGISTERED"))))).Status == CommandStatus.Ok);
    }

    private static async Task<Guid> NewPartner(TestTenant t, string key, string code, string country, bool customer, string kind = "ORGANISATION")
    {
        var req = new CreatePartnerRequest(
            D(("partner_code", code), ("partner_kind", kind), ("is_customer", customer), ("is_supplier", false)),
            D(("legal_name", code + " GmbH"), ("address_line_1", "Hauptstrasse 1"), ("postal_code", "10115"), ("city", "Berlin"), ("country_code", country)),
            [new PartnerRelationInput(t.CompanyId, D(("is_customer", customer), ("is_supplier", false)))]);
        var r = await new PartnerCommands(t.App).CreateAsync(t.TenantId, t.Context(key, "write.master"), req);
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        return r.Value!.EntityId;
    }
}
