using FinanceCore.Api.Http;
using FinanceCore.Core.Feed;
using FinanceCore.Data;

namespace FinanceCore.Api.Commands;

/// <summary>Extension attributes (15, 4.3, 4.4): the namespaces of the apps, the definitions, the values of a record and the read of the definitions.</summary>
public sealed class ExtensionModule : ICommandModule
{
    public void Register(CommandCatalog catalog)
    {
        catalog.Add<CreateAppNamespaceRequest, AppNamespaceResult>("app-namespaces:create", EventFeed.ManagePermission, (sp, t, c, r, ct) => sp.GetRequiredService<ExtensionCommands>().CreateNamespaceAsync(t, c, r, ct));
        catalog.Add<CreateExtensionDefinitionRequest, ExtensionDefinitionResult>("extension-definitions:create", ExtensionRules.DefinePermission, (sp, t, c, r, ct) => sp.GetRequiredService<ExtensionCommands>().CreateDefinitionAsync(t, c, r, ct));
        catalog.Add<UpdateExtensionDefinitionRequest, ExtensionDefinitionResult>("extension-definitions:update", ExtensionRules.DefinePermission, (sp, t, c, r, ct) => sp.GetRequiredService<ExtensionCommands>().UpdateDefinitionAsync(t, c, r, ct));
        catalog.Add<DeactivateExtensionDefinitionRequest, ExtensionDefinitionResult>("extension-definitions:deactivate", ExtensionRules.DefinePermission, (sp, t, c, r, ct) => sp.GetRequiredService<ExtensionCommands>().DeactivateDefinitionAsync(t, c, r, ct));
        catalog.Add<SetExtensionsRequest, ExtensionsResult>("extensions:set", ExtensionRules.WritePermission, (sp, t, c, r, ct) => sp.GetRequiredService<ExtensionCommands>().SetAsync(t, c, r, ct));
        catalog.Add<ClearExtensionsRequest, ExtensionsResult>("extensions:clear", ExtensionRules.WritePermission, (sp, t, c, r, ct) => sp.GetRequiredService<ExtensionCommands>().ClearAsync(t, c, r, ct));
        catalog.Add<ListExtensionDefinitionsRequest, ExtensionDefinitionList>("extension-definitions:list", ExtensionRules.ReadDefinitionsPermission, (sp, t, c, r, ct) => sp.GetRequiredService<ExtensionReads>().ListDefinitionsAsync(t, c, r, ct));
    }
}
