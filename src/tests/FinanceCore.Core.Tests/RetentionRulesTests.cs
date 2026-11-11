using FinanceCore.Core.Retention;

namespace FinanceCore.Core.Tests;

/// <summary>Dating a record by the retention rules (12, 4.1): the four start events, the longest rule across countries, versions by effective date.</summary>
public class RetentionRulesTests
{
    private static readonly DateOnly FiscalYearEnd = new(2026, 3, 31);   // a fiscal year that does not end with the calendar year
    private static readonly DateOnly RecordDate = new(2025, 11, 14);
    private static readonly DateTime Now = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private static RetentionRuleVersion V(Guid rule, string cls, string country, int years, string start, DateTime from) =>
        new(rule, cls, country, years, start, from);

    [Theory]
    [InlineData("FISCAL_YEAR_END", 5, "2031-03-31")]
    [InlineData("RECORD_DATE", 5, "2030-11-14")]
    [InlineData("CALENDAR_YEAR_END_OF_RECORD", 5, "2030-12-31")]
    [InlineData("CALENDAR_YEAR_END_OF_FISCAL_YEAR", 5, "2031-12-31")]
    public void EveryStartEventCountsFromItsOwnDate(string start, int years, string expected) =>
        Assert.Equal(DateOnly.Parse(expected), RetentionRules.RetentionUntil(start, years, FiscalYearEnd, RecordDate));

    [Fact]
    public void AnUnknownStartEventIsAProgrammingError() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RetentionRules.RetentionUntil("TOMORROW", 5, FiscalYearEnd, RecordDate));

    [Fact]
    public void NoRuleGivesNoDate()
    {
        Assert.Null(RetentionRules.For([], "LEDGER", Now, FiscalYearEnd, RecordDate));
        var other = V(Guid.NewGuid(), "STOCK", "DK", 5, "RECORD_DATE", Now.AddDays(-1));
        Assert.Null(RetentionRules.For([other], "LEDGER", Now, FiscalYearEnd, RecordDate));
        var later = V(Guid.NewGuid(), "LEDGER", "DK", 5, "RECORD_DATE", Now.AddDays(1));   // not in force yet
        Assert.Null(RetentionRules.For([later], "LEDGER", Now, FiscalYearEnd, RecordDate));
    }

    [Fact]
    public void TheLongestRuleOfSeveralCountriesWins()
    {
        var dk = V(Guid.NewGuid(), "LEDGER", "DK", 5, "FISCAL_YEAR_END", Now.AddDays(-10));      // 2031-03-31
        var se = V(Guid.NewGuid(), "LEDGER", "SE", 7, "RECORD_DATE", Now.AddDays(-10));          // 2032-11-14
        var de = V(Guid.NewGuid(), "LEDGER", "DE", 10, "RECORD_DATE", Now.AddDays(-10));         // 2035-11-14
        Assert.Equal(new DateOnly(2032, 11, 14), RetentionRules.For([dk, se], "LEDGER", Now, FiscalYearEnd, RecordDate));
        Assert.Equal(new DateOnly(2035, 11, 14), RetentionRules.For([dk, se, de], "LEDGER", Now, FiscalYearEnd, RecordDate));
    }

    [Fact]
    public void OfOneRuleTheVersionInForceAtCreationCounts()
    {
        Guid rule = Guid.NewGuid();
        var v1 = V(rule, "LEDGER", "DK", 5, "RECORD_DATE", new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var v2 = V(rule, "LEDGER", "DK", 10, "RECORD_DATE", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(new DateOnly(2030, 11, 14), RetentionRules.For([v1, v2], "LEDGER", new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc), FiscalYearEnd, RecordDate));
        Assert.Equal(new DateOnly(2035, 11, 14), RetentionRules.For([v1, v2], "LEDGER", Now, FiscalYearEnd, RecordDate));
        Assert.Equal(new DateOnly(2035, 11, 14), RetentionRules.For([v2, v1], "LEDGER", Now, FiscalYearEnd, RecordDate));   // order does not matter
        Assert.Equal(new DateOnly(2030, 11, 14), RetentionRules.For([v1, v2], "LEDGER", v2.EffectiveFrom.AddTicks(-10), FiscalYearEnd, RecordDate));
        Assert.Equal(new DateOnly(2035, 11, 14), RetentionRules.For([v1, v2], "LEDGER", v2.EffectiveFrom, FiscalYearEnd, RecordDate));
    }

    [Fact]
    public void AShorterNewVersionDoesNotShortenTheOtherCountry()
    {
        Guid dk = Guid.NewGuid();
        var dk1 = V(dk, "LEDGER", "DK", 10, "RECORD_DATE", Now.AddDays(-100));
        var dk2 = V(dk, "LEDGER", "DK", 3, "RECORD_DATE", Now.AddDays(-1));
        var se = V(Guid.NewGuid(), "LEDGER", "SE", 7, "RECORD_DATE", Now.AddDays(-100));
        Assert.Equal(new DateOnly(2032, 11, 14), RetentionRules.For([dk1, dk2, se], "LEDGER", Now, FiscalYearEnd, RecordDate));
    }

    [Fact]
    public void ALegalSnapshotIsKeptAtLeastAsLongAsTheLedger()
    {
        var ledger = V(Guid.NewGuid(), "LEDGER", "DK", 10, "RECORD_DATE", Now.AddDays(-1));
        var shortSnapshot = V(Guid.NewGuid(), "LEGAL_SNAPSHOT", "DK", 5, "RECORD_DATE", Now.AddDays(-1));
        var longSnapshot = V(Guid.NewGuid(), "LEGAL_SNAPSHOT", "DK", 15, "RECORD_DATE", Now.AddDays(-1));
        Assert.Equal(new DateOnly(2035, 11, 14), RetentionRules.ForLegalSnapshot([ledger, shortSnapshot], Now, FiscalYearEnd, RecordDate));
        Assert.Equal(new DateOnly(2040, 11, 14), RetentionRules.ForLegalSnapshot([ledger, longSnapshot], Now, FiscalYearEnd, RecordDate));
    }

    [Fact]
    public void ALegalSnapshotNeedsBothRules()
    {
        var ledger = V(Guid.NewGuid(), "LEDGER", "DK", 10, "RECORD_DATE", Now.AddDays(-1));
        var snapshot = V(Guid.NewGuid(), "LEGAL_SNAPSHOT", "DK", 10, "RECORD_DATE", Now.AddDays(-1));
        Assert.Null(RetentionRules.ForLegalSnapshot([ledger], Now, FiscalYearEnd, RecordDate));
        Assert.Null(RetentionRules.ForLegalSnapshot([snapshot], Now, FiscalYearEnd, RecordDate));
        Assert.Null(RetentionRules.ForLegalSnapshot([], Now, FiscalYearEnd, RecordDate));
    }

    [Fact]
    public void TheFingerprintCoversEveryFieldAndKeepsAnAbsentDateApartFromTheGivenOne()
    {
        var req = new CreateRetentionRuleVersionRequest(Guid.NewGuid(), "LEDGER", "DK", 5, "RECORD_DATE", null, null);
        Assert.Equal(RetentionRules.Fingerprint(req), RetentionRules.Fingerprint(req with { }));
        foreach (var changed in new[]
        {
            req with { CompanyId = Guid.NewGuid() }, req with { RecordClass = "STOCK" }, req with { CountryCode = "SE" }, req with { RetentionYears = 6 },
            req with { StartEvent = "FISCAL_YEAR_END" }, req with { LegalBasis = "Bogføringsloven" }, req with { EffectiveFrom = DateTime.UtcNow }
        })
            Assert.NotEqual(RetentionRules.Fingerprint(req), RetentionRules.Fingerprint(changed));
        Assert.NotEqual(RetentionRules.Fingerprint(req with { LegalBasis = "" }), RetentionRules.Fingerprint(req));
    }
}
