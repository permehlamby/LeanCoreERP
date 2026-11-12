using System.Text;

namespace FinanceCore.Core.Reads;

/// <summary>The position a read response is consistent with (09, 2.1, 2.2): the time and the highest chain_seq per ledger of the company.</summary>
public sealed record ReadSnapshot(DateTime TakenAt, IReadOnlyList<LedgerPosition> Ledgers);

public sealed record LedgerPosition(Guid LedgerId, long LedgerSeq);

public sealed record ReadPage(string? NextCursor, int Limit);

/// <summary>An amount in local, group and reporting currency. Rc is null when the company has no reporting currency.</summary>
public sealed record Amounts(decimal Lc, decimal Gc, decimal? Rc);

public sealed record CurrencyCodes(string Lc, string Gc, string? Rc);

/// <summary>The limits and the opaque cursor of the list reads (09, 2.4, 2.5). A cursor is a kind and a number, so a cursor of another query is refused.</summary>
public static class ReadRules
{
    public const int DefaultLimit = 100, MaxLimit = 500, MaxGroups = 10_000;

    public static string EncodeCursor(string kind, long value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"v1|{kind}|{value}"));

    public static bool TryDecodeCursor(string? cursor, string kind, out long value)
    {
        value = 0;
        if (string.IsNullOrEmpty(cursor)) return true;
        try
        {
            string[] parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('|');
            return parts.Length == 3 && parts[0] == "v1" && parts[1] == kind && long.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out value) && value >= 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

// ---- journal-documents:get and :list (09, 3)
public sealed record GetJournalDocumentRequest(Guid DocumentId, bool IncludeLines = true, long? MinLedgerSeq = null);

public sealed record ListJournalDocumentsRequest(
    Guid CompanyId, string? DocumentType = null, int? FiscalYear = null, int? FiscalPeriod = null, DateOnly? PostingDateFrom = null, DateOnly? PostingDateTo = null,
    string? Status = null, string? NumberPrefix = null, int? Limit = null, string? Cursor = null, long? MinLedgerSeq = null, bool IncludeLines = false);

public sealed record JournalLineRead(
    int LineNo, string LineType, Guid AccountId, decimal AmountTc, string CurrencyTc, Amounts Amounts, int? ReversedLineNo, Guid? PartnerId, string? LineText);

public sealed record JournalDocumentRead(
    Guid DocumentId, Guid CompanyId, Guid LedgerId, string DocumentType, string DocumentNumber, int FiscalYear, int FiscalPeriod, DateOnly DocumentDate, DateOnly PostingDate,
    string TransactionCurrency, string Status, bool IsSystemGenerated, Guid? ReversalOfDocumentId, Guid? ReversedByDocumentId, string? ReversalReasonCode, string Origin,
    string? HeaderText, string? ExternalReference, long ChainSeq, string RowHash, string PostedBy, string CorrelationId, DateTime PostedAt, IReadOnlyList<JournalLineRead>? Lines);

public sealed record JournalDocumentResult(JournalDocumentRead Document, ReadSnapshot Snapshot);

public sealed record JournalDocumentList(IReadOnlyList<JournalDocumentRead> Items, ReadPage Page, ReadSnapshot Snapshot);

// ---- balances:query and trial-balances:query (09, 4.1, 4.2)
public sealed record BalancesRequest(
    Guid CompanyId, string Mode, DateOnly? AsOfDate = null, DateOnly? FromDate = null, DateOnly? ToDate = null, string? Rule = null, IReadOnlyList<string>? GroupBy = null,
    Guid? AccountId = null, string? AccountType = null, string? DocumentType = null, string? LineType = null, IReadOnlyList<string>? ExcludeDocumentTypes = null,
    long? MinLedgerSeq = null);

public sealed record BalanceRow(IReadOnlyDictionary<string, string?> Keys, Amounts Debit, Amounts Credit, Amounts Net, long LineCount);

public sealed record BalancesResult(
    string Mode, string Rule, CurrencyCodes Currencies, IReadOnlyList<BalanceRow> Rows, Amounts TotalDebit, Amounts TotalCredit, bool L2Stable, ReadSnapshot Snapshot);

public sealed record TrialBalanceRequest(
    Guid CompanyId, int FiscalYear, int FromPeriod, int ToPeriod, bool IncludeSpecialPeriods = true, long? MinLedgerSeq = null);

public sealed record TrialBalanceRow(Guid AccountId, string AccountNo, string Name, string AccountType, Amounts Opening, Amounts Debit, Amounts Credit, Amounts Closing);

/// <summary>Foots is false when the debit and credit totals differ in any currency. After a year-end close the rule AUTO leaves the P&amp;L accounts before the closing and
/// the retained earnings after it, so a trial balance that includes the closing period does not foot (F-IMPL-41).</summary>
public sealed record TrialBalanceResult(
    int FiscalYear, int FromPeriod, int ToPeriod, CurrencyCodes Currencies, IReadOnlyList<TrialBalanceRow> Rows, Amounts TotalDebit, Amounts TotalCredit, bool Foots, bool L2Stable,
    ReadSnapshot Snapshot);

// ---- account-ledgers:query (09, 4.3)
public sealed record AccountLedgerRequest(
    Guid CompanyId, Guid AccountId, DateOnly FromDate, DateOnly ToDate, string? DocumentType = null, string? LineType = null,
    int? Limit = null, string? Cursor = null, long? MinLedgerSeq = null);

public sealed record AccountLedgerLine(
    Guid DocumentId, string DocumentNumber, string DocumentType, long ChainSeq, int LineNo, DateOnly PostingDate, string LineType, string? LineText,
    decimal AmountTc, string CurrencyTc, Amounts Amounts, Amounts RunningBalance);

public sealed record AccountLedgerResult(
    Guid AccountId, string AccountNo, CurrencyCodes Currencies, Amounts OpeningBalance, IReadOnlyList<AccountLedgerLine> Lines, Amounts ClosingBalance, ReadPage Page, ReadSnapshot Snapshot);

// ---- open-items:query and partner-balances:query (09, 4.4, 4.6)
public sealed record OpenItemsRequest(
    Guid CompanyId, DateOnly? AsOfDate = null, Guid? AccountId = null, Guid? PartnerId = null, DateOnly? DueFrom = null, DateOnly? DueTo = null, string? Currency = null,
    bool IncludeCleared = false, int? Limit = null, string? Cursor = null, long? MinLedgerSeq = null);

public sealed record OpenItemRead(
    Guid DocumentId, int LineNo, string DocumentNumber, Guid AccountId, Guid? PartnerId, DateOnly PostingDate, DateOnly? DueDate, string CurrencyTc,
    decimal OriginalTc, decimal ClearedTc, decimal RemainingTc, Amounts Original, Amounts Cleared, Amounts Remaining, int DaysPastDue, string CachedStatus);

public sealed record OpenItemsResult(DateOnly AsOfDate, CurrencyCodes Currencies, IReadOnlyList<OpenItemRead> Items, ReadPage Page, ReadSnapshot Snapshot);

public sealed record PartnerBalancesRequest(
    Guid CompanyId, DateOnly? AsOfDate = null, Guid? AccountId = null, Guid? PartnerId = null, long? MinLedgerSeq = null);

public sealed record PartnerBalanceRow(Guid? PartnerId, Guid AccountId, Amounts Remaining, DateOnly? OldestDueDate, int OverdueItems, int ItemCount);

public sealed record PartnerBalancesResult(DateOnly AsOfDate, CurrencyCodes Currencies, IReadOnlyList<PartnerBalanceRow> Rows, ReadSnapshot Snapshot);
