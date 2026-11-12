using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;
using FinanceCore.Core.Posting;

namespace FinanceCore.Data.Tests;

/// <summary>The VAT content of the DK package (18, 8, country-packages/DK) applied to a company (F-IMPL-39).</summary>
[Collection("db")]
public class CountryPackageTests
{
    private static string Describe<T>(CommandResult<T> r) => r.Status + ": " + string.Join(", ", r.Errors.Select(e => $"{e.Code} {e.Path}"));
    private static Dictionary<string, object?> D(params (string, object?)[] f) => f.ToDictionary(x => x.Item1, x => x.Item2);

    private static string PackagesDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "country-packages", "DK"))) return Path.Combine(dir.FullName, "country-packages");
        throw new DirectoryNotFoundException("country-packages was not found above " + AppContext.BaseDirectory);
    }

    private static CompanyInfo Company(TestTenant t, string country = "DK") => new(t.CompanyId, "C1", "Test A/S", country, "ACTIVE", null);

    private static PackageInputs Inputs(int profile = 1, bool special = false, string vatId = "DK12345678", string filing = "QUARTERLY", decimal? proRata = null) =>
        new(vatId, filing, null, "ADJUST_NEXT", profile, special, new DateOnly(2026, 1, 1), "DKK", ProRataRatePct: proRata);

    private static Task<PackageApplyResult> Apply(TestTenant t, PackageInputs inputs) =>
        new CountryPackageApplier(TestTenant.AdminSource).ApplyVatAsync(t.TenantId, Company(t), PackagesDirectory(), "DK", inputs);

    private static int Count(PackageApplyResult r, string type, string action = "CREATED") => r.Items.Count(i => i.EntityType == type && i.Action == action);

    [Fact]
    public async Task TheCodesOfBusinessTypeOneAreAppliedWithBoxesKeysRulesAndTheSettlementRole()
    {
        var t = await TestTenant.CreateReadyAsync();
        var r = await Apply(t, Inputs());
        Assert.Equal(("DK", "DRAFT"), (r.PackageCode, r.State));
        Assert.Equal((10, 10, 10, 2, 1, 4, 1), (Count(r, "TAX_CODE"), Count(r, "TAX_CODE_VERSION"), Count(r, "TAX_RETURN_BOX"), Count(r, "TAX_CLASS"),
            Count(r, "VAT_REGISTRATION"), Count(r, "VAT_DEDUCTION_KEY"), Count(r, "ACCOUNT_ROLE_ASSIGNMENT")));
        Assert.Equal(2, Count(r, "TAX_DETERMINATION_RULE"));                                    // the sales rule and the purchase rule of the domestic scenario
        Assert.Equal(0, Count(r, "VAT_DEDUCTION_RATE"));                                         // a rate is a company input
        Assert.True(Count(r, "GL_ACCOUNT") >= 3);

        Assert.Equal(10L, await t.ScalarAsync<long>("select count(*) from core.tax_code where tenant_id = @t", ("t", t.TenantId)));
        Assert.Equal(10L, await t.ScalarAsync<long>("select count(*) from core.tax_code_version where tenant_id = @t", ("t", t.TenantId)));
        Assert.True(await t.ScalarAsync<long>("select count(*) from core.tax_code_version_box where tenant_id = @t", ("t", t.TenantId)) > 0);
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.account_role_assignment where tenant_id = @t and role_code = 'VAT_SETTLEMENT' and vat_registration_id is not null", ("t", t.TenantId)));
        Assert.Equal("S01", await t.ScalarAsync<string>("select c.code from core.tax_determination_rule r join core.tax_code c on c.tenant_id = r.tenant_id and c.tax_code_id = r.tax_code_id where r.tenant_id = @t and r.transaction_type = 'SALES'", ("t", t.TenantId)));
        Assert.Equal(2L, await t.ScalarAsync<long>("select count(*) from core.tax_determination_alternative where tenant_id = @t and rule_id = (select rule_id from core.tax_determination_rule where tenant_id = @t and transaction_type = 'SALES')", ("t", t.TenantId)));

        // the settlement series and the record of the apply (18, 7.1)
        Assert.Equal(1, Count(r, "NUMBER_SERIES_ASSIGNMENT"));
        Assert.Equal("GL-JRN", await t.ScalarAsync<string>("select s.series_code from core.number_series_assignment a join core.number_series s on s.tenant_id = a.tenant_id and s.series_id = a.series_id where a.tenant_id = @t and a.document_type = 'VAT_SETTLE'", ("t", t.TenantId)));
        Assert.NotEqual(Guid.Empty, r.AppliedId);
        Assert.Equal((r.Items.Count, r.Items.Count(i => i.Action == "CREATED")), (
            (int)await t.ScalarAsync<int>("select item_count from core.applied_config_package where tenant_id = @t and applied_id = @a", ("t", t.TenantId), ("a", r.AppliedId)),
            (int)await t.ScalarAsync<int>("select created_count from core.applied_config_package where tenant_id = @t and applied_id = @a", ("t", t.TenantId), ("a", r.AppliedId))));
        Assert.Equal((long)r.Items.Count, await t.ScalarAsync<long>("select count(*) from core.applied_config_package_item where tenant_id = @t and applied_id = @a", ("t", t.TenantId), ("a", r.AppliedId)));
        Assert.Equal(64, (await t.ScalarAsync<string>("select manifest_digest from core.applied_config_package where tenant_id = @t", ("t", t.TenantId))).Length);
        Assert.Equal("DK-TAX-S01", await t.ScalarAsync<string>("select item_id from core.applied_config_package_item where tenant_id = @t and applied_id = @a and entity_type = 'TAX_CODE' and action = 'CREATED' and item_id = 'DK-TAX-S01'", ("t", t.TenantId), ("a", r.AppliedId)));
        await Assert.ThrowsAnyAsync<Exception>(() => t.ExecuteAsync("update core.applied_config_package set item_count = 0 where tenant_id = @t", ("t", t.TenantId)));

        // applied again, also with other inputs, it creates nothing: an object that exists is never changed (18, 7)
        var again = await Apply(t, Inputs());
        Assert.True(again.Items.All(i => i.Action != "CREATED"), string.Join(";", again.Items.Where(i => i.Action == "CREATED").Select(i => i.ItemId)));
        var applies = await new CountryPackageApplier(TestTenant.AdminSource).ListAppliedAsync(t.TenantId, t.CompanyId);
        Assert.Equal(2, applies.Count);
        Assert.Equal((0, r.Items.Count(i => i.Action == "CREATED")), (applies[0].CreatedCount, applies[1].CreatedCount));
        var other = await Apply(t, Inputs(vatId: "DK87654321", filing: "MONTHLY"));
        Assert.True(other.Items.All(i => i.Action != "CREATED"), string.Join(";", other.Items.Where(i => i.Action == "CREATED").Select(i => i.ItemId)));
        Assert.Equal("QUARTERLY", await t.ScalarAsync<string>("select filing_frequency from core.company_vat_registration where tenant_id = @t", ("t", t.TenantId)));
        Assert.Equal(10L, await t.ScalarAsync<long>("select count(*) from core.tax_code where tenant_id = @t", ("t", t.TenantId)));
    }

    [Fact]
    public async Task ADomesticSaleIsTaxedWithTheCodeOfThePackageAndReachesTheReturn()
    {
        var t = await TestTenant.CreateReadyAsync();
        await Apply(t, Inputs());
        Guid control = Guid.NewGuid();
        await t.ExecuteAsync(
            "insert into core.gl_account (tenant_id, company_id, account_id, account_no, name, account_type, tax_code_rule, open_item_mode) values (@t, @c, @k, '1300', 'Receivables', 'ASSET', 'FORBIDDEN', 'CONTROL'); " +
            "update core.gl_account set tax_code_rule = 'MANDATORY' where tenant_id = @t and account_id = @rev",
            ("c", t.CompanyId), ("k", control), ("rev", t.Revenue));
        var partnerRequest = new CreatePartnerRequest(
            D(("partner_code", "KUNDE1"), ("partner_kind", "ORGANISATION"), ("is_customer", true), ("is_supplier", false)),
            D(("legal_name", "Kunde ApS"), ("address_line_1", "Vej 1"), ("postal_code", "2100"), ("city", "København"), ("country_code", "DK")),
            [new PartnerRelationInput(t.CompanyId, D(("is_customer", true), ("is_supplier", false)))]);
        Guid partner = (await new PartnerCommands(t.App).CreateAsync(t.TenantId, t.Context("p", "write.master"), partnerRequest)).Value!.EntityId;

        var invoice = await new JournalDocumentPoster(t.App).PostAsync(t.TenantId, t.Context("i1"), t.Journal("2026-02-10") with
        {
            DocumentType = "AR_INVOICE", PartnerId = partner,
            Lines = [new RequestLine("1", control, 1250m, null, new OpenItemRequest(PartnerId: partner, DueDate: new DateOnly(2026, 3, 10))), new RequestLine("2", t.Revenue, -1000m)]
        });
        Assert.True(invoice.Status == CommandStatus.Ok, Describe(invoice));
        Assert.Equal(-250m, invoice.Value!.Lines.Single(l => l.LineType == "TAX").AmountTc);
        Assert.Equal("S01", await t.ScalarAsync<string>("select c.code from core.journal_line_tax x join core.tax_code c on c.tenant_id = x.tenant_id and c.tax_code_id = x.tax_code_id where x.tenant_id = @t", ("t", t.TenantId)));
        Assert.Equal("7680", await t.ScalarAsync<string>("select a.account_no from core.journal_line l join core.gl_account a on a.tenant_id = l.tenant_id and a.account_id = l.account_id where l.tenant_id = @t and l.line_type = 'TAX'", ("t", t.TenantId)));

        for (int m = 1; m <= 3; m++) await t.SetPeriodStatusAsync(m, "SOFT_CLOSED");
        var registration = await t.ScalarAsync<Guid>("select registration_id from core.company_vat_registration where tenant_id = @t", ("t", t.TenantId));
        var ret = await new VatReturnCommands(t.App).PrepareAsync(t.TenantId, t.Context("prep", "vat.prepare"), new(registration, "STANDARD", 2026, 1));
        Assert.True(ret.Status == CommandStatus.Ok, Describe(ret));
        Assert.Equal(250m, ret.Value!.Lines.Single(l => l.BoxCode == "DK_SALES_VAT").TaxAmount);

        // the package assigned the series of the settlement document: the return is settled against the role of the package
        var settled = await new VatSettlement(t.App).SettleAsync(t.TenantId, t.Context("settle", "vat.settle"), new(ret.Value.ReturnId, null));
        Assert.True(settled.Status == CommandStatus.Ok, Describe(settled));
        Assert.Equal("7840", await t.ScalarAsync<string>("select a.account_no from core.journal_line l join core.gl_account a on a.tenant_id = l.tenant_id and a.account_id = l.account_id where l.tenant_id = @t and l.document_id = @d and l.amount_lc < 0", ("t", t.TenantId), ("d", settled.Value!.Document.DocumentId)));
    }

    [Fact]
    public async Task AllCodesOfBusinessTypeFourWithTheSpecialCodesAndAnInitialProRataRateAreApplied()
    {
        var t = await TestTenant.CreateReadyAsync();
        var r = await Apply(t, Inputs(profile: 4, special: true, proRata: 80m));
        Assert.Equal(55, Count(r, "TAX_CODE"));                                                   // the 55 codes of the package
        Assert.Equal(55, Count(r, "TAX_CODE_VERSION"));
        Assert.Equal(1, Count(r, "VAT_DEDUCTION_RATE"));
        Assert.Equal(80m, await t.ScalarAsync<decimal>("select rate_pct from core.vat_deduction_rate where tenant_id = @t and key_code = 'PRO_RATA'", ("t", t.TenantId)));
        Assert.Equal("PROVISIONAL", await t.ScalarAsync<string>("select rate_status from core.vat_deduction_rate where tenant_id = @t and key_code = 'PRO_RATA'", ("t", t.TenantId)));
        Assert.Equal(10L, await t.ScalarAsync<long>("select count(*) from core.tax_determination_rule where tenant_id = @t", ("t", t.TenantId)));
        Assert.True(await t.ScalarAsync<long>("select count(*) from core.tax_code_version where tenant_id = @t and deduction_key is not null", ("t", t.TenantId)) > 0);
    }

    [Fact]
    public async Task APackageThatIsNotReleasedIsRefusedForAProductionTenantAndTheInputsAreChecked()
    {
        var t = await TestTenant.CreateReadyAsync();
        var applier = new CountryPackageApplier(TestTenant.AdminSource);
        async Task<string> Refused(Func<Task> action) => (await Assert.ThrowsAsync<ProvisioningException>(action)).Code;
        Assert.Equal("PACKAGE.COUNTRY_MISMATCH", await Refused(() => applier.ApplyVatAsync(t.TenantId, Company(t, "SE"), PackagesDirectory(), "DK", Inputs())));
        Assert.Equal("PACKAGE.NOT_FOUND", await Refused(() => applier.ApplyVatAsync(t.TenantId, Company(t), PackagesDirectory(), "XX", Inputs())));
        Assert.Equal("PACKAGE.INPUT_INVALID", await Refused(() => applier.ApplyVatAsync(t.TenantId, Company(t), PackagesDirectory(), "DK", Inputs(profile: 7))));
        Assert.Equal("PACKAGE.INPUT_INVALID", await Refused(() => applier.ApplyVatAsync(t.TenantId, Company(t), PackagesDirectory(), "DK", Inputs(filing: "WEEKLY"))));
        Assert.Equal("PACKAGE.INPUT_INVALID", await Refused(() => applier.ApplyVatAsync(t.TenantId, Company(t), PackagesDirectory(), "DK", Inputs(vatId: " "))));

        await t.ExecuteAsync("update platform.tenant set purpose = 'PRODUCTION' where tenant_id = @t", ("t", t.TenantId));
        Assert.Equal("PACKAGE.NOT_RELEASED", await Refused(() => applier.ApplyVatAsync(t.TenantId, Company(t), PackagesDirectory(), "DK", Inputs())));
        Assert.Equal(0L, await t.ScalarAsync<long>("select count(*) from core.tax_code where tenant_id = @t", ("t", t.TenantId)));
    }
}
