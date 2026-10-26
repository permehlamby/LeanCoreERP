using System.Globalization;
using System.Text.Json;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Hashing;
using FinanceCore.Core.Posting;
using Npgsql;
using NpgsqlTypes;

namespace FinanceCore.Data;

/// <summary>
/// clearings:reset (06, 7.1, 03, 5.4): a new clearing of type RESET with the mirrored items of the clearing C, opposite in sign. The
/// original is never changed. A clearing without a difference document posts nothing. If C has a difference document D, the reset also
/// posts D' of type CLEARING_RESET, a mirror of D (opposite lines, reversed_line_no, reversal_of_document_id = D, the reason code), and
/// D gets status reversed. The items follow 03, 5.4 (B2): none on D's offset line, which stays CLEARED by C, one on the offset line of D',
/// which the reset clears as a whole, and the mirror of every other item of C.
/// Locks (03, 5.6): when C has a difference document the number counter of the series CLEARING_RESET and the LEDGER head first (a fact
/// of C known before the locks, so there is no restart), then the CLEARING head, the period row in share mode, the open items of C sorted
/// by key, the row of D, outbox_head last.
/// </summary>
public sealed partial class ClearingPoster
{
    public const string ResetPermission = "clearing.reset";
    public const string ResetDifferenceType = "CLEARING_RESET";

    public async Task<CommandResult<PostedClearing>> ResetAsync(
        Guid tenantId, CommandContext ctx, ResetClearingRequest req, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(ResetPermission))
            return Reject(JournalDocumentPoster.Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", ResetPermission)));

        var shape = new List<CommandError>();
        void Invalid(string path, string reason) => shape.Add(JournalDocumentPoster.Error("SCHEMA.INVALID_VALUE", "P1", path, ("reason", reason)));
        if (string.IsNullOrWhiteSpace(ctx.IdempotencyKey) || ctx.IdempotencyKey.Length > 100) Invalid("idempotency_key", "required, at most 100 characters");
        if (string.IsNullOrWhiteSpace(ctx.CorrelationId) || ctx.CorrelationId.Length > 100) Invalid("correlation_id", "required, at most 100 characters");
        if (string.IsNullOrWhiteSpace(ctx.Actor) || ctx.Actor.Length > 100) Invalid("actor", "required, at most 100 characters");
        if (string.IsNullOrWhiteSpace(ctx.SourceSystem) || ctx.SourceSystem.Length > 50) Invalid("source_system", "required, at most 50 characters");
        if (req.CompanyId == Guid.Empty) Invalid("company_id", "required");
        if (req.ClearingId == Guid.Empty) Invalid("clearing_id", "required");
        if (req.ReasonText is { } t && (t.Length == 0 || t.Length > 200)) Invalid("reason_text", "1 to 200 characters, or absent");
        if (shape.Count > 0) return CommandResult<PostedClearing>.Rejected(shape);
        if (string.IsNullOrWhiteSpace(req.ReasonCode))
            return Reject(JournalDocumentPoster.Error("REVERSAL.REASON_REQUIRED", "P1", "reason_code"));
        if (!ClearingRules.ExternalResetReasons.Contains(req.ReasonCode))
            return Reject(JournalDocumentPoster.Error("REVERSAL.REASON_NOT_ALLOWED", "P1", "reason_code", ("allowed_values", ClearingRules.ExternalResetReasons)));

        byte[] fingerprint = ClearingRules.Fingerprint(req);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await ResetOnceAsync(tenantId, ctx, req, fingerprint, ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.TableName == "command_log" && attempt == 0)
            {
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                return CommandResult<PostedClearing>.Failed(new CommandError("SYSTEM.LOCK_TIMEOUT", "P7", "", null, true));
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure)
            {
                return CommandResult<PostedClearing>.Failed(new CommandError("SYSTEM.CONFLICT", "P7", "", null, true));
            }
        }
    }

    private sealed record Original(Guid AccountId, Guid? PartnerId, string Type, string Method, DateOnly Date, Guid? DocumentId);
    private sealed record OriginalItem(int ItemNo, Guid DocumentId, int LineNo, string CurrencyTc, decimal Tc, decimal Lc, decimal Gc, decimal? Rc);

    private async Task<CommandResult<PostedClearing>> ResetOnceAsync(
        Guid tenantId, CommandContext ctx, ResetClearingRequest req, byte[] fingerprint, CancellationToken ct)
    {
        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Clearing, ct: ct);

        CommandSupport.EarlierCommand? earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
        if (earlier is not null)
        {
            if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint))
                return Reject(JournalDocumentPoster.Error("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
            PostedClearing? replay = earlier.EntityId is null ? null : await LoadAsync(tx, tenantId, req.CompanyId, earlier.EntityId.Value, ct);
            return replay is null
                ? CommandResult<PostedClearing>.Failed(JournalDocumentPoster.Error("SYSTEM.REPLAY_RESULT_MISSING", "P1", ""))
                : CommandResult<PostedClearing>.Ok(replay, earlier.CommandLogId, replay: true);
        }

        // P2: the clearing to reset. A clearing is append-only, so reading it without a lock is safe.
        var errors = new List<CommandError>();
        JournalDocumentPoster.CompanyRow? company = await JournalDocumentPoster.ReadCompanyAsync(tx, tenantId, req.CompanyId, ct);
        if (company is null) return Reject(JournalDocumentPoster.Error("COMPANY.NOT_FOUND", "P2", "company_id"));
        if (company.Status != "ACTIVE") errors.Add(JournalDocumentPoster.Error("COMPANY.NOT_ACTIVE", "P2", "company_id", ("status", company.Status)));
        Original? c = await ReadOriginalAsync(tx, tenantId, req.CompanyId, req.ClearingId, ct);
        if (c is null) return Reject(JournalDocumentPoster.Error("CLEARING.NOT_FOUND", "P2", "clearing_id"));
        if (c.Type != "CLEAR")
            errors.Add(JournalDocumentPoster.Error("CLEARING.NOT_RESETTABLE", "P2", "clearing_id", ("reason", "a reset is not reset: undo it by a new clearing")));
        else if (c.Method == "REVERSAL")
            errors.Add(JournalDocumentPoster.Error("CLEARING.NOT_RESETTABLE", "P2", "clearing_id", ("reason", "the originals of a reversal stay reversed (03, 5.7)")));
        if (req.ClearingDate < c.Date)
            errors.Add(JournalDocumentPoster.Error("SCHEMA.INVALID_VALUE", "P1", "clearing_date", ("reason", "a reset has its own, later date: not before the clearing")));
        if (errors.Count > 0) return CommandResult<PostedClearing>.Rejected(errors);
        List<OriginalItem> original = await ReadOriginalItemsAsync(tx, tenantId, req.CompanyId, req.ClearingId, ct);

        // The pre-lock: a clearing with a difference document always needs the document of its reset (03, 5.6)
        bool hasDocument = c.DocumentId is not null;
        Guid? ledgerId = null;
        JournalDocumentPoster.SeriesRow? series = null;
        long lastNumber = 0, ledgerSeq = 0; byte[] ledgerHash = [];
        if (hasDocument)
        {
            JournalDocumentPoster.PeriodCheck early = await JournalDocumentPoster.CheckPeriodAsync(tx, tenantId, req.CompanyId, req.ClearingDate, ctx, false, ct);
            errors.AddRange(early.Errors.Select(e => e with { Path = "clearing_date" }));
            ledgerId = await ReadDefaultLedgerAsync(tx, tenantId, req.CompanyId, ct);
            if (ledgerId is null) errors.Add(JournalDocumentPoster.Error("LEDGER.NOT_FOUND", "P2", "company_id"));
            if (errors.Count > 0) return CommandResult<PostedClearing>.Rejected(errors);
            series = await JournalDocumentPoster.ReadSeriesAsync(tx, tenantId, req.CompanyId, ResetDifferenceType, req.ClearingDate, ledgerId!.Value, ct);
            if (series is null)
                return Reject(JournalDocumentPoster.Error("SERIES.NO_ASSIGNMENT", "P2", "clearing_date",
                    ("document_type", ResetDifferenceType), ("ledger_id", ledgerId), ("posting_date", req.ClearingDate)));
            long? last = await LockCounterAsync(tx, tenantId, req.CompanyId, series.SeriesId, early.FiscalYear!.Value, ct);
            if (last is null)
                return Reject(JournalDocumentPoster.Error("LIMIT.NUMBER_COUNTER_MISSING", "P7", "clearing_date", ("series_code", series.SeriesCode), ("fiscal_year", early.FiscalYear)));
            lastNumber = last.Value;
            (ledgerSeq, ledgerHash) = await LockLedgerHeadAsync(tx, tenantId, req.CompanyId, ledgerId.Value, ct);
        }

        (long headSeq, byte[] headHash, _) = await CommandSupport.LockOrCreateHeadAsync(
            tx, tenantId, req.CompanyId, ClearingHash2.ChainType, "", LedgerHash2.Genesis(tenantId, req.CompanyId, ClearingHash2.ChainType, ""), ct);

        // after the head lock the clearings of the company are serialised, so "reset at most once" can be read without a race
        if (await ExistsResetAsync(tx, tenantId, req.CompanyId, req.ClearingId, ct))
            return Reject(JournalDocumentPoster.Error("CLEARING.ALREADY_RESET", "P2", "clearing_id"));

        JournalDocumentPoster.PeriodCheck period = await JournalDocumentPoster.CheckPeriodAsync(tx, tenantId, req.CompanyId, req.ClearingDate, ctx, true, ct);
        errors.AddRange(period.Errors.Select(e => e with { Path = "clearing_date" }));
        if (errors.Count > 0) return CommandResult<PostedClearing>.Rejected(errors);

        // The items of the reset: the mirror of every item of C except the one on D's offset line (B2)
        List<OriginalItem> mirrored = original.Where(i => i.DocumentId != c.DocumentId).ToList();
        Dictionary<(Guid, int), Locked> locked = await ReadItemsAsync(
            tx, tenantId, mirrored.Select(i => new ClearingItemRequest(i.DocumentId, i.LineNo)).ToList(), forUpdate: true, ct);
        foreach (OriginalItem i in mirrored)
        {
            if (!locked.TryGetValue((i.DocumentId, i.LineNo), out Locked? row))
            {
                errors.Add(JournalDocumentPoster.Error("SYSTEM.CONFLICT", "P7", $"items[{i.ItemNo}]", ("reason", "the open item row is missing")));
                continue;
            }
            // not allowed if it would leave an item with a negative cleared amount (03, 5.4)
            decimal tc = row.ClearedTc - i.Tc, lc = row.ClearedLc - i.Lc;
            if (tc < Math.Min(0m, row.LineTc) || tc > Math.Max(0m, row.LineTc) || lc < Math.Min(0m, row.LineLc) || lc > Math.Max(0m, row.LineLc))
                errors.Add(JournalDocumentPoster.Error("CLEARING.NOT_RESETTABLE", "P6", $"items[{i.ItemNo}]",
                    ("reason", "the reset would leave the open item with a negative cleared amount"), ("document_id", i.DocumentId), ("line_no", i.LineNo)));
        }
        if (errors.Count > 0) return CommandResult<PostedClearing>.Rejected(errors);

        Guid clearingId = Guid.NewGuid();
        DateTime now = DateTime.UtcNow;
        DateTime clearedAt = new(now.Ticks - now.Ticks % 10, DateTimeKind.Utc);
        var synthetic = new PostClearingRequest(req.CompanyId, c.AccountId, c.PartnerId, req.ClearingDate, "MANUAL", null, req.ReasonText, []);

        // D', the mirror of the difference document, posted before the clearing that points at it
        Guid? mirrorId = null; string? mirrorNumber = null; byte[]? mirrorHash = null;
        (int LineNo, string Currency, decimal Lc, decimal Gc, decimal? Rc)? ownOffset = null;
        Guid? originalDocument = c.DocumentId;
        if (hasDocument)
        {
            var built = await PostMirrorAsync(tx, tenantId, ctx, req, synthetic, c.DocumentId!.Value, ledgerId!.Value, series!, lastNumber, ledgerSeq, ledgerHash, period, clearedAt, ct);
            if (built.Errors.Count > 0) return CommandResult<PostedClearing>.Rejected(built.Errors);
            mirrorId = built.DocumentId; mirrorNumber = built.Number; mirrorHash = built.RowHash; ownOffset = built.Offset;
        }

        // The node of the clearing chain
        long seq = headSeq + 1;
        var body = new Dictionary<string, object?>
        {
            ["clearing_id"] = clearingId, ["clearing_type"] = "RESET", ["clearing_date"] = req.ClearingDate,
            ["fiscal_year"] = period.FiscalYear!.Value, ["fiscal_period"] = period.PeriodNo!.Value, ["cleared_at"] = clearedAt,
            ["account_id"] = c.AccountId, ["partner_id"] = c.PartnerId, ["document_id"] = mirrorId, ["reset_of_clearing_id"] = req.ClearingId,
            ["clearing_method"] = "MANUAL", ["matching_ref"] = null, ["source_system"] = ctx.SourceSystem,
            ["idempotency_key"] = ctx.IdempotencyKey, ["command_name"] = ClearingRules.ResetCommand, ["cleared_by"] = ctx.Actor,
            ["correlation_id"] = ctx.CorrelationId
        };
        var itemRows = mirrored.Select((i, n) => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["tenant_id"] = tenantId, ["company_id"] = req.CompanyId, ["clearing_id"] = clearingId, ["item_no"] = n + 1,
            ["clearing_date"] = req.ClearingDate, ["open_item_document_id"] = i.DocumentId, ["open_item_line_no"] = i.LineNo,
            ["currency_tc"] = i.CurrencyTc, ["cleared_amount_tc"] = -i.Tc, ["cleared_amount_lc"] = -i.Lc,
            ["cleared_amount_gc"] = -i.Gc, ["cleared_amount_rc"] = i.Rc is null ? null : -i.Rc
        }).ToList();
        if (ownOffset is { } o)
            itemRows.Add(new Dictionary<string, object?>
            {
                ["tenant_id"] = tenantId, ["company_id"] = req.CompanyId, ["clearing_id"] = clearingId, ["item_no"] = itemRows.Count + 1,
                ["clearing_date"] = req.ClearingDate, ["open_item_document_id"] = mirrorId, ["open_item_line_no"] = o.LineNo,
                ["currency_tc"] = o.Currency, ["cleared_amount_tc"] = 0m, ["cleared_amount_lc"] = o.Lc, ["cleared_amount_gc"] = o.Gc,
                ["cleared_amount_rc"] = o.Rc
            });
        byte[] rowHash = ClearingHash2.RowHash(tenantId, req.CompanyId, seq, headHash, body, ClearingHash2.ItemsDigest(itemRows), mirrorHash);

        await InsertClearingAsync(tx, tenantId, synthetic, ctx, clearingId, mirrorId, seq, period.FiscalYear!.Value, period.PeriodNo!.Value, clearedAt,
            headHash, rowHash, "RESET", req.ClearingId, ClearingRules.ResetCommand, ct);
        var cleared = new List<ClearedItem>();
        for (int n = 0; n < mirrored.Count; n++)
        {
            OriginalItem i = mirrored[n];
            Locked row = locked[(i.DocumentId, i.LineNo)];
            decimal tc = -i.Tc, lc = -i.Lc, gc = -i.Gc; decimal? rc = i.Rc is null ? null : -i.Rc;
            await InsertItemAsync(tx, tenantId, synthetic, clearingId, n + 1, i.DocumentId, i.LineNo, i.CurrencyTc, tc, lc, gc, rc, ct);
            decimal newTc = row.ClearedTc + tc, newLc = row.ClearedLc + lc, newGc = row.ClearedGc + gc;
            decimal? newRc = row.LineRc is null ? null : (row.ClearedRc ?? 0m) + (rc ?? 0m);
            string status = ClearingRules.StatusOf(new OpenLine(row.LineTc, row.LineLc, row.LineGc, row.LineRc, 0m, 0m, 0m, null), newTc, newLc, newGc, newRc);
            await UpdateOpenItemAsync(tx, tenantId, row.CompanyId, i.DocumentId, i.LineNo, clearingId, status, newTc, newLc, newGc, newRc, ct);
            cleared.Add(new ClearedItem(n + 1, i.DocumentId, i.LineNo, i.CurrencyTc, tc, lc, gc, rc, status));
        }
        int mirroredCount = cleared.Count;
        if (ownOffset is { } off)
        {
            // the offset line of D' is cleared as a whole by this reset, like any zero-TC offset line (5.2)
            int no = mirroredCount + 1;
            await InsertItemAsync(tx, tenantId, synthetic, clearingId, no, mirrorId!.Value, off.LineNo, off.Currency, 0m, off.Lc, off.Gc, off.Rc, ct);
            await UpdateOpenItemAsync(tx, tenantId, req.CompanyId, mirrorId.Value, off.LineNo, clearingId, "CLEARED", 0m, off.Lc, off.Gc, off.Rc, ct);
            cleared.Add(new ClearedItem(no, mirrorId.Value, off.LineNo, off.Currency, 0m, off.Lc, off.Gc, off.Rc, "CLEARED"));
        }

        await MoveHeadAsync(tx, tenantId, req.CompanyId, "CLEARING", "", seq, rowHash, ClearingHash2.Version, ct);
        Guid logId = await CommandSupport.WriteLogAsync(tx, tenantId, req.CompanyId, ctx, ClearingRules.ResetCommand, fingerprint, "CLEARING", clearingId, ct);

        // Events (15, 6): the mirror of the difference document as the reversal of D, the reset with its chain reference only, every reopened item
        DateTime at = clearedAt;
        if (mirrorId is not null)
            await CommandSupport.WriteOutboxAsync(tx, tenantId, req.CompanyId, ctx, "ledger.document_reversed", "DOCUMENT", $"{req.CompanyId:D}/{originalDocument:D}", "REVERSED",
                at, isAudit: true,
                JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["chain_type"] = "LEDGER", ["scope"] = ledgerId!.Value.ToString("D"), ["chain_seq"] = ledgerSeq + 1,
                    ["row_hash"] = Canonical.FormatHex32(mirrorHash!)
                }),
                JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["document_type"] = ResetDifferenceType, ["posting_date"] = req.ClearingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["period"] = period.PeriodNo, ["ledger_ids"] = new[] { ledgerId!.Value.ToString("D") }, ["ledger_seq"] = ledgerSeq + 1
                }), ct);
        string chainRef = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["chain_type"] = "CLEARING", ["scope"] = "", ["chain_seq"] = seq, ["row_hash"] = Canonical.FormatHex32(rowHash)
        });
        await CommandSupport.WriteOutboxAsync(tx, tenantId, req.CompanyId, ctx, "clearing.reset", "CLEARING", $"{req.CompanyId:D}/{clearingId:D}", "RESET",
            at, isAudit: true, chainRef, JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["clearing_id"] = clearingId, ["reset_of_clearing_id"] = req.ClearingId, ["partner_id"] = c.PartnerId, ["account_id"] = c.AccountId,
                ["items"] = cleared.Count
            }), ct);
        foreach (ClearedItem item in cleared.Take(mirroredCount))
            await CommandSupport.WriteOutboxAsync(tx, tenantId, req.CompanyId, ctx, "open_item.reset", "OPEN_ITEM",
                $"{req.CompanyId:D}/{item.DocumentId:D}/{item.LineNo}", "RESET", at, isAudit: false, null,
                JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["account_id"] = c.AccountId, ["partner_id"] = c.PartnerId, ["due_date"] = null,
                    ["amount_tc"] = item.AmountTc.ToString("F4", CultureInfo.InvariantCulture), ["currency"] = item.CurrencyTc,
                    ["clearing_id"] = clearingId, ["status"] = item.StatusAfter
                }), ct);

        await tx.CommitAsync(ct);
        return CommandResult<PostedClearing>.Ok(
            new PostedClearing(clearingId, req.CompanyId, seq, Canonical.FormatHex32(rowHash), Canonical.FormatHex32(headHash), req.ClearingDate,
                period.FiscalYear!.Value, period.PeriodNo!.Value, "MANUAL", cleared, mirrorId, mirrorNumber, req.ClearingId),
            logId, period.Warnings);
    }

    // ---------------------------------------------------------------------------------------------------------
    private sealed record MirrorBuilt(List<CommandError> Errors, Guid DocumentId, string Number, byte[] RowHash, (int LineNo, string Currency, decimal Lc, decimal Gc, decimal? Rc)? Offset);

    /// <summary>
    /// Posts D', the mirror of the difference document D (03, 5.4): every line of D with the opposite sign, the same accounts, rates and
    /// currencies (no recomputation, 02, Q8), reversed_line_no set, own number from the series CLEARING_RESET, own node in the LEDGER chain.
    /// The mirror of the offset line gets its open item row, which the reset clears as a whole. D gets status reversed.
    /// </summary>
    private static async Task<MirrorBuilt> PostMirrorAsync(
        TenantTransaction tx, Guid tenantId, CommandContext ctx, ResetClearingRequest req, PostClearingRequest synthetic, Guid originalId, Guid ledgerId,
        JournalDocumentPoster.SeriesRow series, long lastNumber, long ledgerSeq, byte[] ledgerHash, JournalDocumentPoster.PeriodCheck period, DateTime postedAt,
        CancellationToken ct)
    {
        var errors = new List<CommandError>();
        LedgerReader.StoredDocument? d = (await LedgerReader.ReadDocumentsAsync(tx, tenantId, originalId, null, null, ct)).FirstOrDefault();
        if (d is null || (string)d.Header["document_type"]! != DifferenceType)
            return new MirrorBuilt([JournalDocumentPoster.Error("SYSTEM.CONFLICT", "P7", "clearing_id", ("reason", "the difference document of the clearing is missing"))], Guid.Empty, "", [], null);
        if (d.Status != "posted")
            return new MirrorBuilt([JournalDocumentPoster.Error("CLEARING.NOT_RESETTABLE", "P2", "clearing_id", ("reason", "the difference document is already reversed"))], Guid.Empty, "", [], null);
        // the row of D is locked: its status changes below
        await using (var cmd = tx.Command("select 1 from core.journal_document where tenant_id = @t and company_id = @c and document_id = @d for update"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("d", originalId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        int fiscalYear = period.FiscalYear!.Value, periodNo = period.PeriodNo!.Value;
        long sequenceNo = lastNumber + 1;
        await using (var cmd = tx.Command(
            "update core.number_range_counter set last_number = @n, updated_at = now() where tenant_id = @t and company_id = @c and series_id = @s and fiscal_year = @y"))
        {
            cmd.Parameters.AddWithValue("n", sequenceNo); cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId);
            cmd.Parameters.AddWithValue("s", series.SeriesId); cmd.Parameters.AddWithValue("y", fiscalYear);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        string number = PostingRules.FormatNumber(series.Prefix, series.IncludeFiscalYear, series.Separator, series.NumberLength, fiscalYear, sequenceNo);
        string currency = (string)d.Header["transaction_currency"]!;
        Guid mirrorId = Guid.NewGuid();
        long chainSeq = ledgerSeq + 1;

        var header = new Dictionary<string, object?>
        {
            ["tenant_id"] = tenantId, ["company_id"] = req.CompanyId, ["document_id"] = mirrorId, ["ledger_id"] = ledgerId,
            ["document_type"] = ResetDifferenceType, ["is_system_generated"] = true, ["number_series_id"] = series.SeriesId,
            ["fiscal_year"] = fiscalYear, ["sequence_no"] = sequenceNo, ["document_number"] = number, ["fiscal_period"] = periodNo,
            ["soft_close_override"] = period.SoftCloseOverride, ["migration_batch_id"] = null, ["detail_digest"] = null,
            ["document_date"] = req.ClearingDate, ["posting_date"] = req.ClearingDate, ["posted_at"] = postedAt, ["transaction_currency"] = currency,
            ["reversal_of_document_id"] = originalId, ["reversal_reason_code"] = req.ReasonCode, ["auto_reversal_date"] = null,
            ["ic_transaction_id"] = null, ["ic_counterpart_company_id"] = null, ["origin"] = "SYSTEM",
            ["source_system"] = ctx.SourceSystem, ["source_document_ref"] = null, ["idempotency_key"] = ctx.IdempotencyKey,
            ["command_name"] = ClearingRules.ResetCommand, ["posted_by"] = ctx.Actor, ["correlation_id"] = ctx.CorrelationId,
            ["hash_version"] = LedgerHash2.Version
        };

        var lines = new List<(Dictionary<string, object?> Row, int Original, bool OpenItem)>();
        var lineHashes = new List<byte[]>();
        foreach (var o in d.Lines.OrderBy(l => (int)l["line_no"]!))
        {
            decimal? Neg(string key) => o[key] is decimal x ? -x : null;
            int lineNo = (int)o["line_no"]!;
            var m = new Dictionary<string, object?>
            {
                ["tenant_id"] = tenantId, ["company_id"] = req.CompanyId, ["document_id"] = mirrorId, ["line_no"] = lineNo,
                ["ledger_id"] = ledgerId, ["line_type"] = o["line_type"], ["account_id"] = o["account_id"],
                ["posting_date"] = req.ClearingDate, ["fiscal_year"] = fiscalYear, ["fiscal_period"] = periodNo,
                ["reversed_line_no"] = lineNo, ["tax_code_id"] = o["tax_code_id"],
                ["amount_tc"] = Neg("amount_tc"), ["amount_lc"] = Neg("amount_lc"), ["currency_lc"] = o["currency_lc"],
                ["rate_lc"] = o["rate_lc"], ["rate_lc_table"] = o["rate_lc_table"],
                ["amount_gc"] = Neg("amount_gc"), ["currency_gc"] = o["currency_gc"], ["rate_gc"] = o["rate_gc"], ["rate_gc_table"] = o["rate_gc_table"],
                ["amount_rc"] = Neg("amount_rc"), ["currency_rc"] = o["currency_rc"], ["rate_rc"] = o["rate_rc"], ["rate_rc_table"] = o["rate_rc_table"],
                ["rate_type"] = o["rate_type"], ["rate_date"] = o["rate_date"], ["ic_counterpart_company_id"] = o["ic_counterpart_company_id"]
            };
            bool open = d.OpenItems.ContainsKey(lineNo);
            lines.Add((m, lineNo, open));
            lineHashes.Add(LedgerHash2.LineHash(new HashedLine(m, OpenItem: open
                ? new Dictionary<string, object?>
                {
                    ["tenant_id"] = tenantId, ["company_id"] = req.CompanyId, ["document_id"] = mirrorId, ["line_no"] = lineNo,
                    ["partner_id"] = d.OpenItems[lineNo]["partner_id"], ["legacy_document_date"] = null, ["legacy_posting_date"] = null,
                    ["legacy_document_ref"] = null
                }
                : null)));
        }
        byte[] rowHash = LedgerHash2.RowHash(header, chainSeq, ledgerHash, LedgerHash2.LineDigest(lineHashes));

        await InsertDocumentAsync(tx, tenantId, synthetic, ctx, mirrorId, ledgerId, series, fiscalYear, periodNo, sequenceNo, number,
            period.SoftCloseOverride, postedAt, chainSeq, ledgerHash, rowHash, currency, ResetDifferenceType, originalId, req.ReasonCode, ClearingRules.ResetCommand, ct);
        (int, string, decimal, decimal, decimal?)? offset = null;
        foreach (var (m, originalLine, open) in lines)
        {
            var pl = new PostedLine((int)m["line_no"]!, null, (Guid)m["account_id"]!, (string)m["line_type"]!,
                (decimal)m["amount_tc"]!, (decimal)m["amount_lc"]!, (string)m["currency_lc"]!, (decimal)m["rate_lc"]!,
                (decimal)m["amount_gc"]!, (string)m["currency_gc"]!, (decimal)m["rate_gc"]!,
                (decimal?)m["amount_rc"], (string?)m["currency_rc"], (decimal?)m["rate_rc"], (string)m["rate_type"]!, (DateOnly)m["rate_date"]!);
            await InsertLineAsync(tx, tenantId, req.CompanyId, mirrorId, ledgerId, fiscalYear, periodNo, req.ClearingDate, pl, originalLine, ct);
            if (open)
            {
                // starts OPEN with nothing cleared: the reset is inserted next and then updates it to CLEARED
                await using var cmd = tx.Command(
                    "insert into core.journal_line_open_item (tenant_id, company_id, document_id, line_no, partner_id, due_date, cleared_amount_rc) values (@t, @c, @d, @n, @p, @due, @rc)");
                // the mirror of the offset line carries the partner and the due date of D's offset line (mandatory on a control account)
                cmd.Parameters.Add(new NpgsqlParameter("due", NpgsqlDbType.Date) { Value = (object?)d.OpenItemDueDates[originalLine] ?? DBNull.Value });
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId);
                cmd.Parameters.AddWithValue("d", mirrorId); cmd.Parameters.AddWithValue("n", pl.LineNo);
                cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Uuid) { Value = (object?)d.OpenItems[originalLine]["partner_id"] ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("rc", NpgsqlDbType.Numeric) { Value = pl.AmountRc is null ? DBNull.Value : 0m });
                await cmd.ExecuteNonQueryAsync(ct);
                offset = (pl.LineNo, currency, pl.AmountLc, pl.AmountGc, pl.AmountRc);
            }
        }
        await MoveHeadAsync(tx, tenantId, req.CompanyId, "LEDGER", ledgerId.ToString("D"), chainSeq, rowHash, LedgerHash2.Version, ct);
        await using (var cmd = tx.Command(
            "update core.journal_document set status = 'reversed', reversed_by_document_id = @m where tenant_id = @t and company_id = @c and document_id = @d and status = 'posted'"))
        {
            cmd.Parameters.AddWithValue("m", mirrorId); cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("d", originalId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        return new MirrorBuilt(errors, mirrorId, number, rowHash, offset);
    }

    // ---------------------------------------------------------------------------------------------------------
    private static async Task<Original?> ReadOriginalAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid clearingId, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select account_id, partner_id, clearing_type, clearing_method, clearing_date, document_id from core.clearing " +
            "where tenant_id = @t and company_id = @c and clearing_id = @id");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("id", clearingId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new Original(r.GetGuid(0), r.IsDBNull(1) ? null : r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetFieldValue<DateOnly>(4), r.IsDBNull(5) ? null : r.GetGuid(5))
            : null;
    }

    private static async Task<List<OriginalItem>> ReadOriginalItemsAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid clearingId, CancellationToken ct)
    {
        var items = new List<OriginalItem>();
        await using var cmd = tx.Command(
            "select item_no, open_item_document_id, open_item_line_no, currency_tc, cleared_amount_tc, cleared_amount_lc, cleared_amount_gc, cleared_amount_rc " +
            "from core.clearing_item where tenant_id = @t and company_id = @c and clearing_id = @id order by item_no");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("id", clearingId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            items.Add(new OriginalItem(r.GetInt32(0), r.GetGuid(1), r.GetInt32(2), r.GetString(3), r.GetDecimal(4), r.GetDecimal(5), r.GetDecimal(6),
                r.IsDBNull(7) ? null : r.GetDecimal(7)));
        return items;
    }

    private static async Task<bool> ExistsResetAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid clearingId, CancellationToken ct)
    {
        await using var cmd = tx.Command("select 1 from core.clearing where tenant_id = @t and company_id = @c and reset_of_clearing_id = @id");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("id", clearingId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }
}
