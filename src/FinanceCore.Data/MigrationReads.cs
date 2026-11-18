using System.Text.Json;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Migration;
using FinanceCore.Core.Reads;
using Npgsql;
using NpgsqlTypes;

namespace FinanceCore.Data;

/// <summary>
/// The reads of the migration (13, 7, 09 section 3): migration-batches:get and :list, and migration-records:list. All need read.audit (09, 2.3: migration batches, their control
/// totals, reconciliations and commit chunks, and the staged records). One repeatable-read transaction of the read role. Not built: the filters of 09 on dates and the sort,
/// migration-control-totals, migration-reconciliations and migration-commit-chunks as lists of their own (the batch detail carries them).
/// </summary>
public sealed class MigrationReads(NpgsqlDataSource source)
{
    public const string Permission = PartnerReadRules.AuditPermission;

    private static CommandError Error(string code, string phase, string path, params (string Key, object? Value)[] parameters) =>
        JournalDocumentPoster.Error(code, phase, path, parameters);

    private static CommandResult<T> Reject<T>(CommandError e) => CommandResult<T>.Rejected([e]);

    private static CommandError? Denied(CommandContext ctx) =>
        ctx.Permissions.Contains(Permission) ? null : Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", Permission));

    private static string? Text(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static DateTime Utc(NpgsqlDataReader r, int i) => DateTime.SpecifyKind(r.GetDateTime(i), DateTimeKind.Utc);

    private static DateTime? UtcOrNull(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : Utc(r, i);

    private const string BatchSelect =
        "batch_id, company_id, status, cutover_date, source_system, opened_by, opened_at, committed_at, sealed_at, accepted_reconciliation_id, commit_requested_by, commit_requested_at, " +
        "commit_approved_by, commit_approved_at, seal_requested_by, seal_requested_at, seal_approved_by, seal_approved_at from core.migration_batch ";

    private static MigrationBatchRead ReadBatch(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetFieldValue<DateOnly>(3), r.GetString(4), r.GetString(5), Utc(r, 6), UtcOrNull(r, 7), UtcOrNull(r, 8),
        r.IsDBNull(9) ? null : r.GetGuid(9), Text(r, 10), UtcOrNull(r, 11), Text(r, 12), UtcOrNull(r, 13), Text(r, 14), UtcOrNull(r, 15), Text(r, 16), UtcOrNull(r, 17));

    private static async Task<long> CountAsync(TenantTransaction tx, string sql, Guid tenantId, Guid batchId, CancellationToken ct)
    {
        await using var cmd = tx.Command(sql);
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("b", batchId);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct) ?? 0L, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<CommandResult<BatchDetail>> GetBatchAsync(Guid tenantId, CommandContext ctx, GetBatchRequest req, CancellationToken ct = default)
    {
        if (Denied(ctx) is { } denied) return Reject<BatchDetail>(denied);
        if (req.BatchId == Guid.Empty) return Reject<BatchDetail>(Error("SCHEMA.INVALID_VALUE", "P1", "batch_id", ("reason", "required")));
        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct, snapshot: true);
        MigrationBatchRead? batch = null;
        await using (var cmd = tx.Command("select " + BatchSelect + "where tenant_id = @t and batch_id = @b"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("b", req.BatchId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) batch = ReadBatch(r);
        }
        if (batch is null) return Reject<BatchDetail>(Error("READ.NOT_FOUND", "P2", "batch_id"));

        // the latest declared total per kind and key counts (13, 3.2), so the list shows what a reconciliation compares with
        var totals = new List<ControlTotalRead>();
        await using (var cmd = tx.Command(
            "select distinct on (control_kind, key_1, key_2) control_no, control_kind, key_1, key_2, amount_lc, amount_gc, amount_rc, declared_at, declared_by from core.migration_control_total " +
            "where tenant_id = @t and batch_id = @b order by control_kind, key_1, key_2, control_no desc"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("b", req.BatchId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                totals.Add(new ControlTotalRead(r.GetInt32(0), r.GetString(1), Text(r, 2), Text(r, 3), r.IsDBNull(4) ? null : r.GetDecimal(4), r.IsDBNull(5) ? null : r.GetDecimal(5),
                    r.IsDBNull(6) ? null : r.GetDecimal(6), Utc(r, 7), r.GetString(8)));
        }
        var runs = new Dictionary<Guid, (DateTime At, string By, List<ReconciliationRow> Checks)>();
        await using (var cmd = tx.Command(
            "select reconciliation_id, check_no, check_code, key, declared_lc, declared_gc, declared_rc, computed_lc, computed_gc, computed_rc, result, detail, run_at, run_by " +
            "from core.migration_reconciliation where tenant_id = @t and batch_id = @b order by run_at, reconciliation_id, check_no"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("b", req.BatchId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                Guid id = r.GetGuid(0);
                if (!runs.TryGetValue(id, out var run)) runs[id] = run = (Utc(r, 12), r.GetString(13), []);
                decimal? D(int i) => r.IsDBNull(i) ? null : r.GetDecimal(i);
                run.Checks.Add(new ReconciliationRow(r.GetInt32(1), r.GetString(2), Text(r, 3), D(4), D(5), D(6), D(7), D(8), D(9), r.GetString(10), Text(r, 11)));
            }
        }
        var reconciliations = runs.Select(x => new ReconciliationRunRead(x.Key, x.Value.At, x.Value.By, x.Value.Checks.Count(c => c.Result == "FAIL"), x.Value.Checks.Count(c => c.Result == "WARNING"), x.Value.Checks)).ToList();
        var chunks = new List<ChunkResult>();
        await using (var cmd = tx.Command("select chunk_no, area, document_count, status from core.migration_commit_chunk where tenant_id = @t and batch_id = @b order by chunk_no"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("b", req.BatchId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) chunks.Add(new ChunkResult(r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetString(3)));
        }
        long records = await CountAsync(tx, "select count(*) from core.migration_record where tenant_id = @t and batch_id = @b and not superseded", tenantId, req.BatchId, ct);
        long invalid = await CountAsync(tx, "select count(*) from core.migration_record where tenant_id = @t and batch_id = @b and not superseded and status = 'INVALID'", tenantId, req.BatchId, ct);
        long documents = await CountAsync(tx, "select count(*) from core.journal_document where tenant_id = @t and migration_batch_id = @b", tenantId, req.BatchId, ct);
        return CommandResult<BatchDetail>.Ok(new BatchDetail(batch, totals, reconciliations, chunks, (int)records, (int)invalid, (int)documents), Guid.Empty);
    }

    public async Task<CommandResult<BatchList>> ListBatchesAsync(Guid tenantId, CommandContext ctx, ListBatchesRequest req, CancellationToken ct = default)
    {
        if (Denied(ctx) is { } denied) return Reject<BatchList>(denied);
        int limit = req.Limit ?? ReadRules.DefaultLimit;
        if (limit is < 1 or > ReadRules.MaxLimit) return Reject<BatchList>(Error("READ.LIMIT_EXCEEDED", "P1", "limit", ("max", ReadRules.MaxLimit)));
        if (!ReadRules.TryDecodeCursor(req.Cursor, "mb", out long offset)) return Reject<BatchList>(Error("READ.CURSOR_INVALID", "P1", "cursor"));
        string[] statuses = ["OPEN", "VALIDATED", "RECONCILED", "COMMITTING", "COMMITTED", "SEALED", "ABORTED", "ROLLED_BACK"];
        if (req.Status is not null && !statuses.Contains(req.Status)) return Reject<BatchList>(Error("READ.INVALID_FILTER", "P1", "status", ("field", "status"), ("allowed", statuses)));

        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct, snapshot: true);
        var items = new List<MigrationBatchRead>();
        await using (var cmd = tx.Command(
            "select " + BatchSelect + "where tenant_id = @t and (@c::uuid is null or company_id = @c) and (@s::text is null or status = @s) order by opened_at desc, batch_id offset @o limit @n"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("o", offset); cmd.Parameters.AddWithValue("n", limit + 1);
            cmd.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Uuid) { Value = (object?)req.CompanyId ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Text) { Value = (object?)req.Status ?? DBNull.Value });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) items.Add(ReadBatch(r));
        }
        string? next = null;
        if (items.Count > limit) { items.RemoveAt(limit); next = ReadRules.EncodeCursor("mb", offset + limit); }
        return CommandResult<BatchList>.Ok(new BatchList(items, new ReadPage(next, limit)), Guid.Empty);
    }

    public async Task<CommandResult<MigrationRecordList>> ListRecordsAsync(Guid tenantId, CommandContext ctx, ListRecordsRequest req, CancellationToken ct = default)
    {
        if (Denied(ctx) is { } denied) return Reject<MigrationRecordList>(denied);
        int limit = req.Limit ?? ReadRules.DefaultLimit;
        if (limit is < 1 or > ReadRules.MaxLimit) return Reject<MigrationRecordList>(Error("READ.LIMIT_EXCEEDED", "P1", "limit", ("max", ReadRules.MaxLimit)));
        if (!ReadRules.TryDecodeCursor(req.Cursor, "mr", out long after)) return Reject<MigrationRecordList>(Error("READ.CURSOR_INVALID", "P1", "cursor"));
        if (req.BatchId == Guid.Empty) return Reject<MigrationRecordList>(Error("SCHEMA.INVALID_VALUE", "P1", "batch_id", ("reason", "required")));
        if (req.Status is not (null or "RECEIVED" or "VALID" or "INVALID")) return Reject<MigrationRecordList>(Error("READ.INVALID_FILTER", "P1", "status", ("field", "status")));

        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct, snapshot: true);
        if (await CountAsync(tx, "select count(*) from core.migration_batch where tenant_id = @t and batch_id = @b", tenantId, req.BatchId, ct) == 0)
            return Reject<MigrationRecordList>(Error("READ.NOT_FOUND", "P2", "batch_id"));
        var items = new List<MigrationRecordRead>();
        await using (var cmd = tx.Command(
            "select record_no, record_type, legacy_ref, status, superseded, received_at, received_by, payload::text, errors::text from core.migration_record " +
            "where tenant_id = @t and batch_id = @b and record_no > @after and (@type::text is null or record_type = @type) and (@st::text is null or status = @st) " +
            "and (@sup or not superseded) order by record_no limit @n"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("b", req.BatchId); cmd.Parameters.AddWithValue("after", after);
            cmd.Parameters.AddWithValue("sup", req.IncludeSuperseded); cmd.Parameters.AddWithValue("n", limit + 1);
            cmd.Parameters.Add(new NpgsqlParameter("type", NpgsqlDbType.Text) { Value = (object?)req.RecordType ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("st", NpgsqlDbType.Text) { Value = (object?)req.Status ?? DBNull.Value });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                items.Add(new MigrationRecordRead(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetBoolean(4), Utc(r, 5), r.GetString(6),
                    JsonDocument.Parse(r.GetString(7)).RootElement.Clone(), Text(r, 8) is { } e ? JsonDocument.Parse(e).RootElement.Clone() : null));
        }
        string? next = null;
        if (items.Count > limit) { items.RemoveAt(limit); next = ReadRules.EncodeCursor("mr", items[^1].RecordNo); }
        return CommandResult<MigrationRecordList>.Ok(new MigrationRecordList(items, new ReadPage(next, limit)), Guid.Empty);
    }
}
