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
/// dropship-confirmations:post and :reverse (11, 5.3, 6.6), first slice. The supplier ships to the customer: the confirmation counts as delivered on the DROPSHIP sales line and as received on the
/// purchase line it is linked to, with no stock movement. One confirmation per line of the command, each a node of the FULFILMENT chain, each with a derived DROPSHIP_COST journal (Dr COGS, Cr
/// DROPSHIP_CLEARING, a GL open item, at the purchase value of the quantity) whose detail_digest is the row hash of the confirmation (the confirmation names the journal in the slot of the
/// derived document). Lock order of 11, 9: the order rows (sales, then purchase, sorted), the counters, the LEDGER head and the FULFILMENT head (inside the posting of the journal). The reversal
/// inserts a mirror confirmation with negative quantities and reverses the journal through the reverser, which clears the open item of the credit against its mirror. Built choices are in 07, 13.2bk.
/// Not built: the invoicing of a dropship line (the sales invoice of the delivered quantity, the supplier invoice that matches the GR/IR credit, 7.2), returns of a confirmation (the links exist),
/// foreign-currency purchase orders.
/// </summary>
public sealed partial class DropshipCommands(NpgsqlDataSource source)
{
    private const string PostName = "dropship-confirmations:post", ReverseName = "dropship-confirmations:reverse";

    private static CommandError Error(string code, string phase, string path, params (string Key, object? Value)[] parameters) => JournalDocumentPoster.Error(code, phase, path, parameters);

    private static CommandResult<DropshipConfirmationResult> Reject(CommandError e) => CommandResult<DropshipConfirmationResult>.Rejected([e]);

    private static DateTime Now() { DateTime n = DateTime.UtcNow; return new DateTime(n.Ticks - n.Ticks % 10, DateTimeKind.Utc); }

    private sealed class Rejected(CommandError error) : Exception("dropship rejected")
    {
        public CommandError Error { get; } = error;
    }

    public async Task<CommandResult<DropshipConfirmationResult>> PostAsync(Guid tenantId, CommandContext ctx, PostDropshipConfirmationRequest req, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(DropshipRules.Permission)) return Reject(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", DropshipRules.Permission)));
        var errors = MasterSupport.ValidateContext(ctx);
        void Invalid(string path, string reason) => errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", path, ("reason", reason)));
        if (req.CompanyId == Guid.Empty) Invalid("/company_id", "required");
        if (req.Lines is null || req.Lines.Count == 0) errors.Add(Error("SCHEMA.REQUIRED_FIELD_MISSING", "P1", "/lines"));
        for (int i = 0; i < (req.Lines?.Count ?? 0); i++)
        {
            DropshipConfirmationLineInput l = req.Lines![i];
            if (string.IsNullOrWhiteSpace(l.LineRef) || l.LineRef.Length > 100) Invalid($"/lines/{i}/line_ref", "required, at most 100 characters");
            else if (req.Lines.Take(i).Any(x => x.LineRef == l.LineRef)) errors.Add(Error("SCHEMA.DUPLICATE_LINE_REF", "P1", $"/lines/{i}/line_ref", ("line_ref", l.LineRef)));
            if (l.SalesOrderId == Guid.Empty || l.SalesOrderLineNo < 1) Invalid($"/lines/{i}/sales_order_id", "a sales order and a line number");
            if (l.Quantity <= 0m || Math.Round(l.Quantity, 6) != l.Quantity) Invalid($"/lines/{i}/quantity", "more than 0, at most 6 fraction digits");
            if (string.IsNullOrWhiteSpace(l.UnitCode)) Invalid($"/lines/{i}/unit_code", "required");
        }
        if (req.SupplierShipmentReference?.Length > 100 || req.CarrierReference?.Length > 100 || req.SourceDocumentRef?.Length > 100) Invalid("/supplier_shipment_reference", "at most 100 characters");
        if (errors.Count > 0) return CommandResult<DropshipConfirmationResult>.Rejected(errors);
        byte[] fingerprint = DropshipRules.Fingerprint(req);

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Stock, ct: ct);
                CommandSupport.EarlierCommand? earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
                if (earlier is not null)
                {
                    if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint)) return Reject(Error("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
                    return CommandResult<DropshipConfirmationResult>.Ok(await LoadAsync(tx, tenantId, req.CompanyId, ctx.IdempotencyKey, req.Lines!, ct), earlier.CommandLogId, replay: true);
                }
                return await PostCoreAsync(tx, tenantId, ctx, req, fingerprint, ct);
            }
            catch (Rejected ex)
            {
                return Reject(ex.Error);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.TableName == "command_log" && attempt == 0)
            {
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                return CommandResult<DropshipConfirmationResult>.Failed(new CommandError("SYSTEM.LOCK_TIMEOUT", "P7", "", null, true));
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure)
            {
                return CommandResult<DropshipConfirmationResult>.Failed(new CommandError("SYSTEM.CONFLICT", "P7", "", null, true));
            }
        }
    }

    private sealed record Plan(
        int Index, DropshipConfirmationLineInput Input, Guid ItemId, decimal BaseQuantity, Guid SalesOrderId, int SalesLine, Guid PurchaseOrderId, int PurchaseLine, Guid Customer, Guid Supplier,
        decimal Value, decimal UnitPrice, Guid Cogs, Guid Clearing);

    private async Task<CommandResult<DropshipConfirmationResult>> PostCoreAsync(
        TenantTransaction tx, Guid tenantId, CommandContext ctx, PostDropshipConfirmationRequest req, byte[] fingerprint, CancellationToken ct)
    {
        Guid companyId = req.CompanyId;
        var company = await JournalDocumentPoster.ReadCompanyAsync(tx, tenantId, companyId, ct);
        if (company is null) return Reject(Error("CONFIG.INVALID_REFERENCE", "P2", "/company_id", ("company_id", companyId)));
        if (company.Status != "ACTIVE") return Reject(Error("COMPANY.NOT_ACTIVE", "P2", "/company_id", ("status", company.Status)));
        var errors = new List<CommandError>();
        DateOnly date = req.ConfirmationDate;

        // position 1 of the lock order: the order rows, the sales orders and the purchase orders they are linked to, sorted. Everything that depends on progress is checked after the lock.
        var salesIds = req.Lines.Select(l => l.SalesOrderId).Distinct().Order().ToArray();
        var salesOrders = new Dictionary<Guid, (string Status, Guid Customer)>();
        await using (var cmd = tx.Command("select sales_order_id, lifecycle_status, customer_partner_id from core.sales_order where tenant_id = @t and company_id = @c and sales_order_id = any(@ids) order by sales_order_id for update"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("ids", salesIds);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) salesOrders[r.GetGuid(0)] = (r.GetString(1), r.GetGuid(2));
        }
        var links = new Dictionary<(Guid, int), (Guid Order, int Line)>();
        foreach (var l in req.Lines.DistinctBy(x => (x.SalesOrderId, x.SalesOrderLineNo)))
        {
            await using var cmd = tx.Command("select dropship_purchase_order_id, dropship_purchase_line_no from core.sales_order_line where tenant_id = @t and company_id = @c and sales_order_id = @o and line_no = @n and fulfilment_type = 'DROPSHIP'");
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("o", l.SalesOrderId); cmd.Parameters.AddWithValue("n", l.SalesOrderLineNo);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct) && !r.IsDBNull(0) && !r.IsDBNull(1)) links[(l.SalesOrderId, l.SalesOrderLineNo)] = (r.GetGuid(0), r.GetInt32(1));
        }
        var purchaseIds = links.Values.Select(v => v.Order).Distinct().Order().ToArray();
        var purchaseOrders = new Dictionary<Guid, (string Status, Guid Supplier, string Currency)>();
        await using (var cmd = tx.Command("select purchase_order_id, lifecycle_status, supplier_partner_id, order_currency from core.purchase_order where tenant_id = @t and company_id = @c and purchase_order_id = any(@ids) order by purchase_order_id for update"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("ids", purchaseIds);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) purchaseOrders[r.GetGuid(0)] = (r.GetString(1), r.GetGuid(2), r.GetString(3).Trim());
        }

        int lcScale = 2;
        await using (var cmd = tx.Command("select minor_units from platform.ref_currency where currency_code = @c"))
        {
            cmd.Parameters.AddWithValue("c", company.LocalCurrency);
            lcScale = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct) ?? 2, CultureInfo.InvariantCulture);
        }
        var plans = new List<Plan>();
        var running = new Dictionary<(Guid, int), decimal>();
        for (int i = 0; i < req.Lines.Count; i++)
        {
            DropshipConfirmationLineInput l = req.Lines[i];
            string p = $"/lines/{i}";
            if (!salesOrders.TryGetValue(l.SalesOrderId, out var so)) { errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", p + "/sales_order_id", ("order_id", l.SalesOrderId))); continue; }
            if (so.Status != "OPEN") { errors.Add(Error("ORDER.ORDER_NOT_OPEN", "P2", p + "/sales_order_id", ("status", so.Status))); continue; }
            if (!links.TryGetValue((l.SalesOrderId, l.SalesOrderLineNo), out var link)) { errors.Add(Error("ORDER.DROPSHIP_LINE_NOT_LINKED", "P2", p + "/sales_order_line_no", ("line_no", l.SalesOrderLineNo))); continue; }
            if (!purchaseOrders.TryGetValue(link.Order, out var po)) { errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", p + "/sales_order_line_no", ("purchase_order_id", link.Order))); continue; }
            if (po.Status != "OPEN") { errors.Add(Error("ORDER.ORDER_NOT_OPEN", "P2", p + "/sales_order_line_no", ("status", po.Status), ("purchase_order_id", link.Order))); continue; }
            if (po.Currency != company.LocalCurrency) { errors.Add(Error("LIMIT.STOCK_FOREIGN_CURRENCY_NOT_BUILT", "P2", p + "/sales_order_line_no", ("currency", po.Currency))); continue; }
            JsonElement? sl = await ReadLineAsync(tx, "sales_order_line", "sales_order_id", tenantId, companyId, l.SalesOrderId, l.SalesOrderLineNo, ct);
            JsonElement? pl = await ReadLineAsync(tx, "purchase_order_line", "purchase_order_id", tenantId, companyId, link.Order, link.Line, ct);
            if (sl is not { } salesLine || pl is not { } purchaseLine) { errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", p + "/sales_order_line_no", ("line_no", l.SalesOrderLineNo))); continue; }
            if (Str(salesLine, "line_status") != "OPEN" || Str(purchaseLine, "line_status") != "OPEN") { errors.Add(Error("ORDER.LINE_NOT_OPEN", "P2", p + "/sales_order_line_no", ("line_no", l.SalesOrderLineNo))); continue; }
            Guid itemId = Guid.Parse(Str(salesLine, "item_id")!);
            string baseUnit = ""; int scale = 0; Guid itemGroup;
            await using (var cmd = tx.Command("select base_unit, quantity_scale, item_group_id from core.item where tenant_id = @t and item_id = @i"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("i", itemId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                await r.ReadAsync(ct);
                baseUnit = r.GetString(0).Trim(); scale = r.GetInt16(1); itemGroup = r.GetGuid(2);
            }
            decimal? factor = await OrderCommands.FactorAsync(tx, tenantId, itemId, l.UnitCode, baseUnit, ct);
            if (factor is null) { errors.Add(Error("INVENTORY.UNIT_CONVERSION_MISSING", "P2", p + "/unit_code", ("item_id", itemId), ("unit_code", l.UnitCode))); continue; }
            decimal baseQty = l.Quantity * factor.Value;
            if (Math.Round(baseQty, scale) != baseQty) { errors.Add(Error("INVENTORY.QUANTITY_SCALE_EXCEEDED", "P1", p + "/quantity", ("base_quantity", baseQty), ("quantity_scale", scale))); continue; }

            // the delivery limit of the sales line and the receipt limit of the purchase line (4.3, 4.6): each side counts its deliveries or receipts and the confirmations
            decimal salesDone = await DoneAsync(tx, tenantId, companyId, true, l.SalesOrderId, l.SalesOrderLineNo, ct) + running.GetValueOrDefault((l.SalesOrderId, l.SalesOrderLineNo));
            decimal purchaseDone = await DoneAsync(tx, tenantId, companyId, false, link.Order, link.Line, ct) + running.GetValueOrDefault((link.Order, link.Line));
            decimal salesLimit = FulfilmentRules.Limit(Num(salesLine, "base_quantity"), Num(salesLine, "over_tolerance_pct"), salesDone);
            decimal purchaseLimit = FulfilmentRules.Limit(Num(purchaseLine, "base_quantity"), Num(purchaseLine, "over_tolerance_pct"), purchaseDone);
            if (baseQty > salesLimit) { errors.Add(Error("ORDER.OVER_DELIVERY", "P4", p + "/quantity", ("base_quantity", baseQty), ("limit", salesLimit), ("fulfilled", salesDone), ("side", "SALES"))); continue; }
            if (baseQty > purchaseLimit) { errors.Add(Error("ORDER.OVER_DELIVERY", "P4", p + "/quantity", ("base_quantity", baseQty), ("limit", purchaseLimit), ("fulfilled", purchaseDone), ("side", "PURCHASE"))); continue; }
            running[(l.SalesOrderId, l.SalesOrderLineNo)] = running.GetValueOrDefault((l.SalesOrderId, l.SalesOrderLineNo)) + baseQty;
            running[(link.Order, link.Line)] = running.GetValueOrDefault((link.Order, link.Line)) + baseQty;

            // the partners must not be restricted (6, common rule 1)
            foreach (Guid partner in new[] { so.Customer, po.Supplier })
            {
                await using var cmd = tx.Command("select restriction_status from core.business_partner where tenant_id = @t and partner_id = @p");
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("p", partner);
                if (await cmd.ExecuteScalarAsync(ct) as string == "RESTRICTED") { errors.Add(Error("MASTER.PARTNER_RESTRICTED", "P2", p + "/sales_order_id", ("partner_id", partner))); goto next; }
            }
            decimal value = FulfilmentRules.ReceiptValue(Num(purchaseLine, "net_amount"), Num(purchaseLine, "base_quantity"), baseQty, lcScale);
            var cogs = await AccountDeterminationResolver.ResolveAsync(tx, tenantId, companyId, "COGS", itemGroup, null, null, itemId, date, ct);
            if (cogs is null) { errors.Add(Error("INVENTORY.ACCOUNT_DETERMINATION_MISSING", "P5", p, ("role_code", "COGS"), ("item_id", itemId))); continue; }
            var clearing = await AccountDeterminationResolver.ResolveAsync(tx, tenantId, companyId, "DROPSHIP_CLEARING", itemGroup, null, null, itemId, date, ct);
            if (clearing is null) { errors.Add(Error("INVENTORY.ACCOUNT_DETERMINATION_MISSING", "P5", p, ("role_code", "DROPSHIP_CLEARING"), ("item_id", itemId))); continue; }
            plans.Add(new Plan(i, l, itemId, baseQty, l.SalesOrderId, l.SalesOrderLineNo, link.Order, link.Line, so.Customer, po.Supplier, value, Math.Round(Num(purchaseLine, "unit_price"), 4), cogs.AccountId, clearing.AccountId));
        next:;
        }
        if (errors.Count > 0) return CommandResult<DropshipConfirmationResult>.Rejected(errors);

        // the numbers: the series of the confirmation at the date, in the fiscal year of the date (04); the counter is locked before the heads
        int fiscalYear;
        await using (var cmd = tx.Command("select fiscal_year from core.fiscal_year where tenant_id = @t and company_id = @c and start_date <= @d and end_date >= @d"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", date);
            if (await cmd.ExecuteScalarAsync(ct) is not int year) return Reject(Error("SYSTEM.FISCAL_YEAR_MISSING", "P2", "/confirmation_date", ("date", date)));
            fiscalYear = year;
        }
        Guid ledger = await ClearingPoster.ReadDefaultLedgerAsync(tx, tenantId, companyId, ct) ?? Guid.Empty;
        var series = await JournalDocumentPoster.ReadSeriesAsync(tx, tenantId, companyId, DropshipRules.SeriesType, date, ledger, ct);
        if (series is null) return Reject(Error("SERIES.NO_ASSIGNMENT", "P2", "/confirmation_date", ("document_type", DropshipRules.SeriesType), ("posting_date", date)));
        long? last = await ClearingPoster.LockCounterAsync(tx, tenantId, companyId, series.SeriesId, fiscalYear, ct);
        if (last is null) return Reject(Error("LIMIT.NUMBER_COUNTER_MISSING", "P7", "/confirmation_date", ("series_code", series.SeriesCode), ("fiscal_year", fiscalYear)));
        long sequenceNo = last.Value;

        string baseKey = ctx.IdempotencyKey.Length > 90 ? ctx.IdempotencyKey[..90] : ctx.IdempotencyKey;
        var jposter = new JournalDocumentPoster(source);
        var results = new List<DropshipConfirmationLineResult>();
        Guid firstId = Guid.Empty;
        foreach (Plan plan in plans)
        {
            sequenceNo++;
            string number = PostingRules.FormatNumber(series.Prefix, series.IncludeFiscalYear, series.Separator, series.NumberLength, fiscalYear, sequenceNo);
            Guid confirmationId = Guid.NewGuid(), journalId = Guid.NewGuid();
            if (firstId == Guid.Empty) firstId = confirmationId;
            DateTime at = Now();
            CommandContext rowCtx = ctx with { IdempotencyKey = $"{baseKey}#D{plan.Index + 1}" };
            long chainSeq = 0; byte[] prev = [], rowHash = [], lineDigest = [];

            Task<byte[]?> NodeAsync(JournalDocumentPoster.EmbeddedFacts _) => BuildNodeAsync();
            async Task<byte[]?> BuildNodeAsync()
            {
                (long seq, byte[] hash, _) = await CommandSupport.LockOrCreateHeadAsync(tx, tenantId, companyId, FulfilmentHash2.ChainType, "", LedgerHash2.Genesis(tenantId, companyId, FulfilmentHash2.ChainType, ""), ct);
                chainSeq = seq + 1; prev = hash;
                var line = LineRow(tenantId, companyId, confirmationId, plan, plan.Input.Quantity, plan.BaseQuantity, plan.Value);
                lineDigest = FulfilmentHash2.LineDigest([line]);
                var body = Body(confirmationId, series.SeriesId, fiscalYear, sequenceNo, number, date, plan.Customer, req.SupplierShipmentReference, req.CarrierReference, journalId, null, null, ctx, rowCtx, at,
                    req.SourceDocumentRef, PostName, lineDigest);
                rowHash = FulfilmentHash2.RowHash(tenantId, companyId, chainSeq, prev, body);
                return rowHash;
            }

            var journalLines = new List<RequestLine>
            {
                new("COGS", plan.Cogs, plan.Value),
                new("CLEARING", plan.Clearing, -plan.Value, OpenItem: new OpenItemRequest(null, date))
            };
            var journal = new PostJournalDocumentRequest(companyId, null, "DROPSHIP_COST", date, date, company.LocalCurrency, null, null, req.SourceDocumentRef, journalLines, null);
            async Task AfterAsync(TenantTransaction t, PostedDocument posted, JournalDocumentPoster.EmbeddedFacts facts)
            {
                await InsertAsync(t, tenantId, companyId, confirmationId, number, series.SeriesId, fiscalYear, sequenceNo, date, plan, plan.Input.Quantity, plan.BaseQuantity, plan.Value, req.SupplierShipmentReference, req.CarrierReference,
                    posted.DocumentId, null, null, req.SourceDocumentRef, at, rowCtx, ctx, chainSeq, prev, rowHash, lineDigest, ct);
                await ClearingPoster.MoveHeadAsync(t, tenantId, companyId, FulfilmentHash2.ChainType, "", chainSeq, rowHash, FulfilmentHash2.Version, ct);
                await CommandSupport.WriteOutboxAsync(t, tenantId, companyId, ctx, "dropship.confirmed", "DROPSHIP_CONFIRMATION", $"{companyId:D}/{confirmationId:D}", "POSTED", at, isAudit: true,
                    JsonSerializer.Serialize(new Dictionary<string, object?> { ["chain_type"] = "FULFILMENT", ["scope"] = "", ["chain_seq"] = chainSeq, ["row_hash"] = Canonical.FormatHex32(rowHash) }),
                    JsonSerializer.Serialize(new Dictionary<string, object?> { ["number"] = number, ["customer_partner_id"] = plan.Customer, ["supplier_partner_id"] = plan.Supplier, ["journal_document_id"] = posted.DocumentId }), ct);
            }
            var embedding = new JournalDocumentPoster.PostEmbedding(PostName, "ORDER", null, null, WriteLog: false, Commit: false, AfterAsync, journalId, NodeAsync);
            CommandContext journalCtx = ctx with { IdempotencyKey = $"{baseKey}#J{plan.Index + 1}" };
            var posted2 = await jposter.PostCoreAsync(tx, tenantId, journalCtx, journal, fingerprint, embedding, ct);
            if (posted2.Status != CommandStatus.Ok)
                return CommandResult<DropshipConfirmationResult>.Rejected(posted2.Errors.Select(e => e with { Path = $"/lines/{plan.Index}" }).ToList());
            results.Add(new DropshipConfirmationLineResult(results.Count + 1, plan.Input.LineRef, confirmationId, number, chainSeq, Canonical.FormatHex32(rowHash), posted2.Value!.DocumentId, plan.Value, 0m));
        }
        await using (var cmd = tx.Command("update core.number_range_counter set last_number = @n, updated_at = now() where tenant_id = @t and company_id = @c and series_id = @s and fiscal_year = @y"))
        {
            cmd.Parameters.AddWithValue("n", sequenceNo); cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId);
            cmd.Parameters.AddWithValue("s", series.SeriesId); cmd.Parameters.AddWithValue("y", fiscalYear);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        Guid logId = await CommandSupport.WriteLogAsync(tx, tenantId, companyId, ctx, PostName, fingerprint, "DROPSHIP_CONFIRMATION", firstId, ct);
        var loaded = await LoadAsync(tx, tenantId, companyId, ctx.IdempotencyKey, req.Lines, ct);
        await tx.CommitAsync(ct);   // the deferred checks: the chains, the journal documents, the outbox position
        return CommandResult<DropshipConfirmationResult>.Ok(loaded, logId);
    }

    private static decimal Num(JsonElement row, string name) => row.GetProperty(name).GetDecimal();

    private static string? Str(JsonElement row, string name) => row.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : null;

    private static async Task<JsonElement?> ReadLineAsync(TenantTransaction tx, string table, string idColumn, Guid tenantId, Guid companyId, Guid orderId, int lineNo, CancellationToken ct)
    {
        await using var cmd = tx.Command($"select to_jsonb(x)::text from core.{table} x where tenant_id = @t and company_id = @c and {idColumn} = @o and line_no = @n");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("o", orderId); cmd.Parameters.AddWithValue("n", lineNo);
        return await cmd.ExecuteScalarAsync(ct) is string json ? JsonDocument.Parse(json).RootElement.Clone() : null;
    }

    /// <summary>What a side has done of an order line (4.3): its deliveries or receipts and the confirmations, mirror rows with their negative quantities included.</summary>
    internal static async Task<decimal> DoneAsync(TenantTransaction tx, Guid tenantId, Guid companyId, bool sales, Guid orderId, int lineNo, CancellationToken ct)
    {
        await using var cmd = tx.Command(sales
            ? "select coalesce((select sum(base_quantity) from core.sales_delivery_line where tenant_id = @t and company_id = @c and sales_order_id = @o and sales_order_line_no = @n), 0) + " +
              "coalesce((select sum(base_quantity) from core.dropship_confirmation where tenant_id = @t and company_id = @c and sales_order_id = @o and sales_order_line_no = @n), 0)"
            : "select coalesce((select sum(base_quantity) from core.purchase_receipt_line where tenant_id = @t and company_id = @c and purchase_order_id = @o and purchase_order_line_no = @n), 0) + " +
              "coalesce((select sum(base_quantity) from core.dropship_confirmation where tenant_id = @t and company_id = @c and purchase_order_id = @o and purchase_order_line_no = @n), 0)");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("o", orderId); cmd.Parameters.AddWithValue("n", lineNo);
        return (decimal)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static Dictionary<string, object?> LineRow(Guid tenantId, Guid companyId, Guid confirmationId, Plan plan, decimal quantity, decimal baseQuantity, decimal value) => new()
    {
        ["tenant_id"] = tenantId, ["company_id"] = companyId, ["document_id"] = confirmationId, ["line_no"] = 1, ["order_id"] = plan.SalesOrderId, ["order_line_no"] = plan.SalesLine, ["item_id"] = plan.ItemId,
        ["quantity"] = quantity, ["unit_code"] = plan.Input.UnitCode, ["base_quantity"] = baseQuantity, ["warehouse_id"] = null, ["location_id"] = null, ["tracking_id"] = null, ["stock_status"] = "AVAILABLE",
        ["stock_document_line_no"] = null, ["stock_document_line_count"] = null, ["unit_price"] = null, ["currency"] = null, ["value_lc"] = value
    };

    /// <summary>The block of the row hash (FulfilmentHash2.Body). The slot of the derived stock document holds the derived journal document.</summary>
    internal static Dictionary<string, object?> Body(
        Guid id, Guid seriesId, int fiscalYear, long sequenceNo, string number, DateOnly date, Guid customer, string? supplierShipment, string? carrier, Guid journalId, Guid? reversalOf, string? reason,
        CommandContext ctx, CommandContext rowCtx, DateTime at, string? sourceRef, string commandName, byte[] lineDigest) => new()
    {
        ["document_id"] = id, ["document_type"] = FulfilmentHash2.DropshipConfirmation, ["number_series_id"] = seriesId, ["fiscal_year"] = fiscalYear, ["sequence_no"] = sequenceNo, ["document_number"] = number,
        ["document_date"] = date, ["partner_id"] = customer, ["warehouse_id"] = null, ["reference_1"] = supplierShipment, ["reference_2"] = carrier, ["stock_document_id"] = journalId, ["reversal_of_id"] = reversalOf,
        ["reason_code"] = reason, ["origin"] = "LIVE", ["migration_batch_id"] = null, ["source_system"] = ctx.SourceSystem, ["source_document_ref"] = sourceRef, ["posted_at"] = at, ["idempotency_key"] = rowCtx.IdempotencyKey,
        ["command_name"] = commandName, ["posted_by"] = ctx.Actor, ["correlation_id"] = ctx.CorrelationId, ["line_count"] = 1, ["line_digest"] = lineDigest, ["hash_version"] = FulfilmentHash2.Version
    };

    private static async Task InsertAsync(
        TenantTransaction tx, Guid tenantId, Guid companyId, Guid id, string number, Guid seriesId, int fiscalYear, long sequenceNo, DateOnly date, Plan plan, decimal quantity, decimal baseQuantity, decimal value,
        string? supplierShipment, string? carrier, Guid journalId, Guid? reversalOf, string? reason, string? sourceRef, DateTime at, CommandContext rowCtx, CommandContext ctx, long chainSeq, byte[] prev, byte[] rowHash, byte[] lineDigest, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "insert into core.dropship_confirmation (tenant_id, company_id, dropship_confirmation_id, confirmation_number, number_series_id, fiscal_year, sequence_no, confirmation_date, customer_partner_id, supplier_partner_id, " +
            "sales_order_id, sales_order_line_no, purchase_order_id, purchase_order_line_no, item_id, quantity, unit_code, base_quantity, supplier_shipment_reference, carrier_reference, journal_document_id, value_lc, " +
            "reversal_of_confirmation_id, reason_code, source_system, source_document_ref, posted_at, posted_by, correlation_id, idempotency_key, chain_seq, prev_hash, row_hash, line_digest, hash_version) values " +
            "(@t, @c, @id, @num, @s, @fy, @seq, @d, @cust, @sup, @so, @sl, @po, @pl, @item, @q, @u, @bq, @ship, @car, @j, @v, @rev, @reason, @src, @ref, @at, @by, @corr, @key, @cs, @prev, @row, @ld, 2)");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("id", id); cmd.Parameters.AddWithValue("num", number); cmd.Parameters.AddWithValue("s", seriesId);
        cmd.Parameters.AddWithValue("fy", fiscalYear); cmd.Parameters.AddWithValue("seq", sequenceNo); cmd.Parameters.AddWithValue("d", date); cmd.Parameters.AddWithValue("cust", plan.Customer); cmd.Parameters.AddWithValue("sup", plan.Supplier);
        cmd.Parameters.AddWithValue("so", plan.SalesOrderId); cmd.Parameters.AddWithValue("sl", plan.SalesLine); cmd.Parameters.AddWithValue("po", plan.PurchaseOrderId); cmd.Parameters.AddWithValue("pl", plan.PurchaseLine);
        cmd.Parameters.AddWithValue("item", plan.ItemId); cmd.Parameters.AddWithValue("q", quantity); cmd.Parameters.AddWithValue("u", plan.Input.UnitCode); cmd.Parameters.AddWithValue("bq", baseQuantity);
        cmd.Parameters.AddWithValue("j", journalId); cmd.Parameters.AddWithValue("v", value); cmd.Parameters.AddWithValue("src", ctx.SourceSystem); cmd.Parameters.AddWithValue("by", ctx.Actor);
        cmd.Parameters.AddWithValue("corr", ctx.CorrelationId); cmd.Parameters.AddWithValue("key", rowCtx.IdempotencyKey); cmd.Parameters.AddWithValue("cs", chainSeq); cmd.Parameters.AddWithValue("prev", prev);
        cmd.Parameters.AddWithValue("row", rowHash); cmd.Parameters.AddWithValue("ld", lineDigest);
        cmd.Parameters.Add(new NpgsqlParameter("ship", NpgsqlDbType.Text) { Value = (object?)supplierShipment ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("car", NpgsqlDbType.Text) { Value = (object?)carrier ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("rev", NpgsqlDbType.Uuid) { Value = (object?)reversalOf ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("reason", NpgsqlDbType.Text) { Value = (object?)reason ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("ref", NpgsqlDbType.Text) { Value = (object?)sourceRef ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = at });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The confirmations written under an idempotency key (the rows have the key and a suffix), in line order.</summary>
    private static async Task<DropshipConfirmationResult> LoadAsync(TenantTransaction tx, Guid tenantId, Guid companyId, string key, IReadOnlyList<DropshipConfirmationLineInput> lines, CancellationToken ct)
    {
        string baseKey = key.Length > 90 ? key[..90] : key;
        var rows = new List<DropshipConfirmationLineResult>();
        await using var cmd = tx.Command(
            "select dropship_confirmation_id, confirmation_number, chain_seq, row_hash, journal_document_id, value_lc, idempotency_key from core.dropship_confirmation " +
            "where tenant_id = @t and company_id = @c and idempotency_key like @k escape '\\' order by chain_seq");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("k", baseKey.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "#D%");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            int index = int.Parse(r.GetString(6)[(r.GetString(6).LastIndexOf("#D", StringComparison.Ordinal) + 2)..], CultureInfo.InvariantCulture) - 1;
            rows.Add(new DropshipConfirmationLineResult(rows.Count + 1, lines[index].LineRef, r.GetGuid(0), r.GetString(1), r.GetInt64(2), Canonical.FormatHex32((byte[])r[3]), r.GetGuid(4), r.GetDecimal(5), 0m));
        }
        return new DropshipConfirmationResult(rows);
    }
}
