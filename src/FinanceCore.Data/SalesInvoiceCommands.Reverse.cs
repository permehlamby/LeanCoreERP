using System.Text.Json;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;
using FinanceCore.Core.Posting;
using Npgsql;
using NpgsqlTypes;

namespace FinanceCore.Data;

/// <summary>
/// sales-invoices:reverse (11, 6.4): the owning command of an order-built AR_INVOICE (6.8). The journal document is reversed by the reversal of the finance pipeline (mirror with the
/// document type AR_INVOICE, the automatic clearing of its open item, REVERSAL.HAS_CLEARED_LINES if the receivable is cleared otherwise) and the mirror link rows with negative quantities and
/// amounts are inserted in the same transaction; the digest of the mirror rows is the detail_digest of the mirror journal document. A credit note that is not reversed blocks it
/// (ORDER.INVOICE_HAS_CREDIT_NOTES). The order rows are locked first (11, 9). The invoiced quantity of the order line and of the delivery line is restored, so they can be invoiced again.
/// </summary>
public sealed partial class SalesInvoiceCommands
{
    private const string ReverseCommandName = "sales-invoices:reverse";

    private sealed record LinkLine(
        int LineNo, Guid OrderId, int OrderLineNo, Guid? DeliveryId, int? DeliveryLineNo, decimal Quantity, string UnitCode, decimal BaseQuantity, decimal UnitPrice, decimal DiscountPct, decimal Net,
        decimal? OrderUnitPrice, decimal? OrderDiscountPct, Guid? TaxCodeId, Guid? TaxCodeVersionId, DateOnly? TaxDate, int JournalLineNo);

    public async Task<CommandResult<ReversedSalesInvoice>> ReverseAsync(Guid tenantId, CommandContext ctx, ReverseSalesInvoiceRequest req, CancellationToken ct = default)
    {
        static CommandResult<ReversedSalesInvoice> Fail(CommandError e) => CommandResult<ReversedSalesInvoice>.Rejected([e]);
        if (!ctx.Permissions.Contains(InvoiceRules.SalesPermission)) return Fail(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", InvoiceRules.SalesPermission)));
        var errors = MasterSupport.ValidateContext(ctx);
        if (req.CompanyId == Guid.Empty) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/company_id", ("reason", "required")));
        if (req.SalesInvoiceId == Guid.Empty) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/sales_invoice_id", ("reason", "required")));
        if (errors.Count > 0) return CommandResult<ReversedSalesInvoice>.Rejected(errors);
        if (string.IsNullOrWhiteSpace(req.ReasonCode)) return Fail(Error("REVERSAL.REASON_REQUIRED", "P1", "/reason_code"));
        if (!ReversalRules.ExternalReasons.Contains(req.ReasonCode)) return Fail(Error("REVERSAL.REASON_NOT_ALLOWED", "P1", "/reason_code", ("allowed_values", ReversalRules.ExternalReasons)));
        byte[] fingerprint = StockReversalRules.Fingerprint(ReverseCommandName, req.CompanyId, req.SalesInvoiceId, req.ReasonCode, req.PostingDate);

        // the invoice and its journal document: read first, the role of the reversal depends on the document
        Guid journalId; string kind;
        await using (TenantTransaction probe = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct))
        await using (var cmd = probe.Command("select journal_document_id, invoice_kind from core.sales_invoice where tenant_id = @t and company_id = @c and sales_invoice_id = @i"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("i", req.SalesInvoiceId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Fail(Error("REVERSAL.NOT_FOUND", "P2", "/sales_invoice_id"));
            journalId = r.GetGuid(0); kind = r.GetString(1);
        }
        if (kind != "INVOICE") return Fail(Error("REVERSAL.TYPE_NOT_REVERSIBLE", "P2", "/sales_invoice_id", ("invoice_kind", kind)));

        Guid mirrorInvoiceId = Guid.NewGuid();
        byte[]? digest = null;
        List<LinkLine> mirrorLines = [];
        Guid customer = Guid.Empty; Guid? terms = null; string currency = "", mode = ""; DateOnly mirrorDate = default;
        var reversal = new DocumentReverser.ReverseMode(ReverseCommandName, "AR_INVOICE", "ORDER",
            async (tx, original, token) =>
            {
                // the link records of the invoice
                var lines = new List<LinkLine>();
                await using (var cmd = tx.Command(
                    "select line_no, sales_order_id, sales_order_line_no, delivery_id, delivery_line_no, quantity, unit_code, base_quantity, unit_price, discount_pct, net_amount, order_unit_price, order_discount_pct, " +
                    "tax_code_id, tax_code_version_id, tax_date, journal_line_no from core.sales_invoice_line where tenant_id = @t and company_id = @c and sales_invoice_id = @i order by line_no"))
                {
                    cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("i", req.SalesInvoiceId);
                    await using var r = await cmd.ExecuteReaderAsync(token);
                    while (await r.ReadAsync(token))
                        lines.Add(new LinkLine(r.GetInt32(0), r.GetGuid(1), r.GetInt32(2), r.IsDBNull(3) ? null : r.GetGuid(3), r.IsDBNull(4) ? null : r.GetInt32(4), r.GetDecimal(5), r.GetString(6).Trim(), r.GetDecimal(7),
                            r.GetDecimal(8), r.GetDecimal(9), r.GetDecimal(10), r.IsDBNull(11) ? null : r.GetDecimal(11), r.IsDBNull(12) ? null : r.GetDecimal(12), r.IsDBNull(13) ? null : r.GetGuid(13),
                            r.IsDBNull(14) ? null : r.GetGuid(14), r.IsDBNull(15) ? null : DateOnly.FromDateTime(r.GetDateTime(15)), r.GetInt32(16)));
                }
                // position 1 of the lock order: the order rows, sorted
                await using (var cmd = tx.Command(
                    "select sales_order_id from core.sales_order where tenant_id = @t and company_id = @c and sales_order_id = any(@ids) order by sales_order_id for update"))
                {
                    cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("ids", lines.Select(l => l.OrderId).Distinct().Order().ToArray());
                    await using var r = await cmd.ExecuteReaderAsync(token);
                    while (await r.ReadAsync(token)) { }
                }
                // a credit note of the invoice that is not reversed is reversed first (6.4, Q25)
                await using (var cmd = tx.Command(
                    "select exists (select 1 from core.sales_invoice c where c.tenant_id = @t and c.company_id = @c and c.credits_invoice_id = @i and c.invoice_kind = 'CREDIT_NOTE' " +
                    "and not exists (select 1 from core.sales_invoice m where m.tenant_id = c.tenant_id and m.company_id = c.company_id and m.reversal_of_sales_invoice_id = c.sales_invoice_id))"))
                {
                    cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("i", req.SalesInvoiceId);
                    if ((bool)(await cmd.ExecuteScalarAsync(token))!) return Error("ORDER.INVOICE_HAS_CREDIT_NOTES", "P6", "/sales_invoice_id");
                }
                await using (var cmd = tx.Command("select customer_partner_id, payment_terms_id, order_currency, invoice_mode from core.sales_invoice where tenant_id = @t and company_id = @c and sales_invoice_id = @i"))
                {
                    cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("i", req.SalesInvoiceId);
                    await using var r = await cmd.ExecuteReaderAsync(token);
                    await r.ReadAsync(token);
                    customer = r.GetGuid(0); terms = r.IsDBNull(1) ? null : r.GetGuid(1); currency = r.GetString(2).Trim(); mode = r.IsDBNull(3) ? "" : r.GetString(3).Trim();
                }

                // the mirror rows: the same references, the opposite signs; the digest of the header and the rows binds the mirror journal document
                mirrorDate = req.PostingDate ?? (DateOnly)original.Header["posting_date"]!;
                mirrorLines = lines.Select(l => l with { Quantity = -l.Quantity, BaseQuantity = -l.BaseQuantity, Net = -l.Net }).ToList();
                var header = new Dictionary<string, object?>
                {
                    ["sales_invoice_id"] = mirrorInvoiceId, ["invoice_kind"] = "REVERSAL", ["customer_partner_id"] = customer, ["invoice_date"] = mirrorDate, ["due_date"] = null, ["payment_terms_id"] = null,
                    ["order_currency"] = currency, ["credits_invoice_id"] = null, ["credit_kind"] = null, ["reversal_of_sales_invoice_id"] = req.SalesInvoiceId, ["reason_code"] = req.ReasonCode,
                    ["invoice_mode"] = null, ["line_count"] = mirrorLines.Count
                };
                digest = InvoiceRules.Digest(header, mirrorLines.Select(l => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                {
                    ["line_no"] = l.LineNo, ["sales_order_id"] = l.OrderId, ["sales_order_line_no"] = l.OrderLineNo, ["delivery_id"] = l.DeliveryId, ["delivery_line_no"] = l.DeliveryLineNo, ["dropship_confirmation_id"] = null,
                    ["quantity"] = l.Quantity, ["unit_code"] = l.UnitCode, ["base_quantity"] = l.BaseQuantity, ["unit_price"] = l.UnitPrice, ["discount_pct"] = l.DiscountPct, ["net_amount"] = l.Net,
                    ["order_unit_price"] = l.OrderUnitPrice, ["order_discount_pct"] = l.OrderDiscountPct, ["tax_code_id"] = l.TaxCodeId, ["tax_code_version_id"] = l.TaxCodeVersionId, ["tax_date"] = l.TaxDate,
                    ["journal_line_no"] = l.JournalLineNo
                }).ToList());
                return null;
            },
            async (tx, mirrorJournalId, token) =>
            {
                await using (var cmd = tx.Command(
                    "insert into core.sales_invoice (tenant_id, company_id, sales_invoice_id, invoice_kind, customer_partner_id, invoice_date, order_currency, journal_document_id, reversal_of_sales_invoice_id, reason_code, " +
                    "source_system, created_by, correlation_id, idempotency_key) values (@t, @c, @id, 'REVERSAL', @cust, @d, @cur, @j, @orig, @reason, @src, @by, @corr, @key)"))
                {
                    cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("id", mirrorInvoiceId); cmd.Parameters.AddWithValue("cust", customer);
                    cmd.Parameters.AddWithValue("d", mirrorDate); cmd.Parameters.AddWithValue("cur", currency); cmd.Parameters.AddWithValue("j", mirrorJournalId); cmd.Parameters.AddWithValue("orig", req.SalesInvoiceId);
                    cmd.Parameters.AddWithValue("reason", req.ReasonCode); cmd.Parameters.AddWithValue("src", ctx.SourceSystem); cmd.Parameters.AddWithValue("by", ctx.Actor);
                    cmd.Parameters.AddWithValue("corr", ctx.CorrelationId); cmd.Parameters.AddWithValue("key", ctx.IdempotencyKey);
                    await cmd.ExecuteNonQueryAsync(token);
                }
                foreach (LinkLine l in mirrorLines)
                {
                    await using var cmd = tx.Command(
                        "insert into core.sales_invoice_line (tenant_id, company_id, sales_invoice_id, line_no, sales_order_id, sales_order_line_no, delivery_id, delivery_line_no, quantity, unit_code, base_quantity, unit_price, " +
                        "discount_pct, net_amount, order_unit_price, order_discount_pct, tax_code_id, tax_code_version_id, tax_date, journal_line_no) values (@t, @c, @id, @n, @o, @ol, @d, @dl, @q, @u, @bq, @up, @dp, @net, @oup, " +
                        "@odp, @tc, @tv, @td, @jl)");
                    cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("id", mirrorInvoiceId); cmd.Parameters.AddWithValue("n", l.LineNo);
                    cmd.Parameters.AddWithValue("o", l.OrderId); cmd.Parameters.AddWithValue("ol", l.OrderLineNo); cmd.Parameters.AddWithValue("q", l.Quantity); cmd.Parameters.AddWithValue("u", l.UnitCode);
                    cmd.Parameters.AddWithValue("bq", l.BaseQuantity); cmd.Parameters.AddWithValue("up", l.UnitPrice); cmd.Parameters.AddWithValue("dp", l.DiscountPct); cmd.Parameters.AddWithValue("net", l.Net);
                    cmd.Parameters.AddWithValue("jl", l.JournalLineNo);
                    cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Uuid) { Value = (object?)l.DeliveryId ?? DBNull.Value });
                    cmd.Parameters.Add(new NpgsqlParameter("dl", NpgsqlDbType.Integer) { Value = (object?)l.DeliveryLineNo ?? DBNull.Value });
                    cmd.Parameters.Add(new NpgsqlParameter("oup", NpgsqlDbType.Numeric) { Value = (object?)l.OrderUnitPrice ?? DBNull.Value });
                    cmd.Parameters.Add(new NpgsqlParameter("odp", NpgsqlDbType.Numeric) { Value = (object?)l.OrderDiscountPct ?? DBNull.Value });
                    cmd.Parameters.Add(new NpgsqlParameter("tc", NpgsqlDbType.Uuid) { Value = (object?)l.TaxCodeId ?? DBNull.Value });
                    cmd.Parameters.Add(new NpgsqlParameter("tv", NpgsqlDbType.Uuid) { Value = (object?)l.TaxCodeVersionId ?? DBNull.Value });
                    cmd.Parameters.Add(new NpgsqlParameter("td", NpgsqlDbType.Date) { Value = (object?)l.TaxDate ?? DBNull.Value });
                    await cmd.ExecuteNonQueryAsync(token);
                }
                await CommandSupport.WriteOutboxAsync(tx, tenantId, req.CompanyId, ctx, "invoice.reversed", "SALES_INVOICE", $"{req.CompanyId:D}/{req.SalesInvoiceId:D}", "REVERSED", DateTime.UtcNow, isAudit: true, null,
                    JsonSerializer.Serialize(new Dictionary<string, object?> { ["reversal_invoice_id"] = mirrorInvoiceId, ["journal_document_id"] = mirrorJournalId, ["lines"] = mirrorLines.Count }), token);
            },
            () => digest);

        var reverser = new DocumentReverser(source);
        var result = await reverser.ReverseOwnedAsync(tenantId, ctx, new ReverseDocumentRequest(journalId, req.PostingDate, req.ReasonCode, null), fingerprint, reversal, ct);
        if (result.Status != CommandStatus.Ok) return CommandResult<ReversedSalesInvoice>.Rejected(result.Errors.ToList());
        PostedDocument doc = result.Value!;
        Guid reversalId;
        await using (TenantTransaction read = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct))
        await using (var cmd = read.Command("select sales_invoice_id from core.sales_invoice where tenant_id = @t and company_id = @c and journal_document_id = @j"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("j", doc.DocumentId);
            reversalId = (Guid)(await cmd.ExecuteScalarAsync(ct))!;
        }
        return CommandResult<ReversedSalesInvoice>.Ok(new ReversedSalesInvoice(req.SalesInvoiceId, reversalId, doc.DocumentId, doc.DocumentNumber, doc.RowHash), result.CommandLogId ?? Guid.Empty, result.Warnings, result.IdempotentReplay);
    }
}
