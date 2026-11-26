using FinanceCore.Api.Http;
using FinanceCore.Core.Masters;
using FinanceCore.Data;

namespace FinanceCore.Api.Commands;

/// <summary>The reads over the lines of the orders (11, 8, read.orders): open-orders:query and uninvoiced:query.</summary>
public sealed class OrderQueryModule : ICommandModule
{
    public void Register(CommandCatalog catalog)
    {
        catalog.Add<OpenOrdersQueryRequest, OpenOrdersQueryResult>("open-orders:query", OrderKind.ReadPermission, (sp, t, c, r, ct) => sp.GetRequiredService<OrderQueries>().OpenOrdersAsync(t, c, r, ct));
        catalog.Add<UninvoicedQueryRequest, UninvoicedQueryResult>("uninvoiced:query", OrderKind.ReadPermission, (sp, t, c, r, ct) => sp.GetRequiredService<OrderQueries>().UninvoicedAsync(t, c, r, ct));
    }
}
