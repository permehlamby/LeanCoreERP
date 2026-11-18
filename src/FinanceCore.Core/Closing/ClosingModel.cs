using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FinanceCore.Core.Hashing;
using FinanceCore.Core.Posting;

namespace FinanceCore.Core.Closing;

/// <summary>Payload of revaluation:run and revaluation:preview (05, 3.1): one regular period of a company.</summary>
public sealed record RunRevaluationRequest(Guid CompanyId, int FiscalYear, int PeriodNo);

/// <summary>Payload of year-end:close (05, 5.1).</summary>
public sealed record CloseYearRequest(Guid CompanyId, int FiscalYear);

/// <summary>Payload of year-end:reopen (05, 5.3): reason code and text are mandatory.</summary>
public sealed record ReopenYearRequest(Guid CompanyId, int FiscalYear, string ReasonCode, string ReasonText);

public sealed record RevaluationRateResult(string FromCurrency, string ToCurrency, decimal Rate, DateOnly RateDate);

public sealed record RevaluationItemResult(
    int ItemNo, string BasisType, Guid AccountId, Guid? PartnerId, string CurrencyTc, Guid? OpenItemDocumentId, int? OpenItemLineNo,
    decimal RemainingTc, decimal CarriedLc, decimal CarriedGc, decimal? CarriedRc, decimal RevaluedLc, decimal RevaluedGc, decimal? RevaluedRc,
    decimal AdjustmentLc, decimal AdjustmentGc, decimal? AdjustmentRc, int? AdjustmentLineNo);

/// <summary>The result of a revaluation run or preview. A preview has no run id and no documents. A run that found nothing to adjust has no documents (05, 3.4, rule 5).</summary>
public sealed record RevaluationResult(
    Guid? RunId, int RunNo, Guid? SupersedesRunId, int FiscalYear, int PeriodNo, DateOnly ValuationDate, int ItemCount, string ItemsDigest,
    PostedDocument? Document, PostedDocument? ReversalDocument, IReadOnlyList<PostedDocument> RerunMirrors,
    IReadOnlyList<RevaluationRateResult> Rates, IReadOnlyList<RevaluationItemResult> Items);

public sealed record YearEndResult(
    Guid CompanyId, int FiscalYear, int ClosingPeriodNo, PostedDocument? Document, Guid CloseEventId, int AnchorRequests);

public sealed record YearReopenResult(
    Guid CompanyId, int FiscalYear, int ClosingPeriodNo, PostedDocument? Mirror, Guid ReopenEventId);

/// <summary>The fingerprints of the closing commands (06, 4.1).</summary>
public static class ClosingFingerprint
{
    private static readonly FieldSpec[] RunSpec = [new("company_id", FieldType.Id), new("fiscal_year", FieldType.Int), new("period_no", FieldType.Int)];
    private static readonly FieldSpec[] CloseSpec = [new("company_id", FieldType.Id), new("fiscal_year", FieldType.Int)];
    private static readonly FieldSpec[] ReopenSpec =
        [new("company_id", FieldType.Id), new("fiscal_year", FieldType.Int), new("reason_code", FieldType.Text), new("reason_text", FieldType.Text)];

    public static byte[] ForRun(RunRevaluationRequest r) => Periods.CommandFingerprint.Compute("revaluation:run", RunSpec,
        new Dictionary<string, object?> { ["company_id"] = r.CompanyId, ["fiscal_year"] = (long)r.FiscalYear, ["period_no"] = (long)r.PeriodNo });

    public static byte[] ForClose(CloseYearRequest r) => Periods.CommandFingerprint.Compute("year-end:close", CloseSpec,
        new Dictionary<string, object?> { ["company_id"] = r.CompanyId, ["fiscal_year"] = (long)r.FiscalYear });

    public static byte[] ForReopen(ReopenYearRequest r) => Periods.CommandFingerprint.Compute("year-end:reopen", ReopenSpec,
        new Dictionary<string, object?> { ["company_id"] = r.CompanyId, ["fiscal_year"] = (long)r.FiscalYear, ["reason_code"] = r.ReasonCode, ["reason_text"] = r.ReasonText });
}

/// <summary>The rates of a line that has no table rate (05, 5.2, 02, 5.2 D9): the amounts are authoritative and the rates are their ratios.</summary>
public static class ClosingMath
{
    /// <summary>Half away from zero, as everywhere in the core (F-IMPL-6).</summary>
    public static decimal Round(decimal value, int scale) => Math.Round(value, scale, MidpointRounding.AwayFromZero);

    /// <summary>Rates for LC, GC and RC with the transaction amount equal to LC: the ratio to the first non-zero amount, 1 for that currency itself and for
    /// an amount of zero. A ratio is never zero or negative (a line needs a positive rate), so a different sign gives the ratio of the absolute amounts.</summary>
    public static (decimal Lc, decimal Gc, decimal? Rc) DerivedRates(decimal lc, decimal gc, decimal? rc)
    {
        decimal basis = lc != 0m ? lc : gc != 0m ? gc : rc ?? 0m;
        decimal Ratio(decimal x)
        {
            if (x == 0m || basis == 0m) return 1m;
            decimal r = Round(Math.Abs(x / basis), Canonical.RateScale);
            return r > 0m ? r : 1m;
        }
        // the currency that carries the basis has the rate 1
        decimal gcRate = lc != 0m ? Ratio(gc) : 1m;
        decimal? rcRate = rc is null ? null : lc != 0m || gc != 0m ? Ratio(rc.Value) : 1m;
        return (1m, gcRate, rcRate);
    }
}

/// <summary>The digest of an anchor request (02, 9.5): what is sent to the timestamp authority.</summary>
public static class AnchorHash
{
    public static byte[] Digest(Guid tenantId, Guid companyId, string chainType, string scopeKey, long chainSeq, byte[] rowHash, DateTime anchoredAtUtc)
    {
        string text = string.Join('|', "anchor", tenantId.ToString("D").ToLowerInvariant(), companyId.ToString("D").ToLowerInvariant(), chainType, scopeKey,
            chainSeq.ToString(CultureInfo.InvariantCulture), Canonical.FormatHex32(rowHash),
            anchoredAtUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture));
        return SHA256.HashData(Encoding.UTF8.GetBytes(text));
    }
}

/// <summary>One basis unit of a revaluation (05, 3.1): an open item or the balance of an account in a transaction currency, at the valuation date.</summary>
public sealed record RevaluationUnit(
    string BasisType, Guid AccountId, Guid? PartnerId, Guid? IcCounterpartCompanyId, string CurrencyTc, Guid? DocumentId, int? LineNo,
    decimal RemainingTc, decimal CarriedLc, decimal CarriedGc, decimal? CarriedRc);

/// <summary>The currencies of a company at the valuation date: LC, GC and RC (null without reporting currency) with their decimal places.</summary>
public sealed record RevaluationTargets(string Lc, int ScaleLc, string Gc, int ScaleGc, string? Rc, int ScaleRc);

/// <summary>One adjustment line of the REVALUATION document: per account, partner and currency (05, 3.3).</summary>
public sealed record AdjustmentLine(int LineNo, Guid AccountId, string CurrencyTc, decimal Lc, decimal Gc, decimal? Rc);

/// <summary>One result line of the document: the opposite of the adjustments of a currency and a direction (05, 3.3).</summary>
public sealed record ResultLine(string CurrencyTc, bool IsGain, decimal Lc, decimal Gc, decimal? Rc);

/// <summary>The computation of a run (05, 3.2, 3.3): items, adjustment lines, result lines and the digest that the document and the run carry.</summary>
public sealed record RevaluationPlan(
    IReadOnlyList<RevaluationRateResult> Rates, IReadOnlyList<RevaluationItemResult> Items, IReadOnlyList<AdjustmentLine> Adjustments,
    IReadOnlyList<ResultLine> Results, byte[] Digest);

public static class RevaluationModel
{
    /// <summary>The currency pairs whose closing rates the units need: the transaction currency to each of LC, GC and RC where they differ.</summary>
    public static IReadOnlyList<(string From, string To)> RatePairs(IEnumerable<RevaluationUnit> units, RevaluationTargets t)
    {
        var pairs = new SortedSet<(string, string)>();
        foreach (RevaluationUnit u in units)
            foreach (string? target in new[] { t.Lc, t.Gc, t.Rc })
                if (target is not null && target != u.CurrencyTc) pairs.Add((u.CurrencyTc, target));
        return pairs.ToList();
    }

    /// <summary>Revalues the units at the rates and rolls the adjustments up into document lines. Pure: the same input gives the same digest.</summary>
    public static RevaluationPlan Build(IEnumerable<RevaluationUnit> units, IReadOnlyDictionary<(string From, string To), RevaluationRateResult> rates, RevaluationTargets t)
    {
        var ordered = units.OrderBy(u => u.BasisType, StringComparer.Ordinal).ThenBy(u => u.AccountId).ThenBy(u => u.PartnerId ?? Guid.Empty)
            .ThenBy(u => u.CurrencyTc, StringComparer.Ordinal).ThenBy(u => u.DocumentId ?? Guid.Empty).ThenBy(u => u.LineNo ?? 0).ToList();

        decimal Revalue(RevaluationUnit u, string? target, int scale, decimal carried)
        {
            if (target is null || target == u.CurrencyTc) return carried;   // nothing to revalue in a currency that is the transaction currency
            return ClosingMath.Round(u.RemainingTc * rates[(u.CurrencyTc, target)].Rate, scale);
        }

        var items = new List<(RevaluationUnit U, decimal Vl, decimal Vg, decimal? Vr)>();
        foreach (RevaluationUnit u in ordered)
            items.Add((u, Revalue(u, t.Lc, t.ScaleLc, u.CarriedLc), Revalue(u, t.Gc, t.ScaleGc, u.CarriedGc),
                u.CarriedRc is null ? null : Revalue(u, t.Rc, t.ScaleRc, u.CarriedRc.Value)));

        // the lines: one per (account, partner, currency), in that order, only where the adjustments are not all zero
        var lines = new List<AdjustmentLine>();
        var lineOf = new Dictionary<(Guid, Guid?, string), int?>();
        foreach (var g in items.GroupBy(i => (i.U.AccountId, i.U.PartnerId, i.U.CurrencyTc))
                     .OrderBy(g => g.Key.AccountId).ThenBy(g => g.Key.PartnerId ?? Guid.Empty).ThenBy(g => g.Key.CurrencyTc, StringComparer.Ordinal))
        {
            decimal lc = g.Sum(i => i.Vl - i.U.CarriedLc), gc = g.Sum(i => i.Vg - i.U.CarriedGc);
            decimal? rc = t.Rc is null ? null : g.Sum(i => (i.Vr ?? 0m) - (i.U.CarriedRc ?? 0m));
            if (lc == 0m && gc == 0m && (rc ?? 0m) == 0m) { lineOf[g.Key] = null; continue; }
            lines.Add(new AdjustmentLine(lines.Count + 1, g.Key.AccountId, g.Key.CurrencyTc, lc, gc, rc));
            lineOf[g.Key] = lines.Count;
        }

        // the result lines: per currency and direction, the direction follows the sign of the LC adjustment, a tie goes to gain (05, 3.3)
        var results = new List<ResultLine>();
        foreach (var g in lines.GroupBy(l => (l.CurrencyTc, Gain: l.Lc >= 0m)).OrderBy(g => g.Key.CurrencyTc, StringComparer.Ordinal).ThenByDescending(g => g.Key.Gain))
        {
            decimal lc = -g.Sum(l => l.Lc), gc = -g.Sum(l => l.Gc);
            decimal? rc = t.Rc is null ? null : -g.Sum(l => l.Rc ?? 0m);
            if (lc != 0m || gc != 0m || (rc ?? 0m) != 0m) results.Add(new ResultLine(g.Key.CurrencyTc, g.Key.Gain, lc, gc, rc));
        }

        var rateList = rates.Values.OrderBy(r => r.FromCurrency, StringComparer.Ordinal).ThenBy(r => r.ToCurrency, StringComparer.Ordinal).ToList();
        var itemResults = items.Select((i, n) =>
        {
            RevaluationUnit u = i.U;
            return new RevaluationItemResult(n + 1, u.BasisType, u.AccountId, u.PartnerId, u.CurrencyTc, u.DocumentId, u.LineNo, u.RemainingTc,
                u.CarriedLc, u.CarriedGc, u.CarriedRc, i.Vl, i.Vg, i.Vr, i.Vl - u.CarriedLc, i.Vg - u.CarriedGc, i.Vr is null ? null : i.Vr - u.CarriedRc,
                lineOf[(u.AccountId, u.PartnerId, u.CurrencyTc)]);
        }).ToList();
        return new RevaluationPlan(rateList, itemResults, lines, results, Digest(rateList, itemResults));
    }

    /// <summary>SHA-256 over the rates and then the items in fixed order (05, 3.5). The digest of nothing is the digest of the empty text.</summary>
    public static byte[] Digest(IReadOnlyList<RevaluationRateResult> rates, IReadOnlyList<RevaluationItemResult> items)
    {
        string A(decimal? v) => v is null ? "~" : v.Value.ToString("F4", CultureInfo.InvariantCulture);
        string G(Guid? v) => v is null ? "~" : v.Value.ToString("D");
        var text = new StringBuilder();
        foreach (RevaluationRateResult r in rates)
            text.Append(string.Join('|', "R", r.FromCurrency, r.ToCurrency, r.Rate.ToString("F10", CultureInfo.InvariantCulture), r.RateDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))).Append('\n');
        foreach (RevaluationItemResult i in items)
            text.Append(string.Join('|', "I", i.ItemNo.ToString(CultureInfo.InvariantCulture), i.BasisType, G(i.AccountId), G(i.PartnerId), i.CurrencyTc, G(i.OpenItemDocumentId),
                i.OpenItemLineNo?.ToString(CultureInfo.InvariantCulture) ?? "~", A(i.RemainingTc), A(i.CarriedLc), A(i.CarriedGc), A(i.CarriedRc),
                A(i.RevaluedLc), A(i.RevaluedGc), A(i.RevaluedRc), A(i.AdjustmentLc), A(i.AdjustmentGc), A(i.AdjustmentRc),
                i.AdjustmentLineNo?.ToString(CultureInfo.InvariantCulture) ?? "~")).Append('\n');
        return SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
    }
}

/// <summary>One group of P&amp;L lines to close (05, 5.1, step 1): an account and a dimension combination, with the sums of its lines.</summary>
public sealed record ClosingGroup(
    Guid AccountId, Guid? CostCenterId, Guid? ProjectId, Guid? ProfitCenterId, Guid? BusinessUnitId, IReadOnlyList<(Guid TypeId, Guid ValueId)> FreeDimensions,
    decimal Lc, decimal Gc, decimal? Rc);

/// <summary>One line of the YEAR_END document (05, 5.2).</summary>
public sealed record ClosingLine(
    int LineNo, Guid AccountId, Guid? CostCenterId, Guid? ProjectId, Guid? ProfitCenterId, Guid? BusinessUnitId,
    IReadOnlyList<(Guid TypeId, Guid ValueId)> FreeDimensions, decimal Lc, decimal Gc, decimal? Rc, decimal RateGc, decimal? RateRc);

public static class YearEndModel
{
    /// <summary>The closing lines: one per group that is non-zero in at least one currency, with the opposite sign and the dimensions of the group (D9), and one
    /// line on the retained earnings account with the net result. Without such a group there is no line and no document (05, 5.1).</summary>
    public static IReadOnlyList<ClosingLine> Build(IEnumerable<ClosingGroup> groups, Guid retainedEarningsAccountId, bool hasReportingCurrency)
    {
        var lines = new List<ClosingLine>();
        decimal sumLc = 0m, sumGc = 0m, sumRc = 0m;
        foreach (ClosingGroup g in groups.Where(g => g.Lc != 0m || g.Gc != 0m || (g.Rc ?? 0m) != 0m)
                     .OrderBy(g => g.AccountId).ThenBy(g => g.CostCenterId ?? Guid.Empty).ThenBy(g => g.ProjectId ?? Guid.Empty)
                     .ThenBy(g => g.ProfitCenterId ?? Guid.Empty).ThenBy(g => g.BusinessUnitId ?? Guid.Empty)
                     .ThenBy(g => string.Join(',', g.FreeDimensions.OrderBy(d => d.TypeId).Select(d => d.TypeId.ToString("N") + ":" + d.ValueId.ToString("N")))))
        {
            decimal lc = -g.Lc, gc = -g.Gc; decimal? rc = hasReportingCurrency ? -(g.Rc ?? 0m) : null;
            var (_, rg, rr) = ClosingMath.DerivedRates(lc, gc, rc);
            lines.Add(new ClosingLine(lines.Count + 1, g.AccountId, g.CostCenterId, g.ProjectId, g.ProfitCenterId, g.BusinessUnitId, g.FreeDimensions, lc, gc, rc, rg, rr));
            sumLc += lc; sumGc += gc; sumRc += rc ?? 0m;
        }
        if (lines.Count == 0) return lines;
        decimal nLc = -sumLc, nGc = -sumGc; decimal? nRc = hasReportingCurrency ? -sumRc : null;
        var (_, nrg, nrr) = ClosingMath.DerivedRates(nLc, nGc, nRc);
        lines.Add(new ClosingLine(lines.Count + 1, retainedEarningsAccountId, null, null, null, null, [], nLc, nGc, nRc, nrg, nrr));
        return lines;
    }
}
