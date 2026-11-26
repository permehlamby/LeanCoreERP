#nullable disable warnings
using System.Globalization;
using System.Text.Json;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Hashing;
using FinanceCore.Core.Masters;
using FinanceCore.Core.Posting;
using Npgsql;
using NpgsqlTypes;

namespace FinanceCore.Data;

/// <summary>
/// landed-costs:allocate and landed-costs:reverse (10, 4.7, 6.6, 6.8, permission landed_cost.allocate). An allocation spreads an amount of a debit line on LANDED_COST_CLEARING (a freight or duty
/// charge of a supplier invoice) over receipt lines by VALUE, QUANTITY, WEIGHT or VOLUME, with the remainder on the last target. Per target the part that is still on hand (FIFO: the remaining
/// quantity of the layer of the receipt line, moving average: the lesser of the received quantity and the cost key) raises the cost of the stock by a COST_ADJUST movement of a STOCK_COST_ADJUSTMENT
/// document (a FIFO layer through stock_layer_adjustment), and the part that has been issued goes to COGS: debit INVENTORY and COGS, credit LANDED_COST_CLEARING. The reversal is the mirror
/// document and journal of the allocation, only if no movement has come on the cost key of any target since (INVENTORY.ALLOCATION_NOT_REVERSIBLE). Built choices: a line of the document for
/// every target also when its on-hand part is zero (a document has at least one movement), and the remaining amount of the source is its amount less the allocations (the mirror allocations
/// restore it).
/// </summary>
public sealed partial class StockCommands
{
    public const string LandedCostPermission = LandedCostRules.Permission;
    private const string CostAdjustmentSeries = "STOCK_DOC_COST_ADJUSTMENT";

    private sealed record Target(int Index, Guid DocumentId, int LineNo, Guid Item, Guid Warehouse, Guid? Location, Guid? Tracking, string Status, Guid? CostWarehouse, decimal Quantity, decimal Value,
        decimal? Weight, decimal? Volume, string BaseUnit);

    private async Task<CommandResult<LandedCostResult>> RunLandedAsync(
        Guid tenantId, CommandContext ctx, string command, byte[] fingerprint, Func<TenantTransaction, Task<CommandResult<LandedCostResult>>> work, CancellationToken ct)
    {
        var shape = MasterSupport.ValidateContext(ctx);
        if (shape.Count > 0) return CommandResult<LandedCostResult>.Rejected(shape);
        return await MasterSupport.RunAsync(async () =>
        {
            await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Stock, ct: ct);
            var earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
            if (earlier is not null)
            {
                if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint)) return CommandResult<LandedCostResult>.Rejected([Error("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key")]);
                return earlier.EntityId is null
                    ? CommandResult<LandedCostResult>.Failed(Error("SYSTEM.REPLAY_RESULT_MISSING", "P1", ""))
                    : CommandResult<LandedCostResult>.Ok(await LoadLandedAsync(tx, tenantId, earlier.EntityId.Value, ct), earlier.CommandLogId, replay: true);
            }
            return await work(tx);
        });
    }

    /// <summary>The allocation of a stock document (the document of the allocation or of its mirror).</summary>
    private static async Task<LandedCostResult> LoadLandedAsync(TenantTransaction tx, Guid tenantId, Guid stockDocumentId, CancellationToken ct)
    {
        Guid id, doc; Guid? journal; decimal total; string number;
        await using (var cmd = tx.Command(
            "select a.allocation_id, a.stock_document_id, d.journal_document_id, a.total_cost_lc, d.document_number from core.landed_cost_allocation a join core.stock_document d on d.tenant_id = a.tenant_id and d.company_id = a.company_id " +
            "and d.stock_document_id = a.stock_document_id where a.tenant_id = @t and a.stock_document_id = @d"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("d", stockDocumentId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            id = r.GetGuid(0); doc = r.GetGuid(1); journal = r.IsDBNull(2) ? null : r.GetGuid(2); total = r.GetDecimal(3); number = r.GetString(4);
        }
        var lines = new List<LandedCostLineResult>();
        await using (var cmd = tx.Command("select line_no, receipt_document_id, receipt_line_no, allocated_cost_lc, on_hand_part_lc, consumed_part_lc from core.landed_cost_allocation_line where tenant_id = @t and allocation_id = @a order by line_no"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("a", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) lines.Add(new LandedCostLineResult(r.GetInt32(0), r.GetGuid(1), r.GetInt32(2), r.GetDecimal(3), r.GetDecimal(4), r.GetDecimal(5)));
        }
        return new LandedCostResult(id, doc, number, journal, total, lines);
    }

    public async Task<CommandResult<LandedCostResult>> AllocateLandedCostAsync(Guid tenantId, CommandContext ctx, AllocateLandedCostRequest req, CancellationToken ct = default)
    {
        static CommandResult<LandedCostResult> Fail(CommandError e) => CommandResult<LandedCostResult>.Rejected([e]);
        if (!ctx.Permissions.Contains(LandedCostPermission)) return Fail(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", LandedCostPermission)));
        var errors = new List<CommandError>();
        void Invalid(string path, string reason) => errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", path, ("reason", reason)));
        if (req.CompanyId == Guid.Empty) Invalid("/company_id", "required");
        if (req.CostSource is null || req.CostSource.DocumentId == Guid.Empty || req.CostSource.LineNo < 1) Invalid("/cost_source", "document_id and line_no are required");
        if (!LandedCostRules.Methods.Contains(req.AllocationMethod ?? "")) Invalid("/allocation_method", string.Join(", ", LandedCostRules.Methods));
        if (req.Targets is null or { Count: 0 } || req.Targets.Count > LandedCostRules.MaxTargets) Invalid("/targets", $"1 to {LandedCostRules.MaxTargets} receipt lines");
        else if (req.Targets.Select(t => (t.StockDocumentId, t.LineNo)).Distinct().Count() != req.Targets.Count) Invalid("/targets", "each receipt line once");
        if (req.AmountLc is <= 0m || (req.AmountLc is { } a && Math.Round(a, 4) != a)) Invalid("/amount_lc", "more than 0, at most 4 fraction digits");
        errors.AddRange(MasterSupport.ValidateContext(ctx));
        if (errors.Count > 0) return CommandResult<LandedCostResult>.Rejected(errors);
        byte[] fingerprint = LandedCostRules.Fingerprint("landed-costs:allocate", $"{req.CompanyId:D}|{req.CostSource.DocumentId:D}/{req.CostSource.LineNo}|{req.AllocationMethod}|{req.PostingDate:yyyy-MM-dd}|{req.AmountLc?.ToString(CultureInfo.InvariantCulture)}|" +
            string.Join(',', req.Targets.Select(t => $"{t.StockDocumentId:D}/{t.LineNo}")));

        return await RunLandedAsync(tenantId, ctx, "landed-costs:allocate", fingerprint, async tx =>
        {
            Guid companyId = req.CompanyId;
            var company = await JournalDocumentPoster.ReadCompanyAsync(tx, tenantId, companyId, ct);
            if (company is null) return Fail(Error("CONFIG.INVALID_REFERENCE", "P2", "/company_id", ("company_id", companyId)));
            if (company.Status != "ACTIVE") return Fail(Error("COMPANY.NOT_ACTIVE", "P2", "/company_id", ("status", company.Status)));

            // P2: the cost line, what is left of it, and the receipt lines
            decimal sourceAmount;
            await using (var cmd = tx.Command("select l.amount_lc, l.account_id from core.journal_line l where l.tenant_id = @t and l.company_id = @c and l.document_id = @d and l.line_no = @n"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", req.CostSource.DocumentId); cmd.Parameters.AddWithValue("n", req.CostSource.LineNo);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) return Fail(Error("CONFIG.INVALID_REFERENCE", "P2", "/cost_source", ("document_id", req.CostSource.DocumentId), ("line_no", req.CostSource.LineNo)));
                sourceAmount = r.GetDecimal(0);
                Guid account = r.GetGuid(1);
                await r.CloseAsync();
                await using var check = tx.Command("select 1 from core.account_determination where tenant_id = @t and company_id = @c and role_code = 'LANDED_COST_CLEARING' and account_id = @a");
                check.Parameters.AddWithValue("t", tenantId); check.Parameters.AddWithValue("c", companyId); check.Parameters.AddWithValue("a", account);
                if (sourceAmount <= 0m || await check.ExecuteScalarAsync(ct) is null)
                    return Fail(Error("CONFIG.INVALID_REFERENCE", "P2", "/cost_source", ("reason", "a debit line on an account of the role LANDED_COST_CLEARING")));
            }
            decimal allocatedBefore;
            await using (var cmd = tx.Command("select coalesce(sum(total_cost_lc), 0) from core.landed_cost_allocation where tenant_id = @t and company_id = @c and cost_source_document_id = @d and cost_source_line_no = @n"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", req.CostSource.DocumentId); cmd.Parameters.AddWithValue("n", req.CostSource.LineNo);
                allocatedBefore = (decimal)(await cmd.ExecuteScalarAsync(ct))!;
            }
            decimal amount = req.AmountLc ?? sourceAmount - allocatedBefore;
            if (amount <= 0m || amount > sourceAmount - allocatedBefore)
                return Fail(Error("INVENTORY.ALLOCATION_EXCEEDS_SOURCE", "P2", "/amount_lc", ("remaining", sourceAmount - allocatedBefore), ("requested", amount)));

            var targets = new List<Target>();
            var items = new Dictionary<Guid, ItemRow>();
            for (int i = 0; i < req.Targets.Count; i++)
            {
                LandedCostTarget t = req.Targets[i];
                await using var cmd = tx.Command(
                    "select m.item_id, m.warehouse_id, m.location_id, m.tracking_id, m.stock_status, m.cost_warehouse_id, m.movement_type, m.base_quantity, m.value_lc, d.status, d.document_type, i.weight_kg, i.volume_m3, i.base_unit " +
                    "from core.stock_movement m join core.stock_document d on d.tenant_id = m.tenant_id and d.company_id = m.company_id and d.stock_document_id = m.stock_document_id " +
                    "join core.item i on i.tenant_id = m.tenant_id and i.item_id = m.item_id where m.tenant_id = @t and m.company_id = @c and m.stock_document_id = @d and m.line_no = @n");
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", t.StockDocumentId); cmd.Parameters.AddWithValue("n", t.LineNo);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) return Fail(Error("CONFIG.INVALID_REFERENCE", "P2", $"/targets/{i}", ("stock_document_id", t.StockDocumentId), ("line_no", t.LineNo)));
                if (r.GetString(6).Trim() != "RECEIPT" || r.GetDecimal(7) <= 0m || r.GetString(9).Trim() != "posted" || r.GetString(10).Trim() is not ("STOCK_RECEIPT"))
                    return Fail(Error("CONFIG.INVALID_REFERENCE", "P2", $"/targets/{i}", ("reason", "a receipt line of a stock document that is not reversed")));
                targets.Add(new Target(i, t.StockDocumentId, t.LineNo, r.GetGuid(0), r.GetGuid(1), r.IsDBNull(2) ? null : r.GetGuid(2), r.IsDBNull(3) ? null : r.GetGuid(3), r.GetString(4).Trim(), r.IsDBNull(5) ? null : r.GetGuid(5),
                    r.GetDecimal(7), r.GetDecimal(8), r.IsDBNull(11) ? null : r.GetDecimal(11), r.IsDBNull(12) ? null : r.GetDecimal(12), r.GetString(13).Trim()));
            }
            foreach (Guid itemId in targets.Select(t => t.Item).Distinct())
            {
                ItemRow? item = await ReadItemAsync(tx, tenantId, companyId, itemId, ct);
                if (item is null || !item.Activated) return Fail(Error("CONFIG.INVALID_REFERENCE", "P2", "/targets", ("item_id", itemId)));
                items[itemId] = item;
            }
            decimal[] basis = targets.Select(t => req.AllocationMethod switch { "VALUE" => t.Value, "QUANTITY" => t.Quantity, "WEIGHT" => t.Quantity * (t.Weight ?? 0m), _ => t.Quantity * (t.Volume ?? 0m) }).ToArray();
            if (basis.Any(b => b <= 0m)) return Fail(Error("INVENTORY.ALLOCATION_BASIS_MISSING", "P2", "/targets", ("allocation_method", req.AllocationMethod)));

            // P3: the finance calendar, the series, the group currency
            var period = await JournalDocumentPoster.CheckPeriodAsync(tx, tenantId, companyId, req.PostingDate, ctx, false, ct);
            if (period.Errors.Count > 0) return CommandResult<LandedCostResult>.Rejected(period.Errors.Select(e => e with { Path = "/posting_date" }).ToList());
            Guid ledgerId = await ClearingPoster.ReadDefaultLedgerAsync(tx, tenantId, companyId, ct) ?? Guid.Empty;
            var docSeries = await JournalDocumentPoster.ReadSeriesAsync(tx, tenantId, companyId, CostAdjustmentSeries, req.PostingDate, ledgerId, ct);
            if (docSeries is null) return Fail(Error("SERIES.NO_ASSIGNMENT", "P2", "/posting_date", ("document_type", CostAdjustmentSeries), ("posting_date", req.PostingDate)));
            var journalSeries = await JournalDocumentPoster.ReadSeriesAsync(tx, tenantId, companyId, "STOCK_COST_ADJUSTMENT", req.PostingDate, ledgerId, ct);
            if (journalSeries is null) return Fail(Error("SERIES.NO_ASSIGNMENT", "P2", "/posting_date", ("document_type", "STOCK_COST_ADJUSTMENT"), ("posting_date", req.PostingDate)));
            string? groupCurrency = await ClearingPoster.ReadGroupCurrencyAsync(tx, tenantId, companyId, req.PostingDate, ct);
            if (groupCurrency is null) return Fail(Error("CONFIG.GROUP_MEMBERSHIP_MISSING", "P2", "/company_id", ("posting_date", req.PostingDate)));
            int fiscalYear = period.FiscalYear!.Value, periodNo = period.PeriodNo!.Value;

            // P7: the locks of 6.4: the item rows, the counters, the LEDGER and STOCK heads, the period row
            await using (var cmd = tx.Command("select item_id from core.item_company where tenant_id = @t and company_id = @c and item_id = any(@ids) order by item_id for update"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("ids", items.Keys.ToArray());
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) { }
            }
            if (await ClosingDocuments.LockCountersAsync(tx, tenantId, companyId, [(docSeries, fiscalYear), (journalSeries, fiscalYear)], ct) is { } missing) return Fail(missing);
            ClosingLedger ledger = (await ClosingDocuments.OpenLedgerAsync(tx, tenantId, companyId, ledgerId, company, groupCurrency, ct))!;
            var (stockSeq, stockPrev, _) = await CommandSupport.LockOrCreateHeadAsync(tx, tenantId, companyId, StockHash2.ChainType, "", LedgerHash2.Genesis(tenantId, companyId, StockHash2.ChainType, ""), ct);
            var recheck = await JournalDocumentPoster.CheckPeriodAsync(tx, tenantId, companyId, req.PostingDate, ctx, true, ct);
            if (recheck.Errors.Count > 0) return CommandResult<LandedCostResult>.Rejected(recheck.Errors.Select(e => e with { Path = "/posting_date" }).ToList());
            // what is left of the cost line, read again under the locks
            await using (var cmd = tx.Command("select coalesce(sum(total_cost_lc), 0) from core.landed_cost_allocation where tenant_id = @t and company_id = @c and cost_source_document_id = @d and cost_source_line_no = @n"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", req.CostSource.DocumentId); cmd.Parameters.AddWithValue("n", req.CostSource.LineNo);
                if (amount > sourceAmount - (decimal)(await cmd.ExecuteScalarAsync(ct))!) return Fail(Error("INVENTORY.ALLOCATION_EXCEEDS_SOURCE", "P7", "/amount_lc", ("requested", amount)));
            }

            // P4 to P6: the split, the on-hand part of every target and the cost adjustment lines
            int lcScale = await ScaleOfAsync(tx, company.LocalCurrency, ct);
            decimal[] shares = LandedCostRules.Split(amount, basis, lcScale);
            Guid stockId = Guid.NewGuid(), allocationId = Guid.NewGuid();
            var cost = new Dictionary<(Guid, Guid?, Guid?), (decimal Qty, decimal Value)>();
            var moves = new List<Derivation.Planned>();
            var pairs = new List<Derivation.JournalPair>();
            var lineResults = new List<(Target T, decimal Share, decimal OnHand, decimal Consumed, (Guid Doc, int Line)? Layer)>();
            foreach (var (t, share) in targets.Zip(shares))
            {
                ItemRow item = items[t.Item];
                var ckey = (t.Item, t.CostWarehouse, t.Tracking);
                if (!cost.ContainsKey(ckey)) cost[ckey] = await CostBalanceAsync(tx, tenantId, companyId, t.Item, t.CostWarehouse, t.Tracking, ct);
                decimal onHandQty; (Guid, int)? layer = null;
                if (item.Valuation == "FIFO")
                {
                    await using var cmd = tx.Command(
                        "select l.quantity_base - coalesce((select sum(c.quantity_base) from core.stock_layer_consumption c where c.tenant_id = l.tenant_id and c.company_id = l.company_id and c.layer_doc_id = l.stock_document_id and c.layer_line_no = l.line_no), 0) " +
                        "from core.stock_cost_layer l where l.tenant_id = @t and l.company_id = @c and l.stock_document_id = @d and l.line_no = @n");
                    cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", t.DocumentId); cmd.Parameters.AddWithValue("n", t.LineNo);
                    onHandQty = await cmd.ExecuteScalarAsync(ct) is decimal rem ? rem : 0m;
                    if (onHandQty > 0m) layer = (t.DocumentId, t.LineNo);
                }
                else onHandQty = Math.Min(t.Quantity, Math.Max(cost[ckey].Qty, 0m));
                decimal onHand = Math.Round(share * onHandQty / t.Quantity, lcScale, MidpointRounding.AwayFromZero), consumed = share - onHand;
                (decimal q, decimal v) = cost[ckey];
                v += onHand; cost[ckey] = (q, v);
                decimal stockQty = await StockBalanceAsync(tx, tenantId, companyId, t.Item, t.Warehouse, t.Location, t.Tracking, t.Status, ct);
                moves.Add(new Derivation.Planned
                {
                    LineNo = moves.Count + 1, LineRef = $"LC {req.CostSource.DocumentId:D}/{req.CostSource.LineNo}", Item = item, MovementType = "COST_ADJUST", CostSource = "COMPUTED", UnitCode = t.BaseUnit, RequestIndex = t.Index,
                    WarehouseId = t.Warehouse, LocationId = t.Location, TrackingId = t.Tracking, StockStatus = t.Status, CostWarehouseId = t.CostWarehouse, ValueLc = onHand, CostQtyAfter = q, CostValueAfter = v, StockQtyAfter = stockQty
                });
                lineResults.Add((t, share, onHand, consumed, layer));
                if (onHand != 0m) pairs.Add(new Derivation.JournalPair("INVENTORY", "LANDED_COST_CLEARING", onHand, item.GroupId, t.Warehouse, t.Warehouse, t.Item, t.Index));
                if (consumed != 0m) pairs.Add(new Derivation.JournalPair("COGS", "LANDED_COST_CLEARING", consumed, item.GroupId, t.Warehouse, t.Warehouse, t.Item, t.Index));
            }
            var built = await BuildJournalAsync(tx, tenantId, companyId, company, groupCurrency, ledger, req.PostingDate, pairs, ct);
            if (built.Error is not null) return Fail(built.Error);

            DateTime at = Now();
            long sequenceNo;
            await using (var cmd = tx.Command("update core.number_range_counter set last_number = last_number + 1, updated_at = now() where tenant_id = @t and company_id = @c and series_id = @s and fiscal_year = @y returning last_number"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("s", docSeries.SeriesId); cmd.Parameters.AddWithValue("y", fiscalYear);
                sequenceNo = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            }
            string number = Core.Posting.PostingRules.FormatNumber(docSeries.Prefix, docSeries.IncludeFiscalYear, docSeries.Separator, docSeries.NumberLength, fiscalYear, sequenceNo);
            long chainSeq = stockSeq + 1;
            Guid journalId = Guid.NewGuid();
            var movementRows = moves.Select(m => (IReadOnlyDictionary<string, object?>)MovementRow(tenantId, companyId, stockId, chainSeq, req.PostingDate, m, company.LocalCurrency)).ToList();
            byte[] movementDigest = StockHash2.MovementDigest(movementRows);
            var body = new Dictionary<string, object?>
            {
                ["stock_document_id"] = stockId, ["document_type"] = "STOCK_COST_ADJUSTMENT", ["number_series_id"] = docSeries.SeriesId, ["fiscal_year"] = fiscalYear, ["sequence_no"] = sequenceNo,
                ["document_number"] = number, ["fiscal_period"] = periodNo, ["document_date"] = req.PostingDate, ["posting_date"] = req.PostingDate, ["posted_at"] = at, ["reason_code"] = null,
                ["reversal_of_stock_document_id"] = null, ["journal_document_id"] = journalId, ["soft_close_override"] = recheck.SoftCloseOverride, ["currency_tc"] = null, ["source_system"] = ctx.SourceSystem,
                ["source_document_ref"] = null, ["idempotency_key"] = ctx.IdempotencyKey, ["command_name"] = "landed-costs:allocate", ["posted_by"] = ctx.Actor, ["correlation_id"] = ctx.CorrelationId,
                ["movement_count"] = moves.Count, ["detail_digest"] = null, ["movement_digest"] = movementDigest, ["hash_version"] = StockHash2.Version
            };
            byte[] rowHash = StockHash2.RowHash(tenantId, companyId, chainSeq, stockPrev, body);
            var posted = await ClosingDocuments.PostAsync(ledger, ctx with { SourceSystem = "INVENTORY" },
                new DocumentSpec("STOCK_COST_ADJUSTMENT", "landed-costs:allocate", journalSeries, fiscalYear, periodNo, recheck.SoftCloseOverride, req.PostingDate, ctx.IdempotencyKey + "#J",
                    DetailDigest: rowHash, Origin: "STOCK", TransactionCurrency: company.LocalCurrency, DocumentId: journalId), built.Lines, null, ct);
            if (posted.Error is not null) return Fail(posted.Error);
            await using (var cmd = tx.Command(
                "insert into core.stock_document (tenant_id, company_id, stock_document_id, document_type, number_series_id, fiscal_year, sequence_no, document_number, fiscal_period, document_date, " +
                "posting_date, posted_at, journal_document_id, soft_close_override, source_system, idempotency_key, command_name, posted_by, correlation_id, movement_count, movement_digest, chain_seq, prev_hash, row_hash, hash_version) " +
                "values (@t, @c, @id, 'STOCK_COST_ADJUSTMENT', @s, @fy, @seq, @num, @fp, @dd, @dd, @at, @j, @soft, @src, @key, 'landed-costs:allocate', @by, @corr, @n, @md, @cs, @prev, @row, 2)"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("id", stockId); cmd.Parameters.AddWithValue("s", docSeries.SeriesId);
                cmd.Parameters.AddWithValue("fy", fiscalYear); cmd.Parameters.AddWithValue("seq", sequenceNo); cmd.Parameters.AddWithValue("num", number); cmd.Parameters.AddWithValue("fp", periodNo);
                cmd.Parameters.AddWithValue("dd", req.PostingDate); cmd.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = at }); cmd.Parameters.AddWithValue("j", journalId);
                cmd.Parameters.AddWithValue("soft", recheck.SoftCloseOverride); cmd.Parameters.AddWithValue("src", ctx.SourceSystem); cmd.Parameters.AddWithValue("key", ctx.IdempotencyKey);
                cmd.Parameters.AddWithValue("by", ctx.Actor); cmd.Parameters.AddWithValue("corr", ctx.CorrelationId); cmd.Parameters.AddWithValue("n", moves.Count);
                cmd.Parameters.AddWithValue("md", movementDigest); cmd.Parameters.AddWithValue("cs", chainSeq); cmd.Parameters.AddWithValue("prev", stockPrev); cmd.Parameters.AddWithValue("row", rowHash);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await WriteMovementsAsync(tx, tenantId, companyId, stockId, chainSeq, req.PostingDate, moves, company.LocalCurrency, ct);
            await using (var cmd = tx.Command(
                "insert into core.landed_cost_allocation (tenant_id, company_id, allocation_id, stock_document_id, cost_source_document_id, cost_source_line_no, allocation_method, total_cost_lc, created_by) " +
                "values (@t, @c, @a, @d, @sd, @sl, @m, @v, @by)"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("a", allocationId); cmd.Parameters.AddWithValue("d", stockId);
                cmd.Parameters.AddWithValue("sd", req.CostSource.DocumentId); cmd.Parameters.AddWithValue("sl", req.CostSource.LineNo); cmd.Parameters.AddWithValue("m", req.AllocationMethod);
                cmd.Parameters.AddWithValue("v", amount); cmd.Parameters.AddWithValue("by", ctx.Actor);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            for (int i = 0; i < lineResults.Count; i++)
            {
                var (t, share, onHand, consumed, layer) = lineResults[i];
                await using (var cmd = tx.Command(
                    "insert into core.landed_cost_allocation_line (tenant_id, company_id, allocation_id, line_no, receipt_document_id, receipt_line_no, allocated_cost_lc, on_hand_part_lc, consumed_part_lc) values (@t, @c, @a, @n, @d, @l, @v, @h, @x)"))
                {
                    cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("a", allocationId); cmd.Parameters.AddWithValue("n", i + 1);
                    cmd.Parameters.AddWithValue("d", t.DocumentId); cmd.Parameters.AddWithValue("l", t.LineNo); cmd.Parameters.AddWithValue("v", share); cmd.Parameters.AddWithValue("h", onHand); cmd.Parameters.AddWithValue("x", consumed);
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                // a FIFO layer takes the on-hand part into its value (4.3)
                if (layer is { } l && onHand != 0m)
                {
                    await using var cmd = tx.Command(
                        "insert into core.stock_layer_adjustment (tenant_id, company_id, stock_document_id, line_no, layer_doc_id, layer_line_no, value_lc) values (@t, @c, @d, @n, @ld, @ll, @v)");
                    cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", stockId); cmd.Parameters.AddWithValue("n", i + 1);
                    cmd.Parameters.AddWithValue("ld", l.Doc); cmd.Parameters.AddWithValue("ll", l.Line); cmd.Parameters.AddWithValue("v", onHand);
                    await cmd.ExecuteNonQueryAsync(ct);
                }
            }
            await ClearingPoster.MoveHeadAsync(tx, tenantId, companyId, StockHash2.ChainType, "", chainSeq, rowHash, StockHash2.Version, ct);
            Guid logId = await CommandSupport.WriteLogAsync(tx, tenantId, companyId, ctx, "landed-costs:allocate", fingerprint, "STOCK_DOCUMENT", stockId, ct);
            await ClosingDocuments.WriteDocumentEventAsync(ledger, ctx, posted.Document!, "ledger.document_posted", posted.Document!.DocumentId, "POSTED", ct);
            await CommandSupport.WriteOutboxAsync(tx, tenantId, companyId, ctx, "stock.document_posted", "STOCK_DOCUMENT", $"{companyId:D}/{stockId:D}", "POSTED", at, isAudit: true,
                JsonSerializer.Serialize(new Dictionary<string, object?> { ["chain_type"] = "STOCK", ["scope"] = "", ["chain_seq"] = chainSeq, ["row_hash"] = Canonical.FormatHex32(rowHash) }),
                JsonSerializer.Serialize(new Dictionary<string, object?> { ["document_type"] = "STOCK_COST_ADJUSTMENT", ["posting_date"] = req.PostingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["movement_count"] = moves.Count, ["journal_document_id"] = journalId }), ct);
            foreach (var last in moves.GroupBy(m => (m.Item.ItemId, m.WarehouseId, m.LocationId)).Select(g => g.Last()))
                await CommandSupport.WriteOutboxAsync(tx, tenantId, companyId, ctx, "stock.balance_changed", "STOCK_BALANCE", $"{companyId:D}/{last.Item.ItemId:D}/{last.WarehouseId:D}", "CHANGED", at, isAudit: false, null,
                    JsonSerializer.Serialize(new Dictionary<string, object?> { ["item_id"] = last.Item.ItemId, ["warehouse_id"] = last.WarehouseId, ["location_id"] = last.LocationId, ["quantity_after"] = last.StockQtyAfter }), ct);
            await tx.CommitAsync(ct);
            return CommandResult<LandedCostResult>.Ok(new LandedCostResult(allocationId, stockId, number, journalId, amount,
                lineResults.Select((x, i) => new LandedCostLineResult(i + 1, x.T.DocumentId, x.T.LineNo, x.Share, x.OnHand, x.Consumed)).ToList()), logId);
        }, ct);
    }

    public async Task<CommandResult<LandedCostResult>> ReverseLandedCostAsync(Guid tenantId, CommandContext ctx, ReverseLandedCostRequest req, CancellationToken ct = default)
    {
        static CommandResult<LandedCostResult> Fail(CommandError e) => CommandResult<LandedCostResult>.Rejected([e]);
        if (!ctx.Permissions.Contains(LandedCostPermission)) return Fail(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", LandedCostPermission)));
        var errors = MasterSupport.ValidateContext(ctx);
        if (req.CompanyId == Guid.Empty) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/company_id", ("reason", "required")));
        if (req.AllocationId == Guid.Empty) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/allocation_id", ("reason", "required")));
        if (errors.Count > 0) return CommandResult<LandedCostResult>.Rejected(errors);
        if (string.IsNullOrWhiteSpace(req.ReasonCode)) return Fail(Error("REVERSAL.REASON_REQUIRED", "P1", "/reason_code"));
        if (!ReversalRules.ExternalReasons.Contains(req.ReasonCode)) return Fail(Error("REVERSAL.REASON_NOT_ALLOWED", "P1", "/reason_code", ("allowed_values", ReversalRules.ExternalReasons)));
        byte[] fingerprint = StockReversalRules.Fingerprint("landed-costs:reverse", req.CompanyId, req.AllocationId, req.ReasonCode, req.PostingDate);

        return await RunLandedAsync(tenantId, ctx, "landed-costs:reverse", fingerprint, async tx =>
        {
            Guid companyId = req.CompanyId;
            Guid originalStock; string status; decimal total; Guid sourceDoc; int sourceLine; string method;
            await using (var cmd = tx.Command("select stock_document_id, status, total_cost_lc, cost_source_document_id, cost_source_line_no, allocation_method from core.landed_cost_allocation where tenant_id = @t and company_id = @c and allocation_id = @a"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("a", req.AllocationId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) return Fail(Error("INVENTORY.ALLOCATION_NOT_FOUND", "P2", "/allocation_id"));
                originalStock = r.GetGuid(0); status = r.GetString(1).Trim(); total = r.GetDecimal(2); sourceDoc = r.GetGuid(3); sourceLine = r.GetInt32(4); method = r.GetString(5).Trim();
            }
            if (status == "REVERSED") return Fail(Error("INVENTORY.ALLOCATION_ALREADY_REVERSED", "P2", "/allocation_id"));
            if (status == "MIRROR") return Fail(Error("INVENTORY.ALLOCATION_NOT_REVERSIBLE", "P2", "/allocation_id", ("reason", "a mirror allocation is not reversed")));
            var lines = new List<(int Line, Guid Doc, int RecLine, decimal Share, decimal OnHand, decimal Consumed)>();
            await using (var cmd = tx.Command("select line_no, receipt_document_id, receipt_line_no, allocated_cost_lc, on_hand_part_lc, consumed_part_lc from core.landed_cost_allocation_line where tenant_id = @t and company_id = @c and allocation_id = @a order by line_no"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("a", req.AllocationId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) lines.Add((r.GetInt32(0), r.GetGuid(1), r.GetInt32(2), r.GetDecimal(3), r.GetDecimal(4), r.GetDecimal(5)));
            }
            var adjustments = new List<(int Line, Guid LayerDoc, int LayerLine, decimal Value)>();
            await using (var cmd = tx.Command("select line_no, layer_doc_id, layer_line_no, value_lc from core.stock_layer_adjustment where tenant_id = @t and company_id = @c and stock_document_id = @d order by line_no"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", originalStock);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) adjustments.Add((r.GetInt32(0), r.GetGuid(1), r.GetInt32(2), r.GetDecimal(3)));
            }
            // the reversibility test (B7): no movement on the cost key of any target since the allocation, under the locks of the reversal
            async Task<CommandError?> NoLaterMovementAsync(TenantTransaction inner, CancellationToken token)
            {
                await using var cmd = inner.Command(
                    "select r.line_no from core.landed_cost_allocation_line r join core.stock_movement tm on tm.tenant_id = r.tenant_id and tm.company_id = r.company_id and tm.stock_document_id = r.receipt_document_id and tm.line_no = r.receipt_line_no " +
                    "where r.tenant_id = @t and r.company_id = @c and r.allocation_id = @a and exists (select 1 from core.stock_movement m join core.stock_document d on d.tenant_id = m.tenant_id and d.company_id = m.company_id and d.stock_document_id = m.stock_document_id " +
                    "where m.tenant_id = r.tenant_id and m.company_id = r.company_id and m.item_id = tm.item_id and m.cost_warehouse_id is not distinct from tm.cost_warehouse_id and m.tracking_id is not distinct from tm.tracking_id " +
                    "and d.chain_seq > (select chain_seq from core.stock_document where tenant_id = r.tenant_id and company_id = r.company_id and stock_document_id = @s)) order by r.line_no limit 1");
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("a", req.AllocationId); cmd.Parameters.AddWithValue("s", originalStock);
                return await cmd.ExecuteScalarAsync(token) is int line ? Error("INVENTORY.ALLOCATION_NOT_REVERSIBLE", "P4", $"/allocation_id", ("line_no", line), ("reason", "a movement has come on the cost key since the allocation")) : null;
            }
            var options = new StockReverseOptions("landed-costs:reverse", true, false, false, null, AllowCostAdjustment: true, Guard: NoLaterMovementAsync);
            var reversed = await ReverseCoreAsync(tx, tenantId, ctx, companyId, originalStock, req.ReasonCode, req.PostingDate, fingerprint, ct, options);
            if (reversed.Status != CommandStatus.Ok) return CommandResult<LandedCostResult>.Rejected(reversed.Errors);
            Guid mirrorStock = reversed.Value!.StockDocumentId;
            Guid mirrorAllocation = Guid.NewGuid();
            await using (var cmd = tx.Command(
                "insert into core.landed_cost_allocation (tenant_id, company_id, allocation_id, stock_document_id, cost_source_document_id, cost_source_line_no, allocation_method, total_cost_lc, reversal_of_allocation_id, reason_code, status, created_by) " +
                "values (@t, @c, @a, @d, @sd, @sl, @m, @v, @o, @r, 'MIRROR', @by)"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("a", mirrorAllocation); cmd.Parameters.AddWithValue("d", mirrorStock);
                cmd.Parameters.AddWithValue("sd", sourceDoc); cmd.Parameters.AddWithValue("sl", sourceLine); cmd.Parameters.AddWithValue("m", method); cmd.Parameters.AddWithValue("v", -total);
                cmd.Parameters.AddWithValue("o", req.AllocationId); cmd.Parameters.AddWithValue("r", req.ReasonCode); cmd.Parameters.AddWithValue("by", ctx.Actor);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            foreach (var l in lines)
            {
                await using var cmd = tx.Command(
                    "insert into core.landed_cost_allocation_line (tenant_id, company_id, allocation_id, line_no, receipt_document_id, receipt_line_no, allocated_cost_lc, on_hand_part_lc, consumed_part_lc) values (@t, @c, @a, @n, @d, @l, @v, @h, @x)");
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("a", mirrorAllocation); cmd.Parameters.AddWithValue("n", l.Line);
                cmd.Parameters.AddWithValue("d", l.Doc); cmd.Parameters.AddWithValue("l", l.RecLine); cmd.Parameters.AddWithValue("v", -l.Share); cmd.Parameters.AddWithValue("h", -l.OnHand); cmd.Parameters.AddWithValue("x", -l.Consumed);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            foreach (var a in adjustments)
            {
                await using var cmd = tx.Command("insert into core.stock_layer_adjustment (tenant_id, company_id, stock_document_id, line_no, layer_doc_id, layer_line_no, value_lc) values (@t, @c, @d, @n, @ld, @ll, @v)");
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", mirrorStock); cmd.Parameters.AddWithValue("n", a.Line);
                cmd.Parameters.AddWithValue("ld", a.LayerDoc); cmd.Parameters.AddWithValue("ll", a.LayerLine); cmd.Parameters.AddWithValue("v", -a.Value);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await using (var cmd = tx.Command("update core.landed_cost_allocation set status = 'REVERSED' where tenant_id = @t and company_id = @c and allocation_id = @a and status = 'POSTED'"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("a", req.AllocationId);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            LandedCostResult mirrorResult = await LoadLandedAsync(tx, tenantId, mirrorStock, ct);
            await tx.CommitAsync(ct);
            return CommandResult<LandedCostResult>.Ok(mirrorResult, reversed.CommandLogId ?? Guid.Empty);
        }, ct);
    }
}
