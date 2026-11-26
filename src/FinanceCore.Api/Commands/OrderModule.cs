using FinanceCore.Api.Http;
using FinanceCore.Core.Masters;
using FinanceCore.Data;

namespace FinanceCore.Api.Commands;

/// <summary>The sales and purchase orders (11, 6.1): create, change, add-line, close-line, cancel and change-line-dates (sales.order or purchase.order), get and list (read.orders), and the order settings (write.config).</summary>
public sealed class OrderModule : ICommandModule
{
    public void Register(CommandCatalog catalog)
    {
        foreach (OrderKind kind in OrderKind.All)
        {
            OrderKind k = kind;
            string p = k.OrderPermission, r = k.Resource;
            catalog.Add<CreateOrderRequest, OrderResult>(r + ":create", p, (sp, t, c, req, ct) => sp.GetRequiredService<OrderCommands>().CreateAsync(k, t, c, req, ct));
            catalog.Add<ChangeOrderRequest, OrderResult>(r + ":change", p, (sp, t, c, req, ct) => sp.GetRequiredService<OrderCommands>().ChangeAsync(k, t, c, req, ct));
            catalog.Add<AddOrderLineRequest, OrderResult>(r + ":add-line", p, (sp, t, c, req, ct) => sp.GetRequiredService<OrderCommands>().AddLineAsync(k, t, c, req, ct));
            catalog.Add<CloseOrderLineRequest, OrderResult>(r + ":close-line", p, (sp, t, c, req, ct) => sp.GetRequiredService<OrderCommands>().CloseLineAsync(k, t, c, req, ct));
            catalog.Add<CancelOrderRequest, OrderResult>(r + ":cancel", p, (sp, t, c, req, ct) => sp.GetRequiredService<OrderCommands>().CancelAsync(k, t, c, req, ct));
            catalog.Add<ChangeLineDatesRequest, OrderResult>(r + ":change-line-dates", p, (sp, t, c, req, ct) => sp.GetRequiredService<OrderCommands>().ChangeLineDatesAsync(k, t, c, req, ct));
            catalog.Add<GetOrderRequest, OrderDetail>(r + ":get", OrderKind.ReadPermission, (sp, t, c, req, ct) => sp.GetRequiredService<OrderReads>().GetAsync(k, t, c, req, ct));
            catalog.Add<ListOrdersRequest, OrderList>(r + ":list", OrderKind.ReadPermission, (sp, t, c, req, ct) => sp.GetRequiredService<OrderReads>().ListAsync(k, t, c, req, ct));
        }
        catalog.Add<CreateIcOrderRequest, IcOrderResult>("ic-orders:create", IcOrderRules.Permission, (sp, t, c, req, ct) => sp.GetRequiredService<IcOrderCommands>().CreateAsync(t, c, req, ct));
        catalog.Add<CreateOrderSettingVersionRequest, OrderSettingVersionResult>("company-order-settings:create-version", OrderSettingCommands.Permission,
            (sp, t, c, req, ct) => sp.GetRequiredService<OrderSettingCommands>().CreateVersionAsync(t, c, req, ct));
    }
}
