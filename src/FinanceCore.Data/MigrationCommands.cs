using System.Globalization;
using System.Text.Json;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Hashing;
using FinanceCore.Core.Migration;
using Npgsql;
using NpgsqlTypes;

namespace FinanceCore.Data;

/// <summary>
/// The migration batch (13): open, declare control totals, stage records, validate, reconcile, commit in chunks, seal and abort. First slice: the opening balances and the open
/// items of the general ledger, without history, stock, orders, legacy tax, four-eyes and rollback. A batch posts its documents (OPENING and OPENING_ITEMS, origin MIGRATION, the
/// batch id on each) as the migration role into a company that is still DRAFT, at the cutover date, with the amounts as supplied and DERIVED rates (13, 6). Every status change of
/// a batch writes one outbox event and the seal writes migration.sealed, none per record (E6).
/// </summary>
public sealed class MigrationCommands(NpgsqlDataSource source)
{
    public const string ManagePermission = "migration.manage", StagePermission = "migration.stage", CommitPermission = "migration.commit";
    private const int MaxStageRecords = 5000;
    private const string BatchEntity = "MIGRATION_BATCH";

    private sealed record BatchRow(Guid BatchId, Guid CompanyId, string Status, DateOnly CutoverDate, string SourceSystem, Guid? AcceptedReconciliationId);

    private static CommandError Error(string code, string phase, string path, params (string Key, object? Value)[] p) => JournalDocumentPoster.Error(code, phase, path, p);
    private static CommandResult<MigrationBatchResult> Reject(CommandError e) => CommandResult<MigrationBatchResult>.Rejected([e]);

    private static (CommandResult<MigrationBatchResult>? Denied, List<CommandError> Shape) Gate(CommandContext ctx, string permission)
    {
        var shape = new List<CommandError>();
        if (!ctx.Permissions.Contains(permission)) return (Reject(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", permission))), shape);
        void Invalid(string path, string reason) => shape.Add(Error("SCHEMA.INVALID_VALUE", "P1", path, ("reason", reason)));
        if (string.IsNullOrWhiteSpace(ctx.IdempotencyKey) || ctx.IdempotencyKey.Length > 80) Invalid("idempotency_key", "required, at most 80 characters");
        if (string.IsNullOrWhiteSpace(ctx.CorrelationId) || ctx.CorrelationId.Length > 100) Invalid("correlation_id", "required, at most 100 characters");
        if (string.IsNullOrWhiteSpace(ctx.Actor) || ctx.Actor.Length > 100) Invalid("actor", "required, at most 100 characters");
        return (null, shape);
    }

    private async Task<CommandResult<MigrationBatchResult>> RunAsync(Func<Task<CommandResult<MigrationBatchResult>>> once)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await once();
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.TableName == "command_log" && attempt == 0)
            {
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                return CommandResult<MigrationBatchResult>.Failed(new CommandError("SYSTEM.LOCK_TIMEOUT", "P7", "", null, true));
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure)
            {
                return CommandResult<MigrationBatchResult>.Failed(new CommandError("SYSTEM.CONFLICT", "P7", "", null, true));
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // shared reads and writes
    // ---------------------------------------------------------------------------------------------------------
    private static async Task<BatchRow?> ReadBatchAsync(TenantTransaction tx, Guid tenantId, Guid batchId, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = tx.Command("select company_id, status, cutover_date, source_system, accepted_reconciliation_id from core.migration_batch where tenant_id = @t and batch_id = @b" + (forUpdate ? " for update" : ""));
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("b", batchId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new BatchRow(batchId, r.GetGuid(0), r.GetString(1), r.GetFieldValue<DateOnly>(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetGuid(4)) : null;
    }

    private static async Task<long> CountAsync(TenantTransaction tx, string sql, Guid tenantId, Guid batchId, CancellationToken ct)
    {
        await using var cmd = tx.Command(sql);
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("b", batchId);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct) ?? 0L, CultureInfo.InvariantCulture);
    }

    private static async Task<MigrationBatchResult> SummaryAsync(
        TenantTransaction tx, Guid tenantId, Guid batchId, IReadOnlyList<StagedRecordResult>? records = null, IReadOnlyList<ReconciliationRow>? recon = null, int documents = 0, CancellationToken ct = default)
    {
        BatchRow b = (await ReadBatchAsync(tx, tenantId, batchId, false, ct))!;
        long total = await CountAsync(tx, "select count(*) from core.migration_record where tenant_id = @t and batch_id = @b and not superseded", tenantId, batchId, ct);
        long invalid = await CountAsync(tx, "select count(*) from core.migration_record where tenant_id = @t and batch_id = @b and not superseded and status = 'INVALID'", tenantId, batchId, ct);
        long totals = await CountAsync(tx, "select count(*) from core.migration_control_total where tenant_id = @t and batch_id = @b", tenantId, batchId, ct);
        var chunks = new List<ChunkResult>();
        await using (var cmd = tx.Command("select chunk_no, area, document_count, status from core.migration_commit_chunk where tenant_id = @t and batch_id = @b order by chunk_no"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("b", batchId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) chunks.Add(new ChunkResult(r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetString(3)));
        }
        return new MigrationBatchResult(batchId, b.CompanyId, b.Status, b.CutoverDate, b.SourceSystem, (int)total, (int)invalid, (int)totals, records, recon, chunks, documents);
    }

    private static async Task SetStatusAsync(
        TenantTransaction tx, Guid tenantId, CommandContext ctx, BatchRow batch, string status, bool clearAccepted, bool committed, bool sealedNow, CancellationToken ct, bool writeEvent = true)
    {
        await using (var cmd = tx.Command(
            "update core.migration_batch set status = @s, row_version = row_version + 1" + (clearAccepted ? ", accepted_reconciliation_id = null" : "") +
            (committed ? ", committed_at = now(), committed_by = @by" : "") + (sealedNow ? ", sealed_at = now(), sealed_by = @by" : "") + " where tenant_id = @t and batch_id = @b"))
        {
            cmd.Parameters.AddWithValue("s", status); cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("b", batch.BatchId);
            if (committed || sealedNow) cmd.Parameters.AddWithValue("by", ctx.Actor);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        if (writeEvent)
            await CommandSupport.WriteOutboxAsync(tx, tenantId, batch.CompanyId, ctx, "migration.batch_status_changed", BatchEntity, $"{batch.CompanyId:D}/{batch.BatchId:D}", "STATUS",
                DateTime.UtcNow, isAudit: true, null, JsonSerializer.Serialize(new Dictionary<string, object?> { ["batch_id"] = batch.BatchId, ["status"] = status }), ct);
    }

    /// <summary>The idempotency record of a command, and the replay of an earlier result.</summary>
    private static async Task<CommandResult<MigrationBatchResult>?> ReplayAsync(TenantTransaction tx, Guid tenantId, CommandContext ctx, byte[] fingerprint, CancellationToken ct)
    {
        CommandSupport.EarlierCommand? earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
        if (earlier is null) return null;
        if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint)) return Reject(Error("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
        return earlier.EntityId is null
            ? CommandResult<MigrationBatchResult>.Failed(Error("SYSTEM.REPLAY_RESULT_MISSING", "P1", ""))
            : CommandResult<MigrationBatchResult>.Ok(await SummaryAsync(tx, tenantId, earlier.EntityId.Value, ct: ct), earlier.CommandLogId, replay: true);
    }

    private static CommandResult<MigrationBatchResult> StatusInvalid(BatchRow b, params string[] allowed) =>
        Reject(Error("MIGRATION.BATCH_STATUS_INVALID", "P3", "batch_id", ("status", b.Status), ("allowed_statuses", allowed)));

    /// <summary>The batches of a PRODUCTION tenant need the four-eyes request and approval (13, Q13), which is not built: nothing is committed or sealed there.</summary>
    private static async Task<bool> IsProductionAsync(TenantTransaction tx, Guid tenantId, CancellationToken ct)
    {
        await using var cmd = tx.Command("select purpose from platform.tenant where tenant_id = @t");
        cmd.Parameters.AddWithValue("t", tenantId);
        return await cmd.ExecuteScalarAsync(ct) as string == "PRODUCTION";
    }

    // ---------------------------------------------------------------------------------------------------------
    // migration-batches:open
    // ---------------------------------------------------------------------------------------------------------
    public async Task<CommandResult<MigrationBatchResult>> OpenAsync(Guid tenantId, CommandContext ctx, OpenBatchRequest req, CancellationToken ct = default)
    {
        var (denied, shape) = Gate(ctx, ManagePermission);
        if (denied is not null) return denied;
        if (req.CompanyId == Guid.Empty) shape.Add(Error("SCHEMA.INVALID_VALUE", "P1", "company_id", ("reason", "required")));
        if (string.IsNullOrWhiteSpace(req.SourceSystem) || req.SourceSystem.Length > 50) shape.Add(Error("SCHEMA.INVALID_VALUE", "P1", "source_system", ("reason", "required, at most 50 characters")));
        if (shape.Count > 0) return CommandResult<MigrationBatchResult>.Rejected(shape);
        byte[] fingerprint = MigrationRecords.Fingerprint("migration-batches:open", req);
        return await RunAsync(async () =>
        {
            await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Migration, ct: ct);
            if (await ReplayAsync(tx, tenantId, ctx, fingerprint, ct) is { } replay) return replay;
            var company = await JournalDocumentPoster.ReadCompanyAsync(tx, tenantId, req.CompanyId, ct);
            if (company is null) return Reject(Error("COMPANY.NOT_FOUND", "P2", "company_id"));
            if (company.Status != "DRAFT") return Reject(Error("MIGRATION.COMPANY_NOT_DRAFT", "P2", "company_id", ("status", company.Status)));

            int? year = null; DateOnly yearEnd = default;
            await using (var cmd = tx.Command("select fiscal_year, end_date from core.fiscal_year where tenant_id = @t and company_id = @c and start_date <= @d and end_date >= @d and status = 'OPEN' and not is_history"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("d", req.CutoverDate);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct)) { year = r.GetInt32(0); yearEnd = r.GetFieldValue<DateOnly>(1); }
            }
            if (year is null) return Reject(Error("MIGRATION.CUTOVER_YEAR_NOT_OPEN", "P2", "cutover_date", ("cutover_date", req.CutoverDate)));
            await using (var cmd = tx.Command("select 1 from core.fiscal_period where tenant_id = @t and company_id = @c and fiscal_year = @y and period_type = 'REGULAR' and start_date = @d"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("y", year.Value); cmd.Parameters.AddWithValue("d", req.CutoverDate);
                if (await cmd.ExecuteScalarAsync(ct) is null) return Reject(Error("MIGRATION.CUTOVER_NOT_PERIOD_START", "P2", "cutover_date", ("cutover_date", req.CutoverDate)));
            }
            foreach (string role in new[] { "MIGRATION_CLEARING", "HISTORY_RESULT" })
                if (await AccountRoleResolver.ResolveAsync(tx, tenantId, req.CompanyId, role, null, null, null, ct) is null)
                    return Reject(Error("CONFIG.ROLE_NOT_ASSIGNED", "P2", "company_id", ("role_code", role)));
            await using (var cmd = tx.Command("select 1 from core.migration_batch where tenant_id = @t and company_id = @c and status not in ('ABORTED', 'ROLLED_BACK')"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId);
                if (await cmd.ExecuteScalarAsync(ct) is not null) return Reject(Error("MIGRATION.BATCH_EXISTS", "P2", "company_id"));
            }

            Guid batchId = Guid.NewGuid();
            await using (var cmd = tx.Command(
                "insert into core.migration_batch (tenant_id, batch_id, company_id, cutover_date, source_system, opened_by, correlation_id, idempotency_key) values (@t, @b, @c, @d, @s, @by, @corr, @key)"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("b", batchId); cmd.Parameters.AddWithValue("c", req.CompanyId);
                cmd.Parameters.AddWithValue("d", req.CutoverDate); cmd.Parameters.AddWithValue("s", req.SourceSystem); cmd.Parameters.AddWithValue("by", ctx.Actor);
                cmd.Parameters.AddWithValue("corr", ctx.CorrelationId); cmd.Parameters.AddWithValue("key", ctx.IdempotencyKey);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            var batch = new BatchRow(batchId, req.CompanyId, "OPEN", req.CutoverDate, req.SourceSystem, null);
            Guid logId = await CommandSupport.WriteLogAsync(tx, tenantId, req.CompanyId, ctx, "migration-batches:open", fingerprint, BatchEntity, batchId, ct);
            await CommandSupport.WriteOutboxAsync(tx, tenantId, req.CompanyId, ctx, "migration.batch_status_changed", BatchEntity, $"{req.CompanyId:D}/{batchId:D}", "CREATED",
                DateTime.UtcNow, isAudit: true, null, JsonSerializer.Serialize(new Dictionary<string, object?> { ["batch_id"] = batchId, ["status"] = "OPEN" }), ct);
            MigrationBatchResult result = await SummaryAsync(tx, tenantId, batch.BatchId, ct: ct);
            await tx.CommitAsync(ct);
            return CommandResult<MigrationBatchResult>.Ok(result, logId);
        });
    }

    // ---------------------------------------------------------------------------------------------------------
    // migration-batches:declare-totals
    // ---------------------------------------------------------------------------------------------------------
    public async Task<CommandResult<MigrationBatchResult>> DeclareTotalsAsync(Guid tenantId, CommandContext ctx, DeclareTotalsRequest req, CancellationToken ct = default)
    {
        var (denied, shape) = Gate(ctx, ManagePermission);
        if (denied is not null) return denied;
        foreach (ControlTotalInput t in req.Totals ?? [])
            if (!ControlKinds.Built.Contains(t.ControlKind))
                shape.Add(Error("MIGRATION.CONTROL_KIND_INVALID", "P1", "totals", ("control_kind", t.ControlKind), ("built", ControlKinds.Built.ToArray())));
            else if (string.IsNullOrWhiteSpace(t.Key1) || t.AmountLc is null || t.AmountGc is null)
                shape.Add(Error("SCHEMA.INVALID_VALUE", "P1", "totals", ("reason", "key_1, amount_lc and amount_gc are required"), ("control_kind", t.ControlKind)));
        if (req.Totals is null or { Count: 0 }) shape.Add(Error("SCHEMA.REQUIRED_FIELD_MISSING", "P1", "totals"));
        if (shape.Count > 0) return CommandResult<MigrationBatchResult>.Rejected(shape);
        byte[] fingerprint = MigrationRecords.Fingerprint("migration-batches:declare-totals", req);
        return await RunAsync(async () =>
        {
            await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Migration, ct: ct);
            if (await ReplayAsync(tx, tenantId, ctx, fingerprint, ct) is { } replay) return replay;
            BatchRow? batch = await ReadBatchAsync(tx, tenantId, req.BatchId, true, ct);
            if (batch is null) return Reject(Error("MIGRATION.BATCH_NOT_FOUND", "P2", "batch_id"));
            if (batch.Status is not ("OPEN" or "VALIDATED" or "RECONCILED")) return StatusInvalid(batch, "OPEN", "VALIDATED", "RECONCILED");
            long next = await CountAsync(tx, "select coalesce(max(control_no), 0) from core.migration_control_total where tenant_id = @t and batch_id = @b", tenantId, batch.BatchId, ct);
            foreach (ControlTotalInput t in req.Totals!)
            {
                await using var cmd = tx.Command(
                    "insert into core.migration_control_total (tenant_id, batch_id, control_no, control_kind, key_1, key_2, amount_lc, amount_gc, amount_rc, source_report_ref, declared_by) " +
                    "values (@t, @b, @n, @k, @k1, @k2, @lc, @gc, @rc, @ref, @by)");
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("b", batch.BatchId); cmd.Parameters.AddWithValue("n", (int)++next); cmd.Parameters.AddWithValue("k", t.ControlKind);
                cmd.Parameters.Add(new NpgsqlParameter("k1", NpgsqlDbType.Text) { Value = (object?)t.Key1 ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("k2", NpgsqlDbType.Text) { Value = (object?)t.Key2 ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("lc", NpgsqlDbType.Numeric) { Value = (object?)t.AmountLc ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("gc", NpgsqlDbType.Numeric) { Value = (object?)t.AmountGc ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("rc", NpgsqlDbType.Numeric) { Value = (object?)t.AmountRc ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("ref", NpgsqlDbType.Text) { Value = (object?)t.SourceReportRef ?? DBNull.Value });
                cmd.Parameters.AddWithValue("by", ctx.Actor);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            // a changed total needs a new reconciliation
            if (batch.Status is "VALIDATED" or "RECONCILED") await SetStatusAsync(tx, tenantId, ctx, batch, "OPEN", true, false, false, ct);
            Guid logId = await CommandSupport.WriteLogAsync(tx, tenantId, batch.CompanyId, ctx, "migration-batches:declare-totals", fingerprint, BatchEntity, batch.BatchId, ct);
            MigrationBatchResult result = await SummaryAsync(tx, tenantId, batch.BatchId, ct: ct);
            await tx.CommitAsync(ct);
            return CommandResult<MigrationBatchResult>.Ok(result, logId);
        });
    }

    // ---------------------------------------------------------------------------------------------------------
    // migration-records:stage
    // ---------------------------------------------------------------------------------------------------------
    private sealed record AccountInfo(Guid AccountId, string AccountType, string OpenItemMode, bool Active);

    private static async Task<Dictionary<string, AccountInfo>> ReadAccountsAsync(TenantTransaction tx, Guid tenantId, Guid companyId, CancellationToken ct)
    {
        var result = new Dictionary<string, AccountInfo>(StringComparer.Ordinal);
        await using var cmd = tx.Command("select account_no, account_id, account_type, open_item_mode, active from core.gl_account where tenant_id = @t and company_id = @c");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) result[r.GetString(0)] = new AccountInfo(r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetBoolean(4));
        return result;
    }

    /// <summary>The checks of a record against the data of the company (13, 5.1): the account exists, is active and is the right kind, the partner exists, the currency is enabled.</summary>
    private static List<CommandError> CheckRecord(
        string type, object payload, Dictionary<string, AccountInfo> accounts, HashSet<string> currencies, Dictionary<string, Guid> partners)
    {
        var errors = new List<CommandError>();
        void Bad(string path, string code, string reason) => errors.Add(Error(code, "P2", path, ("reason", reason)));
        string? accountNo = payload is OpeningBalancePayload ob ? ob.AccountNo : ((OpenItemPayload)payload).AccountNo;
        if (!accounts.TryGetValue(accountNo!, out AccountInfo? account)) { Bad("/account_no", "ACCOUNT.NOT_FOUND", "no account with this number"); return errors; }
        if (!account.Active) Bad("/account_no", "ACCOUNT.INACTIVE", "the account is inactive");
        if (type == RecordTypes.OpeningBalance)
        {
            if (account.AccountType is not ("ASSET" or "LIABILITY" or "EQUITY")) Bad("/account_no", "SCHEMA.INVALID_VALUE", "an opening balance is a balance sheet account");
            if (account.OpenItemMode != "NONE") Bad("/account_no", "SCHEMA.INVALID_VALUE", "the balance of a subledger account comes from its open items");
            return errors;
        }
        var p = (OpenItemPayload)payload;
        if (type == RecordTypes.OpenItem && account.OpenItemMode != "CONTROL") Bad("/account_no", "SCHEMA.INVALID_VALUE", "an OPEN_ITEM is on a CONTROL account");
        if (type == RecordTypes.GlOpenItem && account.OpenItemMode != "GL_OPEN_ITEM") Bad("/account_no", "SCHEMA.INVALID_VALUE", "a GLOPEN_ITEM is on a GL_OPEN_ITEM account");
        if (!currencies.Contains(p.Currency!.ToUpperInvariant())) Bad("/currency", "CONFIG.CURRENCY_NOT_ENABLED", "the currency is not enabled for the tenant");
        if (type == RecordTypes.OpenItem && !partners.ContainsKey(p.PartnerCode!)) Bad("/partner_code", "MASTER.PARTNER_NOT_FOUND", "no partner with this code");
        if (type == RecordTypes.OpenItem && p.DueDate is null) Bad("/due_date", "ACCOUNT_RULE.DUE_DATE_REQUIRED", "an item on a control account needs a due date");
        return errors;
    }

    public async Task<CommandResult<MigrationBatchResult>> StageAsync(Guid tenantId, CommandContext ctx, StageRecordsRequest req, CancellationToken ct = default)
    {
        var (denied, shape) = Gate(ctx, StagePermission);
        if (denied is not null) return denied;
        if (req.Records is null or { Count: 0 }) shape.Add(Error("SCHEMA.REQUIRED_FIELD_MISSING", "P1", "records"));
        else if (req.Records.Count > MaxStageRecords) shape.Add(Error("MIGRATION.STAGE_BATCH_TOO_LARGE", "P1", "records", ("max", MaxStageRecords)));
        else
            foreach (StageRecordInput r in req.Records)
            {
                if (!RecordTypes.Built.Contains(r.RecordType ?? ""))
                    shape.Add(Error("MIGRATION.RECORD_TYPE_INVALID", "P1", "records", ("record_type", r.RecordType), ("built", RecordTypes.Built.ToArray()), ("known", RecordTypes.Known.Contains(r.RecordType ?? ""))));
                if (string.IsNullOrWhiteSpace(r.LegacyRef) || r.LegacyRef.Length > 100) shape.Add(Error("SCHEMA.INVALID_VALUE", "P1", "records", ("reason", "legacy_ref is required, at most 100 characters")));
            }
        if (shape.Count > 0) return CommandResult<MigrationBatchResult>.Rejected(shape);
        byte[] fingerprint = MigrationRecords.Fingerprint("migration-records:stage", req);
        return await RunAsync(async () =>
        {
            await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Migration, ct: ct);
            if (await ReplayAsync(tx, tenantId, ctx, fingerprint, ct) is { } replay) return replay;
            BatchRow? batch = await ReadBatchAsync(tx, tenantId, req.BatchId, true, ct);
            if (batch is null) return Reject(Error("MIGRATION.BATCH_NOT_FOUND", "P2", "batch_id"));
            if (batch.Status is not ("OPEN" or "VALIDATED" or "RECONCILED")) return StatusInvalid(batch, "OPEN", "VALIDATED", "RECONCILED");

            var accounts = await ReadAccountsAsync(tx, tenantId, batch.CompanyId, ct);
            var currencies = new HashSet<string>();
            await using (var cmd = tx.Command("select currency_code from core.tenant_currency where tenant_id = @t and active"))
            {
                cmd.Parameters.AddWithValue("t", tenantId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) currencies.Add(r.GetString(0).Trim());
            }
            var partners = new Dictionary<string, Guid>(StringComparer.Ordinal);
            await using (var cmd = tx.Command("select partner_code, partner_id from core.business_partner where tenant_id = @t"))
            {
                cmd.Parameters.AddWithValue("t", tenantId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) partners[r.GetString(0)] = r.GetGuid(1);
            }
            long recordNo = await CountAsync(tx, "select coalesce(max(record_no), 0) from core.migration_record where tenant_id = @t and batch_id = @b", tenantId, batch.BatchId, ct);
            var results = new List<StagedRecordResult>();
            foreach (StageRecordInput rec in req.Records!)
            {
                var (payload, errors) = MigrationRecords.Parse(rec.RecordType, rec.Payload);
                if (payload is not null) errors.AddRange(CheckRecord(rec.RecordType, payload, accounts, currencies, partners));
                string status = errors.Count == 0 ? "VALID" : "INVALID";
                recordNo++;
                await using (var cmd = tx.Command(
                    "insert into core.migration_record (tenant_id, batch_id, record_no, record_type, legacy_ref, payload, payload_digest, status, errors, received_by) " +
                    "values (@t, @b, @n, @type, @ref, @p, @dig, @st, @err, @by)"))
                {
                    cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("b", batch.BatchId); cmd.Parameters.AddWithValue("n", recordNo);
                    cmd.Parameters.AddWithValue("type", rec.RecordType); cmd.Parameters.AddWithValue("ref", rec.LegacyRef);
                    cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Jsonb) { Value = rec.Payload.GetRawText() });
                    cmd.Parameters.AddWithValue("dig", Convert.FromHexString(MigrationRecords.Digest(rec.Payload)));
                    cmd.Parameters.AddWithValue("st", status);
                    cmd.Parameters.Add(new NpgsqlParameter("err", NpgsqlDbType.Jsonb) { Value = errors.Count == 0 ? DBNull.Value : JsonSerializer.Serialize(errors.Select(e => new { e.Code, e.Path, e.Params })) });
                    cmd.Parameters.AddWithValue("by", ctx.Actor);
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                // a record with the same type and legacy_ref supersedes the earlier ones until the commit (13, 3.3)
                await using (var cmd = tx.Command("update core.migration_record set superseded = true where tenant_id = @t and batch_id = @b and record_type = @type and legacy_ref = @ref and record_no < @n and not superseded"))
                {
                    cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("b", batch.BatchId); cmd.Parameters.AddWithValue("type", rec.RecordType);
                    cmd.Parameters.AddWithValue("ref", rec.LegacyRef); cmd.Parameters.AddWithValue("n", recordNo);
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                results.Add(new StagedRecordResult(recordNo, rec.RecordType, rec.LegacyRef, status, errors));
            }
            if (batch.Status is "VALIDATED" or "RECONCILED") await SetStatusAsync(tx, tenantId, ctx, batch, "OPEN", true, false, false, ct);   // a changed record needs a new reconciliation
            Guid logId = await CommandSupport.WriteLogAsync(tx, tenantId, batch.CompanyId, ctx, "migration-records:stage", fingerprint, BatchEntity, batch.BatchId, ct);
            MigrationBatchResult result = await SummaryAsync(tx, tenantId, batch.BatchId, results, ct: ct);
            await tx.CommitAsync(ct);
            return CommandResult<MigrationBatchResult>.Ok(result, logId);
        });
    }

    // ---------------------------------------------------------------------------------------------------------
    // migration-batches:validate, :reconcile and :abort
    // ---------------------------------------------------------------------------------------------------------
    public async Task<CommandResult<MigrationBatchResult>> ValidateAsync(Guid tenantId, CommandContext ctx, BatchRef req, CancellationToken ct = default)
    {
        var (denied, shape) = Gate(ctx, ManagePermission);
        if (denied is not null) return denied;
        if (shape.Count > 0) return CommandResult<MigrationBatchResult>.Rejected(shape);
        byte[] fingerprint = MigrationRecords.Fingerprint("migration-batches:validate", req);
        return await RunAsync(async () =>
        {
            await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Migration, ct: ct);
            if (await ReplayAsync(tx, tenantId, ctx, fingerprint, ct) is { } replay) return replay;
            BatchRow? batch = await ReadBatchAsync(tx, tenantId, req.BatchId, true, ct);
            if (batch is null) return Reject(Error("MIGRATION.BATCH_NOT_FOUND", "P2", "batch_id"));
            if (batch.Status != "OPEN") return StatusInvalid(batch, "OPEN");
            long invalid = await CountAsync(tx, "select count(*) from core.migration_record where tenant_id = @t and batch_id = @b and not superseded and status = 'INVALID'", tenantId, batch.BatchId, ct);
            if (invalid > 0) return Reject(Error("MIGRATION.RECORDS_INVALID", "P3", "batch_id", ("count", invalid)));
            await SetStatusAsync(tx, tenantId, ctx, batch, "VALIDATED", false, false, false, ct);
            Guid logId = await CommandSupport.WriteLogAsync(tx, tenantId, batch.CompanyId, ctx, "migration-batches:validate", fingerprint, BatchEntity, batch.BatchId, ct);
            MigrationBatchResult result = await SummaryAsync(tx, tenantId, batch.BatchId, ct: ct);
            await tx.CommitAsync(ct);
            return CommandResult<MigrationBatchResult>.Ok(result, logId);
        });
    }

    private sealed record CurrentRecord(string Type, object Payload);

    private static async Task<List<CurrentRecord>> ReadCurrentRecordsAsync(TenantTransaction tx, Guid tenantId, Guid batchId, CancellationToken ct)
    {
        var list = new List<CurrentRecord>();
        await using var cmd = tx.Command("select record_type, payload::text from core.migration_record where tenant_id = @t and batch_id = @b and not superseded and status = 'VALID' order by record_no");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("b", batchId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            using JsonDocument doc = JsonDocument.Parse(r.GetString(1));
            var (payload, _) = MigrationRecords.Parse(r.GetString(0), doc.RootElement.Clone());
            if (payload is not null) list.Add(new CurrentRecord(r.GetString(0), payload));
        }
        return list;
    }

    public async Task<CommandResult<MigrationBatchResult>> ReconcileAsync(Guid tenantId, CommandContext ctx, BatchRef req, CancellationToken ct = default)
    {
        var (denied, shape) = Gate(ctx, ManagePermission);
        if (denied is not null) return denied;
        if (shape.Count > 0) return CommandResult<MigrationBatchResult>.Rejected(shape);
        byte[] fingerprint = MigrationRecords.Fingerprint("migration-batches:reconcile", req);
        return await RunAsync(async () =>
        {
            await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Migration, ct: ct);
            if (await ReplayAsync(tx, tenantId, ctx, fingerprint, ct) is { } replay) return replay;
            BatchRow? batch = await ReadBatchAsync(tx, tenantId, req.BatchId, true, ct);
            if (batch is null) return Reject(Error("MIGRATION.BATCH_NOT_FOUND", "P2", "batch_id"));
            if (batch.Status is not ("OPEN" or "VALIDATED" or "RECONCILED")) return StatusInvalid(batch, "OPEN", "VALIDATED", "RECONCILED");
            var company = (await JournalDocumentPoster.ReadCompanyAsync(tx, tenantId, batch.CompanyId, ct))!;
            bool hasRc = company.ReportingCurrency is not null;

            var records = await ReadCurrentRecordsAsync(tx, tenantId, batch.BatchId, ct);
            long invalid = await CountAsync(tx, "select count(*) from core.migration_record where tenant_id = @t and batch_id = @b and not superseded and status = 'INVALID'", tenantId, batch.BatchId, ct);
            var accounts = await ReadAccountsAsync(tx, tenantId, batch.CompanyId, ct);
            var subledger = accounts.Where(a => a.Value.OpenItemMode != "NONE").Select(a => a.Key).ToHashSet(StringComparer.Ordinal);
            // the latest declared total per kind and key counts
            var declared = new Dictionary<(string, string), AccountAmounts>();
            await using (var cmd = tx.Command("select control_kind, key_1, amount_lc, amount_gc, amount_rc from core.migration_control_total where tenant_id = @t and batch_id = @b order by control_no"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("b", batch.BatchId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) declared[(r.GetString(0), r.GetString(1))] = new AccountAmounts(r.GetString(1), r.IsDBNull(2) ? 0m : r.GetDecimal(2), r.IsDBNull(3) ? 0m : r.GetDecimal(3), r.IsDBNull(4) ? null : r.GetDecimal(4));
            }
            var balances = records.Where(r => r.Type == RecordTypes.OpeningBalance).Select(r => (OpeningBalancePayload)r.Payload)
                .GroupBy(p => p.AccountNo!).Select(g => new AccountAmounts(g.Key, g.Sum(p => p.BalanceLc!.Value), g.Sum(p => p.BalanceGc!.Value), g.Sum(p => p.BalanceRc ?? 0m))).ToList();
            var items = records.Where(r => r.Type is RecordTypes.OpenItem or RecordTypes.GlOpenItem).Select(r => (OpenItemPayload)r.Payload)
                .Select(p => new AccountAmounts(p.AccountNo!, p.AmountLc!.Value, p.AmountGc!.Value, p.AmountRc)).ToList();
            var rows = MigrationReconciliation.Run(
                (int)invalid, declared.Where(d => d.Key.Item1 == ControlKinds.TbByAccount).Select(d => d.Value).ToList(),
                declared.Where(d => d.Key.Item1 == ControlKinds.OpenItemsByAccount).Select(d => d.Value).ToList(), balances, items, subledger, hasRc);

            Guid runId = Guid.NewGuid();
            foreach (ReconciliationRow row in rows)
            {
                await using var cmd = tx.Command(
                    "insert into core.migration_reconciliation (tenant_id, batch_id, reconciliation_id, check_no, check_code, key, declared_lc, declared_gc, declared_rc, computed_lc, computed_gc, computed_rc, " +
                    "difference_lc, difference_gc, difference_rc, result, detail, run_by) values (@t, @b, @r, @n, @code, @key, @dl, @dg, @dr, @cl, @cg, @cr, @fl, @fg, @fr, @res, @det, @by)");
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("b", batch.BatchId); cmd.Parameters.AddWithValue("r", runId); cmd.Parameters.AddWithValue("n", row.CheckNo);
                cmd.Parameters.AddWithValue("code", row.CheckCode);
                cmd.Parameters.Add(new NpgsqlParameter("key", NpgsqlDbType.Text) { Value = (object?)row.Key ?? DBNull.Value });
                void Amt(string name, decimal? v) => cmd.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Numeric) { Value = (object?)v ?? DBNull.Value });
                Amt("dl", row.DeclaredLc); Amt("dg", row.DeclaredGc); Amt("dr", row.DeclaredRc); Amt("cl", row.ComputedLc); Amt("cg", row.ComputedGc); Amt("cr", row.ComputedRc);
                Amt("fl", row.ComputedLc is null || row.DeclaredLc is null ? null : row.ComputedLc - row.DeclaredLc);
                Amt("fg", row.ComputedGc is null || row.DeclaredGc is null ? null : row.ComputedGc - row.DeclaredGc);
                Amt("fr", row.ComputedRc is null || row.DeclaredRc is null ? null : row.ComputedRc - row.DeclaredRc);
                cmd.Parameters.AddWithValue("res", row.Result);
                cmd.Parameters.Add(new NpgsqlParameter("det", NpgsqlDbType.Text) { Value = (object?)row.Detail ?? DBNull.Value });
                cmd.Parameters.AddWithValue("by", ctx.Actor);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            if (MigrationReconciliation.Passed(rows))
            {
                await using (var cmd = tx.Command("update core.migration_batch set accepted_reconciliation_id = @r where tenant_id = @t and batch_id = @b"))
                {
                    cmd.Parameters.AddWithValue("r", runId); cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("b", batch.BatchId);
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                if (batch.Status != "RECONCILED") await SetStatusAsync(tx, tenantId, ctx, batch, "RECONCILED", false, false, false, ct);
            }
            else if (batch.Status == "RECONCILED") await SetStatusAsync(tx, tenantId, ctx, batch, "VALIDATED", true, false, false, ct);
            Guid logId = await CommandSupport.WriteLogAsync(tx, tenantId, batch.CompanyId, ctx, "migration-batches:reconcile", fingerprint, BatchEntity, batch.BatchId, ct);
            MigrationBatchResult result = await SummaryAsync(tx, tenantId, batch.BatchId, null, rows, ct: ct);
            await tx.CommitAsync(ct);
            return CommandResult<MigrationBatchResult>.Ok(result, logId);
        });
    }

    public async Task<CommandResult<MigrationBatchResult>> AbortAsync(Guid tenantId, CommandContext ctx, BatchRef req, CancellationToken ct = default)
    {
        var (denied, shape) = Gate(ctx, ManagePermission);
        if (denied is not null) return denied;
        if (shape.Count > 0) return CommandResult<MigrationBatchResult>.Rejected(shape);
        byte[] fingerprint = MigrationRecords.Fingerprint("migration-batches:abort", req);
        return await RunAsync(async () =>
        {
            await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Migration, ct: ct);
            if (await ReplayAsync(tx, tenantId, ctx, fingerprint, ct) is { } replay) return replay;
            BatchRow? batch = await ReadBatchAsync(tx, tenantId, req.BatchId, true, ct);
            if (batch is null) return Reject(Error("MIGRATION.BATCH_NOT_FOUND", "P2", "batch_id"));
            long done = await CountAsync(tx, "select count(*) from core.migration_commit_chunk where tenant_id = @t and batch_id = @b and status = 'DONE'", tenantId, batch.BatchId, ct);
            bool allowed = batch.Status is "OPEN" or "VALIDATED" or "RECONCILED" || (batch.Status == "COMMITTING" && done == 0);
            if (!allowed) return Reject(Error("MIGRATION.ABORT_NOT_ALLOWED", "P3", "batch_id", ("status", batch.Status), ("rollback_needed", done > 0)));
            await SetStatusAsync(tx, tenantId, ctx, batch, "ABORTED", false, false, false, ct);
            Guid logId = await CommandSupport.WriteLogAsync(tx, tenantId, batch.CompanyId, ctx, "migration-batches:abort", fingerprint, BatchEntity, batch.BatchId, ct);
            MigrationBatchResult result = await SummaryAsync(tx, tenantId, batch.BatchId, ct: ct);
            await tx.CommitAsync(ct);
            return CommandResult<MigrationBatchResult>.Ok(result, logId);
        });
    }

    // ---------------------------------------------------------------------------------------------------------
    // migration-batches:commit (13, 7): chunks, each one atomic transaction
    // ---------------------------------------------------------------------------------------------------------
    private static readonly string[] Areas = ["OPENING_GL", "OPEN_ITEMS"];

    public async Task<CommandResult<MigrationBatchResult>> CommitAsync(Guid tenantId, CommandContext ctx, BatchRef req, CancellationToken ct = default)
    {
        var (denied, shape) = Gate(ctx, CommitPermission);
        if (denied is not null) return denied;
        if (shape.Count > 0) return CommandResult<MigrationBatchResult>.Rejected(shape);
        byte[] fingerprint = MigrationRecords.Fingerprint("migration-batches:commit", req);

        // the plan: the status and the chunks, in one transaction
        Guid companyId;
        var pending = new List<(int No, string Area)>();
        var planned = await RunAsync(async () =>
        {
            await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Migration, ct: ct);
            if (await ReplayAsync(tx, tenantId, ctx, fingerprint, ct) is { } replay) return replay;
            BatchRow? batch = await ReadBatchAsync(tx, tenantId, req.BatchId, true, ct);
            if (batch is null) return Reject(Error("MIGRATION.BATCH_NOT_FOUND", "P2", "batch_id"));
            if (batch.Status is not ("RECONCILED" or "COMMITTING"))
                return batch.Status is "OPEN" or "VALIDATED" ? Reject(Error("MIGRATION.RECONCILIATION_MISSING", "P3", "batch_id", ("status", batch.Status))) : StatusInvalid(batch, "RECONCILED", "COMMITTING");
            if (await IsProductionAsync(tx, tenantId, ct)) return Reject(Error("LIMIT.MIGRATION_FOUR_EYES_NOT_BUILT", "P3", "batch_id"));
            var company = await JournalDocumentPoster.ReadCompanyAsync(tx, tenantId, batch.CompanyId, ct);
            if (company is null || company.Status != "DRAFT") return Reject(Error("MIGRATION.COMPANY_NOT_DRAFT", "P2", "batch_id", ("status", company?.Status)));
            if (batch.Status == "RECONCILED")
            {
                int n = 0;
                foreach (string area in Areas)
                {
                    await using var cmd = tx.Command("insert into core.migration_commit_chunk (tenant_id, batch_id, chunk_no, area) values (@t, @b, @n, @a)");
                    cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("b", batch.BatchId); cmd.Parameters.AddWithValue("n", ++n); cmd.Parameters.AddWithValue("a", area);
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                await SetStatusAsync(tx, tenantId, ctx, batch, "COMMITTING", false, false, false, ct);
            }
            await using (var cmd = tx.Command("select chunk_no, area from core.migration_commit_chunk where tenant_id = @t and batch_id = @b and status <> 'DONE' order by chunk_no"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("b", batch.BatchId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) pending.Add((r.GetInt32(0), r.GetString(1)));
            }
            MigrationBatchResult summary = await SummaryAsync(tx, tenantId, batch.BatchId, ct: ct);
            await tx.CommitAsync(ct);
            return CommandResult<MigrationBatchResult>.Ok(summary, Guid.Empty);
        });
        if (planned.Status != CommandStatus.Ok || planned.IdempotentReplay) return planned;
        companyId = planned.Value!.CompanyId;

        foreach (var (no, area) in pending)
        {
            var failure = await RunChunkAsync(tenantId, ctx, req.BatchId, no, area, ct);
            if (failure is not null)
            {
                await MarkChunkFailedAsync(tenantId, req.BatchId, no, failure.Code, ct);
                return Reject(Error("MIGRATION.CHUNK_FAILED", "P7", "batch_id", ("chunk_no", no), ("area", area), ("failure_codes", failure.Code)));
            }
        }

        return await RunAsync(async () =>
        {
            await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Migration, ct: ct);
            BatchRow batch = (await ReadBatchAsync(tx, tenantId, req.BatchId, true, ct))!;
            await SetStatusAsync(tx, tenantId, ctx, batch, "COMMITTED", false, true, false, ct);
            Guid logId = await CommandSupport.WriteLogAsync(tx, tenantId, companyId, ctx, "migration-batches:commit", fingerprint, BatchEntity, batch.BatchId, ct);
            long documents = await CountAsync(tx, "select count(*) from core.journal_document where tenant_id = @t and migration_batch_id = @b", tenantId, batch.BatchId, ct);
            MigrationBatchResult result = await SummaryAsync(tx, tenantId, batch.BatchId, null, null, (int)documents, ct);
            await tx.CommitAsync(ct);
            return CommandResult<MigrationBatchResult>.Ok(result, logId);
        });
    }

    private async Task MarkChunkFailedAsync(Guid tenantId, Guid batchId, int chunkNo, string code, CancellationToken ct)
    {
        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Migration, ct: ct);
        await using (var cmd = tx.Command("update core.migration_commit_chunk set status = 'FAILED', failure_codes = @c where tenant_id = @t and batch_id = @b and chunk_no = @n"))
        {
            cmd.Parameters.AddWithValue("c", code.Length > 500 ? code[..500] : code); cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("b", batchId); cmd.Parameters.AddWithValue("n", chunkNo);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    private sealed record ChunkFailure(string Code);

    /// <summary>One chunk, one transaction. Returns the failure, or null when the chunk is DONE and committed.</summary>
    private async Task<ChunkFailure?> RunChunkAsync(Guid tenantId, CommandContext ctx, Guid batchId, int chunkNo, string area, CancellationToken ct)
    {
        try
        {
            await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Migration, ct: ct);
            BatchRow batch = (await ReadBatchAsync(tx, tenantId, batchId, true, ct))!;
            Guid companyId = batch.CompanyId;
            var company = (await JournalDocumentPoster.ReadCompanyAsync(tx, tenantId, companyId, ct))!;
            if (company.Status != "DRAFT") return new ChunkFailure("MIGRATION.COMPANY_NOT_DRAFT");
            Guid? ledgerId = await ClearingPoster.ReadDefaultLedgerAsync(tx, tenantId, companyId, ct);
            if (ledgerId is null) return new ChunkFailure("LEDGER.NOT_FOUND");
            string? groupCurrency = await ClearingPoster.ReadGroupCurrencyAsync(tx, tenantId, companyId, batch.CutoverDate, ct);
            if (groupCurrency is null) return new ChunkFailure("CONFIG.GROUP_MEMBERSHIP_MISSING");
            Guid? clearing = await AccountRoleResolver.ResolveAsync(tx, tenantId, companyId, "MIGRATION_CLEARING", null, null, null, ct);
            if (clearing is null) return new ChunkFailure("CONFIG.ROLE_NOT_ASSIGNED");
            bool hasRc = company.ReportingCurrency is not null;
            int maxLines;
            await using (var cmd = tx.Command("select migration_document_max_lines from core.tenant_setting where tenant_id = @t"))
            {
                cmd.Parameters.AddWithValue("t", tenantId);
                maxLines = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct) ?? 5000, CultureInfo.InvariantCulture);
            }

            var records = await ReadCurrentRecordsAsync(tx, tenantId, batchId, ct);
            var accounts = await ReadAccountsAsync(tx, tenantId, companyId, ct);
            IReadOnlyList<PlannedDocument> docs;
            if (area == "OPENING_GL")
            {
                var targets = records.Where(r => r.Type == RecordTypes.OpeningBalance).Select(r => (OpeningBalancePayload)r.Payload).GroupBy(p => p.AccountNo!).ToList();
                var lines = new List<(Guid, decimal, decimal, decimal?)>();
                foreach (var g in targets)
                {
                    Guid account = accounts[g.Key].AccountId;
                    // the line is the target less what the ledger already holds on the account (13, 6.2)
                    await using var cmd = tx.Command("select coalesce(sum(amount_lc), 0), coalesce(sum(amount_gc), 0), coalesce(sum(amount_rc), 0) from core.journal_line where tenant_id = @t and company_id = @c and account_id = @a");
                    cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("a", account);
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    await r.ReadAsync(ct);
                    lines.Add((account, g.Sum(p => p.BalanceLc!.Value) - r.GetDecimal(0), g.Sum(p => p.BalanceGc!.Value) - r.GetDecimal(1), g.Sum(p => p.BalanceRc ?? 0m) - r.GetDecimal(2)));
                }
                docs = MigrationPlan.OpeningDocuments(lines, clearing.Value, company.LocalCurrency, hasRc, maxLines);
            }
            else
            {
                var partners = new Dictionary<string, Guid>(StringComparer.Ordinal);
                await using (var cmd = tx.Command("select partner_code, partner_id from core.business_partner where tenant_id = @t"))
                {
                    cmd.Parameters.AddWithValue("t", tenantId);
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct)) partners[r.GetString(0)] = r.GetGuid(1);
                }
                var items = records.Where(r => r.Type is RecordTypes.OpenItem or RecordTypes.GlOpenItem).Select(r => (OpenItemPayload)r.Payload)
                    .Select(p => new PlannedItem(accounts[p.AccountNo!].AccountId, p.PartnerCode is null ? null : partners[p.PartnerCode], p.Currency!.ToUpperInvariant(), p.AmountTc!.Value, p.AmountLc!.Value,
                        p.AmountGc!.Value, p.AmountRc, p.DocumentDate!.Value, p.PostingDate!.Value, p.DueDate, p.BaselineDate, p.LegacyDocumentRef!)).ToList();
                docs = MigrationPlan.ItemDocuments(items, clearing.Value, hasRc, maxLines);
            }

            // the documents are posted at the cutover date, in its period (the window and the period status still apply to the live periods)
            var period = await JournalDocumentPoster.CheckPeriodAsync(tx, tenantId, companyId, batch.CutoverDate, ctx, false, ct);
            if (period.Errors.Count > 0) return new ChunkFailure(period.Errors[0].Code);
            var series = new Dictionary<string, JournalDocumentPoster.SeriesRow>();
            foreach (string type in docs.Select(d => d.DocumentType).Distinct())
            {
                var s = await JournalDocumentPoster.ReadSeriesAsync(tx, tenantId, companyId, type, batch.CutoverDate, ledgerId.Value, ct);
                if (s is null) return new ChunkFailure("SERIES.NO_ASSIGNMENT");
                series[type] = s;
            }
            if (docs.Count > 0 && await ClosingDocuments.LockCountersAsync(tx, tenantId, companyId, series.Values.Select(s => (s, period.FiscalYear!.Value)), ct) is { } missing) return new ChunkFailure(missing.Code);
            // the company is DRAFT and has no LEDGER head until it is activated: the first migration chunk creates it (F-IMPL-10)
            await CommandSupport.LockOrCreateHeadAsync(tx, tenantId, companyId, "LEDGER", ledgerId.Value.ToString("D"), LedgerHash2.Genesis(tenantId, companyId, "LEDGER", ledgerId.Value.ToString("D")), ct);
            ClosingLedger ledger = (await ClosingDocuments.OpenLedgerAsync(tx, tenantId, companyId, ledgerId.Value, company, groupCurrency, ct))!;
            var recheck = await JournalDocumentPoster.CheckPeriodAsync(tx, tenantId, companyId, batch.CutoverDate, ctx, true, ct);
            if (recheck.Errors.Count > 0) return new ChunkFailure(recheck.Errors[0].Code);

            int posted = 0;
            for (int i = 0; i < docs.Count; i++)
            {
                PlannedDocument d = docs[i];
                var lines = d.Lines.Select(l => new NewLine(l.AccountId, l.Item is null && d.DocumentType == "OPENING" || l.AccountId == clearing ? "OPENING" : "BASE", l.Tc, l.Lc, l.Gc, l.Rc,
                    l.RateLc, l.RateGc, l.RateRc, "DERIVED", batch.CutoverDate,
                    OpenItem: l.Item is { } it ? new OpenItemSpec(it.PartnerId, it.DueDate, it.BaselineDate, it.DocumentDate, it.PostingDate, it.LegacyDocumentRef) : null)).ToList();
                var result = await ClosingDocuments.PostAsync(ledger, ctx,
                    new DocumentSpec(d.DocumentType, "migration-batches:commit", series[d.DocumentType], period.FiscalYear!.Value, period.PeriodNo!.Value, recheck.SoftCloseOverride, batch.CutoverDate,
                        $"{ctx.IdempotencyKey}#{chunkNo}.{i + 1}", Origin: "MIGRATION", MigrationBatchId: batchId, TransactionCurrency: d.Currency), lines, null, ct);
                if (result.Error is not null) return new ChunkFailure(result.Error.Code);
                posted++;
            }
            await using (var cmd = tx.Command("update core.migration_commit_chunk set status = 'DONE', document_count = @n, started_at = coalesce(started_at, now()), completed_at = now(), failure_codes = null where tenant_id = @t and batch_id = @b and chunk_no = @c"))
            {
                cmd.Parameters.AddWithValue("n", posted); cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("b", batchId); cmd.Parameters.AddWithValue("c", chunkNo);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);   // the deferred checks: balance per document, lines, open item rows, snapshot, chain, outbox position
            return null;
        }
        catch (PostgresException ex) when (ex.SqlState is not (PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure))
        {
            return new ChunkFailure("DB." + ex.SqlState + ": " + ex.MessageText);
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // migration-batches:seal (13, 9)
    // ---------------------------------------------------------------------------------------------------------
    public async Task<CommandResult<MigrationBatchResult>> SealAsync(Guid tenantId, CommandContext ctx, BatchRef req, CancellationToken ct = default)
    {
        var (denied, shape) = Gate(ctx, CommitPermission);
        if (denied is not null) return denied;
        if (shape.Count > 0) return CommandResult<MigrationBatchResult>.Rejected(shape);
        byte[] fingerprint = MigrationRecords.Fingerprint("migration-batches:seal", req);
        return await RunAsync(async () =>
        {
            await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Migration, ct: ct);
            if (await ReplayAsync(tx, tenantId, ctx, fingerprint, ct) is { } replay) return replay;
            BatchRow? batch = await ReadBatchAsync(tx, tenantId, req.BatchId, true, ct);
            if (batch is null) return Reject(Error("MIGRATION.BATCH_NOT_FOUND", "P2", "batch_id"));
            if (batch.Status != "COMMITTED") return StatusInvalid(batch, "COMMITTED");
            if (await IsProductionAsync(tx, tenantId, ct)) return Reject(Error("LIMIT.MIGRATION_FOUR_EYES_NOT_BUILT", "P3", "batch_id"));
            Guid companyId = batch.CompanyId;
            var company = (await JournalDocumentPoster.ReadCompanyAsync(tx, tenantId, companyId, ct))!;
            bool hasRc = company.ReportingCurrency is not null;
            Guid ledgerId = (await ClearingPoster.ReadDefaultLedgerAsync(tx, tenantId, companyId, ct))!.Value;
            await ClearingPoster.LockLedgerHeadAsync(tx, tenantId, companyId, ledgerId, ct);   // nothing posts while the seal reads and writes the anchors

            // 1. the ledger balance per account as of the cutover date equals the declared trial balance, and MIGRATION_CLEARING is zero
            var failures = new List<object>();
            var declared = new Dictionary<string, AccountAmounts>(StringComparer.Ordinal);
            var declaredItems = new Dictionary<string, AccountAmounts>(StringComparer.Ordinal);
            await using (var cmd = tx.Command("select control_kind, key_1, amount_lc, amount_gc, amount_rc from core.migration_control_total where tenant_id = @t and batch_id = @b order by control_no"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("b", batch.BatchId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    var a = new AccountAmounts(r.GetString(1), r.IsDBNull(2) ? 0m : r.GetDecimal(2), r.IsDBNull(3) ? 0m : r.GetDecimal(3), r.IsDBNull(4) ? null : r.GetDecimal(4));
                    if (r.GetString(0) == ControlKinds.TbByAccount) declared[a.AccountNo] = a; else if (r.GetString(0) == ControlKinds.OpenItemsByAccount) declaredItems[a.AccountNo] = a;
                }
            }
            var ledger = new Dictionary<string, AccountAmounts>(StringComparer.Ordinal);
            await using (var cmd = tx.Command(
                "select a.account_no, sum(l.amount_lc), sum(l.amount_gc), sum(l.amount_rc) from core.journal_line l join core.gl_account a on a.tenant_id = l.tenant_id and a.company_id = l.company_id and a.account_id = l.account_id " +
                "where l.tenant_id = @t and l.company_id = @c and l.posting_date <= @d group by a.account_no"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", batch.CutoverDate);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) ledger[r.GetString(0)] = new AccountAmounts(r.GetString(0), r.GetDecimal(1), r.GetDecimal(2), r.IsDBNull(3) ? null : r.GetDecimal(3));
            }
            foreach (string no in ledger.Keys.Concat(declared.Keys).Distinct().OrderBy(n => n, StringComparer.Ordinal))
            {
                AccountAmounts l = ledger.GetValueOrDefault(no) ?? new AccountAmounts(no, 0m, 0m, 0m), d = declared.GetValueOrDefault(no) ?? new AccountAmounts(no, 0m, 0m, 0m);
                if (l.Lc != d.Lc || l.Gc != d.Gc || (hasRc && (l.Rc ?? 0m) != (d.Rc ?? 0m)))
                    failures.Add(new { check = "LEDGER_VS_TB", account_no = no, declared_lc = d.Lc, ledger_lc = l.Lc, declared_gc = d.Gc, ledger_gc = l.Gc });
            }
            // 2. the open items by account equal the declared open items
            await using (var cmd = tx.Command(
                "select a.account_no, sum(l.amount_lc), sum(l.amount_gc), sum(l.amount_rc) from core.journal_line_open_item o join core.journal_line l on l.tenant_id = o.tenant_id and l.company_id = o.company_id and l.document_id = o.document_id and l.line_no = o.line_no " +
                "join core.gl_account a on a.tenant_id = l.tenant_id and a.company_id = l.company_id and a.account_id = l.account_id where o.tenant_id = @t and o.company_id = @c and l.posting_date <= @d group by a.account_no"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", batch.CutoverDate);
                var items = new Dictionary<string, AccountAmounts>(StringComparer.Ordinal);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) items[r.GetString(0)] = new AccountAmounts(r.GetString(0), r.GetDecimal(1), r.GetDecimal(2), r.IsDBNull(3) ? null : r.GetDecimal(3));
                foreach (string no in items.Keys.Concat(declaredItems.Keys).Distinct().OrderBy(n => n, StringComparer.Ordinal))
                {
                    AccountAmounts i = items.GetValueOrDefault(no) ?? new AccountAmounts(no, 0m, 0m, 0m), d = declaredItems.GetValueOrDefault(no) ?? new AccountAmounts(no, 0m, 0m, 0m);
                    if (declaredItems.ContainsKey(no) && (i.Lc != d.Lc || i.Gc != d.Gc || (hasRc && (i.Rc ?? 0m) != (d.Rc ?? 0m))))
                        failures.Add(new { check = "OPEN_ITEMS_VS_DECLARED", account_no = no, declared_lc = d.Lc, ledger_lc = i.Lc });
                }
            }
            Guid clearing = (await AccountRoleResolver.ResolveAsync(tx, tenantId, companyId, "MIGRATION_CLEARING", null, null, null, ct))!.Value;
            await using (var cmd = tx.Command("select coalesce(sum(amount_lc), 0), coalesce(sum(amount_gc), 0), coalesce(sum(amount_rc), 0) from core.journal_line where tenant_id = @t and company_id = @c and account_id = @a"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("a", clearing);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                await r.ReadAsync(ct);
                if (r.GetDecimal(0) != 0m || r.GetDecimal(1) != 0m || r.GetDecimal(2) != 0m)
                    return Reject(Error("MIGRATION.CLEARING_NOT_ZERO", "P6", "batch_id", ("lc", r.GetDecimal(0)), ("gc", r.GetDecimal(1)), ("rc", r.GetDecimal(2))));
            }
            if (failures.Count > 0) return Reject(Error("MIGRATION.SEAL_CHECK_FAILED", "P6", "batch_id", ("failed_checks", failures)));

            // 3. the anchors of the chains, the status and the event of the seal (E6)
            await AnchorRequests.WriteAsync(tx, tenantId, companyId, ct);
            long documents = await CountAsync(tx, "select count(*) from core.journal_document where tenant_id = @t and migration_batch_id = @b", tenantId, batch.BatchId, ct);
            long lines = await CountAsync(tx, "select count(*) from core.journal_line l join core.journal_document d on d.tenant_id = l.tenant_id and d.company_id = l.company_id and d.document_id = l.document_id where d.tenant_id = @t and d.migration_batch_id = @b", tenantId, batch.BatchId, ct);
            long openItems = await CountAsync(tx, "select count(*) from core.journal_line_open_item o join core.journal_document d on d.tenant_id = o.tenant_id and d.company_id = o.company_id and d.document_id = o.document_id where d.tenant_id = @t and d.migration_batch_id = @b", tenantId, batch.BatchId, ct);
            long first = 0, last = 0;
            await using (var cmd = tx.Command("select coalesce(min(chain_seq), 0), coalesce(max(chain_seq), 0) from core.journal_document where tenant_id = @t and migration_batch_id = @b"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("b", batch.BatchId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                await r.ReadAsync(ct);
                first = r.GetInt64(0); last = r.GetInt64(1);
            }
            await SetStatusAsync(tx, tenantId, ctx, batch, "SEALED", false, false, true, ct, writeEvent: false);
            await CommandSupport.WriteOutboxAsync(tx, tenantId, companyId, ctx, "migration.sealed", BatchEntity, $"{companyId:D}/{batch.BatchId:D}", "STATUS", DateTime.UtcNow, isAudit: true, null,
                JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["batch_id"] = batch.BatchId, ["status"] = "SEALED", ["documents"] = documents, ["lines"] = lines, ["open_items"] = openItems,
                    ["chain_ranges"] = new[] { new Dictionary<string, object?> { ["chain_type"] = "LEDGER", ["scope"] = ledgerId.ToString("D"), ["first_seq"] = first, ["last_seq"] = last } }
                }), ct);
            Guid logId = await CommandSupport.WriteLogAsync(tx, tenantId, companyId, ctx, "migration-batches:seal", fingerprint, BatchEntity, batch.BatchId, ct);
            MigrationBatchResult result = await SummaryAsync(tx, tenantId, batch.BatchId, null, null, (int)documents, ct);
            await tx.CommitAsync(ct);
            return CommandResult<MigrationBatchResult>.Ok(result, logId);
        });
    }
}
