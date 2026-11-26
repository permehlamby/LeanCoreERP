using FinanceCore.Api.Http;
using FinanceCore.Core.Masters;
using FinanceCore.Data;

namespace FinanceCore.Api.Commands;

/// <summary>Dropship confirmations (11, 5.3, 6.6): dropship-confirmations:post and :reverse (dropship.confirm).</summary>
public sealed class DropshipModule : ICommandModule
{
    public void Register(CommandCatalog catalog)
    {
        catalog.Add<PostDropshipConfirmationRequest, DropshipConfirmationResult>("dropship-confirmations:post", DropshipRules.Permission, (sp, t, c, r, ct) => sp.GetRequiredService<DropshipCommands>().PostAsync(t, c, r, ct));
        catalog.Add<ReverseDropshipConfirmationRequest, ReversedDropshipConfirmation>("dropship-confirmations:reverse", DropshipRules.Permission, (sp, t, c, r, ct) => sp.GetRequiredService<DropshipCommands>().ReverseAsync(t, c, r, ct));
    }
}
