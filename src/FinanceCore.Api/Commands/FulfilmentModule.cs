using FinanceCore.Api.Http;
using FinanceCore.Core.Masters;
using FinanceCore.Data;

namespace FinanceCore.Api.Commands;

/// <summary>The fulfilment of orders (11, 6.3, 7.1): sales-deliveries:post (sales.deliver) and purchase-receipts:post (purchase.receive), and their reversals under the same permissions (11, 6.8).</summary>
public sealed class FulfilmentModule : ICommandModule
{
    public void Register(CommandCatalog catalog)
    {
        catalog.Add<PostDeliveryRequest, FulfilmentResult>("sales-deliveries:post", FulfilmentRules.DeliverPermission, (sp, t, c, r, ct) => sp.GetRequiredService<FulfilmentCommands>().PostDeliveryAsync(t, c, r, ct));
        catalog.Add<PostReceiptRequest, FulfilmentResult>("purchase-receipts:post", FulfilmentRules.ReceivePermission, (sp, t, c, r, ct) => sp.GetRequiredService<FulfilmentCommands>().PostReceiptAsync(t, c, r, ct));
        catalog.Add<ReverseFulfilmentRequest, FulfilmentResult>("sales-deliveries:reverse", FulfilmentRules.DeliverPermission, (sp, t, c, r, ct) => sp.GetRequiredService<FulfilmentCommands>().ReverseDeliveryAsync(t, c, r, ct));
        catalog.Add<ReverseFulfilmentRequest, FulfilmentResult>("purchase-receipts:reverse", FulfilmentRules.ReceivePermission, (sp, t, c, r, ct) => sp.GetRequiredService<FulfilmentCommands>().ReverseReceiptAsync(t, c, r, ct));
    }
}
