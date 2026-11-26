using FinanceCore.Core.Commands;
using FinanceCore.Core.Posting;
using FinanceCore.Core.Masters;
using FinanceCore.Core.Reads;

namespace FinanceCore.Data.Tests;

/// <summary>The second slice of 09 (F-IMPL-45): aging, the document context, chain status, audit events, the partner reads and the read access log.</summary>
[Collection("db")]
public class ReadAuditTests
{
    private static string Describe<T>(CommandResult<T> r) => RevaluationTests.Describe(r);
    private static string[] Codes<T>(CommandResult<T> r) => RevaluationTests.Codes(r);
    private static CommandContext Ledger(TestTenant t, string key = "rd", params string[] more) => t.Context(key, ["read.ledger", .. more]);
    private static CommandContext Audit(TestTenant t, string key = "ra") => t.Context(key, "read.audit");
    private static Dictionary<string, object?> D(params (string, object?)[] f) => f.ToDictionary(x => x.Item1, x => x.Item2);

    private static async Task<Guid> PostOpenItemAsync(TestTenant t, Guid account, string key, string posting, string due, decimal amount)
    {
        var req = new PostJournalDocumentRequest(t.CompanyId, null, "JOURNAL", DateOnly.Parse(posting), DateOnly.Parse(posting), "DKK", "Open", null, null,
            [new RequestLine("1", account, amount, null, new OpenItemRequest(DueDate: DateOnly.Parse(due))), new RequestLine("2", t.Revenue, -amount)]);
        var r = await new JournalDocumentPoster(t.App).PostAsync(t.TenantId, t.Context(key), req);
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        return r.Value!.DocumentId;
    }

    private static async Task<Guid> OpenItemAccountAsync(TestTenant t, string no = "1900")
    {
        Guid id = Guid.NewGuid();
        await t.ExecuteAsync("insert into core.gl_account (tenant_id, company_id, account_id, account_no, name, account_type, tax_code_rule, open_item_mode) values (@t, @c, @g, @no, 'Clearing', 'ASSET', 'FORBIDDEN', 'GL_OPEN_ITEM')",
            ("g", id), ("no", no));
        return id;
    }

    private static async Task<Guid> NewPartnerAsync(TestTenant t, string key, string code, bool email = true)
    {
        var pii = D(("legal_name", code + " A/S"), ("address_line_1", "Havnegade 1"), ("postal_code", "1058"), ("city", "København K"), ("country_code", "DK"));
        if (email) pii["email"] = "post@" + code.ToLowerInvariant() + ".dk";
        var r = await new PartnerCommands(t.App).CreateAsync(t.TenantId, t.Context(key, "write.master"), new CreatePartnerRequest(
            D(("partner_code", code), ("partner_kind", "ORGANISATION"), ("is_customer", true)), pii, [new PartnerRelationInput(t.CompanyId, D(("is_customer", true)))]));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        return r.Value!.EntityId;
    }

    private static async Task<long> LogRowsAsync(TestTenant t, string where = "true") =>
        await t.ScalarAsync<long>($"select count(*) from core.read_access_log where tenant_id = @t and {where}");

    // -------------------------------------------------------------------------------------------------------------
    // aging
    // -------------------------------------------------------------------------------------------------------------
    [Fact]
    public void TheBucketsOfAnAgingRunAreValidatedLaidOutAndFilled()
    {
        Assert.True(AgingRules.ValidBuckets([0, 30, 60, 90]));
        Assert.False(AgingRules.ValidBuckets([30, 30]));
        Assert.False(AgingRules.ValidBuckets([]));
        Assert.Equal(["<=0", "1-30", "31-60", "61-90", ">90"], AgingRules.Layout([0, 30, 60, 90]).Select(b => b.Label).ToArray());
        int[] bounds = [0, 30, 60, 90];
        Assert.Equal([0, 0, 1, 1, 2, 3, 4], new[] { -5, 0, 1, 30, 31, 90, 91 }.Select(d => AgingRules.BucketOf(bounds, d)).ToArray());
    }

    [Fact]
    public async Task AgingPutsTheRemainingAmountOfEveryOpenItemInTheBucketOfItsDaysPastDue()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid account = await OpenItemAccountAsync(t);
        await PostOpenItemAsync(t, account, "a1", "2026-02-01", "2026-03-01", 500m);   // 61 days past on 1 May
        await PostOpenItemAsync(t, account, "a2", "2026-03-15", "2026-04-15", 1000m);  // 16 days past
        await PostOpenItemAsync(t, account, "a3", "2026-04-20", "2026-06-01", 200m);   // not yet due
        var reads = new LedgerReads(t.App);

        var r = await reads.AgingAsync(t.TenantId, Ledger(t), new(t.CompanyId, new DateOnly(2026, 5, 1)));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        var total = r.Value!.GrandTotal!;
        Assert.Equal([200m, 1000m, 0m, 500m, 0m], total.Buckets.Select(b => b.Remaining.Lc).ToArray());
        Assert.Equal([1L, 1L, 0L, 1L, 0L], total.Buckets.Select(b => b.ItemCount).ToArray());
        Assert.Equal((1700m, 3L), (total.Total.Lc, total.ItemCount));
        Assert.Equal("DUE_DATE", r.Value.Basis);
        Assert.Equal(1700m, r.Value.Rows.Single().Total.Lc);   // no group key: one row

        // an item is aged by its remaining amount as of the date, so an earlier date drops what was posted later
        var earlier = (await reads.AgingAsync(t.TenantId, Ledger(t), new(t.CompanyId, new DateOnly(2026, 3, 20)))).Value!.GrandTotal!;
        Assert.Equal((1500m, 2L), (earlier.Total.Lc, earlier.ItemCount));

        var byAccount = await reads.AgingAsync(t.TenantId, Ledger(t), new(t.CompanyId, new DateOnly(2026, 5, 1), Buckets: [30], GroupBy: ["account_id", "currency"]));
        var row = Assert.Single(byAccount.Value!.Rows);
        Assert.Equal(account.ToString("D"), row.Keys["account_id"]);
        Assert.Equal("DKK", row.Keys["currency"]);
        Assert.Equal([1200m, 500m], row.Buckets.Select(b => b.Remaining.Lc).ToArray());
        Assert.Equal(["<=30", ">30"], row.Buckets.Select(b => b.Label).ToArray());

        var byDocumentDate = (await reads.AgingAsync(t.TenantId, Ledger(t), new(t.CompanyId, new DateOnly(2026, 5, 1), Basis: "DOCUMENT_DATE"))).Value!.GrandTotal!;
        Assert.Equal([0m, 200m, 1000m, 500m, 0m], byDocumentDate.Buckets.Select(b => b.Remaining.Lc).ToArray());

        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await reads.AgingAsync(t.TenantId, Ledger(t), new(t.CompanyId, Basis: "NOPE"))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await reads.AgingAsync(t.TenantId, Ledger(t), new(t.CompanyId, Buckets: [60, 30]))));
        Assert.Equal(["READ.INVALID_FILTER"], Codes(await reads.AgingAsync(t.TenantId, Ledger(t), new(t.CompanyId, GroupBy: ["company_id"]))));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await reads.AgingAsync(t.TenantId, t.Context("x"), new(t.CompanyId))));
        Assert.Equal(["READ.NOT_FOUND"], Codes(await reads.AgingAsync(t.TenantId, Ledger(t), new(Guid.NewGuid()))));
    }

    // -------------------------------------------------------------------------------------------------------------
    // documents:get-context
    // -------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task TheDocumentContextCarriesTheOpenItemsTheOriginTheIntegrityAndTheReversal()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid account = await OpenItemAccountAsync(t);
        Guid doc = await PostOpenItemAsync(t, account, "c1", "2026-03-15", "2026-04-15", 1000m);
        var reads = new LedgerReads(t.App);

        var r = await reads.GetDocumentContextAsync(t.TenantId, Ledger(t), new(doc));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        var c = r.Value!;
        Assert.Equal(2, c.Document.Lines!.Count);
        Assert.Equal(("OPEN", new DateOnly(2026, 4, 15)), (Assert.Single(c.OpenItems!).ClearingStatus, c.OpenItems![0].DueDate));
        Assert.Equal(("journal-documents:post", "tester", "corr-c1"), (c.Origin!.CommandName, c.Origin.Actor, c.Origin.CorrelationId));
        Assert.Equal(1L, c.Integrity!.ChainSeq);
        Assert.Equal(c.Document.RowHash, c.Integrity.RowHash);
        Assert.Null(c.Integrity.LastAnchor);
        Assert.Empty(c.Clearings!);
        Assert.Empty(c.Changes!);
        Assert.Null(c.Reversal);
        Assert.Equal(["legal_snapshot"], c.Omitted);   // read.legal_snapshot is missing: left out and named, not rejected
        Assert.Null(c.LegalSnapshot);

        var only = await reads.GetDocumentContextAsync(t.TenantId, Ledger(t), new(doc, Include: ["origin"]));
        Assert.True(only.Value!.Document.Lines is null && only.Value.OpenItems is null && only.Value.Origin is not null && only.Value.Omitted.Count == 0);
        Assert.Equal(["READ.INVALID_FILTER"], Codes(await reads.GetDocumentContextAsync(t.TenantId, Ledger(t), new(doc, Include: ["vat"]))));
        Assert.Equal(["READ.NOT_FOUND"], Codes(await reads.GetDocumentContextAsync(t.TenantId, Ledger(t), new(Guid.NewGuid()))));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await reads.GetDocumentContextAsync(t.TenantId, t.Context("x"), new(doc))));

        // a document without a partner has a snapshot with no personal data: it is returned and nothing is logged (5.2)
        var withLegal = await reads.GetDocumentContextAsync(t.TenantId, Ledger(t, "rd2", "read.legal_snapshot"), new(doc, Include: ["legal_snapshot"]));
        Assert.Null(withLegal.Value!.LegalSnapshot!.PartnerId);
        Assert.Empty(withLegal.Value.Omitted);
        Assert.Equal(0L, await LogRowsAsync(t));

        // the reversal shows on both documents
        var reversed = await new DocumentReverser(t.App).ReverseAsync(t.TenantId, t.Context("rev", "journal.reverse"), new(doc, null, "CORRECTION", "Wrong"));
        Assert.True(reversed.Status == CommandStatus.Ok, Describe(reversed));
        var after = (await reads.GetDocumentContextAsync(t.TenantId, Ledger(t), new(doc, Include: ["reversal"]))).Value!.Reversal!;
        Assert.Equal(reversed.Value!.DocumentId, after.ReversedByDocumentId);
        var mirror = (await reads.GetDocumentContextAsync(t.TenantId, Ledger(t), new(reversed.Value.DocumentId, Include: ["reversal"]))).Value!.Reversal!;
        Assert.Equal((doc, "CORRECTION"), (mirror.ReversalOfDocumentId, mirror.ReasonCode));
    }

    [Fact]
    public async Task TheLegalSnapshotOfAPartnerDocumentIsLoggedWhenItIsRead()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid partner = await NewPartnerAsync(t, "pa", "ACME");
        var invoice = t.Journal("2026-03-15") with { DocumentType = "AR_INVOICE", PartnerId = partner };
        var posted = await new JournalDocumentPoster(t.App).PostAsync(t.TenantId, t.Context("inv"), invoice);
        Assert.True(posted.Status == CommandStatus.Ok, Describe(posted));
        var reads = new LedgerReads(t.App);

        var denied = await reads.GetDocumentContextAsync(t.TenantId, Ledger(t), new(posted.Value!.DocumentId, Include: ["legal_snapshot"]));
        Assert.Equal(["legal_snapshot"], denied.Value!.Omitted);
        Assert.Equal(0L, await LogRowsAsync(t));

        var r = await reads.GetDocumentContextAsync(t.TenantId, Ledger(t, "rd2", "read.legal_snapshot"), new(posted.Value.DocumentId, Include: ["legal_snapshot"], PurposeCode: "INVOICE_RENDER"));
        Assert.Equal(("ACME A/S", partner), (r.Value!.LegalSnapshot!.LegalName, r.Value.LegalSnapshot.PartnerId!.Value));
        Assert.Equal(1L, await LogRowsAsync(t, "field_group = 'LEGAL_SNAPSHOT' and document_id is not null and purpose_code = 'INVOICE_RENDER' and operation = 'documents:get-context' and actor = 'tester' and correlation_id = 'corr-rd2'"));
    }

    // -------------------------------------------------------------------------------------------------------------
    // partners and the read log
    // -------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task PersonalDataIsOmittedWithoutReadPiiAndLoggedWithIt()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid a = await NewPartnerAsync(t, "pa", "ACME"), b = await NewPartnerAsync(t, "pb", "BOLT", email: false);
        var reads = new PartnerReads(t.App);
        CommandContext Cfg(string key, params string[] more) => t.Context(key, ["read.config", .. more]);

        var plain = await reads.GetAsync(t.TenantId, Cfg("g1"), new(a));
        Assert.True(plain.Status == CommandStatus.Ok, Describe(plain));
        Assert.Equal(("ACME", null, false), (plain.Value!.Partner.PartnerCode, plain.Value.Partner.Pii, plain.Value.PiiOmitted));

        var omitted = await reads.GetAsync(t.TenantId, Cfg("g2"), new(a, IncludePii: true));
        Assert.Equal((true, null), (omitted.Value!.PiiOmitted, omitted.Value.Partner.Pii));
        Assert.Equal(0L, await LogRowsAsync(t));   // nothing personal was returned: no row

        var withPii = await reads.GetAsync(t.TenantId, Cfg("g3", "read.pii"), new(a, IncludePii: true, PurposeCode: "SUPPORT"));
        Assert.Equal(("ACME A/S", "post@acme.dk", false), (withPii.Value!.Partner.Pii!.LegalName, withPii.Value.Partner.Pii.Email, withPii.Value.PiiOmitted));
        Assert.Equal(3L, await LogRowsAsync(t, $"partner_id = '{a}' and purpose_code = 'SUPPORT' and operation = 'business-partners:get' and result_position is null"));
        Assert.Equal("PII_ADDRESS,PII_CONTACT,PII_IDENTITY",
            await t.ScalarAsync<string>($"select string_agg(field_group, ',' order by field_group) from core.read_access_log where tenant_id = @t and partner_id = '{a}'"));

        // a list logs a row per partner and field group with the position in the result; a partner without contact data has no PII_CONTACT row
        var list = await reads.ListAsync(t.TenantId, Cfg("l1", "read.pii"), new(IncludePii: true));
        Assert.Equal(["ACME", "BOLT"], list.Value!.Items.Select(i => i.PartnerCode).ToArray());
        Assert.Equal(5L, await LogRowsAsync(t, "operation = 'business-partners:list'"));
        Assert.Equal(2L, await LogRowsAsync(t, $"operation = 'business-partners:list' and partner_id = '{b}' and result_position = 2"));
        var filtered = await reads.ListAsync(t.TenantId, Cfg("l2"), new(CodePrefix: "BO", IsCustomer: true, Limit: 1));
        Assert.Equal(["BOLT"], filtered.Value!.Items.Select(i => i.PartnerCode).ToArray());
        var page1 = await reads.ListAsync(t.TenantId, Cfg("l3"), new(Limit: 1));
        var page2 = await reads.ListAsync(t.TenantId, Cfg("l4"), new(Limit: 1, Cursor: page1.Value!.Page.NextCursor));
        Assert.Equal(("ACME", "BOLT", null), (page1.Value.Items[0].PartnerCode, page2.Value!.Items[0].PartnerCode, page2.Value.Page.NextCursor));

        Assert.Equal(["READ.NOT_FOUND"], Codes(await reads.GetAsync(t.TenantId, Cfg("g4"), new(Guid.NewGuid()))));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await reads.GetAsync(t.TenantId, t.Context("x", "read.pii"), new(a))));
        Assert.Equal(["READ.INVALID_FILTER"], Codes(await reads.ListAsync(t.TenantId, Cfg("l5"), new(PartnerKind: "NOPE"))));
        Assert.Equal(["READ.CURSOR_INVALID"], Codes(await reads.ListAsync(t.TenantId, Cfg("l6"), new(Cursor: "garbage"))));
    }

    [Fact]
    public async Task ARestrictedPartnerIsReadOnlyForTheStatutoryPurpose()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid a = await NewPartnerAsync(t, "pa", "ACME");
        await t.ExecuteAsync("update core.business_partner set restriction_status = 'RESTRICTED' where tenant_id = @t and partner_id = @p", ("p", a));
        var reads = new PartnerReads(t.App);
        CommandContext Ctx(string key) => t.Context(key, "read.config", "read.pii");

        Assert.Equal(["READ.PARTNER_RESTRICTED"], Codes(await reads.GetAsync(t.TenantId, Ctx("g1"), new(a, IncludePii: true))));
        Assert.Equal(0L, await LogRowsAsync(t));
        Assert.Equal("RESTRICTED", (await reads.GetAsync(t.TenantId, Ctx("g2"), new(a))).Value!.Partner.RestrictionStatus);   // without personal data it is readable
        var statutory = await reads.GetAsync(t.TenantId, Ctx("g3"), new(a, IncludePii: true, PurposeCode: PartnerReadRules.StatutoryPurpose));
        Assert.Equal("ACME A/S", statutory.Value!.Partner.Pii!.LegalName);
        Assert.Equal(3L, await LogRowsAsync(t));
        var list = await reads.ListAsync(t.TenantId, Ctx("l1"), new(IncludePii: true));
        Assert.Null(Assert.Single(list.Value!.Items).Pii);
        Assert.Equal(3L, await LogRowsAsync(t));
    }

    [Fact]
    public async Task TheReadAccessLogIsInsertOnlyAndReadableWithReadAudit()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid a = await NewPartnerAsync(t, "pa", "ACME");
        await new PartnerReads(t.App).GetAsync(t.TenantId, t.Context("g", "read.config", "read.pii"), new(a, IncludePii: true));
        var audit = new AuditReads(t.App);

        var log = await audit.ReadAccessLogAsync(t.TenantId, Audit(t), new(PartnerId: a));
        Assert.True(log.Status == CommandStatus.Ok, Describe(log));
        Assert.Equal(["PII_ADDRESS", "PII_CONTACT", "PII_IDENTITY"], log.Value!.Items.Select(i => i.FieldGroup).Order().ToArray());
        Assert.All(log.Value.Items, i => Assert.Equal(("tester", "business-partners:get", "corr-g"), (i.Actor, i.Operation, i.CorrelationId)));
        Assert.Empty((await audit.ReadAccessLogAsync(t.TenantId, Audit(t), new(Actor: "someone-else"))).Value!.Items);
        var paged = await audit.ReadAccessLogAsync(t.TenantId, Audit(t), new(Limit: 2));
        Assert.Equal((2, true), (paged.Value!.Items.Count, paged.Value.Page.NextCursor is not null));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await audit.ReadAccessLogAsync(t.TenantId, t.Context("x", "read.ledger"), new())));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => t.ExecuteAsync("update core.read_access_log set actor = 'x' where tenant_id = @t"));
        Assert.Contains("insert-only", ex.Message);
        // the read role cannot delete either: only the retention job may
        await Assert.ThrowsAnyAsync<Exception>(() => t.ExecuteAsync("set local role cmd_read; delete from core.read_access_log where tenant_id = @t"));
    }

    // -------------------------------------------------------------------------------------------------------------
    // chains:status and audit-events:query
    // -------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task ChainStatusListsTheHeadsOfTheCompanyWithTheirLastAnchor()
    {
        var t = await TestTenant.CreateReadyAsync();
        var first = await new JournalDocumentPoster(t.App).PostAsync(t.TenantId, t.Context("p1"), t.Journal());
        Guid doc = first.Value!.DocumentId;
        var audit = new AuditReads(t.App);

        var r = await audit.ChainsStatusAsync(t.TenantId, Audit(t), new(t.CompanyId));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        var ledger = r.Value!.Chains.Single(c => c.ChainType == "LEDGER");
        Assert.Equal((t.LedgerId.ToString("D"), 1L, null), (ledger.ScopeKey, ledger.LastSeq, ledger.LastAnchor?.AnchorId));
        Assert.Equal(64, ledger.LastHash.Length);

        await t.ExecuteAsync(
            "insert into core.chain_anchor (tenant_id, company_id, chain_type, scope_key, chain_seq, row_hash, anchored_at, anchor_digest) values (@t, @c, 'LEDGER', @s, 1, @h, now(), @h)",
            ("s", t.LedgerId.ToString("D")), ("h", new byte[32]));
        var anchored = (await audit.ChainsStatusAsync(t.TenantId, Audit(t), new(t.CompanyId, "LEDGER"))).Value!.Chains.Single();
        Assert.Equal(("PENDING", 1L), (anchored.LastAnchor!.State, anchored.LastAnchor.ChainSeq));
        var context = (await new LedgerReads(t.App).GetDocumentContextAsync(t.TenantId, Ledger(t), new(doc, Include: ["integrity"]))).Value!;
        Assert.Equal(anchored.LastAnchor.AnchorId, context.Integrity!.LastAnchor!.AnchorId);

        Assert.Equal(["READ.INVALID_FILTER"], Codes(await audit.ChainsStatusAsync(t.TenantId, Audit(t), new(t.CompanyId, "NOPE"))));
        Assert.Equal(["READ.NOT_FOUND"], Codes(await audit.ChainsStatusAsync(t.TenantId, Audit(t), new(Guid.NewGuid()))));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await audit.ChainsStatusAsync(t.TenantId, Ledger(t), new(t.CompanyId))));
    }

    [Fact]
    public async Task AuditEventsAreTheAuditStreamOfTheOutboxInPositionOrder()
    {
        var t = await TestTenant.CreateReadyAsync();
        var poster = new JournalDocumentPoster(t.App);
        Guid d1 = (await poster.PostAsync(t.TenantId, t.Context("p1"), t.Journal())).Value!.DocumentId;
        await poster.PostAsync(t.TenantId, t.Context("p2"), t.Journal("2026-03-16"));
        await new DocumentReverser(t.App).ReverseAsync(t.TenantId, t.Context("rev", "journal.reverse"), new(d1, null, "CORRECTION", "Wrong"));
        var audit = new AuditReads(t.App);

        var all = await audit.AuditEventsAsync(t.TenantId, Audit(t), new(Actor: "tester"));
        Assert.True(all.Status == CommandStatus.Ok, Describe(all));
        var types = all.Value!.Items.Select(i => i.ViewType!).ToArray();
        Assert.Equal(["YEAR_OPENED", "POSTED", "POSTED", "REVERSED"], types);   // the year opened by the test setup, two postings, and the reversal (one event, no posting event of the mirror)
        Assert.Equal(all.Value.Items.Select(i => i.Position).Order().ToArray(), all.Value.Items.Select(i => i.Position).ToArray());
        Assert.True(all.Value.HeadPosition >= all.Value.Items[^1].Position);
        Assert.All(all.Value.Items, i => Assert.Equal(t.CompanyId, i.CompanyId));

        var one = await audit.AuditEventsAsync(t.TenantId, Audit(t), new(CorrelationId: "corr-p2"));
        var e = Assert.Single(one.Value!.Items);
        Assert.Equal(("ledger.document_posted", "DOCUMENT", "corr-p2"), (e.EventType, e.EntityType, e.CorrelationId));
        Assert.NotNull(e.Detail);   // the reference of a chained record (chain_ref)

        var byType = await audit.AuditEventsAsync(t.TenantId, Audit(t), new(Actor: "tester", EventTypes: ["ledger.document_reversed"]));
        Assert.Equal(["REVERSED"], byType.Value!.Items.Select(i => i.ViewType!).ToArray());
        var byEntity = await audit.AuditEventsAsync(t.TenantId, Audit(t), new(EntityType: "DOCUMENT", EntityKey: $"{t.CompanyId:D}/{d1:D}"));
        Assert.Contains("REVERSED", byEntity.Value!.Items.Select(i => i.ViewType));

        var page1 = await audit.AuditEventsAsync(t.TenantId, Audit(t), new(Actor: "tester", Limit: 3));
        var page2 = await audit.AuditEventsAsync(t.TenantId, Audit(t), new(Actor: "tester", Limit: 3, Cursor: page1.Value!.Page.NextCursor));
        Assert.Equal((3, 1, null), (page1.Value.Items.Count, page2.Value!.Items.Count, page2.Value.Page.NextCursor));

        var future = await audit.AuditEventsAsync(t.TenantId, Audit(t), new(From: DateTime.UtcNow.AddDays(1)));
        Assert.Empty(future.Value!.Items);
        Assert.Equal(["READ.INVALID_FILTER"], Codes(await audit.AuditEventsAsync(t.TenantId, Audit(t), new())));   // Q11: a bound is required
        Assert.Equal(["READ.INVALID_FILTER"], Codes(await audit.AuditEventsAsync(t.TenantId, Audit(t), new(EntityKey: "x", Actor: "tester"))));
        Assert.Equal(["READ.CURSOR_INVALID"], Codes(await audit.AuditEventsAsync(t.TenantId, Audit(t), new(Actor: "tester", Cursor: "garbage"))));
        Assert.Equal(["READ.LIMIT_EXCEEDED"], Codes(await audit.AuditEventsAsync(t.TenantId, Audit(t), new(Actor: "tester", Limit: 501))));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await audit.AuditEventsAsync(t.TenantId, Ledger(t), new(Actor: "tester"))));
    }

    [Fact]
    public void TheViewTypesFollowTheMappingOfSection48()
    {
        Assert.Equal("POSTED", AuditViewTypes.Of("ledger.document_posted"));
        Assert.Equal("REVALUATION_RUN", AuditViewTypes.Of("revaluation.rerun"));
        Assert.Equal("PERIOD_HARD_CLOSED", AuditViewTypes.Of("period.status_changed", "HARD_CLOSED"));
        Assert.Equal("MIGRATION_COMMITTED", AuditViewTypes.Of("migration.batch_status_changed", "COMMITTED"));
        Assert.Equal("EXPORT_COMPLETED", AuditViewTypes.Of("export.created", "COMPLETED"));
        Assert.Null(AuditViewTypes.Of("stock.balance_changed"));
    }
}
