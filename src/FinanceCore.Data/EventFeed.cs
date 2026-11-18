using System.Text.Json;
using System.Text.RegularExpressions;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Feed;
using Npgsql;
using NpgsqlTypes;

namespace FinanceCore.Data;

/// <summary>
/// The event feed (15, 3.1 to 3.4, 4.5, 5.3): the app registry commands, the subscriptions, events:list and :head, the acknowledgement of a consumer and the rewind.
/// The registry is configuration: every change is a master_change_log row (entity types APP and APP_SUBSCRIPTION) and an audit event. Delivery is at least once and
/// the transport is outside the core (15, 5.3). Not built: namespaces and extension attributes, the VALIDATE mode, a company check of the events by the entity's company
/// beyond company_id, and the read of the registry (apps:list).
/// </summary>
public sealed class EventFeed(NpgsqlDataSource source)
{
    public const string ManagePermission = "apps.manage";
    public const string ReadPermission = "read.events";
    public const string ConsumePermission = "events.consume";
    public const string AuditPermission = "read.audit";

    private static CommandError Error(string code, string phase, string path, params (string Key, object? Value)[] p) => JournalDocumentPoster.Error(code, phase, path, p);
    private static CommandResult<T> Reject<T>(CommandError e) => CommandResult<T>.Rejected([e]);
    private static CommandResult<T> Denied<T>(string permission) => Reject<T>(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", permission)));

    private sealed record AppRow(Guid AppId, string AppCode, string Name, bool Active, IReadOnlyList<Guid>? Scope, int RowVersion);

    // ---------------------------------------------------------------------------------------------------------
    // reads of the registry
    // ---------------------------------------------------------------------------------------------------------
    private static IReadOnlyList<Guid>? ParseScope(object? json) =>
        json is null or DBNull ? null : JsonSerializer.Deserialize<List<Guid>>((string)json);

    private static async Task<AppRow?> ReadAppAsync(TenantTransaction tx, Guid tenantId, Guid? appId, string? appCode, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select app_id, app_code, name, active, company_scope::text, row_version from core.app_registration where tenant_id = @t and " +
            (appId is not null ? "app_id = @id" : "app_code = @code") + (forUpdate ? " for update" : ""));
        cmd.Parameters.AddWithValue("t", tenantId);
        if (appId is not null) cmd.Parameters.AddWithValue("id", appId.Value); else cmd.Parameters.AddWithValue("code", appCode ?? "");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new AppRow(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetBoolean(3), ParseScope(r.IsDBNull(4) ? null : r.GetString(4)), r.GetInt32(5)) : null;
    }

    private static async Task<List<string>> ReadSubscriptionsAsync(TenantTransaction tx, Guid tenantId, Guid appId, CancellationToken ct)
    {
        var list = new List<string>();
        await using var cmd = tx.Command("select event_type from core.app_event_subscription where tenant_id = @t and app_id = @a order by event_type");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("a", appId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(r.GetString(0));
        return list;
    }

    private static async Task<AppResult> LoadResultAsync(TenantTransaction tx, Guid tenantId, Guid appId, CancellationToken ct)
    {
        AppRow a = (await ReadAppAsync(tx, tenantId, appId, null, false, ct))!;
        return new AppResult(a.AppId, a.AppCode, a.Name, a.Active, a.Scope, a.RowVersion, await ReadSubscriptionsAsync(tx, tenantId, appId, ct));
    }

    /// <summary>The identity of the caller (15, 3.1): a missing or unknown app code is APP.NOT_REGISTERED, an inactive app APP.INACTIVE (only where activity is required).</summary>
    private static async Task<(AppRow? App, CommandError? Error)> IdentifyAsync(TenantTransaction tx, Guid tenantId, CommandContext ctx, bool requireActive, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ctx.AppCode)) return (null, Error("APP.NOT_REGISTERED", "P0", "app_code"));
        AppRow? app = await ReadAppAsync(tx, tenantId, null, ctx.AppCode, false, ct);
        if (app is null) return (null, Error("APP.NOT_REGISTERED", "P0", "app_code", ("app_code", ctx.AppCode)));
        if (requireActive && !app.Active) return (null, Error("APP.INACTIVE", "P0", "app_code", ("app_code", ctx.AppCode)));
        return (app, null);
    }

    // ---------------------------------------------------------------------------------------------------------
    // the registry commands
    // ---------------------------------------------------------------------------------------------------------
    private static readonly Regex AppCodePattern = new("^[a-z][a-z0-9_]{1,49}$", RegexOptions.Compiled);

    private async Task<CommandResult<AppResult>> RegistryAsync(
        Guid tenantId, CommandContext ctx, string command, byte[] fingerprint,
        Func<TenantTransaction, Guid, Guid, CancellationToken, Task<(Guid AppId, CommandError? Error)>> work, CancellationToken ct)
    {
        if (!ctx.Permissions.Contains(ManagePermission)) return Denied<AppResult>(ManagePermission);
        var shape = MasterSupport.ValidateContext(ctx);
        if (shape.Count > 0) return CommandResult<AppResult>.Rejected(shape);
        return await MasterSupport.RunAsync(async () =>
        {
            await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Config, ct: ct);
            CommandSupport.EarlierCommand? earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
            if (earlier is not null)
            {
                if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint))
                    return Reject<AppResult>(Error("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
                return earlier.EntityId is null
                    ? CommandResult<AppResult>.Failed(Error("SYSTEM.REPLAY_RESULT_MISSING", "P1", ""))
                    : CommandResult<AppResult>.Ok(await LoadResultAsync(tx, tenantId, earlier.EntityId.Value, ct), earlier.CommandLogId, replay: true);
            }
            Guid logId = Guid.NewGuid(), changeSet = Guid.NewGuid();
            var (appId, error) = await work(tx, logId, changeSet, ct);
            if (error is not null) return Reject<AppResult>(error);
            await CommandSupport.WriteLogAsync(tx, tenantId, null, ctx, command, fingerprint, "APP", appId, logId, ct);
            await WriteEventAsync(tx, tenantId, ctx, command, appId, ct);
            AppResult result = await LoadResultAsync(tx, tenantId, appId, ct);
            await tx.CommitAsync(ct);
            return CommandResult<AppResult>.Ok(result, logId);
        });
    }

    private static Task WriteEventAsync(TenantTransaction tx, Guid tenantId, CommandContext ctx, string command, Guid appId, CancellationToken ct)
    {
        (string type, string change) = command switch
        {
            "apps:register" => ("app.registered", "CREATED"), "apps:reactivate" => ("app.reactivated", "REACTIVATED"),
            "apps:deactivate" => ("app.changed", "DEACTIVATED"), _ => ("app.changed", "CHANGED")
        };
        DateTime now = DateTime.UtcNow;
        return CommandSupport.WriteOutboxAsync(tx, tenantId, null, ctx, type, "APP", appId.ToString("D"), change, new DateTime(now.Ticks - now.Ticks % 10, DateTimeKind.Utc),
            isAudit: true, null, JsonSerializer.Serialize(new Dictionary<string, object?> { ["app_id"] = appId }), ct);
    }

    private static string ScopeDigest(IReadOnlyList<Guid>? scope) =>
        scope is null ? "ALL" : FeedRules.SubscriptionDigest(scope.Select(g => g.ToString("D")));

    public Task<CommandResult<AppResult>> RegisterAsync(Guid tenantId, CommandContext ctx, RegisterAppRequest req, CancellationToken ct = default)
    {
        if (ctx.Permissions.Contains(ManagePermission))
        {
            var errors = new List<CommandError>();
            if (!AppCodePattern.IsMatch(req.AppCode ?? "") || req.AppCode == "core") errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "app_code", ("reason", "lower case code, 2 to 50 characters, not core")));
            if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Length > 100) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "name", ("reason", "required, at most 100 characters")));
            if (errors.Count > 0) return Task.FromResult(CommandResult<AppResult>.Rejected(errors));
        }
        return RegistryAsync(tenantId, ctx, "apps:register", FeedRules.Fingerprint("apps:register", req), async (tx, logId, set, token) =>
        {
            if (req.CompanyScope is { } scope && await CompaniesMissingAsync(tx, tenantId, scope, token) is { } missing)
                return (Guid.Empty, Error("CONFIG.INVALID_REFERENCE", "P2", "company_scope", ("company_id", missing)));
            await using (var cmd = tx.Command("select 1 from core.app_registration where tenant_id = @t and app_code = @c"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.AppCode!);
                if (await cmd.ExecuteScalarAsync(token) is not null) return (Guid.Empty, Error("CONFIG.DUPLICATE_CODE", "P2", "app_code", ("code", req.AppCode)));
            }
            Guid id = Guid.NewGuid();
            await using (var cmd = tx.Command("insert into core.app_registration (tenant_id, app_id, app_code, name, company_scope) values (@t, @id, @c, @n, @s)"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("id", id); cmd.Parameters.AddWithValue("c", req.AppCode!); cmd.Parameters.AddWithValue("n", req.Name);
                cmd.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Jsonb) { Value = req.CompanyScope is null ? DBNull.Value : JsonSerializer.Serialize(req.CompanyScope) });
                await cmd.ExecuteNonQueryAsync(token);
            }
            await MasterSupport.LogAsync(tx, tenantId, ctx, logId, set, "APP", id.ToString("D"), "CREATE", null, false, null, null, null);
            return (id, null);
        }, ct);
    }

    private static async Task<Guid?> CompaniesMissingAsync(TenantTransaction tx, Guid tenantId, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        foreach (Guid id in ids)
        {
            await using var cmd = tx.Command("select 1 from core.company where tenant_id = @t and company_id = @c");
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", id);
            if (await cmd.ExecuteScalarAsync(ct) is null) return id;
        }
        return null;
    }

    /// <summary>The app, locked, with the compare-and-set of its version (CONFIG.ROW_VERSION_MISMATCH).</summary>
    private static async Task<(AppRow? App, CommandError? Error)> LockAppAsync(TenantTransaction tx, Guid tenantId, Guid appId, int expectedVersion, CancellationToken ct)
    {
        AppRow? app = await ReadAppAsync(tx, tenantId, appId, null, true, ct);
        if (app is null) return (null, Error("CONFIG.INVALID_REFERENCE", "P2", "app_id", ("app_id", appId)));
        if (app.RowVersion != expectedVersion) return (null, Error("CONFIG.ROW_VERSION_MISMATCH", "P2", "expected_version", ("current_version", app.RowVersion)));
        return (app, null);
    }

    public Task<CommandResult<AppResult>> UpdateAsync(Guid tenantId, CommandContext ctx, UpdateAppRequest req, CancellationToken ct = default)
    {
        if (ctx.Permissions.Contains(ManagePermission))
        {
            var errors = new List<CommandError>();
            if (req.Name is not null && (string.IsNullOrWhiteSpace(req.Name) || req.Name.Length > 100)) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "name", ("reason", "at most 100 characters")));
            if (req.CompanyScope is not null && req.AllCompanies) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "company_scope", ("reason", "a scope or all companies, not both")));
            if (req.Name is null && req.CompanyScope is null && !req.AllCompanies) errors.Add(Error("SCHEMA.NO_EFFECTIVE_CHANGE", "P1", ""));
            if (errors.Count > 0) return Task.FromResult(CommandResult<AppResult>.Rejected(errors));
        }
        return RegistryAsync(tenantId, ctx, "apps:update", FeedRules.Fingerprint("apps:update", req), async (tx, logId, set, token) =>
        {
            var (app, error) = await LockAppAsync(tx, tenantId, req.AppId, req.ExpectedVersion, token);
            if (error is not null) return (Guid.Empty, error);
            if (req.CompanyScope is { } scope && await CompaniesMissingAsync(tx, tenantId, scope, token) is { } missing)
                return (Guid.Empty, Error("CONFIG.INVALID_REFERENCE", "P2", "company_scope", ("company_id", missing)));
            string name = req.Name ?? app!.Name;
            IReadOnlyList<Guid>? newScope = req.AllCompanies ? null : req.CompanyScope ?? app!.Scope;
            bool nameChanged = name != app!.Name, scopeChanged = ScopeDigest(newScope) != ScopeDigest(app.Scope);
            if (!nameChanged && !scopeChanged) return (Guid.Empty, Error("SCHEMA.NO_EFFECTIVE_CHANGE", "P2", ""));
            await using (var cmd = tx.Command("update core.app_registration set name = @n, company_scope = @s, row_version = row_version + 1 where tenant_id = @t and app_id = @id"))
            {
                cmd.Parameters.AddWithValue("n", name); cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("id", req.AppId);
                cmd.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Jsonb) { Value = newScope is null ? DBNull.Value : JsonSerializer.Serialize(newScope) });
                await cmd.ExecuteNonQueryAsync(token);
            }
            string key = req.AppId.ToString("D");
            if (nameChanged) await MasterSupport.LogAsync(tx, tenantId, ctx, logId, set, "APP", key, "UPDATE", "name", false, app.Name, name, null);
            if (scopeChanged) await MasterSupport.LogAsync(tx, tenantId, ctx, logId, set, "APP", key, "UPDATE", "company_scope", false, ScopeDigest(app.Scope), ScopeDigest(newScope), null);
            return (req.AppId, null);
        }, ct);
    }

    private Task<CommandResult<AppResult>> StatusAsync(Guid tenantId, CommandContext ctx, AppStatusRequest req, bool activate, CancellationToken ct)
    {
        string command = activate ? "apps:reactivate" : "apps:deactivate";
        if (ctx.Permissions.Contains(ManagePermission) && (string.IsNullOrWhiteSpace(req.ReasonText) || req.ReasonText.Length > 500))
            return Task.FromResult(Reject<AppResult>(Error("SCHEMA.INVALID_VALUE", "P1", "reason_text", ("reason", "required, at most 500 characters"))));
        return RegistryAsync(tenantId, ctx, command, FeedRules.Fingerprint(command, req), async (tx, logId, set, token) =>
        {
            var (app, error) = await LockAppAsync(tx, tenantId, req.AppId, req.ExpectedVersion, token);
            if (error is not null) return (Guid.Empty, error);
            if (activate && app!.Active) return (Guid.Empty, Error("APP.NOT_INACTIVE", "P2", "app_id", ("app_id", req.AppId)));
            if (!activate && !app!.Active) return (Guid.Empty, Error("APP.INACTIVE", "P2", "app_id", ("app_id", req.AppId)));
            await using (var cmd = tx.Command("update core.app_registration set active = @a, row_version = row_version + 1 where tenant_id = @t and app_id = @id"))
            {
                cmd.Parameters.AddWithValue("a", activate); cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("id", req.AppId);
                await cmd.ExecuteNonQueryAsync(token);
            }
            await MasterSupport.LogAsync(tx, tenantId, ctx, logId, set, "APP", req.AppId.ToString("D"), activate ? "REACTIVATE" : "DEACTIVATE", null, false, null, null, null);
            return (req.AppId, null);
        }, ct);
    }

    public Task<CommandResult<AppResult>> DeactivateAsync(Guid tenantId, CommandContext ctx, AppStatusRequest req, CancellationToken ct = default) => StatusAsync(tenantId, ctx, req, false, ct);

    public Task<CommandResult<AppResult>> ReactivateAsync(Guid tenantId, CommandContext ctx, AppStatusRequest req, CancellationToken ct = default) => StatusAsync(tenantId, ctx, req, true, ct);

    public Task<CommandResult<AppResult>> SetSubscriptionsAsync(Guid tenantId, CommandContext ctx, SetSubscriptionsRequest req, CancellationToken ct = default)
    {
        if (ctx.Permissions.Contains(ManagePermission))
        {
            var errors = new List<CommandError>();
            foreach (string type in req.EventTypes ?? [])
            {
                if (!FeedRules.IsKnown(type)) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "event_types", ("event_type", type), ("reason", "not a known event type or family")));
                else if (FeedRules.IsSensitive(type) && !ctx.Permissions.Contains(AuditPermission)) errors.Add(Error("EVENT.NOT_SUBSCRIBED", "P1", "event_types", ("event_type", type)));
            }
            if (req.EventTypes is null) errors.Add(Error("SCHEMA.REQUIRED_FIELD_MISSING", "P1", "event_types"));
            if (errors.Count > 0) return Task.FromResult(CommandResult<AppResult>.Rejected(errors));
        }
        return RegistryAsync(tenantId, ctx, "app-subscriptions:set", FeedRules.Fingerprint("app-subscriptions:set", req), async (tx, logId, set, token) =>
        {
            var (app, error) = await LockAppAsync(tx, tenantId, req.AppId, req.ExpectedVersion, token);
            if (error is not null) return (Guid.Empty, error);
            var old = await ReadSubscriptionsAsync(tx, tenantId, req.AppId, token);
            var wanted = req.EventTypes!.Distinct().OrderBy(t => t, StringComparer.Ordinal).ToList();
            if (old.SequenceEqual(wanted)) return (Guid.Empty, Error("SCHEMA.NO_EFFECTIVE_CHANGE", "P2", "event_types"));
            await using (var cmd = tx.Command("delete from core.app_event_subscription where tenant_id = @t and app_id = @a"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("a", req.AppId);
                await cmd.ExecuteNonQueryAsync(token);
            }
            foreach (string type in wanted)
            {
                await using var cmd = tx.Command("insert into core.app_event_subscription (tenant_id, app_id, event_type) values (@t, @a, @e)");
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("a", req.AppId); cmd.Parameters.AddWithValue("e", type);
                await cmd.ExecuteNonQueryAsync(token);
            }
            await using (var cmd = tx.Command("update core.app_registration set row_version = row_version + 1 where tenant_id = @t and app_id = @a"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("a", req.AppId);
                await cmd.ExecuteNonQueryAsync(token);
            }
            await MasterSupport.LogAsync(tx, tenantId, ctx, logId, set, "APP_SUBSCRIPTION", req.AppId.ToString("D"), "UPDATE", "event_types", false,
                FeedRules.SubscriptionDigest(old), FeedRules.SubscriptionDigest(wanted), null);
            return (req.AppId, null);
        }, ct);
    }

    // ---------------------------------------------------------------------------------------------------------
    // events:list and events:head (15, 5.3)
    // ---------------------------------------------------------------------------------------------------------
    public async Task<CommandResult<EventList>> ListAsync(Guid tenantId, CommandContext ctx, ListEventsRequest req, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(ReadPermission)) return Denied<EventList>(ReadPermission);
        int limit = req.Limit ?? FeedRules.DefaultLimit;
        long since = req.SincePosition ?? 0;
        if (limit is < 1 or > FeedRules.MaxLimit) return Reject<EventList>(Error("READ.LIMIT_EXCEEDED", "P1", "limit", ("max", FeedRules.MaxLimit)));
        if (since < 0) return Reject<EventList>(Error("SCHEMA.INVALID_VALUE", "P1", "since_position", ("reason", "not negative")));
        bool audit = ctx.Permissions.Contains(AuditPermission);
        if (req.EventType is not null && FeedRules.IsSensitive(req.EventType) && !audit) return Reject<EventList>(Error("EVENT.NOT_SUBSCRIBED", "P1", "event_type", ("event_type", req.EventType)));

        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct);
        var (app, error) = await IdentifyAsync(tx, tenantId, ctx, requireActive: false, ct);
        if (error is not null) return Reject<EventList>(error);

        long head;
        await using (var cmd = tx.Command("select last_position from core.outbox_head where tenant_id = @t"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            head = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct) ?? 0L);
        }
        await using (var cmd = tx.Command("select purged_through from core.outbox_purge_state where tenant_id = @t"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            if (await cmd.ExecuteScalarAsync(ct) is long purged && since < purged)
                return Reject<EventList>(Error("EVENT.POSITION_EXPIRED", "P2", "since_position", ("since_position", since), ("purged_through", purged)));
        }

        var items = new List<FeedEvent>();
        await using (var cmd = tx.Command(
            "select e.position, e.event_type, e.event_version, e.company_id, e.entity_type, e.entity_key, e.entity_version, e.change, e.occurred_at, e.actor_ref, e.correlation_id, " +
            "e.is_audit, e.chain_ref::text, e.summary::text from core.outbox_event e where e.tenant_id = @t and e.position > @since " +
            "and exists (select 1 from core.app_event_subscription s where s.tenant_id = e.tenant_id and s.app_id = @app and (s.event_type = e.event_type " +
            "  or (s.event_type like '%.*' and e.event_type like split_part(s.event_type, '.', 1) || '.%'))) " +
            "and (@audit or not (split_part(e.event_type, '.', 1) in ('access', 'retention', 'dsr', 'export', 'deletion', 'app') " +
            "  or e.event_type in ('partner.anonymised', 'partner.restricted', 'legal_hold.changed'))) " +
            "and ((@scope::jsonb is null and true) or (@scope::jsonb is not null and e.company_id is not null and @scope::jsonb ? e.company_id::text)) " +
            "and (@type::text is null or e.event_type = @type) and (@company::uuid is null or e.company_id = @company) " +
            "and (@entity::text is null or e.entity_type = @entity) and (@key::text is null or e.entity_key = @key) order by e.position limit @n"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("since", since); cmd.Parameters.AddWithValue("app", app!.AppId);
            cmd.Parameters.AddWithValue("audit", audit); cmd.Parameters.AddWithValue("n", limit);
            cmd.Parameters.Add(new NpgsqlParameter("scope", NpgsqlDbType.Jsonb) { Value = app.Scope is null ? DBNull.Value : JsonSerializer.Serialize(app.Scope.Select(g => g.ToString("D"))) });
            cmd.Parameters.Add(new NpgsqlParameter("type", NpgsqlDbType.Text) { Value = (object?)req.EventType ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("company", NpgsqlDbType.Uuid) { Value = (object?)req.CompanyId ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("entity", NpgsqlDbType.Text) { Value = (object?)req.EntityType ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("key", NpgsqlDbType.Text) { Value = (object?)req.EntityKey ?? DBNull.Value });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                items.Add(new FeedEvent(r.GetInt64(0), r.GetString(1), r.GetInt16(2), r.IsDBNull(3) ? null : r.GetGuid(3), r.GetString(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetInt64(6),
                    r.GetString(7), DateTime.SpecifyKind(r.GetDateTime(8), DateTimeKind.Utc), r.GetString(9), r.GetString(10), r.GetBoolean(11),
                    r.IsDBNull(12) ? null : r.GetString(12), r.IsDBNull(13) ? null : r.GetString(13)));
        }
        // the filter hides events from this caller, so the next position is the last one it saw, or the head when nothing is left to see
        long next = items.Count == limit ? items[^1].Position : head;
        return CommandResult<EventList>.Ok(new EventList(items, next, head), Guid.Empty);
    }

    public async Task<CommandResult<EventsHead>> HeadAsync(Guid tenantId, CommandContext ctx, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(ReadPermission)) return Denied<EventsHead>(ReadPermission);
        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct);
        var (_, error) = await IdentifyAsync(tx, tenantId, ctx, requireActive: false, ct);
        if (error is not null) return Reject<EventsHead>(error);
        await using var cmd = tx.Command("select last_position from core.outbox_head where tenant_id = @t");
        cmd.Parameters.AddWithValue("t", tenantId);
        return CommandResult<EventsHead>.Ok(new EventsHead(Convert.ToInt64(await cmd.ExecuteScalarAsync(ct) ?? 0L)), Guid.Empty);
    }

    // ---------------------------------------------------------------------------------------------------------
    // events:ack and events:rewind (15, 3.4, 5.3)
    // ---------------------------------------------------------------------------------------------------------
    private static readonly Regex ConsumerPattern = new("^[a-z0-9][a-z0-9_-]{0,49}$", RegexOptions.Compiled);

    public async Task<CommandResult<ConsumerOffset>> AckAsync(Guid tenantId, CommandContext ctx, AckEventsRequest req, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(ConsumePermission)) return Denied<ConsumerOffset>(ConsumePermission);
        if (!ConsumerPattern.IsMatch(req.ConsumerId ?? "")) return Reject<ConsumerOffset>(Error("SCHEMA.INVALID_VALUE", "P1", "consumer_id", ("reason", "lower case code, at most 50 characters")));
        if (req.Position < 0) return Reject<ConsumerOffset>(Error("SCHEMA.INVALID_VALUE", "P1", "position", ("reason", "not negative")));
        return await MasterSupport.RunAsync(async () =>
        {
            await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Config, ct: ct);
            var (app, error) = await IdentifyAsync(tx, tenantId, ctx, requireActive: true, ct);
            if (error is not null) return Reject<ConsumerOffset>(error);
            long head;
            await using (var cmd = tx.Command("select last_position from core.outbox_head where tenant_id = @t"))
            {
                cmd.Parameters.AddWithValue("t", tenantId);
                head = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct) ?? 0L);
            }
            if (req.Position > head) return Reject<ConsumerOffset>(Error("SCHEMA.INVALID_VALUE", "P2", "position", ("reason", "beyond the head of the outbox"), ("head_position", head)));
            long? current = null;
            await using (var cmd = tx.Command("select last_position from core.app_consumer_offset where tenant_id = @t and app_id = @a and consumer_id = @c for update"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("a", app!.AppId); cmd.Parameters.AddWithValue("c", req.ConsumerId!);
                if (await cmd.ExecuteScalarAsync(ct) is long c) current = c;
            }
            if (current is null)
            {
                await using var cmd = tx.Command("insert into core.app_consumer_offset (tenant_id, app_id, consumer_id, last_position) values (@t, @a, @c, @p)");
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("a", app!.AppId); cmd.Parameters.AddWithValue("c", req.ConsumerId!); cmd.Parameters.AddWithValue("p", req.Position);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            else if (req.Position < current) return Reject<ConsumerOffset>(Error("EVENT.ACK_NOT_FORWARD", "P2", "position", ("last_position", current)));
            else if (req.Position > current)
            {
                await using var cmd = tx.Command("update core.app_consumer_offset set last_position = @p, updated_at = now() where tenant_id = @t and app_id = @a and consumer_id = @c");
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("a", app!.AppId); cmd.Parameters.AddWithValue("c", req.ConsumerId!); cmd.Parameters.AddWithValue("p", req.Position);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            else
            {
                await using var cmd = tx.Command("update core.app_consumer_offset set updated_at = now() where tenant_id = @t and app_id = @a and consumer_id = @c");   // alive again
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("a", app!.AppId); cmd.Parameters.AddWithValue("c", req.ConsumerId!);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            return CommandResult<ConsumerOffset>.Ok(new ConsumerOffset(app!.AppId, req.ConsumerId!, req.Position), Guid.Empty);
        });
    }

    public async Task<CommandResult<ConsumerOffset>> RewindAsync(Guid tenantId, CommandContext ctx, RewindConsumerRequest req, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(ManagePermission)) return Denied<ConsumerOffset>(ManagePermission);
        var shape = MasterSupport.ValidateContext(ctx);
        if (!ConsumerPattern.IsMatch(req.ConsumerId ?? "")) shape.Add(Error("SCHEMA.INVALID_VALUE", "P1", "consumer_id", ("reason", "lower case code, at most 50 characters")));
        if (req.Position < 0) shape.Add(Error("SCHEMA.INVALID_VALUE", "P1", "position", ("reason", "not negative")));
        if (shape.Count > 0) return CommandResult<ConsumerOffset>.Rejected(shape);
        byte[] fingerprint = FeedRules.Fingerprint("events:rewind", req);
        return await MasterSupport.RunAsync(async () =>
        {
            await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Config, ct: ct);
            CommandSupport.EarlierCommand? earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
            if (earlier is not null)
                return earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint)
                    ? CommandResult<ConsumerOffset>.Ok(new ConsumerOffset(req.AppId, req.ConsumerId!, req.Position), earlier.CommandLogId, replay: true)
                    : Reject<ConsumerOffset>(Error("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
            if (await ReadAppAsync(tx, tenantId, req.AppId, null, false, ct) is null) return Reject<ConsumerOffset>(Error("CONFIG.INVALID_REFERENCE", "P2", "app_id", ("app_id", req.AppId)));
            long? current = null;
            await using (var cmd = tx.Command("select last_position from core.app_consumer_offset where tenant_id = @t and app_id = @a and consumer_id = @c for update"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("a", req.AppId); cmd.Parameters.AddWithValue("c", req.ConsumerId!);
                if (await cmd.ExecuteScalarAsync(ct) is long c) current = c;
            }
            if (current is null) return Reject<ConsumerOffset>(Error("CONFIG.INVALID_REFERENCE", "P2", "consumer_id", ("consumer_id", req.ConsumerId)));
            if (req.Position >= current) return Reject<ConsumerOffset>(Error("EVENT.REWIND_NOT_BACKWARD", "P2", "position", ("last_position", current)));
            await using (var cmd = tx.Command("select set_config('app.allow_rewind', 'on', true)")) await cmd.ExecuteScalarAsync(ct);
            await using (var cmd = tx.Command("update core.app_consumer_offset set last_position = @p, updated_at = now() where tenant_id = @t and app_id = @a and consumer_id = @c"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("a", req.AppId); cmd.Parameters.AddWithValue("c", req.ConsumerId!); cmd.Parameters.AddWithValue("p", req.Position);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            Guid logId = await CommandSupport.WriteLogAsync(tx, tenantId, null, ctx, "events:rewind", fingerprint, "APP", req.AppId, ct);
            DateTime now = DateTime.UtcNow;
            await CommandSupport.WriteOutboxAsync(tx, tenantId, null, ctx, "app.consumer_rewound", "APP", req.AppId.ToString("D"), "CHANGED", new DateTime(now.Ticks - now.Ticks % 10, DateTimeKind.Utc),
                isAudit: true, null, JsonSerializer.Serialize(new Dictionary<string, object?> { ["app_id"] = req.AppId }), ct);
            await tx.CommitAsync(ct);
            return CommandResult<ConsumerOffset>.Ok(new ConsumerOffset(req.AppId, req.ConsumerId!, req.Position), logId);
        });
    }
}

/// <summary>
/// The purge of app events (15, 5.4), a job of the retention role. It deletes the events that are not audit events, are older than outbox_app_event_retention_days and
/// that every active consumer has passed. A consumer that has not been heard from for outbox_consumer_max_lag_days no longer blocks the purge. The position up to which events
/// were purged is kept, and a consumer below it gets EVENT.POSITION_EXPIRED. Positions are never reused.
/// </summary>
public sealed class OutboxPurge(NpgsqlDataSource source)
{
    public async Task<PurgeResult> PurgeAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Retention, ct: ct);
        int days, lag;
        await using (var cmd = tx.Command("select outbox_app_event_retention_days, outbox_consumer_max_lag_days from core.tenant_setting where tenant_id = @t"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) throw new InvalidOperationException("The tenant has no settings");
            days = r.GetInt32(0); lag = r.GetInt32(1);
        }
        long? bound = null;
        await using (var cmd = tx.Command(
            "select min(o.last_position) from core.app_consumer_offset o join core.app_registration a on a.tenant_id = o.tenant_id and a.app_id = o.app_id " +
            "where o.tenant_id = @t and a.active and o.updated_at > now() - make_interval(days => @lag)"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("lag", lag);
            if (await cmd.ExecuteScalarAsync(ct) is long b) bound = b;
        }
        long deleted = 0, through = 0;
        await using (var cmd = tx.Command(
            "with d as (delete from core.outbox_event where tenant_id = @t and not is_audit and occurred_at < now() - make_interval(days => @days) " +
            "and position <= @bound returning position) select count(*), coalesce(max(position), 0) from d"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("days", days); cmd.Parameters.AddWithValue("bound", bound ?? long.MaxValue);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            deleted = r.GetInt64(0); through = r.GetInt64(1);
        }
        if (deleted > 0)
        {
            await using var cmd = tx.Command(
                "insert into core.outbox_purge_state (tenant_id, purged_through) values (@t, @p) " +
                "on conflict (tenant_id) do update set purged_through = greatest(core.outbox_purge_state.purged_through, excluded.purged_through), purged_at = now()");
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("p", through);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return new PurgeResult(deleted, through, bound);
    }
}
