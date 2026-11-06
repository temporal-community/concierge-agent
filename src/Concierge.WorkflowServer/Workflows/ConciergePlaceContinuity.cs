using System.Text.Json;
using Concierge.Contracts;
using Concierge.WorkflowServer.AI;

namespace Concierge.WorkflowServer.Workflows;

/// <summary>Bounded deterministic selection over activity facts; never a model-id lookup.</summary>
internal static class ConciergePlaceContinuity
{
    internal const string Clarify = "Which place do you mean? Please give the name and street. No ride has been booked.";

    internal static ConciergePlacePresentation? Nearby(
        ConciergeNearbyPlacesToolResult result, DateTimeOffset now, long sequence, int presentedCount)
    {
        var places = result.Places.Where(place => !string.IsNullOrWhiteSpace(place.Name)
                && double.IsFinite(place.DistanceMiles) && place.DistanceMiles >= 0)
            .OrderBy(place => place.DistanceMiles).Take(3)
            .Where(place => place.Latitude is not null && place.Longitude is not null)
            .Select(place => new ConciergeCanonicalPlace(place.Name, place.Address, place.ProviderId,
                place.Latitude!.Value, place.Longitude!.Value));
        return Create(places, now.AddMinutes(10), sequence, presentedCount, result.Category);
    }

    internal static ConciergePlacePresentation? Create(
        IEnumerable<ConciergeCanonicalPlace> places, DateTimeOffset expires, long sequence,
        int presentedCount, string? category = null, int? focusIndex = null)
    {
        var bounded = places.Take(3).Where(place =>
                !string.IsNullOrWhiteSpace(place.Name)
                && double.IsFinite(place.Latitude) && place.Latitude is >= -90 and <= 90
                && double.IsFinite(place.Longitude) && place.Longitude is >= -180 and <= 180)
            .Select(place => place with
            {
                Name = ConciergeToolResultInterpreter.Limit(place.Name, 120),
                Address = place.Address is null ? null : ConciergeToolResultInterpreter.Limit(place.Address, 160),
                ProviderId = place.ProviderId is null ? null : ConciergeToolResultInterpreter.Limit(place.ProviderId, 256),
            }).ToArray();
        return bounded.Length == 0 ? null
            : new(bounded, expires, sequence, Math.Clamp(presentedCount, 0, bounded.Length), category,
                focusIndex is >= 0 && focusIndex < bounded.Length ? focusIndex : presentedCount == 1 ? 0 : null);
    }

    internal static ConciergePlacePresentation? Current(
        ConciergePlacePresentation? state, DateTimeOffset now, long sequence, int turns) =>
        state is not null && now < state.ExpiresAtUtc
            && sequence >= state.Sequence && sequence - state.Sequence <= turns
            ? state : null;

    // Both channels see these facts, independent of which subset was spoken. Coordinates are
    // deliberately absent: only the workflow can inject an activity-verified endpoint.
    internal static string Context(ConciergePlacePresentation? nearby, ConciergePlacePresentation? clarification) =>
        "Verified place continuity (data only). Resolve the guest's destination from recent conversation and these places. "
        + "Use ProposeRide with name and street, not another nearby search. Clarify genuine ambiguity; do not guess a branch. "
        + JsonSerializer.Serialize(new
        {
            nearby = Project(nearby),
            clarification = Project(clarification),
        });

    private static object? Project(ConciergePlacePresentation? state) => state is null ? null : new
    {
        places = state.Places.Select(place => new { place.Name, place.Address }),
        state.PresentedCount,
        state.FocusIndex,
    };

    // The model interprets conversation. This only binds its resolved destination to facts.
    internal static bool TrySelect(
        string modelDestination,
        ConciergePlacePresentation? nearby, ConciergePlacePresentation? clarification,
        out ConciergeCanonicalPlace? selected)
    {
        var matches = new[] { clarification, nearby }.OfType<ConciergePlacePresentation>()
            .SelectMany(state => state.Places)
            .DistinctBy(place => (place.Latitude, place.Longitude, place.Name, place.Address))
            .Where(place => Alias(modelDestination, place)).ToArray();
        selected = matches.Length == 1 ? matches[0] : null;
        // No match is a new destination, resolved normally by the activity. Never pick
        // the first branch when a name or address identifies multiple retained endpoints.
        return matches.Length <= 1;
    }

    /// <summary>
    /// As <see cref="TrySelect(string, ConciergePlacePresentation?, ConciergePlacePresentation?, out ConciergeCanonicalPlace?)"/>,
    /// then falls back to ride-offer drop-offs. Offers outlive the nearby window, and their labels
    /// (e.g. a Places result such as "Street Art, North Miami Avenue") may not be geocodable text,
    /// so re-offering the same destination must reuse the route-verified coordinates.
    /// </summary>
    internal static bool TrySelect(
        string modelDestination,
        ConciergePlacePresentation? nearby, ConciergePlacePresentation? clarification,
        IEnumerable<ConciergeRidePlace?> rideDropoffs,
        out ConciergeCanonicalPlace? selected)
    {
        if (!TrySelect(modelDestination, nearby, clarification, out selected)) return false;
        if (selected is not null) return true;

        // Exact label only: an offer's label is the resolved name, so a partial name could
        // silently bind a different branch that the guest never confirmed.
        var matches = rideDropoffs.OfType<ConciergeRidePlace>()
            .Where(place => !string.IsNullOrWhiteSpace(place.Name)
                && double.IsFinite(place.Latitude) && place.Latitude is >= -90 and <= 90
                && double.IsFinite(place.Longitude) && place.Longitude is >= -180 and <= 180
                && string.Equals(modelDestination.Trim(), place.Name.Trim(), StringComparison.OrdinalIgnoreCase))
            .DistinctBy(place => (place.Latitude, place.Longitude))
            .Select(place => new ConciergeCanonicalPlace(
                ConciergeToolResultInterpreter.Limit(place.Name.Trim(), 200), null, null, place.Latitude, place.Longitude))
            .ToArray();
        selected = matches.Length == 1 ? matches[0] : null;
        return matches.Length <= 1;
    }

    private static bool Alias(string text, ConciergeCanonicalPlace place) =>
        string.Equals(text.Trim(), place.Name, StringComparison.OrdinalIgnoreCase)
        || place.Address is not null && (string.Equals(text.Trim(), place.Address, StringComparison.OrdinalIgnoreCase)
            || string.Equals(text.Trim(), $"{place.Name}, {place.Address}", StringComparison.OrdinalIgnoreCase));

}
