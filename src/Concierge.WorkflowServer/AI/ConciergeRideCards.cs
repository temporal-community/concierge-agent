using Concierge.Contracts;

namespace Concierge.WorkflowServer.AI;

/// <summary>
/// Maps the ride service's decoded results to the card data a turn carries (ADR 0015). Deterministic and
/// bounded: it runs inside the stay workflow, and the card is stored with the transcript turn.
/// </summary>
internal static class ConciergeRideCards
{
    private const int MaxTextBytes = 200;

    public static ConciergeRideCard? Booked(RideSummary? ride) =>
        ride is null || ride.Id == Guid.Empty
            ? null
            : new ConciergeRideCard(ConciergeRideCard.Booked, [Map(ride)]);

    /// <summary>Null for no rides: the answer text already says so, and an empty card adds nothing.</summary>
    public static ConciergeRideCard? Scheduled(IReadOnlyList<RideSummary>? rides)
    {
        var shown = (rides ?? [])
            .Where(ride => ride.Id != Guid.Empty)
            .Take(ConciergeRideCard.MaxRides)
            .Select(Map)
            .ToArray();
        return shown.Length == 0 ? null : new ConciergeRideCard(ConciergeRideCard.Scheduled, shown);
    }

    private static ConciergeRideCardRide Map(RideSummary ride) =>
        new(
            ride.Id,
            Limit(ride.Status),
            Limit(ride.Pickup),
            Limit(ride.Dropoff),
            ride.PickupAt,
            LimitOptional(ride.DriverName),
            LimitOptional(ride.Car),
            LimitOptional(ride.Plate));

    private static string Limit(string? value) =>
        ConciergeToolResultInterpreter.Limit(value ?? string.Empty, MaxTextBytes);

    private static string? LimitOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Limit(value);
}
