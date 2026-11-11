using System.Globalization;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;
using Npgsql;
using NpgsqlTypes;
using static FinanceCore.Data.MasterSupport;

namespace FinanceCore.Data;

public sealed record TaxRuleResult(Guid RuleId, DateOnly ValidFrom, int AlternativeCount);

/// <summary>
/// The partner registrations of 07, 9.4 (partner-tax-registrations: create, update, deactivate, reactivate; permission write.master, because the registration is
/// data of the partner) and the determination rules of 07, 8.4 (tax-determination-rules:create and :create-version; permission write.config). A rule version is
/// insert-only and is written with its alternatives. Built choices are in 07, 13.2l.
/// </summary>
public sealed class TaxDeterminationCommands(NpgsqlDataSource source)
{
    public const string RegistrationPermission = MasterRules.PartnerPermission;
    public const string RulePermission = MasterRules.ConfigPermission;
    private readonly MasterTableCommands _generic = new(source);

    // ---------------------------------------------------------------------------------------------------------
    // partner tax registrations
    // ---------------------------------------------------------------------------------------------------------
    private static readonly MasterSpec RegistrationSpec = new("partner_tax_registration", "partner_tax_registration_id", "PARTNER_TAX_REGISTRATION", "/registration_id",
        "partner-tax-registrations", RegistrationPermission, TaxFields.PartnerRegistrationCreate, TaxFields.PartnerRegistrationUpdate,
        ["partner_id", "country_code", "valid_from", "vat_status"], [], CheckRegistrationAsync);

    private static async Task<List<CommandError>> CheckRegistrationAsync(MasterCheckInput i)
    {
        var errors = new List<CommandError>();
        Guid partner = i.Creating ? Guid.Parse(i.Get("partner_id")!) : Guid.Parse(i.Current!["partner_id"]!);
        string kind = "";
        if (i.Creating)
        {
            string? partnerKind = null, partnerStatus = null;
            await using (var cmd = i.Tx.Command("select partner_kind, status from core.business_partner where tenant_id = @t and partner_id = @x"))
            {
                cmd.Parameters.AddWithValue("t", i.TenantId); cmd.Parameters.AddWithValue("x", partner);
                await using var r = await cmd.ExecuteReaderAsync(i.Ct);
                if (await r.ReadAsync(i.Ct)) { partnerKind = r.GetString(0); partnerStatus = r.GetString(1); }
            }
            if (partnerKind is null) errors.Add(Error("MASTER.PARTNER_NOT_FOUND", "P2", "/fields/partner_id", ("partner_id", partner)));
            else if (partnerStatus == "ANONYMISED") errors.Add(Error("MASTER.PARTNER_ANONYMISED", "P2", "/fields/partner_id"));
            else kind = partnerKind;
            if (errors.Count == 0 && await ExistsAsync(i.Tx, "select 1 from core.partner_tax_registration where tenant_id = @t and partner_id = @p and country_code = @k and valid_from = @f::date",
                    i.TenantId, ("p", partner), ("k", i.Get("country_code")!), ("f", i.Get("valid_from")!)))
                errors.Add(Error("CONFIG.DUPLICATE_CODE", "P2", "/fields/valid_from", ("country_code", i.Get("country_code")), ("valid_from", i.Get("valid_from"))));
            if (errors.Count == 0 && !await ExistsAsync(i.Tx, "select 1 from platform.ref_country where country_code = @x", i.TenantId, ("x", i.Get("country_code")!)))
                errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", "/fields/country_code", ("country_code", i.Get("country_code"))));
        }
        else
        {
            await using var cmd = i.Tx.Command("select partner_kind from core.business_partner where tenant_id = @t and partner_id = @x");
            cmd.Parameters.AddWithValue("t", i.TenantId); cmd.Parameters.AddWithValue("x", partner);
            kind = (string?)await cmd.ExecuteScalarAsync(i.Ct) ?? "";
        }
        if (kind == "PERSON" && i.Get("vat_id") is not null)
            errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/fields/vat_id", ("reason", "the VAT id of a private person is personal data and is not kept here")));
        if (i.Get("valid_to") is { } to)
        {
            string from = i.Creating ? i.Get("valid_from")! : i.Current!["valid_from"]!;
            if (DateOnly.Parse(to, CultureInfo.InvariantCulture) < DateOnly.Parse(from, CultureInfo.InvariantCulture))
                errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/fields/valid_to", ("reason", "not before valid_from")));
        }
        return errors;
    }

    public Task<CommandResult<MasterResult>> CreateRegistrationAsync(Guid t, CommandContext c, CreatePartnerTaxRegistrationRequest r, CancellationToken ct = default) =>
        _generic.CreateAsync(t, c, RegistrationSpec, r.Fields, MasterRules.Fingerprint(r), ct);
    public Task<CommandResult<MasterResult>> UpdateRegistrationAsync(Guid t, CommandContext c, UpdatePartnerTaxRegistrationRequest r, CancellationToken ct = default) =>
        _generic.UpdateAsync(t, c, RegistrationSpec, r.RegistrationId, r.ExpectedVersion, r.Fields, MasterRules.Fingerprint(r), ct);
    public Task<CommandResult<MasterResult>> DeactivateRegistrationAsync(Guid t, CommandContext c, MasterStatusRequest r, CancellationToken ct = default) =>
        _generic.StatusAsync(t, c, RegistrationSpec, r, true, ct);
    public Task<CommandResult<MasterResult>> ReactivateRegistrationAsync(Guid t, CommandContext c, MasterStatusRequest r, CancellationToken ct = default) =>
        _generic.StatusAsync(t, c, RegistrationSpec, r, false, ct);

    // ---------------------------------------------------------------------------------------------------------
    // determination rules
    // ---------------------------------------------------------------------------------------------------------
    private static readonly string[] KeyColumns = ["transaction_type", "origin_country", "destination_country", "origin_eu", "destination_eu", "partner_vat_status", "account_id", "tax_class_id"];

    public Task<CommandResult<TaxRuleResult>> CreateRuleAsync(Guid tenantId, CommandContext ctx, CreateTaxRuleRequest req, CancellationToken ct = default) =>
        WriteAsync(tenantId, ctx, "tax-determination-rules:create", null, req.CompanyId, req.Fields, req.Alternatives, MasterRules.Fingerprint(req), ct);

    public Task<CommandResult<TaxRuleResult>> CreateRuleVersionAsync(Guid tenantId, CommandContext ctx, CreateTaxRuleVersionRequest req, CancellationToken ct = default) =>
        WriteAsync(tenantId, ctx, "tax-determination-rules:create-version", req.RuleId, null, req.Fields, req.Alternatives, MasterRules.Fingerprint(req), ct);

    private Task<CommandResult<TaxRuleResult>> WriteAsync(
        Guid tenantId, CommandContext ctx, string command, Guid? existingRule, Guid? newRuleCompany, IReadOnlyDictionary<string, object?> fields,
        IReadOnlyList<Guid> alternatives, byte[] fingerprint, CancellationToken ct)
    {
        if (!ctx.Permissions.Contains(RulePermission)) return Task.FromResult(RejectR(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", RulePermission))));
        var errors = ValidateContext(ctx);
        if (existingRule is { } rid && rid == Guid.Empty) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/rule_id", ("reason", "required")));
        if (newRuleCompany is { } cid && cid == Guid.Empty) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/company_id", ("reason", "required")));
        Require(fields, "/fields", errors, "valid_from", "priority", "tax_code_id");
        var patches = Validate(fields, TaxFields.RuleVersion, "/fields", errors);
        var alts = (alternatives ?? []).ToList();
        if (alts.Any(a => a == Guid.Empty)) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/alternatives", ("reason", "ids")));
        if (alts.Distinct().Count() != alts.Count) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/alternatives", ("reason", "the same code twice")));
        if (errors.Count > 0) return Task.FromResult(CommandResult<TaxRuleResult>.Rejected(errors));

        return RunAsync(async () =>
        {
            await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Config, ct: ct);
            CommandSupport.EarlierCommand? earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
            if (earlier is not null)
            {
                if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint)) return RejectR(Error("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
                TaxRuleResult? replay = await LoadReplayAsync(tx, tenantId, earlier.CommandLogId);
                return replay is null ? CommandResult<TaxRuleResult>.Failed(Error("SYSTEM.REPLAY_RESULT_MISSING", "P1", "")) : CommandResult<TaxRuleResult>.Ok(replay, earlier.CommandLogId, replay: true);
            }

            Guid company, rule;
            if (existingRule is { } existing)
            {
                await using var cmd = tx.Command("select company_id from core.tax_determination_rule where tenant_id = @t and rule_id = @x limit 1");
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("x", existing);
                if (await cmd.ExecuteScalarAsync(ct) is not Guid found) return RejectR(Error("CONFIG.INVALID_REFERENCE", "P2", "/rule_id", ("rule_id", existing)));
                company = found; rule = existing;
            }
            else
            {
                company = newRuleCompany!.Value; rule = Guid.NewGuid();
                if (!await ExistsAsync(tx, "select 1 from core.company where tenant_id = @t and company_id = @x", tenantId, ("x", company)))
                    return RejectR(Error("CONFIG.INVALID_REFERENCE", "P2", "/company_id", ("company_id", company)));
            }

            var f = patches.ToDictionary(p => p.Field.Name);
            string? Get(string name) => f.TryGetValue(name, out Patch? p) ? p.Text : null;
            var problems = new List<CommandError>();
            DateOnly validFrom = DateOnly.Parse(Get("valid_from")!, CultureInfo.InvariantCulture);
            DateOnly? validTo = Get("valid_to") is { } to ? DateOnly.Parse(to, CultureInfo.InvariantCulture) : null;
            if (validTo is { } end && end < validFrom) problems.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/fields/valid_to", ("reason", "not before valid_from")));

            if (existingRule is not null)
            {
                await using var cmd = tx.Command("select max(valid_from) from core.tax_determination_rule where tenant_id = @t and rule_id = @x");
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("x", rule);
                if (await cmd.ExecuteScalarAsync(ct) is DateTime latest && validFrom <= DateOnly.FromDateTime(latest))
                    problems.Add(Error("CONFIG.VALID_FROM_NOT_LATER", "P2", "/fields/valid_from", ("valid_from", validFrom), ("latest_valid_from", DateOnly.FromDateTime(latest))));
            }

            Guid taxCode = Guid.Parse(Get("tax_code_id")!);
            foreach (Guid code in alts.Prepend(taxCode).Distinct())
                if (!await ExistsAsync(tx, "select 1 from core.tax_code where tenant_id = @t and company_id = @c and tax_code_id = @x and active", tenantId, ("c", company), ("x", code)))
                    problems.Add(Error("CONFIG.INVALID_REFERENCE", "P2", code == taxCode ? "/fields/tax_code_id" : "/alternatives", ("tax_code_id", code)));
            if (Get("account_id") is { } account && !await ExistsAsync(tx, "select 1 from core.gl_account where tenant_id = @t and company_id = @c and account_id = @x", tenantId, ("c", company), ("x", Guid.Parse(account))))
                problems.Add(Error("CONFIG.INVALID_REFERENCE", "P2", "/fields/account_id", ("account_id", account)));
            if (Get("tax_class_id") is { } taxClass && !await ExistsAsync(tx, "select 1 from core.tax_class where tenant_id = @t and tax_class_id = @x and active", tenantId, ("x", Guid.Parse(taxClass))))
                problems.Add(Error("CONFIG.INVALID_REFERENCE", "P2", "/fields/tax_class_id", ("tax_class_id", taxClass)));
            foreach (string country in new[] { "origin_country", "destination_country" })
                if (Get(country) is { } cc && !await ExistsAsync(tx, "select 1 from platform.ref_country where country_code = @x", tenantId, ("x", cc)))
                    problems.Add(Error("CONFIG.INVALID_REFERENCE", "P2", "/fields/" + country, (country, cc)));

            // equal priority on the same key values over an overlapping time is rejected (07, 8.4, 3). A version holds until the next version of its rule starts.
            if (problems.Count == 0)
            {
                await using var cmd = tx.Command(
                    "with eff as (select rule_id, valid_from, priority, transaction_type, origin_country, destination_country, origin_eu, destination_eu, partner_vat_status, account_id, tax_class_id, " +
                    "  least(coalesce(valid_to, 'infinity'::date), coalesce((lead(valid_from) over (partition by rule_id order by valid_from)) - 1, 'infinity'::date)) as eff_to " +
                    "  from core.tax_determination_rule where tenant_id = @t and company_id = @c and rule_id <> @r) " +
                    "select rule_id from eff where priority = @p and transaction_type is not distinct from @k0 and origin_country::text is not distinct from @k1 and destination_country::text is not distinct from @k2 " +
                    "and origin_eu is not distinct from @k3 and destination_eu is not distinct from @k4 and partner_vat_status is not distinct from @k5 and account_id is not distinct from @k6 " +
                    "and tax_class_id is not distinct from @k7 and valid_from <= coalesce(@to, 'infinity'::date) and eff_to >= @from limit 1");
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", company); cmd.Parameters.AddWithValue("r", rule);
                cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Smallint) { Value = f["priority"].Value });
                cmd.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.Date) { Value = validFrom });
                cmd.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.Date) { Value = validTo is { } d ? d : DBNull.Value });
                var types = new[] { NpgsqlDbType.Text, NpgsqlDbType.Text, NpgsqlDbType.Text, NpgsqlDbType.Boolean, NpgsqlDbType.Boolean, NpgsqlDbType.Text, NpgsqlDbType.Uuid, NpgsqlDbType.Uuid };
                for (int k = 0; k < KeyColumns.Length; k++)
                    cmd.Parameters.Add(new NpgsqlParameter("k" + k, types[k]) { Value = f.TryGetValue(KeyColumns[k], out Patch? p) ? p.Value ?? DBNull.Value : DBNull.Value });
                if (await cmd.ExecuteScalarAsync(ct) is Guid other)
                    problems.Add(Error("CONFIG.OVERLAPPING_RULE", "P2", "/fields/priority", ("rule_id", other), ("priority", Get("priority"))));
            }
            if (problems.Count > 0) return CommandResult<TaxRuleResult>.Rejected(problems);

            var cols = new List<string> { "tenant_id", "company_id", "rule_id", "created_by" };
            var vals = new List<string> { "@t", "@c", "@r", "@by" };
            await using (var insert = tx.Command(""))
            {
                insert.Parameters.AddWithValue("t", tenantId); insert.Parameters.AddWithValue("c", company); insert.Parameters.AddWithValue("r", rule);
                insert.Parameters.AddWithValue("by", ctx.Actor);
                for (int p = 0; p < patches.Count; p++) { cols.Add(patches[p].Field.Name); vals.Add($"@p{p}"); AddParam(insert, $"p{p}", patches[p]); }
                insert.CommandText = $"insert into core.tax_determination_rule ({string.Join(", ", cols)}) values ({string.Join(", ", vals)})";
                await insert.ExecuteNonQueryAsync(ct);
            }
            foreach (Guid alt in alts.Order())
            {
                await using var cmd = tx.Command(
                    "insert into core.tax_determination_alternative (tenant_id, company_id, rule_id, valid_from, alt_tax_code_id) values (@t, @c, @r, @f, @a)");
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", company); cmd.Parameters.AddWithValue("r", rule);
                cmd.Parameters.Add(new NpgsqlParameter("f", NpgsqlDbType.Date) { Value = validFrom });
                cmd.Parameters.AddWithValue("a", alt);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            Guid changeSet = Guid.NewGuid(), logId = Guid.NewGuid();
            string key = $"{company:D}/{rule:D}/{validFrom:yyyy-MM-dd}";
            await LogAsync(tx, tenantId, ctx, logId, changeSet, "TAX_DETERMINATION_RULE", key, "CREATE", null, false, null, null, null);
            await CommandSupport.WriteLogAsync(tx, tenantId, company, ctx, command, fingerprint, "TAX_DETERMINATION_RULE", rule, logId, ct);
            await EventAsync(tx, tenantId, ctx, "config.version_created", "TAX_DETERMINATION_RULE", key, "CREATED", patches.Select(p => p.Field.Name).ToList(), ct);
            await tx.CommitAsync(ct);
            return CommandResult<TaxRuleResult>.Ok(new TaxRuleResult(rule, validFrom, alts.Count), logId);
        });
    }

    /// <summary>The version an earlier command wrote: the key is in its master_change_log row.</summary>
    private static async Task<TaxRuleResult?> LoadReplayAsync(TenantTransaction tx, Guid tenantId, Guid commandLogId)
    {
        string? key;
        await using (var cmd = tx.Command("select entity_id from core.master_change_log where tenant_id = @t and command_log_id = @l limit 1"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("l", commandLogId);
            key = await cmd.ExecuteScalarAsync() as string;
        }
        if (key is null) return null;
        string[] parts = key.Split('/');
        Guid company = Guid.Parse(parts[0]), rule = Guid.Parse(parts[1]);
        DateOnly from = DateOnly.ParseExact(parts[2], "yyyy-MM-dd", CultureInfo.InvariantCulture);
        await using var count = tx.Command(
            "select count(*) from core.tax_determination_alternative where tenant_id = @t and company_id = @c and rule_id = @r and valid_from = @f");
        count.Parameters.AddWithValue("t", tenantId); count.Parameters.AddWithValue("c", company); count.Parameters.AddWithValue("r", rule);
        count.Parameters.Add(new NpgsqlParameter("f", NpgsqlDbType.Date) { Value = from });
        return new TaxRuleResult(rule, from, Convert.ToInt32(await count.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
    }

    private static CommandResult<TaxRuleResult> RejectR(CommandError error) => CommandResult<TaxRuleResult>.Rejected([error]);
}
