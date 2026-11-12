using System.Text.Json;
using FinanceCore.Core.Migration;

namespace FinanceCore.Core.Tests;

public class MigrationModelTests
{
    private static readonly Guid Clearing = Guid.Parse("00000000-0000-0000-0000-0000000000c1");
    private static Guid A(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:D12}");

    private static PlannedItem Item(int account, string ccy, decimal tc, decimal lc, decimal gc, string doc) =>
        new(A(account), account == 1 ? Guid.Parse("00000000-0000-0000-0000-0000000000aa") : null, ccy, tc, lc, gc, gc, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 2), new DateOnly(2026, 4, 1), null, doc);

    [Fact]
    public void EveryPlannedDocumentBalancesInItsOwnTransactionCurrency()
    {
        var docs = MigrationPlan.ItemDocuments(
            [Item(1, "DKK", 1200m, 1200m, 160.8m, "A"), Item(1, "USD", 100m, 700m, 94m, "B"), Item(2, "DKK", 100m, 100m, 13.4m, "C")], Clearing, true, 5000);
        Assert.Equal(["DKK", "USD"], docs.Select(d => d.Currency).ToArray());
        foreach (var d in docs)
        {
            Assert.Equal(0m, d.Lines.Sum(l => l.Tc));
            Assert.Equal(0m, d.Lines.Sum(l => l.Lc));
            Assert.Equal(0m, d.Lines.Sum(l => l.Gc));
            Assert.Equal(0m, d.Lines.Sum(l => l.Rc!.Value));
            Assert.Equal(Clearing, d.Lines[^1].AccountId);
            Assert.All(d.Lines.Take(d.Lines.Count - 1), l => Assert.NotNull(l.Item));
        }
        Assert.Equal(2, docs[0].Lines.Count - 1);
    }

    [Fact]
    public void ADocumentIsSplitByTheLineLimitAndEachPartKeepsItsCounterLine()
    {
        var items = Enumerable.Range(1, 7).Select(i => Item(2, "DKK", i, i, i, "D" + i)).ToList();
        var docs = MigrationPlan.ItemDocuments(items, Clearing, false, 4);   // at most 4 lines of which one is the counter line
        Assert.Equal([4, 4, 2], docs.Select(d => d.Lines.Count).ToArray());
        Assert.All(docs, d => Assert.Equal(0m, d.Lines.Sum(l => l.Lc)));
        Assert.All(docs.SelectMany(d => d.Lines), l => Assert.Null(l.Rc));
    }

    [Fact]
    public void OpeningDocumentsLeaveOutZeroTargetsAndNetAgainstTheClearingAccount()
    {
        var docs = MigrationPlan.OpeningDocuments([(A(10), 8000m, 1072m, 1072m), (A(11), -10000m, -1340.2m, -1340.2m), (A(12), 0m, 0m, 0m)], Clearing, "DKK", true, 5000);
        var doc = Assert.Single(docs);
        Assert.Equal(("OPENING", "DKK", 3), (doc.DocumentType, doc.Currency, doc.Lines.Count));
        Assert.Equal((2000m, 268.2m), (doc.Lines[^1].Lc, doc.Lines[^1].Gc));
        Assert.Empty(MigrationPlan.OpeningDocuments([(A(10), 0m, 0m, 0m)], Clearing, "DKK", true, 5000));
    }

    [Theory]
    [InlineData(100, 700, 7)]
    [InlineData(0, 700, 1)]
    [InlineData(100, 0, 1)]
    [InlineData(-100, -700, 7)]
    [InlineData(3, 1, 0.3333333333)]
    public void ImpliedRatesAreTheRatioOfTheAbsoluteAmountsAndNeverZero(double tc, double amount, double expected) =>
        Assert.Equal((decimal)expected, MigrationPlan.Implied((decimal)tc, (decimal)amount));

    [Fact]
    public void RecordsAreParsedByTheirTypeWithKnownFieldsOnly()
    {
        static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();
        var (ok, e1) = MigrationRecords.Parse(RecordTypes.OpeningBalance, J("""{ "account_no": "1000", "balance_lc": 5, "balance_gc": 1 }"""));
        Assert.Empty(e1);
        Assert.IsType<OpeningBalancePayload>(ok);
        Assert.Equal("SCHEMA.INVALID_VALUE", MigrationRecords.Parse(RecordTypes.OpeningBalance, J("""{ "account_no": "1000" }""")).Errors[0].Code);
        Assert.Equal("SCHEMA.INVALID_VALUE", MigrationRecords.Parse(RecordTypes.OpeningBalance, J("""{ "account_no": "1000", "balance_lc": 1, "balance_gc": 1, "x": 1 }""")).Errors[0].Code);
        Assert.Equal("MIGRATION.RECORD_TYPE_INVALID", MigrationRecords.Parse("STOCK_OPENING_LINE", J("{}")).Errors[0].Code);
        var item = """{ "account_no": "1300", "partner_code": "P", "document_date": "2026-03-01", "posting_date": "2026-03-02", "currency": "DKK", "amount_tc": 10, "amount_lc": -10, "amount_gc": 1, "legacy_document_ref": "X" }""";
        Assert.Contains(MigrationRecords.Parse(RecordTypes.OpenItem, J(item)).Errors, e => e.Path == "/amount_lc");   // the signs disagree
        Assert.Contains(MigrationRecords.Parse(RecordTypes.GlOpenItem, J(item.Replace("-10", "10"))).Errors, e => e.Path == "/partner_code");
    }

    [Fact]
    public void TheReconciliationHasNoToleranceAndNamesTheCheckThatFails()
    {
        AccountAmounts Acc(string no, decimal lc) => new(no, lc, lc / 10m, lc / 10m);
        var tb = new[] { Acc("1000", 100m), Acc("1300", 50m), Acc("2000", -150m) };
        var balances = new[] { Acc("1000", 100m), Acc("2000", -150m) };
        var items = new[] { Acc("1300", 50m) };
        var sub = new HashSet<string> { "1300" };

        var ok = MigrationReconciliation.Run(0, tb, [Acc("1300", 50m)], balances, items, sub, true);
        Assert.True(MigrationReconciliation.Passed(ok));
        Assert.Contains(ok, r => r.CheckCode == "CLEARING_ZERO" && r.Result == "PASS");

        var off = MigrationReconciliation.Run(1, tb, [Acc("1300", 51m)], balances, [Acc("1300", 49m)], sub, true);
        string[] failing = off.Where(r => r.Result == "FAIL").Select(r => r.CheckCode).ToArray();
        Assert.Contains("RECORDS_VALID", failing);
        Assert.Contains("OPEN_ITEMS_VS_DECLARED", failing);
        Assert.Contains("OPEN_ITEMS_VS_TB", failing);
        Assert.Contains("CLEARING_ZERO", failing);
        Assert.Contains("ZERO_TOLERANCE", failing);

        var missing = MigrationReconciliation.Run(0, tb, [], [Acc("1000", 100m)], [], sub, true);
        Assert.Contains(missing, r => r.CheckCode == "TB_ACCOUNTS_COMPLETE" && r.Key == "2000");
    }
}
