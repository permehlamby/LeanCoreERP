using FinanceCore.Core.Commands;
using FinanceCore.Core.Migration;
using FinanceCore.Core.Posting;
using Npgsql;
using NpgsqlTypes;

namespace FinanceCore.Data;

public sealed partial class MigrationCommands
{
    private sealed record LegacyRegistration(Guid RegistrationId, string VatId, string Scheme, string FilingFrequency, string? EcListFrequency, DateOnly ValidFrom, DateOnly? ValidTo, string ReturnCurrency);

    private static async Task<Dictionary<string, LegacyRegistration>> ReadLegacyRegistrationsAsync(TenantTransaction tx, Guid tenantId, Guid companyId, CancellationToken ct)
    {
        var result = new Dictionary<string, LegacyRegistration>(StringComparer.Ordinal);
        await using var cmd = tx.Command(
            "select registration_id, vat_id, scheme, filing_frequency, ec_list_frequency, valid_from, valid_to, return_currency from core.company_vat_registration where tenant_id = @t and company_id = @c and active");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            result[r.GetString(1).Trim()] = new LegacyRegistration(r.GetGuid(0), r.GetString(1).Trim(), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
                r.GetFieldValue<DateOnly>(5), r.IsDBNull(6) ? null : r.GetFieldValue<DateOnly>(6), r.GetString(7).Trim());
        return result;
    }

    /// <summary>
    /// The checks of a LEGACY_TAX_SUMMARY record against the data of the company (13, 3.6): the registration of the company with that VAT id, its scheme files the return type, the VAT account and the partner
    /// exist, and, when the fiscal year has its periods, the VAT period holds the cutover date and the registration is valid in it. A year without periods is checked again by the commit.
    /// </summary>
    private static async Task<List<CommandError>> CheckLegacyTaxAsync(
        TenantTransaction tx, Guid tenantId, BatchRow batch, LegacyTaxSummaryPayload p, Dictionary<string, LegacyRegistration> registrations,
        Dictionary<string, AccountInfo> accounts, Dictionary<string, Guid> partners, CancellationToken ct)
    {
        var errors = new List<CommandError>();
        void Bad(string path, string code, string reason) => errors.Add(Error(code, "P2", path, ("reason", reason)));
        if (!registrations.TryGetValue(p.RegistrationVatId!.Trim(), out LegacyRegistration? reg)) { Bad("/registration_vat_id", "VAT.NO_REGISTRATION", "no active registration of the company with this VAT id"); return errors; }
        string schemeType = VatPeriods.ReturnTypeOfScheme(reg.Scheme);
        if (p.ReturnType != schemeType && !(p.ReturnType == "EC_LIST" && reg.Scheme == "STANDARD")) Bad("/return_type", "SCHEMA.INVALID_VALUE", $"the scheme {reg.Scheme} files {schemeType}");
        if (p.ReturnType == "EC_LIST" && reg.EcListFrequency is null) Bad("/return_type", "SCHEMA.INVALID_VALUE", "the registration files no EC list (ec_list_frequency)");
        if (p.VatAccountNo is not null && !accounts.ContainsKey(p.VatAccountNo)) Bad("/vat_account_no", "ACCOUNT.NOT_FOUND", "no account with this number");
        if (p.PartnerCode is not null && !partners.ContainsKey(p.PartnerCode)) Bad("/partner_code", "MASTER.PARTNER_NOT_FOUND", "no partner with this code");
        if (errors.Count == 0 && await CheckLegacyPeriodAsync(tx, tenantId, batch, reg, p, ct) is { } period) errors.Add(period);
        return errors;
    }

    /// <summary>The VAT period of the record holds the cutover date and the registration is valid in it. Null when it does, or when the fiscal year has no periods yet.</summary>
    private static async Task<CommandError?> CheckLegacyPeriodAsync(TenantTransaction tx, Guid tenantId, BatchRow batch, LegacyRegistration reg, LegacyTaxSummaryPayload p, CancellationToken ct)
    {
        var regular = await VatReturnCommands.ReadRegularPeriodsAsync(tx, tenantId, batch.CompanyId, p.FiscalYear!.Value, ct);
        if (regular.Count == 0) return null;
        string? frequency = p.ReturnType == "EC_LIST" ? reg.EcListFrequency : reg.FilingFrequency;
        VatPeriod? period = VatPeriods.Build(regular, frequency!).FirstOrDefault(x => x.No == p.VatPeriodNo);
        if (period is null || batch.CutoverDate < period.Start || batch.CutoverDate > period.End)
            return Error("MIGRATION.LEGACY_TAX_PERIOD_INVALID", "P2", "/vat_period_no", ("reason", "the VAT period must hold the cutover date"), ("cutover_date", batch.CutoverDate));
        if (reg.ValidFrom > period.End || (reg.ValidTo is { } to && to < period.Start))
            return Error("VAT.NO_REGISTRATION", "P2", "/registration_vat_id", ("reason", "the registration is not valid in the VAT period"));
        return null;
    }

    /// <summary>The commit chunk LEGACY_TAX (13, 3.6, 7): the rows of the legacy tax summary of the batch, in one transaction. A chunk that ran before is not repeated (the rows of the batch are unique by legacy_ref).</summary>
    private async Task<ChunkFailure?> RunLegacyTaxChunkAsync(TenantTransaction tx, Guid tenantId, CommandContext ctx, BatchRow batch, int chunkNo, CancellationToken ct)
    {
        var records = await ReadCurrentRecordsWithRefAsync(tx, tenantId, batch.BatchId, ct);
        var registrations = await ReadLegacyRegistrationsAsync(tx, tenantId, batch.CompanyId, ct);
        var accounts = await ReadAccountsAsync(tx, tenantId, batch.CompanyId, ct);
        var partners = new Dictionary<string, Guid>(StringComparer.Ordinal);
        await using (var cmd = tx.Command("select partner_code, partner_id from core.business_partner where tenant_id = @t"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) partners[r.GetString(0)] = r.GetGuid(1);
        }
        int written = 0;
        foreach (var (legacyRef, record) in records.Where(r => r.Payload is LegacyTaxSummaryPayload).Select(r => (r.LegacyRef, (LegacyTaxSummaryPayload)r.Payload)))
        {
            LegacyRegistration reg = registrations.GetValueOrDefault(record.RegistrationVatId!.Trim())!;
            if (reg is null) return new ChunkFailure("VAT.NO_REGISTRATION");
            if (await CheckLegacyPeriodAsync(tx, tenantId, batch, reg, record, ct) is { } period) return new ChunkFailure(period.Code);
            await using var cmd = tx.Command(
                "insert into core.legacy_tax_summary (tenant_id, company_id, batch_id, registration_id, fiscal_year, vat_period_no, return_type, row_type, box_code, partner_id, partner_vat_id, country_code, category, rate, " +
                "supply_kind, currency_code, base_amount, tax_amount, vat_account_id, ledger_lc, ledger_gc, ledger_rc, legacy_ref, created_by) values " +
                "(@t, @c, @b, @reg, @fy, @no, @rt, @row, @box, @p, @pvat, @cc, @cat, @rate, @sk, @cur, @base, @tax, @acc, @llc, @lgc, @lrc, @ref, @by) on conflict (tenant_id, batch_id, legacy_ref) do nothing");
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", batch.CompanyId); cmd.Parameters.AddWithValue("b", batch.BatchId); cmd.Parameters.AddWithValue("reg", reg.RegistrationId);
            cmd.Parameters.AddWithValue("fy", record.FiscalYear!.Value); cmd.Parameters.AddWithValue("no", (short)record.VatPeriodNo!.Value); cmd.Parameters.AddWithValue("rt", record.ReturnType!);
            cmd.Parameters.AddWithValue("row", record.ReturnType == "STANDARD" ? "BOX" : record.ReturnType == "EC_LIST" ? "EC_LIST" : "OSS");
            void Text(string name, string? v, NpgsqlDbType type = NpgsqlDbType.Text) => cmd.Parameters.Add(new NpgsqlParameter(name, type) { Value = (object?)v ?? DBNull.Value });
            Text("box", record.BoxCode); Text("pvat", record.PartnerVatId); Text("cc", record.CountryCode?.ToUpperInvariant(), NpgsqlDbType.Char); Text("cat", record.Category); Text("sk", record.SupplyKind);
            cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Uuid) { Value = record.PartnerCode is null ? DBNull.Value : partners[record.PartnerCode] });
            cmd.Parameters.Add(new NpgsqlParameter("rate", NpgsqlDbType.Numeric) { Value = (object?)record.Rate ?? DBNull.Value });
            cmd.Parameters.AddWithValue("cur", reg.ReturnCurrency); cmd.Parameters.AddWithValue("base", record.BaseAmount!.Value); cmd.Parameters.AddWithValue("tax", record.TaxAmount!.Value);
            cmd.Parameters.Add(new NpgsqlParameter("acc", NpgsqlDbType.Uuid) { Value = record.VatAccountNo is null ? DBNull.Value : accounts[record.VatAccountNo].AccountId });
            cmd.Parameters.AddWithValue("llc", record.LedgerLc ?? 0m); cmd.Parameters.AddWithValue("lgc", record.LedgerGc ?? 0m);
            cmd.Parameters.Add(new NpgsqlParameter("lrc", NpgsqlDbType.Numeric) { Value = (object?)record.LedgerRc ?? DBNull.Value });
            cmd.Parameters.AddWithValue("ref", legacyRef); cmd.Parameters.AddWithValue("by", ctx.Actor);
            written += await cmd.ExecuteNonQueryAsync(ct);
        }
        await using (var cmd = tx.Command("update core.migration_commit_chunk set status = 'DONE', document_count = @n, started_at = coalesce(started_at, now()), completed_at = now(), failure_codes = null where tenant_id = @t and batch_id = @b and chunk_no = @c"))
        {
            cmd.Parameters.AddWithValue("n", written); cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("b", batch.BatchId); cmd.Parameters.AddWithValue("c", chunkNo);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return null;
    }

    private sealed record CurrentRecordWithRef(string Type, string LegacyRef, object Payload);

    private static async Task<List<CurrentRecordWithRef>> ReadCurrentRecordsWithRefAsync(TenantTransaction tx, Guid tenantId, Guid batchId, CancellationToken ct)
    {
        var list = new List<CurrentRecordWithRef>();
        await using var cmd = tx.Command("select record_type, legacy_ref, payload::text from core.migration_record where tenant_id = @t and batch_id = @b and not superseded and status = 'VALID' and record_type = 'LEGACY_TAX_SUMMARY' order by record_no");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("b", batchId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            using var doc = System.Text.Json.JsonDocument.Parse(r.GetString(2));
            var (payload, _) = MigrationRecords.Parse(r.GetString(0), doc.RootElement.Clone());
            if (payload is not null) list.Add(new CurrentRecordWithRef(r.GetString(0), r.GetString(1), payload));
        }
        return list;
    }
}
