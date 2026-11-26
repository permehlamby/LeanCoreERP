using System.Globalization;
using System.Text.Json;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;
using Npgsql;
using NpgsqlTypes;
using static FinanceCore.Data.MasterSupport;

namespace FinanceCore.Data;

/// <summary>
/// The sales and purchase orders (11, 4, 6.1, 6.2): `{sales,purchase}-orders:create`, `:change`, `:add-line`, `:close-line`, `:cancel` and `:change-line-dates`, one set of commands for
/// both kinds (OrderKind). An order is changed only by these commands: the order row is locked, expected_version is checked, the change is applied and written to order_change_log
/// and order_event, and the outbox event is written in the same transaction. First slice: no stock, finance or fulfilment is touched, so the rules that look at delivered, received
/// and invoiced quantities (the floor of a quantity, the lock of price, warehouse and tax geography, the cancel of a part) are vacuous and are not yet enforced. Not built: address
/// snapshots, reservations, dropship and intercompany links, migrated orders. Built choices are in 07, 13.2u.
/// </summary>
public sealed partial class OrderCommands(NpgsqlDataSource source)
{
    private static CommandResult<OrderResult> Reject(CommandError e) => CommandResult<OrderResult>.Rejected([e]);

    private static DateTime Now() { DateTime n = DateTime.UtcNow; return new DateTime(n.Ticks - n.Ticks % 10, DateTimeKind.Utc); }

    private async Task<CommandResult<OrderResult>> RunAsync(Func<Task<CommandResult<OrderResult>>> once)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await once();
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.TableName == "command_log" && attempt == 0)
            {
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                return CommandResult<OrderResult>.Failed(new CommandError("SYSTEM.LOCK_TIMEOUT", "P7", "", null, true));
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure)
            {
                return CommandResult<OrderResult>.Failed(new CommandError("SYSTEM.CONFLICT", "P7", "", null, true));
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // shared reads
    // ---------------------------------------------------------------------------------------------------------
    private sealed record Header(Guid CompanyId, long RowVersion, string Status, string Currency, Guid Party, string Number, DateOnly OrderDate);

    private static async Task<Header?> ReadHeaderAsync(TenantTransaction tx, OrderKind kind, Guid tenantId, Guid id, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            $"select company_id, row_version, lifecycle_status, order_currency, {kind.PartyColumn}, order_number, order_date from core.{kind.HeaderTable} where tenant_id = @t and {kind.IdColumn} = @i" +
            (forUpdate ? " for update" : ""));
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("i", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new Header(r.GetGuid(0), r.GetInt64(1), r.GetString(2), r.GetString(3).Trim(), r.GetGuid(4), r.GetString(5), r.GetFieldValue<DateOnly>(6)) : null;
    }

    private static async Task<Dictionary<int, JsonElement>> ReadLinesAsync(TenantTransaction tx, OrderKind kind, Guid tenantId, Guid companyId, Guid id, CancellationToken ct)
    {
        var lines = new Dictionary<int, JsonElement>();
        await using var cmd = tx.Command(
            $"select line_no, to_jsonb(l)::text from core.{kind.LineTable} l where tenant_id = @t and company_id = @c and {kind.IdColumn} = @i order by line_no");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) lines[r.GetInt32(0)] = JsonDocument.Parse(r.GetString(1)).RootElement.Clone();
        return lines;
    }

    private static decimal Num(JsonElement row, string name) => row.GetProperty(name).GetDecimal();

    private static string? Str(JsonElement row, string name) => row.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : null;

    private static async Task<OrderResult> ResultAsync(
        TenantTransaction tx, OrderKind kind, Guid tenantId, Guid id, IReadOnlyList<string> changed, IReadOnlyDictionary<int, string?>? lineRefs, CancellationToken ct)
    {
        Header h = (await ReadHeaderAsync(tx, kind, tenantId, id, false, ct))!;
        var lines = await ReadLinesAsync(tx, kind, tenantId, h.CompanyId, id, ct);
        var results = lines.Select(l => new OrderLineResult(l.Key, lineRefs?.GetValueOrDefault(l.Key), Guid.Parse(Str(l.Value, "item_id")!), Num(l.Value, "quantity"), Str(l.Value, "unit_code")!,
            Num(l.Value, "base_quantity"), Num(l.Value, "net_amount"), Str(l.Value, "line_status")!)).ToList();
        return new OrderResult(id, h.Number, h.RowVersion, h.Status, results.Where(l => l.LineStatus != "CANCELLED").Sum(l => l.NetAmount), results, changed);
    }

    /// <summary>The result of an order as the commands answer it (for the replay of an IC pair).</summary>
    internal static Task<OrderResult> ResultOfAsync(TenantTransaction tx, OrderKind kind, Guid tenantId, Guid id, CancellationToken ct) =>
        ResultAsync(tx, kind, tenantId, id, [], null, ct);

    private async Task<CommandResult<OrderResult>?> ReplayAsync(
        TenantTransaction tx, OrderKind kind, Guid tenantId, CommandContext ctx, byte[] fingerprint, CancellationToken ct)
    {
        CommandSupport.EarlierCommand? earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
        if (earlier is null) return null;
        if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint)) return Reject(Error("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
        return earlier.EntityId is null
            ? CommandResult<OrderResult>.Failed(Error("SYSTEM.REPLAY_RESULT_MISSING", "P1", ""))
            : CommandResult<OrderResult>.Ok(await ResultAsync(tx, kind, tenantId, earlier.EntityId.Value, [], null, ct), earlier.CommandLogId, replay: true);
    }

    // ---------------------------------------------------------------------------------------------------------
    // the data of the company that a line is checked against
    // ---------------------------------------------------------------------------------------------------------
    private sealed record OrderSettings(string MatchMode, decimal Over, decimal Under, bool AllowBeforeReceipt);

    private static async Task<OrderSettings> ReadSettingsAsync(TenantTransaction tx, Guid tenantId, Guid companyId, DateOnly date, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select match_mode_default, default_over_tolerance_pct, default_under_tolerance_pct, allow_invoice_before_receipt from core.company_order_setting_version " +
            "where tenant_id = @t and company_id = @c and effective_from <= @d order by effective_from desc limit 1");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", date);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new OrderSettings(r.GetString(0), r.GetDecimal(1), r.GetDecimal(2), r.GetBoolean(3)) : new OrderSettings("THREE_WAY", 0m, 0m, false);
    }

    private sealed record ItemInfo(Guid ItemId, string ItemType, string BaseUnit, int Scale, string Status, bool Active, Guid? TaxClass, bool Activated);

    private static async Task<ItemInfo?> ReadItemAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid itemId, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select i.item_type, i.base_unit, i.quantity_scale, i.status, i.active, i.tax_class_id, coalesce(c.active, false) from core.item i " +
            "left join core.item_company c on c.tenant_id = i.tenant_id and c.company_id = @c and c.item_id = i.item_id where i.tenant_id = @t and i.item_id = @i");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", itemId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new ItemInfo(itemId, r.GetString(0), r.GetString(1).Trim(), r.GetInt16(2), r.GetString(3), r.GetBoolean(4), r.IsDBNull(5) ? null : r.GetGuid(5), r.GetBoolean(6)) : null;
    }

    /// <summary>The factor from a unit to the base unit of an item (10, 3.4): 1 for the base unit, then the item's own row, then the tenant's row or its inverse. No chaining.</summary>
    internal static async Task<decimal?> FactorAsync(TenantTransaction tx, Guid tenantId, Guid itemId, string unit, string baseUnit, CancellationToken ct)
    {
        if (unit == baseUnit) return 1m;
        await using (var cmd = tx.Command("select factor_to_base from core.item_unit_conversion where tenant_id = @t and item_id = @i and unit_code = @u and active"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("i", itemId); cmd.Parameters.AddWithValue("u", unit);
            if (await cmd.ExecuteScalarAsync(ct) is decimal f) return f;
        }
        await using (var cmd = tx.Command("select factor from core.unit_conversion where tenant_id = @t and from_unit = @u and to_unit = @b and active"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("u", unit); cmd.Parameters.AddWithValue("b", baseUnit);
            if (await cmd.ExecuteScalarAsync(ct) is decimal f) return f;
        }
        await using (var cmd = tx.Command("select factor from core.unit_conversion where tenant_id = @t and from_unit = @b and to_unit = @u and active"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("u", unit); cmd.Parameters.AddWithValue("b", baseUnit);
            if (await cmd.ExecuteScalarAsync(ct) is decimal f && f != 0m) return 1m / f;
        }
        return null;
    }

    private static async Task<string?> WarehouseCountryAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid warehouseId, CancellationToken ct)
    {
        await using var cmd = tx.Command("select country_code from core.warehouse where tenant_id = @t and company_id = @c and warehouse_id = @w and active");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("w", warehouseId);
        return (await cmd.ExecuteScalarAsync(ct) as string)?.Trim();
    }

    private sealed record NewLine(
        string? LineRef, Guid ItemId, string? Description, decimal Quantity, string UnitCode, decimal BaseQuantity, decimal UnitPrice, decimal PriceUnitQuantity, decimal Discount, decimal Net,
        Guid? TaxClass, Guid? Warehouse, string Origin, string Destination, decimal Over, decimal Under, DateOnly? RequestedDate, string? LineText, string Fulfilment, string? Policy, string? MatchMode);

    private sealed record HeaderContext(Guid CompanyId, string CompanyCountry, int CurrencyScale, OrderSettings Settings, DateOnly? DefaultDate, string? ShipFrom);

    /// <summary>Checks one line against the company's data and derives what the core computes (11, 6.2): base quantity, net amount, tax geography and the defaults of the line.</summary>
    private static async Task<NewLine?> BuildLineAsync(
        TenantTransaction tx, OrderKind kind, Guid tenantId, HeaderContext h, IReadOnlyDictionary<string, object?> fields, List<Patch> patches, string path, List<CommandError> errors, CancellationToken ct)
    {
        var f = patches.ToDictionary(p => p.Field.Name);
        T? V<T>(string name) where T : struct => f.TryGetValue(name, out Patch? p) && p.Value is T v ? v : null;
        string? S(string name) => f.TryGetValue(name, out Patch? p) ? p.Value as string : null;
        void Bad(string field, string code, string reason) => errors.Add(Error(code, "P2", $"{path}/{field}", ("reason", reason)));

        Guid itemId = V<Guid>("item_id") ?? Guid.Empty;
        decimal quantity = V<decimal>("quantity") ?? 0m, unitPrice = V<decimal>("unit_price") ?? 0m;
        string unit = S("unit_code") ?? "";
        if (fields.TryGetValue("confirmed_date", out object? confirmed) && confirmed is not null)
            errors.Add(Error("ORDER.CONFIRMED_DATE_ON_CREATE", "P1", $"{path}/confirmed_date"));
        decimal puq = V<decimal>("price_unit_quantity") ?? 1m, discount = V<decimal>("discount_pct") ?? 0m;
        if (Math.Round(quantity, 6) != quantity) Bad("quantity", "SCHEMA.INVALID_VALUE", "at most 6 fraction digits");
        if (Math.Round(puq, 6) != puq) Bad("price_unit_quantity", "SCHEMA.INVALID_VALUE", "at most 6 fraction digits");
        foreach (string d in new[] { "requested_date", "confirmed_date" })
            if (V<DateOnly>(d) is { } date && !OrderKind.DateInRange(date)) errors.Add(Error("ORDER.DATE_INVALID", "P1", $"{path}/{d}"));

        ItemInfo? item = await ReadItemAsync(tx, tenantId, h.CompanyId, itemId, ct);
        if (item is null) { Bad("item_id", "CONFIG.INVALID_REFERENCE", "no such item"); return null; }
        if (!item.Active || item.Status != "ACTIVE") Bad("item_id", "CONFIG.INVALID_REFERENCE", "the item is inactive or blocked");
        else if (!item.Activated) Bad("item_id", "CONFIG.INVALID_REFERENCE", "the item is not activated in the company (item_company)");
        decimal? factor = await FactorAsync(tx, tenantId, itemId, unit, item.BaseUnit, ct);
        if (factor is null) { errors.Add(Error("INVENTORY.UNIT_CONVERSION_MISSING", "P2", $"{path}/unit_code", ("item_id", itemId), ("unit_code", unit))); return null; }
        decimal baseQuantity = quantity * factor.Value;
        if (Math.Round(baseQuantity, item.Scale) != baseQuantity)
        {
            errors.Add(Error("INVENTORY.QUANTITY_SCALE_EXCEEDED", "P1", $"{path}/quantity", ("base_quantity", baseQuantity), ("quantity_scale", item.Scale)));
            return null;
        }

        Guid? warehouse = V<Guid>("warehouse_id");
        string? warehouseCountry = null;
        if (warehouse is { } w)
        {
            warehouseCountry = await WarehouseCountryAsync(tx, tenantId, h.CompanyId, w, ct);
            if (warehouseCountry is null) Bad("warehouse_id", "CONFIG.INVALID_REFERENCE", "no active warehouse of the company");
        }

        string fulfilment = "STOCK"; string? policy = null, matchMode = null;
        if (kind.Kind == "SALES")
        {
            fulfilment = S("fulfilment_type") ?? (item.ItemType == "GOODS" ? "STOCK" : "SERVICE");
            if (fulfilment == "DROPSHIP") { errors.Add(Error("LIMIT.ORDER_DROPSHIP_NOT_BUILT", "P2", $"{path}/fulfilment_type")); return null; }
            if (fulfilment == "STOCK" && item.ItemType != "GOODS") Bad("fulfilment_type", "SCHEMA.INVALID_VALUE", "a stock line is a goods item");
            if (fulfilment == "STOCK" && warehouse is null) Bad("warehouse_id", "SCHEMA.REQUIRED_FIELD_MISSING", "a stock line is delivered from a warehouse");
            policy = S("invoice_policy") ?? (fulfilment == "STOCK" ? "DELIVERED" : "ORDERED");
        }
        else
        {
            matchMode = S("match_mode") ?? h.Settings.MatchMode;
            if (item.ItemType == "GOODS" && warehouse is null) Bad("warehouse_id", "SCHEMA.REQUIRED_FIELD_MISSING", "a stocked item is received in a warehouse");
            if (item.ItemType == "GOODS" && matchMode == "TWO_WAY" && !h.Settings.AllowBeforeReceipt)
                Bad("match_mode", "SCHEMA.INVALID_VALUE", "a stock item is three-way matched unless the company allows invoicing before receipt");
        }

        // tax geography (11, 4.5): the origin is the warehouse country or, for a service, the company country (a purchase: the supplier's ship-from country); the destination
        // is the ship-to country, and there is no address snapshot yet, so it must be given (F-IMPL-49)
        string? origin = S("origin_country") ?? (kind.Kind == "SALES" ? (fulfilment == "STOCK" ? warehouseCountry : h.CompanyCountry) : h.ShipFrom);
        string? destination = S("destination_country") ?? (kind.Kind == "PURCHASE" ? (warehouseCountry ?? h.CompanyCountry) : null);
        if (origin is null) Bad("origin_country", "SCHEMA.REQUIRED_FIELD_MISSING", "the country the goods come from");
        if (destination is null) Bad("destination_country", "SCHEMA.REQUIRED_FIELD_MISSING", "the country the goods go to");
        foreach (var (field, code) in new[] { ("origin_country", origin), ("destination_country", destination) })
            if (code is not null && S(field) is not null && !await ExistsAsync(tx, "select 1 from platform.ref_country where country_code = @x", tenantId, ("x", code)))
                Bad(field, "CONFIG.INVALID_REFERENCE", "no such country");
        Guid? taxClass = V<Guid>("tax_class_id") ?? item.TaxClass;
        if (V<Guid>("tax_class_id") is { } tc && !await ExistsAsync(tx, "select 1 from core.tax_class where tenant_id = @t and tax_class_id = @x and active", tenantId, ("x", tc)))
            Bad("tax_class_id", "CONFIG.INVALID_REFERENCE", "no such tax class");

        decimal net = OrderKind.NetAmount(quantity, unitPrice, puq, discount, h.CurrencyScale);
        DateOnly? requested = V<DateOnly>("requested_date") ?? h.DefaultDate;
        if (errors.Count > 0 && errors.Any(e => e.Path.StartsWith(path + "/", StringComparison.Ordinal))) return null;
        return new NewLine(S("line_ref"), itemId, S("description"), quantity, unit, baseQuantity, unitPrice, puq, discount, net, taxClass, warehouse, origin!, destination!,
            V<decimal>("over_tolerance_pct") ?? h.Settings.Over, V<decimal>("under_tolerance_pct") ?? h.Settings.Under, requested, S("line_text"), fulfilment, policy, matchMode);
    }

    private static async Task InsertLineAsync(TenantTransaction tx, OrderKind kind, Guid tenantId, Guid companyId, Guid orderId, int lineNo, NewLine l, CancellationToken ct)
    {
        bool sales = kind.Kind == "SALES";
        await using var cmd = tx.Command(
            $"insert into core.{kind.LineTable} (tenant_id, company_id, {kind.IdColumn}, line_no, item_id, description, quantity, unit_code, base_quantity, unit_price, price_unit_quantity, discount_pct, " +
            "net_amount, tax_class_id, warehouse_id, origin_country, destination_country, over_tolerance_pct, under_tolerance_pct, requested_date, line_text" +
            (sales ? ", invoice_policy, fulfilment_type) " : ", match_mode) ") +
            "values (@t, @c, @o, @n, @i, @d, @q, @u, @bq, @p, @puq, @disc, @net, @tax, @w, @orig, @dest, @over, @under, @req, @text" + (sales ? ", @pol, @ful)" : ", @mm)"));
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("o", orderId); cmd.Parameters.AddWithValue("n", lineNo);
        cmd.Parameters.AddWithValue("i", l.ItemId); cmd.Parameters.AddWithValue("q", l.Quantity); cmd.Parameters.AddWithValue("u", l.UnitCode); cmd.Parameters.AddWithValue("bq", l.BaseQuantity);
        cmd.Parameters.AddWithValue("p", l.UnitPrice); cmd.Parameters.AddWithValue("puq", l.PriceUnitQuantity); cmd.Parameters.AddWithValue("disc", l.Discount); cmd.Parameters.AddWithValue("net", l.Net);
        cmd.Parameters.AddWithValue("orig", l.Origin); cmd.Parameters.AddWithValue("dest", l.Destination); cmd.Parameters.AddWithValue("over", l.Over); cmd.Parameters.AddWithValue("under", l.Under);
        cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Text) { Value = (object?)l.Description ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("tax", NpgsqlDbType.Uuid) { Value = (object?)l.TaxClass ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("w", NpgsqlDbType.Uuid) { Value = (object?)l.Warehouse ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("req", NpgsqlDbType.Date) { Value = (object?)l.RequestedDate ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("text", NpgsqlDbType.Text) { Value = (object?)l.LineText ?? DBNull.Value });
        if (sales) { cmd.Parameters.AddWithValue("pol", l.Policy!); cmd.Parameters.AddWithValue("ful", l.Fulfilment); }
        else cmd.Parameters.AddWithValue("mm", l.MatchMode!);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---------------------------------------------------------------------------------------------------------
    // log, event and outbox
    // ---------------------------------------------------------------------------------------------------------
    private static async Task<int> AddEventAsync(
        TenantTransaction tx, OrderKind kind, Guid tenantId, Guid companyId, Guid orderId, string type, int? lineNo, CommandContext ctx, string? reason, DateTime at, CancellationToken ct)
    {
        int no;
        await using (var cmd = tx.Command("select coalesce(max(event_no), 0) + 1 from core.order_event where tenant_id = @t and company_id = @c and order_kind = @k and order_id = @o"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("k", kind.Kind); cmd.Parameters.AddWithValue("o", orderId);
            no = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }
        await using var ins = tx.Command(
            "insert into core.order_event (tenant_id, company_id, order_kind, order_id, event_no, event_type, line_no, event_at, event_by, reason_code, correlation_id) " +
            "values (@t, @c, @k, @o, @n, @type, @line, @at, @by, @reason, @corr)");
        ins.Parameters.AddWithValue("t", tenantId); ins.Parameters.AddWithValue("c", companyId); ins.Parameters.AddWithValue("k", kind.Kind); ins.Parameters.AddWithValue("o", orderId);
        ins.Parameters.AddWithValue("n", no); ins.Parameters.AddWithValue("type", type); ins.Parameters.AddWithValue("by", ctx.Actor); ins.Parameters.AddWithValue("corr", ctx.CorrelationId);
        ins.Parameters.Add(new NpgsqlParameter("line", NpgsqlDbType.Integer) { Value = (object?)lineNo ?? DBNull.Value });
        ins.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = at });
        ins.Parameters.Add(new NpgsqlParameter("reason", NpgsqlDbType.Text) { Value = (object?)reason ?? DBNull.Value });
        await ins.ExecuteNonQueryAsync(ct);
        return no;
    }

    private static string ValueType(MasterKind k) => k switch
    {
        MasterKind.Amount or MasterKind.Percent or MasterKind.Rate => "AMOUNT", MasterKind.Factor => "QUANTITY", MasterKind.Date => "DATE", MasterKind.Id => "ID", MasterKind.Bool => "BOOL",
        MasterKind.Code => "CODE", _ => "TEXT"
    };

    private static string? Normalised(MasterKind k, string? text) =>
        text is not null && k is MasterKind.Amount or MasterKind.Percent or MasterKind.Rate or MasterKind.Factor && decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal d)
            ? (d / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture)
            : text;

    private static async Task LogChangeAsync(
        TenantTransaction tx, OrderKind kind, Guid tenantId, Guid companyId, Guid orderId, Guid changeSet, int? lineNo, MasterField field, string? oldValue, string? newValue,
        string? reason, CommandContext ctx, DateTime at, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "insert into core.order_change_log (tenant_id, change_set_id, company_id, order_kind, order_id, line_no, field_name, value_type, old_value, new_value, reason_code, changed_by, changed_at, " +
            "correlation_id, idempotency_key) values (@t, @cs, @c, @k, @o, @line, @f, @vt, @old, @new, @reason, @by, @at, @corr, @key)");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("cs", changeSet); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("k", kind.Kind);
        cmd.Parameters.AddWithValue("o", orderId); cmd.Parameters.AddWithValue("f", field.Name); cmd.Parameters.AddWithValue("vt", ValueType(field.Kind)); cmd.Parameters.AddWithValue("by", ctx.Actor);
        cmd.Parameters.AddWithValue("corr", ctx.CorrelationId); cmd.Parameters.AddWithValue("key", ctx.IdempotencyKey);
        cmd.Parameters.Add(new NpgsqlParameter("line", NpgsqlDbType.Integer) { Value = (object?)lineNo ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("old", NpgsqlDbType.Text) { Value = (object?)oldValue ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("new", NpgsqlDbType.Text) { Value = (object?)newValue ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("reason", NpgsqlDbType.Text) { Value = (object?)reason ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = at });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static Task OutboxAsync(
        TenantTransaction tx, OrderKind kind, Guid tenantId, Guid companyId, Guid orderId, CommandContext ctx, string eventType, string change, DateTime at, Dictionary<string, object?> summary, CancellationToken ct)
    {
        summary["order_kind"] = kind.Kind; summary["order_id"] = orderId;
        return CommandSupport.WriteOutboxAsync(tx, tenantId, companyId, ctx, eventType, kind.Kind + "_ORDER", $"{companyId:D}/{orderId:D}", change, at, isAudit: true, null, JsonSerializer.Serialize(summary), ct);
    }

    private static async Task BumpAsync(TenantTransaction tx, OrderKind kind, Guid tenantId, Guid orderId, string? status, CancellationToken ct)
    {
        await using var cmd = tx.Command($"update core.{kind.HeaderTable} set row_version = row_version + 1" + (status is null ? "" : ", lifecycle_status = @s") + $" where tenant_id = @t and {kind.IdColumn} = @i");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("i", orderId);
        if (status is not null) cmd.Parameters.AddWithValue("s", status);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static CommandError? Denied(CommandContext ctx, string permission) =>
        ctx.Permissions.Contains(permission) ? null : Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", permission));

    // ---------------------------------------------------------------------------------------------------------
    // :create
    // ---------------------------------------------------------------------------------------------------------
    /// <summary>The shape of a create request against the registries of the kind (also the shape of each side of an intercompany pair, 6.7).</summary>
    internal static (List<CommandError> Errors, Dictionary<string, Patch> Header, List<List<Patch>> Lines) ValidateCreate(OrderKind kind, CommandContext ctx, CreateOrderRequest req)
    {
        var errors = ValidateContext(ctx);
        Require(req.Fields, "/fields", errors, "company_id", "order_date", "order_currency", kind.PartyColumn);
        var patches = Validate(req.Fields, kind.HeaderCreate, "/fields", errors);
        if (req.Lines is null or { Count: 0 }) errors.Add(Error("SCHEMA.REQUIRED_FIELD_MISSING", "P1", "/lines"));
        var linePatches = new List<List<Patch>>();
        for (int i = 0; i < (req.Lines?.Count ?? 0); i++)
        {
            Require(req.Lines![i], $"/lines/{i}", errors, "item_id", "quantity", "unit_code", "unit_price");
            linePatches.Add(Validate(req.Lines[i], kind.LineCreate, $"/lines/{i}", errors));
        }
        var header = patches.ToDictionary(p => p.Field.Name);
        foreach (string d in new[] { "order_date", "requested_delivery_date", "expected_receipt_date" })
            if (header.TryGetValue(d, out Patch? dp) && dp.Value is DateOnly date && !OrderKind.DateInRange(date)) errors.Add(Error("ORDER.DATE_INVALID", "P1", "/fields/" + d));
        return (errors, header, linePatches);
    }

    public async Task<CommandResult<OrderResult>> CreateAsync(OrderKind kind, Guid tenantId, CommandContext ctx, CreateOrderRequest req, CancellationToken ct = default)
    {
        if (Denied(ctx, kind.OrderPermission) is { } denied) return Reject(denied);
        var (errors, header, linePatches) = ValidateCreate(kind, ctx, req);
        if (errors.Count > 0) return CommandResult<OrderResult>.Rejected(errors);
        byte[] fingerprint = OrderRules.Fingerprint(kind.Resource + ":create", req);

        return await RunAsync(async () =>
        {
            await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Order, ct: ct);
            if (await ReplayAsync(tx, kind, tenantId, ctx, fingerprint, ct) is { } replay) return replay;

            return await CreateCoreAsync(tx, kind, tenantId, ctx, req, header, linePatches, fingerprint, ct);
        });
    }
}
