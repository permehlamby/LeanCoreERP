using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;
using Npgsql;
using static FinanceCore.Data.MasterSupport;

namespace FinanceCore.Data;

/// <summary>
/// exchange-rates:mark-used (23, 5, 07, 7.3, ADR-225, permission write.config): sets first_used_at on the named rate rows that have none, for any rate type, so the usage lock of 07, 7.2 applies (the
/// guard trigger refuses a change of a used row). A row that is already used stays as it is. An unknown id rejects the whole command (SCHEMA.INVALID_VALUE). One master_change_log row per marked
/// rate (field first_used_at) and the event master.changed. The purpose reference (a consolidation run) is part of the fingerprint and the answer, not of the log row.
/// </summary>
public sealed class RateUsageCommands(NpgsqlDataSource source)
{
    public const string Permission = MasterRules.ConfigPermission;

    private static CommandResult<MarkedRatesResult> Reject(CommandError e) => CommandResult<MarkedRatesResult>.Rejected([e]);

    public Task<CommandResult<MarkedRatesResult>> MarkUsedAsync(Guid tenantId, CommandContext ctx, MarkRatesUsedRequest req, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(Permission)) return Task.FromResult(Reject(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", Permission))));
        var errors = ValidateContext(ctx);
        if (req.RateIds is null or { Count: 0 }) errors.Add(Error("SCHEMA.REQUIRED_FIELD_MISSING", "P1", "/rate_ids"));
        else if (req.RateIds.Count > RateUsageRules.MaxRateIds) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/rate_ids", ("reason", $"at most {RateUsageRules.MaxRateIds} ids")));
        else if (req.RateIds.Any(i => i == Guid.Empty)) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/rate_ids", ("reason", "an id is empty")));
        if (string.IsNullOrWhiteSpace(req.PurposeRef) || req.PurposeRef.Length > 100) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/purpose_ref", ("reason", "required, at most 100 characters")));
        if (errors.Count > 0) return Task.FromResult(CommandResult<MarkedRatesResult>.Rejected(errors));
        byte[] fingerprint = RateUsageRules.Fingerprint(req);
        Guid[] ids = req.RateIds!.Distinct().Order().ToArray();

        return RunAsync(async () =>
        {
            await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Config, ct: ct);
            CommandSupport.EarlierCommand? earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
            if (earlier is not null)
            {
                if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint)) return Reject(Error("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
                var marked = new List<Guid>();
                await using var cmd = tx.Command("select entity_id from core.master_change_log where tenant_id = @t and command_log_id = @l order by entity_id");
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("l", earlier.CommandLogId);
                await using (var r = await cmd.ExecuteReaderAsync(ct)) while (await r.ReadAsync(ct)) marked.Add(Guid.Parse(r.GetString(0)));
                return CommandResult<MarkedRatesResult>.Ok(new MarkedRatesResult(req.PurposeRef, marked, ids.Except(marked).ToList()), earlier.CommandLogId, replay: true);
            }

            // lock the rows in id order, then every named id must exist
            var state = new Dictionary<Guid, bool>();
            await using (var cmd = tx.Command("select rate_id, first_used_at is not null from core.exchange_rate where tenant_id = @t and rate_id = any(@ids) order by rate_id for update"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("ids", ids);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) state[r.GetGuid(0)] = r.GetBoolean(1);
            }
            var unknown = ids.Where(i => !state.ContainsKey(i)).ToList();
            if (unknown.Count > 0)
                return CommandResult<MarkedRatesResult>.Rejected(unknown.Select(i => Error("SCHEMA.INVALID_VALUE", "P2", "/rate_ids", ("reason", "unknown rate"), ("rate_id", i))).ToList());

            var toMark = ids.Where(i => !state[i]).ToList();
            Guid changeSet = Guid.NewGuid(), logId = Guid.NewGuid();
            DateTime now = DateTime.UtcNow;
            if (toMark.Count > 0)
            {
                await using var cmd = tx.Command("update core.exchange_rate set first_used_at = @now where tenant_id = @t and rate_id = any(@ids) and first_used_at is null");
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("ids", toMark.ToArray()); cmd.Parameters.AddWithValue("now", now);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            foreach (Guid id in toMark)
                await LogAsync(tx, tenantId, ctx, logId, changeSet, "EXCHANGE_RATE", id.ToString("D"), "UPDATE", "first_used_at", false, null, now.ToString("O"), null);
            await CommandSupport.WriteLogAsync(tx, tenantId, null, ctx, "exchange-rates:mark-used", fingerprint, "EXCHANGE_RATE", ids[0], logId, ct);
            foreach (Guid id in toMark)
                await EventAsync(tx, tenantId, ctx, "master.changed", "EXCHANGE_RATE", id.ToString("D"), "CHANGED", ["first_used_at"], ct);
            await tx.CommitAsync(ct);
            return CommandResult<MarkedRatesResult>.Ok(new MarkedRatesResult(req.PurposeRef, toMark, ids.Except(toMark).ToList()), logId);
        });
    }

    private static async Task<CommandResult<MarkedRatesResult>> RunAsync(Func<Task<CommandResult<MarkedRatesResult>>> once)
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
                return CommandResult<MarkedRatesResult>.Failed(new CommandError("SYSTEM.LOCK_TIMEOUT", "P7", "", null, true));
            }
        }
    }
}
