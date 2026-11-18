using System.Globalization;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;
using Npgsql;
using NpgsqlTypes;
using static FinanceCore.Data.MasterSupport;

namespace FinanceCore.Data;

public sealed record DeductionRateResult(Guid DeductionKeyId, DateOnly ValidFrom);

/// <summary>
/// The deduction keys of 07, 8.6 (vat-deduction-keys: create, update, deactivate, reactivate) and their rates (vat-deduction-rates:create-version), permission
/// write.config. A key belongs to a VAT registration and is addressed by a surrogate id. A rate version is insert-only and starts after the latest one of its key.
/// Built choices are in 07, 13.2m.
/// </summary>
public sealed class DeductionCommands(NpgsqlDataSource source)
{
    public const string Permission = MasterRules.ConfigPermission;
    private readonly MasterTableCommands _generic = new(source);

    private static readonly MasterSpec KeySpec = new("vat_deduction_key", "deduction_key_id", "VAT_DEDUCTION_KEY", "/deduction_key_id", "vat-deduction-keys", Permission,
        TaxFields.DeductionKeyCreate, TaxFields.DeductionKeyUpdate, ["company_id", "registration_id", "key_code", "kind"], [], CheckKeyAsync,
        async (tx, tenantId, id, row, ct) => await ExistsAsync(tx,
            "select 1 from core.tax_code_version v join core.tax_code c on c.tenant_id = v.tenant_id and c.company_id = v.company_id and c.tax_code_id = v.tax_code_id " +
            "where v.tenant_id = @t and v.company_id = @c and c.vat_registration_id = @r and v.deduction_key = @k and c.active", tenantId,
            ("c", Guid.Parse(row["company_id"]!)), ("r", Guid.Parse(row["registration_id"]!)), ("k", row["key_code"]!))
            ? Error("CONFIG.DEDUCTION_KEY_REFERENCED", "P2", "/deduction_key_id", ("reason", "AN_ACTIVE_CODE_VERSION_NAMES_THE_KEY")) : null);

    private static async Task<List<CommandError>> CheckKeyAsync(MasterCheckInput i)
    {
        var errors = new List<CommandError>();
        if (i.Creating)
        {
            Guid company = Guid.Parse(i.Get("company_id")!), registration = Guid.Parse(i.Get("registration_id")!);
            if (!await ExistsAsync(i.Tx, "select 1 from core.company_vat_registration where tenant_id = @t and company_id = @c and registration_id = @r and active", i.TenantId, ("c", company), ("r", registration)))
                errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", "/fields/registration_id", ("registration_id", registration)));
            else if (await ExistsAsync(i.Tx, "select 1 from core.vat_deduction_key where tenant_id = @t and company_id = @c and registration_id = @r and key_code = @k", i.TenantId,
                         ("c", company), ("r", registration), ("k", i.Get("key_code")!)))
                errors.Add(Error("CONFIG.DUPLICATE_CODE", "P2", "/fields/key_code", ("key_code", i.Get("key_code"))));
        }
        else if (i.Changed("kind") && await ExistsAsync(i.Tx,
                     "select 1 from core.vat_deduction_rate where tenant_id = @t and company_id = @c and registration_id = @r and key_code = @k", i.TenantId,
                     ("c", Guid.Parse(i.Current!["company_id"]!)), ("r", Guid.Parse(i.Current["registration_id"]!)), ("k", i.Current["key_code"]!)))
            errors.Add(Error("CONFIG.DEDUCTION_KEY_REFERENCED", "P2", "/fields/kind", ("reason", "A_RATE_EXISTS")));
        return errors;
    }

    public Task<CommandResult<MasterResult>> CreateKeyAsync(Guid t, CommandContext c, CreateDeductionKeyRequest r, CancellationToken ct = default) =>
        _generic.CreateAsync(t, c, KeySpec, r.Fields, MasterRules.Fingerprint(r), ct);
    public Task<CommandResult<MasterResult>> UpdateKeyAsync(Guid t, CommandContext c, UpdateDeductionKeyRequest r, CancellationToken ct = default) =>
        _generic.UpdateAsync(t, c, KeySpec, r.DeductionKeyId, r.ExpectedVersion, r.Fields, MasterRules.Fingerprint(r), ct);
    public Task<CommandResult<MasterResult>> DeactivateKeyAsync(Guid t, CommandContext c, MasterStatusRequest r, CancellationToken ct = default) =>
        _generic.StatusAsync(t, c, KeySpec, r, true, ct);
    public Task<CommandResult<MasterResult>> ReactivateKeyAsync(Guid t, CommandContext c, MasterStatusRequest r, CancellationToken ct = default) =>
        _generic.StatusAsync(t, c, KeySpec, r, false, ct);

    // ---------------------------------------------------------------------------------------------------------
    public Task<CommandResult<DeductionRateResult>> CreateRateAsync(Guid tenantId, CommandContext ctx, CreateDeductionRateRequest req, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(Permission)) return Task.FromResult(Rej(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", Permission))));
        var errors = ValidateContext(ctx);
        if (req.DeductionKeyId == Guid.Empty) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/deduction_key_id", ("reason", "required")));
        Require(req.Fields, "/fields", errors, "valid_from", "rate_pct", "rate_status");
        var patches = Validate(req.Fields, TaxFields.DeductionRate, "/fields", errors);
        if (errors.Count > 0) return Task.FromResult(CommandResult<DeductionRateResult>.Rejected(errors));
        byte[] fingerprint = MasterRules.Fingerprint(req);

        return RunAsync(async () =>
        {
            await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Config, ct: ct);
            CommandSupport.EarlierCommand? earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
            if (earlier is not null)
            {
                if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint)) return Rej(Error("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
                string? entity = null;
                await using (var cmd = tx.Command("select entity_id from core.master_change_log where tenant_id = @t and command_log_id = @l limit 1"))
                {
                    cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("l", earlier.CommandLogId);
                    entity = await cmd.ExecuteScalarAsync(ct) as string;
                }
                if (entity is null) return CommandResult<DeductionRateResult>.Failed(Error("SYSTEM.REPLAY_RESULT_MISSING", "P1", ""));
                string[] parts = entity.Split('/');
                return CommandResult<DeductionRateResult>.Ok(new DeductionRateResult(Guid.Parse(parts[0]), DateOnly.ParseExact(parts[1], "yyyy-MM-dd", CultureInfo.InvariantCulture)), earlier.CommandLogId, replay: true);
            }

            Guid company, registration; string keyCode; bool active;
            await using (var cmd = tx.Command("select company_id, registration_id, key_code, active from core.vat_deduction_key where tenant_id = @t and deduction_key_id = @k"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("k", req.DeductionKeyId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) return Rej(Error("CONFIG.INVALID_REFERENCE", "P2", "/deduction_key_id", ("deduction_key_id", req.DeductionKeyId)));
                company = r.GetGuid(0); registration = r.GetGuid(1); keyCode = r.GetString(2); active = r.GetBoolean(3);
            }
            var f = patches.ToDictionary(p => p.Field.Name, p => p.Text);
            DateOnly validFrom = DateOnly.Parse(f["valid_from"]!, CultureInfo.InvariantCulture);
            var problems = new List<CommandError>();
            if (!active) problems.Add(Error("CONFIG.INVALID_REFERENCE", "P2", "/deduction_key_id", ("reason", "INACTIVE")));
            if (f.GetValueOrDefault("valid_to") is { } to && DateOnly.Parse(to, CultureInfo.InvariantCulture) < validFrom)
                problems.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/fields/valid_to", ("reason", "not before valid_from")));
            await using (var cmd = tx.Command("select max(valid_from) from core.vat_deduction_rate where tenant_id = @t and company_id = @c and registration_id = @r and key_code = @k"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", company); cmd.Parameters.AddWithValue("r", registration); cmd.Parameters.AddWithValue("k", keyCode);
                if (await cmd.ExecuteScalarAsync(ct) is DateTime latest && validFrom <= DateOnly.FromDateTime(latest))
                    problems.Add(Error("CONFIG.VALID_FROM_NOT_LATER", "P2", "/fields/valid_from", ("valid_from", validFrom), ("latest_valid_from", DateOnly.FromDateTime(latest))));
            }
            if (problems.Count > 0) return CommandResult<DeductionRateResult>.Rejected(problems);

            var cols = new List<string> { "tenant_id", "company_id", "registration_id", "key_code", "created_by" };
            var vals = new List<string> { "@t", "@c", "@r", "@k", "@by" };
            await using (var insert = tx.Command(""))
            {
                insert.Parameters.AddWithValue("t", tenantId); insert.Parameters.AddWithValue("c", company); insert.Parameters.AddWithValue("r", registration);
                insert.Parameters.AddWithValue("k", keyCode); insert.Parameters.AddWithValue("by", ctx.Actor);
                for (int p = 0; p < patches.Count; p++) { cols.Add(patches[p].Field.Name); vals.Add($"@p{p}"); AddParam(insert, $"p{p}", patches[p]); }
                insert.CommandText = $"insert into core.vat_deduction_rate ({string.Join(", ", cols)}) values ({string.Join(", ", vals)})";
                await insert.ExecuteNonQueryAsync(ct);
            }
            Guid changeSet = Guid.NewGuid(), logId = Guid.NewGuid();
            string key = $"{req.DeductionKeyId:D}/{validFrom:yyyy-MM-dd}";
            await LogAsync(tx, tenantId, ctx, logId, changeSet, "VAT_DEDUCTION_RATE", key, "CREATE", null, false, null, null, null);
            await CommandSupport.WriteLogAsync(tx, tenantId, company, ctx, "vat-deduction-rates:create-version", fingerprint, "VAT_DEDUCTION_RATE", req.DeductionKeyId, logId, ct);
            await EventAsync(tx, tenantId, ctx, "config.version_created", "VAT_DEDUCTION_RATE", key, "CREATED", patches.Select(p => p.Field.Name).ToList(), ct);
            await tx.CommitAsync(ct);
            return CommandResult<DeductionRateResult>.Ok(new DeductionRateResult(req.DeductionKeyId, validFrom), logId);
        });
    }

    private static CommandResult<DeductionRateResult> Rej(CommandError error) => CommandResult<DeductionRateResult>.Rejected([error]);
}
