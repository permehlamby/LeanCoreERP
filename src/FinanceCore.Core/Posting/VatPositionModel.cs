namespace FinanceCore.Core.Posting;

/// <summary>Payload of vat-positions:get (09, 4.10; 08, 7.4): the position of a registration and return type at a date, as a prepared return would show it. as_of_date defaults to today.</summary>
public sealed record GetVatPositionRequest(Guid RegistrationId, string ReturnType, DateOnly? AsOfDate = null);

/// <summary>One row, in the shape of a row of a return (BOX, EC_LIST or OSS). LateLineCount is the number of tax lines of the row with a tax date before the VAT period of the date. NondeductibleAmount is set on a BOX row only (19, 7).</summary>
public sealed record VatPositionLine(
    string RowType, string? BoxCode, Guid? PartnerId, string? PartnerVatId, string? CountryCode, string? Category, decimal? Rate, string? SupplyKind,
    string CurrencyCode, decimal BaseAmount, decimal TaxAmount, int LineCount, int LateLineCount, decimal? NondeductibleAmount);

/// <summary>The unreported tax lines up to the date: their rows, with the VAT period that holds the date.</summary>
public sealed record VatPositionResult(
    Guid RegistrationId, Guid CompanyId, string ReturnType, DateOnly AsOfDate, int FiscalYear, int VatPeriodNo, DateOnly PeriodStartDate, DateOnly PeriodEndDate,
    string CurrencyCode, int TaxLineCount, int LateLineCount, IReadOnlyList<VatPositionLine> Lines);
