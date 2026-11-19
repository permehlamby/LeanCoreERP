using FinanceCore.Api.Http;
using FinanceCore.Core.Masters;
using FinanceCore.Data;

namespace FinanceCore.Api.Commands;

/// <summary>The returns of delivered and received goods (11, 5.4): sales-returns:post and :reverse (sales.return), purchase-returns:post and :reverse (purchase.return).</summary>
public sealed class ReturnModule : ICommandModule
{
    public void Register(CommandCatalog catalog)
    {
        catalog.Add<PostSalesReturnRequest, FulfilmentResult>("sales-returns:post", ReturnRules.SalesPermission, (sp, t, c, r, ct) => sp.GetRequiredService<ReturnCommands>().PostSalesReturnAsync(t, c, r, ct));
        catalog.Add<PostPurchaseReturnRequest, FulfilmentResult>("purchase-returns:post", ReturnRules.PurchasePermission, (sp, t, c, r, ct) => sp.GetRequiredService<ReturnCommands>().PostPurchaseReturnAsync(t, c, r, ct));
        catalog.Add<ReverseReturnRequest, FulfilmentResult>("sales-returns:reverse", ReturnRules.SalesPermission, (sp, t, c, r, ct) => sp.GetRequiredService<ReturnCommands>().ReverseSalesReturnAsync(t, c, r, ct));
        catalog.Add<ReverseReturnRequest, FulfilmentResult>("purchase-returns:reverse", ReturnRules.PurchasePermission, (sp, t, c, r, ct) => sp.GetRequiredService<ReturnCommands>().ReversePurchaseReturnAsync(t, c, r, ct));
    }
}
