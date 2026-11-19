using System.Globalization;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;
using Npgsql;
using NpgsqlTypes;
using static FinanceCore.Data.MasterSupport;

namespace FinanceCore.Data;

/// <summary>The account that a role resolves to at a date, and the determination it came from (10, 3.9).</summary>
public sealed record DeterminationMatch(Guid AccountId, Guid DeterminationId);

/// <summary>
/// The resolution of an account determination (10, 3.9, the algorithm of tax determination, 07, 8.4): the versions that hold at the date and whose keys are null or equal, the most
/// specific key set first, then the lowest priority, then the lowest determination id. A version holds until its valid_to or the day before the next version of its determination
/// starts. DROPSHIP_CLEARING falls back to the rules of GR_IR when it has none of its own (11, 5.3). Null is no match: the caller rejects with INVENTORY.ACCOUNT_DETERMINATION_MISSING.
/// </summary>
public static class AccountDeterminationResolver
{
    public static async Task<DeterminationMatch?> ResolveAsync(
        TenantTransaction tx, Guid tenantId, Guid companyId, string role, Guid? itemGroup, Guid? warehouse, Guid? partner, Guid? item, DateOnly date, CancellationToken ct)
    {
        DeterminationMatch? match = await ResolveOnceAsync(tx, tenantId, companyId, role, itemGroup, warehouse, partner, item, date, ct);
        return match is null && role == "DROPSHIP_CLEARING"
            ? await ResolveOnceAsync(tx, tenantId, companyId, "GR_IR", itemGroup, warehouse, partner, item, date, ct)
            : match;
    }

    private static async Task<DeterminationMatch?> ResolveOnceAsync(
        TenantTransaction tx, Guid tenantId, Guid companyId, string role, Guid? itemGroup, Guid? warehouse, Guid? partner, Guid? item, DateOnly date, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "with eff as (select determination_id, account_id, priority, item_group_id, warehouse_id, partner_id, item_id, valid_from, " +
            "  least(coalesce(valid_to, 'infinity'::date), coalesce((lead(valid_from) over (partition by determination_id order by valid_from)) - 1, 'infinity'::date)) as eff_to " +
            "  from core.account_determination where tenant_id = @t and company_id = @c and role_code = @r) " +
            "select account_id, determination_id from eff where valid_from <= @d and eff_to >= @d " +
            "and (item_group_id is null or item_group_id = @g) and (warehouse_id is null or warehouse_id = @w) and (partner_id is null or partner_id = @p) and (item_id is null or item_id = @i) " +
            "order by ((item_group_id is not null)::int + (warehouse_id is not null)::int + (partner_id is not null)::int + (item_id is not null)::int) desc, priority, determination_id limit 1");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("r", role); cmd.Parameters.AddWithValue("d", date);
        cmd.Parameters.Add(new NpgsqlParameter("g", NpgsqlDbType.Uuid) { Value = (object?)itemGroup ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("w", NpgsqlDbType.Uuid) { Value = (object?)warehouse ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Uuid) { Value = (object?)partner ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("i", NpgsqlDbType.Uuid) { Value = (object?)item ?? DBNull.Value });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new DeterminationMatch(r.GetGuid(0), r.GetGuid(1)) : null;
    }
}

/// <summary>
/// account-determinations:create and :create-version (10, 3.9, permission write.config): a determination is a determination_id with insert-only versions, each version a row
/// with the role, the keys (null means any), the validity, the priority and the account. A version needs a valid_from after the latest of its determination. The account of the
/// role must have the open-item mode that the role needs (CONFIG.ROLE_ACCOUNT_RULE_VIOLATED), and two determinations with equal keys and priority must not overlap in time
/// (CONFIG.OVERLAPPING_RULE). Built choices are in 07, 13.2t.
/// </summary>
public sealed class AccountDeterminationCommands(NpgsqlDataSource source)
{
    public const string Permission = MasterRules.ConfigPermission;

    public Task<CommandResult<AccountDeterminationResult>> CreateAsync(Guid tenantId, CommandContext ctx, CreateAccountDeterminationRequest req, CancellationToken ct = default) =>
        WriteAsync(tenantId, ctx, "account-determinations:create", null, req.CompanyId, req.Fields, AccountDeterminationRules.Fingerprint(req), ct);

    public Task<CommandResult<AccountDeterminationResult>> CreateVersionAsync(Guid tenantId, CommandContext ctx, CreateAccountDeterminationVersionRequest req, CancellationToken ct = default) =>
        WriteAsync(tenantId, ctx, "account-determinations:create-version", req.DeterminationId, null, req.Fields, AccountDeterminationRules.Fingerprint(req), ct);

    private static CommandResult<AccountDeterminationResult> Reject(CommandError e) => CommandResult<AccountDeterminationResult>.Rejected([e]);

    private Task<CommandResult<AccountDeterminationResult>> WriteAsync(
        Guid tenantId, CommandContext ctx, string command, Guid? existing, Guid? newCompany, IReadOnlyDictionary<string, object?> fields, byte[] fingerprint, CancellationToken ct)
    {
        if (!ctx.Permissions.Contains(Permission)) return Task.FromResult(Reject(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", Permission))));
        var errors = ValidateContext(ctx);
        if (existing is { } e && e == Guid.Empty) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/determination_id", ("reason", "required")));
        if (newCompany is { } c && c == Guid.Empty) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/company_id", ("reason", "required")));
        Require(fields, "/fields", errors, "role_code", "valid_from", "priority", "account_id");
        var patches = Validate(fields, AccountDeterminationFields.Version, "/fields", errors);
        if (errors.Count > 0) return Task.FromResult(CommandResult<AccountDeterminationResult>.Rejected(errors));

        return RunAsync(async () =>
        {
            await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Config, ct: ct);
            CommandSupport.EarlierCommand? earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
            if (earlier is not null)
            {
                if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint)) return Reject(Error("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
                AccountDeterminationResult? replay = await LoadReplayAsync(tx, tenantId, earlier.CommandLogId);
                return replay is null ? CommandResult<AccountDeterminationResult>.Failed(Error("SYSTEM.REPLAY_RESULT_MISSING", "P1", ""))
                    : CommandResult<AccountDeterminationResult>.Ok(replay, earlier.CommandLogId, replay: true);
            }

            Guid company, id;
            if (existing is { } known)
            {
                await using var cmd = tx.Command("select company_id from core.account_determination where tenant_id = @t and determination_id = @x limit 1");
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("x", known);
                if (await cmd.ExecuteScalarAsync(ct) is not Guid found) return Reject(Error("CONFIG.INVALID_REFERENCE", "P2", "/determination_id", ("determination_id", known)));
                company = found; id = known;
            }
            else
            {
                company = newCompany!.Value; id = Guid.NewGuid();
                if (!await ExistsAsync(tx, "select 1 from core.company where tenant_id = @t and company_id = @x", tenantId, ("x", company)))
                    return Reject(Error("CONFIG.INVALID_REFERENCE", "P2", "/company_id", ("company_id", company)));
            }

            var f = patches.ToDictionary(p => p.Field.Name);
            string? Get(string name) => f.TryGetValue(name, out Patch? p) ? p.Text : null;
            var problems = new List<CommandError>();
            string role = Get("role_code")!;
            DateOnly validFrom = DateOnly.Parse(Get("valid_from")!, CultureInfo.InvariantCulture);
            DateOnly? validTo = Get("valid_to") is { } to ? DateOnly.Parse(to, CultureInfo.InvariantCulture) : null;
            if (validTo is { } end && end < validFrom) problems.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/fields/valid_to", ("reason", "not before valid_from")));

            if (existing is not null)
            {
                await using var cmd = tx.Command("select max(valid_from) from core.account_determination where tenant_id = @t and determination_id = @x");
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("x", id);
                if (await cmd.ExecuteScalarAsync(ct) is DateTime latest && validFrom <= DateOnly.FromDateTime(latest))
                    problems.Add(Error("CONFIG.VALID_FROM_NOT_LATER", "P2", "/fields/valid_from", ("valid_from", validFrom), ("latest_valid_from", DateOnly.FromDateTime(latest))));
            }

            // the references: the account of the company with the open-item mode of the role, the keys of the tenant and, for a warehouse, of the company
            Guid account = Guid.Parse(Get("account_id")!);
            string? mode = null;
            await using (var cmd = tx.Command("select open_item_mode from core.gl_account where tenant_id = @t and company_id = @c and account_id = @x"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", company); cmd.Parameters.AddWithValue("x", account);
                mode = await cmd.ExecuteScalarAsync(ct) as string;
            }
            if (mode is null) problems.Add(Error("CONFIG.INVALID_REFERENCE", "P2", "/fields/account_id", ("account_id", account)));
            else if (mode != AccountDeterminationFields.RequiredOpenItemMode(role))
                problems.Add(Error("CONFIG.ROLE_ACCOUNT_RULE_VIOLATED", "P2", "/fields/account_id", ("rule", "OPEN_ITEM_MODE"), ("role_code", role), ("required", AccountDeterminationFields.RequiredOpenItemMode(role)), ("actual", mode)));
            foreach (var (name, sql) in new[]
            {
                ("item_group_id", "select 1 from core.item_group where tenant_id = @t and item_group_id = @x"),
                ("partner_id", "select 1 from core.business_partner where tenant_id = @t and partner_id = @x"),
                ("item_id", "select 1 from core.item where tenant_id = @t and item_id = @x"),
                ("warehouse_id", "select 1 from core.warehouse where tenant_id = @t and company_id = @c and warehouse_id = @x")
            })
                if (Get(name) is { } v && !await ExistsAsync(tx, sql, tenantId, ("c", company), ("x", Guid.Parse(v))))
                    problems.Add(Error("CONFIG.INVALID_REFERENCE", "P2", "/fields/" + name, (name, v)));

            // equal keys and priority over an overlapping time are rejected (10, 3.9). A version holds until the next version of its determination starts.
            if (problems.Count == 0)
            {
                await using var cmd = tx.Command(
                    "with eff as (select determination_id, valid_from, priority, role_code, item_group_id, warehouse_id, partner_id, item_id, " +
                    "  least(coalesce(valid_to, 'infinity'::date), coalesce((lead(valid_from) over (partition by determination_id order by valid_from)) - 1, 'infinity'::date)) as eff_to " +
                    "  from core.account_determination where tenant_id = @t and company_id = @c and determination_id <> @r) " +
                    "select determination_id from eff where priority = @p and role_code = @k0 and item_group_id is not distinct from @k1 and warehouse_id is not distinct from @k2 " +
                    "and partner_id is not distinct from @k3 and item_id is not distinct from @k4 and valid_from <= coalesce(@to, 'infinity'::date) and eff_to >= @from limit 1");
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", company); cmd.Parameters.AddWithValue("r", id);
                cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Smallint) { Value = f["priority"].Value });
                cmd.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.Date) { Value = validFrom });
                cmd.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.Date) { Value = validTo is { } d ? d : DBNull.Value });
                cmd.Parameters.AddWithValue("k0", role);
                for (int k = 1; k < AccountDeterminationFields.KeyColumns.Length; k++)
                    cmd.Parameters.Add(new NpgsqlParameter("k" + k, NpgsqlDbType.Uuid) { Value = f.TryGetValue(AccountDeterminationFields.KeyColumns[k], out Patch? p) ? p.Value ?? DBNull.Value : DBNull.Value });
                if (await cmd.ExecuteScalarAsync(ct) is Guid other)
                    problems.Add(Error("CONFIG.OVERLAPPING_RULE", "P2", "/fields/priority", ("determination_id", other), ("priority", Get("priority"))));
            }
            if (problems.Count > 0) return CommandResult<AccountDeterminationResult>.Rejected(problems);

            var cols = new List<string> { "tenant_id", "company_id", "determination_id", "created_by" };
            var vals = new List<string> { "@t", "@c", "@r", "@by" };
            await using (var insert = tx.Command(""))
            {
                insert.Parameters.AddWithValue("t", tenantId); insert.Parameters.AddWithValue("c", company); insert.Parameters.AddWithValue("r", id); insert.Parameters.AddWithValue("by", ctx.Actor);
                for (int p = 0; p < patches.Count; p++) { cols.Add(patches[p].Field.Name); vals.Add($"@p{p}"); AddParam(insert, $"p{p}", patches[p]); }
                insert.CommandText = $"insert into core.account_determination ({string.Join(", ", cols)}) values ({string.Join(", ", vals)})";
                await insert.ExecuteNonQueryAsync(ct);
            }

            Guid changeSet = Guid.NewGuid(), logId = Guid.NewGuid();
            string key = $"{company:D}/{id:D}/{validFrom:yyyy-MM-dd}";
            await LogAsync(tx, tenantId, ctx, logId, changeSet, "ACCOUNT_DETERMINATION", key, "CREATE", null, false, null, null, null);
            await CommandSupport.WriteLogAsync(tx, tenantId, company, ctx, command, fingerprint, "ACCOUNT_DETERMINATION", id, logId, ct);
            await EventAsync(tx, tenantId, ctx, "config.version_created", "ACCOUNT_DETERMINATION", key, "CREATED", patches.Select(p => p.Field.Name).ToList(), ct);
            await tx.CommitAsync(ct);
            return CommandResult<AccountDeterminationResult>.Ok(new AccountDeterminationResult(id, validFrom), logId);
        });
    }

    /// <summary>The version an earlier command wrote: the key is in its master_change_log row.</summary>
    private static async Task<AccountDeterminationResult?> LoadReplayAsync(TenantTransaction tx, Guid tenantId, Guid commandLogId)
    {
        await using var cmd = tx.Command("select entity_id from core.master_change_log where tenant_id = @t and command_log_id = @l limit 1");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("l", commandLogId);
        if (await cmd.ExecuteScalarAsync() is not string key) return null;
        string[] parts = key.Split('/');
        return new AccountDeterminationResult(Guid.Parse(parts[1]), DateOnly.ParseExact(parts[2], "yyyy-MM-dd", CultureInfo.InvariantCulture));
    }
}
