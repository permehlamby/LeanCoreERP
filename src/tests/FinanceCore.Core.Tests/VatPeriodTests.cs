using FinanceCore.Core.Posting;

namespace FinanceCore.Core.Tests;

/// <summary>The VAT periods of a registration are runs of consecutive regular fiscal periods (08, 4.2).</summary>
public class VatPeriodTests
{
    private static List<RegularPeriod> Year(int periods = 12, int firstMonth = 1) =>
        Enumerable.Range(0, periods).Select(i =>
        {
            var start = new DateOnly(2026, 1, 1).AddMonths(firstMonth - 1 + i);
            return new RegularPeriod(i + 1, start, start.AddMonths(1).AddDays(-1), "OPEN");
        }).ToList();

    [Theory]
    [InlineData("MONTHLY", 12, 1)]
    [InlineData("QUARTERLY", 4, 3)]
    [InlineData("HALF_YEARLY", 2, 6)]
    [InlineData("YEARLY", 1, 12)]
    public void TheFrequencyDecidesHowManyFiscalPeriodsMakeAVatPeriod(string frequency, int count, int size)
    {
        var periods = VatPeriods.Build(Year(), frequency);
        Assert.Equal(count, periods.Count);
        Assert.All(periods, p => Assert.Equal(size, p.Periods.Count));
        Assert.Equal(Enumerable.Range(1, count), periods.Select(p => p.No));
    }

    [Fact]
    public void ABoundaryFollowsTheCalendarAndAShortYearGivesAShorterLastPeriod()
    {
        var q = VatPeriods.Build(Year(), "QUARTERLY");
        Assert.Equal((new DateOnly(2026, 4, 1), new DateOnly(2026, 6, 30)), (q[1].Start, q[1].End));
        var shortYear = VatPeriods.Build(Year(10), "QUARTERLY");
        Assert.Equal([3, 3, 3, 1], shortYear.Select(p => p.Periods.Count));
        Assert.Equal(new DateOnly(2026, 10, 31), shortYear[^1].End);
    }

    [Fact]
    public void TheReturnTypeFollowsTheScheme()
    {
        Assert.Equal("STANDARD", VatPeriods.ReturnTypeOfScheme("STANDARD"));
        Assert.Equal("OSS", VatPeriods.ReturnTypeOfScheme("OSS_UNION"));
        Assert.Equal("OSS", VatPeriods.ReturnTypeOfScheme("OSS_NON_UNION"));
        Assert.Equal("IOSS", VatPeriods.ReturnTypeOfScheme("IOSS"));
    }
}
