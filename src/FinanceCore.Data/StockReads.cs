using System.Text.Json;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;
using FinanceCore.Core.Reads;
using Npgsql;
using NpgsqlTypes;

namespace FinanceCore.Data;

/// <summary>
/// The stock reads (10, 8, permission read.inventory): stock-documents:get with the include `movements`, stock-documents:list, and stock-balances:query, which sums the base quantity
/// and the value of the movements up to the posting date (6.3, C1: the running columns are used only by the commands). One repeatable-read transaction of the read role.
/// Not built: stock-ledgers:query, stock-valuations:query with the reconciliation to the GL, availability and lot traces, the includes journal, layers and integrity.
/// </summary>
public sealed class StockReads(NpgsqlDataSource source)
{
    private static readonly string[] Includes = ["movements"];

    private static CommandError Error(string code, string phase, string path, params (string Key, object? Value)[] parameters) =>
        JournalDocumentPoster.Error(code, phase, path, parameters);

    private static CommandResult<T> Reject<T>(CommandError e) => CommandResult<T>.Rejected([e]);

    private static CommandError? Denied(CommandContext ctx) =>
        ctx.Permissions.Contains(StockValuation.ReadPermission) ? null : Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", StockValuation.ReadPermission));

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public async Task<CommandResult<StockDocumentDetail>> GetAsync(Guid tenantId, CommandContext ctx, GetStockDocumentRequest req, CancellationToken ct = default)
    {
        if (Denied(ctx) is { } denied) return Reject<StockDocumentDetail>(denied);
        if (req.Id == Guid.Empty) return Reject<StockDocumentDetail>(Error("SCHEMA.INVALID_VALUE", "P1", "id", ("reason", "required")));
        IReadOnlyList<string> include = req.Include ?? [];
        foreach (string i in include)
            if (!Includes.Contains(i)) return Reject<StockDocumentDetail>(Error("READ.INVALID_FILTER", "P1", "include", ("field", i), ("allowed", Includes)));
        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct, snapshot: true);
        Guid companyId; string json;
        await using (var cmd = tx.Command("select company_id, (to_jsonb(d) - 'tenant_id')::text from core.stock_document d where tenant_id = @t and stock_document_id = @i"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("i", req.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Reject<StockDocumentDetail>(Error("READ.NOT_FOUND", "P2", "id"));
            companyId = r.GetGuid(0); json = r.GetString(1);
        }
        JsonElement? movements = null;
        if (include.Contains("movements"))
        {
            await using var cmd = tx.Command(
                "select coalesce(jsonb_agg(to_jsonb(m) - 'tenant_id' - 'company_id' - 'stock_document_id' order by line_no), '[]'::jsonb)::text from core.stock_movement m " +
                "where tenant_id = @t and company_id = @c and stock_document_id = @i");
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", req.Id);
            movements = Parse((string)(await cmd.ExecuteScalarAsync(ct))!);
        }
        return CommandResult<StockDocumentDetail>.Ok(new StockDocumentDetail(Parse(json), movements), Guid.Empty);
    }

    public async Task<CommandResult<StockDocumentList>> ListAsync(Guid tenantId, CommandContext ctx, ListStockDocumentsRequest req, CancellationToken ct = default)
    {
        if (Denied(ctx) is { } denied) return Reject<StockDocumentList>(denied);
        int limit = req.Limit ?? ReadRules.DefaultLimit;
        if (limit is < 1 or > ReadRules.MaxLimit) return Reject<StockDocumentList>(Error("READ.LIMIT_EXCEEDED", "P1", "limit", ("max", ReadRules.MaxLimit)));
        if (!ReadRules.TryDecodeCursor(req.Cursor, "stk", out long after)) return Reject<StockDocumentList>(Error("READ.CURSOR_INVALID", "P1", "cursor"));
        if (req.DocumentType is not (null or "STOCK_RECEIPT" or "STOCK_ISSUE" or "STOCK_ADJUSTMENT" or "STOCK_TRANSFER" or "STOCK_STATUS_CHANGE" or "STOCK_COST_ADJUSTMENT" or "STOCK_RETURN" or "STOCK_OPENING" or "STOCK_REVERSAL"))
            return Reject<StockDocumentList>(Error("READ.INVALID_FILTER", "P1", "document_type", ("field", "document_type")));
        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct, snapshot: true);
        var items = new List<JsonElement>();
        await using (var cmd = tx.Command(
            "select (to_jsonb(d) - 'tenant_id')::text, d.chain_seq from core.stock_document d where tenant_id = @t and chain_seq > 0 and (@c::uuid is null or company_id = @c) " +
            "and (@type::text is null or document_type = @type) and (@from::date is null or posting_date >= @from) and (@to::date is null or posting_date <= @to) " +
            "and (@ref::text is null or source_document_ref = @ref) " +
            "and (@item::uuid is null or exists (select 1 from core.stock_movement m where m.tenant_id = d.tenant_id and m.company_id = d.company_id and m.stock_document_id = d.stock_document_id and m.item_id = @item)) " +
            "and (@wh::uuid is null or exists (select 1 from core.stock_movement m where m.tenant_id = d.tenant_id and m.company_id = d.company_id and m.stock_document_id = d.stock_document_id and m.warehouse_id = @wh)) " +
            "order by company_id, chain_seq offset @o limit @n"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("o", after); cmd.Parameters.AddWithValue("n", limit + 1);
            cmd.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Uuid) { Value = (object?)req.CompanyId ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("type", NpgsqlDbType.Text) { Value = (object?)req.DocumentType ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.Date) { Value = (object?)req.DateFrom ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.Date) { Value = (object?)req.DateTo ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("ref", NpgsqlDbType.Text) { Value = (object?)req.SourceDocumentRef ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("item", NpgsqlDbType.Uuid) { Value = (object?)req.ItemId ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("wh", NpgsqlDbType.Uuid) { Value = (object?)req.WarehouseId ?? DBNull.Value });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) items.Add(Parse(r.GetString(0)));
        }
        string? next = null;
        if (items.Count > limit) { items.RemoveAt(limit); next = ReadRules.EncodeCursor("stk", after + limit); }
        return CommandResult<StockDocumentList>.Ok(new StockDocumentList(items, new ReadPage(next, limit)), Guid.Empty);
    }

    public async Task<CommandResult<StockBalancesResult>> BalancesAsync(Guid tenantId, CommandContext ctx, StockBalancesRequest req, CancellationToken ct = default)
    {
        if (Denied(ctx) is { } denied) return Reject<StockBalancesResult>(denied);
        DateOnly asOf = req.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct, snapshot: true);
        var rows = new List<StockBalanceRow>();
        await using (var cmd = tx.Command(
            "select item_id, warehouse_id, sum(base_quantity), sum(value_lc) from core.stock_movement where tenant_id = @t and company_id = @c and posting_date <= @d " +
            "and (@i::uuid is null or item_id = @i) and (@w::uuid is null or warehouse_id = @w) and stock_status = 'AVAILABLE' group by item_id, warehouse_id " +
            "having sum(base_quantity) <> 0 or sum(value_lc) <> 0 order by item_id, warehouse_id"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("d", asOf);
            cmd.Parameters.Add(new NpgsqlParameter("i", NpgsqlDbType.Uuid) { Value = (object?)req.ItemId ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("w", NpgsqlDbType.Uuid) { Value = (object?)req.WarehouseId ?? DBNull.Value });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) rows.Add(new StockBalanceRow(r.GetGuid(0), r.GetGuid(1), r.GetDecimal(2), r.GetDecimal(3)));
        }
        return CommandResult<StockBalancesResult>.Ok(new StockBalancesResult(asOf, rows, rows.Sum(x => x.ValueLc)), Guid.Empty);
    }
}
