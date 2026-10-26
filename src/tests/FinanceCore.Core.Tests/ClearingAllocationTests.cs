using FinanceCore.Core.Posting;

namespace FinanceCore.Core.Tests;

/// <summary>The allocation of one clearing item (03, 5.2) and the status of an open item (03, 4.3).</summary>
public class ClearingAllocationTests
{
    // 1000.00 DKK, 134.00 EUR in group and reporting currency
    private static OpenLine Line(decimal clearedTc = 0, decimal clearedLc = 0, decimal clearedGc = 0, decimal? clearedRc = 0) =>
        new(1000m, 1000m, 134m, 134m, clearedTc, clearedLc, clearedGc, clearedRc);

    private static Allocation? Allocate(OpenLine l, decimal? tc, out string? error) => ClearingRules.Allocate(l, tc, 2, 2, 2, out error);

    [Fact]
    public void NoAmountMeansTheFullRemainingAmount()
    {
        var a = Allocate(Line(400m, 400m, 53.6m, 53.6m), null, out string? error);
        Assert.Null(error);
        Assert.Equal(new Allocation(600m, 600m, 80.4m, 80.4m), a);
    }

    [Fact]
    public void ThePartialAmountIsRoundedAtTheScaleOfEachCurrency()
    {
        var a = Allocate(Line(), 333.33m, out _);
        Assert.Equal(new Allocation(333.33m, 333.33m, 44.67m, 44.67m), a);   // 134.00 x 333.33 / 1000 = 44.66622
    }

    [Fact]
    public void TheLastPartialTakesTheRemainingCarriedAmountsSoThePartialsAddUp()
    {
        var first = Allocate(Line(), 333.33m, out _)!;
        var rest = Allocate(Line(first.Tc, first.Lc, first.Gc, first.Rc), 666.67m, out _)!;
        Assert.Equal(1000m, first.Tc + rest.Tc);
        Assert.Equal(134m, first.Gc + rest.Gc);
        Assert.Equal(89.33m, rest.Gc);   // 134.00 - 44.67, not ROUND(134.00 x 666.67 / 1000) = 89.33378
    }

    [Fact]
    public void ARoundedPartialNeverTakesMoreThanRemains()
    {
        // 0.67 of 1.00 EUR-equivalent rounds up to the whole remaining amount after an earlier round-up
        var l = new OpenLine(3m, 3m, 1m, null, 2m, 2m, 0.67m, null);
        var a = Allocate(l, 0.5m, out _)!;
        Assert.True(Math.Abs(a.Gc) <= Math.Abs(l.Gc - l.ClearedGc));
    }

    [Theory]
    [InlineData(1000.01, ClearingRules.OverClearing)]    // more than remains
    [InlineData(-1, ClearingRules.OverClearing)]         // the other sign
    [InlineData(0, ClearingRules.ZeroAmount)]            // nothing
    public void AnAmountThatIsNotAllowedIsRejected(double tc, string code)
    {
        Assert.Null(Allocate(Line(), (decimal)tc, out string? error));
        Assert.Equal(code, error);
    }

    [Fact]
    public void ANegativeLineTakesNegativeAmounts()
    {
        var l = new OpenLine(-1000m, -1000m, -134m, -134m, 0, 0, 0, 0);
        Assert.Equal(new Allocation(-250m, -250m, -33.5m, -33.5m), Allocate(l, -250m, out _));
        Assert.Null(Allocate(l, 250m, out string? error));
        Assert.Equal(ClearingRules.OverClearing, error);
    }

    [Fact]
    public void ALineWithoutReportingAmountClearsWithoutOne()
    {
        var l = new OpenLine(100m, 100m, 13.4m, null, 0, 0, 0, null);
        Assert.Null(Allocate(l, 40m, out _)!.Rc);
    }

    [Fact]
    public void AZeroTcLineIsClearedAsAWhole()
    {
        var offset = new OpenLine(0m, 2m, 0.27m, 0.27m, 0, 0, 0, 0);   // the offset line of a difference document (03, 3.1)
        Assert.Equal(new Allocation(0m, 2m, 0.27m, 0.27m), Allocate(offset, null, out _));
        Assert.Equal(new Allocation(0m, 2m, 0.27m, 0.27m), Allocate(offset, 0m, out _));
        Assert.Null(Allocate(offset, 1m, out string? error));
        Assert.Equal(ClearingRules.OverClearing, error);
    }

    [Theory]
    [InlineData(0, 0, 0, 0, "OPEN")]
    [InlineData(400, 400, 53.6, 53.6, "PARTIAL")]
    [InlineData(1000, 1000, 134, 134, "CLEARED")]
    [InlineData(1000, 1000, 133.99, 134, "PARTIAL")]   // every currency of the line must be cleared
    public void TheStatusFollowsTheClearedAmounts(double tc, double lc, double gc, double rc, string expected) =>
        Assert.Equal(expected, ClearingRules.StatusOf(Line(), (decimal)tc, (decimal)lc, (decimal)gc, (decimal)rc));

    [Fact]
    public void AZeroTcOffsetLineIsClearedWhenAllItsAmountsAre()
    {
        var offset = new OpenLine(0m, 2m, 0.27m, 0.27m, 0, 0, 0, 0);
        Assert.Equal("CLEARED", ClearingRules.StatusOf(offset, 0m, 2m, 0.27m, 0.27m));
        Assert.Equal("OPEN", ClearingRules.StatusOf(offset, 0m, 0m, 0m, 0m));
    }

    [Fact]
    public void AnOffsetLineThatIsOnlyInGroupCurrencyCanBeCleared()
    {
        // TC 0 and LC 0: the difference of the set is in GC and RC only (F-IMPL-20)
        var offset = new OpenLine(0m, 0m, -3.8m, -3.8m, 0, 0, 0, 0);
        Assert.Equal("CLEARED", ClearingRules.StatusOf(offset, 0m, 0m, -3.8m, -3.8m));
        Assert.Equal("OPEN", ClearingRules.StatusOf(offset, 0m, 0m, 0m, 0m));
        Assert.Equal(new Allocation(0m, 0m, -3.8m, -3.8m), ClearingRules.Allocate(offset, null, 2, 2, 2, out _));
    }

    [Fact]
    public void TheFingerprintCoversTheItemsAndTheirOrder()
    {
        Guid c = Guid.NewGuid(), acc = Guid.NewGuid(), d1 = Guid.NewGuid(), d2 = Guid.NewGuid();
        PostClearingRequest Make(params ClearingItemRequest[] items) =>
            new(c, acc, null, new DateOnly(2026, 3, 15), "MANUAL", null, null, items);
        var a = new ClearingItemRequest(d1, 1); var b = new ClearingItemRequest(d2, 1, 5m);
        Assert.Equal(Convert.ToHexString(ClearingRules.Fingerprint(Make(a, b))), Convert.ToHexString(ClearingRules.Fingerprint(Make(a, b))));
        Assert.NotEqual(Convert.ToHexString(ClearingRules.Fingerprint(Make(a, b))), Convert.ToHexString(ClearingRules.Fingerprint(Make(b, a))));
        Assert.NotEqual(Convert.ToHexString(ClearingRules.Fingerprint(Make(a, b))), Convert.ToHexString(ClearingRules.Fingerprint(Make(a, b with { ClearedAmountTc = 6m }))));
    }
}
