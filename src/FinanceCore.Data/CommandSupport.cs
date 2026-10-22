using System.Globalization;
using FinanceCore.Core.Commands;
using Npgsql;
using NpgsqlTypes;

namespace FinanceCore.Data;

/// <summary>The parts every command shares: the idempotency record (06, 4) and the outbox event (15, 3.7, 3.8).</summary>
internal static class CommandSupport
{
    public sealed record EarlierCommand(Guid CommandLogId, byte[] Fingerprint, Guid? EntityId);

    /// <summary>The command_log row of an earlier command with the same idempotency key, with its first result entity.</summary>
    public static async Task<EarlierCommand?> FindEarlierAsync(TenantTransaction tx, Guid tenantId, string key, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select l.command_log_id, l.request_fingerprint, " +
            "(select r.entity_id from core.command_log_result r where r.tenant_id = l.tenant_id and r.command_log_id = l.command_log_id " +
            " order by r.result_seq limit 1) " +
            "from core.command_log l where l.tenant_id = @t and l.idempotency_key = @k");
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("k", key);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new EarlierCommand(r.GetGuid(0), (byte[])r[1], r.IsDBNull(2) ? null : r.GetGuid(2))
            : null;
    }

    public static async Task<Guid> WriteLogAsync(
        TenantTransaction tx, Guid tenantId, Guid? companyId, CommandContext ctx, string commandName, byte[] fingerprint,
        string entityType, Guid entityId, CancellationToken ct)
    {
        Guid logId = Guid.NewGuid();
        await using (var cmd = tx.Command(
            "insert into core.command_log (tenant_id, command_log_id, company_id, idempotency_key, command_name, request_fingerprint, actor, correlation_id) " +
            "values (@t, @id, @c, @k, @n, @f, @a, @corr)"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("id", logId);
            cmd.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Uuid) { Value = (object?)companyId ?? DBNull.Value });
            cmd.Parameters.AddWithValue("k", ctx.IdempotencyKey);
            cmd.Parameters.AddWithValue("n", commandName);
            cmd.Parameters.AddWithValue("f", fingerprint);
            cmd.Parameters.AddWithValue("a", ctx.Actor);
            cmd.Parameters.AddWithValue("corr", ctx.CorrelationId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using (var cmd = tx.Command(
            "insert into core.command_log_result (tenant_id, command_log_id, result_seq, entity_type, entity_id) values (@t, @id, 1, @type, @e)"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("id", logId);
            cmd.Parameters.AddWithValue("type", entityType);
            cmd.Parameters.AddWithValue("e", entityId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        return logId;
    }

    /// <summary>Takes the next outbox position (the head row is the last lock of a command) and inserts the event.</summary>
    public static async Task<long> WriteOutboxAsync(
        TenantTransaction tx, Guid tenantId, Guid? companyId, CommandContext ctx, string eventType, string entityType,
        string entityKey, string change, DateTime occurredAt, bool isAudit, string? chainRefJson, string summaryJson, CancellationToken ct)
    {
        long position;
        await using (var cmd = tx.Command(
            "update core.outbox_head set last_position = last_position + 1 where tenant_id = @t returning last_position"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            object? value = await cmd.ExecuteScalarAsync(ct);
            if (value is null) throw new InvalidOperationException("The tenant has no outbox_head row");
            position = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        await using (var cmd = tx.Command(
            "insert into core.outbox_event (tenant_id, position, event_type, event_version, company_id, entity_type, entity_key, " +
            "entity_version, change, occurred_at, actor_ref, correlation_id, is_audit, chain_ref, summary) " +
            "values (@t, @p, @type, 1, @c, @et, @key, null, @change, @at, @a, @corr, @audit, @chain, @sum)"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("p", position);
            cmd.Parameters.AddWithValue("type", eventType);
            cmd.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Uuid) { Value = (object?)companyId ?? DBNull.Value });
            cmd.Parameters.AddWithValue("et", entityType);
            cmd.Parameters.AddWithValue("key", entityKey);
            cmd.Parameters.AddWithValue("change", change);
            cmd.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = occurredAt });
            cmd.Parameters.AddWithValue("a", ctx.Actor);
            cmd.Parameters.AddWithValue("corr", ctx.CorrelationId);
            cmd.Parameters.AddWithValue("audit", isAudit);
            cmd.Parameters.Add(new NpgsqlParameter("chain", NpgsqlDbType.Jsonb) { Value = (object?)chainRefJson ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("sum", NpgsqlDbType.Jsonb) { Value = summaryJson });
            await cmd.ExecuteNonQueryAsync(ct);
        }
        return position;
    }

    /// <summary>Creates a chain head at sequence 0 with the genesis value if it does not exist. No lock: the configuration
    /// role may insert a head but not update one.</summary>
    public static async Task<bool> EnsureHeadAsync(
        TenantTransaction tx, Guid tenantId, Guid companyId, string chainType, string scopeKey, byte[] genesis, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "insert into core.chain_head (tenant_id, company_id, chain_type, scope_key, last_seq, last_hash, hash_version) " +
            "values (@t, @c, @type, @k, 0, @g, 2) on conflict (tenant_id, company_id, chain_type, scope_key) do nothing");
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", companyId);
        cmd.Parameters.AddWithValue("type", chainType);
        cmd.Parameters.AddWithValue("k", scopeKey);
        cmd.Parameters.AddWithValue("g", genesis);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>Creates a chain head at sequence 0 with the genesis value if it does not exist, then locks it.
    /// Returns (last_seq, last_hash). Who creates heads is not said in 02, 9.2 (finding F-IMPL-10).</summary>
    public static async Task<(long Seq, byte[] Hash, bool Created)> LockOrCreateHeadAsync(
        TenantTransaction tx, Guid tenantId, Guid companyId, string chainType, string scopeKey, byte[] genesis, CancellationToken ct)
    {
        bool created;
        await using (var cmd = tx.Command(
            "insert into core.chain_head (tenant_id, company_id, chain_type, scope_key, last_seq, last_hash, hash_version) " +
            "values (@t, @c, @type, @k, 0, @g, 2) on conflict (tenant_id, company_id, chain_type, scope_key) do nothing"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", companyId);
            cmd.Parameters.AddWithValue("type", chainType);
            cmd.Parameters.AddWithValue("k", scopeKey);
            cmd.Parameters.AddWithValue("g", genesis);
            created = await cmd.ExecuteNonQueryAsync(ct) == 1;
        }
        await using var lockCmd = tx.Command(
            "select last_seq, last_hash from core.chain_head " +
            "where tenant_id = @t and company_id = @c and chain_type = @type and scope_key = @k for update");
        lockCmd.Parameters.AddWithValue("t", tenantId);
        lockCmd.Parameters.AddWithValue("c", companyId);
        lockCmd.Parameters.AddWithValue("type", chainType);
        lockCmd.Parameters.AddWithValue("k", scopeKey);
        await using var r = await lockCmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return (r.GetInt64(0), (byte[])r[1], created);
    }
}
