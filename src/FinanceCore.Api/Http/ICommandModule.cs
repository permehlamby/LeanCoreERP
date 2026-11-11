namespace FinanceCore.Api.Http;

/// <summary>A group of commands that adds itself to the catalog at start-up. Every module is registered in the service collection.</summary>
public interface ICommandModule
{
    void Register(CommandCatalog catalog);
}
