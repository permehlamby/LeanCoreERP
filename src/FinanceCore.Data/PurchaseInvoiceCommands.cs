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
/// purchase-invoices:post (11, 7.2), first slice. The supplier's invoice is matched and posted in one transaction of the posting role: the order rows first (11, 9), then the pipeline of 06 with
/// its own locks (counter, LEDGER head, period), then the GR/IR items of the receipts in id order and the CLEARING head. The journal document is an AP_INVOICE with origin ORDER whose
/// detail_digest is the digest of the link records (ADR-052).
/// Built: THREE_WAY lines for stock items (quantity at most received less returned, allocation to the named receipt lines or the oldest ones with uninvoiced quantity, Dr GR/IR at the invoice
/// value, the GR/IR credit of each receipt line cleared against it by a clearing of method AUTO_MATCH), and lines for service and non-stock items booked to PURCHASE_EXPENSE (quantity at most
/// ordered plus the line over tolerance). Header: the control total within the tax tolerance (MATCH.GROSS_MISMATCH), the unique supplier invoice number, the supplier's tax per line within the
/// tolerance of the pipeline. Built choices are in 07, 13.2ad. Not built: a price difference (a stock line whose invoice value differs from the receipt value is LIMIT.INVOICE_PRICE_DIFFERENCE_NOT_BUILT,
/// 10, 6.6 needs the COST_ADJUST movement), TWO_WAY stock lines, direct receipts, charges, foreign currency, credit notes, the reversal, receipts of service lines.
/// </summary>
public sealed partial class PurchaseInvoiceCommands(NpgsqlDataSource source)
{
    private readonly JournalDocumentPoster _poster = new(source);
    private const string CommandName = "purchase-invoices:post";

    private static CommandError Error(string code, string phase, string path, params (string Key, object? Value)[] parameters) =>
        JournalDocumentPoster.Error(code, phase, path, parameters);

    private static CommandResult<PurchaseInvoiceResult> Reject(CommandError e) => CommandResult<PurchaseInvoiceResult>.Rejected([e]);

    /// <summary>Thrown inside the callback of the poster when the match fails on a fact that is only known under the locks; the transaction is not committed.</summary>
    private sealed class MatchRejection(CommandError error) : Exception("match rejected")
    {
        public CommandError Error { get; } = error;
    }

    private sealed record OrderLine(
        Guid OrderId, int LineNo, Guid ItemId, string ItemType, decimal BaseQuantity, decimal UnitPrice, decimal PriceUnitQuantity, decimal DiscountPct, Guid? TaxClassId, Guid? Warehouse,
        string Origin, string Destination, string MatchMode, decimal OverTolerancePct, string LineStatus, Guid ItemGroup, string UnitCode, decimal Quantity);

    /// <summary>A receipt line: of a purchase receipt, or (Direct) a receipt movement of a stock document posted without an order (D3), whose id and line then take the place of the receipt id and line number.</summary>
    private sealed record ReceiptLine(Guid ReceiptId, int LineNo, decimal BaseQuantity, decimal? ValueLc, Guid? StockDocumentId, int? StockDocumentLineNo, DateOnly ReceiptDate, bool Direct = false, bool CoverageInBase = false);

    private sealed class Link(Guid? receiptId, int? receiptLineNo, decimal baseQuantity, decimal net, ReceiptLine? receipt)
    {
        /// <summary>The receipt value of the link, the difference to the invoice value and its parts on hand and consumed (local currency), and where the on-hand part goes (7.2).</summary>
        public decimal ReceiptLc { get; set; }
        public decimal Diff { get; set; }
        public decimal OnHand { get; set; }
        public decimal Consumed { get; set; }
        public Guid? Warehouse { get; set; }
        public Guid? Location { get; set; }
        public bool Direct => Receipt?.Direct == true;
        public Guid? ReceiptId { get; } = receiptId;
        public int? ReceiptLineNo { get; } = receiptLineNo;
        public decimal BaseQuantity { get; } = baseQuantity;
        public decimal Net { get; set; } = net;
        public ReceiptLine? Receipt { get; } = receipt;
    }

    private sealed record Work(int Index, PurchaseInvoiceLineInput Input, OrderLine Line, string MatchMode, List<Link> Links, Guid Account, bool Stock)
    {
        public bool Direct => Input.ReceiptRef is not null;
        /// <summary>A stock line that is matched to receipt lines (and not one that waits for its receipt, 11, 7.2).</summary>
        public bool Matched => Stock && Links.Any(k => k.Receipt is not null);
    }

    public async Task<CommandResult<PurchaseInvoiceResult>> PostAsync(Guid tenantId, CommandContext ctx, PostPurchaseInvoiceRequest req, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(PurchaseInvoiceRules.Permission)) return Reject(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", PurchaseInvoiceRules.Permission)));
        var errors = MasterSupport.ValidateContext(ctx);
        void Invalid(string path, string reason) => errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", path, ("reason", reason)));
        if (req.CompanyId == Guid.Empty) Invalid("/company_id", "required");
        if (req.SupplierPartnerId == Guid.Empty) Invalid("/supplier_partner_id", "required");
        if (string.IsNullOrWhiteSpace(req.SupplierInvoiceNumber) || req.SupplierInvoiceNumber.Length > 100) Invalid("/supplier_invoice_number", "required, at most 100 characters");
        if (string.IsNullOrWhiteSpace(req.Currency) || req.Currency.Length != 3) Invalid("/currency", "a currency code");
        if (req.Lines is null) req = req with { Lines = [] };
        if (req.Lines.Count == 0 && req.Charges is null or { Count: 0 }) errors.Add(Error("SCHEMA.REQUIRED_FIELD_MISSING", "P1", "/lines"));
        for (int i = 0; i < (req.Charges?.Count ?? 0); i++)
        {
            PurchaseChargeInput c = req.Charges![i];
            string p = $"/charges/{i}";
            if (string.IsNullOrWhiteSpace(c.LineRef) || c.LineRef.Length > 90) Invalid(p + "/line_ref", "required, at most 90 characters");
            else if (req.Lines.Any(x => x.LineRef == c.LineRef) || req.Charges.Take(i).Any(x => x.LineRef == c.LineRef)) errors.Add(Error("SCHEMA.DUPLICATE_LINE_REF", "P1", p + "/line_ref", ("line_ref", c.LineRef)));
            if (string.IsNullOrWhiteSpace(c.Description) || c.Description.Length > 200) Invalid(p + "/description", "required, at most 200 characters");
            if (c.NetAmount == 0m || Math.Round(c.NetAmount, 4) != c.NetAmount) Invalid(p + "/net_amount", "not 0, at most 4 fraction digits");
            if (c.TaxAmount is null) errors.Add(Error("SCHEMA.REQUIRED_FIELD_MISSING", "P1", p + "/tax_amount"));
            if (!PurchaseInvoiceRules.ChargeTreatments.Contains(c.Treatment)) Invalid(p + "/treatment", "EXPENSE or LANDED_COST");
        }
        for (int i = 0; i < (req.Lines?.Count ?? 0); i++)
        {
            PurchaseInvoiceLineInput l = req.Lines![i];
            string p = $"/lines/{i}";
            if (string.IsNullOrWhiteSpace(l.LineRef) || l.LineRef.Length > 90) Invalid(p + "/line_ref", "required, at most 90 characters");
            else if (req.Lines.Take(i).Any(x => x.LineRef == l.LineRef)) errors.Add(Error("SCHEMA.DUPLICATE_LINE_REF", "P1", p + "/line_ref", ("line_ref", l.LineRef)));
            bool hasOrder = l.PurchaseOrderId != Guid.Empty && l.PurchaseOrderLineNo >= 1, hasReceipt = l.ReceiptRef is { StockDocumentId: var sd, LineNo: >= 1 } && sd != Guid.Empty;
            if (hasOrder == hasReceipt || (l.ReceiptRef is not null && !hasReceipt)) errors.Add(Error("ORDER.INVOICE_LINE_REFERENCE_MISSING", "P1", p + "/purchase_order_id", ("line_ref", l.LineRef)));
            else if (hasReceipt && l.ReceiptLineRefs is { Count: > 0 }) Invalid(p + "/receipt_line_refs", "a line with a receipt reference names no receipt lines");
            if (l.Quantity <= 0m || Math.Round(l.Quantity, 6) != l.Quantity) Invalid(p + "/quantity", "more than 0, at most 6 fraction digits");
            if (l.UnitPrice < 0m || Math.Round(l.UnitPrice, 4) != l.UnitPrice) Invalid(p + "/unit_price", "not negative, at most 4 fraction digits");
            if (Math.Round(l.NetAmount, 4) != l.NetAmount) Invalid(p + "/net_amount", "at most 4 fraction digits");
            if (l.TaxAmount is null) errors.Add(Error("SCHEMA.REQUIRED_FIELD_MISSING", "P1", p + "/tax_amount"));
            if (l.ReceiptLineRefs?.Any(r => r.ReceiptId == Guid.Empty || r.ReceiptLineNo < 1) == true) Invalid(p + "/receipt_line_refs", "a receipt and a line number");
        }
        if (req.SourceDocumentRef?.Length > 100) Invalid("/source_document_ref", "at most 100 characters");
        if (errors.Count > 0) return CommandResult<PurchaseInvoiceResult>.Rejected(errors);
        byte[] fingerprint = PurchaseInvoiceRules.Fingerprint(req);

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Post, ct: ct);
                CommandSupport.EarlierCommand? earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
                if (earlier is not null)
                {
                    if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint)) return Reject(Error("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
                    return earlier.EntityId is null
                        ? CommandResult<PurchaseInvoiceResult>.Failed(Error("SYSTEM.REPLAY_RESULT_MISSING", "P1", ""))
                        : CommandResult<PurchaseInvoiceResult>.Ok(await LoadAsync(tx, tenantId, req.CompanyId, earlier.EntityId.Value, ct), earlier.CommandLogId, replay: true);
                }
                return await PostCoreAsync(tx, tenantId, ctx, req, fingerprint, ct);
            }
            catch (MatchRejection ex)
            {
                return Reject(ex.Error);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.TableName == "command_log" && attempt == 0)
            {
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.TableName == "purchase_invoice")
            {
                return Reject(Error("ORDER.DUPLICATE_SUPPLIER_INVOICE", "P2", "/supplier_invoice_number", ("supplier_invoice_number", req.SupplierInvoiceNumber)));
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                return CommandResult<PurchaseInvoiceResult>.Failed(new CommandError("SYSTEM.LOCK_TIMEOUT", "P7", "", null, true));
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure)
            {
                return CommandResult<PurchaseInvoiceResult>.Failed(new CommandError("SYSTEM.CONFLICT", "P7", "", null, true));
            }
        }
    }

    /// <summary>icId and icCounterpart make it the AP side of an IC pair (11, 6.7). owned false: the transaction is the caller's, no log row and no commit.</summary>
    internal async Task<CommandResult<PurchaseInvoiceResult>> PostCoreAsync(
        TenantTransaction tx, Guid tenantId, CommandContext ctx, PostPurchaseInvoiceRequest req, byte[] fingerprint, CancellationToken ct, Guid? icId = null, Guid? icCounterpart = null, bool owned = true)
    {
        Guid companyId = req.CompanyId;
        var company = await JournalDocumentPoster.ReadCompanyAsync(tx, tenantId, companyId, ct);
        if (company is null) return Reject(Error("CONFIG.INVALID_REFERENCE", "P2", "/company_id", ("company_id", companyId)));
        if (company.Status != "ACTIVE") return Reject(Error("COMPANY.NOT_ACTIVE", "P2", "/company_id", ("status", company.Status)));
        DateOnly postingDate = req.PostingDate ?? req.InvoiceDate;
        var errors = new List<CommandError>();

        // position 1 of the lock order: the order rows, sorted
        var orderIds = req.Lines.Where(l => l.ReceiptRef is null).Select(l => l.PurchaseOrderId).Distinct().Order().ToArray();
        var orders = new Dictionary<Guid, (string Status, Guid Supplier, string Currency, Guid? Terms)>();
        await using (var cmd = tx.Command(
            "select purchase_order_id, lifecycle_status, supplier_partner_id, order_currency, payment_terms_id from core.purchase_order where tenant_id = @t and company_id = @c and purchase_order_id = any(@ids) order by purchase_order_id for update"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("ids", orderIds);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) orders[r.GetGuid(0)] = (r.GetString(1), r.GetGuid(2), r.GetString(3).Trim(), r.IsDBNull(4) ? null : r.GetGuid(4));
        }
        for (int i = 0; i < req.Lines.Count; i++)
        {
            if (req.Lines[i].ReceiptRef is not null) continue;
            if (!orders.TryGetValue(req.Lines[i].PurchaseOrderId, out var o)) errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", $"/lines/{i}/purchase_order_id", ("order_id", req.Lines[i].PurchaseOrderId)));
            else if (o.Status == "CANCELLED") errors.Add(Error("ORDER.ORDER_NOT_OPEN", "P2", $"/lines/{i}/purchase_order_id", ("status", o.Status)));
            else if (o.Supplier != req.SupplierPartnerId) errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", $"/lines/{i}/purchase_order_id", ("reason", "the order belongs to another supplier")));
        }
        if (errors.Count > 0) return CommandResult<PurchaseInvoiceResult>.Rejected(errors);
        if (orders.Count > 0 && orders.Values.Select(o => (o.Currency, o.Terms)).Distinct().Count() != 1)
            return Reject(Error("SCHEMA.INVALID_VALUE", "P2", "/lines", ("reason", "the orders of one invoice share the currency and the payment terms")));
        // an invoice of direct receipts only has no order to take the currency and the terms from
        var (_, supplier, currency, terms) = orders.Count > 0 ? orders.Values.First() : ("", req.SupplierPartnerId, req.Currency, (Guid?)null);
        if (req.Currency != currency) return Reject(Error("SCHEMA.INVALID_VALUE", "P2", "/currency", ("reason", "the currency of the invoice equals the order currency of the matched lines (7.2)"), ("order_currency", currency)));
        if (currency != company.LocalCurrency) return Reject(Error("LIMIT.INVOICE_FOREIGN_CURRENCY_NOT_BUILT", "P2", "/currency", ("currency", currency), ("local_currency", company.LocalCurrency)));
        await using (var cmd = tx.Command("select restriction_status from core.business_partner where tenant_id = @t and partner_id = @p"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("p", supplier);
            if (await cmd.ExecuteScalarAsync(ct) as string == "RESTRICTED") return Reject(Error("MASTER.PARTNER_RESTRICTED", "P2", "/supplier_partner_id", ("partner_id", supplier)));
        }
        await using (var cmd = tx.Command("select exists (select 1 from core.purchase_invoice where tenant_id = @t and company_id = @c and supplier_partner_id = @p and supplier_invoice_number = @n and invoice_kind in ('INVOICE', 'CREDIT_NOTE'))"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("p", supplier); cmd.Parameters.AddWithValue("n", req.SupplierInvoiceNumber);
            if ((bool)(await cmd.ExecuteScalarAsync(ct))!) return Reject(Error("ORDER.DUPLICATE_SUPPLIER_INVOICE", "P2", "/supplier_invoice_number", ("supplier_invoice_number", req.SupplierInvoiceNumber)));
        }
        decimal taxTolerance = 0m;
        await using (var cmd = tx.Command("select tax_amount_tolerance from core.company_setting_version where tenant_id = @t and company_id = @c and effective_from <= now() order by effective_from desc limit 1"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId);
            if (await cmd.ExecuteScalarAsync(ct) is decimal tol) taxTolerance = tol;
        }
        decimal declared = req.Lines.Sum(l => l.NetAmount + l.TaxAmount!.Value) + (req.Charges ?? []).Sum(c => c.NetAmount + c.TaxAmount!.Value);
        if (Math.Abs(declared - req.GrossAmount) > taxTolerance) return Reject(Error("MATCH.GROSS_MISMATCH", "P4", "/gross_amount", ("gross_amount", req.GrossAmount), ("lines_total", declared), ("tolerance", taxTolerance)));
        bool allowBeforeReceipt = false;
        await using (var cmd = tx.Command("select allow_invoice_before_receipt from core.company_order_setting_version where tenant_id = @t and company_id = @c and effective_from <= @d order by effective_from desc limit 1"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", postingDate);
            if (await cmd.ExecuteScalarAsync(ct) is bool b) allowBeforeReceipt = b;
        }

        var apAccount = await AccountDeterminationResolver.ResolveAsync(tx, tenantId, companyId, "AP_CONTROL", null, null, supplier, null, postingDate, ct);
        if (apAccount is null) return Reject(Error("ORDER.ACCOUNT_DETERMINATION_MISSING", "P5", "/lines", ("role_code", "AP_CONTROL"), ("partner_id", supplier)));

        // the lines under the lock
        var work = new List<Work>();
        var running = new Dictionary<(Guid, int), decimal>();
        var receiptRunning = new Dictionary<(Guid, int), decimal>();
        for (int i = 0; i < req.Lines.Count; i++)
        {
            PurchaseInvoiceLineInput l = req.Lines[i];
            string p = $"/lines/{i}";
            ReceiptLine? directReceipt = null;
            OrderLine? ol;
            if (l.ReceiptRef is { } rr)
            {
                directReceipt = await ReadDirectReceiptAsync(tx, tenantId, companyId, rr.StockDocumentId, rr.LineNo, ct);
                if (directReceipt is null) { errors.Add(Error("MATCH.NO_RECEIPT", "P2", p + "/receipt_ref", ("stock_document_id", rr.StockDocumentId), ("line_no", rr.LineNo), ("reason", "a receipt of a stock document posted without an order"))); continue; }
                ol = await DirectOrderLineAsync(tx, tenantId, companyId, supplier, directReceipt, ct);
                if (ol is null) { errors.Add(Error("MATCH.NO_RECEIPT", "P2", p + "/receipt_ref", ("reason", "the receipt is not of a stock item"))); continue; }
            }
            else ol = await ReadOrderLineAsync(tx, tenantId, companyId, l.PurchaseOrderId, l.PurchaseOrderLineNo, ct);
            if (ol is null) { errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", p + "/purchase_order_line_no", ("line_no", l.PurchaseOrderLineNo))); continue; }
            if (ol.LineStatus == "CANCELLED") { errors.Add(Error("ORDER.LINE_NOT_OPEN", "P2", p + "/purchase_order_line_no", ("line_no", l.PurchaseOrderLineNo), ("status", ol.LineStatus))); continue; }
            bool stock = ol.ItemType == "GOODS";
            string mode = stock ? ol.MatchMode : "TWO_WAY";
            if (stock && mode == "TWO_WAY" && directReceipt is null && !allowBeforeReceipt) { errors.Add(Error("MATCH.TWO_WAY_NOT_ALLOWED", "P2", p + "/purchase_order_line_no", ("line_no", l.PurchaseOrderLineNo))); continue; }
            if (!stock && l.ReceiptLineRefs is { Count: > 0 }) { errors.Add(Error("MATCH.NO_RECEIPT", "P2", p + "/receipt_line_refs", ("reason", "a service or non-stock line is not matched to a receipt line"))); continue; }

            var key = directReceipt is null ? (l.PurchaseOrderId, l.PurchaseOrderLineNo) : (directReceipt.ReceiptId, directReceipt.LineNo);
            decimal received = directReceipt is not null ? directReceipt.BaseQuantity : await SumAsync(tx,
                "select coalesce(sum(x.base_quantity), 0) from core.purchase_receipt_line x join core.purchase_receipt h on h.tenant_id = x.tenant_id and h.company_id = x.company_id and h.receipt_id = x.receipt_id " +
                "where x.tenant_id = @t and x.company_id = @c and x.purchase_order_id = @o and x.purchase_order_line_no = @n and h.status = 'posted' and h.reversal_of_receipt_id is null", tenantId, companyId, key, ct);
            decimal returned = directReceipt is not null ? 0m : await SumAsync(tx,
                "select coalesce(sum(x.base_quantity), 0) from core.purchase_return_line x join core.purchase_return h on h.tenant_id = x.tenant_id and h.company_id = x.company_id and h.return_id = x.return_id " +
                "where x.tenant_id = @t and x.company_id = @c and x.purchase_order_id = @o and x.purchase_order_line_no = @n and h.status = 'posted' and h.reversal_of_return_id is null", tenantId, companyId, key, ct);
            if (stock && returned > 0m) { errors.Add(Error("LIMIT.INVOICE_AFTER_RETURN_NOT_BUILT", "P2", p + "/purchase_order_line_no", ("line_no", l.PurchaseOrderLineNo), ("returned", returned))); continue; }
            decimal invoiced = (directReceipt is not null ? await InvoicedByReceiptLineAsync(tx, tenantId, companyId, directReceipt.ReceiptId, directReceipt.LineNo, true, ct) : await InvoicedAsync(tx, tenantId, companyId, key, ct)) + running.GetValueOrDefault(key);
            decimal open = PurchaseInvoiceRules.OpenToInvoice(mode, ol.BaseQuantity, ol.OverTolerancePct, received, returned, invoiced);

            // the quantity is in the unit of the order line, so it is the base quantity of the order line scaled by its own factor
            decimal factor = ol.BaseQuantity / ol.Quantity;
            decimal baseQty = l.Quantity * factor;
            if (Math.Round(baseQty, 6) != baseQty) { errors.Add(Error("INVENTORY.QUANTITY_SCALE_EXCEEDED", "P1", p + "/quantity", ("base_quantity", baseQty))); continue; }
            if (baseQty > open)
            {
                errors.Add(Error(stock && mode == "THREE_WAY" ? "MATCH.QUANTITY_EXCEEDS_RECEIVED" : "MATCH.QUANTITY_EXCEEDS_ORDERED", "P4", p + "/quantity", ("base_quantity", baseQty), ("open_to_invoice", open), ("invoiced", invoiced), ("received", received)));
                continue;
            }

            // the allocation to receipt lines (stock lines): the named ones, otherwise the oldest with uninvoiced quantity
            // a TWO_WAY stock line is matched to the receipt lines that are received and not invoiced; the quantity that is not received yet is posted to GR/IR and waits for its receipt (7.2)
            bool waiting = false;
            if (stock && mode == "TWO_WAY" && directReceipt is null)
            {
                decimal avail = 0m;
                foreach (ReceiptLine c in await ReadReceiptLinesAsync(tx, tenantId, companyId, l.PurchaseOrderId, l.PurchaseOrderLineNo, ct))
                    avail += Math.Max(0m, c.BaseQuantity - (await InvoicedByReceiptLineAsync(tx, tenantId, companyId, c.ReceiptId, c.LineNo, false, ct)
                        + (c.CoverageInBase ? 0m : await CoveredByReceiptLineAsync(tx, tenantId, companyId, c.ReceiptId, c.LineNo, ct)) + receiptRunning.GetValueOrDefault((c.ReceiptId, c.LineNo))));
                if (baseQty > avail)
                {
                    if (avail > 0m) { errors.Add(Error("LIMIT.INVOICE_TWO_WAY_MIXED_NOT_BUILT", "P2", p + "/quantity", ("base_quantity", baseQty), ("received_not_invoiced", avail))); continue; }
                    waiting = true;
                }
            }
            var links = new List<Link>();
            if (stock && !waiting)
            {
                var candidates = directReceipt is not null ? [directReceipt] : await ReadReceiptLinesAsync(tx, tenantId, companyId, l.PurchaseOrderId, l.PurchaseOrderLineNo, ct);
                if (l.ReceiptLineRefs is { Count: > 0 } named)
                {
                    var byKey = candidates.ToDictionary(c => (c.ReceiptId, c.LineNo));
                    var ordered = new List<ReceiptLine>();
                    bool bad = false;
                    foreach (ReceiptLineRef n in named)
                    {
                        if (!byKey.TryGetValue((n.ReceiptId, n.ReceiptLineNo), out ReceiptLine? c)) { errors.Add(Error("MATCH.NO_RECEIPT", "P2", p + "/receipt_line_refs", ("receipt_id", n.ReceiptId), ("line_no", n.ReceiptLineNo))); bad = true; break; }
                        ordered.Add(c);
                    }
                    if (bad) continue;
                    candidates = ordered;
                }
                decimal left = baseQty;
                foreach (ReceiptLine c in candidates)
                {
                    decimal already = await InvoicedByReceiptLineAsync(tx, tenantId, companyId, c.ReceiptId, c.LineNo, c.Direct, ct) + (c.Direct || c.CoverageInBase ? 0m : await CoveredByReceiptLineAsync(tx, tenantId, companyId, c.ReceiptId, c.LineNo, ct)) + receiptRunning.GetValueOrDefault((c.ReceiptId, c.LineNo));
                    decimal take = Math.Min(left, Math.Max(0m, c.BaseQuantity - already));
                    if (take <= 0m) continue;
                    receiptRunning[(c.ReceiptId, c.LineNo)] = receiptRunning.GetValueOrDefault((c.ReceiptId, c.LineNo)) + take;
                    left -= take;
                    links.Add(new Link(c.ReceiptId, c.LineNo, take, 0m, c));
                    if (left == 0m) break;
                }
                if (left > 0m) { errors.Add(Error("MATCH.NO_RECEIPT", "P4", p + "/quantity", ("base_quantity", baseQty), ("not_covered", left))); continue; }
                if (links.Any(k => k.Receipt!.StockDocumentId is null || k.Receipt.ValueLc is null or 0m)) { errors.Add(Error("MATCH.NO_RECEIPT", "P2", p, ("reason", "the receipt line has no GR/IR value"))); continue; }
                // the net amount is shared by the base quantity of the receipt lines, the last one takes what is left
                decimal assigned = 0m;
                int scale = await ScaleOfAsync(tx, currency, ct);
                for (int k = 0; k < links.Count; k++)
                {
                    decimal share = k == links.Count - 1 ? l.NetAmount - assigned : Math.Round(l.NetAmount * links[k].BaseQuantity / baseQty, scale, MidpointRounding.AwayFromZero);
                    assigned += share;
                    links[k].Net = share;
                }
            }
            else links.Add(new Link(null, null, baseQty, l.NetAmount, null));

            string role = stock ? "GR_IR" : "PURCHASE_EXPENSE";
            var account = await AccountDeterminationResolver.ResolveAsync(tx, tenantId, companyId, role, ol.ItemGroup, ol.Warehouse, stock ? null : supplier, ol.ItemId, postingDate, ct);
            if (account is null) { errors.Add(Error("ORDER.ACCOUNT_DETERMINATION_MISSING", "P5", p, ("role_code", role), ("item_id", ol.ItemId))); continue; }
            running[key] = running.GetValueOrDefault(key) + baseQty;
            work.Add(new Work(i, l, ol, mode, links, account.AccountId, stock));
        }
        if (errors.Count > 0) return CommandResult<PurchaseInvoiceResult>.Rejected(errors);

        // the receipt value R of every link, from the GR/IR items as they stand now (read again under the locks, in the callbacks below), and the difference to the invoice value (7.2)
        var plan = new MatchPlan();
        var costRead = new Dictionary<Guid, (decimal Qty, decimal Value)>();
        int lcScale = await ScaleOfAsync(tx, currency, ct);
        if (work.Any(w => w.Matched))
        {
            var located = await LocateItemsAsync(tx, tenantId, companyId, work, ct);
            var items = await ReadItemsAsync(tx, tenantId, companyId, located, false, ct);
            await ComputeSharesAsync(tx, tenantId, companyId, work, located, items, plan, ct);
            foreach (Work w in work.Where(x => x.Matched))
                foreach (Link k in w.Links)
                {
                    k.ReceiptLc = -plan.Shares[k].Lc;
                    k.Diff = k.Net - k.ReceiptLc;
                    if (k.Diff == 0m) continue;
                    if (!costRead.TryGetValue(w.Line.ItemId, out var cs)) costRead[w.Line.ItemId] = cs = await CostBalanceAsync(tx, tenantId, companyId, w.Line.ItemId, ct);
                    decimal onHandQuantity = Math.Min(k.BaseQuantity, cs.Qty);
                    k.OnHand = Math.Round(k.Diff * onHandQuantity / k.BaseQuantity, lcScale, MidpointRounding.AwayFromZero);
                    k.Consumed = k.Diff - k.OnHand;
                    if (k.OnHand == 0m) continue;
                    var place = await MovementPlaceAsync(tx, tenantId, companyId, k.Receipt!, ct);
                    if (place is null) { errors.Add(Error("MATCH.NO_RECEIPT", "P2", $"/lines/{w.Index}", ("reason", "the movement of the receipt line is not found"))); continue; }
                    (k.Warehouse, k.Location) = place.Value;
                }
            if (errors.Count > 0) return CommandResult<PurchaseInvoiceResult>.Rejected(errors);
        }
        bool adjusts = work.Any(w => w.Links.Any(k => k.OnHand != 0m));

        // the finance document: a debit line per invoice line with the tax facts of the order line, the difference lines of a stock line, and the payable that the pipeline balances
        var lines = new List<RequestLine>();
        var lineNos = new Dictionary<int, LineNos>();
        foreach (Work w in work)
        {
            lines.Add(new RequestLine(w.Input.LineRef, w.Account, w.Input.NetAmount, TaxClassId: w.Line.TaxClassId, TaxDate: req.InvoiceDate, OriginCountry: w.Line.Origin, DestinationCountry: w.Line.Destination,
                TaxCodeId: w.Input.TaxCodeId, TaxAmountTc: w.Input.TaxCodeId is null && w.Input.TaxAmount == 0m ? null : w.Input.TaxAmount, OpenItem: w.Stock ? new OpenItemRequest(null, req.InvoiceDate) : null));
            var nos = new LineNos { Main = lines.Count };
            lineNos[w.Index] = nos;
            decimal diff = w.Links.Sum(k => k.Diff), onHand = w.Links.Sum(k => k.OnHand), consumed = w.Links.Sum(k => k.Consumed);
            if (diff == 0m) continue;
            // the GR/IR debit is the invoice value; what it exceeds the receipt value by goes back to GR/IR and to inventory and cost of goods sold (7.2)
            lines.Add(new RequestLine(w.Input.LineRef + "#GA", w.Account, -diff, OpenItem: new OpenItemRequest(null, req.InvoiceDate)));
            nos.Adjust = lines.Count;
            if (onHand != 0m)
            {
                var inventory = await AccountDeterminationResolver.ResolveAsync(tx, tenantId, companyId, "INVENTORY", w.Line.ItemGroup, w.Line.Warehouse, null, w.Line.ItemId, postingDate, ct);
                if (inventory is null) return Reject(Error("INVENTORY.ACCOUNT_DETERMINATION_MISSING", "P5", $"/lines/{w.Index}", ("role_code", "INVENTORY"), ("item_id", w.Line.ItemId)));
                lines.Add(new RequestLine(w.Input.LineRef + "#IV", inventory.AccountId, onHand));
            }
            if (consumed != 0m)
            {
                var cogs = await AccountDeterminationResolver.ResolveAsync(tx, tenantId, companyId, "COGS", w.Line.ItemGroup, w.Line.Warehouse, null, w.Line.ItemId, postingDate, ct);
                if (cogs is null) return Reject(Error("INVENTORY.ACCOUNT_DETERMINATION_MISSING", "P5", $"/lines/{w.Index}", ("role_code", "COGS"), ("item_id", w.Line.ItemId)));
                lines.Add(new RequestLine(w.Input.LineRef + "#CG", cogs.AccountId, consumed));
            }
        }
        // the charges of the invoice (7.2): freight, duty and fees without an order, to the expense account or to the landed cost clearing account, with the tax code and tax the supplier states
        var chargeLines = new List<(PurchaseChargeInput Charge, int JournalLine)>();
        string? supplierCountry = null;
        if (req.Charges is { Count: > 0 })
            await using (var cmd = tx.Command("select country_code from core.business_partner_pii where tenant_id = @t and partner_id = @p"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("p", supplier);
                supplierCountry = (await cmd.ExecuteScalarAsync(ct) as string)?.Trim();
            }
        foreach (PurchaseChargeInput c in req.Charges ?? [])
        {
            string chargeRole = c.Treatment == "LANDED_COST" ? "LANDED_COST_CLEARING" : "PURCHASE_EXPENSE";
            var chargeAccount = await AccountDeterminationResolver.ResolveAsync(tx, tenantId, companyId, chargeRole, null, null, c.Treatment == "LANDED_COST" ? null : supplier, null, postingDate, ct);
            if (chargeAccount is null) return Reject(Error("ORDER.ACCOUNT_DETERMINATION_MISSING", "P5", "/charges", ("role_code", chargeRole)));
            lines.Add(new RequestLine(c.LineRef, chargeAccount.AccountId, c.NetAmount, LineText: c.Description, TaxDate: req.InvoiceDate, OriginCountry: supplierCountry ?? company.Country, DestinationCountry: company.Country,
                TaxCodeId: c.TaxCodeId, TaxAmountTc: c.TaxAmount));
            chargeLines.Add((c, lines.Count));
        }
        const string ApRef = "AP";
        DateOnly? due = req.DueDate ?? (terms is null ? req.InvoiceDate : null);
        lines.Add(new RequestLine(ApRef, apAccount.AccountId, 0m, OpenItem: new OpenItemRequest(supplier, req.InvoiceDate, terms, due)));
        var journal = new PostJournalDocumentRequest(companyId, null, "AP_INVOICE", req.InvoiceDate, postingDate, currency, null, null, req.SourceDocumentRef, lines, supplier);

        Guid invoiceId = Guid.NewGuid(), journalId = Guid.NewGuid();
        Guid? stockId = adjusts ? Guid.NewGuid() : null;
        var clearingIds = work.Where(w => w.Matched).Select(w => w.Account).Distinct().ToDictionary(a => a, _ => Guid.NewGuid());
        List<Dictionary<string, object?>> linkRows = [];
        var headerDigest = new Dictionary<string, object?>
        {
            ["purchase_invoice_id"] = invoiceId, ["invoice_kind"] = "INVOICE", ["supplier_partner_id"] = supplier, ["supplier_invoice_number"] = req.SupplierInvoiceNumber, ["invoice_date"] = req.InvoiceDate,
            ["due_date"] = req.DueDate, ["currency"] = currency, ["gross_amount"] = req.GrossAmount, ["stock_document_id"] = stockId, ["credits_invoice_id"] = null, ["credit_kind"] = null,
            ["reversal_of_purchase_invoice_id"] = null, ["reason_code"] = null, ["line_count"] = work.Sum(w => w.Links.Count) + chargeLines.Count
        };
        byte[] DigestOf(JournalDocumentPoster.EmbeddedFacts facts)
        {
            linkRows = [];
            foreach (Work w in work)
            {
                TaxLineSeed? tax = facts.Tax.Lines.FirstOrDefault(t => t.Bases.Any(b => b.Index == w.Index));
                Guid? code = tax?.CodeId ?? (facts.Tax.BaseCodes.TryGetValue(w.Index, out Guid c) ? c : null);
                foreach (Link k in w.Links)
                    linkRows.Add(new Dictionary<string, object?>
                    {
                        ["line_no"] = linkRows.Count + 1, ["line_kind"] = "MATCHED", ["purchase_order_id"] = w.Direct ? null : w.Input.PurchaseOrderId, ["purchase_order_line_no"] = w.Direct ? null : w.Input.PurchaseOrderLineNo,
                        ["receipt_id"] = k.Direct ? null : k.ReceiptId, ["receipt_line_no"] = k.Direct ? null : k.ReceiptLineNo, ["stock_document_id"] = k.Direct ? k.ReceiptId : null, ["stock_document_line_no"] = k.Direct ? k.ReceiptLineNo : null, ["base_quantity"] = k.BaseQuantity,
                        ["unit_price"] = w.Input.UnitPrice, ["net_amount"] = k.Net, ["tax_amount"] = w.Links[0] == k ? w.Input.TaxAmount!.Value : 0m, ["order_unit_price"] = w.Direct ? null : w.Line.UnitPrice, ["match_mode_used"] = w.MatchMode,
                        ["tolerance_used"] = w.MatchMode == "TWO_WAY" ? w.Line.OverTolerancePct : null, ["price_difference_lc"] = k.Diff, ["on_hand_part_lc"] = k.OnHand, ["consumed_part_lc"] = k.Consumed,
                        ["dropship_part_lc"] = 0m, ["unreceived_gr_ir_lc"] = w.Stock && k.Receipt is null ? k.Net : 0m, ["gr_ir_clearing_id"] = k.ReceiptId is null ? null : clearingIds[w.Account], ["tax_code_id"] = code,
                        ["tax_code_version_id"] = tax?.VersionId, ["tax_date"] = tax?.TaxDate, ["journal_line_no"] = lineNos[w.Index].Main, ["line_ref"] = w.Input.LineRef
                    });
            }
            foreach (var (c, jl) in chargeLines)
            {
                TaxLineSeed? tax = facts.Tax.Lines.FirstOrDefault(t => t.Bases.Any(b => b.Index == jl - 1));
                Guid? code = tax?.CodeId ?? (facts.Tax.BaseCodes.TryGetValue(jl - 1, out Guid cc) ? cc : null);
                linkRows.Add(new Dictionary<string, object?>
                {
                    ["line_no"] = linkRows.Count + 1, ["line_kind"] = "CHARGE", ["purchase_order_id"] = null, ["purchase_order_line_no"] = null, ["receipt_id"] = null, ["receipt_line_no"] = null, ["stock_document_id"] = null,
                    ["stock_document_line_no"] = null, ["base_quantity"] = 0m, ["unit_price"] = 0m, ["net_amount"] = c.NetAmount, ["tax_amount"] = c.TaxAmount!.Value, ["order_unit_price"] = null, ["match_mode_used"] = null,
                    ["tolerance_used"] = null, ["price_difference_lc"] = 0m, ["on_hand_part_lc"] = 0m, ["consumed_part_lc"] = 0m, ["dropship_part_lc"] = 0m, ["unreceived_gr_ir_lc"] = 0m, ["gr_ir_clearing_id"] = null,
                    ["tax_code_id"] = code, ["tax_code_version_id"] = tax?.VersionId, ["tax_date"] = tax?.TaxDate, ["journal_line_no"] = jl, ["line_ref"] = c.LineRef, ["description"] = c.Description, ["charge_treatment"] = c.Treatment
                });
            }
            return PurchaseInvoiceRules.Digest(headerDigest, linkRows.Select(r => (IReadOnlyDictionary<string, object?>)r.Where(kv => kv.Key is not ("line_ref" or "description" or "charge_treatment")).ToDictionary(kv => kv.Key, kv => kv.Value)).ToList());
        }

        // the digest the journal document binds: the link digest alone, or with the stock document of a cost adjustment, which is prepared here under the LEDGER lock of the poster
        StockDraft? stockDraft = null;
        async Task<byte[]?> DetailAsync(JournalDocumentPoster.EmbeddedFacts facts)
        {
            byte[] link = DigestOf(facts);
            if (stockId is null) return link;
            var adjustments = work.Where(x => x.Matched).SelectMany(w => w.Links.Where(k => k.OnHand != 0m).Select(k => new AdjItem(w.Line.ItemId, k.Warehouse!.Value, k.Location, k.OnHand, w.Input.LineRef))).ToList();
            stockDraft = await PrepareStockAsync(tx, tenantId, companyId, ctx, stockId.Value, journalId, req.InvoiceDate, postingDate, currency, adjustments, costRead, link, ct);
            return CombinedDigest(link, stockDraft.RowHash);
        }

        Guid logId = Guid.Empty;
        decimal taxTotal = 0m, gross = 0m;
        async Task AfterAsync(TenantTransaction t, PostedDocument posted, JournalDocumentPoster.EmbeddedFacts facts)
        {
            taxTotal = facts.Tax.Lines.Sum(x => x.TaxTc);
            gross = -facts.Request.Lines.Single(x => x.LineRef == ApRef).AmountTc;
            await InsertHeaderAsync(t, tenantId, companyId, ctx, req, supplier, currency, invoiceId, posted.DocumentId, stockId, icId, ct);
            // the clearing first: what the invoices took of a receipt line before this one is read from the link rows, which this invoice has not inserted yet
            if (clearingIds.Count > 0) await ClearGrIrAsync(t, tenantId, companyId, ctx, work, posted, clearingIds, invoiceId, postingDate, lineNos, ct);
            foreach (var row in linkRows) await (row["line_kind"] as string == "CHARGE" ? InsertChargeLineAsync(t, tenantId, companyId, invoiceId, row, ct) : InsertLineAsync(t, tenantId, companyId, invoiceId, row, ct));
            if (stockDraft is not null) await InsertStockAsync(t, tenantId, companyId, ctx, stockDraft, req.InvoiceDate, postingDate, currency, ct);
            if (owned) logId = await CommandSupport.WriteLogAsync(t, tenantId, companyId, ctx, CommandName, fingerprint, "PURCHASE_INVOICE", invoiceId, ct);
            await CommandSupport.WriteOutboxAsync(t, tenantId, companyId, ctx, "invoice.posted", "PURCHASE_INVOICE", $"{companyId:D}/{invoiceId:D}", "POSTED", DateTime.UtcNow, isAudit: true,
                JsonSerializer.Serialize(new Dictionary<string, object?> { ["chain_type"] = "LEDGER", ["row_hash"] = posted.RowHash, ["chain_seq"] = posted.ChainSeq }),
                JsonSerializer.Serialize(new Dictionary<string, object?> { ["document_number"] = posted.DocumentNumber, ["supplier_partner_id"] = supplier, ["lines"] = linkRows.Count, ["journal_document_id"] = posted.DocumentId }), ct);
        }

        var embedding = new JournalDocumentPoster.PostEmbedding(CommandName, "ORDER", ApRef, null, WriteLog: false, Commit: false, AfterAsync, journalId, DetailAsync, IcTransactionId: icId, IcCounterpartCompanyId: icCounterpart);
        var postedResult = await _poster.PostCoreAsync(tx, tenantId, ctx, journal, fingerprint, embedding, ct);
        if (postedResult.Status != CommandStatus.Ok) return CommandResult<PurchaseInvoiceResult>.Rejected(postedResult.Errors.ToList());
        PostedDocument doc = postedResult.Value!;
        var result = new PurchaseInvoiceResult(invoiceId, doc.DocumentId, doc.DocumentNumber, work.Sum(w => w.Input.NetAmount) + chargeLines.Sum(c => c.Charge.NetAmount), taxTotal, gross, doc.RowHash,
            linkRows.Select(r => new PurchaseInvoiceLineResult((int)r["line_no"]!, (string)r["line_ref"]!, (Guid?)r["purchase_order_id"] ?? Guid.Empty, (int?)r["purchase_order_line_no"] ?? 0, (Guid?)r["receipt_id"], (int?)r["receipt_line_no"],
                (decimal)r["base_quantity"]!, (decimal)r["net_amount"]!, (decimal)r["tax_amount"]!, (string?)r["match_mode_used"] ?? "", (int)r["journal_line_no"]!, (Guid?)r["gr_ir_clearing_id"])).ToList(),
            clearingIds.Values.Cast<Guid?>().FirstOrDefault(), stockId);
        if (owned) await tx.CommitAsync(ct);   // the deferred checks of the journal document and of the clearing, the chain of the stock document, the outbox position
        return CommandResult<PurchaseInvoiceResult>.Ok(result, logId, postedResult.Warnings);
    }

    /// <summary>The journal lines of an invoice line: the main line and, when the invoice value differs from the receipt value, the GR/IR reversal of the difference.</summary>
    private sealed class LineNos
    {
        public int Main { get; set; }
        public int? Adjust { get; set; }
    }

    /// <summary>What the GR/IR items give for the links of the invoice: the share of each receipt item that a link takes, and the cleared amounts of the items after all links.</summary>
    private sealed class MatchPlan
    {
        public Dictionary<Link, Share> Shares { get; } = [];
        public Dictionary<(Guid, int), (decimal Tc, decimal Lc, decimal Gc, decimal? Rc)> Cleared { get; } = [];
    }

    /// <summary>One open item of a receipt: its line amounts and what is cleared of it.</summary>
    private sealed record GrIrItem(Guid DocumentId, int LineNo, Guid AccountId, string Currency, decimal Tc, decimal Lc, decimal Gc, decimal? Rc, decimal ClearedTc, decimal ClearedLc, decimal ClearedGc, decimal? ClearedRc, string GcCurrency, string? RcCurrency);

    private sealed record Share(GrIrItem Item, decimal Tc, decimal Lc, decimal Gc, decimal? Rc);

    /// <summary>The GR/IR open item of every receipt line the invoice takes, found through the stock document of the receipt.</summary>
    private static async Task<Dictionary<(Guid, int), (Guid Doc, int Line)>> LocateItemsAsync(TenantTransaction tx, Guid tenantId, Guid companyId, List<Work> work, CancellationToken ct)
    {
        var wanted = new Dictionary<(Guid, int), (Guid Doc, int Line)>();
        foreach (Work w in work.Where(x => x.Matched))
            foreach (Link k in w.Links)
            {
                Guid journalId = await StockJournalAsync(tx, tenantId, companyId, k.Receipt!.StockDocumentId!.Value, ct);
                int? line = await FindGrIrItemAsync(tx, tenantId, companyId, k.Receipt, journalId, ct);
                if (line is null) throw new MatchRejection(Error("MATCH.NO_RECEIPT", "P4", "/lines", ("receipt_id", k.ReceiptId), ("line_no", k.ReceiptLineNo), ("reason", "the GR/IR item of the receipt line is not found")));
                wanted[(k.ReceiptId!.Value, k.ReceiptLineNo!.Value)] = (journalId, line.Value);
            }
        return wanted;
    }

    /// <summary>The items with their amounts and cleared amounts, locked in id order when asked (7.2, locks).</summary>
    private static async Task<Dictionary<(Guid, int), GrIrItem>> ReadItemsAsync(
        TenantTransaction tx, Guid tenantId, Guid companyId, Dictionary<(Guid, int), (Guid Doc, int Line)> wanted, bool forUpdate, CancellationToken ct)
    {
        var items = new Dictionary<(Guid, int), GrIrItem>();
        foreach (var d in wanted.Values.Distinct().OrderBy(v => v.Doc).ThenBy(v => v.Line))
        {
            await using var cmd = tx.Command(
                "select l.amount_tc, l.amount_lc, l.amount_gc, l.amount_rc, l.currency_gc, l.currency_rc, oi.cleared_amount_tc, oi.cleared_amount_lc, oi.cleared_amount_gc, oi.cleared_amount_rc, d.transaction_currency, l.account_id " +
                "from core.journal_line_open_item oi join core.journal_line l on l.tenant_id = oi.tenant_id and l.company_id = oi.company_id and l.document_id = oi.document_id and l.line_no = oi.line_no " +
                "join core.journal_document d on d.tenant_id = l.tenant_id and d.company_id = l.company_id and d.document_id = l.document_id " +
                "where oi.tenant_id = @t and oi.company_id = @c and oi.document_id = @d and oi.line_no = @n" + (forUpdate ? " for update of oi" : ""));
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", d.Doc); cmd.Parameters.AddWithValue("n", d.Line);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            items[d] = new GrIrItem(d.Doc, d.Line, r.GetGuid(11), r.GetString(10).Trim(), r.GetDecimal(0), r.GetDecimal(1), r.GetDecimal(2), r.IsDBNull(3) ? null : r.GetDecimal(3), r.GetDecimal(6), r.GetDecimal(7),
                r.GetDecimal(8), r.IsDBNull(9) ? null : r.GetDecimal(9), r.GetString(4).Trim(), r.IsDBNull(5) ? null : r.GetString(5).Trim());
        }
        return items;
    }

    /// <summary>
    /// The share of a receipt item that each link takes, signed as the item (a credit is negative): proportional to the base quantity, the share that completes a receipt line takes the rest of the
    /// item, so no rounding remains.
    /// </summary>
    private static async Task ComputeSharesAsync(
        TenantTransaction tx, Guid tenantId, Guid companyId, List<Work> work, Dictionary<(Guid, int), (Guid Doc, int Line)> wanted, Dictionary<(Guid, int), GrIrItem> items, MatchPlan plan, CancellationToken ct)
    {
        int gcScale = await ScaleOfAsync(tx, items.Values.First().GcCurrency, ct);
        int? rcScale = items.Values.First().RcCurrency is { } rcCurrency ? await ScaleOfAsync(tx, rcCurrency, ct) : null;
        var running = new Dictionary<(Guid, int), decimal>();
        foreach (Work w in work.Where(x => x.Matched))
            foreach (Link k in w.Links)
            {
                var key = (k.ReceiptId!.Value, k.ReceiptLineNo!.Value);
                GrIrItem item = items[wanted[key]];
                (decimal Tc, decimal Lc, decimal Gc, decimal? Rc) so = plan.Cleared.TryGetValue(wanted[key], out var seen) ? seen : (item.ClearedTc, item.ClearedLc, item.ClearedGc, item.ClearedRc);
                decimal before = await InvoicedByReceiptLineAsync(tx, tenantId, companyId, k.ReceiptId.Value, k.ReceiptLineNo.Value, k.Direct, ct);
                running[key] = running.GetValueOrDefault(key) + k.BaseQuantity;
                bool completes = before + running[key] >= k.Receipt!.BaseQuantity;
                decimal ratio = k.BaseQuantity / k.Receipt.BaseQuantity;
                decimal tc = completes ? item.Tc - so.Tc : Math.Round(item.Tc * ratio, 4, MidpointRounding.AwayFromZero);
                decimal lc = completes ? item.Lc - so.Lc : Math.Round(item.Lc * ratio, 4, MidpointRounding.AwayFromZero);
                decimal gc = completes ? item.Gc - so.Gc : Math.Round(item.Gc * ratio, gcScale, MidpointRounding.AwayFromZero);
                decimal? rc = item.Rc is null ? null : completes ? item.Rc - so.Rc : Math.Round(item.Rc.Value * ratio, rcScale ?? 4, MidpointRounding.AwayFromZero);
                plan.Cleared[wanted[key]] = (so.Tc + tc, so.Lc + lc, so.Gc + gc, rc is null ? null : (so.Rc ?? 0m) + rc);
                plan.Shares[k] = new Share(item, tc, lc, gc, rc);
            }
    }

    /// <summary>
    /// The GR/IR match of 7.2 (03): per GR/IR account, under the CLEARING head, the GR/IR credit of each receipt line the invoice takes is cleared by the share the invoice takes, against the GR/IR
    /// debit lines of the invoice (the main line at the invoice value and, with a price difference, the line that gives the difference back), in one clearing of method AUTO_MATCH. The items net to
    /// zero in the transaction and local currency; the group and reporting amounts of the main item are those that make the clearing net to zero too.
    /// </summary>
    private static async Task ClearGrIrAsync(
        TenantTransaction tx, Guid tenantId, Guid companyId, CommandContext ctx, List<Work> work, PostedDocument invoice, Dictionary<Guid, Guid> clearingIds, Guid invoiceId, DateOnly date,
        Dictionary<int, LineNos> lineNos, CancellationToken ct)
    {
        // the receipt items, locked in id order and read under the lock; the shares are worked out again and must be those the difference was made from
        var wanted = await LocateItemsAsync(tx, tenantId, companyId, work, ct);
        var items = await ReadItemsAsync(tx, tenantId, companyId, wanted, true, ct);
        var plan = new MatchPlan();
        await ComputeSharesAsync(tx, tenantId, companyId, work, wanted, items, plan, ct);
        foreach (Work w in work.Where(x => x.Matched))
            foreach (Link k in w.Links)
                if (-plan.Shares[k].Lc != k.ReceiptLc) throw new MatchRejection(new CommandError("SYSTEM.CONFLICT", "P7", "", null, true));

        (long headSeq, byte[] headHash, _) = await CommandSupport.LockOrCreateHeadAsync(tx, tenantId, companyId, ClearingHash2.ChainType, "", LedgerHash2.Genesis(tenantId, companyId, ClearingHash2.ChainType, ""), ct);
        DateTime at = DateTime.UtcNow;
        at = new DateTime(at.Ticks - at.Ticks % 10, DateTimeKind.Utc);

        foreach (var group in work.Where(x => x.Matched).GroupBy(x => x.Account).OrderBy(g => g.Key))
        {
            var shares = new Dictionary<(Guid, int), (GrIrItem Item, decimal Tc, decimal Lc, decimal Gc, decimal? Rc)>();
            var invoiceItems = new List<(int LineNo, string Currency, decimal Tc, decimal Lc, decimal Gc, decimal? Rc)>();
            foreach (Work w in group)
            {
                decimal sumTc = 0m, sumLc = 0m, sumGc = 0m; decimal? sumRc = 0m;
                foreach (Link k in w.Links)
                {
                    Share s = plan.Shares[k];
                    if (s.Item.AccountId != group.Key) throw new MatchRejection(Error("LIMIT.INVOICE_PRICE_DIFFERENCE_NOT_BUILT", "P4", $"/lines/{w.Index}", ("reason", "the GR/IR account of the invoice line differs from that of the receipt"), ("line_ref", w.Input.LineRef)));
                    var key = (s.Item.DocumentId, s.Item.LineNo);
                    shares[key] = shares.TryGetValue(key, out var have) ? (s.Item, have.Tc + s.Tc, have.Lc + s.Lc, have.Gc + s.Gc, have.Rc is null ? null : have.Rc + s.Rc) : (s.Item, s.Tc, s.Lc, s.Gc, s.Rc);
                    sumTc -= s.Tc; sumLc -= s.Lc; sumGc -= s.Gc; sumRc = sumRc is null || s.Rc is null ? null : sumRc - s.Rc;
                }
                // the invoice lines are cleared in full by what the shares sum to: a difference in the transaction or local amount means the match is not what the difference was made from
                LineNos nos = lineNos[w.Index];
                PostedLine main = invoice.Lines.Single(x => x.LineNo == nos.Main);
                PostedLine? adjust = nos.Adjust is { } an ? invoice.Lines.Single(x => x.LineNo == an) : null;
                if (main.AmountTc + (adjust?.AmountTc ?? 0m) != sumTc || main.AmountLc + (adjust?.AmountLc ?? 0m) != sumLc)
                    throw new MatchRejection(Error("LIMIT.INVOICE_PRICE_DIFFERENCE_NOT_BUILT", "P4", $"/lines/{w.Index}", ("invoice_value_lc", main.AmountLc + (adjust?.AmountLc ?? 0m)), ("receipt_value_lc", sumLc), ("line_ref", w.Input.LineRef)));
                // the group and reporting amounts of the main item are what is left of the shares after the adjustment line, so the clearing nets to zero in them
                invoiceItems.Add((main.LineNo, main.CurrencyLc, main.AmountTc, main.AmountLc, sumGc - (adjust?.AmountGc ?? 0m), sumRc is null ? null : sumRc - (adjust?.AmountRc ?? 0m)));
                if (adjust is not null) invoiceItems.Add((adjust.LineNo, adjust.CurrencyLc, adjust.AmountTc, adjust.AmountLc, adjust.AmountGc, adjust.AmountRc));
            }

            Guid clearingId = clearingIds[group.Key];
            long seq = headSeq + 1;
            string baseKey = ctx.IdempotencyKey.Length > 90 ? ctx.IdempotencyKey[..90] : ctx.IdempotencyKey;
            CommandContext cctx = ctx with { IdempotencyKey = $"{baseKey}#C{clearingIds.Keys.Order().ToList().IndexOf(group.Key) + 1}" };
            var all = new List<(Guid Doc, int Line, string Currency, decimal Tc, decimal Lc, decimal Gc, decimal? Rc)>();
            foreach (var s in shares.Values.OrderBy(s => s.Item.DocumentId).ThenBy(s => s.Item.LineNo)) all.Add((s.Item.DocumentId, s.Item.LineNo, s.Item.Currency, s.Tc, s.Lc, s.Gc, s.Rc));
            foreach (var ii in invoiceItems.OrderBy(i => i.LineNo)) all.Add((invoice.DocumentId, ii.LineNo, ii.Currency, ii.Tc, ii.Lc, ii.Gc, ii.Rc));
            string matchingRef = invoiceId.ToString("D");
            var body = new Dictionary<string, object?>
            {
                ["clearing_id"] = clearingId, ["clearing_type"] = "CLEAR", ["clearing_date"] = date, ["fiscal_year"] = invoice.FiscalYear, ["fiscal_period"] = invoice.FiscalPeriod, ["cleared_at"] = at,
                ["account_id"] = group.Key, ["partner_id"] = null, ["document_id"] = null, ["reset_of_clearing_id"] = null, ["clearing_method"] = "AUTO_MATCH", ["matching_ref"] = matchingRef,
                ["source_system"] = ctx.SourceSystem, ["idempotency_key"] = cctx.IdempotencyKey, ["command_name"] = CommandName, ["cleared_by"] = ctx.Actor, ["correlation_id"] = ctx.CorrelationId
            };
            var itemRows = all.Select((i, k) => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["tenant_id"] = tenantId, ["company_id"] = companyId, ["clearing_id"] = clearingId, ["item_no"] = k + 1, ["clearing_date"] = date, ["open_item_document_id"] = i.Doc, ["open_item_line_no"] = i.Line,
                ["currency_tc"] = i.Currency, ["cleared_amount_tc"] = i.Tc, ["cleared_amount_lc"] = i.Lc, ["cleared_amount_gc"] = i.Gc, ["cleared_amount_rc"] = i.Rc
            }).ToList();
            byte[] rowHash = ClearingHash2.RowHash(tenantId, companyId, seq, headHash, body, ClearingHash2.ItemsDigest(itemRows), null);
            var synthetic = new PostClearingRequest(companyId, group.Key, null, date, "AUTO_MATCH", matchingRef, null, []);
            await ClearingPoster.InsertClearingAsync(tx, tenantId, synthetic, cctx, clearingId, null, seq, invoice.FiscalYear, invoice.FiscalPeriod, at, headHash, rowHash, "CLEAR", null, CommandName, ct);
            for (int k = 0; k < all.Count; k++)
                await ClearingPoster.InsertItemAsync(tx, tenantId, synthetic, clearingId, k + 1, all[k].Doc, all[k].Line, all[k].Currency, all[k].Tc, all[k].Lc, all[k].Gc, all[k].Rc, ct);
            foreach (var s in shares.Values)
            {
                var total = plan.Cleared[(s.Item.DocumentId, s.Item.LineNo)];
                string status = total.Tc == s.Item.Tc && total.Lc == s.Item.Lc ? "CLEARED" : "PARTIAL";
                await ClearingPoster.UpdateOpenItemAsync(tx, tenantId, companyId, s.Item.DocumentId, s.Item.LineNo, clearingId, status, total.Tc, total.Lc, total.Gc, total.Rc, ct);
            }
            foreach (var ii in invoiceItems)
                await ClearingPoster.UpdateOpenItemAsync(tx, tenantId, companyId, invoice.DocumentId, ii.LineNo, clearingId, "CLEARED", ii.Tc, ii.Lc, ii.Gc, ii.Rc, ct);
            await ClearingPoster.MoveHeadAsync(tx, tenantId, companyId, ClearingHash2.ChainType, "", seq, rowHash, ClearingHash2.Version, ct);
            await CommandSupport.WriteOutboxAsync(tx, tenantId, companyId, ctx, "clearing.posted", "CLEARING", $"{companyId:D}/{clearingId:D}", "POSTED", at, isAudit: true,
                JsonSerializer.Serialize(new Dictionary<string, object?> { ["chain_type"] = "CLEARING", ["scope"] = "", ["chain_seq"] = seq, ["row_hash"] = Canonical.FormatHex32(rowHash) }),
                JsonSerializer.Serialize(new Dictionary<string, object?> { ["clearing_id"] = clearingId, ["partner_id"] = null, ["account_id"] = group.Key, ["items"] = all.Count }), ct);
            headSeq = seq; headHash = rowHash;
        }
    }

    /// <summary>The journal document that the stock document of a receipt derived.</summary>
    private static async Task<Guid> StockJournalAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid stockDocumentId, CancellationToken ct)
    {
        await using var cmd = tx.Command("select journal_document_id from core.stock_document where tenant_id = @t and company_id = @c and stock_document_id = @s");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("s", stockDocumentId);
        return (Guid)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>
    /// The line of the GR/IR open item of a receipt line: the derived journal has one open item without partner per valued movement line (10, 7), in the order of the movement lines, so the
    /// item is found at the position of the movement among the valued ones.
    /// </summary>
    private static async Task<int?> FindGrIrItemAsync(TenantTransaction tx, Guid tenantId, Guid companyId, ReceiptLine receipt, Guid journalId, CancellationToken ct)
    {
        var movements = new List<int>();
        await using (var cmd = tx.Command("select line_no from core.stock_movement where tenant_id = @t and company_id = @c and stock_document_id = @s and value_lc <> 0 order by line_no"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("s", receipt.StockDocumentId!.Value);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) movements.Add(r.GetInt32(0));
        }
        int position = movements.IndexOf(receipt.StockDocumentLineNo ?? -1);
        if (position < 0) return null;
        var itemLines = new List<int>();
        await using (var cmd = tx.Command("select line_no from core.journal_line_open_item where tenant_id = @t and company_id = @c and document_id = @d and partner_id is null order by line_no"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", journalId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) itemLines.Add(r.GetInt32(0));
        }
        return position < itemLines.Count ? itemLines[position] : null;
    }

    private static async Task<OrderLine?> ReadOrderLineAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid orderId, int lineNo, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select l.item_id, i.item_type, l.base_quantity, l.unit_price, l.price_unit_quantity, l.discount_pct, l.tax_class_id, l.warehouse_id, l.origin_country, l.destination_country, l.match_mode, " +
            "l.over_tolerance_pct, l.line_status, i.item_group_id, l.unit_code, l.quantity from core.purchase_order_line l join core.item i on i.tenant_id = l.tenant_id and i.item_id = l.item_id " +
            "where l.tenant_id = @t and l.company_id = @c and l.purchase_order_id = @o and l.line_no = @n");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("o", orderId); cmd.Parameters.AddWithValue("n", lineNo);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new OrderLine(orderId, lineNo, r.GetGuid(0), r.GetString(1).Trim(), r.GetDecimal(2), r.GetDecimal(3), r.GetDecimal(4), r.GetDecimal(5), r.IsDBNull(6) ? null : r.GetGuid(6), r.IsDBNull(7) ? null : r.GetGuid(7),
                r.GetString(8).Trim(), r.GetString(9).Trim(), r.GetString(10).Trim(), r.GetDecimal(11), r.GetString(12).Trim(), r.GetGuid(13), r.GetString(14).Trim(), r.GetDecimal(15))
            : null;
    }

    /// <summary>The receipt lines of an order line that count: posted, not a mirror, oldest first (the chain order of the receipts).</summary>
    private static async Task<List<ReceiptLine>> ReadReceiptLinesAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid orderId, int lineNo, CancellationToken ct)
    {
        var list = new List<ReceiptLine>();
        await using var cmd = tx.Command(
            "select x.receipt_id, x.line_no, x.base_quantity, x.value_lc, h.stock_document_id, x.stock_document_line_no, h.receipt_date, x.stock_document_line_count, m2.base_quantity, m2.value_lc from core.purchase_receipt_line x join core.purchase_receipt h " +
            "on h.tenant_id = x.tenant_id and h.company_id = x.company_id and h.receipt_id = x.receipt_id left join core.stock_movement m2 on m2.tenant_id = x.tenant_id and m2.company_id = x.company_id and m2.stock_document_id = h.stock_document_id " +
            "and x.stock_document_line_count = 2 and m2.line_no = x.stock_document_line_no + 1 where x.tenant_id = @t and x.company_id = @c and x.purchase_order_id = @o and x.purchase_order_line_no = @n " +
            "and h.status = 'posted' and h.reversal_of_receipt_id is null order by h.chain_seq, x.line_no");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("o", orderId); cmd.Parameters.AddWithValue("n", lineNo);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            {
            // a line of two movements (the covered units and the rest) is matched through the second one, where the units that no invoice covered are
            if (!r.IsDBNull(8)) list.Add(new ReceiptLine(r.GetGuid(0), r.GetInt32(1), r.GetDecimal(8), r.IsDBNull(9) ? null : r.GetDecimal(9), r.IsDBNull(4) ? null : r.GetGuid(4), r.GetInt32(5) + 1, DateOnly.FromDateTime(r.GetDateTime(6)), false, true));
            else list.Add(new ReceiptLine(r.GetGuid(0), r.GetInt32(1), r.GetDecimal(2), r.IsDBNull(3) ? null : r.GetDecimal(3), r.IsDBNull(4) ? null : r.GetGuid(4), r.IsDBNull(5) ? null : r.GetInt32(5), DateOnly.FromDateTime(r.GetDateTime(6))));
        }
        return list;
    }

    /// <summary>The receipt movement of a stock document posted without an order (10, 7; 11, 7.2, D3): a posted, not reversed STOCK_RECEIPT of stock-documents:post with a value, so with a GR/IR credit.</summary>
    private static async Task<ReceiptLine?> ReadDirectReceiptAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid stockDocumentId, int lineNo, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select m.base_quantity, m.value_lc, d.posting_date from core.stock_movement m join core.stock_document d on d.tenant_id = m.tenant_id and d.company_id = m.company_id and d.stock_document_id = m.stock_document_id " +
            "where m.tenant_id = @t and m.company_id = @c and m.stock_document_id = @s and m.line_no = @n and d.document_type = 'STOCK_RECEIPT' and d.command_name = 'stock-documents:post' and d.status = 'posted' " +
            "and d.reversal_of_stock_document_id is null and m.movement_type = 'RECEIPT' and m.value_lc > 0");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("s", stockDocumentId); cmd.Parameters.AddWithValue("n", lineNo);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new ReceiptLine(stockDocumentId, lineNo, r.GetDecimal(0), r.GetDecimal(1), stockDocumentId, lineNo, DateOnly.FromDateTime(r.GetDateTime(2)), true) : null;
    }

    /// <summary>The facts of an order line for a direct receipt, from the item, the warehouse and the supplier: the tax class of the item, the country of the supplier to the country of the warehouse (7.2). Null for an item that is not a stock item.</summary>
    private static async Task<OrderLine?> DirectOrderLineAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid supplier, ReceiptLine receipt, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select m.item_id, m.warehouse_id, i.item_group_id, i.item_type, i.tax_class_id, i.base_unit, w.country_code, (select p.country_code from core.business_partner_pii p where p.tenant_id = @t and p.partner_id = @p) " +
            "from core.stock_movement m join core.item i on i.tenant_id = m.tenant_id and i.item_id = m.item_id join core.warehouse w on w.tenant_id = m.tenant_id and w.company_id = m.company_id and w.warehouse_id = m.warehouse_id " +
            "where m.tenant_id = @t and m.company_id = @c and m.stock_document_id = @s and m.line_no = @n");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("s", receipt.StockDocumentId!.Value); cmd.Parameters.AddWithValue("n", receipt.StockDocumentLineNo!.Value);
        cmd.Parameters.AddWithValue("p", supplier);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct) || r.GetString(3).Trim() != "GOODS") return null;
        string destination = r.GetString(6).Trim(), origin = r.IsDBNull(7) ? destination : r.GetString(7).Trim();
        return new OrderLine(Guid.Empty, 0, r.GetGuid(0), "GOODS", receipt.BaseQuantity, 0m, 1m, 0m, r.IsDBNull(4) ? null : r.GetGuid(4), r.GetGuid(1), origin, destination, "THREE_WAY", 0m, "OPEN", r.GetGuid(2), r.GetString(5).Trim(), receipt.BaseQuantity);
    }

    /// <summary>What receipts cover of a receipt line before it is matched: the units that an invoice before receipt took (11, 7.1), net of the reversals of the receipt.</summary>
    private static async Task<decimal> CoveredByReceiptLineAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid receiptId, int lineNo, CancellationToken ct)
    {
        await using var cmd = tx.Command("select coalesce(sum(covered_quantity), 0) from core.purchase_invoice_coverage where tenant_id = @t and company_id = @c and receipt_id = @r and receipt_line_no = @n");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("r", receiptId); cmd.Parameters.AddWithValue("n", lineNo);
        return (decimal)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task<int> ScaleOfAsync(TenantTransaction tx, string currency, CancellationToken ct)
    {
        await using var cmd = tx.Command("select minor_units from platform.ref_currency where currency_code = @c");
        cmd.Parameters.AddWithValue("c", currency);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct) ?? 2, CultureInfo.InvariantCulture);
    }

    private static async Task<decimal> SumAsync(TenantTransaction tx, string sql, Guid tenantId, Guid companyId, (Guid Order, int Line) key, CancellationToken ct)
    {
        await using var cmd = tx.Command(sql);
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("o", key.Order); cmd.Parameters.AddWithValue("n", key.Line);
        return (decimal)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>What is invoiced of an order line (11, 4.3): the invoice rows less the rows of their reversals. Credit notes are not in it.</summary>
    private static Task<decimal> InvoicedAsync(TenantTransaction tx, Guid tenantId, Guid companyId, (Guid Order, int Line) key, CancellationToken ct) =>
        SumAsync(tx,
            "select coalesce(sum(l.base_quantity), 0) from core.purchase_invoice_line l join core.purchase_invoice h on h.tenant_id = l.tenant_id and h.company_id = l.company_id and h.purchase_invoice_id = l.purchase_invoice_id " +
            "left join core.purchase_invoice o on o.tenant_id = h.tenant_id and o.company_id = h.company_id and o.purchase_invoice_id = h.reversal_of_purchase_invoice_id " +
            "where l.tenant_id = @t and l.company_id = @c and l.purchase_order_id = @o and l.purchase_order_line_no = @n and l.line_kind = 'MATCHED' and (h.invoice_kind = 'INVOICE' or (h.invoice_kind = 'REVERSAL' and o.invoice_kind = 'INVOICE'))",
            tenantId, companyId, key, ct);

    /// <summary>What the invoices already take of a receipt line (a reversal row gives it back).</summary>
    private static async Task<decimal> InvoicedByReceiptLineAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid receiptId, int lineNo, bool direct, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select coalesce(sum(l.base_quantity), 0) from core.purchase_invoice_line l join core.purchase_invoice h on h.tenant_id = l.tenant_id and h.company_id = l.company_id and h.purchase_invoice_id = l.purchase_invoice_id " +
            "left join core.purchase_invoice o on o.tenant_id = h.tenant_id and o.company_id = h.company_id and o.purchase_invoice_id = h.reversal_of_purchase_invoice_id " +
            "where l.tenant_id = @t and l.company_id = @c and " + (direct ? "l.stock_document_id = @r and l.stock_document_line_no = @n" : "l.receipt_id = @r and l.receipt_line_no = @n") + " and (h.invoice_kind = 'INVOICE' or (h.invoice_kind = 'REVERSAL' and o.invoice_kind = 'INVOICE'))");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("r", receiptId); cmd.Parameters.AddWithValue("n", lineNo);
        return (decimal)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task InsertHeaderAsync(
        TenantTransaction t, Guid tenantId, Guid companyId, CommandContext ctx, PostPurchaseInvoiceRequest req, Guid supplier, string currency, Guid invoiceId, Guid journalId, Guid? stockId, Guid? icId, CancellationToken ct)
    {
        await using var cmd = t.Command(
            "insert into core.purchase_invoice (tenant_id, company_id, purchase_invoice_id, invoice_kind, supplier_partner_id, supplier_invoice_number, invoice_date, due_date, currency, gross_amount, journal_document_id, " +
            "stock_document_id, source_system, source_document_ref, created_by, correlation_id, idempotency_key, ic_transaction_id) values (@t, @c, @id, 'INVOICE', @s, @num, @d, @due, @cur, @g, @j, @sd, @src, @ref, @by, @corr, @key, @icx)");
        cmd.Parameters.Add(new NpgsqlParameter("icx", NpgsqlDbType.Uuid) { Value = (object?)icId ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("sd", NpgsqlDbType.Uuid) { Value = (object?)stockId ?? DBNull.Value });
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("id", invoiceId); cmd.Parameters.AddWithValue("s", supplier);
        cmd.Parameters.AddWithValue("num", req.SupplierInvoiceNumber); cmd.Parameters.AddWithValue("d", req.InvoiceDate); cmd.Parameters.AddWithValue("cur", currency); cmd.Parameters.AddWithValue("g", req.GrossAmount);
        cmd.Parameters.AddWithValue("j", journalId); cmd.Parameters.AddWithValue("src", ctx.SourceSystem); cmd.Parameters.AddWithValue("by", ctx.Actor); cmd.Parameters.AddWithValue("corr", ctx.CorrelationId);
        cmd.Parameters.AddWithValue("key", ctx.IdempotencyKey);
        cmd.Parameters.Add(new NpgsqlParameter("due", NpgsqlDbType.Date) { Value = (object?)req.DueDate ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("ref", NpgsqlDbType.Text) { Value = (object?)req.SourceDocumentRef ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertLineAsync(TenantTransaction t, Guid tenantId, Guid companyId, Guid invoiceId, Dictionary<string, object?> row, CancellationToken ct)
    {
        await using var cmd = t.Command(
            "insert into core.purchase_invoice_line (tenant_id, company_id, purchase_invoice_id, line_no, line_kind, purchase_order_id, purchase_order_line_no, receipt_id, receipt_line_no, stock_document_id, stock_document_line_no, base_quantity, unit_price, net_amount, " +
            "tax_amount, order_unit_price, match_mode_used, tolerance_used, price_difference_lc, on_hand_part_lc, consumed_part_lc, dropship_part_lc, unreceived_gr_ir_lc, gr_ir_clearing_id, tax_code_id, tax_code_version_id, " +
            "tax_date, journal_line_no) values (@t, @c, @id, @n, 'MATCHED', @o, @ol, @r, @rl, @sd, @sl, @bq, @up, @net, @tax, @oup, @mm, @tol, @pd, @oh, @co, 0, @ur, @gc, @tc, @tv, @td, @jl)");
        cmd.Parameters.AddWithValue("ur", row["unreceived_gr_ir_lc"]!);
        cmd.Parameters.Add(new NpgsqlParameter("sd", NpgsqlDbType.Uuid) { Value = row["stock_document_id"] ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("sl", NpgsqlDbType.Integer) { Value = row["stock_document_line_no"] ?? DBNull.Value });
        cmd.Parameters.AddWithValue("pd", row["price_difference_lc"]!); cmd.Parameters.AddWithValue("oh", row["on_hand_part_lc"]!); cmd.Parameters.AddWithValue("co", row["consumed_part_lc"]!);
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("id", invoiceId); cmd.Parameters.AddWithValue("n", row["line_no"]!);
        cmd.Parameters.Add(new NpgsqlParameter("o", NpgsqlDbType.Uuid) { Value = row["purchase_order_id"] ?? DBNull.Value }); cmd.Parameters.Add(new NpgsqlParameter("ol", NpgsqlDbType.Integer) { Value = row["purchase_order_line_no"] ?? DBNull.Value }); cmd.Parameters.AddWithValue("bq", row["base_quantity"]!);
        cmd.Parameters.AddWithValue("up", row["unit_price"]!); cmd.Parameters.AddWithValue("net", row["net_amount"]!); cmd.Parameters.AddWithValue("tax", row["tax_amount"]!); cmd.Parameters.Add(new NpgsqlParameter("oup", NpgsqlDbType.Numeric) { Value = row["order_unit_price"] ?? DBNull.Value });
        cmd.Parameters.AddWithValue("mm", row["match_mode_used"]!); cmd.Parameters.AddWithValue("jl", row["journal_line_no"]!);
        cmd.Parameters.Add(new NpgsqlParameter("r", NpgsqlDbType.Uuid) { Value = row["receipt_id"] ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("rl", NpgsqlDbType.Integer) { Value = row["receipt_line_no"] ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("tol", NpgsqlDbType.Numeric) { Value = row["tolerance_used"] ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("gc", NpgsqlDbType.Uuid) { Value = row["gr_ir_clearing_id"] ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("tc", NpgsqlDbType.Uuid) { Value = row["tax_code_id"] ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("tv", NpgsqlDbType.Uuid) { Value = row["tax_code_version_id"] ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("td", NpgsqlDbType.Date) { Value = row["tax_date"] ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertChargeLineAsync(TenantTransaction t, Guid tenantId, Guid companyId, Guid invoiceId, Dictionary<string, object?> row, CancellationToken ct)
    {
        await using var cmd = t.Command(
            "insert into core.purchase_invoice_line (tenant_id, company_id, purchase_invoice_id, line_no, line_kind, description, charge_treatment, base_quantity, unit_price, net_amount, tax_amount, tax_code_id, tax_code_version_id, tax_date, journal_line_no) " +
            "values (@t, @c, @id, @n, 'CHARGE', @ds, @ct, 0, 0, @net, @tax, @tc, @tv, @td, @jl)");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("id", invoiceId); cmd.Parameters.AddWithValue("n", row["line_no"]!);
        cmd.Parameters.AddWithValue("ds", row["description"]!); cmd.Parameters.AddWithValue("ct", row["charge_treatment"]!); cmd.Parameters.AddWithValue("net", row["net_amount"]!);
        cmd.Parameters.AddWithValue("tax", row["tax_amount"]!); cmd.Parameters.AddWithValue("jl", row["journal_line_no"]!);
        cmd.Parameters.Add(new NpgsqlParameter("tc", NpgsqlDbType.Uuid) { Value = row["tax_code_id"] ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("tv", NpgsqlDbType.Uuid) { Value = row["tax_code_version_id"] ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("td", NpgsqlDbType.Date) { Value = row["tax_date"] ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    internal static async Task<PurchaseInvoiceResult> LoadAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid id, CancellationToken ct)
    {
        Guid journal; decimal grossAmount; Guid? stock;
        await using (var cmd = tx.Command("select journal_document_id, gross_amount, stock_document_id from core.purchase_invoice where tenant_id = @t and company_id = @c and purchase_invoice_id = @i"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            journal = r.GetGuid(0); grossAmount = r.GetDecimal(1); stock = r.IsDBNull(2) ? null : r.GetGuid(2);
        }
        string number; byte[] hash;
        await using (var cmd = tx.Command("select document_number, row_hash from core.journal_document where tenant_id = @t and company_id = @c and document_id = @d"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", journal);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            number = r.GetString(0); hash = (byte[])r[1];
        }
        var lines = new List<PurchaseInvoiceLineResult>();
        Guid? clearing = null;
        await using (var cmd = tx.Command(
            "select line_no, purchase_order_id, purchase_order_line_no, receipt_id, receipt_line_no, base_quantity, net_amount, tax_amount, match_mode_used, journal_line_no, gr_ir_clearing_id from core.purchase_invoice_line " +
            "where tenant_id = @t and company_id = @c and purchase_invoice_id = @i order by line_no"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                Guid? gc = r.IsDBNull(10) ? null : r.GetGuid(10);
                clearing ??= gc;
                lines.Add(new PurchaseInvoiceLineResult(r.GetInt32(0), "", r.IsDBNull(1) ? Guid.Empty : r.GetGuid(1), r.IsDBNull(2) ? 0 : r.GetInt32(2), r.IsDBNull(3) ? null : r.GetGuid(3), r.IsDBNull(4) ? null : r.GetInt32(4), r.GetDecimal(5), r.GetDecimal(6), r.GetDecimal(7),
                    r.IsDBNull(8) ? "" : r.GetString(8).Trim(), r.GetInt32(9), gc));
            }
        }
        decimal net = lines.Sum(l => l.NetAmount), tax = lines.Sum(l => l.TaxAmount);
        return new PurchaseInvoiceResult(id, journal, number, net, tax, grossAmount, Canonical.FormatHex32(hash), lines, clearing, stock);
    }
}
