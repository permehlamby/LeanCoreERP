using FinanceCore.Api.Http;
using FinanceCore.Core.Masters;
using FinanceCore.Data;

namespace FinanceCore.Api.Commands;

/// <summary>The reads of the GR/IR items (11, 8, read.orders): gr-ir:query.</summary>
public sealed class GrIrReadModule : ICommandModule
{
    public void Register(CommandCatalog catalog)
    {
        catalog.Add<GrIrQueryRequest, GrIrQueryResult>("gr-ir:query", OrderKind.ReadPermission, (sp, t, c, r, ct) => sp.GetRequiredService<PurchaseInvoiceCommands>().QueryGrIrAsync(t, c, r, ct));
    }
}
