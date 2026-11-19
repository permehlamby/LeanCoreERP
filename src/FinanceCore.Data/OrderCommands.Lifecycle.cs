using System.Globalization;
using System.Text.Json;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;
using FinanceCore.Core.Posting;
using Npgsql;
using NpgsqlTypes;
using static FinanceCore.Data.MasterSupport;

namespace FinanceCore.Data;

public sealed partial class OrderCommands
{
    private static NpgsqlDbType DbType(MasterKind k) => k switch
    {
        MasterKind.Bool => NpgsqlDbType.Boolean, MasterKind.Amount or MasterKind.Percent or MasterKind.Rate or MasterKind.Factor => NpgsqlDbType.Numeric, MasterKind.Id => NpgsqlDbType.Uuid,
        MasterKind.SmallInt => NpgsqlDbType.Smallint, MasterKind.Date => NpgsqlDbType.Date, _ => NpgsqlDbType.Text
    };

    private static string? CurrentText(MasterField f, JsonElement row)
    {
        if (!row.TryGetProperty(f.Name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        return f.Kind switch
        {
            MasterKind.Bool => v.GetBoolean() ? "true" : "false",
            MasterKind.Amount or MasterKind.Percent or MasterKind.Rate or MasterKind.Factor => Normalised(f.Kind, v.GetDecimal().ToString(CultureInfo.InvariantCulture)),
            MasterKind.Id => v.GetString()!.ToLowerInvariant(),
            _ => v.ToString()
        };
    }

    private static async Task UpdateAsync(TenantTransaction tx, string table, string where, IReadOnlyList<(string Name, NpgsqlDbType Type, object? Value)> sets, IReadOnlyList<(string, object)> keys, CancellationToken ct)
    {
        await using var cmd = tx.Command($"update core.{table} set " + string.Join(", ", sets.Select((s, i) => $"{s.Name} = @s{i}")) + $" where {where}");
        for (int i = 0; i < sets.Count; i++) cmd.Parameters.Add(new NpgsqlParameter($"s{i}", sets[i].Type) { Value = sets[i].Value ?? DBNull.Value });
        foreach (var (n, v) in keys) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static IReadOnlyList<(string, object)> LineKey(OrderKind kind, Guid tenantId, Guid companyId, Guid orderId, int lineNo) =>
        [("t", tenantId), ("c", companyId), ("o", orderId), ("n", lineNo)];

    private static string LineWhere(OrderKind kind) => $"tenant_id = @t and company_id = @c and {kind.IdColumn} = @o and line_no = @n";

    private static async Task<HeaderContext> BuildContextAsync(
        TenantTransaction tx, Guid tenantId, JournalDocumentPoster.CompanyRow company, Guid companyId, DateOnly orderDate, string currency, DateOnly? defaultDate, string? shipFrom, CancellationToken ct)
    {
        int scale;
        await using (var cmd = tx.Command("select minor_units from platform.ref_currency where currency_code = @c"))
        {
            cmd.Parameters.AddWithValue("c", currency);
            scale = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct) ?? 2, CultureInfo.InvariantCulture);
        }
        return new HeaderContext(companyId, company.Country ?? "", scale, await ReadSettingsAsync(tx, tenantId, companyId, orderDate, ct), defaultDate, shipFrom);
    }

    // ---------------------------------------------------------------------------------------------------------
    // :create
    // ---------------------------------------------------------------------------------------------------------
    private async Task<CommandResult<OrderResult>> CreateCoreAsync(
        TenantTransaction tx, OrderKind kind, Guid tenantId, CommandContext ctx, CreateOrderRequest req, Dictionary<string, Patch> header, List<List<Patch>> linePatches, byte[] fingerprint, CancellationToken ct)
    {
        Guid companyId = (Guid)header["company_id"].Value!, party = (Guid)header[kind.PartyColumn].Value!;
        DateOnly orderDate = (DateOnly)header["order_date"].Value!;
        string currency = (string)header["order_currency"].Value!;
        var company = await JournalDocumentPoster.ReadCompanyAsync(tx, tenantId, companyId, ct);
        if (company is null) return Reject(Error("CONFIG.INVALID_REFERENCE", "P2", "/fields/company_id", ("company_id", companyId)));

        Guid? defaultTerms = null; string? partnerStatus = null, restriction = null;
        await using (var cmd = tx.Command(
            "select r.default_payment_terms_id, p.status, p.restriction_status from core.partner_company_relation r join core.business_partner p on p.tenant_id = r.tenant_id and p.partner_id = r.partner_id " +
            $"where r.tenant_id = @t and r.partner_id = @p and r.company_id = @c and r.status = 'ACTIVE' and r.{(kind.Kind == "SALES" ? "is_customer" : "is_supplier")}"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("p", party); cmd.Parameters.AddWithValue("c", companyId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) { defaultTerms = r.IsDBNull(0) ? null : r.GetGuid(0); partnerStatus = r.GetString(1); restriction = r.GetString(2); }
        }
        var errors = new List<CommandError>();
        if (partnerStatus is null || partnerStatus != "ACTIVE")
            errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", "/fields/" + kind.PartyColumn, (kind.PartyColumn, party), ("reason", $"no active {kind.PartyRole} relation in the company")));
        else if (restriction == "RESTRICTED") errors.Add(Error("MASTER.PARTNER_RESTRICTED", "P2", "/fields/" + kind.PartyColumn, ("partner_id", party)));
        if (!await ExistsAsync(tx, "select 1 from core.tenant_currency where tenant_id = @t and currency_code = @x and active", tenantId, ("x", currency)))
            errors.Add(Error("CONFIG.CURRENCY_NOT_ENABLED", "P2", "/fields/order_currency", ("currency", currency)));
        if (header.TryGetValue("payment_terms_id", out Patch? terms) && terms.Value is Guid termsId
            && !await ExistsAsync(tx, "select 1 from core.payment_terms where tenant_id = @t and payment_terms_id = @x and active", tenantId, ("x", termsId)))
            errors.Add(Error("CONFIG.INVALID_REFERENCE", "P2", "/fields/payment_terms_id", ("payment_terms_id", termsId)));
        if (errors.Count > 0) return CommandResult<OrderResult>.Rejected(errors);

        DateOnly? defaultDate = header.TryGetValue(kind.Kind == "SALES" ? "requested_delivery_date" : "expected_receipt_date", out Patch? dd) ? dd.Value as DateOnly? : null;
        string? shipFrom = header.TryGetValue("ship_from_country", out Patch? sf) ? sf.Value as string : null;
        HeaderContext hc = await BuildContextAsync(tx, tenantId, company, companyId, orderDate, currency, defaultDate, shipFrom, ct);
        var lines = new List<NewLine>();
        for (int i = 0; i < req.Lines.Count; i++)
            if (await BuildLineAsync(tx, kind, tenantId, hc, req.Lines[i], linePatches[i], $"/lines/{i}", errors, ct) is { } built) lines.Add(built);
        if (errors.Count > 0) return CommandResult<OrderResult>.Rejected(errors);

        // the number: the series of the order type at the order date, in the fiscal year of that date (04)
        int fiscalYear;
        await using (var cmd = tx.Command("select fiscal_year from core.fiscal_year where tenant_id = @t and company_id = @c and start_date <= @d and end_date >= @d"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", orderDate);
            if (await cmd.ExecuteScalarAsync(ct) is not int year) return Reject(Error("SYSTEM.FISCAL_YEAR_MISSING", "P2", "/fields/order_date", ("order_date", orderDate)));
            fiscalYear = year;
        }
        Guid ledger = await ClearingPoster.ReadDefaultLedgerAsync(tx, tenantId, companyId, ct) ?? Guid.Empty;
        var series = await JournalDocumentPoster.ReadSeriesAsync(tx, tenantId, companyId, kind.DocumentType, orderDate, ledger, ct);
        if (series is null) return Reject(Error("SERIES.NO_ASSIGNMENT", "P2", "/fields/order_date", ("document_type", kind.DocumentType), ("posting_date", orderDate)));
        long? last = await ClearingPoster.LockCounterAsync(tx, tenantId, companyId, series.SeriesId, fiscalYear, ct);
        if (last is null) return Reject(Error("LIMIT.NUMBER_COUNTER_MISSING", "P7", "/fields/order_date", ("series_code", series.SeriesCode), ("fiscal_year", fiscalYear)));
        long sequenceNo = last.Value + 1;
        await using (var cmd = tx.Command("update core.number_range_counter set last_number = @n, updated_at = now() where tenant_id = @t and company_id = @c and series_id = @s and fiscal_year = @y"))
        {
            cmd.Parameters.AddWithValue("n", sequenceNo); cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId);
            cmd.Parameters.AddWithValue("s", series.SeriesId); cmd.Parameters.AddWithValue("y", fiscalYear);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        string number = PostingRules.FormatNumber(series.Prefix, series.IncludeFiscalYear, series.Separator, series.NumberLength, fiscalYear, sequenceNo);

        Guid orderId = Guid.NewGuid();
        DateTime at = Now();
        var sets = header.Values.Select(p => (p.Field.Name, DbType(p.Field.Kind), p.Value)).ToList();
        if (!header.ContainsKey("payment_terms_id") && defaultTerms is { } dt) sets.Add(("payment_terms_id", NpgsqlDbType.Uuid, dt));
        sets.AddRange([
            (kind.IdColumn, NpgsqlDbType.Uuid, orderId), ("order_number", NpgsqlDbType.Text, number), ("sequence_no", NpgsqlDbType.Bigint, sequenceNo), ("fiscal_year", NpgsqlDbType.Integer, fiscalYear),
            ("number_series_id", NpgsqlDbType.Uuid, series.SeriesId), ("source_system", NpgsqlDbType.Text, ctx.SourceSystem), ("placed_by", NpgsqlDbType.Text, ctx.Actor),
            ("correlation_id", NpgsqlDbType.Text, ctx.CorrelationId), ("idempotency_key", NpgsqlDbType.Text, ctx.IdempotencyKey), ("tenant_id", NpgsqlDbType.Uuid, tenantId)]);
        await using (var insert = tx.Command($"insert into core.{kind.HeaderTable} ({string.Join(", ", sets.Select(s => s.Item1))}) values ({string.Join(", ", sets.Select((s, i) => $"@h{i}"))})"))
        {
            for (int i = 0; i < sets.Count; i++) insert.Parameters.Add(new NpgsqlParameter($"h{i}", sets[i].Item2) { Value = sets[i].Item3 ?? DBNull.Value });
            await insert.ExecuteNonQueryAsync(ct);
        }
        var refs = new Dictionary<int, string?>();
        for (int i = 0; i < lines.Count; i++) { await InsertLineAsync(tx, kind, tenantId, companyId, orderId, i + 1, lines[i], ct); refs[i + 1] = lines[i].LineRef; }

        await AddEventAsync(tx, kind, tenantId, companyId, orderId, "PLACED", null, ctx, null, at, ct);
        Guid logId = await CommandSupport.WriteLogAsync(tx, tenantId, companyId, ctx, kind.Resource + ":create", fingerprint, kind.Kind + "_ORDER", orderId, ct);
        await OutboxAsync(tx, kind, tenantId, companyId, orderId, ctx, "order.created", "CREATED", at,
            new() { ["order_number"] = number, ["partner_id"] = party, ["currency"] = currency, ["lines"] = lines.Count }, ct);
        OrderResult result = await ResultAsync(tx, kind, tenantId, orderId, [], refs, ct);
        await tx.CommitAsync(ct);
        return CommandResult<OrderResult>.Ok(result, logId);
    }

    // ---------------------------------------------------------------------------------------------------------
    // the prelude of every change: the order row locked, the version, the status
    // ---------------------------------------------------------------------------------------------------------
    private static async Task<(Header? Header, CommandError? Error)> OpenForChangeAsync(
        TenantTransaction tx, OrderKind kind, Guid tenantId, Guid id, long expectedVersion, CancellationToken ct)
    {
        Header? h = await ReadHeaderAsync(tx, kind, tenantId, id, true, ct);
        if (h is null) return (null, Error("CONFIG.INVALID_REFERENCE", "P2", "/id", ("id", id)));
        if (h.RowVersion != expectedVersion)
            return (null, Error("CONFIG.ROW_VERSION_MISMATCH", "P2", "/expected_version", ("current_version", h.RowVersion), ("expected_version", expectedVersion)));
        if (h.Status != "OPEN") return (null, Error("ORDER.ORDER_NOT_OPEN", "P2", "/id", ("status", h.Status)));
        return (h, null);
    }

    private static List<CommandError> ShapeOfChange(CommandContext ctx, Guid id, long expectedVersion)
    {
        var errors = ValidateContext(ctx);
        if (id == Guid.Empty) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/id", ("reason", "required")));
        if (expectedVersion < 1) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/expected_version", ("reason", "required")));
        return errors;
    }

    private static CommandError? ReasonError(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? Error("SCHEMA.REQUIRED_FIELD_MISSING", "P1", "/reason_code")
        : reason.Length > 50 ? Error("SCHEMA.INVALID_VALUE", "P1", "/reason_code", ("reason", "at most 50 characters")) : null;

    private static async Task<JsonElement> HeaderJsonAsync(TenantTransaction tx, OrderKind kind, Guid tenantId, Guid id, CancellationToken ct)
    {
        await using var cmd = tx.Command($"select to_jsonb(h)::text from core.{kind.HeaderTable} h where tenant_id = @t and {kind.IdColumn} = @i");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("i", id);
        return JsonDocument.Parse((string)(await cmd.ExecuteScalarAsync(ct))!).RootElement.Clone();
    }

    /// <summary>After a line closed or was cancelled: an order with no open line is CLOSED (a line was closed) or CANCELLED (every line was cancelled), with its event (11, 4.7).</summary>
    private static async Task<string?> FinishIfNoOpenLinesAsync(
        TenantTransaction tx, OrderKind kind, Guid tenantId, Guid companyId, Guid orderId, CommandContext ctx, DateTime at, CancellationToken ct)
    {
        var lines = await ReadLinesAsync(tx, kind, tenantId, companyId, orderId, ct);
        if (lines.Values.Any(l => Str(l, "line_status") == "OPEN")) return null;
        bool closed = lines.Values.Any(l => Str(l, "line_status") == "CLOSED");
        await AddEventAsync(tx, kind, tenantId, companyId, orderId, closed ? "CLOSED" : "CANCELLED", null, ctx, null, at, ct);
        await OutboxAsync(tx, kind, tenantId, companyId, orderId, ctx, closed ? "order.changed" : "order.cancelled", closed ? "CLOSED" : "CANCELLED", at,
            new() { ["status"] = closed ? "CLOSED" : "CANCELLED" }, ct);
        return closed ? "CLOSED" : "CANCELLED";
    }

    // ---------------------------------------------------------------------------------------------------------
    // :change
    // ---------------------------------------------------------------------------------------------------------
    public async Task<CommandResult<OrderResult>> ChangeAsync(OrderKind kind, Guid tenantId, CommandContext ctx, ChangeOrderRequest req, CancellationToken ct = default)
    {
        if (Denied(ctx, kind.OrderPermission) is { } denied) return Reject(denied);
        var errors = ShapeOfChange(ctx, req.Id, req.ExpectedVersion);
        var headerPatches = Validate(req.Fields, kind.HeaderChange, "/fields", errors);
        var linePatches = new List<(LineChange Change, List<Patch> Patches)>();
        for (int i = 0; i < (req.Lines?.Count ?? 0); i++)
            linePatches.Add((req.Lines![i], Validate(req.Lines[i].Fields, kind.LineChange, $"/lines/{i}/fields", errors)));
        if (headerPatches.Count == 0 && linePatches.Count == 0 && errors.Count == 0) errors.Add(Error("SCHEMA.REQUIRED_FIELD_MISSING", "P1", "/fields"));
        foreach (Patch p in headerPatches.Concat(linePatches.SelectMany(l => l.Patches)))
            if (p.Value is DateOnly d && !OrderKind.DateInRange(d)) errors.Add(Error("ORDER.DATE_INVALID", "P1", "/fields/" + p.Field.Name));
        if (errors.Count > 0) return CommandResult<OrderResult>.Rejected(errors);
        byte[] fingerprint = OrderRules.Fingerprint(kind.Resource + ":change", req);

        return await RunAsync(async () =>
        {
            await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Order, ct: ct);
            if (await ReplayAsync(tx, kind, tenantId, ctx, fingerprint, ct) is { } replay) return replay;
            var (h, error) = await OpenForChangeAsync(tx, kind, tenantId, req.Id, req.ExpectedVersion, ct);
            if (h is null) return Reject(error!);
            var problems = new List<CommandError>();
            JsonElement current = await HeaderJsonAsync(tx, kind, tenantId, req.Id, ct);

            var headerChanges = headerPatches.Where(p => Normalised(p.Field.Kind, p.Text) != CurrentText(p.Field, current)).ToList();
            if (headerChanges.FirstOrDefault(p => p.Field.Name == "payment_terms_id") is { Value: Guid terms } && !await ExistsAsync(tx, "select 1 from core.payment_terms where tenant_id = @t and payment_terms_id = @x and active", tenantId, ("x", terms)))
                problems.Add(Error("CONFIG.INVALID_REFERENCE", "P2", "/fields/payment_terms_id", ("payment_terms_id", terms)));
            if (headerChanges.FirstOrDefault(p => p.Field.Name == "ship_from_country") is { Value: string country } && !await ExistsAsync(tx, "select 1 from platform.ref_country where country_code = @x", tenantId, ("x", country)))
                problems.Add(Error("CONFIG.INVALID_REFERENCE", "P2", "/fields/ship_from_country", ("country_code", country)));

            var existing = await ReadLinesAsync(tx, kind, tenantId, h.CompanyId, req.Id, ct);
            var lineWork = new List<(int LineNo, List<(string Name, NpgsqlDbType Type, object? Value)> Sets, List<(MasterField Field, string? Old, string? New)> Logs)>();
            for (int i = 0; i < linePatches.Count; i++)
            {
                var (change, patches) = linePatches[i];
                if (!existing.TryGetValue(change.LineNo, out JsonElement line)) { problems.Add(Error("CONFIG.INVALID_REFERENCE", "P2", $"/lines/{i}/line_no", ("line_no", change.LineNo))); continue; }
                if (Str(line, "line_status") != "OPEN") { problems.Add(Error("ORDER.LINE_NOT_OPEN", "P2", $"/lines/{i}/line_no", ("line_no", change.LineNo), ("status", Str(line, "line_status")))); continue; }
                var changed = patches.Where(p => Normalised(p.Field.Kind, p.Text) != CurrentText(p.Field, line)).ToList();
                var sets = changed.Select(p => (p.Field.Name, DbType(p.Field.Kind), p.Value)).ToList();
                var logs = changed.Select(p => (p.Field, CurrentText(p.Field, line), Normalised(p.Field.Kind, p.Text))).ToList();

                Patch? Of(string n) => changed.FirstOrDefault(p => p.Field.Name == n);
                decimal quantity = Of("quantity")?.Value as decimal? ?? Num(line, "quantity"), price = Of("unit_price")?.Value as decimal? ?? Num(line, "unit_price");
                decimal discount = Of("discount_pct")?.Value as decimal? ?? Num(line, "discount_pct");
                if (Of("quantity") is not null)
                {
                    if (Math.Round(quantity, 6) != quantity) problems.Add(Error("SCHEMA.INVALID_VALUE", "P1", $"/lines/{i}/fields/quantity", ("reason", "at most 6 fraction digits")));
                    ItemInfo? item = await ReadItemAsync(tx, tenantId, h.CompanyId, Guid.Parse(Str(line, "item_id")!), ct);
                    decimal? factor = item is null ? null : await FactorAsync(tx, tenantId, item.ItemId, Str(line, "unit_code")!, item.BaseUnit, ct);
                    if (item is null || factor is null) problems.Add(Error("INVENTORY.UNIT_CONVERSION_MISSING", "P2", $"/lines/{i}/fields/quantity", ("unit_code", Str(line, "unit_code"))));
                    else if (Math.Round(quantity * factor.Value, item.Scale) != quantity * factor.Value)
                        problems.Add(Error("INVENTORY.QUANTITY_SCALE_EXCEEDED", "P1", $"/lines/{i}/fields/quantity", ("base_quantity", quantity * factor.Value), ("quantity_scale", item.Scale)));
                    else
                    {
                        sets.Add(("base_quantity", NpgsqlDbType.Numeric, quantity * factor.Value));
                        logs.Add((new MasterField("base_quantity", MasterKind.Factor), Normalised(MasterKind.Factor, Num(line, "base_quantity").ToString(CultureInfo.InvariantCulture)), Normalised(MasterKind.Factor, (quantity * factor.Value).ToString(CultureInfo.InvariantCulture))));
                    }
                }
                if (Of("quantity") is not null || Of("unit_price") is not null || Of("discount_pct") is not null)
                {
                    decimal net = OrderKind.NetAmount(quantity, price, Num(line, "price_unit_quantity"), discount, (await BuildContextAsync(tx, tenantId, (await JournalDocumentPoster.ReadCompanyAsync(tx, tenantId, h.CompanyId, ct))!, h.CompanyId, h.OrderDate, h.Currency, null, null, ct)).CurrencyScale);
                    if (net != Num(line, "net_amount"))
                    {
                        sets.Add(("net_amount", NpgsqlDbType.Numeric, net));
                        logs.Add((new MasterField("net_amount", MasterKind.Amount), Normalised(MasterKind.Amount, Num(line, "net_amount").ToString(CultureInfo.InvariantCulture)), Normalised(MasterKind.Amount, net.ToString(CultureInfo.InvariantCulture))));
                    }
                }
                if (Of("warehouse_id") is { } wh)
                {
                    if (wh.Value is null && (Str(line, "fulfilment_type") == "STOCK" || (kind.Kind == "PURCHASE")))
                        problems.Add(Error("SCHEMA.REQUIRED_FIELD_MISSING", "P2", $"/lines/{i}/fields/warehouse_id", ("reason", "a stocked line has a warehouse")));
                    else if (wh.Value is Guid w && await WarehouseCountryAsync(tx, tenantId, h.CompanyId, w, ct) is null)
                        problems.Add(Error("CONFIG.INVALID_REFERENCE", "P2", $"/lines/{i}/fields/warehouse_id", ("warehouse_id", w)));
                }
                foreach (string c in new[] { "origin_country", "destination_country" })
                    if (Of(c)?.Value is string code && !await ExistsAsync(tx, "select 1 from platform.ref_country where country_code = @x", tenantId, ("x", code)))
                        problems.Add(Error("CONFIG.INVALID_REFERENCE", "P2", $"/lines/{i}/fields/{c}", (c, code)));
                if (sets.Count > 0) lineWork.Add((change.LineNo, sets, logs));
            }
            if (problems.Count > 0) return CommandResult<OrderResult>.Rejected(problems);
            if (headerChanges.Count == 0 && lineWork.Count == 0) return Reject(Error("SCHEMA.NO_EFFECTIVE_CHANGE", "P6", "/fields"));

            DateTime at = Now();
            Guid changeSet = Guid.NewGuid();
            var changedNames = new List<string>();
            if (headerChanges.Count > 0)
            {
                await UpdateAsync(tx, kind.HeaderTable, $"tenant_id = @t and {kind.IdColumn} = @o", headerChanges.Select(p => (p.Field.Name, DbType(p.Field.Kind), p.Value)).ToList(), [("t", tenantId), ("o", req.Id)], ct);
                foreach (Patch p in headerChanges)
                {
                    await LogChangeAsync(tx, kind, tenantId, h.CompanyId, req.Id, changeSet, null, p.Field, CurrentText(p.Field, current), Normalised(p.Field.Kind, p.Text), null, ctx, at, ct);
                    changedNames.Add(p.Field.Name);
                }
            }
            foreach (var (lineNo, sets, logs) in lineWork)
            {
                await UpdateAsync(tx, kind.LineTable, LineWhere(kind), sets, LineKey(kind, tenantId, h.CompanyId, req.Id, lineNo), ct);
                foreach (var (field, old, @new) in logs)
                {
                    await LogChangeAsync(tx, kind, tenantId, h.CompanyId, req.Id, changeSet, lineNo, field, old, @new, null, ctx, at, ct);
                    changedNames.Add($"lines/{lineNo}/{field.Name}");
                }
            }
            await BumpAsync(tx, kind, tenantId, req.Id, null, ct);
            await AddEventAsync(tx, kind, tenantId, h.CompanyId, req.Id, "CHANGED", null, ctx, null, at, ct);
            Guid logId = await CommandSupport.WriteLogAsync(tx, tenantId, h.CompanyId, ctx, kind.Resource + ":change", fingerprint, kind.Kind + "_ORDER", req.Id, ct);
            await OutboxAsync(tx, kind, tenantId, h.CompanyId, req.Id, ctx, "order.changed", "CHANGED", at, new() { ["status"] = "CHANGED", ["fields"] = changedNames }, ct);
            OrderResult result = await ResultAsync(tx, kind, tenantId, req.Id, changedNames, null, ct);
            await tx.CommitAsync(ct);
            return CommandResult<OrderResult>.Ok(result, logId);
        });
    }

    // ---------------------------------------------------------------------------------------------------------
    // :add-line
    // ---------------------------------------------------------------------------------------------------------
    public async Task<CommandResult<OrderResult>> AddLineAsync(OrderKind kind, Guid tenantId, CommandContext ctx, AddOrderLineRequest req, CancellationToken ct = default)
    {
        if (Denied(ctx, kind.OrderPermission) is { } denied) return Reject(denied);
        var errors = ShapeOfChange(ctx, req.Id, req.ExpectedVersion);
        Require(req.Line, "/line", errors, "item_id", "quantity", "unit_code", "unit_price");
        var patches = Validate(req.Line, kind.LineCreate, "/line", errors);
        foreach (Patch p in patches)
            if (p.Value is DateOnly d && !OrderKind.DateInRange(d)) errors.Add(Error("ORDER.DATE_INVALID", "P1", "/line/" + p.Field.Name));
        if (errors.Count > 0) return CommandResult<OrderResult>.Rejected(errors);
        byte[] fingerprint = OrderRules.Fingerprint(kind.Resource + ":add-line", req);

        return await RunAsync(async () =>
        {
            await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Order, ct: ct);
            if (await ReplayAsync(tx, kind, tenantId, ctx, fingerprint, ct) is { } replay) return replay;
            var (h, error) = await OpenForChangeAsync(tx, kind, tenantId, req.Id, req.ExpectedVersion, ct);
            if (h is null) return Reject(error!);
            var company = (await JournalDocumentPoster.ReadCompanyAsync(tx, tenantId, h.CompanyId, ct))!;
            JsonElement current = await HeaderJsonAsync(tx, kind, tenantId, req.Id, ct);
            DateOnly? defaultDate = DateOnly.TryParse(Str(current, kind.Kind == "SALES" ? "requested_delivery_date" : "expected_receipt_date"), CultureInfo.InvariantCulture, out DateOnly dd) ? dd : null;
            HeaderContext hc = await BuildContextAsync(tx, tenantId, company, h.CompanyId, h.OrderDate, h.Currency, defaultDate, Str(current, "ship_from_country"), ct);
            var problems = new List<CommandError>();
            NewLine? line = await BuildLineAsync(tx, kind, tenantId, hc, req.Line, patches, "/line", problems, ct);
            if (line is null || problems.Count > 0) return CommandResult<OrderResult>.Rejected(problems);

            int lineNo;
            await using (var cmd = tx.Command($"select coalesce(max(line_no), 0) + 1 from core.{kind.LineTable} where tenant_id = @t and company_id = @c and {kind.IdColumn} = @o"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", h.CompanyId); cmd.Parameters.AddWithValue("o", req.Id);
                lineNo = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            }
            DateTime at = Now();
            await InsertLineAsync(tx, kind, tenantId, h.CompanyId, req.Id, lineNo, line, ct);
            await BumpAsync(tx, kind, tenantId, req.Id, null, ct);
            await AddEventAsync(tx, kind, tenantId, h.CompanyId, req.Id, "LINE_ADDED", lineNo, ctx, null, at, ct);
            Guid logId = await CommandSupport.WriteLogAsync(tx, tenantId, h.CompanyId, ctx, kind.Resource + ":add-line", fingerprint, kind.Kind + "_ORDER", req.Id, ct);
            await OutboxAsync(tx, kind, tenantId, h.CompanyId, req.Id, ctx, "order.changed", "LINE_ADDED", at, new() { ["status"] = "LINE_ADDED", ["line_no"] = lineNo }, ct);
            OrderResult result = await ResultAsync(tx, kind, tenantId, req.Id, [$"lines/{lineNo}"], new Dictionary<int, string?> { [lineNo] = line.LineRef }, ct);
            await tx.CommitAsync(ct);
            return CommandResult<OrderResult>.Ok(result, logId);
        });
    }

    // ---------------------------------------------------------------------------------------------------------
    // :close-line and :cancel
    // ---------------------------------------------------------------------------------------------------------
    public Task<CommandResult<OrderResult>> CloseLineAsync(OrderKind kind, Guid tenantId, CommandContext ctx, CloseOrderLineRequest req, CancellationToken ct = default) =>
        EndLinesAsync(kind, tenantId, ctx, req.Id, req.ExpectedVersion, req.LineNo, req.ReasonCode, "CLOSED", kind.Resource + ":close-line", OrderRules.Fingerprint(kind.Resource + ":close-line", req), ct);

    public Task<CommandResult<OrderResult>> CancelAsync(OrderKind kind, Guid tenantId, CommandContext ctx, CancelOrderRequest req, CancellationToken ct = default) =>
        EndLinesAsync(kind, tenantId, ctx, req.Id, req.ExpectedVersion, req.LineNo, req.ReasonCode, "CANCELLED", kind.Resource + ":cancel", OrderRules.Fingerprint(kind.Resource + ":cancel", req), ct);

    /// <summary>Closes (short-close) or cancels one line, or cancels the whole order when no line is named. Only the undelivered and uninvoiced part can be cancelled (11, 4.4): with no
    /// delivery or invoice yet, that is every line.</summary>
    private async Task<CommandResult<OrderResult>> EndLinesAsync(
        OrderKind kind, Guid tenantId, CommandContext ctx, Guid id, long expectedVersion, int? lineNo, string reason, string target, string command, byte[] fingerprint, CancellationToken ct)
    {
        if (Denied(ctx, kind.OrderPermission) is { } denied) return Reject(denied);
        var errors = ShapeOfChange(ctx, id, expectedVersion);
        if (ReasonError(reason) is { } re) errors.Add(re);
        if (target == "CLOSED" && lineNo is null) errors.Add(Error("SCHEMA.REQUIRED_FIELD_MISSING", "P1", "/line_no"));
        if (errors.Count > 0) return CommandResult<OrderResult>.Rejected(errors);

        return await RunAsync(async () =>
        {
            await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Order, ct: ct);
            if (await ReplayAsync(tx, kind, tenantId, ctx, fingerprint, ct) is { } replay) return replay;
            var (h, error) = await OpenForChangeAsync(tx, kind, tenantId, id, expectedVersion, ct);
            if (h is null) return Reject(error!);
            var lines = await ReadLinesAsync(tx, kind, tenantId, h.CompanyId, id, ct);
            var affected = new List<int>();
            if (lineNo is { } n)
            {
                if (!lines.TryGetValue(n, out JsonElement line)) return Reject(Error("CONFIG.INVALID_REFERENCE", "P2", "/line_no", ("line_no", n)));
                if (Str(line, "line_status") != "OPEN") return Reject(Error("ORDER.LINE_NOT_OPEN", "P2", "/line_no", ("line_no", n), ("status", Str(line, "line_status"))));
                affected.Add(n);
            }
            else affected.AddRange(lines.Where(l => Str(l.Value, "line_status") == "OPEN").Select(l => l.Key));

            DateTime at = Now();
            Guid changeSet = Guid.NewGuid();
            var field = new MasterField("line_status", MasterKind.Code, 50);
            foreach (int endedLine in affected)
            {
                await UpdateAsync(tx, kind.LineTable, LineWhere(kind), [("line_status", NpgsqlDbType.Text, target)], LineKey(kind, tenantId, h.CompanyId, id, endedLine), ct);
                await LogChangeAsync(tx, kind, tenantId, h.CompanyId, id, changeSet, endedLine, field, "OPEN", target, reason, ctx, at, ct);
            }
            if (lineNo is null)
            {
                await BumpAsync(tx, kind, tenantId, id, "CANCELLED", ct);
                await AddEventAsync(tx, kind, tenantId, h.CompanyId, id, "CANCELLED", null, ctx, reason, at, ct);
                await OutboxAsync(tx, kind, tenantId, h.CompanyId, id, ctx, "order.cancelled", "CANCELLED", at, new() { ["status"] = "CANCELLED", ["reason_code"] = reason }, ct);
            }
            else
            {
                bool closing = target == "CLOSED";
                await AddEventAsync(tx, kind, tenantId, h.CompanyId, id, closing ? "LINE_CLOSED" : "LINE_CANCELLED", lineNo, ctx, reason, at, ct);
                await OutboxAsync(tx, kind, tenantId, h.CompanyId, id, ctx, closing ? "order.line_closed" : "order.changed", closing ? "LINE_CLOSED" : "LINE_CANCELLED", at,
                    new() { ["status"] = closing ? "LINE_CLOSED" : "LINE_CANCELLED", ["line_no"] = lineNo, ["reason_code"] = reason }, ct);
                string? finished = await FinishIfNoOpenLinesAsync(tx, kind, tenantId, h.CompanyId, id, ctx, at, ct);
                await BumpAsync(tx, kind, tenantId, id, finished, ct);   // one version for the command, with the status when the last line ended
            }
            Guid logId = await CommandSupport.WriteLogAsync(tx, tenantId, h.CompanyId, ctx, command, fingerprint, kind.Kind + "_ORDER", id, ct);
            OrderResult result = await ResultAsync(tx, kind, tenantId, id, affected.Select(n => $"lines/{n}/line_status").ToList(), null, ct);
            await tx.CommitAsync(ct);
            return CommandResult<OrderResult>.Ok(result, logId);
        });
    }

    // ---------------------------------------------------------------------------------------------------------
    // :change-line-dates
    // ---------------------------------------------------------------------------------------------------------
    public async Task<CommandResult<OrderResult>> ChangeLineDatesAsync(OrderKind kind, Guid tenantId, CommandContext ctx, ChangeLineDatesRequest req, CancellationToken ct = default)
    {
        if (Denied(ctx, kind.OrderPermission) is { } denied) return Reject(denied);
        var errors = ShapeOfChange(ctx, req.Id, req.ExpectedVersion);
        if (req.Lines is null or { Count: 0 }) errors.Add(Error("SCHEMA.REQUIRED_FIELD_MISSING", "P1", "/lines"));
        var work = new List<(LineChange Change, List<Patch> Patches)>();
        for (int i = 0; i < (req.Lines?.Count ?? 0); i++)
        {
            var patches = Validate(req.Lines![i].Fields, kind.LineDates, $"/lines/{i}/fields", errors);
            if (patches.Count == 0 && errors.Count == 0) errors.Add(Error("SCHEMA.REQUIRED_FIELD_MISSING", "P1", $"/lines/{i}/fields"));
            foreach (Patch p in patches)
            {
                if (p.Value is DateOnly d && !OrderKind.DateInRange(d)) errors.Add(Error("ORDER.DATE_INVALID", "P1", $"/lines/{i}/fields/{p.Field.Name}"));
                if (p.Field.Name == "confirmed_date" && Denied(ctx, OrderKind.PromisePermission) is { } promise) return Reject(promise);
            }
            work.Add((req.Lines[i], patches));
        }
        if (errors.Count > 0) return CommandResult<OrderResult>.Rejected(errors);
        byte[] fingerprint = OrderRules.Fingerprint(kind.Resource + ":change-line-dates", req);

        return await RunAsync(async () =>
        {
            await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Order, ct: ct);
            if (await ReplayAsync(tx, kind, tenantId, ctx, fingerprint, ct) is { } replay) return replay;
            var (h, error) = await OpenForChangeAsync(tx, kind, tenantId, req.Id, req.ExpectedVersion, ct);
            if (h is null) return Reject(error!);
            var existing = await ReadLinesAsync(tx, kind, tenantId, h.CompanyId, req.Id, ct);
            var problems = new List<CommandError>();
            var changes = new List<(int LineNo, Patch Patch, string? Old)>();
            for (int i = 0; i < work.Count; i++)
            {
                if (!existing.TryGetValue(work[i].Change.LineNo, out JsonElement line)) { problems.Add(Error("CONFIG.INVALID_REFERENCE", "P2", $"/lines/{i}/line_no", ("line_no", work[i].Change.LineNo))); continue; }
                if (Str(line, "line_status") != "OPEN") { problems.Add(Error("ORDER.LINE_NOT_OPEN", "P2", $"/lines/{i}/line_no", ("line_no", work[i].Change.LineNo), ("status", Str(line, "line_status")))); continue; }
                foreach (Patch p in work[i].Patches)
                    if (Normalised(p.Field.Kind, p.Text) != CurrentText(p.Field, line)) changes.Add((work[i].Change.LineNo, p, CurrentText(p.Field, line)));
            }
            if (problems.Count > 0) return CommandResult<OrderResult>.Rejected(problems);
            if (changes.Count == 0) return Reject(Error("SCHEMA.NO_EFFECTIVE_CHANGE", "P6", "/lines"));

            DateTime at = Now();
            Guid changeSet = Guid.NewGuid();
            foreach (var group in changes.GroupBy(c => c.LineNo))
            {
                await UpdateAsync(tx, kind.LineTable, LineWhere(kind), group.Select(c => (c.Patch.Field.Name, NpgsqlDbType.Date, c.Patch.Value)).ToList(), LineKey(kind, tenantId, h.CompanyId, req.Id, group.Key), ct);
                foreach (var c in group) await LogChangeAsync(tx, kind, tenantId, h.CompanyId, req.Id, changeSet, c.LineNo, c.Patch.Field, c.Old, c.Patch.Text, null, ctx, at, ct);
                await AddEventAsync(tx, kind, tenantId, h.CompanyId, req.Id, "DATE_CHANGED", group.Key, ctx, null, at, ct);
            }
            await BumpAsync(tx, kind, tenantId, req.Id, null, ct);
            Guid logId = await CommandSupport.WriteLogAsync(tx, tenantId, h.CompanyId, ctx, kind.Resource + ":change-line-dates", fingerprint, kind.Kind + "_ORDER", req.Id, ct);
            await OutboxAsync(tx, kind, tenantId, h.CompanyId, req.Id, ctx, "order.date_changed", "DATE_CHANGED", at,
                new() { ["lines"] = changes.Select(c => c.LineNo).Distinct().Order().ToArray() }, ct);
            OrderResult result = await ResultAsync(tx, kind, tenantId, req.Id, changes.Select(c => $"lines/{c.LineNo}/{c.Patch.Field.Name}").ToList(), null, ct);
            await tx.CommitAsync(ct);
            return CommandResult<OrderResult>.Ok(result, logId);
        });
    }
}
