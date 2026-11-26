using System.Globalization;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;
using Npgsql;
using static FinanceCore.Data.MasterSupport;

namespace FinanceCore.Data;

/// <summary>
/// account-group-mappings:create (07, 6.4, 23, 4, ADR-227; permission write.config): maps an account of a company to a group account from valid_from. The company must be a member of a group at
/// valid_from (CONFIG.NOT_A_GROUP_MEMBER) and the group account must belong to that group (CONFIG.GROUP_ACCOUNT_OTHER_GROUP). The open mapping of the account ends on valid_from, so one group
/// account holds at a time; valid_from must be later than the latest mapping of the account (CONFIG.VALID_FROM_NOT_LATER). A group change remaps nothing.
/// </summary>
public sealed class AccountGroupMappingCommands(NpgsqlDataSource source)
{
    public const string Permission = MasterRules.ConfigPermission;
    public const string Resource = "account-group-mappings";

    public static readonly MasterReadSpec ReadSpec = new(Resource, "account_group_mapping", "account_id", ["company_id", "account_id", "group_account_id"], "company_id, account_id, valid_from", Versioned: true);

    private static CommandResult<AccountGroupMappingResult> Reject(CommandError e) => CommandResult<AccountGroupMappingResult>.Rejected([e]);

    public Task<CommandResult<AccountGroupMappingResult>> CreateAsync(Guid tenantId, CommandContext ctx, CreateAccountGroupMappingRequest req, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(Permission)) return Task.FromResult(Reject(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", Permission))));
        var errors = ValidateContext(ctx);
        if (req.CompanyId == Guid.Empty) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/company_id", ("reason", "required")));
        if (req.AccountId == Guid.Empty) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/account_id", ("reason", "required")));
        if (req.GroupAccountId == Guid.Empty) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/group_account_id", ("reason", "required")));
        if (errors.Count > 0) return Task.FromResult(CommandResult<AccountGroupMappingResult>.Rejected(errors));
        byte[] fingerprint = AccountGroupMappingRules.Fingerprint(req);

        return RunAsync(async () =>
        {
            await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Config, ct: ct);
            CommandSupport.EarlierCommand? earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
            if (earlier is not null)
            {
                if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint)) return Reject(Error("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
                return CommandResult<AccountGroupMappingResult>.Ok(new AccountGroupMappingResult(req.CompanyId, req.AccountId, req.ValidFrom, req.GroupAccountId, null), earlier.CommandLogId, replay: true);
            }
            if (!await ExistsAsync(tx, "select 1 from core.gl_account where tenant_id = @t and company_id = @c and account_id = @a", tenantId, ("c", req.CompanyId), ("a", req.AccountId)))
                return Reject(Error("CONFIG.INVALID_REFERENCE", "P2", "/account_id", ("account_id", req.AccountId)));
            Guid? groupOfAccount = null; bool active = false;
            await using (var cmd = tx.Command("select group_id, active from core.group_chart_account where tenant_id = @t and group_account_id = @g"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("g", req.GroupAccountId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct)) { groupOfAccount = r.GetGuid(0); active = r.GetBoolean(1); }
            }
            if (groupOfAccount is null || !active) return Reject(Error("CONFIG.INVALID_REFERENCE", "P2", "/group_account_id", ("group_account_id", req.GroupAccountId)));

            Guid? group;
            await using (var cmd = tx.Command("select group_id from core.group_membership where tenant_id = @t and company_id = @c and valid_from <= @d and (valid_to is null or valid_to > @d)"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("d", req.ValidFrom);
                group = await cmd.ExecuteScalarAsync(ct) as Guid?;
            }
            if (group is null) return Reject(Error("CONFIG.NOT_A_GROUP_MEMBER", "P2", "/company_id", ("company_id", req.CompanyId), ("valid_from", req.ValidFrom)));
            if (group != groupOfAccount) return Reject(Error("CONFIG.GROUP_ACCOUNT_OTHER_GROUP", "P2", "/group_account_id", ("group_account_id", req.GroupAccountId), ("group_id", group)));

            DateOnly? openFrom = null;
            await using (var cmd = tx.Command(
                "select valid_from, valid_to from core.account_group_mapping where tenant_id = @t and company_id = @c and account_id = @a order by valid_from desc limit 1 for update"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("a", req.AccountId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    DateOnly latest = DateOnly.FromDateTime(r.GetDateTime(0));
                    if (req.ValidFrom <= latest) return Reject(Error("CONFIG.VALID_FROM_NOT_LATER", "P2", "/valid_from", ("valid_from", req.ValidFrom), ("latest_valid_from", latest)));
                    if (r.IsDBNull(1)) openFrom = latest;
                }
            }
            if (openFrom is { } of)
            {
                await using var close = tx.Command("update core.account_group_mapping set valid_to = @d where tenant_id = @t and company_id = @c and account_id = @a and valid_from = @f and valid_to is null");
                close.Parameters.AddWithValue("t", tenantId); close.Parameters.AddWithValue("c", req.CompanyId); close.Parameters.AddWithValue("a", req.AccountId);
                close.Parameters.AddWithValue("f", of); close.Parameters.AddWithValue("d", req.ValidFrom);
                await close.ExecuteNonQueryAsync(ct);
            }
            await using (var insert = tx.Command(
                "insert into core.account_group_mapping (tenant_id, company_id, account_id, valid_from, group_account_id, created_by) values (@t, @c, @a, @d, @g, @by)"))
            {
                insert.Parameters.AddWithValue("t", tenantId); insert.Parameters.AddWithValue("c", req.CompanyId); insert.Parameters.AddWithValue("a", req.AccountId);
                insert.Parameters.AddWithValue("d", req.ValidFrom); insert.Parameters.AddWithValue("g", req.GroupAccountId); insert.Parameters.AddWithValue("by", ctx.Actor);
                await insert.ExecuteNonQueryAsync(ct);
            }
            Guid changeSet = Guid.NewGuid(), logId = Guid.NewGuid();
            string entityKey = string.Create(CultureInfo.InvariantCulture, $"{req.CompanyId:D}/{req.AccountId:D}/{req.ValidFrom:yyyy-MM-dd}");
            await LogAsync(tx, tenantId, ctx, logId, changeSet, "ACCOUNT_GROUP_MAPPING", entityKey, "CREATE", null, false, null, null, null);
            await CommandSupport.WriteLogAsync(tx, tenantId, req.CompanyId, ctx, "account-group-mappings:create", fingerprint, "ACCOUNT_GROUP_MAPPING", req.AccountId, logId, ct);
            await EventAsync(tx, tenantId, ctx, "config.version_created", "ACCOUNT_GROUP_MAPPING", entityKey, "CREATED", ["group_account_id", "valid_from"], ct);
            await tx.CommitAsync(ct);
            return CommandResult<AccountGroupMappingResult>.Ok(new AccountGroupMappingResult(req.CompanyId, req.AccountId, req.ValidFrom, req.GroupAccountId, openFrom), logId);
        });
    }

    private static async Task<CommandResult<AccountGroupMappingResult>> RunAsync(Func<Task<CommandResult<AccountGroupMappingResult>>> once)
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
                return CommandResult<AccountGroupMappingResult>.Failed(new CommandError("SYSTEM.LOCK_TIMEOUT", "P7", "", null, true));
            }
        }
    }
}
