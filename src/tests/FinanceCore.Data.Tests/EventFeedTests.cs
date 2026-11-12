using FinanceCore.Core.Commands;
using FinanceCore.Core.Feed;
using FinanceCore.Core.Posting;

namespace FinanceCore.Data.Tests;

/// <summary>The app registry, the event feed, the acknowledgement and the purge of app events (15).</summary>
[Collection("db")]
public class EventFeedTests
{
    private static string Describe<T>(CommandResult<T> r) => RevaluationTests.Describe(r);
    private static string[] Codes<T>(CommandResult<T> r) => RevaluationTests.Codes(r);
    private static CommandContext Manage(TestTenant t, string key, params string[] more) => t.Context(key, ["apps.manage", .. more]);
    private static CommandContext AsApp(TestTenant t, string key, string app, params string[] permissions) => t.Context(key, permissions) with { AppCode = app };

    private static async Task<AppResult> RegisterAsync(TestTenant t, string code, string[]? types = null, string? audit = null)
    {
        var feed = new EventFeed(t.App);
        var reg = await feed.RegisterAsync(t.TenantId, Manage(t, "reg-" + code), new(code, "App " + code));
        Assert.True(reg.Status == CommandStatus.Ok, Describe(reg));
        if (types is null) return reg.Value!;
        var set = await feed.SetSubscriptionsAsync(t.TenantId, audit is null ? Manage(t, "sub-" + code) : Manage(t, "sub-" + code, audit), new(reg.Value!.AppId, reg.Value.RowVersion, types));
        Assert.True(set.Status == CommandStatus.Ok, Describe(set));
        return set.Value!;
    }

    private static async Task PostAsync(TestTenant t, string key, string date = "2026-03-10")
    {
        var r = await new JournalDocumentPoster(t.App).PostAsync(t.TenantId, t.Context(key), t.Journal(postingDate: date));
        Assert.Equal(CommandStatus.Ok, r.Status);
    }

    [Fact]
    public async Task TheRegistryIsManagedWithVersionsLogsAndAuditEvents()
    {
        var t = await TestTenant.CreateReadyAsync();
        var feed = new EventFeed(t.App);
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await feed.RegisterAsync(t.TenantId, t.Context("a"), new("crm", "CRM"))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await feed.RegisterAsync(t.TenantId, Manage(t, "b"), new("Core", "x"))));

        var app = (await feed.RegisterAsync(t.TenantId, Manage(t, "r1"), new("crm", "CRM"))).Value!;
        Assert.Equal((true, 1), (app.Active, app.RowVersion));
        Assert.Equal(["CONFIG.DUPLICATE_CODE"], Codes(await feed.RegisterAsync(t.TenantId, Manage(t, "r2"), new("crm", "Again"))));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await feed.RegisterAsync(t.TenantId, Manage(t, "r3"), new("scoped", "S", [Guid.NewGuid()]))));
        Assert.True((await feed.RegisterAsync(t.TenantId, Manage(t, "r1"), new("crm", "CRM"))).IdempotentReplay);

        var upd = await feed.UpdateAsync(t.TenantId, Manage(t, "u1"), new(app.AppId, 1, Name: "CRM 2", CompanyScope: [t.CompanyId]));
        Assert.True(upd.Status == CommandStatus.Ok, Describe(upd));
        Assert.Equal((2, "CRM 2", t.CompanyId), (upd.Value!.RowVersion, upd.Value.Name, upd.Value.CompanyScope!.Single()));
        Assert.Equal(["CONFIG.ROW_VERSION_MISMATCH"], Codes(await feed.UpdateAsync(t.TenantId, Manage(t, "u2"), new(app.AppId, 1, Name: "x"))));
        Assert.Equal(["SCHEMA.NO_EFFECTIVE_CHANGE"], Codes(await feed.UpdateAsync(t.TenantId, Manage(t, "u3"), new(app.AppId, 2, Name: "CRM 2"))));
        Assert.Null((await feed.UpdateAsync(t.TenantId, Manage(t, "u4"), new(app.AppId, 2, AllCompanies: true))).Value!.CompanyScope);

        Assert.Equal(["APP.NOT_INACTIVE"], Codes(await feed.ReactivateAsync(t.TenantId, Manage(t, "x1"), new(app.AppId, 3, "again"))));
        var off = await feed.DeactivateAsync(t.TenantId, Manage(t, "d1"), new(app.AppId, 3, "left the company"));
        Assert.False(off.Value!.Active);
        var on = await feed.ReactivateAsync(t.TenantId, Manage(t, "x2"), new(app.AppId, 4, "back"));
        Assert.True(on.Value!.Active);

        Assert.Equal(["EVENT.NOT_SUBSCRIBED"], Codes(await feed.SetSubscriptionsAsync(t.TenantId, Manage(t, "s1"), new(app.AppId, 5, ["app.*"]))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await feed.SetSubscriptionsAsync(t.TenantId, Manage(t, "s2"), new(app.AppId, 5, ["nothing.here"]))));
        var subs = await feed.SetSubscriptionsAsync(t.TenantId, Manage(t, "s3"), new(app.AppId, 5, ["ledger.*", "period.status_changed"]));
        Assert.Equal(["ledger.*", "period.status_changed"], subs.Value!.EventTypes.ToArray());
        Assert.Equal(["SCHEMA.NO_EFFECTIVE_CHANGE"], Codes(await feed.SetSubscriptionsAsync(t.TenantId, Manage(t, "s4"), new(app.AppId, 6, ["period.status_changed", "ledger.*"]))));

        Assert.True(await t.ScalarAsync<long>("select count(*) from core.master_change_log where tenant_id = @t and entity_type in ('APP', 'APP_SUBSCRIPTION')") >= 7);
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.outbox_event where tenant_id = @t and event_type = 'app.registered'"));
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.outbox_event where tenant_id = @t and event_type = 'app.reactivated'"));
    }

    [Fact]
    public async Task AnAppSeesTheEventsItSubscribedToAndNothingSensitiveWithoutAuditRights()
    {
        var t = await TestTenant.CreateReadyAsync();
        var feed = new EventFeed(t.App);
        await RegisterAsync(t, "crm", ["ledger.*", "app.*"], audit: "read.audit");
        await RegisterAsync(t, "idle");   // no subscription
        await PostAsync(t, "p1");
        await PostAsync(t, "p2");

        var list = await feed.ListAsync(t.TenantId, AsApp(t, "l1", "crm", "read.events"), new());
        Assert.True(list.Status == CommandStatus.Ok, Describe(list));
        Assert.Equal(["ledger.document_posted", "ledger.document_posted"], list.Value!.Items.Select(i => i.EventType).ToArray());
        Assert.True(list.Value.Items[0].Position < list.Value.Items[1].Position);
        Assert.Equal(list.Value.HeadPosition, await t.ScalarAsync<long>("select last_position from core.outbox_head where tenant_id = @t"));
        Assert.Equal(list.Value.HeadPosition, list.Value.NextPosition);

        // the app events (app.registered and the others) are subscribed to but need read.audit in the feed
        var withAudit = await feed.ListAsync(t.TenantId, AsApp(t, "l2", "crm", "read.events", "read.audit"), new());
        Assert.Contains(withAudit.Value!.Items, i => i.EventType == "app.registered");
        Assert.DoesNotContain(list.Value.Items, i => i.EventType.StartsWith("app."));

        Assert.Empty((await feed.ListAsync(t.TenantId, AsApp(t, "l3", "idle", "read.events"), new())).Value!.Items);
        var page = await feed.ListAsync(t.TenantId, AsApp(t, "l4", "crm", "read.events"), new(Limit: 1));
        Assert.Single(page.Value!.Items);
        Assert.Equal(page.Value.Items[0].Position, page.Value.NextPosition);
        var rest = await feed.ListAsync(t.TenantId, AsApp(t, "l5", "crm", "read.events"), new(SincePosition: page.Value.NextPosition));
        Assert.Single(rest.Value!.Items);
        Assert.Equal(["EVENT.NOT_SUBSCRIBED"], Codes(await feed.ListAsync(t.TenantId, AsApp(t, "l6", "crm", "read.events"), new(EventType: "app.registered"))));

        Assert.Equal(["APP.NOT_REGISTERED"], Codes(await feed.ListAsync(t.TenantId, t.Context("l7", "read.events"), new())));
        Assert.Equal(["APP.NOT_REGISTERED"], Codes(await feed.ListAsync(t.TenantId, AsApp(t, "l8", "ghost", "read.events"), new())));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await feed.ListAsync(t.TenantId, AsApp(t, "l9", "crm"), new())));
        Assert.Equal(["READ.LIMIT_EXCEEDED"], Codes(await feed.ListAsync(t.TenantId, AsApp(t, "l10", "crm", "read.events"), new(Limit: 5001))));
        Assert.Equal(list.Value.HeadPosition, (await feed.HeadAsync(t.TenantId, AsApp(t, "h1", "crm", "read.events"))).Value!.HeadPosition);
    }

    [Fact]
    public async Task AnAppWithACompanyScopeNeverSeesTenantLevelEvents()
    {
        var t = await TestTenant.CreateReadyAsync();
        var feed = new EventFeed(t.App);
        var open = await RegisterAsync(t, "open", ["ledger.*", "app.*"], audit: "read.audit");
        var scoped = await RegisterAsync(t, "scoped", ["ledger.*", "app.*"], audit: "read.audit");
        Assert.Equal(CommandStatus.Ok, (await feed.UpdateAsync(t.TenantId, Manage(t, "scope"), new(scoped.AppId, scoped.RowVersion, CompanyScope: [t.CompanyId]))).Status);
        await PostAsync(t, "p1");

        var all = (await feed.ListAsync(t.TenantId, AsApp(t, "a", "open", "read.events", "read.audit"), new())).Value!.Items;
        var some = (await feed.ListAsync(t.TenantId, AsApp(t, "b", "scoped", "read.events", "read.audit"), new())).Value!.Items;
        Assert.Contains(all, i => i.CompanyId is null);
        Assert.All(some, i => Assert.Equal(t.CompanyId, i.CompanyId));
        Assert.Contains(some, i => i.EventType == "ledger.document_posted");
    }

    [Fact]
    public async Task AnAckMovesForwardOnlyAndARewindIsItsOwnCommand()
    {
        var t = await TestTenant.CreateReadyAsync();
        var feed = new EventFeed(t.App);
        var app = await RegisterAsync(t, "crm", ["ledger.*"]);
        await PostAsync(t, "p1"); await PostAsync(t, "p2");
        long head = (await feed.HeadAsync(t.TenantId, AsApp(t, "h", "crm", "read.events"))).Value!.HeadPosition;
        CommandContext Ack(string key) => AsApp(t, key, "crm", "events.consume");

        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await feed.AckAsync(t.TenantId, AsApp(t, "a0", "crm"), new("main", 1))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await feed.AckAsync(t.TenantId, Ack("a1"), new("main", head + 1))));
        Assert.Equal(head, (await feed.AckAsync(t.TenantId, Ack("a2"), new("main", head))).Value!.LastPosition);
        Assert.Equal(CommandStatus.Ok, (await feed.AckAsync(t.TenantId, Ack("a3"), new("main", head))).Status);   // the same position again is nothing
        Assert.Equal(["EVENT.ACK_NOT_FORWARD"], Codes(await feed.AckAsync(t.TenantId, Ack("a4"), new("main", head - 1))));

        Assert.Equal(["EVENT.REWIND_NOT_BACKWARD"], Codes(await feed.RewindAsync(t.TenantId, Manage(t, "w0"), new(app.AppId, "main", head))));
        var back = await feed.RewindAsync(t.TenantId, Manage(t, "w1"), new(app.AppId, "main", 1));
        Assert.True(back.Status == CommandStatus.Ok, Describe(back));
        Assert.Equal(1L, await t.ScalarAsync<long>("select last_position from core.app_consumer_offset where tenant_id = @t and consumer_id = 'main'"));
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.outbox_event where tenant_id = @t and event_type = 'app.consumer_rewound'"));
        // nothing but the rewind lowers an offset, whatever role asks
        await using (var tx = await TenantTransaction.BeginAsync(t.App, t.TenantId, CommandRole.Config))
        await using (var cmd = tx.Command("update core.app_consumer_offset set last_position = 0 where tenant_id = @t"))
        {
            cmd.Parameters.AddWithValue("t", t.TenantId);
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => cmd.ExecuteNonQueryAsync());
        }
        // a deactivated app cannot ack, but can still read
        Assert.Equal(CommandStatus.Ok, (await feed.DeactivateAsync(t.TenantId, Manage(t, "d"), new(app.AppId, app.RowVersion, "gone"))).Status);
        Assert.Equal(["APP.INACTIVE"], Codes(await feed.AckAsync(t.TenantId, Ack("a5"), new("main", head))));
        Assert.Equal(CommandStatus.Ok, (await feed.ListAsync(t.TenantId, AsApp(t, "l", "crm", "read.events"), new())).Status);
    }

    /// <summary>Synthetic app events (not audit events), dated in the past, so that the purge has something to remove.</summary>
    private static async Task AddOldAppEventsAsync(TestTenant t, int count)
    {
        await t.ExecuteAsync("""
            update core.outbox_head set last_position = last_position + @n where tenant_id = @t;
            insert into core.outbox_event (tenant_id, position, event_type, event_version, company_id, entity_type, entity_key, change, occurred_at, actor_ref, correlation_id, is_audit, summary)
              select @t, (select last_position from core.outbox_head where tenant_id = @t) - @n + g, 'stock.balance_changed', 1, @c, 'STOCK', 'k' || g, 'CHANGED',
                     now() - interval '40 days', 'test', 'test', false, '{}'::jsonb from generate_series(1, @n) g;
            """, ("n", count));
    }

    [Fact]
    public async Task ThePurgeRemovesOnlyOldAppEventsThatEveryActiveConsumerHasPassed()
    {
        var t = await TestTenant.CreateReadyAsync();
        var feed = new EventFeed(t.App);
        var app = await RegisterAsync(t, "stock", ["stock.*", "ledger.*"]);
        long before = await t.ScalarAsync<long>("select last_position from core.outbox_head where tenant_id = @t");
        await AddOldAppEventsAsync(t, 5);
        long head = before + 5;
        var purge = new OutboxPurge(t.App);
        CommandContext Ack(string key) => AsApp(t, key, "stock", "events.consume");

        // a consumer that has read up to the second old event holds back the rest
        Assert.Equal(CommandStatus.Ok, (await feed.AckAsync(t.TenantId, Ack("a1"), new("main", before + 2))).Status);
        var first = await purge.PurgeAsync(t.TenantId);
        Assert.Equal((2L, before + 2, (long?)(before + 2)), (first.Deleted, first.PurgedThrough, first.BlockedAtPosition));
        Assert.Equal(3L, await t.ScalarAsync<long>("select count(*) from core.outbox_event where tenant_id = @t and event_type = 'stock.balance_changed'"));
        Assert.Equal(0L, (await purge.PurgeAsync(t.TenantId)).Deleted);

        // audit events stay, whatever their age
        Assert.True(await t.ScalarAsync<long>("select count(*) from core.outbox_event where tenant_id = @t and is_audit") > 0);

        // the consumer that has passed everything lets the rest go, and a reader below the purge mark must resynchronise
        Assert.Equal(CommandStatus.Ok, (await feed.AckAsync(t.TenantId, Ack("a2"), new("main", head))).Status);
        Assert.Equal(3L, (await purge.PurgeAsync(t.TenantId)).Deleted);
        var expired = await feed.ListAsync(t.TenantId, AsApp(t, "l1", "stock", "read.events"), new(SincePosition: before));
        Assert.Equal(["EVENT.POSITION_EXPIRED"], Codes(expired));
        Assert.Equal(CommandStatus.Ok, (await feed.ListAsync(t.TenantId, AsApp(t, "l2", "stock", "read.events"), new(SincePosition: head))).Status);

        // a consumer that has been silent for longer than the lag no longer blocks the purge
        await AddOldAppEventsAsync(t, 2);
        await t.ExecuteAsync("update core.app_consumer_offset set updated_at = now() - interval '100 days' where tenant_id = @t");
        var unblocked = await purge.PurgeAsync(t.TenantId);
        Assert.Equal((2L, (long?)null), (unblocked.Deleted, unblocked.BlockedAtPosition));
    }
}
