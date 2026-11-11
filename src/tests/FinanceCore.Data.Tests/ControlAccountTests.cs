using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;
using FinanceCore.Core.Posting;
using Npgsql;

namespace FinanceCore.Data.Tests;

/// <summary>Open items on CONTROL accounts (02, 5A, 06, 5.2): the partner, the due date, the payment terms and the blocks of the relation,
/// the rules about the status of the partner, and what a partner on an open item changes elsewhere (reversal, change, the guards of the partner).</summary>
[Collection("db")]
public class ControlAccountTests
{
    private static string Describe<T>(CommandResult<T> r) =>
        r.Status + ": " + string.Join(", ", r.Errors.Select(e => $"{e.Code} {e.Path}"));

    private static string[] Codes<T>(CommandResult<T> r) => r.Errors.Select(e => e.Code).ToArray();

    private static Dictionary<string, object?> D(params (string, object?)[] f) => f.ToDictionary(x => x.Item1, x => x.Item2);

    private sealed record Setup(TestTenant Tenant, Guid Control, Guid Partner, Guid Terms);

    private static async Task<Guid> NewPartner(TestTenant t, string key, string code, Dictionary<string, object?>? relation, bool withRelation = true)
    {
        var req = new CreatePartnerRequest(
            D(("partner_code", code), ("partner_kind", "ORGANISATION"), ("is_customer", true)),
            D(("legal_name", code + " A/S"), ("country_code", "DK")),
            withRelation ? [new PartnerRelationInput(t.CompanyId, relation ?? D(("is_customer", true)))] : []);
        var r = await new PartnerCommands(t.App).CreateAsync(t.TenantId, t.Context(key, "write.master"), req);
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        return r.Value!.EntityId;
    }

    private static async Task<Setup> CreateAsync()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid control = Guid.NewGuid();
        await t.ExecuteAsync(
            "insert into core.gl_account (tenant_id, company_id, account_id, account_no, name, account_type, tax_code_rule, open_item_mode) " +
            "values (@t, @c, @a, '1300', 'Receivables', 'ASSET', 'FORBIDDEN', 'CONTROL'), (@t, @c, @b, '1900', 'Bank clearing', 'ASSET', 'FORBIDDEN', 'GL_OPEN_ITEM')",
            ("c", t.CompanyId), ("a", control), ("b", Guid.NewGuid()));
        var terms = await new PaymentTermsCommands(t.App).CreateAsync(t.TenantId, t.Context("terms", "write.config"),
            new(D(("code", "N30"), ("description", "30 days"), ("baseline_rule", "DOCUMENT_DATE"), ("net_days", 30))));
        Guid partner = await NewPartner(t, "p", "ACME", D(("is_customer", true), ("payment_block", "HOLD"), ("dunning_block", "NODUN")));
        return new Setup(t, control, partner, terms.Value!.EntityId);
    }

    private static OpenItemRequest Item(Guid? partner, DateOnly? due = null, Guid? terms = null, string? block = null) =>
        new(PartnerId: partner, DueDate: due ?? new DateOnly(2026, 4, 14), PaymentTermsId: terms, PaymentBlock: block);

    private static PostJournalDocumentRequest Journal(Setup s, OpenItemRequest? item, decimal amount = 1000m, Guid? account = null, string date = "2026-03-15") =>
        s.Tenant.Journal(date, "DKK", amount) with
        {
            Lines = [new RequestLine("1", account ?? s.Control, amount, null, item), new RequestLine("2", s.Tenant.Revenue, -amount)]
        };

    private static Task<CommandResult<PostedDocument>> Post(Setup s, string key, PostJournalDocumentRequest req) =>
        new JournalDocumentPoster(s.Tenant.App).PostAsync(s.Tenant.TenantId, s.Tenant.Context(key), req);

    private static async Task<string> Text(TestTenant t, string sql, params (string, object)[] p)
    {
        string q = sql.TrimStart().StartsWith("select", StringComparison.Ordinal) ? sql : "select " + sql;
        return await t.ScalarAsync<string>("select coalesce((" + q + ")::text, '<null>')", p);
    }

    [Fact]
    public async Task AControlLineCarriesThePartnerTheDueDateAndTheDefaultsOfTheRelation()
    {
        var s = await CreateAsync();
        var r = await Post(s, "p1", Journal(s, Item(s.Partner, terms: s.Terms)));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        Guid doc = r.Value!.DocumentId;

        Assert.Equal($"{s.Partner}|2026-04-14|{s.Terms}|HOLD|NODUN|OPEN", await Text(s.Tenant,
            "partner_id || '|' || due_date || '|' || payment_terms_id || '|' || payment_block || '|' || dunning_block || '|' || clearing_status " +
            "from core.journal_line_open_item where tenant_id = @t and document_id = @d and line_no = 1", ("d", doc)));
        // a block of the request wins over the default of the relation
        var own = await Post(s, "p2", Journal(s, Item(s.Partner, block: "DISPUTE")));
        Assert.Equal("DISPUTE|NODUN", await Text(s.Tenant, "payment_block || '|' || dunning_block from core.journal_line_open_item where tenant_id = @t and document_id = @d and line_no = 1", ("d", own.Value!.DocumentId)));

        // the partner is part of the line hash (02, 9.3a): the posting and the verification agree
        var v = await new ChainVerifier(s.Tenant.App).VerifyLedgerAsync(s.Tenant.TenantId, s.Tenant.CompanyId, s.Tenant.LedgerId);
        Assert.True(v.Ok, string.Join("; ", v.Problems.Select(p => p.Code + " " + p.Detail)));

        // a repeated key with another partner is another request
        Guid other = await NewPartner(s.Tenant, "p-other", "OTHER", null);
        Assert.True((await Post(s, "p1", Journal(s, Item(s.Partner, terms: s.Terms)))).IdempotentReplay);
        Assert.Equal(["IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST"], Codes(await Post(s, "p1", Journal(s, Item(other, terms: s.Terms)))));
    }

    [Fact]
    public async Task TheRulesOfAControlLineAndOfItsPartner()
    {
        var s = await CreateAsync();
        var t = s.Tenant;
        async Task<string[]> Try(string key, OpenItemRequest? item, Guid? account = null) => Codes(await Post(s, key, Journal(s, item, account: account)));

        Assert.Equal(["ACCOUNT_RULE.OPEN_ITEM_REQUIRED"], await Try("a", null));
        Assert.Equal(["ACCOUNT_RULE.PARTNER_REQUIRED"], await Try("b", Item(null)));
        Assert.Equal(["ACCOUNT_RULE.DUE_DATE_REQUIRED"], await Try("c", new OpenItemRequest(PartnerId: s.Partner)));
        Assert.Equal(["ACCOUNT_RULE.PARTNER_REQUIRED", "ACCOUNT_RULE.DUE_DATE_REQUIRED"], await Try("d", new OpenItemRequest()));
        Assert.Equal(["MASTER.PARTNER_NOT_FOUND"], await Try("e", Item(Guid.NewGuid())));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], await Try("f", Item(s.Partner, terms: Guid.NewGuid())));
        Assert.Equal(["ACCOUNT_RULE.PARTNER_FORBIDDEN"], await Try("g", Item(s.Partner), Guid.Parse(await Text(t, "account_id from core.gl_account where tenant_id = @t and account_no = '1900'"))));
        Assert.Equal(["LIMIT.OPEN_ITEM_FIELD_NOT_BUILT"], await Try("h", new OpenItemRequest(PartnerId: s.Partner, DueDate: new DateOnly(2026, 4, 1), BankAccountId: Guid.NewGuid())));

        // the status of the partner
        Guid blocked = await NewPartner(t, "pb", "BLOCKED", null);
        Assert.True((await new PartnerCommands(t.App).DeactivateAsync(t.TenantId, t.Context("pb-d", "write.master"), new(blocked, 1, "CREDIT"))).Status == CommandStatus.Ok);
        Assert.Equal(["MASTER.PARTNER_INACTIVE"], await Try("i", Item(blocked)));
        Guid anonymised = await NewPartner(t, "pa", "ANON", null);
        await t.ExecuteAsync("update core.business_partner set status = 'ANONYMISED', anonymised_at = now() where tenant_id = @t and partner_id = @p", ("p", anonymised));
        Assert.Equal(["MASTER.PARTNER_ANONYMISED"], await Try("j", Item(anonymised)));
        Guid restricted = await NewPartner(t, "pr", "RESTR", null);
        await t.ExecuteAsync("update core.business_partner set restriction_status = 'RESTRICTED' where tenant_id = @t and partner_id = @p", ("p", restricted));
        Assert.Equal(["MASTER.PARTNER_RESTRICTED"], await Try("k", Item(restricted)));

        // the relation to the company
        Guid noRelation = await NewPartner(t, "pn", "NOREL", null, withRelation: false);
        Assert.Equal(["MASTER.PARTNER_NOT_FOUND"], await Try("l", Item(noRelation)));
        Guid relBlocked = await NewPartner(t, "pq", "RELBLK", D(("is_customer", true), ("status", "BLOCKED")));
        Assert.Equal(["MASTER.PARTNER_INACTIVE"], await Try("m", Item(relBlocked)));
        Guid noRole = await NewPartner(t, "pw", "NOROLE", D(("is_customer", false), ("is_supplier", false)));
        Assert.Equal(["MASTER.PARTNER_INACTIVE"], await Try("n", Item(noRole)));
        Assert.Equal(0L, await t.ScalarAsync<long>("select count(*) from core.journal_document where tenant_id = @t"));

        // a deactivated partner is open again after reactivation
        Assert.True((await new PartnerCommands(t.App).ReactivateAsync(t.TenantId, t.Context("pb-r", "write.master"), new(blocked, 2, "PAID"))).Status == CommandStatus.Ok);
        Assert.True((await Post(s, "o", Journal(s, Item(blocked)))).Status == CommandStatus.Ok);
    }

    [Fact]
    public async Task AControlDocumentIsReversedWithItsPartnerAndTheClearingCarriesIt()
    {
        var s = await CreateAsync();
        var posted = await Post(s, "p1", Journal(s, Item(s.Partner)));
        Guid doc = posted.Value!.DocumentId;

        var r = await new DocumentReverser(s.Tenant.App).ReverseAsync(s.Tenant.TenantId, s.Tenant.Context("rev", "journal.reverse"), new(doc, null, "CORRECTION", null));

        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        Assert.Equal($"{s.Partner}|CLEARED|2026-04-14", await Text(s.Tenant, "partner_id || '|' || clearing_status || '|' || due_date from core.journal_line_open_item where tenant_id = @t and document_id = @d and line_no = 1", ("d", r.Value!.DocumentId)));
        Assert.Equal($"{s.Partner}|{s.Control}|REVERSAL", await Text(s.Tenant, "partner_id || '|' || account_id || '|' || clearing_method from core.clearing where tenant_id = @t"));
        var ledger = await new ChainVerifier(s.Tenant.App).VerifyLedgerAsync(s.Tenant.TenantId, s.Tenant.CompanyId, s.Tenant.LedgerId);
        Assert.True(ledger.Ok, string.Join("; ", ledger.Problems.Select(p => p.Code + " " + p.Detail)));
        var clearing = await new ChainVerifier(s.Tenant.App).VerifyClearingsAsync(s.Tenant.TenantId, s.Tenant.CompanyId);
        Assert.True(clearing.Ok, string.Join("; ", clearing.Problems.Select(p => p.Code + " " + p.Detail)));
    }

    [Fact]
    public async Task APartnerThatOpenItemsReferToKeepsItsKindAndItsRoles()
    {
        var s = await CreateAsync();
        Assert.True((await Post(s, "p1", Journal(s, Item(s.Partner)))).Status == CommandStatus.Ok);
        var commands = new PartnerCommands(s.Tenant.App);

        var kind = await commands.UpdateAsync(s.Tenant.TenantId, s.Tenant.Context("u1", "write.master"), new UpdatePartnerRequest(s.Partner, 1, D(("partner_kind", "PERSON")), null, []));
        Assert.Equal(["CONFIG.IMMUTABLE_AFTER_POSTING"], Codes(kind));
        var role = await commands.UpdateAsync(s.Tenant.TenantId, s.Tenant.Context("u2", "write.master"), new UpdatePartnerRequest(s.Partner, 1, D(("is_customer", false)), null, []));
        Assert.Equal(["CONFIG.REFERENCED"], Codes(role));
        // a role can be added any time, and other fields change freely
        var ok = await commands.UpdateAsync(s.Tenant.TenantId, s.Tenant.Context("u3", "write.master"), new UpdatePartnerRequest(s.Partner, 1, D(("is_supplier", true), ("partner_code", "ACME2")), null, []));
        Assert.True(ok.Status == CommandStatus.Ok, Describe(ok));

        // the table says the same to anyone who does not use the command
        var ex = await Assert.ThrowsAsync<PostgresException>(() => s.Tenant.ExecuteAsync(
            "update core.business_partner set partner_kind = 'PERSON' where tenant_id = @t and partner_id = @p", ("p", s.Partner)));
        Assert.Contains("IMMUTABLE_AFTER_POSTING", ex.MessageText);
        var fk = await Assert.ThrowsAsync<PostgresException>(() => s.Tenant.ExecuteAsync(
            "update core.journal_line_open_item set partner_id = gen_random_uuid() where tenant_id = @t and partner_id is not null"));
        Assert.Contains("L1 column cannot change", fk.MessageText);
    }

    [Fact]
    public async Task ThePaymentTermsAndTheDueDateOfAControlItemChangeWithinTheirRules()
    {
        var s = await CreateAsync();
        var doc = (await Post(s, "p1", Journal(s, Item(s.Partner)))).Value!.DocumentId;
        Task<CommandResult<ChangedDocument>> Change(string key, params FieldChange[] changes) =>
            new DocumentChanger(s.Tenant.App).ChangeAsync(s.Tenant.TenantId, s.Tenant.Context(key, "journal.change"), new ChangeOpenFieldsRequest(doc, changes, "CORRECTION", null));
        FieldChange OI(string field, string? old, string? value) => new("LINE_OPEN_ITEM", 1, field, old, value);

        var ok = await Change("c1", OI("payment_terms_id", null, s.Terms.ToString("D")), OI("due_date", "2026-04-14", "2026-05-14"));
        Assert.True(ok.Status == CommandStatus.Ok, Describe(ok));
        Assert.Equal($"{s.Terms}|2026-05-14", await Text(s.Tenant, "payment_terms_id || '|' || due_date from core.journal_line_open_item where tenant_id = @t and document_id = @d and line_no = 1", ("d", doc)));
        Assert.Equal("ID", await Text(s.Tenant, "value_type from core.change_log where tenant_id = @t and field_name = 'payment_terms_id'"));

        // the due date of a control account stays, in the command and in the table
        var clear = await Change("c2", OI("due_date", "2026-05-14", null));
        Assert.Equal(["ACCOUNT_RULE.DUE_DATE_REQUIRED"], Codes(clear));
        var ex = await Assert.ThrowsAsync<PostgresException>(() => s.Tenant.ExecuteAsync(
            "select set_config('app.tenant_id', @t::text, true); " +
            "insert into core.change_log (tenant_id, change_set_id, company_id, entity_type, document_id, line_no, field_name, value_type, old_value, new_value, lock_class, period_status, reason_code, changed_by, changed_at, correlation_id, idempotency_key) " +
            "select @t, gen_random_uuid(), company_id, 'LINE_OPEN_ITEM', document_id, 1, 'due_date', 'DATE', '2026-05-14', null, 'L2', 'OPEN', 'X', 'a', now(), 'c', 'k' from core.journal_line_open_item where tenant_id = @t and line_no = 1; " +
            "update core.journal_line_open_item set due_date = null where tenant_id = @t and line_no = 1"));
        Assert.Contains("DUE_DATE_REQUIRED", ex.MessageText);

        // terms that were deactivated are not named any more
        Assert.True((await new PaymentTermsCommands(s.Tenant.App).DeactivateAsync(s.Tenant.TenantId, s.Tenant.Context("td", "write.config"), new(s.Terms, 1, "OBSOLETE"))).Status == CommandStatus.Ok);
        var other = await new PaymentTermsCommands(s.Tenant.App).CreateAsync(s.Tenant.TenantId, s.Tenant.Context("t2", "write.config"),
            new(D(("code", "N60"), ("description", "60 days"), ("baseline_rule", "DOCUMENT_DATE"), ("net_days", 60))));
        Assert.Equal(["CONFIG.INVALID_REFERENCE"], Codes(await Change("c3", OI("payment_terms_id", s.Terms.ToString("D"), Guid.NewGuid().ToString()))));
        Assert.True((await Change("c4", OI("payment_terms_id", s.Terms.ToString("D"), other.Value!.EntityId.ToString("D")))).Status == CommandStatus.Ok);
    }
}
