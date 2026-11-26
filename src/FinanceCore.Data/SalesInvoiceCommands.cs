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
/// sales-invoices:create (11, 6.4), first slice. The command builds the finance document in the pipeline of 06 (the tax of each revenue line from its tax class, origin and destination,
/// the gross receivable as the balance of the lines and the tax, an open item with the partner, the terms and the due date) and the link records of 6.9 in one transaction of the posting
/// role: the order rows first (11, 9), then the pipeline with its own locks. The journal document is an AR_INVOICE with origin ORDER whose detail_digest is the digest of the link records
/// (ADR-052). Both modes (6.4): CORE_BUILDS takes quantity and price from the order, APP_SUPPLIES takes them from the caller and keeps the order price beside them. The quantity is limited
/// by what is open to invoice (4.3) under the order lock, and a delivery line that is named is limited by its own quantity. Built choices are in 07, 13.2aa. Not built: credit notes, the reversal,
/// dropship lines, intercompany, tracking, the tax point of a line invoiced without a delivery reference (the invoice date is used), the invoice lines of several delivery lines in the price check.
/// </summary>
public sealed partial class SalesInvoiceCommands(NpgsqlDataSource source)
{
    private readonly JournalDocumentPoster _poster = new(source);
    private const string CommandName = "sales-invoices:create";

    private static CommandError Error(string code, string phase, string path, params (string Key, object? Value)[] parameters) =>
        JournalDocumentPoster.Error(code, phase, path, parameters);

    private static CommandResult<SalesInvoiceResult> Reject(CommandError e) => CommandResult<SalesInvoiceResult>.Rejected([e]);

    private sealed record OrderLine(
        Guid OrderId, int LineNo, Guid ItemId, decimal Quantity, string UnitCode, decimal BaseQuantity, decimal UnitPrice, decimal PriceUnitQuantity, decimal DiscountPct, Guid? TaxClassId, Guid? Warehouse,
        string Origin, string Destination, string Policy, decimal OverTolerancePct, string FulfilmentType, string LineStatus, Guid ItemGroup);

    private sealed record Link(int DeliveryLine, Guid? DeliveryId, int? DeliveryLineNo, decimal Quantity, decimal BaseQuantity, decimal Net, DateOnly? DeliveryDate);

    private sealed record Work(int Index, SalesInvoiceLineInput Input, OrderLine Line, decimal Quantity, string UnitCode, decimal BaseQuantity, decimal UnitPrice, decimal DiscountPct, decimal Net, List<Link> Links, Guid RevenueAccount);

    public async Task<CommandResult<SalesInvoiceResult>> CreateAsync(Guid tenantId, CommandContext ctx, CreateSalesInvoiceRequest req, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(InvoiceRules.SalesPermission)) return Reject(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", InvoiceRules.SalesPermission)));
        var errors = MasterSupport.ValidateContext(ctx);
        void Invalid(string path, string reason) => errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", path, ("reason", reason)));
        if (req.CompanyId == Guid.Empty) Invalid("/company_id", "required");
        if (req.Lines is null or { Count: 0 }) errors.Add(Error("SCHEMA.REQUIRED_FIELD_MISSING", "P1", "/lines"));
        for (int i = 0; i < (req.Lines?.Count ?? 0); i++)
        {
            SalesInvoiceLineInput l = req.Lines![i];
            string p = $"/lines/{i}";
            if (string.IsNullOrWhiteSpace(l.LineRef) || l.LineRef.Length > 100) Invalid(p + "/line_ref", "required, at most 100 characters");
            else if (req.Lines.Take(i).Any(x => x.LineRef == l.LineRef)) errors.Add(Error("SCHEMA.DUPLICATE_LINE_REF", "P1", p + "/line_ref", ("line_ref", l.LineRef)));
            if (l.SalesOrderId == Guid.Empty || l.SalesOrderLineNo < 1) Invalid(p + "/sales_order_id", "an order and a line number");
            if (l.Quantity is { } q && (q <= 0m || Math.Round(q, 6) != q)) Invalid(p + "/quantity", "more than 0, at most 6 fraction digits");
            if (l.UnitPrice is < 0m || (l.UnitPrice is { } up && Math.Round(up, 4) != up)) Invalid(p + "/unit_price", "not negative, at most 4 fraction digits");
            if (l.DiscountPct is < 0m or > 100m) Invalid(p + "/discount_pct", "between 0 and 100");
            if (l.DeliveryLineRefs?.Any(d => d.DeliveryId == Guid.Empty || d.DeliveryLineNo < 1) == true) Invalid(p + "/delivery_line_refs", "a delivery and a line number");
        }
        if (req.SourceDocumentRef?.Length > 100) Invalid("/source_document_ref", "at most 100 characters");
        if (errors.Count > 0) return CommandResult<SalesInvoiceResult>.Rejected(errors);
        byte[] fingerprint = InvoiceRules.Fingerprint(req);

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
                        ? CommandResult<SalesInvoiceResult>.Failed(Error("SYSTEM.REPLAY_RESULT_MISSING", "P1", ""))
                        : CommandResult<SalesInvoiceResult>.Ok(await LoadAsync(tx, tenantId, req.CompanyId, earlier.EntityId.Value, ct), earlier.CommandLogId, replay: true);
                }
                return await CreateCoreAsync(tx, tenantId, ctx, req, fingerprint, ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.TableName == "command_log" && attempt == 0)
            {
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                return CommandResult<SalesInvoiceResult>.Failed(new CommandError("SYSTEM.LOCK_TIMEOUT", "P7", "", null, true));
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure)
            {
                return CommandResult<SalesInvoiceResult>.Failed(new CommandError("SYSTEM.CONFLICT", "P7", "", null, true));
            }
        }
    }

    /// <summary>icId and icCounterpart make it the AR side of an IC pair (11, 6.7): the ids and the marker go to the journal document and the link row. owned false: the transaction is the caller's, no log row and no commit.</summary>
    internal async Task<CommandResult<SalesInvoiceResult>> CreateCoreAsync(
        TenantTransaction tx, Guid tenantId, CommandContext ctx, CreateSalesInvoiceRequest req, byte[] fingerprint, CancellationToken ct, Guid? icId = null, Guid? icCounterpart = null, bool owned = true)
    {
        Guid companyId = req.CompanyId;
        var company = await JournalDocumentPoster.ReadCompanyAsync(tx, tenantId, companyId, ct);
        if (company is null) return Reject(Error("CONFIG.INVALID_REFERENCE", "P2", "/company_id", ("company_id", companyId)));
        if (company.Status != "ACTIVE") return Reject(Error("COMPANY.NOT_ACTIVE", "P2", "/company_id", ("status", company.Status)));
        DateOnly postingDate = req.PostingDate ?? req.InvoiceDate;
        var errors = new List<CommandError>();

        // the mode in force (3.1): the newest setting version at the invoice date, CORE_BUILDS without one
        string mode = "CORE_BUILDS";
        await using (var cmd = tx.Command(
            "select sales_invoice_mode from core.company_order_setting_version where tenant_id = @t and company_id = @c and effective_from <= @d order by effective_from desc limit 1"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", postingDate);
            if (await cmd.ExecuteScalarAsync(ct) is string m) mode = m.Trim();
        }

        // position 1 of the lock order: the order rows, sorted
        var orderIds = req.Lines.Select(l => l.SalesOrderId).Distinct().Order().ToArray();
        var orders = new Dictionary<Guid, (string Status, Guid Customer, string Currency, Guid? Terms)>();
        await using (var cmd = tx.Command(
            "select sales_order_id, lifecycle_status, customer_partner_id, order_currency, payment_terms_id from core.sales_order where tenant_id = @t and company_id = @c and sales_order_id = any(@ids) order by sales_order_id for update"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("ids", orderIds);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) orders[r.GetGuid(0)] = (r.GetString(1), r.GetGuid(2), r.GetString(3).Trim(), r.IsDBNull(4) ? null : r.GetGuid(4));
        }
        for (int i = 0; i < req.Lines.Count; i++)
        {
            if (!orders.TryGetValue(req.Lines[i].SalesOrderId, out var o)) errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", $"/lines/{i}/sales_order_id", ("order_id", req.Lines[i].SalesOrderId)));
            else if (o.Status == "CANCELLED") errors.Add(Error("ORDER.ORDER_NOT_OPEN", "P2", $"/lines/{i}/sales_order_id", ("status", o.Status)));
        }
        if (errors.Count > 0) return CommandResult<SalesInvoiceResult>.Rejected(errors);
        if (orders.Values.Select(o => (o.Customer, o.Currency, o.Terms)).Distinct().Count() != 1)
            return Reject(Error("SCHEMA.INVALID_VALUE", "P2", "/lines", ("reason", "the orders of one invoice share the customer, the currency and the payment terms")));
        var (_, customer, currency, terms) = orders.Values.First();
        await using (var cmd = tx.Command("select restriction_status from core.business_partner where tenant_id = @t and partner_id = @p"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("p", customer);
            if (await cmd.ExecuteScalarAsync(ct) as string == "RESTRICTED") return Reject(Error("MASTER.PARTNER_RESTRICTED", "P2", "/lines", ("partner_id", customer)));
        }
        int scale;
        await using (var cmd = tx.Command("select minor_units from platform.ref_currency where currency_code = @c"))
        {
            cmd.Parameters.AddWithValue("c", currency);
            scale = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct) ?? 2, CultureInfo.InvariantCulture);
        }
        var arAccount = await AccountDeterminationResolver.ResolveAsync(tx, tenantId, companyId, "AR_CONTROL", null, null, customer, null, postingDate, ct);
        if (arAccount is null) return Reject(Error("ORDER.ACCOUNT_DETERMINATION_MISSING", "P5", "/lines", ("role_code", "AR_CONTROL"), ("partner_id", customer)));

        // the lines under the lock
        var work = new List<Work>();
        var running = new Dictionary<(Guid, int), decimal>();
        var linkedRunning = new Dictionary<(Guid, int), decimal>();
        for (int i = 0; i < req.Lines.Count; i++)
        {
            SalesInvoiceLineInput l = req.Lines[i];
            string p = $"/lines/{i}";
            OrderLine? ol = await ReadOrderLineAsync(tx, tenantId, companyId, l.SalesOrderId, l.SalesOrderLineNo, ct);
            if (ol is null) { errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", p + "/sales_order_line_no", ("line_no", l.SalesOrderLineNo))); continue; }
            if (ol.LineStatus == "CANCELLED") { errors.Add(Error("ORDER.LINE_NOT_OPEN", "P2", p + "/sales_order_line_no", ("line_no", l.SalesOrderLineNo), ("status", ol.LineStatus))); continue; }
            if (ol.FulfilmentType == "DROPSHIP") { errors.Add(Error("LIMIT.INVOICE_DROPSHIP_NOT_BUILT", "P2", p + "/sales_order_line_no", ("line_no", l.SalesOrderLineNo))); continue; }
            bool goods = ol.FulfilmentType == "STOCK";
            decimal price = ol.UnitPrice, discount = ol.DiscountPct;
            if (mode == "APP_SUPPLIES")
            {
                if (l.Quantity is null) { errors.Add(Error("ORDER.INVOICE_LINE_REFERENCE_MISSING", "P1", p + "/quantity", ("line_ref", l.LineRef), ("field", "quantity"))); continue; }
                if (l.UnitPrice is null) { errors.Add(Error("ORDER.INVOICE_LINE_REFERENCE_MISSING", "P1", p + "/unit_price", ("line_ref", l.LineRef), ("field", "unit_price"))); continue; }
                if (l.DiscountPct is null) { errors.Add(Error("ORDER.INVOICE_LINE_REFERENCE_MISSING", "P1", p + "/discount_pct", ("line_ref", l.LineRef), ("field", "discount_pct"))); continue; }
                if (goods && ol.Policy == "DELIVERED" && l.DeliveryLineRefs is not { Count: > 0 })
                { errors.Add(Error("ORDER.INVOICE_LINE_REFERENCE_MISSING", "P1", p + "/delivery_line_refs", ("line_ref", l.LineRef), ("field", "delivery_line_refs"))); continue; }
                price = l.UnitPrice.Value; discount = l.DiscountPct.Value;
            }
            else if (l.UnitPrice is not null || l.DiscountPct is not null)
            { errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", p + "/unit_price", ("reason", "prices come from the order in mode CORE_BUILDS (6.4)"))); continue; }

            // quantity in the unit asked for, its base quantity, and what is open to invoice
            string baseUnit; int qtyScale;
            await using (var cmd = tx.Command("select base_unit, quantity_scale from core.item where tenant_id = @t and item_id = @i"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("i", ol.ItemId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                await r.ReadAsync(ct);
                baseUnit = r.GetString(0).Trim(); qtyScale = r.GetInt16(1);
            }
            string unit = l.UnitCode ?? ol.UnitCode;
            decimal? factor = unit == ol.UnitCode ? ol.BaseQuantity / ol.Quantity : await OrderCommands.FactorAsync(tx, tenantId, ol.ItemId, unit, baseUnit, ct);
            if (factor is null) { errors.Add(Error("INVENTORY.UNIT_CONVERSION_MISSING", "P2", p + "/unit_code", ("item_id", ol.ItemId), ("unit_code", unit))); continue; }
            var key = (l.SalesOrderId, l.SalesOrderLineNo);
            decimal delivered = await SumAsync(tx, "select coalesce(sum(base_quantity), 0) from core.sales_delivery_line where tenant_id = @t and company_id = @c and sales_order_id = @o and sales_order_line_no = @n", tenantId, companyId, key, ct);
            decimal invoiced = await InvoicedAsync(tx, tenantId, companyId, key, ct) + running.GetValueOrDefault(key);
            decimal open = InvoiceRules.OpenToInvoice(ol.Policy, ol.BaseQuantity, ol.OverTolerancePct, delivered, invoiced);
            // without a quantity the default is the delivered less the invoiced (DELIVERED) or the ordered less the invoiced (ORDERED); the over tolerance is only the ceiling (4.3)
            decimal defaultBase = ol.Policy == "DELIVERED" ? open : Math.Min(open, Math.Max(0m, ol.BaseQuantity - invoiced));
            decimal baseQty = l.Quantity is { } given ? given * factor.Value : defaultBase;
            decimal quantity = l.Quantity ?? baseQty / factor.Value;
            if (baseQty <= 0m) { errors.Add(Error("ORDER.INVOICE_EXCEEDS_OPEN", "P4", p + "/quantity", ("base_quantity", baseQty), ("open_to_invoice", open))); continue; }
            if (Math.Round(baseQty, qtyScale) != baseQty) { errors.Add(Error("INVENTORY.QUANTITY_SCALE_EXCEEDED", "P1", p + "/quantity", ("base_quantity", baseQty), ("quantity_scale", qtyScale))); continue; }
            if (baseQty > open) { errors.Add(Error("ORDER.INVOICE_EXCEEDS_OPEN", "P4", p + "/quantity", ("base_quantity", baseQty), ("open_to_invoice", open), ("invoiced", invoiced))); continue; }

            // the delivery lines that are named: each belongs to the order line and takes at most its own quantity
            var links = new List<Link>();
            decimal orderUnitQuantity = baseQty / (ol.BaseQuantity / ol.Quantity);
            decimal net = OrderKind.NetAmount(orderUnitQuantity, price, ol.PriceUnitQuantity, discount, scale);
            if (l.DeliveryLineRefs is { Count: > 0 } refs)
            {
                decimal left = baseQty; bool bad = false;
                foreach (DeliveryLineRef d in refs)
                {
                    await using var cmd = tx.Command(
                        "select x.base_quantity, h.delivery_date, h.status, h.reversal_of_delivery_id is not null from core.sales_delivery_line x join core.sales_delivery h on h.tenant_id = x.tenant_id and h.company_id = x.company_id and h.delivery_id = x.delivery_id " +
                        "where x.tenant_id = @t and x.company_id = @c and x.delivery_id = @d and x.line_no = @n and x.sales_order_id = @o and x.sales_order_line_no = @ol");
                    cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", d.DeliveryId); cmd.Parameters.AddWithValue("n", d.DeliveryLineNo);
                    cmd.Parameters.AddWithValue("o", l.SalesOrderId); cmd.Parameters.AddWithValue("ol", l.SalesOrderLineNo);
                    decimal dq; DateOnly dd;
                    await using (var r = await cmd.ExecuteReaderAsync(ct))
                    {
                        if (!await r.ReadAsync(ct) || r.GetString(2) != "posted" || r.GetBoolean(3))
                        { errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", p + "/delivery_line_refs", ("delivery_id", d.DeliveryId), ("reason", "a posted delivery line of this order line"))); bad = true; break; }
                        dq = r.GetDecimal(0); dd = DateOnly.FromDateTime(r.GetDateTime(1));
                    }
                    decimal linkedBefore = await LinkedAsync(tx, tenantId, companyId, d, ct) + linkedRunning.GetValueOrDefault((d.DeliveryId, d.DeliveryLineNo));
                    decimal take = Math.Min(left, Math.Max(0m, dq - linkedBefore));
                    if (take <= 0m) continue;
                    linkedRunning[(d.DeliveryId, d.DeliveryLineNo)] = linkedRunning.GetValueOrDefault((d.DeliveryId, d.DeliveryLineNo)) + take;
                    left -= take;
                    links.Add(new Link(links.Count + 1, d.DeliveryId, d.DeliveryLineNo, take / factor.Value, take, 0m, dd));
                    if (left == 0m) break;
                }
                if (bad) continue;
                if (left > 0m) { errors.Add(Error("ORDER.INVOICE_EXCEEDS_OPEN", "P4", p + "/delivery_line_refs", ("base_quantity", baseQty), ("not_covered", left))); continue; }
                // the net amount is shared by the quantity of the delivery lines, the last one takes what is left
                decimal assigned = 0m;
                for (int k = 0; k < links.Count; k++)
                {
                    decimal share = k == links.Count - 1 ? net - assigned : Math.Round(net * links[k].BaseQuantity / baseQty, scale, MidpointRounding.AwayFromZero);
                    assigned += share;
                    links[k] = links[k] with { Net = share };
                }
            }
            else links.Add(new Link(1, null, null, quantity, baseQty, net, null));

            var revenue = await AccountDeterminationResolver.ResolveAsync(tx, tenantId, companyId, "REVENUE", ol.ItemGroup, ol.Warehouse, customer, ol.ItemId, postingDate, ct);
            if (revenue is null) { errors.Add(Error("ORDER.ACCOUNT_DETERMINATION_MISSING", "P5", p, ("role_code", "REVENUE"), ("item_id", ol.ItemId))); continue; }
            running[key] = running.GetValueOrDefault(key) + baseQty;
            work.Add(new Work(i, l, ol, quantity, unit, baseQty, price, discount, net, links, revenue.AccountId));
        }
        if (errors.Count > 0) return CommandResult<SalesInvoiceResult>.Rejected(errors);

        // the finance document: a revenue line per invoice line with the tax facts of the order line, and the receivable that the pipeline balances
        var lines = new List<RequestLine>();
        foreach (Work w in work)
        {
            // the tax point is the delivery date of a goods line invoiced from a delivery, the invoice date otherwise (6.4)
            DateOnly taxDate = w.Line.FulfilmentType == "STOCK" && w.Links.FirstOrDefault(k => k.DeliveryDate is not null)?.DeliveryDate is { } dd ? dd : req.InvoiceDate;
            lines.Add(new RequestLine(w.Input.LineRef, w.RevenueAccount, -w.Net, TaxClassId: w.Line.TaxClassId, TaxDate: taxDate, OriginCountry: w.Line.Origin, DestinationCountry: w.Line.Destination, TaxCodeId: w.Input.TaxCodeId));
        }
        const string ArRef = "AR";
        lines.Add(new RequestLine(ArRef, arAccount.AccountId, 0m, OpenItem: new OpenItemRequest(customer, req.InvoiceDate, terms, req.DueDate ?? (terms is null ? req.InvoiceDate : null))));
        var journal = new PostJournalDocumentRequest(companyId, null, "AR_INVOICE", req.InvoiceDate, postingDate, currency, null, null, req.SourceDocumentRef, lines, customer);

        Guid invoiceId = Guid.NewGuid();
        List<Dictionary<string, object?>> linkRows = [];
        int lineCount = work.Sum(w => w.Links.Count);
        var headerDigest = new Dictionary<string, object?>
        {
            ["sales_invoice_id"] = invoiceId, ["invoice_kind"] = "INVOICE", ["customer_partner_id"] = customer, ["invoice_date"] = req.InvoiceDate, ["due_date"] = req.DueDate, ["payment_terms_id"] = terms,
            ["order_currency"] = currency, ["credits_invoice_id"] = null, ["credit_kind"] = null, ["reversal_of_sales_invoice_id"] = null, ["reason_code"] = null, ["invoice_mode"] = mode, ["line_count"] = lineCount
        };
        byte[]? DigestOf(JournalDocumentPoster.EmbeddedFacts facts)
        {
            linkRows = [];
            foreach (Work w in work)
            {
                TaxLineSeed? tax = facts.Tax.Lines.FirstOrDefault(t => t.Bases.Any(b => b.Index == w.Index));
                Guid? code = tax?.CodeId ?? (facts.Tax.BaseCodes.TryGetValue(w.Index, out Guid c) ? c : null);
                foreach (Link k in w.Links)
                    linkRows.Add(new Dictionary<string, object?>
                    {
                        ["line_no"] = linkRows.Count + 1, ["sales_order_id"] = w.Input.SalesOrderId, ["sales_order_line_no"] = w.Input.SalesOrderLineNo, ["delivery_id"] = k.DeliveryId, ["delivery_line_no"] = k.DeliveryLineNo,
                        ["dropship_confirmation_id"] = null, ["quantity"] = k.Quantity, ["unit_code"] = w.UnitCode, ["base_quantity"] = k.BaseQuantity, ["unit_price"] = w.UnitPrice, ["discount_pct"] = w.DiscountPct,
                        ["net_amount"] = k.Net, ["order_unit_price"] = mode == "APP_SUPPLIES" ? w.Line.UnitPrice : null, ["order_discount_pct"] = mode == "APP_SUPPLIES" ? w.Line.DiscountPct : null,
                        ["tax_code_id"] = code, ["tax_code_version_id"] = tax?.VersionId, ["tax_date"] = tax?.TaxDate, ["journal_line_no"] = w.Index + 1, ["line_ref"] = w.Input.LineRef
                    });
            }
            return InvoiceRules.Digest(headerDigest, linkRows.Select(r => (IReadOnlyDictionary<string, object?>)r.Where(kv => kv.Key != "line_ref").ToDictionary(kv => kv.Key, kv => kv.Value)).ToList());
        }

        Guid logId = Guid.Empty;
        decimal taxTotal = 0m, gross = 0m;
        async Task AfterAsync(TenantTransaction t, PostedDocument posted, JournalDocumentPoster.EmbeddedFacts facts)
        {
            taxTotal = facts.Tax.Lines.Sum(x => x.TaxTc);
            gross = facts.Request.Lines.Single(x => x.LineRef == ArRef).AmountTc;
            await using (var cmd = t.Command(
                "insert into core.sales_invoice (tenant_id, company_id, sales_invoice_id, invoice_kind, customer_partner_id, invoice_date, due_date, payment_terms_id, order_currency, journal_document_id, invoice_mode, " +
                "source_system, source_document_ref, created_by, correlation_id, idempotency_key, ic_transaction_id) values (@t, @c, @id, 'INVOICE', @cust, @d, @due, @terms, @cur, @j, @mode, @src, @ref, @by, @corr, @key, @icx)"))
            {
                cmd.Parameters.Add(new NpgsqlParameter("icx", NpgsqlDbType.Uuid) { Value = (object?)icId ?? DBNull.Value });
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("id", invoiceId); cmd.Parameters.AddWithValue("cust", customer);
                cmd.Parameters.AddWithValue("d", req.InvoiceDate); cmd.Parameters.AddWithValue("cur", currency); cmd.Parameters.AddWithValue("j", posted.DocumentId); cmd.Parameters.AddWithValue("mode", mode);
                cmd.Parameters.AddWithValue("src", ctx.SourceSystem); cmd.Parameters.AddWithValue("by", ctx.Actor); cmd.Parameters.AddWithValue("corr", ctx.CorrelationId); cmd.Parameters.AddWithValue("key", ctx.IdempotencyKey);
                cmd.Parameters.Add(new NpgsqlParameter("due", NpgsqlDbType.Date) { Value = (object?)req.DueDate ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("terms", NpgsqlDbType.Uuid) { Value = (object?)terms ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("ref", NpgsqlDbType.Text) { Value = (object?)req.SourceDocumentRef ?? DBNull.Value });
                await cmd.ExecuteNonQueryAsync(ct);
            }
            foreach (var row in linkRows)
            {
                await using var cmd = t.Command(
                    "insert into core.sales_invoice_line (tenant_id, company_id, sales_invoice_id, line_no, sales_order_id, sales_order_line_no, delivery_id, delivery_line_no, quantity, unit_code, base_quantity, unit_price, " +
                    "discount_pct, net_amount, order_unit_price, order_discount_pct, tax_code_id, tax_code_version_id, tax_date, journal_line_no) values (@t, @c, @id, @n, @o, @ol, @d, @dl, @q, @u, @bq, @up, @dp, @net, @oup, @odp, " +
                    "@tc, @tv, @td, @jl)");
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("id", invoiceId); cmd.Parameters.AddWithValue("n", row["line_no"]!);
                cmd.Parameters.AddWithValue("o", row["sales_order_id"]!); cmd.Parameters.AddWithValue("ol", row["sales_order_line_no"]!); cmd.Parameters.AddWithValue("q", row["quantity"]!);
                cmd.Parameters.AddWithValue("u", row["unit_code"]!); cmd.Parameters.AddWithValue("bq", row["base_quantity"]!); cmd.Parameters.AddWithValue("up", row["unit_price"]!);
                cmd.Parameters.AddWithValue("dp", row["discount_pct"]!); cmd.Parameters.AddWithValue("net", row["net_amount"]!); cmd.Parameters.AddWithValue("jl", row["journal_line_no"]!);
                cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Uuid) { Value = row["delivery_id"] ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("dl", NpgsqlDbType.Integer) { Value = row["delivery_line_no"] ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("oup", NpgsqlDbType.Numeric) { Value = row["order_unit_price"] ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("odp", NpgsqlDbType.Numeric) { Value = row["order_discount_pct"] ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("tc", NpgsqlDbType.Uuid) { Value = row["tax_code_id"] ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("tv", NpgsqlDbType.Uuid) { Value = row["tax_code_version_id"] ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("td", NpgsqlDbType.Date) { Value = row["tax_date"] ?? DBNull.Value });
                await cmd.ExecuteNonQueryAsync(ct);
            }
            if (owned) logId = await CommandSupport.WriteLogAsync(t, tenantId, companyId, ctx, CommandName, fingerprint, "SALES_INVOICE", invoiceId, ct);
            await CommandSupport.WriteOutboxAsync(t, tenantId, companyId, ctx, "invoice.posted", "SALES_INVOICE", $"{companyId:D}/{invoiceId:D}", "POSTED", DateTime.UtcNow, isAudit: true,
                JsonSerializer.Serialize(new Dictionary<string, object?> { ["chain_type"] = "LEDGER", ["row_hash"] = posted.RowHash, ["chain_seq"] = posted.ChainSeq }),
                JsonSerializer.Serialize(new Dictionary<string, object?> { ["document_number"] = posted.DocumentNumber, ["customer_partner_id"] = customer, ["lines"] = linkRows.Count, ["journal_document_id"] = posted.DocumentId }), ct);
        }

        var embedding = new JournalDocumentPoster.PostEmbedding(CommandName, "ORDER", ArRef, DigestOf, WriteLog: false, Commit: false, AfterAsync, IcTransactionId: icId, IcCounterpartCompanyId: icCounterpart);
        var posted = await _poster.PostCoreAsync(tx, tenantId, ctx, journal, fingerprint, embedding, ct);
        if (posted.Status != CommandStatus.Ok) return CommandResult<SalesInvoiceResult>.Rejected(posted.Errors.ToList());
        PostedDocument doc = posted.Value!;
        decimal netTotal = work.Sum(w => w.Net);
        var result = new SalesInvoiceResult(invoiceId, doc.DocumentId, doc.DocumentNumber, mode, netTotal, -taxTotal, gross, doc.RowHash,
            linkRows.Select(r => new SalesInvoiceLineResult((int)r["line_no"]!, (string)r["line_ref"]!, (Guid)r["sales_order_id"]!, (int)r["sales_order_line_no"]!, (Guid?)r["delivery_id"], (int?)r["delivery_line_no"],
                (decimal)r["base_quantity"]!, (decimal)r["net_amount"]!, (int)r["journal_line_no"]!, (Guid?)r["tax_code_id"])).ToList());
        if (owned) await tx.CommitAsync(ct);   // the deferred checks of the journal document, the outbox position
        return CommandResult<SalesInvoiceResult>.Ok(result, logId, posted.Warnings);
    }

    private static async Task<OrderLine?> ReadOrderLineAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid orderId, int lineNo, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select l.item_id, l.quantity, l.unit_code, l.base_quantity, l.unit_price, l.price_unit_quantity, l.discount_pct, l.tax_class_id, l.warehouse_id, l.origin_country, l.destination_country, " +
            "l.invoice_policy, l.over_tolerance_pct, l.fulfilment_type, l.line_status, i.item_group_id from core.sales_order_line l join core.item i on i.tenant_id = l.tenant_id and i.item_id = l.item_id " +
            "where l.tenant_id = @t and l.company_id = @c and l.sales_order_id = @o and l.line_no = @n");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("o", orderId); cmd.Parameters.AddWithValue("n", lineNo);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new OrderLine(orderId, lineNo, r.GetGuid(0), r.GetDecimal(1), r.GetString(2).Trim(), r.GetDecimal(3), r.GetDecimal(4), r.GetDecimal(5), r.GetDecimal(6), r.IsDBNull(7) ? null : r.GetGuid(7),
                r.IsDBNull(8) ? null : r.GetGuid(8), r.GetString(9).Trim(), r.GetString(10).Trim(), r.GetString(11), r.GetDecimal(12), r.GetString(13), r.GetString(14), r.GetGuid(15))
            : null;
    }

    private static async Task<decimal> SumAsync(TenantTransaction tx, string sql, Guid tenantId, Guid companyId, (Guid Order, int Line) key, CancellationToken ct)
    {
        await using var cmd = tx.Command(sql);
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("o", key.Order); cmd.Parameters.AddWithValue("n", key.Line);
        return (decimal)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>What is invoiced of an order line (11, 4.3): the invoice rows less the rows of their reversals. Credit notes are not in it.</summary>
    private static Task<decimal> InvoicedAsync(TenantTransaction tx, Guid tenantId, Guid companyId, (Guid Order, int Line) key, CancellationToken ct) =>
        SumAsync(tx,
            "select coalesce(sum(l.base_quantity), 0) from core.sales_invoice_line l join core.sales_invoice h on h.tenant_id = l.tenant_id and h.company_id = l.company_id and h.sales_invoice_id = l.sales_invoice_id " +
            "left join core.sales_invoice o on o.tenant_id = h.tenant_id and o.company_id = h.company_id and o.sales_invoice_id = h.reversal_of_sales_invoice_id " +
            "where l.tenant_id = @t and l.company_id = @c and l.sales_order_id = @o and l.sales_order_line_no = @n and (h.invoice_kind = 'INVOICE' or (h.invoice_kind = 'REVERSAL' and o.invoice_kind = 'INVOICE'))",
            tenantId, companyId, key, ct);

    /// <summary>What the invoices already take of a delivery line (a reversal row gives it back).</summary>
    private static async Task<decimal> LinkedAsync(TenantTransaction tx, Guid tenantId, Guid companyId, DeliveryLineRef d, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select coalesce(sum(l.base_quantity), 0) from core.sales_invoice_line l join core.sales_invoice h on h.tenant_id = l.tenant_id and h.company_id = l.company_id and h.sales_invoice_id = l.sales_invoice_id " +
            "left join core.sales_invoice o on o.tenant_id = h.tenant_id and o.company_id = h.company_id and o.sales_invoice_id = h.reversal_of_sales_invoice_id " +
            "where l.tenant_id = @t and l.company_id = @c and l.delivery_id = @d and l.delivery_line_no = @n and (h.invoice_kind = 'INVOICE' or (h.invoice_kind = 'REVERSAL' and o.invoice_kind = 'INVOICE'))");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", d.DeliveryId); cmd.Parameters.AddWithValue("n", d.DeliveryLineNo);
        return (decimal)(await cmd.ExecuteScalarAsync(ct))!;
    }

    internal static async Task<SalesInvoiceResult> LoadAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid id, CancellationToken ct)
    {
        Guid journal; string mode;
        await using (var cmd = tx.Command("select journal_document_id, invoice_mode from core.sales_invoice where tenant_id = @t and company_id = @c and sales_invoice_id = @i"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            journal = r.GetGuid(0); mode = r.IsDBNull(1) ? "" : r.GetString(1).Trim();
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
            "select coalesce(sum(amount_tc) filter (where line_type = 'TAX'), 0), coalesce(sum(amount_tc) filter (where line_type = 'BASE' and amount_tc > 0), 0) from core.journal_line where tenant_id = @t and company_id = @c and document_id = @d"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", journal);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            tax = -r.GetDecimal(0); gross = r.GetDecimal(1);
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
        return new SalesInvoiceResult(id, journal, number, mode, lines.Sum(l => l.NetAmount), tax, gross, Canonical.FormatHex32(hash), lines);
    }
}
