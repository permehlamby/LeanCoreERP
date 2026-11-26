using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;
using Npgsql;

namespace FinanceCore.Data;

/// <summary>
/// ic-orders:create (11, 6.7, ADR-016; permission ic.order): a sales order in the seller and a purchase order in the buyer in one transaction, sharing ic_transaction_id and one set of lines,
/// and one row in ic_order_link. The partners are the intercompany partners: the customer of the seller has the buyer as ic_company_id and the supplier of the buyer has the seller
/// (CONFIG.INVALID_REFERENCE otherwise). One order currency; the amounts of each side use its own rules. The two orders are created in company order, so the number counters are locked in the
/// same order by every IC command; the keys of the orders are the key of the command with #1 (sales) and #2 (purchase). A failure of one side rolls back the other.
/// </summary>
public sealed class IcOrderCommands(NpgsqlDataSource source)
{
    private readonly OrderCommands _orders = new(source);
    private const string CommandName = "ic-orders:create";

    private static CommandResult<IcOrderResult> Reject(CommandError e) => CommandResult<IcOrderResult>.Rejected([e]);

    private static CommandError Error(string code, string phase, string path, params (string Key, object? Value)[] p) => JournalDocumentPoster.Error(code, phase, path, p);

    public async Task<CommandResult<IcOrderResult>> CreateAsync(Guid tenantId, CommandContext ctx, CreateIcOrderRequest req, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(IcOrderRules.Permission)) return Reject(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", IcOrderRules.Permission)));
        var errors = new List<CommandError>();
        if (string.IsNullOrWhiteSpace(ctx.IdempotencyKey) || ctx.IdempotencyKey.Length > 90) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "idempotency_key", ("reason", "required, at most 90 characters")));
        if (req.SalesCompanyId == Guid.Empty || req.PurchaseCompanyId == Guid.Empty || req.SalesCompanyId == req.PurchaseCompanyId)
            errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "sales_company_id", ("reason", "two different companies")));
        if (string.IsNullOrWhiteSpace(req.OrderCurrency)) errors.Add(Error("SCHEMA.REQUIRED_FIELD_MISSING", "P1", "order_currency"));
        if (req.Lines is null or { Count: 0 }) errors.Add(Error("SCHEMA.REQUIRED_FIELD_MISSING", "P1", "lines"));
        if (errors.Count > 0) return CommandResult<IcOrderResult>.Rejected(errors);

        // the two requests in the shape of 6.1, checked against the registries of both kinds
        var salesFields = new Dictionary<string, object?>
        {
            ["company_id"] = req.SalesCompanyId, ["order_date"] = req.OrderDate.ToString("yyyy-MM-dd"), ["order_currency"] = req.OrderCurrency, ["customer_partner_id"] = req.CustomerPartnerId
        };
        var purchaseFields = new Dictionary<string, object?>
        {
            ["company_id"] = req.PurchaseCompanyId, ["order_date"] = req.OrderDate.ToString("yyyy-MM-dd"), ["order_currency"] = req.OrderCurrency, ["supplier_partner_id"] = req.SupplierPartnerId
        };
        if (req.DeliveryDate is { } delivery) { salesFields["requested_delivery_date"] = delivery.ToString("yyyy-MM-dd"); purchaseFields["expected_receipt_date"] = delivery.ToString("yyyy-MM-dd"); }
        if (req.ShipFromCountry is not null) purchaseFields["ship_from_country"] = req.ShipFromCountry;
        if (req.HeaderText is not null) { salesFields["header_text"] = req.HeaderText; purchaseFields["header_text"] = req.HeaderText; }
        if (req.SourceDocumentRef is not null) { salesFields["source_document_ref"] = req.SourceDocumentRef; purchaseFields["source_document_ref"] = req.SourceDocumentRef; }
        var salesReq = new CreateOrderRequest(salesFields, req.Lines!.Select(IcOrderRules.SalesLine).ToList());
        var purchaseReq = new CreateOrderRequest(purchaseFields, req.Lines!.Select(IcOrderRules.PurchaseLine).ToList());
        var sideCtx = new[] { ctx with { IdempotencyKey = IcOrderRules.SideKey(ctx.IdempotencyKey, 1) }, ctx with { IdempotencyKey = IcOrderRules.SideKey(ctx.IdempotencyKey, 2) } };
        var sales = OrderCommands.ValidateCreate(OrderKind.Sales, sideCtx[0], salesReq);
        var purchase = OrderCommands.ValidateCreate(OrderKind.Purchase, sideCtx[1], purchaseReq);
        errors.AddRange(sales.Errors.Select(e => e with { Path = "sales_order" + e.Path }));
        errors.AddRange(purchase.Errors.Select(e => e with { Path = "purchase_order" + e.Path }));
        if (errors.Count > 0) return CommandResult<IcOrderResult>.Rejected(errors);
        byte[] fingerprint = IcOrderRules.Fingerprint(req);

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Order, ct: ct);
                CommandSupport.EarlierCommand? earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
                if (earlier is not null)
                {
                    if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint)) return Reject(Error("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
                    return earlier.EntityId is { } known
                        ? CommandResult<IcOrderResult>.Ok(await LoadAsync(tx, tenantId, known, ct), earlier.CommandLogId, replay: true)
                        : CommandResult<IcOrderResult>.Failed(Error("SYSTEM.REPLAY_RESULT_MISSING", "P1", ""));
                }

                // the intercompany partners: each names the other company
                if (await PartnerIcAsync(tx, tenantId, req.CustomerPartnerId, ct) != req.PurchaseCompanyId)
                    return Reject(Error("CONFIG.INVALID_REFERENCE", "P2", "customer_partner_id", ("reason", "the partner is not the intercompany partner of the buyer")));
                if (await PartnerIcAsync(tx, tenantId, req.SupplierPartnerId, ct) != req.SalesCompanyId)
                    return Reject(Error("CONFIG.INVALID_REFERENCE", "P2", "supplier_partner_id", ("reason", "the partner is not the intercompany partner of the seller")));

                Guid icId = Guid.NewGuid();
                var results = new OrderResult?[2];
                // company order: the number counters of the two companies are locked in the same order whatever the roles
                foreach (int side in new[] { 0, 1 }.OrderBy(i => i == 0 ? req.SalesCompanyId : req.PurchaseCompanyId))
                {
                    var (kind, v, request, label) = side == 0 ? (OrderKind.Sales, sales, salesReq, "sales_order") : (OrderKind.Purchase, purchase, purchaseReq, "purchase_order");
                    var result = await _orders.CreateCoreAsync(tx, kind, tenantId, sideCtx[side], request, v.Header, v.Lines, OrderRules.Fingerprint(kind.Resource + ":create", request), ct, owned: false, icTransactionId: icId);
                    if (result.Status != CommandStatus.Ok)
                        return CommandResult<IcOrderResult>.Rejected(result.Errors.Select(e => e with { Path = label + e.Path }).ToList()) with { Status = result.Status };
                    results[side] = result.Value!;
                }
                await using (var cmd = tx.Command(
                    "insert into core.ic_order_link (tenant_id, ic_transaction_id, sales_company_id, sales_order_id, purchase_company_id, purchase_order_id, created_by, correlation_id, idempotency_key) " +
                    "values (@t, @i, @sc, @so, @pc, @po, @by, @corr, @key)"))
                {
                    cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("i", icId);
                    cmd.Parameters.AddWithValue("sc", req.SalesCompanyId); cmd.Parameters.AddWithValue("so", results[0]!.OrderId);
                    cmd.Parameters.AddWithValue("pc", req.PurchaseCompanyId); cmd.Parameters.AddWithValue("po", results[1]!.OrderId);
                    cmd.Parameters.AddWithValue("by", ctx.Actor); cmd.Parameters.AddWithValue("corr", ctx.CorrelationId); cmd.Parameters.AddWithValue("key", ctx.IdempotencyKey);
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                Guid logId = await CommandSupport.WriteLogAsync(tx, tenantId, null, ctx, CommandName, fingerprint, "IC_ORDER", icId, ct);
                await tx.CommitAsync(ct);
                return CommandResult<IcOrderResult>.Ok(new IcOrderResult(icId, results[0]!, results[1]!), logId);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.TableName == "command_log" && attempt == 0)
            {
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                return CommandResult<IcOrderResult>.Failed(new CommandError("SYSTEM.LOCK_TIMEOUT", "P7", "", null, true));
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure)
            {
                return CommandResult<IcOrderResult>.Failed(new CommandError("SYSTEM.CONFLICT", "P7", "", null, true));
            }
        }
    }

    private static async Task<Guid?> PartnerIcAsync(TenantTransaction tx, Guid tenantId, Guid partnerId, CancellationToken ct)
    {
        await using var cmd = tx.Command("select ic_company_id from core.business_partner where tenant_id = @t and partner_id = @p");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("p", partnerId);
        return await cmd.ExecuteScalarAsync(ct) as Guid?;
    }

    private static async Task<IcOrderResult> LoadAsync(TenantTransaction tx, Guid tenantId, Guid icId, CancellationToken ct)
    {
        Guid sales, purchase;
        await using (var cmd = tx.Command("select sales_order_id, purchase_order_id from core.ic_order_link where tenant_id = @t and ic_transaction_id = @i"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("i", icId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            sales = r.GetGuid(0); purchase = r.GetGuid(1);
        }
        return new IcOrderResult(icId, await OrderCommands.ResultOfAsync(tx, OrderKind.Sales, tenantId, sales, ct), await OrderCommands.ResultOfAsync(tx, OrderKind.Purchase, tenantId, purchase, ct));
    }
}
