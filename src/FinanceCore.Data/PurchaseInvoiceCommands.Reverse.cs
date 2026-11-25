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
/// purchase-invoices:reverse (11, 7.4): the owning command of an order-built AP_INVOICE, its cost adjustment and the clearing of its GR/IR items (6.8). In one transaction of the clearing role:
/// the clearings that the invoice made are reset (the GR/IR items of the receipts and of the invoice are open again), the journal document is reversed by the reversal of the finance pipeline
/// (mirror of type AP_INVOICE, the automatic clearing of its open items, REVERSAL.HAS_CLEARED_LINES if the payable or a GR/IR item is cleared otherwise), the cost adjustment is mirrored by a
/// STOCK_REVERSAL document with COST_ADJUST lines at the original values, and mirror link rows with negative quantities and amounts are inserted. The mirror journal document binds the digest of
/// the mirror link rows and the row hash of the stock mirror, as at posting. A credit note that is not reversed blocks it (ORDER.INVOICE_HAS_CREDIT_NOTES). The order rows are locked first (11, 9).
/// A reversal of an adjustment that would leave the cost key with a value and no quantity, or a negative value, is INVENTORY.RECEIPT_CONSUMED (the stock is sold at the adjusted cost).
/// </summary>
public sealed partial class PurchaseInvoiceCommands
{
    private const string ReverseCommandName = "purchase-invoices:reverse";

    private sealed record PurchaseLink(
        int LineNo, Guid? OrderId, int? OrderLineNo, Guid? ReceiptId, int? ReceiptLineNo, decimal BaseQuantity, decimal UnitPrice, decimal Net, decimal Tax, decimal? OrderUnitPrice, string? MatchMode,
        decimal? Tolerance, decimal Diff, decimal OnHand, decimal Consumed, Guid? TaxCodeId, Guid? TaxCodeVersionId, DateOnly? TaxDate, int JournalLineNo, int? CreditsLineNo, Guid? StockDocId, int? StockLine, string LineKind, string? Description, string? Treatment, Guid? ReturnId, int? ReturnLine);

    /// <summary>What differs between the reversal of an invoice and that of a credit note: the kind of the original, the command, the type of the mirror journal document.</summary>
    private sealed record ReverseKind(string Expected, string Command, string MirrorType, bool Credit);

    private static readonly ReverseKind InvoiceReversal = new("INVOICE", ReverseCommandName, "AP_INVOICE", false);
    private static readonly ReverseKind CreditReversal = new("CREDIT_NOTE", "purchase-credit-notes:reverse", "AP_CREDIT", true);

    public Task<CommandResult<ReversedPurchaseInvoice>> ReverseAsync(Guid tenantId, CommandContext ctx, ReversePurchaseInvoiceRequest req, CancellationToken ct = default) =>
        ReverseInternalAsync(tenantId, ctx, req, InvoiceReversal, ct);

    /// <summary>purchase-credit-notes:reverse (11, 7.3): the owning command of a supplier credit note, its cost adjustment and the clearing of its GR/IR pair; the same reversal as that of an invoice, with the mirror rows restoring the caps.</summary>
    public async Task<CommandResult<ReversedPurchaseCreditNote>> ReverseCreditNoteAsync(Guid tenantId, CommandContext ctx, ReversePurchaseCreditNoteRequest req, CancellationToken ct = default)
    {
        var r = await ReverseInternalAsync(tenantId, ctx, new ReversePurchaseInvoiceRequest(req.CompanyId, req.CreditNoteId, req.ReasonCode, req.PostingDate), CreditReversal, ct);
        return r.Status != CommandStatus.Ok ? CommandResult<ReversedPurchaseCreditNote>.Rejected(r.Errors.Select(e => e.Path == "/purchase_invoice_id" ? e with { Path = "/credit_note_id" } : e).ToList())
            : CommandResult<ReversedPurchaseCreditNote>.Ok(new ReversedPurchaseCreditNote(r.Value!.PurchaseInvoiceId, r.Value.ReversalInvoiceId, r.Value.JournalDocumentId, r.Value.DocumentNumber, r.Value.RowHash, r.Value.StockReversalId), r.CommandLogId ?? Guid.Empty, r.Warnings, r.IdempotentReplay);
    }


    private async Task<CommandResult<ReversedPurchaseInvoice>> ReverseInternalAsync(Guid tenantId, CommandContext ctx, ReversePurchaseInvoiceRequest req, ReverseKind k, CancellationToken ct)
    {
        static CommandResult<ReversedPurchaseInvoice> Fail(CommandError e) => CommandResult<ReversedPurchaseInvoice>.Rejected([e]);
        if (!ctx.Permissions.Contains(PurchaseInvoiceRules.Permission)) return Fail(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", PurchaseInvoiceRules.Permission)));
        var errors = MasterSupport.ValidateContext(ctx);
        if (req.CompanyId == Guid.Empty) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/company_id", ("reason", "required")));
        if (req.PurchaseInvoiceId == Guid.Empty) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/purchase_invoice_id", ("reason", "required")));
        if (errors.Count > 0) return CommandResult<ReversedPurchaseInvoice>.Rejected(errors);
        if (string.IsNullOrWhiteSpace(req.ReasonCode)) return Fail(Error("REVERSAL.REASON_REQUIRED", "P1", "/reason_code"));
        if (!ReversalRules.ExternalReasons.Contains(req.ReasonCode)) return Fail(Error("REVERSAL.REASON_NOT_ALLOWED", "P1", "/reason_code", ("allowed_values", ReversalRules.ExternalReasons)));
        byte[] fingerprint = StockReversalRules.Fingerprint(k.Command, req.CompanyId, req.PurchaseInvoiceId, req.ReasonCode, req.PostingDate);

        // the invoice and its journal document: read first, the role of the reversal depends on the document
        Guid journalId; string kind; Guid? originalStockId; Guid supplier; string currency; decimal grossAmount;
        await using (TenantTransaction probe = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct))
        await using (var cmd = probe.Command("select journal_document_id, invoice_kind, stock_document_id, supplier_partner_id, currency, gross_amount from core.purchase_invoice where tenant_id = @t and company_id = @c and purchase_invoice_id = @i"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("i", req.PurchaseInvoiceId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Fail(Error("REVERSAL.NOT_FOUND", "P2", "/purchase_invoice_id"));
            journalId = r.GetGuid(0); kind = r.GetString(1); originalStockId = r.IsDBNull(2) ? null : r.GetGuid(2); supplier = r.GetGuid(3); currency = r.GetString(4).Trim(); grossAmount = r.GetDecimal(5);
        }
        if (kind != k.Expected) return Fail(Error("REVERSAL.TYPE_NOT_REVERSIBLE", "P2", "/purchase_invoice_id", ("invoice_kind", kind)));

        Guid mirrorInvoiceId = Guid.NewGuid();
        Guid? mirrorStockId = originalStockId is null ? null : Guid.NewGuid();
        List<PurchaseLink> mirrorLines = [];
        List<Guid> clearings = [];
        DateOnly mirrorDate = default;
        StockDraft? stockDraft = null;
        var reversal = new DocumentReverser.ReverseMode(k.Command, k.MirrorType, "ORDER",
            async (tx, original, token) =>
            {
                var lines = new List<PurchaseLink>();
                await using (var cmd = tx.Command(
                    "select line_no, purchase_order_id, purchase_order_line_no, receipt_id, receipt_line_no, base_quantity, unit_price, net_amount, tax_amount, order_unit_price, match_mode_used, tolerance_used, " +
                    "price_difference_lc, on_hand_part_lc, consumed_part_lc, tax_code_id, tax_code_version_id, tax_date, journal_line_no, gr_ir_clearing_id, credits_invoice_line_no, stock_document_id, stock_document_line_no, line_kind, description, charge_treatment, return_id, return_line_no from core.purchase_invoice_line " +
                    "where tenant_id = @t and company_id = @c and purchase_invoice_id = @i order by line_no"))
                {
                    cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("i", req.PurchaseInvoiceId);
                    await using var r = await cmd.ExecuteReaderAsync(token);
                    while (await r.ReadAsync(token))
                    {
                        lines.Add(new PurchaseLink(r.GetInt32(0), r.IsDBNull(1) ? null : r.GetGuid(1), r.IsDBNull(2) ? null : r.GetInt32(2), r.IsDBNull(3) ? null : r.GetGuid(3), r.IsDBNull(4) ? null : r.GetInt32(4),
                            r.GetDecimal(5), r.GetDecimal(6), r.GetDecimal(7), r.GetDecimal(8), r.IsDBNull(9) ? null : r.GetDecimal(9), r.IsDBNull(10) ? null : r.GetString(10).Trim(), r.IsDBNull(11) ? null : r.GetDecimal(11),
                            r.GetDecimal(12), r.GetDecimal(13), r.GetDecimal(14), r.IsDBNull(15) ? null : r.GetGuid(15), r.IsDBNull(16) ? null : r.GetGuid(16),
                            r.IsDBNull(17) ? null : DateOnly.FromDateTime(r.GetDateTime(17)), r.GetInt32(18), r.IsDBNull(20) ? null : r.GetInt32(20), r.IsDBNull(21) ? null : r.GetGuid(21), r.IsDBNull(22) ? null : r.GetInt32(22),
                            r.GetString(23).Trim(), r.IsDBNull(24) ? null : r.GetString(24), r.IsDBNull(25) ? null : r.GetString(25),
                            r.IsDBNull(26) ? null : r.GetGuid(26), r.IsDBNull(27) ? null : r.GetInt32(27)));
                        if (!r.IsDBNull(19) && !clearings.Contains(r.GetGuid(19))) clearings.Add(r.GetGuid(19));
                    }
                }
                // position 1 of the lock order: the order rows, sorted
                await using (var cmd = tx.Command(
                    "select purchase_order_id from core.purchase_order where tenant_id = @t and company_id = @c and purchase_order_id = any(@ids) order by purchase_order_id for update"))
                {
                    cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId);
                    cmd.Parameters.AddWithValue("ids", lines.Where(l => l.OrderId is not null).Select(l => l.OrderId!.Value).Distinct().Order().ToArray());
                    await using var r = await cmd.ExecuteReaderAsync(token);
                    while (await r.ReadAsync(token)) { }
                }
                // a credit note of the invoice that is not reversed is reversed first (7.4, Q25)
                if (!k.Credit)
                await using (var cmd = tx.Command(
                    "select exists (select 1 from core.purchase_invoice c where c.tenant_id = @t and c.company_id = @c and c.credits_invoice_id = @i and c.invoice_kind = 'CREDIT_NOTE' " +
                    "and not exists (select 1 from core.purchase_invoice m where m.tenant_id = c.tenant_id and m.company_id = c.company_id and m.reversal_of_purchase_invoice_id = c.purchase_invoice_id))"))
                {
                    cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("i", req.PurchaseInvoiceId);
                    if ((bool)(await cmd.ExecuteScalarAsync(token))!) return Error("ORDER.INVOICE_HAS_CREDIT_NOTES", "P6", "/purchase_invoice_id");
                }
                mirrorDate = req.PostingDate ?? (DateOnly)original.Header["posting_date"]!;
                mirrorLines = lines.Select(l => l with { BaseQuantity = -l.BaseQuantity, Net = -l.Net, Tax = -l.Tax, Diff = -l.Diff, OnHand = -l.OnHand, Consumed = -l.Consumed }).ToList();
                return null;
            },
            async (tx, mirrorJournalId, token) =>
            {
                await using (var cmd = tx.Command(
                    "insert into core.purchase_invoice (tenant_id, company_id, purchase_invoice_id, invoice_kind, supplier_partner_id, invoice_date, currency, gross_amount, journal_document_id, stock_document_id, " +
                    "reversal_of_purchase_invoice_id, reason_code, source_system, created_by, correlation_id, idempotency_key) values (@t, @c, @id, 'REVERSAL', @s, @d, @cur, @g, @j, @sd, @orig, @reason, @src, @by, @corr, @key)"))
                {
                    cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("id", mirrorInvoiceId); cmd.Parameters.AddWithValue("s", supplier);
                    cmd.Parameters.AddWithValue("d", mirrorDate); cmd.Parameters.AddWithValue("cur", currency); cmd.Parameters.AddWithValue("g", -grossAmount); cmd.Parameters.AddWithValue("j", mirrorJournalId);
                    cmd.Parameters.Add(new NpgsqlParameter("sd", NpgsqlDbType.Uuid) { Value = (object?)mirrorStockId ?? DBNull.Value });
                    cmd.Parameters.AddWithValue("orig", req.PurchaseInvoiceId); cmd.Parameters.AddWithValue("reason", req.ReasonCode); cmd.Parameters.AddWithValue("src", ctx.SourceSystem);
                    cmd.Parameters.AddWithValue("by", ctx.Actor); cmd.Parameters.AddWithValue("corr", ctx.CorrelationId); cmd.Parameters.AddWithValue("key", ctx.IdempotencyKey);
                    await cmd.ExecuteNonQueryAsync(token);
                }
                foreach (PurchaseLink l in mirrorLines)
                {
                    await using var cmd = tx.Command(
                        "insert into core.purchase_invoice_line (tenant_id, company_id, purchase_invoice_id, line_no, line_kind, purchase_order_id, purchase_order_line_no, receipt_id, receipt_line_no, stock_document_id, stock_document_line_no, base_quantity, " +
                        "unit_price, net_amount, tax_amount, order_unit_price, match_mode_used, tolerance_used, price_difference_lc, on_hand_part_lc, consumed_part_lc, tax_code_id, tax_code_version_id, tax_date, journal_line_no, credits_invoice_line_no, description, charge_treatment, return_id, return_line_no) " +
                        "values (@t, @c, @id, @n, @lk, @o, @ol, @r, @rl, @sd, @sl, @bq, @up, @net, @tax, @oup, @mm, @tol, @pd, @oh, @co, @tc, @tv, @td, @jl, @cl, @ds, @ct, @rid, @rln)");
                    cmd.Parameters.Add(new NpgsqlParameter("rid", NpgsqlDbType.Uuid) { Value = (object?)l.ReturnId ?? DBNull.Value });
                    cmd.Parameters.Add(new NpgsqlParameter("rln", NpgsqlDbType.Integer) { Value = (object?)l.ReturnLine ?? DBNull.Value });
                    cmd.Parameters.AddWithValue("lk", l.LineKind);
                    cmd.Parameters.Add(new NpgsqlParameter("ds", NpgsqlDbType.Text) { Value = (object?)l.Description ?? DBNull.Value });
                    cmd.Parameters.Add(new NpgsqlParameter("ct", NpgsqlDbType.Text) { Value = (object?)l.Treatment ?? DBNull.Value });
                    cmd.Parameters.Add(new NpgsqlParameter("cl", NpgsqlDbType.Integer) { Value = (object?)l.CreditsLineNo ?? DBNull.Value });
                    cmd.Parameters.Add(new NpgsqlParameter("sd", NpgsqlDbType.Uuid) { Value = (object?)l.StockDocId ?? DBNull.Value });
                    cmd.Parameters.Add(new NpgsqlParameter("sl", NpgsqlDbType.Integer) { Value = (object?)l.StockLine ?? DBNull.Value });
                    cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("id", mirrorInvoiceId); cmd.Parameters.AddWithValue("n", l.LineNo);
                    cmd.Parameters.AddWithValue("bq", l.BaseQuantity); cmd.Parameters.AddWithValue("up", l.UnitPrice); cmd.Parameters.AddWithValue("net", l.Net); cmd.Parameters.AddWithValue("tax", l.Tax);
                    cmd.Parameters.AddWithValue("pd", l.Diff); cmd.Parameters.AddWithValue("oh", l.OnHand); cmd.Parameters.AddWithValue("co", l.Consumed); cmd.Parameters.AddWithValue("jl", l.JournalLineNo);
                    cmd.Parameters.Add(new NpgsqlParameter("o", NpgsqlDbType.Uuid) { Value = (object?)l.OrderId ?? DBNull.Value });
                    cmd.Parameters.Add(new NpgsqlParameter("ol", NpgsqlDbType.Integer) { Value = (object?)l.OrderLineNo ?? DBNull.Value });
                    cmd.Parameters.Add(new NpgsqlParameter("r", NpgsqlDbType.Uuid) { Value = (object?)l.ReceiptId ?? DBNull.Value });
                    cmd.Parameters.Add(new NpgsqlParameter("rl", NpgsqlDbType.Integer) { Value = (object?)l.ReceiptLineNo ?? DBNull.Value });
                    cmd.Parameters.Add(new NpgsqlParameter("oup", NpgsqlDbType.Numeric) { Value = (object?)l.OrderUnitPrice ?? DBNull.Value });
                    cmd.Parameters.Add(new NpgsqlParameter("mm", NpgsqlDbType.Text) { Value = (object?)l.MatchMode ?? DBNull.Value });
                    cmd.Parameters.Add(new NpgsqlParameter("tol", NpgsqlDbType.Numeric) { Value = (object?)l.Tolerance ?? DBNull.Value });
                    cmd.Parameters.Add(new NpgsqlParameter("tc", NpgsqlDbType.Uuid) { Value = (object?)l.TaxCodeId ?? DBNull.Value });
                    cmd.Parameters.Add(new NpgsqlParameter("tv", NpgsqlDbType.Uuid) { Value = (object?)l.TaxCodeVersionId ?? DBNull.Value });
                    cmd.Parameters.Add(new NpgsqlParameter("td", NpgsqlDbType.Date) { Value = (object?)l.TaxDate ?? DBNull.Value });
                    await cmd.ExecuteNonQueryAsync(token);
                }
                if (stockDraft is not null)
                {
                    await InsertStockAsync(tx, tenantId, req.CompanyId, ctx, stockDraft, mirrorDate, mirrorDate, currency, token);
                    await using var cmd = tx.Command("update core.stock_document set status = 'reversed', reversed_by_stock_document_id = @m where tenant_id = @t and company_id = @c and stock_document_id = @d and status = 'posted'");
                    cmd.Parameters.AddWithValue("m", stockDraft.StockId); cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("d", originalStockId!.Value);
                    await cmd.ExecuteNonQueryAsync(token);
                }
                await CommandSupport.WriteOutboxAsync(tx, tenantId, req.CompanyId, ctx, "invoice.reversed", "PURCHASE_INVOICE", $"{req.CompanyId:D}/{req.PurchaseInvoiceId:D}", "REVERSED", DateTime.UtcNow, isAudit: true, null,
                    JsonSerializer.Serialize(new Dictionary<string, object?> { ["reversal_invoice_id"] = mirrorInvoiceId, ["journal_document_id"] = mirrorJournalId, ["lines"] = mirrorLines.Count }), token);
            },
            null,
            // the clearings that the invoice made are reset first, under the CLEARING head
            async (tx, original, date, fiscalYear, periodNo, at, seq, hash, token) =>
            {
                int n = 0;
                foreach (Guid clearing in clearings)
                    (seq, hash) = await ResetClearingAsync(tx, tenantId, req.CompanyId, ctx, clearing, req.PurchaseInvoiceId, k.Command, ++n, date, fiscalYear, periodNo, at, seq, hash, token);
                return (seq, hash);
            },
            // the mirror journal binds the digest of the mirror link rows and the row hash of the stock mirror, prepared under the LEDGER and CLEARING locks
            async (tx, mirrorJournalId) =>
            {
                var header = new Dictionary<string, object?>
                {
                    ["purchase_invoice_id"] = mirrorInvoiceId, ["invoice_kind"] = "REVERSAL", ["supplier_partner_id"] = supplier, ["supplier_invoice_number"] = null, ["invoice_date"] = mirrorDate, ["due_date"] = null,
                    ["currency"] = currency, ["gross_amount"] = -grossAmount, ["stock_document_id"] = mirrorStockId, ["credits_invoice_id"] = null, ["credit_kind"] = null,
                    ["reversal_of_purchase_invoice_id"] = req.PurchaseInvoiceId, ["reason_code"] = req.ReasonCode, ["line_count"] = mirrorLines.Count
                };
                var rowsOf = mirrorLines.Select(l =>
                {
                    var d = new Dictionary<string, object?>
                {
                    ["line_no"] = l.LineNo, ["line_kind"] = l.LineKind, ["purchase_order_id"] = l.OrderId, ["purchase_order_line_no"] = l.OrderLineNo, ["receipt_id"] = l.ReceiptId, ["receipt_line_no"] = l.ReceiptLineNo,
                    ["stock_document_id"] = l.StockDocId, ["stock_document_line_no"] = l.StockLine, ["base_quantity"] = l.BaseQuantity, ["unit_price"] = l.UnitPrice, ["net_amount"] = l.Net, ["tax_amount"] = l.Tax,
                    ["order_unit_price"] = l.OrderUnitPrice, ["match_mode_used"] = l.MatchMode, ["tolerance_used"] = l.Tolerance, ["price_difference_lc"] = l.Diff, ["on_hand_part_lc"] = l.OnHand,
                    ["consumed_part_lc"] = l.Consumed, ["dropship_part_lc"] = 0m, ["unreceived_gr_ir_lc"] = 0m, ["gr_ir_clearing_id"] = null, ["tax_code_id"] = l.TaxCodeId,
                    ["tax_code_version_id"] = l.TaxCodeVersionId, ["tax_date"] = l.TaxDate, ["journal_line_no"] = l.JournalLineNo
                };
                    if (k.Credit) { d["credits_invoice_line_no"] = l.CreditsLineNo; d["return_id"] = l.ReturnId; d["return_line_no"] = l.ReturnLine; }
                    return (IReadOnlyDictionary<string, object?>)d;
                }).ToList();
                byte[] link = k.Credit ? PurchaseCreditRules.Digest(header, rowsOf) : PurchaseInvoiceRules.Digest(header, rowsOf);
                if (originalStockId is null) return link;
                stockDraft = await PrepareStockMirrorAsync(tx, tenantId, req.CompanyId, ctx, originalStockId.Value, mirrorStockId!.Value, mirrorJournalId, mirrorDate, req.ReasonCode, currency, link, k.Command, default);
                return CombinedDigest(link, stockDraft.RowHash);
            });
        var reverser = new DocumentReverser(source);
        CommandResult<PostedDocument> result;
        try
        {
            result = await reverser.ReverseOwnedAsync(tenantId, ctx, new ReverseDocumentRequest(journalId, req.PostingDate, req.ReasonCode, null), fingerprint, reversal, ct);
        }
        catch (MatchRejection ex)
        {
            return Fail(ex.Error);   // a fact found under the locks; the transaction was not committed
        }
        if (result.Status != CommandStatus.Ok) return CommandResult<ReversedPurchaseInvoice>.Rejected(result.Errors.ToList());
        PostedDocument doc = result.Value!;
        Guid reversalId; Guid? stockReversalId;
        await using (TenantTransaction read = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct))
        await using (var cmd = read.Command("select purchase_invoice_id, stock_document_id from core.purchase_invoice where tenant_id = @t and company_id = @c and journal_document_id = @j"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("j", doc.DocumentId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            reversalId = r.GetGuid(0); stockReversalId = r.IsDBNull(1) ? null : r.GetGuid(1);
        }
        return CommandResult<ReversedPurchaseInvoice>.Ok(new ReversedPurchaseInvoice(req.PurchaseInvoiceId, reversalId, doc.DocumentId, doc.DocumentNumber, doc.RowHash, stockReversalId), result.CommandLogId ?? Guid.Empty, result.Warnings, result.IdempotentReplay);
    }

    /// <summary>
    /// The mirror of the cost adjustment (10, 5.5): a STOCK_REVERSAL document with one COST_ADJUST line per original line at the original value and the opposite sign, whose derived journal is the
    /// mirror journal of the invoice. Under the STOCK head lock; a result that leaves the cost key with a value and no quantity, or a negative value, is INVENTORY.RECEIPT_CONSUMED.
    /// </summary>
    private static async Task<StockDraft> PrepareStockMirrorAsync(
        TenantTransaction tx, Guid tenantId, Guid companyId, CommandContext ctx, Guid originalStockId, Guid stockId, Guid journalId, DateOnly date, string reasonCode, string currency, byte[] linkDigest, string commandName, CancellationToken ct)
    {
        var recheck = await JournalDocumentPoster.CheckPeriodAsync(tx, tenantId, companyId, date, ctx, true, ct);
        if (recheck.Errors.Count > 0) throw new MatchRejection(recheck.Errors[0]);
        Guid ledgerId = await ClearingPoster.ReadDefaultLedgerAsync(tx, tenantId, companyId, ct) ?? Guid.Empty;
        var series = await JournalDocumentPoster.ReadSeriesAsync(tx, tenantId, companyId, "STOCK_DOC_REVERSAL", date, ledgerId, ct)
            ?? throw new MatchRejection(Error("SERIES.NO_ASSIGNMENT", "P2", "/posting_date", ("document_type", "STOCK_DOC_REVERSAL"), ("posting_date", date)));
        int fiscalYear = recheck.FiscalYear!.Value, periodNo = recheck.PeriodNo!.Value;
        long sequenceNo;
        await using (var cmd = tx.Command("update core.number_range_counter set last_number = last_number + 1, updated_at = now() where tenant_id = @t and company_id = @c and series_id = @s and fiscal_year = @y returning last_number"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("s", series.SeriesId); cmd.Parameters.AddWithValue("y", fiscalYear);
            sequenceNo = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }
        string number = Core.Posting.PostingRules.FormatNumber(series.Prefix, series.IncludeFiscalYear, series.Separator, series.NumberLength, fiscalYear, sequenceNo);
        (long headSeq, byte[] prev, _) = await CommandSupport.LockOrCreateHeadAsync(tx, tenantId, companyId, StockHash2.ChainType, "", LedgerHash2.Genesis(tenantId, companyId, StockHash2.ChainType, ""), ct);

        var originals = new List<(int LineNo, Guid Item, Guid Warehouse, Guid? Location, decimal Value, string Unit, string? Ref)>();
        await using (var cmd = tx.Command("select line_no, item_id, warehouse_id, location_id, value_lc, unit_code, source_line_ref from core.stock_movement where tenant_id = @t and company_id = @c and stock_document_id = @s order by line_no"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("s", originalStockId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) originals.Add((r.GetInt32(0), r.GetGuid(1), r.GetGuid(2), r.IsDBNull(3) ? null : r.GetGuid(3), r.GetDecimal(4), r.GetString(5).Trim(), r.IsDBNull(6) ? null : r.GetString(6)));
        }
        DateTime at = DateTime.UtcNow;
        at = new DateTime(at.Ticks - at.Ticks % 10, DateTimeKind.Utc);
        long chainSeq = headSeq + 1;
        var cost = new Dictionary<Guid, (decimal Qty, decimal Value)>();
        var movements = new List<Dictionary<string, object?>>();
        foreach (var o in originals)
        {
            if (!cost.TryGetValue(o.Item, out var cs)) cost[o.Item] = cs = await CostBalanceAsync(tx, tenantId, companyId, o.Item, ct);
            decimal value = -o.Value, costValue = cs.Value + value;
            if (costValue < 0m || (cs.Qty == 0m && costValue != 0m))
                throw new MatchRejection(Error("INVENTORY.RECEIPT_CONSUMED", "P4", "/purchase_invoice_id", ("item_id", o.Item), ("on_hand", cs.Qty), ("value", cs.Value)));
            cost[o.Item] = (cs.Qty, costValue);
            decimal stockQty = await StockBalanceAsync(tx, tenantId, companyId, o.Item, o.Warehouse, o.Location, ct);
            movements.Add(new Dictionary<string, object?>
            {
                ["tenant_id"] = tenantId, ["company_id"] = companyId, ["stock_document_id"] = stockId, ["line_no"] = movements.Count + 1, ["chain_seq"] = chainSeq, ["movement_type"] = "COST_ADJUST",
                ["cost_source"] = "INVOICE", ["valuation_ref_document_id"] = null, ["valuation_ref_line_no"] = null, ["posting_date"] = date, ["item_id"] = o.Item, ["warehouse_id"] = o.Warehouse,
                ["location_id"] = o.Location, ["tracking_id"] = null, ["stock_status"] = "AVAILABLE", ["quantity"] = 0m, ["unit_code"] = o.Unit, ["base_quantity"] = 0m, ["value_lc"] = value,
                ["unit_cost_lc"] = null, ["value_tc"] = value, ["currency_tc"] = currency, ["cost_warehouse_id"] = null, ["cost_qty_after"] = cs.Qty, ["cost_value_after_lc"] = costValue,
                ["stock_qty_after"] = stockQty, ["lot_expired"] = false, ["count_ref"] = null, ["expected_book_quantity"] = null, ["source_line_ref"] = o.Ref, ["reversal_of_line_no"] = o.LineNo
            });
        }
        byte[] movementDigest = StockHash2.MovementDigest(movements.Select(m => (IReadOnlyDictionary<string, object?>)m).ToList());
        var body = new Dictionary<string, object?>
        {
            ["stock_document_id"] = stockId, ["document_type"] = "STOCK_REVERSAL", ["number_series_id"] = series.SeriesId, ["fiscal_year"] = fiscalYear, ["sequence_no"] = sequenceNo,
            ["document_number"] = number, ["fiscal_period"] = periodNo, ["document_date"] = date, ["posting_date"] = date, ["posted_at"] = at, ["reason_code"] = reasonCode,
            ["reversal_of_stock_document_id"] = originalStockId, ["journal_document_id"] = journalId, ["soft_close_override"] = recheck.SoftCloseOverride, ["currency_tc"] = currency,
            ["source_system"] = ctx.SourceSystem, ["source_document_ref"] = null, ["idempotency_key"] = ctx.IdempotencyKey, ["command_name"] = commandName, ["posted_by"] = ctx.Actor,
            ["correlation_id"] = ctx.CorrelationId, ["movement_count"] = movements.Count, ["detail_digest"] = linkDigest, ["movement_digest"] = movementDigest, ["hash_version"] = StockHash2.Version
        };
        byte[] rowHash = StockHash2.RowHash(tenantId, companyId, chainSeq, prev, body);
        return new StockDraft(stockId, journalId, chainSeq, prev, rowHash, sequenceNo, number, fiscalYear, periodNo, recheck.SoftCloseOverride, series.SeriesId, at, linkDigest, movementDigest, movements,
            "STOCK_REVERSAL", reasonCode, originalStockId, commandName);
    }

    /// <summary>
    /// The reset of one clearing that the invoice made (03, 5.4): a new clearing of type RESET with the mirrored items, opposite in sign. The open items get their cleared amounts and status back.
    /// </summary>
    internal static async Task<(long Seq, byte[] Hash)> ResetClearingAsync(
        TenantTransaction tx, Guid tenantId, Guid companyId, CommandContext ctx, Guid clearingId, Guid invoiceId, string command, int number, DateOnly date, int fiscalYear, int periodNo, DateTime at, long seq0, byte[] hash0, CancellationToken ct)
    {
        Guid account;
        await using (var cmd = tx.Command("select account_id from core.clearing where tenant_id = @t and company_id = @c and clearing_id = @i"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", clearingId);
            account = (Guid)(await cmd.ExecuteScalarAsync(ct))!;
        }
        var items = new List<(Guid Doc, int Line, string Currency, decimal Tc, decimal Lc, decimal Gc, decimal? Rc)>();
        await using (var cmd = tx.Command("select open_item_document_id, open_item_line_no, currency_tc, cleared_amount_tc, cleared_amount_lc, cleared_amount_gc, cleared_amount_rc from core.clearing_item where tenant_id = @t and company_id = @c and clearing_id = @i order by item_no"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", clearingId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) items.Add((r.GetGuid(0), r.GetInt32(1), r.GetString(2).Trim(), r.GetDecimal(3), r.GetDecimal(4), r.GetDecimal(5), r.IsDBNull(6) ? null : r.GetDecimal(6)));
        }
        // the open items, locked in key order and read under the lock
        var rows = new Dictionary<(Guid, int), (decimal LineTc, decimal LineLc, decimal LineGc, decimal? LineRc, decimal ClearedTc, decimal ClearedLc, decimal ClearedGc, decimal? ClearedRc)>();
        foreach (var key in items.Select(i => (i.Doc, i.Line)).Distinct().OrderBy(k => k.Doc).ThenBy(k => k.Line))
        {
            await using var cmd = tx.Command(
                "select l.amount_tc, l.amount_lc, l.amount_gc, l.amount_rc, oi.cleared_amount_tc, oi.cleared_amount_lc, oi.cleared_amount_gc, oi.cleared_amount_rc from core.journal_line_open_item oi " +
                "join core.journal_line l on l.tenant_id = oi.tenant_id and l.company_id = oi.company_id and l.document_id = oi.document_id and l.line_no = oi.line_no " +
                "where oi.tenant_id = @t and oi.company_id = @c and oi.document_id = @d and oi.line_no = @n for update of oi");
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", key.Doc); cmd.Parameters.AddWithValue("n", key.Line);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            rows[key] = (r.GetDecimal(0), r.GetDecimal(1), r.GetDecimal(2), r.IsDBNull(3) ? null : r.GetDecimal(3), r.GetDecimal(4), r.GetDecimal(5), r.GetDecimal(6), r.IsDBNull(7) ? null : r.GetDecimal(7));
        }

        Guid resetId = Guid.NewGuid();
        long seq = seq0 + 1;
        string baseKey = ctx.IdempotencyKey.Length > 90 ? ctx.IdempotencyKey[..90] : ctx.IdempotencyKey;
        CommandContext rctx = ctx with { IdempotencyKey = $"{baseKey}#R{number}" };
        string matchingRef = invoiceId.ToString("D");
        var body = new Dictionary<string, object?>
        {
            ["clearing_id"] = resetId, ["clearing_type"] = "RESET", ["clearing_date"] = date, ["fiscal_year"] = fiscalYear, ["fiscal_period"] = periodNo, ["cleared_at"] = at, ["account_id"] = account,
            ["partner_id"] = null, ["document_id"] = null, ["reset_of_clearing_id"] = clearingId, ["clearing_method"] = "AUTO_MATCH", ["matching_ref"] = matchingRef, ["source_system"] = ctx.SourceSystem,
            ["idempotency_key"] = rctx.IdempotencyKey, ["command_name"] = command, ["cleared_by"] = ctx.Actor, ["correlation_id"] = ctx.CorrelationId
        };
        var itemRows = items.Select((i, n) => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["tenant_id"] = tenantId, ["company_id"] = companyId, ["clearing_id"] = resetId, ["item_no"] = n + 1, ["clearing_date"] = date, ["open_item_document_id"] = i.Doc, ["open_item_line_no"] = i.Line,
            ["currency_tc"] = i.Currency, ["cleared_amount_tc"] = -i.Tc, ["cleared_amount_lc"] = -i.Lc, ["cleared_amount_gc"] = -i.Gc, ["cleared_amount_rc"] = i.Rc is null ? null : -i.Rc
        }).ToList();
        byte[] rowHash = ClearingHash2.RowHash(tenantId, companyId, seq, hash0, body, ClearingHash2.ItemsDigest(itemRows), null);
        var synthetic = new PostClearingRequest(companyId, account, null, date, "AUTO_MATCH", matchingRef, null, []);
        await ClearingPoster.InsertClearingAsync(tx, tenantId, synthetic, rctx, resetId, null, seq, fiscalYear, periodNo, at, hash0, rowHash, "RESET", clearingId, command, ct);
        for (int n = 0; n < items.Count; n++)
        {
            var i = items[n];
            decimal tc = -i.Tc, lc = -i.Lc, gc = -i.Gc; decimal? rc = i.Rc is null ? null : -i.Rc;
            await ClearingPoster.InsertItemAsync(tx, tenantId, synthetic, resetId, n + 1, i.Doc, i.Line, i.Currency, tc, lc, gc, rc, ct);
            var row = rows[(i.Doc, i.Line)];
            decimal newTc = row.ClearedTc + tc, newLc = row.ClearedLc + lc, newGc = row.ClearedGc + gc;
            decimal? newRc = row.LineRc is null ? null : (row.ClearedRc ?? 0m) + (rc ?? 0m);
            string status = ClearingRules.StatusOf(new OpenLine(row.LineTc, row.LineLc, row.LineGc, row.LineRc, 0m, 0m, 0m, null), newTc, newLc, newGc, newRc);
            rows[(i.Doc, i.Line)] = (row.LineTc, row.LineLc, row.LineGc, row.LineRc, newTc, newLc, newGc, newRc);
            await ClearingPoster.UpdateOpenItemAsync(tx, tenantId, companyId, i.Doc, i.Line, resetId, status, newTc, newLc, newGc, newRc, ct);
        }
        await ClearingPoster.MoveHeadAsync(tx, tenantId, companyId, ClearingHash2.ChainType, "", seq, rowHash, ClearingHash2.Version, ct);
        await CommandSupport.WriteOutboxAsync(tx, tenantId, companyId, ctx, "clearing.reset", "CLEARING", $"{companyId:D}/{resetId:D}", "RESET", at, isAudit: true,
            JsonSerializer.Serialize(new Dictionary<string, object?> { ["chain_type"] = "CLEARING", ["scope"] = "", ["chain_seq"] = seq, ["row_hash"] = Canonical.FormatHex32(rowHash) }),
            JsonSerializer.Serialize(new Dictionary<string, object?> { ["clearing_id"] = resetId, ["reset_of_clearing_id"] = clearingId, ["partner_id"] = null, ["account_id"] = account, ["items"] = items.Count }), ct);
        return (seq, rowHash);
    }
}
