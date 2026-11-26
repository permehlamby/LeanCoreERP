using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;
using Npgsql;

namespace FinanceCore.Data;

/// <summary>The status of a lot or serial (10, 3.8): ACTIVE or RECALLED. A recalled lot blocks issues (INVENTORY.TRACKING_RECALLED).</summary>
public sealed record SetTrackingStatusRequest(Guid Id, long ExpectedVersion, string Status);

public sealed record TrackingStatusResult(Guid Id, string TrackingNo, string Status, long RowVersion);

/// <summary>
/// item-trackings:set-status (10, 3.8, permission write.config): recalls a lot or serial, or releases the recall. The rows themselves are created by the stock command that first receives the lot or
/// serial and are never created or deleted here. Compare and set on row_version; a change is a master_change_log row (entity ITEM_TRACKING) and a master event.
/// </summary>
public sealed class ItemTrackingCommands(NpgsqlDataSource source)
{
    public const string Permission = MasterRules.ConfigPermission;

    private static CommandError Error(string code, string phase, string path, params (string Key, object? Value)[] p) => JournalDocumentPoster.Error(code, phase, path, p);

    public async Task<CommandResult<TrackingStatusResult>> SetStatusAsync(Guid tenantId, CommandContext ctx, SetTrackingStatusRequest req, CancellationToken ct = default)
    {
        static CommandResult<TrackingStatusResult> Fail(CommandError e) => CommandResult<TrackingStatusResult>.Rejected([e]);
        if (!ctx.Permissions.Contains(Permission)) return Fail(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", Permission)));
        var shape = MasterSupport.ValidateContext(ctx);
        if (req.Id == Guid.Empty) shape.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/id", ("reason", "required")));
        if (req.Status is not ("ACTIVE" or "RECALLED")) shape.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/status", ("reason", "ACTIVE or RECALLED")));
        if (shape.Count > 0) return CommandResult<TrackingStatusResult>.Rejected(shape);
        byte[] fingerprint = MasterRules.Fingerprint("item-trackings:set-status", $"{req.Id:D}|{req.ExpectedVersion}|{req.Status}");
        return await MasterSupport.RunAsync(async () =>
        {
            await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Config, ct: ct);
            var earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
            if (earlier is not null)
            {
                if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint)) return Fail(Error("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
                return earlier.EntityId is null
                    ? CommandResult<TrackingStatusResult>.Failed(Error("SYSTEM.REPLAY_RESULT_MISSING", "P1", ""))
                    : CommandResult<TrackingStatusResult>.Ok((await ReadAsync(tx, tenantId, earlier.EntityId.Value, false, ct))!, earlier.CommandLogId, replay: true);
            }
            TrackingStatusResult? row = await ReadAsync(tx, tenantId, req.Id, true, ct);
            if (row is null) return Fail(Error("READ.NOT_FOUND", "P2", "/id"));
            if (row.RowVersion != req.ExpectedVersion) return Fail(Error("CONFIG.ROW_VERSION_MISMATCH", "P2", "/expected_version", ("current_version", row.RowVersion)));
            if (row.Status == req.Status) return Fail(Error("SCHEMA.NO_EFFECTIVE_CHANGE", "P2", "/status"));
            await using (var cmd = tx.Command("update core.item_tracking set status = @s, row_version = row_version + 1, updated_at = now(), updated_by = @by where tenant_id = @t and tracking_id = @i"))
            {
                cmd.Parameters.AddWithValue("s", req.Status); cmd.Parameters.AddWithValue("by", ctx.Actor); cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("i", req.Id);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            Guid logId = Guid.NewGuid(), changeSet = Guid.NewGuid();
            await MasterSupport.LogAsync(tx, tenantId, ctx, logId, changeSet, "ITEM_TRACKING", req.Id.ToString("D"), "UPDATE", "status", false, row.Status, req.Status, null);
            await CommandSupport.WriteLogAsync(tx, tenantId, null, ctx, "item-trackings:set-status", fingerprint, "ITEM_TRACKING", req.Id, logId, ct);
            DateTime now = DateTime.UtcNow;
            await CommandSupport.WriteOutboxAsync(tx, tenantId, null, ctx, "master.changed", "ITEM_TRACKING", req.Id.ToString("D"), "CHANGED", new DateTime(now.Ticks - now.Ticks % 10, DateTimeKind.Utc), isAudit: true, null,
                System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?> { ["tracking_id"] = req.Id, ["status"] = req.Status }), ct);
            TrackingStatusResult result = (await ReadAsync(tx, tenantId, req.Id, false, ct))!;
            await tx.CommitAsync(ct);
            return CommandResult<TrackingStatusResult>.Ok(result, logId);
        });
    }

    private static async Task<TrackingStatusResult?> ReadAsync(TenantTransaction tx, Guid tenantId, Guid id, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = tx.Command("select tracking_no, status, row_version from core.item_tracking where tenant_id = @t and tracking_id = @i" + (forUpdate ? " for update" : ""));
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("i", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new TrackingStatusResult(id, r.GetString(0), r.GetString(1).Trim(), r.GetInt64(2)) : null;
    }
}
