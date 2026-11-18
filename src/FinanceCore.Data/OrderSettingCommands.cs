using System.Globalization;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;
using Npgsql;
using NpgsqlTypes;
using static FinanceCore.Data.MasterSupport;

namespace FinanceCore.Data;

/// <summary>
/// company-order-settings:create-version (11, 3.1, permission write.config): a new version of the order settings of a company with a later effective_from. A field that is absent takes
/// its default (THREE_WAY, tolerances 0, no auto-reserve, CORE_BUILDS, no invoice before receipt). The rows are insert-only and the version in force at an order date applies.
/// </summary>
public sealed class OrderSettingCommands(NpgsqlDataSource source)
{
    public const string Permission = MasterRules.ConfigPermission;

    private static CommandResult<OrderSettingVersionResult> Reject(CommandError e) => CommandResult<OrderSettingVersionResult>.Rejected([e]);

    public Task<CommandResult<OrderSettingVersionResult>> CreateVersionAsync(Guid tenantId, CommandContext ctx, CreateOrderSettingVersionRequest req, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(Permission)) return Task.FromResult(Reject(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", Permission))));
        var errors = ValidateContext(ctx);
        if (req.CompanyId == Guid.Empty) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/company_id", ("reason", "required")));
        Require(req.Fields, "/fields", errors, "effective_from");
        var patches = Validate(req.Fields, OrderSettingFields.Version, "/fields", errors);
        if (errors.Count > 0) return Task.FromResult(CommandResult<OrderSettingVersionResult>.Rejected(errors));
        byte[] fingerprint = OrderRules.Fingerprint(req);

        return RunAsync(async () =>
        {
            await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Config, ct: ct);
            CommandSupport.EarlierCommand? earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
            if (earlier is not null)
            {
                if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint)) return Reject(Error("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
                await using var cmd = tx.Command("select entity_id from core.master_change_log where tenant_id = @t and command_log_id = @l limit 1");
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("l", earlier.CommandLogId);
                return await cmd.ExecuteScalarAsync(ct) is string key
                    ? CommandResult<OrderSettingVersionResult>.Ok(new OrderSettingVersionResult(req.CompanyId, DateOnly.ParseExact(key.Split('/')[1], "yyyy-MM-dd", CultureInfo.InvariantCulture)), earlier.CommandLogId, replay: true)
                    : CommandResult<OrderSettingVersionResult>.Failed(Error("SYSTEM.REPLAY_RESULT_MISSING", "P1", ""));
            }
            if (!await ExistsAsync(tx, "select 1 from core.company where tenant_id = @t and company_id = @x", tenantId, ("x", req.CompanyId)))
                return Reject(Error("CONFIG.INVALID_REFERENCE", "P2", "/company_id", ("company_id", req.CompanyId)));
            var f = patches.ToDictionary(p => p.Field.Name);
            DateOnly from = (DateOnly)f["effective_from"].Value!;
            await using (var cmd = tx.Command("select max(effective_from) from core.company_order_setting_version where tenant_id = @t and company_id = @c"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId);
                if (await cmd.ExecuteScalarAsync(ct) is DateTime latest && from <= DateOnly.FromDateTime(latest))
                    return Reject(Error("CONFIG.VALID_FROM_NOT_LATER", "P2", "/fields/effective_from", ("effective_from", from), ("latest_effective_from", DateOnly.FromDateTime(latest))));
            }
            var cols = new List<string> { "tenant_id", "company_id", "created_by" };
            var vals = new List<string> { "@t", "@c", "@by" };
            await using (var insert = tx.Command(""))
            {
                insert.Parameters.AddWithValue("t", tenantId); insert.Parameters.AddWithValue("c", req.CompanyId); insert.Parameters.AddWithValue("by", ctx.Actor);
                for (int i = 0; i < patches.Count; i++) { cols.Add(patches[i].Field.Name); vals.Add($"@p{i}"); AddParam(insert, $"p{i}", patches[i]); }
                insert.CommandText = $"insert into core.company_order_setting_version ({string.Join(", ", cols)}) values ({string.Join(", ", vals)})";
                await insert.ExecuteNonQueryAsync(ct);
            }
            Guid changeSet = Guid.NewGuid(), logId = Guid.NewGuid();
            string entityKey = $"{req.CompanyId:D}/{from:yyyy-MM-dd}";
            await LogAsync(tx, tenantId, ctx, logId, changeSet, "COMPANY_ORDER_SETTING", entityKey, "CREATE", null, false, null, null, null);
            await CommandSupport.WriteLogAsync(tx, tenantId, req.CompanyId, ctx, "company-order-settings:create-version", fingerprint, "COMPANY_ORDER_SETTING", req.CompanyId, logId, ct);
            await EventAsync(tx, tenantId, ctx, "config.version_created", "COMPANY_ORDER_SETTING", entityKey, "CREATED", patches.Select(p => p.Field.Name).ToList(), ct);
            await tx.CommitAsync(ct);
            return CommandResult<OrderSettingVersionResult>.Ok(new OrderSettingVersionResult(req.CompanyId, from), logId);
        });
    }

    private async Task<CommandResult<OrderSettingVersionResult>> RunAsync(Func<Task<CommandResult<OrderSettingVersionResult>>> once)
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
                return CommandResult<OrderSettingVersionResult>.Failed(new CommandError("SYSTEM.LOCK_TIMEOUT", "P7", "", null, true));
            }
        }
    }
}
