using FinanceCore.Api.Http;
using FinanceCore.Core.Masters;
using FinanceCore.Data;

namespace FinanceCore.Api.Commands;

/// <summary>The get and list of the documents of orders (11, 8, read.orders): deliveries, receipts, returns and invoices.</summary>
public sealed class DocumentReadModule : ICommandModule
{
    public void Register(CommandCatalog catalog)
    {
        foreach (DocumentKind kind in DocumentKind.All)
        {
            DocumentKind k = kind;
            catalog.Add<GetDocumentRequest, DocumentDetail>(k.Resource + ":get", OrderKind.ReadPermission, (sp, t, c, r, ct) => sp.GetRequiredService<DocumentReads>().GetAsync(k, t, c, r, ct));
            catalog.Add<ListDocumentsRequest, DocumentList>(k.Resource + ":list", OrderKind.ReadPermission, (sp, t, c, r, ct) => sp.GetRequiredService<DocumentReads>().ListAsync(k, t, c, r, ct));
        }
    }
}
