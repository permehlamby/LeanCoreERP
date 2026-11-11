using FinanceCore.Core.Roles;

namespace FinanceCore.Core.Tests;

public class AccountRoleRulesTests
{
    private static RoleAccount Account(string type, string openItem = "NONE", string? currency = null, bool revalue = false, bool tax = false) =>
        new(type, openItem, currency, revalue, tax);

    private static string[] Check(string role, string? currency, RoleAccount account, params string[] others) =>
        AccountRoles.CheckAccount(AccountRoles.Find(role)!, currency, account, others).Select(v => v.Rule ?? v.Code).ToArray();

    [Fact]
    public void TheListIsTheRolesOfTheAssignmentTable() =>
        Assert.Equal(
            ["FX_REALISED", "FX_UNREALISED_GAIN", "FX_UNREALISED_LOSS", "HISTORY_RESULT", "IC_PAYABLE", "IC_RECEIVABLE",
             "MIGRATION_CLEARING", "RETAINED_EARNINGS", "ROUNDING", "TAX_ROUNDING", "VAT_SETTLEMENT"],
            AccountRoles.Definitions.Select(d => d.Code).Order());

    [Theory]
    [InlineData("ROUNDING", "EXPENSE", true)]
    [InlineData("ROUNDING", "REVENUE", true)]
    [InlineData("ROUNDING", "ASSET", false)]
    [InlineData("FX_REALISED", "LIABILITY", false)]
    [InlineData("RETAINED_EARNINGS", "EQUITY", true)]
    [InlineData("RETAINED_EARNINGS", "LIABILITY", false)]
    [InlineData("IC_RECEIVABLE", "ASSET", true)]
    [InlineData("IC_PAYABLE", "EQUITY", true)]
    [InlineData("IC_PAYABLE", "EXPENSE", false)]
    [InlineData("HISTORY_RESULT", "EQUITY", true)]
    [InlineData("MIGRATION_CLEARING", "REVENUE", false)]
    public void AccountTypeRule(string role, string type, bool allowed) =>
        Assert.Equal(allowed, !Check(role, null, Account(type)).Contains("ACCOUNT_TYPE"));

    [Theory]
    [InlineData("CONTROL")]
    [InlineData("GL_OPEN_ITEM")]
    public void EveryRoleRequiresOpenItemModeNone(string mode) =>
        Assert.All(AccountRoles.Definitions, d =>
            Assert.Contains("OPEN_ITEM_MODE", AccountRoles.CheckAccount(d, null, Account("EXPENSE", mode), []).Select(v => v.Rule)));

    [Fact]
    public void OneAccountHasOneRoleButMayRepeatIt()
    {
        Assert.Empty(Check("ROUNDING", null, Account("EXPENSE"), "ROUNDING", "ROUNDING"));
        Assert.Equal(["ONE_ROLE_PER_ACCOUNT"], Check("ROUNDING", null, Account("EXPENSE"), "FX_REALISED"));
    }

    [Fact]
    public void CurrencyRule()
    {
        Assert.Empty(Check("ROUNDING", "DKK", Account("EXPENSE")));                      // account without currency serves any row
        Assert.Empty(Check("ROUNDING", "DKK", Account("EXPENSE", currency: "DKK")));
        Assert.Equal(["CURRENCY"], Check("ROUNDING", "USD", Account("EXPENSE", currency: "DKK")));
        Assert.Equal(["CURRENCY"], Check("ROUNDING", null, Account("EXPENSE", currency: "DKK")));   // a default row needs a free account
    }

    [Fact]
    public void TaxAccountAcceptsOnlyVatSettlement()
    {
        Assert.Equal(["TAX_ACCOUNT"], Check("ROUNDING", null, Account("EXPENSE", tax: true)));
        Assert.Empty(Check("VAT_SETTLEMENT", null, Account("LIABILITY", tax: true)));
    }

    [Fact]
    public void RetainedEarningsHasNoRevalueFlag()
    {
        Assert.Equal([AccountRoles.RevalueOnRetainedEarnings], Check("RETAINED_EARNINGS", null, Account("EQUITY", revalue: true)));
        Assert.Empty(Check("IC_RECEIVABLE", null, Account("ASSET", revalue: true)));
    }

    [Fact]
    public void ShapeFollowsTheScopeOfTheRole()
    {
        Guid c = Guid.NewGuid(), a = Guid.NewGuid(), other = Guid.NewGuid();
        string[] Paths(AssignAccountRoleRequest r) => AccountRoles.CheckShape(r).Select(e => e.Path).ToArray();

        Assert.Empty(Paths(new(c, "ROUNDING", a)));
        Assert.Empty(Paths(new(c, "ROUNDING", a, "DKK")));
        Assert.Empty(Paths(new(c, "IC_PAYABLE", a, CounterpartCompanyId: other)));
        Assert.Empty(Paths(new(c, "VAT_SETTLEMENT", a, VatRegistrationId: other)));
        Assert.Equal(["role_code"], Paths(new(c, "SOMETHING", a)));
        Assert.Equal(["currency_code"], Paths(new(c, "FX_REALISED", a, "DKK")));
        Assert.Equal(["currency_code"], Paths(new(c, "ROUNDING", a, "dk")));
        Assert.Equal(["counterpart_company_id"], Paths(new(c, "IC_PAYABLE", a)));
        Assert.Equal(["counterpart_company_id"], Paths(new(c, "ROUNDING", a, CounterpartCompanyId: other)));
        Assert.Equal(["counterpart_company_id"], Paths(new(c, "IC_PAYABLE", a, CounterpartCompanyId: c)));
        Assert.Equal(["vat_registration_id"], Paths(new(c, "ROUNDING", a, VatRegistrationId: other)));
        Assert.Equal(["company_id", "account_id"], Paths(new(Guid.Empty, "ROUNDING", Guid.Empty)));
    }

    [Fact]
    public void ActivationNeedsSixRoles() =>
        Assert.Equal(["ROUNDING", "TAX_ROUNDING", "FX_REALISED", "FX_UNREALISED_GAIN", "FX_UNREALISED_LOSS", "RETAINED_EARNINGS"],
            AccountRoles.RequiredForActivation);

    [Fact]
    public void FingerprintDependsOnEveryField()
    {
        Guid c = Guid.NewGuid(), a = Guid.NewGuid(), x = Guid.NewGuid();
        var at = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var baseline = new AssignAccountRoleRequest(c, "IC_PAYABLE", a, null, x, null, at);
        byte[] f(AssignAccountRoleRequest r) => AccountRoles.Fingerprint(r);

        Assert.Equal(f(baseline), f(baseline with { }));
        foreach (var changed in new[]
        {
            baseline with { CompanyId = Guid.NewGuid() }, baseline with { RoleCode = "IC_RECEIVABLE" },
            baseline with { AccountId = Guid.NewGuid() }, baseline with { CurrencyCode = "DKK" },
            baseline with { CounterpartCompanyId = Guid.NewGuid() }, baseline with { VatRegistrationId = Guid.NewGuid() },
            baseline with { EffectiveFrom = at.AddSeconds(1) }, baseline with { EffectiveFrom = null }
        })
            Assert.NotEqual(f(baseline), f(changed));
    }
}
