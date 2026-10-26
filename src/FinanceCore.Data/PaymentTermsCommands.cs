using System.Globalization;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;
using Npgsql;
using static FinanceCore.Data.MasterSupport;

namespace FinanceCore.Data;

/// <summary>
/// payment-terms:create, :update, :deactivate and :reactivate (07, 9.6, 11.1). Permission write.config. Terms are informational for the core:
/// due dates are supplied by the caller and a change never affects posted items. Deactivation sets active to false, so that no new partner
/// relation names the terms. Every change is a row of master_change_log and an audit event.
/// </summary>
public sealed class PaymentTermsCommands(NpgsqlDataSource source)
{
    public const string Permission = MasterRules.ConfigPermission;
    private const string Table = "payment_terms";
    private static readonly string[] Columns = [.. MasterFields.PaymentTerms.Select(f => f.Name), "active", "row_version"];

    public Task<CommandResult<MasterResult>> CreateAsync(Guid tenantId, CommandContext ctx, CreatePaymentTermsRequest req, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(Permission)) return Task.FromResult(Reject(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", Permission))));
        var errors = ValidateContext(ctx);
        Require(req.Fields, "/fields", errors, "code", "description", "baseline_rule", "net_days");
        var patches = Validate(req.Fields, MasterFields.PaymentTerms, "/fields", errors);
        CheckDiscount(patches, null, errors);
        if (errors.Count > 0) return Task.FromResult(CommandResult<MasterResult>.Rejected(errors));
        byte[] fingerprint = MasterRules.Fingerprint(req);
        return RunAsync(async () =>
        {
            await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Config, ct: ct);
            var replayed = await ReplayOrNullAsync(tx, tenantId, ctx, fingerprint, ct);
            if (replayed is not null) return replayed;

            string code = patches.First(p => p.Field.Name == "code").Text!;
            if (await ExistsAsync(tx, "select 1 from core.payment_terms where tenant_id = @t and code = @c", tenantId, ("c", code)))
                return Reject(Error("CONFIG.DUPLICATE_CODE", "P2", "/fields/code", ("code", code)));
            Guid id = Guid.NewGuid(), changeSet = Guid.NewGuid(), logId = Guid.NewGuid();
            await InsertAsync(tx, Table, [("payment_terms_id", NpgsqlTypes.NpgsqlDbType.Uuid, id)], patches, ctx, tenantId);
            await LogAsync(tx, tenantId, ctx, logId, changeSet, "PAYMENT_TERMS", id.ToString("D"), "CREATE", null, false, null, null, null);
            await CommandSupport.WriteLogAsync(tx, tenantId, null, ctx, "payment-terms:create", fingerprint, "PAYMENT_TERMS", id, logId, ct);
            var names = patches.Select(p => p.Field.Name).ToList();
            await EventAsync(tx, tenantId, ctx, "master.created", "PAYMENT_TERMS", id.ToString("D"), "CREATED", names, ct);
            await tx.CommitAsync(ct);
            return CommandResult<MasterResult>.Ok(new MasterResult(id, 1, changeSet, names), logId);
        });
    }

    /// <summary>Discount days and percent come together (07, 9.6). The current values count for a field that is not part of the change.</summary>
    private static void CheckDiscount(List<Patch> patches, Dictionary<string, string?>? current, List<CommandError> errors)
    {
        string? D = patches.FirstOrDefault(p => p.Field.Name == "discount_days") is { } pd ? pd.Text : current?.GetValueOrDefault("discount_days");
        string? P = patches.FirstOrDefault(p => p.Field.Name == "discount_pct") is { } pp ? pp.Text : current?.GetValueOrDefault("discount_pct");
        if ((D is null) != (P is null))
            errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/fields/discount_days", ("reason", "discount_days and discount_pct are both set or both null")));
    }

    public Task<CommandResult<MasterResult>> UpdateAsync(Guid tenantId, CommandContext ctx, UpdatePaymentTermsRequest req, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(Permission)) return Task.FromResult(Reject(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", Permission))));
        var errors = ValidateContext(ctx);
        if (req.PaymentTermsId == Guid.Empty) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/payment_terms_id", ("reason", "required")));
        if (req.ExpectedVersion < 1) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/expected_version", ("reason", "required")));
        var patches = Validate(req.Fields, MasterFields.PaymentTerms, "/fields", errors);
        if (errors.Count > 0) return Task.FromResult(CommandResult<MasterResult>.Rejected(errors));
        byte[] fingerprint = MasterRules.Fingerprint(req);
        return RunAsync(async () =>
        {
            await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Config, ct: ct);
            var replayed = await ReplayOrNullAsync(tx, tenantId, ctx, fingerprint, ct);
            if (replayed is not null) return replayed;

            var current = await ReadRowAsync(tx, Table, "payment_terms_id = @id", Columns, true, tenantId, ("id", req.PaymentTermsId));
            if (current is null) return Reject(Error("CONFIG.INVALID_REFERENCE", "P2", "/payment_terms_id", ("payment_terms_id", req.PaymentTermsId)));
            long version = long.Parse(current["row_version"]!, CultureInfo.InvariantCulture);
            if (version != req.ExpectedVersion)
                return Reject(Error("CONFIG.ROW_VERSION_MISMATCH", "P2", "/expected_version", ("current_version", version), ("expected_version", req.ExpectedVersion)));

            var effective = patches.Where(p => p.Text != current[p.Field.Name]).ToList();
            var errors2 = new List<CommandError>();
            CheckDiscount(patches, current, errors2);
            if (effective.Any(p => p.Field.Name == "code")
                && await ExistsAsync(tx, "select 1 from core.payment_terms where tenant_id = @t and code = @c and payment_terms_id <> @id", tenantId,
                    ("c", effective.First(p => p.Field.Name == "code").Text!), ("id", req.PaymentTermsId)))
                errors2.Add(Error("CONFIG.DUPLICATE_CODE", "P2", "/fields/code"));
            if (errors2.Count > 0) return CommandResult<MasterResult>.Rejected(errors2);
            if (effective.Count == 0) return Reject(Error("SCHEMA.NO_EFFECTIVE_CHANGE", "P6", "/fields"));

            Guid changeSet = Guid.NewGuid(), logId = Guid.NewGuid();
            await MasterSupport.UpdateAsync(tx, Table, "payment_terms_id = @id", effective, ctx, tenantId, ("id", req.PaymentTermsId));
            foreach (Patch p in effective)
                await LogAsync(tx, tenantId, ctx, logId, changeSet, "PAYMENT_TERMS", req.PaymentTermsId.ToString("D"), "UPDATE", p.Field.Name, false, current[p.Field.Name], p.Text, null);
            await CommandSupport.WriteLogAsync(tx, tenantId, null, ctx, "payment-terms:update", fingerprint, "PAYMENT_TERMS", req.PaymentTermsId, logId, ct);
            var names = effective.Select(p => p.Field.Name).ToList();
            await EventAsync(tx, tenantId, ctx, "master.changed", "PAYMENT_TERMS", req.PaymentTermsId.ToString("D"), "CHANGED", names, ct);
            await tx.CommitAsync(ct);
            return CommandResult<MasterResult>.Ok(new MasterResult(req.PaymentTermsId, version + 1, changeSet, names), logId);
        });
    }

    public Task<CommandResult<MasterResult>> DeactivateAsync(Guid tenantId, CommandContext ctx, MasterStatusRequest req, CancellationToken ct = default) =>
        StatusAsync(tenantId, ctx, req, "payment-terms:deactivate", true, "DEACTIVATE", "master.deactivated", "DEACTIVATED", ct);

    public Task<CommandResult<MasterResult>> ReactivateAsync(Guid tenantId, CommandContext ctx, MasterStatusRequest req, CancellationToken ct = default) =>
        StatusAsync(tenantId, ctx, req, "payment-terms:reactivate", false, "REACTIVATE", "master.reactivated", "REACTIVATED", ct);

    private Task<CommandResult<MasterResult>> StatusAsync(
        Guid tenantId, CommandContext ctx, MasterStatusRequest req, string command, bool deactivate, string action, string eventType, string change, CancellationToken ct)
    {
        if (!ctx.Permissions.Contains(Permission)) return Task.FromResult(Reject(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", Permission))));
        var errors = ValidateContext(ctx);
        if (req.Id == Guid.Empty) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/payment_terms_id", ("reason", "required")));
        if (req.ExpectedVersion < 1) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/expected_version", ("reason", "required")));
        if (string.IsNullOrWhiteSpace(req.ReasonCode)) errors.Add(Error("SCHEMA.REQUIRED_FIELD_MISSING", "P1", "/reason_code"));
        else if (req.ReasonCode.Length > 50) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/reason_code", ("reason", "at most 50 characters")));
        if (errors.Count > 0) return Task.FromResult(CommandResult<MasterResult>.Rejected(errors));
        byte[] fingerprint = MasterRules.Fingerprint(command, req);
        return RunAsync(async () =>
        {
            await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Config, ct: ct);
            var replayed = await ReplayOrNullAsync(tx, tenantId, ctx, fingerprint, ct);
            if (replayed is not null) return replayed;
            var row = await ReadRowAsync(tx, Table, "payment_terms_id = @id", ["active", "row_version"], true, tenantId, ("id", req.Id));
            if (row is null) return Reject(Error("CONFIG.INVALID_REFERENCE", "P2", "/payment_terms_id", ("payment_terms_id", req.Id)));
            long version = long.Parse(row["row_version"]!, CultureInfo.InvariantCulture);
            if (version != req.ExpectedVersion)
                return Reject(Error("CONFIG.ROW_VERSION_MISMATCH", "P2", "/expected_version", ("current_version", version), ("expected_version", req.ExpectedVersion)));
            if ((row["active"] == "true") != deactivate) return Reject(Error("SCHEMA.NO_EFFECTIVE_CHANGE", "P6", "/payment_terms_id", ("active", row["active"])));

            await using (var cmd = tx.Command("update core.payment_terms set active = @a, row_version = row_version + 1, updated_at = now(), updated_by = @by where tenant_id = @t and payment_terms_id = @id"))
            {
                cmd.Parameters.AddWithValue("a", !deactivate); cmd.Parameters.AddWithValue("by", ctx.Actor);
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("id", req.Id);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            Guid changeSet = Guid.NewGuid(), logId = Guid.NewGuid();
            await LogAsync(tx, tenantId, ctx, logId, changeSet, "PAYMENT_TERMS", req.Id.ToString("D"), action, null, false, null, null, req.ReasonCode);
            await CommandSupport.WriteLogAsync(tx, tenantId, null, ctx, command, fingerprint, "PAYMENT_TERMS", req.Id, logId, ct);
            await EventAsync(tx, tenantId, ctx, eventType, "PAYMENT_TERMS", req.Id.ToString("D"), change, ["active"], ct);
            await tx.CommitAsync(ct);
            return CommandResult<MasterResult>.Ok(new MasterResult(req.Id, version + 1, changeSet, ["active"]), logId);
        });
    }

    private static async Task<CommandResult<MasterResult>?> ReplayOrNullAsync(TenantTransaction tx, Guid tenantId, CommandContext ctx, byte[] fingerprint, CancellationToken ct)
    {
        CommandSupport.EarlierCommand? earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
        if (earlier is null) return null;
        if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint))
            return Reject(Error("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
        MasterResult? replay = earlier.EntityId is null ? null : await ReplayAsync(tx, tenantId, earlier.CommandLogId, Table, "payment_terms_id", earlier.EntityId.Value);
        return replay is null ? CommandResult<MasterResult>.Failed(Error("SYSTEM.REPLAY_RESULT_MISSING", "P1", "")) : CommandResult<MasterResult>.Ok(replay, earlier.CommandLogId, replay: true);
    }

    private static CommandResult<MasterResult> Reject(CommandError error) => CommandResult<MasterResult>.Rejected([error]);
}
