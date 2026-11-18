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
/// Tax lines (07, 8.4, 13.2l) are built without partial deduction and with a VAT rate type of BOOKING. Not in the slice (answered with a LIMIT.* error, never
/// silently ignored): special periods, rate overrides. Rounding differences of the derived currencies go to one
/// ROUNDING line on the account of the role ROUNDING (07, 6.3).
/// One command is one transaction. Everything that can be checked is checked before the first lock (P1 to P6),
/// the locks come in the order of 06, 9: number counter, ledger chain head, period rows, outbox head.
/// </summary>
public sealed class JournalDocumentPoster(NpgsqlDataSource source)
{
    public const string CommandName = "journal-documents:post";
    public const string PostPermission = "journal.post";
    public const string SoftClosedPermission = "post.soft_closed";

    private static readonly HashSet<string> ExternalTypes = ["JOURNAL", "AP_INVOICE", "AP_CREDIT", "AR_INVOICE", "AR_CREDIT", "PAYMENT"];
    private static readonly HashSet<string> SalesTypes = ["AR_INVOICE", "AR_CREDIT"];
    private static readonly HashSet<string> PurchaseTypes = ["AP_INVOICE", "AP_CREDIT"];

    public async Task<CommandResult<PostedDocument>> PostAsync(
        Guid tenantId, CommandContext ctx, PostJournalDocumentRequest req, CancellationToken ct = default)
    {
        // P0
        if (!ctx.Permissions.Contains(PostPermission))
            return Reject(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", PostPermission)));

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
                if (l.Dimensions is { } dims)
                {
                    foreach (var (column, _) in DimensionChecks.Typed)
                        if (DimensionChecks.TypedValue(dims, column) == Guid.Empty) Invalid($"lines[{i}].dimensions.{column}", "an id, or absent");
                    for (int j = 0; j < (dims.Free?.Count ?? 0); j++)
                    {
                        if (dims.Free![j].DimensionTypeId == Guid.Empty) Invalid($"lines[{i}].dimensions.free[{j}].dimension_type_id", "required");
                        if (dims.Free![j].DimensionValueId == Guid.Empty) Invalid($"lines[{i}].dimensions.free[{j}].dimension_value_id", "required");
                    }
                }
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
        else if ((SalesTypes.Contains(req.DocumentType) || PurchaseTypes.Contains(req.DocumentType)) && req.PartnerId is null)
            errors.Add(Error("SCHEMA.REQUIRED_FIELD_MISSING", "P1", "partner_id", ("document_type", req.DocumentType)));
        if (req.PartnerId is { } headerPartner)
            await CheckHeaderPartnerAsync(tx, tenantId, req, headerPartner, errors, ct);

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
            ValidateOpenItem(errors, i, a, req.Lines[i].OpenItem, scales.GetValueOrDefault(req.TransactionCurrency, 4));
            // the open item of a control account belongs to the partner of the document (F-IMPL-28)
            if (a.OpenItemMode == "CONTROL" && req.PartnerId is { } headerPartnerId && req.Lines[i].OpenItem?.PartnerId is { } linePartner && linePartner != headerPartnerId)
                errors.Add(Error("ACCOUNT_RULE.PARTNER_MISMATCH", "P4", $"lines[{i}].open_item/partner_id", ("account_no", a.AccountNo), ("partner_id", linePartner), ("header_partner_id", headerPartnerId)));
            if (a.TaxAccount) errors.Add(Error("ACCOUNT_RULE.TAX_ACCOUNT_DIRECT_POSTING", "P4", path, ("account_no", a.AccountNo)));
            if (a.CurrencyCode is not null) errors.Add(Error("LIMIT.ACCOUNT_CURRENCY", "P2", path, ("account_no", a.AccountNo)));
        }

        errors.AddRange(await DimensionChecks.CheckPostingAsync(tx, tenantId, req.CompanyId, req.PostingDate, req.Lines, ct));

        Dictionary<int, RelationDefaults> openItemDefaults = await CheckOpenItemReferencesAsync(tx, tenantId, req, accounts, errors, ct);

        SeriesRow? series = ledgerId is null ? null : await ReadSeriesAsync(tx, tenantId, req.CompanyId, req.DocumentType, req.PostingDate, ledgerId.Value, ct);
        if (ledgerId is not null && series is null)
            errors.Add(Error("SERIES.NO_ASSIGNMENT", "P2", "document_type",
                ("document_type", req.DocumentType), ("ledger_id", ledgerId), ("posting_date", req.PostingDate)));

        // P3: posting window, period, year, status (04, 7.1)
        PeriodCheck period = await CheckPeriodAsync(tx, tenantId, req.CompanyId, req.PostingDate, ctx, lockShare: false, ct);
        errors.AddRange(period.Errors);
        warnings.AddRange(period.Warnings);

        // P4: the tax of the base lines (07, 8.4): the code of each, the version in force, the tax per line or per document, the reverse-charge pair
        TaxPlan taxPlan = TaxPlan.None;
        Guid? taxRoundingAccount = null;
        bool taxFailed = false;
        if (errors.Count == 0)
        {
            taxPlan = await TaxPlanner.PlanAsync(tx, tenantId, req, company, accounts, scales.GetValueOrDefault(req.TransactionCurrency, 4), company.ReportingCurrency, errors, warnings, ct);
            if (errors.Count == 0 && taxPlan.RoundingTc != 0m)
            {
                taxRoundingAccount = await AccountRoleResolver.ResolveAsync(tx, tenantId, req.CompanyId, "TAX_ROUNDING", null, null, null, ct);
                if (taxRoundingAccount is null)
                    errors.Add(Error("BALANCE.ROUNDING_ACCOUNT_MISSING", "P6", "lines", ("role_code", "TAX_ROUNDING"), ("difference_tc", taxPlan.RoundingTc)));
                else
                {
                    AccountRow? a = (await ReadAccountRowsAsync(tx, tenantId, req.CompanyId, [taxRoundingAccount.Value], ct)).GetValueOrDefault(taxRoundingAccount.Value);
                    if (a is null || !a.Active || a.PostingBlocked || a.ValidFrom > req.PostingDate || (a.ValidTo is { } to && to < req.PostingDate))
                        errors.Add(Error("ACCOUNT.ROLE_ACCOUNT_NOT_USABLE", "P6", "lines", ("role_code", "TAX_ROUNDING"), ("account_id", taxRoundingAccount)));
                }
            }
            taxFailed = errors.Count > 0;
        }

        // P6 (TC balance) is cheap and independent, so it is reported with the other errors.
        decimal sumTc = req.Lines.Sum(l => l.AmountTc) + taxPlan.Lines.Sum(t => t.TaxTc + t.Nondeductible.Sum(n => n.Tc)) + taxPlan.RoundingTc;
        // a document whose tax could not be worked out has no balance to speak of
        if (sumTc != 0m && !taxFailed) errors.Add(Error("BALANCE.UNBALANCED_TC", "P6", "lines", ("currency", "TC"), ("difference", sumTc)));

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

        // the generated tax lines and the tax rounding line are derived like the base lines, from their transaction currency amount
        var extras = new List<(string Type, Guid Account, decimal Tc, Guid? Code, int? Seed, decimal Lc, decimal Gc, decimal? Rc, int? BaseIdx)>();
        void AddExtra(string type, Guid account, decimal tc, Guid? code, int? seed, int? baseIdx) => extras.Add((type, account, tc, code, seed,
            PostingRules.Round(tc * rateLc.Value, scales[company.LocalCurrency]), PostingRules.Round(tc * rateGc.Value, scales[groupCurrency!]),
            rateRc is null ? null : PostingRules.Round(tc * rateRc.Value, scales[company.ReportingCurrency!]), baseIdx));
        for (int sd = 0; sd < taxPlan.Lines.Count; sd++) AddExtra("TAX", taxPlan.Lines[sd].AccountId, taxPlan.Lines[sd].TaxTc, taxPlan.Lines[sd].CodeId, sd, null);
        // the non-deductible part of an input tax is cost on the account of the base line it belongs to (19, 4.1)
        for (int sd = 0; sd < taxPlan.Lines.Count; sd++)
            foreach (var (index, tc) in taxPlan.Lines[sd].Nondeductible) AddExtra("TAX_NONDEDUCTIBLE", req.Lines[index].AccountId, tc, null, sd, index);
        if (taxPlan.RoundingTc != 0m) AddExtra("TAX_ROUNDING", taxRoundingAccount!.Value, taxPlan.RoundingTc, null, null, null);

        // P5/P6: the derived currencies balance with one ROUNDING line (design principle 4 of 00) on the account of the role
        // ROUNDING for the transaction currency, else the default row. Its amounts are the differences, TC is 0 (finding F-IMPL-16).
        decimal diffLc = -(derived.Sum(d => d.Lc) + extras.Sum(e => e.Lc)), diffGc = -(derived.Sum(d => d.Gc) + extras.Sum(e => e.Gc));
        decimal? diffRc = rateRc is null ? null : -(derived.Sum(d => d.Rc!.Value) + extras.Sum(e => e.Rc!.Value));
        Guid? roundingAccount = null;
        if (diffLc != 0m || diffGc != 0m || diffRc is not null and not 0m)
        {
            roundingAccount = await AccountRoleResolver.ResolveAsync(tx, tenantId, req.CompanyId, "ROUNDING", req.TransactionCurrency, null, null, ct);
            if (roundingAccount is null)
                errors.Add(Error("BALANCE.ROUNDING_ACCOUNT_MISSING", "P6", "lines", ("role_code", "ROUNDING"),
                    ("currency", req.TransactionCurrency), ("difference_lc", diffLc), ("difference_gc", diffGc), ("difference_rc", diffRc)));
            else
            {
                AccountRow? a = (await ReadAccountRowsAsync(tx, tenantId, req.CompanyId, [roundingAccount.Value], ct)).GetValueOrDefault(roundingAccount.Value);
                if (a is null || !a.Active || a.PostingBlocked || a.ValidFrom > req.PostingDate || (a.ValidTo is { } to && to < req.PostingDate))
                    errors.Add(Error("ACCOUNT.ROLE_ACCOUNT_NOT_USABLE", "P6", "lines", ("role_code", "ROUNDING"), ("account_id", roundingAccount)));
            }
        }
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

        // Base lines first, then the generated lines (06, A2)
        var lines = new List<PostedLine>();
        for (int i = 0; i < derived.Count; i++)
        {
            var d = derived[i];
            lines.Add(new PostedLine(i + 1, d.Line.LineRef, d.Line.AccountId, "BASE",
                d.Line.AmountTc, d.Lc, company.LocalCurrency, rateLc.Value,
                d.Gc, groupCurrency!, rateGc.Value,
                d.Rc, company.ReportingCurrency, rateRc?.Value,
                "SPOT", req.PostingDate, taxPlan.BaseCodes.TryGetValue(i, out Guid baseCode) ? baseCode : null));
        }
        var taxLineNos = new int[taxPlan.Lines.Count];
        var baseOfLine = new Dictionary<int, int>();                      // a base line, and the non-deductible line that belongs to it, by line number
        var nondeductibleLineNo = new Dictionary<(int Seed, int BaseIdx), int>();
        for (int i = 0; i < req.Lines.Count; i++) baseOfLine[i + 1] = i;
        foreach (var e in extras)
        {
            int lineNo = lines.Count + 1;
            if (e.Type == "TAX" && e.Seed is { } sd) taxLineNos[sd] = lineNo;
            if (e.Type == "TAX_NONDEDUCTIBLE") { baseOfLine[lineNo] = e.BaseIdx!.Value; nondeductibleLineNo[(e.Seed!.Value, e.BaseIdx.Value)] = lineNo; }
            lines.Add(new PostedLine(lineNo, null, e.Account, e.Type, e.Tc, e.Lc, company.LocalCurrency, rateLc.Value, e.Gc, groupCurrency!, rateGc.Value,
                e.Rc, company.ReportingCurrency, rateRc?.Value, "SPOT", req.PostingDate, e.Code));
        }
        if (roundingAccount is not null)
            // A DERIVED line (02, 5.2, D9): TC 0 and rate 1 in every currency, the amounts are authoritative
            lines.Add(new PostedLine(lines.Count + 1, null, roundingAccount.Value, "ROUNDING",
                0m, diffLc, company.LocalCurrency, 1m,
                diffGc, groupCurrency!, 1m,
                diffRc, company.ReportingCurrency, rateRc is null ? null : 1m,
                "DERIVED", req.PostingDate));

        // the tax rows of the tax lines: the base amounts in the other currencies are the sums of the derived amounts of the base lines
        var taxRows = new Dictionary<int, (Dictionary<string, object?> Row, List<Dictionary<string, object?>> Bases)>();
        for (int sd = 0; sd < taxPlan.Lines.Count; sd++)
        {
            TaxLineSeed t = taxPlan.Lines[sd];
            PostedLine tl = lines[taxLineNos[sd] - 1];
            decimal baseLc = t.Bases.Sum(b => derived[b.Index].Lc), baseGc = t.Bases.Sum(b => derived[b.Index].Gc);
            decimal? baseRc = rateRc is null ? null : t.Bases.Sum(b => derived[b.Index].Rc!.Value);
            decimal baseTc = t.Bases.Sum(b => b.BaseTc);
            bool inLocal = t.VatCurrency == company.LocalCurrency;
            var row = new Dictionary<string, object?>
            {
                ["tenant_id"] = tenantId, ["company_id"] = req.CompanyId, ["document_id"] = documentId, ["line_no"] = tl.LineNo,
                ["tax_code_id"] = t.CodeId, ["tax_code_version_id"] = t.VersionId, ["tax_type"] = t.TaxType, ["tax_rate"] = t.Rate,
                ["base_amount_tc"] = baseTc, ["base_amount_lc"] = baseLc, ["base_amount_gc"] = baseGc, ["base_amount_rc"] = baseRc,
                ["determined_tax_code_id"] = t.DeterminedCodeId, ["tax_class_id"] = t.ClassId, ["origin_country"] = t.Origin, ["destination_country"] = t.Destination,
                ["partner_vat_status"] = t.PartnerVatStatus, ["tax_date"] = t.TaxDate, ["vat_currency"] = t.VatCurrency,
                ["vat_base_amount"] = inLocal ? baseLc : baseRc!.Value, ["vat_tax_amount"] = inLocal ? tl.AmountLc : tl.AmountRc!.Value,
                ["vat_rate"] = inLocal ? rateLc.Value : rateRc!.Value, ["vat_rate_date"] = null, ["is_override"] = t.IsOverride,
                ["tax_amount_supplied_tc"] = t.SuppliedTc, ["rounding_mode"] = t.RoundingMode, ["origin_eu"] = t.OriginEu, ["destination_eu"] = t.DestinationEu,
                ["deductible_pct"] = t.DeductiblePct, ["deduction_key"] = t.DeductionKey, ["deduction_key_rate"] = t.KeyRate, ["deduction_share"] = t.Share,
                ["deduction_cap_amount_tc"] = t.CapAmountTc, ["gross_tax_amount_tc"] = t.GrossTc
            };
            var bases = t.Bases.Select(b => new Dictionary<string, object?>
            {
                ["tenant_id"] = tenantId, ["company_id"] = req.CompanyId, ["document_id"] = documentId, ["tax_line_no"] = tl.LineNo, ["base_line_no"] = b.Index + 1,
                ["base_amount_tc"] = b.BaseTc,
                ["nondeductible_tax_tc"] = t.Nondeductible.Where(x => x.Index == b.Index).Select(x => x.Tc).FirstOrDefault(),
                ["nondeductible_line_no"] = nondeductibleLineNo.TryGetValue((sd, b.Index), out int ndLine) ? ndLine : null
            }).ToList();
            taxRows[tl.LineNo] = (row, bases);
        }

        var lineHashes = new List<byte[]>();
        foreach (PostedLine line in lines)
            lineHashes.Add(LedgerHash2.LineHash(new HashedLine(OpenItem: OpenItemHashRow(tenantId, req, documentId, line),
                Tax: taxRows.TryGetValue(line.LineNo, out var taxRow) ? taxRow.Row : null, TaxBases: taxRows.TryGetValue(line.LineNo, out var taxBase) ? taxBase.Bases : null,
                Line: new Dictionary<string, object?>
            {
                ["tenant_id"] = tenantId, ["company_id"] = req.CompanyId, ["document_id"] = documentId, ["line_no"] = line.LineNo,
                ["ledger_id"] = ledgerId.Value, ["line_type"] = line.LineType, ["account_id"] = line.AccountId,
                ["posting_date"] = req.PostingDate, ["fiscal_year"] = fiscalYear, ["fiscal_period"] = recheck.PeriodNo!.Value,
                ["reversed_line_no"] = null, ["tax_code_id"] = line.TaxCodeId,
                ["amount_tc"] = line.AmountTc, ["amount_lc"] = line.AmountLc, ["currency_lc"] = line.CurrencyLc,
                ["rate_lc"] = line.RateLc, ["rate_lc_table"] = null,
                ["amount_gc"] = line.AmountGc, ["currency_gc"] = line.CurrencyGc, ["rate_gc"] = line.RateGc, ["rate_gc_table"] = null,
                ["amount_rc"] = line.AmountRc, ["currency_rc"] = line.CurrencyRc, ["rate_rc"] = line.RateRc, ["rate_rc_table"] = null,
                ["rate_type"] = line.RateType, ["rate_date"] = line.RateDate, ["ic_counterpart_company_id"] = null
            })));
        byte[] rowHash = LedgerHash2.RowHash(header, chainSeq, headHash, LedgerHash2.LineDigest(lineHashes));

        // Insert document and lines
        await InsertDocumentAsync(tx, tenantId, req, ctx, documentId, ledgerId.Value, series, fiscalYear, recheck.PeriodNo!.Value,
            sequenceNo, number, softOverride, postedAt, chainSeq, headHash, rowHash, ct);
        if (await LegalSnapshots.WriteAsync(tx, tenantId, req.CompanyId, documentId, fiscalYear, req.PostingDate, postedAt, ct, partnerId: req.PartnerId) is { } noSnapshot)
            return Reject(noSnapshot);
        foreach (PostedLine l in lines)
            await InsertLineAsync(tx, tenantId, req, documentId, ledgerId.Value, fiscalYear, recheck.PeriodNo!.Value,
                baseOfLine.TryGetValue(l.LineNo, out int textOf) ? req.Lines[textOf].LineText : null, baseOfLine.TryGetValue(l.LineNo, out int dimsOf) ? req.Lines[dimsOf].Dimensions : null, l, ct);
        foreach (var (taxLineNo, tax) in taxRows.OrderBy(t => t.Key))
        {
            await TaxRows.InsertTaxAsync(tx, tax.Row, ct);
            foreach (var b in tax.Bases) await TaxRows.InsertBaseAsync(tx, b, ct);
        }
        foreach (PostedLine l in lines.Where(l => baseOfLine.TryGetValue(l.LineNo, out int b) && req.Lines[b].Dimensions?.Free is { Count: > 0 }))
            foreach (FreeDimension f in req.Lines[baseOfLine[l.LineNo]].Dimensions!.Free!.OrderBy(f => f.DimensionTypeId))
                await InsertFreeDimensionAsync(tx, tenantId, req.CompanyId, documentId, l.LineNo, f, ct);
        foreach (PostedLine l in lines.Where(l => l.LineType == "BASE" && req.Lines[l.LineNo - 1].OpenItem is not null))
            await InsertOpenItemAsync(tx, tenantId, req.CompanyId, documentId, l, req.Lines[l.LineNo - 1].OpenItem!, openItemDefaults.GetValueOrDefault(l.LineNo - 1), ct);

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
    internal sealed record CompanyRow(string Status, string LocalCurrency, string? ReportingCurrency, string? Country = null);
    internal sealed record AccountRow(string AccountNo, bool Active, DateOnly ValidFrom, DateOnly? ValidTo, bool PostingBlocked,
        string OpenItemMode, string TaxCodeRule, string? CurrencyCode, bool TaxAccount = false, Guid? DefaultTaxClassId = null);
    internal sealed record SeriesRow(Guid SeriesId, string SeriesCode, string? Prefix, bool IncludeFiscalYear, string? Separator, int NumberLength);
    internal sealed record Rate(decimal Value);
    internal sealed record PeriodCheck(int? FiscalYear, int? PeriodNo, bool SoftCloseOverride, List<CommandError> Errors, List<CommandError> Warnings);

    internal static async Task<CompanyRow?> ReadCompanyAsync(TenantTransaction tx, Guid tenantId, Guid companyId, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select status, local_currency, reporting_currency, country_code from core.company where tenant_id = @t and company_id = @c");
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", companyId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new CompanyRow(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3).Trim())
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

    private static Task<Dictionary<Guid, AccountRow>> ReadAccountsAsync(
        TenantTransaction tx, Guid tenantId, PostJournalDocumentRequest req, CancellationToken ct) =>
        ReadAccountRowsAsync(tx, tenantId, req.CompanyId, req.Lines.Select(l => l.AccountId).Distinct().ToArray(), ct);

    internal static async Task<Dictionary<Guid, AccountRow>> ReadAccountRowsAsync(
        TenantTransaction tx, Guid tenantId, Guid companyId, Guid[] accountIds, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select account_id, account_no, active, valid_from, valid_to, posting_blocked, open_item_mode, tax_code_rule, currency_code, tax_account, default_tax_class_id " +
            "from core.gl_account where tenant_id = @t and company_id = @c and account_id = any(@ids)");
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", companyId);
        cmd.Parameters.AddWithValue("ids", accountIds);
        var result = new Dictionary<Guid, AccountRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            result[r.GetGuid(0)] = new AccountRow(r.GetString(1), r.GetBoolean(2), r.GetFieldValue<DateOnly>(3),
                r.IsDBNull(4) ? null : r.GetFieldValue<DateOnly>(4), r.GetBoolean(5), r.GetString(6), r.GetString(7),
                r.IsDBNull(8) ? null : r.GetString(8), r.GetBoolean(9), r.IsDBNull(10) ? null : r.GetGuid(10));
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

    /// <summary>The open item rules of 02, 5A and 06, 5.2 (P4) for the accounts the slice supports. A CONTROL account needs a partner,
    /// and there is no partner table yet. The attributes that point at tables that do not exist yet are rejected, not stored unchecked.</summary>
    private static void ValidateOpenItem(List<CommandError> errors, int i, AccountRow account, OpenItemRequest? oi, int tcScale)
    {
        string at = $"lines[{i}].open_item";
        if (account.OpenItemMode == "NONE")
        {
            if (oi is not null) errors.Add(Error("ACCOUNT_RULE.OPEN_ITEM_FORBIDDEN", "P4", at, ("account_no", account.AccountNo)));
            return;
        }
        if (oi is null)
        {
            errors.Add(Error("ACCOUNT_RULE.OPEN_ITEM_REQUIRED", "P4", at, ("account_no", account.AccountNo)));
            return;
        }
        if (account.OpenItemMode == "CONTROL")
        {
            // a control account needs the partner and the due date (02, 5A, 06, 5.2)
            if (oi.PartnerId is null) errors.Add(Error("ACCOUNT_RULE.PARTNER_REQUIRED", "P4", $"{at}/partner_id", ("account_no", account.AccountNo)));
            if (oi.DueDate is null) errors.Add(Error("ACCOUNT_RULE.DUE_DATE_REQUIRED", "P4", $"{at}/due_date", ("account_no", account.AccountNo)));
        }
        else if (oi.PartnerId is not null)
            errors.Add(Error("ACCOUNT_RULE.PARTNER_FORBIDDEN", "P4", $"{at}/partner_id", ("account_no", account.AccountNo)));
        if (oi.BankAccountId is not null) errors.Add(Error("LIMIT.OPEN_ITEM_FIELD_NOT_BUILT", "P4", $"{at}/bank_account_id"));
        if (oi.DiscountAmountTc is { } d && (d < 0 || Math.Round(d, tcScale) != d))
            errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", $"{at}/discount_amount_tc", ("reason", $"not negative, at most {tcScale} fraction digits")));
        void Text(string name, string? value, int max)
        {
            if (value is not null && (value.Length == 0 || value.Length > max))
                errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", $"{at}/{name}", ("reason", $"1 to {max} characters, or absent")));
        }
        Text("payment_block", oi.PaymentBlock, 50);
        Text("dunning_block", oi.DunningBlock, 50);
        Text("payment_reference", oi.PaymentReference, 100);
    }

    /// <summary>The L1 columns of the open item row that the line hash covers (02, 5A, 9.3a). Null for a line without a row.</summary>
    private static Dictionary<string, object?>? OpenItemHashRow(Guid tenantId, PostJournalDocumentRequest req, Guid documentId, PostedLine line) =>
        line.LineType == "BASE" && req.Lines[line.LineNo - 1].OpenItem is not null
            ? new Dictionary<string, object?>
            {
                ["tenant_id"] = tenantId, ["company_id"] = req.CompanyId, ["document_id"] = documentId, ["line_no"] = line.LineNo,
                ["partner_id"] = req.Lines[line.LineNo - 1].OpenItem!.PartnerId, ["legacy_document_date"] = null, ["legacy_posting_date"] = null, ["legacy_document_ref"] = null
            }
            : null;

    /// <summary>
    /// The header partner of a document (06, 5.2): it exists, is not anonymised, blocked or restricted, and has an active relation to the company. A sales
    /// type needs a customer, a purchase type a supplier, the other types either role.
    /// </summary>
    private static async Task CheckHeaderPartnerAsync(
        TenantTransaction tx, Guid tenantId, PostJournalDocumentRequest req, Guid partner, List<CommandError> errors, CancellationToken ct)
    {
        string? status, restriction, relStatus; bool customer, supplier;
        await using (var cmd = tx.Command(
            "select p.status, p.restriction_status, r.status, coalesce(r.is_customer, false), coalesce(r.is_supplier, false) " +
            "from core.business_partner p left join core.partner_company_relation r on r.tenant_id = p.tenant_id and r.partner_id = p.partner_id and r.company_id = @c " +
            "where p.tenant_id = @t and p.partner_id = @p"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("p", partner);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) { errors.Add(Error("MASTER.PARTNER_NOT_FOUND", "P2", "partner_id", ("partner_id", partner))); return; }
            status = r.GetString(0); restriction = r.GetString(1); relStatus = r.IsDBNull(2) ? null : r.GetString(2); customer = r.GetBoolean(3); supplier = r.GetBoolean(4);
        }
        bool sales = SalesTypes.Contains(req.DocumentType), purchase = PurchaseTypes.Contains(req.DocumentType);
        if (status == "ANONYMISED") errors.Add(Error("MASTER.PARTNER_ANONYMISED", "P2", "partner_id", ("partner_id", partner)));
        else if (status == "BLOCKED") errors.Add(Error("MASTER.PARTNER_INACTIVE", "P2", "partner_id", ("partner_id", partner), ("reason", "the partner is blocked")));
        else if (restriction == "RESTRICTED") errors.Add(Error("MASTER.PARTNER_RESTRICTED", "P2", "partner_id", ("partner_id", partner)));
        else if (relStatus is null) errors.Add(Error("MASTER.PARTNER_NOT_FOUND", "P2", "partner_id", ("partner_id", partner), ("reason", "the partner has no relation to the company")));
        else if (relStatus == "BLOCKED") errors.Add(Error("MASTER.PARTNER_INACTIVE", "P2", "partner_id", ("partner_id", partner), ("reason", "the relation to the company is blocked")));
        else if (sales ? !customer : purchase ? !supplier : !(customer || supplier))
            errors.Add(Error("MASTER.PARTNER_INACTIVE", "P2", "partner_id", ("partner_id", partner),
                ("reason", sales ? "the relation is not a customer" : purchase ? "the relation is not a supplier" : "the relation is neither customer nor supplier")));
    }

    /// <summary>What a partner's relation to the company hands to a new open item: the payment block and the dunning block (07, 9.3).</summary>
    internal sealed record RelationDefaults(string? PaymentBlock, string? DunningBlock);

    /// <summary>
    /// The references of the open items that need the database (06, 5.2 P2 and P4): payment terms that exist and are active, and for a control
    /// account the partner: it exists, is not anonymised, not blocked and not restricted (a restricted partner takes no new items, 12, 5.3), and
    /// has an active relation to the company with a role. The partner is read without a lock: a block that commits later applies to the postings
    /// that start after it. Returns the defaults of the relation by line index.
    /// </summary>
    private static async Task<Dictionary<int, RelationDefaults>> CheckOpenItemReferencesAsync(
        TenantTransaction tx, Guid tenantId, PostJournalDocumentRequest req, Dictionary<Guid, AccountRow> accounts, List<CommandError> errors, CancellationToken ct)
    {
        var defaults = new Dictionary<int, RelationDefaults>();
        var partners = new Dictionary<Guid, (string? Status, string? Restriction, string? RelStatus, bool Role, string? PaymentBlock, string? DunningBlock)>();
        for (int i = 0; i < req.Lines.Count; i++)
        {
            OpenItemRequest? oi = req.Lines[i].OpenItem;
            if (oi is null) continue;
            string at = $"lines[{i}].open_item";
            if (oi.PaymentTermsId is { } terms)
            {
                await using var cmd = tx.Command("select 1 from core.payment_terms where tenant_id = @t and payment_terms_id = @p and active");
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("p", terms);
                if (await cmd.ExecuteScalarAsync(ct) is null)
                    errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", $"{at}/payment_terms_id", ("payment_terms_id", terms)));
            }
            if (oi.PartnerId is not { } partner || !accounts.TryGetValue(req.Lines[i].AccountId, out AccountRow? account) || account.OpenItemMode != "CONTROL") continue;
            if (!partners.TryGetValue(partner, out var p))
            {
                await using var cmd = tx.Command(
                    "select p.status, p.restriction_status, r.status, coalesce(r.is_customer or r.is_supplier, false), r.payment_block, r.dunning_block " +
                    "from core.business_partner p left join core.partner_company_relation r on r.tenant_id = p.tenant_id and r.partner_id = p.partner_id and r.company_id = @c " +
                    "where p.tenant_id = @t and p.partner_id = @p");
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("p", partner);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                p = await r.ReadAsync(ct)
                    ? (r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetBoolean(3), r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5))
                    : (null, null, null, false, null, null);
                partners[partner] = p;
            }
            string path = $"{at}/partner_id";
            if (p.Status is null) errors.Add(Error("MASTER.PARTNER_NOT_FOUND", "P2", path, ("partner_id", partner)));
            else if (p.Status == "ANONYMISED") errors.Add(Error("MASTER.PARTNER_ANONYMISED", "P2", path, ("partner_id", partner)));
            else if (p.Status == "BLOCKED") errors.Add(Error("MASTER.PARTNER_INACTIVE", "P2", path, ("partner_id", partner), ("reason", "the partner is blocked")));
            else if (p.Restriction == "RESTRICTED") errors.Add(Error("MASTER.PARTNER_RESTRICTED", "P2", path, ("partner_id", partner)));
            else if (p.RelStatus is null) errors.Add(Error("MASTER.PARTNER_NOT_FOUND", "P2", path, ("partner_id", partner), ("reason", "the partner has no relation to the company")));
            else if (p.RelStatus == "BLOCKED") errors.Add(Error("MASTER.PARTNER_INACTIVE", "P2", path, ("partner_id", partner), ("reason", "the relation to the company is blocked")));
            else if (!p.Role) errors.Add(Error("MASTER.PARTNER_INACTIVE", "P2", path, ("partner_id", partner), ("reason", "the relation is neither customer nor supplier")));
            else defaults[i] = new RelationDefaults(p.PaymentBlock, p.DunningBlock);
        }
        return defaults;
    }

    private static async Task InsertOpenItemAsync(
        TenantTransaction tx, Guid tenantId, Guid companyId, Guid documentId, PostedLine line, OpenItemRequest oi, RelationDefaults? defaults, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "insert into core.journal_line_open_item (tenant_id, company_id, document_id, line_no, partner_id, payment_terms_id, baseline_date, due_date, discount_due_date, " +
            "discount_amount_tc, payment_block, dunning_block, payment_reference, cleared_amount_rc) " +
            "values (@t, @c, @d, @n, @partner, @terms, @bd, @dd, @ddd, @da, @pb, @db, @pr, @rc)");
        cmd.Parameters.Add(new NpgsqlParameter("partner", NpgsqlDbType.Uuid) { Value = (object?)oi.PartnerId ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("terms", NpgsqlDbType.Uuid) { Value = (object?)oi.PaymentTermsId ?? DBNull.Value });
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", companyId);
        cmd.Parameters.AddWithValue("d", documentId);
        cmd.Parameters.AddWithValue("n", line.LineNo);
        cmd.Parameters.Add(new NpgsqlParameter("bd", NpgsqlDbType.Date) { Value = (object?)oi.BaselineDate ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("dd", NpgsqlDbType.Date) { Value = (object?)oi.DueDate ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("ddd", NpgsqlDbType.Date) { Value = (object?)oi.DiscountDueDate ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("da", NpgsqlDbType.Numeric) { Value = (object?)oi.DiscountAmountTc ?? DBNull.Value });
        // the blocks of the partner's relation are the defaults of a new open item, a block of the request wins (07, 9.3)
        cmd.Parameters.Add(new NpgsqlParameter("pb", NpgsqlDbType.Text) { Value = (object?)(oi.PaymentBlock ?? defaults?.PaymentBlock) ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("db", NpgsqlDbType.Text) { Value = (object?)(oi.DunningBlock ?? defaults?.DunningBlock) ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("pr", NpgsqlDbType.Text) { Value = (object?)oi.PaymentReference ?? DBNull.Value });
        // the cleared reporting amount is 0 when the line has a reporting amount, null when it has none (02, 5A)
        cmd.Parameters.Add(new NpgsqlParameter("rc", NpgsqlDbType.Numeric) { Value = line.AmountRc is null ? DBNull.Value : 0m });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertLineAsync(
        TenantTransaction tx, Guid tenantId, PostJournalDocumentRequest req, Guid documentId, Guid ledgerId,
        int fiscalYear, int period, string? lineText, LineDimensions? dims, PostedLine l, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "insert into core.journal_line (tenant_id, company_id, document_id, line_no, ledger_id, line_type, account_id, posting_date, " +
            "fiscal_year, fiscal_period, line_text, amount_tc, amount_lc, currency_lc, rate_lc, amount_gc, currency_gc, rate_gc, " +
            "amount_rc, currency_rc, rate_rc, rate_type, rate_date, cost_center_id, project_id, profit_center_id, business_unit_id, tax_code_id) " +
            "values (@t, @c, @d, @n, @l, @lt, @a, @pd, @fy, @fp, @txt, @tc, @lc, @clc, @rlc, @gc, @cgc, @rgc, @rc, @crc, @rrc, @rt, @rd, @cc, @pr, @pc, @bu, @tax)");
        cmd.Parameters.Add(new NpgsqlParameter("tax", NpgsqlDbType.Uuid) { Value = (object?)l.TaxCodeId ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("cc", NpgsqlDbType.Uuid) { Value = (object?)dims?.CostCenterId ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("pr", NpgsqlDbType.Uuid) { Value = (object?)dims?.ProjectId ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("pc", NpgsqlDbType.Uuid) { Value = (object?)dims?.ProfitCenterId ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("bu", NpgsqlDbType.Uuid) { Value = (object?)dims?.BusinessUnitId ?? DBNull.Value });
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

    private static async Task InsertFreeDimensionAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid documentId, int lineNo, FreeDimension f, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "insert into core.journal_line_dimension (tenant_id, company_id, document_id, line_no, dimension_type_id, dimension_value_id) values (@t, @c, @d, @n, @ty, @v)");
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", companyId);
        cmd.Parameters.AddWithValue("d", documentId);
        cmd.Parameters.AddWithValue("n", lineNo);
        cmd.Parameters.AddWithValue("ty", f.DimensionTypeId);
        cmd.Parameters.AddWithValue("v", f.DimensionValueId);
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
