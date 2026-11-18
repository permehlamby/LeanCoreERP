using FinanceCore.Core.Commands;
using FinanceCore.Core.Hashing;
using FinanceCore.Core.Posting;
using Npgsql;
using NpgsqlTypes;

namespace FinanceCore.Data;

/// <summary>One line of a system document of the closing commands: the REVALUATION, REVAL_REVERSAL and YEAR_END documents and their mirrors (05, 3.3, 5.2).</summary>
/// <summary>The open item row of a line (02, 5A): the partner, the dates and the legacy references of a migrated item (13, 6.3).</summary>
internal sealed record OpenItemSpec(Guid? PartnerId, DateOnly? DueDate, DateOnly? BaselineDate, DateOnly? LegacyDocumentDate, DateOnly? LegacyPostingDate, string? LegacyDocumentRef);

internal sealed record NewLine(
    Guid AccountId, string LineType, decimal Tc, decimal Lc, decimal Gc, decimal? Rc, decimal RateLc, decimal RateGc, decimal? RateRc, string RateType, DateOnly RateDate,
    int? ReversedLineNo = null, Guid? CostCenterId = null, Guid? ProjectId = null, Guid? ProfitCenterId = null, Guid? BusinessUnitId = null,
    IReadOnlyList<(Guid TypeId, Guid ValueId)>? FreeDimensions = null, OpenItemSpec? OpenItem = null);

internal sealed record DocumentSpec(
    string DocumentType, string CommandName, JournalDocumentPoster.SeriesRow Series, int FiscalYear, int PeriodNo, bool SoftOverride, DateOnly PostingDate,
    string IdempotencyKey, Guid? ReversalOf = null, string? ReasonCode = null, DateOnly? AutoReversalDate = null, byte[]? DetailDigest = null, string Origin = "SYSTEM", Guid? MigrationBatchId = null, string? TransactionCurrency = null);

/// <summary>The ledger of one command and the head of its chain, moved by every document the command posts.</summary>
internal sealed class ClosingLedger(TenantTransaction tx, Guid tenantId, Guid companyId, Guid ledgerId, JournalDocumentPoster.CompanyRow company, string groupCurrency, long seq, byte[] hash)
{
    public TenantTransaction Tx { get; } = tx;
    public Guid TenantId { get; } = tenantId;
    public Guid CompanyId { get; } = companyId;
    public Guid LedgerId { get; } = ledgerId;
    public JournalDocumentPoster.CompanyRow Company { get; } = company;
    public string GroupCurrency { get; } = groupCurrency;
    public long Seq { get; set; } = seq;
    public byte[] Hash { get; set; } = hash;
}

/// <summary>
/// The shared writer of the closing commands: numbers, chains and inserts one system document with its lines, dimensions and legal snapshot, and moves the LEDGER head.
/// The caller has locked the counters, the heads and the period rows, in the order of 04, 6.3, and has checked the period.
/// </summary>
internal static class ClosingDocuments
{
    /// <summary>Locks the counters of the documents in sorted order (04, 6.3). Null when all exist, else the error of the first missing one.</summary>
    public static async Task<CommandError?> LockCountersAsync(
        TenantTransaction tx, Guid tenantId, Guid companyId, IEnumerable<(JournalDocumentPoster.SeriesRow Series, int FiscalYear)> counters, CancellationToken ct)
    {
        foreach (var (series, year) in counters.DistinctBy(c => (c.Series.SeriesId, c.FiscalYear)).OrderBy(c => c.Series.SeriesId).ThenBy(c => c.FiscalYear))
            if (await ClearingPoster.LockCounterAsync(tx, tenantId, companyId, series.SeriesId, year, ct) is null)
                return JournalDocumentPoster.Error("LIMIT.NUMBER_COUNTER_MISSING", "P7", "", ("series_code", series.SeriesCode), ("fiscal_year", year));
        return null;
    }

    public static async Task<ClosingLedger?> OpenLedgerAsync(
        TenantTransaction tx, Guid tenantId, Guid companyId, Guid ledgerId, JournalDocumentPoster.CompanyRow company, string groupCurrency, CancellationToken ct)
    {
        (long seq, byte[] hash) = await ClearingPoster.LockLedgerHeadAsync(tx, tenantId, companyId, ledgerId, ct);
        return new ClosingLedger(tx, tenantId, companyId, ledgerId, company, groupCurrency, seq, hash);
    }

    /// <summary>The line of a document with an amount in every currency and the rates of ClosingMath (TC given).</summary>
    public static NewLine Line(
        ClosingLedger l, Guid account, string lineType, decimal tc, decimal lc, decimal gc, decimal? rc, DateOnly rateDate,
        Guid? cc = null, Guid? project = null, Guid? profitCenter = null, Guid? businessUnit = null, IReadOnlyList<(Guid, Guid)>? free = null)
    {
        bool hasRc = l.Company.ReportingCurrency is not null;
        var (rl, rg, rr) = Core.Closing.ClosingMath.DerivedRates(lc, gc, hasRc ? rc ?? 0m : null);
        return new NewLine(account, lineType, tc, lc, gc, hasRc ? rc ?? 0m : null, rl, rg, rr, "DERIVED", rateDate, null, cc, project, profitCenter, businessUnit, free);
    }

    /// <summary>The mirror lines of a document: opposite signs, the rates and the dimensions of the original (05, 3.6, 5.3).</summary>
    public static async Task<List<NewLine>> MirrorLinesAsync(TenantTransaction tx, Guid tenantId, Guid companyId, LedgerReader.StoredDocument original, CancellationToken ct)
    {
        Guid documentId = (Guid)original.Header["document_id"]!;
        var typed = new Dictionary<int, (Guid?, Guid?, Guid?, Guid?)>();
        await using (var cmd = tx.Command(
            "select line_no, cost_center_id, project_id, profit_center_id, business_unit_id from core.journal_line where tenant_id = @t and company_id = @c and document_id = @d"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", documentId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                typed[r.GetInt32(0)] = (r.IsDBNull(1) ? null : r.GetGuid(1), r.IsDBNull(2) ? null : r.GetGuid(2), r.IsDBNull(3) ? null : r.GetGuid(3), r.IsDBNull(4) ? null : r.GetGuid(4));
        }
        var free = new Dictionary<int, List<(Guid, Guid)>>();
        await using (var cmd = tx.Command(
            "select line_no, dimension_type_id, dimension_value_id from core.journal_line_dimension where tenant_id = @t and company_id = @c and document_id = @d order by line_no, dimension_type_id"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", documentId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (!free.TryGetValue(r.GetInt32(0), out var list)) free[r.GetInt32(0)] = list = [];
                list.Add((r.GetGuid(1), r.GetGuid(2)));
            }
        }
        var result = new List<NewLine>();
        foreach (var o in original.Lines.OrderBy(l => (int)l["line_no"]!))
        {
            int no = (int)o["line_no"]!;
            var (cc, pr, pc, bu) = typed.GetValueOrDefault(no);
            result.Add(new NewLine((Guid)o["account_id"]!, (string)o["line_type"]!, -(decimal)o["amount_tc"]!, -(decimal)o["amount_lc"]!, -(decimal)o["amount_gc"]!,
                o["amount_rc"] is decimal rc ? -rc : null, (decimal)o["rate_lc"]!, (decimal)o["rate_gc"]!, (decimal?)o["rate_rc"], (string)o["rate_type"]!,
                (DateOnly)o["rate_date"]!, no, cc, pr, pc, bu, free.GetValueOrDefault(no)));
        }
        return result;
    }

    /// <summary>Numbers, chains and inserts the document, its lines and its legal snapshot, and moves the LEDGER head. Null error when it went in.</summary>
    public static async Task<(PostedDocument? Document, CommandError? Error)> PostAsync(
        ClosingLedger l, CommandContext ctx, DocumentSpec spec, IReadOnlyList<NewLine> newLines, Guid? copySnapshotFrom, CancellationToken ct)
    {
        TenantTransaction tx = l.Tx;
        long? last = await ClearingPoster.LockCounterAsync(tx, l.TenantId, l.CompanyId, spec.Series.SeriesId, spec.FiscalYear, ct);
        if (last is null)
            return (null, JournalDocumentPoster.Error("LIMIT.NUMBER_COUNTER_MISSING", "P7", "", ("series_code", spec.Series.SeriesCode), ("fiscal_year", spec.FiscalYear)));
        long sequenceNo = last.Value + 1;
        await using (var cmd = tx.Command(
            "update core.number_range_counter set last_number = @n, updated_at = now() where tenant_id = @t and company_id = @c and series_id = @s and fiscal_year = @y"))
        {
            cmd.Parameters.AddWithValue("n", sequenceNo); cmd.Parameters.AddWithValue("t", l.TenantId); cmd.Parameters.AddWithValue("c", l.CompanyId);
            cmd.Parameters.AddWithValue("s", spec.Series.SeriesId); cmd.Parameters.AddWithValue("y", spec.FiscalYear);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        string number = PostingRules.FormatNumber(spec.Series.Prefix, spec.Series.IncludeFiscalYear, spec.Series.Separator, spec.Series.NumberLength, spec.FiscalYear, sequenceNo);
        Guid documentId = Guid.NewGuid();
        long chainSeq = l.Seq + 1;
        DateTime now = DateTime.UtcNow;
        DateTime postedAt = new(now.Ticks - now.Ticks % 10, DateTimeKind.Utc);
        string lc = l.Company.LocalCurrency;
        string tcCurrency = spec.TransactionCurrency ?? lc;
        bool hasRc = l.Company.ReportingCurrency is not null;

        var header = new Dictionary<string, object?>
        {
            ["tenant_id"] = l.TenantId, ["company_id"] = l.CompanyId, ["document_id"] = documentId, ["ledger_id"] = l.LedgerId,
            ["document_type"] = spec.DocumentType, ["is_system_generated"] = true, ["number_series_id"] = spec.Series.SeriesId,
            ["fiscal_year"] = spec.FiscalYear, ["sequence_no"] = sequenceNo, ["document_number"] = number,
            ["fiscal_period"] = spec.PeriodNo, ["soft_close_override"] = spec.SoftOverride, ["detail_digest"] = spec.DetailDigest, ["migration_batch_id"] = spec.MigrationBatchId,
            ["document_date"] = spec.PostingDate, ["posting_date"] = spec.PostingDate, ["posted_at"] = postedAt, ["transaction_currency"] = tcCurrency,
            ["reversal_of_document_id"] = spec.ReversalOf, ["reversal_reason_code"] = spec.ReasonCode, ["auto_reversal_date"] = spec.AutoReversalDate,
            ["ic_transaction_id"] = null, ["ic_counterpart_company_id"] = null, ["origin"] = spec.Origin,
            ["source_system"] = ctx.SourceSystem, ["source_document_ref"] = null, ["idempotency_key"] = spec.IdempotencyKey,
            ["command_name"] = spec.CommandName, ["posted_by"] = ctx.Actor, ["correlation_id"] = ctx.CorrelationId, ["hash_version"] = (long)LedgerHash2.Version
        };
        var rows = new List<Dictionary<string, object?>>();
        for (int i = 0; i < newLines.Count; i++)
        {
            NewLine n = newLines[i];
            rows.Add(new Dictionary<string, object?>
            {
                ["tenant_id"] = l.TenantId, ["company_id"] = l.CompanyId, ["document_id"] = documentId, ["line_no"] = i + 1, ["ledger_id"] = l.LedgerId,
                ["line_type"] = n.LineType, ["account_id"] = n.AccountId, ["posting_date"] = spec.PostingDate, ["fiscal_year"] = spec.FiscalYear,
                ["fiscal_period"] = spec.PeriodNo, ["reversed_line_no"] = n.ReversedLineNo, ["tax_code_id"] = null,
                ["amount_tc"] = n.Tc, ["amount_lc"] = n.Lc, ["currency_lc"] = lc, ["rate_lc"] = n.RateLc, ["rate_lc_table"] = null,
                ["amount_gc"] = n.Gc, ["currency_gc"] = l.GroupCurrency, ["rate_gc"] = n.RateGc, ["rate_gc_table"] = null,
                ["amount_rc"] = hasRc ? n.Rc : null, ["currency_rc"] = hasRc ? l.Company.ReportingCurrency : null, ["rate_rc"] = hasRc ? n.RateRc : null, ["rate_rc_table"] = null,
                ["rate_type"] = n.RateType, ["rate_date"] = n.RateDate, ["ic_counterpart_company_id"] = null
            });
        }
        byte[] rowHash = LedgerHash2.RowHash(header, chainSeq, l.Hash, LedgerHash2.LineDigest(rows.Select((r, i) => LedgerHash2.LineHash(new HashedLine(r,
            OpenItem: newLines[i].OpenItem is { } oi
                ? new Dictionary<string, object?>
                {
                    ["tenant_id"] = l.TenantId, ["company_id"] = l.CompanyId, ["document_id"] = documentId, ["line_no"] = i + 1, ["partner_id"] = oi.PartnerId,
                    ["legacy_document_date"] = oi.LegacyDocumentDate, ["legacy_posting_date"] = oi.LegacyPostingDate, ["legacy_document_ref"] = oi.LegacyDocumentRef
                }
                : null))).ToList()));

        await using (var cmd = tx.Command(
            "insert into core.journal_document (tenant_id, company_id, document_id, ledger_id, document_type, is_system_generated, number_series_id, fiscal_year, sequence_no, " +
            "document_number, fiscal_period, soft_close_override, migration_batch_id, detail_digest, document_date, posting_date, posted_at, transaction_currency, reversal_of_document_id, " +
            "reversal_reason_code, auto_reversal_date, origin, source_system, idempotency_key, command_name, posted_by, correlation_id, chain_seq, prev_hash, row_hash, hash_version) " +
            "values (@t, @c, @id, @l, @dt, true, @s, @fy, @seq, @num, @fp, @soft, @mb, @dig, @d, @d, @at, @tc, @rev, @reason, @auto, @origin, @src, @key, @cmd, @by, @corr, @cs, @prev, @row, @hv)"))
        {
            cmd.Parameters.AddWithValue("t", l.TenantId); cmd.Parameters.AddWithValue("c", l.CompanyId); cmd.Parameters.AddWithValue("id", documentId);
            cmd.Parameters.AddWithValue("l", l.LedgerId); cmd.Parameters.AddWithValue("dt", spec.DocumentType); cmd.Parameters.AddWithValue("origin", spec.Origin);
            cmd.Parameters.Add(new NpgsqlParameter("mb", NpgsqlDbType.Uuid) { Value = (object?)spec.MigrationBatchId ?? DBNull.Value }); cmd.Parameters.AddWithValue("s", spec.Series.SeriesId);
            cmd.Parameters.AddWithValue("fy", spec.FiscalYear); cmd.Parameters.AddWithValue("seq", sequenceNo); cmd.Parameters.AddWithValue("num", number);
            cmd.Parameters.AddWithValue("fp", spec.PeriodNo); cmd.Parameters.AddWithValue("soft", spec.SoftOverride);
            cmd.Parameters.Add(new NpgsqlParameter("dig", NpgsqlDbType.Bytea) { Value = (object?)spec.DetailDigest ?? DBNull.Value });
            cmd.Parameters.AddWithValue("d", spec.PostingDate); cmd.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = postedAt });
            cmd.Parameters.AddWithValue("tc", tcCurrency);
            cmd.Parameters.Add(new NpgsqlParameter("rev", NpgsqlDbType.Uuid) { Value = (object?)spec.ReversalOf ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("reason", NpgsqlDbType.Text) { Value = (object?)spec.ReasonCode ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("auto", NpgsqlDbType.Date) { Value = (object?)spec.AutoReversalDate ?? DBNull.Value });
            cmd.Parameters.AddWithValue("src", ctx.SourceSystem); cmd.Parameters.AddWithValue("key", spec.IdempotencyKey); cmd.Parameters.AddWithValue("cmd", spec.CommandName);
            cmd.Parameters.AddWithValue("by", ctx.Actor); cmd.Parameters.AddWithValue("corr", ctx.CorrelationId); cmd.Parameters.AddWithValue("cs", chainSeq);
            cmd.Parameters.AddWithValue("prev", l.Hash); cmd.Parameters.AddWithValue("row", rowHash); cmd.Parameters.AddWithValue("hv", (short)LedgerHash2.Version);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        if (await LegalSnapshots.WriteAsync(tx, l.TenantId, l.CompanyId, documentId, spec.FiscalYear, spec.PostingDate, postedAt, ct, copyFromDocumentId: copySnapshotFrom) is { } noSnapshot)
            return (null, noSnapshot);
        for (int i = 0; i < newLines.Count; i++) await InsertLineAsync(l, documentId, i + 1, newLines[i], spec, hasRc, ct);
        await ClearingPoster.MoveHeadAsync(tx, l.TenantId, l.CompanyId, "LEDGER", l.LedgerId.ToString("D"), chainSeq, rowHash, LedgerHash2.Version, ct);

        byte[] prev = l.Hash;
        l.Seq = chainSeq; l.Hash = rowHash;
        var lines = rows.Select(r => new PostedLine((int)r["line_no"]!, null, (Guid)r["account_id"]!, (string)r["line_type"]!, (decimal)r["amount_tc"]!, (decimal)r["amount_lc"]!,
            lc, (decimal)r["rate_lc"]!, (decimal)r["amount_gc"]!, l.GroupCurrency, (decimal)r["rate_gc"]!, (decimal?)r["amount_rc"], (string?)r["currency_rc"], (decimal?)r["rate_rc"],
            (string)r["rate_type"]!, (DateOnly)r["rate_date"]!)).ToList();
        return (new PostedDocument(documentId, l.CompanyId, l.LedgerId, spec.DocumentType, number, sequenceNo, spec.FiscalYear, spec.PeriodNo, spec.PostingDate, spec.PostingDate,
            tcCurrency, "posted", spec.SoftOverride, chainSeq, Canonical.FormatHex32(rowHash), Canonical.FormatHex32(prev), LedgerHash2.Version, lines), null);
    }

    private static async Task InsertLineAsync(ClosingLedger l, Guid documentId, int lineNo, NewLine n, DocumentSpec spec, bool hasRc, CancellationToken ct)
    {
        await using (var cmd = l.Tx.Command(
            "insert into core.journal_line (tenant_id, company_id, document_id, line_no, ledger_id, line_type, account_id, posting_date, fiscal_year, fiscal_period, reversed_line_no, " +
            "amount_tc, amount_lc, currency_lc, rate_lc, amount_gc, currency_gc, rate_gc, amount_rc, currency_rc, rate_rc, rate_type, rate_date, " +
            "cost_center_id, project_id, profit_center_id, business_unit_id) " +
            "values (@t, @c, @d, @n, @l, @lt, @a, @pd, @fy, @fp, @rev, @tc, @lc, @clc, @rlc, @gc, @cgc, @rgc, @rc, @crc, @rrc, @rt, @rd, @cc, @pr, @pc, @bu)"))
        {
            cmd.Parameters.AddWithValue("t", l.TenantId); cmd.Parameters.AddWithValue("c", l.CompanyId); cmd.Parameters.AddWithValue("d", documentId);
            cmd.Parameters.AddWithValue("n", lineNo); cmd.Parameters.AddWithValue("l", l.LedgerId); cmd.Parameters.AddWithValue("lt", n.LineType);
            cmd.Parameters.AddWithValue("a", n.AccountId); cmd.Parameters.AddWithValue("pd", spec.PostingDate); cmd.Parameters.AddWithValue("fy", spec.FiscalYear);
            cmd.Parameters.AddWithValue("fp", spec.PeriodNo);
            cmd.Parameters.Add(new NpgsqlParameter("rev", NpgsqlDbType.Integer) { Value = (object?)n.ReversedLineNo ?? DBNull.Value });
            cmd.Parameters.AddWithValue("tc", n.Tc); cmd.Parameters.AddWithValue("lc", n.Lc); cmd.Parameters.AddWithValue("clc", l.Company.LocalCurrency);
            cmd.Parameters.AddWithValue("rlc", n.RateLc); cmd.Parameters.AddWithValue("gc", n.Gc); cmd.Parameters.AddWithValue("cgc", l.GroupCurrency);
            cmd.Parameters.AddWithValue("rgc", n.RateGc);
            cmd.Parameters.Add(new NpgsqlParameter("rc", NpgsqlDbType.Numeric) { Value = hasRc ? (object?)n.Rc ?? DBNull.Value : DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("crc", NpgsqlDbType.Text) { Value = hasRc ? (object?)l.Company.ReportingCurrency ?? DBNull.Value : DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("rrc", NpgsqlDbType.Numeric) { Value = hasRc ? (object?)n.RateRc ?? DBNull.Value : DBNull.Value });
            cmd.Parameters.AddWithValue("rt", n.RateType); cmd.Parameters.AddWithValue("rd", n.RateDate);
            cmd.Parameters.Add(new NpgsqlParameter("cc", NpgsqlDbType.Uuid) { Value = (object?)n.CostCenterId ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("pr", NpgsqlDbType.Uuid) { Value = (object?)n.ProjectId ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("pc", NpgsqlDbType.Uuid) { Value = (object?)n.ProfitCenterId ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("bu", NpgsqlDbType.Uuid) { Value = (object?)n.BusinessUnitId ?? DBNull.Value });
            await cmd.ExecuteNonQueryAsync(ct);
        }
        if (n.OpenItem is { } item)
        {
            await using var cmd = l.Tx.Command(
                "insert into core.journal_line_open_item (tenant_id, company_id, document_id, line_no, partner_id, baseline_date, due_date, legacy_document_date, legacy_posting_date, " +
                "legacy_document_ref, cleared_amount_rc) values (@t, @c, @d, @n, @p, @bd, @due, @ldd, @lpd, @ref, @rc)");
            cmd.Parameters.AddWithValue("t", l.TenantId); cmd.Parameters.AddWithValue("c", l.CompanyId); cmd.Parameters.AddWithValue("d", documentId); cmd.Parameters.AddWithValue("n", lineNo);
            cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Uuid) { Value = (object?)item.PartnerId ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("bd", NpgsqlDbType.Date) { Value = (object?)item.BaselineDate ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("due", NpgsqlDbType.Date) { Value = (object?)item.DueDate ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("ldd", NpgsqlDbType.Date) { Value = (object?)item.LegacyDocumentDate ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("lpd", NpgsqlDbType.Date) { Value = (object?)item.LegacyPostingDate ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("ref", NpgsqlDbType.Text) { Value = (object?)item.LegacyDocumentRef ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("rc", NpgsqlDbType.Numeric) { Value = hasRc ? 0m : DBNull.Value });
            await cmd.ExecuteNonQueryAsync(ct);
        }
        foreach (var (typeId, valueId) in n.FreeDimensions ?? [])
        {
            await using var cmd = l.Tx.Command(
                "insert into core.journal_line_dimension (tenant_id, company_id, document_id, line_no, dimension_type_id, dimension_value_id) values (@t, @c, @d, @n, @ty, @v)");
            cmd.Parameters.AddWithValue("t", l.TenantId); cmd.Parameters.AddWithValue("c", l.CompanyId); cmd.Parameters.AddWithValue("d", documentId);
            cmd.Parameters.AddWithValue("n", lineNo); cmd.Parameters.AddWithValue("ty", typeId); cmd.Parameters.AddWithValue("v", valueId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>The event of a posted document for the outbox (15, 6): ledger.document_posted, or ledger.document_reversed for a mirror (the entity is the original).</summary>
    public static async Task WriteDocumentEventAsync(ClosingLedger l, CommandContext ctx, PostedDocument doc, string eventType, Guid entityDocumentId, string change, CancellationToken ct) =>
        await CommandSupport.WriteOutboxAsync(l.Tx, l.TenantId, l.CompanyId, ctx, eventType, "DOCUMENT", $"{l.CompanyId:D}/{entityDocumentId:D}", change,
            DateTime.UtcNow, isAudit: true,
            System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["chain_type"] = "LEDGER", ["scope"] = l.LedgerId.ToString("D"), ["chain_seq"] = doc.ChainSeq, ["row_hash"] = doc.RowHash
            }),
            System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["document_type"] = doc.DocumentType, ["posting_date"] = doc.PostingDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                ["period"] = doc.FiscalPeriod, ["ledger_ids"] = new[] { l.LedgerId.ToString("D") }, ["ledger_seq"] = doc.ChainSeq
            }), ct);
}
