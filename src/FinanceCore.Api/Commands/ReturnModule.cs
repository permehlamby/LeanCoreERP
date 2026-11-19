using FinanceCore.Api.Http;
using FinanceCore.Core.Masters;
using FinanceCore.Data;

namespace FinanceCore.Api.Commands;

/// <summary>The returns of delivered and received goods (11, 5.4): sales-returns:post (sales.return) and purchase-returns:post (purchase.return).</summary>
public sealed class ReturnModule : ICommandModule
{
    public void Register(CommandCatalog catalog)
    {
        catalog.Add<PostSalesReturnRequest, FulfilmentResult>("sales-returns:post", ReturnRules.SalesPermission, (sp, t, c, r, ct) => sp.GetRequiredService<ReturnCommands>().PostSalesReturnAsync(t, c, r, ct));
        catalog.Add<PostPurchaseReturnRequest, FulfilmentResult>("purchase-returns:post", ReturnRules.PurchasePermission, (sp, t, c, r, ct) => sp.GetRequiredService<ReturnCommands>().PostPurchaseReturnAsync(t, c, r, ct));
    }
}
