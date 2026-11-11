using FinanceCore.Core.Commands;
using FinanceCore.Core.Retention;
using Npgsql;
using NpgsqlTypes;
using static FinanceCore.Data.MasterSupport;

namespace FinanceCore.Data;

/// <summary>
/// retention-rules:create-version (12, 13, 07, 11.1): a new version of the retention rule of a (company, record class, country), or its first
/// version. Permission retention.manage. The table is insert-only (pattern V): a rule change affects only the records created afterwards (12, Q13).
/// A new version starts later than the newest version of its rule (CONFIG.VALID_FROM_NOT_LATER). The audit event is config.version_created.
/// </summary>
public sealed class RetentionRuleCommands(NpgsqlDataSource source)
{
    public Task<CommandResult<CreatedRetentionRule>> CreateVersionAsync(
        Guid tenantId, CommandContext ctx, CreateRetentionRuleVersionRequest req, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(RetentionRules.Permission))
            return Task.FromResult(Reject(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", RetentionRules.Permission))));
        var errors = ValidateContext(ctx);
        void Invalid(string path, string reason) => errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", path, ("reason", reason)));
        if (req.CompanyId == Guid.Empty) Invalid("/company_id", "required");
        if (!RetentionRules.RecordClasses.Contains(req.RecordClass))
            errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/record_class", ("allowed_values", RetentionRules.RecordClasses)));
        if (req.CountryCode is not { Length: 2 } || !req.CountryCode.All(c => c is >= 'A' and <= 'Z')) Invalid("/country_code", "two upper-case letters");
        if (req.RetentionYears is < 1 or > 100) Invalid("/retention_years", "whole years from 1 to 100");
        if (!RetentionRules.StartEvents.Contains(req.StartEvent))
            errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/start_event", ("allowed_values", RetentionRules.StartEvents)));
        if (req.LegalBasis is { } lb && (lb.Length == 0 || lb.Length > 200)) Invalid("/legal_basis", "1 to 200 characters, or absent");
        if (errors.Count > 0) return Task.FromResult(CommandResult<CreatedRetentionRule>.Rejected(errors));

        byte[] fingerprint = RetentionRules.Fingerprint(req);
        return RunAsync(async () =>
        {
            await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Config, ct: ct);
            CommandSupport.EarlierCommand? earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
            if (earlier is not null)
            {
                if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint))
                    return Reject(Error("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
                CreatedRetentionRule? replay = earlier.EntityId is null ? null : await ReadAsync(tx, tenantId, req.CompanyId, earlier.EntityId.Value, ct);
                return replay is null
                    ? CommandResult<CreatedRetentionRule>.Failed(Error("SYSTEM.REPLAY_RESULT_MISSING", "P1", ""))
                    : CommandResult<CreatedRetentionRule>.Ok(replay, earlier.CommandLogId, replay: true);
            }

            var errors2 = new List<CommandError>();
            if (!await ExistsAsync(tx, "select 1 from core.company where tenant_id = @t and company_id = @c", tenantId, ("c", req.CompanyId)))
                errors2.Add(Error("COMPANY.NOT_FOUND", "P2", "/company_id"));
            if (!await ExistsAsync(tx, "select 1 from platform.ref_country where @t is not null and country_code = @c", Guid.Empty, ("c", req.CountryCode)))
                errors2.Add(Error("CONFIG.INVALID_REFERENCE", "P2", "/country_code", ("country_code", req.CountryCode)));
            if (errors2.Count > 0) return CommandResult<CreatedRetentionRule>.Rejected(errors2);

            // the rule of this (company, class, country), and the newest version of it, read with a lock so two versions cannot race
            Guid? ruleId = null; DateTime? latest = null;
            await using (var cmd = tx.Command(
                "select rule_id, max(effective_from) from core.retention_rule where tenant_id = @t and company_id = @c and record_class = @k and country_code = @cc group by rule_id"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId);
                cmd.Parameters.AddWithValue("k", req.RecordClass); cmd.Parameters.AddWithValue("cc", req.CountryCode);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct)) { ruleId = r.GetGuid(0); latest = DateTime.SpecifyKind(r.GetDateTime(1), DateTimeKind.Utc); }
            }
            DateTime now = DateTime.UtcNow;
            DateTime from = req.EffectiveFrom is { } given ? new DateTime(given.ToUniversalTime().Ticks - given.ToUniversalTime().Ticks % 10, DateTimeKind.Utc)
                                                           : new DateTime(now.Ticks - now.Ticks % 10, DateTimeKind.Utc);
            if (latest is { } l && l >= from)
                return Reject(Error("CONFIG.VALID_FROM_NOT_LATER", "P2", "/effective_from", ("effective_from", from), ("latest_effective_from", l)));

            bool isNew = ruleId is null;
            Guid id = ruleId ?? Guid.NewGuid();
            await using (var cmd = tx.Command(
                "insert into core.retention_rule (tenant_id, company_id, rule_id, effective_from, record_class, country_code, retention_years, start_event, legal_basis, created_by) " +
                "values (@t, @c, @r, @from, @k, @cc, @y, @se, @lb, @by)"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("r", id);
                cmd.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = from });
                cmd.Parameters.AddWithValue("k", req.RecordClass); cmd.Parameters.AddWithValue("cc", req.CountryCode);
                cmd.Parameters.AddWithValue("y", (short)req.RetentionYears); cmd.Parameters.AddWithValue("se", req.StartEvent);
                cmd.Parameters.Add(new NpgsqlParameter("lb", NpgsqlDbType.Text) { Value = (object?)req.LegalBasis ?? DBNull.Value });
                cmd.Parameters.AddWithValue("by", ctx.Actor);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            Guid logId = await CommandSupport.WriteLogAsync(tx, tenantId, req.CompanyId, ctx, RetentionRules.CreateCommand, fingerprint, "RETENTION_RULE", id, ct);
            await CommandSupport.WriteOutboxAsync(tx, tenantId, req.CompanyId, ctx, "config.version_created", "RETENTION_RULE", $"{req.CompanyId:D}/{id:D}", "CREATED",
                new DateTime(now.Ticks - now.Ticks % 10, DateTimeKind.Utc), isAudit: true, null,
                System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["entity_type"] = "RETENTION_RULE", ["record_class"] = req.RecordClass, ["country_code"] = req.CountryCode,
                    ["effective_from"] = from.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
                }), ct);
            await tx.CommitAsync(ct);
            return CommandResult<CreatedRetentionRule>.Ok(new CreatedRetentionRule(id, from, isNew), logId);
        });
    }

    private static async Task<CreatedRetentionRule?> ReadAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid ruleId, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select max(effective_from) from core.retention_rule where tenant_id = @t and company_id = @c and rule_id = @r");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("r", ruleId);
        object? v = await cmd.ExecuteScalarAsync(ct);
        return v is DateTime d ? new CreatedRetentionRule(ruleId, DateTime.SpecifyKind(d, DateTimeKind.Utc), false) : null;
    }

    /// <summary>All versions of the retention rules of a company, for the commands that date a record (12, 4.1).</summary>
    public static async Task<List<RetentionRuleVersion>> ReadVersionsAsync(TenantTransaction tx, Guid tenantId, Guid companyId, CancellationToken ct)
    {
        var list = new List<RetentionRuleVersion>();
        await using var cmd = tx.Command(
            "select rule_id, record_class, country_code, retention_years, start_event, effective_from from core.retention_rule where tenant_id = @t and company_id = @c");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new RetentionRuleVersion(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetInt16(3), r.GetString(4), DateTime.SpecifyKind(r.GetDateTime(5), DateTimeKind.Utc)));
        return list;
    }

    private static CommandResult<CreatedRetentionRule> Reject(CommandError error) => CommandResult<CreatedRetentionRule>.Rejected([error]);
}
