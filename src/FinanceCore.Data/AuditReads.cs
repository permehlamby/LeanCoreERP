using System.Text.Json;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Hashing;
using FinanceCore.Core.Reads;
using Npgsql;
using NpgsqlTypes;

namespace FinanceCore.Data;

/// <summary>
/// The audit side of the reads (09): chains:status (4.9), audit-events:query (4.8) and read-access-log:list (5.2). All need read.audit. They run in one repeatable-read
/// transaction of the read role. The audit view is the stream of outbox_event where is_audit, in position order, so a missing source is a missing event type (Q11).
/// Not built: the other audit resources of section 3 (anchors, change-log, command-log and so on as lists), include=detail, company access beyond the tenant.
/// </summary>
public sealed class AuditReads(NpgsqlDataSource source)
{
    public const string Permission = PartnerReadRules.AuditPermission;

    private static CommandError Error(string code, string phase, string path, params (string Key, object? Value)[] parameters) =>
        JournalDocumentPoster.Error(code, phase, path, parameters);

    private static CommandResult<T> Reject<T>(CommandError e) => CommandResult<T>.Rejected([e]);

    private static CommandError? Denied(CommandContext ctx) =>
        ctx.Permissions.Contains(Permission) ? null : Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", Permission));

    private static DateTime Now() { DateTime n = DateTime.UtcNow; return new DateTime(n.Ticks - n.Ticks % 10, DateTimeKind.Utc); }

    private static string? Text(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static DateTime Utc(NpgsqlDataReader r, int i) => DateTime.SpecifyKind(r.GetDateTime(i), DateTimeKind.Utc);

    private static NpgsqlParameter Param(string name, NpgsqlDbType type, object? value) => new(name, type) { Value = value ?? DBNull.Value };

    // ---------------------------------------------------------------------------------------------------------
    // chains:status (09, 4.9)
    // ---------------------------------------------------------------------------------------------------------
    private static readonly string[] ChainTypes = ["LEDGER", "CLEARING", "PERIOD_EVENT", "VAT_RETURN", "STOCK", "FULFILMENT"];

    public async Task<CommandResult<ChainsStatusResult>> ChainsStatusAsync(Guid tenantId, CommandContext ctx, ChainsStatusRequest req, CancellationToken ct = default)
    {
        if (Denied(ctx) is { } denied) return Reject<ChainsStatusResult>(denied);
        if (req.ChainType is not null && !ChainTypes.Contains(req.ChainType))
            return Reject<ChainsStatusResult>(Error("READ.INVALID_FILTER", "P1", "chain_type", ("field", "chain_type"), ("allowed", ChainTypes)));

        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct, snapshot: true);
        await using (var cmd = tx.Command("select 1 from core.company where tenant_id = @t and company_id = @c"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId);
            if (await cmd.ExecuteScalarAsync(ct) is null) return Reject<ChainsStatusResult>(Error("READ.NOT_FOUND", "P2", "company_id"));
        }
        var heads = new List<(string Type, string Scope, long Seq, byte[] Hash, int Version, DateTime At)>();
        await using (var cmd = tx.Command(
            "select chain_type, scope_key, last_seq, last_hash, hash_version, updated_at from core.chain_head where tenant_id = @t and company_id = @c " +
            "and (@type::text is null or chain_type = @type) and (@scope::text is null or scope_key = @scope) order by chain_type, scope_key"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId);
            cmd.Parameters.Add(Param("type", NpgsqlDbType.Text, req.ChainType)); cmd.Parameters.Add(Param("scope", NpgsqlDbType.Text, req.ScopeKey));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) heads.Add((r.GetString(0), r.GetString(1), r.GetInt64(2), (byte[])r[3], r.GetInt16(4), Utc(r, 5)));
        }
        var chains = new List<ChainStatusEntry>();
        foreach (var h in heads)
        {
            AnchorRead? anchor = null;
            await using (var cmd = tx.Command(
                "select anchor_id, chain_seq, anchored_at, anchor_state, tsa_id from core.chain_anchor where tenant_id = @t and company_id = @c and chain_type = @type and scope_key = @scope " +
                "order by chain_seq desc, anchored_at desc limit 1"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("type", h.Type); cmd.Parameters.AddWithValue("scope", h.Scope);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct)) anchor = new AnchorRead(r.GetGuid(0), r.GetInt64(1), Utc(r, 2), r.GetString(3), Text(r, 4));
            }
            chains.Add(new ChainStatusEntry(h.Type, h.Scope, h.Seq, Canonical.FormatHex32(h.Hash), h.Version, h.At, anchor));
        }
        return CommandResult<ChainsStatusResult>.Ok(new ChainsStatusResult(chains, Now()), Guid.Empty);
    }

    // ---------------------------------------------------------------------------------------------------------
    // audit-events:query (09, 4.8)
    // ---------------------------------------------------------------------------------------------------------
    public async Task<CommandResult<AuditEventList>> AuditEventsAsync(Guid tenantId, CommandContext ctx, AuditEventsRequest req, CancellationToken ct = default)
    {
        if (Denied(ctx) is { } denied) return Reject<AuditEventList>(denied);
        int limit = req.Limit ?? ReadRules.DefaultLimit;
        if (limit is < 1 or > ReadRules.MaxLimit) return Reject<AuditEventList>(Error("READ.LIMIT_EXCEEDED", "P1", "limit", ("max", ReadRules.MaxLimit)));
        if (!ReadRules.TryDecodeCursor(req.Cursor, "audit", out long after)) return Reject<AuditEventList>(Error("READ.CURSOR_INVALID", "P1", "cursor"));
        if (req.From is not null && req.To is not null && req.From > req.To)
            return Reject<AuditEventList>(Error("SCHEMA.INVALID_VALUE", "P1", "from", ("reason", "from is after to")));
        // Q11: a time range, an actor, an entity or a correlation id keeps a wide query bounded
        if (req.From is null && req.To is null && req.Actor is null && req.EntityType is null && req.CorrelationId is null)
            return Reject<AuditEventList>(Error("READ.INVALID_FILTER", "P1", "from", ("reason", "a time range, an actor, an entity or a correlation_id is required (Q11)")));
        if (req.EntityKey is not null && req.EntityType is null)
            return Reject<AuditEventList>(Error("READ.INVALID_FILTER", "P1", "entity_key", ("reason", "entity_key needs entity_type")));

        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct, snapshot: true);
        long head;
        await using (var cmd = tx.Command("select coalesce(max(last_position), 0) from core.outbox_head where tenant_id = @t"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            head = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        var items = new List<AuditEventRead>();
        await using (var cmd = tx.Command(
            "select position, occurred_at, actor_ref, app_id, event_type, entity_type, entity_key, company_id, correlation_id, chain_ref::text, summary::text from core.outbox_event " +
            "where tenant_id = @t and is_audit and position > @after and (@company::uuid is null or company_id = @company) and (@from::timestamptz is null or occurred_at >= @from) " +
            "and (@to::timestamptz is null or occurred_at <= @to) and (@actor::text is null or actor_ref = @actor) and (@app::uuid is null or app_id = @app) " +
            "and (@types::text[] is null or event_type = any(@types)) and (@etype::text is null or entity_type = @etype) and (@ekey::text is null or entity_key = @ekey) " +
            "and (@corr::text is null or correlation_id = @corr) order by position limit @n"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("after", after); cmd.Parameters.AddWithValue("n", limit + 1);
            cmd.Parameters.Add(Param("company", NpgsqlDbType.Uuid, req.CompanyId));
            cmd.Parameters.Add(Param("from", NpgsqlDbType.TimestampTz, req.From is { } f ? DateTime.SpecifyKind(f, f.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : f.Kind).ToUniversalTime() : null));
            cmd.Parameters.Add(Param("to", NpgsqlDbType.TimestampTz, req.To is { } to ? DateTime.SpecifyKind(to, to.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : to.Kind).ToUniversalTime() : null));
            cmd.Parameters.Add(Param("actor", NpgsqlDbType.Text, req.Actor)); cmd.Parameters.Add(Param("app", NpgsqlDbType.Uuid, req.AppId));
            cmd.Parameters.Add(Param("types", NpgsqlDbType.Array | NpgsqlDbType.Text, req.EventTypes is { Count: > 0 } ? req.EventTypes.ToArray() : null));
            cmd.Parameters.Add(Param("etype", NpgsqlDbType.Text, req.EntityType)); cmd.Parameters.Add(Param("ekey", NpgsqlDbType.Text, req.EntityKey));
            cmd.Parameters.Add(Param("corr", NpgsqlDbType.Text, req.CorrelationId));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                JsonElement? chain = Text(r, 9) is { } c ? JsonDocument.Parse(c).RootElement.Clone() : null;
                JsonElement? summary = Text(r, 10) is { } s ? JsonDocument.Parse(s).RootElement.Clone() : null;
                string? Field(string name) => summary is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                string type = r.GetString(4);
                items.Add(new AuditEventRead(r.GetInt64(0), Utc(r, 1), r.GetString(2), r.IsDBNull(3) ? null : r.GetGuid(3), type, AuditViewTypes.Of(type, Field("status"), Field("kind")),
                    r.GetString(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetGuid(7), r.GetString(8), Field("reason_code"), chain, summary));
            }
        }
        string? next = null;
        if (items.Count > limit) { items.RemoveAt(limit); next = ReadRules.EncodeCursor("audit", items[^1].Position); }
        return CommandResult<AuditEventList>.Ok(new AuditEventList(items, new ReadPage(next, limit), head, Now()), Guid.Empty);
    }

    // ---------------------------------------------------------------------------------------------------------
    // read-access-log:list (09, 5.2)
    // ---------------------------------------------------------------------------------------------------------
    public async Task<CommandResult<ReadAccessLogList>> ReadAccessLogAsync(Guid tenantId, CommandContext ctx, ReadAccessLogRequest req, CancellationToken ct = default)
    {
        if (Denied(ctx) is { } denied) return Reject<ReadAccessLogList>(denied);
        int limit = req.Limit ?? ReadRules.DefaultLimit;
        if (limit is < 1 or > ReadRules.MaxLimit) return Reject<ReadAccessLogList>(Error("READ.LIMIT_EXCEEDED", "P1", "limit", ("max", ReadRules.MaxLimit)));
        if (!ReadRules.TryDecodeCursor(req.Cursor, "ral", out long offset)) return Reject<ReadAccessLogList>(Error("READ.CURSOR_INVALID", "P1", "cursor"));

        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct, snapshot: true);
        var items = new List<ReadAccessLogRead>();
        await using (var cmd = tx.Command(
            "select read_id, read_at, actor, operation, company_id, partner_id, partner_count, export_job_id, document_id, field_group, purpose_code, correlation_id, result_position " +
            "from core.read_access_log where tenant_id = @t and (@partner::uuid is null or partner_id = @partner) and (@actor::text is null or actor = @actor) " +
            "and (@from::timestamptz is null or read_at >= @from) and (@to::timestamptz is null or read_at <= @to) and (@job::uuid is null or export_job_id = @job) " +
            "order by read_at, read_id offset @o limit @n"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("o", offset); cmd.Parameters.AddWithValue("n", limit + 1);
            cmd.Parameters.Add(Param("partner", NpgsqlDbType.Uuid, req.PartnerId)); cmd.Parameters.Add(Param("actor", NpgsqlDbType.Text, req.Actor));
            cmd.Parameters.Add(Param("from", NpgsqlDbType.TimestampTz, req.From is { } f ? DateTime.SpecifyKind(f, f.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : f.Kind).ToUniversalTime() : null));
            cmd.Parameters.Add(Param("to", NpgsqlDbType.TimestampTz, req.To is { } to ? DateTime.SpecifyKind(to, to.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : to.Kind).ToUniversalTime() : null));
            cmd.Parameters.Add(Param("job", NpgsqlDbType.Uuid, req.ExportJobId));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                items.Add(new ReadAccessLogRead(r.GetGuid(0), Utc(r, 1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetGuid(4), r.IsDBNull(5) ? null : r.GetGuid(5),
                    r.IsDBNull(6) ? null : r.GetInt32(6), r.IsDBNull(7) ? null : r.GetGuid(7), r.IsDBNull(8) ? null : r.GetGuid(8), r.GetString(9), Text(r, 10), r.GetString(11),
                    r.IsDBNull(12) ? null : r.GetInt32(12)));
        }
        string? next = null;
        if (items.Count > limit) { items.RemoveAt(limit); next = ReadRules.EncodeCursor("ral", offset + limit); }
        return CommandResult<ReadAccessLogList>.Ok(new ReadAccessLogList(items, new ReadPage(next, limit), Now()), Guid.Empty);
    }
}
