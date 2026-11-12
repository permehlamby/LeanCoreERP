using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;

namespace FinanceCore.Data.Tests;

/// <summary>The VAT configuration (07, 8.1 to 8.3, 08, 4.1): tax classes, VAT registrations, tax codes with versions and boxes, return boxes.</summary>
[Collection("db")]
public class TaxConfigTests
{
    private static string Describe<T>(CommandResult<T> r) => r.Status + ": " + string.Join(", ", r.Errors.Select(e => $"{e.Code} {e.Path}"));
    private static string[] Codes<T>(CommandResult<T> r) => r.Errors.Select(e => e.Code).ToArray();
    private static Dictionary<string, object?> D(params (string, object?)[] f) => f.ToDictionary(x => x.Item1, x => x.Item2);
    private static CommandContext Cfg(TestTenant t, string key) => t.Context(key, "write.config");

    private static async Task<Guid> Registration(TestTenant t, string key, params (string, object?)[] extra)
    {
        var fields = D(("company_id", t.CompanyId), ("country_code", "DK"), ("vat_id", "DK12345678"), ("scheme", "STANDARD"), ("filing_frequency", "QUARTERLY"),
            ("correction_method", "ADJUST_NEXT"), ("return_currency", "DKK"), ("valid_from", "2026-01-01"));
        foreach (var (k, v) in extra) fields[k] = v;
        var r = await new TaxCommands(t.App).CreateRegistrationAsync(t.TenantId, Cfg(t, key), new(fields));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        return r.Value!.EntityId;
    }

    private static async Task<Guid> Code(TestTenant t, Guid registration, string code, string type, string key)
    {
        var r = await new TaxCommands(t.App).CreateCodeAsync(t.TenantId, Cfg(t, key),
            new(D(("company_id", t.CompanyId), ("code", code), ("description", code + " code"), ("tax_type", type), ("vat_registration_id", registration))));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        return r.Value!.EntityId;
    }

    private static async Task Box(TestTenant t, string box, string key, string type = "STANDARD", string country = "DK")
    {
        var r = await new TaxCommands(t.App).CreateBoxAsync(t.TenantId, Cfg(t, key), new(country, type, box, D(("name", "Box " + box))));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
    }

    private static CreateTaxCodeVersionRequest Version(Guid code, string from, decimal rate, Guid account, IReadOnlyList<TaxBoxMapping>? boxes = null, params (string, object?)[] extra)
    {
        var fields = D(("valid_from", from), ("rate", rate), ("vat_account_id", account));
        foreach (var (k, v) in extra) fields[k] = v;
        return new(code, fields, boxes ?? []);
    }

    // ---------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task ATaxClassIsCreatedUpdatedReplayedAndDeactivated()
    {
        var t = await TestTenant.CreateReadyAsync();
        var commands = new TaxCommands(t.App);
        var created = await commands.CreateClassAsync(t.TenantId, Cfg(t, "c1"), new(D(("code", "GOODS"), ("name", "Goods"))));
        Assert.True(created.Status == CommandStatus.Ok, Describe(created));
        Guid id = created.Value!.EntityId;
        Assert.True((await commands.CreateClassAsync(t.TenantId, Cfg(t, "c1"), new(D(("code", "GOODS"), ("name", "Goods"))))).IdempotentReplay);
        Assert.Equal(["CONFIG.DUPLICATE_CODE"], Codes(await commands.CreateClassAsync(t.TenantId, Cfg(t, "c2"), new(D(("code", "GOODS"), ("name", "Again"))))));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await commands.CreateClassAsync(t.TenantId, t.Context("c3", "write.master"), new(D(("code", "X"), ("name", "x"))))));
        Assert.Equal(["SCHEMA.UNKNOWN_FIELD"], Codes(await commands.CreateClassAsync(t.TenantId, Cfg(t, "c4"), new(D(("code", "Y"), ("name", "y"), ("rate", 1))))));

        var update = await commands.UpdateClassAsync(t.TenantId, Cfg(t, "u1"), new(id, 1, D(("name", "Goods and food"))));
        Assert.True(update.Status == CommandStatus.Ok, Describe(update));
        Assert.Equal(["CONFIG.ROW_VERSION_MISMATCH"], Codes(await commands.UpdateClassAsync(t.TenantId, Cfg(t, "u2"), new(id, 1, D(("name", "x"))))));
        Assert.True((await commands.DeactivateClassAsync(t.TenantId, Cfg(t, "d1"), new(id, 2, "RETIRED"))).Status == CommandStatus.Ok);
        Assert.Equal(["SCHEMA.NO_EFFECTIVE_CHANGE"], Codes(await commands.DeactivateClassAsync(t.TenantId, Cfg(t, "d2"), new(id, 3, "RETIRED"))));
        Assert.True((await commands.ReactivateClassAsync(t.TenantId, Cfg(t, "r1"), new(id, 3, "BACK"))).Status == CommandStatus.Ok);
        Assert.Equal(4L, await t.ScalarAsync<long>("select count(*) from core.master_change_log where tenant_id = @t and entity_type = 'TAX_CLASS'"));
    }

    [Fact]
    public async Task ARegistrationNeedsItsCompanyCurrencyAndDates()
    {
        var t = await TestTenant.CreateReadyAsync();
        var commands = new TaxCommands(t.App);
        Guid reg = await Registration(t, "r1");
        Assert.Equal("QUARTERLY", await t.ScalarAsync<string>("select filing_frequency from core.company_vat_registration where tenant_id = @t and registration_id = @i", ("i", reg)));
        Assert.Equal("BOOKING", await t.ScalarAsync<string>("select vat_rate_type from core.company_vat_registration where tenant_id = @t and registration_id = @i", ("i", reg)));

        Assert.Equal(["CONFIG.DUPLICATE_CODE"], Codes(await commands.CreateRegistrationAsync(t.TenantId, Cfg(t, "r2"), new(D(("company_id", t.CompanyId), ("country_code", "DK"), ("vat_id", "DK1"),
            ("scheme", "STANDARD"), ("filing_frequency", "MONTHLY"), ("correction_method", "ADJUST_NEXT"), ("return_currency", "DKK"), ("valid_from", "2026-01-01"))))));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await commands.CreateRegistrationAsync(t.TenantId, Cfg(t, "r3"), new(D(("company_id", Guid.NewGuid()), ("country_code", "DK"), ("vat_id", "DK1"),
            ("scheme", "STANDARD"), ("filing_frequency", "MONTHLY"), ("correction_method", "ADJUST_NEXT"), ("return_currency", "DKK"), ("valid_from", "2027-01-01"))))));
        Assert.Equal(["CONFIG.CURRENCY_NOT_ENABLED"], Codes(await commands.CreateRegistrationAsync(t.TenantId, Cfg(t, "r4"), new(D(("company_id", t.CompanyId), ("country_code", "DK"), ("vat_id", "DK1"),
            ("scheme", "OSS_UNION"), ("filing_frequency", "MONTHLY"), ("correction_method", "ADJUST_NEXT"), ("return_currency", "NOK"), ("valid_from", "2027-01-01"))))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await commands.CreateRegistrationAsync(t.TenantId, Cfg(t, "r5"), new(D(("company_id", t.CompanyId), ("country_code", "DK"), ("vat_id", "DK1"),
            ("scheme", "OSS"), ("filing_frequency", "MONTHLY"), ("correction_method", "ADJUST_NEXT"), ("return_currency", "DKK"), ("valid_from", "2027-01-01"))))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await commands.CreateRegistrationAsync(t.TenantId, Cfg(t, "r6"), new(D(("company_id", t.CompanyId), ("country_code", "SE"), ("vat_id", "SE1"),
            ("scheme", "STANDARD"), ("filing_frequency", "MONTHLY"), ("correction_method", "ADJUST_NEXT"), ("return_currency", "DKK"), ("valid_from", "2027-02-01"), ("valid_to", "2027-01-01"))))));
        Assert.Equal(["SCHEMA.REQUIRED_FIELD_MISSING"], Codes(await commands.CreateRegistrationAsync(t.TenantId, Cfg(t, "r7"), new(D(("company_id", t.CompanyId), ("country_code", "SE"))))).Distinct().ToArray());

        var update = await commands.UpdateRegistrationAsync(t.TenantId, Cfg(t, "u1"), new(reg, 1, D(("filing_frequency", "MONTHLY"), ("ec_list_frequency", "QUARTERLY"))));
        Assert.True(update.Status == CommandStatus.Ok, Describe(update));
    }

    [Fact]
    public async Task ACodeNeedsAnActiveRegistrationOfItsCompanyAndItsTypeIsFixedOnceItHasAVersion()
    {
        var t = await TestTenant.CreateReadyAsync();
        var commands = new TaxCommands(t.App);
        Guid reg = await Registration(t, "r1");
        Guid code = await Code(t, reg, "U25", "OUTPUT", "c1");

        Assert.Equal(["CONFIG.DUPLICATE_CODE"], Codes(await commands.CreateCodeAsync(t.TenantId, Cfg(t, "c2"), new(D(("company_id", t.CompanyId), ("code", "U25"), ("description", "x"), ("tax_type", "OUTPUT"), ("vat_registration_id", reg))))));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await commands.CreateCodeAsync(t.TenantId, Cfg(t, "c3"), new(D(("company_id", t.CompanyId), ("code", "X"), ("description", "x"), ("tax_type", "OUTPUT"), ("vat_registration_id", Guid.NewGuid()))))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await commands.CreateCodeAsync(t.TenantId, Cfg(t, "c4"), new(D(("company_id", t.CompanyId), ("code", "X"), ("description", "x"), ("tax_type", "SALES"), ("vat_registration_id", reg))))));

        // the type changes while there is no version
        var retype = await commands.UpdateCodeAsync(t.TenantId, Cfg(t, "u1"), new(code, 1, D(("tax_type", "INPUT"))));
        Assert.True(retype.Status == CommandStatus.Ok, Describe(retype));
        await Box(t, "A", "b1");
        var version = await commands.CreateVersionAsync(t.TenantId, Cfg(t, "v1"), Version(code, "2026-01-01", 25m, t.Bank));
        Assert.True(version.Status == CommandStatus.Ok, Describe(version));
        Assert.Equal(["CONFIG.IMMUTABLE_AFTER_POSTING"], Codes(await commands.UpdateCodeAsync(t.TenantId, Cfg(t, "u2"), new(code, 2, D(("tax_type", "OUTPUT"))))));
        Assert.True((await commands.UpdateCodeAsync(t.TenantId, Cfg(t, "u3"), new(code, 2, D(("description", "Input VAT"))))).Status == CommandStatus.Ok);

        // the registration of a code that is active cannot be deactivated, an IAP field of it cannot change
        Assert.Equal(["CONFIG.REFERENCED"], Codes(await commands.DeactivateRegistrationAsync(t.TenantId, Cfg(t, "d1"), new(reg, 1, "CLOSED"))));
        Assert.Equal(["CONFIG.IMMUTABLE_AFTER_POSTING"], Codes(await commands.UpdateRegistrationAsync(t.TenantId, Cfg(t, "u4"), new(reg, 1, D(("scheme", "OSS_UNION"))))));
        Assert.True((await commands.DeactivateCodeAsync(t.TenantId, Cfg(t, "d2"), new(code, 3, "RETIRED"))).Status == CommandStatus.Ok);
        Assert.True((await commands.DeactivateRegistrationAsync(t.TenantId, Cfg(t, "d3"), new(reg, 1, "CLOSED"))).Status == CommandStatus.Ok);
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await commands.CreateCodeAsync(t.TenantId, Cfg(t, "c5"), new(D(("company_id", t.CompanyId), ("code", "N"), ("description", "x"), ("tax_type", "OUTPUT"), ("vat_registration_id", reg))))));
    }

    [Fact]
    public async Task AVersionHoldsItsBoxesMarksItsAccountsAndStartsAfterTheLatest()
    {
        var t = await TestTenant.CreateReadyAsync();
        var commands = new TaxCommands(t.App);
        Guid reg = await Registration(t, "r1");
        Guid output = await Code(t, reg, "U25", "OUTPUT", "c1");
        await Box(t, "A", "b1"); await Box(t, "B", "b2");

        var first = await commands.CreateVersionAsync(t.TenantId, Cfg(t, "v1"), Version(output, "2026-01-01", 25m, t.Bank,
            [new("PRIMARY", "STANDARD", "A", "BASE", 1), new("PRIMARY", "STANDARD", "B", "TAX", 1)]));
        Assert.True(first.Status == CommandStatus.Ok, Describe(first));
        Assert.Equal(2, first.Value!.BoxCount);
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.gl_account where tenant_id = @t and account_id = @a and tax_account", ("a", t.Bank)));
        Assert.Equal(2L, await t.ScalarAsync<long>("select count(*) from core.tax_code_version_box where tenant_id = @t and tax_code_id = @c", ("c", output)));
        Assert.Equal("100.0000000000", await t.ScalarAsync<string>("select deductible_pct::text from core.tax_code_version where tenant_id = @t and tax_code_version_id = @v", ("v", first.Value.TaxCodeVersionId)));

        var replay = await commands.CreateVersionAsync(t.TenantId, Cfg(t, "v1"), Version(output, "2026-01-01", 25m, t.Bank, [new("PRIMARY", "STANDARD", "A", "BASE", 1), new("PRIMARY", "STANDARD", "B", "TAX", 1)]));
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(first.Value.TaxCodeVersionId, replay.Value!.TaxCodeVersionId);
        Assert.Equal(["IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST"], Codes(await commands.CreateVersionAsync(t.TenantId, Cfg(t, "v1"), Version(output, "2026-01-01", 20m, t.Bank))));

        Assert.Equal(["CONFIG.VALID_FROM_NOT_LATER"], Codes(await commands.CreateVersionAsync(t.TenantId, Cfg(t, "v2"), Version(output, "2026-01-01", 24m, t.Bank))));
        var later = await commands.CreateVersionAsync(t.TenantId, Cfg(t, "v3"), Version(output, "2026-07-01", 24m, t.Bank, [new("PRIMARY", "STANDARD", "A", "BASE", 1)]));
        Assert.True(later.Status == CommandStatus.Ok, Describe(later));
        var end = await commands.CreateVersionAsync(t.TenantId, Cfg(t, "v4"), Version(output, "2027-01-01", 24m, t.Bank, null, ("valid_to", "2027-06-30")));
        Assert.True(end.Status == CommandStatus.Ok, Describe(end));   // the end-version of 07, 2.1 (F7)
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await commands.CreateVersionAsync(t.TenantId, Cfg(t, "v5"), Version(output, "2028-01-01", 24m, t.Bank, null, ("valid_to", "2027-01-01")))));

        // what a version may not hold
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await commands.CreateVersionAsync(t.TenantId, Cfg(t, "v6"), Version(output, "2029-01-01", 25m, t.Revenue))));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await commands.CreateVersionAsync(t.TenantId, Cfg(t, "v7"), Version(output, "2029-01-01", 25m, Guid.NewGuid()))));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await commands.CreateVersionAsync(t.TenantId, Cfg(t, "v8"), Version(output, "2029-01-01", 25m, t.Bank, [new("PRIMARY", "STANDARD", "ZZ", "BASE", 1)]))));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await commands.CreateVersionAsync(t.TenantId, Cfg(t, "v9"), Version(output, "2029-01-01", 25m, t.Bank, [new("COUNTER", "STANDARD", "A", "BASE", 1)]))));
        Assert.Equal(["CONFIG.DEDUCTION_ON_NON_INPUT_CODE"], Codes(await commands.CreateVersionAsync(t.TenantId, Cfg(t, "v10"), Version(output, "2029-01-01", 25m, t.Bank, null, ("deductible_pct", 50m)))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await commands.CreateVersionAsync(t.TenantId, Cfg(t, "v11"), Version(output, "2029-01-01", 25m, t.Bank, [new("PRIMARY", "STANDARD", "A", "BASE", 2)]))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await commands.CreateVersionAsync(t.TenantId, Cfg(t, "v12"), Version(output, "2029-01-01", 101m, t.Bank))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await commands.CreateVersionAsync(t.TenantId, Cfg(t, "v13"), Version(output, "2029-01-01", 25m, t.Bank,
            [new("PRIMARY", "STANDARD", "A", "BASE", 1), new("PRIMARY", "STANDARD", "A", "TAX", 1)]))));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await commands.CreateVersionAsync(t.TenantId, t.Context("v14", "write.master"), Version(output, "2029-01-01", 25m, t.Bank))));
    }

    [Fact]
    public async Task ZeroRatedExemptReverseChargeAndInputCodesFollowTheirRules()
    {
        var t = await TestTenant.CreateReadyAsync();
        var commands = new TaxCommands(t.App);
        Guid reg = await Registration(t, "r1");
        Guid zero = await Code(t, reg, "Z0", "ZERO_RATED", "c1"), input = await Code(t, reg, "I25", "INPUT", "c2"), output = await Code(t, reg, "U25", "OUTPUT", "c3");
        await Box(t, "A", "b1"); await Box(t, "B", "b2");

        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await commands.CreateVersionAsync(t.TenantId, Cfg(t, "z1"), Version(zero, "2026-01-01", 5m, t.Bank, null, ("en16931_vat_category", "Z")))));
        Assert.Equal(["SCHEMA.REQUIRED_FIELD_MISSING"], Codes(await commands.CreateVersionAsync(t.TenantId, Cfg(t, "z2"), Version(zero, "2026-01-01", 0m, t.Bank))));
        Assert.True((await commands.CreateVersionAsync(t.TenantId, Cfg(t, "z3"), Version(zero, "2026-01-01", 0m, t.Bank, null, ("en16931_vat_category", "Z"), ("ec_list_category", "GOODS")))).Status == CommandStatus.Ok);

        // reverse charge: an input code, with its counter account and a category, and its counter boxes
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await commands.CreateVersionAsync(t.TenantId, Cfg(t, "r2"), Version(output, "2026-01-01", 25m, t.Bank, null,
            ("reverse_charge", true), ("reverse_charge_account_id", t.Bank), ("en16931_vat_category", "AE")))));
        Assert.Equal(["SCHEMA.REQUIRED_FIELD_MISSING"], Codes(await commands.CreateVersionAsync(t.TenantId, Cfg(t, "r3"), Version(input, "2026-01-01", 25m, t.Bank, null,
            ("reverse_charge", true), ("en16931_vat_category", "AE")))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await commands.CreateVersionAsync(t.TenantId, Cfg(t, "r4"), Version(input, "2026-01-01", 25m, t.Bank, null, ("reverse_charge_account_id", t.Bank)))));
        Assert.Equal(["SCHEMA.REQUIRED_FIELD_MISSING"], Codes(await commands.CreateVersionAsync(t.TenantId, Cfg(t, "r5"), Version(input, "2026-01-01", 25m, t.Bank, null,
            ("reverse_charge", true), ("reverse_charge_account_id", t.Bank)))));
        var rc = await commands.CreateVersionAsync(t.TenantId, Cfg(t, "r6"), Version(input, "2026-01-01", 25m, t.Bank,
            [new("PRIMARY", "STANDARD", "A", "BASE", 1), new("COUNTER", "STANDARD", "B", "TAX", -1)], ("reverse_charge", true), ("reverse_charge_account_id", t.Bank), ("en16931_vat_category", "AE")));
        Assert.True(rc.Status == CommandStatus.Ok, Describe(rc));

        // partial deduction: the share is stored, the key is not built
        var partial = await commands.CreateVersionAsync(t.TenantId, Cfg(t, "p1"), Version(input, "2027-01-01", 25m, t.Bank, null, ("deductible_pct", 25.5m), ("deduction_cap", "SUPPLIED_AMOUNT")));
        Assert.True(partial.Status == CommandStatus.Ok, Describe(partial));
        Assert.Equal("25.5000000000", await t.ScalarAsync<string>("select deductible_pct::text from core.tax_code_version where tenant_id = @t and tax_code_version_id = @v", ("v", partial.Value!.TaxCodeVersionId)));
        Assert.Equal(["CONFIG.DEDUCTION_KEY_UNKNOWN"], Codes(await commands.CreateVersionAsync(t.TenantId, Cfg(t, "p2"), Version(input, "2028-01-01", 25m, t.Bank, null, ("deduction_key", "PRORATA")))));
        Assert.Equal(["CONFIG.DEDUCTION_ON_NON_INPUT_CODE"], Codes(await commands.CreateVersionAsync(t.TenantId, Cfg(t, "p3"), Version(output, "2028-01-01", 25m, t.Bank, null, ("deduction_cap", "SUPPLIED_AMOUNT")))));
    }

    [Fact]
    public async Task AReturnBoxIsKeyedByCountryTypeAndCodeAndStaysWhileAVersionMapsToIt()
    {
        var t = await TestTenant.CreateReadyAsync();
        var commands = new TaxCommands(t.App);
        var created = await commands.CreateBoxAsync(t.TenantId, Cfg(t, "b1"), new("DK", "STANDARD", "A", D(("name", "Box A"), ("box_group", "SALES"))));
        Assert.True(created.Status == CommandStatus.Ok, Describe(created));
        Assert.True((await commands.CreateBoxAsync(t.TenantId, Cfg(t, "b1"), new("DK", "STANDARD", "A", D(("name", "Box A"), ("box_group", "SALES"))))).IdempotentReplay);
        Assert.Equal(["CONFIG.DUPLICATE_CODE"], Codes(await commands.CreateBoxAsync(t.TenantId, Cfg(t, "b2"), new("DK", "STANDARD", "A", D(("name", "Again"))))));
        Assert.True((await commands.CreateBoxAsync(t.TenantId, Cfg(t, "b3"), new("DK", "EC_LIST", "A", D(("name", "EC A"))))).Status == CommandStatus.Ok);   // another return type, another box
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await commands.CreateBoxAsync(t.TenantId, Cfg(t, "b4"), new("dk", "STANDARD", "A", D(("name", "x"))))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await commands.CreateBoxAsync(t.TenantId, Cfg(t, "b5"), new("DK", "SALES", "A", D(("name", "x"))))));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await commands.CreateBoxAsync(t.TenantId, Cfg(t, "b6"), new("ZZ", "STANDARD", "A", D(("name", "x"))))));

        var update = await commands.UpdateBoxAsync(t.TenantId, Cfg(t, "u1"), new("DK", "STANDARD", "A", 1, D(("name", "Box A, sales"))));
        Assert.True(update.Status == CommandStatus.Ok, Describe(update));
        Assert.Equal(["CONFIG.ROW_VERSION_MISMATCH"], Codes(await commands.UpdateBoxAsync(t.TenantId, Cfg(t, "u2"), new("DK", "STANDARD", "A", 1, D(("name", "x"))))));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await commands.UpdateBoxAsync(t.TenantId, Cfg(t, "u3"), new("DK", "STANDARD", "Q", 1, D(("name", "x"))))));

        Guid reg = await Registration(t, "r1");
        Guid code = await Code(t, reg, "U25", "OUTPUT", "c1");
        Assert.True((await commands.CreateVersionAsync(t.TenantId, Cfg(t, "v1"), Version(code, "2026-01-01", 25m, t.Bank, [new("PRIMARY", "STANDARD", "A", "BASE", 1)]))).Status == CommandStatus.Ok);
        Assert.Equal(["CONFIG.REFERENCED"], Codes(await commands.DeactivateBoxAsync(t.TenantId, Cfg(t, "d1"), new("DK", "STANDARD", "A", 2, "RETIRED"))));
        Assert.True((await commands.CreateBoxAsync(t.TenantId, Cfg(t, "b7"), new("DK", "STANDARD", "C", D(("name", "Box C"))))).Status == CommandStatus.Ok);
        Assert.True((await commands.DeactivateBoxAsync(t.TenantId, Cfg(t, "d2"), new("DK", "STANDARD", "C", 1, "RETIRED"))).Status == CommandStatus.Ok);
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await commands.CreateVersionAsync(t.TenantId, Cfg(t, "v2"), Version(code, "2027-01-01", 25m, t.Bank, [new("PRIMARY", "STANDARD", "C", "BASE", 1)]))));   // a retired box takes no new mapping
        Assert.True((await commands.ReactivateBoxAsync(t.TenantId, Cfg(t, "r2"), new("DK", "STANDARD", "C", 2, "BACK"))).Status == CommandStatus.Ok);
    }
}
