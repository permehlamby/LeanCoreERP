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
        catalog.Add<AgingRequest, AgingResult>("aging:query", p, (sp, t, c, r, ct) => sp.GetRequiredService<LedgerReads>().AgingAsync(t, c, r, ct));
        catalog.Add<DocumentContextRequest, DocumentContextResult>("documents:get-context", p, (sp, t, c, r, ct) => sp.GetRequiredService<LedgerReads>().GetDocumentContextAsync(t, c, r, ct));

        // the audit reads (09, 4.8, 4.9, 5.2): read.audit
        const string audit = AuditReads.Permission;
        catalog.Add<ChainsStatusRequest, ChainsStatusResult>("chains:status", audit, (sp, t, c, r, ct) => sp.GetRequiredService<AuditReads>().ChainsStatusAsync(t, c, r, ct));
        catalog.Add<AuditEventsRequest, AuditEventList>("audit-events:query", audit, (sp, t, c, r, ct) => sp.GetRequiredService<AuditReads>().AuditEventsAsync(t, c, r, ct));
        catalog.Add<ReadAccessLogRequest, ReadAccessLogList>("read-access-log:list", audit, (sp, t, c, r, ct) => sp.GetRequiredService<AuditReads>().ReadAccessLogAsync(t, c, r, ct));

        // the partner reads (09, 3, 5): read.config, and read.pii for the personal data
        const string config = PartnerReadRules.ConfigPermission;
        catalog.Add<GetPartnerRequest, PartnerResult>("business-partners:get", config, (sp, t, c, r, ct) => sp.GetRequiredService<PartnerReads>().GetAsync(t, c, r, ct));
        catalog.Add<ListPartnersRequest, PartnerList>("business-partners:list", config, (sp, t, c, r, ct) => sp.GetRequiredService<PartnerReads>().ListAsync(t, c, r, ct));
    }
}
