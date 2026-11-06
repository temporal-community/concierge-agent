using System.Text;
using System.Text.Json;
using Concierge.Contracts;
using Concierge.WorkflowServer.AI;
using Concierge.WorkflowServer.Workflows;

namespace Concierge.Core.Tests;

public sealed class ConciergePlaceContinuityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 15, 0, 0, TimeSpan.Zero);
    private static readonly ConciergeCanonicalPlace Wynwood =
        new("Panther Coffee", "2390 Northwest 2nd Avenue", "wynwood", 25.801, -80.199);
    private static readonly ConciergeCanonicalPlace Beach =
        new("Panther Coffee", "1875 Purdy Avenue", "beach", 25.795, -80.144);
    private static readonly ConciergeCanonicalPlace First =
        new("First Cafe", "1 Main Street", "first", 25.799, -80.198);

    [Theory]
    [InlineData(599, 6, true)]
    [InlineData(600, 6, false)]
    [InlineData(1, 7, false)]
    [InlineData(1, -1, false)]
    public void NearbyExpiresByOriginalClockAndTurnProvenance(int seconds, long turns, bool available)
    {
        var state = ConciergePlaceContinuity.Create([Wynwood], Now.AddMinutes(10), 10, 1);
        Assert.Equal(available,
            ConciergePlaceContinuity.Current(state, Now.AddSeconds(seconds), 10 + turns, 6) is not null);
    }

    [Theory]
    [InlineData(299, 2, true)]
    [InlineData(300, 2, false)]
    [InlineData(1, 3, false)]
    public void ClarificationHasShorterExpiry(int seconds, int turns, bool available)
    {
        var state = ConciergePlaceContinuity.Create([Wynwood, Beach], Now.AddMinutes(5), 10, 2);
        Assert.Equal(available,
            ConciergePlaceContinuity.Current(state, Now.AddSeconds(seconds), 10 + turns, 2) is not null);
    }

    [Theory]
    [InlineData("Panther Coffee", false, null)]
    [InlineData("Panther Coffee, 1875 Purdy Avenue", true, "beach")]
    [InlineData("Panther Coffee, 2390 Northwest 2nd Avenue", true, "wynwood")]
    [InlineData("  first cafe  ", true, "first")]
    [InlineData("1875 Purdy Avenue", true, "beach")]
    [InlineData("Panther Coffee, Another Street", true, null)]
    [InlineData("New Destination", true, null)]
    public void ModelResolvedDestinationBindsOnlyToUniqueCanonicalMatch(
        string model, bool allowed, string? providerId)
    {
        var nearby = ConciergePlaceContinuity.Create([First, Wynwood, Beach], Now.AddMinutes(10), 0, 3, "cafe");
        Assert.Equal(allowed, ConciergePlaceContinuity.TrySelect(model, nearby, null, out var selected));
        Assert.Equal(providerId, selected?.ProviderId);
    }

    [Fact]
    public void SameTurnIntermediatePlacesCanBindModelResolvedDestination()
    {
        var nearby = ConciergePlaceContinuity.Create([First, Wynwood, Beach], Now.AddMinutes(10), 0, 0, "cafe")!;
        Assert.True(ConciergePlaceContinuity.TrySelect(
            First.Name, nearby, null, out var selected));
        Assert.Equal(First, selected);
    }

    [Fact]
    public void ClarificationAndNearbyDeduplicateSameEndpointButNotDifferentBranches()
    {
        var nearby = ConciergePlaceContinuity.Create([Wynwood], Now.AddMinutes(10), 0, 1, "cafe");
        var clarification = ConciergePlaceContinuity.Create([Beach, Wynwood], Now.AddMinutes(5), 1, 2);
        Assert.True(ConciergePlaceContinuity.TrySelect(
            $"{Beach.Name}, {Beach.Address}", nearby, clarification, out var branch));
        Assert.Equal(Beach, branch);
        Assert.True(ConciergePlaceContinuity.TrySelect(
            $"{Wynwood.Name}, {Wynwood.Address}", nearby, clarification, out branch));
        Assert.Equal(Wynwood, branch);
        Assert.False(ConciergePlaceContinuity.TrySelect(
            Wynwood.Name, nearby, clarification, out branch));
        Assert.Null(branch);
    }

    [Fact]
    public void EvenNameAndAddressCannotSelectConflictingCoordinates()
    {
        var nearby = ConciergePlaceContinuity.Create(
            [Wynwood, Wynwood with { Latitude = 25.9 }], Now.AddMinutes(10), 0, 2);
        Assert.False(ConciergePlaceContinuity.TrySelect(
            $"{Wynwood.Name}, {Wynwood.Address}", nearby, null, out var selected));
        Assert.Null(selected);
    }

    [Fact]
    public void MissingOrExpiredStateUsesNormalResolverWithoutTrustedCoordinates()
    {
        var state = ConciergePlaceContinuity.Create([Wynwood], Now, 0, 1);
        Assert.True(ConciergePlaceContinuity.TrySelect(
            Wynwood.Name, ConciergePlaceContinuity.Current(state, Now, 1, 6), null, out var selected));
        Assert.Null(selected);
    }

    private static readonly ConciergeRidePlace BookedStreetArt =
        new("Street Art, North Miami Avenue", 25.7995499, -80.1946837);

    [Theory]
    [InlineData("Street Art, North Miami Avenue", true)]
    [InlineData("  street art, north miami avenue ", true)]
    [InlineData("Street Art", false)]
    [InlineData("Street Art (North Miami Avenue)", false)]
    public void ExpiredNearbyFallsBackToExactRideOfferDropoff(string model, bool bound)
    {
        Assert.True(ConciergePlaceContinuity.TrySelect(model, null, null, [null, BookedStreetArt], out var selected));
        Assert.Equal(bound, selected is not null);
        if (bound)
        {
            Assert.Equal(new ConciergeCanonicalPlace(BookedStreetArt.Name, null, null,
                BookedStreetArt.Latitude, BookedStreetArt.Longitude), selected);
        }
    }

    [Fact]
    public void RetainedPlacesTakePrecedenceOverRideOfferDropoffs()
    {
        var nearby = ConciergePlaceContinuity.Create([First], Now.AddMinutes(10), 0, 1);
        var sameLabelElsewhere = new ConciergeRidePlace(First.Name, 1, 1);
        Assert.True(ConciergePlaceContinuity.TrySelect(First.Name, nearby, null, [sameLabelElsewhere], out var selected));
        Assert.Equal(First, selected);
    }

    [Fact]
    public void PendingAndConsumedOffersForOneEndpointBindButConflictingEndpointsClarify()
    {
        Assert.True(ConciergePlaceContinuity.TrySelect(
            BookedStreetArt.Name, null, null, [BookedStreetArt, BookedStreetArt], out var selected));
        Assert.NotNull(selected);

        Assert.False(ConciergePlaceContinuity.TrySelect(
            BookedStreetArt.Name, null, null, [BookedStreetArt, BookedStreetArt with { Latitude = 25.9 }], out selected));
        Assert.Null(selected);
    }

    [Fact]
    public void InvalidRideOfferCoordinatesAreNeverTrusted()
    {
        Assert.True(ConciergePlaceContinuity.TrySelect(
            BookedStreetArt.Name, null, null, [BookedStreetArt with { Latitude = double.NaN }], out var selected));
        Assert.Null(selected);
    }

    [Fact]
    public void StateAndModelProjectionAreBoundedWithoutCuttingSerializedJson()
    {
        var oversized = Wynwood with { Name = new string('界', 2_000), Address = new string('"', 2_000), ProviderId = new string('x', 2_000) };
        var state = ConciergePlaceContinuity.Create(Enumerable.Repeat(oversized, 100), Now.AddMinutes(10), 4, 100)!;
        Assert.Equal(3, state.Places.Count);
        Assert.Equal(3, state.PresentedCount);
        Assert.All(state.Places, place =>
        {
            Assert.True(Encoding.UTF8.GetByteCount(place.Name) <= 120);
            Assert.True(Encoding.UTF8.GetByteCount(place.Address!) <= 160);
            Assert.True(Encoding.UTF8.GetByteCount(place.ProviderId!) <= 256);
        });
        Assert.True(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(state)) < 6_000);
        var serialized = JsonSerializer.Serialize(new ConciergeNearbyPlacesToolResult("Available", "cafe",
            Enumerable.Repeat(new ConciergeNearbyPlace(oversized.Name, oversized.Address, 0.2, oversized.ProviderId, 25, -80), 3).ToArray()),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var result = ConciergeToolResultInterpreter.Decode(ConciergeToolDefinitions.FindNearbyPlaces, serialized)
            .Interpret(new("nearby", "Cafes nearby?", "voice"), new Dictionary<string, object?>(), false);
        Assert.True(Encoding.UTF8.GetByteCount(result.CompactModelContext) <= 2_048);
        using var facts = JsonDocument.Parse(result.CompactModelContext);
        Assert.Equal(3, facts.RootElement.GetProperty("places").GetArrayLength());
    }

    [Theory]
    [InlineData("__concierge_verified_destination")]
    [InlineData("providerId")]
    [InlineData("latitude")]
    public void ModelCannotSupplyTrustedEndpointFields(string key)
    {
        Assert.False(ConciergeToolCallPolicy.TryValidateArguments(ConciergeToolDefinitions.ProposeRide,
            new Dictionary<string, object?>
            {
                ["dropoff"] = Wynwood.Name, ["immediate"] = true, [key] = "forged",
            }, out _));
    }
}
