using FinanceCore.Api.Http;
using FinanceCore.Core.Masters;
using FinanceCore.Data;

namespace FinanceCore.Api.Commands;

/// <summary>The invoices built from orders (11, 6.4): sales-invoices and sales-credit-notes :create and :reverse (sales.invoice).</summary>
public sealed class InvoiceModule : ICommandModule
{
    public void Register(CommandCatalog catalog)
    {
        catalog.Add<CreateSalesInvoiceRequest, SalesInvoiceResult>("sales-invoices:create", InvoiceRules.SalesPermission, (sp, t, c, r, ct) => sp.GetRequiredService<SalesInvoiceCommands>().CreateAsync(t, c, r, ct));
        catalog.Add<ReverseSalesInvoiceRequest, ReversedSalesInvoice>("sales-invoices:reverse", InvoiceRules.SalesPermission, (sp, t, c, r, ct) => sp.GetRequiredService<SalesInvoiceCommands>().ReverseAsync(t, c, r, ct));
        catalog.Add<CreateSalesCreditNoteRequest, SalesCreditNoteResult>("sales-credit-notes:create", InvoiceRules.SalesPermission, (sp, t, c, r, ct) => sp.GetRequiredService<SalesCreditNoteCommands>().CreateAsync(t, c, r, ct));
        catalog.Add<ReverseSalesCreditNoteRequest, ReversedSalesCreditNote>("sales-credit-notes:reverse", InvoiceRules.SalesPermission, (sp, t, c, r, ct) => sp.GetRequiredService<SalesCreditNoteCommands>().ReverseAsync(t, c, r, ct));
    }
}
