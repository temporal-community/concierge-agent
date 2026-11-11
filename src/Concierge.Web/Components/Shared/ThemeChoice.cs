namespace Concierge.Web.Components.Shared;

/// <summary>
/// Brand and color mode chosen in the theme picker, as one "{brand}-{mode}" value.
/// Mirrors normalize() in wwwroot/js/theme.js, which applies the value to the page.
/// </summary>
public static class ThemeChoice
{
    public const string Default = "temporal-system";

    private static readonly string[] Brands = ["temporal", "deepgram"];
    private static readonly string[] Modes = ["light", "dark", "system"];

    /// <summary>Returns a supported theme value, or <see cref="Default"/> for anything else.</summary>
    public static string Normalize(string? value)
    {
        if (value is null) return Default;

        // Values stored before brands existed were a bare mode, and the palette they chose is now Temporal's.
        if (Modes.Contains(value)) return $"temporal-{value}";

        var parts = value.Split('-');
        return parts.Length == 2 && Brands.Contains(parts[0]) && Modes.Contains(parts[1]) ? value : Default;
    }
}
