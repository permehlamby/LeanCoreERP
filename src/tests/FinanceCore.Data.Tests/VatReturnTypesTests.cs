using FinanceCore.Core.Chains;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;
using FinanceCore.Core.Posting;
using FinanceCore.Core.Roles;

namespace FinanceCore.Data.Tests;

/// <summary>The EC list, the OSS return, the tax evidence, the preview and the list of returns (08, 5.3, 6.1, 7.1, 7.4; built choices in 07, 13.2o).</summary>
[Collection("db")]
public class VatReturnTypesTests
{
    private static string Describe<T>(CommandResult<T> r) => r.Status + ": " + string.Join(", ", r.Errors.Select(e => $"{e.Code} {e.Path}"));
    private static string[] Codes<T>(CommandResult<T> r) => r.Errors.Select(e => e.Code).ToArray();
    private static Dictionary<string, object?> D(params (string, object?)[] f) => f.ToDictionary(x => x.Item1, x => x.Item2);
    private static CommandContext Cfg(TestTenant t, string key) => t.Context(key, "write.config");

    private static async Task CloseQuarter(TestTenant t, int quarter)
    {
        for (int m = (quarter - 1) * 3 + 1; m <= quarter * 3; m++) await t.SetPeriodStatusAsync(m, "SOFT_CLOSED");
    }

    private static async Task<Guid> NewPartner(TestTenant t, string key, string code, string country)
    {
        var req = new CreatePartnerRequest(
            D(("partner_code", code), ("partner_kind", "ORGANISATION"), ("is_customer", true), ("is_supplier", false)),
            D(("legal_name", code + " AB"), ("address_line_1", "Storgatan 1"), ("postal_code", "11122"), ("city", "Stockholm"), ("country_code", country)),
            [new PartnerRelationInput(t.CompanyId, D(("is_customer", true), ("is_supplier", false)))]);
        var r = await new PartnerCommands(t.App).CreateAsync(t.TenantId, t.Context(key, "write.master"), req);
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        return r.Value!.EntityId;
    }

    private sealed record Eu(Guid Registration, Guid Code, Guid Control);

    /// <summary>A quarterly registration that files an EC list, an intra-EU code that is on the list in the category GOODS and needs a transport document, and the rule for sales to Sweden.</summary>
    private static async Task<Eu> SetupEc(TestTenant t)
    {
        Guid payable = Guid.NewGuid(), control = Guid.NewGuid();
        await t.ExecuteAsync(
            "insert into core.gl_account (tenant_id, company_id, account_id, account_no, name, account_type, tax_code_rule) values (@t, @c, @p, '2700', 'VAT payable', 'LIABILITY', 'FORBIDDEN'); " +
            "insert into core.gl_account (tenant_id, company_id, account_id, account_no, name, account_type, tax_code_rule, open_item_mode) values (@t, @c, @k, '1300', 'Receivables', 'ASSET', 'FORBIDDEN', 'CONTROL'); " +
            "update core.gl_account set tax_code_rule = 'MANDATORY' where tenant_id = @t and account_id = @rev",
            ("c", t.CompanyId), ("p", payable), ("k", control), ("rev", t.Revenue));
        var tax = new TaxCommands(t.App);
        Assert.Equal(CommandStatus.Ok, (await tax.CreateBoxAsync(t.TenantId, Cfg(t, "box"), new("DK", "STANDARD", "B1", D(("name", "Supplies"))))).Status);
        var reg = await tax.CreateRegistrationAsync(t.TenantId, Cfg(t, "reg"), new(D(("company_id", t.CompanyId), ("country_code", "DK"), ("vat_id", "DK12345678"), ("scheme", "STANDARD"),
            ("filing_frequency", "QUARTERLY"), ("ec_list_frequency", "QUARTERLY"), ("correction_method", "ADJUST_NEXT"), ("return_currency", "DKK"), ("valid_from", "2026-01-01"))));
        Assert.True(reg.Status == CommandStatus.Ok, Describe(reg));
        var code = await tax.CreateCodeAsync(t.TenantId, Cfg(t, "code"), new(D(("company_id", t.CompanyId), ("code", "IC0"), ("description", "Intra-EU supply"), ("tax_type", "ZERO_RATED"), ("vat_registration_id", reg.Value!.EntityId))));
        var version = await tax.CreateVersionAsync(t.TenantId, Cfg(t, "version"), new(code.Value!.EntityId,
            D(("valid_from", "2026-01-01"), ("vat_account_id", payable), ("rate", 0m), ("en16931_vat_category", "K"), ("ec_list_category", "GOODS"), ("evidence_required_type", "TRANSPORT_DOCUMENT")),
            [new TaxBoxMapping("PRIMARY", "STANDARD", "B1", "BASE", -1)]));
        Assert.True(version.Status == CommandStatus.Ok, Describe(version));
        var rule = await new TaxDeterminationCommands(t.App).CreateRuleAsync(t.TenantId, Cfg(t, "rule"),
            new(t.CompanyId, D(("valid_from", "2026-01-01"), ("priority", 10), ("tax_code_id", code.Value.EntityId), ("transaction_type", "SALES"), ("destination_country", "SE")), []));
        Assert.True(rule.Status == CommandStatus.Ok, Describe(rule));
        return new Eu(reg.Value.EntityId, code.Value.EntityId, control);
    }

    private static async Task<PostedDocument> Invoice(TestTenant t, Eu eu, string key, Guid partner, decimal net, string date)
    {
        var r = await new JournalDocumentPoster(t.App).PostAsync(t.TenantId, t.Context(key),
            t.Journal(date) with
            {
                DocumentType = "AR_INVOICE", PartnerId = partner,
                Lines = [new RequestLine("1", eu.Control, net, null, new OpenItemRequest(PartnerId: partner, DueDate: new DateOnly(2026, 6, 30))), new RequestLine("2", t.Revenue, -net)]
            });
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        return r.Value!;
    }

    private static Task<CommandResult<VatReturnResult>> Prepare(TestTenant t, Guid registration, string type, string key, int period = 1) =>
        new VatReturnCommands(t.App).PrepareAsync(t.TenantId, t.Context(key, "vat.prepare"), new(registration, type, 2026, period));

    private static Task<CommandResult<ChangedDocument>> Change(TestTenant t, string key, Guid document, params FieldChange[] changes) =>
        new DocumentChanger(t.App).ChangeAsync(t.TenantId, t.Context(key, "journal.change"), new ChangeOpenFieldsRequest(document, changes, "CORRECTION", null));

    // ---------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task AnEcListHasOneRowPerPartnerCountryAndCategoryWithTheVatIdOfThePartner()
    {
        var t = await TestTenant.CreateReadyAsync();
        var eu = await SetupEc(t);
        Guid a = await NewPartner(t, "pa", "SEA", "SE"), b = await NewPartner(t, "pb", "SEB", "SE");
        var registration = await new TaxDeterminationCommands(t.App).CreateRegistrationAsync(t.TenantId, t.Context("preg", "write.master"),
            new(D(("partner_id", a), ("country_code", "SE"), ("valid_from", "2026-01-01"), ("vat_status", "REGISTERED"), ("vat_id", "SE556000000001"))));
        Assert.True(registration.Status == CommandStatus.Ok, Describe(registration));
        await Invoice(t, eu, "i1", a, 1000m, "2026-01-10");
        await Invoice(t, eu, "i2", a, 500m, "2026-02-10");
        await Invoice(t, eu, "i3", b, 300m, "2026-03-10");
        await Invoice(t, eu, "i4", a, 900m, "2026-05-10");   // another quarter
        await CloseQuarter(t, 1);

        var r = await Prepare(t, eu.Registration, "EC_LIST", "ec1");
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        var list = r.Value!;
        Assert.Equal(("EC_LIST", "ORIGINAL", 4L - 3L, 3), (list.ReturnType, list.ReturnKind, list.ChainSeq, list.TaxLineCount));
        Assert.Equal(2, list.Lines.Count);
        var rows = await t.ScalarAsync<long>("select count(*) from core.vat_return_line where tenant_id = @t and row_type = 'EC_LIST' and country_code = 'SE' and category = 'GOODS'", ("t", t.TenantId));
        Assert.Equal(2L, rows);
        Assert.Equal(1500m, await t.ScalarAsync<decimal>("select base_amount from core.vat_return_line where tenant_id = @t and partner_id = @p", ("t", t.TenantId), ("p", a)));
        Assert.Equal("SE556000000001", await t.ScalarAsync<string>("select partner_vat_id from core.vat_return_line where tenant_id = @t and partner_id = @p", ("t", t.TenantId), ("p", a)));
        Assert.Equal(300m, await t.ScalarAsync<decimal>("select base_amount from core.vat_return_line where tenant_id = @t and partner_id = @p", ("t", t.TenantId), ("p", b)));
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.vat_return_line where tenant_id = @t and partner_id = @p and partner_vat_id is null", ("t", t.TenantId), ("p", b)));

        // the standard return of the same period takes the same lines: a line is reported once per registration and return type
        var standard = await Prepare(t, eu.Registration, "STANDARD", "std1");
        Assert.True(standard.Status == CommandStatus.Ok, Describe(standard));
        Assert.Equal(3, standard.Value!.TaxLineCount);
        Assert.Equal(2L, standard.Value.ChainSeq);
        var chain = await new ChainVerification(t.App).VerifyAsync(t.TenantId, t.Context("verify", ChainVerification.Permission), new(t.CompanyId, "VAT_RETURN"));
        Assert.True(chain.Value!.Ok, Describe(chain) + string.Join(";", chain.Value.Breaks.Select(x => x.Detail)));
        // an EC list is not settled
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await new VatSettlement(t.App).SettleAsync(t.TenantId, t.Context("s", "vat.settle"), new(list.ReturnId, null))));
    }

    [Fact]
    public async Task AnEcListNeedsItsFrequencyAndAPartnerOnEveryLine()
    {
        var t = await TestTenant.CreateReadyAsync();
        var eu = await SetupEc(t);
        await t.ExecuteAsync("update core.company_vat_registration set ec_list_frequency = null where tenant_id = @t", ("t", t.TenantId));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await Prepare(t, eu.Registration, "EC_LIST", "x1")));
        await t.ExecuteAsync("update core.company_vat_registration set ec_list_frequency = 'QUARTERLY' where tenant_id = @t", ("t", t.TenantId));

        // a journal without a partner that carries an intra-EU code (a rule for a document without a transaction type)
        Assert.Equal(CommandStatus.Ok, (await new TaxDeterminationCommands(t.App).CreateRuleAsync(t.TenantId, Cfg(t, "rule2"),
            new(t.CompanyId, D(("valid_from", "2026-01-01"), ("priority", 5), ("tax_code_id", eu.Code), ("destination_country", "SE")), []))).Status);
        var partnerless = await new JournalDocumentPoster(t.App).PostAsync(t.TenantId, t.Context("j1"),
            t.Journal("2026-01-10") with { OriginCountry = "DK", DestinationCountry = "SE", Lines = [new RequestLine("1", t.Bank, 100m), new RequestLine("2", t.Revenue, -100m, TaxCodeId: eu.Code)] });
        Assert.True(partnerless.Status == CommandStatus.Ok, Describe(partnerless));
        await CloseQuarter(t, 1);
        Assert.Equal(["VAT.PARTNER_MISSING"], Codes(await Prepare(t, eu.Registration, "EC_LIST", "x2")));
        Assert.Equal(CommandStatus.Ok, (await Prepare(t, eu.Registration, "STANDARD", "x3")).Status);       // the standard return does not need a partner
    }

    [Fact]
    public async Task EvidenceIsAddedToATaxLineInEveryPeriodStatusAndNeverChanged()
    {
        var t = await TestTenant.CreateReadyAsync();
        var eu = await SetupEc(t);
        Guid a = await NewPartner(t, "pa", "SEA", "SE");
        var doc = await Invoice(t, eu, "i1", a, 1000m, "2026-01-10");
        int taxLine = doc.Lines.Single(l => l.LineType == "TAX").LineNo;
        FieldChange F(string field, string? value, int no = 1) => new("TAX_EVIDENCE", taxLine, field, null, value, no.ToString());

        // a new row has a type and a reference
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await Change(t, "e0", doc.DocumentId, F("evidence_type", "OTHER"))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await Change(t, "e0b", doc.DocumentId, F("evidence_type", "NONSENSE"), F("reference", "X"))));
        Assert.Equal(["DOCUMENT.TAX_LINE_NOT_FOUND"], Codes(await Change(t, "e0c", doc.DocumentId,
            new FieldChange("TAX_EVIDENCE", 1, "evidence_type", null, "OTHER", "1"), new FieldChange("TAX_EVIDENCE", 1, "reference", null, "X", "1"))));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await Change(t, "e0d", doc.DocumentId, F("evidence_type", "OTHER", 2), F("reference", "X", 2))));    // the next number is 1

        var added = await Change(t, "e1", doc.DocumentId, F("evidence_type", "TRANSPORT_DOCUMENT"), F("reference", "CMR-1"), F("evidence_date", "2026-01-12"), F("note", "signed"));
        Assert.True(added.Status == CommandStatus.Ok, Describe(added));
        Assert.Equal(4L, await t.ScalarAsync<long>("select count(*) from core.change_log where tenant_id = @t and entity_type = 'TAX_EVIDENCE' and sub_key = '1' and old_value is null", ("t", t.TenantId)));
        Assert.Equal("CMR-1", await t.ScalarAsync<string>("select reference from core.journal_line_tax_evidence where tenant_id = @t and document_id = @d and evidence_no = 1", ("t", t.TenantId), ("d", doc.DocumentId)));
        Assert.Equal(1L, await t.ScalarAsync<long>("select count(*) from core.outbox_event where tenant_id = @t and event_type = 'vat.evidence_added'", ("t", t.TenantId)));
        Assert.True((await Change(t, "e1", doc.DocumentId, F("evidence_type", "TRANSPORT_DOCUMENT"), F("reference", "CMR-1"), F("evidence_date", "2026-01-12"), F("note", "signed"))).IdempotentReplay);

        // a row is never changed: a correction is the next row
        Assert.Equal(["LOCK.FIELD_LOCKED_L1"], Codes(await Change(t, "e2", doc.DocumentId, new FieldChange("TAX_EVIDENCE", taxLine, "reference", "CMR-1", "CMR-2", "1"))));
        Assert.Contains("LOCK.FIELD_LOCKED_L1", Codes(await Change(t, "e3", doc.DocumentId, F("evidence_type", "OTHER"), F("reference", "again"))));
        Assert.Equal(CommandStatus.Ok, (await Change(t, "e4", doc.DocumentId, F("evidence_type", "OTHER", 2), F("reference", "corrects CMR-1", 2))).Status);
        await Assert.ThrowsAnyAsync<Exception>(() => t.ExecuteAsync("update core.journal_line_tax_evidence set reference = 'x' where tenant_id = @t", ("t", t.TenantId)));
        await Assert.ThrowsAnyAsync<Exception>(() => t.ExecuteAsync("delete from core.journal_line_tax_evidence where tenant_id = @t", ("t", t.TenantId)));

        // in a soft-closed and in a hard-closed period too, but other fields of a hard-closed period stay locked
        await t.SetPeriodStatusAsync(1, "SOFT_CLOSED");
        Assert.Equal(CommandStatus.Ok, (await Change(t, "e5", doc.DocumentId, F("evidence_type", "PARTNER_VAT_CHECK", 3), F("reference", "VIES-1", 3))).Status);
        await t.SetPeriodStatusAsync(1, "HARD_CLOSED");
        var hard = await Change(t, "e6", doc.DocumentId, F("evidence_type", "PARTNER_VAT_CHECK", 4), F("reference", "VIES-2", 4));
        Assert.True(hard.Status == CommandStatus.Ok, Describe(hard));
        Assert.Equal("HARD_CLOSED", hard.Value!.PeriodStatus);
        Assert.Equal(["LOCK.FIELD_LOCKED_L3"], Codes(await Change(t, "e7", doc.DocumentId, new FieldChange("DOCUMENT", null, "header_text", null, "x"))));
        Assert.Equal(["LOCK.FIELD_LOCKED_L3"], Codes(await Change(t, "e8", doc.DocumentId, new FieldChange("DOCUMENT", null, "header_text", null, "x"), F("evidence_type", "OTHER", 5), F("reference", "mixed", 5))));
    }

    [Fact]
    public async Task AReturnWarnsOfTaxLinesThatLackTheEvidenceTheirCodeRequires()
    {
        var t = await TestTenant.CreateReadyAsync();
        var eu = await SetupEc(t);
        Guid a = await NewPartner(t, "pa", "SEA", "SE");
        var proven = await Invoice(t, eu, "i1", a, 1000m, "2026-01-10");
        var unproven = await Invoice(t, eu, "i2", a, 500m, "2026-01-20");
        int line = proven.Lines.Single(l => l.LineType == "TAX").LineNo;
        Assert.Equal(CommandStatus.Ok, (await Change(t, "e1", proven.DocumentId,
            new FieldChange("TAX_EVIDENCE", line, "evidence_type", null, "TRANSPORT_DOCUMENT", "1"), new FieldChange("TAX_EVIDENCE", line, "reference", null, "CMR-1", "1"))).Status);
        await CloseQuarter(t, 1);

        var r = await Prepare(t, eu.Registration, "STANDARD", "w1");
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        Assert.Equal(2, r.Value!.TaxLineCount);
        var warning = Assert.Single(r.Warnings);
        Assert.Equal("VAT.EVIDENCE_MISSING", warning.Code);
        Assert.Equal(unproven.DocumentId, warning.Params!["document_id"]);
    }

    [Fact]
    public async Task APreviewShowsTheSnapshotAndStoresNothing()
    {
        var t = await TestTenant.CreateReadyAsync();
        var eu = await SetupEc(t);
        Guid a = await NewPartner(t, "pa", "SEA", "SE");
        await Invoice(t, eu, "i1", a, 1000m, "2026-01-10");
        await CloseQuarter(t, 1);
        var vat = new VatReturnCommands(t.App);

        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await vat.PreviewAsync(t.TenantId, t.Context("p0", "vat.settle"), new(eu.Registration, "STANDARD", 2026, 1))));
        var preview = await vat.PreviewAsync(t.TenantId, t.Context("p1", "vat.prepare"), new(eu.Registration, "STANDARD", 2026, 1));
        Assert.True(preview.Status == CommandStatus.Ok, Describe(preview));
        Assert.Equal((1, "ORIGINAL", 1L), (preview.Value!.TaxLineCount, preview.Value.ReturnKind, preview.Value.ChainSeq));
        Assert.Equal(1000m, preview.Value.Lines.Single().BaseAmount);
        Assert.Equal(0L, await t.ScalarAsync<long>("select count(*) from core.vat_return where tenant_id = @t", ("t", t.TenantId)));
        Assert.Equal(0L, await t.ScalarAsync<long>("select count(*) from core.command_log where tenant_id = @t and command_name = 'vat-returns:prepare'", ("t", t.TenantId)));

        // the same key and request can be prepared afterwards and the preview used no number of the chain
        var real = await Prepare(t, eu.Registration, "STANDARD", "p1");
        Assert.Equal(1L, real.Value!.ChainSeq);
        Assert.Equal(["VAT.ALREADY_PREPARED"], Codes(await vat.PreviewAsync(t.TenantId, t.Context("p2", "vat.prepare"), new(eu.Registration, "STANDARD", 2026, 1))));
    }

    [Fact]
    public async Task AnOssReturnHasOneRowPerCountryRateAndKindAndIsSettledLikeAStandardReturn()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid payable = Guid.NewGuid(), services = Guid.NewGuid(), settlement = Guid.NewGuid(), series = Guid.NewGuid();
        await t.ExecuteAsync(
            "insert into core.gl_account (tenant_id, company_id, account_id, account_no, name, account_type, tax_code_rule) values " +
            "(@t, @c, @p, '2700', 'OSS payable', 'LIABILITY', 'FORBIDDEN'), (@t, @c, @sv, '3100', 'Services', 'REVENUE', 'MANDATORY'), (@t, @c, @s, '2790', 'OSS settlement', 'LIABILITY', 'FORBIDDEN'); " +
            "update core.gl_account set tax_code_rule = 'MANDATORY' where tenant_id = @t and account_id = @rev; " +
            "insert into core.number_series (tenant_id, company_id, series_id, series_code, prefix, include_fiscal_year, separator, number_length, is_legal) values (@t, @c, @ser, 'GL-VAT', 'VS', true, '-', 6, true); " +
            "insert into core.number_series_assignment (tenant_id, company_id, document_type, series_id, effective_from, created_by) values (@t, @c, 'VAT_SETTLE', @ser, date '2026-01-01', 'test'); " +
            "insert into core.number_range_counter (tenant_id, company_id, series_id, fiscal_year, last_number) values (@t, @c, @ser, 2026, 0)",
            ("c", t.CompanyId), ("p", payable), ("sv", services), ("s", settlement), ("ser", series), ("rev", t.Revenue));
        var tax = new TaxCommands(t.App);
        var reg = await tax.CreateRegistrationAsync(t.TenantId, Cfg(t, "reg"), new(D(("company_id", t.CompanyId), ("country_code", "DK"), ("vat_id", "DK12345678"), ("scheme", "OSS_UNION"),
            ("filing_frequency", "QUARTERLY"), ("correction_method", "ADJUST_NEXT"), ("return_currency", "DKK"), ("valid_from", "2026-01-01"))));
        Assert.True(reg.Status == CommandStatus.Ok, Describe(reg));
        async Task Code(string code, string kind, Guid account)
        {
            var c = await tax.CreateCodeAsync(t.TenantId, Cfg(t, "c-" + code), new(D(("company_id", t.CompanyId), ("code", code), ("description", code), ("tax_type", "OUTPUT"), ("vat_registration_id", reg.Value!.EntityId))));
            var v = await tax.CreateVersionAsync(t.TenantId, Cfg(t, "v-" + code), new(c.Value!.EntityId, D(("valid_from", "2026-01-01"), ("vat_account_id", payable), ("rate", 25m), ("supply_kind", kind)), []));
            Assert.True(v.Status == CommandStatus.Ok, Describe(v));
            var rule = await new TaxDeterminationCommands(t.App).CreateRuleAsync(t.TenantId, Cfg(t, "r-" + code),
                new(t.CompanyId, D(("valid_from", "2026-01-01"), ("priority", 10), ("tax_code_id", c.Value.EntityId), ("account_id", account), ("destination_country", "SE")), []));
            Assert.True(rule.Status == CommandStatus.Ok, Describe(rule));
        }
        await Code("OSS-G", "GOODS", t.Revenue);
        await Code("OSS-S", "SERVICES", services);
        Assert.Equal(CommandStatus.Ok, (await new AccountRoleAssigner(t.App).AssignAsync(t.TenantId, Cfg(t, "role"), new(t.CompanyId, "VAT_SETTLEMENT", settlement, VatRegistrationId: reg.Value!.EntityId))).Status);

        async Task Sale(string key, Guid account, decimal net, string date)
        {
            var r = await new JournalDocumentPoster(t.App).PostAsync(t.TenantId, t.Context(key), t.Journal(date) with
            {
                OriginCountry = "DK", DestinationCountry = "SE", Lines = [new RequestLine("1", t.Bank, net * 1.25m), new RequestLine("2", account, -net)]
            });
            Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        }
        await Sale("g1", t.Revenue, 1000m, "2026-01-10");
        await Sale("g2", t.Revenue, 200m, "2026-02-10");
        await Sale("s1", services, 400m, "2026-03-10");
        await CloseQuarter(t, 1);

        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(await Prepare(t, reg.Value.EntityId, "STANDARD", "wrong")));       // the scheme files OSS
        var r = await Prepare(t, reg.Value.EntityId, "OSS", "oss1");
        Assert.True(r.Status == CommandStatus.Ok, Describe(r));
        Assert.Equal(3, r.Value!.TaxLineCount);
        Assert.Equal(2, r.Value.Lines.Count);
        Assert.Equal(1200m, await t.ScalarAsync<decimal>("select base_amount from core.vat_return_line where tenant_id = @t and supply_kind = 'GOODS' and country_code = 'SE' and rate = 25", ("t", t.TenantId)));
        Assert.Equal(300m, await t.ScalarAsync<decimal>("select tax_amount from core.vat_return_line where tenant_id = @t and supply_kind = 'GOODS'", ("t", t.TenantId)));
        Assert.Equal(100m, await t.ScalarAsync<decimal>("select tax_amount from core.vat_return_line where tenant_id = @t and supply_kind = 'SERVICES'", ("t", t.TenantId)));

        var settled = await new VatSettlement(t.App).SettleAsync(t.TenantId, t.Context("settle", "vat.settle"), new(r.Value.ReturnId, null));
        Assert.True(settled.Status == CommandStatus.Ok, Describe(settled));
        Assert.Equal(-400m, settled.Value!.Document.Lines.Single(l => l.AccountId == settlement).AmountLc);
        var chain = await new ChainVerification(t.App).VerifyAsync(t.TenantId, t.Context("verify", ChainVerification.Permission), new(t.CompanyId, "VAT_RETURN"));
        Assert.True(chain.Value!.Ok, Describe(chain));
    }

    [Fact]
    public async Task ReturnsAreListedByRegistrationTypeAndYear()
    {
        var t = await TestTenant.CreateReadyAsync();
        var eu = await SetupEc(t);
        Guid a = await NewPartner(t, "pa", "SEA", "SE");
        await Invoice(t, eu, "i1", a, 1000m, "2026-01-10");
        await CloseQuarter(t, 1);
        await Prepare(t, eu.Registration, "STANDARD", "l1");
        await Prepare(t, eu.Registration, "EC_LIST", "l2");
        var vat = new VatReturnCommands(t.App);
        var all = await vat.ListAsync(t.TenantId, t.Context("g1", "vat.read"), new(null, null, null));
        Assert.Equal(["STANDARD", "EC_LIST"], all.Value!.Items.Select(i => i.ReturnType).ToArray());
        Assert.Equal(["EC_LIST"], (await vat.ListAsync(t.TenantId, t.Context("g2", "vat.read"), new(eu.Registration, "EC_LIST", 2026))).Value!.Items.Select(i => i.ReturnType).ToArray());
        Assert.Empty((await vat.ListAsync(t.TenantId, t.Context("g3", "vat.read"), new(null, null, 2025))).Value!.Items);
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(await vat.ListAsync(t.TenantId, t.Context("g4", "vat.prepare"), new(null, null, null))));
    }
}
