using System.Globalization;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;
using Npgsql;
using NpgsqlTypes;
using static FinanceCore.Data.MasterSupport;

namespace FinanceCore.Data;

/// <summary>
/// business-partners:create, :update, :deactivate and :reactivate (07, 11.1): the partner with its personal data and its relations to the
/// companies, in one atomic command with one change set. Permission write.master. Every change is a row of master_change_log (a personal
/// field without values) and an audit event. The S columns of the partner (retention, restriction, anonymisation) are not reachable here, and
/// an anonymised partner changes no more. Deactivation sets the status BLOCKED, reactivation ACTIVE.
/// </summary>
public sealed class PartnerCommands(NpgsqlDataSource source)
{
    public const string Permission = MasterRules.PartnerPermission;
    private const string Table = "business_partner";

    private static readonly string[] PartnerColumns =
        [.. MasterFields.Partner.Select(f => f.Name), "status", "row_version"];

    // ---------------------------------------------------------------------------------------------------------
    // create
    // ---------------------------------------------------------------------------------------------------------
    public Task<CommandResult<MasterResult>> CreateAsync(Guid tenantId, CommandContext ctx, CreatePartnerRequest req, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(Permission)) return Task.FromResult(Reject(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", Permission))));
        var errors = ValidateContext(ctx);
        Require(req.Partner, "/partner", errors, "partner_code", "partner_kind");
        Require(req.Pii, "/pii", errors, "legal_name", "country_code");
        var partner = Validate(req.Partner, MasterFields.Partner, "/partner", errors);
        var pii = Validate(req.Pii, MasterFields.Pii, "/pii", errors);
        var relations = ValidateRelations(req.Relations, errors);
        CheckCredit(partner, null, "/partner/tenant_credit_limit", "tenant_credit_limit", "tenant_credit_limit_currency", errors);
        for (int i = 0; i < relations.Count; i++)
            CheckCredit(relations[i].Patches, null, $"/relations/{i}/credit_limit", "credit_limit", "credit_limit_currency", errors);
        if (errors.Count > 0) return Task.FromResult(CommandResult<MasterResult>.Rejected(errors));

        byte[] fingerprint = MasterRules.Fingerprint(req);
        return RunAsync(() => CreateOnceAsync(tenantId, ctx, partner, pii, relations, fingerprint, ct));
    }

    private sealed record Relation(Guid CompanyId, List<Patch> Patches);

    private static List<Relation> ValidateRelations(IReadOnlyList<PartnerRelationInput>? input, List<CommandError> errors)
    {
        var list = new List<Relation>();
        var seen = new HashSet<Guid>();
        for (int i = 0; i < (input?.Count ?? 0); i++)
        {
            PartnerRelationInput r = input![i];
            string path = $"/relations/{i}";
            if (r.CompanyId == Guid.Empty) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", $"{path}/company_id", ("reason", "required")));
            else if (!seen.Add(r.CompanyId)) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", $"{path}/company_id", ("reason", "the same company twice")));
            list.Add(new Relation(r.CompanyId, Validate(r.Fields, MasterFields.Relation, path, errors)));
        }
        return list;
    }

    /// <summary>A credit limit needs its currency (07, 9.1, 9.3). The current values count when a field is not part of the change.</summary>
    private static void CheckCredit(List<Patch> patches, Dictionary<string, string?>? current, string path, string limit, string currency, List<CommandError> errors)
    {
        string? L = patches.FirstOrDefault(p => p.Field.Name == limit) is { } pl ? pl.Text : current?.GetValueOrDefault(limit);
        string? C = patches.FirstOrDefault(p => p.Field.Name == currency) is { } pc ? pc.Text : current?.GetValueOrDefault(currency);
        if (L is not null && C is null)
            errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", path, ("reason", $"{limit} needs {currency}")));
    }

    private static string? TextOf(List<Patch> patches, string name) => patches.FirstOrDefault(p => p.Field.Name == name)?.Text;

    private async Task<CommandResult<MasterResult>> CreateOnceAsync(
        Guid tenantId, CommandContext ctx, List<Patch> partner, List<Patch> pii, List<Relation> relations, byte[] fingerprint, CancellationToken ct)
    {
        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Config, ct: ct);
        CommandSupport.EarlierCommand? earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
        if (earlier is not null)
        {
            if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint))
                return Reject(Error("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
            MasterResult? replay = earlier.EntityId is null ? null : await ReplayAsync(tx, tenantId, earlier.CommandLogId, Table, "partner_id", earlier.EntityId.Value);
            return replay is null ? CommandResult<MasterResult>.Failed(Error("SYSTEM.REPLAY_RESULT_MISSING", "P1", "")) : CommandResult<MasterResult>.Ok(replay, earlier.CommandLogId, replay: true);
        }

        var errors = new List<CommandError>();
        string code = TextOf(partner, "partner_code")!;
        if (await ExistsAsync(tx, "select 1 from core.business_partner where tenant_id = @t and partner_code = @c", tenantId, ("c", code)))
            errors.Add(Error("CONFIG.DUPLICATE_CODE", "P2", "/partner/partner_code", ("code", code)));
        await CheckReferencesAsync(tx, tenantId, partner, "/partner", errors);
        await CheckPiiReferencesAsync(tx, pii, errors);
        for (int i = 0; i < relations.Count; i++)
        {
            await CheckReferencesAsync(tx, tenantId, relations[i].Patches, $"/relations/{i}", errors);
            if (!await ExistsAsync(tx, "select 1 from core.company where tenant_id = @t and company_id = @c", tenantId, ("c", relations[i].CompanyId)))
                errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", $"/relations/{i}/company_id", ("company_id", relations[i].CompanyId)));
        }
        if (errors.Count > 0) return CommandResult<MasterResult>.Rejected(errors);

        Guid partnerId = Guid.NewGuid(), changeSet = Guid.NewGuid(), logId = Guid.NewGuid();
        bool person = TextOf(partner, "partner_kind") == "PERSON";
        await InsertAsync(tx, Table, [("partner_id", NpgsqlDbType.Uuid, partnerId)], partner, ctx, tenantId);
        await InsertAsync(tx, "business_partner_pii", [("partner_id", NpgsqlDbType.Uuid, partnerId)], pii, ctx, tenantId);
        foreach (Relation r in relations)
            await InsertAsync(tx, "partner_company_relation", [("partner_id", NpgsqlDbType.Uuid, partnerId), ("company_id", NpgsqlDbType.Uuid, r.CompanyId)], r.Patches, ctx, tenantId);

        await LogAsync(tx, tenantId, ctx, logId, changeSet, "BUSINESS_PARTNER", partnerId.ToString("D"), "CREATE", null, false, null, null, null);
        await LogAsync(tx, tenantId, ctx, logId, changeSet, "BUSINESS_PARTNER_PII", partnerId.ToString("D"), "CREATE", null, true, null, null, null);
        foreach (Relation r in relations)
            await LogAsync(tx, tenantId, ctx, logId, changeSet, "PARTNER_COMPANY_RELATION", $"{partnerId:D}/{r.CompanyId:D}", "CREATE", null, false, null, null, null);
        await CommandSupport.WriteLogAsync(tx, tenantId, null, ctx, "business-partners:create", fingerprint, "BUSINESS_PARTNER", partnerId, logId, ct);

        var fields = partner.Select(p => "partner." + p.Field.Name).Concat(pii.Select(p => "pii." + p.Field.Name))
            .Concat(relations.Select(r => "relation." + r.CompanyId.ToString("D"))).ToList();
        await EventAsync(tx, tenantId, ctx, "master.created", "BUSINESS_PARTNER", partnerId.ToString("D"), "CREATED", fields, ct);
        await tx.CommitAsync(ct);
        _ = person;
        return CommandResult<MasterResult>.Ok(new MasterResult(partnerId, 1, changeSet, fields), logId);
    }

    /// <summary>References of a set of fields: companies, payment terms and the currencies, which must be enabled for the tenant (07, 7.1).</summary>
    private static async Task CheckReferencesAsync(TenantTransaction tx, Guid tenantId, List<Patch> patches, string path, List<CommandError> errors)
    {
        foreach (Patch p in patches.Where(p => p.Value is not null))
        {
            string at = $"{path}/{p.Field.Name}";
            switch (p.Field.Name)
            {
                case "ic_company_id":
                    if (!await ExistsAsync(tx, "select 1 from core.company where tenant_id = @t and company_id = @c", tenantId, ("c", (Guid)p.Value!)))
                        errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", at, ("company_id", p.Value)));
                    break;
                case "default_payment_terms_id":
                    if (!await ExistsAsync(tx, "select 1 from core.payment_terms where tenant_id = @t and payment_terms_id = @c and active", tenantId, ("c", (Guid)p.Value!)))
                        errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", at, ("payment_terms_id", p.Value)));
                    break;
                case "tenant_credit_limit_currency" or "credit_limit_currency" or "default_currency":
                    if (!await ExistsAsync(tx, "select 1 from core.tenant_currency where tenant_id = @t and currency_code = @c and active", tenantId, ("c", (string)p.Value!)))
                        errors.Add(Error("CONFIG.CURRENCY_NOT_ENABLED", "P2", at, ("currency", p.Value)));
                    break;
            }
        }
    }

    private static async Task CheckPiiReferencesAsync(TenantTransaction tx, List<Patch> pii, List<CommandError> errors)
    {
        foreach (Patch p in pii.Where(p => p.Field.Name == "country_code" && p.Value is not null))
            if (!await ExistsAsync(tx, "select 1 from platform.ref_country where @t is not null and country_code = @c", Guid.Empty, ("c", (string)p.Value!)))
                errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", "/pii/country_code", ("country_code", p.Value)));
    }

    // ---------------------------------------------------------------------------------------------------------
    // update
    // ---------------------------------------------------------------------------------------------------------
    public Task<CommandResult<MasterResult>> UpdateAsync(Guid tenantId, CommandContext ctx, UpdatePartnerRequest req, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(Permission)) return Task.FromResult(Reject(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", Permission))));
        var errors = ValidateContext(ctx);
        if (req.PartnerId == Guid.Empty) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/partner_id", ("reason", "required")));
        if (req.ExpectedVersion < 1) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/expected_version", ("reason", "required")));
        var partner = Validate(req.Partner, MasterFields.Partner, "/partner", errors);
        var pii = Validate(req.Pii, MasterFields.Pii, "/pii", errors);
        var relations = ValidateRelations(req.Relations, errors);
        if (errors.Count > 0) return Task.FromResult(CommandResult<MasterResult>.Rejected(errors));
        byte[] fingerprint = MasterRules.Fingerprint(req);
        return RunAsync(() => UpdateOnceAsync(tenantId, ctx, req, partner, pii, relations, fingerprint, ct));
    }

    private sealed record Change(string EntityType, string EntityId, MasterField Field, string? Old, string? New);

    private async Task<CommandResult<MasterResult>> UpdateOnceAsync(
        Guid tenantId, CommandContext ctx, UpdatePartnerRequest req, List<Patch> partner, List<Patch> pii, List<Relation> relations, byte[] fingerprint, CancellationToken ct)
    {
        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Config, ct: ct);
        CommandSupport.EarlierCommand? earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
        if (earlier is not null)
        {
            if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint))
                return Reject(Error("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
            MasterResult? replay = earlier.EntityId is null ? null : await ReplayAsync(tx, tenantId, earlier.CommandLogId, Table, "partner_id", earlier.EntityId.Value);
            return replay is null ? CommandResult<MasterResult>.Failed(Error("SYSTEM.REPLAY_RESULT_MISSING", "P1", "")) : CommandResult<MasterResult>.Ok(replay, earlier.CommandLogId, replay: true);
        }

        // The partner row is locked first: it carries the version of the whole command
        var current = await ReadRowAsync(tx, Table, "partner_id = @id", PartnerColumns, true, tenantId, ("id", req.PartnerId));
        if (current is null) return Reject(Error("CONFIG.INVALID_REFERENCE", "P2", "/partner_id", ("partner_id", req.PartnerId)));
        if (current["status"] == "ANONYMISED") return Reject(Error("MASTER.PARTNER_ANONYMISED", "P2", "/partner_id"));
        long version = long.Parse(current["row_version"]!, CultureInfo.InvariantCulture);
        if (version != req.ExpectedVersion)
            return Reject(Error("CONFIG.ROW_VERSION_MISMATCH", "P2", "/expected_version", ("current_version", version), ("expected_version", req.ExpectedVersion)));

        var errors = new List<CommandError>();
        var changes = new List<Change>();
        string kind = TextOf(partner, "partner_kind") ?? current["partner_kind"]!;
        string pid = req.PartnerId.ToString("D");

        // partner fields
        var effectivePartner = partner.Where(p => p.Text != current[p.Field.Name]).ToList();
        foreach (Patch p in effectivePartner) changes.Add(new Change("BUSINESS_PARTNER", pid, p.Field, current[p.Field.Name], p.Text));
        CheckCredit(partner, current, "/partner/tenant_credit_limit", "tenant_credit_limit", "tenant_credit_limit_currency", errors);
        await CheckReferencesAsync(tx, tenantId, effectivePartner, "/partner", errors);
        if (effectivePartner.Any(p => p.Field.Name == "partner_code")
            && await ExistsAsync(tx, "select 1 from core.business_partner where tenant_id = @t and partner_code = @c and partner_id <> @id", tenantId, ("c", TextOf(partner, "partner_code")!), ("id", req.PartnerId)))
            errors.Add(Error("CONFIG.DUPLICATE_CODE", "P2", "/partner/partner_code", ("code", TextOf(partner, "partner_code"))));
        bool referenced = await ExistsAsync(tx, "select 1 from core.journal_line_open_item where tenant_id = @t and partner_id = @id", tenantId, ("id", req.PartnerId));
        if (referenced && effectivePartner.Any(p => p.Field.Name == "partner_kind"))
            errors.Add(Error("CONFIG.IMMUTABLE_AFTER_POSTING", "P2", "/partner/partner_kind"));
        // a role is removed only if no posted record uses it (07, 9.1): an open item of the partner uses it
        if (referenced && effectivePartner.Any(p => p.Field.Name is "is_customer" or "is_supplier" && p.Value is false))
            errors.Add(Error("CONFIG.REFERENCED", "P2", "/partner", ("reason", "open items refer to the partner")));

        // personal data
        var piiCurrent = pii.Count == 0 ? null : await ReadRowAsync(tx, "business_partner_pii", "partner_id = @id", MasterFields.Pii.Select(f => f.Name).Append("row_version").ToList(), true, tenantId, ("id", req.PartnerId));
        var effectivePii = pii.Where(p => piiCurrent is null || p.Text != piiCurrent[p.Field.Name]).ToList();
        foreach (Patch p in effectivePii) changes.Add(new Change("BUSINESS_PARTNER_PII", pid, p.Field, piiCurrent?[p.Field.Name], p.Text));
        await CheckPiiReferencesAsync(tx, effectivePii, errors);

        // relations: a patch for a company without a row creates the row
        var relationWork = new List<(Relation Input, Dictionary<string, string?>? Current, List<Patch> Effective)>();
        foreach (Relation r in relations)
        {
            var cur = await ReadRowAsync(tx, "partner_company_relation", "partner_id = @id and company_id = @c",
                MasterFields.Relation.Select(f => f.Name).Append("row_version").ToList(), true, tenantId, ("id", req.PartnerId), ("c", r.CompanyId));
            int index = relations.IndexOf(r);
            if (cur is null && !await ExistsAsync(tx, "select 1 from core.company where tenant_id = @t and company_id = @c", tenantId, ("c", r.CompanyId)))
                errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", $"/relations/{index}/company_id", ("company_id", r.CompanyId)));
            var effective = cur is null ? r.Patches : r.Patches.Where(p => p.Text != cur[p.Field.Name]).ToList();
            CheckCredit(r.Patches, cur, $"/relations/{index}/credit_limit", "credit_limit", "credit_limit_currency", errors);
            await CheckReferencesAsync(tx, tenantId, effective, $"/relations/{index}", errors);
            string entityId = $"{pid}/{r.CompanyId:D}";
            if (cur is null) changes.Add(new Change("PARTNER_COMPANY_RELATION", entityId, new MasterField("", MasterKind.Text), null, null));
            else foreach (Patch p in effective) changes.Add(new Change("PARTNER_COMPANY_RELATION", entityId, p.Field, cur[p.Field.Name], p.Text));
            relationWork.Add((r, cur, effective));
        }
        if (errors.Count > 0) return CommandResult<MasterResult>.Rejected(errors);
        if (changes.Count == 0) return Reject(Error("SCHEMA.NO_EFFECTIVE_CHANGE", "P6", ""));

        // write: the partner row always moves its version, the others when they change
        Guid changeSet = Guid.NewGuid(), logId = Guid.NewGuid();
        if (effectivePartner.Count > 0) await MasterSupport.UpdateAsync(tx, Table, "partner_id = @id", effectivePartner, ctx, tenantId, ("id", req.PartnerId));
        else
        {
            await using var cmd = tx.Command("update core.business_partner set row_version = row_version + 1, updated_at = now(), updated_by = @by where tenant_id = @t and partner_id = @id");
            cmd.Parameters.AddWithValue("by", ctx.Actor); cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("id", req.PartnerId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        if (effectivePii.Count > 0) await MasterSupport.UpdateAsync(tx, "business_partner_pii", "partner_id = @id", effectivePii, ctx, tenantId, ("id", req.PartnerId));
        foreach (var (input, cur, effective) in relationWork)
        {
            if (cur is null)
                await InsertAsync(tx, "partner_company_relation", [("partner_id", NpgsqlDbType.Uuid, req.PartnerId), ("company_id", NpgsqlDbType.Uuid, input.CompanyId)], effective, ctx, tenantId);
            else if (effective.Count > 0)
                await MasterSupport.UpdateAsync(tx, "partner_company_relation", "partner_id = @id and company_id = @c", effective, ctx, tenantId, ("id", req.PartnerId), ("c", input.CompanyId));
        }

        foreach (Change c in changes)
        {
            bool created = c.Field.Name == "";
            bool isPii = !created && (c.Field.Pii || (c.Field.PiiIfPerson && kind == "PERSON"));
            await LogAsync(tx, tenantId, ctx, logId, changeSet, c.EntityType, c.EntityId, created ? "CREATE" : "UPDATE", created ? null : c.Field.Name, isPii || (created && false), c.Old, c.New, null);
        }
        await CommandSupport.WriteLogAsync(tx, tenantId, null, ctx, "business-partners:update", fingerprint, "BUSINESS_PARTNER", req.PartnerId, logId, ct);
        var names = changes.Select(c => c.Field.Name == "" ? "relation." + c.EntityId : (c.EntityType == "BUSINESS_PARTNER_PII" ? "pii." : c.EntityType == "BUSINESS_PARTNER" ? "partner." : "relation.") + c.Field.Name).ToList();
        await EventAsync(tx, tenantId, ctx, "master.changed", "BUSINESS_PARTNER", pid, "CHANGED", names, ct);
        await tx.CommitAsync(ct);
        return CommandResult<MasterResult>.Ok(new MasterResult(req.PartnerId, version + 1, changeSet, names), logId);
    }

    // ---------------------------------------------------------------------------------------------------------
    // deactivate and reactivate: status BLOCKED and ACTIVE
    // ---------------------------------------------------------------------------------------------------------
    public Task<CommandResult<MasterResult>> DeactivateAsync(Guid tenantId, CommandContext ctx, MasterStatusRequest req, CancellationToken ct = default) =>
        StatusAsync(tenantId, ctx, req, "business-partners:deactivate", "ACTIVE", "BLOCKED", "DEACTIVATE", "master.deactivated", "DEACTIVATED", ct);

    public Task<CommandResult<MasterResult>> ReactivateAsync(Guid tenantId, CommandContext ctx, MasterStatusRequest req, CancellationToken ct = default) =>
        StatusAsync(tenantId, ctx, req, "business-partners:reactivate", "BLOCKED", "ACTIVE", "REACTIVATE", "master.reactivated", "REACTIVATED", ct);

    private Task<CommandResult<MasterResult>> StatusAsync(
        Guid tenantId, CommandContext ctx, MasterStatusRequest req, string command, string from, string to, string action, string eventType, string change, CancellationToken ct)
    {
        if (!ctx.Permissions.Contains(Permission)) return Task.FromResult(Reject(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", Permission))));
        var errors = ValidateContext(ctx);
        if (req.Id == Guid.Empty) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/partner_id", ("reason", "required")));
        if (req.ExpectedVersion < 1) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/expected_version", ("reason", "required")));
        if (string.IsNullOrWhiteSpace(req.ReasonCode)) errors.Add(Error("SCHEMA.REQUIRED_FIELD_MISSING", "P1", "/reason_code"));
        else if (req.ReasonCode.Length > 50) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/reason_code", ("reason", "at most 50 characters")));
        if (errors.Count > 0) return Task.FromResult(CommandResult<MasterResult>.Rejected(errors));
        byte[] fingerprint = MasterRules.Fingerprint(command, req);
        return RunAsync(async () =>
        {
            await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Config, ct: ct);
            CommandSupport.EarlierCommand? earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
            if (earlier is not null)
            {
                if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint))
                    return Reject(Error("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
                MasterResult? replay = earlier.EntityId is null ? null : await ReplayAsync(tx, tenantId, earlier.CommandLogId, Table, "partner_id", earlier.EntityId.Value);
                return replay is null ? CommandResult<MasterResult>.Failed(Error("SYSTEM.REPLAY_RESULT_MISSING", "P1", "")) : CommandResult<MasterResult>.Ok(replay, earlier.CommandLogId, replay: true);
            }
            var row = await ReadRowAsync(tx, Table, "partner_id = @id", ["status", "row_version"], true, tenantId, ("id", req.Id));
            if (row is null) return Reject(Error("CONFIG.INVALID_REFERENCE", "P2", "/partner_id", ("partner_id", req.Id)));
            if (row["status"] == "ANONYMISED") return Reject(Error("MASTER.PARTNER_ANONYMISED", "P2", "/partner_id"));
            long version = long.Parse(row["row_version"]!, CultureInfo.InvariantCulture);
            if (version != req.ExpectedVersion)
                return Reject(Error("CONFIG.ROW_VERSION_MISMATCH", "P2", "/expected_version", ("current_version", version), ("expected_version", req.ExpectedVersion)));
            if (row["status"] != from) return Reject(Error("SCHEMA.NO_EFFECTIVE_CHANGE", "P6", "/partner_id", ("status", row["status"])));

            await using (var cmd = tx.Command("update core.business_partner set status = @s, row_version = row_version + 1, updated_at = now(), updated_by = @by where tenant_id = @t and partner_id = @id"))
            {
                cmd.Parameters.AddWithValue("s", to); cmd.Parameters.AddWithValue("by", ctx.Actor);
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("id", req.Id);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            Guid changeSet = Guid.NewGuid(), logId = Guid.NewGuid();
            await LogAsync(tx, tenantId, ctx, logId, changeSet, "BUSINESS_PARTNER", req.Id.ToString("D"), action, null, false, null, null, req.ReasonCode);
            await CommandSupport.WriteLogAsync(tx, tenantId, null, ctx, command, fingerprint, "BUSINESS_PARTNER", req.Id, logId, ct);
            await EventAsync(tx, tenantId, ctx, eventType, "BUSINESS_PARTNER", req.Id.ToString("D"), change, ["status"], ct);
            await tx.CommitAsync(ct);
            return CommandResult<MasterResult>.Ok(new MasterResult(req.Id, version + 1, changeSet, ["partner.status"]), logId);
        });
    }

    private static CommandResult<MasterResult> Reject(CommandError error) => CommandResult<MasterResult>.Rejected([error]);
}
