using FinanceCore.Core.Commands;
using FinanceCore.Core.Posting;
using Npgsql;
using NpgsqlTypes;

namespace FinanceCore.Data;

public sealed partial class VatReturnCommands
{
    /// <summary>
    /// vat-positions:get (09, 4.10; 08, 7.4; permission read.ledger): the tax lines of a registration up to the date that no current snapshot holds, aggregated as a prepared return of the VAT period of the
    /// date would (BOX rows by the box mapping of the code version, EC_LIST rows per partner, country and category, OSS rows per country, rate and kind). A line with a tax date before that VAT period is
    /// late. A BOX row also gives the non-deductible tax of its lines (19, 7), derived from the allocation of journal_line_tax_base in the return currency, with the sign of the tax part of the row. Nothing is
    /// stored and no state of the return chain is read. Not built: the missing-evidence warning, the position at a past date by reconstruction (as_of_date reads today's snapshot of the tax lines).
    /// </summary>
    public async Task<CommandResult<VatPositionResult>> GetPositionAsync(Guid tenantId, CommandContext ctx, GetVatPositionRequest req, CancellationToken ct = default)
    {
        static CommandResult<VatPositionResult> Denied(CommandError e) => CommandResult<VatPositionResult>.Rejected([e]);
        if (!ctx.Permissions.Contains(LedgerReads.Permission)) return Denied(JournalDocumentPoster.Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", LedgerReads.Permission)));
        if (req.RegistrationId == Guid.Empty) return Denied(JournalDocumentPoster.Error("SCHEMA.INVALID_VALUE", "P1", "registration_id", ("reason", "required")));
        if (req.ReturnType is not ("STANDARD" or "EC_LIST" or "OSS" or "IOSS"))
            return Denied(JournalDocumentPoster.Error("SCHEMA.INVALID_VALUE", "P1", "return_type", ("allowed_values", new[] { "STANDARD", "EC_LIST", "OSS", "IOSS" })));
        DateOnly asOf = req.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);

        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct, snapshot: true);
        Registration? reg = await ReadRegistrationAsync(tx, tenantId, req.RegistrationId, ct);
        if (reg is null) return Denied(JournalDocumentPoster.Error("VAT.NO_REGISTRATION", "P2", "registration_id", ("registration_id", req.RegistrationId)));
        if (!reg.Active) return Denied(JournalDocumentPoster.Error("VAT.REGISTRATION_INACTIVE", "P2", "registration_id", ("registration_id", req.RegistrationId)));
        Guid companyId = reg.CompanyId;
        string schemeType = VatPeriods.ReturnTypeOfScheme(reg.Scheme);
        if (req.ReturnType != schemeType && !(req.ReturnType == "EC_LIST" && reg.Scheme == "STANDARD"))
            return Denied(JournalDocumentPoster.Error("SCHEMA.INVALID_VALUE", "P2", "return_type", ("reason", $"the scheme {reg.Scheme} files {schemeType}")));
        string? frequency = req.ReturnType == "EC_LIST" ? reg.EcListFrequency : reg.FilingFrequency;
        if (frequency is null) return Denied(JournalDocumentPoster.Error("SCHEMA.INVALID_VALUE", "P2", "return_type", ("reason", "the registration files no EC list (ec_list_frequency)")));

        // the VAT period of the date
        int? fiscalYear = null;
        await using (var cmd = tx.Command(
            "select fiscal_year from core.fiscal_period where tenant_id = @t and company_id = @c and period_type = 'REGULAR' and start_date <= @d and end_date >= @d order by fiscal_year limit 1"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", asOf);
            if (await cmd.ExecuteScalarAsync(ct) is int fy) fiscalYear = fy;
        }
        VatPeriod? period = fiscalYear is { } year
            ? VatPeriods.Build(await ReadRegularPeriodsAsync(tx, tenantId, companyId, year, ct), frequency).FirstOrDefault(p => p.Start <= asOf && p.End >= asOf)
            : null;
        if (period is null) return Denied(JournalDocumentPoster.Error("VAT.PERIOD_NOT_FOUND", "P2", "as_of_date", ("as_of_date", asOf)));
        if (reg.ValidFrom > period.End || (reg.ValidTo is { } to && to < period.Start))
            return Denied(JournalDocumentPoster.Error("VAT.NO_REGISTRATION", "P2", "registration_id", ("registration_id", req.RegistrationId), ("period_start", period.Start)));

        // the unreported tax lines up to the date, each with its non-deductible tax in the return currency
        var rows = new List<(TaxRow Row, decimal Nondeductible)>();
        await using (var cmd = tx.Command(
            "select t.document_id, t.line_no, t.tax_date, t.tax_code_version_id, t.vat_base_amount, t.vat_tax_amount, l.account_id, v.tax_code_id, v.valid_from, v.reverse_charge_account_id, " +
            "t.destination_country, t.tax_rate, v.ec_list_category, v.supply_kind, v.evidence_required_type, true, " +
            "round(coalesce((select sum(b.nondeductible_tax_tc) from core.journal_line_tax_base b where b.tenant_id = t.tenant_id and b.company_id = t.company_id " +
            "and b.document_id = t.document_id and b.tax_line_no = t.line_no), 0) * t.vat_rate, 4) " +
            "from core.journal_line_tax t " +
            "join core.journal_line l on l.tenant_id = t.tenant_id and l.company_id = t.company_id and l.document_id = t.document_id and l.line_no = t.line_no " +
            "join core.tax_code_version v on v.tenant_id = t.tenant_id and v.tax_code_version_id = t.tax_code_version_id " +
            "join core.tax_code c on c.tenant_id = v.tenant_id and c.company_id = v.company_id and c.tax_code_id = v.tax_code_id " +
            "where t.tenant_id = @t and t.company_id = @c and c.vat_registration_id = @r and t.tax_date <= @d and (@rt <> 'EC_LIST' or v.ec_list_category is not null) " +
            "and not exists (select 1 from core.vat_return_tax_line x where x.tenant_id = t.tenant_id and x.company_id = t.company_id and x.registration_id = @r " +
            "and x.return_type = @rt and x.document_id = t.document_id and x.line_no = t.line_no and x.is_current) order by t.document_id, t.line_no"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("r", req.RegistrationId);
            cmd.Parameters.AddWithValue("rt", req.ReturnType); cmd.Parameters.AddWithValue("d", asOf);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                rows.Add((new TaxRow(r.GetGuid(0), r.GetInt32(1), r.GetFieldValue<DateOnly>(2), r.GetGuid(3), r.GetDecimal(4), r.GetDecimal(5), r.GetGuid(6),
                    r.GetGuid(7), r.GetFieldValue<DateOnly>(8), r.IsDBNull(9) ? null : r.GetGuid(9), r.GetString(10).Trim(), r.GetDecimal(11),
                    r.IsDBNull(12) ? null : r.GetString(12), r.IsDBNull(13) ? null : r.GetString(13), r.IsDBNull(14) ? null : r.GetString(14), r.GetBoolean(15)), r.GetDecimal(16)));
        }

        var lines = new List<VatPositionLine>();
        bool Late(TaxRow row) => row.TaxDate < period.Start;
        string currency = reg.ReturnCurrency;
        if (req.ReturnType == "STANDARD")
        {
            var mappings = new Dictionary<(Guid, DateOnly), List<BoxMapping>>();
            await using (var cmd = tx.Command(
                "select tax_code_id, valid_from, line_side, box_code, amount_kind, sign from core.tax_code_version_box where tenant_id = @t and company_id = @c and return_type = @rt"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("rt", req.ReturnType);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    var key = (r.GetGuid(0), r.GetFieldValue<DateOnly>(1));
                    if (!mappings.TryGetValue(key, out var list)) mappings[key] = list = [];
                    list.Add(new BoxMapping(r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt16(5)));
                }
            }
            var boxes = new SortedDictionary<string, (decimal Base, decimal Tax, decimal Nondeductible, HashSet<(Guid, int)> Lines, HashSet<(Guid, int)> LateLines)>(StringComparer.Ordinal);
            foreach (var (row, nondeductible) in rows)
            {
                string side = row.ReverseChargeAccount is { } rc && rc == row.AccountId ? "COUNTER" : "PRIMARY";
                foreach (BoxMapping m in mappings.GetValueOrDefault((row.TaxCodeId, row.VersionFrom)) ?? [])
                {
                    if (m.LineSide != side) continue;
                    var (b, t, nd, set, lateSet) = boxes.GetValueOrDefault(m.BoxCode, (0m, 0m, 0m, new HashSet<(Guid, int)>(), new HashSet<(Guid, int)>()));
                    if (m.AmountKind == "BASE") b += m.Sign * row.VatBase; else { t += m.Sign * row.VatTax; nd += m.Sign * nondeductible; }
                    set.Add((row.DocumentId, row.LineNo));
                    if (Late(row)) lateSet.Add((row.DocumentId, row.LineNo));
                    boxes[m.BoxCode] = (b, t, nd, set, lateSet);
                }
            }
            foreach (var (box, v) in boxes) lines.Add(new("BOX", box, null, null, null, null, null, null, currency, v.Base, v.Tax, v.Lines.Count, v.LateLines.Count, v.Nondeductible));
        }
        else if (req.ReturnType == "EC_LIST")
        {
            var partners = await ReadPartnersAsync(tx, tenantId, companyId, rows.Select(r => r.Row.DocumentId).Distinct().ToArray(), ct);
            foreach (var (row, _) in rows)
                if (!partners.TryGetValue(row.DocumentId, out Guid? p) || p is null)
                    return Denied(JournalDocumentPoster.Error("VAT.PARTNER_MISSING", "P5", "as_of_date", ("document_id", row.DocumentId), ("line_no", row.LineNo)));
            var groups = rows.GroupBy(r => (Country: r.Row.DestinationCountry, Partner: partners[r.Row.DocumentId]!.Value, Category: r.Row.EcListCategory!))
                .OrderBy(g => g.Key.Country, StringComparer.Ordinal).ThenBy(g => g.Key.Partner.ToString("D"), StringComparer.Ordinal).ThenBy(g => g.Key.Category, StringComparer.Ordinal);
            foreach (var g in groups)
            {
                string? vatId = await ReadPartnerVatIdAsync(tx, tenantId, g.Key.Partner, g.Key.Country, g.Max(r => r.Row.TaxDate), ct);
                lines.Add(new("EC_LIST", null, g.Key.Partner, vatId, g.Key.Country, g.Key.Category, null, null, currency, -g.Sum(r => r.Row.VatBase), -g.Sum(r => r.Row.VatTax), g.Count(), g.Count(r => Late(r.Row)), null));
            }
        }
        else
        {
            foreach (var (row, _) in rows)
                if (row.SupplyKind is null)
                    return Denied(JournalDocumentPoster.Error("VAT.SUPPLY_KIND_MISSING", "P5", "as_of_date", ("document_id", row.DocumentId), ("line_no", row.LineNo)));
            foreach (var g in rows.GroupBy(r => (Country: r.Row.DestinationCountry, Rate: r.Row.TaxRate, Kind: r.Row.SupplyKind!))
                         .OrderBy(g => g.Key.Country, StringComparer.Ordinal).ThenBy(g => g.Key.Rate).ThenBy(g => g.Key.Kind, StringComparer.Ordinal))
                lines.Add(new("OSS", null, null, null, g.Key.Country, null, g.Key.Rate, g.Key.Kind, currency, -g.Sum(r => r.Row.VatBase), -g.Sum(r => r.Row.VatTax), g.Count(), g.Count(r => Late(r.Row)), null));
        }
        return CommandResult<VatPositionResult>.Ok(new VatPositionResult(req.RegistrationId, companyId, req.ReturnType, asOf, fiscalYear!.Value, period.No, period.Start, period.End,
            currency, rows.Count, rows.Count(r => Late(r.Row)), lines), Guid.Empty);
    }
}
