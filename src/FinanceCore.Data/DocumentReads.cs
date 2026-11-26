using System.Text.Json;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;
using Npgsql;
using NpgsqlTypes;

namespace FinanceCore.Data;

/// <summary>
/// The get and list of the documents of orders (11, 8, permission read.orders): sales-deliveries, purchase-receipts, sales-returns, purchase-returns, sales-invoices and purchase-invoices. A get gives
/// the header as typed JSON (the hashes as hex) and the includes `lines` (for a purchase invoice with the match details: the receipt, the order price, the price difference and its parts), for
/// an invoice `journal_document` and `issue_records`. A list filters by company, partner, date range and status (a fulfilment document) or kind (an invoice), newest first, cut at the limit. One
/// snapshot of the read role. Not built: a cursor, the filter by issue state (22), the dropship confirmations (not built), the include of the stock document.
/// </summary>
public sealed class DocumentReads(NpgsqlDataSource source)
{
    private static CommandError Error(string code, string phase, string path, params (string Key, object? Value)[] parameters) => JournalDocumentPoster.Error(code, phase, path, parameters);
    private static CommandResult<T> Reject<T>(CommandError e) => CommandResult<T>.Rejected([e]);
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // the header without what is internal: the tenant, the chain columns, and the row hash as hex
    private static string HeaderJson(DocumentKind k) =>
        k.Chained ? "((to_jsonb(h) - 'tenant_id' - 'prev_hash' - 'line_digest' - 'row_hash') || jsonb_build_object('row_hash', encode(h.row_hash, 'hex')))::text" : "(to_jsonb(h) - 'tenant_id')::text";

    public async Task<CommandResult<DocumentDetail>> GetAsync(DocumentKind kind, Guid tenantId, CommandContext ctx, GetDocumentRequest req, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(OrderKind.ReadPermission)) return Reject<DocumentDetail>(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", OrderKind.ReadPermission)));
        if (req.Id == Guid.Empty) return Reject<DocumentDetail>(Error("SCHEMA.INVALID_VALUE", "P1", "id", ("reason", "required")));
        IReadOnlyList<string> include = req.Include ?? [];
        foreach (string i in include)
            if (!DocumentKind.Includes.Contains(i) || (!kind.Invoice && i != "lines"))
                return Reject<DocumentDetail>(Error("READ.INVALID_FILTER", "P1", "include", ("field", i), ("allowed", kind.Invoice ? DocumentKind.Includes : ["lines"])));
        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct, snapshot: true);
        string json; Guid companyId; Guid? journal = null;
        await using (var cmd = tx.Command($"select h.company_id, {HeaderJson(kind)}{(kind.Invoice ? ", h.journal_document_id" : "")} from core.{kind.Header} h where h.tenant_id = @t and h.{kind.IdColumn} = @i"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("i", req.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Reject<DocumentDetail>(Error("READ.NOT_FOUND", "P2", "id"));
            companyId = r.GetGuid(0); json = r.GetString(1);
            if (kind.Invoice) journal = r.GetGuid(2);
        }
        async Task<JsonElement> Query(string sql, Action<NpgsqlParameterCollection> bind)
        {
            await using var cmd = tx.Command(sql);
            bind(cmd.Parameters);
            return Parse((string)(await cmd.ExecuteScalarAsync(ct))!);
        }
        JsonElement? lines = include.Contains("lines")
            ? await Query($"select coalesce(jsonb_agg(to_jsonb(l) - 'tenant_id' - 'company_id' - '{kind.IdColumn}' order by line_no), '[]'::jsonb)::text from core.{kind.Line} l where tenant_id = @t and company_id = @c and {kind.IdColumn} = @i",
                p => { p.AddWithValue("t", tenantId); p.AddWithValue("c", companyId); p.AddWithValue("i", req.Id); })
            : null;
        JsonElement? journalDocument = include.Contains("journal_document")
            ? await Query("select coalesce((select jsonb_build_object('document_id', d.document_id, 'document_type', d.document_type, 'document_number', d.document_number, 'posting_date', d.posting_date, 'status', d.status, 'row_hash', encode(d.row_hash, 'hex'), " +
                "'detail_digest', encode(d.detail_digest, 'hex'), 'reversed_by_document_id', d.reversed_by_document_id) from core.journal_document d where d.tenant_id = @t and d.company_id = @c and d.document_id = @j), 'null'::jsonb)::text",
                p => { p.AddWithValue("t", tenantId); p.AddWithValue("c", companyId); p.AddWithValue("j", journal!.Value); })
            : null;
        JsonElement? issues = include.Contains("issue_records")
            ? await Query("select coalesce(jsonb_agg((to_jsonb(x) - 'tenant_id' - 'company_id' - 'document_sha256') || jsonb_build_object('document_sha256', encode(x.document_sha256, 'hex')) order by x.issued_at, x.issue_id), '[]'::jsonb)::text " +
                "from core.invoice_issue_record x where x.tenant_id = @t and x.company_id = @c and x.invoice_kind = @k and x.invoice_id = @i",
                p => { p.AddWithValue("t", tenantId); p.AddWithValue("c", companyId); p.AddWithValue("k", kind.InvoiceKind); p.AddWithValue("i", req.Id); })
            : null;
        return CommandResult<DocumentDetail>.Ok(new DocumentDetail(Parse(json), lines, journalDocument, issues), Guid.Empty);
    }

    public async Task<CommandResult<DocumentList>> ListAsync(DocumentKind kind, Guid tenantId, CommandContext ctx, ListDocumentsRequest req, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(OrderKind.ReadPermission)) return Reject<DocumentList>(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", OrderKind.ReadPermission)));
        if (req.CompanyId == Guid.Empty) return Reject<DocumentList>(Error("SCHEMA.INVALID_VALUE", "P1", "company_id", ("reason", "required")));
        int limit = req.Limit ?? DocumentKind.DefaultLimit;
        if (limit < 1 || limit > DocumentKind.MaxLimit) return Reject<DocumentList>(Error("READ.INVALID_FILTER", "P1", "limit", ("field", "limit"), ("allowed", $"1 to {DocumentKind.MaxLimit}")));
        string[] statuses = kind.Invoice ? ["INVOICE", "CREDIT_NOTE", "REVERSAL"] : ["posted", "reversed"];
        if (req.Status is not null && !statuses.Contains(req.Status)) return Reject<DocumentList>(Error("READ.INVALID_FILTER", "P1", "status", ("field", "status"), ("allowed", statuses)));
        if (req.DateFrom is { } from && req.DateTo is { } to && from > to) return Reject<DocumentList>(Error("READ.INVALID_FILTER", "P1", "date_from", ("field", "date_from"), ("allowed", "not after date_to")));
        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct, snapshot: true);
        await using (var cmd = tx.Command("select 1 from core.company where tenant_id = @t and company_id = @c"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId);
            if (await cmd.ExecuteScalarAsync(ct) is null) return Reject<DocumentList>(Error("READ.NOT_FOUND", "P2", "company_id"));
        }
        var items = new List<JsonElement>();
        await using (var cmd = tx.Command(
            $"select {HeaderJson(kind)} from core.{kind.Header} h where h.tenant_id = @t and h.company_id = @c and (@p::uuid is null or h.{kind.PartyColumn} = @p) and (@f::date is null or h.{kind.DateColumn} >= @f) " +
            $"and (@u::date is null or h.{kind.DateColumn} <= @u) and (@s::text is null or h.{kind.StatusColumn} = @s) order by h.{kind.DateColumn} desc, h.{kind.IdColumn} limit @n"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("n", limit + 1);
            cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Uuid) { Value = (object?)req.PartnerId ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("f", NpgsqlDbType.Date) { Value = (object?)req.DateFrom ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("u", NpgsqlDbType.Date) { Value = (object?)req.DateTo ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Text) { Value = (object?)req.Status ?? DBNull.Value });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) items.Add(Parse(r.GetString(0)));
        }
        return CommandResult<DocumentList>.Ok(new DocumentList(items.Take(limit).ToList(), limit, items.Count > limit), Guid.Empty);
    }
}
