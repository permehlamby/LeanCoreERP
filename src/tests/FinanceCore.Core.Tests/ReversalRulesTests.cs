using FinanceCore.Core.Posting;

namespace FinanceCore.Core.Tests;

public class ReversalRulesTests
{
    [Theory]
    [InlineData("CLEARING", "clearings:reset")]
    [InlineData("REVALUATION", "revaluation:run")]
    [InlineData("REVAL_REVERSAL", "revaluation:run")]
    [InlineData("STOCK_RECEIPT", "stock-documents:reverse")]
    [InlineData("STOCK_TRANSFER", "stock-documents:reverse")]
    [InlineData("DROPSHIP_COST", "dropship-confirmations:reverse")]
    [InlineData("VAT_SETTLE", "vat-returns:reverse-settlement")]
    [InlineData("YEAR_END", "year-end:reopen")]
    [InlineData("GR_IR_WRITE_OFF", "gr-ir:reverse-write-off")]
    [InlineData("CLOSING", "migration-batches:rollback")]
    [InlineData("OPENING_ITEMS", "migration-batches:rollback")]
    [InlineData("REVERSAL", null)]
    public void OwningCommandOfADerivedType(string type, string? expected) =>
        Assert.Equal(expected, ReversalRules.OwningCommand(type));

    [Fact]
    public void OnlyTheExternalTypesAreReversibleHere() =>
        Assert.Equal(["AP_CREDIT", "AP_INVOICE", "AR_CREDIT", "AR_INVOICE", "JOURNAL", "PAYMENT"], ReversalRules.ReversibleTypes.Order());

    [Fact]
    public void TheSixReasonsAreSplitBetweenCallersAndSystemCommands()
    {
        Assert.Equal(["CORRECTION", "ERROR"], ReversalRules.ExternalReasons);
        Assert.Equal(6, ReversalRules.ExternalReasons.Length + ReversalRules.SystemReasons.Length);
    }

    [Fact]
    public void FingerprintDependsOnEveryField()
    {
        var id = Guid.NewGuid();
        byte[] f(ReverseDocumentRequest r) => ReversalRules.Fingerprint(r);
        var baseline = f(new(id, null, "ERROR", null));
        Assert.Equal(baseline, f(new(id, null, "ERROR", null)));
        Assert.NotEqual(baseline, f(new(Guid.NewGuid(), null, "ERROR", null)));
        Assert.NotEqual(baseline, f(new(id, new DateOnly(2026, 1, 1), "ERROR", null)));
        Assert.NotEqual(baseline, f(new(id, null, "CORRECTION", null)));
        Assert.NotEqual(baseline, f(new(id, null, "ERROR", "text")));
    }
}
