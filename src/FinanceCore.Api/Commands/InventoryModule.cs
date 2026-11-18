using FinanceCore.Api.Http;
using FinanceCore.Core.Masters;
using FinanceCore.Data;

namespace FinanceCore.Api.Commands;

/// <summary>The master data of items and warehouses (10, 3): create, update, deactivate and reactivate of eight resources (write.config), their get and list, and units:list (read.config).</summary>
public sealed class InventoryModule : ICommandModule
{
    public void Register(CommandCatalog catalog)
    {
        const string write = InventoryMasterCommands.Permission;
        foreach (string resource in InventoryMasterCommands.Resources)
        {
            string r = resource;
            catalog.Add<CreateInventoryMasterRequest, MasterResult>(r + ":create", write, (sp, t, c, req, ct) => sp.GetRequiredService<InventoryMasterCommands>().CreateAsync(r, t, c, req, ct));
            catalog.Add<UpdateInventoryMasterRequest, MasterResult>(r + ":update", write, (sp, t, c, req, ct) => sp.GetRequiredService<InventoryMasterCommands>().UpdateAsync(r, t, c, req, ct));
            catalog.Add<MasterStatusRequest, MasterResult>(r + ":deactivate", write, (sp, t, c, req, ct) => sp.GetRequiredService<InventoryMasterCommands>().DeactivateAsync(r, t, c, req, ct));
            catalog.Add<MasterStatusRequest, MasterResult>(r + ":reactivate", write, (sp, t, c, req, ct) => sp.GetRequiredService<InventoryMasterCommands>().ReactivateAsync(r, t, c, req, ct));
        }
        foreach (MasterReadSpec spec in MasterReads.InventorySpecs)
        {
            MasterReadSpec s = spec;
            if (!s.Versioned) catalog.Add<GetMasterRequest, MasterRowResult>(s.Resource + ":get", MasterReads.Permission, (sp, t, c, req, ct) => sp.GetRequiredService<MasterReads>().GetAsync(t, c, s, req, ct));
            catalog.Add<ListMasterRequest, MasterRowList>(s.Resource + ":list", MasterReads.Permission, (sp, t, c, req, ct) => sp.GetRequiredService<MasterReads>().ListAsync(t, c, s, req, ct));
        }
        catalog.Add<CreateAccountDeterminationRequest, AccountDeterminationResult>("account-determinations:create", write, (sp, t, c, req, ct) => sp.GetRequiredService<AccountDeterminationCommands>().CreateAsync(t, c, req, ct));
        catalog.Add<CreateAccountDeterminationVersionRequest, AccountDeterminationResult>("account-determinations:create-version", write, (sp, t, c, req, ct) => sp.GetRequiredService<AccountDeterminationCommands>().CreateVersionAsync(t, c, req, ct));
        catalog.Add<ListMasterRequest, MasterRowList>("units:list", MasterReads.Permission, (sp, t, c, req, ct) => sp.GetRequiredService<MasterReads>().ListUnitsAsync(t, c, req, ct));
    }
}
