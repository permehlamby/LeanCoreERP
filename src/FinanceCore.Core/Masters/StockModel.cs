using System.Globalization;
using FinanceCore.Core.Reads;

namespace FinanceCore.Core.Masters;

// ---- stock-documents:post (10, 6.2). First slice: RECEIPT, ISSUE and ADJUSTMENT, moving average, items without tracking, the company currency.
public sealed record StockAdjustment(string Mode, string? Direction = null, decimal? CountedQuantity = null, decimal? ExpectedBookQuantity = null, string? CountRef = null);

public sealed record StockLineInput(
    string LineRef, Guid ItemId, Guid WarehouseId, decimal Quantity, string UnitCode, Guid? LocationId = null, decimal? UnitPrice = null, string? Currency = null,
    StockAdjustment? Adjustment = null, string? LineText = null);

public sealed record PostStockDocumentRequest(
    Guid CompanyId, string DocumentType, DateOnly DocumentDate, DateOnly PostingDate, IReadOnlyList<StockLineInput> Lines, string? ReasonCode = null, string? HeaderText = null,
    string? SourceDocumentRef = null);

public sealed record StockMovementResult(
    int LineNo, string? LineRef, string MovementType, string CostSource, Guid ItemId, Guid WarehouseId, decimal BaseQuantity, decimal ValueLc, decimal CostQtyAfter,
    decimal CostValueAfterLc, decimal StockQtyAfter);

public sealed record StockDocumentResult(
    Guid StockDocumentId, string DocumentNumber, string DocumentType, long ChainSeq, string RowHash, Guid? JournalDocumentId, IReadOnlyList<StockMovementResult> Movements);

// ---- reads (10, 8, permission read.inventory)
public sealed record GetStockDocumentRequest(Guid Id, IReadOnlyList<string>? Include = null);

public sealed record ListStockDocumentsRequest(
    Guid? CompanyId = null, string? DocumentType = null, DateOnly? DateFrom = null, DateOnly? DateTo = null, Guid? ItemId = null, Guid? WarehouseId = null,
    string? SourceDocumentRef = null, int? Limit = null, string? Cursor = null);

public sealed record StockDocumentDetail(System.Text.Json.JsonElement Document, System.Text.Json.JsonElement? Movements);

public sealed record StockDocumentList(IReadOnlyList<System.Text.Json.JsonElement> Items, ReadPage Page);

public sealed record StockBalancesRequest(Guid CompanyId, DateOnly? AsOfDate = null, Guid? ItemId = null, Guid? WarehouseId = null);

public sealed record StockBalanceRow(Guid ItemId, Guid WarehouseId, decimal BaseQuantity, decimal ValueLc);

public sealed record StockBalancesResult(DateOnly AsOfDate, IReadOnlyList<StockBalanceRow> Rows, decimal TotalValueLc);

/// <summary>The valuation arithmetic of 5.2 (moving average) and the rules of the movement types of the first slice.</summary>
public static class StockValuation
{
    public const string PostPermission = "stock.post", ReadPermission = "read.inventory";
    public static readonly string[] DocumentTypes = ["RECEIPT", "ISSUE", "ADJUSTMENT"];

    /// <summary>The value of an issue (5.2): the value of the cost key times the issued share, rounded at the currency scale; an issue of the whole remaining quantity takes the whole remaining value.</summary>
    public static decimal IssueValue(decimal costQtyBefore, decimal costValueBefore, decimal issuedQuantity, int currencyScale) =>
        issuedQuantity == costQtyBefore ? costValueBefore : Math.Round(costValueBefore * issuedQuantity / costQtyBefore, currencyScale, MidpointRounding.AwayFromZero);

    /// <summary>The value of a receipt (6.2): quantity times the unit price, at the currency scale.</summary>
    public static decimal ReceiptValue(decimal quantity, decimal unitPrice, int currencyScale) =>
        Math.Round(quantity * unitPrice, currencyScale, MidpointRounding.AwayFromZero);

    /// <summary>The unit cost shown on a movement: value over base quantity at the rate scale, null for a movement without quantity.</summary>
    public static decimal? UnitCost(decimal valueLc, decimal baseQuantity) =>
        baseQuantity == 0m ? null : Math.Round(Math.Abs(valueLc / baseQuantity), 10, MidpointRounding.AwayFromZero);

    /// <summary>The role that an issue debits (10, 7): a loss reason goes to ADJUSTMENT_LOSS, any other issue to COGS.</summary>
    public static string IssueRole(string? reasonCode) =>
        reasonCode is "LOSS" or "SCRAP" or "DAMAGE" or "WRITE_OFF" ? "ADJUSTMENT_LOSS" : "COGS";

    public static string DocumentTypeOf(string requestType) => "STOCK_" + requestType;

    public static byte[] Fingerprint(PostStockDocumentRequest r)
    {
        string lines = string.Join("#", r.Lines.Select(l => string.Join("|",
            l.LineRef, l.ItemId.ToString("D"), l.WarehouseId.ToString("D"), l.LocationId?.ToString("D") ?? "-", l.Quantity.ToString(CultureInfo.InvariantCulture), l.UnitCode,
            l.UnitPrice?.ToString(CultureInfo.InvariantCulture) ?? "-", l.Currency ?? "-", l.Adjustment is { } a
                ? $"{a.Mode}/{a.Direction}/{a.CountedQuantity}/{a.ExpectedBookQuantity}/{a.CountRef}" : "-", l.LineText ?? "-")));
        return MasterRules.Fingerprint("stock-documents:post",
            $"{r.CompanyId:D}|{r.DocumentType}|{r.DocumentDate:yyyy-MM-dd}|{r.PostingDate:yyyy-MM-dd}|{r.ReasonCode}|{r.HeaderText}|{r.SourceDocumentRef}|{lines}");
    }
}
