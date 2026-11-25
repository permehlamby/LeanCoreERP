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
/// purchase-credit-notes:post (11, 7.3), first slice: the amount credit, a price correction or goodwill without quantity, against the lines of one invoice that is not reversed. The supplier's
/// credit note is matched against the invoice line (a link row of the invoice): the net amount is capped by the net amount of the line less what is credited (ORDER.CREDIT_EXCEEDS_INVOICED). The
/// finance document is an AP_CREDIT with origin ORDER: a service or non-stock line credits PURCHASE_EXPENSE; a stock line credits GR/IR (the line that carries the tax facts), gives it back on a
/// second GR/IR line, and credits INVENTORY for the part on hand and COGS for the part consumed (moving average: on hand is the lesser of the quantity of the invoice line and of the cost key),
/// the two GR/IR lines being cleared against each other; the part on hand is a COST_ADJUST movement of a STOCK_COST_ADJUSTMENT document that uses the journal document as its derived journal
/// (as 13.2ae). The payable is debited as the balance, an open item of the supplier that the caller clears against the invoice. Built choices are in 07, 13.2ah. Not built: the return credit, the
/// reversal, a credit against several invoices, partial deduction, foreign currency.
/// </summary>
public sealed partial class PurchaseInvoiceCommands
{
    private const string CreditCommandName = "purchase-credit-notes:post";

    private sealed record InvoiceRow(
        int LineNo, Guid OrderId, int OrderLineNo, Guid? ReceiptId, int? ReceiptLineNo, decimal BaseQuantity, decimal UnitPrice, decimal Net, decimal? OrderUnitPrice, string? MatchMode,
        decimal? Tolerance, Guid? TaxCodeId);

    private sealed class CreditWork(int index, PurchaseCreditLineInput input, InvoiceRow row, OrderLine line, bool stock, Guid account)
    {
        public int Index { get; } = index;
        public PurchaseCreditLineInput Input { get; } = input;
        public InvoiceRow Row { get; } = row;
        public OrderLine Line { get; } = line;
        public bool Stock { get; } = stock;
        public Guid Account { get; } = account;
        public decimal OnHand { get; set; }
        public decimal Consumed { get; set; }
        public Guid? Warehouse { get; set; }
        public Guid? Location { get; set; }
        public int Main { get; set; }
        public int? Adjust { get; set; }
    }

    public async Task<CommandResult<PurchaseCreditNoteResult>> PostCreditNoteAsync(Guid tenantId, CommandContext ctx, PostPurchaseCreditNoteRequest req, CancellationToken ct = default)
    {
        static CommandResult<PurchaseCreditNoteResult> Fail(CommandError e) => CommandResult<PurchaseCreditNoteResult>.Rejected([e]);
        if (!ctx.Permissions.Contains(PurchaseInvoiceRules.Permission)) return Fail(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", PurchaseInvoiceRules.Permission)));
        var errors = MasterSupport.ValidateContext(ctx);
        void Invalid(string path, string reason) => errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", path, ("reason", reason)));
        if (req.CompanyId == Guid.Empty) Invalid("/company_id", "required");
        if (req.PurchaseInvoiceId == Guid.Empty) Invalid("/purchase_invoice_id", "required");
        if (!PurchaseCreditRules.Kinds.Contains(req.CreditKind)) Invalid("/credit_kind", "RETURN or AMOUNT");
        if (string.IsNullOrWhiteSpace(req.SupplierCreditNoteNumber) || req.SupplierCreditNoteNumber.Length > 100) Invalid("/supplier_credit_note_number", "required, at most 100 characters");
        if (string.IsNullOrWhiteSpace(req.ReasonCode)) errors.Add(Error("SCHEMA.REQUIRED_FIELD_MISSING", "P1", "/reason_code"));
        if (req.Lines is null or { Count: 0 }) errors.Add(Error("SCHEMA.REQUIRED_FIELD_MISSING", "P1", "/lines"));
        for (int i = 0; i < (req.Lines?.Count ?? 0); i++)
        {
            PurchaseCreditLineInput l = req.Lines![i];
            string p = $"/lines/{i}";
            if (string.IsNullOrWhiteSpace(l.LineRef) || l.LineRef.Length > 90) Invalid(p + "/line_ref", "required, at most 90 characters");
            else if (req.Lines.Take(i).Any(x => x.LineRef == l.LineRef)) errors.Add(Error("SCHEMA.DUPLICATE_LINE_REF", "P1", p + "/line_ref", ("line_ref", l.LineRef)));
            if (l.InvoiceLineNo < 1) errors.Add(Error("ORDER.INVOICE_LINE_REFERENCE_MISSING", "P1", p + "/invoice_line_no", ("line_ref", l.LineRef)));
            if (l.CreditAmountNet <= 0m || Math.Round(l.CreditAmountNet, 4) != l.CreditAmountNet) Invalid(p + "/credit_amount_net", "more than 0, at most 4 fraction digits");
            if (l.TaxAmount is null) errors.Add(Error("SCHEMA.REQUIRED_FIELD_MISSING", "P1", p + "/tax_amount"));
        }
        if (errors.Count > 0) return CommandResult<PurchaseCreditNoteResult>.Rejected(errors);
        if (req.CreditKind == "RETURN") return Fail(Error("LIMIT.CREDIT_RETURN_NOT_BUILT", "P1", "/credit_kind"));
        byte[] fingerprint = PurchaseCreditRules.Fingerprint(req);

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
                        ? CommandResult<PurchaseCreditNoteResult>.Failed(Error("SYSTEM.REPLAY_RESULT_MISSING", "P1", ""))
                        : CommandResult<PurchaseCreditNoteResult>.Ok(await LoadCreditAsync(tx, tenantId, req.CompanyId, earlier.EntityId.Value, ct), earlier.CommandLogId, replay: true);
                }
                return await PostCreditCoreAsync(tx, tenantId, ctx, req, fingerprint, ct);
            }
            catch (MatchRejection ex)
            {
                return Fail(ex.Error);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.TableName == "command_log" && attempt == 0)
            {
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.TableName == "purchase_invoice")
            {
                return Fail(Error("ORDER.DUPLICATE_SUPPLIER_INVOICE", "P2", "/supplier_credit_note_number", ("supplier_invoice_number", req.SupplierCreditNoteNumber)));
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                return CommandResult<PurchaseCreditNoteResult>.Failed(new CommandError("SYSTEM.LOCK_TIMEOUT", "P7", "", null, true));
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure)
            {
                return CommandResult<PurchaseCreditNoteResult>.Failed(new CommandError("SYSTEM.CONFLICT", "P7", "", null, true));
            }
        }
    }

    private async Task<CommandResult<PurchaseCreditNoteResult>> PostCreditCoreAsync(
        TenantTransaction tx, Guid tenantId, CommandContext ctx, PostPurchaseCreditNoteRequest req, byte[] fingerprint, CancellationToken ct)
    {
        static CommandResult<PurchaseCreditNoteResult> Fail(CommandError e) => CommandResult<PurchaseCreditNoteResult>.Rejected([e]);
        Guid companyId = req.CompanyId;
        var company = await JournalDocumentPoster.ReadCompanyAsync(tx, tenantId, companyId, ct);
        if (company is null) return Fail(Error("CONFIG.INVALID_REFERENCE", "P2", "/company_id", ("company_id", companyId)));
        if (company.Status != "ACTIVE") return Fail(Error("COMPANY.NOT_ACTIVE", "P2", "/company_id", ("status", company.Status)));
        DateOnly postingDate = req.PostingDate ?? req.CreditDate;
        var errors = new List<CommandError>();

        // the invoice that is credited: an invoice, not reversed
        Guid supplier; string currency; string kind;
        await using (var cmd = tx.Command("select invoice_kind, supplier_partner_id, currency from core.purchase_invoice where tenant_id = @t and company_id = @c and purchase_invoice_id = @i"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", req.PurchaseInvoiceId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Fail(Error("CONFIG.INVALID_REFERENCE", "P2", "/purchase_invoice_id", ("invoice_id", req.PurchaseInvoiceId)));
            kind = r.GetString(0); supplier = r.GetGuid(1); currency = r.GetString(2).Trim();
        }
        if (kind != "INVOICE") return Fail(Error("CONFIG.INVALID_REFERENCE", "P2", "/purchase_invoice_id", ("reason", "a credit note credits an invoice"), ("invoice_kind", kind)));
        await using (var cmd = tx.Command("select exists (select 1 from core.purchase_invoice where tenant_id = @t and company_id = @c and reversal_of_purchase_invoice_id = @i)"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", req.PurchaseInvoiceId);
            if ((bool)(await cmd.ExecuteScalarAsync(ct))!) return Fail(Error("ORDER.INVOICE_REVERSED", "P2", "/purchase_invoice_id", ("invoice_id", req.PurchaseInvoiceId)));
        }
        if (currency != company.LocalCurrency) return Fail(Error("LIMIT.INVOICE_FOREIGN_CURRENCY_NOT_BUILT", "P2", "/purchase_invoice_id", ("currency", currency), ("local_currency", company.LocalCurrency)));

        // the rows of the invoice, then position 1 of the lock order: the order rows, sorted
        var rows = new Dictionary<int, InvoiceRow>();
        await using (var cmd = tx.Command(
            "select line_no, purchase_order_id, purchase_order_line_no, receipt_id, receipt_line_no, base_quantity, unit_price, net_amount, order_unit_price, match_mode_used, tolerance_used, tax_code_id " +
            "from core.purchase_invoice_line where tenant_id = @t and company_id = @c and purchase_invoice_id = @i and line_kind = 'MATCHED'"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", req.PurchaseInvoiceId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                rows[r.GetInt32(0)] = new InvoiceRow(r.GetInt32(0), r.GetGuid(1), r.GetInt32(2), r.IsDBNull(3) ? null : r.GetGuid(3), r.IsDBNull(4) ? null : r.GetInt32(4), r.GetDecimal(5), r.GetDecimal(6), r.GetDecimal(7),
                    r.IsDBNull(8) ? null : r.GetDecimal(8), r.IsDBNull(9) ? null : r.GetString(9).Trim(), r.IsDBNull(10) ? null : r.GetDecimal(10), r.IsDBNull(11) ? null : r.GetGuid(11));
        }
        await using (var cmd = tx.Command("select purchase_order_id from core.purchase_order where tenant_id = @t and company_id = @c and purchase_order_id = any(@ids) order by purchase_order_id for update"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("ids", rows.Values.Select(x => x.OrderId).Distinct().Order().ToArray());
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) { }
        }
        await using (var cmd = tx.Command("select restriction_status from core.business_partner where tenant_id = @t and partner_id = @p"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("p", supplier);
            if (await cmd.ExecuteScalarAsync(ct) as string == "RESTRICTED") return Fail(Error("MASTER.PARTNER_RESTRICTED", "P2", "/purchase_invoice_id", ("partner_id", supplier)));
        }
        await using (var cmd = tx.Command("select exists (select 1 from core.purchase_invoice where tenant_id = @t and company_id = @c and supplier_partner_id = @p and supplier_invoice_number = @n and invoice_kind in ('INVOICE', 'CREDIT_NOTE'))"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("p", supplier); cmd.Parameters.AddWithValue("n", req.SupplierCreditNoteNumber);
            if ((bool)(await cmd.ExecuteScalarAsync(ct))!) return Fail(Error("ORDER.DUPLICATE_SUPPLIER_INVOICE", "P2", "/supplier_credit_note_number", ("supplier_invoice_number", req.SupplierCreditNoteNumber)));
        }
        decimal taxTolerance = 0m;
        await using (var cmd = tx.Command("select tax_amount_tolerance from core.company_setting_version where tenant_id = @t and company_id = @c and effective_from <= now() order by effective_from desc limit 1"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId);
            if (await cmd.ExecuteScalarAsync(ct) is decimal tol) taxTolerance = tol;
        }
        decimal declared = req.Lines.Sum(l => l.CreditAmountNet + l.TaxAmount!.Value);
        if (Math.Abs(declared - req.GrossAmount) > taxTolerance) return Fail(Error("MATCH.GROSS_MISMATCH", "P4", "/gross_amount", ("gross_amount", req.GrossAmount), ("lines_total", declared), ("tolerance", taxTolerance)));
        var apAccount = await AccountDeterminationResolver.ResolveAsync(tx, tenantId, companyId, "AP_CONTROL", null, null, supplier, null, postingDate, ct);
        if (apAccount is null) return Fail(Error("ORDER.ACCOUNT_DETERMINATION_MISSING", "P5", "/lines", ("role_code", "AP_CONTROL"), ("partner_id", supplier)));
        int lcScale = await ScaleOfAsync(tx, currency, ct);

        // the lines: each credits a row of the invoice within its net amount less what is credited
        var work = new List<CreditWork>();
        var running = new Dictionary<int, decimal>();
        var costRead = new Dictionary<Guid, (decimal Qty, decimal Value)>();
        for (int i = 0; i < req.Lines.Count; i++)
        {
            PurchaseCreditLineInput l = req.Lines[i];
            string p = $"/lines/{i}";
            if (!rows.TryGetValue(l.InvoiceLineNo, out InvoiceRow? row)) { errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", p + "/invoice_line_no", ("line_no", l.InvoiceLineNo))); continue; }
            decimal credited = 0m;
            await using (var cmd = tx.Command(
                "select coalesce(sum(l.net_amount), 0) from core.purchase_invoice_line l join core.purchase_invoice h on h.tenant_id = l.tenant_id and h.company_id = l.company_id and h.purchase_invoice_id = l.purchase_invoice_id " +
                "left join core.purchase_invoice o on o.tenant_id = h.tenant_id and o.company_id = h.company_id and o.purchase_invoice_id = h.reversal_of_purchase_invoice_id " +
                "where l.tenant_id = @t and l.company_id = @c and (h.credits_invoice_id = @i or o.credits_invoice_id = @i) and l.credits_invoice_line_no = @n"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", req.PurchaseInvoiceId); cmd.Parameters.AddWithValue("n", l.InvoiceLineNo);
                credited = -(decimal)(await cmd.ExecuteScalarAsync(ct))!;
            }
            decimal cap = row.Net - credited - running.GetValueOrDefault(l.InvoiceLineNo);
            if (l.CreditAmountNet > cap) { errors.Add(Error("ORDER.CREDIT_EXCEEDS_INVOICED", "P4", p + "/credit_amount_net", ("credit_amount_net", l.CreditAmountNet), ("cap", Math.Max(0m, cap)), ("cap_source", "INVOICE_LINE"))); continue; }
            OrderLine? ol = await ReadOrderLineAsync(tx, tenantId, companyId, row.OrderId, row.OrderLineNo, ct);
            if (ol is null) { errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", p, ("reason", "the order line of the invoice line is not found"))); continue; }
            bool stock = ol.ItemType == "GOODS" && row.ReceiptId is not null;
            string role = stock ? "GR_IR" : "PURCHASE_EXPENSE";
            var account = await AccountDeterminationResolver.ResolveAsync(tx, tenantId, companyId, role, ol.ItemGroup, ol.Warehouse, stock ? null : supplier, ol.ItemId, postingDate, ct);
            if (account is null) { errors.Add(Error("ORDER.ACCOUNT_DETERMINATION_MISSING", "P5", p, ("role_code", role), ("item_id", ol.ItemId))); continue; }
            running[l.InvoiceLineNo] = running.GetValueOrDefault(l.InvoiceLineNo) + l.CreditAmountNet;
            var w = new CreditWork(i, l, row, ol, stock, account.AccountId);
            if (stock)
            {
                if (!costRead.TryGetValue(ol.ItemId, out var cs)) costRead[ol.ItemId] = cs = await CostBalanceAsync(tx, tenantId, companyId, ol.ItemId, ct);
                decimal onHandQuantity = Math.Min(row.BaseQuantity, cs.Qty);
                w.OnHand = Math.Round(l.CreditAmountNet * onHandQuantity / row.BaseQuantity, lcScale, MidpointRounding.AwayFromZero);
                w.Consumed = l.CreditAmountNet - w.OnHand;
                if (w.OnHand != 0m)
                {
                    (Guid Warehouse, Guid? Location)? place = await PlaceOfReceiptLineAsync(tx, tenantId, companyId, row.ReceiptId!.Value, row.ReceiptLineNo!.Value, ct);
                    if (place is null) { errors.Add(Error("MATCH.NO_RECEIPT", "P2", p, ("reason", "the movement of the receipt line is not found"))); continue; }
                    (w.Warehouse, w.Location) = place.Value;
                }
            }
            work.Add(w);
        }
        if (errors.Count > 0) return CommandResult<PurchaseCreditNoteResult>.Rejected(errors);

        // the finance document: credit lines (negative) with the tax facts, the difference lines of a stock line, and the payable that the pipeline balances
        var lines = new List<RequestLine>();
        foreach (CreditWork w in work)
        {
            PurchaseCreditLineInput l = w.Input;
            lines.Add(new RequestLine(l.LineRef, w.Account, -l.CreditAmountNet, TaxClassId: w.Line.TaxClassId, TaxDate: req.CreditDate, OriginCountry: w.Line.Origin, DestinationCountry: w.Line.Destination,
                TaxCodeId: l.TaxCodeId ?? w.Row.TaxCodeId, TaxAmountTc: -l.TaxAmount, OpenItem: w.Stock ? new OpenItemRequest(null, req.CreditDate) : null));
            w.Main = lines.Count;
            if (!w.Stock) continue;
            lines.Add(new RequestLine(l.LineRef + "#GA", w.Account, l.CreditAmountNet, OpenItem: new OpenItemRequest(null, req.CreditDate)));
            w.Adjust = lines.Count;
            if (w.OnHand != 0m)
            {
                var inventory = await AccountDeterminationResolver.ResolveAsync(tx, tenantId, companyId, "INVENTORY", w.Line.ItemGroup, w.Line.Warehouse, null, w.Line.ItemId, postingDate, ct);
                if (inventory is null) return Fail(Error("INVENTORY.ACCOUNT_DETERMINATION_MISSING", "P5", $"/lines/{w.Index}", ("role_code", "INVENTORY"), ("item_id", w.Line.ItemId)));
                lines.Add(new RequestLine(l.LineRef + "#IV", inventory.AccountId, -w.OnHand));
            }
            if (w.Consumed != 0m)
            {
                var cogs = await AccountDeterminationResolver.ResolveAsync(tx, tenantId, companyId, "COGS", w.Line.ItemGroup, w.Line.Warehouse, null, w.Line.ItemId, postingDate, ct);
                if (cogs is null) return Fail(Error("INVENTORY.ACCOUNT_DETERMINATION_MISSING", "P5", $"/lines/{w.Index}", ("role_code", "COGS"), ("item_id", w.Line.ItemId)));
                lines.Add(new RequestLine(l.LineRef + "#CG", cogs.AccountId, -w.Consumed));
            }
        }
        const string ApRef = "AP";
        lines.Add(new RequestLine(ApRef, apAccount.AccountId, 0m, OpenItem: new OpenItemRequest(supplier, req.CreditDate, null, req.DueDate ?? req.CreditDate)));
        var journal = new PostJournalDocumentRequest(companyId, null, "AP_CREDIT", req.CreditDate, postingDate, currency, null, null, req.SourceDocumentRef, lines, supplier);

        Guid creditId = Guid.NewGuid(), journalId = Guid.NewGuid();
        Guid? stockId = work.Any(w => w.OnHand != 0m) ? Guid.NewGuid() : null;
        var clearingIds = work.Where(w => w.Stock).Select(w => w.Account).Distinct().ToDictionary(a => a, _ => Guid.NewGuid());
        List<Dictionary<string, object?>> linkRows = [];
        var headerDigest = new Dictionary<string, object?>
        {
            ["purchase_invoice_id"] = creditId, ["invoice_kind"] = "CREDIT_NOTE", ["supplier_partner_id"] = supplier, ["supplier_invoice_number"] = req.SupplierCreditNoteNumber, ["invoice_date"] = req.CreditDate,
            ["due_date"] = req.DueDate, ["currency"] = currency, ["gross_amount"] = -req.GrossAmount, ["stock_document_id"] = stockId, ["credits_invoice_id"] = req.PurchaseInvoiceId, ["credit_kind"] = req.CreditKind,
            ["reversal_of_purchase_invoice_id"] = null, ["reason_code"] = req.ReasonCode, ["line_count"] = work.Count
        };
        byte[] DigestOf(JournalDocumentPoster.EmbeddedFacts facts)
        {
            linkRows = [];
            foreach (CreditWork w in work)
            {
                TaxLineSeed? tax = facts.Tax.Lines.FirstOrDefault(t => t.Bases.Any(b => b.Index == w.Index));
                Guid? code = tax?.CodeId ?? (facts.Tax.BaseCodes.TryGetValue(w.Index, out Guid c) ? c : null);
                linkRows.Add(new Dictionary<string, object?>
                {
                    ["line_no"] = linkRows.Count + 1, ["line_kind"] = "MATCHED", ["purchase_order_id"] = w.Row.OrderId, ["purchase_order_line_no"] = w.Row.OrderLineNo, ["receipt_id"] = null, ["receipt_line_no"] = null,
                    ["stock_document_id"] = null, ["stock_document_line_no"] = null, ["base_quantity"] = 0m, ["unit_price"] = w.Row.UnitPrice, ["net_amount"] = -w.Input.CreditAmountNet,
                    ["tax_amount"] = -w.Input.TaxAmount!.Value, ["order_unit_price"] = w.Row.OrderUnitPrice, ["match_mode_used"] = w.Row.MatchMode, ["tolerance_used"] = w.Row.Tolerance,
                    ["price_difference_lc"] = w.Stock ? -w.Input.CreditAmountNet : 0m, ["on_hand_part_lc"] = -w.OnHand, ["consumed_part_lc"] = -w.Consumed, ["dropship_part_lc"] = 0m, ["unreceived_gr_ir_lc"] = 0m,
                    ["gr_ir_clearing_id"] = w.Stock ? clearingIds[w.Account] : null, ["tax_code_id"] = code, ["tax_code_version_id"] = tax?.VersionId, ["tax_date"] = tax?.TaxDate, ["journal_line_no"] = w.Main,
                    ["credits_invoice_line_no"] = w.Input.InvoiceLineNo, ["return_id"] = null, ["return_line_no"] = null, ["line_ref"] = w.Input.LineRef
                });
            }
            return PurchaseCreditRules.Digest(headerDigest, linkRows.Select(r => (IReadOnlyDictionary<string, object?>)r.Where(kv => kv.Key != "line_ref").ToDictionary(kv => kv.Key, kv => kv.Value)).ToList());
        }

        StockDraft? stockDraft = null;
        async Task<byte[]?> DetailAsync(JournalDocumentPoster.EmbeddedFacts facts)
        {
            byte[] link = DigestOf(facts);
            if (stockId is null) return link;
            var adjustments = work.Where(w => w.OnHand != 0m).Select(w => new AdjItem(w.Line.ItemId, w.Warehouse!.Value, w.Location, -w.OnHand, w.Input.LineRef)).ToList();
            stockDraft = await PrepareStockAsync(tx, tenantId, companyId, ctx, stockId.Value, journalId, req.CreditDate, postingDate, currency, adjustments, costRead, link, ct, CreditCommandName);
            return CombinedDigest(link, stockDraft.RowHash);
        }

        Guid logId = Guid.Empty;
        decimal taxTotal = 0m, gross = 0m;
        async Task AfterAsync(TenantTransaction t, PostedDocument posted, JournalDocumentPoster.EmbeddedFacts facts)
        {
            taxTotal = -facts.Tax.Lines.Sum(x => x.TaxTc);
            gross = facts.Request.Lines.Single(x => x.LineRef == ApRef).AmountTc;
            await using (var cmd = t.Command(
                "insert into core.purchase_invoice (tenant_id, company_id, purchase_invoice_id, invoice_kind, supplier_partner_id, supplier_invoice_number, invoice_date, due_date, currency, gross_amount, journal_document_id, " +
                "stock_document_id, credits_invoice_id, credit_kind, reason_code, source_system, source_document_ref, created_by, correlation_id, idempotency_key) " +
                "values (@t, @c, @id, 'CREDIT_NOTE', @s, @num, @d, @due, @cur, @g, @j, @sd, @credits, @kind, @reason, @src, @ref, @by, @corr, @key)"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("id", creditId); cmd.Parameters.AddWithValue("s", supplier);
                cmd.Parameters.AddWithValue("num", req.SupplierCreditNoteNumber); cmd.Parameters.AddWithValue("d", req.CreditDate); cmd.Parameters.AddWithValue("cur", currency);
                cmd.Parameters.AddWithValue("g", -req.GrossAmount); cmd.Parameters.AddWithValue("j", posted.DocumentId); cmd.Parameters.AddWithValue("credits", req.PurchaseInvoiceId);
                cmd.Parameters.AddWithValue("kind", req.CreditKind); cmd.Parameters.AddWithValue("reason", req.ReasonCode); cmd.Parameters.AddWithValue("src", ctx.SourceSystem);
                cmd.Parameters.AddWithValue("by", ctx.Actor); cmd.Parameters.AddWithValue("corr", ctx.CorrelationId); cmd.Parameters.AddWithValue("key", ctx.IdempotencyKey);
                cmd.Parameters.Add(new NpgsqlParameter("due", NpgsqlDbType.Date) { Value = (object?)req.DueDate ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("sd", NpgsqlDbType.Uuid) { Value = (object?)stockId ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("ref", NpgsqlDbType.Text) { Value = (object?)req.SourceDocumentRef ?? DBNull.Value });
                await cmd.ExecuteNonQueryAsync(ct);
            }
            foreach (var group in work.Where(w => w.Stock).GroupBy(w => w.Account).OrderBy(g => g.Key))
                await ClearCreditPairsAsync(t, tenantId, companyId, ctx, group.ToList(), group.Key, posted, clearingIds[group.Key], creditId, req.CreditDate, clearingIds.Keys.Order().ToList().IndexOf(group.Key) + 1, ct);
            foreach (var row in linkRows) await InsertCreditLineAsync(t, tenantId, companyId, creditId, row, ct);
            if (stockDraft is not null) await InsertStockAsync(t, tenantId, companyId, ctx, stockDraft, req.CreditDate, postingDate, currency, ct);
            logId = await CommandSupport.WriteLogAsync(t, tenantId, companyId, ctx, CreditCommandName, fingerprint, "PURCHASE_INVOICE", creditId, ct);
            await CommandSupport.WriteOutboxAsync(t, tenantId, companyId, ctx, "invoice.posted", "PURCHASE_INVOICE", $"{companyId:D}/{creditId:D}", "POSTED", DateTime.UtcNow, isAudit: true,
                JsonSerializer.Serialize(new Dictionary<string, object?> { ["chain_type"] = "LEDGER", ["row_hash"] = posted.RowHash, ["chain_seq"] = posted.ChainSeq }),
                JsonSerializer.Serialize(new Dictionary<string, object?> { ["document_number"] = posted.DocumentNumber, ["supplier_partner_id"] = supplier, ["lines"] = linkRows.Count, ["journal_document_id"] = posted.DocumentId, ["credit_kind"] = req.CreditKind }), ct);
        }

        var embedding = new JournalDocumentPoster.PostEmbedding(CreditCommandName, "ORDER", ApRef, null, WriteLog: false, Commit: false, AfterAsync, journalId, DetailAsync);
        var postedResult = await _poster.PostCoreAsync(tx, tenantId, ctx, journal, fingerprint, embedding, ct);
        if (postedResult.Status != CommandStatus.Ok) return CommandResult<PurchaseCreditNoteResult>.Rejected(postedResult.Errors.ToList());
        PostedDocument doc = postedResult.Value!;
        var result = new PurchaseCreditNoteResult(creditId, doc.DocumentId, doc.DocumentNumber, req.CreditKind, work.Sum(w => w.Input.CreditAmountNet), taxTotal, gross, doc.RowHash,
            work.Select((w, k) => new PurchaseCreditLineResult(k + 1, w.Input.LineRef, w.Input.InvoiceLineNo, w.Input.CreditAmountNet, w.Input.TaxAmount!.Value, w.Main, w.OnHand, w.Consumed)).ToList(), stockId);
        await tx.CommitAsync(ct);
        return CommandResult<PurchaseCreditNoteResult>.Ok(result, logId, postedResult.Warnings);
    }

    /// <summary>
    /// The two GR/IR lines of a stock credit line (the credit that carries the tax facts and the line that gives it back) are open items without partner on one account; they are cleared against
    /// each other in one clearing of method AUTO_MATCH per account, so no GR/IR item stays open for a price correction. The items net to zero in every currency.
    /// </summary>
    private static async Task ClearCreditPairsAsync(
        TenantTransaction tx, Guid tenantId, Guid companyId, CommandContext ctx, List<CreditWork> works, Guid account, PostedDocument credit, Guid clearingId, Guid creditId, DateOnly date, int number, CancellationToken ct)
    {
        (long headSeq, byte[] headHash, _) = await CommandSupport.LockOrCreateHeadAsync(tx, tenantId, companyId, ClearingHash2.ChainType, "", LedgerHash2.Genesis(tenantId, companyId, ClearingHash2.ChainType, ""), ct);
        DateTime at = DateTime.UtcNow;
        at = new DateTime(at.Ticks - at.Ticks % 10, DateTimeKind.Utc);
        var items = new List<PostedLine>();
        foreach (CreditWork w in works)
        {
            items.Add(credit.Lines.Single(x => x.LineNo == w.Main));
            items.Add(credit.Lines.Single(x => x.LineNo == w.Adjust!.Value));
        }
        if (items.Sum(i => i.AmountTc) != 0m || items.Sum(i => i.AmountLc) != 0m || items.Sum(i => i.AmountGc) != 0m || items.Sum(i => i.AmountRc ?? 0m) != 0m)
            throw new MatchRejection(Error("LIMIT.INVOICE_PRICE_DIFFERENCE_NOT_BUILT", "P4", "/lines", ("reason", "the GR/IR lines of the credit do not net to zero")));
        long seq = headSeq + 1;
        string baseKey = ctx.IdempotencyKey.Length > 90 ? ctx.IdempotencyKey[..90] : ctx.IdempotencyKey;
        CommandContext cctx = ctx with { IdempotencyKey = $"{baseKey}#C{number}" };
        string matchingRef = creditId.ToString("D");
        var body = new Dictionary<string, object?>
        {
            ["clearing_id"] = clearingId, ["clearing_type"] = "CLEAR", ["clearing_date"] = date, ["fiscal_year"] = credit.FiscalYear, ["fiscal_period"] = credit.FiscalPeriod, ["cleared_at"] = at,
            ["account_id"] = account, ["partner_id"] = null, ["document_id"] = null, ["reset_of_clearing_id"] = null, ["clearing_method"] = "AUTO_MATCH", ["matching_ref"] = matchingRef,
            ["source_system"] = ctx.SourceSystem, ["idempotency_key"] = cctx.IdempotencyKey, ["command_name"] = CreditCommandName, ["cleared_by"] = ctx.Actor, ["correlation_id"] = ctx.CorrelationId
        };
        var itemRows = items.Select((i, k) => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["tenant_id"] = tenantId, ["company_id"] = companyId, ["clearing_id"] = clearingId, ["item_no"] = k + 1, ["clearing_date"] = date, ["open_item_document_id"] = credit.DocumentId, ["open_item_line_no"] = i.LineNo,
            ["currency_tc"] = i.CurrencyLc, ["cleared_amount_tc"] = i.AmountTc, ["cleared_amount_lc"] = i.AmountLc, ["cleared_amount_gc"] = i.AmountGc, ["cleared_amount_rc"] = i.AmountRc
        }).ToList();
        byte[] rowHash = ClearingHash2.RowHash(tenantId, companyId, seq, headHash, body, ClearingHash2.ItemsDigest(itemRows), null);
        var synthetic = new PostClearingRequest(companyId, account, null, date, "AUTO_MATCH", matchingRef, null, []);
        await ClearingPoster.InsertClearingAsync(tx, tenantId, synthetic, cctx, clearingId, null, seq, credit.FiscalYear, credit.FiscalPeriod, at, headHash, rowHash, "CLEAR", null, CreditCommandName, ct);
        for (int k = 0; k < items.Count; k++)
        {
            PostedLine i = items[k];
            await ClearingPoster.InsertItemAsync(tx, tenantId, synthetic, clearingId, k + 1, credit.DocumentId, i.LineNo, i.CurrencyLc, i.AmountTc, i.AmountLc, i.AmountGc, i.AmountRc, ct);
            await ClearingPoster.UpdateOpenItemAsync(tx, tenantId, companyId, credit.DocumentId, i.LineNo, clearingId, "CLEARED", i.AmountTc, i.AmountLc, i.AmountGc, i.AmountRc, ct);
        }
        await ClearingPoster.MoveHeadAsync(tx, tenantId, companyId, ClearingHash2.ChainType, "", seq, rowHash, ClearingHash2.Version, ct);
        await CommandSupport.WriteOutboxAsync(tx, tenantId, companyId, ctx, "clearing.posted", "CLEARING", $"{companyId:D}/{clearingId:D}", "POSTED", at, isAudit: true,
            JsonSerializer.Serialize(new Dictionary<string, object?> { ["chain_type"] = "CLEARING", ["scope"] = "", ["chain_seq"] = seq, ["row_hash"] = Canonical.FormatHex32(rowHash) }),
            JsonSerializer.Serialize(new Dictionary<string, object?> { ["clearing_id"] = clearingId, ["partner_id"] = null, ["account_id"] = account, ["items"] = items.Count }), ct);
    }

    /// <summary>Where the stock of a receipt line is: the warehouse and location of its movement.</summary>
    private static async Task<(Guid Warehouse, Guid? Location)?> PlaceOfReceiptLineAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid receiptId, int lineNo, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select m.warehouse_id, m.location_id from core.purchase_receipt_line x join core.purchase_receipt h on h.tenant_id = x.tenant_id and h.company_id = x.company_id and h.receipt_id = x.receipt_id " +
            "join core.stock_movement m on m.tenant_id = h.tenant_id and m.company_id = h.company_id and m.stock_document_id = h.stock_document_id and m.line_no = x.stock_document_line_no " +
            "where x.tenant_id = @t and x.company_id = @c and x.receipt_id = @r and x.line_no = @n");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("r", receiptId); cmd.Parameters.AddWithValue("n", lineNo);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (r.GetGuid(0), r.IsDBNull(1) ? null : r.GetGuid(1)) : null;
    }

    private static async Task InsertCreditLineAsync(TenantTransaction t, Guid tenantId, Guid companyId, Guid creditId, Dictionary<string, object?> row, CancellationToken ct)
    {
        await using var cmd = t.Command(
            "insert into core.purchase_invoice_line (tenant_id, company_id, purchase_invoice_id, line_no, line_kind, purchase_order_id, purchase_order_line_no, base_quantity, unit_price, net_amount, tax_amount, " +
            "order_unit_price, match_mode_used, tolerance_used, price_difference_lc, on_hand_part_lc, consumed_part_lc, gr_ir_clearing_id, tax_code_id, tax_code_version_id, tax_date, journal_line_no, credits_invoice_line_no) " +
            "values (@t, @c, @id, @n, 'MATCHED', @o, @ol, 0, @up, @net, @tax, @oup, @mm, @tol, @pd, @oh, @co, @gc, @tc, @tv, @td, @jl, @cl)");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("id", creditId); cmd.Parameters.AddWithValue("n", row["line_no"]!);
        cmd.Parameters.AddWithValue("o", row["purchase_order_id"]!); cmd.Parameters.AddWithValue("ol", row["purchase_order_line_no"]!); cmd.Parameters.AddWithValue("up", row["unit_price"]!);
        cmd.Parameters.AddWithValue("net", row["net_amount"]!); cmd.Parameters.AddWithValue("tax", row["tax_amount"]!); cmd.Parameters.AddWithValue("pd", row["price_difference_lc"]!);
        cmd.Parameters.AddWithValue("oh", row["on_hand_part_lc"]!); cmd.Parameters.AddWithValue("co", row["consumed_part_lc"]!); cmd.Parameters.AddWithValue("jl", row["journal_line_no"]!);
        cmd.Parameters.AddWithValue("cl", row["credits_invoice_line_no"]!);
        cmd.Parameters.Add(new NpgsqlParameter("oup", NpgsqlDbType.Numeric) { Value = row["order_unit_price"] ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("mm", NpgsqlDbType.Text) { Value = row["match_mode_used"] ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("tol", NpgsqlDbType.Numeric) { Value = row["tolerance_used"] ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("gc", NpgsqlDbType.Uuid) { Value = row["gr_ir_clearing_id"] ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("tc", NpgsqlDbType.Uuid) { Value = row["tax_code_id"] ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("tv", NpgsqlDbType.Uuid) { Value = row["tax_code_version_id"] ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("td", NpgsqlDbType.Date) { Value = row["tax_date"] ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<PurchaseCreditNoteResult> LoadCreditAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid id, CancellationToken ct)
    {
        Guid journal; decimal gross; Guid? stock; string kind;
        await using (var cmd = tx.Command("select journal_document_id, gross_amount, stock_document_id, credit_kind from core.purchase_invoice where tenant_id = @t and company_id = @c and purchase_invoice_id = @i"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            journal = r.GetGuid(0); gross = -r.GetDecimal(1); stock = r.IsDBNull(2) ? null : r.GetGuid(2); kind = r.GetString(3).Trim();
        }
        string number; byte[] hash;
        await using (var cmd = tx.Command("select document_number, row_hash from core.journal_document where tenant_id = @t and company_id = @c and document_id = @d"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", journal);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            number = r.GetString(0); hash = (byte[])r[1];
        }
        var lines = new List<PurchaseCreditLineResult>();
        await using (var cmd = tx.Command("select line_no, credits_invoice_line_no, net_amount, tax_amount, journal_line_no, on_hand_part_lc, consumed_part_lc from core.purchase_invoice_line where tenant_id = @t and company_id = @c and purchase_invoice_id = @i order by line_no"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) lines.Add(new PurchaseCreditLineResult(r.GetInt32(0), "", r.GetInt32(1), -r.GetDecimal(2), -r.GetDecimal(3), r.GetInt32(4), -r.GetDecimal(5), -r.GetDecimal(6)));
        }
        return new PurchaseCreditNoteResult(id, journal, number, kind, lines.Sum(l => l.NetAmount), lines.Sum(l => l.TaxAmount), gross, Canonical.FormatHex32(hash), lines, stock);
    }
}
