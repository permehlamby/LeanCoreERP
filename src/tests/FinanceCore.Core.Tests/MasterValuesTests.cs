using FinanceCore.Core.Masters;

namespace FinanceCore.Core.Tests;

/// <summary>The checking and the canonical text of the fields that master commands take (07, 9 and 11.1).</summary>
public class MasterValuesTests
{
    private static MasterField F(string name) =>
        MasterFields.Partner.Concat(MasterFields.Pii).Concat(MasterFields.Relation).Concat(MasterFields.PaymentTerms).First(f => f.Name == name);

    [Fact]
    public void ACanonicalTextIsStableForEveryKind()
    {
        Assert.Equal("true", MasterValues.Parse(F("is_customer"), true, out _)!.Text);
        Assert.Equal("1200.5000", MasterValues.Parse(F("tenant_credit_limit"), 1200.5m, out _)!.Text);
        Assert.Equal("1200.0000", MasterValues.Parse(F("tenant_credit_limit"), 1200, out _)!.Text);
        Assert.Equal("2.5000000000", MasterValues.Parse(F("discount_pct"), 2.5m, out _)!.Text);
        Assert.Equal("30", MasterValues.Parse(F("net_days"), 30, out _)!.Text);
        Guid id = Guid.NewGuid();
        Assert.Equal(id.ToString("D"), MasterValues.Parse(F("ic_company_id"), id, out _)!.Text);
        Assert.Null(MasterValues.Parse(F("trade_name"), null, out _)!.Text);
    }

    [Theory]
    [InlineData("partner_code", null)]                   // cannot be cleared
    [InlineData("partner_code", "")]
    [InlineData("partner_kind", "COMPANY")]
    [InlineData("is_customer", "yes")]
    [InlineData("tenant_credit_limit", -1)]
    [InlineData("country_code", "dk")]
    [InlineData("tenant_credit_limit_currency", "eur")]
    [InlineData("net_days", -1)]
    [InlineData("discount_pct", 0)]
    [InlineData("discount_pct", 101)]
    [InlineData("baseline_rule", "TOMORROW")]
    public void AValueThatDoesNotFitItsFieldIsRejectedWithAReason(string field, object? value)
    {
        object? v = value is int i && field is "tenant_credit_limit" or "discount_pct" ? (decimal)(int)value! : value;
        Assert.Null(MasterValues.Parse(F(field), v, out string? reason));
        Assert.False(string.IsNullOrEmpty(reason));
    }

    [Fact]
    public void ATextLongerThanItsColumnIsRejected()
    {
        Assert.Null(MasterValues.Parse(F("postal_code"), new string('9', 21), out _));
        Assert.NotNull(MasterValues.Parse(F("postal_code"), new string('9', 20), out _));
        Assert.Null(MasterValues.Parse(F("tenant_credit_limit"), 0.00001m, out string? reason));   // more fraction digits than the column has
        Assert.Contains("fraction", reason);
    }

    [Fact]
    public void TheRequestFingerprintCoversEveryFieldAndKeepsNullAndAbsentApart()
    {
        var a = new Dictionary<string, object?> { ["email"] = null };
        var b = new Dictionary<string, object?>();
        Assert.NotEqual(MasterRules.Text(a), MasterRules.Text(b));
        Assert.Equal(MasterRules.Text(new Dictionary<string, object?> { ["a"] = 1m, ["b"] = "x" }), MasterRules.Text(new Dictionary<string, object?> { ["b"] = "x", ["a"] = 1m }));
        Guid id = Guid.NewGuid();
        var statusA = new MasterStatusRequest(id, 1, "CREDIT");
        Assert.NotEqual(Convert.ToHexString(MasterRules.Fingerprint("business-partners:deactivate", statusA)), Convert.ToHexString(MasterRules.Fingerprint("business-partners:reactivate", statusA)));
        Assert.NotEqual(Convert.ToHexString(MasterRules.Fingerprint("x", statusA)), Convert.ToHexString(MasterRules.Fingerprint("x", statusA with { ExpectedVersion = 2 })));
    }

    [Fact]
    public void ThePartnerRegistryHasNoSColumnAndNoStatus() =>
        Assert.DoesNotContain(MasterFields.Partner, f => f.Name is "status" or "retention_until" or "retention_floor" or "restriction_status" or "anonymised_at");
}
