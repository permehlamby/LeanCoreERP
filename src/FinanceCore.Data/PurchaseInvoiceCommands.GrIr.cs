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
/// gr-ir:write-off and gr-ir:reverse-write-off (11, 7.5, 7.5a). A write-off clears GR/IR residuals that no supplier invoice will match: a receipt that is never invoiced, an invoice debit without
/// a further receipt, a return that no credit note clears. The items are named as open items or through an order line (the receipts and returns of the line); the caller states the residual in local
/// currency that it saw (ORDER.GRIR_RESIDUAL_CHANGED when it is another, ORDER.GRIR_ITEM_INVALID when an item is not an open GR/IR item of the company). One transaction of the posting role: the
/// order rows first, then the pipeline (a GR_IR_WRITE_OFF document with origin ORDER: a GR/IR line of the opposite sign per residual and the counter line on the account of the role
/// GR_IR_WRITE_OFF, an account without open items), then the residual items in id order, read again under the lock, and one clearing of method AUTO_MATCH per GR/IR account. No stock movement
/// and no change of the order progress. The reversal resets that clearing and reverses the document (the owning command, the reason is CORRECTION or ERROR). Built choices are in 07, 13.2aj. Not
/// built: the direct receipt selector (LIMIT.GRIR_DIRECT_RECEIPT_NOT_BUILT), foreign currency, gr-ir:query.
/// </summary>
public sealed partial class PurchaseInvoiceCommands
{
    private const string WriteOffCommandName = "gr-ir:write-off";
    private const string WriteOffReverseCommandName = "gr-ir:reverse-write-off";

    private sealed record Residual(Guid Doc, int Line, Guid Account, string Currency, decimal Tc, decimal Lc, decimal Gc, decimal? Rc, Guid? ItemGroup, Guid? Warehouse, Guid? Item);

    public async Task<CommandResult<GrIrWriteOffResult>> WriteOffAsync(Guid tenantId, CommandContext ctx, GrIrWriteOffRequest req, CancellationToken ct = default)
    {
        static CommandResult<GrIrWriteOffResult> Fail(CommandError e) => CommandResult<GrIrWriteOffResult>.Rejected([e]);
        if (!ctx.Permissions.Contains(GrIrRules.Permission)) return Fail(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", GrIrRules.Permission)));
        var errors = MasterSupport.ValidateContext(ctx);
        void Invalid(string path, string reason) => errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", path, ("reason", reason)));
        if (req.CompanyId == Guid.Empty) Invalid("/company_id", "required");
        if (string.IsNullOrWhiteSpace(req.ReasonCode) || req.ReasonCode.Length > 50) Invalid("/reason_code", "required, at most 50 characters");
        if (string.IsNullOrWhiteSpace(req.ReasonText) || req.ReasonText.Length > 200) Invalid("/reason_text", "required, at most 200 characters");
        if (req.Items is null or { Count: 0 }) errors.Add(Error("SCHEMA.REQUIRED_FIELD_MISSING", "P1", "/items"));
        for (int i = 0; i < (req.Items?.Count ?? 0); i++)
        {
            GrIrWriteOffItem it = req.Items![i];
            int selectors = (it.OpenItems is { Count: > 0 } ? 1 : 0) + (it.PurchaseOrderId is not null || it.PurchaseOrderLineNo is not null ? 1 : 0) + (it.ReceiptRef is not null ? 1 : 0);
            if (selectors != 1) Invalid($"/items/{i}", "exactly one of open_items, purchase_order_id with purchase_order_line_no, receipt_ref");
            else if ((it.PurchaseOrderId is null) != (it.PurchaseOrderLineNo is null)) Invalid($"/items/{i}/purchase_order_line_no", "an order and a line number");
            else if (it.ReceiptRef is not null) errors.Add(Error("LIMIT.GRIR_DIRECT_RECEIPT_NOT_BUILT", "P1", $"/items/{i}/receipt_ref"));
            if (Math.Round(it.ExpectedResidualLc, 4) != it.ExpectedResidualLc) Invalid($"/items/{i}/expected_residual_lc", "at most 4 fraction digits");
        }
        if (errors.Count > 0) return CommandResult<GrIrWriteOffResult>.Rejected(errors);
        byte[] fingerprint = GrIrRules.Fingerprint(req);

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Post, ct: ct);
                CommandSupport.EarlierCommand? earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
                if (earlier is not null)
                {
                    if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint)) return Fail(Error("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
                    return earlier.EntityId is null
                        ? CommandResult<GrIrWriteOffResult>.Failed(Error("SYSTEM.REPLAY_RESULT_MISSING", "P1", ""))
                        : CommandResult<GrIrWriteOffResult>.Ok(await LoadWriteOffAsync(tx, tenantId, req.CompanyId, earlier.EntityId.Value, ct), earlier.CommandLogId, replay: true);
                }
                return await WriteOffCoreAsync(tx, tenantId, ctx, req, fingerprint, ct);
            }
            catch (MatchRejection ex)
            {
                return Fail(ex.Error);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.TableName == "command_log" && attempt == 0)
            {
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                return CommandResult<GrIrWriteOffResult>.Failed(new CommandError("SYSTEM.LOCK_TIMEOUT", "P7", "", null, true));
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure)
            {
                return CommandResult<GrIrWriteOffResult>.Failed(new CommandError("SYSTEM.CONFLICT", "P7", "", null, true));
            }
        }
    }

    private async Task<CommandResult<GrIrWriteOffResult>> WriteOffCoreAsync(
        TenantTransaction tx, Guid tenantId, CommandContext ctx, GrIrWriteOffRequest req, byte[] fingerprint, CancellationToken ct)
    {
        static CommandResult<GrIrWriteOffResult> Fail(CommandError e) => CommandResult<GrIrWriteOffResult>.Rejected([e]);
        Guid companyId = req.CompanyId;
        var company = await JournalDocumentPoster.ReadCompanyAsync(tx, tenantId, companyId, ct);
        if (company is null) return Fail(Error("CONFIG.INVALID_REFERENCE", "P2", "/company_id", ("company_id", companyId)));
        if (company.Status != "ACTIVE") return Fail(Error("COMPANY.NOT_ACTIVE", "P2", "/company_id", ("status", company.Status)));

        // position 1 of the lock order: the order rows of the selected lines, sorted
        var orderIds = req.Items.Where(i => i.PurchaseOrderId is not null).Select(i => i.PurchaseOrderId!.Value).Distinct().Order().ToArray();
        if (orderIds.Length > 0)
            await using (var cmd = tx.Command("select purchase_order_id from core.purchase_order where tenant_id = @t and company_id = @c and purchase_order_id = any(@ids) order by purchase_order_id for update"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("ids", orderIds);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) { }
            }

        // the residuals of every entry, read now and again under the lock
        var planned = new List<Residual>();
        var seen = new HashSet<(Guid, int)>();
        for (int i = 0; i < req.Items.Count; i++)
        {
            GrIrWriteOffItem it = req.Items[i];
            var found = new List<Residual>();
            if (it.OpenItems is { Count: > 0 })
            {
                foreach (GrIrItemRef r in it.OpenItems)
                {
                    Residual? x = await ReadResidualAsync(tx, tenantId, companyId, r.DocumentId, r.LineNo, null, null, null, false, ct);
                    if (x is null) return Fail(Error("ORDER.GRIR_ITEM_INVALID", "P2", $"/items/{i}/open_items", ("document_id", r.DocumentId), ("line_no", r.LineNo)));
                    found.Add(x);
                }
            }
            else
            {
                OrderLine? ol = await ReadOrderLineAsync(tx, tenantId, companyId, it.PurchaseOrderId!.Value, it.PurchaseOrderLineNo!.Value, ct);
                if (ol is null) return Fail(Error("CONFIG.INVALID_REFERENCE", "P2", $"/items/{i}/purchase_order_id", ("order_id", it.PurchaseOrderId)));
                foreach (ReceiptLine rl in await ReadReceiptLinesAsync(tx, tenantId, companyId, it.PurchaseOrderId.Value, it.PurchaseOrderLineNo.Value, ct))
                    if (rl.StockDocumentId is { } sd && rl.StockDocumentLineNo is { } sl)
                        await AddResidualAsync(sd, sl);
                var returns = new List<(Guid Stock, int Line)>();
                await using (var cmd = tx.Command(
                    "select h.stock_document_id, x.stock_document_line_no from core.purchase_return_line x join core.purchase_return h on h.tenant_id = x.tenant_id and h.company_id = x.company_id and h.return_id = x.return_id " +
                    "where x.tenant_id = @t and x.company_id = @c and x.purchase_order_id = @o and x.purchase_order_line_no = @n and h.status = 'posted' and h.reversal_of_return_id is null and h.stock_document_id is not null and x.stock_document_line_no is not null order by h.chain_seq, x.line_no"))
                {
                    cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("o", it.PurchaseOrderId.Value); cmd.Parameters.AddWithValue("n", it.PurchaseOrderLineNo.Value);
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct)) returns.Add((r.GetGuid(0), r.GetInt32(1)));
                }
                foreach (var (sd, sl) in returns) await AddResidualAsync(sd, sl);

                async Task AddResidualAsync(Guid stockDocument, int stockLine)
                {
                    Guid journal = await StockJournalAsync(tx, tenantId, companyId, stockDocument, ct);
                    int? line = await FindGrIrItemAsync(tx, tenantId, companyId, new ReceiptLine(Guid.Empty, 0, 0m, null, stockDocument, stockLine, default), journal, ct);
                    if (line is null) return;
                    Residual? x = await ReadResidualAsync(tx, tenantId, companyId, journal, line.Value, ol.ItemGroup, ol.Warehouse, ol.ItemId, false, ct);
                    if (x is not null) found.Add(x);   // a fully cleared item is not part of the residual
                }
            }
            if (found.Count == 0) return Fail(Error("ORDER.GRIR_ITEM_INVALID", "P2", $"/items/{i}", ("reason", "no open GR/IR item is selected")));
            decimal residual = found.Sum(x => x.Lc);
            if (residual != it.ExpectedResidualLc) return Fail(Error("ORDER.GRIR_RESIDUAL_CHANGED", "P4", $"/items/{i}/expected_residual_lc", ("expected_residual_lc", it.ExpectedResidualLc), ("current_residual_lc", residual)));
            foreach (Residual x in found)
            {
                if (!seen.Add((x.Doc, x.Line))) return Fail(Error("ORDER.GRIR_ITEM_INVALID", "P2", $"/items/{i}", ("reason", "the item is selected twice"), ("document_id", x.Doc), ("line_no", x.Line)));
                if (x.Currency != company.LocalCurrency) return Fail(Error("LIMIT.INVOICE_FOREIGN_CURRENCY_NOT_BUILT", "P2", $"/items/{i}", ("currency", x.Currency)));
                planned.Add(x);
            }
        }

        // the finance document: a GR/IR line of the opposite sign per residual, and the counter line per account of the role GR_IR_WRITE_OFF
        string text = $"{req.ReasonCode}: {req.ReasonText}";
        var lines = new List<RequestLine>();
        var lineNoOf = new Dictionary<(Guid, int), int>();
        foreach (Residual x in planned)
        {
            lines.Add(new RequestLine($"G{lines.Count + 1}", x.Account, -x.Tc, text, new OpenItemRequest(null, req.PostingDate)));
            lineNoOf[(x.Doc, x.Line)] = lines.Count;
        }
        var counters = new Dictionary<Guid, decimal>();
        foreach (Residual x in planned)
        {
            var account = await AccountDeterminationResolver.ResolveAsync(tx, tenantId, companyId, "GR_IR_WRITE_OFF", x.ItemGroup, x.Warehouse, null, x.Item, req.PostingDate, ct);
            if (account is null) return Fail(Error("INVENTORY.ACCOUNT_DETERMINATION_MISSING", "P5", "/items", ("role_code", "GR_IR_WRITE_OFF"), ("item_id", x.Item), ("warehouse_id", x.Warehouse)));
            counters[account.AccountId] = counters.GetValueOrDefault(account.AccountId) + x.Tc;
        }
        foreach (var (account, amount) in counters.OrderBy(c => c.Key)) lines.Add(new RequestLine($"W{lines.Count + 1}", account, amount, text));
        var journal = new PostJournalDocumentRequest(companyId, null, "GR_IR_WRITE_OFF", req.PostingDate, req.PostingDate, company.LocalCurrency, null, null, req.SourceDocumentRef, lines, null);

        var clearingIds = planned.Select(x => x.Account).Distinct().Order().ToDictionary(a => a, _ => Guid.NewGuid());
        Guid journalId = Guid.NewGuid(), logId = Guid.Empty;
        async Task AfterAsync(TenantTransaction t, PostedDocument posted, JournalDocumentPoster.EmbeddedFacts facts)
        {
            // the residual items, locked in id order and read again: they must be what the lines were made from
            var locked = new Dictionary<(Guid, int), Residual>();
            foreach (Residual x in planned.OrderBy(x => x.Doc).ThenBy(x => x.Line))
            {
                Residual? now = await ReadResidualAsync(t, tenantId, companyId, x.Doc, x.Line, x.ItemGroup, x.Warehouse, x.Item, true, ct);
                if (now is null || now.Tc != x.Tc || now.Lc != x.Lc || now.Gc != x.Gc || now.Rc != x.Rc) throw new MatchRejection(new CommandError("SYSTEM.CONFLICT", "P7", "", null, true));
                locked[(x.Doc, x.Line)] = now;
            }
            (long headSeq, byte[] headHash, _) = await CommandSupport.LockOrCreateHeadAsync(t, tenantId, companyId, ClearingHash2.ChainType, "", LedgerHash2.Genesis(tenantId, companyId, ClearingHash2.ChainType, ""), ct);
            DateTime at = DateTime.UtcNow;
            at = new DateTime(at.Ticks - at.Ticks % 10, DateTimeKind.Utc);
            int number = 0;
            foreach (var (account, clearingId) in clearingIds)
            {
                number++;
                // the residual items are cleared in full, the new lines by the opposite amounts, so the items net to zero in every currency
                var items = new List<(Guid Doc, int Line, string Currency, decimal Tc, decimal Lc, decimal Gc, decimal? Rc)>();
                foreach (Residual x in planned.Where(x => x.Account == account).OrderBy(x => x.Doc).ThenBy(x => x.Line)) items.Add((x.Doc, x.Line, x.Currency, x.Tc, x.Lc, x.Gc, x.Rc));
                foreach (Residual x in planned.Where(x => x.Account == account))
                {
                    PostedLine pl = posted.Lines.Single(l => l.LineNo == lineNoOf[(x.Doc, x.Line)]);
                    if (pl.AmountTc != -x.Tc || pl.AmountLc != -x.Lc) throw new MatchRejection(new CommandError("SYSTEM.CONFLICT", "P7", "", null, true));
                    items.Add((posted.DocumentId, pl.LineNo, pl.CurrencyLc, -x.Tc, -x.Lc, -x.Gc, x.Rc is null ? null : -x.Rc));
                }
                long seq = headSeq + 1;
                string baseKey = ctx.IdempotencyKey.Length > 90 ? ctx.IdempotencyKey[..90] : ctx.IdempotencyKey;
                CommandContext cctx = ctx with { IdempotencyKey = $"{baseKey}#C{number}" };
                string matchingRef = posted.DocumentId.ToString("D");
                var body = new Dictionary<string, object?>
                {
                    ["clearing_id"] = clearingId, ["clearing_type"] = "CLEAR", ["clearing_date"] = req.PostingDate, ["fiscal_year"] = posted.FiscalYear, ["fiscal_period"] = posted.FiscalPeriod, ["cleared_at"] = at,
                    ["account_id"] = account, ["partner_id"] = null, ["document_id"] = null, ["reset_of_clearing_id"] = null, ["clearing_method"] = "AUTO_MATCH", ["matching_ref"] = matchingRef,
                    ["source_system"] = ctx.SourceSystem, ["idempotency_key"] = cctx.IdempotencyKey, ["command_name"] = WriteOffCommandName, ["cleared_by"] = ctx.Actor, ["correlation_id"] = ctx.CorrelationId
                };
                var itemRows = items.Select((i, k) => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                {
                    ["tenant_id"] = tenantId, ["company_id"] = companyId, ["clearing_id"] = clearingId, ["item_no"] = k + 1, ["clearing_date"] = req.PostingDate, ["open_item_document_id"] = i.Doc, ["open_item_line_no"] = i.Line,
                    ["currency_tc"] = i.Currency, ["cleared_amount_tc"] = i.Tc, ["cleared_amount_lc"] = i.Lc, ["cleared_amount_gc"] = i.Gc, ["cleared_amount_rc"] = i.Rc
                }).ToList();
                byte[] rowHash = ClearingHash2.RowHash(tenantId, companyId, seq, headHash, body, ClearingHash2.ItemsDigest(itemRows), null);
                var synthetic = new PostClearingRequest(companyId, account, null, req.PostingDate, "AUTO_MATCH", matchingRef, null, []);
                await ClearingPoster.InsertClearingAsync(t, tenantId, synthetic, cctx, clearingId, null, seq, posted.FiscalYear, posted.FiscalPeriod, at, headHash, rowHash, "CLEAR", null, WriteOffCommandName, ct);
                for (int k = 0; k < items.Count; k++)
                    await ClearingPoster.InsertItemAsync(t, tenantId, synthetic, clearingId, k + 1, items[k].Doc, items[k].Line, items[k].Currency, items[k].Tc, items[k].Lc, items[k].Gc, items[k].Rc, ct);
                // an item is cleared in full: the residual was all that was left of it
                foreach (var i in items)
                {
                    if (i.Doc == posted.DocumentId) await ClearingPoster.UpdateOpenItemAsync(t, tenantId, companyId, i.Doc, i.Line, clearingId, "CLEARED", i.Tc, i.Lc, i.Gc, i.Rc, ct);
                    else
                    {
                        Residual before = locked[(i.Doc, i.Line)];
                        (decimal lineTc, decimal lineLc, decimal lineGc, decimal? lineRc) = await ReadLineAmountsAsync(t, tenantId, companyId, i.Doc, i.Line, ct);
                        _ = before;
                        await ClearingPoster.UpdateOpenItemAsync(t, tenantId, companyId, i.Doc, i.Line, clearingId, "CLEARED", lineTc, lineLc, lineGc, lineRc, ct);
                    }
                }
                await ClearingPoster.MoveHeadAsync(t, tenantId, companyId, ClearingHash2.ChainType, "", seq, rowHash, ClearingHash2.Version, ct);
                await CommandSupport.WriteOutboxAsync(t, tenantId, companyId, ctx, "clearing.posted", "CLEARING", $"{companyId:D}/{clearingId:D}", "POSTED", at, isAudit: true,
                    JsonSerializer.Serialize(new Dictionary<string, object?> { ["chain_type"] = "CLEARING", ["scope"] = "", ["chain_seq"] = seq, ["row_hash"] = Canonical.FormatHex32(rowHash) }),
                    JsonSerializer.Serialize(new Dictionary<string, object?> { ["clearing_id"] = clearingId, ["partner_id"] = null, ["account_id"] = account, ["items"] = items.Count }), ct);
                headSeq = seq; headHash = rowHash;
            }
            logId = await CommandSupport.WriteLogAsync(t, tenantId, companyId, ctx, WriteOffCommandName, fingerprint, "GR_IR_WRITE_OFF", posted.DocumentId, ct);
            await CommandSupport.WriteOutboxAsync(t, tenantId, companyId, ctx, "gr_ir.written_off", "GR_IR_WRITE_OFF", $"{companyId:D}/{posted.DocumentId:D}", "WRITTEN_OFF", DateTime.UtcNow, isAudit: true, null,
                JsonSerializer.Serialize(new Dictionary<string, object?> { ["journal_document_id"] = posted.DocumentId, ["items"] = planned.Count, ["residual_lc"] = planned.Sum(x => x.Lc), ["reason_code"] = req.ReasonCode }), ct);
        }

        var embedding = new JournalDocumentPoster.PostEmbedding(WriteOffCommandName, "ORDER", null, null, WriteLog: false, Commit: false, AfterAsync, journalId);
        var postedResult = await _poster.PostCoreAsync(tx, tenantId, ctx, journal, fingerprint, embedding, ct);
        if (postedResult.Status != CommandStatus.Ok) return CommandResult<GrIrWriteOffResult>.Rejected(postedResult.Errors.ToList());
        PostedDocument doc = postedResult.Value!;
        var result = new GrIrWriteOffResult(doc.DocumentId, doc.DocumentNumber, planned.Sum(x => x.Lc), planned.Count, doc.RowHash, clearingIds.Values.ToList());
        await tx.CommitAsync(ct);
        return CommandResult<GrIrWriteOffResult>.Ok(result, logId, postedResult.Warnings);
    }

    /// <summary>The open GR/IR item of a line as it stands: the residual is the line amounts less the cleared amounts. Null when it is not an open item without partner on a GR/IR account of the company, or nothing is left.</summary>
    private static async Task<Residual?> ReadResidualAsync(
        TenantTransaction tx, Guid tenantId, Guid companyId, Guid document, int line, Guid? itemGroup, Guid? warehouse, Guid? item, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select l.account_id, d.transaction_currency, l.amount_tc - oi.cleared_amount_tc, l.amount_lc - oi.cleared_amount_lc, l.amount_gc - oi.cleared_amount_gc, " +
            "case when l.amount_rc is null then null else l.amount_rc - coalesce(oi.cleared_amount_rc, 0) end from core.journal_line_open_item oi " +
            "join core.journal_line l on l.tenant_id = oi.tenant_id and l.company_id = oi.company_id and l.document_id = oi.document_id and l.line_no = oi.line_no " +
            "join core.journal_document d on d.tenant_id = l.tenant_id and d.company_id = l.company_id and d.document_id = l.document_id " +
            "where oi.tenant_id = @t and oi.company_id = @c and oi.document_id = @d and oi.line_no = @n and oi.partner_id is null and exists (select 1 from core.account_determination a where a.tenant_id = l.tenant_id " +
            "and a.company_id = l.company_id and a.role_code = 'GR_IR' and a.account_id = l.account_id)" + (forUpdate ? " for update of oi" : ""));
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", document); cmd.Parameters.AddWithValue("n", line);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        decimal lc = r.GetDecimal(3);
        if (lc == 0m && r.GetDecimal(2) == 0m) return null;
        return new Residual(document, line, r.GetGuid(0), r.GetString(1).Trim(), r.GetDecimal(2), lc, r.GetDecimal(4), r.IsDBNull(5) ? null : r.GetDecimal(5), itemGroup, warehouse, item);
    }

    private static async Task<(decimal Tc, decimal Lc, decimal Gc, decimal? Rc)> ReadLineAmountsAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid document, int line, CancellationToken ct)
    {
        await using var cmd = tx.Command("select amount_tc, amount_lc, amount_gc, amount_rc from core.journal_line where tenant_id = @t and company_id = @c and document_id = @d and line_no = @n");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", document); cmd.Parameters.AddWithValue("n", line);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return (r.GetDecimal(0), r.GetDecimal(1), r.GetDecimal(2), r.IsDBNull(3) ? null : r.GetDecimal(3));
    }

    private static async Task<GrIrWriteOffResult> LoadWriteOffAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid document, CancellationToken ct)
    {
        string number; byte[] hash;
        await using (var cmd = tx.Command("select document_number, row_hash from core.journal_document where tenant_id = @t and company_id = @c and document_id = @d"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", document);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            number = r.GetString(0); hash = (byte[])r[1];
        }
        decimal total; long count;
        await using (var cmd = tx.Command("select coalesce(sum(l.amount_lc) filter (where l.amount_lc > 0 and oi.document_id is not null), 0), count(oi.document_id) from core.journal_line l left join core.journal_line_open_item oi on oi.tenant_id = l.tenant_id and oi.company_id = l.company_id and oi.document_id = l.document_id and oi.line_no = l.line_no where l.tenant_id = @t and l.company_id = @c and l.document_id = @d"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", document);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            total = r.GetDecimal(0); count = r.GetInt64(1);
        }
        var clearings = new List<Guid>();
        await using (var cmd = tx.Command("select clearing_id from core.clearing where tenant_id = @t and company_id = @c and matching_ref = @m and command_name = 'gr-ir:write-off' and clearing_type = 'CLEAR' order by chain_seq"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("m", document.ToString("D"));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) clearings.Add(r.GetGuid(0));
        }
        return new GrIrWriteOffResult(document, number, total, (int)count, Canonical.FormatHex32(hash), clearings);
    }

    public async Task<CommandResult<ReversedGrIrWriteOff>> ReverseWriteOffAsync(Guid tenantId, CommandContext ctx, ReverseGrIrWriteOffRequest req, CancellationToken ct = default)
    {
        static CommandResult<ReversedGrIrWriteOff> Fail(CommandError e) => CommandResult<ReversedGrIrWriteOff>.Rejected([e]);
        if (!ctx.Permissions.Contains(GrIrRules.Permission)) return Fail(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", GrIrRules.Permission)));
        var errors = MasterSupport.ValidateContext(ctx);
        if (req.CompanyId == Guid.Empty) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/company_id", ("reason", "required")));
        if (req.JournalDocumentId == Guid.Empty) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/journal_document_id", ("reason", "required")));
        if (req.ReasonText?.Length > 200) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/reason_text", ("reason", "at most 200 characters")));
        if (errors.Count > 0) return CommandResult<ReversedGrIrWriteOff>.Rejected(errors);
        if (string.IsNullOrWhiteSpace(req.ReasonCode)) return Fail(Error("REVERSAL.REASON_REQUIRED", "P1", "/reason_code"));
        if (!GrIrRules.ReverseReasons.Contains(req.ReasonCode)) return Fail(Error("REVERSAL.REASON_NOT_ALLOWED", "P1", "/reason_code", ("allowed_values", GrIrRules.ReverseReasons)));
        byte[] fingerprint = StockReversalRules.Fingerprint(WriteOffReverseCommandName, req.CompanyId, req.JournalDocumentId, req.ReasonCode, req.PostingDate);

        bool reversed = false;
        await using (TenantTransaction probe = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct))
        await using (var cmd = probe.Command("select document_type, status from core.journal_document where tenant_id = @t and company_id = @c and document_id = @d"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("d", req.JournalDocumentId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct) || r.GetString(0) != "GR_IR_WRITE_OFF") return Fail(Error("ORDER.GRIR_WRITE_OFF_NOT_FOUND", "P2", "/journal_document_id"));
            reversed = r.GetString(1) == "reversed";
        }
        // a replay of the reversal has the same key and answers with the earlier result, whatever the status is now
        if (reversed)
        {
            await using TenantTransaction check = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct);
            if (await CommandSupport.FindEarlierAsync(check, tenantId, ctx.IdempotencyKey, ct) is null) return Fail(Error("ORDER.GRIR_WRITE_OFF_REVERSED", "P2", "/journal_document_id"));
        }

        var clearings = new List<Guid>();
        var reversal = new DocumentReverser.ReverseMode(WriteOffReverseCommandName, "GR_IR_WRITE_OFF", "ORDER",
            async (tx, original, token) =>
            {
                await using var cmd = tx.Command("select clearing_id from core.clearing where tenant_id = @t and company_id = @c and matching_ref = @m and command_name = 'gr-ir:write-off' and clearing_type = 'CLEAR' order by chain_seq");
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("m", req.JournalDocumentId.ToString("D"));
                await using var r = await cmd.ExecuteReaderAsync(token);
                while (await r.ReadAsync(token)) clearings.Add(r.GetGuid(0));
                return null;
            },
            async (tx, mirrorJournalId, token) =>
                await CommandSupport.WriteOutboxAsync(tx, tenantId, req.CompanyId, ctx, "gr_ir.write_off_reversed", "GR_IR_WRITE_OFF", $"{req.CompanyId:D}/{req.JournalDocumentId:D}", "REVERSED", DateTime.UtcNow, isAudit: true, null,
                    JsonSerializer.Serialize(new Dictionary<string, object?> { ["journal_document_id"] = req.JournalDocumentId, ["reversal_document_id"] = mirrorJournalId, ["reason_code"] = req.ReasonCode }), token),
            null,
            // the clearing of the write-off is reset first, so that the residual items are open again
            async (tx, original, date, fiscalYear, periodNo, at, seq, hash, token) =>
            {
                int n = 0;
                foreach (Guid clearing in clearings)
                    (seq, hash) = await ResetClearingAsync(tx, tenantId, req.CompanyId, ctx, clearing, req.JournalDocumentId, WriteOffReverseCommandName, ++n, date, fiscalYear, periodNo, at, seq, hash, token);
                return (seq, hash);
            });
        var reverser = new DocumentReverser(source);
        CommandResult<PostedDocument> result;
        try
        {
            result = await reverser.ReverseOwnedAsync(tenantId, ctx, new ReverseDocumentRequest(req.JournalDocumentId, req.PostingDate, req.ReasonCode, req.ReasonText), fingerprint, reversal, ct);
        }
        catch (MatchRejection ex)
        {
            return Fail(ex.Error);
        }
        if (result.Status != CommandStatus.Ok)
            return CommandResult<ReversedGrIrWriteOff>.Rejected(result.Errors.Select(e => e.Code == "REVERSAL.ALREADY_REVERSED" ? Error("ORDER.GRIR_WRITE_OFF_REVERSED", "P7", "/journal_document_id") : e).ToList());
        PostedDocument doc = result.Value!;
        return CommandResult<ReversedGrIrWriteOff>.Ok(new ReversedGrIrWriteOff(req.JournalDocumentId, doc.DocumentId, doc.DocumentNumber, doc.RowHash), result.CommandLogId ?? Guid.Empty, result.Warnings, result.IdempotentReplay);
    }
}
