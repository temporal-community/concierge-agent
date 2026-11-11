using Concierge.Web.Components.Shared;

namespace Concierge.Core.Tests;

public sealed class ThemeChoiceTests
{
    [Theory]
    [InlineData("temporal-light", "temporal-light")]
    [InlineData("temporal-dark", "temporal-dark")]
    [InlineData("temporal-system", "temporal-system")]
    [InlineData("deepgram-light", "deepgram-light")]
    [InlineData("deepgram-dark", "deepgram-dark")]
    [InlineData("deepgram-system", "deepgram-system")]
    // Stored before brands existed.
    [InlineData("light", "temporal-light")]
    [InlineData("dark", "temporal-dark")]
    [InlineData("system", "temporal-system")]
    [InlineData(null, "temporal-system")]
    [InlineData("", "temporal-system")]
    [InlineData("acme-dark", "temporal-system")]
    [InlineData("deepgram-dark-extra", "temporal-system")]
    [InlineData("Deepgram-Dark", "temporal-system")]
    public void NormalizeAcceptsOnlySupportedThemes(string? value, string expected) =>
        Assert.Equal(expected, ThemeChoice.Normalize(value));
}
