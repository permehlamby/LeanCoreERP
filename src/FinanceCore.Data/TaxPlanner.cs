using System.Globalization;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Hashing;
using FinanceCore.Core.Posting;
using Npgsql;
using NpgsqlTypes;

namespace FinanceCore.Data;

/// <summary>One tax line to be posted, with the base lines it covers (02, 7.1 and 7.2). The base amounts in the other currencies and the amounts of the
/// line itself are filled in by the poster once the rates are known.</summary>
internal sealed record TaxLineSeed(
    Guid CodeId, Guid VersionId, string TaxType, decimal Rate, Guid AccountId, decimal TaxTc, decimal? SuppliedTc,
    IReadOnlyList<(int Index, decimal BaseTc)> Bases, Guid DeterminedCodeId, Guid? ClassId, string Origin, string Destination,
    string? PartnerVatStatus, DateOnly TaxDate, bool IsOverride, bool OriginEu, bool DestinationEu, string VatCurrency, string RoundingMode,
    decimal GrossTc, decimal DeductiblePct, string? DeductionKey, decimal? KeyRate, decimal Share, decimal? CapAmountTc, IReadOnlyList<(int Index, decimal Tc)> Nondeductible);

/// <summary>What the tax of a document comes to: the tax lines, the difference to the supplied tax (it goes to the TAX_ROUNDING account) and the code each
/// taxed base line ended up with.</summary>
internal sealed record TaxPlan(IReadOnlyList<TaxLineSeed> Lines, decimal RoundingTc, IReadOnlyDictionary<int, Guid> BaseCodes)
{
    public static readonly TaxPlan None = new([], 0m, new Dictionary<int, Guid>());
    public bool Any => Lines.Count > 0;
}

/// <summary>
/// The tax of a posting (06, 5.2 P4 and P5, 07, 8.4 and 8.5, 08, 3.1, 5.1 and 5.2): which base lines are taxed, the tax code of each by the determination
/// rules, the version of the code in force, the tax amount per line or per document, and the reverse-charge pair. Built choices are in 07, 13.2l.
/// The split of an input tax (19) is built. Not built, answered with a LIMIT code: a VAT rate type other than BOOKING, a VAT currency that is neither the local nor the
/// reporting currency, a non-deductible part on a line of an account with open items.
/// </summary>
internal static class TaxPlanner
{
    private sealed record Rule(Guid RuleId, DateOnly ValidFrom, short Priority, Guid TaxCodeId, string? TransactionType, string? Origin, string? Destination,
        bool? OriginEu, bool? DestinationEu, string? PartnerVatStatus, Guid? AccountId, Guid? ClassId)
    {
        public int Specificity => new object?[] { TransactionType, Origin, Destination, OriginEu, DestinationEu, PartnerVatStatus, AccountId, ClassId }.Count(k => k is not null);
    }

    private sealed record Version(Guid VersionId, decimal Rate, Guid VatAccountId, bool ReverseCharge, Guid? ReverseChargeAccountId,
        string TaxType, string ReturnCurrency, Guid RegistrationId, decimal DeductiblePct, string? DeductionKey, string DeductionCap);

    private sealed record Resolved(int Index, decimal BaseTc, decimal? SuppliedTc, Version Version, Guid CodeId, Guid DeterminedCodeId, Guid? ClassId, string Origin,
        string Destination, string? PartnerVatStatus, DateOnly TaxDate, bool IsOverride, bool OriginEu, bool DestinationEu, decimal? DeductibleTc);

    internal static async Task<TaxPlan> PlanAsync(
        TenantTransaction tx, Guid tenantId, PostJournalDocumentRequest req, JournalDocumentPoster.CompanyRow company,
        IReadOnlyDictionary<Guid, JournalDocumentPoster.AccountRow> accounts, int tcScale, string? reportingCurrency,
        List<CommandError> errors, List<CommandError> warnings, CancellationToken ct)
    {
        bool sales = req.DocumentType is "AR_INVOICE" or "AR_CREDIT", purchase = req.DocumentType is "AP_INVOICE" or "AP_CREDIT";
        string? transactionType = sales ? "SALES" : purchase ? "PURCHASE" : null;

        // which base lines are taxed: one that names a code, or one on an account that needs a code (07, 6.1, ADR-029)
        var taxed = new List<int>();
        for (int i = 0; i < req.Lines.Count; i++)
        {
            RequestLine l = req.Lines[i];
            if (!accounts.TryGetValue(l.AccountId, out var account)) continue;
            if (l.TaxCodeId is not null && account.TaxCodeRule == "FORBIDDEN")
                errors.Add(JournalDocumentPoster.Error("TAX.CODE_NOT_PERMITTED", "P4", $"lines[{i}].tax_code_id", ("reason", "ACCOUNT_FORBIDS_TAX_CODE"), ("account_no", account.AccountNo)));
            else if (l.TaxCodeId is not null || account.TaxCodeRule == "MANDATORY") taxed.Add(i);
            else if (l.TaxAmountTc is not null)
                errors.Add(JournalDocumentPoster.Error("SCHEMA.INVALID_VALUE", "P1", $"lines[{i}].tax_amount_tc", ("reason", "the line is not taxed")));
        }
        if (errors.Count > 0 || taxed.Count == 0) return TaxPlan.None;

        // the settings of the company in force
        string roundingMode; decimal tolerance;
        await using (var cmd = tx.Command(
            "select tax_rounding_mode, tax_amount_tolerance from core.company_setting_version where tenant_id = @t and company_id = @c and effective_from <= now() order by effective_from desc limit 1"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) { roundingMode = r.GetString(0); tolerance = r.GetDecimal(1); } else { roundingMode = "PER_DOCUMENT"; tolerance = 0m; }
        }

        string? partnerCountry = null;
        if (req.PartnerId is { } partner)
        {
            await using var cmd = tx.Command("select country_code from core.business_partner_pii where tenant_id = @t and partner_id = @p");
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("p", partner);
            partnerCountry = (await cmd.ExecuteScalarAsync(ct) as string)?.Trim();
        }

        List<Rule> rules = await ReadRulesAsync(tx, tenantId, req.CompanyId, req.PostingDate, ct);
        var resolved = new List<Resolved>();
        var euCache = new Dictionary<(string, DateOnly), bool>();
        var versionCache = new Dictionary<Guid, (Version? Version, bool Active, bool RegistrationUsable, string? Problem)>();

        foreach (int i in taxed)
        {
            RequestLine l = req.Lines[i];
            JournalDocumentPoster.AccountRow account = accounts[l.AccountId];
            string at = $"lines[{i}]";
            DateOnly taxDate = l.TaxDate ?? req.TaxDate ?? req.DocumentDate;
            Guid? classId = l.TaxClassId ?? account.DefaultTaxClassId;

            // origin and destination: the line, else the document, else the default of the direction (07, Q1). Without a partner there is none (07, 8.4, F1)
            string? origin = l.OriginCountry ?? req.OriginCountry, destination = l.DestinationCountry ?? req.DestinationCountry;
            if (req.PartnerId is not null && partnerCountry is not null)
            {
                if (sales) { origin ??= company.Country; destination ??= partnerCountry; }
                else if (purchase) { origin ??= partnerCountry; destination ??= company.Country; }
            }
            if (origin is null || destination is null)
            {
                errors.Add(JournalDocumentPoster.Error("TAX.ORIGIN_DESTINATION_REQUIRED", "P4", at, ("origin_country", origin), ("destination_country", destination)));
                continue;
            }
            bool originEu = await IsEuAsync(tx, origin, taxDate, euCache, ct), destinationEu = await IsEuAsync(tx, destination, taxDate, euCache, ct);

            // the VAT status of the partner in the destination country at the tax date: absent, inactive or expired is NOT_REGISTERED (08, 3.1)
            string partnerStatus = "NOT_REGISTERED";
            if (req.PartnerId is { } p)
            {
                await using var cmd = tx.Command(
                    "select vat_status from core.partner_tax_registration where tenant_id = @t and partner_id = @p and country_code = @k and active and valid_from <= @d " +
                    "and (valid_to is null or valid_to >= @d) order by valid_from desc limit 1");
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("p", p); cmd.Parameters.AddWithValue("k", destination);
                cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Date) { Value = taxDate });
                partnerStatus = await cmd.ExecuteScalarAsync(ct) as string ?? "NOT_REGISTERED";
            }

            // determination (07, 8.4): lowest priority, then most specific, then lowest rule id
            Rule? winner = rules
                .Where(r => (r.TransactionType is null || r.TransactionType == transactionType) && (r.Origin is null || r.Origin == origin) && (r.Destination is null || r.Destination == destination)
                         && (r.OriginEu is null || r.OriginEu == originEu) && (r.DestinationEu is null || r.DestinationEu == destinationEu)
                         && (r.PartnerVatStatus is null || r.PartnerVatStatus == partnerStatus) && (r.AccountId is null || r.AccountId == l.AccountId) && (r.ClassId is null || r.ClassId == classId))
                .OrderBy(r => r.Priority).ThenByDescending(r => r.Specificity).ThenBy(r => r.RuleId.ToString("D"), StringComparer.Ordinal).FirstOrDefault();
            if (winner is null)
            {
                errors.Add(JournalDocumentPoster.Error("TAX.NO_RULE_MATCHED", "P4", at, ("transaction_type", transactionType), ("origin_country", origin),
                    ("destination_country", destination), ("origin_eu", originEu), ("destination_eu", destinationEu), ("partner_vat_status", partnerStatus),
                    ("account_id", l.AccountId), ("tax_class_id", classId)));
                continue;
            }

            Guid code = winner.TaxCodeId;
            bool isOverride = false;
            if (l.TaxCodeId is { } supplied && supplied != winner.TaxCodeId)
            {
                if (!await IsAlternativeAsync(tx, tenantId, req.CompanyId, winner, supplied, ct))
                {
                    errors.Add(JournalDocumentPoster.Error("TAX.CODE_NOT_PERMITTED", "P4", $"{at}.tax_code_id", ("reason", "NOT_AN_ALTERNATIVE"), ("determined_tax_code_id", winner.TaxCodeId), ("tax_code_id", supplied)));
                    continue;
                }
                code = supplied; isOverride = true;
                warnings.Add(JournalDocumentPoster.Error("TAX.CODE_DIFFERS_FROM_DETERMINED", "P4", $"{at}.tax_code_id", ("determined_tax_code_id", winner.TaxCodeId), ("tax_code_id", supplied)) with { Severity = "WARNING" });
            }

            if (!versionCache.TryGetValue(code, out var cached))
            {
                cached = await ReadVersionAsync(tx, tenantId, req.CompanyId, code, req.PostingDate, ct);
                versionCache[code] = cached;
            }
            if (cached.Problem is not null || cached.Version is null)
            {
                string problem = cached.Problem ?? "NO_VERSION_AT_POSTING_DATE";
                errors.Add(problem switch
                {
                    "VAT_RATE_TYPE_NOT_BUILT" => JournalDocumentPoster.Error("LIMIT.VAT_RATE_TYPE_NOT_BUILT", "P4", $"{at}.tax_code_id", ("reason", "the registration converts with the rate type VAT"), ("tax_code_id", code)),
                    _ => JournalDocumentPoster.Error("TAX.CODE_NOT_PERMITTED", "P4", $"{at}.tax_code_id", ("reason", problem), ("tax_code_id", code))
                });
                continue;
            }
            // a registration that is not valid at the tax date cannot carry the tax (08, 4.1)
            if (!await RegistrationValidAsync(tx, tenantId, req.CompanyId, code, taxDate, ct))
            {
                errors.Add(JournalDocumentPoster.Error("TAX.CODE_NOT_PERMITTED", "P4", $"{at}.tax_code_id", ("reason", "REGISTRATION_NOT_VALID_AT_TAX_DATE"), ("tax_code_id", code), ("tax_date", taxDate)));
                continue;
            }
            // the deductible amount of the line (19, 6.2): required by a code with a cap, not allowed for any other
            if (cached.Version.DeductionCap == "SUPPLIED_AMOUNT" && l.DeductibleTaxAmountTc is null)
            {
                errors.Add(JournalDocumentPoster.Error("TAX.DEDUCTION_AMOUNT_REQUIRED", "P4", $"{at}.deductible_tax_amount_tc", ("tax_code_id", code)));
                continue;
            }
            if (cached.Version.DeductionCap != "SUPPLIED_AMOUNT" && l.DeductibleTaxAmountTc is not null)
            {
                errors.Add(JournalDocumentPoster.Error("TAX.DEDUCTION_AMOUNT_NOT_ALLOWED", "P4", $"{at}.deductible_tax_amount_tc", ("tax_code_id", code)));
                continue;
            }
            if (l.TaxAmountTc is not null && cached.Version.ReverseCharge)
            {
                errors.Add(JournalDocumentPoster.Error("SCHEMA.INVALID_VALUE", "P1", $"{at}.tax_amount_tc", ("reason", "the partner invoice of a reverse-charge line carries no tax")));
                continue;
            }
            resolved.Add(new Resolved(i, l.AmountTc, l.TaxAmountTc, cached.Version, code, winner.TaxCodeId, classId, origin, destination,
                winner.PartnerVatStatus is null ? null : partnerStatus, taxDate, isOverride, originEu, destinationEu, l.DeductibleTaxAmountTc));
        }
        if (errors.Count > 0) return TaxPlan.None;

        // one tax line per group of base lines: per document the lines with the same code, date and determination inputs, per line each line alone (ADR-027)
        var groups = roundingMode == "PER_LINE"
            ? resolved.Select(r => new List<Resolved> { r }).ToList()
            : resolved.GroupBy(r => (r.Version.VersionId, r.TaxDate, r.Origin, r.Destination, r.PartnerVatStatus, r.ClassId, r.IsOverride, r.DeterminedCodeId, r.OriginEu, r.DestinationEu))
                .Select(g => g.ToList()).ToList();

        var seeds = new List<TaxLineSeed>();
        decimal roundingTc = 0m;
        var keyRates = new Dictionary<(Guid, string, DateOnly), decimal?>();
        foreach (var group in groups.OrderBy(g => g.Min(r => r.Index)))
        {
            Resolved first = group[0];
            Version v = first.Version;
            decimal baseSum = group.Sum(r => r.BaseTc);
            decimal tax = PostingRules.Round(baseSum * v.Rate / 100m, tcScale);
            string vatCurrency = v.ReturnCurrency;
            if (vatCurrency != company.LocalCurrency && vatCurrency != reportingCurrency)
            {
                errors.Add(JournalDocumentPoster.Error("LIMIT.VAT_RATE_TYPE_NOT_BUILT", "P4", $"lines[{first.Index}]", ("reason", "the return currency is neither the local nor the reporting currency"), ("return_currency", vatCurrency)));
                continue;
            }
            var bases = group.Select(r => (r.Index, r.BaseTc)).ToList();
            decimal? supplied = null;
            if (group.Any(r => r.SuppliedTc is not null))
            {
                if (group.Any(r => r.SuppliedTc is null))
                {
                    errors.Add(JournalDocumentPoster.Error("SCHEMA.INVALID_VALUE", "P1", $"lines[{group.First(r => r.SuppliedTc is null).Index}].tax_amount_tc", ("reason", "every line that is taxed together names the tax amount, or none does")));
                    continue;
                }
                supplied = group.Sum(r => r.SuppliedTc!.Value);
                if (Math.Abs(supplied.Value - tax) > tolerance)
                {
                    errors.Add(JournalDocumentPoster.Error("TAX.AMOUNT_OUT_OF_TOLERANCE", "P4", $"lines[{first.Index}].tax_amount_tc", ("supplied", supplied), ("computed", tax), ("tolerance", tolerance)));
                    continue;
                }
                roundingTc += supplied.Value - tax;
            }

            // the split of an input tax into the deductible part D and the non-deductible part N (19, 3.3), on the deductible side only
            decimal deductible = tax, share = 100m, pct = 100m;
            string? key = null; decimal? keyRate = null, cap = null;
            var nondeductible = new List<(int Index, decimal Tc)>();
            if (v.TaxType == "INPUT")
            {
                pct = v.DeductiblePct; key = v.DeductionKey;
                if (key is not null)
                {
                    if (!keyRates.TryGetValue((v.RegistrationId, key, first.TaxDate), out keyRate))
                        keyRates[(v.RegistrationId, key, first.TaxDate)] = keyRate = await ReadKeyRateAsync(tx, tenantId, req.CompanyId, v.RegistrationId, key, first.TaxDate, ct);
                    if (keyRate is null)
                    {
                        errors.Add(JournalDocumentPoster.Error("TAX.DEDUCTION_RATE_NOT_FOUND", "P4", $"lines[{first.Index}]", ("deduction_key", key), ("tax_date", first.TaxDate)));
                        continue;
                    }
                }
                if (v.DeductionCap == "SUPPLIED_AMOUNT")
                {
                    decimal suppliedDeductible = group.Sum(r => r.DeductibleTc!.Value);
                    decimal limit = PostingRules.Round(tax * pct / 100m, tcScale);
                    if ((suppliedDeductible != 0m && Math.Sign(suppliedDeductible) != Math.Sign(tax)) || Math.Abs(suppliedDeductible) > Math.Abs(limit))
                    {
                        errors.Add(JournalDocumentPoster.Error("TAX.DEDUCTION_AMOUNT_EXCEEDS_LIMIT", "P4", $"lines[{first.Index}].deductible_tax_amount_tc", ("supplied", suppliedDeductible), ("limit", limit)));
                        continue;
                    }
                    cap = suppliedDeductible;
                    deductible = PostingRules.Round(suppliedDeductible * (keyRate ?? 100m) / 100m, tcScale);
                    share = tax == 0m ? 100m : Math.Round(deductible / tax * 100m, 10, MidpointRounding.AwayFromZero);
                }
                else
                {
                    decimal fraction = pct / 100m * (keyRate ?? 100m) / 100m;
                    deductible = PostingRules.Round(tax * fraction, tcScale);
                    share = Math.Round(fraction * 100m, 10, MidpointRounding.AwayFromZero);
                }
                decimal n = tax - deductible;
                if (n != 0m)
                {
                    // a base line on an account with open items would need an open item of its own for the cost line
                    int onOpenItemAccount = group.Select(r => r.Index).FirstOrDefault(i => accounts[req.Lines[i].AccountId].OpenItemMode != "NONE", -1);
                    if (onOpenItemAccount >= 0)
                    {
                        errors.Add(JournalDocumentPoster.Error("LIMIT.PARTIAL_DEDUCTION_NOT_BUILT", "P4", $"lines[{onOpenItemAccount}]", ("reason", "the non-deductible tax of a line on an account with open items")));
                        continue;
                    }
                    nondeductible = Allocate(n, bases, tcScale);
                }
            }

            if (v.ReverseCharge)
            {
                // the output side with the opposite sign of the base and always the full tax, the deductible side with the sign of the base (08, 5.2, 19, 4.3)
                seeds.Add(new TaxLineSeed(first.CodeId, v.VersionId, "OUTPUT", v.Rate, v.VatAccountId, -tax, null, bases, first.DeterminedCodeId, first.ClassId, first.Origin, first.Destination,
                    first.PartnerVatStatus, first.TaxDate, first.IsOverride, first.OriginEu, first.DestinationEu, vatCurrency, roundingMode, -tax, 100m, null, null, 100m, null, []));
                seeds.Add(new TaxLineSeed(first.CodeId, v.VersionId, "INPUT", v.Rate, v.ReverseChargeAccountId!.Value, deductible, null, bases, first.DeterminedCodeId, first.ClassId, first.Origin, first.Destination,
                    first.PartnerVatStatus, first.TaxDate, first.IsOverride, first.OriginEu, first.DestinationEu, vatCurrency, roundingMode, tax, pct, key, keyRate, share, cap, nondeductible));
            }
            else
                seeds.Add(new TaxLineSeed(first.CodeId, v.VersionId, v.TaxType, v.Rate, v.VatAccountId, deductible, supplied, bases, first.DeterminedCodeId, first.ClassId, first.Origin, first.Destination,
                    first.PartnerVatStatus, first.TaxDate, first.IsOverride, first.OriginEu, first.DestinationEu, vatCurrency, roundingMode, tax, pct, key, keyRate, share, cap, nondeductible));
        }
        if (errors.Count > 0) return TaxPlan.None;
        return new TaxPlan(seeds, roundingTc, resolved.ToDictionary(r => r.Index, r => r.CodeId));
    }

    /// <summary>The non-deductible tax N allocated to the base lines by their base amounts: largest remainder, ties to the lowest line (19, 4.4). A base line that gets nothing is left out.</summary>
    internal static List<(int Index, decimal Tc)> Allocate(decimal n, IReadOnlyList<(int Index, decimal BaseTc)> bases, int scale)
    {
        decimal unit = 1m;
        for (int i = 0; i < scale; i++) unit /= 10m;
        long total = (long)Math.Round(Math.Abs(n) / unit);
        decimal weight = bases.Sum(b => Math.Abs(b.BaseTc));
        var shares = new long[bases.Count];
        if (weight == 0m) shares[0] = total;
        else
        {
            var raw = bases.Select(b => (decimal)total * Math.Abs(b.BaseTc) / weight).ToArray();
            for (int i = 0; i < raw.Length; i++) shares[i] = (long)Math.Floor(raw[i]);
            long left = total - shares.Sum();
            foreach (int i in Enumerable.Range(0, raw.Length).OrderByDescending(i => raw[i] - Math.Floor(raw[i])).ThenBy(i => i).Take((int)left)) shares[i]++;
        }
        int sign = Math.Sign(n);
        return bases.Select((b, i) => (b.Index, Tc: sign * shares[i] * unit)).Where(x => x.Tc != 0m).ToList();
    }

    private static async Task<decimal?> ReadKeyRateAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid registration, string key, DateOnly taxDate, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select rate_pct from core.vat_deduction_rate where tenant_id = @t and company_id = @c and registration_id = @r and key_code = @k and valid_from <= @d " +
            "and (valid_to is null or valid_to >= @d) order by valid_from desc limit 1");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("r", registration); cmd.Parameters.AddWithValue("k", key);
        cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Date) { Value = taxDate });
        return await cmd.ExecuteScalarAsync(ct) is decimal rate ? rate : null;
    }

    private static async Task<List<Rule>> ReadRulesAsync(TenantTransaction tx, Guid tenantId, Guid companyId, DateOnly postingDate, CancellationToken ct)
    {
        // the version of a rule in force at the date: the latest one that started, if it has not ended
        await using var cmd = tx.Command(
            "select r.rule_id, r.valid_from, r.priority, r.tax_code_id, r.transaction_type, r.origin_country, r.destination_country, r.origin_eu, r.destination_eu, " +
            "r.partner_vat_status, r.account_id, r.tax_class_id from core.tax_determination_rule r " +
            "where r.tenant_id = @t and r.company_id = @c and r.valid_from <= @d and (r.valid_to is null or r.valid_to >= @d) " +
            "and not exists (select 1 from core.tax_determination_rule n where n.tenant_id = r.tenant_id and n.company_id = r.company_id and n.rule_id = r.rule_id " +
            "and n.valid_from > r.valid_from and n.valid_from <= @d)");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId);
        cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Date) { Value = postingDate });
        var rules = new List<Rule>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            rules.Add(new Rule(r.GetGuid(0), r.GetFieldValue<DateOnly>(1), r.GetInt16(2), r.GetGuid(3), r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5).Trim(), r.IsDBNull(6) ? null : r.GetString(6).Trim(), r.IsDBNull(7) ? null : r.GetBoolean(7), r.IsDBNull(8) ? null : r.GetBoolean(8),
                r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetGuid(10), r.IsDBNull(11) ? null : r.GetGuid(11)));
        return rules;
    }

    private static async Task<bool> IsAlternativeAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Rule rule, Guid code, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select 1 from core.tax_determination_alternative where tenant_id = @t and company_id = @c and rule_id = @r and valid_from = @f and alt_tax_code_id = @a");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("r", rule.RuleId);
        cmd.Parameters.Add(new NpgsqlParameter("f", NpgsqlDbType.Date) { Value = rule.ValidFrom });
        cmd.Parameters.AddWithValue("a", code);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task<bool> IsEuAsync(TenantTransaction tx, string country, DateOnly date, Dictionary<(string, DateOnly), bool> cache, CancellationToken ct)
    {
        if (cache.TryGetValue((country, date), out bool known)) return known;
        await using var cmd = tx.Command(
            "select 1 from platform.ref_country_eu_membership where country_code = @c and valid_from <= @d and (valid_to is null or valid_to >= @d)");
        cmd.Parameters.AddWithValue("c", country);
        cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Date) { Value = date });
        bool eu = await cmd.ExecuteScalarAsync(ct) is not null;
        cache[(country, date)] = eu;
        return eu;
    }

    private static async Task<(Version? Version, bool Active, bool RegistrationUsable, string? Problem)> ReadVersionAsync(
        TenantTransaction tx, Guid tenantId, Guid companyId, Guid code, DateOnly postingDate, CancellationToken ct)
    {
        string type; bool active; string returnCurrency, rateType; bool registrationActive; Guid registrationId;
        await using (var cmd = tx.Command(
            "select c.tax_type, c.active, r.return_currency, r.vat_rate_type, r.active, c.vat_registration_id from core.tax_code c join core.company_vat_registration r " +
            "on r.tenant_id = c.tenant_id and r.company_id = c.company_id and r.registration_id = c.vat_registration_id " +
            "where c.tenant_id = @t and c.company_id = @c and c.tax_code_id = @x"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("x", code);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return (null, false, false, "TAX_CODE_NOT_FOUND");
            type = r.GetString(0); active = r.GetBoolean(1); returnCurrency = r.GetString(2).Trim(); rateType = r.GetString(3); registrationActive = r.GetBoolean(4); registrationId = r.GetGuid(5);
        }
        if (!active) return (null, false, false, "TAX_CODE_INACTIVE");
        if (!registrationActive) return (null, true, false, "REGISTRATION_INACTIVE");
        if (rateType != "BOOKING") return (null, true, true, "VAT_RATE_TYPE_NOT_BUILT");
        await using var vc = tx.Command(
            "select tax_code_version_id, rate, vat_account_id, reverse_charge, reverse_charge_account_id, deductible_pct, deduction_key, deduction_cap from core.tax_code_version " +
            "where tenant_id = @t and company_id = @c and tax_code_id = @x and valid_from <= @d and (valid_to is null or valid_to >= @d) order by valid_from desc limit 1");
        vc.Parameters.AddWithValue("t", tenantId); vc.Parameters.AddWithValue("c", companyId); vc.Parameters.AddWithValue("x", code);
        vc.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Date) { Value = postingDate });
        await using var v = await vc.ExecuteReaderAsync(ct);
        if (!await v.ReadAsync(ct)) return (null, true, true, "NO_VERSION_AT_POSTING_DATE");
        return (new Version(v.GetGuid(0), v.GetDecimal(1), v.GetGuid(2), v.GetBoolean(3), v.IsDBNull(4) ? null : v.GetGuid(4), type, returnCurrency, registrationId,
            v.GetDecimal(5), v.IsDBNull(6) ? null : v.GetString(6), v.GetString(7)), true, true, null);
    }

    private static async Task<bool> RegistrationValidAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid code, DateOnly taxDate, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select 1 from core.tax_code c join core.company_vat_registration r on r.tenant_id = c.tenant_id and r.company_id = c.company_id and r.registration_id = c.vat_registration_id " +
            "where c.tenant_id = @t and c.company_id = @c and c.tax_code_id = @x and r.valid_from <= @d and (r.valid_to is null or r.valid_to >= @d)");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("x", code);
        cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Date) { Value = taxDate });
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }
}
