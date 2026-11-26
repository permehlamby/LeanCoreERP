using FinanceCore.Api.Http;
using FinanceCore.Core.Masters;
using FinanceCore.Data;

namespace FinanceCore.Api.Commands;

/// <summary>The stock ledger (10, 6.1, 8): stock-documents:post (stock.post), stock-documents:reverse (stock.reverse), stock-documents:get and :list, and stock-balances:query (read.inventory).</summary>
public sealed class StockModule : ICommandModule
{
    public void Register(CommandCatalog catalog)
    {
        const string read = StockValuation.ReadPermission;
        catalog.Add<PostStockDocumentRequest, StockDocumentResult>("stock-documents:post", StockValuation.PostPermission, (sp, t, c, r, ct) => sp.GetRequiredService<StockCommands>().PostAsync(t, c, r, ct));
        catalog.Add<ReverseStockDocumentRequest, StockDocumentResult>("stock-documents:reverse", StockReversalRules.Permission, (sp, t, c, r, ct) => sp.GetRequiredService<StockCommands>().ReverseAsync(t, c, r, ct));
        catalog.Add<GetStockDocumentRequest, StockDocumentDetail>("stock-documents:get", read, (sp, t, c, r, ct) => sp.GetRequiredService<StockReads>().GetAsync(t, c, r, ct));
        catalog.Add<ListStockDocumentsRequest, StockDocumentList>("stock-documents:list", read, (sp, t, c, r, ct) => sp.GetRequiredService<StockReads>().ListAsync(t, c, r, ct));
        catalog.Add<SetTrackingStatusRequest, TrackingStatusResult>("item-trackings:set-status", ItemTrackingCommands.Permission, (sp, t, c, r, ct) => sp.GetRequiredService<ItemTrackingCommands>().SetStatusAsync(t, c, r, ct));
        catalog.Add<AllocateLandedCostRequest, LandedCostResult>("landed-costs:allocate", LandedCostRules.Permission, (sp, t, c, r, ct) => sp.GetRequiredService<StockCommands>().AllocateLandedCostAsync(t, c, r, ct));
        catalog.Add<ReverseLandedCostRequest, LandedCostResult>("landed-costs:reverse", LandedCostRules.Permission, (sp, t, c, r, ct) => sp.GetRequiredService<StockCommands>().ReverseLandedCostAsync(t, c, r, ct));
        catalog.Add<CreateReservationRequest, ReservationResult>("reservations:create", ReservationRules.Permission, (sp, t, c, r, ct) => sp.GetRequiredService<ReservationCommands>().CreateAsync(t, c, r, ct));
        catalog.Add<AdjustReservationRequest, ReservationResult>("reservations:adjust", ReservationRules.Permission, (sp, t, c, r, ct) => sp.GetRequiredService<ReservationCommands>().AdjustAsync(t, c, r, ct));
        catalog.Add<ReleaseReservationRequest, ReservationResult>("reservations:release", ReservationRules.Permission, (sp, t, c, r, ct) => sp.GetRequiredService<ReservationCommands>().ReleaseAsync(t, c, r, ct));
        catalog.Add<AvailabilityRequest, AvailabilityResult>("availability:query", read, (sp, t, c, r, ct) => sp.GetRequiredService<ReservationCommands>().AvailabilityAsync(t, c, r, ct));
        catalog.Add<StockBalancesRequest, StockBalancesResult>("stock-balances:query", read, (sp, t, c, r, ct) => sp.GetRequiredService<StockReads>().BalancesAsync(t, c, r, ct));
    }
}
