using FinanceCore.Core.Hashing;

namespace FinanceCore.Core.Periods;

/// <summary>Payload of fiscal-years:open (06, command table; 04, 7.3). start_date only for the first year of a company.
/// calendar_id is not supported yet: the company's calendar is used.</summary>
public sealed record OpenFiscalYearRequest(Guid CompanyId, int FiscalYear, DateOnly? StartDate = null, Guid? CalendarId = null);

public sealed record OpenedPeriod(int PeriodNo, string PeriodType, DateOnly StartDate, DateOnly EndDate);

public sealed record OpenedFiscalYear(
    Guid CompanyId, int FiscalYear, Guid CalendarId, DateOnly StartDate, DateOnly EndDate, int FirstPeriodNo,
    int SpecialPeriodCount, IReadOnlyList<OpenedPeriod> Periods, IReadOnlyList<Guid> CounterSeriesIds,
    Guid EventId, long EventSeq, string RowHash, string PrevHash);

/// <summary>One check of the readiness list of 07, 11.3 and whether it passed.</summary>
public sealed record ReadinessCheck(string Check, bool Passed, string? Detail = null);

public sealed record ReadinessReport(Guid CompanyId, string Status, bool Ready, IReadOnlyList<ReadinessCheck> Checks);

public sealed record ActivatedCompany(Guid CompanyId, string Status, IReadOnlyList<string> CreatedChainHeads);

/// <summary>The dates of a fiscal year from a calendar (04, 3.1 to 3.3).</summary>
public static class FiscalCalendarMath
{
    /// <summary>First day of the nominal twelve month year with the label <paramref name="label"/>.</summary>
    public static DateOnly NominalStart(int label, int firstMonth, string yearLabelRule) =>
        yearLabelRule == "END" && firstMonth > 1
            ? new DateOnly(label - 1, firstMonth, 1)
            : new DateOnly(label, firstMonth, 1);

    public static DateOnly NominalEnd(DateOnly nominalStart) => nominalStart.AddMonths(12).AddDays(-1);

    /// <summary>Regular periods first_period_no to 12 from the calendar month boundaries, then the special periods,
    /// which have the range of period 12 (04, 3.3).</summary>
    public static List<OpenedPeriod> Periods(DateOnly nominalStart, int firstPeriodNo, int specialPeriodCount)
    {
        var periods = new List<OpenedPeriod>();
        for (int no = firstPeriodNo; no <= 12; no++)
        {
            DateOnly start = nominalStart.AddMonths(no - 1);
            periods.Add(new OpenedPeriod(no, "REGULAR", start, start.AddMonths(1).AddDays(-1)));
        }
        OpenedPeriod last = periods[^1];
        for (int i = 1; i <= specialPeriodCount; i++)
            periods.Add(new OpenedPeriod(12 + i, "SPECIAL", last.StartDate, last.EndDate));
        return periods;
    }
}

/// <summary>The fingerprint of a request for the idempotency check (06, 4.1) of commands other than the posting.</summary>
public static class CommandFingerprint
{
    public static byte[] Compute(string commandName, IReadOnlyList<FieldSpec> schema, IReadOnlyDictionary<string, object?> values)
    {
        using var all = new MemoryStream();
        all.Write(Canonical.Field(System.Text.Encoding.UTF8.GetBytes(commandName)));
        all.Write(Canonical.EncodeFields(schema, values));
        return System.Security.Cryptography.SHA256.HashData(all.ToArray());
    }

    private static readonly FieldSpec[] OpenYear =
    [
        new("company_id", FieldType.Id), new("fiscal_year", FieldType.Int), new("start_date", FieldType.Date, true),
        new("calendar_id", FieldType.Id, true)
    ];

    public static byte[] ForOpenFiscalYear(OpenFiscalYearRequest r) =>
        Compute("fiscal-years:open", OpenYear, new Dictionary<string, object?>
        {
            ["company_id"] = r.CompanyId, ["fiscal_year"] = (long)r.FiscalYear, ["start_date"] = r.StartDate, ["calendar_id"] = r.CalendarId
        });

    private static readonly FieldSpec[] CompanyOnly = [new("company_id", FieldType.Id)];

    public static byte[] ForActivateCompany(Guid companyId) =>
        Compute("companies:activate", CompanyOnly, new Dictionary<string, object?> { ["company_id"] = companyId });
}
