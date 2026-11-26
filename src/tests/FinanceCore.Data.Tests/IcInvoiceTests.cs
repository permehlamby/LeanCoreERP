using FinanceCore.Core.Commands;
using FinanceCore.Core.Hashing;
using FinanceCore.Core.Masters;
using FinanceCore.Core.Reads;

namespace FinanceCore.Data.Tests;

/// <summary>ic-invoices:create (11, 6.7, ADR-016): the AR invoice of the seller and the AP invoice of the buyer as one atomic pair over an ic-orders:create, service lines.</summary>
[Collection("db")]
public class IcInvoiceTests
{
    private static string Describe<T>(CommandResult<T> r) => r.Status + ": " + string.Join(", ", r.Errors.Select(e => $"{e.Code} {e.Path}"));
    private static string[] Codes<T>(CommandResult<T> r) => r.Errors.Select(e => e.Code).ToArray();
    private static Dictionary<string, object?> D(params (string, object?)[] f) => f.ToDictionary(x => x.Item1, x => x.Item2);

    private sealed record Setup(
        TestTenant T, Guid Seller, Guid Buyer, Guid Customer, Guid Supplier, Guid Service, Guid Receivable, Guid Revenue, Guid Payable, Guid Expense, Guid IcPair);

    private static async Task<Guid> MasterAsync(TestTenant t, string resource, string key, Dictionary<string, object?> fields)
    {
        var r = await new InventoryMasterCommands(t.App).CreateAsync(resource, t.TenantId, t.Context(key, "write.config"), new CreateInventoryMasterRequest(fields));
        Assert.True(r.Status == CommandStatus.Ok, resource + " " + Describe(r));
        return r.Value!.EntityId;
    }

    private static async Task<Guid> PartnerAsync(TestTenant t, string key, string code, Guid company, bool customer, Guid icCompany)
    {
        var r = await new PartnerCommands(t.App).CreateAsync(t.TenantId, t.Context(key, "write.master"), new CreatePartnerRequest(
            D(("partner_code", code), ("partner_kind", "ORGANISATION"), ("is_customer", customer), ("is_supplier", !customer), ("ic_company_id", icCompany)),
            D(("legal_name", code + " A/S"), ("country_code", "DK")), [new PartnerRelationInput(company, D(("is_customer", customer), ("is_supplier", !customer)))]));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        return r.Value!.EntityId;
    }

    /// <summary>Two active DKK companies of one group (the seller is the company of the test tenant), the accounts and determinations of an AR and an AP invoice, a service item, the IC partners and an IC order pair of one service line (10 hours at 100, policy ORDERED).</summary>
    private static async Task<Setup> SetupAsync()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid buyer = Guid.NewGuid(), ledger2 = Guid.NewGuid(), receivable = Guid.NewGuid(), revenue = Guid.NewGuid(), payable = Guid.NewGuid(), expense = Guid.NewGuid();
        await t.ExecuteAsync(
            "insert into core.company (tenant_id, company_id, company_code, legal_name, country_code, local_currency, reporting_currency, fiscal_calendar_id, status) " +
            "select tenant_id, @n, 'C2', 'Buyer A/S', country_code, local_currency, reporting_currency, fiscal_calendar_id, 'ACTIVE' from core.company where tenant_id = @t and company_id = @c; " +
            "insert into core.company_setting_version (tenant_id, company_id, effective_from) values (@t, @n, timestamptz '2026-01-01 00:00:00+00'); " +
            "insert into core.retention_rule (tenant_id, company_id, rule_id, effective_from, record_class, country_code, retention_years, start_event, created_by) " +
            "select @t, @n, gen_random_uuid(), timestamptz '2000-01-01 00:00:00+00', v.cls, 'DK', v.years, v.start, 'test' from (values ('LEDGER', 10, 'FISCAL_YEAR_END'), ('LEGAL_SNAPSHOT', 5, 'RECORD_DATE')) v(cls, years, start); " +
            "insert into core.ledger (tenant_id, company_id, ledger_id, ledger_code) values (@t, @n, @l, 'L1'); " +
            "insert into core.group_membership (tenant_id, company_id, valid_from, group_id) select tenant_id, @n, date '2026-01-01', group_id from core.group_membership where tenant_id = @t and company_id = @c; " +
            "insert into core.fiscal_year (tenant_id, company_id, fiscal_year, calendar_id, start_date, end_date, opened_by) select @t, @n, 2026, fiscal_calendar_id, date '2026-01-01', date '2026-12-31', 'test' from core.company where tenant_id = @t and company_id = @n; " +
            "insert into core.fiscal_period (tenant_id, company_id, fiscal_year, period_no, period_type, start_date, end_date) select @t, @n, 2026, m, 'REGULAR', make_date(2026, m, 1), (make_date(2026, m, 1) + interval '1 month - 1 day')::date from generate_series(1, 12) m; " +
            "insert into core.chain_head (tenant_id, company_id, chain_type, scope_key, last_seq, last_hash, hash_version) values (@t, @n, 'LEDGER', @l::text, 0, @genesis, 2), (@t, @n, 'CLEARING', '', 0, @cgenesis, 2); " +
            // the rounding roles of the buyer (the seller has them from the test tenant)
            "insert into core.gl_account (tenant_id, company_id, account_id, account_no, name, account_type, tax_code_rule) values (@t, @n, gen_random_uuid(), '7900', 'Rounding', 'EXPENSE', 'FORBIDDEN'); " +
            "insert into core.account_role_assignment (tenant_id, company_id, role_code, account_id, effective_from) select @t, @n, 'ROUNDING', account_id, timestamptz '2026-01-01 00:00:00+00' from core.gl_account where tenant_id = @t and company_id = @n and account_no = '7900'; " +
            // the accounts of the invoices of both companies
            "insert into core.gl_account (tenant_id, company_id, account_id, account_no, name, account_type, tax_code_rule, open_item_mode) values " +
            "(@t, @c, @ar, '1300', 'Receivables', 'ASSET', 'FORBIDDEN', 'CONTROL'), (@t, @c, @rev, '3100', 'IC revenue', 'REVENUE', 'FORBIDDEN', 'NONE'), " +
            "(@t, @n, @ap, '2300', 'Payables', 'LIABILITY', 'FORBIDDEN', 'CONTROL'), (@t, @n, @ex, '4100', 'IC purchases', 'EXPENSE', 'FORBIDDEN', 'NONE'); " +
            // the series: journal documents of both companies (AR_INVOICE and AP_INVOICE), orders of both
            "insert into core.number_series (tenant_id, company_id, series_id, series_code, prefix, include_fiscal_year, separator, number_length, is_legal) values " +
            "(@t, @n, @s1, 'GL-JRN', 'GL', true, '-', 6, true), (@t, @c, @s2, 'ORD', 'SO', true, '-', 5, false), (@t, @n, @s3, 'ORD', 'SO', true, '-', 5, false); " +
            "insert into core.number_series_assignment (tenant_id, company_id, document_type, series_id, effective_from, created_by) select @t, @n, ty, @s1, date '2026-01-01', 'test' from unnest(array['JOURNAL', 'AR_INVOICE', 'AP_INVOICE', 'AR_CREDIT', 'AP_CREDIT']) ty; " +
            "insert into core.number_series_assignment (tenant_id, company_id, document_type, series_id, effective_from, created_by) select @t, @c, ty, @s2, date '2026-01-01', 'test' from unnest(array['SALES_ORDER', 'PURCHASE_ORDER']) ty; " +
            "insert into core.number_series_assignment (tenant_id, company_id, document_type, series_id, effective_from, created_by) select @t, @n, ty, @s3, date '2026-01-01', 'test' from unnest(array['SALES_ORDER', 'PURCHASE_ORDER']) ty; " +
            "insert into core.number_range_counter (tenant_id, company_id, series_id, fiscal_year, last_number) values (@t, @n, @s1, 2026, 0), (@t, @c, @s2, 2026, 0), (@t, @n, @s3, 2026, 0)",
            ("n", buyer), ("l", ledger2), ("genesis", LedgerHash2.Genesis(t.TenantId, buyer, "LEDGER", ledger2.ToString("D"))), ("cgenesis", LedgerHash2.Genesis(t.TenantId, buyer, "CLEARING", "")),
            ("ar", receivable), ("rev", revenue), ("ap", payable), ("ex", expense), ("s1", Guid.NewGuid()), ("s2", Guid.NewGuid()), ("s3", Guid.NewGuid()));

        var det = new AccountDeterminationCommands(t.App);
        foreach (var (company, role, account) in new[] { (t.CompanyId, "REVENUE", revenue), (t.CompanyId, "AR_CONTROL", receivable), (buyer, "AP_CONTROL", payable), (buyer, "PURCHASE_EXPENSE", expense) })
            Assert.Equal(CommandStatus.Ok, (await det.CreateAsync(t.TenantId, t.Context("det-" + role, "write.config"), new(company, D(("role_code", role), ("valid_from", "2026-01-01"), ("priority", 1), ("account_id", account))))).Status);

        Guid customer = await PartnerAsync(t, "cust", "BUYERCO", t.CompanyId, true, buyer), supplier = await PartnerAsync(t, "supp", "SELLERCO", buyer, false, t.CompanyId);
        Guid group = await MasterAsync(t, "item-groups", "grp", D(("code", "SV"), ("name", "Services")));
        Guid service = await MasterAsync(t, "items", "svc", D(("item_no", "S-1"), ("name", "Consulting"), ("item_type", "SERVICE"), ("base_unit", "HUR"), ("quantity_scale", 2), ("item_group_id", group)));
        foreach (Guid c in new[] { t.CompanyId, buyer }) await MasterAsync(t, "item-companies", "ic-" + c, D(("company_id", c), ("item_id", service), ("valuation_method", "MOVING_AVERAGE")));

        var order = await new IcOrderCommands(t.App).CreateAsync(t.TenantId, t.Context("ico", IcOrderRules.Permission), new CreateIcOrderRequest(
            t.CompanyId, buyer, new DateOnly(2026, 3, 10), "DKK", customer, supplier,
            [D(("line_ref", "L1"), ("item_id", service), ("quantity", 10m), ("unit_code", "HUR"), ("unit_price", 100m), ("fulfilment_type", "SERVICE"), ("invoice_policy", "ORDERED"), ("destination_country", "DK"), ("origin_country", "DK"))],
            ShipFromCountry: "DK"));
        Assert.True(order.Status == CommandStatus.Ok, Describe(order));
        return new Setup(t, t.CompanyId, buyer, customer, supplier, service, receivable, revenue, payable, expense, order.Value!.IcTransactionId);
    }

    private static CommandContext Ctx(Setup s, string key) => s.T.Context(key, IcInvoiceRules.Permission);

    private static CreateIcInvoiceRequest Request(Setup s, decimal quantity = 6m) =>
        new(s.IcPair, new DateOnly(2026, 3, 20), [new IcInvoiceLineInput("L1", 1, quantity)], SourceDocumentRef: "IC-INV-1");

    private static Task<decimal> SumAsync(Setup s, Guid company, Guid document, Guid account) =>
        s.T.ScalarAsync<decimal>("select coalesce(sum(amount_lc), 0) from core.journal_line where tenant_id = @t and company_id = @co and document_id = @d and account_id = @a", ("co", company), ("d", document), ("a", account));

    [Fact]
    public async Task APairOfInvoicesIsPostedWithTheMarkerOnControlAndBaseLinesAndTheLinkRows()
    {
        var s = await SetupAsync();
        var t = s.T;
        var r = await new IcInvoiceCommands(t.App).CreateAsync(t.TenantId, Ctx(s, "ici1"), Request(s));
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        var ar = r.Value!.SalesInvoice; var ap = r.Value.PurchaseInvoice;
        Assert.Equal(600m, ar.GrossAmount);
        Assert.Equal(0m, ar.TaxAmount);
        Assert.Equal(600m, ap.GrossAmount);

        // the amounts of the two sides: a receivable of 600 against revenue, an expense against a payable
        Assert.Equal((600m, -600m), (await SumAsync(s, s.Seller, ar.JournalDocumentId, s.Receivable), await SumAsync(s, s.Seller, ar.JournalDocumentId, s.Revenue)));
        Assert.Equal((600m, -600m), (await SumAsync(s, s.Buyer, ap.JournalDocumentId, s.Expense), await SumAsync(s, s.Buyer, ap.JournalDocumentId, s.Payable)));

        // both journals and both link rows carry the id; ORDER origin; the marker is on every BASE line (the control line is one) and on no other line
        Assert.Equal(2L, await t.ScalarAsync<long>("select count(*) from core.journal_document where tenant_id = @t and ic_transaction_id = @i and origin = 'ORDER' and reversal_of_document_id is null and document_type in ('AR_INVOICE', 'AP_INVOICE')", ("i", s.IcPair)));
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.sales_invoice where tenant_id = @t and ic_transaction_id = @i and company_id = @c", ("i", s.IcPair), ("c", s.Seller)));
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.purchase_invoice where tenant_id = @t and ic_transaction_id = @i and company_id = @b", ("i", s.IcPair), ("b", s.Buyer)));
        Assert.Equal(4L, await t.ScalarAsync<long>("select count(*) from core.journal_line where tenant_id = @t and ic_counterpart_company_id is not null"));
        Assert.Equal(0L, await t.ScalarAsync<long>("select count(*) from core.journal_line where tenant_id = @t and line_type <> 'BASE' and ic_counterpart_company_id is not null"));
        Assert.Equal(s.Buyer.ToString(), await t.ScalarAsync<string>("select ic_counterpart_company_id::text from core.journal_line where tenant_id = @t and document_id = @d and account_id = @a", ("d", ar.JournalDocumentId), ("a", s.Receivable)));
        Assert.Equal(s.Seller.ToString(), await t.ScalarAsync<string>("select ic_counterpart_company_id::text from core.journal_line where tenant_id = @t and document_id = @d and account_id = @a", ("d", ap.JournalDocumentId), ("a", s.Payable)));
        // the progress of both orders: 6 of 10 hours invoiced on each side
        Assert.Equal(6m, await t.ScalarAsync<decimal>("select coalesce(sum(base_quantity), 0) from core.sales_invoice_line where tenant_id = @t and company_id = @c", ("c", s.Seller)));
        Assert.Equal(6m, await t.ScalarAsync<decimal>("select coalesce(sum(base_quantity), 0) from core.purchase_invoice_line where tenant_id = @t and company_id = @b", ("b", s.Buyer)));

        // the chains verify with the marker in the hashes, the pair reconciles through the new read
        foreach (Guid company in new[] { s.Seller, s.Buyer })
        {
            Guid ledger = Guid.Parse(await t.ScalarAsync<string>("select ledger_id::text from core.ledger where tenant_id = @t and company_id = @co", ("co", company)));
            var verify = await new ChainVerification(t.App).VerifyAsync(t.TenantId, t.Context("v" + company, ChainVerification.Permission), new(company, "LEDGER", ledger.ToString("D")));
            Assert.True(verify.Value!.Ok, Describe(verify));
        }
        var rec = await new IcReconciliationReads(t.App).QueryAsync(t.TenantId, t.Context("rec", "read.ledger", IcReconciliationRules.CrossCompanyPermission), new(new DateOnly(2026, 6, 30), CompanyIds: [s.Seller, s.Buyer]));
        var pair = Assert.Single(rec.Value!.Pairs!);
        Assert.Equal(0m, pair.DifferenceGc);
        Assert.Equal(0m, pair.PnlDifferenceGc);
        Assert.Empty((await new IcReconciliationReads(t.App).QueryAsync(t.TenantId, t.Context("rec2", "read.ledger", IcReconciliationRules.CrossCompanyPermission), new(new DateOnly(2026, 6, 30), CompanyIds: [s.Seller, s.Buyer], View: "DOCUMENTS"))).Value!.Findings!);
    }

    [Fact]
    public async Task TheSameKeyReplaysAndTheQuantityIsCappedBySharedProgress()
    {
        var s = await SetupAsync();
        var cmds = new IcInvoiceCommands(s.T.App);
        var first = await cmds.CreateAsync(s.T.TenantId, Ctx(s, "ici2"), Request(s));
        var replay = await cmds.CreateAsync(s.T.TenantId, Ctx(s, "ici2"), Request(s));
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(first.Value!.SalesInvoice.JournalDocumentId, replay.Value!.SalesInvoice.JournalDocumentId);
        Assert.Equal(first.Value.PurchaseInvoice.JournalDocumentId, replay.Value.PurchaseInvoice.JournalDocumentId);
        Assert.Equal(["IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST"], Codes(await cmds.CreateAsync(s.T.TenantId, Ctx(s, "ici2"), Request(s, 5m))));

        // 4 hours are left: a second invoice for 4 works, one more hour is above the order on both sides
        var second = await cmds.CreateAsync(s.T.TenantId, Ctx(s, "ici3"), Request(s, 4m) with { SupplierInvoiceNumber = "SUP-2" });
        Assert.True(second.Status == CommandStatus.Ok, Describe(second));
        long before = await s.T.ScalarAsync<long>("select count(*) from core.journal_document where tenant_id = @t");
        var over = await cmds.CreateAsync(s.T.TenantId, Ctx(s, "ici4"), Request(s, 1m) with { SupplierInvoiceNumber = "SUP-3" });
        Assert.Equal(CommandStatus.Rejected, over.Status);
        Assert.Equal(before, await s.T.ScalarAsync<long>("select count(*) from core.journal_document where tenant_id = @t"));
    }

    [Fact]
    public async Task TheShapeThePairAndAFailingSideAreRefusedWithNothingKept()
    {
        var s = await SetupAsync();
        var cmds = new IcInvoiceCommands(s.T.App);
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await cmds.CreateAsync(s.T.TenantId, s.T.Context("a", "sales.invoice"), Request(s))));
        Assert.Equal(["ORDER.IC_ORDER_PAIR_NOT_FOUND"], Codes(await cmds.CreateAsync(s.T.TenantId, Ctx(s, "b"), Request(s) with { IcTransactionId = Guid.NewGuid() })));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await cmds.CreateAsync(s.T.TenantId, Ctx(s, "c"), Request(s, 0m))));
        Assert.Equal(["SCHEMA.REQUIRED_FIELD_MISSING"], Codes(await cmds.CreateAsync(s.T.TenantId, Ctx(s, "d"), Request(s) with { Lines = [] })));

        // the period of the seller is closed (the order of the two sides follows the company ids): nothing of the buyer is kept either
        await s.T.SetPeriodStatusAsync(3, "HARD_CLOSED");
        var closed = await cmds.CreateAsync(s.T.TenantId, Ctx(s, "e"), Request(s));
        Assert.Equal(CommandStatus.Rejected, closed.Status);
        Assert.Equal(0L, await s.T.ScalarAsync<long>("select count(*) from core.journal_document where tenant_id = @t"));
        Assert.Equal(0L, await s.T.ScalarAsync<long>("select count(*) from core.sales_invoice where tenant_id = @t"));
        Assert.Equal(0L, await s.T.ScalarAsync<long>("select count(*) from core.command_log where tenant_id = @t and command_name = 'ic-invoices:create'"));
    }
}
