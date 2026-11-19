using FinanceCore.Api.Http;
using FinanceCore.Core.Masters;
using FinanceCore.Data;

namespace FinanceCore.Api.Commands;

/// <summary>The invoices built from orders (11, 6.4): sales-invoices:create (sales.invoice).</summary>
public sealed class InvoiceModule : ICommandModule
{
    public void Register(CommandCatalog catalog)
    {
        catalog.Add<CreateSalesInvoiceRequest, SalesInvoiceResult>("sales-invoices:create", InvoiceRules.SalesPermission, (sp, t, c, r, ct) => sp.GetRequiredService<SalesInvoiceCommands>().CreateAsync(t, c, r, ct));
    }
}
