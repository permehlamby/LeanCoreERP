using System.Globalization;
using System.Text.Json;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Hashing;
using FinanceCore.Core.Posting;
using Npgsql;
using NpgsqlTypes;

namespace FinanceCore.Data;

/// <summary>
/// clearings:post (06, 7.1, 03, 5): open items of one GL_OPEN_ITEM account, cleared in full or in part against each other. A set whose
/// local, group or reporting amounts do not net to zero (realised FX, 03, 3.1) gets a difference document of type CLEARING with the
/// two lines FX_REALISED and CLEARING_DIFF, posted in the same transaction; the offset line is an open item that is created already
/// cleared, by this clearing. A difference in the transaction currency of a set in one currency is rejected (Q1). Items on CONTROL
/// accounts need a partner and wait for business_partner.
/// Locks (04, 6.3, 03, 5.6): the number counter of the CLEARING series and the LEDGER head first when a difference document can be
/// needed (the pre-lock), then the CLEARING head, the period row in share mode (A1: the status is read again after the head locks), the
/// open items sorted by key, outbox_head last. The amounts are derived after the locks, from the locked rows. If they show that a
/// difference document is needed although nothing was pre-locked, the transaction is rolled back and starts again with the pre-lock.
/// </summary>
public sealed partial class ClearingPoster(NpgsqlDataSource source)
{
    public const string Permission = "clearing.post";
    public const string DifferenceType = "CLEARING";
    public const int MaxItems = 500;

    /// <summary>For tests: never pre-lock at the first attempt, so that a difference found after the locks takes the restart path of 03, 5.6.</summary>
    public bool DisablePreLock { get; init; }

    private sealed class NeedsPreLock : Exception;

    public async Task<CommandResult<PostedClearing>> PostAsync(
        Guid tenantId, CommandContext ctx, PostClearingRequest req, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(Permission))
            return Reject(JournalDocumentPoster.Error("AUTH.PERMISSION_DENIED", "P0", "", ("permission", Permission)));

        List<CommandError> shape = Validate(ctx, req);
        if (shape.Count > 0) return CommandResult<PostedClearing>.Rejected(shape);

        byte[] fingerprint = ClearingRules.Fingerprint(req);
        bool preLock = false;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await PostOnceAsync(tenantId, ctx, req, fingerprint, preLock, ct);
            }
            catch (NeedsPreLock) when (!preLock)
            {
                preLock = true;   // the second attempt takes the counter and the LEDGER head before anything else (03, 5.6)
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.TableName == "command_log" && attempt < 2)
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

    // ---------------------------------------------------------------------------------------------------------
    // P1: the shape of the request, without the database
    // ---------------------------------------------------------------------------------------------------------
    internal static List<CommandError> Validate(CommandContext ctx, PostClearingRequest req)
    {
        var errors = new List<CommandError>();
        void Invalid(string path, string reason) => errors.Add(JournalDocumentPoster.Error("SCHEMA.INVALID_VALUE", "P1", path, ("reason", reason)));
        if (string.IsNullOrWhiteSpace(ctx.IdempotencyKey) || ctx.IdempotencyKey.Length > 100) Invalid("idempotency_key", "required, at most 100 characters");
        if (string.IsNullOrWhiteSpace(ctx.CorrelationId) || ctx.CorrelationId.Length > 100) Invalid("correlation_id", "required, at most 100 characters");
        if (string.IsNullOrWhiteSpace(ctx.Actor) || ctx.Actor.Length > 100) Invalid("actor", "required, at most 100 characters");
        if (string.IsNullOrWhiteSpace(ctx.SourceSystem) || ctx.SourceSystem.Length > 50) Invalid("source_system", "required, at most 50 characters");
        if (req.CompanyId == Guid.Empty) Invalid("company_id", "required");
        if (req.AccountId == Guid.Empty) Invalid("account_id", "required");
        if (!ClearingRules.ExternalMethods.Contains(req.ClearingMethod))
            errors.Add(JournalDocumentPoster.Error("SCHEMA.INVALID_VALUE", "P1", "clearing_method", ("allowed_values", ClearingRules.ExternalMethods)));
        if (req.MatchingRef is { } m && (m.Length == 0 || m.Length > 100)) Invalid("matching_ref", "1 to 100 characters, or absent");
        if (req.Note is { } n && (n.Length == 0 || n.Length > 200)) Invalid("note", "1 to 200 characters, or absent");

        if (req.Items is null || req.Items.Count < 2)
        {
            errors.Add(JournalDocumentPoster.Error("SCHEMA.INVALID_VALUE", "P1", "items", ("reason", "a clearing has at least two items")));
            return errors;
        }
        if (req.Items.Count > MaxItems)
        {
            errors.Add(JournalDocumentPoster.Error("SCHEMA.BATCH_TOO_LARGE", "P1", "items", ("limit", MaxItems)));
            return errors;
        }
        var seen = new HashSet<(Guid, int)>();
        for (int i = 0; i < req.Items.Count; i++)
        {
            ClearingItemRequest it = req.Items[i];
            if (it.DocumentId == Guid.Empty) Invalid($"items[{i}].open_item_document_id", "required");
            if (it.LineNo < 1) Invalid($"items[{i}].open_item_line_no", "at least 1");
            if (!seen.Add((it.DocumentId, it.LineNo))) Invalid($"items[{i}]", "the same open item twice in one clearing");
            if (it.ClearedAmountTc is { } a && Math.Round(a, Canonical.AmountScale) != a)
                Invalid($"items[{i}].cleared_amount_tc", $"at most {Canonical.AmountScale} fraction digits");
        }
        return errors;
    }

    // ---------------------------------------------------------------------------------------------------------
    private sealed record Locked(
        Guid CompanyId, Guid DocumentId, int LineNo, Guid? PartnerId, string Status, decimal ClearedTc, decimal ClearedLc, decimal ClearedGc, decimal? ClearedRc,
        Guid AccountId, decimal LineTc, decimal LineLc, decimal LineGc, decimal? LineRc, string CurrencyLc, string CurrencyGc, string? CurrencyRc, string CurrencyTc);

    private async Task<CommandResult<PostedClearing>> PostOnceAsync(
        Guid tenantId, CommandContext ctx, PostClearingRequest req, byte[] fingerprint, bool preLock, CancellationToken ct)
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

        // P2: company and account (no lock: the locks below protect what the amounts depend on)
        var errors = new List<CommandError>();
        JournalDocumentPoster.CompanyRow? company = await JournalDocumentPoster.ReadCompanyAsync(tx, tenantId, req.CompanyId, ct);
        if (company is null) return Reject(JournalDocumentPoster.Error("COMPANY.NOT_FOUND", "P2", "company_id"));
        if (company.Status != "ACTIVE") errors.Add(JournalDocumentPoster.Error("COMPANY.NOT_ACTIVE", "P2", "company_id", ("status", company.Status)));
        string? mode = await ReadOpenItemModeAsync(tx, tenantId, req.CompanyId, req.AccountId, ct);
        if (mode is null) errors.Add(JournalDocumentPoster.Error("ACCOUNT.NOT_FOUND", "P2", "account_id", ("account_id", req.AccountId)));
        else if (mode == "NONE") errors.Add(JournalDocumentPoster.Error("CLEARING.NOT_AN_OPEN_ITEM", "P2", "account_id", ("reason", "the account is not open-item managed")));
        else if (mode == "CONTROL") errors.Add(JournalDocumentPoster.Error("LIMIT.CONTROL_ACCOUNT_NEEDS_PARTNER", "P2", "account_id"));
        if (req.PartnerId is not null)
            errors.Add(JournalDocumentPoster.Error("ACCOUNT_RULE.PARTNER_FORBIDDEN", "P2", "partner_id", ("reason", "a GL_OPEN_ITEM account takes no partner")));
        if (errors.Count > 0) return CommandResult<PostedClearing>.Rejected(errors);

        // The pre-lock (03, 5.6): a difference document can be needed when the items do not net to zero at their full booked amounts,
        // or when any of them is partly cleared or is to be cleared in part. The read is without locks and only decides how to lock.
        bool pre = preLock;
        if (!pre && !DisablePreLock)
        {
            Dictionary<(Guid, int), Locked> candidates = await ReadItemsAsync(tx, tenantId, req.Items, forUpdate: false, ct);
            var rows = req.Items.Select(i => candidates.GetValueOrDefault((i.DocumentId, i.LineNo))).Where(r => r is not null).ToList();
            pre = req.Items.Any(i => i.ClearedAmountTc is not null) || rows.Any(r => r!.Status != "OPEN")
                  || rows.Sum(r => r!.LineLc) != 0m || rows.Sum(r => r!.LineGc) != 0m || rows.Sum(r => r!.LineRc ?? 0m) != 0m;
        }

        Guid? ledgerId = null;
        JournalDocumentPoster.SeriesRow? series = null;
        long ledgerSeq = 0; byte[] ledgerHash = [];
        int sequenceNo = 0;
        if (pre)
        {
            // number counter first, then the LEDGER head: the order of 04, 6.3
            JournalDocumentPoster.PeriodCheck early = await JournalDocumentPoster.CheckPeriodAsync(tx, tenantId, req.CompanyId, req.ClearingDate, ctx, false, ct);
            errors.AddRange(early.Errors.Select(e => e with { Path = "clearing_date" }));
            ledgerId = await ReadDefaultLedgerAsync(tx, tenantId, req.CompanyId, ct);
            if (ledgerId is null) errors.Add(JournalDocumentPoster.Error("LEDGER.NOT_FOUND", "P2", "company_id"));
            if (errors.Count > 0) return CommandResult<PostedClearing>.Rejected(errors);
            series = await JournalDocumentPoster.ReadSeriesAsync(tx, tenantId, req.CompanyId, DifferenceType, req.ClearingDate, ledgerId!.Value, ct);
            if (series is null)
            {
                // Without a series no difference document can be posted. A set that needs none can still clear, so this is not an error yet.
            }
            else
            {
                long? last = await LockCounterAsync(tx, tenantId, req.CompanyId, series.SeriesId, early.FiscalYear!.Value, ct);
                if (last is null) series = null;
                else sequenceNo = (int)last.Value;   // the number that the document would take is last + 1, decided below
            }
            (ledgerSeq, ledgerHash) = await LockLedgerHeadAsync(tx, tenantId, req.CompanyId, ledgerId.Value, ct);
        }

        (long headSeq, byte[] headHash, _) = await CommandSupport.LockOrCreateHeadAsync(
            tx, tenantId, req.CompanyId, ClearingHash2.ChainType, "", LedgerHash2.Genesis(tenantId, req.CompanyId, ClearingHash2.ChainType, ""), ct);

        JournalDocumentPoster.PeriodCheck period = await JournalDocumentPoster.CheckPeriodAsync(tx, tenantId, req.CompanyId, req.ClearingDate, ctx, true, ct);
        errors.AddRange(period.Errors.Select(e => e with { Path = "clearing_date" }));
        if (errors.Count > 0) return CommandResult<PostedClearing>.Rejected(errors);

        Dictionary<(Guid, int), Locked> locked = await ReadItemsAsync(tx, tenantId, req.Items, forUpdate: true, ct);

        // P2, P4, P5, P6 after the locks, from the locked rows
        var allocations = new List<(ClearingItemRequest Request, Locked Row, Allocation Amounts)>();
        var scaleCache = new Dictionary<string, int>();
        async Task<int> Scale(string currency)
        {
            if (!scaleCache.TryGetValue(currency, out int s)) scaleCache[currency] = s = await ReadMinorUnitsAsync(tx, currency, ct);
            return s;
        }
        for (int i = 0; i < req.Items.Count; i++)
        {
            ClearingItemRequest it = req.Items[i];
            string path = $"items[{i}]";
            if (!locked.TryGetValue((it.DocumentId, it.LineNo), out Locked? row))
            {
                errors.Add(JournalDocumentPoster.Error("CLEARING.NOT_AN_OPEN_ITEM", "P2", path, ("document_id", it.DocumentId), ("line_no", it.LineNo)));
                continue;
            }
            if (row.CompanyId != req.CompanyId)
            {
                errors.Add(JournalDocumentPoster.Error("CLEARING.ITEM_OTHER_COMPANY", "P2", path));
                continue;
            }
            if (row.AccountId != req.AccountId)
            {
                errors.Add(JournalDocumentPoster.Error("CLEARING.ITEMS_NOT_SAME_ACCOUNT", "P4", path, ("account_id", row.AccountId)));
                continue;
            }
            if (row.PartnerId is not null)
            {
                errors.Add(JournalDocumentPoster.Error("CLEARING.ITEMS_NOT_SAME_PARTNER", "P4", path));
                continue;
            }
            if (row.Status == "CLEARED")
            {
                errors.Add(JournalDocumentPoster.Error("CONFLICT.ITEM_ALREADY_CLEARED", "P6", path, ("document_id", it.DocumentId), ("line_no", it.LineNo)));
                continue;
            }
            Allocation? a = ClearingRules.Allocate(
                new OpenLine(row.LineTc, row.LineLc, row.LineGc, row.LineRc, row.ClearedTc, row.ClearedLc, row.ClearedGc, row.ClearedRc),
                it.ClearedAmountTc, await Scale(row.CurrencyLc), await Scale(row.CurrencyGc), row.CurrencyRc is null ? null : await Scale(row.CurrencyRc), out string? error);
            if (a is null)
                errors.Add(JournalDocumentPoster.Error(error!, "P6", $"{path}.cleared_amount_tc", ("remaining_tc", row.LineTc - row.ClearedTc)));
            else
                allocations.Add((it, row, a));
        }
        if (errors.Count > 0) return CommandResult<PostedClearing>.Rejected(errors);

        // P6: the set nets to zero in TC when the items share a currency (Q1). What is left in LC, GC and RC is the realised FX (03, 3.1).
        decimal sumTc = allocations.Sum(x => x.Amounts.Tc), sumLc = allocations.Sum(x => x.Amounts.Lc), sumGc = allocations.Sum(x => x.Amounts.Gc);
        decimal? sumRc = company.ReportingCurrency is null ? null : allocations.Sum(x => x.Amounts.Rc ?? 0m);
        string[] currencies = allocations.Select(x => x.Row.CurrencyTc).Distinct().ToArray();
        if (currencies.Length == 1 && sumTc != 0m)
            return Reject(JournalDocumentPoster.Error("CLEARING.TC_DIFFERENCE", "P6", "items", ("difference_tc", sumTc)));
        bool difference = sumLc != 0m || sumGc != 0m || sumRc is not null and not 0m;
        if (difference && !pre) throw new NeedsPreLock();   // 03, 5.6: roll back and start again with the counter and the LEDGER head locked first

        // The difference document, if there is one (03, 3.1)
        Guid clearingId = Guid.NewGuid();
        Guid? documentId = null; string? documentNumber = null; byte[]? documentRowHash = null;
        DifferenceItem? offset = null;
        DateTime now = DateTime.UtcNow;
        DateTime clearedAt = new(now.Ticks - now.Ticks % 10, DateTimeKind.Utc);
        if (difference)
        {
            var built = await BuildDifferenceAsync(
                tx, tenantId, ctx, req, company, ledgerId!.Value, series, sequenceNo, ledgerSeq, ledgerHash, period, currencies, sumLc, sumGc, sumRc, clearedAt, ct);
            if (built.Errors.Count > 0) return CommandResult<PostedClearing>.Rejected(built.Errors);
            documentId = built.DocumentId; documentNumber = built.Number; documentRowHash = built.RowHash; offset = built.Offset;
        }

        // The node of the clearing chain
        long seq = headSeq + 1;
        var body = new Dictionary<string, object?>
        {
            ["clearing_id"] = clearingId, ["clearing_type"] = "CLEAR", ["clearing_date"] = req.ClearingDate,
            ["fiscal_year"] = period.FiscalYear!.Value, ["fiscal_period"] = period.PeriodNo!.Value, ["cleared_at"] = clearedAt,
            ["account_id"] = req.AccountId, ["partner_id"] = null, ["document_id"] = documentId, ["reset_of_clearing_id"] = null,
            ["clearing_method"] = req.ClearingMethod, ["matching_ref"] = req.MatchingRef, ["source_system"] = ctx.SourceSystem,
            ["idempotency_key"] = ctx.IdempotencyKey, ["command_name"] = ClearingRules.PostCommand, ["cleared_by"] = ctx.Actor,
            ["correlation_id"] = ctx.CorrelationId
        };
        // the items of the caller, then the offset line of the difference document as the last item (03, 3.1)
        var itemRows = allocations.Select((x, n) => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["tenant_id"] = tenantId, ["company_id"] = req.CompanyId, ["clearing_id"] = clearingId, ["item_no"] = n + 1,
            ["clearing_date"] = req.ClearingDate, ["open_item_document_id"] = x.Row.DocumentId, ["open_item_line_no"] = x.Row.LineNo,
            ["currency_tc"] = x.Row.CurrencyTc, ["cleared_amount_tc"] = x.Amounts.Tc, ["cleared_amount_lc"] = x.Amounts.Lc,
            ["cleared_amount_gc"] = x.Amounts.Gc, ["cleared_amount_rc"] = x.Amounts.Rc
        }).ToList();
        if (offset is not null)
            itemRows.Add(new Dictionary<string, object?>
            {
                ["tenant_id"] = tenantId, ["company_id"] = req.CompanyId, ["clearing_id"] = clearingId, ["item_no"] = itemRows.Count + 1,
                ["clearing_date"] = req.ClearingDate, ["open_item_document_id"] = documentId, ["open_item_line_no"] = offset.LineNo,
                ["currency_tc"] = offset.CurrencyTc, ["cleared_amount_tc"] = 0m, ["cleared_amount_lc"] = offset.Lc,
                ["cleared_amount_gc"] = offset.Gc, ["cleared_amount_rc"] = offset.Rc
            });
        byte[] rowHash = ClearingHash2.RowHash(tenantId, req.CompanyId, seq, headHash, body, ClearingHash2.ItemsDigest(itemRows), documentRowHash);

        await InsertClearingAsync(tx, tenantId, req, ctx, clearingId, documentId, seq, period.FiscalYear!.Value, period.PeriodNo!.Value, clearedAt, headHash, rowHash, "CLEAR", null, ClearingRules.PostCommand, ct);
        var cleared = new List<ClearedItem>();
        for (int n = 0; n < allocations.Count; n++)
        {
            var (_, row, a) = allocations[n];
            await InsertItemAsync(tx, tenantId, req, clearingId, n + 1, row.DocumentId, row.LineNo, row.CurrencyTc, a.Tc, a.Lc, a.Gc, a.Rc, ct);
            decimal tc = row.ClearedTc + a.Tc, lc = row.ClearedLc + a.Lc, gc = row.ClearedGc + a.Gc;
            decimal? rc = row.LineRc is null ? null : (row.ClearedRc ?? 0m) + (a.Rc ?? 0m);
            string status = ClearingRules.StatusOf(new OpenLine(row.LineTc, row.LineLc, row.LineGc, row.LineRc, 0m, 0m, 0m, null), tc, lc, gc, rc);
            await UpdateOpenItemAsync(tx, tenantId, row.CompanyId, row.DocumentId, row.LineNo, clearingId, status, tc, lc, gc, rc, ct);
            cleared.Add(new ClearedItem(n + 1, row.DocumentId, row.LineNo, row.CurrencyTc, a.Tc, a.Lc, a.Gc, a.Rc, status));
        }
        if (offset is not null)
        {
            // The offset line is an open item that is created already cleared: this clearing has an item for it
            int no = allocations.Count + 1;
            await InsertItemAsync(tx, tenantId, req, clearingId, no, documentId!.Value, offset.LineNo, offset.CurrencyTc, 0m, offset.Lc, offset.Gc, offset.Rc, ct);
            await UpdateOpenItemAsync(tx, tenantId, req.CompanyId, documentId.Value, offset.LineNo, clearingId, "CLEARED", 0m, offset.Lc, offset.Gc, offset.Rc, ct);
            cleared.Add(new ClearedItem(no, documentId.Value, offset.LineNo, offset.CurrencyTc, 0m, offset.Lc, offset.Gc, offset.Rc, "CLEARED"));
        }

        await MoveHeadAsync(tx, tenantId, req.CompanyId, "CLEARING", "", seq, rowHash, ClearingHash2.Version, ct);

        Guid logId = await CommandSupport.WriteLogAsync(tx, tenantId, req.CompanyId, ctx, ClearingRules.PostCommand, fingerprint, "CLEARING", clearingId, ct);

        // Events (15, 6): the difference document, the clearing with its chain reference only, every open item that was cleared. outbox_head last.
        DateTime at = clearedAt;
        if (documentId is not null)
            await CommandSupport.WriteOutboxAsync(tx, tenantId, req.CompanyId, ctx, "ledger.document_posted", "DOCUMENT", $"{req.CompanyId:D}/{documentId:D}", "POSTED",
                at, isAudit: true,
                JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["chain_type"] = "LEDGER", ["scope"] = ledgerId!.Value.ToString("D"), ["chain_seq"] = ledgerSeq + 1,
                    ["row_hash"] = Canonical.FormatHex32(documentRowHash!)
                }),
                JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["document_type"] = DifferenceType, ["posting_date"] = req.ClearingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["period"] = period.PeriodNo, ["ledger_ids"] = new[] { ledgerId!.Value.ToString("D") }, ["ledger_seq"] = ledgerSeq + 1
                }), ct);
        string chainRef = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["chain_type"] = "CLEARING", ["scope"] = "", ["chain_seq"] = seq, ["row_hash"] = Canonical.FormatHex32(rowHash)
        });
        await CommandSupport.WriteOutboxAsync(tx, tenantId, req.CompanyId, ctx, "clearing.posted", "CLEARING", $"{req.CompanyId:D}/{clearingId:D}", "POSTED",
            at, isAudit: true, chainRef, JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["clearing_id"] = clearingId, ["partner_id"] = null, ["account_id"] = req.AccountId, ["items"] = cleared.Count
            }), ct);
        foreach (ClearedItem c in cleared)
            await CommandSupport.WriteOutboxAsync(tx, tenantId, req.CompanyId, ctx, "open_item.cleared", "OPEN_ITEM",
                $"{req.CompanyId:D}/{c.DocumentId:D}/{c.LineNo}", "CLEARED", at, isAudit: false, null,
                JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["account_id"] = req.AccountId, ["partner_id"] = null, ["due_date"] = null,
                    ["amount_tc"] = c.AmountTc.ToString("F4", CultureInfo.InvariantCulture), ["currency"] = c.CurrencyTc,
                    ["clearing_id"] = clearingId, ["status"] = c.StatusAfter
                }), ct);

        await tx.CommitAsync(ct);
        return CommandResult<PostedClearing>.Ok(
            new PostedClearing(clearingId, req.CompanyId, seq, Canonical.FormatHex32(rowHash), Canonical.FormatHex32(headHash), req.ClearingDate,
                period.FiscalYear!.Value, period.PeriodNo!.Value, req.ClearingMethod, cleared, documentId, documentNumber),
            logId, period.Warnings);
    }

    // ---------------------------------------------------------------------------------------------------------
    // The difference document (03, 3.1)
    // ---------------------------------------------------------------------------------------------------------
    private sealed record DifferenceItem(int LineNo, string CurrencyTc, decimal Lc, decimal Gc, decimal? Rc);
    private sealed record Built(List<CommandError> Errors, Guid DocumentId, string Number, byte[] RowHash, DifferenceItem? Offset);

    /// <summary>
    /// Posts the document of type CLEARING with two lines: FX_REALISED on the account of the role of that name, with the residual of the
    /// cleared items in LC, GC and RC (TC 0), and CLEARING_DIFF on the cleared account with the opposite amounts. Both are DERIVED lines with
    /// rate 1 (02, 5.2, D9). The transaction currency of the document is the one of the items, or the local currency when they differ.
    /// The offset line gets its open item row, which the caller then clears.
    /// </summary>
    private static async Task<Built> BuildDifferenceAsync(
        TenantTransaction tx, Guid tenantId, CommandContext ctx, PostClearingRequest req, JournalDocumentPoster.CompanyRow company, Guid ledgerId,
        JournalDocumentPoster.SeriesRow? series, int lastNumber, long ledgerSeq, byte[] ledgerHash, JournalDocumentPoster.PeriodCheck period,
        string[] itemCurrencies, decimal resLc, decimal resGc, decimal? resRc, DateTime postedAt, CancellationToken ct)
    {
        var errors = new List<CommandError>();
        if (series is null)
            errors.Add(JournalDocumentPoster.Error("SERIES.NO_ASSIGNMENT", "P2", "clearing_date",
                ("document_type", DifferenceType), ("ledger_id", ledgerId), ("posting_date", req.ClearingDate)));
        string? groupCurrency = await ReadGroupCurrencyAsync(tx, tenantId, req.CompanyId, req.ClearingDate, ct);
        if (groupCurrency is null) errors.Add(JournalDocumentPoster.Error("CONFIG.GROUP_MEMBERSHIP_MISSING", "P2", "company_id", ("posting_date", req.ClearingDate)));
        Guid? fxAccount = await AccountRoleResolver.ResolveAsync(tx, tenantId, req.CompanyId, "FX_REALISED", null, null, null, ct);
        if (fxAccount is null)
            errors.Add(JournalDocumentPoster.Error("CONFIG.ROLE_NOT_ASSIGNED", "P2", "clearing_date", ("role_code", "FX_REALISED")));
        else
        {
            var a = (await JournalDocumentPoster.ReadAccountRowsAsync(tx, tenantId, req.CompanyId, [fxAccount.Value], ct)).GetValueOrDefault(fxAccount.Value);
            if (a is null || !a.Active || a.PostingBlocked || a.ValidFrom > req.ClearingDate || (a.ValidTo is { } to && to < req.ClearingDate))
                errors.Add(JournalDocumentPoster.Error("ACCOUNT.ROLE_ACCOUNT_NOT_USABLE", "P2", "clearing_date", ("role_code", "FX_REALISED"), ("account_id", fxAccount)));
        }
        if (errors.Count > 0) return new Built(errors, Guid.Empty, "", [], null);

        int fiscalYear = period.FiscalYear!.Value;
        long sequenceNo = lastNumber + 1;
        await using (var cmd = tx.Command(
            "update core.number_range_counter set last_number = @n, updated_at = now() " +
            "where tenant_id = @t and company_id = @c and series_id = @s and fiscal_year = @y"))
        {
            cmd.Parameters.AddWithValue("n", sequenceNo);
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", req.CompanyId);
            cmd.Parameters.AddWithValue("s", series!.SeriesId);
            cmd.Parameters.AddWithValue("y", fiscalYear);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        string number = PostingRules.FormatNumber(series.Prefix, series.IncludeFiscalYear, series.Separator, series.NumberLength, fiscalYear, sequenceNo);
        string transactionCurrency = itemCurrencies.Length == 1 ? itemCurrencies[0] : company.LocalCurrency;
        Guid documentId = Guid.NewGuid();
        long chainSeq = ledgerSeq + 1;
        bool hasRc = resRc is not null;

        var header = new Dictionary<string, object?>
        {
            ["tenant_id"] = tenantId, ["company_id"] = req.CompanyId, ["document_id"] = documentId, ["ledger_id"] = ledgerId,
            ["document_type"] = DifferenceType, ["is_system_generated"] = true, ["number_series_id"] = series.SeriesId,
            ["fiscal_year"] = fiscalYear, ["sequence_no"] = sequenceNo, ["document_number"] = number,
            ["fiscal_period"] = period.PeriodNo!.Value, ["soft_close_override"] = period.SoftCloseOverride,
            ["migration_batch_id"] = null, ["detail_digest"] = null,
            ["document_date"] = req.ClearingDate, ["posting_date"] = req.ClearingDate, ["posted_at"] = postedAt,
            ["transaction_currency"] = transactionCurrency,
            ["reversal_of_document_id"] = null, ["reversal_reason_code"] = null, ["auto_reversal_date"] = null,
            ["ic_transaction_id"] = null, ["ic_counterpart_company_id"] = null, ["origin"] = "SYSTEM",
            ["source_system"] = ctx.SourceSystem, ["source_document_ref"] = null, ["idempotency_key"] = ctx.IdempotencyKey,
            ["command_name"] = ClearingRules.PostCommand, ["posted_by"] = ctx.Actor, ["correlation_id"] = ctx.CorrelationId,
            ["hash_version"] = LedgerHash2.Version
        };
        // Both lines: TC 0, rate 1 in every currency, type DERIVED, the amounts are authoritative (02, 5.2, D9)
        PostedLine Line(int no, Guid account, string type, decimal lc, decimal gc, decimal? rc) =>
            new(no, null, account, type, 0m, lc, company.LocalCurrency, 1m, gc, groupCurrency!, 1m, rc, hasRc ? company.ReportingCurrency : null, hasRc ? 1m : null,
                "DERIVED", req.ClearingDate);
        var fx = Line(1, fxAccount!.Value, "FX_REALISED", resLc, resGc, resRc);
        var diff = Line(2, req.AccountId, "CLEARING_DIFF", -resLc, -resGc, resRc is null ? null : -resRc);

        Dictionary<string, object?> LineRow(PostedLine l) => new()
        {
            ["tenant_id"] = tenantId, ["company_id"] = req.CompanyId, ["document_id"] = documentId, ["line_no"] = l.LineNo,
            ["ledger_id"] = ledgerId, ["line_type"] = l.LineType, ["account_id"] = l.AccountId,
            ["posting_date"] = req.ClearingDate, ["fiscal_year"] = fiscalYear, ["fiscal_period"] = period.PeriodNo!.Value,
            ["reversed_line_no"] = null, ["tax_code_id"] = null,
            ["amount_tc"] = l.AmountTc, ["amount_lc"] = l.AmountLc, ["currency_lc"] = l.CurrencyLc, ["rate_lc"] = l.RateLc, ["rate_lc_table"] = null,
            ["amount_gc"] = l.AmountGc, ["currency_gc"] = l.CurrencyGc, ["rate_gc"] = l.RateGc, ["rate_gc_table"] = null,
            ["amount_rc"] = l.AmountRc, ["currency_rc"] = l.CurrencyRc, ["rate_rc"] = l.RateRc, ["rate_rc_table"] = null,
            ["rate_type"] = l.RateType, ["rate_date"] = l.RateDate, ["ic_counterpart_company_id"] = null
        };
        var offsetOpenItem = new Dictionary<string, object?>
        {
            ["tenant_id"] = tenantId, ["company_id"] = req.CompanyId, ["document_id"] = documentId, ["line_no"] = 2,
            ["partner_id"] = null, ["legacy_document_date"] = null, ["legacy_posting_date"] = null, ["legacy_document_ref"] = null
        };
        var lineHashes = new List<byte[]>
        {
            LedgerHash2.LineHash(new HashedLine(LineRow(fx))),
            LedgerHash2.LineHash(new HashedLine(LineRow(diff), OpenItem: offsetOpenItem))
        };
        byte[] rowHash = LedgerHash2.RowHash(header, chainSeq, ledgerHash, LedgerHash2.LineDigest(lineHashes));

        await InsertDocumentAsync(tx, tenantId, req, ctx, documentId, ledgerId, series, fiscalYear, period.PeriodNo!.Value, sequenceNo, number,
            period.SoftCloseOverride, postedAt, chainSeq, ledgerHash, rowHash, transactionCurrency, DifferenceType, null, null, ClearingRules.PostCommand, ct);
        foreach (PostedLine l in new[] { fx, diff })
            await InsertLineAsync(tx, tenantId, req.CompanyId, documentId, ledgerId, fiscalYear, period.PeriodNo!.Value, req.ClearingDate, l, null, ct);
        // The row starts as OPEN with nothing cleared. The clearing is inserted next and then updates it to CLEARED.
        await using (var cmd = tx.Command(
            "insert into core.journal_line_open_item (tenant_id, company_id, document_id, line_no, cleared_amount_rc) values (@t, @c, @d, 2, @rc)"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", req.CompanyId);
            cmd.Parameters.AddWithValue("d", documentId);
            cmd.Parameters.Add(new NpgsqlParameter("rc", NpgsqlDbType.Numeric) { Value = hasRc ? 0m : DBNull.Value });
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await MoveHeadAsync(tx, tenantId, req.CompanyId, "LEDGER", ledgerId.ToString("D"), chainSeq, rowHash, LedgerHash2.Version, ct);
        return new Built(errors, documentId, number, rowHash, new DifferenceItem(2, transactionCurrency, diff.AmountLc, diff.AmountGc, diff.AmountRc));
    }

    // ---------------------------------------------------------------------------------------------------------
    private static async Task<string?> ReadOpenItemModeAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid accountId, CancellationToken ct)
    {
        await using var cmd = tx.Command("select open_item_mode from core.gl_account where tenant_id = @t and company_id = @c and account_id = @a");
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", companyId);
        cmd.Parameters.AddWithValue("a", accountId);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    private static async Task<int> ReadMinorUnitsAsync(TenantTransaction tx, string currency, CancellationToken ct)
    {
        await using var cmd = tx.Command("select minor_units from platform.ref_currency where currency_code = @c");
        cmd.Parameters.AddWithValue("c", currency);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct) ?? 4, CultureInfo.InvariantCulture);
    }

    private static async Task<Guid?> ReadDefaultLedgerAsync(TenantTransaction tx, Guid tenantId, Guid companyId, CancellationToken ct)
    {
        await using var cmd = tx.Command("select ledger_id from core.ledger where tenant_id = @t and company_id = @c and is_default and active");
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", companyId);
        return await cmd.ExecuteScalarAsync(ct) is Guid g ? g : null;
    }

    private static async Task<string?> ReadGroupCurrencyAsync(TenantTransaction tx, Guid tenantId, Guid companyId, DateOnly date, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select g.group_currency from core.group_membership m " +
            "join core.company_group g on g.tenant_id = m.tenant_id and g.group_id = m.group_id " +
            "where m.tenant_id = @t and m.company_id = @c and m.valid_from <= @d and (m.valid_to is null or m.valid_to > @d)");
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", companyId);
        cmd.Parameters.AddWithValue("d", date);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>Locks the number counter of the series for the fiscal year and returns last_number, or null when there is no counter.</summary>
    private static async Task<long?> LockCounterAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid seriesId, int fiscalYear, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select last_number from core.number_range_counter where tenant_id = @t and company_id = @c and series_id = @s and fiscal_year = @y for update");
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", companyId);
        cmd.Parameters.AddWithValue("s", seriesId);
        cmd.Parameters.AddWithValue("y", fiscalYear);
        object? value = await cmd.ExecuteScalarAsync(ct);
        return value is null ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task<(long Seq, byte[] Hash)> LockLedgerHeadAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid ledgerId, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select last_seq, last_hash from core.chain_head where tenant_id = @t and company_id = @c and chain_type = 'LEDGER' and scope_key = @k for update");
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", companyId);
        cmd.Parameters.AddWithValue("k", ledgerId.ToString("D"));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new InvalidOperationException("The company has no LEDGER chain head: companies:activate creates it");
        return (r.GetInt64(0), (byte[])r[1]);
    }

    internal static async Task MoveHeadAsync(
        TenantTransaction tx, Guid tenantId, Guid companyId, string chainType, string scopeKey, long seq, byte[] hash, int version, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "update core.chain_head set last_seq = @s, last_hash = @h, hash_version = @v, updated_at = now() " +
            "where tenant_id = @t and company_id = @c and chain_type = @type and scope_key = @k");
        cmd.Parameters.AddWithValue("s", seq);
        cmd.Parameters.AddWithValue("h", hash);
        cmd.Parameters.AddWithValue("v", (short)version);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", companyId);
        cmd.Parameters.AddWithValue("type", chainType);
        cmd.Parameters.AddWithValue("k", scopeKey);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Reads the open item rows with their lines, sorted by key. With forUpdate the rows are locked in that order (04, 6.3).
    /// A key without an open item row is missing from the result.</summary>
    private static async Task<Dictionary<(Guid, int), Locked>> ReadItemsAsync(
        TenantTransaction tx, Guid tenantId, IReadOnlyList<ClearingItemRequest> items, bool forUpdate, CancellationToken ct)
    {
        var rows = new Dictionary<(Guid, int), Locked>();
        await using var cmd = tx.Command(
            "select o.company_id, o.document_id, o.line_no, o.partner_id, o.clearing_status, o.cleared_amount_tc, o.cleared_amount_lc, " +
            "o.cleared_amount_gc, o.cleared_amount_rc, l.account_id, l.amount_tc, l.amount_lc, l.amount_gc, l.amount_rc, " +
            "l.currency_lc, l.currency_gc, l.currency_rc, d.transaction_currency " +
            "from core.journal_line_open_item o " +
            "join core.journal_line l on l.tenant_id = o.tenant_id and l.company_id = o.company_id and l.document_id = o.document_id and l.line_no = o.line_no " +
            "join core.journal_document d on d.tenant_id = o.tenant_id and d.company_id = o.company_id and d.document_id = o.document_id " +
            "join unnest(@docs, @lines) as k(doc, line) on k.doc = o.document_id and k.line = o.line_no " +
            "where o.tenant_id = @t order by o.document_id, o.line_no" + (forUpdate ? " for update of o" : ""));
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.Add(new NpgsqlParameter("docs", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = items.Select(i => i.DocumentId).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("lines", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = items.Select(i => i.LineNo).ToArray() });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var row = new Locked(r.GetGuid(0), r.GetGuid(1), r.GetInt32(2), r.IsDBNull(3) ? null : r.GetGuid(3), r.GetString(4),
                r.GetDecimal(5), r.GetDecimal(6), r.GetDecimal(7), r.IsDBNull(8) ? null : r.GetDecimal(8), r.GetGuid(9),
                r.GetDecimal(10), r.GetDecimal(11), r.GetDecimal(12), r.IsDBNull(13) ? null : r.GetDecimal(13),
                r.GetString(14), r.GetString(15), r.IsDBNull(16) ? null : r.GetString(16), r.GetString(17));
            rows[(row.DocumentId, row.LineNo)] = row;
        }
        return rows;
    }

    internal static async Task InsertClearingAsync(
        TenantTransaction tx, Guid tenantId, PostClearingRequest req, CommandContext ctx, Guid clearingId, Guid? documentId, long seq, int fiscalYear, int period,
        DateTime clearedAt, byte[] prevHash, byte[] rowHash, string type, Guid? resetOf, string command, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "insert into core.clearing (tenant_id, company_id, clearing_id, chain_seq, clearing_type, clearing_date, fiscal_year, fiscal_period, " +
            "cleared_at, account_id, partner_id, document_id, reset_of_clearing_id, clearing_method, matching_ref, source_system, idempotency_key, command_name, cleared_by, correlation_id, note, " +
            "prev_hash, row_hash, hash_version) " +
            "values (@t, @c, @id, @seq, @type, @date, @fy, @fp, @at, @acc, @partner, @doc, @reset, @method, @ref, @src, @key, @cmd, @by, @corr, @note, @prev, @hash, @v)");
        cmd.Parameters.Add(new NpgsqlParameter("partner", NpgsqlDbType.Uuid) { Value = (object?)req.PartnerId ?? DBNull.Value });
        cmd.Parameters.AddWithValue("type", type);
        cmd.Parameters.Add(new NpgsqlParameter("reset", NpgsqlDbType.Uuid) { Value = (object?)resetOf ?? DBNull.Value });
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", req.CompanyId);
        cmd.Parameters.AddWithValue("id", clearingId);
        cmd.Parameters.AddWithValue("seq", seq);
        cmd.Parameters.AddWithValue("date", req.ClearingDate);
        cmd.Parameters.AddWithValue("fy", fiscalYear);
        cmd.Parameters.AddWithValue("fp", period);
        cmd.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = clearedAt });
        cmd.Parameters.AddWithValue("acc", req.AccountId);
        cmd.Parameters.Add(new NpgsqlParameter("doc", NpgsqlDbType.Uuid) { Value = (object?)documentId ?? DBNull.Value });
        cmd.Parameters.AddWithValue("method", req.ClearingMethod);
        cmd.Parameters.Add(new NpgsqlParameter("ref", NpgsqlDbType.Text) { Value = (object?)req.MatchingRef ?? DBNull.Value });
        cmd.Parameters.AddWithValue("src", ctx.SourceSystem);
        cmd.Parameters.AddWithValue("key", ctx.IdempotencyKey);
        cmd.Parameters.AddWithValue("cmd", command);
        cmd.Parameters.AddWithValue("by", ctx.Actor);
        cmd.Parameters.AddWithValue("corr", ctx.CorrelationId);
        cmd.Parameters.Add(new NpgsqlParameter("note", NpgsqlDbType.Text) { Value = (object?)req.Note ?? DBNull.Value });
        cmd.Parameters.AddWithValue("prev", prevHash);
        cmd.Parameters.AddWithValue("hash", rowHash);
        cmd.Parameters.AddWithValue("v", (short)ClearingHash2.Version);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    internal static async Task InsertItemAsync(
        TenantTransaction tx, Guid tenantId, PostClearingRequest req, Guid clearingId, int itemNo, Guid documentId, int lineNo, string currencyTc,
        decimal tc, decimal lc, decimal gc, decimal? rc, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "insert into core.clearing_item (tenant_id, company_id, clearing_id, item_no, clearing_date, open_item_document_id, open_item_line_no, " +
            "currency_tc, cleared_amount_tc, cleared_amount_lc, cleared_amount_gc, cleared_amount_rc) " +
            "values (@t, @c, @id, @n, @date, @doc, @line, @ccy, @tc, @lc, @gc, @rc)");
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", req.CompanyId);
        cmd.Parameters.AddWithValue("id", clearingId);
        cmd.Parameters.AddWithValue("n", itemNo);
        cmd.Parameters.AddWithValue("date", req.ClearingDate);
        cmd.Parameters.AddWithValue("doc", documentId);
        cmd.Parameters.AddWithValue("line", lineNo);
        cmd.Parameters.AddWithValue("ccy", currencyTc);
        cmd.Parameters.AddWithValue("tc", tc);
        cmd.Parameters.AddWithValue("lc", lc);
        cmd.Parameters.AddWithValue("gc", gc);
        cmd.Parameters.Add(new NpgsqlParameter("rc", NpgsqlDbType.Numeric) { Value = (object?)rc ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    internal static async Task UpdateOpenItemAsync(
        TenantTransaction tx, Guid tenantId, Guid companyId, Guid documentId, int lineNo, Guid clearingId, string status,
        decimal tc, decimal lc, decimal gc, decimal? rc, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "update core.journal_line_open_item set clearing_status = @s, cleared_amount_tc = @tc, cleared_amount_lc = @lc, cleared_amount_gc = @gc, " +
            "cleared_amount_rc = @rc, last_clearing_id = @id where tenant_id = @t and company_id = @c and document_id = @d and line_no = @n");
        cmd.Parameters.AddWithValue("s", status);
        cmd.Parameters.AddWithValue("tc", tc);
        cmd.Parameters.AddWithValue("lc", lc);
        cmd.Parameters.AddWithValue("gc", gc);
        cmd.Parameters.Add(new NpgsqlParameter("rc", NpgsqlDbType.Numeric) { Value = (object?)rc ?? DBNull.Value });
        cmd.Parameters.AddWithValue("id", clearingId);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", companyId);
        cmd.Parameters.AddWithValue("d", documentId);
        cmd.Parameters.AddWithValue("n", lineNo);
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("The open item row to update does not exist");
    }

    private static async Task InsertDocumentAsync(
        TenantTransaction tx, Guid tenantId, PostClearingRequest req, CommandContext ctx, Guid documentId, Guid ledgerId,
        JournalDocumentPoster.SeriesRow series, int fiscalYear, int period, long sequenceNo, string number, bool softOverride, DateTime postedAt,
        long chainSeq, byte[] prevHash, byte[] rowHash, string transactionCurrency, string documentType, Guid? reversalOf, string? reasonCode, string command,
        CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "insert into core.journal_document (tenant_id, company_id, document_id, ledger_id, document_type, is_system_generated, " +
            "number_series_id, fiscal_year, sequence_no, document_number, fiscal_period, soft_close_override, document_date, posting_date, " +
            "posted_at, transaction_currency, reversal_of_document_id, reversal_reason_code, origin, source_system, idempotency_key, command_name, posted_by, " +
            "correlation_id, chain_seq, prev_hash, row_hash, hash_version) " +
            "values (@t, @c, @id, @l, @dt, true, @s, @fy, @seq, @num, @fp, @soft, @d, @d, @at, @tc, @rev, @reason, 'SYSTEM', @src, @key, @cmd, " +
            "@by, @corr, @cs, @prev, @row, @hv)");
        cmd.Parameters.AddWithValue("dt", documentType);
        cmd.Parameters.Add(new NpgsqlParameter("rev", NpgsqlDbType.Uuid) { Value = (object?)reversalOf ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("reason", NpgsqlDbType.Text) { Value = (object?)reasonCode ?? DBNull.Value });
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", req.CompanyId);
        cmd.Parameters.AddWithValue("id", documentId);
        cmd.Parameters.AddWithValue("l", ledgerId);
        cmd.Parameters.AddWithValue("s", series.SeriesId);
        cmd.Parameters.AddWithValue("fy", fiscalYear);
        cmd.Parameters.AddWithValue("seq", sequenceNo);
        cmd.Parameters.AddWithValue("num", number);
        cmd.Parameters.AddWithValue("fp", period);
        cmd.Parameters.AddWithValue("soft", softOverride);
        cmd.Parameters.AddWithValue("d", req.ClearingDate);
        cmd.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = postedAt });
        cmd.Parameters.AddWithValue("tc", transactionCurrency);
        cmd.Parameters.AddWithValue("src", ctx.SourceSystem);
        cmd.Parameters.AddWithValue("key", ctx.IdempotencyKey);
        cmd.Parameters.AddWithValue("cmd", command);
        cmd.Parameters.AddWithValue("by", ctx.Actor);
        cmd.Parameters.AddWithValue("corr", ctx.CorrelationId);
        cmd.Parameters.AddWithValue("cs", chainSeq);
        cmd.Parameters.AddWithValue("prev", prevHash);
        cmd.Parameters.AddWithValue("row", rowHash);
        cmd.Parameters.AddWithValue("hv", (short)LedgerHash2.Version);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertLineAsync(
        TenantTransaction tx, Guid tenantId, Guid companyId, Guid documentId, Guid ledgerId, int fiscalYear, int period, DateOnly postingDate,
        PostedLine l, int? reversedLineNo, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "insert into core.journal_line (tenant_id, company_id, document_id, line_no, ledger_id, line_type, account_id, posting_date, " +
            "fiscal_year, fiscal_period, reversed_line_no, amount_tc, amount_lc, currency_lc, rate_lc, amount_gc, currency_gc, rate_gc, " +
            "amount_rc, currency_rc, rate_rc, rate_type, rate_date) " +
            "values (@t, @c, @d, @n, @l, @lt, @a, @pd, @fy, @fp, @rev, @tc, @lc, @clc, @rlc, @gc, @cgc, @rgc, @rc, @crc, @rrc, @rt, @rd)");
        cmd.Parameters.Add(new NpgsqlParameter("rev", NpgsqlDbType.Integer) { Value = (object?)reversedLineNo ?? DBNull.Value });
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", companyId);
        cmd.Parameters.AddWithValue("d", documentId);
        cmd.Parameters.AddWithValue("n", l.LineNo);
        cmd.Parameters.AddWithValue("l", ledgerId);
        cmd.Parameters.AddWithValue("lt", l.LineType);
        cmd.Parameters.AddWithValue("a", l.AccountId);
        cmd.Parameters.AddWithValue("pd", postingDate);
        cmd.Parameters.AddWithValue("fy", fiscalYear);
        cmd.Parameters.AddWithValue("fp", period);
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

    /// <summary>The clearing as stored, with the status that each of its open items has now. For the replay of an idempotent request.</summary>
    internal static async Task<PostedClearing?> LoadAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid clearingId, CancellationToken ct)
    {
        PostedClearing? head = null;
        await using (var cmd = tx.Command(
            "select c.chain_seq, c.row_hash, c.prev_hash, c.clearing_date, c.fiscal_year, c.fiscal_period, c.clearing_method, c.document_id, d.document_number, c.reset_of_clearing_id " +
            "from core.clearing c left join core.journal_document d on d.tenant_id = c.tenant_id and d.company_id = c.company_id and d.document_id = c.document_id " +
            "where c.tenant_id = @t and c.company_id = @c and c.clearing_id = @id"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", companyId);
            cmd.Parameters.AddWithValue("id", clearingId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
                head = new PostedClearing(clearingId, companyId, r.GetInt64(0), Canonical.FormatHex32((byte[])r[1]), Canonical.FormatHex32((byte[])r[2]),
                    r.GetFieldValue<DateOnly>(3), r.GetInt32(4), r.GetInt32(5), r.GetString(6), [],
                    r.IsDBNull(7) ? null : r.GetGuid(7), r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetGuid(9));
        }
        if (head is null) return null;
        var items = new List<ClearedItem>();
        await using (var cmd = tx.Command(
            "select i.item_no, i.open_item_document_id, i.open_item_line_no, i.currency_tc, i.cleared_amount_tc, i.cleared_amount_lc, i.cleared_amount_gc, " +
            "i.cleared_amount_rc, o.clearing_status from core.clearing_item i " +
            "join core.journal_line_open_item o on o.tenant_id = i.tenant_id and o.company_id = i.company_id and o.document_id = i.open_item_document_id and o.line_no = i.open_item_line_no " +
            "where i.tenant_id = @t and i.company_id = @c and i.clearing_id = @id order by i.item_no"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", companyId);
            cmd.Parameters.AddWithValue("id", clearingId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                items.Add(new ClearedItem(r.GetInt32(0), r.GetGuid(1), r.GetInt32(2), r.GetString(3), r.GetDecimal(4), r.GetDecimal(5), r.GetDecimal(6),
                    r.IsDBNull(7) ? null : r.GetDecimal(7), r.GetString(8)));
        }
        return head with { Items = items };
    }

    private static CommandResult<PostedClearing> Reject(CommandError error) => CommandResult<PostedClearing>.Rejected([error]);
}
