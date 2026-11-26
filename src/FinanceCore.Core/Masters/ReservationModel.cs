using FinanceCore.Core.Commands;

namespace FinanceCore.Core.Masters;

// ---- reservations (10, 4.5, 6.5): permission stock.reserve; availability:query needs read.inventory
/// <summary>reservations:create. TrackingNo reserves a specific lot or serial. DemandRefType is ORDER_LINE or MANUAL.</summary>
public sealed record CreateReservationRequest(
    Guid CompanyId, Guid ItemId, Guid WarehouseId, decimal Quantity, string UnitCode, string DemandRefType, string DemandRef, string? TrackingNo = null);

/// <summary>reservations:adjust: INCREASE must fit what is available, DECREASE cannot take the reserved quantity below zero (release it instead).</summary>
public sealed record AdjustReservationRequest(Guid CompanyId, Guid ReservationId, string Direction, decimal Quantity, string? ReasonCode = null);

public sealed record ReleaseReservationRequest(Guid CompanyId, Guid ReservationId, string? ReasonCode = null);

/// <summary>ReservedQuantity is what the reservation holds now, in base units. Available is what the item has left in the warehouse (of the lot, for a reservation of a lot) after all reservations.</summary>
public sealed record ReservationResult(Guid ReservationId, Guid ItemId, Guid WarehouseId, Guid? TrackingId, decimal ReservedQuantity, decimal Available);

public sealed record AvailabilityRequest(Guid CompanyId, Guid ItemId, Guid WarehouseId, string? TrackingNo = null);

/// <summary>On hand with status AVAILABLE outside TRANSIT and QUARANTINE locations, the reserved quantity of the active reservations, and what is left (10, 4.5, 8).</summary>
public sealed record AvailabilityResult(Guid ItemId, Guid WarehouseId, Guid? TrackingId, decimal OnHand, decimal Reserved, decimal Available);

public static class ReservationRules
{
    public const string Permission = "stock.reserve";
    public static readonly string[] DemandTypes = ["ORDER_LINE", "MANUAL"];

    public static byte[] Fingerprint(string command, string payload) => MasterRules.Fingerprint(command, payload);
}
