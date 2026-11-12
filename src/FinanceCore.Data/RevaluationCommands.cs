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
/// revaluation:run and revaluation:preview (05, 3). A run revalues the open items and balances of the accounts with revalue_flag at the closing rates of the end date of a
/// regular period and posts two documents in one transaction: REVALUATION on the valuation date and its automatic reversal REVAL_REVERSAL on the first day of the next period.
/// A run for a period that has an ACTIVE run is a rerun: the old pair is neutralised by two mirrors and a new pair is posted (3.6). Preview computes the same and writes nothing.
/// Locks (04, 6.3): the number counters in sorted order, the LEDGER and CLEARING heads, the period rows in share mode, outbox_head last. The derivation runs after the locks.
/// </summary>
public sealed class RevaluationCommands(NpgsqlDataSource source)
{
    public const string RunPermission = "revaluation.run";
    public const string PreviewPermission = "revaluation.preview";
    public const string RunCommand = "revaluation:run";
    public const string PreviewCommand = "revaluation:preview";

    private static List<CommandError> ShapeErrors(CommandContext ctx, RunRevaluationRequest req)
    {
        var shape = new List<CommandError>();
        void Invalid(string path, string reason) => shape.Add(JournalDocumentPoster.Error("SCHEMA.INVALID_VALUE", "P1", path, ("reason", reason)));
        if (string.IsNullOrWhiteSpace(ctx.IdempotencyKey) || ctx.IdempotencyKey.Length > 90) Invalid("idempotency_key", "required, at most 90 characters");
        if (string.IsNullOrWhiteSpace(ctx.CorrelationId) || ctx.CorrelationId.Length > 100) Invalid("correlation_id", "required, at most 100 characters");
        if (string.IsNullOrWhiteSpace(ctx.Actor) || ctx.Actor.Length > 100) Invalid("actor", "required, at most 100 characters");
        if (req.CompanyId == Guid.Empty) Invalid("company_id", "required");
        if (req.PeriodNo < 1 || req.PeriodNo > 12) Invalid("period_no", "a regular period, 1 to 12");
        return shape;
    }

    // ---------------------------------------------------------------------------------------------------------
    public async Task<CommandResult<RevaluationResult>> PreviewAsync(Guid tenantId, CommandContext ctx, RunRevaluationRequest req, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(PreviewPermission))
            return Reject(JournalDocumentPoster.Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", PreviewPermission)));
        List<CommandError> shape = ShapeErrors(ctx, req);
        if (shape.Count > 0) return CommandResult<RevaluationResult>.Rejected(shape);

        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct);
        var period = await ReadPeriodAsync(tx, tenantId, req, ct);
        if (period is null) return Reject(JournalDocumentPoster.Error("PERIOD.NOT_FOUND", "P2", "period_no", ("fiscal_year", req.FiscalYear), ("period_no", req.PeriodNo)));
        var (computation, error) = await RevaluationBasis.ComputeAsync(tx, tenantId, req.CompanyId, period.Value, ct);
        if (error is not null) return Reject(error);
        RevaluationPlan plan = computation!.Plan;
        return CommandResult<RevaluationResult>.Ok(
            new RevaluationResult(null, 0, null, req.FiscalYear, req.PeriodNo, period.Value, plan.Items.Count, Canonical.FormatHex32(plan.Digest), null, null, [], plan.Rates, plan.Items),
            Guid.Empty);
    }

    private static async Task<DateOnly?> ReadPeriodAsync(TenantTransaction tx, Guid tenantId, RunRevaluationRequest req, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select end_date from core.fiscal_period where tenant_id = @t and company_id = @c and fiscal_year = @y and period_no = @p and period_type = 'REGULAR'");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("y", req.FiscalYear); cmd.Parameters.AddWithValue("p", req.PeriodNo);
        return await cmd.ExecuteScalarAsync(ct) is DateTime d ? DateOnly.FromDateTime(d) : null;
    }

    // ---------------------------------------------------------------------------------------------------------
    public async Task<CommandResult<RevaluationResult>> RunAsync(Guid tenantId, CommandContext ctx, RunRevaluationRequest req, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(RunPermission))
            return Reject(JournalDocumentPoster.Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", RunPermission)));
        List<CommandError> shape = ShapeErrors(ctx, req);
        if (shape.Count > 0) return CommandResult<RevaluationResult>.Rejected(shape);

        byte[] fingerprint = ClosingFingerprint.ForRun(req);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await RunOnceAsync(tenantId, ctx, req, fingerprint, ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.TableName == "command_log" && attempt == 0)
            {
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                return CommandResult<RevaluationResult>.Failed(new CommandError("SYSTEM.LOCK_TIMEOUT", "P7", "", null, true));
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure)
            {
                return CommandResult<RevaluationResult>.Failed(new CommandError("SYSTEM.CONFLICT", "P7", "", null, true));
            }
        }
    }

    /// <summary>A period or posting-window error of a date, in the codes of 05, 3.4 rule 8. For the reversal date the period codes are the next period's.</summary>
    private static CommandError Map(CommandError e, bool next, DateOnly date) => e.Code switch
    {
        "POSTING.OUTSIDE_WINDOW" => JournalDocumentPoster.Error("REVAL.OUTSIDE_POSTING_WINDOW", "P3", "period_no", ("date", date)),
        "PERIOD.SOFT_CLOSED_NOT_AUTHORISED" => JournalDocumentPoster.Error("REVAL.SOFT_CLOSE_NOT_AUTHORISED", "P3", "period_no", ("date", date)),
        "PERIOD.NOT_FOUND" => JournalDocumentPoster.Error(next ? "REVAL.NEXT_YEAR_NOT_OPENED" : "REVAL.PERIOD_NOT_OPEN", "P3", "period_no", ("date", date)),
        _ => JournalDocumentPoster.Error(next ? "REVAL.NEXT_PERIOD_NOT_OPEN" : "REVAL.PERIOD_NOT_OPEN", "P3", "period_no", ("date", date), ("cause", e.Code))
    };

    private async Task<CommandResult<RevaluationResult>> RunOnceAsync(Guid tenantId, CommandContext ctx, RunRevaluationRequest req, byte[] fingerprint, CancellationToken ct)
    {
        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Closing, ct: ct);
        Guid companyId = req.CompanyId;

        CommandSupport.EarlierCommand? earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
        if (earlier is not null)
        {
            if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint))
                return Reject(JournalDocumentPoster.Error("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
            RevaluationResult? replay = earlier.EntityId is null ? null : await LoadRunAsync(tx, tenantId, companyId, earlier.EntityId.Value, ct);
            return replay is null
                ? CommandResult<RevaluationResult>.Failed(JournalDocumentPoster.Error("SYSTEM.REPLAY_RESULT_MISSING", "P1", ""))
                : CommandResult<RevaluationResult>.Ok(replay, earlier.CommandLogId, replay: true);
        }

        // P2
        JournalDocumentPoster.CompanyRow? company = await JournalDocumentPoster.ReadCompanyAsync(tx, tenantId, companyId, ct);
        if (company is null || company.Status != "ACTIVE") return Reject(JournalDocumentPoster.Error("COMPANY.NOT_ACTIVE", "P2", "company_id", ("status", company?.Status)));
        DateOnly? end = await ReadPeriodAsync(tx, tenantId, req, ct);
        if (end is null) return Reject(JournalDocumentPoster.Error("PERIOD.NOT_FOUND", "P2", "period_no", ("fiscal_year", req.FiscalYear), ("period_no", req.PeriodNo)));
        DateOnly valuationDate = end.Value, reversalDate = valuationDate.AddDays(1);
        Guid? ledgerId = await ClearingPoster.ReadDefaultLedgerAsync(tx, tenantId, companyId, ct);
        if (ledgerId is null) return Reject(JournalDocumentPoster.Error("LEDGER.NOT_FOUND", "P2", "company_id"));
        string? groupCurrency = await ClearingPoster.ReadGroupCurrencyAsync(tx, tenantId, companyId, valuationDate, ct);
        if (groupCurrency is null) return Reject(JournalDocumentPoster.Error("CONFIG.GROUP_MEMBERSHIP_MISSING", "P2", "period_no", ("posting_date", valuationDate)));

        // P3: both dates must be postable, the period of the run and the next one (rules 1 to 3)
        var check = await JournalDocumentPoster.CheckPeriodAsync(tx, tenantId, companyId, valuationDate, ctx, false, ct);
        if (check.Errors.Count > 0) return CommandResult<RevaluationResult>.Rejected(check.Errors.Select(e => Map(e, false, valuationDate)).ToList());
        var checkNext = await JournalDocumentPoster.CheckPeriodAsync(tx, tenantId, companyId, reversalDate, ctx, false, ct);
        if (checkNext.Errors.Count > 0) return CommandResult<RevaluationResult>.Rejected(checkNext.Errors.Select(e => Map(e, true, reversalDate)).ToList());

        var seriesRev = await JournalDocumentPoster.ReadSeriesAsync(tx, tenantId, companyId, "REVALUATION", valuationDate, ledgerId.Value, ct);
        var seriesRevReversal = await JournalDocumentPoster.ReadSeriesAsync(tx, tenantId, companyId, "REVAL_REVERSAL", reversalDate, ledgerId.Value, ct);
        var seriesRevReversalAtValuation = await JournalDocumentPoster.ReadSeriesAsync(tx, tenantId, companyId, "REVAL_REVERSAL", valuationDate, ledgerId.Value, ct);
        if (seriesRev is null)
            return Reject(JournalDocumentPoster.Error("SERIES.NO_ASSIGNMENT", "P2", "period_no", ("document_type", "REVALUATION"), ("ledger_id", ledgerId), ("posting_date", valuationDate)));
        if (seriesRevReversal is null || seriesRevReversalAtValuation is null)
            return Reject(JournalDocumentPoster.Error("SERIES.NO_ASSIGNMENT", "P2", "period_no", ("document_type", "REVAL_REVERSAL"), ("ledger_id", ledgerId), ("posting_date", reversalDate)));

        // P7: counters in sorted order, then LEDGER and CLEARING, then the period rows in share mode, outbox_head last
        int yearV = check.FiscalYear!.Value, yearN = checkNext.FiscalYear!.Value;
        var counters = new List<(JournalDocumentPoster.SeriesRow, int)> { (seriesRev, yearV), (seriesRevReversal, yearN), (seriesRevReversalAtValuation, yearV) };
        if (await ClosingDocuments.LockCountersAsync(tx, tenantId, companyId, counters, ct) is { } missing) return Reject(missing);
        await using (var cmd = tx.Command("select core.lock_chain_heads(@c)")) { cmd.Parameters.AddWithValue("c", companyId); await cmd.ExecuteScalarAsync(ct); }
        ClosingLedger? ledger = await ClosingDocuments.OpenLedgerAsync(tx, tenantId, companyId, ledgerId.Value, company, groupCurrency, ct);
        var recheck = await JournalDocumentPoster.CheckPeriodAsync(tx, tenantId, companyId, valuationDate, ctx, true, ct);
        if (recheck.Errors.Count > 0) return CommandResult<RevaluationResult>.Rejected(recheck.Errors.Select(e => Map(e, false, valuationDate)).ToList());
        var recheckNext = await JournalDocumentPoster.CheckPeriodAsync(tx, tenantId, companyId, reversalDate, ctx, true, ct);
        if (recheckNext.Errors.Count > 0) return CommandResult<RevaluationResult>.Rejected(recheckNext.Errors.Select(e => Map(e, true, reversalDate)).ToList());

        // The ACTIVE run, locked: a second run of the period waits here and then sees the first one as the ACTIVE run (rerun)
        (Guid RunId, int RunNo, Guid? DocumentId, Guid? ReversalDocumentId)? active = null;
        await using (var cmd = tx.Command(
            "select run_id, run_no, document_id, reversal_document_id from core.revaluation_run " +
            "where tenant_id = @t and company_id = @c and fiscal_year = @y and fiscal_period = @p and status = 'ACTIVE' for update"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("y", req.FiscalYear); cmd.Parameters.AddWithValue("p", req.PeriodNo);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) active = (r.GetGuid(0), r.GetInt32(1), r.IsDBNull(2) ? null : r.GetGuid(2), r.IsDBNull(3) ? null : r.GetGuid(3));
        }

        // The derivation, after the locks (3.4, rule 6)
        var (computation, error) = await RevaluationBasis.ComputeAsync(tx, tenantId, companyId, valuationDate, ct);
        if (error is not null) return Reject(error);
        RevaluationPlan plan = computation!.Plan;

        // The accounts of the result lines: unrealised gain or loss per transaction currency, with the override of the currency (07, 6.3)
        var resultAccounts = new Dictionary<(string, bool), Guid>();
        foreach (ResultLine rl in plan.Results)
        {
            string role = rl.IsGain ? "FX_UNREALISED_GAIN" : "FX_UNREALISED_LOSS";
            Guid? account = await AccountRoleResolver.ResolveAsync(tx, tenantId, companyId, role, rl.CurrencyTc, null, null, ct);
            if (account is null) return Reject(JournalDocumentPoster.Error("CONFIG.ROLE_NOT_ASSIGNED", "P2", "company_id", ("role_code", role), ("currency", rl.CurrencyTc)));
            var row = (await JournalDocumentPoster.ReadAccountRowsAsync(tx, tenantId, companyId, [account.Value], ct)).GetValueOrDefault(account.Value);
            if (row is null || !row.Active || row.PostingBlocked || row.ValidFrom > valuationDate || (row.ValidTo is { } vt && vt < valuationDate))
                return Reject(JournalDocumentPoster.Error("ACCOUNT.ROLE_ACCOUNT_NOT_USABLE", "P2", "company_id", ("role_code", role), ("account_id", account)));
            resultAccounts[(rl.CurrencyTc, rl.IsGain)] = account.Value;
        }

        Guid runId = Guid.NewGuid();
        var mirrors = new List<PostedDocument>();
        var mirrorEvents = new List<(PostedDocument Mirror, Guid Original)>();
        string key = ctx.IdempotencyKey;
        PostedDocument? revaluation = null, reversal = null;

        // Rerun, step 0 to 2 (3.6): supersede the old run first, then neutralise its pair with two mirrors
        if (active is { } old)
        {
            await using (var cmd = tx.Command(
                "update core.revaluation_run set status = 'SUPERSEDED', superseded_by_run_id = @n where tenant_id = @t and company_id = @c and run_id = @r and status = 'ACTIVE'"))
            {
                cmd.Parameters.AddWithValue("n", runId); cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("r", old.RunId);
                if (await cmd.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("The ACTIVE run is no longer active");
            }
            if (old.DocumentId is { } r0Id && old.ReversalDocumentId is { } a0Id)
            {
                var r0 = (await LedgerReader.ReadDocumentsAsync(tx, tenantId, r0Id, null, null, ct))[0];
                var a0 = (await LedgerReader.ReadDocumentsAsync(tx, tenantId, a0Id, null, null, ct))[0];
                DateOnly a0Date = (DateOnly)a0.Header["posting_date"]!;
                // M1 mirrors R0 on the valuation date. R0 keeps A0 as its first mirror (B1), so its S columns do not change
                var m1 = await PostMirrorAsync(ledger!, ctx, r0, seriesRevReversalAtValuation, yearV, recheck.PeriodNo!.Value, recheck.SoftCloseOverride, valuationDate, "RERUN", key + "#M1", false, ct);
                if (m1.Error is not null) return Reject(m1.Error);
                mirrors.Add(m1.Document!); mirrorEvents.Add((m1.Document!, r0Id));
                // M2 mirrors A0 on its own date; A0 is reversed by it
                var m2 = await PostMirrorAsync(ledger!, ctx, a0, seriesRevReversal, yearN, recheckNext.PeriodNo!.Value, recheckNext.SoftCloseOverride, a0Date, "RERUN", key + "#M2", true, ct);
                if (m2.Error is not null) return Reject(m2.Error);
                mirrors.Add(m2.Document!); mirrorEvents.Add((m2.Document!, a0Id));
            }
        }

        // The new pair (3.3)
        if (plan.Adjustments.Count > 0)
        {
            var lines = new List<NewLine>();
            foreach (AdjustmentLine a in plan.Adjustments)
                lines.Add(ClosingDocuments.Line(ledger!, a.AccountId, "REVAL_ADJUSTMENT", 0m, a.Lc, a.Gc, a.Rc, valuationDate));
            foreach (ResultLine rl in plan.Results)
                lines.Add(ClosingDocuments.Line(ledger!, resultAccounts[(rl.CurrencyTc, rl.IsGain)], "FX_UNREALISED", 0m, rl.Lc, rl.Gc, rl.Rc, valuationDate));
            var posted = await ClosingDocuments.PostAsync(ledger!, ctx,
                new DocumentSpec("REVALUATION", RunCommand, seriesRev, yearV, recheck.PeriodNo!.Value, recheck.SoftCloseOverride, valuationDate, key + "#R",
                    DetailDigest: plan.Digest, AutoReversalDate: reversalDate), lines, null, ct);
            if (posted.Error is not null) return Reject(posted.Error);
            revaluation = posted.Document;

            var mirrorLines = lines.Select((n, i) => n with { Tc = -n.Tc, Lc = -n.Lc, Gc = -n.Gc, Rc = n.Rc is decimal rc ? -rc : null, ReversedLineNo = i + 1 }).ToList();
            var auto = await ClosingDocuments.PostAsync(ledger!, ctx,
                new DocumentSpec("REVAL_REVERSAL", RunCommand, seriesRevReversal, yearN, recheckNext.PeriodNo!.Value, recheckNext.SoftCloseOverride, reversalDate, key + "#A",
                    ReversalOf: revaluation!.DocumentId, ReasonCode: "AUTO_REVERSAL"), mirrorLines, revaluation.DocumentId, ct);
            if (auto.Error is not null) return Reject(auto.Error);
            reversal = auto.Document;
            await MarkReversedAsync(tx, tenantId, companyId, revaluation.DocumentId, reversal!.DocumentId, ct);
        }

        // The run, its rates and its items
        DateTime now = DateTime.UtcNow;
        DateTime runAt = new(now.Ticks - now.Ticks % 10, DateTimeKind.Utc);
        await using (var cmd = tx.Command(
            "insert into core.revaluation_run (tenant_id, company_id, run_id, fiscal_year, fiscal_period, valuation_date, run_no, supersedes_run_id, status, document_id, reversal_document_id, " +
            "item_count, items_digest, run_at, run_by, correlation_id, idempotency_key) values (@t, @c, @r, @y, @p, @vd, @no, @sup, 'ACTIVE', @doc, @rev, @n, @dig, @at, @by, @corr, @key)"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("r", runId);
            cmd.Parameters.AddWithValue("y", req.FiscalYear); cmd.Parameters.AddWithValue("p", req.PeriodNo); cmd.Parameters.AddWithValue("vd", valuationDate);
            cmd.Parameters.AddWithValue("no", (active?.RunNo ?? 0) + 1);
            cmd.Parameters.Add(new NpgsqlParameter("sup", NpgsqlDbType.Uuid) { Value = (object?)active?.RunId ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("doc", NpgsqlDbType.Uuid) { Value = (object?)revaluation?.DocumentId ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("rev", NpgsqlDbType.Uuid) { Value = (object?)reversal?.DocumentId ?? DBNull.Value });
            cmd.Parameters.AddWithValue("n", plan.Items.Count); cmd.Parameters.AddWithValue("dig", plan.Digest);
            cmd.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = runAt });
            cmd.Parameters.AddWithValue("by", ctx.Actor); cmd.Parameters.AddWithValue("corr", ctx.CorrelationId); cmd.Parameters.AddWithValue("key", key);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        foreach (RevaluationRateResult rate in plan.Rates)
        {
            await using var cmd = tx.Command(
                "insert into core.revaluation_run_rate (tenant_id, company_id, run_id, from_currency, to_currency, rate, rate_date) values (@t, @c, @r, @f, @to, @rate, @d)");
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("r", runId);
            cmd.Parameters.AddWithValue("f", rate.FromCurrency); cmd.Parameters.AddWithValue("to", rate.ToCurrency); cmd.Parameters.AddWithValue("rate", rate.Rate); cmd.Parameters.AddWithValue("d", rate.RateDate);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        foreach (RevaluationItemResult i in plan.Items)
        {
            await using var cmd = tx.Command(
                "insert into core.revaluation_run_item (tenant_id, company_id, run_id, item_no, basis_type, account_id, partner_id, currency_tc, open_item_document_id, open_item_line_no, " +
                "remaining_tc, carried_lc, carried_gc, carried_rc, revalued_lc, revalued_gc, revalued_rc, adjustment_lc, adjustment_gc, adjustment_rc, adjustment_line_no) " +
                "values (@t, @c, @r, @n, @b, @a, @p, @ccy, @od, @ol, @rem, @cl, @cg, @cr, @vl, @vg, @vr, @al, @ag, @ar, @ln)");
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("r", runId);
            cmd.Parameters.AddWithValue("n", i.ItemNo); cmd.Parameters.AddWithValue("b", i.BasisType); cmd.Parameters.AddWithValue("a", i.AccountId);
            cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Uuid) { Value = (object?)i.PartnerId ?? DBNull.Value });
            cmd.Parameters.AddWithValue("ccy", i.CurrencyTc);
            cmd.Parameters.Add(new NpgsqlParameter("od", NpgsqlDbType.Uuid) { Value = (object?)i.OpenItemDocumentId ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("ol", NpgsqlDbType.Integer) { Value = (object?)i.OpenItemLineNo ?? DBNull.Value });
            cmd.Parameters.AddWithValue("rem", i.RemainingTc); cmd.Parameters.AddWithValue("cl", i.CarriedLc); cmd.Parameters.AddWithValue("cg", i.CarriedGc);
            cmd.Parameters.Add(new NpgsqlParameter("cr", NpgsqlDbType.Numeric) { Value = (object?)i.CarriedRc ?? DBNull.Value });
            cmd.Parameters.AddWithValue("vl", i.RevaluedLc); cmd.Parameters.AddWithValue("vg", i.RevaluedGc);
            cmd.Parameters.Add(new NpgsqlParameter("vr", NpgsqlDbType.Numeric) { Value = (object?)i.RevaluedRc ?? DBNull.Value });
            cmd.Parameters.AddWithValue("al", i.AdjustmentLc); cmd.Parameters.AddWithValue("ag", i.AdjustmentGc);
            cmd.Parameters.Add(new NpgsqlParameter("ar", NpgsqlDbType.Numeric) { Value = (object?)i.AdjustmentRc ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("ln", NpgsqlDbType.Integer) { Value = (object?)i.AdjustmentLineNo ?? DBNull.Value });
            await cmd.ExecuteNonQueryAsync(ct);
        }
        if (computation.UsedRates.Count > 0)
        {
            await using var cmd = tx.Command("update core.exchange_rate set first_used_at = now() where tenant_id = @t and rate_id = any(@ids) and first_used_at is null");
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("ids", computation.UsedRates.Distinct().ToArray());
            await cmd.ExecuteNonQueryAsync(ct);
        }

        Guid logId = await CommandSupport.WriteLogAsync(tx, tenantId, companyId, ctx, RunCommand, fingerprint, "REVALUATION_RUN", runId, ct);

        // The outbox (3.4, rule 7): the documents, the mirrors, then the run events
        foreach (var (m, original) in mirrorEvents) await ClosingDocuments.WriteDocumentEventAsync(ledger!, ctx, m, "ledger.document_reversed", original, "REVERSED", ct);
        if (revaluation is not null) await ClosingDocuments.WriteDocumentEventAsync(ledger!, ctx, revaluation, "ledger.document_posted", revaluation.DocumentId, "POSTED", ct);
        if (reversal is not null) await ClosingDocuments.WriteDocumentEventAsync(ledger!, ctx, reversal, "ledger.document_posted", reversal.DocumentId, "POSTED", ct);
        string summary = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["run_id"] = runId, ["run_no"] = (active?.RunNo ?? 0) + 1, ["fiscal_year"] = req.FiscalYear, ["period_no"] = req.PeriodNo, ["items_digest"] = Canonical.FormatHex32(plan.Digest)
        });
        await CommandSupport.WriteOutboxAsync(tx, tenantId, companyId, ctx, "revaluation.run_completed", "REVALUATION_RUN", $"{companyId:D}/{runId:D}", "COMPLETED", runAt, isAudit: true, null, summary, ct);
        if (active is { } prior)
            await CommandSupport.WriteOutboxAsync(tx, tenantId, companyId, ctx, "revaluation.rerun", "REVALUATION_RUN", $"{companyId:D}/{runId:D}", "RERUN", runAt, isAudit: true, null,
                JsonSerializer.Serialize(new Dictionary<string, object?> { ["old_run_id"] = prior.RunId, ["new_run_id"] = runId }), ct);

        await tx.CommitAsync(ct);   // the deferred checks: balance, lines, snapshot, chain, outbox, the key of superseded_by_run_id
        var result = new RevaluationResult(runId, (active?.RunNo ?? 0) + 1, active?.RunId, req.FiscalYear, req.PeriodNo, valuationDate, plan.Items.Count,
            Canonical.FormatHex32(plan.Digest), revaluation, reversal, mirrors, plan.Rates, plan.Items);
        return CommandResult<RevaluationResult>.Ok(result, logId, recheck.Warnings.Concat(recheckNext.Warnings).ToList());
    }

    /// <summary>A mirror of a REVALUATION or REVAL_REVERSAL document of a rerun (3.6): type REVAL_REVERSAL, the reason RERUN, opposite signs. Marks the original reversed
    /// only when asked: R0 already has its first mirror and keeps it (B1).</summary>
    private static async Task<(PostedDocument? Document, CommandError? Error)> PostMirrorAsync(
        ClosingLedger ledger, CommandContext ctx, LedgerReader.StoredDocument original, JournalDocumentPoster.SeriesRow series, int fiscalYear, int periodNo,
        bool soft, DateOnly date, string reason, string key, bool markOriginal, CancellationToken ct)
    {
        Guid originalId = (Guid)original.Header["document_id"]!;
        var lines = await ClosingDocuments.MirrorLinesAsync(ledger.Tx, ledger.TenantId, ledger.CompanyId, original, ct);
        var posted = await ClosingDocuments.PostAsync(ledger, ctx,
            new DocumentSpec("REVAL_REVERSAL", RunCommand, series, fiscalYear, periodNo, soft, date, key, ReversalOf: originalId, ReasonCode: reason), lines, originalId, ct);
        if (posted.Error is null && markOriginal) await MarkReversedAsync(ledger.Tx, ledger.TenantId, ledger.CompanyId, originalId, posted.Document!.DocumentId, ct);
        return posted;
    }

    private static async Task MarkReversedAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid originalId, Guid mirrorId, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "update core.journal_document set status = 'reversed', reversed_by_document_id = @m where tenant_id = @t and company_id = @c and document_id = @d and status = 'posted'");
        cmd.Parameters.AddWithValue("m", mirrorId); cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", originalId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The run as stored, for the replay of an idempotent request. The mirrors of a rerun are not part of a replay.</summary>
    private static async Task<RevaluationResult?> LoadRunAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid runId, CancellationToken ct)
    {
        int year, period, runNo, count; DateOnly vd; Guid? sup, doc, rev; byte[] digest;
        await using (var cmd = tx.Command(
            "select fiscal_year, fiscal_period, valuation_date, run_no, supersedes_run_id, document_id, reversal_document_id, item_count, items_digest " +
            "from core.revaluation_run where tenant_id = @t and company_id = @c and run_id = @r"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("r", runId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            year = r.GetInt32(0); period = r.GetInt32(1); vd = r.GetFieldValue<DateOnly>(2); runNo = r.GetInt32(3);
            sup = r.IsDBNull(4) ? null : r.GetGuid(4); doc = r.IsDBNull(5) ? null : r.GetGuid(5); rev = r.IsDBNull(6) ? null : r.GetGuid(6); count = r.GetInt32(7); digest = (byte[])r[8];
        }
        var rates = new List<RevaluationRateResult>();
        await using (var cmd = tx.Command("select from_currency, to_currency, rate, rate_date from core.revaluation_run_rate where tenant_id = @t and company_id = @c and run_id = @r order by from_currency, to_currency"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("r", runId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) rates.Add(new RevaluationRateResult(r.GetString(0).Trim(), r.GetString(1).Trim(), r.GetDecimal(2), r.GetFieldValue<DateOnly>(3)));
        }
        var items = new List<RevaluationItemResult>();
        await using (var cmd = tx.Command(
            "select item_no, basis_type, account_id, partner_id, currency_tc, open_item_document_id, open_item_line_no, remaining_tc, carried_lc, carried_gc, carried_rc, " +
            "revalued_lc, revalued_gc, revalued_rc, adjustment_lc, adjustment_gc, adjustment_rc, adjustment_line_no from core.revaluation_run_item " +
            "where tenant_id = @t and company_id = @c and run_id = @r order by item_no"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("r", runId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                items.Add(new RevaluationItemResult(r.GetInt32(0), r.GetString(1), r.GetGuid(2), r.IsDBNull(3) ? null : r.GetGuid(3), r.GetString(4).Trim(),
                    r.IsDBNull(5) ? null : r.GetGuid(5), r.IsDBNull(6) ? null : r.GetInt32(6), r.GetDecimal(7), r.GetDecimal(8), r.GetDecimal(9), r.IsDBNull(10) ? null : r.GetDecimal(10),
                    r.GetDecimal(11), r.GetDecimal(12), r.IsDBNull(13) ? null : r.GetDecimal(13), r.GetDecimal(14), r.GetDecimal(15), r.IsDBNull(16) ? null : r.GetDecimal(16),
                    r.IsDBNull(17) ? null : r.GetInt32(17)));
        }
        PostedDocument? d = doc is null ? null : await LedgerReader.LoadDocumentAsync(tx, tenantId, doc.Value, ct);
        PostedDocument? a = rev is null ? null : await LedgerReader.LoadDocumentAsync(tx, tenantId, rev.Value, ct);
        return new RevaluationResult(runId, runNo, sup, year, period, vd, count, Canonical.FormatHex32(digest), d, a, [], rates, items);
    }

    private static CommandResult<RevaluationResult> Reject(CommandError error) => CommandResult<RevaluationResult>.Rejected([error]);
}
