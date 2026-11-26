using System.Globalization;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;
using Npgsql;
using NpgsqlTypes;

namespace FinanceCore.Data;

public sealed partial class StockCommands
{
    /// <summary>
    /// The derivation of a stock document under the locks (10, 5.1 to 5.4, P4 to P6): per request line the movements with their running balances, the FIFO layers that they create and consume, the
    /// tracking rows that they create, negative stock and its settlement, and the journal pairs. The state of a cost key (item, warehouse if the cost scope is WAREHOUSE, lot or serial) and of a stock
    /// key (item, warehouse, location, lot or serial, status) is loaded once and followed in memory, so two lines of one document see each other.
    /// </summary>
    internal sealed class Derivation(TenantTransaction tx, Guid tenantId, Guid companyId, DateOnly date, int lcScale, string costScope, Guid stockId)
    {
        internal sealed record ConsumptionPlan(Guid LayerDoc, int LayerLine, decimal Quantity, decimal Value);

        internal sealed record LayerPlan(long Seq, DateOnly Date, decimal Quantity, decimal Value);

        internal sealed record JournalPair(
            string DebitRole, string CreditRole, decimal Value, Guid ItemGroup, Guid DebitWarehouse, Guid CreditWarehouse, Guid Item, int RequestIndex, int? CreditRequestIndex = null, bool SkipWhenSameAccount = false);

        internal sealed class Planned
        {
            public int LineNo;
            public string? LineRef;
            public string? LineText;
            public required ItemRow Item;
            public required string MovementType, CostSource, UnitCode;
            public decimal Quantity, BaseQuantity, ValueLc, CostQtyAfter, CostValueAfter, StockQtyAfter;
            public decimal? ValueTc, ExpectedBook;
            public string? CountRef;
            public int RequestIndex;
            public Guid? RefDocument;
            public int? RefLine;
            public Guid WarehouseId;
            public Guid? LocationId, TrackingId, CostWarehouseId;
            public string StockStatus = "AVAILABLE";
            public bool LotExpired;
            public int? ReversalOfLine;
            public List<ConsumptionPlan> Consumptions = [];
            public LayerPlan? NewLayer;
        }

        private sealed class LayerState
        {
            public Guid DocId; public int LineNo; public long Seq; public DateOnly Date; public decimal Qty, Value, ConsQty, ConsValue;
            public decimal RemQty => Qty - ConsQty;
            public decimal RemValue => Value - ConsValue;
        }

        private sealed class CostKeyState
        {
            public Guid? CostWarehouse, Tracking; public decimal Qty, Value, LastUnitCost; public bool Fifo; public long LastSeq; public decimal? LastLayerUnitCost;
            public List<LayerState> Layers = [];
        }

        private sealed record CostKey(Guid Item, Guid? CostWarehouse, Guid? Tracking);

        private sealed record StockKey(Guid Item, Guid Warehouse, Guid? Location, Guid? Tracking, string Status);

        public List<Planned> Moves { get; } = [];
        public List<JournalPair> Pairs { get; } = [];
        public List<CommandError> Warnings { get; } = [];
        private readonly Dictionary<CostKey, CostKeyState> costs = [];
        private readonly Dictionary<StockKey, decimal> stocks = [];
        private readonly Dictionary<(Guid, Guid), bool> negative = [];
        private readonly Dictionary<(Guid, Guid), decimal> serials = [];
        private readonly List<(Guid Item, Guid Id, string Kind, StockTracking Tracking)> newTrackings = [];
        private readonly Dictionary<(Guid, string), Guid> newTrackingIds = [];
        private readonly Dictionary<(Guid, Guid), string?> locationTypes = [];
        private readonly Dictionary<(Guid Item, Guid Warehouse, Guid? Tracking), decimal> availableDelta = [];
        private readonly Dictionary<(Guid Item, Guid Warehouse), decimal> consumedAll = [];
        private readonly Dictionary<(Guid Item, Guid Warehouse, Guid Tracking), decimal> consumedLot = [];
        private readonly Dictionary<Guid, decimal> consumedBy = [];
        private readonly List<(Guid Reservation, decimal Quantity, Guid Item, Guid Warehouse, string DemandType, string DemandRef)> consumes = [];

        private static CommandError Fail(string code, string phase, string path, params (string Key, object? Value)[] p) => JournalDocumentPoster.Error(code, phase, path, p);

        // ---------------------------------------------------------------------------------------------------------
        // the state of the keys
        // ---------------------------------------------------------------------------------------------------------
        private async Task<CostKeyState> CostAsync(ItemRow item, Guid warehouse, Guid? tracking, CancellationToken ct)
        {
            Guid? cw = costScope == "WAREHOUSE" ? warehouse : null;
            var key = new CostKey(item.ItemId, cw, tracking);
            if (costs.TryGetValue(key, out CostKeyState? known)) return known;
            var s = new CostKeyState { CostWarehouse = cw, Tracking = tracking, Fifo = item.Valuation == "FIFO" };
            const string where = "tenant_id = @t and company_id = @c and item_id = @i and cost_warehouse_id is not distinct from @cw::uuid and tracking_id is not distinct from @tr::uuid";
            NpgsqlCommand Cmd(string sql)
            {
                var cmd = tx.Command(sql);
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", item.ItemId);
                cmd.Parameters.Add(new NpgsqlParameter("cw", NpgsqlDbType.Uuid) { Value = (object?)cw ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("tr", NpgsqlDbType.Uuid) { Value = (object?)tracking ?? DBNull.Value });
                return cmd;
            }
            await using (var cmd = Cmd($"select cost_qty_after, cost_value_after_lc from core.stock_movement where {where} order by chain_seq desc, line_no desc limit 1"))
            {
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct)) { s.Qty = r.GetDecimal(0); s.Value = r.GetDecimal(1); }
            }
            await using (var cmd = Cmd($"select cost_value_after_lc / cost_qty_after from core.stock_movement where {where} and cost_qty_after > 0 order by chain_seq desc, line_no desc limit 1"))
                if (await cmd.ExecuteScalarAsync(ct) is decimal unit) s.LastUnitCost = unit;
            if (s.Fifo)
            {
                await using (var cmd = Cmd($"select layer_seq, unit_cost_lc from core.stock_cost_layer where {where} order by layer_seq desc limit 1"))
                {
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    if (await r.ReadAsync(ct)) { s.LastSeq = r.GetInt64(0); s.LastLayerUnitCost = r.GetDecimal(1); }
                }
                await using (var cmd = Cmd(
                    $"select l.stock_document_id, l.line_no, l.layer_seq, l.layer_date, l.quantity_base, l.value_lc + coalesce((select sum(a.value_lc) from core.stock_layer_adjustment a where a.tenant_id = l.tenant_id " +
                    "and a.company_id = l.company_id and a.layer_doc_id = l.stock_document_id and a.layer_line_no = l.line_no), 0), " +
                    "coalesce((select sum(c.quantity_base) from core.stock_layer_consumption c where c.tenant_id = l.tenant_id and c.company_id = l.company_id and c.layer_doc_id = l.stock_document_id and c.layer_line_no = l.line_no), 0), " +
                    "coalesce((select sum(c.value_lc) from core.stock_layer_consumption c where c.tenant_id = l.tenant_id and c.company_id = l.company_id and c.layer_doc_id = l.stock_document_id and c.layer_line_no = l.line_no), 0) " +
                    "from core.stock_cost_layer l where l.tenant_id = @t and l.company_id = @c and l.item_id = @i and l.cost_warehouse_id is not distinct from @cw::uuid and l.tracking_id is not distinct from @tr::uuid " +
                    "and not l.exhausted order by l.layer_seq"))
                {
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct))
                        s.Layers.Add(new LayerState { DocId = r.GetGuid(0), LineNo = r.GetInt32(1), Seq = r.GetInt64(2), Date = r.GetFieldValue<DateOnly>(3), Qty = r.GetDecimal(4), Value = r.GetDecimal(5), ConsQty = r.GetDecimal(6), ConsValue = r.GetDecimal(7) });
                }
            }
            costs[key] = s;
            return s;
        }

        private async Task<decimal> StockAsync(StockKey key, CancellationToken ct)
        {
            if (stocks.TryGetValue(key, out decimal known)) return known;
            await using var cmd = tx.Command(
                "select stock_qty_after from core.stock_movement where tenant_id = @t and company_id = @c and item_id = @i and warehouse_id = @w and location_id is not distinct from @l::uuid " +
                "and tracking_id is not distinct from @tr::uuid and stock_status = @st order by chain_seq desc, line_no desc limit 1");
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", key.Item); cmd.Parameters.AddWithValue("w", key.Warehouse);
            cmd.Parameters.Add(new NpgsqlParameter("l", NpgsqlDbType.Uuid) { Value = (object?)key.Location ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("tr", NpgsqlDbType.Uuid) { Value = (object?)key.Tracking ?? DBNull.Value });
            cmd.Parameters.AddWithValue("st", key.Status);
            decimal q = await cmd.ExecuteScalarAsync(ct) is decimal d ? d : 0m;
            stocks[key] = q;
            return q;
        }

        private async Task<bool> AllowNegativeAsync(Guid item, Guid warehouse, CancellationToken ct)
        {
            if (negative.TryGetValue((item, warehouse), out bool known)) return known;
            await using var cmd = tx.Command("select allow_negative_stock from core.item_warehouse where tenant_id = @t and company_id = @c and warehouse_id = @w and item_id = @i and active");
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("w", warehouse); cmd.Parameters.AddWithValue("i", item);
            bool allow = await cmd.ExecuteScalarAsync(ct) is true;
            negative[(item, warehouse)] = allow;
            return allow;
        }

        /// <summary>The quantity of a serial on hand in the company, over all its stock keys (3.8.1: a serial cannot be received while it is on hand).</summary>
        private async Task<decimal> SerialOnHandAsync(Guid item, Guid tracking, CancellationToken ct)
        {
            if (serials.TryGetValue((item, tracking), out decimal known)) return known;
            await using var cmd = tx.Command(
                "select coalesce(sum(q), 0) from (select distinct on (warehouse_id, location_id, stock_status) stock_qty_after q from core.stock_movement where tenant_id = @t and company_id = @c " +
                "and item_id = @i and tracking_id = @tr order by warehouse_id, location_id, stock_status, chain_seq desc, line_no desc) x");
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", item); cmd.Parameters.AddWithValue("tr", tracking);
            decimal q = (decimal)(await cmd.ExecuteScalarAsync(ct))!;
            serials[(item, tracking)] = q;
            return q;
        }

        // ---------------------------------------------------------------------------------------------------------
        // the arithmetic of a movement on a cost key
        // ---------------------------------------------------------------------------------------------------------
        private (decimal Value, List<ConsumptionPlan> Consumptions, decimal Left) ConsumeFifo(CostKeyState ck, decimal quantity)
        {
            decimal need = quantity, value = 0m;
            var consumptions = new List<ConsumptionPlan>();
            foreach (LayerState layer in ck.Layers)
            {
                if (need <= 0m) break;
                if (layer.RemQty <= 0m) continue;
                decimal t = Math.Min(layer.RemQty, need);
                decimal v = t == layer.RemQty ? layer.RemValue : Math.Round(layer.RemValue * t / layer.RemQty, lcScale, MidpointRounding.AwayFromZero);
                layer.ConsQty += t; layer.ConsValue += v; need -= t; value += v;
                consumptions.Add(new ConsumptionPlan(layer.DocId, layer.LineNo, t, v));
            }
            return (value, consumptions, need);
        }

        private static void Track(CostKeyState ck) { if (ck.Qty > 0m) ck.LastUnitCost = ck.Value / ck.Qty; }

        /// <summary>An issue-like outbound movement on a cost key (5.2, 5.3, 5.4): moving average takes the share of the value, FIFO consumes layers, and a quantity that is not there is issued at the last unit cost when negative stock is allowed.</summary>
        private CommandError? Outbound(Planned p, CostKeyState ck, decimal q, bool allowNegative, bool consumeLayers, string path)
        {
            decimal value;
            if (ck.Qty >= q)
            {
                if (ck.Fifo && consumeLayers) { var (v, cons, _) = ConsumeFifo(ck, q); value = v; p.Consumptions.AddRange(cons); }
                else value = StockValuation.IssueValue(ck.Qty, ck.Value, q, lcScale);
            }
            else
            {
                if (!allowNegative) return Fail("INVENTORY.INSUFFICIENT_STOCK", "P4", path, ("item_id", p.Item.ItemId), ("on_hand", ck.Qty), ("requested", q));
                decimal covered = Math.Max(ck.Qty, 0m);
                if (ck.Fifo && consumeLayers)
                {
                    var (v, cons, _) = ConsumeFifo(ck, covered);
                    p.Consumptions.AddRange(cons);
                    value = v + Math.Round(ck.LastUnitCost * (q - covered), lcScale, MidpointRounding.AwayFromZero);
                }
                else value = ck.Qty > 0m ? Math.Round(ck.Value * q / ck.Qty, lcScale, MidpointRounding.AwayFromZero) : Math.Round(ck.LastUnitCost * q, lcScale, MidpointRounding.AwayFromZero);
                Warnings.Add(Fail("INVENTORY.NEGATIVE_STOCK_ISSUED", "P4", path, ("item_id", p.Item.ItemId), ("on_hand", ck.Qty), ("requested", q)) with { Severity = "WARNING" });
            }
            ck.Qty -= q; ck.Value -= value;
            Track(ck);
            p.BaseQuantity = -q; p.ValueLc = -value; p.CostQtyAfter = ck.Qty; p.CostValueAfter = ck.Value;
            return null;
        }

        /// <summary>An inbound movement (5.2, 5.3, 5.4): the value is added to the cost key; a quantity that covers negative stock is valued at the receipt cost and the difference to the value issued is a COST_ADJUST line; FIFO creates a layer for the rest.</summary>
        private Planned? Inbound(Planned p, CostKeyState ck, decimal q, decimal value, bool createLayer, DateOnly layerDate)
        {
            decimal coverQty = 0m, coverCost = 0m, issued = 0m;
            if (ck.Qty < 0m)
            {
                coverQty = Math.Min(q, -ck.Qty);
                coverCost = Math.Round(value * coverQty / q, lcScale, MidpointRounding.AwayFromZero);
                issued = coverQty == -ck.Qty ? -ck.Value : Math.Round(-ck.Value * coverQty / -ck.Qty, lcScale, MidpointRounding.AwayFromZero);
            }
            ck.Qty += q; ck.Value += value;
            p.BaseQuantity = q; p.ValueLc = value; p.CostQtyAfter = ck.Qty; p.CostValueAfter = ck.Value;
            if (createLayer && ck.Fifo && q - coverQty > 0m)
            {
                var layer = new LayerState { DocId = stockId, LineNo = -1, Seq = ++ck.LastSeq, Date = layerDate, Qty = q - coverQty, Value = value - coverCost };
                p.NewLayer = new LayerPlan(layer.Seq, layerDate, layer.Qty, layer.Value);
                ck.Layers.Add(layer);
                ck.LastLayerUnitCost = layer.Value / layer.Qty;
            }
            Planned? variance = null;
            if (coverQty > 0m && issued - coverCost != 0m)
            {
                decimal d = issued - coverCost;
                ck.Value += d;
                variance = new Planned
                {
                    LineRef = p.LineRef, Item = p.Item, MovementType = "COST_ADJUST", CostSource = "COMPUTED", UnitCode = p.UnitCode, RequestIndex = -1, WarehouseId = p.WarehouseId, LocationId = p.LocationId,
                    TrackingId = p.TrackingId, StockStatus = p.StockStatus, CostWarehouseId = p.CostWarehouseId, ValueLc = d, BaseQuantity = 0m, Quantity = 0m, CostQtyAfter = ck.Qty, CostValueAfter = ck.Value
                };
            }
            Track(ck);
            return variance;
        }

        private static string StatusOf(StockLineInput l, string documentType) => (documentType == "STATUS_CHANGE" ? l.FromStatus : l.StockStatus) ?? "AVAILABLE";

        private Planned New(StockLineInput l, ItemRow item, string type, string unitCode, int requestIndex, Guid warehouse, Guid? location, Guid? tracking, string status, string source = "COMPUTED") => new()
        {
            LineNo = Moves.Count + 1, LineRef = l.LineRef, LineText = l.LineText, Item = item, MovementType = type, CostSource = source, UnitCode = unitCode, RequestIndex = requestIndex,
            WarehouseId = warehouse, LocationId = location, TrackingId = tracking, StockStatus = status, CostWarehouseId = costScope == "WAREHOUSE" ? warehouse : null
        };

        private void Add(Planned p, Planned? variance = null)
        {
            p.LineNo = Moves.Count + 1;
            Moves.Add(p);
            TrackAvailable(p);
            if (variance is not null)
            {
                variance.LineNo = Moves.Count + 1;
                variance.StockQtyAfter = p.StockQtyAfter;
                Moves.Add(variance);
            }
        }

        private void PairFor(Planned p, Planned? variance, string debit, string credit, int? creditRequestIndex = null)
        {
            if (p.ValueLc != 0m) Pairs.Add(new JournalPair(debit, credit, Math.Abs(p.ValueLc), p.Item.GroupId, p.WarehouseId, p.WarehouseId, p.Item.ItemId, p.RequestIndex, creditRequestIndex));
            if (variance is { ValueLc: not 0m })
                Pairs.Add(variance.ValueLc > 0m
                    ? new JournalPair("INVENTORY", "NEG_STOCK_VARIANCE", variance.ValueLc, p.Item.GroupId, p.WarehouseId, p.WarehouseId, p.Item.ItemId, p.RequestIndex)
                    : new JournalPair("NEG_STOCK_VARIANCE", "INVENTORY", -variance.ValueLc, p.Item.GroupId, p.WarehouseId, p.WarehouseId, p.Item.ItemId, p.RequestIndex));
        }

        // ---------------------------------------------------------------------------------------------------------
        // the lines
        // ---------------------------------------------------------------------------------------------------------
        public async Task<CommandError?> DeriveAsync(PostStockDocumentRequest req, LineInfo[] infos, StockPostOptions? options, CancellationToken ct)
        {
            for (int i = 0; i < req.Lines.Count; i++)
            {
                StockLineInput l = req.Lines[i];
                LineInfo info = infos[i];
                ItemRow item = info.Item;
                string p = $"/lines/{i}", docType = req.DocumentType;
                bool isReturn = docType == "RETURN";
                string status = StatusOf(l, docType);
                if (l.LocationId is { } fromLoc) locationTypes[(l.WarehouseId, fromLoc)] = info.LocationType;
                if (l.ToWarehouseId is { } toWarehouse && l.ToLocationId is { } toLoc) locationTypes[(toWarehouse, toLoc)] = info.ToLocationType;

                // the lot or serial: a new one is created by the movement that first brings it (3.8)
                Guid? tracking = info.Tracking?.Id;
                if (l.Tracking is { } tr && info.Tracking is null)
                {
                    bool inbound = docType == "RECEIPT" || (docType == "ADJUSTMENT" && (l.Adjustment?.Direction == "IN" || (l.Adjustment?.Mode == "TO_COUNTED" && l.Adjustment.CountedQuantity > 0m))) || (isReturn && options?.ReturnDirection == "IN");
                    if (!inbound) return Fail("CONFIG.INVALID_REFERENCE", "P2", p + "/tracking/tracking_no", ("tracking_no", tr.TrackingNo), ("reason", "an unknown lot or serial can only be received"));
                    if (!newTrackingIds.TryGetValue((item.ItemId, tr.TrackingNo), out Guid created))
                    {
                        created = Guid.NewGuid();
                        newTrackingIds[(item.ItemId, tr.TrackingNo)] = created;
                        newTrackings.Add((item.ItemId, created, item.TrackingMode, tr));
                    }
                    tracking = created;
                }
                DateOnly? expiry = info.Tracking?.Expiry;
                bool serial = item.TrackingMode == "SERIAL";

                if (docType == "RECEIPT" || (isReturn && options?.ReturnDirection == "IN"))
                {
                    CostKeyState ck = await CostAsync(item, l.WarehouseId, tracking, ct);
                    decimal baseQty = l.Quantity * info.Factor, value; decimal? valueTc = null; string source = "COMPUTED";
                    ReturnRef? rr = null;
                    if (isReturn)
                    {
                        rr = options!.ReturnRefs![i];
                        value = ReturnRules.ReturnValue(rr.Value, rr.Quantity, rr.ReturnedValue, rr.ReturnedQuantity, baseQty, lcScale);
                    }
                    else if (options?.LineValues is { } lineValues && lineValues.TryGetValue(i, out decimal orderValue)) { value = orderValue; valueTc = value; }
                    else { value = l.UnitPrice is { } price ? StockValuation.ReceiptValue(l.Quantity, price, lcScale) : 0m; valueTc = l.UnitPrice is null ? null : value; }
                    if (!isReturn && options?.LineSources is { } lineSources && lineSources.TryGetValue(i, out string? named)) source = named;   // a line covered by an invoice (11, 7.1)
                    if (await SerialInAsync(item, tracking, serial, baseQty, p, ct) is { } serialError) return serialError;
                    var key = new StockKey(item.ItemId, l.WarehouseId, l.LocationId, tracking, status);
                    decimal stockAfter = await StockAsync(key, ct) + baseQty; stocks[key] = stockAfter;
                    Planned m = New(l, item, isReturn ? "RETURN_IN" : "RECEIPT", l.UnitCode, i, l.WarehouseId, l.LocationId, tracking, status, source);
                    m.Quantity = l.Quantity; m.ValueTc = valueTc; m.StockQtyAfter = stockAfter;
                    if (rr is not null) { m.RefDocument = rr.DocumentId; m.RefLine = rr.LineNo; }
                    Planned? variance = Inbound(m, ck, baseQty, value, true, date);
                    Add(m, variance);
                    PairFor(m, variance, "INVENTORY", isReturn ? "COGS" : "GR_IR", isReturn ? null : i);
                }
                else if (docType == "ISSUE" || (isReturn && options?.ReturnDirection == "OUT"))
                {
                    if (await OutAsync(req, options, l, info, i, tracking, status, serial, expiry, isReturn ? "RETURN_OUT" : "ISSUE", l.Quantity * info.Factor, l.Quantity, l.UnitCode, null, null, ct) is { } err) return err;
                }
                else if (docType == "ADJUSTMENT")
                {
                    if (l.Adjustment!.Mode == "TO_COUNTED")
                    {
                        var key = new StockKey(item.ItemId, l.WarehouseId, l.LocationId, tracking, status);
                        decimal book = await StockAsync(key, ct);
                        if (book != l.Adjustment.ExpectedBookQuantity) return Fail("INVENTORY.BOOK_QUANTITY_CHANGED", "P4", p + "/adjustment/expected_book_quantity", ("current", book), ("expected", l.Adjustment.ExpectedBookQuantity));
                        decimal diff = l.Adjustment.CountedQuantity!.Value - book;
                        if (Math.Round(diff, item.Scale) != diff) return Fail("INVENTORY.QUANTITY_SCALE_EXCEEDED", "P1", p + "/adjustment/counted_quantity", ("quantity_scale", item.Scale));
                        if (diff == 0m) continue;   // a count without a difference creates no movement (6.2)
                        if (diff > 0m) { if (await AdjustInAsync(l, item, info, diff, i, tracking, status, ct, p) is { } err) return err; }
                        else if (await OutAsync(req, options, l, info, i, tracking, status, serial, expiry, "ADJUST_OUT", -diff, -diff, item.BaseUnit, l.Adjustment.ExpectedBookQuantity, l.Adjustment.CountRef, ct) is { } err2) return err2;
                    }
                    else if (l.Adjustment.Direction == "IN") { if (await AdjustInAsync(l, item, info, l.Quantity * info.Factor, i, tracking, status, ct, p) is { } err) return err; }
                    else if (await OutAsync(req, options, l, info, i, tracking, status, serial, expiry, "ADJUST_OUT", l.Quantity * info.Factor, l.Quantity, l.UnitCode, null, null, ct) is { } err3) return err3;
                }
                else if (docType == "TRANSFER")
                {
                    if (await TransferAsync(l, info, i, tracking, status, serial, p, ct) is { } err) return err;
                }
                else if (docType == "STATUS_CHANGE")
                {
                    if (await StatusChangeAsync(l, info, i, tracking, serial, p, ct) is { } err) return err;
                }
            }
            return null;
        }

        /// <summary>An outbound movement of the document: an issue, an adjustment out or a return to the supplier (5.2 to 5.4).</summary>
        private async Task<CommandError?> OutAsync(
            PostStockDocumentRequest req, StockPostOptions? options, StockLineInput l, LineInfo info, int i, Guid? tracking, string status, bool serial, DateOnly? expiry, string type,
            decimal baseQty, decimal quantity, string unit, decimal? expectedBook, string? countRef, CancellationToken ct)
        {
            ItemRow item = info.Item;
            string p = $"/lines/{i}", docType = req.DocumentType;
            bool isReturn = type == "RETURN_OUT";
            var key = new StockKey(item.ItemId, l.WarehouseId, l.LocationId, tracking, status);
            if (serial && baseQty != 1m) return Fail("INVENTORY.SERIAL_QUANTITY_INVALID", "P2", p + "/quantity", ("base_quantity", baseQty));
                        if (docType == "ISSUE" && info.Tracking is { Status: "RECALLED" }) return Fail("INVENTORY.TRACKING_RECALLED", "P4", p + "/tracking", ("tracking_no", info.Tracking.No));
            CostKeyState ck = await CostAsync(item, l.WarehouseId, tracking, ct);
            Planned m = New(l, item, type, unit, i, l.WarehouseId, l.LocationId, tracking, status);
            m.Quantity = -quantity; m.ExpectedBook = expectedBook; m.CountRef = countRef;
            if (isReturn)
            {
                ReturnRef rr = options!.ReturnRefs![i];
                if (ck.Fifo)
                {
                    // a return to the supplier takes the layer of the original receipt (5.3), at its cost
                    LayerState? layer = ck.Layers.FirstOrDefault(x => x.DocId == rr.DocumentId && x.LineNo == rr.LineNo);
                    if (layer is null || layer.RemQty < baseQty) return Fail("INVENTORY.RECEIPT_CONSUMED", "P4", p + "/quantity", ("item_id", item.ItemId), ("on_hand", layer?.RemQty ?? 0m), ("requested", baseQty));
                    decimal v = baseQty == layer.RemQty ? layer.RemValue : Math.Round(layer.RemValue * baseQty / layer.RemQty, lcScale, MidpointRounding.AwayFromZero);
                    layer.ConsQty += baseQty; layer.ConsValue += v;
                    m.Consumptions.Add(new ConsumptionPlan(layer.DocId, layer.LineNo, baseQty, v));
                    ck.Qty -= baseQty; ck.Value -= v; Track(ck);
                    m.BaseQuantity = -baseQty; m.ValueLc = -v; m.CostQtyAfter = ck.Qty; m.CostValueAfter = ck.Value;
                }
                else
                {
                    decimal v = ReturnRules.ReturnValue(rr.Value, rr.Quantity, rr.ReturnedValue, rr.ReturnedQuantity, baseQty, lcScale);
                    decimal q2 = ck.Qty - baseQty, v2 = ck.Value - v;
                    if (q2 < 0m || v2 < 0m || (q2 == 0m && v2 != 0m)) return Fail("INVENTORY.RECEIPT_CONSUMED", "P4", p + "/quantity", ("item_id", item.ItemId), ("on_hand", ck.Qty), ("requested", baseQty));
                    ck.Qty = q2; ck.Value = v2; Track(ck);
                    m.BaseQuantity = -baseQty; m.ValueLc = -v; m.CostQtyAfter = q2; m.CostValueAfter = v2;
                }
                m.RefDocument = rr.DocumentId; m.RefLine = rr.LineNo;
                if (await StockOutAsync(key, baseQty, serial, p, ct, true) is { } stockError) return stockError;
            }
            else
            {
                bool allow = !serial && await AllowNegativeAsync(item.ItemId, l.WarehouseId, ct);
                if (Outbound(m, ck, baseQty, allow, true, p + "/quantity") is { } error) return error;
                if (await StockOutAsync(key, baseQty, serial, p, ct, false) is { } stockError) return stockError;
            }
            if (serial) serials[(item.ItemId, tracking!.Value)] = await SerialOnHandAsync(item.ItemId, tracking.Value, ct) - baseQty;
            m.StockQtyAfter = stocks[key];
            if (docType == "ISSUE" && expiry is { } exp && exp < date)
            {
                m.LotExpired = true;
                Warnings.Add(Fail("INVENTORY.LOT_EXPIRED", "P4", p + "/tracking", ("tracking_no", info.Tracking!.No), ("expiry_date", exp)) with { Severity = "WARNING" });
            }
            if (await ReservationGuardAsync(m, baseQty, status, info.LocationType, l.ReservationId, p, ct) is { } reserved) return reserved;
            Add(m);
            (string debit, string credit) = type switch { "ISSUE" => (StockValuation.IssueRole(req.ReasonCode), "INVENTORY"), "RETURN_OUT" => ("GR_IR", "INVENTORY"), _ => ("ADJUSTMENT_LOSS", "INVENTORY") };
            PairFor(m, null, debit, credit);
            return null;
        }


        /// <summary>The stock key of an outbound movement: a transfer or a status change must find the stock in its key; an issue or an adjustment out only tests the cost key (5.4).</summary>
        private async Task<CommandError?> StockOutAsync(StockKey key, decimal baseQty, bool serial, string path, CancellationToken ct, bool strict, string strictCode = "INVENTORY.RECEIPT_CONSUMED")
        {
            decimal on = await StockAsync(key, ct);
            if (strict && on < baseQty) return Fail(strictCode, "P4", path + "/quantity", ("item_id", key.Item), ("on_hand", on), ("requested", baseQty));
            if (serial && on < 1m) return Fail("INVENTORY.INSUFFICIENT_STOCK", "P4", path + "/quantity", ("item_id", key.Item), ("on_hand", on), ("requested", baseQty));
            stocks[key] = on - baseQty;
            return null;
        }

        private async Task<CommandError?> SerialInAsync(ItemRow item, Guid? tracking, bool serial, decimal baseQty, string path, CancellationToken ct)
        {
            if (!serial) return null;
            if (baseQty != 1m) return Fail("INVENTORY.SERIAL_QUANTITY_INVALID", "P2", path + "/quantity", ("base_quantity", baseQty));
            decimal on = await SerialOnHandAsync(item.ItemId, tracking!.Value, ct);
            if (on != 0m) return Fail("INVENTORY.SERIAL_ON_HAND", "P4", path + "/tracking", ("item_id", item.ItemId), ("on_hand", on));
            serials[(item.ItemId, tracking.Value)] = on + baseQty;
            return null;
        }

        /// <summary>An adjustment in (5.2, 5.3): a given price is a supplied value, else the average (moving average) or the unit cost of the last layer (FIFO); a cost key without either needs a value.</summary>
        private async Task<CommandError?> AdjustInAsync(StockLineInput l, ItemRow item, LineInfo info, decimal baseQty, int i, Guid? tracking, string status, CancellationToken ct, string p)
        {
            CostKeyState ck = await CostAsync(item, l.WarehouseId, tracking, ct);
            bool counted = l.Adjustment!.Mode == "TO_COUNTED";
            decimal quantity = counted ? baseQty : l.Quantity; string unit = counted ? item.BaseUnit : l.UnitCode;
            decimal value; string source = "COMPUTED"; decimal? valueTc = null;
            if (l.UnitPrice is { } suppliedPrice) { value = StockValuation.ReceiptValue(quantity, suppliedPrice, lcScale); source = "SUPPLIED"; valueTc = value; }
            else if (ck.Fifo && ck.LastLayerUnitCost is { } lastUnit) value = Math.Round(lastUnit * baseQty, lcScale, MidpointRounding.AwayFromZero);
            else if (!ck.Fifo && ck.Qty > 0m) value = Math.Round(ck.Value * baseQty / ck.Qty, lcScale, MidpointRounding.AwayFromZero);
            else return Fail("INVENTORY.VALUE_REQUIRED", "P4", p + "/unit_price", ("item_id", item.ItemId));
            if (await SerialInAsync(item, tracking, item.TrackingMode == "SERIAL", baseQty, p, ct) is { } serialError) return serialError;
            var key = new StockKey(item.ItemId, l.WarehouseId, l.LocationId, tracking, status);
            decimal stockAfter = await StockAsync(key, ct) + baseQty; stocks[key] = stockAfter;
            Planned m = New(l, item, "ADJUST_IN", unit, i, l.WarehouseId, l.LocationId, tracking, status, source);
            m.Quantity = quantity; m.ValueTc = valueTc; m.StockQtyAfter = stockAfter;
            if (counted) { m.ExpectedBook = l.Adjustment.ExpectedBookQuantity; m.CountRef = l.Adjustment.CountRef; }
            Planned? variance = Inbound(m, ck, baseQty, value, true, date);
            Add(m, variance);
            PairFor(m, variance, "INVENTORY", "ADJUSTMENT_GAIN");
            return null;
        }

        /// <summary>A transfer (4.2, 5.2, 5.3): TRANSFER_OUT and TRANSFER_IN at the same value. With cost scope COMPANY the cost key does not change and no layer moves. With cost scope WAREHOUSE a FIFO transfer consumes the layers of the source and creates one layer per consumed layer in the destination, at its cost and date.</summary>
        private async Task<CommandError?> TransferAsync(StockLineInput l, LineInfo info, int i, Guid? tracking, string status, bool serial, string p, CancellationToken ct)
        {
            ItemRow item = info.Item;
            Guid toWh = l.ToWarehouseId!.Value;
            decimal baseQty = l.Quantity * info.Factor;
            var from = new StockKey(item.ItemId, l.WarehouseId, l.LocationId, tracking, status);
            var to = new StockKey(item.ItemId, toWh, l.ToLocationId, tracking, status);
            if (await StockOutAsync(from, baseQty, serial, p, ct, true, "INVENTORY.INSUFFICIENT_STOCK") is { } stockError) return stockError;
            CostKeyState src = await CostAsync(item, l.WarehouseId, tracking, ct), dst = await CostAsync(item, toWh, tracking, ct);
            Planned outMove = New(l, item, "TRANSFER_OUT", l.UnitCode, i, l.WarehouseId, l.LocationId, tracking, status);
            outMove.Quantity = -l.Quantity;
            bool movesLayers = src.Fifo && costScope == "WAREHOUSE";
            if (src.Qty < baseQty) return Fail("INVENTORY.INSUFFICIENT_STOCK", "P4", p + "/quantity", ("item_id", item.ItemId), ("on_hand", src.Qty), ("requested", baseQty));
            var sourceLayers = movesLayers ? src.Layers.Select(x => (x.DocId, x.LineNo, x.Date)).ToList() : [];
            if (Outbound(outMove, src, baseQty, false, movesLayers, p + "/quantity") is { } error) return error;
            outMove.StockQtyAfter = stocks[from];
            if (await ReservationGuardAsync(outMove, baseQty, status, info.LocationType, null, p, ct) is { } reserved) return reserved;
            Add(outMove);
            decimal total = -outMove.ValueLc;
            var ins = new List<(decimal Qty, decimal Value, DateOnly Date, bool Split)>();
            if (movesLayers)
                foreach (ConsumptionPlan c in outMove.Consumptions) ins.Add((c.Quantity, c.Value, sourceLayers.First(x => x.DocId == c.LayerDoc && x.LineNo == c.LayerLine).Date, true));
            else ins.Add((baseQty, total, date, false));
            decimal stockAfter = await StockAsync(to, ct);
            foreach (var part in ins)
            {
                stockAfter += part.Qty; stocks[to] = stockAfter;
                Planned inMove = New(l, item, "TRANSFER_IN", part.Split ? item.BaseUnit : l.UnitCode, i, toWh, l.ToLocationId, tracking, status);
                inMove.Quantity = part.Split ? part.Qty : l.Quantity; inMove.StockQtyAfter = stockAfter;
                Planned? variance = Inbound(inMove, dst, part.Qty, part.Value, movesLayers, part.Date);
                Add(inMove, variance);
                if (inMove.ValueLc != 0m)
                    Pairs.Add(new JournalPair("INVENTORY", "INVENTORY", inMove.ValueLc, item.GroupId, toWh, l.WarehouseId, item.ItemId, i, null, true));
                if (variance is { ValueLc: not 0m })
                    Pairs.Add(variance.ValueLc > 0m
                        ? new JournalPair("INVENTORY", "NEG_STOCK_VARIANCE", variance.ValueLc, item.GroupId, toWh, toWh, item.ItemId, i)
                        : new JournalPair("NEG_STOCK_VARIANCE", "INVENTORY", -variance.ValueLc, item.GroupId, toWh, toWh, item.ItemId, i));
            }
            return null;
        }

        /// <summary>A status change (4.2): STATUS_OUT and STATUS_IN on the same warehouse, location and lot or serial, without effect on the cost key.</summary>
        private async Task<CommandError?> StatusChangeAsync(StockLineInput l, LineInfo info, int i, Guid? tracking, bool serial, string p, CancellationToken ct)
        {
            ItemRow item = info.Item;
            decimal baseQty = l.Quantity * info.Factor;
            var from = new StockKey(item.ItemId, l.WarehouseId, l.LocationId, tracking, l.FromStatus!);
            var to = new StockKey(item.ItemId, l.WarehouseId, l.LocationId, tracking, l.ToStatus!);
            if (await StockOutAsync(from, baseQty, serial, p, ct, true, "INVENTORY.INSUFFICIENT_STOCK") is { } stockError) return stockError;
            CostKeyState ck = await CostAsync(item, l.WarehouseId, tracking, ct);
            Planned outMove = New(l, item, "STATUS_OUT", l.UnitCode, i, l.WarehouseId, l.LocationId, tracking, l.FromStatus!);
            outMove.Quantity = -l.Quantity; outMove.BaseQuantity = -baseQty; outMove.CostQtyAfter = ck.Qty; outMove.CostValueAfter = ck.Value; outMove.StockQtyAfter = stocks[from];
            if (await ReservationGuardAsync(outMove, baseQty, l.FromStatus!, info.LocationType, null, p, ct) is { } reserved) return reserved;
            Add(outMove);
            decimal after = await StockAsync(to, ct) + baseQty; stocks[to] = after;
            Planned inMove = New(l, item, "STATUS_IN", l.UnitCode, i, l.WarehouseId, l.LocationId, tracking, l.ToStatus!);
            inMove.Quantity = l.Quantity; inMove.BaseQuantity = baseQty; inMove.CostQtyAfter = ck.Qty; inMove.CostValueAfter = ck.Value; inMove.StockQtyAfter = after;
            Add(inMove);
            return null;
        }

        // ---------------------------------------------------------------------------------------------------------
        // what the document creates beside its movements
        // ---------------------------------------------------------------------------------------------------------
        /// <summary>The tracking rows of the lots and serials that the document brings for the first time (3.8): created in the transaction of the movement.</summary>
        public async Task CreateTrackingAsync(CommandContext ctx, CancellationToken ct)
        {
            foreach (var (item, id, kind, t) in newTrackings)
            {
                await using var cmd = tx.Command(
                    "insert into core.item_tracking (tenant_id, item_id, tracking_id, tracking_no, kind, expiry_date, production_date, supplier_ref, created_by, updated_by) values (@t, @i, @id, @n, @k, @e, @p, @s, @by, @by)");
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("i", item); cmd.Parameters.AddWithValue("id", id); cmd.Parameters.AddWithValue("n", t.TrackingNo);
                cmd.Parameters.AddWithValue("k", kind); cmd.Parameters.AddWithValue("by", ctx.Actor);
                cmd.Parameters.Add(new NpgsqlParameter("e", NpgsqlDbType.Date) { Value = (object?)t.ExpiryDate ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Date) { Value = (object?)t.ProductionDate ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Text) { Value = (object?)t.SupplierRef ?? DBNull.Value });
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }

        private bool Standard(Guid warehouse, Guid? location) => location is null || locationTypes.GetValueOrDefault((warehouse, location.Value)) is null or "STANDARD";

        /// <summary>What a movement does to the available stock (4.5): AVAILABLE stock outside TRANSIT and QUARANTINE locations, of the warehouse and of its lot or serial.</summary>
        private void TrackAvailable(Planned p)
        {
            if (p.StockStatus != "AVAILABLE" || !Standard(p.WarehouseId, p.LocationId) || p.BaseQuantity == 0m) return;
            availableDelta[(p.Item.ItemId, p.WarehouseId, null)] = availableDelta.GetValueOrDefault((p.Item.ItemId, p.WarehouseId, (Guid?)null)) + p.BaseQuantity;
            if (p.TrackingId is { } t) availableDelta[(p.Item.ItemId, p.WarehouseId, t)] = availableDelta.GetValueOrDefault((p.Item.ItemId, p.WarehouseId, (Guid?)t)) + p.BaseQuantity;
        }

        /// <summary>
        /// An outbound movement must not eat into reserved stock (4.5, INVENTORY.STOCK_RESERVED) unless it is the reserving demand: a reservation named by the line (reservation_id) is consumed by
        /// the quantity of the movement, up to what it holds, and what it holds is then not reserved against this movement. The check is on the warehouse and, for a lot or serial, on the lot.
        /// </summary>
        private async Task<CommandError?> ReservationGuardAsync(Planned p, decimal q, string status, string? locationType, Guid? reservationId, string path, CancellationToken ct)
        {
            Guid item = p.Item.ItemId, wh = p.WarehouseId;
            if (reservationId is not null && (status != "AVAILABLE" || locationType is "TRANSIT" or "QUARANTINE"))
                return Fail("INVENTORY.LOCATION_NOT_AVAILABLE", "P4", path + "/reservation_id", ("reason", "a reservation is consumed from available stock"));
            if (status != "AVAILABLE" || locationType is "TRANSIT" or "QUARANTINE") return null;
            decimal consumedNow = 0m;
            if (reservationId is { } rid)
            {
                var held = await ReservationCommands.ReadForIssueAsync(tx, tenantId, companyId, rid, ct);
                if (held is null || held.Value.Item != item || held.Value.Warehouse != wh || (held.Value.Tracking is not null && held.Value.Tracking != p.TrackingId))
                    return Fail("CONFIG.INVALID_REFERENCE", "P2", path + "/reservation_id", ("reservation_id", rid));
                decimal left = held.Value.Reserved - consumedBy.GetValueOrDefault(rid);
                consumedNow = Math.Min(q, Math.Max(left, 0m));
                if (consumedNow > 0m)
                {
                    consumedBy[rid] = consumedBy.GetValueOrDefault(rid) + consumedNow;
                    consumes.Add((rid, consumedNow, item, wh, held.Value.DemandType, held.Value.DemandRef));
                    consumedAll[(item, wh)] = consumedAll.GetValueOrDefault((item, wh)) + consumedNow;
                    if (held.Value.Tracking is { } lot) consumedLot[(item, wh, lot)] = consumedLot.GetValueOrDefault((item, wh, lot)) + consumedNow;
                }
            }
            decimal onHandAfter = await ReservationCommands.OnHandAvailableAsync(tx, tenantId, companyId, item, wh, null, ct) + availableDelta.GetValueOrDefault((item, wh, (Guid?)null)) - q;
            decimal reservedAfter = await ReservationCommands.ReservedAsync(tx, tenantId, companyId, item, wh, null, false, ct) - consumedAll.GetValueOrDefault((item, wh));
            if (reservedAfter > 0m && onHandAfter < reservedAfter) return Fail("INVENTORY.STOCK_RESERVED", "P4", path + "/quantity", ("item_id", item), ("available", onHandAfter + q - reservedAfter), ("requested", q));
            if (p.TrackingId is { } tr)
            {
                decimal lotAfter = await ReservationCommands.OnHandAvailableAsync(tx, tenantId, companyId, item, wh, tr, ct) + availableDelta.GetValueOrDefault((item, wh, (Guid?)tr)) - q;
                decimal lotReserved = await ReservationCommands.ReservedAsync(tx, tenantId, companyId, item, wh, tr, true, ct) - consumedLot.GetValueOrDefault((item, wh, tr));
                if (lotReserved > 0m && lotAfter < lotReserved) return Fail("INVENTORY.STOCK_RESERVED", "P4", path + "/quantity", ("item_id", item), ("tracking_id", tr), ("requested", q));
            }
            return null;
        }

        /// <summary>The events of the reservations that the document consumed (6.5, 15, 6): CONSUME with the stock document, and reservation.consumed.</summary>
        public async Task WriteReservationEventsAsync(CommandContext ctx, DateTime at, CancellationToken ct)
        {
            foreach (var c in consumes)
            {
                await ReservationCommands.AppendEventAsync(tx, tenantId, companyId, c.Reservation, "CONSUME", -c.Quantity, ctx.Actor, null, stockId, ct);
                await ReservationCommands.EventAsync(tx, tenantId, companyId, ctx, "reservation.consumed", c.Reservation, "STATUS", c.Item, c.Warehouse, -c.Quantity, c.DemandType == "ORDER_LINE" ? c.DemandRef : null, ct);
            }
        }
    }
}
