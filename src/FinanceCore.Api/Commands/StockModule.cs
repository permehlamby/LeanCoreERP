using FinanceCore.Api.Http;
using FinanceCore.Core.Masters;
using FinanceCore.Data;

namespace FinanceCore.Api.Commands;

/// <summary>The stock ledger (10, 6.1, 8): stock-documents:post (stock.post), stock-documents:get and :list, and stock-balances:query (read.inventory).</summary>
public sealed class StockModule : ICommandModule
{
    public void Register(CommandCatalog catalog)
    {
        const string read = StockValuation.ReadPermission;
        catalog.Add<PostStockDocumentRequest, StockDocumentResult>("stock-documents:post", StockValuation.PostPermission, (sp, t, c, r, ct) => sp.GetRequiredService<StockCommands>().PostAsync(t, c, r, ct));
        catalog.Add<GetStockDocumentRequest, StockDocumentDetail>("stock-documents:get", read, (sp, t, c, r, ct) => sp.GetRequiredService<StockReads>().GetAsync(t, c, r, ct));
        catalog.Add<ListStockDocumentsRequest, StockDocumentList>("stock-documents:list", read, (sp, t, c, r, ct) => sp.GetRequiredService<StockReads>().ListAsync(t, c, r, ct));
        catalog.Add<StockBalancesRequest, StockBalancesResult>("stock-balances:query", read, (sp, t, c, r, ct) => sp.GetRequiredService<StockReads>().BalancesAsync(t, c, r, ct));
    }
}
