using System.Globalization;
using System.Text.Json;
using FinanceCore.Core.Closing;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Hashing;
using FinanceCore.Core.Posting;
using Npgsql;
using NpgsqlTypes;

namespace FinanceCore.Data;

/// <summary>
/// year-end:close and year-end:reopen (05, 5). Close posts one YEAR_END document that zeroes every P&amp;L account and dimension combination of the year against the
/// retained earnings account, into the closing special period, then hard-closes that period and closes the year with their period events and writes the anchor requests.
/// Reopen reopens the closing period and the year and posts the mirror of the closing document, in that order (A4).
/// Locks (04, 6.3): the YEAR_END counter, the LEDGER and CLEARING heads, the PERIOD_EVENT head, the other heads in share mode, then the period rows and the year row exclusively,
/// outbox_head last. Every precondition is checked after the locks.
/// </summary>
public sealed class YearEndCommands(NpgsqlDataSource source)
{
    public const string ClosePermission = "year_end.close";
    public const string ReopenPermission = "year_end.reopen";
    public const string CloseCommand = "year-end:close";
    public const string ReopenCommand = "year-end:reopen";

    private sealed record YearRow(string Status, bool IsHistory, int SpecialCount, DateOnly EndDate);
    private sealed record PeriodRow(int No, string Type, string Status);
    private sealed class EventCursor(long seq, byte[] hash) { public long Seq { get; set; } = seq; public byte[] Hash { get; set; } = hash; }

    private static List<CommandError> ShapeErrors(CommandContext ctx, Guid companyId, int year)
    {
        var shape = new List<CommandError>();
        void Invalid(string path, string reason) => shape.Add(JournalDocumentPoster.Error("SCHEMA.INVALID_VALUE", "P1", path, ("reason", reason)));
        if (string.IsNullOrWhiteSpace(ctx.IdempotencyKey) || ctx.IdempotencyKey.Length > 90) Invalid("idempotency_key", "required, at most 90 characters");
        if (string.IsNullOrWhiteSpace(ctx.CorrelationId) || ctx.CorrelationId.Length > 100) Invalid("correlation_id", "required, at most 100 characters");
        if (string.IsNullOrWhiteSpace(ctx.Actor) || ctx.Actor.Length > 100) Invalid("actor", "required, at most 100 characters");
        if (companyId == Guid.Empty) Invalid("company_id", "required");
        if (year < 1) Invalid("fiscal_year", "required");
        return shape;
    }

    // ---------------------------------------------------------------------------------------------------------
    public Task<CommandResult<YearEndResult>> CloseAsync(Guid tenantId, CommandContext ctx, CloseYearRequest req, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(ClosePermission))
            return Task.FromResult(CommandResult<YearEndResult>.Rejected([JournalDocumentPoster.Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", ClosePermission))]));
        List<CommandError> shape = ShapeErrors(ctx, req.CompanyId, req.FiscalYear);
        if (shape.Count > 0) return Task.FromResult(CommandResult<YearEndResult>.Rejected(shape));
        byte[] fingerprint = ClosingFingerprint.ForClose(req);
        return WithRetryAsync(() => CloseOnceAsync(tenantId, ctx, req, fingerprint, ct), CommandResult<YearEndResult>.Failed);
    }

    public Task<CommandResult<YearReopenResult>> ReopenAsync(Guid tenantId, CommandContext ctx, ReopenYearRequest req, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(ReopenPermission))
            return Task.FromResult(CommandResult<YearReopenResult>.Rejected([JournalDocumentPoster.Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", ReopenPermission))]));
        List<CommandError> shape = ShapeErrors(ctx, req.CompanyId, req.FiscalYear);
        if (string.IsNullOrWhiteSpace(req.ReasonCode) || req.ReasonCode.Length > 50)
            shape.Add(JournalDocumentPoster.Error("SCHEMA.INVALID_VALUE", "P1", "reason_code", ("reason", "required, at most 50 characters")));
        if (string.IsNullOrWhiteSpace(req.ReasonText) || req.ReasonText.Length > 500)
            shape.Add(JournalDocumentPoster.Error("SCHEMA.INVALID_VALUE", "P1", "reason_text", ("reason", "required, at most 500 characters")));
        if (shape.Count > 0) return Task.FromResult(CommandResult<YearReopenResult>.Rejected(shape));
        byte[] fingerprint = ClosingFingerprint.ForReopen(req);
        return WithRetryAsync(() => ReopenOnceAsync(tenantId, ctx, req, fingerprint, ct), CommandResult<YearReopenResult>.Failed);
    }

    private static async Task<CommandResult<T>> WithRetryAsync<T>(Func<Task<CommandResult<T>>> once, Func<CommandError, CommandResult<T>> failed)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await once();
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.TableName == "command_log" && attempt == 0)
            {
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                return failed(new CommandError("SYSTEM.LOCK_TIMEOUT", "P7", "", null, true));
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure)
            {
                return failed(new CommandError("SYSTEM.CONFLICT", "P7", "", null, true));
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    private async Task<CommandResult<YearEndResult>> CloseOnceAsync(Guid tenantId, CommandContext ctx, CloseYearRequest req, byte[] fingerprint, CancellationToken ct)
    {
        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Closing, ct: ct);
        Guid companyId = req.CompanyId; int year = req.FiscalYear;

        CommandSupport.EarlierCommand? earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
        if (earlier is not null)
        {
            if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint))
                return RejectClose(JournalDocumentPoster.Error("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
            (Guid? docId, int closing)? stored = earlier.EntityId is null ? null : await LoadEventAsync(tx, tenantId, earlier.EntityId.Value, ct);
            if (stored is null) return CommandResult<YearEndResult>.Failed(JournalDocumentPoster.Error("SYSTEM.REPLAY_RESULT_MISSING", "P1", ""));
            PostedDocument? replayDoc = stored.Value.docId is null ? null : await LedgerReader.LoadDocumentAsync(tx, tenantId, stored.Value.docId.Value, ct);
            return CommandResult<YearEndResult>.Ok(new YearEndResult(companyId, year, stored.Value.closing, replayDoc, earlier.EntityId!.Value, 0), earlier.CommandLogId, replay: true);
        }

        // P2
        JournalDocumentPoster.CompanyRow? company = await JournalDocumentPoster.ReadCompanyAsync(tx, tenantId, companyId, ct);
        if (company is null || company.Status != "ACTIVE") return RejectClose(JournalDocumentPoster.Error("COMPANY.NOT_ACTIVE", "P2", "company_id", ("status", company?.Status)));
        YearRow? row = await ReadYearAsync(tx, tenantId, companyId, year, false, ct);
        if (row is null) return RejectClose(JournalDocumentPoster.Error("PERIOD.NOT_FOUND", "P2", "fiscal_year", ("fiscal_year", year)));
        Guid? ledgerId = await ClearingPoster.ReadDefaultLedgerAsync(tx, tenantId, companyId, ct);
        if (ledgerId is null) return RejectClose(JournalDocumentPoster.Error("LEDGER.NOT_FOUND", "P2", "company_id"));
        string? groupCurrency = await ClearingPoster.ReadGroupCurrencyAsync(tx, tenantId, companyId, row.EndDate, ct);
        if (groupCurrency is null) return RejectClose(JournalDocumentPoster.Error("CONFIG.GROUP_MEMBERSHIP_MISSING", "P2", "fiscal_year", ("posting_date", row.EndDate)));
        var series = await JournalDocumentPoster.ReadSeriesAsync(tx, tenantId, companyId, "YEAR_END", row.EndDate, ledgerId.Value, ct);

        // P7: counter, LEDGER and CLEARING, PERIOD_EVENT, the other heads in share mode, then the period rows and the year row
        long? counter = series is null ? null : await ClearingPoster.LockCounterAsync(tx, tenantId, companyId, series.SeriesId, year, ct);
        await using (var cmd = tx.Command("select core.lock_chain_heads(@c)")) { cmd.Parameters.AddWithValue("c", companyId); await cmd.ExecuteScalarAsync(ct); }
        ClosingLedger ledger = (await ClosingDocuments.OpenLedgerAsync(tx, tenantId, companyId, ledgerId.Value, company, groupCurrency, ct))!;
        (long eventSeq, byte[] eventHash, _) = await CommandSupport.LockOrCreateHeadAsync(
            tx, tenantId, companyId, PeriodEventHash2.ChainType, "", LedgerHash2.Genesis(tenantId, companyId, PeriodEventHash2.ChainType, ""), ct);
        await using (var cmd = tx.Command("select core.lock_anchor_heads(@c)")) { cmd.Parameters.AddWithValue("c", companyId); await cmd.ExecuteScalarAsync(ct); }
        var periods = await ReadPeriodsAsync(tx, tenantId, companyId, year, ct);
        row = (await ReadYearAsync(tx, tenantId, companyId, year, true, ct))!;

        // The preconditions, after the locks (5.1)
        if (row.IsHistory) return RejectClose(JournalDocumentPoster.Error("PERIOD.HISTORY_YEAR", "P3", "fiscal_year", ("fiscal_year", year)));
        if (row.Status != "OPEN") return RejectClose(JournalDocumentPoster.Error("CLOSE.YEAR_NOT_OPEN", "P3", "fiscal_year", ("fiscal_year", year), ("status", row.Status)));
        var special = periods.Where(p => p.Type == "SPECIAL").OrderBy(p => p.No).ToList();
        if (special.Count == 0) return RejectClose(JournalDocumentPoster.Error("CLOSE.CLOSING_PERIOD_MISSING", "P3", "fiscal_year", ("fiscal_year", year)));
        PeriodRow closing = special[^1];
        PeriodRow? openRegular = periods.Where(p => p.Type == "REGULAR" && p.Status != "HARD_CLOSED").OrderBy(p => p.No).FirstOrDefault();
        if (openRegular is not null)
            return RejectClose(JournalDocumentPoster.Error("CLOSE.PREDECESSOR_NOT_HARD_CLOSED", "P3", "fiscal_year", ("period_no", openRegular.No), ("status", openRegular.Status)));
        if (closing.Status != "OPEN") return RejectClose(JournalDocumentPoster.Error("CLOSE.CLOSING_PERIOD_NOT_OPEN", "P3", "fiscal_year", ("period_no", closing.No), ("status", closing.Status)));
        PeriodRow? notClosed = special.Take(special.Count - 1).FirstOrDefault(p => p.Status != "HARD_CLOSED");
        if (notClosed is not null) return RejectClose(JournalDocumentPoster.Error("CLOSE.SPECIAL_PERIOD_NOT_CLOSED", "P3", "fiscal_year", ("period_no", notClosed.No), ("status", notClosed.Status)));
        if (await RevaluationBasis.StaleCheckAsync(tx, tenantId, companyId, year, closing.No, ct) is { } stale) return RejectClose(stale);

        Guid? retained = await AccountRoleResolver.ResolveAsync(tx, tenantId, companyId, "RETAINED_EARNINGS", null, null, null, ct);
        var reRow = retained is null ? null : (await JournalDocumentPoster.ReadAccountRowsAsync(tx, tenantId, companyId, [retained.Value], ct)).GetValueOrDefault(retained.Value);
        if (reRow is null || !reRow.Active || reRow.PostingBlocked)
            return RejectClose(JournalDocumentPoster.Error("CLOSE.RETAINED_EARNINGS_INVALID", "P3", "company_id", ("account_id", retained)));

        // Step 1 and 2: the groups and the document
        List<ClosingGroup> groups = await ReadGroupsAsync(tx, tenantId, companyId, year, ct);
        IReadOnlyList<ClosingLine> closingLines = YearEndModel.Build(groups, retained!.Value, company.ReportingCurrency is not null);
        PostedDocument? document = null;
        if (closingLines.Count > 0)
        {
            if (series is null)
                return RejectClose(JournalDocumentPoster.Error("SERIES.NO_ASSIGNMENT", "P2", "fiscal_year", ("document_type", "YEAR_END"), ("ledger_id", ledgerId), ("posting_date", row.EndDate)));
            if (counter is null)
                return RejectClose(JournalDocumentPoster.Error("LIMIT.NUMBER_COUNTER_MISSING", "P7", "fiscal_year", ("series_code", series.SeriesCode), ("fiscal_year", year)));
            var newLines = closingLines.Select(c => new NewLine(c.AccountId, "CLOSING", c.Lc, c.Lc, c.Gc, c.Rc, 1m, c.RateGc, c.RateRc, "DERIVED", row.EndDate, null,
                c.CostCenterId, c.ProjectId, c.ProfitCenterId, c.BusinessUnitId, c.FreeDimensions)).ToList();
            var posted = await ClosingDocuments.PostAsync(ledger, ctx,
                new DocumentSpec("YEAR_END", CloseCommand, series, year, closing.No, false, row.EndDate, ctx.IdempotencyKey + "#D"), newLines, null, ct);
            if (posted.Error is not null) return RejectClose(posted.Error);
            document = posted.Document;
        }

        // Step 3: the closing period is hard-closed and the year is closed, each with its event, so that the status trigger finds the matching latest one
        var cursor = new EventCursor(eventSeq, eventHash);
        string key = ctx.IdempotencyKey;
        Guid periodEvent = await AppendEventAsync(tx, tenantId, companyId, ctx, cursor, "PERIOD_HARD_CLOSE", year, closing.No, "OPEN", "HARD_CLOSED", null, null, ClosePermission, null, key + "#E1", ct);
        await using (var cmd = tx.Command(
            "update core.fiscal_period set status = 'HARD_CLOSED', last_event_id = @e where tenant_id = @t and company_id = @c and fiscal_year = @y and period_no = @p"))
        {
            cmd.Parameters.AddWithValue("e", periodEvent); cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId);
            cmd.Parameters.AddWithValue("y", year); cmd.Parameters.AddWithValue("p", closing.No);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        long periodEventSeq = cursor.Seq; byte[] periodEventHash = cursor.Hash;
        Guid yearEvent = await AppendEventAsync(tx, tenantId, companyId, ctx, cursor, "YEAR_END_CLOSE", year, null, "OPEN", "CLOSED", null, null, ClosePermission, document?.DocumentId, key + "#E2", ct);
        await using (var cmd = tx.Command(
            "update core.fiscal_year set status = 'CLOSED', year_end_document_id = @d, last_event_id = @e where tenant_id = @t and company_id = @c and fiscal_year = @y"))
        {
            cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Uuid) { Value = (object?)document?.DocumentId ?? DBNull.Value });
            cmd.Parameters.AddWithValue("e", yearEvent); cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("y", year);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Step 4: the anchor requests of all chains of the company (A2)
        int anchors = await AnchorRequests.WriteAsync(tx, tenantId, companyId, ct);

        Guid logId = await CommandSupport.WriteLogAsync(tx, tenantId, companyId, ctx, CloseCommand, fingerprint, "PERIOD_EVENT", yearEvent, ct);

        // Step 5: the outbox
        if (document is not null) await ClosingDocuments.WriteDocumentEventAsync(ledger, ctx, document, "ledger.document_posted", document.DocumentId, "POSTED", ct);
        await WritePeriodChangedAsync(tx, tenantId, companyId, ctx, year, closing.No, "HARD_CLOSED", periodEventSeq, periodEventHash, ct);
        await CommandSupport.WriteOutboxAsync(tx, tenantId, companyId, ctx, "year.closed", "FISCAL_YEAR", $"{companyId:D}/{year}", "CLOSED", DateTime.UtcNow, isAudit: true,
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["chain_type"] = PeriodEventHash2.ChainType, ["scope"] = "", ["chain_seq"] = cursor.Seq, ["row_hash"] = Canonical.FormatHex32(cursor.Hash)
            }),
            JsonSerializer.Serialize(new Dictionary<string, object?> { ["fiscal_year"] = year, ["document_id"] = document?.DocumentId }), ct);

        await tx.CommitAsync(ct);   // the deferred checks: balance, lines, snapshot, chain and head, outbox positions
        return CommandResult<YearEndResult>.Ok(new YearEndResult(companyId, year, closing.No, document, yearEvent, anchors), logId);
    }

    // ---------------------------------------------------------------------------------------------------------
    private async Task<CommandResult<YearReopenResult>> ReopenOnceAsync(Guid tenantId, CommandContext ctx, ReopenYearRequest req, byte[] fingerprint, CancellationToken ct)
    {
        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Closing, ct: ct);
        Guid companyId = req.CompanyId; int year = req.FiscalYear;

        CommandSupport.EarlierCommand? earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
        if (earlier is not null)
        {
            if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint))
                return RejectReopen(JournalDocumentPoster.Error("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
            (Guid? docId, int closing)? stored = earlier.EntityId is null ? null : await LoadEventAsync(tx, tenantId, earlier.EntityId.Value, ct);
            if (stored is null) return CommandResult<YearReopenResult>.Failed(JournalDocumentPoster.Error("SYSTEM.REPLAY_RESULT_MISSING", "P1", ""));
            Guid? mirrorId = stored.Value.docId is null ? null : await MirrorOfAsync(tx, tenantId, companyId, stored.Value.docId.Value, ct);
            PostedDocument? replayDoc = mirrorId is null ? null : await LedgerReader.LoadDocumentAsync(tx, tenantId, mirrorId.Value, ct);
            return CommandResult<YearReopenResult>.Ok(new YearReopenResult(companyId, year, stored.Value.closing, replayDoc, earlier.EntityId!.Value), earlier.CommandLogId, replay: true);
        }

        JournalDocumentPoster.CompanyRow? company = await JournalDocumentPoster.ReadCompanyAsync(tx, tenantId, companyId, ct);
        if (company is null || company.Status != "ACTIVE") return RejectReopen(JournalDocumentPoster.Error("COMPANY.NOT_ACTIVE", "P2", "company_id", ("status", company?.Status)));
        YearRow? row = await ReadYearAsync(tx, tenantId, companyId, year, false, ct);
        if (row is null) return RejectReopen(JournalDocumentPoster.Error("PERIOD.NOT_FOUND", "P2", "fiscal_year", ("fiscal_year", year)));
        Guid? ledgerId = await ClearingPoster.ReadDefaultLedgerAsync(tx, tenantId, companyId, ct);
        if (ledgerId is null) return RejectReopen(JournalDocumentPoster.Error("LEDGER.NOT_FOUND", "P2", "company_id"));
        string? groupCurrency = await ClearingPoster.ReadGroupCurrencyAsync(tx, tenantId, companyId, row.EndDate, ct);
        if (groupCurrency is null) return RejectReopen(JournalDocumentPoster.Error("CONFIG.GROUP_MEMBERSHIP_MISSING", "P2", "fiscal_year", ("posting_date", row.EndDate)));
        var series = await JournalDocumentPoster.ReadSeriesAsync(tx, tenantId, companyId, "YEAR_END", row.EndDate, ledgerId.Value, ct);

        long? counter = series is null ? null : await ClearingPoster.LockCounterAsync(tx, tenantId, companyId, series.SeriesId, year, ct);
        await using (var cmd = tx.Command("select core.lock_chain_heads(@c)")) { cmd.Parameters.AddWithValue("c", companyId); await cmd.ExecuteScalarAsync(ct); }
        ClosingLedger ledger = (await ClosingDocuments.OpenLedgerAsync(tx, tenantId, companyId, ledgerId.Value, company, groupCurrency, ct))!;
        (long eventSeq, byte[] eventHash, _) = await CommandSupport.LockOrCreateHeadAsync(
            tx, tenantId, companyId, PeriodEventHash2.ChainType, "", LedgerHash2.Genesis(tenantId, companyId, PeriodEventHash2.ChainType, ""), ct);
        await using (var cmd = tx.Command("select core.lock_anchor_heads(@c)")) { cmd.Parameters.AddWithValue("c", companyId); await cmd.ExecuteScalarAsync(ct); }
        var periods = await ReadPeriodsAsync(tx, tenantId, companyId, year, ct);
        row = (await ReadYearAsync(tx, tenantId, companyId, year, true, ct))!;

        // The preconditions, after the locks (5.3)
        if (row.IsHistory) return RejectReopen(JournalDocumentPoster.Error("PERIOD.HISTORY_YEAR_SEALED", "P3", "fiscal_year", ("fiscal_year", year)));
        if (row.Status != "CLOSED") return RejectReopen(JournalDocumentPoster.Error("CLOSE.YEAR_NOT_CLOSED", "P3", "fiscal_year", ("fiscal_year", year), ("status", row.Status)));
        await using (var cmd = tx.Command("select fiscal_year from core.fiscal_year where tenant_id = @t and company_id = @c and fiscal_year > @y and status = 'CLOSED' order by fiscal_year limit 1"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("y", year);
            if (await cmd.ExecuteScalarAsync(ct) is int later)
                return RejectReopen(JournalDocumentPoster.Error("CLOSE.REOPEN_LATER_YEAR_CLOSED", "P3", "fiscal_year", ("later_fiscal_year", later)));
        }
        // later years may hold only the automatic reversals of the last revaluation of this year (5.3)
        await using (var cmd = tx.Command(
            "select exists (select 1 from core.journal_document d where d.tenant_id = @t and d.company_id = @c and d.fiscal_year > @y " +
            "and not (d.document_type = 'REVAL_REVERSAL' and d.reversal_reason_code = 'AUTO_REVERSAL')) " +
            "or exists (select 1 from core.clearing c where c.tenant_id = @t and c.company_id = @c and c.fiscal_year > @y)"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("y", year);
            if ((bool)(await cmd.ExecuteScalarAsync(ct))!) return RejectReopen(JournalDocumentPoster.Error("CLOSE.REOPEN_BLOCKED_BY_POSTINGS", "P3", "fiscal_year", ("fiscal_year", year)));
        }
        PeriodRow closing = periods.Where(p => p.Type == "SPECIAL").OrderBy(p => p.No).Last();

        Guid? closingDocumentId = null;
        await using (var cmd = tx.Command("select year_end_document_id from core.fiscal_year where tenant_id = @t and company_id = @c and fiscal_year = @y"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("y", year);
            closingDocumentId = await cmd.ExecuteScalarAsync(ct) as Guid?;
        }
        LedgerReader.StoredDocument? original = closingDocumentId is null ? null : (await LedgerReader.ReadDocumentsAsync(tx, tenantId, closingDocumentId, null, null, ct)).FirstOrDefault();
        if (original is not null && (series is null || counter is null))
            return RejectReopen(series is null
                ? JournalDocumentPoster.Error("SERIES.NO_ASSIGNMENT", "P2", "fiscal_year", ("document_type", "YEAR_END"), ("ledger_id", ledgerId), ("posting_date", row.EndDate))
                : JournalDocumentPoster.Error("LIMIT.NUMBER_COUNTER_MISSING", "P7", "fiscal_year", ("series_code", series.SeriesCode), ("fiscal_year", year)));

        // The steps in this order (A4): the closing period, the year, then the mirror, which needs both open
        var cursor = new EventCursor(eventSeq, eventHash);
        string key = ctx.IdempotencyKey;
        Guid periodEvent = await AppendEventAsync(tx, tenantId, companyId, ctx, cursor, "PERIOD_REOPEN", year, closing.No, "HARD_CLOSED", "OPEN", req.ReasonCode, req.ReasonText, ReopenPermission, null, key + "#E1", ct);
        await using (var cmd = tx.Command(
            "update core.fiscal_period set status = 'OPEN', last_event_id = @e where tenant_id = @t and company_id = @c and fiscal_year = @y and period_no = @p"))
        {
            cmd.Parameters.AddWithValue("e", periodEvent); cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId);
            cmd.Parameters.AddWithValue("y", year); cmd.Parameters.AddWithValue("p", closing.No);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        long periodEventSeq = cursor.Seq; byte[] periodEventHash = cursor.Hash;
        Guid yearEvent = await AppendEventAsync(tx, tenantId, companyId, ctx, cursor, "YEAR_END_REOPEN", year, null, "CLOSED", "OPEN", req.ReasonCode, req.ReasonText, ReopenPermission, closingDocumentId, key + "#E2", ct);
        await using (var cmd = tx.Command(
            "update core.fiscal_year set status = 'OPEN', year_end_document_id = null, last_event_id = @e where tenant_id = @t and company_id = @c and fiscal_year = @y"))
        {
            cmd.Parameters.AddWithValue("e", yearEvent); cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("y", year);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        PostedDocument? mirror = null;
        if (original is not null)
        {
            var lines = await ClosingDocuments.MirrorLinesAsync(tx, tenantId, companyId, original, ct);
            var posted = await ClosingDocuments.PostAsync(ledger, ctx,
                new DocumentSpec("YEAR_END", ReopenCommand, series!, year, closing.No, false, (DateOnly)original.Header["posting_date"]!, key + "#D",
                    ReversalOf: closingDocumentId, ReasonCode: "REOPEN"), lines, closingDocumentId, ct);
            if (posted.Error is not null) return RejectReopen(posted.Error);
            mirror = posted.Document;
            await using var cmd = tx.Command(
                "update core.journal_document set status = 'reversed', reversed_by_document_id = @m where tenant_id = @t and company_id = @c and document_id = @d and status = 'posted'");
            cmd.Parameters.AddWithValue("m", mirror!.DocumentId); cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", closingDocumentId!.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        Guid logId = await CommandSupport.WriteLogAsync(tx, tenantId, companyId, ctx, ReopenCommand, fingerprint, "PERIOD_EVENT", yearEvent, ct);
        if (mirror is not null) await ClosingDocuments.WriteDocumentEventAsync(ledger, ctx, mirror, "ledger.document_reversed", closingDocumentId!.Value, "REVERSED", ct);
        await WritePeriodChangedAsync(tx, tenantId, companyId, ctx, year, closing.No, "OPEN", periodEventSeq, periodEventHash, ct);
        await CommandSupport.WriteOutboxAsync(tx, tenantId, companyId, ctx, "year.reopened", "FISCAL_YEAR", $"{companyId:D}/{year}", "REOPENED", DateTime.UtcNow, isAudit: true,
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["chain_type"] = PeriodEventHash2.ChainType, ["scope"] = "", ["chain_seq"] = cursor.Seq, ["row_hash"] = Canonical.FormatHex32(cursor.Hash)
            }),
            JsonSerializer.Serialize(new Dictionary<string, object?> { ["fiscal_year"] = year, ["document_id"] = mirror?.DocumentId, ["reason_code"] = req.ReasonCode }), ct);

        await tx.CommitAsync(ct);
        return CommandResult<YearReopenResult>.Ok(new YearReopenResult(companyId, year, closing.No, mirror, yearEvent), logId);
    }

    // ---------------------------------------------------------------------------------------------------------
    private static async Task<Guid> AppendEventAsync(
        TenantTransaction tx, Guid tenantId, Guid companyId, CommandContext ctx, EventCursor cursor, string type, int year, int? periodNo, string? from, string to,
        string? reasonCode, string? reasonText, string authorisation, Guid? documentId, string idempotencyKey, CancellationToken ct)
    {
        Guid eventId = Guid.NewGuid();
        DateTime now = DateTime.UtcNow;
        DateTime eventAt = new(now.Ticks - now.Ticks % 10, DateTimeKind.Utc);
        long seq = cursor.Seq + 1;
        var body = new Dictionary<string, object?>
        {
            ["event_id"] = eventId, ["fiscal_year"] = (long)year, ["period_no"] = periodNo is null ? null : (long)periodNo.Value, ["event_type"] = type,
            ["from_status"] = from, ["to_status"] = to, ["reason_code"] = reasonCode, ["reason_text"] = reasonText, ["authorisation"] = authorisation,
            ["document_id"] = documentId, ["event_at"] = eventAt, ["event_by"] = ctx.Actor, ["correlation_id"] = ctx.CorrelationId, ["idempotency_key"] = idempotencyKey
        };
        byte[] rowHash = PeriodEventHash2.RowHash(tenantId, companyId, seq, cursor.Hash, body);
        await using (var cmd = tx.Command(
            "insert into core.period_event (tenant_id, event_id, company_id, event_seq, fiscal_year, period_no, event_type, from_status, to_status, reason_code, reason_text, " +
            "authorisation, document_id, event_at, event_by, correlation_id, idempotency_key, prev_hash, row_hash, hash_version) " +
            "values (@t, @e, @c, @seq, @y, @p, @type, @from, @to, @rc, @rt, @auth, @doc, @at, @by, @corr, @key, @prev, @row, 2)"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("e", eventId); cmd.Parameters.AddWithValue("c", companyId);
            cmd.Parameters.AddWithValue("seq", seq); cmd.Parameters.AddWithValue("y", year);
            cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Integer) { Value = (object?)periodNo ?? DBNull.Value });
            cmd.Parameters.AddWithValue("type", type); cmd.Parameters.AddWithValue("from", (object?)from ?? DBNull.Value); cmd.Parameters.AddWithValue("to", to);
            cmd.Parameters.Add(new NpgsqlParameter("rc", NpgsqlDbType.Text) { Value = (object?)reasonCode ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("rt", NpgsqlDbType.Text) { Value = (object?)reasonText ?? DBNull.Value });
            cmd.Parameters.AddWithValue("auth", authorisation);
            cmd.Parameters.Add(new NpgsqlParameter("doc", NpgsqlDbType.Uuid) { Value = (object?)documentId ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = eventAt });
            cmd.Parameters.AddWithValue("by", ctx.Actor); cmd.Parameters.AddWithValue("corr", ctx.CorrelationId); cmd.Parameters.AddWithValue("key", idempotencyKey);
            cmd.Parameters.AddWithValue("prev", cursor.Hash); cmd.Parameters.AddWithValue("row", rowHash);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await ClearingPoster.MoveHeadAsync(tx, tenantId, companyId, PeriodEventHash2.ChainType, "", seq, rowHash, 2, ct);
        cursor.Seq = seq; cursor.Hash = rowHash;
        return eventId;
    }

    private static Task WritePeriodChangedAsync(
        TenantTransaction tx, Guid tenantId, Guid companyId, CommandContext ctx, int year, int periodNo, string status, long seq, byte[] hash, CancellationToken ct) =>
        CommandSupport.WriteOutboxAsync(tx, tenantId, companyId, ctx, "period.status_changed", "FISCAL_PERIOD", $"{companyId:D}/{year}/{periodNo}", "STATUS", DateTime.UtcNow, isAudit: true,
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["chain_type"] = PeriodEventHash2.ChainType, ["scope"] = "", ["chain_seq"] = seq, ["row_hash"] = Canonical.FormatHex32(hash)
            }),
            JsonSerializer.Serialize(new Dictionary<string, object?> { ["fiscal_year"] = year, ["period_no"] = periodNo, ["status"] = status }), ct);

    private static async Task<YearRow?> ReadYearAsync(TenantTransaction tx, Guid tenantId, Guid companyId, int year, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select status, is_history, special_period_count, end_date from core.fiscal_year where tenant_id = @t and company_id = @c and fiscal_year = @y" + (forUpdate ? " for update" : ""));
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("y", year);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new YearRow(r.GetString(0), r.GetBoolean(1), r.GetInt16(2), r.GetFieldValue<DateOnly>(3)) : null;
    }

    /// <summary>The period rows of the year, locked exclusively in period order.</summary>
    private static async Task<List<PeriodRow>> ReadPeriodsAsync(TenantTransaction tx, Guid tenantId, Guid companyId, int year, CancellationToken ct)
    {
        var result = new List<PeriodRow>();
        await using var cmd = tx.Command(
            "select period_no, period_type, status from core.fiscal_period where tenant_id = @t and company_id = @c and fiscal_year = @y order by period_no for update");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("y", year);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) result.Add(new PeriodRow(r.GetInt32(0), r.GetString(1), r.GetString(2)));
        return result;
    }

    /// <summary>The lines of the P&amp;L accounts of the year, grouped by account and dimension combination (5.1, step 1). YEAR_END documents and their mirrors and the CLOSING
    /// documents of migrated history years are left out, so a close after a reopen does not pick up the reversed closing.</summary>
    private static async Task<List<ClosingGroup>> ReadGroupsAsync(TenantTransaction tx, Guid tenantId, Guid companyId, int year, CancellationToken ct)
    {
        var groups = new List<ClosingGroup>();
        await using var cmd = tx.Command(
            "select l.account_id, l.cost_center_id, l.project_id, l.profit_center_id, l.business_unit_id, " +
            "coalesce((select string_agg(x.dimension_type_id::text || ':' || x.dimension_value_id::text, ',' order by x.dimension_type_id) from core.journal_line_dimension x " +
            "  where x.tenant_id = l.tenant_id and x.company_id = l.company_id and x.document_id = l.document_id and x.line_no = l.line_no), '') as free, " +
            "sum(l.amount_lc), sum(l.amount_gc), sum(l.amount_rc) " +
            "from core.journal_line l " +
            "join core.journal_document d on d.tenant_id = l.tenant_id and d.company_id = l.company_id and d.document_id = l.document_id " +
            "join core.gl_account a on a.tenant_id = l.tenant_id and a.company_id = l.company_id and a.account_id = l.account_id " +
            "where l.tenant_id = @t and l.company_id = @c and l.fiscal_year = @y and a.account_type in ('REVENUE', 'EXPENSE') and d.document_type not in ('YEAR_END', 'CLOSING') " +
            "group by 1, 2, 3, 4, 5, 6");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("y", year);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var free = r.GetString(5).Length == 0 ? [] : r.GetString(5).Split(',').Select(s => s.Split(':')).Select(p => (Guid.Parse(p[0]), Guid.Parse(p[1]))).ToList();
            groups.Add(new ClosingGroup(r.GetGuid(0), r.IsDBNull(1) ? null : r.GetGuid(1), r.IsDBNull(2) ? null : r.GetGuid(2), r.IsDBNull(3) ? null : r.GetGuid(3),
                r.IsDBNull(4) ? null : r.GetGuid(4), free, r.GetDecimal(6), r.GetDecimal(7), r.IsDBNull(8) ? null : r.GetDecimal(8)));
        }
        return groups;
    }

    /// <summary>For a replay: the document of a period event and the closing period of its year.</summary>
    private static async Task<(Guid? DocumentId, int ClosingPeriodNo)?> LoadEventAsync(TenantTransaction tx, Guid tenantId, Guid eventId, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select e.document_id, e.company_id, e.fiscal_year from core.period_event e where e.tenant_id = @t and e.event_id = @e");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("e", eventId);
        Guid? doc; Guid company; int year;
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return null;
            doc = r.IsDBNull(0) ? null : r.GetGuid(0); company = r.GetGuid(1); year = r.GetInt32(2);
        }
        await using var cmd2 = tx.Command("select max(period_no) from core.fiscal_period where tenant_id = @t and company_id = @c and fiscal_year = @y and period_type = 'SPECIAL'");
        cmd2.Parameters.AddWithValue("t", tenantId); cmd2.Parameters.AddWithValue("c", company); cmd2.Parameters.AddWithValue("y", year);
        return (doc, await cmd2.ExecuteScalarAsync(ct) is int n ? n : 0);
    }

    private static async Task<Guid?> MirrorOfAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid originalId, CancellationToken ct)
    {
        await using var cmd = tx.Command("select reversed_by_document_id from core.journal_document where tenant_id = @t and company_id = @c and document_id = @d");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", originalId);
        return await cmd.ExecuteScalarAsync(ct) as Guid?;
    }

    private static CommandResult<YearEndResult> RejectClose(CommandError e) => CommandResult<YearEndResult>.Rejected([e]);
    private static CommandResult<YearReopenResult> RejectReopen(CommandError e) => CommandResult<YearReopenResult>.Rejected([e]);
}
