using FinanceCore.Api.Http;
using FinanceCore.Core.Posting;
using FinanceCore.Data;

namespace FinanceCore.Api.Commands;

/// <summary>The VAT return commands (08, 7): prepare, record the filing, settle, reverse the settlement, and read a return. Each command checks its permission in P0.</summary>
public sealed class VatModule : ICommandModule
{
    public void Register(CommandCatalog catalog)
    {
        catalog.Add<PrepareVatReturnRequest, VatReturnResult>("vat-returns:prepare", VatReturnCommands.PreparePermission,
            (sp, t, c, r, ct) => sp.GetRequiredService<VatReturnCommands>().PrepareAsync(t, c, r, ct));
        catalog.Add<RecordVatFilingRequest, VatReturnResult>("vat-returns:record-filing", VatReturnCommands.FilePermission,
            (sp, t, c, r, ct) => sp.GetRequiredService<VatReturnCommands>().RecordFilingAsync(t, c, r, ct));
        catalog.Add<SettleVatReturnRequest, SettledVatReturn>("vat-returns:settle", VatSettlement.Permission,
            (sp, t, c, r, ct) => sp.GetRequiredService<VatSettlement>().SettleAsync(t, c, r, ct));
        catalog.Add<ReverseVatSettlementRequest, SettledVatReturn>("vat-returns:reverse-settlement", VatSettlement.Permission,
            (sp, t, c, r, ct) => sp.GetRequiredService<VatSettlement>().ReverseAsync(t, c, r, ct));
        catalog.Add<PrepareVatReturnRequest, VatReturnResult>("vat-returns:preview", VatReturnCommands.PreparePermission,
            (sp, t, c, r, ct) => sp.GetRequiredService<VatReturnCommands>().PreviewAsync(t, c, r, ct));
        catalog.Add<ListVatReturnsRequest, VatReturnList>("vat-returns:list", VatReturnCommands.ReadPermission,
            (sp, t, c, r, ct) => sp.GetRequiredService<VatReturnCommands>().ListAsync(t, c, r, ct));
        catalog.Add<GetVatReturnRequest, VatReturnResult>("vat-returns:get", VatReturnCommands.ReadPermission,
            (sp, t, c, r, ct) => sp.GetRequiredService<VatReturnCommands>().GetAsync(t, c, r.ReturnId, ct));
    }
}
