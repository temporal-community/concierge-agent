namespace Concierge.Web.Services;

/// <summary>Guest-facing constants for the guest chat page and its components.</summary>
public static class ConciergeGuestUi
{
    /// <summary>Hotel ZGY's clock: transcript times and day separators use it, whatever the browser's zone.</summary>
    public static readonly TimeZoneInfo HotelTimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    /// <summary>Starter questions offered before the guest's first message.</summary>
    public static readonly IReadOnlyList<string> Suggestions =
    [
        "What time is breakfast?",
        "How do I connect to Wi-Fi?",
        "What amenities are available?",
        "Can you recommend a nearby restaurant?",
    ];
}
