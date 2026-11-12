using System.Security.Cryptography;
using FinanceCore.Core.Closing;
using FinanceCore.Core.Hashing;

namespace FinanceCore.Core.Tests;

public class ClosingModelTests
{
    private static readonly Guid Bank = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Other = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly RevaluationTargets Dkk = new("DKK", 2, "EUR", 2, "EUR", 2);

    private static IReadOnlyDictionary<(string, string), RevaluationRateResult> Rates(decimal toDkk, decimal toEur) => new Dictionary<(string, string), RevaluationRateResult>
    {
        [("USD", "DKK")] = new("USD", "DKK", toDkk, new DateOnly(2026, 3, 31)), [("USD", "EUR")] = new("USD", "EUR", toEur, new DateOnly(2026, 3, 31))
    };

    private static RevaluationUnit Unit(Guid account, decimal tc, decimal lc, decimal gc, string ccy = "USD", Guid? partner = null) =>
        new("ACCOUNT_BALANCE", account, partner, null, ccy, null, null, tc, lc, gc, gc);

    [Fact]
    public void RevaluationRoundsEachCurrencyAtItsScaleAndRollsUpPerAccountAndCurrency()
    {
        var plan = RevaluationModel.Build([Unit(Bank, 100m, 90m, 90m), Unit(Bank, 50.55m, 350m, 45m)], Rates(7.2m, 0.95m), Dkk);
        Assert.Equal(2, plan.Items.Count);
        var line = Assert.Single(plan.Adjustments);
        // 150.55 USD: 100 x 7.2 = 720.00 and 50.55 x 7.2 = 363.96 give adjustments 630 and 13.96 in DKK
        Assert.Equal((Bank, "USD", 643.96m), (line.AccountId, line.CurrencyTc, line.Lc));
        Assert.All(plan.Items, i => Assert.Equal(1, i.AdjustmentLineNo));
        // a gain is the opposite on the gain account, one line per currency and direction
        var gain = Assert.Single(plan.Results);
        Assert.Equal((true, -643.96m), (gain.IsGain, gain.Lc));
        Assert.Equal(-plan.Adjustments.Sum(a => a.Gc), plan.Results.Sum(r => r.Gc));
    }

    [Fact]
    public void ALossGoesToTheLossDirectionAndATieToGain()
    {
        var plan = RevaluationModel.Build([Unit(Bank, 100m, 800m, 100m), Unit(Other, 100m, 720m, 95m)], Rates(7.2m, 0.95m), Dkk);
        Assert.Single(plan.Adjustments);   // the second account is at its closing rate in DKK and EUR: no line
        Assert.False(plan.Results.Single().IsGain);
        Assert.Equal(80m, plan.Results.Single().Lc);
    }

    [Fact]
    public void ACurrencyThatIsTheTransactionCurrencyIsNotRevalued()
    {
        var targets = new RevaluationTargets("USD", 2, "EUR", 2, null, 2);
        var unit = new RevaluationUnit("ACCOUNT_BALANCE", Bank, null, null, "USD", null, null, 100m, 100m, 90m, null);
        Assert.Equal([("USD", "EUR")], RevaluationModel.RatePairs([unit], targets));
        var plan = RevaluationModel.Build([unit], new Dictionary<(string, string), RevaluationRateResult> { [("USD", "EUR")] = new("USD", "EUR", 0.95m, new DateOnly(2026, 3, 31)) }, targets);
        var item = Assert.Single(plan.Items);
        Assert.Equal((0m, 5m, null), (item.AdjustmentLc, item.AdjustmentGc, item.AdjustmentRc));
    }

    [Fact]
    public void TheDigestIsStableAndTheDigestOfNothingIsTheDigestOfTheEmptyText()
    {
        var a = RevaluationModel.Build([Unit(Bank, 100m, 90m, 90m)], Rates(7.2m, 0.95m), Dkk);
        var b = RevaluationModel.Build([Unit(Bank, 100m, 90m, 90m)], Rates(7.2m, 0.95m), Dkk);
        var c = RevaluationModel.Build([Unit(Bank, 100m, 90m, 90m)], Rates(7.3m, 0.95m), Dkk);
        Assert.Equal(a.Digest, b.Digest);
        Assert.NotEqual(a.Digest, c.Digest);
        Assert.Equal(SHA256.HashData([]), RevaluationModel.Build([], new Dictionary<(string, string), RevaluationRateResult>(), Dkk).Digest);
    }

    [Theory]
    [InlineData(100, 90, 85, 1, 0.9, 0.85)]
    [InlineData(0, 90, 85, 1, 1, 0.9444444444)]
    [InlineData(0, 0, 85, 1, 1, 1)]
    [InlineData(100, -90, 0, 1, 0.9, 1)]
    public void DerivedRatesAreTheRatioToTheFirstNonZeroAmount(double lc, double gc, double rc, double rateLc, double rateGc, double rateRc)
    {
        var (l, g, r) = ClosingMath.DerivedRates((decimal)lc, (decimal)gc, (decimal)rc);
        Assert.Equal(((decimal)rateLc, (decimal)rateGc, (decimal?)rateRc), (l, g, r));
    }

    private static ClosingGroup Group(Guid account, decimal lc, decimal gc, decimal rc, Guid? cc = null) => new(account, cc, null, null, null, [], lc, gc, rc);

    [Fact]
    public void ClosingLinesOpposeEachGroupAndNetAgainstRetainedEarnings()
    {
        Guid re = Guid.NewGuid(), revenue = Guid.NewGuid(), expense = Guid.NewGuid();
        var lines = YearEndModel.Build([Group(revenue, -1000m, -134m, -134m), Group(expense, 400m, 54m, 54m), Group(expense, 0m, 0m, 0m, Guid.NewGuid())], re, true);
        Assert.Equal(3, lines.Count);   // the zero group is left out
        Assert.Equal(re, lines[^1].AccountId);
        Assert.Equal((-600m, -80m, -80m), (lines[^1].Lc, lines[^1].Gc, lines[^1].Rc));
        Assert.Equal(0m, lines.Sum(l => l.Lc));
        Assert.Equal(0m, lines.Sum(l => l.Gc));
        Assert.Equal([1, 2, 3], lines.Select(l => l.LineNo).ToArray());
    }

    [Fact]
    public void AGroupThatIsZeroInLocalCurrencyButNotInGroupCurrencyIsStillClosed()
    {
        Guid re = Guid.NewGuid();
        var lines = YearEndModel.Build([Group(Guid.NewGuid(), 0m, 5m, 5m)], re, true);
        Assert.Equal(2, lines.Count);
        Assert.Equal((0m, -5m, 1m), (lines[0].Lc, lines[0].Gc, lines[0].RateGc));   // the ratio to the first non-zero amount is its own rate, 1
        Assert.Empty(YearEndModel.Build([Group(Guid.NewGuid(), 0m, 0m, 0m)], re, true));
    }

    [Fact]
    public void AnAnchorDigestCoversTheChainTheHeadAndTheTime()
    {
        Guid t = Guid.NewGuid(), c = Guid.NewGuid();
        byte[] h = SHA256.HashData([1]);
        var at = new DateTime(2026, 12, 31, 10, 0, 0, DateTimeKind.Utc);
        byte[] d = AnchorHash.Digest(t, c, "LEDGER", "x", 5, h, at);
        Assert.Equal(d, AnchorHash.Digest(t, c, "LEDGER", "x", 5, h, at));
        Assert.NotEqual(d, AnchorHash.Digest(t, c, "LEDGER", "x", 6, h, at));
        Assert.NotEqual(d, AnchorHash.Digest(t, c, "CLEARING", "x", 5, h, at));
        Assert.NotEqual(d, AnchorHash.Digest(t, c, "LEDGER", "x", 5, h, at.AddSeconds(1)));
    }

    [Fact]
    public void TheFingerprintsDifferByCommandAndPayload()
    {
        Guid c = Guid.NewGuid();
        Assert.NotEqual(ClosingFingerprint.ForRun(new(c, 2026, 3)), ClosingFingerprint.ForRun(new(c, 2026, 4)));
        Assert.NotEqual(ClosingFingerprint.ForClose(new(c, 2026)), ClosingFingerprint.ForClose(new(c, 2027)));
        Assert.NotEqual(ClosingFingerprint.ForReopen(new(c, 2026, "ERROR", "a")), ClosingFingerprint.ForReopen(new(c, 2026, "ERROR", "b")));
    }
}
