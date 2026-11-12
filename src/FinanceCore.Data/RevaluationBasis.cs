using System.Globalization;
using FinanceCore.Core.Closing;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Hashing;
using Npgsql;

namespace FinanceCore.Data;

/// <summary>
/// The basis and the computation of a revaluation (05, 3.1, 3.2), read only: revaluation:run, revaluation:preview and the stale check of hard-close
/// and year-end all use it, so that they compute the same digest from the same data. It runs in any command role that can read the ledger.
/// </summary>
internal static class RevaluationBasis
{
    public sealed record Computation(RevaluationTargets Targets, RevaluationPlan Plan, DateOnly ValuationDate, IReadOnlyList<Guid> UsedRates);

    private static readonly string[] ExcludedTypes = ["REVALUATION", "REVAL_REVERSAL"];

    public static async Task<(Computation? Result, CommandError? Error)> ComputeAsync(
        TenantTransaction tx, Guid tenantId, Guid companyId, DateOnly valuationDate, CancellationToken ct)
    {
        JournalDocumentPoster.CompanyRow? company = await JournalDocumentPoster.ReadCompanyAsync(tx, tenantId, companyId, ct);
        if (company is null) return (null, JournalDocumentPoster.Error("COMPANY.NOT_FOUND", "P2", "company_id"));
        string? group = await ClearingPoster.ReadGroupCurrencyAsync(tx, tenantId, companyId, valuationDate, ct);
        if (group is null) return (null, JournalDocumentPoster.Error("CONFIG.GROUP_MEMBERSHIP_MISSING", "P2", "period_no", ("posting_date", valuationDate)));

        async Task<int> Scale(string? currency)
        {
            if (currency is null) return 0;
            await using var cmd = tx.Command("select minor_units from platform.ref_currency where currency_code = @c");
            cmd.Parameters.AddWithValue("c", currency);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct) ?? 2, CultureInfo.InvariantCulture);
        }
        var targets = new RevaluationTargets(company.LocalCurrency, await Scale(company.LocalCurrency), group, await Scale(group),
            company.ReportingCurrency, await Scale(company.ReportingCurrency));

        var units = new List<RevaluationUnit>();
        await using (var cmd = tx.Command(
            "select l.account_id, o.partner_id, l.ic_counterpart_company_id, d.transaction_currency, l.document_id, l.line_no, " +
            "l.amount_tc - coalesce(c.tc, 0), l.amount_lc - coalesce(c.lc, 0), l.amount_gc - coalesce(c.gc, 0), " +
            "case when l.amount_rc is null then null else l.amount_rc - coalesce(c.rc, 0) end " +
            "from core.journal_line_open_item o " +
            "join core.journal_line l on l.tenant_id = o.tenant_id and l.company_id = o.company_id and l.document_id = o.document_id and l.line_no = o.line_no " +
            "join core.journal_document d on d.tenant_id = l.tenant_id and d.company_id = l.company_id and d.document_id = l.document_id " +
            "join core.gl_account a on a.tenant_id = l.tenant_id and a.company_id = l.company_id and a.account_id = l.account_id " +
            "left join lateral (select sum(i.cleared_amount_tc) tc, sum(i.cleared_amount_lc) lc, sum(i.cleared_amount_gc) gc, sum(i.cleared_amount_rc) rc " +
            "  from core.clearing_item i where i.tenant_id = l.tenant_id and i.company_id = l.company_id and i.open_item_document_id = l.document_id " +
            "  and i.open_item_line_no = l.line_no and i.clearing_date <= @vd) c on true " +
            "where o.tenant_id = @t and o.company_id = @c and a.revalue_flag and a.open_item_mode in ('CONTROL', 'GL_OPEN_ITEM') " +
            "and l.posting_date <= @vd and not (d.document_type = any(@ex))"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("vd", valuationDate);
            cmd.Parameters.AddWithValue("ex", ExcludedTypes);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var u = new RevaluationUnit("OPEN_ITEM", r.GetGuid(0), r.IsDBNull(1) ? null : r.GetGuid(1), r.IsDBNull(2) ? null : r.GetGuid(2), r.GetString(3).Trim(),
                    r.GetGuid(4), r.GetInt32(5), r.GetDecimal(6), r.GetDecimal(7), r.GetDecimal(8), r.IsDBNull(9) ? null : r.GetDecimal(9));
                if (u.RemainingTc != 0m || u.CarriedLc != 0m || u.CarriedGc != 0m || (u.CarriedRc ?? 0m) != 0m) units.Add(u);
            }
        }
        await using (var cmd = tx.Command(
            "select l.account_id, d.transaction_currency, sum(l.amount_tc), sum(l.amount_lc), sum(l.amount_gc), sum(l.amount_rc) " +
            "from core.journal_line l " +
            "join core.journal_document d on d.tenant_id = l.tenant_id and d.company_id = l.company_id and d.document_id = l.document_id " +
            "join core.gl_account a on a.tenant_id = l.tenant_id and a.company_id = l.company_id and a.account_id = l.account_id " +
            "where l.tenant_id = @t and l.company_id = @c and a.revalue_flag and a.open_item_mode = 'NONE' " +
            "and l.posting_date <= @vd and not (d.document_type = any(@ex)) " +
            "group by l.account_id, d.transaction_currency"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("vd", valuationDate);
            cmd.Parameters.AddWithValue("ex", ExcludedTypes);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var u = new RevaluationUnit("ACCOUNT_BALANCE", r.GetGuid(0), null, null, r.GetString(1).Trim(), null, null,
                    r.GetDecimal(2), r.GetDecimal(3), r.GetDecimal(4), r.IsDBNull(5) ? null : r.GetDecimal(5));
                if (u.RemainingTc != 0m || u.CarriedLc != 0m || u.CarriedGc != 0m || (u.CarriedRc ?? 0m) != 0m) units.Add(u);
            }
        }
        // a unit in the currency of every target has nothing to revalue
        units = units.Where(u => RevaluationModel.RatePairs([u], targets).Count > 0).ToList();

        int fallbackDays;
        string? pivot;
        await using (var cmd = tx.Command("select rate_fallback_days, pivot_currency from core.tenant_setting where tenant_id = @t"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            fallbackDays = Convert.ToInt32(r.GetValue(0), CultureInfo.InvariantCulture); pivot = r.IsDBNull(1) ? null : r.GetString(1).Trim();
        }
        var rates = new Dictionary<(string From, string To), RevaluationRateResult>();
        var used = new List<Guid>();
        foreach (var (from, to) in RevaluationModel.RatePairs(units, targets))
        {
            (decimal Rate, DateOnly Date)? rate = await ClosingRateAsync(tx, tenantId, from, to, valuationDate, fallbackDays, pivot, used, ct);
            if (rate is null) return (null, JournalDocumentPoster.Error("REVAL.CLOSING_RATE_MISSING", "P4", "period_no", ("from", from), ("to", to), ("rate_type", "CLOSING"), ("date", valuationDate)));
            rates[(from, to)] = new RevaluationRateResult(from, to, rate.Value.Rate, rate.Value.Date);
        }
        return (new Computation(targets, RevaluationModel.Build(units, rates, targets), valuationDate, used), null);
    }

    /// <summary>The CLOSING rate of a pair at the date (07, 7.2, ADR-019): direct with the fallback, inverse, or crossed through the pivot. Its date is the date of the table rate.</summary>
    private static async Task<(decimal Rate, DateOnly Date)?> ClosingRateAsync(
        TenantTransaction tx, Guid tenantId, string from, string to, DateOnly date, int fallbackDays, string? pivot, List<Guid> used, CancellationToken ct)
    {
        (decimal, DateOnly)? direct = await DirectOrInverseAsync(tx, tenantId, from, to, date, fallbackDays, used, ct);
        if (direct is not null) return direct;
        if (pivot is not null && pivot != from && pivot != to)
        {
            var a = await DirectOrInverseAsync(tx, tenantId, from, pivot, date, fallbackDays, used, ct);
            var b = a is null ? null : await DirectOrInverseAsync(tx, tenantId, pivot, to, date, fallbackDays, used, ct);
            if (a is not null && b is not null)
                return (Math.Round(a.Value.Item1 * b.Value.Item1, Canonical.RateScale, MidpointRounding.AwayFromZero), a.Value.Item2 < b.Value.Item2 ? a.Value.Item2 : b.Value.Item2);
        }
        return null;
    }

    private static async Task<(decimal, DateOnly)?> DirectOrInverseAsync(
        TenantTransaction tx, Guid tenantId, string from, string to, DateOnly date, int fallbackDays, List<Guid> used, CancellationToken ct)
    {
        foreach (bool inverse in new[] { false, true })
        {
            await using var cmd = tx.Command(
                "select rate_id, rate, valid_from from core.exchange_rate where tenant_id = @t and rate_type = 'CLOSING' and from_currency = @f and to_currency = @to " +
                "and valid_from <= @d order by valid_from desc limit 1");
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("f", inverse ? to : from);
            cmd.Parameters.AddWithValue("to", inverse ? from : to);
            cmd.Parameters.AddWithValue("d", date);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) continue;
            DateOnly validFrom = r.GetFieldValue<DateOnly>(2);
            if (date.DayNumber - validFrom.DayNumber > fallbackDays) continue;
            used.Add(r.GetGuid(0));
            decimal rate = r.GetDecimal(1);
            return (inverse ? Math.Round(1m / rate, Canonical.RateScale, MidpointRounding.AwayFromZero) : rate, validFrom);
        }
        return null;
    }

    /// <summary>The regular period whose range holds the end date of the given period: the period itself when it is regular, else the period a special period shares its dates with (05, 3.7, D4).</summary>
    public static async Task<(int PeriodNo, DateOnly End)?> ValuationPeriodAsync(TenantTransaction tx, Guid tenantId, Guid companyId, int fiscalYear, int periodNo, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select r.period_no, r.end_date from core.fiscal_period p join core.fiscal_period r on r.tenant_id = p.tenant_id and r.company_id = p.company_id " +
            "and r.fiscal_year = p.fiscal_year and r.period_type = 'REGULAR' and r.start_date <= p.end_date and r.end_date >= p.end_date " +
            "where p.tenant_id = @t and p.company_id = @c and p.fiscal_year = @y and p.period_no = @p order by r.period_no limit 1");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("y", fiscalYear); cmd.Parameters.AddWithValue("p", periodNo);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (r.GetInt32(0), r.GetFieldValue<DateOnly>(1)) : null;
    }

    /// <summary>The stale check (05, 3.7, ADR-053): the recomputed digest must equal that of the ACTIVE run of the regular period whose valuation date lies in the range
    /// of the period being closed. Without a run, the preview must be empty. A company without a revalue_flag account has nothing to check.
    /// Run after the heads and the period rows are locked.</summary>
    public static async Task<CommandError?> StaleCheckAsync(TenantTransaction tx, Guid tenantId, Guid companyId, int fiscalYear, int periodNo, CancellationToken ct)
    {
        await using (var cmd = tx.Command("select exists (select 1 from core.gl_account where tenant_id = @t and company_id = @c and revalue_flag)"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId);
            if (!(bool)(await cmd.ExecuteScalarAsync(ct))!) return null;
        }
        var valuation = await ValuationPeriodAsync(tx, tenantId, companyId, fiscalYear, periodNo, ct);
        if (valuation is null) return null;
        var (computation, error) = await ComputeAsync(tx, tenantId, companyId, valuation.Value.End, ct);
        if (error is not null) return error;

        byte[]? runDigest = null; Guid? runId = null;
        await using (var cmd = tx.Command(
            "select run_id, items_digest from core.revaluation_run where tenant_id = @t and company_id = @c and fiscal_year = @y and fiscal_period = @p and status = 'ACTIVE'"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("y", fiscalYear);
            cmd.Parameters.AddWithValue("p", valuation.Value.PeriodNo);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) { runId = r.GetGuid(0); runDigest = (byte[])r[1]; }
        }
        bool fresh = runDigest is null ? computation!.Plan.Items.Count == 0 : computation!.Plan.Digest.AsSpan().SequenceEqual(runDigest);
        return fresh ? null : JournalDocumentPoster.Error("CLOSE.REVALUATION_STALE", "P3", "period_no",
            ("fiscal_year", fiscalYear), ("revaluation_period_no", valuation.Value.PeriodNo), ("run_id", runId), ("has_run", runId is not null),
            ("preview_digest", Canonical.FormatHex32(computation.Plan.Digest)));
    }
}
