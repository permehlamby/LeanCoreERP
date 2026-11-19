using System.Globalization;
using FinanceCore.Core.Reads;

namespace FinanceCore.Core.Masters;

/// <summary>
/// The two kinds of order (11, 4.1, 4.6) with their fields, so that one set of commands serves both. A request is a dictionary of header fields and a list of dictionaries
/// of line fields, validated against these registries like the master data. Permissions are the ones of 11, 6.1.
/// </summary>
public sealed record OrderKind(
    string Kind, string Resource, string HeaderTable, string LineTable, string IdColumn, string PartyColumn, string PartyRole, string DocumentType, string OrderPermission,
    IReadOnlyList<MasterField> HeaderCreate, IReadOnlyList<MasterField> HeaderChange, IReadOnlyList<MasterField> LineCreate, IReadOnlyList<MasterField> LineChange)
{
    public const string PromisePermission = "order.promise";
    public const string ReadPermission = "read.orders";
    public const int MinYear = 1900, MaxYear = 2200;

    private static readonly string[] Policies = ["DELIVERED", "ORDERED"], Fulfilment = ["STOCK", "SERVICE", "DROPSHIP"], MatchModes = ["THREE_WAY", "TWO_WAY"];

    private static IReadOnlyList<MasterField> CommonLine(bool sales) =>
    [
        new("line_ref", MasterKind.Text, 100), new("item_id", MasterKind.Id, 0, false), new("description", MasterKind.Text, 200),
        new("quantity", MasterKind.Factor, 0, false), new("unit_code", MasterKind.Code, 3, false), new("unit_price", MasterKind.Amount, 0, false),
        new("price_unit_quantity", MasterKind.Factor), new("discount_pct", MasterKind.Rate), new("tax_class_id", MasterKind.Id), new("warehouse_id", MasterKind.Id),
        new("origin_country", MasterKind.Country), new("destination_country", MasterKind.Country),
        new("over_tolerance_pct", MasterKind.Rate), new("under_tolerance_pct", MasterKind.Rate), new("requested_date", MasterKind.Date), new("confirmed_date", MasterKind.Date),
        new("line_text", MasterKind.Text, 200),
        .. sales
            ? new[] { new MasterField("fulfilment_type", MasterKind.Code, 50, true, Fulfilment), new MasterField("invoice_policy", MasterKind.Code, 50, true, Policies) }
            : [new MasterField("match_mode", MasterKind.Code, 50, true, MatchModes)]
    ];

    private static readonly string[] LineChangeable =
    [
        "description", "quantity", "unit_price", "discount_pct", "warehouse_id", "origin_country", "destination_country", "over_tolerance_pct", "under_tolerance_pct", "line_text"
    ];

    public static readonly OrderKind Sales = new(
        "SALES", "sales-orders", "sales_order", "sales_order_line", "sales_order_id", "customer_partner_id", "customer", "SALES_ORDER", "sales.order",
        [
            new("company_id", MasterKind.Id, 0, false), new("order_date", MasterKind.Date, 0, false), new("order_currency", MasterKind.Currency, 0, false), new("customer_partner_id", MasterKind.Id, 0, false),
            new("payment_terms_id", MasterKind.Id), new("customer_reference", MasterKind.Text, 100), new("requested_delivery_date", MasterKind.Date),
            new("incoterms_code", MasterKind.Code, 50), new("buyer_reference", MasterKind.Text, 100), new("contract_reference", MasterKind.Text, 100),
            new("header_text", MasterKind.Text, 200), new("source_document_ref", MasterKind.Text, 100)
        ],
        [
            new("payment_terms_id", MasterKind.Id), new("customer_reference", MasterKind.Text, 100), new("requested_delivery_date", MasterKind.Date),
            new("incoterms_code", MasterKind.Code, 50), new("buyer_reference", MasterKind.Text, 100), new("contract_reference", MasterKind.Text, 100), new("header_text", MasterKind.Text, 200)
        ],
        CommonLine(true), [.. CommonLine(true).Where(f => LineChangeable.Contains(f.Name))]);

    public static readonly OrderKind Purchase = new(
        "PURCHASE", "purchase-orders", "purchase_order", "purchase_order_line", "purchase_order_id", "supplier_partner_id", "supplier", "PURCHASE_ORDER", "purchase.order",
        [
            new("company_id", MasterKind.Id, 0, false), new("order_date", MasterKind.Date, 0, false), new("order_currency", MasterKind.Currency, 0, false), new("supplier_partner_id", MasterKind.Id, 0, false),
            new("payment_terms_id", MasterKind.Id), new("supplier_reference", MasterKind.Text, 100), new("expected_receipt_date", MasterKind.Date),
            new("ship_from_country", MasterKind.Country), new("incoterms_code", MasterKind.Code, 50), new("header_text", MasterKind.Text, 200),
            new("source_document_ref", MasterKind.Text, 100)
        ],
        [
            new("payment_terms_id", MasterKind.Id), new("supplier_reference", MasterKind.Text, 100), new("expected_receipt_date", MasterKind.Date),
            new("ship_from_country", MasterKind.Country), new("incoterms_code", MasterKind.Code, 50), new("header_text", MasterKind.Text, 200)
        ],
        CommonLine(false), [.. CommonLine(false).Where(f => LineChangeable.Contains(f.Name) || f.Name == "match_mode")]);

    public static readonly IReadOnlyList<OrderKind> All = [Sales, Purchase];

    public IReadOnlyList<MasterField> LineDates { get; } = [new("requested_date", MasterKind.Date), new("confirmed_date", MasterKind.Date)];

    /// <summary>The net amount of a line (11, 4.2): quantity × unit price / price unit quantity × (1 − discount), rounded half away from zero at the currency scale.</summary>
    public static decimal NetAmount(decimal quantity, decimal unitPrice, decimal priceUnitQuantity, decimal discountPct, int currencyScale) =>
        Math.Round(quantity * unitPrice / priceUnitQuantity * (1m - discountPct / 100m), currencyScale, MidpointRounding.AwayFromZero);

    public static bool DateInRange(DateOnly d) => d.Year is >= MinYear and <= MaxYear;
}

// ---- requests (11, 6.1, 6.2). `fields` and the line dictionaries are checked against the registries of the kind.
public sealed record CreateOrderRequest(IReadOnlyDictionary<string, object?> Fields, IReadOnlyList<IReadOnlyDictionary<string, object?>> Lines);

public sealed record LineChange(int LineNo, IReadOnlyDictionary<string, object?> Fields);

public sealed record ChangeOrderRequest(Guid Id, long ExpectedVersion, IReadOnlyDictionary<string, object?>? Fields = null, IReadOnlyList<LineChange>? Lines = null);

public sealed record AddOrderLineRequest(Guid Id, long ExpectedVersion, IReadOnlyDictionary<string, object?> Line);

public sealed record CloseOrderLineRequest(Guid Id, long ExpectedVersion, int LineNo, string ReasonCode);

/// <summary>Cancels one line (LineNo set) or the whole order (LineNo null): only the undelivered and uninvoiced part (11, 4.4).</summary>
public sealed record CancelOrderRequest(Guid Id, long ExpectedVersion, string ReasonCode, int? LineNo = null);

public sealed record ChangeLineDatesRequest(Guid Id, long ExpectedVersion, IReadOnlyList<LineChange> Lines);

/// <summary>A new version of the order settings of a company (11, 3.1): a later effective_from. A field that is absent takes its default.</summary>
public sealed record CreateOrderSettingVersionRequest(Guid CompanyId, IReadOnlyDictionary<string, object?> Fields);

public sealed record OrderSettingVersionResult(Guid CompanyId, DateOnly EffectiveFrom);

public static class OrderSettingFields
{
    public static readonly IReadOnlyList<MasterField> Version =
    [
        new("effective_from", MasterKind.Date, 0, false), new("match_mode_default", MasterKind.Code, 50, true, ["THREE_WAY", "TWO_WAY"]),
        new("default_over_tolerance_pct", MasterKind.Rate), new("default_under_tolerance_pct", MasterKind.Rate), new("auto_reserve", MasterKind.Bool),
        new("sales_invoice_mode", MasterKind.Code, 50, true, ["CORE_BUILDS", "APP_SUPPLIES"]), new("allow_invoice_before_receipt", MasterKind.Bool)
    ];
}

// ---- results
public sealed record OrderLineResult(int LineNo, string? LineRef, Guid ItemId, decimal Quantity, string UnitCode, decimal BaseQuantity, decimal NetAmount, string LineStatus);

public sealed record OrderResult(
    Guid OrderId, string OrderNumber, long RowVersion, string LifecycleStatus, decimal TotalNetAmount, IReadOnlyList<OrderLineResult> Lines, IReadOnlyList<string> ChangedFields);

// ---- reads (11, 8): sales-orders:get, :list, purchase-orders:get, :list, permission read.orders
public sealed record GetOrderRequest(Guid Id, IReadOnlyList<string>? Include = null);

public sealed record ListOrdersRequest(
    Guid? PartnerId = null, string? LifecycleStatus = null, DateOnly? DateFrom = null, DateOnly? DateTo = null, string? SourceDocumentRef = null, int? Limit = null, string? Cursor = null);

public sealed record OrderDetail(System.Text.Json.JsonElement Order, System.Text.Json.JsonElement? Lines, System.Text.Json.JsonElement? Events, System.Text.Json.JsonElement? Changes, System.Text.Json.JsonElement? Progress = null);

public sealed record OrderList(IReadOnlyList<System.Text.Json.JsonElement> Items, ReadPage Page);

public static class OrderRules
{
    public static readonly IReadOnlyList<string> Includes = ["lines", "events", "changes", "progress"];

    public static byte[] Fingerprint(string command, CreateOrderRequest r) =>
        MasterRules.Fingerprint(command, MasterRules.Text(r.Fields) + "|" + string.Join("#", r.Lines.Select(l => MasterRules.Text(l))));

    private static string Lines(IReadOnlyList<LineChange>? l) => l is null ? "-" : string.Join("#", l.Select(x => x.LineNo.ToString(CultureInfo.InvariantCulture) + "{" + MasterRules.Text(x.Fields) + "}"));

    public static byte[] Fingerprint(string command, ChangeOrderRequest r) =>
        MasterRules.Fingerprint(command, $"{r.Id:D}|{r.ExpectedVersion}|{MasterRules.Text(r.Fields)}|{Lines(r.Lines)}");

    public static byte[] Fingerprint(string command, AddOrderLineRequest r) =>
        MasterRules.Fingerprint(command, $"{r.Id:D}|{r.ExpectedVersion}|{MasterRules.Text(r.Line)}");

    public static byte[] Fingerprint(string command, CloseOrderLineRequest r) =>
        MasterRules.Fingerprint(command, $"{r.Id:D}|{r.ExpectedVersion}|{r.LineNo}|{r.ReasonCode}");

    public static byte[] Fingerprint(string command, CancelOrderRequest r) =>
        MasterRules.Fingerprint(command, $"{r.Id:D}|{r.ExpectedVersion}|{r.LineNo}|{r.ReasonCode}");

    public static byte[] Fingerprint(string command, ChangeLineDatesRequest r) =>
        MasterRules.Fingerprint(command, $"{r.Id:D}|{r.ExpectedVersion}|{Lines(r.Lines)}");

    public static byte[] Fingerprint(CreateOrderSettingVersionRequest r) =>
        MasterRules.Fingerprint("company-order-settings:create-version", $"{r.CompanyId:D}|{MasterRules.Text(r.Fields)}");
}
