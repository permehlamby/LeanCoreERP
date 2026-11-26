using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;
using FinanceCore.Core.Posting;
using FinanceCore.Core.Reads;

namespace FinanceCore.Data.Tests;

/// <summary>The rest of 09 (F-IMPL-90): several companies and groups in one read, the operator contains, business-partners:search, issue_state on journal documents, and the jobs of the read access log.</summary>
[Collection("db")]
public class ReadCompletionTests
{
    private static string Describe<T>(CommandResult<T> r) => r.Status + ": " + string.Join(", ", r.Errors.Select(e => $"{e.Code} {e.Path}"));
    private static string[] Codes<T>(CommandResult<T> r) => r.Errors.Select(e => e.Code).ToArray();
    private static Dictionary<string, object?> D(params (string, object?)[] f) => f.ToDictionary(x => x.Item1, x => x.Item2);
    private static Dictionary<string, string> S(params (string, string)[] f) => f.ToDictionary(x => x.Item1, x => x.Item2);

    private static async Task<Guid> PostAsync(TestTenant t, Guid company, string key, Guid debit, Guid credit, decimal amount, string? header = null, string date = "2026-03-15", string? external = null)
    {
        var r = await new JournalDocumentPoster(t.App).PostAsync(t.TenantId, t.Context(key), new PostJournalDocumentRequest(company, null, "JOURNAL", DateOnly.Parse(date), DateOnly.Parse(date), "DKK", header, external, null,
            [new RequestLine("1", debit, amount), new RequestLine("2", credit, -amount)]));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        return r.Value!.DocumentId;
    }

    // -------------------------------------------------------------------------------------------------------------
    // several companies and groups (09, 4.1.2, 4.1.3)
    // -------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task BalancesAndAgingReadSeveralCompaniesAndAGroupAndNeverAddAcrossCurrencies()
    {
        var t = await TestTenant.CreateReadyAsync();
        var s = await IntercompanyTests.AddSecond(t);
        await PostAsync(t, t.CompanyId, "p1", t.Bank, t.Revenue, 1000m);
        await PostAsync(t, s.CompanyId, "p2", s.Bank, s.Expense, 300m);
        var reads = new LedgerReads(t.App);
        CommandContext Cross(string key, params string[] more) => t.Context(key, ["read.ledger", "read.cross_company", .. more]);
        var asOf = new DateOnly(2026, 6, 30);

        // two companies with one currency combination: the rows are added, the response names both companies and their ledgers
        var both = await reads.BalancesAsync(t.TenantId, Cross("b1"), new(Guid.Empty, "CUMULATIVE", AsOfDate: asOf, CompanyIds: [t.CompanyId, s.CompanyId]));
        Assert.True(both.Status == CommandStatus.Ok, Describe(both));
        var row = Assert.Single(both.Value!.Rows);
        Assert.Equal((1300m, -1300m, 4L), (row.Debit.Lc, row.Credit.Lc, row.LineCount));
        Assert.Equal(2, both.Value.Companies!.Count);
        Assert.Equal(2, both.Value.Snapshot.Ledgers.Count);
        Assert.NotNull(both.Value.Currencies);
        Assert.Equal(1300m, both.Value.TotalDebit!.Lc);
        Assert.Single(both.Value.TotalsByCurrency!);

        // company_id as a key keeps the companies apart
        var byCompany = await reads.BalancesAsync(t.TenantId, Cross("b2"), new(Guid.Empty, "CUMULATIVE", AsOfDate: asOf, GroupBy: ["company_id"], CompanyIds: [t.CompanyId, s.CompanyId]));
        Assert.Equal([1000m, 300m], byCompany.Value!.Rows.Select(r => r.Debit.Lc).Order().Reverse().ToArray());
        Assert.Equal(2, byCompany.Value.Rows.Select(r => r.Keys["company_id"]).Distinct().Count());

        // a group: its members at the date
        Guid group = Guid.Parse(await t.ScalarAsync<string>("select group_id::text from core.group_membership where tenant_id = @t and company_id = @c", ("c", t.CompanyId)));
        var byGroup = await reads.BalancesAsync(t.TenantId, Cross("b3"), new(Guid.Empty, "CUMULATIVE", AsOfDate: asOf, GroupId: group));
        Assert.True(byGroup.Status == CommandStatus.Ok, Describe(byGroup));
        Assert.Equal((group, 2), (byGroup.Value!.Group!.GroupId, byGroup.Value.Group.CompanyIds.Count));
        Assert.Equal(1300m, Assert.Single(byGroup.Value.Rows).Debit.Lc);
        var beforeMembership = await reads.BalancesAsync(t.TenantId, Cross("b4"), new(Guid.Empty, "CUMULATIVE", AsOfDate: new DateOnly(2025, 12, 31), GroupId: group));
        Assert.Empty(beforeMembership.Value!.Group!.CompanyIds);
        Assert.Empty(beforeMembership.Value.Rows);

        // companies with different local currencies: the same key gives one row per currency combination and there is no single total
        await t.ExecuteAsync("set session_replication_role = replica; update core.company set local_currency = 'SEK' where tenant_id = @t and company_id = @co; set session_replication_role = origin", ("co", s.CompanyId));
        var split = await reads.BalancesAsync(t.TenantId, Cross("b5"), new(Guid.Empty, "CUMULATIVE", AsOfDate: asOf, CompanyIds: [t.CompanyId, s.CompanyId]));
        Assert.True(split.Status == CommandStatus.Ok, Describe(split));
        Assert.Equal(2, split.Value!.Rows.Count);
        Assert.Equal(["DKK", "SEK"], split.Value.Rows.Select(r => r.Currencies!.Lc).ToArray());
        Assert.Null(split.Value.Currencies);
        Assert.Null(split.Value.TotalDebit);
        Assert.Equal(2, split.Value.TotalsByCurrency!.Count);
        var aging = await reads.AgingAsync(t.TenantId, Cross("g1"), new(Guid.Empty, asOf, CompanyIds: [t.CompanyId, s.CompanyId]));
        Assert.True(aging.Status == CommandStatus.Ok, Describe(aging));
        Assert.Equal(2, aging.Value!.GrandTotals!.Count);
        Assert.Null(aging.Value.GrandTotal);
        Assert.Equal(2, aging.Value.Companies!.Count);

        // the rules of the choice
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await reads.BalancesAsync(t.TenantId, t.Context("b6", "read.ledger"), new(Guid.Empty, "CUMULATIVE", AsOfDate: asOf, CompanyIds: [t.CompanyId, s.CompanyId]))));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await reads.AgingAsync(t.TenantId, t.Context("g2", "read.ledger"), new(Guid.Empty, asOf, GroupId: group))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await reads.BalancesAsync(t.TenantId, Cross("b7"), new(t.CompanyId, "CUMULATIVE", AsOfDate: asOf, CompanyIds: [t.CompanyId, s.CompanyId]))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await reads.BalancesAsync(t.TenantId, Cross("b8"), new(Guid.Empty, "CUMULATIVE", AsOfDate: asOf, CompanyIds: [t.CompanyId, t.CompanyId]))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await reads.BalancesAsync(t.TenantId, Cross("b9"), new(Guid.Empty, "CUMULATIVE", AsOfDate: asOf, CompanyIds: []))));
        Assert.Equal(["READ.NOT_FOUND"], Codes(await reads.BalancesAsync(t.TenantId, Cross("b10"), new(Guid.Empty, "CUMULATIVE", AsOfDate: asOf, GroupId: Guid.NewGuid()))));
        Assert.Equal(["READ.NOT_FOUND"], Codes(await reads.BalancesAsync(t.TenantId, Cross("b11"), new(Guid.Empty, "CUMULATIVE", AsOfDate: asOf, CompanyIds: [t.CompanyId, Guid.NewGuid()]))));
        // one company in the list needs no cross-company permission
        Assert.Equal(CommandStatus.Ok, (await reads.BalancesAsync(t.TenantId, t.Context("b12", "read.ledger"), new(Guid.Empty, "CUMULATIVE", AsOfDate: asOf, CompanyIds: [t.CompanyId]))).Status);
    }

    // -------------------------------------------------------------------------------------------------------------
    // contains (09, 2.4.1)
    // -------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task ContainsIsALiteralCaseAndAccentInsensitiveSubstringWithAScopeOnTheLedger()
    {
        var t = await TestTenant.CreateReadyAsync();
        await PostAsync(t, t.CompanyId, "c1", t.Bank, t.Revenue, 100m, "Café Zürich faktura", external: "EXT-100%-A");
        await PostAsync(t, t.CompanyId, "c2", t.Bank, t.Revenue, 200m, "Regning   Odense", external: "EXT-100-B");
        await PostAsync(t, t.CompanyId, "c3", t.Bank, t.Revenue, 300m, "Æble_x kreditnota", "2026-01-10");
        var reads = new LedgerReads(t.App);
        Task<CommandResult<JournalDocumentList>> List(ListJournalDocumentsRequest req) => reads.ListDocumentsAsync(t.TenantId, t.Context("l" + Guid.NewGuid().ToString("N")[..6], "read.ledger"), req);
        var year = new DateOnly(2026, 2, 1); var end = new DateOnly(2026, 12, 31);
        ListJournalDocumentsRequest Q(params (string, string)[] contains) => new(t.CompanyId, PostingDateFrom: year, PostingDateTo: end, Contains: S(contains));

        // case and accents do not matter, white space is collapsed, the match is anywhere in the text
        Assert.Equal("Café Zürich faktura", Assert.Single((await List(Q(("header_text", "CAFE zurich")))).Value!.Items).HeaderText);
        Assert.Single((await List(Q(("header_text", "regning odense")))).Value!.Items);
        Assert.Single((await List(Q(("header_text", "ürich fak")))).Value!.Items);
        // the value is a literal: a percent sign and an underscore are characters, not wildcards
        Assert.Equal("EXT-100%-A", Assert.Single((await List(Q(("external_reference", "100%-")))).Value!.Items).ExternalReference);
        Assert.Empty((await List(Q(("external_reference", "1%0-")))).Value!.Items);
        Assert.Empty((await List(Q(("external_reference", "EXT_100")))).Value!.Items);
        Assert.Empty((await List(Q(("header_text", "Reg_ing")))).Value!.Items);
        // the fields are ANDed with each other and with the other filters
        Assert.Single((await List(Q(("header_text", "Zürich"), ("external_reference", "100%")))).Value!.Items);
        Assert.Empty((await List(Q(("header_text", "Zürich"), ("external_reference", "-100-B")))).Value!.Items);
        Assert.Empty((await List(Q(("header_text", "Zürich")) with { Status = "reversed" })).Value!.Items);
        // a text before the date range is not found inside it, and found with a scope that holds it
        Assert.Empty((await List(Q(("header_text", "kreditnota")))).Value!.Items);
        Assert.Single((await List(new(t.CompanyId, PostingDateFrom: new DateOnly(2026, 1, 1), PostingDateTo: new DateOnly(2026, 1, 31), Contains: S(("header_text", "KREDITNOTA"))))).Value!.Items);
        Assert.Single((await List(new(t.CompanyId, NumberPrefix: "GL-2026", Contains: S(("header_text", "kreditnota"))))).Value!.Items);
        Assert.Single((await List(new(t.CompanyId, AccountId: t.Revenue, Contains: S(("header_text", "odense"))))).Value!.Items);

        // the guard rails
        Assert.Equal(["READ.CONTAINS_TOO_SHORT"], Codes(await List(Q(("header_text", "ab")))));
        Assert.Equal(["READ.CONTAINS_TOO_SHORT"], Codes(await List(Q(("header_text", "  ab    ")))));
        Assert.Equal(["READ.CONTAINS_REQUIRES_SCOPE"], Codes(await List(new(t.CompanyId, Contains: S(("header_text", "faktura"))))));
        Assert.Equal(["READ.CONTAINS_REQUIRES_SCOPE"], Codes(await List(new(t.CompanyId, PostingDateFrom: new DateOnly(2025, 1, 1), PostingDateTo: new DateOnly(2026, 12, 31), Contains: S(("header_text", "faktura"))))));
        Assert.Equal(["READ.INVALID_FILTER"], Codes(await List(Q(("posted_by", "tester")))));
    }

    [Fact]
    public async Task ContainsSearchesNameAndDescriptionOfAMasterResource()
    {
        var t = await TestTenant.CreateReadyAsync();
        foreach ((string code, string name) in new[] { ("G1", "Réservedele Østjylland"), ("G2", "Arbejdstøj"), ("G3", "Reserve 100%") })
            await t.ExecuteAsync("insert into core.item_group (tenant_id, item_group_id, code, name, created_by, updated_by) values (@t, gen_random_uuid(), @cd, @nm, 'test', 'test')", ("cd", code), ("nm", name));
        var reads = new MasterReads(t.App);
        var spec = MasterReads.InventorySpecs.Single(x => x.Resource == "item-groups");
        Task<CommandResult<MasterRowList>> List(params (string, string)[] contains) => reads.ListAsync(t.TenantId, t.Context("m" + Guid.NewGuid().ToString("N")[..6], "read.config"), spec, new(Contains: S(contains)));

        Assert.Equal(["G1"], (await List(("name", "RESERVEDELE"))).Value!.Items.Select(i => i.GetProperty("code").GetString()!).ToArray());
        Assert.Equal(["G1", "G3"], (await List(("name", "reserve"))).Value!.Items.Select(i => i.GetProperty("code").GetString()!).ToArray());
        Assert.Equal(["G3"], (await List(("name", "100%"))).Value!.Items.Select(i => i.GetProperty("code").GetString()!).ToArray());
        Assert.Empty((await List(("name", "1%0"))).Value!.Items);
        Assert.Equal(["READ.CONTAINS_TOO_SHORT"], Codes(await List(("name", "re"))));
        Assert.Equal(["READ.INVALID_FILTER"], Codes(await List(("code", "G1G"))));
        // item_group has no description column
        Assert.Equal(["READ.INVALID_FILTER"], Codes(await List(("description", "reserve"))));
    }

    [Fact]
    public async Task TheNormalisationOfASearchIsTheSameInTheDatabaseAsTheSpecSays()
    {
        var t = await TestTenant.CreateReadyAsync();
        Assert.Equal("cafe zurich", await t.ScalarAsync<string>("select core.search_norm('  CAFÉ   Zürich ')"));
        Assert.Equal("ærø", await t.ScalarAsync<string>("select core.search_norm('Ærø')"));
        // NFKD turns the full-width percent sign into a percent sign: the pattern escapes it after the normalisation, so it stays a literal
        Assert.Equal(@"%100\%%", await t.ScalarAsync<string>("select core.search_pattern(U&'100\\FF05')"));
        Assert.Equal(@"%a\_b\\c%", await t.ScalarAsync<string>(@"select core.search_pattern('a_b\c')"));
    }

    // -------------------------------------------------------------------------------------------------------------
    // business-partners:search (09, 5.3)
    // -------------------------------------------------------------------------------------------------------------
    private static async Task<Guid> PartnerAsync(TestTenant t, string key, string code, string name, string city, string? email = null)
    {
        var pii = D(("legal_name", name), ("address_line_1", "Havnegade 1"), ("postal_code", "1058"), ("city", city), ("country_code", "DK"));
        if (email is not null) pii["email"] = email;
        var r = await new PartnerCommands(t.App).CreateAsync(t.TenantId, t.Context(key, "write.master"), new CreatePartnerRequest(
            D(("partner_code", code), ("partner_kind", "ORGANISATION"), ("is_customer", true)), pii, [new PartnerRelationInput(t.CompanyId, D(("is_customer", true)))]));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        return r.Value!.EntityId;
    }

    [Fact]
    public async Task APartnerSearchFindsByNameAndAddressNeedsPiiAndIsLogged()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid a = await PartnerAsync(t, "a", "C-A", "Café Møller A/S", "København K", "post@moller.dk");
        Guid b = await PartnerAsync(t, "b", "C-B", "Nordisk Handel ApS", "Aarhus C");
        Guid c = await PartnerAsync(t, "c", "C-C", "Cafe Zürich GmbH", "Zürich");
        var reads = new PartnerReads(t.App);
        CommandContext Pii(string key) => t.Context(key, "read.config", "read.pii");
        Task<CommandResult<PartnerList>> Search(SearchPartnersRequest req, string key = "s") => reads.SearchAsync(t.TenantId, Pii(key + Guid.NewGuid().ToString("N")[..6]), req);

        var cafe = (await Search(new(Contains: S(("legal_name", "CAFE"))))).Value!;
        Assert.Equal(["C-A", "C-C"], cafe.Items.Select(p => p.PartnerCode).ToArray());
        Assert.All(cafe.Items, p => Assert.NotNull(p.Pii));
        Assert.Equal(["C-A"], (await Search(new(Contains: S(("email", "MOLLER.dk"))))).Value!.Items.Select(p => p.PartnerCode).ToArray());
        Assert.Equal(["C-C"], (await Search(new(Contains: S(("city", "zurich"), ("legal_name", "gmbh"))))).Value!.Items.Select(p => p.PartnerCode).ToArray());
        Assert.Equal(["C-B"], (await Search(new(Prefix: S(("city", "AARHUS"))))).Value!.Items.Select(p => p.PartnerCode).ToArray());
        Assert.Empty((await Search(new(Prefix: S(("city", "arhus"))))).Value!.Items);
        Assert.Equal(["C-A"], (await Search(new SearchPartnersRequest(Contains: S(("legal_name", "cafe")), Prefix: S(("postal_code", "10")), PartnerKind: "ORGANISATION", IsCustomer: true, Status: "ACTIVE", IsSupplier: false)))
            .Value!.Items.Take(1).Select(p => p.PartnerCode).ToArray());
        Assert.Empty((await Search(new(Contains: S(("legal_name", "cafe")), IsSupplier: true))).Value!.Items);

        // a page and its cursor
        var p1 = (await Search(new(Contains: S(("legal_name", "cafe")), Limit: 1))).Value!;
        Assert.Equal(["C-A"], p1.Items.Select(p => p.PartnerCode).ToArray());
        var p2 = (await Search(new(Contains: S(("legal_name", "cafe")), Limit: 1, Cursor: p1.Page.NextCursor))).Value!;
        Assert.Equal(["C-C"], p2.Items.Select(p => p.PartnerCode).ToArray());
        Assert.Null(p2.Page.NextCursor);
        Assert.Equal(["READ.CURSOR_INVALID"], Codes(await Search(new(Contains: S(("legal_name", "moller")), Limit: 1, Cursor: p1.Page.NextCursor))));

        // the log: SEARCH for every partner found and the groups of the fields returned, and one row for a search that finds nothing
        await Search(new(Contains: S(("legal_name", "cafe"))), "log");
        Assert.Equal(2L, await t.ScalarAsync<long>("select count(*) from core.read_access_log where tenant_id = @t and operation = 'business-partners:search' and field_group = 'SEARCH' and partner_id is not null and correlation_id like 'corr-log%'"));
        Assert.Equal(2L, await t.ScalarAsync<long>("select count(*) from core.read_access_log where tenant_id = @t and operation = 'business-partners:search' and field_group = 'PII_IDENTITY' and correlation_id like 'corr-log%'"));
        await Search(new(Contains: S(("legal_name", "nothing here"))), "none");
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.read_access_log where tenant_id = @t and field_group = 'SEARCH' and partner_id is null and correlation_id like 'corr-none%'"));

        // a restricted partner is not found without the statutory purpose
        await t.ExecuteAsync("update core.business_partner set restriction_status = 'RESTRICTED' where tenant_id = @t and partner_id = @p", ("p", c));
        Assert.Equal(["C-A"], (await Search(new(Contains: S(("legal_name", "cafe"))))).Value!.Items.Select(p => p.PartnerCode).ToArray());
        Assert.Equal(["C-A", "C-C"], (await Search(new(Contains: S(("legal_name", "cafe")), PurposeCode: PartnerReadRules.StatutoryPurpose))).Value!.Items.Select(p => p.PartnerCode).ToArray());

        // the errors
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await reads.SearchAsync(t.TenantId, t.Context("n1", "read.config"), new(Contains: S(("legal_name", "cafe"))))));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await reads.SearchAsync(t.TenantId, t.Context("n2", "read.pii"), new(Contains: S(("legal_name", "cafe"))))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await Search(new())));
        Assert.Equal(["READ.CONTAINS_TOO_SHORT"], Codes(await Search(new(Contains: S(("legal_name", "ca"))))));
        Assert.Equal(["READ.INVALID_FILTER"], Codes(await Search(new(Contains: S(("phone", "555 123"))))));
        Assert.Equal(["READ.INVALID_FILTER"], Codes(await Search(new(Prefix: S(("phone", "555"))))));
        Assert.Equal(["READ.LIMIT_EXCEEDED"], Codes(await Search(new(Contains: S(("legal_name", "cafe")), Limit: 501))));
        Assert.NotEqual(Guid.Empty, b);
    }

    // -------------------------------------------------------------------------------------------------------------
    // the jobs of the read access log (09, 5.2, Q6)
    // -------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task TheReadLogIsSummarisedOncePerDayAndPurgedAfterTheRetention()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid a = await PartnerAsync(t, "a", "C-A", "Café Møller A/S", "København K", "post@moller.dk");
        Guid b = await PartnerAsync(t, "b", "C-B", "Nordisk Handel ApS", "Aarhus C");
        var reads = new PartnerReads(t.App);
        foreach ((string actor, Guid partner) in new[] { ("u1", a), ("u1", b), ("u2", a) })
        {
            var ctx = new CommandContext("k-" + actor + partner, "corr-" + actor + partner, actor, new HashSet<string> { "read.config", "read.pii" });
            Assert.Equal(CommandStatus.Ok, (await reads.GetAsync(t.TenantId, ctx, new(partner, IncludePii: true))).Status);
        }
        long rows = await t.ScalarAsync<long>("select count(*) from core.read_access_log where tenant_id = @t");
        Assert.True(rows >= 6);
        var jobs = new ReadLogJobs(t.App);
        DateTime now = DateTime.UtcNow;
        DateOnly today = DateOnly.FromDateTime(now);

        // nothing is purged before it is summarised, and without a setting or a rule nothing is purged at all
        Assert.Equal(ReadLogJobRules.NotSummarised, (await jobs.PurgeAsync(t.TenantId, now.AddYears(30))).Skipped);
        Assert.Equal(0, (await jobs.SummariseAsync(t.TenantId, today)).Events);   // today is not a full day

        // tomorrow, today is a full day: one event per day and field group, counts only
        var first = await jobs.SummariseAsync(t.TenantId, today.AddDays(1));
        Assert.Equal((1, today), (first.Days, first.SummarisedThrough));
        Assert.True(first.Events >= 2);
        Assert.Equal((long)first.Events, await t.ScalarAsync<long>("select count(*) from core.outbox_event where tenant_id = @t and event_type = 'access.summary' and is_audit"));
        string identity = await t.ScalarAsync<string>("select summary::text from core.outbox_event where tenant_id = @t and event_type = 'access.summary' and summary ->> 'field_group' = 'PII_IDENTITY'");
        var summary = System.Text.Json.JsonDocument.Parse(identity).RootElement;
        Assert.Equal((3, 2, 2), (summary.GetProperty("read_count").GetInt32(), summary.GetProperty("actor_count").GetInt32(), summary.GetProperty("partner_count").GetInt32()));
        Assert.False(summary.TryGetProperty("actor", out _));
        Assert.Equal("SUMMARY", await t.ScalarAsync<string>("select change from core.outbox_event where tenant_id = @t and event_type = 'access.summary' limit 1"));

        // a day is summarised once
        var again = await jobs.SummariseAsync(t.TenantId, today.AddDays(1));
        Assert.Equal((0, 0), (again.Days, again.Events));
        Assert.Equal((long)first.Events, await t.ScalarAsync<long>("select count(*) from core.outbox_event where tenant_id = @t and event_type = 'access.summary'"));

        // the outbox stays gapless
        Assert.Equal(await t.ScalarAsync<long>("select last_position from core.outbox_head where tenant_id = @t"), await t.ScalarAsync<long>("select max(position) from core.outbox_event where tenant_id = @t"));

        // the default retention is the longest rule of the tenant (10 years here): nothing is old enough
        var byRule = await jobs.PurgeAsync(t.TenantId, now.AddDays(40));
        Assert.Equal((0L, null), (byRule.Deleted, byRule.Skipped));
        // the setting rules: 30 days, and only the summarised days go
        await t.ExecuteAsync("update core.tenant_setting set read_log_retention_days = 30 where tenant_id = @t");
        Assert.Equal(0, (await jobs.PurgeAsync(t.TenantId, now.AddDays(10))).Deleted);
        var purged = await jobs.PurgeAsync(t.TenantId, now.AddDays(40));
        Assert.Equal((rows, null), (purged.Deleted, purged.Skipped));
        Assert.Equal(0L, await t.ScalarAsync<long>("select count(*) from core.read_access_log where tenant_id = @t"));
        // a row of a day that has not been summarised is not deleted, though it is older than the cutoff
        await t.ExecuteAsync("insert into core.read_access_log (tenant_id, actor, operation, field_group, correlation_id, read_at) values (@t, 'u9', 'business-partners:get', 'PII_IDENTITY', 'c9', now() + interval '60 days')");
        Assert.Equal(0, (await jobs.PurgeAsync(t.TenantId, now.AddDays(200))).Deleted);
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.read_access_log where tenant_id = @t"));
    }
}
