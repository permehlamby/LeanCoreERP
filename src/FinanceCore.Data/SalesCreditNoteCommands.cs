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
/// sales-credit-notes:create and :reverse (11, 6.5), first slice. A credit note credits the lines of one invoice that is not reversed. A return credit names return lines and credits the returned
/// quantity at the unit price, the discount and the tax code of the invoice line; an amount credit names invoice lines and a net amount, with the tax of the invoice line applied to it. The
/// finance document is an AR_CREDIT of the pipeline of 06 with origin ORDER (the revenue of the original line, debited, the tax the pipeline derives, the receivable as the balance, an open
/// item that the caller may clear against the invoice); the link rows are those of 6.9, signed negative. The caps of 6.5 (ORDER.CREDIT_EXCEEDS_INVOICED): a return credit by the returned
/// quantity of the return line and by the invoiced quantity of the invoice line, less what is credited; an amount credit by the net amount of the invoice line, less what is credited. The
/// reversal mirrors the journal and the rows and restores the caps. Built choices are in 07, 13.2ac. Not built: purchase credit notes, the reads, the credit against several invoices.
/// </summary>
public sealed class SalesCreditNoteCommands(NpgsqlDataSource source)
{
    private readonly JournalDocumentPoster _poster = new(source);
    private const string CreateName = "sales-credit-notes:create", ReverseName = "sales-credit-notes:reverse";

    private static CommandError Error(string code, string phase, string path, params (string Key, object? Value)[] parameters) =>
        JournalDocumentPoster.Error(code, phase, path, parameters);

    private static CommandResult<SalesCreditNoteResult> Reject(CommandError e) => CommandResult<SalesCreditNoteResult>.Rejected([e]);

    private sealed record InvoiceLine(
        int LineNo, Guid OrderId, int OrderLineNo, Guid? DeliveryId, int? DeliveryLineNo, decimal Quantity, string UnitCode, decimal BaseQuantity, decimal UnitPrice, decimal DiscountPct, decimal Net,
        Guid? TaxCodeId, int JournalLineNo, Guid RevenueAccount);

    private sealed record Work(
        int Index, CreditLineInput Input, InvoiceLine Line, decimal Quantity, decimal BaseQuantity, decimal Net, Guid? TaxClassId, string Origin, string Destination);

    private sealed record Row(
        int LineNo, Guid OrderId, int OrderLineNo, Guid? DeliveryId, int? DeliveryLineNo, decimal Quantity, string UnitCode, decimal BaseQuantity, decimal UnitPrice, decimal DiscountPct, decimal Net,
        Guid? TaxCodeId, Guid? TaxVersionId, DateOnly? TaxDate, int JournalLineNo, int? CreditsLineNo, Guid? ReturnId, int? ReturnLineNo);

    private static Dictionary<string, object?> RowOf(Row r) => new()
    {
        ["line_no"] = r.LineNo, ["sales_order_id"] = r.OrderId, ["sales_order_line_no"] = r.OrderLineNo, ["delivery_id"] = r.DeliveryId, ["delivery_line_no"] = r.DeliveryLineNo, ["dropship_confirmation_id"] = null,
        ["quantity"] = r.Quantity, ["unit_code"] = r.UnitCode, ["base_quantity"] = r.BaseQuantity, ["unit_price"] = r.UnitPrice, ["discount_pct"] = r.DiscountPct, ["net_amount"] = r.Net,
        ["order_unit_price"] = null, ["order_discount_pct"] = null, ["tax_code_id"] = r.TaxCodeId, ["tax_code_version_id"] = r.TaxVersionId, ["tax_date"] = r.TaxDate, ["journal_line_no"] = r.JournalLineNo,
        ["credits_invoice_line_no"] = r.CreditsLineNo, ["return_id"] = r.ReturnId, ["return_line_no"] = r.ReturnLineNo
    };

    private static async Task InsertRowAsync(TenantTransaction t, Guid tenantId, Guid companyId, Guid id, Row r, CancellationToken ct)
    {
        await using var cmd = t.Command(
            "insert into core.sales_invoice_line (tenant_id, company_id, sales_invoice_id, line_no, sales_order_id, sales_order_line_no, delivery_id, delivery_line_no, quantity, unit_code, base_quantity, unit_price, " +
            "discount_pct, net_amount, tax_code_id, tax_code_version_id, tax_date, journal_line_no, credits_invoice_line_no, return_id, return_line_no) values (@t, @c, @id, @n, @o, @ol, @d, @dl, @q, @u, @bq, @up, @dp, @net, " +
            "@tc, @tv, @td, @jl, @cl, @rid, @rln)");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("id", id); cmd.Parameters.AddWithValue("n", r.LineNo);
        cmd.Parameters.AddWithValue("o", r.OrderId); cmd.Parameters.AddWithValue("ol", r.OrderLineNo); cmd.Parameters.AddWithValue("q", r.Quantity); cmd.Parameters.AddWithValue("u", r.UnitCode);
        cmd.Parameters.AddWithValue("bq", r.BaseQuantity); cmd.Parameters.AddWithValue("up", r.UnitPrice); cmd.Parameters.AddWithValue("dp", r.DiscountPct); cmd.Parameters.AddWithValue("net", r.Net);
        cmd.Parameters.AddWithValue("jl", r.JournalLineNo);
        cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Uuid) { Value = (object?)r.DeliveryId ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("dl", NpgsqlDbType.Integer) { Value = (object?)r.DeliveryLineNo ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("tc", NpgsqlDbType.Uuid) { Value = (object?)r.TaxCodeId ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("tv", NpgsqlDbType.Uuid) { Value = (object?)r.TaxVersionId ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("td", NpgsqlDbType.Date) { Value = (object?)r.TaxDate ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("cl", NpgsqlDbType.Integer) { Value = (object?)r.CreditsLineNo ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("rid", NpgsqlDbType.Uuid) { Value = (object?)r.ReturnId ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("rln", NpgsqlDbType.Integer) { Value = (object?)r.ReturnLineNo ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // the credit rows of an invoice line and of a return line: the rows of the credit notes and of their reversals, negative as stored
    private const string CreditRows =
        "from core.sales_invoice_line l join core.sales_invoice h on h.tenant_id = l.tenant_id and h.company_id = l.company_id and h.sales_invoice_id = l.sales_invoice_id " +
        "left join core.sales_invoice o on o.tenant_id = h.tenant_id and o.company_id = h.company_id and o.sales_invoice_id = h.reversal_of_sales_invoice_id " +
        "where l.tenant_id = @t and l.company_id = @c and (h.invoice_kind = 'CREDIT_NOTE' or (h.invoice_kind = 'REVERSAL' and o.invoice_kind = 'CREDIT_NOTE'))";

    private static async Task<(decimal Quantity, decimal Net)> CreditedAsync(TenantTransaction tx, Guid tenantId, Guid companyId, string filter, Action<NpgsqlCommand> bind, CancellationToken ct)
    {
        await using var cmd = tx.Command($"select coalesce(-sum(l.base_quantity), 0), coalesce(-sum(l.net_amount), 0) {CreditRows} and {filter}");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId);
        bind(cmd);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return (r.GetDecimal(0), r.GetDecimal(1));
    }

    public async Task<CommandResult<SalesCreditNoteResult>> CreateAsync(Guid tenantId, CommandContext ctx, CreateSalesCreditNoteRequest req, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(InvoiceRules.SalesPermission)) return Reject(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", InvoiceRules.SalesPermission)));
        var errors = MasterSupport.ValidateContext(ctx);
        void Invalid(string path, string reason) => errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", path, ("reason", reason)));
        if (req.CompanyId == Guid.Empty) Invalid("/company_id", "required");
        if (req.SalesInvoiceId == Guid.Empty) Invalid("/sales_invoice_id", "required");
        if (!CreditRules.Kinds.Contains(req.CreditKind)) Invalid("/credit_kind", "RETURN or AMOUNT");
        if (req.CreditKind == "AMOUNT" && string.IsNullOrWhiteSpace(req.ReasonCode)) errors.Add(Error("SCHEMA.REQUIRED_FIELD_MISSING", "P1", "/reason_code"));
        if (req.ReasonCode?.Length > 50) Invalid("/reason_code", "at most 50 characters");
        if (req.Lines is null or { Count: 0 }) errors.Add(Error("SCHEMA.REQUIRED_FIELD_MISSING", "P1", "/lines"));
        for (int i = 0; i < (req.Lines?.Count ?? 0); i++)
        {
            CreditLineInput l = req.Lines![i];
            string p = $"/lines/{i}";
            if (string.IsNullOrWhiteSpace(l.LineRef) || l.LineRef.Length > 100) Invalid(p + "/line_ref", "required, at most 100 characters");
            else if (req.Lines.Take(i).Any(x => x.LineRef == l.LineRef)) errors.Add(Error("SCHEMA.DUPLICATE_LINE_REF", "P1", p + "/line_ref", ("line_ref", l.LineRef)));
            if (l.InvoiceLineNo < 1) Invalid(p + "/invoice_line_no", "required");
            if (req.CreditKind == "RETURN")
            {
                if (l.ReturnId is null || l.ReturnLineNo is null or < 1) Invalid(p + "/return_id", "a return and a line number");
                if (l.CreditAmountNet is not null) Invalid(p + "/credit_amount_net", "an amount belongs to an amount credit");
                if (l.Quantity is { } q && (q <= 0m || Math.Round(q, 6) != q)) Invalid(p + "/quantity", "more than 0, at most 6 fraction digits");
            }
            else if (req.CreditKind == "AMOUNT")
            {
                if (l.CreditAmountNet is not { } a || a <= 0m || Math.Round(a, 4) != a) Invalid(p + "/credit_amount_net", "more than 0, at most 4 fraction digits");
                if (l.ReturnId is not null || l.Quantity is not null) Invalid(p + "/quantity", "an amount credit has no quantity and no return");
            }
        }
        if (req.SourceDocumentRef?.Length > 100) Invalid("/source_document_ref", "at most 100 characters");
        if (errors.Count > 0) return CommandResult<SalesCreditNoteResult>.Rejected(errors);
        byte[] fingerprint = CreditRules.Fingerprint(req);

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Post, ct: ct);
                CommandSupport.EarlierCommand? earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
                if (earlier is not null)
                {
                    if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint)) return Reject(Error("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
                    return earlier.EntityId is null
                        ? CommandResult<SalesCreditNoteResult>.Failed(Error("SYSTEM.REPLAY_RESULT_MISSING", "P1", ""))
                        : CommandResult<SalesCreditNoteResult>.Ok(await LoadAsync(tx, tenantId, req.CompanyId, earlier.EntityId.Value, ct), earlier.CommandLogId, replay: true);
                }
                return await CreateCoreAsync(tx, tenantId, ctx, req, fingerprint, ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.TableName == "command_log" && attempt == 0)
            {
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                return CommandResult<SalesCreditNoteResult>.Failed(new CommandError("SYSTEM.LOCK_TIMEOUT", "P7", "", null, true));
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure)
            {
                return CommandResult<SalesCreditNoteResult>.Failed(new CommandError("SYSTEM.CONFLICT", "P7", "", null, true));
            }
        }
    }

    private async Task<CommandResult<SalesCreditNoteResult>> CreateCoreAsync(
        TenantTransaction tx, Guid tenantId, CommandContext ctx, CreateSalesCreditNoteRequest req, byte[] fingerprint, CancellationToken ct)
    {
        Guid companyId = req.CompanyId;
        var company = await JournalDocumentPoster.ReadCompanyAsync(tx, tenantId, companyId, ct);
        if (company is null) return Reject(Error("CONFIG.INVALID_REFERENCE", "P2", "/company_id", ("company_id", companyId)));
        if (company.Status != "ACTIVE") return Reject(Error("COMPANY.NOT_ACTIVE", "P2", "/company_id", ("status", company.Status)));
        DateOnly postingDate = req.PostingDate ?? req.CreditDate;
        var errors = new List<CommandError>();

        // the invoice that is credited: an invoice record that is not reversed
        Guid journalId, customer; Guid? terms; string currency, kind;
        await using (var cmd = tx.Command(
            "select journal_document_id, invoice_kind, customer_partner_id, payment_terms_id, order_currency from core.sales_invoice where tenant_id = @t and company_id = @c and sales_invoice_id = @i"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", req.SalesInvoiceId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Reject(Error("CONFIG.INVALID_REFERENCE", "P2", "/sales_invoice_id", ("sales_invoice_id", req.SalesInvoiceId)));
            journalId = r.GetGuid(0); kind = r.GetString(1); customer = r.GetGuid(2); terms = r.IsDBNull(3) ? null : r.GetGuid(3); currency = r.GetString(4).Trim();
        }
        if (kind != "INVOICE") return Reject(Error("CONFIG.INVALID_REFERENCE", "P2", "/sales_invoice_id", ("reason", "an invoice record, not a credit note or a reversal")));

        // position 1 of the lock order: the order rows of the invoice lines, sorted
        await using (var cmd = tx.Command(
            "select sales_order_id from core.sales_order where tenant_id = @t and company_id = @c and sales_order_id in (select sales_order_id from core.sales_invoice_line where tenant_id = @t and company_id = @c and sales_invoice_id = @i) " +
            "order by sales_order_id for update"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", req.SalesInvoiceId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) { }
        }
        await using (var cmd = tx.Command("select exists (select 1 from core.sales_invoice where tenant_id = @t and company_id = @c and reversal_of_sales_invoice_id = @i)"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", req.SalesInvoiceId);
            if ((bool)(await cmd.ExecuteScalarAsync(ct))!) return Reject(Error("ORDER.INVOICE_REVERSED", "P2", "/sales_invoice_id"));
        }
        await using (var cmd = tx.Command("select restriction_status from core.business_partner where tenant_id = @t and partner_id = @p"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("p", customer);
            if (await cmd.ExecuteScalarAsync(ct) as string == "RESTRICTED") return Reject(Error("MASTER.PARTNER_RESTRICTED", "P2", "/sales_invoice_id", ("partner_id", customer)));
        }
        int scale;
        await using (var cmd = tx.Command("select minor_units from platform.ref_currency where currency_code = @c"))
        {
            cmd.Parameters.AddWithValue("c", currency);
            scale = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct) ?? 2, CultureInfo.InvariantCulture);
        }
        var arAccount = await AccountDeterminationResolver.ResolveAsync(tx, tenantId, companyId, "AR_CONTROL", null, null, customer, null, postingDate, ct);
        if (arAccount is null) return Reject(Error("ORDER.ACCOUNT_DETERMINATION_MISSING", "P5", "/lines", ("role_code", "AR_CONTROL"), ("partner_id", customer)));

        var work = new List<Work>();
        var runningQty = new Dictionary<int, decimal>(); var runningNet = new Dictionary<int, decimal>(); var runningReturn = new Dictionary<(Guid, int), decimal>();
        for (int i = 0; i < req.Lines.Count; i++)
        {
            CreditLineInput l = req.Lines[i];
            string p = $"/lines/{i}";
            InvoiceLine? il = null;
            await using (var cmd = tx.Command(
                "select l.sales_order_id, l.sales_order_line_no, l.delivery_id, l.delivery_line_no, l.quantity, l.unit_code, l.base_quantity, l.unit_price, l.discount_pct, l.net_amount, l.tax_code_id, l.journal_line_no, " +
                "(select account_id from core.journal_line jl where jl.tenant_id = l.tenant_id and jl.company_id = l.company_id and jl.document_id = @j and jl.line_no = l.journal_line_no) " +
                "from core.sales_invoice_line l where l.tenant_id = @t and l.company_id = @c and l.sales_invoice_id = @i and l.line_no = @n"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", req.SalesInvoiceId); cmd.Parameters.AddWithValue("n", l.InvoiceLineNo); cmd.Parameters.AddWithValue("j", journalId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                    il = new InvoiceLine(l.InvoiceLineNo, r.GetGuid(0), r.GetInt32(1), r.IsDBNull(2) ? null : r.GetGuid(2), r.IsDBNull(3) ? null : r.GetInt32(3), r.GetDecimal(4), r.GetString(5).Trim(), r.GetDecimal(6),
                        r.GetDecimal(7), r.GetDecimal(8), r.GetDecimal(9), r.IsDBNull(10) ? null : r.GetGuid(10), r.GetInt32(11), r.GetGuid(12));
            }
            if (il is null) { errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", p + "/invoice_line_no", ("line_no", l.InvoiceLineNo))); continue; }
            Guid? taxClass; string origin, destination; decimal priceUnit, ratio;
            await using (var cmd = tx.Command("select tax_class_id, origin_country, destination_country, price_unit_quantity, base_quantity / quantity from core.sales_order_line where tenant_id = @t and company_id = @c and sales_order_id = @o and line_no = @n"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("o", il.OrderId); cmd.Parameters.AddWithValue("n", il.OrderLineNo);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                await r.ReadAsync(ct);
                taxClass = r.IsDBNull(0) ? null : r.GetGuid(0); origin = r.GetString(1).Trim(); destination = r.GetString(2).Trim(); priceUnit = r.GetDecimal(3); ratio = r.GetDecimal(4);
            }
            (decimal creditedQty, decimal creditedNet) = await CreditedAsync(tx, tenantId, companyId, "l.credits_invoice_line_no = @n and coalesce(h.credits_invoice_id, o.credits_invoice_id) = @i",
                c => { c.Parameters.AddWithValue("i", req.SalesInvoiceId); c.Parameters.AddWithValue("n", l.InvoiceLineNo); }, ct);
            creditedQty += runningQty.GetValueOrDefault(l.InvoiceLineNo); creditedNet += runningNet.GetValueOrDefault(l.InvoiceLineNo);

            decimal quantity = 0m, baseQty = 0m, net;
            if (req.CreditKind == "RETURN")
            {
                // the return line: posted, not a mirror, of the same order line and customer
                Guid returnId = l.ReturnId!.Value; int returnLine = l.ReturnLineNo!.Value;
                decimal returnedBase; Guid orderId; int orderLine; Guid returnCustomer; string returnStatus; bool mirror;
                await using (var cmd = tx.Command(
                    "select rl.base_quantity, rl.sales_order_id, rl.sales_order_line_no, h.customer_partner_id, h.status, h.reversal_of_return_id is not null from core.sales_return_line rl join core.sales_return h " +
                    "on h.tenant_id = rl.tenant_id and h.company_id = rl.company_id and h.return_id = rl.return_id where rl.tenant_id = @t and rl.company_id = @c and rl.return_id = @r and rl.line_no = @n"))
                {
                    cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("r", returnId); cmd.Parameters.AddWithValue("n", returnLine);
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    if (!await r.ReadAsync(ct)) { errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", p + "/return_id", ("return_id", returnId))); continue; }
                    returnedBase = r.GetDecimal(0); orderId = r.GetGuid(1); orderLine = r.GetInt32(2); returnCustomer = r.GetGuid(3); returnStatus = r.GetString(4); mirror = r.GetBoolean(5);
                }
                if (returnStatus != "posted" || mirror || orderId != il.OrderId || orderLine != il.OrderLineNo || returnCustomer != customer)
                { errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", p + "/return_id", ("reason", "a posted return line of the order line and the customer of the invoice line"))); continue; }
                (decimal creditedByReturn, _) = await CreditedAsync(tx, tenantId, companyId, "l.return_id = @r and l.return_line_no = @n", c => { c.Parameters.AddWithValue("r", returnId); c.Parameters.AddWithValue("n", returnLine); }, ct);
                creditedByReturn += runningReturn.GetValueOrDefault((returnId, returnLine));
                decimal returnCap = Math.Max(0m, returnedBase - creditedByReturn), invoiceCap = Math.Max(0m, il.BaseQuantity - creditedQty);
                baseQty = l.Quantity is { } q ? q * (il.BaseQuantity / il.Quantity) : Math.Min(returnCap, invoiceCap);
                quantity = baseQty / (il.BaseQuantity / il.Quantity);
                if (baseQty <= 0m || baseQty > returnCap) { errors.Add(Error("ORDER.CREDIT_EXCEEDS_INVOICED", "P4", p + "/quantity", ("line_ref", l.LineRef), ("cap", returnCap), ("requested", baseQty), ("cap_source", "RETURN_LINE"))); continue; }
                if (baseQty > invoiceCap) { errors.Add(Error("ORDER.CREDIT_EXCEEDS_INVOICED", "P4", p + "/quantity", ("line_ref", l.LineRef), ("cap", invoiceCap), ("requested", baseQty), ("cap_source", "INVOICE_LINE"))); continue; }
                net = OrderKind.NetAmount(baseQty / ratio, il.UnitPrice, priceUnit, il.DiscountPct, scale);
                runningReturn[(returnId, returnLine)] = runningReturn.GetValueOrDefault((returnId, returnLine)) + baseQty;
            }
            else
            {
                net = l.CreditAmountNet!.Value;
                decimal cap = Math.Max(0m, il.Net - creditedNet);
                if (net > cap) { errors.Add(Error("ORDER.CREDIT_EXCEEDS_INVOICED", "P4", p + "/credit_amount_net", ("line_ref", l.LineRef), ("cap", cap), ("requested", net), ("cap_source", "INVOICE_LINE"))); continue; }
            }
            runningQty[l.InvoiceLineNo] = runningQty.GetValueOrDefault(l.InvoiceLineNo) + baseQty;
            runningNet[l.InvoiceLineNo] = runningNet.GetValueOrDefault(l.InvoiceLineNo) + net;
            work.Add(new Work(i, l, il, quantity, baseQty, net, taxClass, origin, destination));
        }
        if (errors.Count > 0) return CommandResult<SalesCreditNoteResult>.Rejected(errors);

        // the finance document: the revenue of the original lines debited with the tax code of the invoice line, the receivable credit as the balance
        var lines = work.Select(w => new RequestLine(w.Input.LineRef, w.Line.RevenueAccount, w.Net, TaxClassId: w.TaxClassId, TaxDate: req.CreditDate, OriginCountry: w.Origin, DestinationCountry: w.Destination, TaxCodeId: w.Line.TaxCodeId)).ToList();
        const string ArRef = "AR";
        lines.Add(new RequestLine(ArRef, arAccount.AccountId, 0m, OpenItem: new OpenItemRequest(customer, req.CreditDate, terms, req.DueDate ?? (terms is null ? req.CreditDate : null))));
        var journal = new PostJournalDocumentRequest(companyId, null, "AR_CREDIT", req.CreditDate, postingDate, currency, null, null, req.SourceDocumentRef, lines, customer);

        Guid creditId = Guid.NewGuid();
        List<Row> rows = [];
        var header = new Dictionary<string, object?>
        {
            ["sales_invoice_id"] = creditId, ["invoice_kind"] = "CREDIT_NOTE", ["customer_partner_id"] = customer, ["invoice_date"] = req.CreditDate, ["due_date"] = req.DueDate, ["payment_terms_id"] = terms,
            ["order_currency"] = currency, ["credits_invoice_id"] = req.SalesInvoiceId, ["credit_kind"] = req.CreditKind, ["reversal_of_sales_invoice_id"] = null, ["reason_code"] = req.ReasonCode,
            ["invoice_mode"] = null, ["line_count"] = work.Count
        };
        byte[]? DigestOf(JournalDocumentPoster.EmbeddedFacts facts)
        {
            rows = [];
            foreach (Work w in work)
            {
                TaxLineSeed? tax = facts.Tax.Lines.FirstOrDefault(t => t.Bases.Any(b => b.Index == w.Index));
                Guid? code = tax?.CodeId ?? (facts.Tax.BaseCodes.TryGetValue(w.Index, out Guid c) ? c : null);
                rows.Add(new Row(rows.Count + 1, w.Line.OrderId, w.Line.OrderLineNo, w.Line.DeliveryId, w.Line.DeliveryLineNo, -w.Quantity, w.Line.UnitCode, -w.BaseQuantity, w.Line.UnitPrice, w.Line.DiscountPct, -w.Net,
                    code, tax?.VersionId, tax?.TaxDate, w.Index + 1, w.Input.InvoiceLineNo, w.Input.ReturnId, w.Input.ReturnLineNo));
            }
            return CreditRules.Digest(header, rows.Select(r => (IReadOnlyDictionary<string, object?>)RowOf(r)).ToList());
        }

        Guid logId = Guid.Empty;
        decimal taxTotal = 0m, gross = 0m;
        async Task AfterAsync(TenantTransaction t, PostedDocument posted, JournalDocumentPoster.EmbeddedFacts facts)
        {
            taxTotal = facts.Tax.Lines.Sum(x => x.TaxTc);
            gross = facts.Request.Lines.Single(x => x.LineRef == ArRef).AmountTc;
            await using (var cmd = t.Command(
                "insert into core.sales_invoice (tenant_id, company_id, sales_invoice_id, invoice_kind, customer_partner_id, invoice_date, due_date, payment_terms_id, order_currency, journal_document_id, credits_invoice_id, " +
                "credit_kind, reason_code, source_system, source_document_ref, created_by, correlation_id, idempotency_key) values (@t, @c, @id, 'CREDIT_NOTE', @cust, @d, @due, @terms, @cur, @j, @credits, @kind, @reason, " +
                "@src, @ref, @by, @corr, @key)"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("id", creditId); cmd.Parameters.AddWithValue("cust", customer);
                cmd.Parameters.AddWithValue("d", req.CreditDate); cmd.Parameters.AddWithValue("cur", currency); cmd.Parameters.AddWithValue("j", posted.DocumentId); cmd.Parameters.AddWithValue("credits", req.SalesInvoiceId);
                cmd.Parameters.AddWithValue("kind", req.CreditKind); cmd.Parameters.AddWithValue("src", ctx.SourceSystem); cmd.Parameters.AddWithValue("by", ctx.Actor); cmd.Parameters.AddWithValue("corr", ctx.CorrelationId);
                cmd.Parameters.AddWithValue("key", ctx.IdempotencyKey);
                cmd.Parameters.Add(new NpgsqlParameter("due", NpgsqlDbType.Date) { Value = (object?)req.DueDate ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("terms", NpgsqlDbType.Uuid) { Value = (object?)terms ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("reason", NpgsqlDbType.Text) { Value = (object?)req.ReasonCode ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("ref", NpgsqlDbType.Text) { Value = (object?)req.SourceDocumentRef ?? DBNull.Value });
                await cmd.ExecuteNonQueryAsync(ct);
            }
            foreach (Row r in rows) await InsertRowAsync(t, tenantId, companyId, creditId, r, ct);
            logId = await CommandSupport.WriteLogAsync(t, tenantId, companyId, ctx, CreateName, fingerprint, "SALES_INVOICE", creditId, ct);
            await CommandSupport.WriteOutboxAsync(t, tenantId, companyId, ctx, "invoice.posted", "SALES_INVOICE", $"{companyId:D}/{creditId:D}", "POSTED", DateTime.UtcNow, isAudit: true,
                JsonSerializer.Serialize(new Dictionary<string, object?> { ["chain_type"] = "LEDGER", ["row_hash"] = posted.RowHash, ["chain_seq"] = posted.ChainSeq }),
                JsonSerializer.Serialize(new Dictionary<string, object?> { ["document_number"] = posted.DocumentNumber, ["customer_partner_id"] = customer, ["lines"] = rows.Count, ["journal_document_id"] = posted.DocumentId, ["credit_note"] = true }), ct);
        }

        var embedding = new JournalDocumentPoster.PostEmbedding(CreateName, "ORDER", ArRef, DigestOf, WriteLog: false, Commit: false, AfterAsync);
        var posted = await _poster.PostCoreAsync(tx, tenantId, ctx, journal, fingerprint, embedding, ct);
        if (posted.Status != CommandStatus.Ok) return CommandResult<SalesCreditNoteResult>.Rejected(posted.Errors.ToList());
        PostedDocument doc = posted.Value!;
        var result = new SalesCreditNoteResult(creditId, doc.DocumentId, doc.DocumentNumber, req.CreditKind, work.Sum(w => w.Net), taxTotal, -gross, doc.RowHash,
            rows.Select(r => new SalesInvoiceLineResult(r.LineNo, work[r.LineNo - 1].Input.LineRef, r.OrderId, r.OrderLineNo, r.DeliveryId, r.DeliveryLineNo, r.BaseQuantity, r.Net, r.JournalLineNo, r.TaxCodeId)).ToList());
        await tx.CommitAsync(ct);   // the deferred checks of the journal document, the outbox position
        return CommandResult<SalesCreditNoteResult>.Ok(result, logId, posted.Warnings);
    }

    private static async Task<SalesCreditNoteResult> LoadAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid id, CancellationToken ct)
    {
        Guid journal; string kind;
        await using (var cmd = tx.Command("select journal_document_id, credit_kind from core.sales_invoice where tenant_id = @t and company_id = @c and sales_invoice_id = @i"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            journal = r.GetGuid(0); kind = r.IsDBNull(1) ? "" : r.GetString(1).Trim();
        }
        string number; byte[] hash;
        await using (var cmd = tx.Command("select document_number, row_hash from core.journal_document where tenant_id = @t and company_id = @c and document_id = @d"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", journal);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            number = r.GetString(0); hash = (byte[])r[1];
        }
        decimal tax, gross;
        await using (var cmd = tx.Command(
            "select coalesce(sum(amount_tc) filter (where line_type = 'TAX'), 0), coalesce(sum(amount_tc) filter (where line_type = 'BASE' and amount_tc < 0), 0) from core.journal_line where tenant_id = @t and company_id = @c and document_id = @d"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", journal);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            tax = r.GetDecimal(0); gross = -r.GetDecimal(1);
        }
        var lines = new List<SalesInvoiceLineResult>();
        await using (var cmd = tx.Command(
            "select line_no, sales_order_id, sales_order_line_no, delivery_id, delivery_line_no, base_quantity, net_amount, journal_line_no, tax_code_id from core.sales_invoice_line where tenant_id = @t and company_id = @c and sales_invoice_id = @i order by line_no"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                lines.Add(new SalesInvoiceLineResult(r.GetInt32(0), "", r.GetGuid(1), r.GetInt32(2), r.IsDBNull(3) ? null : r.GetGuid(3), r.IsDBNull(4) ? null : r.GetInt32(4), r.GetDecimal(5), r.GetDecimal(6), r.GetInt32(7), r.IsDBNull(8) ? null : r.GetGuid(8)));
        }
        return new SalesCreditNoteResult(id, journal, number, kind, lines.Sum(l => l.NetAmount), tax, gross, Canonical.FormatHex32(hash), lines);
    }

    // ---------------------------------------------------------------------------------------------------------
    // sales-credit-notes:reverse
    // ---------------------------------------------------------------------------------------------------------
    public async Task<CommandResult<ReversedSalesCreditNote>> ReverseAsync(Guid tenantId, CommandContext ctx, ReverseSalesCreditNoteRequest req, CancellationToken ct = default)
    {
        static CommandResult<ReversedSalesCreditNote> Fail(CommandError e) => CommandResult<ReversedSalesCreditNote>.Rejected([e]);
        if (!ctx.Permissions.Contains(InvoiceRules.SalesPermission)) return Fail(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", InvoiceRules.SalesPermission)));
        var errors = MasterSupport.ValidateContext(ctx);
        if (req.CompanyId == Guid.Empty) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/company_id", ("reason", "required")));
        if (req.CreditNoteId == Guid.Empty) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/credit_note_id", ("reason", "required")));
        if (errors.Count > 0) return CommandResult<ReversedSalesCreditNote>.Rejected(errors);
        if (string.IsNullOrWhiteSpace(req.ReasonCode)) return Fail(Error("REVERSAL.REASON_REQUIRED", "P1", "/reason_code"));
        if (!ReversalRules.ExternalReasons.Contains(req.ReasonCode)) return Fail(Error("REVERSAL.REASON_NOT_ALLOWED", "P1", "/reason_code", ("allowed_values", ReversalRules.ExternalReasons)));
        byte[] fingerprint = StockReversalRules.Fingerprint(ReverseName, req.CompanyId, req.CreditNoteId, req.ReasonCode, req.PostingDate);

        Guid journalId; string kind; bool reversed;
        await using (TenantTransaction probe = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct))
        await using (var cmd = probe.Command(
            "select journal_document_id, invoice_kind, exists (select 1 from core.sales_invoice m where m.tenant_id = i.tenant_id and m.company_id = i.company_id and m.reversal_of_sales_invoice_id = i.sales_invoice_id) " +
            "from core.sales_invoice i where tenant_id = @t and company_id = @c and sales_invoice_id = @i"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("i", req.CreditNoteId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct) || r.GetString(1) is not ("CREDIT_NOTE" or "REVERSAL")) return Fail(Error("ORDER.CREDIT_NOTE_NOT_FOUND", "P2", "/credit_note_id"));
            journalId = r.GetGuid(0); kind = r.GetString(1); reversed = r.GetBoolean(2);
        }
        if (kind != "CREDIT_NOTE") return Fail(Error("REVERSAL.TYPE_NOT_REVERSIBLE", "P2", "/credit_note_id", ("invoice_kind", kind)));

        Guid mirrorId = Guid.NewGuid();
        byte[]? digest = null;
        List<Row> mirrorRows = [];
        Guid customer = Guid.Empty; string currency = ""; DateOnly mirrorDate = default;
        var mode = new DocumentReverser.ReverseMode(ReverseName, "AR_CREDIT", "ORDER",
            async (tx, original, token) =>
            {
                if (reversed) return Error("ORDER.CREDIT_NOTE_ALREADY_REVERSED", "P2", "/credit_note_id");
                var lines = new List<Row>();
                await using (var cmd = tx.Command(
                    "select line_no, sales_order_id, sales_order_line_no, delivery_id, delivery_line_no, quantity, unit_code, base_quantity, unit_price, discount_pct, net_amount, tax_code_id, tax_code_version_id, tax_date, " +
                    "journal_line_no, credits_invoice_line_no, return_id, return_line_no from core.sales_invoice_line where tenant_id = @t and company_id = @c and sales_invoice_id = @i order by line_no"))
                {
                    cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("i", req.CreditNoteId);
                    await using var r = await cmd.ExecuteReaderAsync(token);
                    while (await r.ReadAsync(token))
                        lines.Add(new Row(r.GetInt32(0), r.GetGuid(1), r.GetInt32(2), r.IsDBNull(3) ? null : r.GetGuid(3), r.IsDBNull(4) ? null : r.GetInt32(4), r.GetDecimal(5), r.GetString(6).Trim(), r.GetDecimal(7),
                            r.GetDecimal(8), r.GetDecimal(9), r.GetDecimal(10), r.IsDBNull(11) ? null : r.GetGuid(11), r.IsDBNull(12) ? null : r.GetGuid(12), r.IsDBNull(13) ? null : DateOnly.FromDateTime(r.GetDateTime(13)),
                            r.GetInt32(14), r.IsDBNull(15) ? null : r.GetInt32(15), r.IsDBNull(16) ? null : r.GetGuid(16), r.IsDBNull(17) ? null : r.GetInt32(17)));
                }
                await using (var cmd = tx.Command(
                    "select sales_order_id from core.sales_order where tenant_id = @t and company_id = @c and sales_order_id = any(@ids) order by sales_order_id for update"))
                {
                    cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("ids", lines.Select(l => l.OrderId).Distinct().Order().ToArray());
                    await using var r = await cmd.ExecuteReaderAsync(token);
                    while (await r.ReadAsync(token)) { }
                }
                await using (var cmd = tx.Command("select customer_partner_id, order_currency from core.sales_invoice where tenant_id = @t and company_id = @c and sales_invoice_id = @i"))
                {
                    cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("i", req.CreditNoteId);
                    await using var r = await cmd.ExecuteReaderAsync(token);
                    await r.ReadAsync(token);
                    customer = r.GetGuid(0); currency = r.GetString(1).Trim();
                }
                mirrorDate = req.PostingDate ?? (DateOnly)original.Header["posting_date"]!;
                // the mirror rows are positive: the credited quantity and amount are restored (a return credit does not change the return, 6.5)
                mirrorRows = lines.Select(l => l with { Quantity = -l.Quantity, BaseQuantity = -l.BaseQuantity, Net = -l.Net }).ToList();
                var header = new Dictionary<string, object?>
                {
                    ["sales_invoice_id"] = mirrorId, ["invoice_kind"] = "REVERSAL", ["customer_partner_id"] = customer, ["invoice_date"] = mirrorDate, ["due_date"] = null, ["payment_terms_id"] = null,
                    ["order_currency"] = currency, ["credits_invoice_id"] = null, ["credit_kind"] = null, ["reversal_of_sales_invoice_id"] = req.CreditNoteId, ["reason_code"] = req.ReasonCode,
                    ["invoice_mode"] = null, ["line_count"] = mirrorRows.Count
                };
                digest = CreditRules.Digest(header, mirrorRows.Select(r => (IReadOnlyDictionary<string, object?>)RowOf(r)).ToList());
                return null;
            },
            async (tx, mirrorJournalId, token) =>
            {
                await using (var cmd = tx.Command(
                    "insert into core.sales_invoice (tenant_id, company_id, sales_invoice_id, invoice_kind, customer_partner_id, invoice_date, order_currency, journal_document_id, reversal_of_sales_invoice_id, reason_code, " +
                    "source_system, created_by, correlation_id, idempotency_key) values (@t, @c, @id, 'REVERSAL', @cust, @d, @cur, @j, @orig, @reason, @src, @by, @corr, @key)"))
                {
                    cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("id", mirrorId); cmd.Parameters.AddWithValue("cust", customer);
                    cmd.Parameters.AddWithValue("d", mirrorDate); cmd.Parameters.AddWithValue("cur", currency); cmd.Parameters.AddWithValue("j", mirrorJournalId); cmd.Parameters.AddWithValue("orig", req.CreditNoteId);
                    cmd.Parameters.AddWithValue("reason", req.ReasonCode); cmd.Parameters.AddWithValue("src", ctx.SourceSystem); cmd.Parameters.AddWithValue("by", ctx.Actor);
                    cmd.Parameters.AddWithValue("corr", ctx.CorrelationId); cmd.Parameters.AddWithValue("key", ctx.IdempotencyKey);
                    await cmd.ExecuteNonQueryAsync(token);
                }
                foreach (Row r in mirrorRows) await InsertRowAsync(tx, tenantId, req.CompanyId, mirrorId, r, token);
                await CommandSupport.WriteOutboxAsync(tx, tenantId, req.CompanyId, ctx, "invoice.reversed", "SALES_INVOICE", $"{req.CompanyId:D}/{req.CreditNoteId:D}", "REVERSED", DateTime.UtcNow, isAudit: true, null,
                    JsonSerializer.Serialize(new Dictionary<string, object?> { ["reversal_invoice_id"] = mirrorId, ["journal_document_id"] = mirrorJournalId, ["lines"] = mirrorRows.Count, ["credit_note"] = true }), token);
            },
            () => digest);

        var result = await new DocumentReverser(source).ReverseOwnedAsync(tenantId, ctx, new ReverseDocumentRequest(journalId, req.PostingDate, req.ReasonCode, null), fingerprint, mode, ct);
        if (result.Status != CommandStatus.Ok) return CommandResult<ReversedSalesCreditNote>.Rejected(result.Errors.ToList());
        PostedDocument doc = result.Value!;
        Guid reversalId;
        await using (TenantTransaction read = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct))
        await using (var cmd = read.Command("select sales_invoice_id from core.sales_invoice where tenant_id = @t and company_id = @c and journal_document_id = @j"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("j", doc.DocumentId);
            reversalId = (Guid)(await cmd.ExecuteScalarAsync(ct))!;
        }
        return CommandResult<ReversedSalesCreditNote>.Ok(new ReversedSalesCreditNote(req.CreditNoteId, reversalId, doc.DocumentId, doc.DocumentNumber, doc.RowHash), result.CommandLogId ?? Guid.Empty, result.Warnings, result.IdempotentReplay);
    }
}
