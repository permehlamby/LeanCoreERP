using FinanceCore.Api.Http;
using FinanceCore.Core.Reads;
using FinanceCore.Data;

namespace FinanceCore.Api.Commands;

/// <summary>The read operations of the ledger (09): documents, balances, trial balance, account ledger, open items and partner balances. All need read.ledger.</summary>
public sealed class ReadModule : ICommandModule
{
    public void Register(CommandCatalog catalog)
    {
        const string p = LedgerReads.Permission;
        catalog.Add<GetJournalDocumentRequest, JournalDocumentResult>("journal-documents:get", p, (sp, t, c, r, ct) => sp.GetRequiredService<LedgerReads>().GetDocumentAsync(t, c, r, ct));
        catalog.Add<ListJournalDocumentsRequest, JournalDocumentList>("journal-documents:list", p, (sp, t, c, r, ct) => sp.GetRequiredService<LedgerReads>().ListDocumentsAsync(t, c, r, ct));
        catalog.Add<BalancesRequest, BalancesResult>("balances:query", p, (sp, t, c, r, ct) => sp.GetRequiredService<LedgerReads>().BalancesAsync(t, c, r, ct));
        catalog.Add<TrialBalanceRequest, TrialBalanceResult>("trial-balances:query", p, (sp, t, c, r, ct) => sp.GetRequiredService<LedgerReads>().TrialBalanceAsync(t, c, r, ct));
        catalog.Add<AccountLedgerRequest, AccountLedgerResult>("account-ledgers:query", p, (sp, t, c, r, ct) => sp.GetRequiredService<LedgerReads>().AccountLedgerAsync(t, c, r, ct));
        catalog.Add<OpenItemsRequest, OpenItemsResult>("open-items:query", p, (sp, t, c, r, ct) => sp.GetRequiredService<LedgerReads>().OpenItemsAsync(t, c, r, ct));
        catalog.Add<PartnerBalancesRequest, PartnerBalancesResult>("partner-balances:query", p, (sp, t, c, r, ct) => sp.GetRequiredService<LedgerReads>().PartnerBalancesAsync(t, c, r, ct));
    }
}
