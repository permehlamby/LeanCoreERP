using System.Globalization;
using System.Text.Json;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Roles;
using Npgsql;
using NpgsqlTypes;

namespace FinanceCore.Data;

/// <summary>
/// account-role-assignments:create (07, 6.3, 11.1): writes one version row of the role table. The account must satisfy the rules
/// of the role (CONFIG.ROLE_ACCOUNT_RULE_VIOLATED with the rule in the parameters). A change of a role is a new row with a later
/// effective_from, never an update. Not in this slice: VAT_SETTLEMENT (it needs company_vat_registration, 08, 4.1), the system
/// setting of tax_account, CONFIG.VALID_FROM_IN_CLOSED_PERIOD, master_change_log.
/// </summary>
public sealed class AccountRoleAssigner(NpgsqlDataSource source)
{
    public const string CommandName = "account-role-assignments:create";
    public const string Permission = "write.config";

    public async Task<CommandResult<AssignedAccountRole>> AssignAsync(
        Guid tenantId, CommandContext ctx, AssignAccountRoleRequest req, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(Permission))
            return Reject(new CommandError("AUTH.PERMISSION_DENIED", "P0", "", new Dictionary<string, object?> { ["permission"] = Permission }));

        var shape = new List<CommandError>();
        void Invalid(string path, string reason) =>
            shape.Add(new CommandError("SCHEMA.INVALID_VALUE", "P1", path, new Dictionary<string, object?> { ["reason"] = reason }));
        if (string.IsNullOrWhiteSpace(ctx.IdempotencyKey) || ctx.IdempotencyKey.Length > 100) Invalid("idempotency_key", "required, at most 100 characters");
        if (string.IsNullOrWhiteSpace(ctx.CorrelationId) || ctx.CorrelationId.Length > 100) Invalid("correlation_id", "required, at most 100 characters");
        if (string.IsNullOrWhiteSpace(ctx.Actor) || ctx.Actor.Length > 100) Invalid("actor", "required, at most 100 characters");
        foreach ((string path, string reason) in AccountRoles.CheckShape(req)) Invalid(path, reason);
        if (shape.Count > 0) return CommandResult<AssignedAccountRole>.Rejected(shape);

        // The database keeps microseconds in UTC, and so does the hash and the fingerprint
        if (req.EffectiveFrom is { } given) req = req with { EffectiveFrom = Micro(given.ToUniversalTime()) };

        byte[] fingerprint = AccountRoles.Fingerprint(req);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await AssignOnceAsync(tenantId, ctx, req, fingerprint, ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.TableName == "command_log" && attempt == 0)
            {
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                return CommandResult<AssignedAccountRole>.Failed(new CommandError("SYSTEM.LOCK_TIMEOUT", "P7", "", null, true));
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName == "account_role_assignment_key")
            {
                return Reject(new CommandError("CONFIG.DUPLICATE_CODE", "P2", "effective_from",
                    new Dictionary<string, object?> { ["role_code"] = req.RoleCode }));
            }
        }
    }

    private async Task<CommandResult<AssignedAccountRole>> AssignOnceAsync(
        Guid tenantId, CommandContext ctx, AssignAccountRoleRequest req, byte[] fingerprint, CancellationToken ct)
    {
        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Config, ct: ct);

        CommandSupport.EarlierCommand? earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
        if (earlier is not null)
        {
            if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint))
                return Reject(new CommandError("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
            AssignedAccountRole? replay = earlier.EntityId is null ? null : await LoadAsync(tx, tenantId, earlier.EntityId.Value, ct);
            return replay is null
                ? CommandResult<AssignedAccountRole>.Failed(new CommandError("SYSTEM.REPLAY_RESULT_MISSING", "P1", ""))
                : CommandResult<AssignedAccountRole>.Ok(replay, earlier.CommandLogId, replay: true);
        }

        RoleDefinition role = AccountRoles.Find(req.RoleCode)!;
        var errors = new List<CommandError>();
        CommandError Err(string code, string path, params (string Key, object? Value)[] p) =>
            new(code, "P2", path, p.Length == 0 ? null : p.ToDictionary(x => x.Key, x => x.Value));

        // The company row is locked, so two assignments for one company are serialised and the later-version check below is exact.
        await using (var cmd = tx.Command("select 1 from core.company where tenant_id = @t and company_id = @c for update"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", req.CompanyId);
            if (await cmd.ExecuteScalarAsync(ct) is null) return Reject(Err("COMPANY.NOT_FOUND", "company_id"));
        }

        if (role.PerVatRegistration)
            errors.Add(Err("LIMIT.VAT_REGISTRATION_NOT_BUILT", "role_code", ("role_code", role.Code)));

        if (req.CurrencyCode is not null && !await ExistsAsync(tx,
                "select 1 from core.tenant_currency where tenant_id = @t and currency_code = @x and active", tenantId, req.CurrencyCode, ct))
            errors.Add(Err("CONFIG.CURRENCY_NOT_ENABLED", "currency_code", ("currency", req.CurrencyCode)));

        if (req.CounterpartCompanyId is { } cp && !await ExistsAsync(tx,
                "select 1 from core.company where tenant_id = @t and company_id = @x", tenantId, cp, ct))
            errors.Add(Err("CONFIG.INVALID_REFERENCE", "counterpart_company_id", ("counterpart_company_id", cp)));

        RoleAccount? account = await ReadAccountAsync(tx, tenantId, req.CompanyId, req.AccountId, ct);
        if (account is null)
            errors.Add(Err("CONFIG.INVALID_REFERENCE", "account_id", ("account_id", req.AccountId)));
        else
        {
            var others = new List<string>();
            await using (var cmd = tx.Command(
                "select distinct role_code from core.account_role_assignment where tenant_id = @t and company_id = @c and account_id = @a"))
            {
                cmd.Parameters.AddWithValue("t", tenantId);
                cmd.Parameters.AddWithValue("c", req.CompanyId);
                cmd.Parameters.AddWithValue("a", req.AccountId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) others.Add(r.GetString(0));
            }
            foreach (RoleViolation violation in AccountRoles.CheckAccount(role, req.CurrencyCode, account, others))
                errors.Add(violation.Rule is null
                    ? Err(violation.Code, "account_id", ("role_code", role.Code), ("account_id", req.AccountId))
                    : Err(violation.Code, "account_id", ("role_code", role.Code), ("account_id", req.AccountId), ("rule", violation.Rule)));
        }

        DateTime now = DateTime.UtcNow;
        DateTime effectiveFrom = req.EffectiveFrom ?? Micro(now);
        if (await LatestVersionAsync(tx, tenantId, req, ct) is { } latest && latest >= effectiveFrom)
            errors.Add(Err("CONFIG.VALID_FROM_NOT_LATER", "effective_from",
                ("effective_from", effectiveFrom), ("latest_effective_from", latest)));

        if (errors.Count > 0) return CommandResult<AssignedAccountRole>.Rejected(errors);

        Guid assignmentId = Guid.NewGuid();
        await using (var cmd = tx.Command(
            "insert into core.account_role_assignment (tenant_id, company_id, assignment_id, role_code, currency_code, " +
            "counterpart_company_id, vat_registration_id, account_id, effective_from) " +
            "values (@t, @c, @id, @r, @cur, @cp, @v, @a, @from)"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", req.CompanyId);
            cmd.Parameters.AddWithValue("id", assignmentId);
            cmd.Parameters.AddWithValue("r", req.RoleCode);
            cmd.Parameters.Add(new NpgsqlParameter("cur", NpgsqlDbType.Text) { Value = (object?)req.CurrencyCode ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("cp", NpgsqlDbType.Uuid) { Value = (object?)req.CounterpartCompanyId ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Uuid) { Value = (object?)req.VatRegistrationId ?? DBNull.Value });
            cmd.Parameters.AddWithValue("a", req.AccountId);
            cmd.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = effectiveFrom });
            await cmd.ExecuteNonQueryAsync(ct);
        }

        Guid logId = await CommandSupport.WriteLogAsync(tx, tenantId, req.CompanyId, ctx, CommandName, fingerprint,
            "ACCOUNT_ROLE_ASSIGNMENT", assignmentId, ct);
        await CommandSupport.WriteOutboxAsync(tx, tenantId, req.CompanyId, ctx, "config.version_created", "ACCOUNT_ROLE_ASSIGNMENT",
            $"{req.CompanyId:D}/{assignmentId:D}", "CREATED", Micro(now), isAudit: true, null,
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["entity_type"] = "ACCOUNT_ROLE_ASSIGNMENT",
                ["valid_from"] = effectiveFrom.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture)
            }), ct);

        await tx.CommitAsync(ct);
        return CommandResult<AssignedAccountRole>.Ok(
            new AssignedAccountRole(assignmentId, req.CompanyId, req.RoleCode, req.CurrencyCode, req.CounterpartCompanyId,
                req.VatRegistrationId, req.AccountId, effectiveFrom), logId);
    }

    private static DateTime Micro(DateTime t) => new(t.Ticks - t.Ticks % 10, DateTimeKind.Utc);

    private static async Task<bool> ExistsAsync(TenantTransaction tx, string sql, Guid tenantId, object value, CancellationToken ct)
    {
        await using var cmd = tx.Command(sql);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("x", value);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task<RoleAccount?> ReadAccountAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid accountId, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select account_type, open_item_mode, currency_code, revalue_flag, tax_account from core.gl_account " +
            "where tenant_id = @t and company_id = @c and account_id = @a");
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", companyId);
        cmd.Parameters.AddWithValue("a", accountId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new RoleAccount(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetBoolean(3), r.GetBoolean(4))
            : null;
    }

    /// <summary>The newest version of the same key (role, currency, counterpart, registration).</summary>
    private static async Task<DateTime?> LatestVersionAsync(TenantTransaction tx, Guid tenantId, AssignAccountRoleRequest req, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select max(effective_from) from core.account_role_assignment " +
            "where tenant_id = @t and company_id = @c and role_code = @r " +
            "and currency_code is not distinct from @cur and counterpart_company_id is not distinct from @cp " +
            "and vat_registration_id is not distinct from @v");
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", req.CompanyId);
        cmd.Parameters.AddWithValue("r", req.RoleCode);
        cmd.Parameters.Add(new NpgsqlParameter("cur", NpgsqlDbType.Text) { Value = (object?)req.CurrencyCode ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("cp", NpgsqlDbType.Uuid) { Value = (object?)req.CounterpartCompanyId ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Uuid) { Value = (object?)req.VatRegistrationId ?? DBNull.Value });
        object? value = await cmd.ExecuteScalarAsync(ct);
        return value is DateTime d ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null;
    }

    private static async Task<AssignedAccountRole?> LoadAsync(TenantTransaction tx, Guid tenantId, Guid assignmentId, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select company_id, role_code, currency_code, counterpart_company_id, vat_registration_id, account_id, effective_from " +
            "from core.account_role_assignment where tenant_id = @t and assignment_id = @id");
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("id", assignmentId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new AssignedAccountRole(assignmentId, r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetGuid(3), r.IsDBNull(4) ? null : r.GetGuid(4), r.GetGuid(5),
                DateTime.SpecifyKind(r.GetDateTime(6), DateTimeKind.Utc))
            : null;
    }

    private static CommandResult<AssignedAccountRole> Reject(CommandError error) => CommandResult<AssignedAccountRole>.Rejected([error]);
}
