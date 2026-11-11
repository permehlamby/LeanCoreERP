using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;
using Npgsql;
using NpgsqlTypes;
using static FinanceCore.Data.MasterSupport;

namespace FinanceCore.Data;

public sealed record TaxCodeVersionResult(Guid TaxCodeVersionId, Guid TaxCodeId, DateOnly ValidFrom, int BoxCount);

/// <summary>
/// The VAT configuration commands (07, 8.1 to 8.3, 08, 4.1, 11.1; permission write.config): tax-classes, company-vat-registrations and tax-codes (create, update,
/// deactivate, reactivate), tax-codes:create-version (a version with its boxes, insert-only) and tax-return-boxes (keyed by country, return type and box).
/// Built choices are in 07, 13.2k.
/// </summary>
public sealed class TaxCommands(NpgsqlDataSource source)
{
    public const string Permission = MasterRules.ConfigPermission;
    private readonly MasterTableCommands _generic = new(source);

    // ---------------------------------------------------------------------------------------------------------
    // tax classes
    // ---------------------------------------------------------------------------------------------------------
    private static readonly MasterSpec ClassSpec = new("tax_class", "tax_class_id", "TAX_CLASS", "/tax_class_id", "tax-classes", Permission,
        TaxFields.TaxClass, TaxFields.TaxClass, ["code", "name"], [], CheckClassAsync);

    private static async Task<List<CommandError>> CheckClassAsync(MasterCheckInput i)
    {
        var errors = new List<CommandError>();
        if (i.Changed("code") && await ExistsAsync(i.Tx, "select 1 from core.tax_class where tenant_id = @t and code = @c and tax_class_id is distinct from @id::uuid", i.TenantId,
                ("c", i.Get("code")!), ("id", (object?)i.Id ?? DBNull.Value)))
            errors.Add(Error("CONFIG.DUPLICATE_CODE", "P2", "/fields/code", ("code", i.Get("code"))));
        return errors;
    }

    public Task<CommandResult<MasterResult>> CreateClassAsync(Guid t, CommandContext c, CreateTaxClassRequest r, CancellationToken ct = default) =>
        _generic.CreateAsync(t, c, ClassSpec, r.Fields, MasterRules.Fingerprint(r), ct);
    public Task<CommandResult<MasterResult>> UpdateClassAsync(Guid t, CommandContext c, UpdateTaxClassRequest r, CancellationToken ct = default) =>
        _generic.UpdateAsync(t, c, ClassSpec, r.TaxClassId, r.ExpectedVersion, r.Fields, MasterRules.Fingerprint(r), ct);
    public Task<CommandResult<MasterResult>> DeactivateClassAsync(Guid t, CommandContext c, MasterStatusRequest r, CancellationToken ct = default) =>
        _generic.StatusAsync(t, c, ClassSpec, r, true, ct);
    public Task<CommandResult<MasterResult>> ReactivateClassAsync(Guid t, CommandContext c, MasterStatusRequest r, CancellationToken ct = default) =>
        _generic.StatusAsync(t, c, ClassSpec, r, false, ct);

    // ---------------------------------------------------------------------------------------------------------
    // VAT registrations
    // ---------------------------------------------------------------------------------------------------------
    private static readonly MasterSpec RegistrationSpec = new("company_vat_registration", "registration_id", "VAT_REGISTRATION", "/registration_id", "company-vat-registrations", Permission,
        TaxFields.RegistrationCreate, TaxFields.RegistrationUpdate,
        ["company_id", "country_code", "vat_id", "scheme", "filing_frequency", "correction_method", "return_currency", "valid_from"], [],
        CheckRegistrationAsync,
        async (tx, tenantId, id, _, ct) => await ExistsAsync(tx, "select 1 from core.tax_code where tenant_id = @t and vat_registration_id = @x and active", tenantId, ("x", id))
            ? Error("CONFIG.REFERENCED", "P2", "/registration_id", ("reason", "ACTIVE_TAX_CODE")) : null);

    private static async Task<List<CommandError>> CheckRegistrationAsync(MasterCheckInput i)
    {
        var errors = new List<CommandError>();
        Guid company = Guid.Parse(i.Get("company_id")!);
        if (i.Creating && !await ExistsAsync(i.Tx, "select 1 from core.company where tenant_id = @t and company_id = @x", i.TenantId, ("x", company)))
            errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", "/fields/company_id", ("company_id", company)));
        if (i.Changed("country_code") && !await ExistsAsync(i.Tx, "select 1 from platform.ref_country where country_code = @x", i.TenantId, ("x", i.Get("country_code")!)))
            errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", "/fields/country_code", ("country_code", i.Get("country_code"))));
        if (i.Changed("return_currency") && !await ExistsAsync(i.Tx, "select 1 from core.tenant_currency where tenant_id = @t and currency_code = @x and active", i.TenantId, ("x", i.Get("return_currency")!)))
            errors.Add(Error("CONFIG.CURRENCY_NOT_ENABLED", "P2", "/fields/return_currency", ("currency", i.Get("return_currency"))));
        if (i.Get("valid_to") is { } to && DateOnly.Parse(to, CultureInfo.InvariantCulture) < DateOnly.Parse(i.Get("valid_from")!, CultureInfo.InvariantCulture))
            errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/fields/valid_to", ("reason", "not before valid_from")));
        if ((i.Changed("country_code") || i.Changed("scheme") || i.Changed("valid_from")) && await ExistsAsync(i.Tx,
                "select 1 from core.company_vat_registration where tenant_id = @t and company_id = @c and country_code = @k and scheme = @s and valid_from = @f::date and registration_id is distinct from @id::uuid",
                i.TenantId, ("c", company), ("k", i.Get("country_code")!), ("s", i.Get("scheme")!), ("f", i.Get("valid_from")!), ("id", (object?)i.Id ?? DBNull.Value)))
            errors.Add(Error("CONFIG.DUPLICATE_CODE", "P2", "/fields/valid_from", ("country_code", i.Get("country_code")), ("scheme", i.Get("scheme"))));
        if (!i.Creating && (i.Changed("country_code") || i.Changed("scheme")) && await ExistsAsync(i.Tx,
                "select 1 from core.tax_code where tenant_id = @t and vat_registration_id = @x", i.TenantId, ("x", i.Id!.Value)))
            errors.Add(Error("CONFIG.IMMUTABLE_AFTER_POSTING", "P2", "/fields/" + (i.Changed("country_code") ? "country_code" : "scheme"), ("reason", "a tax code uses the registration")));
        return errors;
    }

    public Task<CommandResult<MasterResult>> CreateRegistrationAsync(Guid t, CommandContext c, CreateVatRegistrationRequest r, CancellationToken ct = default) =>
        _generic.CreateAsync(t, c, RegistrationSpec, r.Fields, MasterRules.Fingerprint(r), ct);
    public Task<CommandResult<MasterResult>> UpdateRegistrationAsync(Guid t, CommandContext c, UpdateVatRegistrationRequest r, CancellationToken ct = default) =>
        _generic.UpdateAsync(t, c, RegistrationSpec, r.RegistrationId, r.ExpectedVersion, r.Fields, MasterRules.Fingerprint(r), ct);
    public Task<CommandResult<MasterResult>> DeactivateRegistrationAsync(Guid t, CommandContext c, MasterStatusRequest r, CancellationToken ct = default) =>
        _generic.StatusAsync(t, c, RegistrationSpec, r, true, ct);
    public Task<CommandResult<MasterResult>> ReactivateRegistrationAsync(Guid t, CommandContext c, MasterStatusRequest r, CancellationToken ct = default) =>
        _generic.StatusAsync(t, c, RegistrationSpec, r, false, ct);

    // ---------------------------------------------------------------------------------------------------------
    // tax codes
    // ---------------------------------------------------------------------------------------------------------
    private static readonly MasterSpec CodeSpec = new("tax_code", "tax_code_id", "TAX_CODE", "/tax_code_id", "tax-codes", Permission,
        TaxFields.TaxCodeCreate, TaxFields.TaxCodeUpdate, ["company_id", "code", "description", "tax_type", "vat_registration_id"], [], CheckCodeAsync);

    private static async Task<List<CommandError>> CheckCodeAsync(MasterCheckInput i)
    {
        var errors = new List<CommandError>();
        Guid company = Guid.Parse(i.Get("company_id")!);
        if (i.Creating && !await ExistsAsync(i.Tx, "select 1 from core.company where tenant_id = @t and company_id = @x", i.TenantId, ("x", company)))
            errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", "/fields/company_id", ("company_id", company)));
        if (i.Changed("vat_registration_id"))
        {
            Guid registration = Guid.Parse(i.Get("vat_registration_id")!);
            if (!await ExistsAsync(i.Tx, "select 1 from core.company_vat_registration where tenant_id = @t and company_id = @c and registration_id = @x and active", i.TenantId, ("c", company), ("x", registration)))
                errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", "/fields/vat_registration_id", ("vat_registration_id", registration)));
        }
        if (i.Changed("code") && await ExistsAsync(i.Tx, "select 1 from core.tax_code where tenant_id = @t and company_id = @c and code = @k and tax_code_id is distinct from @id::uuid", i.TenantId,
                ("c", company), ("k", i.Get("code")!), ("id", (object?)i.Id ?? DBNull.Value)))
            errors.Add(Error("CONFIG.DUPLICATE_CODE", "P2", "/fields/code", ("code", i.Get("code"))));
        if (!i.Creating && (i.Changed("tax_type") || i.Changed("vat_registration_id")) && await ExistsAsync(i.Tx,
                "select 1 from core.tax_code_version where tenant_id = @t and tax_code_id = @x", i.TenantId, ("x", i.Id!.Value)))
            errors.Add(Error("CONFIG.IMMUTABLE_AFTER_POSTING", "P2", "/fields/" + (i.Changed("tax_type") ? "tax_type" : "vat_registration_id"), ("reason", "the code has a version")));
        return errors;
    }

    public Task<CommandResult<MasterResult>> CreateCodeAsync(Guid t, CommandContext c, CreateTaxCodeRequest r, CancellationToken ct = default) =>
        _generic.CreateAsync(t, c, CodeSpec, r.Fields, MasterRules.Fingerprint(r), ct);
    public Task<CommandResult<MasterResult>> UpdateCodeAsync(Guid t, CommandContext c, UpdateTaxCodeRequest r, CancellationToken ct = default) =>
        _generic.UpdateAsync(t, c, CodeSpec, r.TaxCodeId, r.ExpectedVersion, r.Fields, MasterRules.Fingerprint(r), ct);
    public Task<CommandResult<MasterResult>> DeactivateCodeAsync(Guid t, CommandContext c, MasterStatusRequest r, CancellationToken ct = default) =>
        _generic.StatusAsync(t, c, CodeSpec, r, true, ct);
    public Task<CommandResult<MasterResult>> ReactivateCodeAsync(Guid t, CommandContext c, MasterStatusRequest r, CancellationToken ct = default) =>
        _generic.StatusAsync(t, c, CodeSpec, r, false, ct);

    // ---------------------------------------------------------------------------------------------------------
    // tax-codes:create-version
    // ---------------------------------------------------------------------------------------------------------
    public Task<CommandResult<TaxCodeVersionResult>> CreateVersionAsync(Guid tenantId, CommandContext ctx, CreateTaxCodeVersionRequest req, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(Permission)) return Task.FromResult(RejectV(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", Permission))));
        var errors = ValidateContext(ctx);
        if (req.TaxCodeId == Guid.Empty) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/tax_code_id", ("reason", "required")));
        Require(req.Fields, "/fields", errors, "valid_from", "rate", "vat_account_id");
        var patches = Validate(req.Fields, TaxFields.TaxCodeVersion, "/fields", errors);
        var boxes = req.Boxes ?? [];
        var seen = new HashSet<string>();
        for (int b = 0; b < boxes.Count; b++)
        {
            TaxBoxMapping m = boxes[b];
            if (m.LineSide is not ("PRIMARY" or "COUNTER")) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", $"/boxes/{b}/line_side", ("reason", "PRIMARY or COUNTER")));
            if (!TaxFields.ReturnTypes.Contains(m.ReturnType)) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", $"/boxes/{b}/return_type", ("reason", "one of " + string.Join(", ", TaxFields.ReturnTypes))));
            if (string.IsNullOrWhiteSpace(m.BoxCode) || m.BoxCode.Length > 50) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", $"/boxes/{b}/box_code", ("reason", "1 to 50 characters")));
            if (m.AmountKind is not ("BASE" or "TAX")) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", $"/boxes/{b}/amount_kind", ("reason", "BASE or TAX")));
            if (m.Sign is not (1 or -1)) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", $"/boxes/{b}/sign", ("reason", "1 or -1")));
            if (!seen.Add($"{m.LineSide}/{m.ReturnType}/{m.BoxCode}")) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", $"/boxes/{b}", ("reason", "the same box twice for one side")));
        }
        if (errors.Count > 0) return Task.FromResult(CommandResult<TaxCodeVersionResult>.Rejected(errors));
        byte[] fingerprint = MasterRules.Fingerprint(req);
        return RunAsync(async () =>
        {
            await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Config, ct: ct);
            CommandSupport.EarlierCommand? earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
            if (earlier is not null)
            {
                if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint)) return RejectV(Error("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
                TaxCodeVersionResult? replay = earlier.EntityId is null ? null : await LoadVersionAsync(tx, tenantId, earlier.EntityId.Value);
                return replay is null ? CommandResult<TaxCodeVersionResult>.Failed(Error("SYSTEM.REPLAY_RESULT_MISSING", "P1", "")) : CommandResult<TaxCodeVersionResult>.Ok(replay, earlier.CommandLogId, replay: true);
            }

            // the code, with the type and the country of its registration
            Guid company; string type, country; bool active;
            await using (var cmd = tx.Command(
                "select c.company_id, c.tax_type, r.country_code, c.active from core.tax_code c join core.company_vat_registration r on r.tenant_id = c.tenant_id " +
                "and r.company_id = c.company_id and r.registration_id = c.vat_registration_id where c.tenant_id = @t and c.tax_code_id = @x"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("x", req.TaxCodeId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) return RejectV(Error("CONFIG.INVALID_REFERENCE", "P2", "/tax_code_id", ("tax_code_id", req.TaxCodeId)));
                company = r.GetGuid(0); type = r.GetString(1); country = r.GetString(2).Trim(); active = r.GetBoolean(3);
            }
            var problems = new List<CommandError>();
            if (!active) problems.Add(Error("CONFIG.INVALID_REFERENCE", "P2", "/tax_code_id", ("reason", "INACTIVE")));

            var f = patches.ToDictionary(p => p.Field.Name, p => p.Text);
            string? Get(string name) => f.GetValueOrDefault(name);
            DateOnly validFrom = DateOnly.Parse(Get("valid_from")!, CultureInfo.InvariantCulture);
            if (Get("valid_to") is { } to && DateOnly.Parse(to, CultureInfo.InvariantCulture) < validFrom)
                problems.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/fields/valid_to", ("reason", "not before valid_from")));
            await using (var cmd = tx.Command("select max(valid_from) from core.tax_code_version where tenant_id = @t and tax_code_id = @x"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("x", req.TaxCodeId);
                if (await cmd.ExecuteScalarAsync(ct) is DateTime latest && validFrom <= DateOnly.FromDateTime(latest))
                    problems.Add(Error("CONFIG.VALID_FROM_NOT_LATER", "P2", "/fields/valid_from", ("valid_from", validFrom), ("latest_valid_from", DateOnly.FromDateTime(latest))));
            }
            decimal rate = decimal.Parse(Get("rate")!, CultureInfo.InvariantCulture);
            bool reverse = Get("reverse_charge") == "true";
            if (type is "EXEMPT" or "ZERO_RATED" && rate != 0m) problems.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/fields/rate", ("reason", "the rate of an exempt or zero-rated code is 0")));
            if (reverse && type != "INPUT") problems.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/fields/reverse_charge", ("reason", "only an input code is reverse charge")));
            if (reverse && Get("reverse_charge_account_id") is null) problems.Add(Error("SCHEMA.REQUIRED_FIELD_MISSING", "P1", "/fields/reverse_charge_account_id"));
            if (!reverse && Get("reverse_charge_account_id") is not null) problems.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/fields/reverse_charge_account_id", ("reason", "only a reverse-charge version has one")));
            if ((type is "EXEMPT" or "ZERO_RATED" || reverse) && Get("en16931_vat_category") is null)
                problems.Add(Error("SCHEMA.REQUIRED_FIELD_MISSING", "P1", "/fields/en16931_vat_category"));
            if (type != "INPUT")
            {
                if (Get("deductible_pct") is { } pct && decimal.Parse(pct, CultureInfo.InvariantCulture) != 100m) problems.Add(Error("CONFIG.DEDUCTION_ON_NON_INPUT_CODE", "P2", "/fields/deductible_pct"));
                if (Get("deduction_key") is not null) problems.Add(Error("CONFIG.DEDUCTION_ON_NON_INPUT_CODE", "P2", "/fields/deduction_key"));
                if (Get("deduction_cap") is { } cap && cap != "NONE") problems.Add(Error("CONFIG.DEDUCTION_ON_NON_INPUT_CODE", "P2", "/fields/deduction_cap"));
            }
            if (Get("deduction_key") is not null) problems.Add(Error("LIMIT.DEDUCTION_KEY_NOT_BUILT", "P2", "/fields/deduction_key"));
            foreach (string account in new[] { "vat_account_id", "reverse_charge_account_id" })
                if (Get(account) is { } id)
                {
                    string? accountType = null;
                    await using var cmd = tx.Command("select account_type from core.gl_account where tenant_id = @t and company_id = @c and account_id = @x");
                    cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", company); cmd.Parameters.AddWithValue("x", Guid.Parse(id));
                    accountType = await cmd.ExecuteScalarAsync(ct) as string;
                    if (accountType is null) problems.Add(Error("CONFIG.INVALID_REFERENCE", "P2", "/fields/" + account, (account, id), ("reason", "NOT_FOUND")));
                    else if (accountType is not ("ASSET" or "LIABILITY" or "EQUITY")) problems.Add(Error("CONFIG.INVALID_REFERENCE", "P2", "/fields/" + account, (account, id), ("reason", "NOT_BALANCE_SHEET")));
                }
            for (int b = 0; b < boxes.Count; b++)
            {
                TaxBoxMapping m = boxes[b];
                if (!await ExistsAsync(tx, "select 1 from core.tax_return_box where tenant_id = @t and country_code = @k and return_type = @r and box_code = @b and active", tenantId,
                        ("k", country), ("r", m.ReturnType), ("b", m.BoxCode)))
                    problems.Add(Error("CONFIG.INVALID_REFERENCE", "P2", $"/boxes/{b}/box_code", ("box_code", m.BoxCode), ("country_code", country)));
                if (m.LineSide == "COUNTER" && !(reverse && type == "INPUT"))
                    problems.Add(Error("CONFIG.INVALID_REFERENCE", "P2", $"/boxes/{b}/line_side", ("reason", "the counter side belongs to a reverse-charge version of an input code")));
            }
            if (problems.Count > 0) return CommandResult<TaxCodeVersionResult>.Rejected(problems);

            Guid versionId = Guid.NewGuid(), logId = Guid.NewGuid();
            var cols = new List<string> { "tenant_id", "company_id", "tax_code_id", "tax_code_version_id", "created_by" };
            var vals = new List<string> { "@t", "@c", "@x", "@v", "@by" };
            await using (var insert = tx.Command(""))
            {
                insert.Parameters.AddWithValue("t", tenantId); insert.Parameters.AddWithValue("c", company); insert.Parameters.AddWithValue("x", req.TaxCodeId);
                insert.Parameters.AddWithValue("v", versionId); insert.Parameters.AddWithValue("by", ctx.Actor);
                for (int p = 0; p < patches.Count; p++) { cols.Add(patches[p].Field.Name); vals.Add($"@p{p}"); AddParam(insert, $"p{p}", patches[p]); }
                insert.CommandText = $"insert into core.tax_code_version ({string.Join(", ", cols)}) values ({string.Join(", ", vals)})";
                await insert.ExecuteNonQueryAsync(ct);
            }
            foreach (TaxBoxMapping m in boxes)
            {
                await using var cmd = tx.Command(
                    "insert into core.tax_code_version_box (tenant_id, company_id, tax_code_id, valid_from, line_side, return_type, box_code, amount_kind, sign) " +
                    "values (@t, @c, @x, @f, @s, @r, @b, @k, @g)");
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", company); cmd.Parameters.AddWithValue("x", req.TaxCodeId);
                cmd.Parameters.Add(new NpgsqlParameter("f", NpgsqlDbType.Date) { Value = validFrom });
                cmd.Parameters.AddWithValue("s", m.LineSide); cmd.Parameters.AddWithValue("r", m.ReturnType); cmd.Parameters.AddWithValue("b", m.BoxCode);
                cmd.Parameters.AddWithValue("k", m.AmountKind); cmd.Parameters.Add(new NpgsqlParameter("g", NpgsqlDbType.Smallint) { Value = (short)m.Sign });
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await CommandSupport.WriteLogAsync(tx, tenantId, company, ctx, "tax-codes:create-version", fingerprint, "TAX_CODE_VERSION", versionId, logId, ct);
            await EventAsync(tx, tenantId, ctx, "config.version_created", "TAX_CODE_VERSION", $"{company:D}/{req.TaxCodeId:D}/{validFrom:yyyy-MM-dd}", "CREATED",
                patches.Select(p => p.Field.Name).ToList(), ct);
            await tx.CommitAsync(ct);
            return CommandResult<TaxCodeVersionResult>.Ok(new TaxCodeVersionResult(versionId, req.TaxCodeId, validFrom, boxes.Count), logId);
        });
    }

    private static async Task<TaxCodeVersionResult?> LoadVersionAsync(TenantTransaction tx, Guid tenantId, Guid versionId)
    {
        await using var cmd = tx.Command(
            "select v.tax_code_id, v.valid_from, (select count(*) from core.tax_code_version_box b where b.tenant_id = v.tenant_id and b.company_id = v.company_id " +
            "and b.tax_code_id = v.tax_code_id and b.valid_from = v.valid_from) from core.tax_code_version v where v.tenant_id = @t and v.tax_code_version_id = @x");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("x", versionId);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? new TaxCodeVersionResult(versionId, r.GetGuid(0), r.GetFieldValue<DateOnly>(1), (int)r.GetInt64(2)) : null;
    }

    // ---------------------------------------------------------------------------------------------------------
    // tax-return-boxes: the key is country, return type and box code
    // ---------------------------------------------------------------------------------------------------------
    private static Guid KeyGuid(string country, string type, string box) => new(MD5.HashData(Encoding.UTF8.GetBytes($"{country}/{type}/{box}")));
    private static string KeyText(string country, string type, string box) => $"{country}/{type}/{box}";
    private static readonly string[] BoxColumns = ["name", "box_group", "active", "row_version"];
    private const string BoxWhere = "country_code = @k and return_type = @r and box_code = @b";

    private static List<CommandError> BoxKeyErrors(CommandContext ctx, string country, string type, string box)
    {
        var errors = ValidateContext(ctx);
        if (country is not { Length: 2 } || !country.All(c => c is >= 'A' and <= 'Z')) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/country_code", ("reason", "two upper-case letters")));
        if (!TaxFields.ReturnTypes.Contains(type)) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/return_type", ("reason", "one of " + string.Join(", ", TaxFields.ReturnTypes))));
        if (string.IsNullOrWhiteSpace(box) || box.Length > 50) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/box_code", ("reason", "1 to 50 characters")));
        return errors;
    }

    public Task<CommandResult<MasterResult>> CreateBoxAsync(Guid tenantId, CommandContext ctx, CreateTaxReturnBoxRequest req, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(Permission)) return Task.FromResult(Reject(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", Permission))));
        var errors = BoxKeyErrors(ctx, req.CountryCode, req.ReturnType, req.BoxCode);
        Require(req.Fields, "/fields", errors, "name");
        var patches = Validate(req.Fields, TaxFields.ReturnBoxFields, "/fields", errors);
        if (errors.Count > 0) return Task.FromResult(CommandResult<MasterResult>.Rejected(errors));
        byte[] fingerprint = MasterRules.Fingerprint(req);
        return RunAsync(async () =>
        {
            await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Config, ct: ct);
            var replayed = await BoxReplayOrNullAsync(tx, tenantId, ctx, req.CountryCode, req.ReturnType, req.BoxCode, fingerprint, ct);
            if (replayed is not null) return replayed;
            if (!await ExistsAsync(tx, "select 1 from platform.ref_country where country_code = @x", tenantId, ("x", req.CountryCode)))
                return Reject(Error("CONFIG.INVALID_REFERENCE", "P2", "/country_code", ("country_code", req.CountryCode)));
            if (await ExistsAsync(tx, "select 1 from core.tax_return_box where tenant_id = @t and " + BoxWhere, tenantId, ("k", req.CountryCode), ("r", req.ReturnType), ("b", req.BoxCode)))
                return Reject(Error("CONFIG.DUPLICATE_CODE", "P2", "/box_code", ("box_code", req.BoxCode)));
            Guid changeSet = Guid.NewGuid(), logId = Guid.NewGuid();
            await InsertAsync(tx, "tax_return_box", [("country_code", NpgsqlDbType.Text, req.CountryCode), ("return_type", NpgsqlDbType.Text, req.ReturnType), ("box_code", NpgsqlDbType.Text, req.BoxCode)],
                patches, ctx, tenantId);
            string key = KeyText(req.CountryCode, req.ReturnType, req.BoxCode);
            await LogAsync(tx, tenantId, ctx, logId, changeSet, "TAX_RETURN_BOX", key, "CREATE", null, false, null, null, null);
            await CommandSupport.WriteLogAsync(tx, tenantId, null, ctx, "tax-return-boxes:create", fingerprint, "TAX_RETURN_BOX", KeyGuid(req.CountryCode, req.ReturnType, req.BoxCode), logId, ct);
            var names = patches.Select(p => p.Field.Name).ToList();
            await EventAsync(tx, tenantId, ctx, "master.created", "TAX_RETURN_BOX", key, "CREATED", names, ct);
            await tx.CommitAsync(ct);
            return CommandResult<MasterResult>.Ok(new MasterResult(KeyGuid(req.CountryCode, req.ReturnType, req.BoxCode), 1, changeSet, names), logId);
        });
    }

    public Task<CommandResult<MasterResult>> UpdateBoxAsync(Guid tenantId, CommandContext ctx, UpdateTaxReturnBoxRequest req, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(Permission)) return Task.FromResult(Reject(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", Permission))));
        var errors = BoxKeyErrors(ctx, req.CountryCode, req.ReturnType, req.BoxCode);
        if (req.ExpectedVersion < 1) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/expected_version", ("reason", "required")));
        var patches = Validate(req.Fields, TaxFields.ReturnBoxFields, "/fields", errors);
        if (errors.Count > 0) return Task.FromResult(CommandResult<MasterResult>.Rejected(errors));
        byte[] fingerprint = MasterRules.Fingerprint(req);
        return RunAsync(async () =>
        {
            await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Config, ct: ct);
            var replayed = await BoxReplayOrNullAsync(tx, tenantId, ctx, req.CountryCode, req.ReturnType, req.BoxCode, fingerprint, ct);
            if (replayed is not null) return replayed;
            var current = await ReadRowAsync(tx, "tax_return_box", BoxWhere, BoxColumns, true, tenantId, ("k", req.CountryCode), ("r", req.ReturnType), ("b", req.BoxCode));
            if (current is null) return Reject(Error("CONFIG.INVALID_REFERENCE", "P2", "/box_code", ("box_code", req.BoxCode)));
            long version = long.Parse(current["row_version"]!, CultureInfo.InvariantCulture);
            if (version != req.ExpectedVersion)
                return Reject(Error("CONFIG.ROW_VERSION_MISMATCH", "P2", "/expected_version", ("current_version", version), ("expected_version", req.ExpectedVersion)));
            var changes = patches.Where(p => p.Text != current[p.Field.Name]).ToList();
            if (changes.Count == 0) return Reject(Error("SCHEMA.NO_EFFECTIVE_CHANGE", "P6", "/fields"));
            Guid changeSet = Guid.NewGuid(), logId = Guid.NewGuid();
            string key = KeyText(req.CountryCode, req.ReturnType, req.BoxCode);
            await MasterSupport.UpdateAsync(tx, "tax_return_box", BoxWhere, changes, ctx, tenantId, ("k", req.CountryCode), ("r", req.ReturnType), ("b", req.BoxCode));
            foreach (Patch p in changes) await LogAsync(tx, tenantId, ctx, logId, changeSet, "TAX_RETURN_BOX", key, "UPDATE", p.Field.Name, false, current[p.Field.Name], p.Text, null);
            await CommandSupport.WriteLogAsync(tx, tenantId, null, ctx, "tax-return-boxes:update", fingerprint, "TAX_RETURN_BOX", KeyGuid(req.CountryCode, req.ReturnType, req.BoxCode), logId, ct);
            var names = changes.Select(p => p.Field.Name).ToList();
            await EventAsync(tx, tenantId, ctx, "master.changed", "TAX_RETURN_BOX", key, "CHANGED", names, ct);
            await tx.CommitAsync(ct);
            return CommandResult<MasterResult>.Ok(new MasterResult(KeyGuid(req.CountryCode, req.ReturnType, req.BoxCode), version + 1, changeSet, names), logId);
        });
    }

    public Task<CommandResult<MasterResult>> DeactivateBoxAsync(Guid t, CommandContext c, TaxReturnBoxStatusRequest r, CancellationToken ct = default) => BoxStatusAsync(t, c, r, true, ct);
    public Task<CommandResult<MasterResult>> ReactivateBoxAsync(Guid t, CommandContext c, TaxReturnBoxStatusRequest r, CancellationToken ct = default) => BoxStatusAsync(t, c, r, false, ct);

    private Task<CommandResult<MasterResult>> BoxStatusAsync(Guid tenantId, CommandContext ctx, TaxReturnBoxStatusRequest req, bool deactivate, CancellationToken ct)
    {
        string command = "tax-return-boxes:" + (deactivate ? "deactivate" : "reactivate");
        if (!ctx.Permissions.Contains(Permission)) return Task.FromResult(Reject(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", Permission))));
        var errors = BoxKeyErrors(ctx, req.CountryCode, req.ReturnType, req.BoxCode);
        if (req.ExpectedVersion < 1) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/expected_version", ("reason", "required")));
        if (string.IsNullOrWhiteSpace(req.ReasonCode)) errors.Add(Error("SCHEMA.REQUIRED_FIELD_MISSING", "P1", "/reason_code"));
        else if (req.ReasonCode.Length > 50) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/reason_code", ("reason", "at most 50 characters")));
        if (errors.Count > 0) return Task.FromResult(CommandResult<MasterResult>.Rejected(errors));
        byte[] fingerprint = MasterRules.Fingerprint(command, req);
        return RunAsync(async () =>
        {
            await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Config, ct: ct);
            var replayed = await BoxReplayOrNullAsync(tx, tenantId, ctx, req.CountryCode, req.ReturnType, req.BoxCode, fingerprint, ct);
            if (replayed is not null) return replayed;
            var row = await ReadRowAsync(tx, "tax_return_box", BoxWhere, BoxColumns, true, tenantId, ("k", req.CountryCode), ("r", req.ReturnType), ("b", req.BoxCode));
            if (row is null) return Reject(Error("CONFIG.INVALID_REFERENCE", "P2", "/box_code", ("box_code", req.BoxCode)));
            long version = long.Parse(row["row_version"]!, CultureInfo.InvariantCulture);
            if (version != req.ExpectedVersion)
                return Reject(Error("CONFIG.ROW_VERSION_MISMATCH", "P2", "/expected_version", ("current_version", version), ("expected_version", req.ExpectedVersion)));
            if ((row["active"] == "true") != deactivate) return Reject(Error("SCHEMA.NO_EFFECTIVE_CHANGE", "P6", "/box_code", ("active", row["active"])));
            // a box that a version of a code of the country maps to stays (07, 2.3)
            if (deactivate && await ExistsAsync(tx,
                    "select 1 from core.tax_code_version_box b join core.tax_code c on c.tenant_id = b.tenant_id and c.company_id = b.company_id and c.tax_code_id = b.tax_code_id " +
                    "join core.company_vat_registration r on r.tenant_id = c.tenant_id and r.company_id = c.company_id and r.registration_id = c.vat_registration_id " +
                    "where b.tenant_id = @t and r.country_code = @k and b.return_type = @r and b.box_code = @b", tenantId, ("k", req.CountryCode), ("r", req.ReturnType), ("b", req.BoxCode)))
                return Reject(Error("CONFIG.REFERENCED", "P2", "/box_code", ("reason", "A_VERSION_MAPS_TO_IT")));
            await using (var cmd = tx.Command("update core.tax_return_box set active = @a, row_version = row_version + 1, updated_at = now(), updated_by = @by where tenant_id = @t and " + BoxWhere))
            {
                cmd.Parameters.AddWithValue("a", !deactivate); cmd.Parameters.AddWithValue("by", ctx.Actor); cmd.Parameters.AddWithValue("t", tenantId);
                cmd.Parameters.AddWithValue("k", req.CountryCode); cmd.Parameters.AddWithValue("r", req.ReturnType); cmd.Parameters.AddWithValue("b", req.BoxCode);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            Guid changeSet = Guid.NewGuid(), logId = Guid.NewGuid();
            string key = KeyText(req.CountryCode, req.ReturnType, req.BoxCode);
            await LogAsync(tx, tenantId, ctx, logId, changeSet, "TAX_RETURN_BOX", key, deactivate ? "DEACTIVATE" : "REACTIVATE", null, false, null, null, req.ReasonCode);
            await CommandSupport.WriteLogAsync(tx, tenantId, null, ctx, command, fingerprint, "TAX_RETURN_BOX", KeyGuid(req.CountryCode, req.ReturnType, req.BoxCode), logId, ct);
            await EventAsync(tx, tenantId, ctx, deactivate ? "master.deactivated" : "master.reactivated", "TAX_RETURN_BOX", key, deactivate ? "DEACTIVATED" : "REACTIVATED", ["active"], ct);
            await tx.CommitAsync(ct);
            return CommandResult<MasterResult>.Ok(new MasterResult(KeyGuid(req.CountryCode, req.ReturnType, req.BoxCode), version + 1, changeSet, ["active"]), logId);
        });
    }

    private static async Task<CommandResult<MasterResult>?> BoxReplayOrNullAsync(
        TenantTransaction tx, Guid tenantId, CommandContext ctx, string country, string type, string box, byte[] fingerprint, CancellationToken ct)
    {
        CommandSupport.EarlierCommand? earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
        if (earlier is null) return null;
        if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint)) return Reject(Error("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
        Guid? set = null;
        await using (var cmd = tx.Command("select change_set_id from core.master_change_log where tenant_id = @t and command_log_id = @l limit 1"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("l", earlier.CommandLogId);
            if (await cmd.ExecuteScalarAsync(ct) is Guid g) set = g;
        }
        var row = set is null ? null : await ReadRowAsync(tx, "tax_return_box", BoxWhere, BoxColumns, false, tenantId, ("k", country), ("r", type), ("b", box));
        return row is null ? CommandResult<MasterResult>.Failed(Error("SYSTEM.REPLAY_RESULT_MISSING", "P1", ""))
            : CommandResult<MasterResult>.Ok(new MasterResult(KeyGuid(country, type, box), long.Parse(row["row_version"]!, CultureInfo.InvariantCulture), set!.Value, []), earlier.CommandLogId, replay: true);
    }

    // ---------------------------------------------------------------------------------------------------------
    private static async Task<bool> ExistsAsync(TenantTransaction tx, string sql, Guid tenantId, params (string, object)[] keys)
    {
        await using var cmd = tx.Command(sql);
        if (sql.Contains("@t", StringComparison.Ordinal)) cmd.Parameters.AddWithValue("t", tenantId);
        foreach (var (n, v) in keys) cmd.Parameters.AddWithValue(n, v);
        return await cmd.ExecuteScalarAsync() is not null;
    }

    private static CommandResult<MasterResult> Reject(CommandError error) => CommandResult<MasterResult>.Rejected([error]);
    private static CommandResult<TaxCodeVersionResult> RejectV(CommandError error) => CommandResult<TaxCodeVersionResult>.Rejected([error]);
}
