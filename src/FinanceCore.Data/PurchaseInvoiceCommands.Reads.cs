using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;
using FinanceCore.Core.Reads;
using Npgsql;

namespace FinanceCore.Data;

/// <summary>
/// gr-ir:query (11, 8, permission read.orders): the GR/IR items of a company, that is the open items without partner on the accounts that its GR_IR role names, with the document that holds each (a receipt,
/// a receipt without an order, a return, an invoice, a credit note or a write-off), the supplier and the orders where the document has them, the age in days, the value and the state: OPEN (open or
/// partly cleared), WRITTEN_OFF (cleared by gr-ir:write-off and not reset), CLEARED. The answer also gives the reconciliation: the residual of all open items and the balance of the accounts in the ledger,
/// which are equal because every clearing of a GR/IR item nets to zero. One snapshot of the read role. A cursor is an offset in the order (posting date, document, line), signed and bound to the query. Not built: the as-of reconstruction of past states, the
/// order line of an item (only the orders of its document).
/// </summary>
public sealed partial class PurchaseInvoiceCommands
{
    public async Task<CommandResult<GrIrQueryResult>> QueryGrIrAsync(Guid tenantId, CommandContext ctx, GrIrQueryRequest req, CancellationToken ct = default)
    {
        static CommandResult<GrIrQueryResult> Fail(CommandError e) => CommandResult<GrIrQueryResult>.Rejected([e]);
        if (!ctx.Permissions.Contains(OrderKind.ReadPermission)) return Fail(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", OrderKind.ReadPermission)));
        if (req.CompanyId == Guid.Empty) return Fail(Error("SCHEMA.INVALID_VALUE", "P1", "company_id", ("reason", "required")));
        string state = req.State ?? "OPEN";
        if (!GrIrQueryRules.States.Contains(state)) return Fail(Error("READ.INVALID_FILTER", "P1", "state", ("field", "state"), ("allowed", GrIrQueryRules.States)));
        int limit = req.Limit ?? GrIrQueryRules.DefaultLimit;
        if (limit < 1 || limit > GrIrQueryRules.MaxLimit) return Fail(Error("READ.INVALID_FILTER", "P1", "limit", ("field", "limit"), ("allowed", $"1 to {GrIrQueryRules.MaxLimit}")));
        DateOnly asOf = req.AsOf ?? DateOnly.FromDateTime(DateTime.UtcNow);
        string cursorKind = ReadRules.QueryKind("gr-ir", req.CompanyId, state, req.PurchaseOrderId, req.SupplierPartnerId);
        if (!ReadRules.TryDecodeCursor(req.Cursor, cursorKind, out long offset)) return Fail(Error("READ.CURSOR_INVALID", "P1", "cursor"));

        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct, snapshot: true);
        await using (var cmd = tx.Command("select 1 from core.company where tenant_id = @t and company_id = @c"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId);
            if (await cmd.ExecuteScalarAsync(ct) is null) return Fail(Error("READ.NOT_FOUND", "P2", "company_id"));
        }
        var all = new List<GrIrQueryItem>();
        await using (var cmd = tx.Command(
            "select oi.document_id, oi.line_no, l.account_id, d.document_type, d.posting_date, l.amount_lc, oi.cleared_amount_lc, oi.clearing_status, " +
            "case when pr.receipt_id is not null then 'RECEIPT' when ret.return_id is not null then 'RETURN' when sd.document_type = 'STOCK_RECEIPT' and sd.command_name = 'stock-documents:post' then 'DIRECT_RECEIPT' " +
            "when pi.invoice_kind = 'INVOICE' then 'INVOICE' when pi.invoice_kind = 'CREDIT_NOTE' then 'CREDIT_NOTE' when d.document_type = 'GR_IR_WRITE_OFF' then 'WRITE_OFF' else 'OTHER' end, " +
            "coalesce(pr.receipt_id, ret.return_id, sd.stock_document_id, pi.purchase_invoice_id, d.document_id), coalesce(pr.supplier_partner_id, ret.supplier_partner_id, pi.supplier_partner_id), " +
            "coalesce(case when pr.receipt_id is not null then (select array_agg(distinct x.purchase_order_id) from core.purchase_receipt_line x where x.tenant_id = pr.tenant_id and x.company_id = pr.company_id and x.receipt_id = pr.receipt_id) " +
            "when ret.return_id is not null then (select array_agg(distinct x.purchase_order_id) from core.purchase_return_line x where x.tenant_id = ret.tenant_id and x.company_id = ret.company_id and x.return_id = ret.return_id) " +
            "when pi.purchase_invoice_id is not null then (select array_agg(distinct x.purchase_order_id) from core.purchase_invoice_line x where x.tenant_id = pi.tenant_id and x.company_id = pi.company_id and x.purchase_invoice_id = pi.purchase_invoice_id and x.purchase_order_id is not null) end, '{}'::uuid[]), " +
            "exists (select 1 from core.clearing_item ci join core.clearing c on c.tenant_id = ci.tenant_id and c.company_id = ci.company_id and c.clearing_id = ci.clearing_id where ci.tenant_id = oi.tenant_id and ci.company_id = oi.company_id " +
            "and ci.open_item_document_id = oi.document_id and ci.open_item_line_no = oi.line_no and c.command_name = 'gr-ir:write-off' and c.clearing_type = 'CLEAR' and not exists (select 1 from core.clearing r where r.tenant_id = c.tenant_id " +
            "and r.company_id = c.company_id and r.reset_of_clearing_id = c.clearing_id)) " +
            "from core.journal_line_open_item oi join core.journal_line l on l.tenant_id = oi.tenant_id and l.company_id = oi.company_id and l.document_id = oi.document_id and l.line_no = oi.line_no " +
            "join core.journal_document d on d.tenant_id = l.tenant_id and d.company_id = l.company_id and d.document_id = l.document_id " +
            "left join core.stock_document sd on sd.tenant_id = d.tenant_id and sd.company_id = d.company_id and sd.journal_document_id = d.document_id " +
            "left join core.purchase_receipt pr on pr.tenant_id = sd.tenant_id and pr.company_id = sd.company_id and pr.stock_document_id = sd.stock_document_id " +
            "left join core.purchase_return ret on ret.tenant_id = sd.tenant_id and ret.company_id = sd.company_id and ret.stock_document_id = sd.stock_document_id " +
            "left join core.purchase_invoice pi on pi.tenant_id = d.tenant_id and pi.company_id = d.company_id and pi.journal_document_id = d.document_id " +
            "where oi.tenant_id = @t and oi.company_id = @c and oi.partner_id is null and exists (select 1 from core.account_determination a where a.tenant_id = l.tenant_id and a.company_id = l.company_id " +
            "and a.role_code = 'GR_IR' and a.account_id = l.account_id) order by d.posting_date, oi.document_id, oi.line_no"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                string docType = r.GetString(3), status = r.GetString(7).Trim();
                decimal amount = r.GetDecimal(5), cleared = r.GetDecimal(6);
                string itemState = status != "CLEARED" ? "OPEN" : r.GetBoolean(12) && docType != "GR_IR_WRITE_OFF" ? "WRITTEN_OFF" : "CLEARED";
                DateOnly posted = DateOnly.FromDateTime(r.GetDateTime(4));
                all.Add(new GrIrQueryItem(r.GetGuid(0), r.GetInt32(1), r.GetGuid(2), docType, posted, asOf.DayNumber - posted.DayNumber, amount, cleared, amount - cleared, itemState, r.GetString(8), r.GetGuid(9),
                    r.IsDBNull(10) ? null : r.GetGuid(10), (Guid[])r[11]));
            }
        }
        decimal gl;
        await using (var cmd = tx.Command(
            "select coalesce(sum(l.amount_lc), 0) from core.journal_line l where l.tenant_id = @t and l.company_id = @c and exists (select 1 from core.account_determination a where a.tenant_id = l.tenant_id " +
            "and a.company_id = l.company_id and a.role_code = 'GR_IR' and a.account_id = l.account_id)"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId);
            gl = (decimal)(await cmd.ExecuteScalarAsync(ct))!;
        }
        decimal open = all.Where(i => i.State == "OPEN").Sum(i => i.ResidualLc);
        var filtered = all.Where(i => (state == "ALL" || i.State == state) && (req.PurchaseOrderId is null || i.OrderIds.Contains(req.PurchaseOrderId.Value)) && (req.SupplierPartnerId is null || i.SupplierPartnerId == req.SupplierPartnerId)).ToList();
        return CommandResult<GrIrQueryResult>.Ok(new GrIrQueryResult(filtered.Skip((int)offset).Take(limit).ToList(), open, gl, open == gl,
            new ReadPage(filtered.Count > offset + limit ? ReadRules.EncodeCursor(cursorKind, offset + limit) : null, limit)), Guid.Empty);
    }
}
