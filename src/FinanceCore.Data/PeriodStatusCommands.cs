using System.Text.Json;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Hashing;
using FinanceCore.Core.Periods;
using Npgsql;
using NpgsqlTypes;

namespace FinanceCore.Data;

/// <summary>
/// periods:soft-close, periods:hard-close and periods:reopen (04, 7.3). Each appends a period_event to the period chain,
/// changes the status of one period together with that event (the trigger of 0006 insists on it) and emits period.status_changed.
///
/// Locks: the PERIOD_EVENT head, then the fiscal_year and fiscal_period rows exclusively, then outbox_head last (04, 6.3).
/// All status commands of a company take the head first, so they run one after the other and the checks that read other
/// periods (predecessor, later hard-closed period) see a stable picture.
///
/// Deviations from the specs, all in db/README.md: (F-IMPL-13) hard-close does not lock the LEDGER and CLEARING heads, because
/// the period role may lock only its own chain (row-level security of 0006). A posting still cannot commit into a period that
/// closes, because it holds the period row in share mode after its head locks and the close takes the row exclusively.
/// No anchor request is written (no chain_anchor table yet) and the revaluation stale check does not run (no revaluation yet).
/// </summary>
public sealed class PeriodStatusCommands(NpgsqlDataSource source)
{
    public const string SoftClosePermission = "period.admin";
    public const string HardClosePermission = "period.hard_close";
    public const string ReopenPermission = "period.reopen";

    private enum Kind { SoftClose, HardClose, Reopen }

    public Task<CommandResult<PeriodStatusChange>> SoftCloseAsync(
        Guid tenantId, CommandContext ctx, PeriodRef period, CancellationToken ct = default) =>
        RunAsync(tenantId, ctx, period, Kind.SoftClose, "SOFT_CLOSED", null, null, ct);

    public Task<CommandResult<PeriodStatusChange>> HardCloseAsync(
        Guid tenantId, CommandContext ctx, PeriodRef period, CancellationToken ct = default) =>
        RunAsync(tenantId, ctx, period, Kind.HardClose, "HARD_CLOSED", null, null, ct);

    public Task<CommandResult<PeriodStatusChange>> ReopenAsync(
        Guid tenantId, CommandContext ctx, ReopenPeriodRequest req, CancellationToken ct = default) =>
        RunAsync(tenantId, ctx, req.Period, Kind.Reopen, req.TargetStatus, req.ReasonCode, req.ReasonText, ct);

    private static string CommandName(Kind k) => k switch
    {
        Kind.SoftClose => "periods:soft-close", Kind.HardClose => "periods:hard-close", _ => "periods:reopen"
    };

    private static string Permission(Kind k) => k switch
    {
        Kind.SoftClose => SoftClosePermission, Kind.HardClose => HardClosePermission, _ => ReopenPermission
    };

    private static string EventType(Kind k) => k switch
    {
        Kind.SoftClose => "PERIOD_SOFT_CLOSE", Kind.HardClose => "PERIOD_HARD_CLOSE", _ => "PERIOD_REOPEN"
    };

    private async Task<CommandResult<PeriodStatusChange>> RunAsync(
        Guid tenantId, CommandContext ctx, PeriodRef p, Kind kind, string target, string? reasonCode, string? reasonText, CancellationToken ct)
    {
        string permission = Permission(kind);
        if (!ctx.Permissions.Contains(permission))
            return Reject(new CommandError("AUTH.PERMISSION_DENIED", "P0", "", new Dictionary<string, object?> { ["permission"] = permission }));

        var shape = new List<CommandError>();
        void Invalid(string path, string reason) =>
            shape.Add(new CommandError("SCHEMA.INVALID_VALUE", "P1", path, new Dictionary<string, object?> { ["reason"] = reason }));
        if (string.IsNullOrWhiteSpace(ctx.IdempotencyKey) || ctx.IdempotencyKey.Length > 100) Invalid("idempotency_key", "required, at most 100 characters");
        if (string.IsNullOrWhiteSpace(ctx.CorrelationId) || ctx.CorrelationId.Length > 100) Invalid("correlation_id", "required, at most 100 characters");
        if (string.IsNullOrWhiteSpace(ctx.Actor) || ctx.Actor.Length > 100) Invalid("actor", "required, at most 100 characters");
        if (p.CompanyId == Guid.Empty) Invalid("company_id", "required");
        if (p.PeriodNo < 1) Invalid("period_no", "at least 1");
        if (kind == Kind.Reopen)
        {
            if (target is not ("SOFT_CLOSED" or "OPEN")) Invalid("target_status", "SOFT_CLOSED or OPEN");
            if (string.IsNullOrWhiteSpace(reasonCode) || reasonCode.Length > 50) Invalid("reason_code", "required (ADR-023), at most 50 characters");
            if (string.IsNullOrWhiteSpace(reasonText) || reasonText.Length > 500) Invalid("reason_text", "required (ADR-023), at most 500 characters");
        }
        if (shape.Count > 0) return CommandResult<PeriodStatusChange>.Rejected(shape);

        byte[] fingerprint = kind == Kind.Reopen
            ? PeriodFingerprint.ForReopen(new ReopenPeriodRequest(p, target, reasonCode!, reasonText!))
            : PeriodFingerprint.ForStatus(CommandName(kind), p);

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await RunOnceAsync(tenantId, ctx, p, kind, target, reasonCode, reasonText, fingerprint, ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.TableName == "command_log" && attempt == 0)
            {
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                return CommandResult<PeriodStatusChange>.Failed(new CommandError("SYSTEM.LOCK_TIMEOUT", "P7", "", null, true));
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure)
            {
                return CommandResult<PeriodStatusChange>.Failed(new CommandError("SYSTEM.CONFLICT", "P7", "", null, true));
            }
        }
    }

    private async Task<CommandResult<PeriodStatusChange>> RunOnceAsync(
        Guid tenantId, CommandContext ctx, PeriodRef p, Kind kind, string target, string? reasonCode, string? reasonText,
        byte[] fingerprint, CancellationToken ct)
    {
        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Period, ct: ct);

        // Idempotency
        CommandSupport.EarlierCommand? earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
        if (earlier is not null)
        {
            if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint))
                return Reject(new CommandError("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
            PeriodStatusChange? replay = earlier.EntityId is null ? null : await LoadByEventAsync(tx, tenantId, earlier.EntityId.Value, ct);
            return replay is null
                ? CommandResult<PeriodStatusChange>.Failed(new CommandError("SYSTEM.REPLAY_RESULT_MISSING", "P1", ""))
                : CommandResult<PeriodStatusChange>.Ok(replay, earlier.CommandLogId, replay: true);
        }

        // P2: the period must exist (early rejection without a lock)
        if (!await PeriodExistsAsync(tx, tenantId, p, ct))
            return Reject(new CommandError("PERIOD.NOT_FOUND", "P2", "period_no",
                new Dictionary<string, object?> { ["fiscal_year"] = p.FiscalYear, ["period_no"] = p.PeriodNo }));

        // P7: PERIOD_EVENT head, then year and period rows exclusively (04, 6.3), then everything is read again under the locks
        byte[] genesis = LedgerHash2.Genesis(tenantId, p.CompanyId, PeriodEventHash2.ChainType, "");
        (long headSeq, byte[] headHash, _) = await CommandSupport.LockOrCreateHeadAsync(
            tx, tenantId, p.CompanyId, PeriodEventHash2.ChainType, "", genesis, ct);

        bool history; int specialCount; string yearStatus;
        await using (var cmd = tx.Command(
            "select is_history, special_period_count, status from core.fiscal_year " +
            "where tenant_id = @t and company_id = @c and fiscal_year = @y for update"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", p.CompanyId);
            cmd.Parameters.AddWithValue("y", p.FiscalYear);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            history = r.GetBoolean(0); specialCount = r.GetInt16(1); yearStatus = r.GetString(2);
        }
        string from;
        await using (var cmd = tx.Command(
            "select status from core.fiscal_period where tenant_id = @t and company_id = @c and fiscal_year = @y and period_no = @p for update"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", p.CompanyId);
            cmd.Parameters.AddWithValue("y", p.FiscalYear);
            cmd.Parameters.AddWithValue("p", p.PeriodNo);
            from = (string)(await cmd.ExecuteScalarAsync(ct))!;
        }

        // Checks, after the locks
        CommandError? error = null;
        if (history)
            error = new CommandError(kind == Kind.Reopen ? "PERIOD.HISTORY_YEAR_SEALED" : "PERIOD.HISTORY_YEAR", "P3", "fiscal_year",
                new Dictionary<string, object?> { ["fiscal_year"] = p.FiscalYear });
        else if (specialCount >= 1 && p.PeriodNo == 12 + specialCount)
            // The closing period is changed only by year-end:close and year-end:reopen (04, 3.3)
            error = new CommandError("PERIOD.CLOSING_PERIOD_MANAGED", "P3", "period_no",
                new Dictionary<string, object?> { ["fiscal_year"] = p.FiscalYear, ["period_no"] = p.PeriodNo });
        else
            error = kind switch
            {
                Kind.SoftClose => from == "OPEN" ? null : StatusInvalid(from, ["OPEN"]),
                Kind.HardClose => from is "OPEN" or "SOFT_CLOSED" ? await PredecessorErrorAsync(tx, tenantId, p, ct) : StatusInvalid(from, ["OPEN", "SOFT_CLOSED"]),
                _ => await ReopenErrorAsync(tx, tenantId, p, from, target, yearStatus, ct)
            };
        if (error is not null) return Reject(error);

        // The event, its hash and the status change
        Guid eventId = Guid.NewGuid();
        DateTime now = DateTime.UtcNow;
        DateTime eventAt = new(now.Ticks - now.Ticks % 10, DateTimeKind.Utc);
        long eventSeq = headSeq + 1;
        string eventType = EventType(kind);
        string authorisation = Permission(kind);
        var body = new Dictionary<string, object?>
        {
            ["event_id"] = eventId, ["fiscal_year"] = (long)p.FiscalYear, ["period_no"] = (long)p.PeriodNo, ["event_type"] = eventType,
            ["from_status"] = from, ["to_status"] = target, ["reason_code"] = reasonCode, ["reason_text"] = reasonText,
            ["authorisation"] = authorisation, ["document_id"] = null, ["event_at"] = eventAt, ["event_by"] = ctx.Actor,
            ["correlation_id"] = ctx.CorrelationId, ["idempotency_key"] = ctx.IdempotencyKey
        };
        byte[] rowHash = PeriodEventHash2.RowHash(tenantId, p.CompanyId, eventSeq, headHash, body);

        await using (var cmd = tx.Command(
            "insert into core.period_event (tenant_id, event_id, company_id, event_seq, fiscal_year, period_no, event_type, from_status, to_status, " +
            "reason_code, reason_text, authorisation, event_at, event_by, correlation_id, idempotency_key, prev_hash, row_hash, hash_version) " +
            "values (@t, @e, @c, @seq, @y, @p, @type, @from, @to, @rc, @rt, @auth, @at, @by, @corr, @key, @prev, @row, 2)"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("e", eventId);
            cmd.Parameters.AddWithValue("c", p.CompanyId);
            cmd.Parameters.AddWithValue("seq", eventSeq);
            cmd.Parameters.AddWithValue("y", p.FiscalYear);
            cmd.Parameters.AddWithValue("p", p.PeriodNo);
            cmd.Parameters.AddWithValue("type", eventType);
            cmd.Parameters.AddWithValue("from", from);
            cmd.Parameters.AddWithValue("to", target);
            cmd.Parameters.Add(new NpgsqlParameter("rc", NpgsqlDbType.Text) { Value = (object?)reasonCode ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("rt", NpgsqlDbType.Text) { Value = (object?)reasonText ?? DBNull.Value });
            cmd.Parameters.AddWithValue("auth", authorisation);
            cmd.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = eventAt });
            cmd.Parameters.AddWithValue("by", ctx.Actor);
            cmd.Parameters.AddWithValue("corr", ctx.CorrelationId);
            cmd.Parameters.AddWithValue("key", ctx.IdempotencyKey);
            cmd.Parameters.AddWithValue("prev", headHash);
            cmd.Parameters.AddWithValue("row", rowHash);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var cmd = tx.Command(
            "update core.fiscal_period set status = @to, last_event_id = @e " +
            "where tenant_id = @t and company_id = @c and fiscal_year = @y and period_no = @p"))
        {
            cmd.Parameters.AddWithValue("to", target);
            cmd.Parameters.AddWithValue("e", eventId);
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", p.CompanyId);
            cmd.Parameters.AddWithValue("y", p.FiscalYear);
            cmd.Parameters.AddWithValue("p", p.PeriodNo);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var cmd = tx.Command(
            "update core.chain_head set last_seq = @s, last_hash = @h, hash_version = 2, updated_at = now() " +
            "where tenant_id = @t and company_id = @c and chain_type = 'PERIOD_EVENT' and scope_key = ''"))
        {
            cmd.Parameters.AddWithValue("s", eventSeq);
            cmd.Parameters.AddWithValue("h", rowHash);
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", p.CompanyId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        Guid logId = await CommandSupport.WriteLogAsync(tx, tenantId, p.CompanyId, ctx, CommandName(kind), fingerprint, "PERIOD_EVENT", eventId, ct);

        string rowHashHex = Canonical.FormatHex32(rowHash);
        await CommandSupport.WriteOutboxAsync(tx, tenantId, p.CompanyId, ctx, "period.status_changed", "FISCAL_PERIOD",
            $"{p.CompanyId:D}/{p.FiscalYear}/{p.PeriodNo}", "STATUS", eventAt, isAudit: true,
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["chain_type"] = PeriodEventHash2.ChainType, ["scope"] = "", ["chain_seq"] = eventSeq, ["row_hash"] = rowHashHex
            }),
            JsonSerializer.Serialize(new Dictionary<string, object?> { ["fiscal_year"] = p.FiscalYear, ["period_no"] = p.PeriodNo, ["status"] = target }), ct);

        await tx.CommitAsync(ct);
        return CommandResult<PeriodStatusChange>.Ok(
            new PeriodStatusChange(p.CompanyId, p.FiscalYear, p.PeriodNo, from, target, eventId, eventSeq, rowHashHex, Canonical.FormatHex32(headHash)), logId);
    }

    // ---------------------------------------------------------------------------------------------------------
    private static CommandError StatusInvalid(string status, string[] allowedFrom) =>
        new("PERIOD.STATUS_INVALID", "P3", "period_no", new Dictionary<string, object?> { ["status"] = status, ["allowed_from"] = allowedFrom });

    /// <summary>The previous period of the company, in order of year and period number, must be HARD_CLOSED (04, 7.3).
    /// The first period of a company has no predecessor (Q5).</summary>
    private static async Task<CommandError?> PredecessorErrorAsync(TenantTransaction tx, Guid tenantId, PeriodRef p, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select fiscal_year, period_no, status from core.fiscal_period " +
            "where tenant_id = @t and company_id = @c and (fiscal_year, period_no) < (@y, @p) order by fiscal_year desc, period_no desc limit 1");
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", p.CompanyId);
        cmd.Parameters.AddWithValue("y", p.FiscalYear);
        cmd.Parameters.AddWithValue("p", p.PeriodNo);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct) || r.GetString(2) == "HARD_CLOSED") return null;
        return new CommandError("CLOSE.PREDECESSOR_NOT_HARD_CLOSED", "P3", "period_no", new Dictionary<string, object?>
        {
            ["predecessor_fiscal_year"] = r.GetInt32(0), ["predecessor_period_no"] = r.GetInt32(1), ["predecessor_status"] = r.GetString(2)
        });
    }

    private static async Task<CommandError?> ReopenErrorAsync(
        TenantTransaction tx, Guid tenantId, PeriodRef p, string from, string target, string yearStatus, CancellationToken ct)
    {
        bool allowed = (from, target) is ("HARD_CLOSED", "SOFT_CLOSED") or ("HARD_CLOSED", "OPEN") or ("SOFT_CLOSED", "OPEN");
        if (!allowed)
            return new CommandError("PERIOD.STATUS_INVALID", "P3", "target_status",
                new Dictionary<string, object?> { ["status"] = from, ["target_status"] = target });
        if (yearStatus != "OPEN")
            return new CommandError("PERIOD.YEAR_CLOSED", "P3", "fiscal_year", new Dictionary<string, object?> { ["fiscal_year"] = p.FiscalYear });

        await using var cmd = tx.Command(
            "select fiscal_year, period_no from core.fiscal_period " +
            "where tenant_id = @t and company_id = @c and (fiscal_year, period_no) > (@y, @p) and status = 'HARD_CLOSED' " +
            "order by fiscal_year, period_no limit 1");
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", p.CompanyId);
        cmd.Parameters.AddWithValue("y", p.FiscalYear);
        cmd.Parameters.AddWithValue("p", p.PeriodNo);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new CommandError("PERIOD.REOPEN_LATER_PERIOD_CLOSED", "P3", "period_no", new Dictionary<string, object?>
              {
                  ["later_fiscal_year"] = r.GetInt32(0), ["later_period_no"] = r.GetInt32(1)
              })
            : null;
    }

    private static async Task<bool> PeriodExistsAsync(TenantTransaction tx, Guid tenantId, PeriodRef p, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select 1 from core.fiscal_period where tenant_id = @t and company_id = @c and fiscal_year = @y and period_no = @p");
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", p.CompanyId);
        cmd.Parameters.AddWithValue("y", p.FiscalYear);
        cmd.Parameters.AddWithValue("p", p.PeriodNo);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task<PeriodStatusChange?> LoadByEventAsync(TenantTransaction tx, Guid tenantId, Guid eventId, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select company_id, fiscal_year, period_no, from_status, to_status, event_seq, row_hash, prev_hash " +
            "from core.period_event where tenant_id = @t and event_id = @e");
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("e", eventId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new PeriodStatusChange(r.GetGuid(0), r.GetInt32(1), r.GetInt32(2), r.GetString(3), r.GetString(4), eventId,
                r.GetInt64(5), Canonical.FormatHex32((byte[])r[6]), Canonical.FormatHex32((byte[])r[7]))
            : null;
    }

    private static CommandResult<PeriodStatusChange> Reject(CommandError error) => CommandResult<PeriodStatusChange>.Rejected([error]);
}
