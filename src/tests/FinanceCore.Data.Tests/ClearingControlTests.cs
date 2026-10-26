using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;
using FinanceCore.Core.Posting;

namespace FinanceCore.Data.Tests;

/// <summary>Clearing on CONTROL accounts (03, 5.1, 06, 7.1): the partner of the clearing, the items of one partner, the difference document
/// and its reset with the partner and the due date on the offset line.</summary>
[Collection("db")]
public class ClearingControlTests
{
    private static string Describe<T>(CommandResult<T> r) =>
        r.Status + ": " + string.Join(", ", r.Errors.Select(e => $"{e.Code} {e.Path}"));

    private static string[] Codes<T>(CommandResult<T> r) => r.Errors.Select(e => e.Code).ToArray();

    private static Dictionary<string, object?> D(params (string, object?)[] f) => f.ToDictionary(x => x.Item1, x => x.Item2);

    private sealed record Setup(TestTenant Tenant, Guid Control, Guid PartnerA, Guid PartnerB);

    private static async Task<Guid> NewPartner(TestTenant t, string key, string code)
    {
        var r = await new PartnerCommands(t.App).CreateAsync(t.TenantId, t.Context(key, "write.master"), new CreatePartnerRequest(
            D(("partner_code", code), ("partner_kind", "ORGANISATION"), ("is_customer", true)), D(("legal_name", code + " A/S"), ("country_code", "DK")),
            [new PartnerRelationInput(t.CompanyId, D(("is_customer", true)))]));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        return r.Value!.EntityId;
    }

    private static async Task<Setup> CreateAsync()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid control = Guid.NewGuid(), s1 = Guid.NewGuid(), s2 = Guid.NewGuid();
        await t.ExecuteAsync(
            "insert into core.gl_account (tenant_id, company_id, account_id, account_no, name, account_type, tax_code_rule, open_item_mode) " +
            "values (@t, @c, @a, '1300', 'Receivables', 'ASSET', 'FORBIDDEN', 'CONTROL'); " +
            "insert into core.exchange_rate (tenant_id, rate_type, from_currency, to_currency, valid_from, rate) values " +
            "(@t, 'SPOT', 'USD', 'DKK', date '2026-03-20', 7.2000000000), (@t, 'SPOT', 'USD', 'EUR', date '2026-03-20', 0.9200000000); " +
            "insert into core.number_series (tenant_id, company_id, series_id, series_code, prefix, include_fiscal_year, separator, number_length, is_legal) values " +
            "(@t, @c, @s1, 'GL-CLR', 'CL', true, '-', 6, true), (@t, @c, @s2, 'GL-CLRR', 'CR', true, '-', 6, true); " +
            "insert into core.number_series_assignment (tenant_id, company_id, document_type, series_id, effective_from, created_by) values " +
            "(@t, @c, 'CLEARING', @s1, date '2026-01-01', 'test'), (@t, @c, 'CLEARING_RESET', @s2, date '2026-01-01', 'test'); " +
            "insert into core.number_range_counter (tenant_id, company_id, series_id, fiscal_year, last_number) values (@t, @c, @s1, 2026, 0), (@t, @c, @s2, 2026, 0)",
            ("c", t.CompanyId), ("a", control), ("s1", s1), ("s2", s2));
        return new Setup(t, control, await NewPartner(t, "pa", "ALPHA"), await NewPartner(t, "pb", "BETA"));
    }

    private static async Task<Guid> PostOpen(Setup s, string key, Guid partner, decimal amount, string date = "2026-03-15", string currency = "DKK")
    {
        var req = s.Tenant.Journal(date, currency, amount) with
        {
            Lines =
            [
                new RequestLine("1", s.Control, amount, null, new OpenItemRequest(PartnerId: partner, DueDate: new DateOnly(2026, 4, 14))),
                new RequestLine("2", s.Tenant.Revenue, -amount)
            ]
        };
        var r = await new JournalDocumentPoster(s.Tenant.App).PostAsync(s.Tenant.TenantId, s.Tenant.Context(key), req);
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        return r.Value!.DocumentId;
    }

    private static Task<CommandResult<PostedClearing>> Clear(Setup s, string key, Guid? partner, Guid a, Guid b, string date = "2026-03-31") =>
        new ClearingPoster(s.Tenant.App).PostAsync(s.Tenant.TenantId, s.Tenant.Context(key, "clearing.post"),
            new PostClearingRequest(s.Tenant.CompanyId, s.Control, partner, DateOnly.Parse(date), "MANUAL", null, null,
                [new ClearingItemRequest(a, 1), new ClearingItemRequest(b, 1)]));

    private static async Task<string> Text(TestTenant t, string sql, params (string, object)[] p)
    {
        string q = sql.TrimStart().StartsWith("select", StringComparison.Ordinal) ? sql : "select " + sql;
        return await t.ScalarAsync<string>("select coalesce((" + q + ")::text, '<null>')", p);
    }

    private static async Task AssertChainsOk(Setup s)
    {
        var l = await new ChainVerifier(s.Tenant.App).VerifyLedgerAsync(s.Tenant.TenantId, s.Tenant.CompanyId, s.Tenant.LedgerId);
        Assert.True(l.Ok, "ledger: " + string.Join("; ", l.Problems.Select(p => p.Code + " " + p.Detail)));
        var c = await new ChainVerifier(s.Tenant.App).VerifyClearingsAsync(s.Tenant.TenantId, s.Tenant.CompanyId);
        Assert.True(c.Ok, "clearing: " + string.Join("; ", c.Problems.Select(p => p.Code + " " + p.Detail)));
    }

    [Fact]
    public async Task APartnersInvoiceAndPaymentClearEachOther()
    {
        var s = await CreateAsync();
        Guid a = await PostOpen(s, "a", s.PartnerA, 1000m), b = await PostOpen(s, "b", s.PartnerA, -1000m);

        var r = await Clear(s, "c1", s.PartnerA, a, b);

        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        Assert.Equal($"{s.PartnerA}|{s.Control}|CLEAR", await Text(s.Tenant, "partner_id || '|' || account_id || '|' || clearing_type from core.clearing where tenant_id = @t"));
        Assert.Equal("CLEARED|CLEARED", await Text(s.Tenant,
            "string_agg(clearing_status, '|') from core.journal_line_open_item where tenant_id = @t and document_id in (@a, @b) and line_no = 1", ("a", a), ("b", b)));
        // the partner is in the hash of the clearing (03, 6.1) and in the events
        Assert.Equal(r.Value!.RowHash, await ClearingDifferenceTests.RecomputeAsync(s.Tenant, r.Value.ClearingId, null));
        Assert.Equal(1L, await s.Tenant.ScalarAsync<long>("select count(*) from core.outbox_event where tenant_id = @t and event_type = 'clearing.posted' and summary ->> 'partner_id' = @p::text", ("p", s.PartnerA)));
        Assert.Equal(2L, await s.Tenant.ScalarAsync<long>("select count(*) from core.outbox_event where tenant_id = @t and event_type = 'open_item.cleared' and summary ->> 'partner_id' = @p::text", ("p", s.PartnerA)));
        await AssertChainsOk(s);
    }

    [Fact]
    public async Task ThePartnerIsRequiredAndEveryItemIsThatPartners()
    {
        var s = await CreateAsync();
        Guid a1 = await PostOpen(s, "a1", s.PartnerA, 1000m), a2 = await PostOpen(s, "a2", s.PartnerA, -1000m);
        Guid b1 = await PostOpen(s, "b1", s.PartnerB, -1000m);

        Assert.Equal(["ACCOUNT_RULE.PARTNER_REQUIRED"], Codes(await Clear(s, "e1", null, a1, a2)));
        Assert.Equal(["MASTER.PARTNER_NOT_FOUND"], Codes(await Clear(s, "e2", Guid.NewGuid(), a1, a2)));
        var mixed = await Clear(s, "e3", s.PartnerA, a1, b1);
        Assert.Equal(["CLEARING.ITEMS_NOT_SAME_PARTNER"], Codes(mixed));
        Assert.Equal("items[1]", mixed.Errors[0].Path);
        Assert.Equal(["CLEARING.ITEMS_NOT_SAME_PARTNER", "CLEARING.ITEMS_NOT_SAME_PARTNER"], Codes(await Clear(s, "e4", s.PartnerB, a1, a2)));
        Assert.Equal(0L, await s.Tenant.ScalarAsync<long>("select count(*) from core.clearing where tenant_id = @t"));
        Assert.Equal("OPEN", await Text(s.Tenant, "clearing_status from core.journal_line_open_item where tenant_id = @t and document_id = @d and line_no = 1", ("d", a1)));
    }

    [Fact]
    public async Task ABlockedOrRestrictedPartnerStillClears()
    {
        var s = await CreateAsync();
        Guid a = await PostOpen(s, "a", s.PartnerA, 1000m), b = await PostOpen(s, "b", s.PartnerA, -1000m);
        Assert.True((await new PartnerCommands(s.Tenant.App).DeactivateAsync(s.Tenant.TenantId, s.Tenant.Context("d", "write.master"), new(s.PartnerA, 1, "CREDIT"))).Status == CommandStatus.Ok);
        await s.Tenant.ExecuteAsync("update core.business_partner set restriction_status = 'RESTRICTED' where tenant_id = @t and partner_id = @p", ("p", s.PartnerA));

        var r = await Clear(s, "c1", s.PartnerA, a, b);   // a clearing closes existing obligations (12, 5.3)
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        // but a new open item for that partner is still refused
        var req = s.Tenant.Journal() with { Lines = [new RequestLine("1", s.Control, 5m, null, new OpenItemRequest(PartnerId: s.PartnerA, DueDate: new DateOnly(2026, 4, 1))), new RequestLine("2", s.Tenant.Revenue, -5m)] };
        Assert.Equal(["MASTER.PARTNER_INACTIVE"], Codes(await new JournalDocumentPoster(s.Tenant.App).PostAsync(s.Tenant.TenantId, s.Tenant.Context("n"), req)));
    }

    [Fact]
    public async Task TheOffsetLineOfADifferenceDocumentCarriesThePartnerAndADueDate()
    {
        var s = await CreateAsync();
        Guid inv = await PostOpen(s, "inv", s.PartnerA, 100m, "2026-01-05", "USD"), pay = await PostOpen(s, "pay", s.PartnerA, -100m, "2026-03-25", "USD");

        var r = await Clear(s, "c1", s.PartnerA, inv, pay);

        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        Guid doc = r.Value!.DocumentId!.Value;
        Assert.Equal($"{s.PartnerA}|2026-03-31|CLEARED|20.0000", await Text(s.Tenant,
            "partner_id || '|' || due_date || '|' || clearing_status || '|' || cleared_amount_lc from core.journal_line_open_item where tenant_id = @t and document_id = @d and line_no = 2", ("d", doc)));
        string docHash = await Text(s.Tenant, "encode(row_hash, 'hex') from core.journal_document where tenant_id = @t and document_id = @d", ("d", doc));
        Assert.Equal(r.Value.RowHash, await ClearingDifferenceTests.RecomputeAsync(s.Tenant, r.Value.ClearingId, Convert.FromHexString(docHash)));
        await AssertChainsOk(s);
    }

    [Fact]
    public async Task AResetKeepsThePartnerAndTheDueDateOnTheMirrorOfTheOffsetLine()
    {
        var s = await CreateAsync();
        Guid inv = await PostOpen(s, "inv", s.PartnerA, 100m, "2026-01-05", "USD"), pay = await PostOpen(s, "pay", s.PartnerA, -100m, "2026-03-25", "USD");
        var c = (await Clear(s, "c1", s.PartnerA, inv, pay)).Value!;
        await s.Tenant.SetPeriodStatusAsync(4, "OPEN");

        var reset = await new ClearingPoster(s.Tenant.App).ResetAsync(s.Tenant.TenantId, s.Tenant.Context("r1", "clearing.reset"),
            new ResetClearingRequest(s.Tenant.CompanyId, c.ClearingId, new DateOnly(2026, 4, 5), "CORRECTION", null));

        Assert.True(reset.Status == CommandStatus.Ok, Describe(reset));
        Guid mirror = reset.Value!.DocumentId!.Value;
        Assert.Equal($"{s.PartnerA}|2026-03-31|CLEARED", await Text(s.Tenant,
            "partner_id || '|' || due_date || '|' || clearing_status from core.journal_line_open_item where tenant_id = @t and document_id = @d and line_no = 2", ("d", mirror)));
        Assert.Equal($"RESET|{s.PartnerA}", await Text(s.Tenant, "clearing_type || '|' || partner_id from core.clearing where tenant_id = @t and clearing_id = @c", ("c", reset.Value.ClearingId)));
        Assert.Equal("OPEN|OPEN", await Text(s.Tenant,
            "string_agg(clearing_status, '|') from core.journal_line_open_item where tenant_id = @t and document_id in (@a, @b) and line_no = 1", ("a", inv), ("b", pay)));
        string mirrorHash = await Text(s.Tenant, "encode(row_hash, 'hex') from core.journal_document where tenant_id = @t and document_id = @d", ("d", mirror));
        Assert.Equal(reset.Value.RowHash, await ClearingDifferenceTests.RecomputeAsync(s.Tenant, reset.Value.ClearingId, Convert.FromHexString(mirrorHash)));
        await AssertChainsOk(s);

        // and the partner's two documents clear again
        var again = await Clear(s, "c2", s.PartnerA, inv, pay, "2026-04-10");
        Assert.True(again.Status == CommandStatus.Ok, Describe(again));
        await AssertChainsOk(s);
    }

    [Fact]
    public async Task AControlItemCanBeReversedAfterItsClearingWasReset()
    {
        var s = await CreateAsync();
        Guid a = await PostOpen(s, "a", s.PartnerA, 1000m), b = await PostOpen(s, "b", s.PartnerA, -1000m);
        var c = (await Clear(s, "c1", s.PartnerA, a, b)).Value!;
        Assert.Equal(["REVERSAL.HAS_CLEARED_LINES"], Codes(await new DocumentReverser(s.Tenant.App).ReverseAsync(s.Tenant.TenantId,
            s.Tenant.Context("rev0", "journal.reverse"), new(a, null, "CORRECTION", null))));
        await s.Tenant.SetPeriodStatusAsync(4, "OPEN");
        Assert.True((await new ClearingPoster(s.Tenant.App).ResetAsync(s.Tenant.TenantId, s.Tenant.Context("r1", "clearing.reset"),
            new ResetClearingRequest(s.Tenant.CompanyId, c.ClearingId, new DateOnly(2026, 4, 5), "ERROR", null))).Status == CommandStatus.Ok);
        var rev = await new DocumentReverser(s.Tenant.App).ReverseAsync(s.Tenant.TenantId, s.Tenant.Context("rev1", "journal.reverse"), new(a, new DateOnly(2026, 4, 6), "CORRECTION", null));
        Assert.True(rev.Status == CommandStatus.Ok, Describe(rev));
        await AssertChainsOk(s);
    }
}
