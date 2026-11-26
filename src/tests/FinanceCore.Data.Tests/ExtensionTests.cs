using System.Text.Json;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Feed;
using FinanceCore.Core.Masters;
using FinanceCore.Core.Posting;
using FinanceCore.Core.Reads;

namespace FinanceCore.Data.Tests;

/// <summary>Extension attributes (15, 3.2 to 3.6a, 4; 09, 2.4.2; F-IMPL-95): namespaces, definitions, values with the counter, the reads with include and ext filters, and the personal values.</summary>
[Collection("db")]
public class ExtensionTests
{
    private static string Describe<T>(CommandResult<T> r) => r.Status + ": " + string.Join(", ", r.Errors.Select(e => $"{e.Code} {e.Path}"));
    private static string[] Codes<T>(CommandResult<T> r) => r.Errors.Select(e => e.Code).ToArray();
    private static Dictionary<string, object?> D(params (string, object?)[] f) => f.ToDictionary(x => x.Item1, x => x.Item2);
    private static JsonElement J(object value) => JsonSerializer.SerializeToElement(value);
    private static Dictionary<string, JsonElement> V(params (string, object)[] f) => f.ToDictionary(x => x.Item1, x => J(x.Item2));

    private sealed record Setup(TestTenant T, Guid AppId, ExtensionCommands Cmds, ExtensionReads Reads);

    private static CommandContext App(TestTenant t, string key, string app, params string[] permissions) => new(key, "corr-" + key, "tester", new HashSet<string>(permissions), "API", app);

    private static async Task<Guid> RegisterAsync(TestTenant t, string code, IReadOnlyList<Guid>? scope = null)
    {
        var r = await new EventFeed(t.App).RegisterAsync(t.TenantId, t.Context("reg-" + code, "apps.manage"), new RegisterAppRequest(code, code.ToUpperInvariant(), scope));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        return r.Value!.AppId;
    }

    private static async Task<Setup> SetupAsync()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid app = await RegisterAsync(t, "crm");
        var cmds = new ExtensionCommands(t.App);
        var ns = await cmds.CreateNamespaceAsync(t.TenantId, t.Context("ns1", "apps.manage"), new(app, "crm"));
        Assert.True(ns.Status == CommandStatus.Ok, Describe(ns));
        return new Setup(t, app, cmds, new ExtensionReads(t.App));
    }

    private static async Task<ExtensionDefinitionResult> DefineAsync(Setup s, string key, string entity, string attr, string type, Action<Dictionary<string, object?>>? more = null, string ns = "crm", string app = "crm")
    {
        var f = D(("EntityType", entity), ("Namespace", ns), ("AttrKey", attr), ("DataType", type));
        more?.Invoke(f);
        var req = new CreateExtensionDefinitionRequest(entity, ns, attr, type, (int?)f.GetValueOrDefault("MaxLength"), (int?)f.GetValueOrDefault("Precision"), (int?)f.GetValueOrDefault("Scale"),
            (IReadOnlyList<string>?)f.GetValueOrDefault("Allowed"), (string?)f.GetValueOrDefault("Ref"), f.GetValueOrDefault("Pii") is true);
        var r = await s.Cmds.CreateDefinitionAsync(s.T.TenantId, App(s.T, key, app, "ext.define"), req);
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        return r.Value!;
    }

    private static async Task<Guid> PartnerAsync(TestTenant t, string code, string name = "Acme A/S")
    {
        var r = await new PartnerCommands(t.App).CreateAsync(t.TenantId, t.Context("p" + code, "write.master"), new CreatePartnerRequest(
            D(("partner_code", code), ("partner_kind", "ORGANISATION"), ("is_customer", true)),
            D(("legal_name", name), ("address_line_1", "Havnegade 1"), ("postal_code", "1058"), ("city", "København K"), ("country_code", "DK")),
            [new PartnerRelationInput(t.CompanyId, D(("is_customer", true)))]));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        return r.Value!.EntityId;
    }

    private static Task<CommandResult<ExtensionsResult>> Set(Setup s, string key, string type, string entityKey, Dictionary<string, JsonElement> values, long? expected = null, string app = "crm") =>
        s.Cmds.SetAsync(s.T.TenantId, App(s.T, key, app, "ext.write"), new(type, entityKey, values, expected));

    // -------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task ANamespaceBelongsToOneAppAndDefinitionsAreCheckedLimitedAndLocked()
    {
        var s = await SetupAsync();
        var t = s.T;
        Assert.Equal(["APP.NAMESPACE_TAKEN"], Codes(await s.Cmds.CreateNamespaceAsync(t.TenantId, t.Context("ns2", "apps.manage"), new(s.AppId, "crm"))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await s.Cmds.CreateNamespaceAsync(t.TenantId, t.Context("ns3", "apps.manage"), new(s.AppId, "core"))));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await s.Cmds.CreateNamespaceAsync(t.TenantId, t.Context("ns4", "apps.manage"), new(Guid.NewGuid(), "other"))));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await s.Cmds.CreateNamespaceAsync(t.TenantId, t.Context("ns5", "read.config"), new(s.AppId, "x1"))));

        var tier = await DefineAsync(s, "d1", "BUSINESS_PARTNER", "tier", "TEXT", f => { f["MaxLength"] = 10; f["Allowed"] = new List<string> { "GOLD", "SILVER" }; });
        Assert.Equal((true, 1L, false), (tier.Active, tier.RowVersion, tier.IsPii));
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.outbox_event where tenant_id = @t and event_type = 'extension.defined' and is_audit"));

        // the rules of a definition
        CommandContext Def(string key, string app = "crm") => App(t, key, app, "ext.define");
        CreateExtensionDefinitionRequest Req(string attr = "x", string type = "TEXT", string entity = "BUSINESS_PARTNER", int? ml = null, string? refType = null, int? precision = null, int? scale = null) =>
            new(entity, "crm", attr, type, ml, precision, scale, null, refType);
        Assert.Equal(["CONFIG.DUPLICATE_CODE"], Codes(await s.Cmds.CreateDefinitionAsync(t.TenantId, Def("d2"), Req("tier"))));
        Assert.Equal(["EXT.ENTITY_NOT_EXTENDABLE"], Codes(await s.Cmds.CreateDefinitionAsync(t.TenantId, Def("d3"), Req(entity: "JOURNAL_DOCUMENT"))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await s.Cmds.CreateDefinitionAsync(t.TenantId, Def("d4"), Req(type: "NUMBER"))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await s.Cmds.CreateDefinitionAsync(t.TenantId, Def("d5"), Req(type: "DATE", ml: 5))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await s.Cmds.CreateDefinitionAsync(t.TenantId, Def("d6"), Req(type: "REF"))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await s.Cmds.CreateDefinitionAsync(t.TenantId, Def("d7"), Req(type: "DECIMAL", precision: 4, scale: 6))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await s.Cmds.CreateDefinitionAsync(t.TenantId, Def("d8"), Req("Bad-Key"))));
        // another app cannot define in the namespace, nor call without a registration
        await RegisterAsync(t, "erp");
        Assert.Equal(["EXT.NOT_OWNER"], Codes(await s.Cmds.CreateDefinitionAsync(t.TenantId, Def("d9", "erp"), Req("y"))));
        Assert.Equal(["APP.NOT_REGISTERED"], Codes(await s.Cmds.CreateDefinitionAsync(t.TenantId, Def("d10", "nobody"), Req("y"))));
        Assert.Equal(["APP.NOT_REGISTERED"], Codes(await s.Cmds.CreateDefinitionAsync(t.TenantId, t.Context("d11", "ext.define"), Req("y"))));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await s.Cmds.CreateDefinitionAsync(t.TenantId, App(t, "d12", "crm", "ext.write"), Req("y"))));

        // the limit of active definitions per entity type and namespace is a tenant setting
        await t.ExecuteAsync("update core.tenant_setting set extension_max_definitions = 2 where tenant_id = @t");
        await DefineAsync(s, "d13", "BUSINESS_PARTNER", "second", "BOOLEAN");
        Assert.Equal(["EXT.DEFINITION_LIMIT"], Codes(await s.Cmds.CreateDefinitionAsync(t.TenantId, Def("d14"), Req("third"))));

        // update: compare and set, and is_pii is locked once a value exists
        Guid partner = await PartnerAsync(t, "C1");
        var wrongVersion = await s.Cmds.UpdateDefinitionAsync(t.TenantId, Def("u1"), new(tier.DefinitionId, 9, MaxLength: 20));
        Assert.Equal(["CONFIG.ROW_VERSION_MISMATCH"], Codes(wrongVersion));
        var updated = await s.Cmds.UpdateDefinitionAsync(t.TenantId, Def("u2"), new(tier.DefinitionId, 1, MaxLength: 20, IsPii: true));
        Assert.True(updated.Status == CommandStatus.Ok, Describe(updated));
        Assert.Equal((2L, true), (updated.Value!.RowVersion, updated.Value.IsPii));
        Assert.Equal(["SCHEMA.NO_EFFECTIVE_CHANGE"], Codes(await s.Cmds.UpdateDefinitionAsync(t.TenantId, Def("u3"), new(tier.DefinitionId, 2, MaxLength: 20))));
        Assert.Equal(CommandStatus.Ok, (await Set(s, "v1", "BUSINESS_PARTNER", partner.ToString(), V(("crm.tier", "GOLD")))).Status);
        Assert.Equal(["EXT.DEFINITION_LOCKED"], Codes(await s.Cmds.UpdateDefinitionAsync(t.TenantId, Def("u4"), new(tier.DefinitionId, 2, IsPii: false))));
        Assert.Equal(["EXT.NOT_OWNER"], Codes(await s.Cmds.UpdateDefinitionAsync(t.TenantId, Def("u5", "erp"), new(tier.DefinitionId, 2, MaxLength: 30))));
        // the identity of a definition never changes (the guard of the table)
        await Assert.ThrowsAnyAsync<Exception>(() => t.ExecuteAsync("update core.extension_definition set data_type = 'DATE' where tenant_id = @t and definition_id = @d", ("d", tier.DefinitionId)));

        // deactivate: the values stay readable, a new write is refused
        var off = await s.Cmds.DeactivateDefinitionAsync(t.TenantId, Def("x1"), new(tier.DefinitionId, 2));
        Assert.True(off.Status == CommandStatus.Ok, Describe(off));
        Assert.False(off.Value!.Active);
        Assert.Equal(["EXT.UNKNOWN_DEFINITION"], Codes(await Set(s, "v2", "BUSINESS_PARTNER", partner.ToString(), V(("crm.tier", "SILVER")))));
        Assert.Equal(["SCHEMA.NO_EFFECTIVE_CHANGE"], Codes(await s.Cmds.DeactivateDefinitionAsync(t.TenantId, Def("x2"), new(tier.DefinitionId, 3))));

        // the list of definitions
        var list = await s.Reads.ListDefinitionsAsync(t.TenantId, t.Context("l1", "read.config"), new(EntityType: "BUSINESS_PARTNER"));
        Assert.Equal(["second", "tier"], list.Value!.Items.Select(i => i.GetProperty("attr_key").GetString()!).ToArray());
        Assert.Equal(["second"], (await s.Reads.ListDefinitionsAsync(t.TenantId, t.Context("l2", "read.config"), new(Active: true))).Value!.Items.Select(i => i.GetProperty("attr_key").GetString()!).ToArray());
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await s.Reads.ListDefinitionsAsync(t.TenantId, t.Context("l3", "read.pii"), new())));
        var p1 = (await s.Reads.ListDefinitionsAsync(t.TenantId, t.Context("l4", "read.config"), new(Limit: 1))).Value!;
        Assert.NotNull(p1.Page.NextCursor);
        Assert.Single((await s.Reads.ListDefinitionsAsync(t.TenantId, t.Context("l5", "read.config"), new(Limit: 1, Cursor: p1.Page.NextCursor))).Value!.Items);
    }

    // -------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task ValuesAreTypedCheckedCountedLoggedAndNeverTouchTheOwnerRow()
    {
        var s = await SetupAsync();
        var t = s.T;
        Guid partner = await PartnerAsync(t, "C1"), other = await PartnerAsync(t, "C2");
        await DefineAsync(s, "d1", "BUSINESS_PARTNER", "tier", "TEXT", f => { f["MaxLength"] = 6; f["Allowed"] = new List<string> { "GOLD", "SILVER" }; });
        await DefineAsync(s, "d2", "BUSINESS_PARTNER", "score", "DECIMAL", f => { f["Precision"] = 6; f["Scale"] = 2; });
        await DefineAsync(s, "d3", "BUSINESS_PARTNER", "since", "DATE");
        await DefineAsync(s, "d4", "BUSINESS_PARTNER", "vip", "BOOLEAN");
        await DefineAsync(s, "d5", "BUSINESS_PARTNER", "parent", "REF", f => f["Ref"] = "BUSINESS_PARTNER");
        await DefineAsync(s, "d6", "BUSINESS_PARTNER", "nickname", "TEXT", f => f["Pii"] = true);
        string key = partner.ToString("D").ToUpperInvariant();
        long rowVersion = await t.ScalarAsync<long>("select row_version from core.business_partner where tenant_id = @t and partner_id = @p", ("p", partner));

        // a set of several values is one change of the counter, and the key is made canonical
        var first = await Set(s, "s1", "BUSINESS_PARTNER", key, V(("crm.tier", "GOLD"), ("crm.score", 12.5m), ("crm.since", "2026-03-01"), ("crm.vip", true), ("crm.parent", other.ToString()), ("crm.nickname", "Boss")), expected: 0);
        Assert.True(first.Status == CommandStatus.Ok, Describe(first));
        Assert.Equal((partner.ToString("D"), 1L, 6), (first.Value!.EntityKey, first.Value.Version, first.Value.Changed.Count));
        Assert.Equal(rowVersion, await t.ScalarAsync<long>("select row_version from core.business_partner where tenant_id = @t and partner_id = @p", ("p", partner)));
        Assert.Equal(6L, await t.ScalarAsync<long>("select count(*) from core.extension_value where tenant_id = @t and entity_key = @k", ("k", partner.ToString("D"))));
        Assert.Equal(6L, await t.ScalarAsync<long>("select count(*) from core.outbox_event where tenant_id = @t and event_type = 'extension.changed' and not is_audit"));
        // the event holds no value, the change log holds none for a personal definition
        Assert.DoesNotContain("Boss", string.Join(' ', await t.ScalarAsync<string>("select string_agg(summary::text, ' ') from core.outbox_event where tenant_id = @t and event_type = 'extension.changed'")));
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.master_change_log where tenant_id = @t and entity_type = 'EXTENSION_VALUE' and field_name = 'crm.nickname' and is_pii and new_value is null and old_value is null"));
        Assert.Equal("GOLD", await t.ScalarAsync<string>("select new_value from core.master_change_log where tenant_id = @t and entity_type = 'EXTENSION_VALUE' and field_name = 'crm.tier'"));

        // compare and set: the counter, not the row; the same value changes nothing but still counts
        Assert.Equal(["EXT.VERSION_CONFLICT"], Codes(await Set(s, "s2", "BUSINESS_PARTNER", key, V(("crm.tier", "SILVER")), expected: 0)));
        var same = await Set(s, "s3", "BUSINESS_PARTNER", key, V(("crm.tier", "GOLD")), expected: 1);
        Assert.Equal((2L, 0), (same.Value!.Version, same.Value.Changed.Count));
        var silver = await Set(s, "s4", "BUSINESS_PARTNER", key, V(("crm.tier", "SILVER")));
        Assert.Equal((3L, 1), (silver.Value!.Version, silver.Value.Changed.Count));
        // a replay of a key returns the same result and writes nothing
        var replay = await Set(s, "s4", "BUSINESS_PARTNER", key, V(("crm.tier", "SILVER")));
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(3L, replay.Value!.Version);
        Assert.Equal(["IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST"], Codes(await Set(s, "s4", "BUSINESS_PARTNER", key, V(("crm.tier", "GOLD")))));

        // clear: the row goes, the counter stays and goes on
        var cleared = await s.Cmds.ClearAsync(t.TenantId, App(t, "c1", "crm", "ext.write"), new("BUSINESS_PARTNER", key, ["crm.vip", "crm.since"], 3));
        Assert.True(cleared.Status == CommandStatus.Ok, Describe(cleared));
        Assert.Equal((4L, 2), (cleared.Value!.Version, cleared.Value.Changed.Count));
        Assert.Equal(4L, await t.ScalarAsync<long>("select count(*) from core.extension_value where tenant_id = @t and entity_key = @k", ("k", partner.ToString("D"))));
        Assert.Equal(2L, await t.ScalarAsync<long>("select count(*) from core.master_change_log where tenant_id = @t and entity_type = 'EXTENSION_VALUE' and action = 'CLEAR'"));
        var cleared2 = await s.Cmds.ClearAsync(t.TenantId, App(t, "c2", "crm", "ext.write"), new("BUSINESS_PARTNER", key, ["crm.vip", "crm.score", "crm.parent", "crm.tier", "crm.nickname"]));
        Assert.Equal((5L, 4), (cleared2.Value!.Version, cleared2.Value.Changed.Count));
        Assert.Equal(0L, await t.ScalarAsync<long>("select count(*) from core.extension_value where tenant_id = @t"));
        Assert.Equal(5L, await t.ScalarAsync<long>("select counter from core.extension_counter where tenant_id = @t and entity_key = @k", ("k", partner.ToString("D"))));
        await Assert.ThrowsAnyAsync<Exception>(() => t.ExecuteAsync("update core.extension_counter set counter = 1 where tenant_id = @t"));
        await Assert.ThrowsAnyAsync<Exception>(() => t.ExecuteAsync("delete from core.extension_counter where tenant_id = @t"));

        // the checks of a value
        Assert.Equal(["EXT.VALUE_NOT_ALLOWED"], Codes(await Set(s, "e1", "BUSINESS_PARTNER", key, V(("crm.tier", "BRONZE")))));
        Assert.Equal(["EXT.TYPE_MISMATCH"], Codes(await Set(s, "e2", "BUSINESS_PARTNER", key, V(("crm.tier", "TOOLONGTEXT")))));
        Assert.Equal(["EXT.TYPE_MISMATCH"], Codes(await Set(s, "e3", "BUSINESS_PARTNER", key, V(("crm.score", "abc")))));
        Assert.Equal(["EXT.TYPE_MISMATCH"], Codes(await Set(s, "e4", "BUSINESS_PARTNER", key, V(("crm.score", 1.234m)))));
        Assert.Equal(["EXT.TYPE_MISMATCH"], Codes(await Set(s, "e5", "BUSINESS_PARTNER", key, V(("crm.score", 12345.6m)))));
        Assert.Equal(CommandStatus.Ok, (await Set(s, "e6", "BUSINESS_PARTNER", key, V(("crm.score", "1234.50")))).Status);
        Assert.Equal(["EXT.TYPE_MISMATCH"], Codes(await Set(s, "e7", "BUSINESS_PARTNER", key, V(("crm.since", "01-03-2026")))));
        Assert.Equal(["EXT.TYPE_MISMATCH"], Codes(await Set(s, "e8", "BUSINESS_PARTNER", key, V(("crm.vip", "yes")))));
        Assert.Equal(["EXT.REF_INVALID"], Codes(await Set(s, "e9", "BUSINESS_PARTNER", key, V(("crm.parent", Guid.NewGuid().ToString())))));
        Assert.Equal(["EXT.UNKNOWN_DEFINITION"], Codes(await Set(s, "e10", "BUSINESS_PARTNER", key, V(("crm.unknown", "x")))));
        Assert.Equal(["EXT.NOT_OWNER"], Codes(await Set(s, "e11", "BUSINESS_PARTNER", key, V(("other.tier", "x")))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await Set(s, "e12", "BUSINESS_PARTNER", key, V(("crm.tier", "GOLD"), ("other.tier", "x")))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await Set(s, "e13", "BUSINESS_PARTNER", key, V(("tier", "GOLD")))));
        Assert.Equal(["EXT.ENTITY_NOT_EXTENDABLE"], Codes(await Set(s, "e14", "JOURNAL_DOCUMENT", key, V(("crm.tier", "GOLD")))));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await Set(s, "e15", "BUSINESS_PARTNER", Guid.NewGuid().ToString(), V(("crm.tier", "GOLD")))));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await Set(s, "e16", "BUSINESS_PARTNER", "not-a-key", V(("crm.tier", "GOLD")))));
        Assert.Equal(["APP.NOT_REGISTERED"], Codes(await Set(s, "e17", "BUSINESS_PARTNER", key, V(("crm.tier", "GOLD")), app: "nobody")));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await s.Cmds.SetAsync(t.TenantId, App(t, "e18", "crm", "ext.define"), new("BUSINESS_PARTNER", key, V(("crm.tier", "GOLD"))))));
        // a deactivated app cannot write, and can again after the reactivation (15, 4.5)
        var app = await new EventFeed(t.App).DeactivateAsync(t.TenantId, t.Context("off", "apps.manage"), new(s.AppId, 1, "paused"));
        Assert.True(app.Status == CommandStatus.Ok, Describe(app));
        Assert.Equal(["APP.INACTIVE"], Codes(await Set(s, "e19", "BUSINESS_PARTNER", key, V(("crm.tier", "GOLD")))));
    }

    [Fact]
    public async Task AnAppWithACompanyScopeWritesOnlyForItsCompaniesAndNotOnTenantLevelRecords()
    {
        var s = await SetupAsync();
        var t = s.T;
        Guid partner = await PartnerAsync(t, "C1");
        // an open item of the company
        var doc = await new JournalDocumentPoster(t.App).PostAsync(t.TenantId, t.Context("op"), new PostJournalDocumentRequest(t.CompanyId, null, "JOURNAL", new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 1), "DKK", "Open", null, null,
            [new RequestLine("1", await OpenItemAccountAsync(t), 500m, null, new OpenItemRequest(DueDate: new DateOnly(2026, 4, 1))), new RequestLine("2", t.Revenue, -500m)]));
        Assert.True(doc.Status == CommandStatus.Ok, Describe(doc));
        string itemKey = $"{t.CompanyId:D}/{doc.Value!.DocumentId:D}/1";
        await DefineAsync(s, "d1", "OPEN_ITEM", "dunning", "TEXT");
        await DefineAsync(s, "d2", "BUSINESS_PARTNER", "tier", "TEXT");

        Assert.Equal(CommandStatus.Ok, (await Set(s, "s1", "OPEN_ITEM", itemKey, V(("crm.dunning", "LEVEL1")))).Status);
        // an app with a scope of another company: out of scope for this company, and never on a tenant-level record
        Guid otherCompany = Guid.NewGuid();
        await t.ExecuteAsync("insert into core.company (tenant_id, company_id, company_code, legal_name, country_code, local_currency, reporting_currency, fiscal_calendar_id, status) " +
            "select tenant_id, @n, 'OTH', 'Other A/S', country_code, local_currency, reporting_currency, fiscal_calendar_id, 'DRAFT' from core.company where tenant_id = @t and company_id = @c", ("n", otherCompany));
        Guid scoped = await RegisterAsync(t, "scoped", [otherCompany]);
        Assert.Equal(CommandStatus.Ok, (await s.Cmds.CreateNamespaceAsync(t.TenantId, t.Context("ns9", "apps.manage"), new(scoped, "scp"))).Status);
        Assert.Equal(CommandStatus.Ok, (await s.Cmds.CreateDefinitionAsync(t.TenantId, App(t, "d9", "scoped", "ext.define"), new("OPEN_ITEM", "scp", "note", "TEXT"))).Status);
        Assert.Equal(CommandStatus.Ok, (await s.Cmds.CreateDefinitionAsync(t.TenantId, App(t, "d10", "scoped", "ext.define"), new("BUSINESS_PARTNER", "scp", "note", "TEXT"))).Status);
        Assert.Equal(["EXT.COMPANY_OUT_OF_SCOPE"], Codes(await Set(s, "s2", "OPEN_ITEM", itemKey, V(("scp.note", "x")), app: "scoped")));
        Assert.Equal(["EXT.COMPANY_OUT_OF_SCOPE"], Codes(await Set(s, "s3", "BUSINESS_PARTNER", partner.ToString(), V(("scp.note", "x")), app: "scoped")));
        // the app that has no scope writes on the tenant-level record
        Assert.Equal(CommandStatus.Ok, (await Set(s, "s4", "BUSINESS_PARTNER", partner.ToString(), V(("crm.tier", "GOLD")))).Status);
        // a closed order cannot be extended: not tested here for lack of an order, the rule is in the entity table
    }

    private static async Task<Guid> OpenItemAccountAsync(TestTenant t)
    {
        Guid id = Guid.NewGuid();
        await t.ExecuteAsync("insert into core.gl_account (tenant_id, company_id, account_id, account_no, name, account_type, tax_code_rule, open_item_mode) values (@t, @c, @g, '1900', 'Clearing', 'ASSET', 'FORBIDDEN', 'GL_OPEN_ITEM')", ("g", id));
        return id;
    }

    // -------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task TheGetsAndListsEmbedExtensionsFilterOnThemAndLogThePersonalOnes()
    {
        var s = await SetupAsync();
        var t = s.T;
        Guid a = await PartnerAsync(t, "C1"), b = await PartnerAsync(t, "C2"), c = await PartnerAsync(t, "C3");
        await DefineAsync(s, "d1", "BUSINESS_PARTNER", "tier", "TEXT");
        await DefineAsync(s, "d2", "BUSINESS_PARTNER", "score", "DECIMAL", f => { f["Precision"] = 8; f["Scale"] = 2; });
        await DefineAsync(s, "d3", "BUSINESS_PARTNER", "since", "DATE");
        await DefineAsync(s, "d4", "BUSINESS_PARTNER", "vip", "BOOLEAN");
        await DefineAsync(s, "d5", "BUSINESS_PARTNER", "nickname", "TEXT", f => f["Pii"] = true);
        Assert.Equal(CommandStatus.Ok, (await Set(s, "s1", "BUSINESS_PARTNER", a.ToString(), V(("crm.tier", "Gold Customer"), ("crm.score", 10.5m), ("crm.since", "2026-01-15"), ("crm.vip", true), ("crm.nickname", "Boss")))).Status);
        Assert.Equal(CommandStatus.Ok, (await Set(s, "s2", "BUSINESS_PARTNER", b.ToString(), V(("crm.tier", "Silver"), ("crm.score", 3m), ("crm.since", "2025-06-01"), ("crm.vip", false)))).Status);
        var reads = new PartnerReads(t.App);
        CommandContext Read(string key, params string[] more) => t.Context(key, ["read.config", .. more]);
        Task<CommandResult<PartnerList>> List(ListPartnersRequest req, params string[] more) => reads.ListAsync(t.TenantId, Read("l" + Guid.NewGuid().ToString("N")[..6], more), req);
        string[] Codes(CommandResult<PartnerList> r) => r.Value!.Items.Select(p => p.PartnerCode).ToArray();
        ExtensionFilter F(string field, string op, string? value = null, params string[] values) => new(field, op, value, values.Length == 0 ? null : values);

        // a get without the include has no extensions, with it the flat map; the personal value needs read.pii and is logged
        Assert.Null((await reads.GetAsync(t.TenantId, Read("g1"), new(a))).Value!.Partner.Extensions);
        var plain = (await reads.GetAsync(t.TenantId, Read("g2"), new(a, IncludeExtensions: true))).Value!.Partner.Extensions!;
        Assert.Equal(["crm.score", "crm.since", "crm.tier", "crm.vip"], plain.Keys.Order().ToArray());
        Assert.Equal(("Gold Customer", "10.50", "2026-01-15", true), ((string)plain["crm.tier"]!, (string)plain["crm.score"]!, (string)plain["crm.since"]!, (bool)plain["crm.vip"]!));
        Assert.Equal(0L, await t.ScalarAsync<long>("select count(*) from core.read_access_log where tenant_id = @t and field_group = 'PII_EXTENSION'"));
        var withPii = (await reads.GetAsync(t.TenantId, Read("g3", "read.pii"), new(a, IncludeExtensions: true))).Value!.Partner.Extensions!;
        Assert.Equal("Boss", withPii["crm.nickname"]);
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.read_access_log where tenant_id = @t and field_group = 'PII_EXTENSION' and partner_id = @p", ("p", a)));
        Assert.Empty((await reads.GetAsync(t.TenantId, Read("g4"), new(c, IncludeExtensions: true))).Value!.Partner.Extensions!);

        // a list with the include
        var all = (await List(new(IncludeExtensions: true))).Value!;
        Assert.Equal([4, 4, 0], all.Items.Select(p => p.Extensions!.Count).ToArray());

        // the filters
        Assert.Equal(["C1"], Codes(await List(new(Ext: [F("crm.tier", "=", "Gold Customer")]))));
        Assert.Equal(["C2"], Codes(await List(new(Ext: [F("crm.tier", "!=", "Gold Customer")]))));
        Assert.Equal(["C1", "C2"], Codes(await List(new(Ext: [F("crm.tier", "in", values: ["Gold Customer", "Silver", "Nothing"])]))));
        Assert.Equal(["C3"], Codes(await List(new(Ext: [F("crm.tier", "is_null", "true")]))));
        Assert.Equal(["C1", "C2"], Codes(await List(new(Ext: [F("crm.tier", "is_null", "false")]))));
        Assert.Equal(["C1"], Codes(await List(new(Ext: [F("crm.score", ">", "5")]))));
        Assert.Equal(["C2"], Codes(await List(new(Ext: [F("crm.score", "<=", "3.00")]))));
        Assert.Equal(["C1"], Codes(await List(new(Ext: [F("crm.since", ">=", "2026-01-01")]))));
        Assert.Equal(["C1"], Codes(await List(new(Ext: [F("crm.vip", "=", "true")]))));
        Assert.Equal(["C1"], Codes(await List(new(Ext: [F("crm.tier", "contains", "GOLD cust")]))));
        Assert.Empty(Codes(await List(new(Ext: [F("crm.tier", "contains", "g%d")]))));
        // filters are ANDed with each other and with the other filters
        Assert.Equal(["C1"], Codes(await List(new(Ext: [F("crm.score", ">", "1"), F("crm.vip", "=", "true")]))));
        Assert.Empty(Codes(await List(new(Ext: [F("crm.score", ">", "1"), F("crm.vip", "=", "true")], CodePrefix: "C2"))));
        // a filter on a personal definition needs read.pii and is logged
        Assert.Equal(["AUTH.PERMISSION_MISSING"], (await List(new(Ext: [F("crm.nickname", "=", "Boss")]))).Errors.Select(e => e.Code).ToArray());
        Assert.Equal(["C1"], Codes(await List(new(Ext: [F("crm.nickname", "=", "Boss")]), "read.pii")));
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.read_access_log where tenant_id = @t and field_group = 'PII_EXTENSION' and partner_id is null"));

        // the errors
        string[] Err(CommandResult<PartnerList> r) => r.Errors.Select(e => e.Code).ToArray();
        Assert.Equal(["READ.INVALID_FILTER"], Err(await List(new(Ext: [F("crm.unknown", "=", "x")]))));
        Assert.Equal(["READ.INVALID_FILTER"], Err(await List(new(Ext: [F("tier", "=", "x")]))));
        Assert.Equal(["READ.INVALID_FILTER"], Err(await List(new(Ext: [F("crm.tier", "~", "x")]))));
        Assert.Equal(["READ.INVALID_FILTER"], Err(await List(new(Ext: [F("crm.tier", ">", "x")]))));
        Assert.Equal(["READ.INVALID_FILTER"], Err(await List(new(Ext: [F("crm.score", "=", "abc")]))));
        Assert.Equal(["READ.INVALID_FILTER"], Err(await List(new(Ext: [F("crm.score", "contains", "123")]))));
        Assert.Equal(["READ.INVALID_FILTER"], Err(await List(new(Ext: [F("crm.tier", "is_null", "maybe")]))));
        Assert.Equal(["READ.CONTAINS_TOO_SHORT"], Err(await List(new(Ext: [F("crm.tier", "contains", "go")]))));
    }

    [Fact]
    public async Task OpenItemsAndTheMasterResourcesCarryExtensionsToo()
    {
        var s = await SetupAsync();
        var t = s.T;
        Guid account = await OpenItemAccountAsync(t);
        Guid[] docs = new Guid[2];
        for (int i = 0; i < 2; i++)
        {
            var doc = await new JournalDocumentPoster(t.App).PostAsync(t.TenantId, t.Context("op" + i), new PostJournalDocumentRequest(t.CompanyId, null, "JOURNAL", new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 1), "DKK", "Open", null, null,
                [new RequestLine("1", account, 500m, null, new OpenItemRequest(DueDate: new DateOnly(2026, 4, 1 + i))), new RequestLine("2", t.Revenue, -500m)]));
            Assert.True(doc.Status == CommandStatus.Ok, Describe(doc));
            docs[i] = doc.Value!.DocumentId;
        }
        await DefineAsync(s, "d1", "OPEN_ITEM", "dunning", "TEXT");
        Assert.Equal(CommandStatus.Ok, (await Set(s, "s1", "OPEN_ITEM", $"{t.CompanyId:D}/{docs[0]:D}/1", V(("crm.dunning", "LEVEL ONE")))).Status);
        var reads = new LedgerReads(t.App);
        CommandContext Ctx(string key) => t.Context(key, "read.ledger");
        var r = await reads.OpenItemsAsync(t.TenantId, Ctx("o1"), new(t.CompanyId, new DateOnly(2026, 5, 1), Include: ["extensions"]));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        Assert.Equal(new string?[] { "LEVEL ONE", null }, r.Value!.Items.Select(i => i.Extensions!.GetValueOrDefault("crm.dunning") as string).ToArray());
        Assert.Null((await reads.OpenItemsAsync(t.TenantId, Ctx("o2"), new(t.CompanyId, new DateOnly(2026, 5, 1)))).Value!.Items[0].Extensions);
        var filtered = await reads.OpenItemsAsync(t.TenantId, Ctx("o3"), new(t.CompanyId, new DateOnly(2026, 5, 1), Ext: [new("crm.dunning", "=", "LEVEL ONE")]));
        Assert.Equal([docs[0]], filtered.Value!.Items.Select(i => i.DocumentId).ToArray());
        // a contains on an open item needs a scope
        Assert.Equal(["READ.CONTAINS_REQUIRES_SCOPE"], Codes(await reads.OpenItemsAsync(t.TenantId, Ctx("o4"), new(t.CompanyId, new DateOnly(2026, 5, 1), Ext: [new("crm.dunning", "contains", "level")]))));
        Assert.Equal([docs[0]], (await reads.OpenItemsAsync(t.TenantId, Ctx("o5"), new(t.CompanyId, new DateOnly(2026, 5, 1), AccountId: account, Ext: [new("crm.dunning", "contains", "level")]))).Value!.Items.Select(i => i.DocumentId).ToArray());
        Assert.Equal(["READ.INVALID_FILTER"], Codes(await reads.OpenItemsAsync(t.TenantId, Ctx("o6"), new(t.CompanyId, Include: ["lines"]))));

        // the master resources that are extendable
        Guid item = Guid.NewGuid();
        await t.ExecuteAsync("insert into core.item_group (tenant_id, item_group_id, code, name, created_by, updated_by) values (@t, gen_random_uuid(), 'G1', 'Group', 'test', 'test')");
        var master = new MasterReads(t.App);
        var groups = MasterReads.InventorySpecs.Single(x => x.Resource == "item-groups");
        Assert.Equal(["READ.INVALID_FILTER"], Codes(await master.ListAsync(t.TenantId, t.Context("m1", "read.config"), groups, new(Include: ["extensions"]))));
        Assert.Equal(["READ.INVALID_FILTER"], Codes(await master.ListAsync(t.TenantId, t.Context("m2", "read.config"), groups, new(Ext: [new("crm.x", "=", "y")]))));
        Assert.Equal(["READ.INVALID_FILTER"], Codes(await master.GetAsync(t.TenantId, t.Context("m3", "read.config"), groups, new(Guid.NewGuid(), ["extensions"]))));
        var items = MasterReads.InventorySpecs.Single(x => x.Resource == "items");
        Assert.True((await master.ListAsync(t.TenantId, t.Context("m4", "read.config"), items, new(Include: ["extensions"]))).Status == CommandStatus.Ok);
        Assert.NotEqual(Guid.Empty, item);
    }
}
