using System.Globalization;
using System.Text.Json;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Hashing;
using FinanceCore.Core.Posting;
using Npgsql;
using NpgsqlTypes;

namespace FinanceCore.Data;

/// <summary>
/// journal-documents:reverse (06, 5.3): a new document of type REVERSAL that mirrors every line of the original 1:1 with the
/// opposite sign (same line_no, same rates and currencies, reversed_line_no set, no recomputation, 02 Q8), with its own number
/// and chain node. The original gets status reversed and reversed_by_document_id (S columns). Only documents that the posting
/// command made (origin EXTERNAL, allow-listed type) can be reversed here. The check for cleared lines (ADR-039) and the mirror
/// open items wait for the open item tables.
/// Locks (04, 6.3): number counter, LEDGER head, the original document row, period rows in share mode, outbox_head last.
/// </summary>
public sealed class DocumentReverser(NpgsqlDataSource source)
{
    public const string CommandName = "journal-documents:reverse";
    public const string Permission = "journal.reverse";
    public const string MirrorType = "REVERSAL";

    public async Task<CommandResult<PostedDocument>> ReverseAsync(
        Guid tenantId, CommandContext ctx, ReverseDocumentRequest req, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(Permission))
            return Reject(JournalDocumentPoster.Error("AUTH.PERMISSION_DENIED", "P0", "", ("permission", Permission)));

        var shape = new List<CommandError>();
        void Invalid(string path, string reason) => shape.Add(JournalDocumentPoster.Error("SCHEMA.INVALID_VALUE", "P1", path, ("reason", reason)));
        if (string.IsNullOrWhiteSpace(ctx.IdempotencyKey) || ctx.IdempotencyKey.Length > 100) Invalid("idempotency_key", "required, at most 100 characters");
        if (string.IsNullOrWhiteSpace(ctx.CorrelationId) || ctx.CorrelationId.Length > 100) Invalid("correlation_id", "required, at most 100 characters");
        if (string.IsNullOrWhiteSpace(ctx.Actor) || ctx.Actor.Length > 100) Invalid("actor", "required, at most 100 characters");
        if (req.DocumentId == Guid.Empty) Invalid("document_id", "required");
        if (req.ReasonText?.Length > 500) Invalid("reason_text", "at most 500 characters");
        if (shape.Count > 0) return CommandResult<PostedDocument>.Rejected(shape);

        if (string.IsNullOrWhiteSpace(req.ReasonCode))
            return Reject(JournalDocumentPoster.Error("REVERSAL.REASON_REQUIRED", "P1", "reason_code"));
        if (!ReversalRules.ExternalReasons.Contains(req.ReasonCode))
            return Reject(JournalDocumentPoster.Error("REVERSAL.REASON_NOT_ALLOWED", "P1", "reason_code",
                ("allowed_values", ReversalRules.ExternalReasons)));

        byte[] fingerprint = ReversalRules.Fingerprint(req);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await ReverseOnceAsync(tenantId, ctx, req, fingerprint, ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.TableName == "command_log" && attempt == 0)
            {
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                return CommandResult<PostedDocument>.Failed(new CommandError("SYSTEM.LOCK_TIMEOUT", "P7", "", null, true));
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure)
            {
                return CommandResult<PostedDocument>.Failed(new CommandError("SYSTEM.CONFLICT", "P7", "", null, true));
            }
        }
    }

    private async Task<CommandResult<PostedDocument>> ReverseOnceAsync(
        Guid tenantId, CommandContext ctx, ReverseDocumentRequest req, byte[] fingerprint, CancellationToken ct)
    {
        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Post, ct: ct);

        // Idempotency
        CommandSupport.EarlierCommand? earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
        if (earlier is not null)
        {
            if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint))
                return Reject(JournalDocumentPoster.Error("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
            PostedDocument? replay = earlier.EntityId is null ? null : await LedgerReader.LoadDocumentAsync(tx, tenantId, earlier.EntityId.Value, ct);
            return replay is null
                ? CommandResult<PostedDocument>.Failed(JournalDocumentPoster.Error("SYSTEM.REPLAY_RESULT_MISSING", "P1", ""))
                : CommandResult<PostedDocument>.Ok(replay, earlier.CommandLogId, replay: true);
        }

        // P2 and P6: the original
        var found = await LedgerReader.ReadDocumentsAsync(tx, tenantId, req.DocumentId, null, null, ct);
        if (found.Count == 0)
            return Reject(JournalDocumentPoster.Error("REVERSAL.NOT_FOUND", "P2", "document_id"));
        LedgerReader.StoredDocument original = found[0];
        string type = (string)original.Header["document_type"]!;
        Guid companyId = (Guid)original.Header["company_id"]!;
        Guid ledgerId = (Guid)original.Header["ledger_id"]!;

        if (!ReversalRules.ReversibleTypes.Contains(type) || (string)original.Header["origin"]! != "EXTERNAL")
            return Reject(JournalDocumentPoster.Error("REVERSAL.TYPE_NOT_REVERSIBLE", "P2", "document_id",
                ("document_type", type), ("owning_command", ReversalRules.OwningCommand(type))));
        if (original.Status != "posted")
            return Reject(JournalDocumentPoster.Error("REVERSAL.ALREADY_REVERSED", "P2", "document_id"));

        JournalDocumentPoster.CompanyRow? company = await JournalDocumentPoster.ReadCompanyAsync(tx, tenantId, companyId, ct);
        if (company is null || company.Status != "ACTIVE")
            return Reject(JournalDocumentPoster.Error("COMPANY.NOT_ACTIVE", "P2", "document_id", ("status", company?.Status)));

        DateOnly reversalDate = req.ReversalDate ?? (DateOnly)original.Header["posting_date"]!;
        var errors = new List<CommandError>();
        var warnings = new List<CommandError>();

        JournalDocumentPoster.SeriesRow? series = await JournalDocumentPoster.ReadSeriesAsync(tx, tenantId, companyId, MirrorType, reversalDate, ledgerId, ct);
        if (series is null)
            errors.Add(JournalDocumentPoster.Error("SERIES.NO_ASSIGNMENT", "P2", "reversal_date",
                ("document_type", MirrorType), ("ledger_id", ledgerId), ("posting_date", reversalDate)));

        // P3: the reversal date must be postable. The core never moves it (06, 5.3, Q8).
        JournalDocumentPoster.PeriodCheck period = await JournalDocumentPoster.CheckPeriodAsync(tx, tenantId, companyId, reversalDate, ctx, false, ct);
        errors.AddRange(period.Errors.Select(e => e with { Path = "reversal_date" }));
        warnings.AddRange(period.Warnings);
        if (errors.Count > 0) return CommandResult<PostedDocument>.Rejected(errors);

        // P7: counter, LEDGER head, the original, period rows
        int fiscalYear = period.FiscalYear!.Value;
        long lastNumber;
        await using (var cmd = tx.Command(
            "select last_number from core.number_range_counter " +
            "where tenant_id = @t and company_id = @c and series_id = @s and fiscal_year = @y for update"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", companyId);
            cmd.Parameters.AddWithValue("s", series!.SeriesId);
            cmd.Parameters.AddWithValue("y", fiscalYear);
            object? value = await cmd.ExecuteScalarAsync(ct);
            if (value is null)
                return Reject(JournalDocumentPoster.Error("LIMIT.NUMBER_COUNTER_MISSING", "P7", "reversal_date",
                    ("series_code", series.SeriesCode), ("fiscal_year", fiscalYear)));
            lastNumber = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        long sequenceNo = lastNumber + 1;
        await using (var cmd = tx.Command(
            "update core.number_range_counter set last_number = @n, updated_at = now() " +
            "where tenant_id = @t and company_id = @c and series_id = @s and fiscal_year = @y"))
        {
            cmd.Parameters.AddWithValue("n", sequenceNo);
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", companyId);
            cmd.Parameters.AddWithValue("s", series.SeriesId);
            cmd.Parameters.AddWithValue("y", fiscalYear);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        long headSeq; byte[] headHash;
        await using (var cmd = tx.Command(
            "select last_seq, last_hash from core.chain_head " +
            "where tenant_id = @t and company_id = @c and chain_type = 'LEDGER' and scope_key = @k for update"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", companyId);
            cmd.Parameters.AddWithValue("k", ledgerId.ToString("D"));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
                return CommandResult<PostedDocument>.Failed(JournalDocumentPoster.Error("SYSTEM.CHAIN_HEAD_MISSING", "P7", "", ("ledger_id", ledgerId)));
            headSeq = r.GetInt64(0); headHash = (byte[])r[1];
        }

        // The original, locked, and its status read again: two reversals of one document are serialised by the head above,
        // the second one sees status reversed here.
        string status;
        await using (var cmd = tx.Command(
            "select status from core.journal_document where tenant_id = @t and company_id = @c and document_id = @d for update"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", companyId);
            cmd.Parameters.AddWithValue("d", req.DocumentId);
            status = (string)(await cmd.ExecuteScalarAsync(ct))!;
        }
        if (status != "posted") return Reject(JournalDocumentPoster.Error("REVERSAL.ALREADY_REVERSED", "P7", "document_id"));

        JournalDocumentPoster.PeriodCheck recheck = await JournalDocumentPoster.CheckPeriodAsync(tx, tenantId, companyId, reversalDate, ctx, true, ct);
        if (recheck.Errors.Count > 0)
            return CommandResult<PostedDocument>.Rejected(recheck.Errors.Select(e => e with { Path = "reversal_date" }).ToList());
        int periodNo = recheck.PeriodNo!.Value;

        // The mirror: opposite sign, everything else of the line as it was
        Guid mirrorId = Guid.NewGuid();
        DateTime now = DateTime.UtcNow;
        DateTime postedAt = new(now.Ticks - now.Ticks % 10, DateTimeKind.Utc);
        long chainSeq = headSeq + 1;
        string number = PostingRules.FormatNumber(series.Prefix, series.IncludeFiscalYear, series.Separator, series.NumberLength, fiscalYear, sequenceNo);
        string currency = (string)original.Header["transaction_currency"]!;

        var header = new Dictionary<string, object?>
        {
            ["tenant_id"] = tenantId, ["company_id"] = companyId, ["document_id"] = mirrorId, ["ledger_id"] = ledgerId,
            ["document_type"] = MirrorType, ["is_system_generated"] = false, ["number_series_id"] = series.SeriesId,
            ["fiscal_year"] = fiscalYear, ["sequence_no"] = sequenceNo, ["document_number"] = number, ["fiscal_period"] = periodNo,
            ["soft_close_override"] = recheck.SoftCloseOverride, ["migration_batch_id"] = null, ["detail_digest"] = null,
            ["document_date"] = reversalDate, ["posting_date"] = reversalDate, ["posted_at"] = postedAt, ["transaction_currency"] = currency,
            ["reversal_of_document_id"] = req.DocumentId, ["reversal_reason_code"] = req.ReasonCode, ["auto_reversal_date"] = null,
            ["ic_transaction_id"] = null, ["ic_counterpart_company_id"] = null, ["origin"] = "EXTERNAL",
            ["source_system"] = ctx.SourceSystem, ["source_document_ref"] = null, ["idempotency_key"] = ctx.IdempotencyKey,
            ["command_name"] = CommandName, ["posted_by"] = ctx.Actor, ["correlation_id"] = ctx.CorrelationId,
            ["hash_version"] = (long)LedgerHash2.Version
        };

        var mirrorLines = new List<Dictionary<string, object?>>();
        var lineHashes = new List<byte[]>();
        foreach (var o in original.Lines.OrderBy(l => (int)l["line_no"]!))
        {
            decimal? Neg(string key) => o[key] is decimal d ? -d : null;
            var m = new Dictionary<string, object?>
            {
                ["tenant_id"] = tenantId, ["company_id"] = companyId, ["document_id"] = mirrorId, ["line_no"] = o["line_no"],
                ["ledger_id"] = ledgerId, ["line_type"] = o["line_type"], ["account_id"] = o["account_id"],
                ["posting_date"] = reversalDate, ["fiscal_year"] = fiscalYear, ["fiscal_period"] = periodNo,
                ["reversed_line_no"] = o["line_no"], ["tax_code_id"] = o["tax_code_id"],
                ["amount_tc"] = Neg("amount_tc"), ["amount_lc"] = Neg("amount_lc"), ["currency_lc"] = o["currency_lc"],
                ["rate_lc"] = o["rate_lc"], ["rate_lc_table"] = o["rate_lc_table"],
                ["amount_gc"] = Neg("amount_gc"), ["currency_gc"] = o["currency_gc"], ["rate_gc"] = o["rate_gc"], ["rate_gc_table"] = o["rate_gc_table"],
                ["amount_rc"] = Neg("amount_rc"), ["currency_rc"] = o["currency_rc"], ["rate_rc"] = o["rate_rc"], ["rate_rc_table"] = o["rate_rc_table"],
                ["rate_type"] = o["rate_type"], ["rate_date"] = o["rate_date"], ["ic_counterpart_company_id"] = o["ic_counterpart_company_id"]
            };
            mirrorLines.Add(m);
            lineHashes.Add(LedgerHash2.LineHash(new HashedLine(m)));
        }
        byte[] rowHash = LedgerHash2.RowHash(header, chainSeq, headHash, LedgerHash2.LineDigest(lineHashes));

        await InsertDocumentAsync(tx, tenantId, companyId, ctx, req, mirrorId, ledgerId, series.SeriesId, fiscalYear, periodNo, sequenceNo,
            number, recheck.SoftCloseOverride, postedAt, currency, reversalDate, chainSeq, headHash, rowHash, ct);
        foreach (var m in mirrorLines) await InsertLineAsync(tx, m, ct);

        await using (var cmd = tx.Command(
            "update core.chain_head set last_seq = @s, last_hash = @h, hash_version = @v, updated_at = now() " +
            "where tenant_id = @t and company_id = @c and chain_type = 'LEDGER' and scope_key = @k"))
        {
            cmd.Parameters.AddWithValue("s", chainSeq);
            cmd.Parameters.AddWithValue("h", rowHash);
            cmd.Parameters.AddWithValue("v", (short)LedgerHash2.Version);
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", companyId);
            cmd.Parameters.AddWithValue("k", ledgerId.ToString("D"));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // The S columns of the original
        await using (var cmd = tx.Command(
            "update core.journal_document set status = 'reversed', reversed_by_document_id = @m " +
            "where tenant_id = @t and company_id = @c and document_id = @d and status = 'posted'"))
        {
            cmd.Parameters.AddWithValue("m", mirrorId);
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", companyId);
            cmd.Parameters.AddWithValue("d", req.DocumentId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        Guid logId = await CommandSupport.WriteLogAsync(tx, tenantId, companyId, ctx, CommandName, fingerprint, "DOCUMENT", mirrorId, ct);

        string rowHashHex = Canonical.FormatHex32(rowHash);
        await CommandSupport.WriteOutboxAsync(tx, tenantId, companyId, ctx, "ledger.document_reversed", "DOCUMENT",
            $"{companyId:D}/{req.DocumentId:D}", "REVERSED", postedAt, isAudit: true,
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["chain_type"] = "LEDGER", ["scope"] = ledgerId.ToString("D"), ["chain_seq"] = chainSeq, ["row_hash"] = rowHashHex
            }),
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["document_type"] = MirrorType, ["posting_date"] = reversalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["period"] = periodNo, ["ledger_ids"] = new[] { ledgerId.ToString("D") }, ["ledger_seq"] = chainSeq
            }), ct);

        await tx.CommitAsync(ct);   // runs the deferred checks: balance, lines present, chain link and head, outbox position

        var lines = mirrorLines.Select(m => new PostedLine(
            (int)m["line_no"]!, null, (Guid)m["account_id"]!, (string)m["line_type"]!,
            (decimal)m["amount_tc"]!, (decimal)m["amount_lc"]!, (string)m["currency_lc"]!, (decimal)m["rate_lc"]!,
            (decimal)m["amount_gc"]!, (string)m["currency_gc"]!, (decimal)m["rate_gc"]!,
            (decimal?)m["amount_rc"], (string?)m["currency_rc"], (decimal?)m["rate_rc"], (string)m["rate_type"]!, (DateOnly)m["rate_date"]!)).ToList();
        var posted = new PostedDocument(mirrorId, companyId, ledgerId, MirrorType, number, sequenceNo, fiscalYear, periodNo, reversalDate,
            reversalDate, currency, "posted", recheck.SoftCloseOverride, chainSeq, rowHashHex, Canonical.FormatHex32(headHash), LedgerHash2.Version, lines);
        warnings.AddRange(recheck.Warnings.Where(w => warnings.All(x => x.Code != w.Code)));
        return CommandResult<PostedDocument>.Ok(posted, logId, warnings);
    }

    private static async Task InsertDocumentAsync(
        TenantTransaction tx, Guid tenantId, Guid companyId, CommandContext ctx, ReverseDocumentRequest req, Guid mirrorId, Guid ledgerId,
        Guid seriesId, int fiscalYear, int period, long sequenceNo, string number, bool softOverride, DateTime postedAt, string currency,
        DateOnly reversalDate, long chainSeq, byte[] prevHash, byte[] rowHash, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "insert into core.journal_document (tenant_id, company_id, document_id, ledger_id, document_type, is_system_generated, " +
            "number_series_id, fiscal_year, sequence_no, document_number, fiscal_period, soft_close_override, document_date, posting_date, " +
            "posted_at, transaction_currency, reversal_of_document_id, reversal_reason_code, origin, source_system, idempotency_key, " +
            "command_name, posted_by, correlation_id, chain_seq, prev_hash, row_hash, hash_version) " +
            "values (@t, @c, @id, @l, @dt, false, @s, @fy, @seq, @num, @fp, @soft, @dd, @pd, @at, @tc, @orig, @reason, 'EXTERNAL', @src, @key, " +
            "@cmd, @by, @corr, @cs, @prev, @row, @hv)");
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", companyId);
        cmd.Parameters.AddWithValue("id", mirrorId);
        cmd.Parameters.AddWithValue("l", ledgerId);
        cmd.Parameters.AddWithValue("dt", MirrorType);
        cmd.Parameters.AddWithValue("s", seriesId);
        cmd.Parameters.AddWithValue("fy", fiscalYear);
        cmd.Parameters.AddWithValue("seq", sequenceNo);
        cmd.Parameters.AddWithValue("num", number);
        cmd.Parameters.AddWithValue("fp", period);
        cmd.Parameters.AddWithValue("soft", softOverride);
        cmd.Parameters.AddWithValue("dd", reversalDate);
        cmd.Parameters.AddWithValue("pd", reversalDate);
        cmd.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = postedAt });
        cmd.Parameters.AddWithValue("tc", currency);
        cmd.Parameters.AddWithValue("orig", req.DocumentId);
        cmd.Parameters.AddWithValue("reason", req.ReasonCode);
        cmd.Parameters.AddWithValue("src", ctx.SourceSystem);
        cmd.Parameters.AddWithValue("key", ctx.IdempotencyKey);
        cmd.Parameters.AddWithValue("cmd", CommandName);
        cmd.Parameters.AddWithValue("by", ctx.Actor);
        cmd.Parameters.AddWithValue("corr", ctx.CorrelationId);
        cmd.Parameters.AddWithValue("cs", chainSeq);
        cmd.Parameters.AddWithValue("prev", prevHash);
        cmd.Parameters.AddWithValue("row", rowHash);
        cmd.Parameters.AddWithValue("hv", (short)LedgerHash2.Version);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertLineAsync(TenantTransaction tx, Dictionary<string, object?> m, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "insert into core.journal_line (tenant_id, company_id, document_id, line_no, ledger_id, line_type, account_id, posting_date, " +
            "fiscal_year, fiscal_period, reversed_line_no, tax_code_id, amount_tc, amount_lc, currency_lc, rate_lc, rate_lc_table, " +
            "amount_gc, currency_gc, rate_gc, rate_gc_table, amount_rc, currency_rc, rate_rc, rate_rc_table, rate_type, rate_date, " +
            "ic_counterpart_company_id) " +
            "values (@t, @c, @d, @n, @l, @lt, @a, @pd, @fy, @fp, @rev, @tax, @tc, @lc, @clc, @rlc, @rlct, @gc, @cgc, @rgc, @rgct, @rc, @crc, @rrc, @rrct, " +
            "@rt, @rd, @ic)");
        void Add(string name, string key, NpgsqlDbType type) =>
            cmd.Parameters.Add(new NpgsqlParameter(name, type) { Value = m[key] ?? DBNull.Value });
        Add("t", "tenant_id", NpgsqlDbType.Uuid); Add("c", "company_id", NpgsqlDbType.Uuid); Add("d", "document_id", NpgsqlDbType.Uuid);
        Add("n", "line_no", NpgsqlDbType.Integer); Add("l", "ledger_id", NpgsqlDbType.Uuid); Add("lt", "line_type", NpgsqlDbType.Text);
        Add("a", "account_id", NpgsqlDbType.Uuid); Add("pd", "posting_date", NpgsqlDbType.Date); Add("fy", "fiscal_year", NpgsqlDbType.Integer);
        Add("fp", "fiscal_period", NpgsqlDbType.Integer); Add("rev", "reversed_line_no", NpgsqlDbType.Integer);
        Add("tax", "tax_code_id", NpgsqlDbType.Uuid); Add("tc", "amount_tc", NpgsqlDbType.Numeric); Add("lc", "amount_lc", NpgsqlDbType.Numeric);
        Add("clc", "currency_lc", NpgsqlDbType.Text); Add("rlc", "rate_lc", NpgsqlDbType.Numeric); Add("rlct", "rate_lc_table", NpgsqlDbType.Numeric);
        Add("gc", "amount_gc", NpgsqlDbType.Numeric); Add("cgc", "currency_gc", NpgsqlDbType.Text); Add("rgc", "rate_gc", NpgsqlDbType.Numeric);
        Add("rgct", "rate_gc_table", NpgsqlDbType.Numeric); Add("rc", "amount_rc", NpgsqlDbType.Numeric); Add("crc", "currency_rc", NpgsqlDbType.Text);
        Add("rrc", "rate_rc", NpgsqlDbType.Numeric); Add("rrct", "rate_rc_table", NpgsqlDbType.Numeric);
        Add("rt", "rate_type", NpgsqlDbType.Text); Add("rd", "rate_date", NpgsqlDbType.Date); Add("ic", "ic_counterpart_company_id", NpgsqlDbType.Uuid);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static CommandResult<PostedDocument> Reject(CommandError error) => CommandResult<PostedDocument>.Rejected([error]);
}
