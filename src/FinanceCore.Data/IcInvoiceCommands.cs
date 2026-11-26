using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;
using Npgsql;

namespace FinanceCore.Data;

/// <summary>
/// ic-invoices:create (11, 6.7, ADR-016; permission ic.invoice), first slice: the AR invoice of the seller (the path of sales-invoices:create, mode CORE_BUILDS, service lines of the policy ORDERED)
/// and the AP invoice of the buyer (the path of purchase-invoices:post, the service lines matched against the ordered quantity) as one atomic pair over the two orders of an ic-orders:create. Both
/// journals carry the ic_transaction_id and the marker ic_counterpart_company_id on the BASE lines, the control lines included, never on tax lines; both link rows carry the id. The buyer's
/// invoice equals the seller's by construction: its lines take quantity and price from the purchase order line, which ic-orders:create made equal to the sales order line. The sides are posted in
/// company order. Not built: stock lines (they need ic-deliveries:post and the GR/IR clearing of 7.2), tax, foreign currency, the reversal.
/// </summary>
public sealed class IcInvoiceCommands(NpgsqlDataSource source)
{
    private readonly SalesInvoiceCommands _sales = new(source);
    private readonly PurchaseInvoiceCommands _purchase = new(source);
    private const string CommandName = "ic-invoices:create";

    private static CommandResult<IcInvoiceResult> Reject(CommandError e) => CommandResult<IcInvoiceResult>.Rejected([e]);

    private static CommandError Error(string code, string phase, string path, params (string Key, object? Value)[] p) => JournalDocumentPoster.Error(code, phase, path, p);

    public async Task<CommandResult<IcInvoiceResult>> CreateAsync(Guid tenantId, CommandContext ctx, CreateIcInvoiceRequest req, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(IcInvoiceRules.Permission)) return Reject(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", IcInvoiceRules.Permission)));
        var errors = new List<CommandError>();
        void Invalid(string path, string reason) => errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", path, ("reason", reason)));
        if (string.IsNullOrWhiteSpace(ctx.IdempotencyKey) || ctx.IdempotencyKey.Length > 90) Invalid("idempotency_key", "required, at most 90 characters (the sides add #1 and #2)");
        if (req.IcTransactionId == Guid.Empty) Invalid("ic_transaction_id", "required");
        if (req.Lines is null or { Count: 0 }) errors.Add(Error("SCHEMA.REQUIRED_FIELD_MISSING", "P1", "lines"));
        for (int i = 0; i < (req.Lines?.Count ?? 0); i++)
        {
            IcInvoiceLineInput l = req.Lines![i];
            if (string.IsNullOrWhiteSpace(l.LineRef) || l.LineRef.Length > 100) Invalid($"lines[{i}]/line_ref", "required, at most 100 characters");
            else if (req.Lines.Take(i).Any(x => x.LineRef == l.LineRef || x.OrderLineNo == l.OrderLineNo)) errors.Add(Error("SCHEMA.DUPLICATE_LINE_REF", "P1", $"lines[{i}]/line_ref", ("line_ref", l.LineRef)));
            if (l.OrderLineNo < 1) Invalid($"lines[{i}]/order_line_no", "1 or more");
            if (l.Quantity <= 0m || Math.Round(l.Quantity, 6) != l.Quantity) Invalid($"lines[{i}]/quantity", "more than 0, at most 6 fraction digits");
        }
        if (req.SupplierInvoiceNumber?.Length > 100 || req.SourceDocumentRef?.Length > 100) Invalid("supplier_invoice_number", "at most 100 characters");
        if (errors.Count > 0) return CommandResult<IcInvoiceResult>.Rejected(errors);
        byte[] fingerprint = IcInvoiceRules.Fingerprint(req);

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
                        ? CommandResult<IcInvoiceResult>.Failed(Error("SYSTEM.REPLAY_RESULT_MISSING", "P1", ""))
                        : CommandResult<IcInvoiceResult>.Ok(await LoadAsync(tx, tenantId, req.IcTransactionId, ctx.IdempotencyKey, ct), earlier.CommandLogId, replay: true);
                }
                return await CreateCoreAsync(tx, tenantId, ctx, req, fingerprint, ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.TableName == "command_log" && attempt == 0)
            {
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                return CommandResult<IcInvoiceResult>.Failed(new CommandError("SYSTEM.LOCK_TIMEOUT", "P7", "", null, true));
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure)
            {
                return CommandResult<IcInvoiceResult>.Failed(new CommandError("SYSTEM.CONFLICT", "P7", "", null, true));
            }
        }
    }

    private async Task<CommandResult<IcInvoiceResult>> CreateCoreAsync(TenantTransaction tx, Guid tenantId, CommandContext ctx, CreateIcInvoiceRequest req, byte[] fingerprint, CancellationToken ct)
    {
        // the pair of orders
        Guid salesCompany, salesOrder, purchaseCompany, purchaseOrder;
        await using (var cmd = tx.Command("select sales_company_id, sales_order_id, purchase_company_id, purchase_order_id from core.ic_order_link where tenant_id = @t and ic_transaction_id = @i"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("i", req.IcTransactionId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Reject(Error("ORDER.IC_ORDER_PAIR_NOT_FOUND", "P2", "ic_transaction_id", ("ic_transaction_id", req.IcTransactionId)));
            salesCompany = r.GetGuid(0); salesOrder = r.GetGuid(1); purchaseCompany = r.GetGuid(2); purchaseOrder = r.GetGuid(3);
        }
        Guid supplier; string currency;
        await using (var cmd = tx.Command("select supplier_partner_id, order_currency from core.purchase_order where tenant_id = @t and company_id = @c and purchase_order_id = @o"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", purchaseCompany); cmd.Parameters.AddWithValue("o", purchaseOrder);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            supplier = r.GetGuid(0); currency = r.GetString(1).Trim();
        }
        int scale;
        await using (var cmd = tx.Command("select minor_units from platform.ref_currency where currency_code = @c"))
        {
            cmd.Parameters.AddWithValue("c", currency);
            scale = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct) ?? 2, System.Globalization.CultureInfo.InvariantCulture);
        }

        // the lines of the buyer's invoice come from the purchase order line: the same quantity, price and discount as the sales order line (ic-orders:create)
        var apLines = new List<PurchaseInvoiceLineInput>();
        for (int i = 0; i < req.Lines.Count; i++)
        {
            IcInvoiceLineInput l = req.Lines[i];
            decimal price, priceUnit, discount;
            await using (var cmd = tx.Command("select unit_price, price_unit_quantity, discount_pct from core.purchase_order_line where tenant_id = @t and company_id = @c and purchase_order_id = @o and line_no = @n"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", purchaseCompany); cmd.Parameters.AddWithValue("o", purchaseOrder); cmd.Parameters.AddWithValue("n", l.OrderLineNo);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) return Reject(Error("ORDER.LINE_NOT_OPEN", "P2", $"lines[{i}]/order_line_no", ("order_line_no", l.OrderLineNo)));
                price = r.GetDecimal(0); priceUnit = r.GetDecimal(1); discount = r.GetDecimal(2);
            }
            apLines.Add(new PurchaseInvoiceLineInput(l.LineRef, purchaseOrder, l.OrderLineNo, l.Quantity, price, OrderKind.NetAmount(l.Quantity, price, priceUnit, discount, scale), null, 0m));
        }
        string supplierNumber = req.SupplierInvoiceNumber ?? $"IC-{req.IcTransactionId:N}"[..11] + "-" + (ctx.IdempotencyKey.Length > 40 ? ctx.IdempotencyKey[..40] : ctx.IdempotencyKey);
        var salesReq = new CreateSalesInvoiceRequest(salesCompany, req.InvoiceDate, req.Lines.Select(l => new SalesInvoiceLineInput(l.LineRef, salesOrder, l.OrderLineNo, l.Quantity)).ToList(), req.PostingDate, req.DueDate, req.SourceDocumentRef);
        var purchaseReq = new PostPurchaseInvoiceRequest(purchaseCompany, supplier, supplierNumber, req.InvoiceDate, currency, apLines.Sum(l => l.NetAmount), apLines, req.PostingDate, req.DueDate, req.SourceDocumentRef);

        SalesInvoiceResult? sales = null; PurchaseInvoiceResult? purchase = null;
        // the sides in company order (the locks of 06, 9 are taken in one order by every IC command)
        foreach (bool salesSide in new[] { true, false }.OrderBy(s => s ? salesCompany : purchaseCompany))
        {
            if (salesSide)
            {
                var result = await _sales.CreateCoreAsync(tx, tenantId, ctx with { IdempotencyKey = IcInvoiceRules.SideKey(ctx.IdempotencyKey, 1) }, salesReq, fingerprint, ct, req.IcTransactionId, purchaseCompany, owned: false);
                if (result.Status != CommandStatus.Ok) return CommandResult<IcInvoiceResult>.Rejected(result.Errors.Select(e => e with { Path = "sales_invoice" + e.Path }).ToList()) with { Status = result.Status };
                sales = result.Value!;
            }
            else
            {
                var result = await _purchase.PostCoreAsync(tx, tenantId, ctx with { IdempotencyKey = IcInvoiceRules.SideKey(ctx.IdempotencyKey, 2) }, purchaseReq, fingerprint, ct, req.IcTransactionId, salesCompany, owned: false);
                if (result.Status != CommandStatus.Ok) return CommandResult<IcInvoiceResult>.Rejected(result.Errors.Select(e => e with { Path = "purchase_invoice" + e.Path }).ToList()) with { Status = result.Status };
                purchase = result.Value!;
            }
        }
        // tax between the companies is not built: the buyer's invoice would need the tax of the seller's per line
        if (sales is null || purchase is null || sales.TaxAmount != 0m || purchase.TaxAmount != 0m)
            return Reject(Error("LIMIT.IC_INVOICE_TAX_NOT_BUILT", "P4", "lines", ("tax_sales", sales?.TaxAmount), ("tax_purchase", purchase?.TaxAmount)));
        Guid logId = await CommandSupport.WriteLogAsync(tx, tenantId, null, ctx, CommandName, fingerprint, "IC_INVOICE", req.IcTransactionId, ct);
        await tx.CommitAsync(ct);
        return CommandResult<IcInvoiceResult>.Ok(new IcInvoiceResult(req.IcTransactionId, sales, purchase), logId);
    }

    private static async Task<IcInvoiceResult> LoadAsync(TenantTransaction tx, Guid tenantId, Guid icId, string key, CancellationToken ct)
    {
        Guid salesCompany, purchaseCompany;
        await using (var cmd = tx.Command("select sales_company_id, purchase_company_id from core.ic_order_link where tenant_id = @t and ic_transaction_id = @i"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("i", icId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            salesCompany = r.GetGuid(0); purchaseCompany = r.GetGuid(1);
        }
        Guid salesInvoice, purchaseInvoice;
        await using (var cmd = tx.Command("select sales_invoice_id from core.sales_invoice where tenant_id = @t and company_id = @c and idempotency_key = @k"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", salesCompany); cmd.Parameters.AddWithValue("k", IcInvoiceRules.SideKey(key, 1));
            salesInvoice = (Guid)(await cmd.ExecuteScalarAsync(ct))!;
        }
        await using (var cmd = tx.Command("select purchase_invoice_id from core.purchase_invoice where tenant_id = @t and company_id = @c and idempotency_key = @k"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", purchaseCompany); cmd.Parameters.AddWithValue("k", IcInvoiceRules.SideKey(key, 2));
            purchaseInvoice = (Guid)(await cmd.ExecuteScalarAsync(ct))!;
        }
        return new IcInvoiceResult(icId, await SalesInvoiceCommands.LoadAsync(tx, tenantId, salesCompany, salesInvoice, ct), await PurchaseInvoiceCommands.LoadAsync(tx, tenantId, purchaseCompany, purchaseInvoice, ct));
    }
}
