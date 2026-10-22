using System.Globalization;
using System.Text.Json;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Hashing;
using FinanceCore.Core.Posting;
using Npgsql;
using NpgsqlTypes;

namespace FinanceCore.Data;

/// <summary>
/// journal-documents:post of 06, 5.2 for the first slice: base lines on plain accounts, one transaction currency,
/// derived local, group and reporting amounts, gapless number, hash chain, idempotency and outbox event.
/// Not in the slice (answered with a LIMIT.* error, never silently ignored): tax, partners, open items, dimensions,
/// special periods, rate overrides, rounding lines, the AP and AR document types.
/// One command is one transaction. Everything that can be checked is checked before the first lock (P1 to P6),
/// the locks come in the order of 06, 9: number counter, ledger chain head, period rows, outbox head.
/// </summary>
public sealed class JournalDocumentPoster(NpgsqlDataSource source)
{
    public const string CommandName = "journal-documents:post";
    public const string PostPermission = "journal.post";
    public const string SoftClosedPermission = "post.soft_closed";

    private static readonly HashSet<string> ExternalTypes = ["JOURNAL", "AP_INVOICE", "AP_CREDIT", "AR_INVOICE", "AR_CREDIT", "PAYMENT"];
    private static readonly HashSet<string> SliceTypes = ["JOURNAL", "PAYMENT"];

    public async Task<CommandResult<PostedDocument>> PostAsync(
        Guid tenantId, CommandContext ctx, PostJournalDocumentRequest req, CancellationToken ct = default)
    {
        // P0
        if (!ctx.Permissions.Contains(PostPermission))
            return Reject(Error("AUTH.PERMISSION_DENIED", "P0", "", ("permission", PostPermission)));

        // P1, schema part
        List<CommandError> shape = ValidateShape(ctx, req);
        if (shape.Count > 0) return CommandResult<PostedDocument>.Rejected(shape);

        byte[] fingerprint = RequestFingerprint.Compute(req);

        // A concurrent command with the same key can win the insert into command_log. The unique key then stops this
        // transaction at commit, and the second round finds the winner and answers as a replay.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await PostOnceAsync(tenantId, ctx, req, fingerprint, ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.TableName == "command_log" && attempt == 0)
            {
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                return CommandResult<PostedDocument>.Failed(Error("SYSTEM.LOCK_TIMEOUT", "P7", "", retryable: true));
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure)
            {
                return CommandResult<PostedDocument>.Failed(Error("SYSTEM.CONFLICT", "P7", "", retryable: true));
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // P1: shape of the request
    // ---------------------------------------------------------------------------------------------------------
    private static List<CommandError> ValidateShape(CommandContext ctx, PostJournalDocumentRequest r)
    {
        var errors = new List<CommandError>();
        void Invalid(string path, string reason) => errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", path, ("reason", reason)));

        if (string.IsNullOrWhiteSpace(ctx.IdempotencyKey) || ctx.IdempotencyKey.Length > 100) Invalid("idempotency_key", "required, at most 100 characters");
        if (string.IsNullOrWhiteSpace(ctx.CorrelationId) || ctx.CorrelationId.Length > 100) Invalid("correlation_id", "required, at most 100 characters");
        if (string.IsNullOrWhiteSpace(ctx.Actor) || ctx.Actor.Length > 100) Invalid("actor", "required, at most 100 characters");
        if (r.CompanyId == Guid.Empty) Invalid("company_id", "required");
        if (string.IsNullOrWhiteSpace(r.DocumentType)) Invalid("document_type", "required");
        if (r.TransactionCurrency is not { Length: 3 } || !r.TransactionCurrency.All(c => c is >= 'A' and <= 'Z')) Invalid("transaction_currency", "three capital letters");
        if (r.HeaderText?.Length > 200) Invalid("header_text", "at most 200 characters");
        if (r.ExternalReference?.Length > 100) Invalid("external_reference", "at most 100 characters");
        if (r.SourceDocumentRef?.Length > 100) Invalid("source_document_ref", "at most 100 characters");

        if (r.Lines is null || r.Lines.Count < 2)
            Invalid("lines", "at least 2 base lines");
        else
        {
            var seen = new HashSet<string>();
            for (int i = 0; i < r.Lines.Count; i++)
            {
                RequestLine l = r.Lines[i];
                if (string.IsNullOrWhiteSpace(l.LineRef)) Invalid($"lines[{i}].line_ref", "required");
                else if (!seen.Add(l.LineRef)) errors.Add(Error("SCHEMA.DUPLICATE_LINE_REF", "P1", $"lines[{i}].line_ref", ("line_ref", l.LineRef)));
                if (l.AccountId == Guid.Empty) Invalid($"lines[{i}].account_id", "required");
                if (Math.Round(l.AmountTc, Canonical.AmountScale) != l.AmountTc) Invalid($"lines[{i}].amount_tc", $"at most {Canonical.AmountScale} fraction digits");
                if (l.LineText?.Length > 200) Invalid($"lines[{i}].line_text", "at most 200 characters");
            }
        }
        return errors;
    }

    // ---------------------------------------------------------------------------------------------------------
    // One attempt: P1 (idempotency) to P7 in one transaction
    // ---------------------------------------------------------------------------------------------------------
    private async Task<CommandResult<PostedDocument>> PostOnceAsync(
        Guid tenantId, CommandContext ctx, PostJournalDocumentRequest req, byte[] fingerprint, CancellationToken ct)
    {
        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Post, ct: ct);

        // P1, idempotency (06, 4)
        await using (var cmd = tx.Command(
            "select command_log_id, request_fingerprint from core.command_log where tenant_id = @t and idempotency_key = @k"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("k", ctx.IdempotencyKey);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                Guid earlierLogId = reader.GetGuid(0);
                byte[] stored = (byte[])reader[1];
                await reader.CloseAsync();
                if (!stored.AsSpan().SequenceEqual(fingerprint))
                    return Reject(Error("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
                PostedDocument? replay = await LoadReplayAsync(tx, tenantId, earlierLogId, ct);
                return replay is null
                    ? CommandResult<PostedDocument>.Failed(Error("SYSTEM.REPLAY_RESULT_MISSING", "P1", "", ("command_log_id", earlierLogId)))
                    : CommandResult<PostedDocument>.Ok(replay, earlierLogId, replay: true);
            }
        }

        var errors = new List<CommandError>();
        var warnings = new List<CommandError>();

        // P2: company, ledger, type, currency, accounts, series
        if (!ExternalTypes.Contains(req.DocumentType))
            errors.Add(Error("DOCUMENT.TYPE_NOT_ALLOWED", "P2", "document_type", ("allowed_values", ExternalTypes.Order().ToArray())));
        else if (!SliceTypes.Contains(req.DocumentType))
            errors.Add(Error("LIMIT.DOCUMENT_TYPE_NEEDS_PARTNER", "P2", "document_type", ("allowed_values", SliceTypes.Order().ToArray())));

        CompanyRow? company = await ReadCompanyAsync(tx, tenantId, req.CompanyId, ct);
        if (company is null)
            return Reject(Error("COMPANY.NOT_FOUND", "P2", "company_id"));
        if (company.Status != "ACTIVE")
            errors.Add(Error("COMPANY.NOT_ACTIVE", "P2", "company_id", ("status", company.Status)));

        Guid? ledgerId = await ReadLedgerAsync(tx, tenantId, req, ct);
        if (ledgerId is null) errors.Add(Error("LEDGER.NOT_FOUND", "P2", "ledger_id"));

        string? groupCurrency = await ReadGroupCurrencyAsync(tx, tenantId, req, ct);
        if (groupCurrency is null) errors.Add(Error("CONFIG.GROUP_MEMBERSHIP_MISSING", "P2", "company_id", ("posting_date", req.PostingDate)));

        if (!await IsTenantCurrencyActiveAsync(tx, tenantId, req.TransactionCurrency, ct))
            errors.Add(Error("CURRENCY.NOT_ACTIVE", "P2", "transaction_currency", ("currency", req.TransactionCurrency)));

        var scales = new Dictionary<string, int>();
        foreach (string c in new[] { req.TransactionCurrency, company.LocalCurrency, groupCurrency, company.ReportingCurrency }.Where(c => c is not null).Distinct()!)
        {
            int? minor = await ReadMinorUnitsAsync(tx, c!, ct);
            if (minor is null) errors.Add(Error("CURRENCY.UNKNOWN", "P2", "transaction_currency", ("currency", c)));
            else scales[c!] = minor.Value;
        }
        if (scales.TryGetValue(req.TransactionCurrency, out int tcScale))
            for (int i = 0; i < req.Lines.Count; i++)
                if (Math.Round(req.Lines[i].AmountTc, tcScale) != req.Lines[i].AmountTc)
                    errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", $"lines[{i}].amount_tc",
                        ("reason", $"{req.TransactionCurrency} has {tcScale} fraction digits")));

        Dictionary<Guid, AccountRow> accounts = await ReadAccountsAsync(tx, tenantId, req, ct);
        for (int i = 0; i < req.Lines.Count; i++)
        {
            Guid id = req.Lines[i].AccountId;
            string path = $"lines[{i}].account_id";
            if (!accounts.TryGetValue(id, out AccountRow? a)) { errors.Add(Error("ACCOUNT.NOT_FOUND", "P2", path, ("account_id", id))); continue; }
            if (!a.Active) errors.Add(Error("ACCOUNT.INACTIVE", "P2", path, ("account_no", a.AccountNo)));
            if (a.ValidFrom > req.PostingDate || (a.ValidTo is { } to && to < req.PostingDate))
                errors.Add(Error("ACCOUNT.NOT_VALID_AT_DATE", "P2", path, ("account_no", a.AccountNo), ("posting_date", req.PostingDate)));
            if (a.PostingBlocked) errors.Add(Error("ACCOUNT.POSTING_BLOCKED", "P2", path, ("account_no", a.AccountNo)));
            if (a.OpenItemMode != "NONE") errors.Add(Error("LIMIT.OPEN_ITEM_ACCOUNT", "P2", path, ("account_no", a.AccountNo)));
            if (a.TaxCodeRule == "MANDATORY") errors.Add(Error("LIMIT.TAX_CODE_MANDATORY", "P2", path, ("account_no", a.AccountNo)));
            if (a.CurrencyCode is not null) errors.Add(Error("LIMIT.ACCOUNT_CURRENCY", "P2", path, ("account_no", a.AccountNo)));
        }

        SeriesRow? series = ledgerId is null ? null : await ReadSeriesAsync(tx, tenantId, req.CompanyId, req.DocumentType, req.PostingDate, ledgerId.Value, ct);
        if (ledgerId is not null && series is null)
            errors.Add(Error("SERIES.NO_ASSIGNMENT", "P2", "document_type",
                ("document_type", req.DocumentType), ("ledger_id", ledgerId), ("posting_date", req.PostingDate)));

        // P3: posting window, period, year, status (04, 7.1)
        PeriodCheck period = await CheckPeriodAsync(tx, tenantId, req.CompanyId, req.PostingDate, ctx, lockShare: false, ct);
        errors.AddRange(period.Errors);
        warnings.AddRange(period.Warnings);

        // P6 (TC balance) is cheap and independent, so it is reported with the other errors.
        decimal sumTc = req.Lines.Sum(l => l.AmountTc);
        if (sumTc != 0m) errors.Add(Error("DOCUMENT.NOT_BALANCED", "P6", "lines", ("currency", "TC"), ("difference", sumTc)));

        if (errors.Count > 0) return CommandResult<PostedDocument>.Rejected(errors.Take(100).ToList());

        // P4 and P5: rates and derived amounts
        int fallbackDays = await ReadFallbackDaysAsync(tx, tenantId, ct);
        string? pivot = await ReadPivotAsync(tx, tenantId, ct);
        var usedRates = new List<Guid>();
        var rateErrors = new List<CommandError>();

        Rate rateLc = await LookupRateAsync(tx, tenantId, req.TransactionCurrency, company.LocalCurrency, req.PostingDate, fallbackDays, pivot, usedRates, warnings, rateErrors, "local", ct);
        Rate rateGc = await LookupRateAsync(tx, tenantId, req.TransactionCurrency, groupCurrency!, req.PostingDate, fallbackDays, pivot, usedRates, warnings, rateErrors, "group", ct);
        Rate? rateRc = company.ReportingCurrency is null ? null
            : await LookupRateAsync(tx, tenantId, req.TransactionCurrency, company.ReportingCurrency, req.PostingDate, fallbackDays, pivot, usedRates, warnings, rateErrors, "reporting", ct);
        if (rateErrors.Count > 0) return CommandResult<PostedDocument>.Rejected(rateErrors);

        var derived = new List<(RequestLine Line, decimal Lc, decimal Gc, decimal? Rc)>();
        foreach (RequestLine l in req.Lines)
            derived.Add((l,
                PostingRules.Round(l.AmountTc * rateLc.Value, scales[company.LocalCurrency]),
                PostingRules.Round(l.AmountTc * rateGc.Value, scales[groupCurrency!]),
                rateRc is null ? null : PostingRules.Round(l.AmountTc * rateRc.Value, scales[company.ReportingCurrency!])));

        // P6: balance of the derived currencies. Rounding lines need configured rounding accounts (03, design
        // principle 4), which the schema does not have yet. Finding F-IMPL-7.
        void CheckBalance(string label, string currency, decimal sum)
        {
            if (sum != 0m)
                errors.Add(Error("LIMIT.ROUNDING_DIFFERENCE", "P6", "lines", ("currency", currency), ("scope", label), ("difference", sum)));
        }
        CheckBalance("local", company.LocalCurrency, derived.Sum(d => d.Lc));
        CheckBalance("group", groupCurrency!, derived.Sum(d => d.Gc));
        if (rateRc is not null) CheckBalance("reporting", company.ReportingCurrency!, derived.Sum(d => d.Rc!.Value));
        if (errors.Count > 0) return CommandResult<PostedDocument>.Rejected(errors);

        // P7: locks in the order of 06, 9. Counter first.
        int fiscalYear = period.FiscalYear!.Value;
        decimal lastNumber;
        await using (var cmd = tx.Command(
            "select last_number from core.number_range_counter " +
            "where tenant_id = @t and company_id = @c and series_id = @s and fiscal_year = @y for update"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", req.CompanyId);
            cmd.Parameters.AddWithValue("s", series!.SeriesId);
            cmd.Parameters.AddWithValue("y", fiscalYear);
            object? value = await cmd.ExecuteScalarAsync(ct);
            if (value is null)
                return Reject(Error("LIMIT.NUMBER_COUNTER_MISSING", "P7", "document_type", ("series_code", series.SeriesCode), ("fiscal_year", fiscalYear)));
            lastNumber = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        }
        long sequenceNo = (long)lastNumber + 1;
        await using (var cmd = tx.Command(
            "update core.number_range_counter set last_number = @n, updated_at = now() " +
            "where tenant_id = @t and company_id = @c and series_id = @s and fiscal_year = @y"))
        {
            cmd.Parameters.AddWithValue("n", sequenceNo);
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", req.CompanyId);
            cmd.Parameters.AddWithValue("s", series.SeriesId);
            cmd.Parameters.AddWithValue("y", fiscalYear);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Ledger chain head
        long headSeq;
        byte[] headHash;
        await using (var cmd = tx.Command(
            "select last_seq, last_hash from core.chain_head " +
            "where tenant_id = @t and company_id = @c and chain_type = 'LEDGER' and scope_key = @k for update"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", req.CompanyId);
            cmd.Parameters.AddWithValue("k", ledgerId!.Value.ToString("D"));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return CommandResult<PostedDocument>.Failed(Error("SYSTEM.CHAIN_HEAD_MISSING", "P7", "", ("ledger_id", ledgerId)));
            headSeq = reader.GetInt64(0);
            headHash = (byte[])reader[1];
        }

        // Period rows, share lock, and the status check again under the locks (06, 3: the status may have changed
        // between P3 and the locks).
        PeriodCheck recheck = await CheckPeriodAsync(tx, tenantId, req.CompanyId, req.PostingDate, ctx, lockShare: true, ct);
        if (recheck.Errors.Count > 0) return CommandResult<PostedDocument>.Rejected(recheck.Errors);

        // Build the document and its hashes. Everything the hash needs is known here.
        Guid documentId = Guid.NewGuid();
        DateTime now = DateTime.UtcNow;
        DateTime postedAt = new(now.Ticks - now.Ticks % 10, DateTimeKind.Utc);   // microseconds, as the database stores them
        long chainSeq = headSeq + 1;
        string number = PostingRules.FormatNumber(series.Prefix, series.IncludeFiscalYear, series.Separator, series.NumberLength, fiscalYear, sequenceNo);
        bool softOverride = recheck.SoftCloseOverride;

        var header = new Dictionary<string, object?>
        {
            ["tenant_id"] = tenantId, ["company_id"] = req.CompanyId, ["document_id"] = documentId, ["ledger_id"] = ledgerId.Value,
            ["document_type"] = req.DocumentType, ["is_system_generated"] = false, ["number_series_id"] = series.SeriesId,
            ["fiscal_year"] = fiscalYear, ["sequence_no"] = sequenceNo, ["document_number"] = number,
            ["fiscal_period"] = recheck.PeriodNo!.Value, ["soft_close_override"] = softOverride,
            ["migration_batch_id"] = null, ["detail_digest"] = null,
            ["document_date"] = req.DocumentDate, ["posting_date"] = req.PostingDate, ["posted_at"] = postedAt,
            ["transaction_currency"] = req.TransactionCurrency,
            ["reversal_of_document_id"] = null, ["reversal_reason_code"] = null, ["auto_reversal_date"] = null,
            ["ic_transaction_id"] = null, ["ic_counterpart_company_id"] = null, ["origin"] = "EXTERNAL",
            ["source_system"] = ctx.SourceSystem, ["source_document_ref"] = req.SourceDocumentRef,
            ["idempotency_key"] = ctx.IdempotencyKey, ["command_name"] = CommandName, ["posted_by"] = ctx.Actor,
            ["correlation_id"] = ctx.CorrelationId, ["hash_version"] = LedgerHash2.Version
        };

        var lines = new List<PostedLine>();
        var lineHashes = new List<byte[]>();
        for (int i = 0; i < derived.Count; i++)
        {
            var d = derived[i];
            int lineNo = i + 1;
            var line = new PostedLine(lineNo, d.Line.LineRef, d.Line.AccountId, "BASE",
                d.Line.AmountTc, d.Lc, company.LocalCurrency, rateLc.Value,
                d.Gc, groupCurrency!, rateGc.Value,
                d.Rc, company.ReportingCurrency, rateRc?.Value,
                "SPOT", req.PostingDate);
            lines.Add(line);
            lineHashes.Add(LedgerHash2.LineHash(new HashedLine(new Dictionary<string, object?>
            {
                ["tenant_id"] = tenantId, ["company_id"] = req.CompanyId, ["document_id"] = documentId, ["line_no"] = lineNo,
                ["ledger_id"] = ledgerId.Value, ["line_type"] = "BASE", ["account_id"] = d.Line.AccountId,
                ["posting_date"] = req.PostingDate, ["fiscal_year"] = fiscalYear, ["fiscal_period"] = recheck.PeriodNo!.Value,
                ["reversed_line_no"] = null, ["tax_code_id"] = null,
                ["amount_tc"] = line.AmountTc, ["amount_lc"] = line.AmountLc, ["currency_lc"] = line.CurrencyLc,
                ["rate_lc"] = line.RateLc, ["rate_lc_table"] = null,
                ["amount_gc"] = line.AmountGc, ["currency_gc"] = line.CurrencyGc, ["rate_gc"] = line.RateGc, ["rate_gc_table"] = null,
                ["amount_rc"] = line.AmountRc, ["currency_rc"] = line.CurrencyRc, ["rate_rc"] = line.RateRc, ["rate_rc_table"] = null,
                ["rate_type"] = "SPOT", ["rate_date"] = req.PostingDate, ["ic_counterpart_company_id"] = null
            })));
        }
        byte[] rowHash = LedgerHash2.RowHash(header, chainSeq, headHash, LedgerHash2.LineDigest(lineHashes));

        // Insert document and lines
        await InsertDocumentAsync(tx, tenantId, req, ctx, documentId, ledgerId.Value, series, fiscalYear, recheck.PeriodNo!.Value,
            sequenceNo, number, softOverride, postedAt, chainSeq, headHash, rowHash, ct);
        foreach (PostedLine l in lines)
            await InsertLineAsync(tx, tenantId, req, documentId, ledgerId.Value, fiscalYear, recheck.PeriodNo!.Value,
                req.Lines[l.LineNo - 1].LineText, l, ct);

        // Move the chain head
        await using (var cmd = tx.Command(
            "update core.chain_head set last_seq = @s, last_hash = @h, hash_version = @v, updated_at = now() " +
            "where tenant_id = @t and company_id = @c and chain_type = 'LEDGER' and scope_key = @k"))
        {
            cmd.Parameters.AddWithValue("s", chainSeq);
            cmd.Parameters.AddWithValue("h", rowHash);
            cmd.Parameters.AddWithValue("v", (short)LedgerHash2.Version);
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", req.CompanyId);
            cmd.Parameters.AddWithValue("k", ledgerId.Value.ToString("D"));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Rates are marked used, rows in key order so that two commands never wait for each other in a circle
        if (usedRates.Count > 0)
        {
            Guid[] ids = usedRates.Distinct().Order().ToArray();
            await using var cmd = tx.Command(
                "with locked as (select rate_id from core.exchange_rate where tenant_id = @t and rate_id = any(@ids) order by rate_id for update) " +
                "update core.exchange_rate r set first_used_at = now() from locked " +
                "where r.tenant_id = @t and r.rate_id = locked.rate_id and r.first_used_at is null");
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("ids", ids);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Command log and its result
        Guid logId = Guid.NewGuid();
        await using (var cmd = tx.Command(
            "insert into core.command_log (tenant_id, command_log_id, company_id, idempotency_key, command_name, request_fingerprint, actor, correlation_id) " +
            "values (@t, @id, @c, @k, @n, @f, @a, @corr)"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("id", logId);
            cmd.Parameters.AddWithValue("c", req.CompanyId);
            cmd.Parameters.AddWithValue("k", ctx.IdempotencyKey);
            cmd.Parameters.AddWithValue("n", CommandName);
            cmd.Parameters.AddWithValue("f", fingerprint);
            cmd.Parameters.AddWithValue("a", ctx.Actor);
            cmd.Parameters.AddWithValue("corr", ctx.CorrelationId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using (var cmd = tx.Command(
            "insert into core.command_log_result (tenant_id, command_log_id, result_seq, entity_type, entity_id) " +
            "values (@t, @id, 1, 'DOCUMENT', @e)"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("id", logId);
            cmd.Parameters.AddWithValue("e", documentId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Outbox last: the head row is the hot spot, so its lock is held for the shortest time (15, 3.8)
        long position;
        await using (var cmd = tx.Command(
            "update core.outbox_head set last_position = last_position + 1 where tenant_id = @t returning last_position"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            object? value = await cmd.ExecuteScalarAsync(ct);
            if (value is null) return CommandResult<PostedDocument>.Failed(Error("SYSTEM.OUTBOX_HEAD_MISSING", "P7", ""));
            position = (long)value;
        }
        string rowHashHex = Canonical.FormatHex32(rowHash);
        string chainRef = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["chain_type"] = "LEDGER", ["scope"] = ledgerId.Value.ToString("D"), ["chain_seq"] = chainSeq, ["row_hash"] = rowHashHex
        });
        string summary = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["document_type"] = req.DocumentType, ["posting_date"] = req.PostingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["period"] = recheck.PeriodNo, ["ledger_ids"] = new[] { ledgerId.Value.ToString("D") }, ["ledger_seq"] = chainSeq
        });
        await using (var cmd = tx.Command(
            "insert into core.outbox_event (tenant_id, position, event_type, event_version, company_id, entity_type, entity_key, " +
            "entity_version, change, occurred_at, actor_ref, correlation_id, is_audit, chain_ref, summary) " +
            "values (@t, @p, 'ledger.document_posted', 1, @c, 'DOCUMENT', @key, null, 'POSTED', @at, @a, @corr, true, @chain, @sum)"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("p", position);
            cmd.Parameters.AddWithValue("c", req.CompanyId);
            cmd.Parameters.AddWithValue("key", $"{req.CompanyId:D}/{documentId:D}");
            cmd.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = postedAt });
            cmd.Parameters.AddWithValue("a", ctx.Actor);
            cmd.Parameters.AddWithValue("corr", ctx.CorrelationId);
            cmd.Parameters.Add(new NpgsqlParameter("chain", NpgsqlDbType.Jsonb) { Value = chainRef });
            cmd.Parameters.Add(new NpgsqlParameter("sum", NpgsqlDbType.Jsonb) { Value = summary });
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Commit runs the deferred checks: balance, lines present, chain link and head, outbox position.
        await tx.CommitAsync(ct);

        warnings.AddRange(recheck.Warnings.Where(w => warnings.All(x => x.Code != w.Code)));
        var posted = new PostedDocument(documentId, req.CompanyId, ledgerId.Value, req.DocumentType, number, sequenceNo,
            fiscalYear, recheck.PeriodNo!.Value, req.DocumentDate, req.PostingDate, req.TransactionCurrency,
            "posted", softOverride, chainSeq, rowHashHex, Canonical.FormatHex32(headHash), LedgerHash2.Version, lines);
        return CommandResult<PostedDocument>.Ok(posted, logId, warnings);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Reads
    // ---------------------------------------------------------------------------------------------------------
    internal sealed record CompanyRow(string Status, string LocalCurrency, string? ReportingCurrency);
    internal sealed record AccountRow(string AccountNo, bool Active, DateOnly ValidFrom, DateOnly? ValidTo, bool PostingBlocked,
        string OpenItemMode, string TaxCodeRule, string? CurrencyCode);
    internal sealed record SeriesRow(Guid SeriesId, string SeriesCode, string? Prefix, bool IncludeFiscalYear, string? Separator, int NumberLength);
    internal sealed record Rate(decimal Value);
    internal sealed record PeriodCheck(int? FiscalYear, int? PeriodNo, bool SoftCloseOverride, List<CommandError> Errors, List<CommandError> Warnings);

    internal static async Task<CompanyRow?> ReadCompanyAsync(TenantTransaction tx, Guid tenantId, Guid companyId, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select status, local_currency, reporting_currency from core.company where tenant_id = @t and company_id = @c");
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", companyId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new CompanyRow(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2))
            : null;
    }

    private static async Task<Guid?> ReadLedgerAsync(TenantTransaction tx, Guid tenantId, PostJournalDocumentRequest req, CancellationToken ct)
    {
        await using var cmd = tx.Command(req.LedgerId is null
            ? "select ledger_id from core.ledger where tenant_id = @t and company_id = @c and is_default and active"
            : "select ledger_id from core.ledger where tenant_id = @t and company_id = @c and ledger_id = @l and active");
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", req.CompanyId);
        if (req.LedgerId is not null) cmd.Parameters.AddWithValue("l", req.LedgerId.Value);
        object? value = await cmd.ExecuteScalarAsync(ct);
        return value is Guid g ? g : null;
    }

    private static async Task<string?> ReadGroupCurrencyAsync(TenantTransaction tx, Guid tenantId, PostJournalDocumentRequest req, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select g.group_currency from core.group_membership m " +
            "join core.company_group g on g.tenant_id = m.tenant_id and g.group_id = m.group_id " +
            "where m.tenant_id = @t and m.company_id = @c and m.valid_from <= @d and (m.valid_to is null or m.valid_to > @d)");
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", req.CompanyId);
        cmd.Parameters.AddWithValue("d", req.PostingDate);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    private static async Task<bool> IsTenantCurrencyActiveAsync(TenantTransaction tx, Guid tenantId, string currency, CancellationToken ct)
    {
        await using var cmd = tx.Command("select 1 from core.tenant_currency where tenant_id = @t and currency_code = @c and active");
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", currency);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task<int?> ReadMinorUnitsAsync(TenantTransaction tx, string currency, CancellationToken ct)
    {
        await using var cmd = tx.Command("select minor_units from platform.ref_currency where currency_code = @c");
        cmd.Parameters.AddWithValue("c", currency);
        object? value = await cmd.ExecuteScalarAsync(ct);
        return value is null ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static async Task<Dictionary<Guid, AccountRow>> ReadAccountsAsync(
        TenantTransaction tx, Guid tenantId, PostJournalDocumentRequest req, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select account_id, account_no, active, valid_from, valid_to, posting_blocked, open_item_mode, tax_code_rule, currency_code " +
            "from core.gl_account where tenant_id = @t and company_id = @c and account_id = any(@ids)");
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", req.CompanyId);
        cmd.Parameters.AddWithValue("ids", req.Lines.Select(l => l.AccountId).Distinct().ToArray());
        var result = new Dictionary<Guid, AccountRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            result[r.GetGuid(0)] = new AccountRow(r.GetString(1), r.GetBoolean(2), r.GetFieldValue<DateOnly>(3),
                r.IsDBNull(4) ? null : r.GetFieldValue<DateOnly>(4), r.GetBoolean(5), r.GetString(6), r.GetString(7),
                r.IsDBNull(8) ? null : r.GetString(8));
        return result;
    }

    /// <summary>04, 5.2: the assignment of the ledger, else the one without a ledger, effective at the posting date.
    /// The order "ledger first" is an assumption of this slice, finding F-IMPL-8.</summary>
    internal static async Task<SeriesRow?> ReadSeriesAsync(
        TenantTransaction tx, Guid tenantId, Guid companyId, string documentType, DateOnly postingDate, Guid ledgerId, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select s.series_id, s.series_code, s.prefix, s.include_fiscal_year, s.separator, s.number_length " +
            "from core.number_series_assignment a " +
            "join core.number_series s on s.tenant_id = a.tenant_id and s.company_id = a.company_id and s.series_id = a.series_id " +
            "where a.tenant_id = @t and a.company_id = @c and a.document_type = @dt and a.effective_from <= @d " +
            "and (a.ledger_id = @l or a.ledger_id is null) " +
            "order by (a.ledger_id is not null) desc, a.effective_from desc limit 1");
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", companyId);
        cmd.Parameters.AddWithValue("dt", documentType);
        cmd.Parameters.AddWithValue("d", postingDate);
        cmd.Parameters.AddWithValue("l", ledgerId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new SeriesRow(r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetBoolean(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.GetInt16(5))
            : null;
    }

    private static async Task<int> ReadFallbackDaysAsync(TenantTransaction tx, Guid tenantId, CancellationToken ct)
    {
        await using var cmd = tx.Command("select rate_fallback_days from core.tenant_setting where tenant_id = @t");
        cmd.Parameters.AddWithValue("t", tenantId);
        object? value = await cmd.ExecuteScalarAsync(ct);
        return value is null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static async Task<string?> ReadPivotAsync(TenantTransaction tx, Guid tenantId, CancellationToken ct)
    {
        await using var cmd = tx.Command("select pivot_currency from core.tenant_setting where tenant_id = @t");
        cmd.Parameters.AddWithValue("t", tenantId);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    // ---------------------------------------------------------------------------------------------------------
    // P3 and the check under the locks (04, 7.1)
    // ---------------------------------------------------------------------------------------------------------
    internal static async Task<PeriodCheck> CheckPeriodAsync(
        TenantTransaction tx, Guid tenantId, Guid companyId, DateOnly postingDate, CommandContext ctx, bool lockShare, CancellationToken ct)
    {
        var errors = new List<CommandError>();
        var warnings = new List<CommandError>();

        await using (var cmd = tx.Command(
            "select earliest_posting_date, latest_posting_date from core.posting_window " +
            "where tenant_id = @t and company_id = @c and effective_from <= now() order by effective_from desc limit 1"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", companyId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                DateOnly from = r.GetFieldValue<DateOnly>(0), to = r.GetFieldValue<DateOnly>(1);
                if (postingDate < from || postingDate > to)
                    errors.Add(Error("POSTING.OUTSIDE_WINDOW", "P3", "posting_date", ("earliest", from), ("latest", to)));
            }
        }

        string sql =
            "select p.fiscal_year, p.period_no, p.status, y.status, y.is_history " +
            "from core.fiscal_period p join core.fiscal_year y " +
            "on y.tenant_id = p.tenant_id and y.company_id = p.company_id and y.fiscal_year = p.fiscal_year " +
            "where p.tenant_id = @t and p.company_id = @c and p.period_type = 'REGULAR' and p.start_date <= @d and p.end_date >= @d" +
            (lockShare ? " order by p.fiscal_year, p.period_no for share of p, y" : "");
        await using (var cmd = tx.Command(sql))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", companyId);
            cmd.Parameters.AddWithValue("d", postingDate);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
            {
                errors.Add(Error("PERIOD.NOT_FOUND", "P3", "posting_date", ("posting_date", postingDate)));
                return new PeriodCheck(null, null, false, errors, warnings);
            }
            int year = r.GetInt32(0), no = r.GetInt32(1);
            string periodStatus = r.GetString(2), yearStatus = r.GetString(3);
            bool history = r.GetBoolean(4);

            bool soft = false;
            if (history)
                errors.Add(Error("PERIOD.HISTORY_YEAR", "P3", "posting_date", ("fiscal_year", year)));
            else if (yearStatus != "OPEN")
                errors.Add(Error("PERIOD.YEAR_CLOSED", "P3", "posting_date", ("fiscal_year", year)));
            else if (periodStatus == "HARD_CLOSED")
                errors.Add(Error("PERIOD.HARD_CLOSED", "P3", "posting_date", ("fiscal_year", year), ("period", no)));
            else if (periodStatus == "SOFT_CLOSED")
            {
                if (!ctx.Permissions.Contains(SoftClosedPermission))
                    errors.Add(Error("PERIOD.SOFT_CLOSED_NOT_AUTHORISED", "P3", "posting_date", ("fiscal_year", year), ("period", no)));
                else
                {
                    soft = true;
                    warnings.Add(Error("POSTING.SOFT_CLOSE_OVERRIDE", "P3", "posting_date", ("fiscal_year", year), ("period", no)) with { Severity = "WARNING" });
                }
            }
            return new PeriodCheck(year, no, soft, errors, warnings);
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Rates (07, 7.2): direct with fallback, inverse, cross through the pivot
    // ---------------------------------------------------------------------------------------------------------
    private static async Task<Rate> LookupRateAsync(
        TenantTransaction tx, Guid tenantId, string from, string to, DateOnly date, int fallbackDays, string? pivot,
        List<Guid> used, List<CommandError> warnings, List<CommandError> errors, string scope, CancellationToken ct)
    {
        if (from == to) return new Rate(1m);

        decimal? direct = await DirectOrInverseAsync(tx, tenantId, from, to, date, fallbackDays, used, warnings, ct);
        if (direct is not null) return new Rate(direct.Value);

        if (pivot is not null && pivot != from && pivot != to)
        {
            decimal? a = await DirectOrInverseAsync(tx, tenantId, from, pivot, date, fallbackDays, used, warnings, ct);
            decimal? b = a is null ? null : await DirectOrInverseAsync(tx, tenantId, pivot, to, date, fallbackDays, used, warnings, ct);
            if (a is not null && b is not null)
                return new Rate(Math.Round(a.Value * b.Value, Canonical.RateScale, MidpointRounding.AwayFromZero));
        }
        errors.Add(Error("RATE.RATE_NOT_FOUND", "P4", "transaction_currency",
            ("from", from), ("to", to), ("rate_type", "SPOT"), ("date", date), ("scope", scope)));
        return new Rate(0m);
    }

    private static async Task<decimal?> DirectOrInverseAsync(
        TenantTransaction tx, Guid tenantId, string from, string to, DateOnly date, int fallbackDays,
        List<Guid> used, List<CommandError> warnings, CancellationToken ct)
    {
        foreach (bool inverse in new[] { false, true })
        {
            await using var cmd = tx.Command(
                "select rate_id, rate, valid_from from core.exchange_rate " +
                "where tenant_id = @t and rate_type = 'SPOT' and from_currency = @f and to_currency = @to and valid_from <= @d " +
                "order by valid_from desc limit 1");
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("f", inverse ? to : from);
            cmd.Parameters.AddWithValue("to", inverse ? from : to);
            cmd.Parameters.AddWithValue("d", date);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) continue;
            DateOnly validFrom = r.GetFieldValue<DateOnly>(2);
            if (date.DayNumber - validFrom.DayNumber > fallbackDays) continue;   // older than the fallback window of ADR-019
            used.Add(r.GetGuid(0));
            if (validFrom != date)
                warnings.Add(Error("RATE.FALLBACK_USED", "P4", "", ("from", from), ("to", to), ("rate_date", validFrom)) with { Severity = "WARNING" });
            decimal rate = r.GetDecimal(1);
            return inverse ? Math.Round(1m / rate, Canonical.RateScale, MidpointRounding.AwayFromZero) : rate;
        }
        return null;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Writes
    // ---------------------------------------------------------------------------------------------------------
    private static async Task InsertDocumentAsync(
        TenantTransaction tx, Guid tenantId, PostJournalDocumentRequest req, CommandContext ctx, Guid documentId, Guid ledgerId,
        SeriesRow series, int fiscalYear, int period, long sequenceNo, string number, bool softOverride, DateTime postedAt,
        long chainSeq, byte[] prevHash, byte[] rowHash, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "insert into core.journal_document (tenant_id, company_id, document_id, ledger_id, document_type, is_system_generated, " +
            "number_series_id, fiscal_year, sequence_no, document_number, fiscal_period, soft_close_override, document_date, posting_date, " +
            "posted_at, transaction_currency, origin, source_system, source_document_ref, idempotency_key, command_name, posted_by, " +
            "correlation_id, header_text, external_reference, chain_seq, prev_hash, row_hash, hash_version) " +
            "values (@t, @c, @id, @l, @dt, false, @s, @fy, @seq, @num, @fp, @soft, @dd, @pd, @at, @tc, 'EXTERNAL', @src, @ref, @key, @cmd, " +
            "@by, @corr, @ht, @ext, @cs, @prev, @row, @hv)");
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", req.CompanyId);
        cmd.Parameters.AddWithValue("id", documentId);
        cmd.Parameters.AddWithValue("l", ledgerId);
        cmd.Parameters.AddWithValue("dt", req.DocumentType);
        cmd.Parameters.AddWithValue("s", series.SeriesId);
        cmd.Parameters.AddWithValue("fy", fiscalYear);
        cmd.Parameters.AddWithValue("seq", sequenceNo);
        cmd.Parameters.AddWithValue("num", number);
        cmd.Parameters.AddWithValue("fp", period);
        cmd.Parameters.AddWithValue("soft", softOverride);
        cmd.Parameters.AddWithValue("dd", req.DocumentDate);
        cmd.Parameters.AddWithValue("pd", req.PostingDate);
        cmd.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = postedAt });
        cmd.Parameters.AddWithValue("tc", req.TransactionCurrency);
        cmd.Parameters.AddWithValue("src", ctx.SourceSystem);
        cmd.Parameters.AddWithValue("ref", (object?)req.SourceDocumentRef ?? DBNull.Value);
        cmd.Parameters.AddWithValue("key", ctx.IdempotencyKey);
        cmd.Parameters.AddWithValue("cmd", CommandName);
        cmd.Parameters.AddWithValue("by", ctx.Actor);
        cmd.Parameters.AddWithValue("corr", ctx.CorrelationId);
        cmd.Parameters.AddWithValue("ht", (object?)req.HeaderText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("ext", (object?)req.ExternalReference ?? DBNull.Value);
        cmd.Parameters.AddWithValue("cs", chainSeq);
        cmd.Parameters.AddWithValue("prev", prevHash);
        cmd.Parameters.AddWithValue("row", rowHash);
        cmd.Parameters.AddWithValue("hv", (short)LedgerHash2.Version);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertLineAsync(
        TenantTransaction tx, Guid tenantId, PostJournalDocumentRequest req, Guid documentId, Guid ledgerId,
        int fiscalYear, int period, string? lineText, PostedLine l, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "insert into core.journal_line (tenant_id, company_id, document_id, line_no, ledger_id, line_type, account_id, posting_date, " +
            "fiscal_year, fiscal_period, line_text, amount_tc, amount_lc, currency_lc, rate_lc, amount_gc, currency_gc, rate_gc, " +
            "amount_rc, currency_rc, rate_rc, rate_type, rate_date) " +
            "values (@t, @c, @d, @n, @l, @lt, @a, @pd, @fy, @fp, @txt, @tc, @lc, @clc, @rlc, @gc, @cgc, @rgc, @rc, @crc, @rrc, @rt, @rd)");
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", req.CompanyId);
        cmd.Parameters.AddWithValue("d", documentId);
        cmd.Parameters.AddWithValue("n", l.LineNo);
        cmd.Parameters.AddWithValue("l", ledgerId);
        cmd.Parameters.AddWithValue("lt", l.LineType);
        cmd.Parameters.AddWithValue("a", l.AccountId);
        cmd.Parameters.AddWithValue("pd", req.PostingDate);
        cmd.Parameters.AddWithValue("fy", fiscalYear);
        cmd.Parameters.AddWithValue("fp", period);
        cmd.Parameters.AddWithValue("txt", (object?)lineText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("tc", l.AmountTc);
        cmd.Parameters.AddWithValue("lc", l.AmountLc);
        cmd.Parameters.AddWithValue("clc", l.CurrencyLc);
        cmd.Parameters.AddWithValue("rlc", l.RateLc);
        cmd.Parameters.AddWithValue("gc", l.AmountGc);
        cmd.Parameters.AddWithValue("cgc", l.CurrencyGc);
        cmd.Parameters.AddWithValue("rgc", l.RateGc);
        cmd.Parameters.Add(new NpgsqlParameter("rc", NpgsqlDbType.Numeric) { Value = (object?)l.AmountRc ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("crc", NpgsqlDbType.Text) { Value = (object?)l.CurrencyRc ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("rrc", NpgsqlDbType.Numeric) { Value = (object?)l.RateRc ?? DBNull.Value });
        cmd.Parameters.AddWithValue("rt", l.RateType);
        cmd.Parameters.AddWithValue("rd", l.RateDate);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Replay: the stored result of an earlier command with the same key and the same request
    // ---------------------------------------------------------------------------------------------------------
    private static async Task<PostedDocument?> LoadReplayAsync(TenantTransaction tx, Guid tenantId, Guid logId, CancellationToken ct)
    {
        Guid documentId;
        await using (var cmd = tx.Command(
            "select entity_id from core.command_log_result where tenant_id = @t and command_log_id = @id and entity_type = 'DOCUMENT' order by result_seq limit 1"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("id", logId);
            if (await cmd.ExecuteScalarAsync(ct) is not Guid g) return null;
            documentId = g;
        }
        return await LedgerReader.LoadDocumentAsync(tx, tenantId, documentId, ct);
    }

    // ---------------------------------------------------------------------------------------------------------
    private static CommandResult<PostedDocument> Reject(CommandError error) =>
        CommandResult<PostedDocument>.Rejected([error]);

    internal static CommandError Error(string code, string phase, string path, params (string Key, object? Value)[] parameters) =>
        Error(code, phase, path, retryable: false, parameters);

    private static CommandError Error(string code, string phase, string path, bool retryable, params (string Key, object? Value)[] parameters) =>
        new(code, phase, path, parameters.Length == 0 ? null : parameters.ToDictionary(p => p.Key, p => p.Value), retryable);
}
