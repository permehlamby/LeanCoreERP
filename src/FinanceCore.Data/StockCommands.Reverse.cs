using System.Globalization;
using System.Text.Json;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Hashing;
using FinanceCore.Core.Masters;
using FinanceCore.Core.Posting;
using Npgsql;
using NpgsqlTypes;

namespace FinanceCore.Data;

/// <summary>
/// How a fulfilment command embeds the reversal of its stock document (11, 6.8): the command name, whether the stock reversal writes its own command log and commits (a fulfilment
/// reversal does neither), that a derived stock document (detail_digest set) may be reversed, and the callback that gives the detail_digest of the mirror stock document once it is
/// derived (the row hash of the mirror fulfilment document).
/// </summary>
internal sealed record StockReverseOptions(string CommandName, bool WriteLog, bool Commit, bool AllowDerived, Func<StockDerivation, Task<byte[]?>>? DetailDigest,
    Func<TenantTransaction, DateOnly, int, int, long, byte[], CancellationToken, Task<(long Seq, byte[] Hash)>>? ResetClearings = null);

/// <summary>
/// stock-documents:reverse (10, 5.5, 6.2): the mirror of a stock document posted directly, movement by movement with the opposite sign and the original value, and the mirror of its
/// derived journal with the automatic clearing of a GR/IR open item. The reversal date defaults to the posting date of the original and is never moved. A receipt of a moving average item
/// is reversed only if what it brought is still there (INVENTORY.RECEIPT_CONSUMED). Built choices are in 07, 13.2x. Not built: FIFO layers, tracking, negative stock.
/// </summary>
public sealed partial class StockCommands
{
    public const string ReversePermission = StockReversalRules.Permission;

    private sealed record Original(
        string DocumentType, string Status, Guid? JournalId, bool Derived, DateOnly PostingDate, string? CurrencyTc, int MovementCount);

    private sealed record OriginalMovement(
        int LineNo, string MovementType, string CostSource, Guid ItemId, Guid WarehouseId, Guid? LocationId, decimal Quantity, string UnitCode, decimal BaseQuantity, decimal ValueLc,
        decimal? ValueTc, string? LineRef, string? LineText, Guid? RefDocument, int? RefLine);

    private sealed record Mirror(OriginalMovement From, decimal Quantity, decimal BaseQuantity, decimal ValueLc, decimal? ValueTc, decimal CostQtyAfter, decimal CostValueAfter, decimal StockQtyAfter);

    public async Task<CommandResult<StockDocumentResult>> ReverseAsync(Guid tenantId, CommandContext ctx, ReverseStockDocumentRequest req, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(ReversePermission)) return Reject(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", ReversePermission)));
        var errors = MasterSupport.ValidateContext(ctx);
        if (req.CompanyId == Guid.Empty) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/company_id", ("reason", "required")));
        if (req.StockDocumentId == Guid.Empty) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/stock_document_id", ("reason", "required")));
        if (errors.Count > 0) return CommandResult<StockDocumentResult>.Rejected(errors);
        if (string.IsNullOrWhiteSpace(req.ReasonCode)) return Reject(Error("REVERSAL.REASON_REQUIRED", "P1", "/reason_code"));
        if (!ReversalRules.ExternalReasons.Contains(req.ReasonCode)) return Reject(Error("REVERSAL.REASON_NOT_ALLOWED", "P1", "/reason_code", ("allowed_values", ReversalRules.ExternalReasons)));
        byte[] fingerprint = StockReversalRules.Fingerprint("stock-documents:reverse", req.CompanyId, req.StockDocumentId, req.ReasonCode, req.ReversalDate);

        return await RunAsync(async () =>
        {
            await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Stock, ct: ct);
            CommandSupport.EarlierCommand? earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
            if (earlier is not null)
            {
                if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint)) return Reject(Error("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
                return earlier.EntityId is null
                    ? CommandResult<StockDocumentResult>.Failed(Error("SYSTEM.REPLAY_RESULT_MISSING", "P1", ""))
                    : CommandResult<StockDocumentResult>.Ok(await LoadResultAsync(tx, tenantId, req.CompanyId, earlier.EntityId.Value, ct), earlier.CommandLogId, replay: true);
            }
            return await ReverseCoreAsync(tx, tenantId, ctx, req.CompanyId, req.StockDocumentId, req.ReasonCode, req.ReversalDate, fingerprint, ct, null);
        });
    }

    internal async Task<CommandResult<StockDocumentResult>> ReverseCoreAsync(
        TenantTransaction tx, Guid tenantId, CommandContext ctx, Guid companyId, Guid originalId, string reasonCode, DateOnly? reversalDateRequest, byte[] fingerprint, CancellationToken ct,
        StockReverseOptions? options)
    {
        string commandName = options?.CommandName ?? "stock-documents:reverse";
        async Task<Original?> ReadOriginalAsync(bool forUpdate)
        {
            await using var cmd = tx.Command(
                "select document_type, status, journal_document_id, detail_digest is not null, posting_date, currency_tc, movement_count from core.stock_document " +
                "where tenant_id = @t and company_id = @c and stock_document_id = @d" + (forUpdate ? " for update" : ""));
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", originalId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            return await r.ReadAsync(ct)
                ? new Original(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetGuid(2), r.GetBoolean(3), DateOnly.FromDateTime(r.GetDateTime(4)), r.IsDBNull(5) ? null : r.GetString(5).Trim(), r.GetInt32(6))
                : null;
        }

        // P2: the original, the company, the date
        Original? original = await ReadOriginalAsync(false);
        if (original is null) return Reject(Error("REVERSAL.NOT_FOUND", "P2", "/stock_document_id"));
        if (original.DocumentType is not ("STOCK_RECEIPT" or "STOCK_ISSUE" or "STOCK_ADJUSTMENT" or "STOCK_RETURN") || (original.Derived && options?.AllowDerived != true))
            return Reject(Error("REVERSAL.TYPE_NOT_REVERSIBLE", "P2", "/stock_document_id", ("document_type", original.DocumentType),
                ("owning_command", original.Derived ? "the order command that created it (11, 6.8)" : null)));
        if (original.Status != "posted") return Reject(Error("REVERSAL.ALREADY_REVERSED", "P2", "/stock_document_id"));
        // a receipt without an order is not reversed while a supplier invoice that is not reversed is matched to one of its lines (11, 7.2, D3)
        if (original.DocumentType == "STOCK_RECEIPT")
        {
            await using var inv = tx.Command(
                "select coalesce(max(s), 0) from (select sum(l.base_quantity) s from core.purchase_invoice_line l join core.purchase_invoice h on h.tenant_id = l.tenant_id and h.company_id = l.company_id and h.purchase_invoice_id = l.purchase_invoice_id " +
                "left join core.purchase_invoice o on o.tenant_id = h.tenant_id and o.company_id = h.company_id and o.purchase_invoice_id = h.reversal_of_purchase_invoice_id where l.tenant_id = @t and l.company_id = @c and l.stock_document_id = @d " +
                "and (h.invoice_kind = 'INVOICE' or (h.invoice_kind = 'REVERSAL' and o.invoice_kind = 'INVOICE')) group by l.stock_document_line_no) x");
            inv.Parameters.AddWithValue("t", tenantId); inv.Parameters.AddWithValue("c", companyId); inv.Parameters.AddWithValue("d", originalId);
            if ((decimal)(await inv.ExecuteScalarAsync(ct))! > 0m) return Reject(Error("ORDER.RECEIPT_INVOICED", "P6", "/stock_document_id", ("reason", "the supplier invoice is reversed first")));
        }
        var company = await JournalDocumentPoster.ReadCompanyAsync(tx, tenantId, companyId, ct);
        if (company is null || company.Status != "ACTIVE") return Reject(Error("COMPANY.NOT_ACTIVE", "P2", "/company_id", ("status", company?.Status)));
        DateOnly date = reversalDateRequest ?? original.PostingDate;
        var period = await JournalDocumentPoster.CheckPeriodAsync(tx, tenantId, companyId, date, ctx, false, ct);
        if (period.Errors.Count > 0) return CommandResult<StockDocumentResult>.Rejected(period.Errors.Select(e => e with { Path = "/reversal_date" }).ToList());
        Guid ledgerId = await ClearingPoster.ReadDefaultLedgerAsync(tx, tenantId, companyId, ct) ?? Guid.Empty;
        var docSeries = await JournalDocumentPoster.ReadSeriesAsync(tx, tenantId, companyId, "STOCK_DOC_REVERSAL", date, ledgerId, ct);
        if (docSeries is null) return Reject(Error("SERIES.NO_ASSIGNMENT", "P2", "/reversal_date", ("document_type", "STOCK_DOC_REVERSAL"), ("posting_date", date)));
        var journalSeries = original.JournalId is null ? null : await JournalDocumentPoster.ReadSeriesAsync(tx, tenantId, companyId, "STOCK_REVERSAL", date, ledgerId, ct);
        if (original.JournalId is not null && journalSeries is null)
            return Reject(Error("SERIES.NO_ASSIGNMENT", "P2", "/reversal_date", ("document_type", "STOCK_REVERSAL"), ("posting_date", date)));
        string? groupCurrency = await ClearingPoster.ReadGroupCurrencyAsync(tx, tenantId, companyId, date, ct);
        if (groupCurrency is null) return Reject(Error("CONFIG.GROUP_MEMBERSHIP_MISSING", "P2", "/company_id", ("posting_date", date)));
        int fiscalYear = period.FiscalYear!.Value, periodNo = period.PeriodNo!.Value;

        var movements = new List<OriginalMovement>();
        await using (var cmd = tx.Command(
            "select line_no, movement_type, cost_source, item_id, warehouse_id, location_id, quantity, unit_code, base_quantity, value_lc, value_tc, source_line_ref, line_text, valuation_ref_document_id, valuation_ref_line_no from core.stock_movement " +
            "where tenant_id = @t and company_id = @c and stock_document_id = @d order by line_no"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", originalId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                movements.Add(new OriginalMovement(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetGuid(3), r.GetGuid(4), r.IsDBNull(5) ? null : r.GetGuid(5), r.GetDecimal(6), r.GetString(7).Trim(),
                    r.GetDecimal(8), r.GetDecimal(9), r.IsDBNull(10) ? null : r.GetDecimal(10), r.IsDBNull(11) ? null : r.GetString(11), r.IsDBNull(12) ? null : r.GetString(12),
                    r.IsDBNull(13) ? null : r.GetGuid(13), r.IsDBNull(14) ? null : r.GetInt32(14)));
        }
        bool hasOpenItems = false;
        if (original.JournalId is { } journalOfOriginal)
            await using (var cmd = tx.Command("select exists (select 1 from core.journal_line_open_item where tenant_id = @t and company_id = @c and document_id = @d)"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", journalOfOriginal);
                hasOpenItems = (bool)(await cmd.ExecuteScalarAsync(ct))!;
            }

        // P7: the locks, in the order of 10, 6.4 with the CLEARING head after the LEDGER head: the item rows, the counters, LEDGER, CLEARING, STOCK, then the original and the period rows
        await using (var cmd = tx.Command("select item_id from core.item_company where tenant_id = @t and company_id = @c and item_id = any(@ids) order by item_id for update"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("ids", movements.Select(m => m.ItemId).Distinct().ToArray());
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) { }
        }
        var counters = new List<(JournalDocumentPoster.SeriesRow, int)> { (docSeries, fiscalYear) };
        if (journalSeries is not null) counters.Add((journalSeries, fiscalYear));
        if (await ClosingDocuments.LockCountersAsync(tx, tenantId, companyId, counters, ct) is { } missing) return Reject(missing);
        ClosingLedger ledger = (await ClosingDocuments.OpenLedgerAsync(tx, tenantId, companyId, ledgerId, company, groupCurrency, ct))!;
        long clearingSeq = 0; byte[] clearingHash = [];
        if (hasOpenItems)
            (clearingSeq, clearingHash, _) = await CommandSupport.LockOrCreateHeadAsync(tx, tenantId, companyId, ClearingHash2.ChainType, "", LedgerHash2.Genesis(tenantId, companyId, ClearingHash2.ChainType, ""), ct);
        var (stockSeq, stockPrev, _) = await CommandSupport.LockOrCreateHeadAsync(tx, tenantId, companyId, StockHash2.ChainType, "", LedgerHash2.Genesis(tenantId, companyId, StockHash2.ChainType, ""), ct);
        if ((await ReadOriginalAsync(true))!.Status != "posted") return Reject(Error("REVERSAL.ALREADY_REVERSED", "P7", "/stock_document_id"));
        var recheck = await JournalDocumentPoster.CheckPeriodAsync(tx, tenantId, companyId, date, ctx, true, ct);
        if (recheck.Errors.Count > 0) return CommandResult<StockDocumentResult>.Rejected(recheck.Errors.Select(e => e with { Path = "/reversal_date" }).ToList());
        // an owning command that cleared items of the original itself (a receipt that covered an invoice) resets those clearings first, under the CLEARING head
        if (options?.ResetClearings is { } resetClearings && hasOpenItems) (clearingSeq, clearingHash) = await resetClearings(tx, date, fiscalYear, periodNo, clearingSeq, clearingHash, ct);

        // P4 to P6 under the locks: the mirror of each movement and the running balances. What a receipt brought must still be there.
        var cost = new Dictionary<Guid, (decimal Qty, decimal Value)>();
        var stock = new Dictionary<(Guid, Guid, Guid?), decimal>();
        var mirrors = new List<Mirror>();
        foreach (OriginalMovement m in movements)
        {
            if (!cost.ContainsKey(m.ItemId)) cost[m.ItemId] = await CostBalanceAsync(tx, tenantId, companyId, m.ItemId, ct);
            var skey = (m.ItemId, m.WarehouseId, m.LocationId);
            if (!stock.ContainsKey(skey)) stock[skey] = await StockBalanceAsync(tx, tenantId, companyId, m.ItemId, m.WarehouseId, m.LocationId, ct);
            decimal baseQty = -m.BaseQuantity, value = -m.ValueLc;
            (decimal costQty, decimal costValue) = cost[m.ItemId];
            costQty += baseQty; costValue += value; stock[skey] += baseQty;
            if (baseQty < 0m && (costQty < 0m || costValue < 0m || (costQty == 0m && costValue != 0m) || stock[skey] < 0m))
                return Reject(Error("INVENTORY.RECEIPT_CONSUMED", "P4", $"/movements/{m.LineNo}", ("item_id", m.ItemId), ("on_hand", cost[m.ItemId].Qty), ("reversed", -baseQty)));
            cost[m.ItemId] = (costQty, costValue);
            mirrors.Add(new Mirror(m, -m.Quantity, baseQty, value, m.ValueTc is { } vt ? -vt : null, costQty, costValue, stock[skey]));
        }

        // the derived journal: the mirror of the journal of the original (ADR-048), its GR/IR open item cleared against the original item
        Guid? mirrorJournalId = original.JournalId is null ? null : Guid.NewGuid();
        LedgerReader.StoredDocument? originalJournal = null;
        var journalLines = new List<NewLine>();
        if (original.JournalId is { } oj)
        {
            originalJournal = (await LedgerReader.ReadDocumentsAsync(tx, tenantId, oj, null, null, ct)).FirstOrDefault();
            if (originalJournal is null) return CommandResult<StockDocumentResult>.Failed(Error("SYSTEM.UNAVAILABLE", "P7", "/stock_document_id", ("journal_document_id", oj)));
            if (originalJournal.OpenItems.Count > 0)
            {
                await using (var lockCmd = tx.Command("select line_no from core.journal_line_open_item where tenant_id = @t and company_id = @c and document_id = @d order by line_no for update"))
                {
                    lockCmd.Parameters.AddWithValue("t", tenantId); lockCmd.Parameters.AddWithValue("c", companyId); lockCmd.Parameters.AddWithValue("d", oj);
                    await using var lr = await lockCmd.ExecuteReaderAsync(ct);
                    while (await lr.ReadAsync(ct)) { }
                }
                var cleared = new List<int>();
                await using (var cmd = tx.Command(
                    "select line_no from core.journal_line_open_item where tenant_id = @t and company_id = @c and document_id = @d " +
                    "and (clearing_status <> 'OPEN' or cleared_amount_tc <> 0 or cleared_amount_lc <> 0 or cleared_amount_gc <> 0 or coalesce(cleared_amount_rc, 0) <> 0) order by line_no"))
                {
                    cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", oj);
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct)) cleared.Add(r.GetInt32(0));
                }
                if (cleared.Count > 0) return Reject(Error("REVERSAL.HAS_CLEARED_LINES", "P6", "/stock_document_id", ("line_nos", cleared.ToArray())));
            }
            journalLines = (await ClosingDocuments.MirrorLinesAsync(tx, tenantId, companyId, originalJournal, ct))
                .Select(l => originalJournal.OpenItems.TryGetValue(l.ReversedLineNo!.Value, out var oi)
                    ? l with { OpenItem = new OpenItemSpec(oi["partner_id"] as Guid?, originalJournal.OpenItemDueDates.GetValueOrDefault(l.ReversedLineNo!.Value), null, null, null, null) }
                    : l).ToList();
        }

        // the number, the movements and the hash of the mirror stock document
        DateTime at = Now();
        long sequenceNo;
        await using (var cmd = tx.Command("update core.number_range_counter set last_number = last_number + 1, updated_at = now() where tenant_id = @t and company_id = @c and series_id = @s and fiscal_year = @y returning last_number"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("s", docSeries.SeriesId); cmd.Parameters.AddWithValue("y", fiscalYear);
            sequenceNo = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }
        string number = Core.Posting.PostingRules.FormatNumber(docSeries.Prefix, docSeries.IncludeFiscalYear, docSeries.Separator, docSeries.NumberLength, fiscalYear, sequenceNo);
        Guid stockId = Guid.NewGuid();
        long chainSeq = stockSeq + 1;
        string? currencyTc = original.CurrencyTc;
        var movementRows = mirrors.Select((m, i) => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["tenant_id"] = tenantId, ["company_id"] = companyId, ["stock_document_id"] = stockId, ["line_no"] = i + 1, ["chain_seq"] = chainSeq, ["movement_type"] = m.From.MovementType,
            ["cost_source"] = m.From.CostSource, ["valuation_ref_document_id"] = m.From.RefDocument, ["valuation_ref_line_no"] = m.From.RefLine, ["posting_date"] = date, ["item_id"] = m.From.ItemId,
            ["warehouse_id"] = m.From.WarehouseId, ["location_id"] = m.From.LocationId, ["tracking_id"] = null, ["stock_status"] = "AVAILABLE", ["quantity"] = m.Quantity,
            ["unit_code"] = m.From.UnitCode, ["base_quantity"] = m.BaseQuantity, ["value_lc"] = m.ValueLc, ["unit_cost_lc"] = StockValuation.UnitCost(m.ValueLc, m.BaseQuantity),
            ["value_tc"] = m.ValueTc, ["currency_tc"] = m.ValueTc is null ? null : currencyTc, ["cost_warehouse_id"] = null, ["cost_qty_after"] = m.CostQtyAfter,
            ["cost_value_after_lc"] = m.CostValueAfter, ["stock_qty_after"] = m.StockQtyAfter, ["lot_expired"] = false, ["count_ref"] = null,
            ["expected_book_quantity"] = null, ["source_line_ref"] = m.From.LineRef, ["reversal_of_line_no"] = m.From.LineNo
        }).ToList();
        byte[] movementDigest = StockHash2.MovementDigest(movementRows);
        byte[]? detailDigest = options?.DetailDigest is { } provide
            ? await provide(new StockDerivation(stockId, mirrorJournalId, mirrors.Select((m, i) => new DerivedMovement(i + 1, m.From.LineNo - 1, m.BaseQuantity, m.ValueLc)).ToList()))
            : null;
        var body = new Dictionary<string, object?>
        {
            ["stock_document_id"] = stockId, ["document_type"] = "STOCK_REVERSAL", ["number_series_id"] = docSeries.SeriesId, ["fiscal_year"] = fiscalYear, ["sequence_no"] = sequenceNo,
            ["document_number"] = number, ["fiscal_period"] = periodNo, ["document_date"] = date, ["posting_date"] = date, ["posted_at"] = at,
            ["reason_code"] = reasonCode, ["reversal_of_stock_document_id"] = originalId, ["journal_document_id"] = mirrorJournalId, ["soft_close_override"] = recheck.SoftCloseOverride,
            ["currency_tc"] = currencyTc, ["source_system"] = ctx.SourceSystem, ["source_document_ref"] = null, ["idempotency_key"] = ctx.IdempotencyKey,
            ["command_name"] = commandName, ["posted_by"] = ctx.Actor, ["correlation_id"] = ctx.CorrelationId, ["movement_count"] = mirrors.Count, ["detail_digest"] = detailDigest,
            ["movement_digest"] = movementDigest, ["hash_version"] = StockHash2.Version
        };
        byte[] rowHash = StockHash2.RowHash(tenantId, companyId, chainSeq, stockPrev, body);

        // the journal mirror first (it binds the stock hash), then the stock document and its movements
        PostedDocument? journal = null;
        List<DocumentReverser.AutoClearing> autoClearings = [];
        if (mirrorJournalId is { } mj && originalJournal is not null)
        {
            var posted = await ClosingDocuments.PostAsync(ledger, ctx with { SourceSystem = "INVENTORY" },
                new DocumentSpec("STOCK_REVERSAL", commandName, journalSeries!, fiscalYear, periodNo, recheck.SoftCloseOverride, date, ctx.IdempotencyKey + "#J",
                    ReversalOf: original.JournalId, ReasonCode: reasonCode, DetailDigest: rowHash, Origin: "STOCK", TransactionCurrency: (string)originalJournal.Header["transaction_currency"]!, DocumentId: mj),
                journalLines, original.JournalId, ct);
            if (posted.Error is not null) return Reject(posted.Error);
            journal = posted.Document;
            await using (var cmd = tx.Command(
                "update core.journal_document set status = 'reversed', reversed_by_document_id = @m where tenant_id = @t and company_id = @c and document_id = @d and status = 'posted'"))
            {
                cmd.Parameters.AddWithValue("m", mj); cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", original.JournalId!.Value);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            if (originalJournal.OpenItems.Count > 0)
                autoClearings = await DocumentReverser.ClearOpenItemsAsync(tx, tenantId, companyId, ctx, originalJournal, mj, [], date, fiscalYear, periodNo, at, clearingSeq, clearingHash, ct, commandName);
        }
        await using (var cmd = tx.Command(
            "insert into core.stock_document (tenant_id, company_id, stock_document_id, document_type, number_series_id, fiscal_year, sequence_no, document_number, fiscal_period, document_date, " +
            "posting_date, posted_at, reason_code, reversal_of_stock_document_id, journal_document_id, soft_close_override, currency_tc, source_system, idempotency_key, command_name, posted_by, " +
            "correlation_id, movement_count, detail_digest, movement_digest, chain_seq, prev_hash, row_hash, hash_version) values (@t, @c, @id, 'STOCK_REVERSAL', @s, @fy, @seq, @num, @fp, @dd, @pd, @at, " +
            "@reason, @orig, @j, @soft, @tc, @src, @key, @cmdname, @by, @corr, @n, @dd2, @md, @cs, @prev, @row, 2)"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("id", stockId);
            cmd.Parameters.AddWithValue("s", docSeries.SeriesId); cmd.Parameters.AddWithValue("fy", fiscalYear); cmd.Parameters.AddWithValue("seq", sequenceNo); cmd.Parameters.AddWithValue("num", number);
            cmd.Parameters.AddWithValue("fp", periodNo); cmd.Parameters.AddWithValue("dd", date); cmd.Parameters.AddWithValue("pd", date);
            cmd.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = at });
            cmd.Parameters.AddWithValue("reason", reasonCode); cmd.Parameters.AddWithValue("orig", originalId);
            cmd.Parameters.Add(new NpgsqlParameter("j", NpgsqlDbType.Uuid) { Value = (object?)mirrorJournalId ?? DBNull.Value });
            cmd.Parameters.AddWithValue("soft", recheck.SoftCloseOverride);
            cmd.Parameters.Add(new NpgsqlParameter("tc", NpgsqlDbType.Text) { Value = (object?)currencyTc ?? DBNull.Value });
            cmd.Parameters.AddWithValue("src", ctx.SourceSystem); cmd.Parameters.AddWithValue("key", ctx.IdempotencyKey); cmd.Parameters.AddWithValue("by", ctx.Actor);
            cmd.Parameters.AddWithValue("corr", ctx.CorrelationId); cmd.Parameters.AddWithValue("cmdname", commandName);
            cmd.Parameters.Add(new NpgsqlParameter("dd2", NpgsqlDbType.Bytea) { Value = (object?)detailDigest ?? DBNull.Value });
            cmd.Parameters.AddWithValue("n", mirrors.Count); cmd.Parameters.AddWithValue("md", movementDigest); cmd.Parameters.AddWithValue("cs", chainSeq);
            cmd.Parameters.AddWithValue("prev", stockPrev); cmd.Parameters.AddWithValue("row", rowHash);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        for (int i = 0; i < mirrors.Count; i++)
        {
            Mirror m = mirrors[i];
            await using var cmd = tx.Command(
                "insert into core.stock_movement (tenant_id, company_id, stock_document_id, line_no, chain_seq, movement_type, cost_source, posting_date, item_id, warehouse_id, location_id, stock_status, " +
                "quantity, unit_code, base_quantity, value_lc, unit_cost_lc, value_tc, currency_tc, cost_qty_after, cost_value_after_lc, stock_qty_after, lot_expired, source_line_ref, reversal_of_line_no, " +
                "line_text, valuation_ref_document_id, valuation_ref_line_no) values (@t, @c, @d, @n, @cs, @type, @src, @pd, @i, @w, @l, 'AVAILABLE', @q, @u, @bq, @v, @uc, @vt, @ct, @cq, @cv, @sq, false, @ref, @rl, @text, @rd, @rn)");
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", stockId); cmd.Parameters.AddWithValue("n", i + 1);
            cmd.Parameters.AddWithValue("cs", chainSeq); cmd.Parameters.AddWithValue("type", m.From.MovementType); cmd.Parameters.AddWithValue("src", m.From.CostSource);
            cmd.Parameters.AddWithValue("pd", date); cmd.Parameters.AddWithValue("i", m.From.ItemId); cmd.Parameters.AddWithValue("w", m.From.WarehouseId);
            cmd.Parameters.Add(new NpgsqlParameter("l", NpgsqlDbType.Uuid) { Value = (object?)m.From.LocationId ?? DBNull.Value });
            cmd.Parameters.AddWithValue("q", m.Quantity); cmd.Parameters.AddWithValue("u", m.From.UnitCode); cmd.Parameters.AddWithValue("bq", m.BaseQuantity); cmd.Parameters.AddWithValue("v", m.ValueLc);
            cmd.Parameters.Add(new NpgsqlParameter("uc", NpgsqlDbType.Numeric) { Value = (object?)StockValuation.UnitCost(m.ValueLc, m.BaseQuantity) ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("vt", NpgsqlDbType.Numeric) { Value = (object?)m.ValueTc ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("ct", NpgsqlDbType.Text) { Value = m.ValueTc is null ? DBNull.Value : currencyTc ?? company.LocalCurrency });
            cmd.Parameters.AddWithValue("cq", m.CostQtyAfter); cmd.Parameters.AddWithValue("cv", m.CostValueAfter); cmd.Parameters.AddWithValue("sq", m.StockQtyAfter);
            cmd.Parameters.Add(new NpgsqlParameter("ref", NpgsqlDbType.Text) { Value = (object?)m.From.LineRef ?? DBNull.Value });
            cmd.Parameters.AddWithValue("rl", m.From.LineNo);
            cmd.Parameters.Add(new NpgsqlParameter("text", NpgsqlDbType.Text) { Value = (object?)m.From.LineText ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("rd", NpgsqlDbType.Uuid) { Value = (object?)m.From.RefDocument ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("rn", NpgsqlDbType.Integer) { Value = (object?)m.From.RefLine ?? DBNull.Value });
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using (var cmd = tx.Command(
            "update core.stock_document set status = 'reversed', reversed_by_stock_document_id = @m where tenant_id = @t and company_id = @c and stock_document_id = @d and status = 'posted'"))
        {
            cmd.Parameters.AddWithValue("m", stockId); cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", originalId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await ClearingPoster.MoveHeadAsync(tx, tenantId, companyId, StockHash2.ChainType, "", chainSeq, rowHash, StockHash2.Version, ct);
        Guid logId = options?.WriteLog == false ? Guid.Empty : await CommandSupport.WriteLogAsync(tx, tenantId, companyId, ctx, commandName, fingerprint, "STOCK_DOCUMENT", stockId, ct);

        // the events (15, 6): the journal mirror, the clearings, the stock reversal and a balance change per stock key
        if (journal is not null) await ClosingDocuments.WriteDocumentEventAsync(ledger, ctx, journal, "ledger.document_reversed", original.JournalId!.Value, "REVERSED", ct);
        foreach (var ac in autoClearings)
        {
            await CommandSupport.WriteOutboxAsync(tx, tenantId, companyId, ctx, "clearing.posted", "CLEARING", $"{companyId:D}/{ac.ClearingId:D}", "POSTED", at, isAudit: true,
                JsonSerializer.Serialize(new Dictionary<string, object?> { ["chain_type"] = "CLEARING", ["scope"] = "", ["chain_seq"] = ac.Seq, ["row_hash"] = Canonical.FormatHex32(ac.RowHash) }),
                JsonSerializer.Serialize(new Dictionary<string, object?> { ["clearing_id"] = ac.ClearingId, ["partner_id"] = ac.PartnerId, ["account_id"] = ac.AccountId, ["items"] = ac.Items.Count }), ct);
            foreach (var item in ac.Items)
                await CommandSupport.WriteOutboxAsync(tx, tenantId, companyId, ctx, "open_item.cleared", "OPEN_ITEM", $"{companyId:D}/{item.DocumentId:D}/{item.LineNo}", "CLEARED", at, isAudit: false, null,
                    JsonSerializer.Serialize(new Dictionary<string, object?>
                    {
                        ["account_id"] = ac.AccountId, ["partner_id"] = ac.PartnerId, ["due_date"] = null, ["amount_tc"] = item.Tc.ToString("F4", CultureInfo.InvariantCulture),
                        ["currency"] = item.Currency, ["clearing_id"] = ac.ClearingId, ["status"] = "CLEARED"
                    }), ct);
        }
        await CommandSupport.WriteOutboxAsync(tx, tenantId, companyId, ctx, "stock.document_reversed", "STOCK_DOCUMENT", $"{companyId:D}/{originalId:D}", "REVERSED", at, isAudit: true,
            JsonSerializer.Serialize(new Dictionary<string, object?> { ["chain_type"] = "STOCK", ["scope"] = "", ["chain_seq"] = chainSeq, ["row_hash"] = Canonical.FormatHex32(rowHash) }),
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["document_type"] = "STOCK_REVERSAL", ["posting_date"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["movement_count"] = mirrors.Count,
                ["journal_document_id"] = mirrorJournalId, ["reversal_of_stock_document_id"] = originalId
            }), ct);
        foreach (var last in mirrors.GroupBy(m => (m.From.ItemId, m.From.WarehouseId, m.From.LocationId)).Select(g => g.Last()))
            await CommandSupport.WriteOutboxAsync(tx, tenantId, companyId, ctx, "stock.balance_changed", "STOCK_BALANCE", $"{companyId:D}/{last.From.ItemId:D}/{last.From.WarehouseId:D}", "CHANGED", at, isAudit: false, null,
                JsonSerializer.Serialize(new Dictionary<string, object?> { ["item_id"] = last.From.ItemId, ["warehouse_id"] = last.From.WarehouseId, ["location_id"] = last.From.LocationId, ["quantity_after"] = last.StockQtyAfter }), ct);

        StockDocumentResult result = await LoadResultAsync(tx, tenantId, companyId, stockId, ct);
        result = result with { Movements = result.Movements.Select(m => m with { LineRef = mirrors[m.LineNo - 1].From.LineRef }).ToList() };
        if (options?.Commit != false) await tx.CommitAsync(ct);   // the deferred checks: the journal balances, the chain links and the head, the movement count, the outbox position
        return CommandResult<StockDocumentResult>.Ok(result, logId);
    }

}
