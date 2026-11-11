using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;
using FinanceCore.Core.Posting;

namespace FinanceCore.Data.Tests;

/// <summary>Dimensions (07, 6.2, 6.5, 02, 5.3 and 6): the master commands, the rules of an account at posting, the typed and the free dimensions of a
/// line, and what a reversal copies.</summary>
[Collection("db")]
public class DimensionTests
{
    private static string Describe<T>(CommandResult<T> r) => r.Status + ": " + string.Join(", ", r.Errors.Select(e => $"{e.Code} {e.Path} {string.Join("/", (r.Errors.FirstOrDefault()?.Params ?? new Dictionary<string, object?>()).Select(p => p.Value))}"));
    private static string[] Codes<T>(CommandResult<T> r) => r.Errors.Select(e => e.Code).ToArray();
    private static Dictionary<string, object?> D(params (string, object?)[] f) => f.ToDictionary(x => x.Item1, x => x.Item2);

    private static CommandContext Master(TestTenant t, string key) => t.Context(key, "write.master");
    private static CommandContext Config(TestTenant t, string key) => t.Context(key, "write.config");

    private static Task<Guid> StandardType(TestTenant t, string role) =>
        t.ScalarAsync<Guid>("select dimension_type_id from core.dimension_type where tenant_id = @t and standard_role = @r", ("r", role));

    private static async Task<Guid> FreeType(TestTenant t, string code, string key)
    {
        var r = await new DimensionCommands(t.App).CreateTypeAsync(t.TenantId, Master(t, key), new(D(("type_code", code), ("name", code + " type"))));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        return r.Value!.EntityId;
    }

    private static async Task<Guid> Value(TestTenant t, Guid typeId, string code, string key, params (string, object?)[] extra)
    {
        var fields = D(("dimension_type_id", typeId), ("value_code", code), ("name", code), ("valid_from", "2026-01-01"));
        foreach (var (k, v) in extra) fields[k] = v;
        var r = await new DimensionCommands(t.App).CreateValueAsync(t.TenantId, Master(t, key), new(fields));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        return r.Value!.EntityId;
    }

    private static async Task<MasterResult> Rule(TestTenant t, Guid account, Guid typeId, string rule, string key)
    {
        var r = await new DimensionCommands(t.App).CreateRuleAsync(t.TenantId, Config(t, key), new(t.CompanyId, account, typeId, rule));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        return r.Value!;
    }

    private static PostJournalDocumentRequest Post(TestTenant t, LineDimensions? bank = null, LineDimensions? revenue = null, string date = "2026-03-15") =>
        new(t.CompanyId, null, "JOURNAL", DateOnly.Parse(date), DateOnly.Parse(date), "DKK", "Dimensions", null, null,
            [new RequestLine("1", t.Bank, 1000m, Dimensions: bank), new RequestLine("2", t.Revenue, -1000m, Dimensions: revenue)]);

    private static Task<CommandResult<PostedDocument>> PostAsync(TestTenant t, string key, PostJournalDocumentRequest req) =>
        new JournalDocumentPoster(t.App).PostAsync(t.TenantId, t.Context(key), req);

    // ---------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task EveryTenantHasTheFourStandardTypesAndTheyCannotBeDeactivated()
    {
        var t = await TestTenant.CreateReadyAsync();
        Assert.Equal(4L, await t.ScalarAsync<long>("select count(*) from core.dimension_type where tenant_id = @t and kind = 'STANDARD'"));
        Assert.Equal("COST_CENTER,PROFIT_CENTER,PROJECT,BUSINESS_UNIT".Split(',').Order().ToArray(),
            (await t.ScalarAsync<string>("select string_agg(standard_role, ',' order by standard_role) from core.dimension_type where tenant_id = @t")).Split(','));

        Guid project = await StandardType(t, "PROJECT");
        var commands = new DimensionCommands(t.App);
        var deactivate = await commands.DeactivateTypeAsync(t.TenantId, Master(t, "d1"), new(project, 1, "CLOSED"));
        Assert.Equal(["CONFIG.REFERENCED"], Codes(deactivate));
        var rename = await commands.UpdateTypeAsync(t.TenantId, Master(t, "d2"), new(project, 1, D(("type_code", "PRJ"))));
        Assert.Equal(["CONFIG.IMMUTABLE_AFTER_POSTING"], Codes(rename));
        var name = await commands.UpdateTypeAsync(t.TenantId, Master(t, "d3"), new(project, 1, D(("name", "Projects"))));
        Assert.True(name.Status == CommandStatus.Ok, Describe(name));
    }

    [Fact]
    public async Task AFreeTypeAndItsValuesAreCreatedLoggedAndReplayed()
    {
        var t = await TestTenant.CreateReadyAsync();
        var commands = new DimensionCommands(t.App);
        var created = await commands.CreateTypeAsync(t.TenantId, Master(t, "ty"), new(D(("type_code", "REGION"), ("name", "Region"))));
        Assert.True(created.Status == CommandStatus.Ok, Describe(created));
        Guid region = created.Value!.EntityId;
        Assert.Equal("FREE", await t.ScalarAsync<string>("select kind from core.dimension_type where tenant_id = @t and dimension_type_id = @i", ("i", region)));

        var replay = await commands.CreateTypeAsync(t.TenantId, Master(t, "ty"), new(D(("type_code", "REGION"), ("name", "Region"))));
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(region, replay.Value!.EntityId);
        Assert.Equal(["IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST"], Codes(await commands.CreateTypeAsync(t.TenantId, Master(t, "ty"), new(D(("type_code", "OTHER"), ("name", "x"))))));
        Assert.Equal(["CONFIG.DUPLICATE_CODE"], Codes(await commands.CreateTypeAsync(t.TenantId, Master(t, "ty2"), new(D(("type_code", "REGION"), ("name", "Again"))))));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await commands.CreateTypeAsync(t.TenantId, t.Context("ty3", "write.config"), new(D(("type_code", "X"), ("name", "x"))))));

        Guid north = await Value(t, region, "NORTH", "v1");
        Assert.Equal(["CONFIG.DUPLICATE_CODE"], Codes(await commands.CreateValueAsync(t.TenantId, Master(t, "v2"),
            new(D(("dimension_type_id", region), ("value_code", "NORTH"), ("name", "n"), ("valid_from", "2026-01-01"))))));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await commands.CreateValueAsync(t.TenantId, Master(t, "v3"),
            new(D(("dimension_type_id", Guid.NewGuid()), ("value_code", "X"), ("name", "n"), ("valid_from", "2026-01-01"))))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await commands.CreateValueAsync(t.TenantId, Master(t, "v4"),
            new(D(("dimension_type_id", region), ("value_code", "Y"), ("name", "n"), ("valid_from", "2026-05-01"), ("valid_to", "2026-04-01"))))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await commands.CreateValueAsync(t.TenantId, Master(t, "v5"),
            new(D(("dimension_type_id", region), ("value_code", "Z"), ("name", "n"), ("valid_from", "01/05/2026"))))));

        var update = await commands.UpdateValueAsync(t.TenantId, Master(t, "v6"), new(north, 1, D(("name", "North region"), ("valid_to", "2026-12-31"))));
        Assert.True(update.Status == CommandStatus.Ok, Describe(update));
        Assert.Equal(2L, update.Value!.RowVersion);
        Assert.Equal(["CONFIG.ROW_VERSION_MISMATCH"], Codes(await commands.UpdateValueAsync(t.TenantId, Master(t, "v7"), new(north, 1, D(("name", "x"))))));
        Assert.Equal(["SCHEMA.NO_EFFECTIVE_CHANGE"], Codes(await commands.UpdateValueAsync(t.TenantId, Master(t, "v8"), new(north, 2, D(("name", "North region"))))));

        Assert.Equal(4L, await t.ScalarAsync<long>("select count(*) from core.master_change_log where tenant_id = @t and entity_type in ('DIMENSION_TYPE', 'DIMENSION_VALUE')"));
        var deact = await commands.DeactivateValueAsync(t.TenantId, Master(t, "v9"), new(north, 2, "CLOSED")); Assert.True(deact.Status == CommandStatus.Ok, Describe(deact));
        Assert.Equal(["SCHEMA.NO_EFFECTIVE_CHANGE"], Codes(await commands.DeactivateValueAsync(t.TenantId, Master(t, "v10"), new(north, 3, "CLOSED"))));
    }

    [Fact]
    public async Task TheHierarchyStaysWithinOneTypeAndHasNoCycle()
    {
        var t = await TestTenant.CreateReadyAsync();
        var commands = new DimensionCommands(t.App);
        Guid cc = await StandardType(t, "COST_CENTER"), project = await StandardType(t, "PROJECT");
        Guid top = await Value(t, cc, "TOP", "a");
        Guid child = await Value(t, cc, "CHILD", "b", ("parent_value_id", top));
        Guid other = await Value(t, project, "P1", "c");

        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await commands.CreateValueAsync(t.TenantId, Master(t, "d"),
            new(D(("dimension_type_id", cc), ("value_code", "BAD"), ("name", "x"), ("valid_from", "2026-01-01"), ("parent_value_id", other))))));
        Assert.Equal(["CONFIG.HIERARCHY_CYCLE"], Codes(await commands.UpdateValueAsync(t.TenantId, Master(t, "e"), new(top, 1, D(("parent_value_id", child))))));
        Assert.Equal(["CONFIG.HIERARCHY_CYCLE"], Codes(await commands.UpdateValueAsync(t.TenantId, Master(t, "f"), new(top, 1, D(("parent_value_id", top))))));
    }

    [Fact]
    public async Task ALineCarriesTypedAndFreeDimensionsAndTheyAreStored()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid cc = await StandardType(t, "COST_CENTER"), project = await StandardType(t, "PROJECT");
        Guid region = await FreeType(t, "REGION", "ft"), channel = await FreeType(t, "CHANNEL", "ft2");
        Guid cc1 = await Value(t, cc, "CC1", "v1"), p1 = await Value(t, project, "P1", "v2");
        Guid north = await Value(t, region, "NORTH", "v3"), web = await Value(t, channel, "WEB", "v4");

        var doc = await PostAsync(t, "post", Post(t,
            bank: new LineDimensions(CostCenterId: cc1, ProjectId: p1, Free: [new FreeDimension(region, north), new FreeDimension(channel, web)]),
            revenue: new LineDimensions(CostCenterId: cc1)));
        Assert.True(doc.Status == CommandStatus.Ok, Describe(doc));
        Guid id = doc.Value!.DocumentId;

        Assert.Equal(cc1, await t.ScalarAsync<Guid>("select cost_center_id from core.journal_line where tenant_id = @t and document_id = @d and line_no = 1", ("d", id)));
        Assert.Equal(p1, await t.ScalarAsync<Guid>("select project_id from core.journal_line where tenant_id = @t and document_id = @d and line_no = 1", ("d", id)));
        Assert.Equal(cc1, await t.ScalarAsync<Guid>("select cost_center_id from core.journal_line where tenant_id = @t and document_id = @d and line_no = 2", ("d", id)));
        Assert.Equal(0L, await t.ScalarAsync<long>("select count(*) from core.journal_line where tenant_id = @t and document_id = @d and line_no = 2 and project_id is not null", ("d", id)));
        Assert.Equal(2L, await t.ScalarAsync<long>("select count(*) from core.journal_line_dimension where tenant_id = @t and document_id = @d and line_no = 1", ("d", id)));
        Assert.Equal(north, await t.ScalarAsync<Guid>("select dimension_value_id from core.journal_line_dimension where tenant_id = @t and document_id = @d and dimension_type_id = @ty", ("d", id), ("ty", region)));

        // the chain does not see the dimensions (they are L2): the verification still passes
        var verify = await new ChainVerification(t.App).VerifyAsync(t.TenantId, t.Context("verify", "chain.verify"), new(t.CompanyId, "LEDGER", t.LedgerId.ToString("D"), null, null));
        Assert.True(verify.Status == CommandStatus.Ok, Describe(verify));
    }

    [Fact]
    public async Task TheIdempotencyFingerprintIncludesTheDimensions()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid cc = await StandardType(t, "COST_CENTER");
        Guid a = await Value(t, cc, "A", "v1"), b = await Value(t, cc, "B", "v2");
        var first = await PostAsync(t, "same", Post(t, bank: new LineDimensions(CostCenterId: a)));
        Assert.True(first.Status == CommandStatus.Ok, Describe(first));
        var replay = await PostAsync(t, "same", Post(t, bank: new LineDimensions(CostCenterId: a)));
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(first.Value!.DocumentId, replay.Value!.DocumentId);
        Assert.Equal(["IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST"], Codes(await PostAsync(t, "same", Post(t, bank: new LineDimensions(CostCenterId: b)))));
        Assert.Equal(["IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST"], Codes(await PostAsync(t, "same", Post(t))));
    }

    [Fact]
    public async Task TheRulesOfAnAccountDecideWhetherADimensionIsRequiredOrForbidden()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid cc = await StandardType(t, "COST_CENTER"), project = await StandardType(t, "PROJECT");
        Guid region = await FreeType(t, "REGION", "ft");
        Guid cc1 = await Value(t, cc, "CC1", "v1"), p1 = await Value(t, project, "P1", "v2"), north = await Value(t, region, "NORTH", "v3");

        await Rule(t, t.Revenue, cc, "MANDATORY", "r1");
        await Rule(t, t.Revenue, region, "MANDATORY", "r2");
        await Rule(t, t.Bank, project, "FORBIDDEN", "r3");
        await Rule(t, t.Bank, cc, "OPTIONAL", "r4");

        var missing = await PostAsync(t, "m1", Post(t));
        Assert.Equal(["ACCOUNT_RULE.DIMENSION_REQUIRED", "ACCOUNT_RULE.DIMENSION_REQUIRED"], Codes(missing));
        Assert.All(missing.Errors, e => Assert.Equal("lines[1].dimensions", e.Path));
        Assert.Equal(["COST_CENTER", "REGION"], missing.Errors.Select(e => (string)e.Params!["type_code"]!).Order().ToArray());

        var forbidden = await PostAsync(t, "m2", Post(t, bank: new LineDimensions(ProjectId: p1), revenue: new LineDimensions(CostCenterId: cc1, Free: [new FreeDimension(region, north)])));
        Assert.Equal(["ACCOUNT_RULE.DIMENSION_FORBIDDEN"], Codes(forbidden));
        Assert.Equal("lines[0].dimensions.project_id", forbidden.Errors[0].Path);

        var ok = await PostAsync(t, "m3", Post(t, bank: new LineDimensions(CostCenterId: cc1), revenue: new LineDimensions(CostCenterId: cc1, Free: [new FreeDimension(region, north)])));
        Assert.True(ok.Status == CommandStatus.Ok, Describe(ok));

        // a rule that says MANDATORY and then OPTIONAL: the rule in force counts
        var relax = await new DimensionCommands(t.App).UpdateRuleAsync(t.TenantId, Config(t, "r5"), new(t.CompanyId, t.Revenue, region, 1, "OPTIONAL"));
        Assert.True(relax.Status == CommandStatus.Ok, Describe(relax));
        var withoutRegion = await PostAsync(t, "m4", Post(t, revenue: new LineDimensions(CostCenterId: cc1)));
        Assert.True(withoutRegion.Status == CommandStatus.Ok, Describe(withoutRegion));
    }

    [Fact]
    public async Task AValueMustBeUsableForItsTypeItsDateAndItsCompany()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid cc = await StandardType(t, "COST_CENTER"), project = await StandardType(t, "PROJECT");
        Guid region = await FreeType(t, "REGION", "ft");
        Guid other = Guid.NewGuid();
        await t.ExecuteAsync(
            "insert into core.company (tenant_id, company_id, company_code, legal_name, country_code, local_currency, reporting_currency, fiscal_calendar_id, status) " +
            "select tenant_id, @n, 'C2', 'Other A/S', country_code, local_currency, reporting_currency, fiscal_calendar_id, 'DRAFT' from core.company where tenant_id = @t and company_id = @c",
            ("n", other));

        Guid good = await Value(t, cc, "GOOD", "v1");
        Guid future = await Value(t, cc, "FUTURE", "v2", ("valid_from", "2026-06-01"));
        Guid ended = await Value(t, cc, "ENDED", "v3", ("valid_to", "2026-02-28"));
        Guid closed = await Value(t, cc, "CLOSED", "v4");
        Guid foreign = await Value(t, cc, "FOREIGN", "v5", ("company_id", other));
        Guid p1 = await Value(t, project, "P1", "v6");
        Guid north = await Value(t, region, "NORTH", "v7");
        Assert.True((await new DimensionCommands(t.App).DeactivateValueAsync(t.TenantId, Master(t, "v8"), new(closed, 1, "CLOSED"))).Status == CommandStatus.Ok);

        async Task<(string Code, string Reason, string Path)> Try(string key, LineDimensions d)
        {
            var r = await PostAsync(t, key, Post(t, bank: d));
            Assert.Single(r.Errors);
            return (r.Errors[0].Code, (string)r.Errors[0].Params!["reason"]!, r.Errors[0].Path);
        }

        Assert.Equal(("ACCOUNT_RULE.DIMENSION_VALUE_INVALID", "NOT_FOUND", "lines[0].dimensions.cost_center_id"), await Try("n", new LineDimensions(CostCenterId: Guid.NewGuid())));
        Assert.Equal(("ACCOUNT_RULE.DIMENSION_VALUE_INVALID", "NOT_VALID_AT_DATE", "lines[0].dimensions.cost_center_id"), await Try("f", new LineDimensions(CostCenterId: future)));
        Assert.Equal(("ACCOUNT_RULE.DIMENSION_VALUE_INVALID", "NOT_VALID_AT_DATE", "lines[0].dimensions.cost_center_id"), await Try("e", new LineDimensions(CostCenterId: ended)));
        Assert.Equal(("ACCOUNT_RULE.DIMENSION_VALUE_INVALID", "INACTIVE", "lines[0].dimensions.cost_center_id"), await Try("c", new LineDimensions(CostCenterId: closed)));
        Assert.Equal(("ACCOUNT_RULE.DIMENSION_VALUE_INVALID", "COMPANY_MISMATCH", "lines[0].dimensions.cost_center_id"), await Try("o", new LineDimensions(CostCenterId: foreign)));
        Assert.Equal(("ACCOUNT_RULE.DIMENSION_VALUE_INVALID", "TYPE_MISMATCH", "lines[0].dimensions.cost_center_id"), await Try("t", new LineDimensions(CostCenterId: p1)));
        Assert.Equal(("ACCOUNT_RULE.DIMENSION_VALUE_INVALID", "TYPE_MISMATCH", "lines[0].dimensions.free[0].dimension_value_id"), await Try("x", new LineDimensions(Free: [new FreeDimension(region, p1)])));
        Assert.Equal(("ACCOUNT_RULE.DIMENSION_VALUE_INVALID", "TYPE_NOT_FREE", "lines[0].dimensions.free[0].dimension_type_id"), await Try("s", new LineDimensions(Free: [new FreeDimension(cc, good)])));
        Assert.Equal(("ACCOUNT_RULE.DIMENSION_VALUE_INVALID", "TYPE_NOT_FOUND", "lines[0].dimensions.free[0].dimension_type_id"), await Try("u", new LineDimensions(Free: [new FreeDimension(Guid.NewGuid(), north)])));
        Assert.Equal(("ACCOUNT_RULE.DIMENSION_VALUE_INVALID", "DUPLICATE_TYPE", "lines[0].dimensions.free[1].dimension_type_id"),
            await Try("d", new LineDimensions(Free: [new FreeDimension(region, north), new FreeDimension(region, north)])));

        // a date inside the validity of the value is fine
        Assert.True((await PostAsync(t, "ok", Post(t, bank: new LineDimensions(CostCenterId: future), date: "2026-06-15"))).Status == CommandStatus.Ok);
        // a nil id is a shape error of the request
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await PostAsync(t, "nil", Post(t, bank: new LineDimensions(CostCenterId: Guid.Empty)))));
    }

    [Fact]
    public async Task AReversalCopiesTheDimensionsOfTheLinesItReverses()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid cc = await StandardType(t, "COST_CENTER");
        Guid region = await FreeType(t, "REGION", "ft");
        Guid cc1 = await Value(t, cc, "CC1", "v1"), north = await Value(t, region, "NORTH", "v2");
        var doc = await PostAsync(t, "post", Post(t, bank: new LineDimensions(CostCenterId: cc1, Free: [new FreeDimension(region, north)]), revenue: new LineDimensions(CostCenterId: cc1)));
        Assert.True(doc.Status == CommandStatus.Ok, Describe(doc));

        // the value is closed after posting: the reversal must still go through, and it copies what the lines carry
        Assert.True((await new DimensionCommands(t.App).DeactivateValueAsync(t.TenantId, Master(t, "close"), new(cc1, 1, "CLOSED"))).Status == CommandStatus.Ok);
        var reversal = await new DocumentReverser(t.App).ReverseAsync(t.TenantId, t.Context("rev", "journal.reverse"), new(doc.Value!.DocumentId, null, "CORRECTION", null));
        Assert.True(reversal.Status == CommandStatus.Ok, Describe(reversal));
        Guid mirror = reversal.Value!.DocumentId;

        Assert.Equal(2L, await t.ScalarAsync<long>("select count(*) from core.journal_line where tenant_id = @t and document_id = @d and cost_center_id = @v", ("d", mirror), ("v", cc1)));
        Assert.Equal(north, await t.ScalarAsync<Guid>("select dimension_value_id from core.journal_line_dimension where tenant_id = @t and document_id = @d and line_no = 1", ("d", mirror)));
    }

    // ---------------------------------------------------------------------------------------------------------
    // The L2 change of a dimension (06, 5.4)
    // ---------------------------------------------------------------------------------------------------------
    private static Task<CommandResult<ChangedDocument>> Change(TestTenant t, string key, Guid document, params FieldChange[] changes) =>
        new DocumentChanger(t.App).ChangeAsync(t.TenantId, t.Context(key, "journal.change"), new ChangeOpenFieldsRequest(document, changes, "CORRECTION", null));

    [Fact]
    public async Task ATypedDimensionChangesWithItsLogRowAndIsCheckedLikeAPosting()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid cc = await StandardType(t, "COST_CENTER"), project = await StandardType(t, "PROJECT");
        Guid cc1 = await Value(t, cc, "CC1", "v1"), cc2 = await Value(t, cc, "CC2", "v2"), p1 = await Value(t, project, "P1", "v3");
        Guid later = await Value(t, cc, "LATER", "v4", ("valid_from", "2026-09-01"));
        var doc = await PostAsync(t, "post", Post(t));
        Guid id = doc.Value!.DocumentId;

        var set = await Change(t, "c1", id, new FieldChange("LINE", 1, "cost_center_id", null, cc1.ToString()));
        Assert.True(set.Status == CommandStatus.Ok, Describe(set));
        Assert.Equal(cc1, await t.ScalarAsync<Guid>("select cost_center_id from core.journal_line where tenant_id = @t and document_id = @d and line_no = 1", ("d", id)));
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.change_log where tenant_id = @t and entity_type = 'LINE' and field_name = 'cost_center_id' and value_type = 'ID' and new_value = @v", ("v", cc1.ToString())));

        var replay = await Change(t, "c1", id, new FieldChange("LINE", 1, "cost_center_id", null, cc1.ToString()));
        Assert.True(replay.IdempotentReplay);

        // compare and set, and the new value is checked at the posting date of the line
        Assert.Equal(["CONFLICT.EXPECTED_VALUE_MISMATCH"], Codes(await Change(t, "c2", id, new FieldChange("LINE", 1, "cost_center_id", null, cc2.ToString()))));
        var swap = await Change(t, "c3", id, new FieldChange("LINE", 1, "cost_center_id", cc1.ToString(), cc2.ToString()));
        Assert.True(swap.Status == CommandStatus.Ok, Describe(swap));
        var notYet = await Change(t, "c4", id, new FieldChange("LINE", 1, "cost_center_id", cc2.ToString(), later.ToString()));
        Assert.Equal(["ACCOUNT_RULE.DIMENSION_VALUE_INVALID"], Codes(notYet));
        Assert.Equal("NOT_VALID_AT_DATE", notYet.Errors[0].Params!["reason"]);
        Assert.Equal("/changes/0/new_value", notYet.Errors[0].Path);
        var wrongType = await Change(t, "c5", id, new FieldChange("LINE", 1, "cost_center_id", cc2.ToString(), p1.ToString()));
        Assert.Equal("TYPE_MISMATCH", wrongType.Errors[0].Params!["reason"]);

        var clear = await Change(t, "c6", id, new FieldChange("LINE", 1, "cost_center_id", cc2.ToString(), null));
        Assert.True(clear.Status == CommandStatus.Ok, Describe(clear));
        Assert.Equal(0L, await t.ScalarAsync<long>("select count(*) from core.journal_line where tenant_id = @t and document_id = @d and cost_center_id is not null", ("d", id)));
        Assert.Equal(3L, await t.ScalarAsync<long>("select count(*) from core.change_log where tenant_id = @t and entity_type = 'LINE' and field_name = 'cost_center_id'"));
    }

    [Fact]
    public async Task TheRulesOfTheAccountHoldForAChangeToo()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid cc = await StandardType(t, "COST_CENTER"), project = await StandardType(t, "PROJECT");
        Guid cc1 = await Value(t, cc, "CC1", "v1"), p1 = await Value(t, project, "P1", "v2");
        var doc = await PostAsync(t, "post", Post(t, revenue: new LineDimensions(CostCenterId: cc1)));
        Guid id = doc.Value!.DocumentId;
        // the rules come after the posting, so the posting itself is free of them
        await Rule(t, t.Revenue, cc, "MANDATORY", "r1");
        await Rule(t, t.Revenue, project, "FORBIDDEN", "r2");

        var clear = await Change(t, "c1", id, new FieldChange("LINE", 2, "cost_center_id", cc1.ToString(), null));
        Assert.Equal(["ACCOUNT_RULE.DIMENSION_REQUIRED"], Codes(clear));
        var forbidden = await Change(t, "c2", id, new FieldChange("LINE", 2, "project_id", null, p1.ToString()));
        Assert.Equal(["ACCOUNT_RULE.DIMENSION_FORBIDDEN"], Codes(forbidden));
        Assert.Equal(cc1, await t.ScalarAsync<Guid>("select cost_center_id from core.journal_line where tenant_id = @t and document_id = @d and line_no = 2", ("d", id)));
        Assert.Equal(0L, await t.ScalarAsync<long>("select count(*) from core.change_log where tenant_id = @t and entity_type = 'LINE'"));

        // the other line has no rule and takes the project
        var ok = await Change(t, "c3", id, new FieldChange("LINE", 1, "project_id", null, p1.ToString()));
        Assert.True(ok.Status == CommandStatus.Ok, Describe(ok));
    }

    [Fact]
    public async Task AFreeDimensionIsAddedChangedAndClearedWithoutDeletingTheRow()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid cc = await StandardType(t, "COST_CENTER");
        Guid region = await FreeType(t, "REGION", "ft");
        Guid north = await Value(t, region, "NORTH", "v1"), south = await Value(t, region, "SOUTH", "v2"), cc1 = await Value(t, cc, "CC1", "v3");
        var doc = await PostAsync(t, "post", Post(t));
        Guid id = doc.Value!.DocumentId;

        FieldChange Free(string? old, string? @new) => new("LINE_DIMENSION", 1, "dimension_value_id", old, @new, region.ToString());
        var add = await Change(t, "c1", id, Free(null, north.ToString()));
        Assert.True(add.Status == CommandStatus.Ok, Describe(add));
        Assert.Equal(region.ToString(), add.Value!.Changed.Single().SubKey);
        Assert.Equal(north, await t.ScalarAsync<Guid>("select dimension_value_id from core.journal_line_dimension where tenant_id = @t and document_id = @d and line_no = 1", ("d", id)));

        Assert.True((await Change(t, "c2", id, Free(north.ToString(), south.ToString()))).Status == CommandStatus.Ok);
        Assert.Equal(["CONFLICT.EXPECTED_VALUE_MISMATCH"], Codes(await Change(t, "c3", id, Free(north.ToString(), null))));
        Assert.True((await Change(t, "c4", id, Free(south.ToString(), null))).Status == CommandStatus.Ok);
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.journal_line_dimension where tenant_id = @t and document_id = @d and dimension_value_id is null", ("d", id)));   // a tombstone
        Assert.True((await Change(t, "c5", id, Free(null, north.ToString()))).Status == CommandStatus.Ok);   // the tombstone takes a value again
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.journal_line_dimension where tenant_id = @t and document_id = @d", ("d", id)));
        Assert.Equal(4L, await t.ScalarAsync<long>("select count(*) from core.change_log where tenant_id = @t and entity_type = 'LINE_DIMENSION' and sub_key = @s", ("s", region.ToString())));

        // a standard type is a typed column, not a free dimension; the sub_key is a type id; the field is fixed
        var standard = await Change(t, "c6", id, new FieldChange("LINE_DIMENSION", 1, "dimension_value_id", null, cc1.ToString(), cc.ToString()));
        Assert.Equal("TYPE_NOT_FREE", standard.Errors[0].Params!["reason"]);
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await Change(t, "c7", id, new FieldChange("LINE_DIMENSION", 1, "dimension_value_id", null, north.ToString(), "not-an-id"))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await Change(t, "c8", id, new FieldChange("LINE_DIMENSION", 1, "dimension_value_id", null, north.ToString()))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await Change(t, "c9", id, new FieldChange("LINE_DIMENSION", 1, "line_text", null, "x", region.ToString()))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await Change(t, "c10", id, new FieldChange("LINE", 1, "line_text", null, "x", region.ToString()))));
        Assert.Equal(["ACCOUNT_RULE.DIMENSION_VALUE_INVALID"], Codes(await Change(t, "c11", id, new FieldChange("LINE_DIMENSION", 1, "dimension_value_id", north.ToString(), south.ToString(), Guid.NewGuid().ToString()))));

        // L3: a hard-closed period rejects a change of a dimension as it rejects every other
        await t.SetPeriodStatusAsync(3, "HARD_CLOSED");
        Assert.Equal(["LOCK.FIELD_LOCKED_L3"], Codes(await Change(t, "c12", id, Free(north.ToString(), south.ToString()))));
    }

    [Fact]
    public async Task ARuleNeedsItsAccountAndTypeAndIsKeyedByBoth()
    {
        var t = await TestTenant.CreateReadyAsync();
        var commands = new DimensionCommands(t.App);
        Guid cc = await StandardType(t, "COST_CENTER");
        var created = await Rule(t, t.Revenue, cc, "MANDATORY", "r1");
        Assert.Equal(1L, created.RowVersion);

        Assert.Equal(["CONFIG.DUPLICATE_CODE"], Codes(await commands.CreateRuleAsync(t.TenantId, Config(t, "r2"), new(t.CompanyId, t.Revenue, cc, "FORBIDDEN"))));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await commands.CreateRuleAsync(t.TenantId, Config(t, "r3"), new(t.CompanyId, Guid.NewGuid(), cc, "FORBIDDEN"))));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await commands.CreateRuleAsync(t.TenantId, Config(t, "r4"), new(t.CompanyId, t.Bank, Guid.NewGuid(), "FORBIDDEN"))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await commands.CreateRuleAsync(t.TenantId, Config(t, "r5"), new(t.CompanyId, t.Bank, cc, "SOMETIMES"))));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await commands.CreateRuleAsync(t.TenantId, Master(t, "r6"), new(t.CompanyId, t.Bank, cc, "FORBIDDEN"))));
        Assert.Equal(["CONFIG.ROW_VERSION_MISMATCH"], Codes(await commands.UpdateRuleAsync(t.TenantId, Config(t, "r7"), new(t.CompanyId, t.Revenue, cc, 5, "FORBIDDEN"))));
        Assert.Equal(["SCHEMA.NO_EFFECTIVE_CHANGE"], Codes(await commands.UpdateRuleAsync(t.TenantId, Config(t, "r8"), new(t.CompanyId, t.Revenue, cc, 1, "MANDATORY"))));
        var update = await commands.UpdateRuleAsync(t.TenantId, Config(t, "r9"), new(t.CompanyId, t.Revenue, cc, 1, "FORBIDDEN"));
        Assert.True(update.Status == CommandStatus.Ok, Describe(update));
        Assert.Equal(2L, update.Value!.RowVersion);

        var replay = await commands.UpdateRuleAsync(t.TenantId, Config(t, "r9"), new(t.CompanyId, t.Revenue, cc, 1, "FORBIDDEN"));
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(2L, replay.Value!.RowVersion);
        Assert.Equal(2L, await t.ScalarAsync<long>("select count(*) from core.master_change_log where tenant_id = @t and entity_type = 'ACCOUNT_DIMENSION_RULE'"));
    }

    [Fact]
    public async Task TheAccountOfRetainedEarningsTakesNoMandatoryDimension()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid cc = await StandardType(t, "COST_CENTER");
        Guid retained = await t.ScalarAsync<Guid>("select account_id from core.gl_account where tenant_id = @t and account_no = '8100'");

        var mandatory = await new DimensionCommands(t.App).CreateRuleAsync(t.TenantId, Config(t, "r1"), new(t.CompanyId, retained, cc, "MANDATORY"));
        Assert.Equal(["CONFIG.ROLE_ACCOUNT_RULE_VIOLATED"], Codes(mandatory));
        Assert.Equal("MANDATORY_DIMENSION", mandatory.Errors[0].Params!["rule"]);
        Assert.True((await new DimensionCommands(t.App).CreateRuleAsync(t.TenantId, Config(t, "r2"), new(t.CompanyId, retained, cc, "FORBIDDEN"))).Status == CommandStatus.Ok);

        // the other direction: a role is not given to an account that has a mandatory rule
        Assert.True((await new DimensionCommands(t.App).CreateRuleAsync(t.TenantId, Config(t, "r3"), new(t.CompanyId, t.Bank, cc, "MANDATORY"))).Status == CommandStatus.Ok);
        var assign = await new FinanceCore.Data.AccountRoleAssigner(t.App).AssignAsync(t.TenantId, Config(t, "r4"), new(t.CompanyId, "HISTORY_RESULT", t.Bank));
        Assert.Equal(["CONFIG.ROLE_ACCOUNT_RULE_VIOLATED"], Codes(assign));
        Assert.Equal("MANDATORY_DIMENSION", assign.Errors[0].Params!["rule"]);
    }
}
