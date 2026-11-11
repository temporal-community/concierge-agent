using System.Text.Json;
using Concierge.Contracts;
using Concierge.Web.Agentic;
using Concierge.WorkflowServer.Activities;
using Concierge.WorkflowServer.AI;
using Microsoft.Extensions.AI;

namespace Concierge.Core.Tests;

/// <summary>ADR 0015: ride card mapping, storage and the AG-UI carrier.</summary>
public sealed class RideCardTests
{
    private static readonly DateTimeOffset Pickup = new(2026, 11, 6, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BookedCardNeedsARealRide()
    {
        Assert.Null(ConciergeRideCards.Booked(null));
        Assert.Null(ConciergeRideCards.Booked(Ride(Guid.Empty)));

        var card = Assert.IsType<ConciergeRideCard>(ConciergeRideCards.Booked(Ride(Guid.NewGuid(), driver: "Alex Rivera")));
        Assert.Equal(ConciergeRideCard.Booked, card.Kind);
        Assert.Equal("Alex Rivera", Assert.Single(card.Rides).DriverName);
    }

    [Fact]
    public void ScheduledCardIsBoundedAndEmptyHistoryHasNone()
    {
        Assert.Null(ConciergeRideCards.Scheduled([]));
        Assert.Null(ConciergeRideCards.Scheduled(null));

        var rides = Enumerable.Range(0, 8).Select(_ => Ride(Guid.NewGuid(), dropoff: new string('x', 500))).ToArray();
        var card = Assert.IsType<ConciergeRideCard>(ConciergeRideCards.Scheduled(rides));

        Assert.Equal(ConciergeRideCard.Scheduled, card.Kind);
        Assert.Equal(ConciergeRideCard.MaxRides, card.Rides.Count);
        Assert.All(card.Rides, ride => Assert.True(System.Text.Encoding.UTF8.GetByteCount(ride.Dropoff) <= 200));
        Assert.All(card.Rides, ride => Assert.Null(ride.DriverName));
    }

    [Fact]
    public void TranscriptMetadataRoundTripsTheCardAndDropsUnreadableValues()
    {
        var card = ConciergeRideCards.Booked(Ride(Guid.NewGuid()))!;
        var turn = new ConciergeTranscriptTurn("t1", 0, "web", "Book it", "Booked.", Pickup, RideCard: card);

        var metadata = ConciergeTranscriptActivities.WriteMetadata(turn);
        var read = ConciergeTranscriptActivities.ReadRideCard(metadata);

        Assert.Equal(card.Kind, read?.Kind);
        Assert.Equal(card.Rides, read?.Rides);
        Assert.Equal("{}", ConciergeTranscriptActivities.WriteMetadata(turn with { RideCard = null }));
        Assert.Null(ConciergeTranscriptActivities.ReadRideCard("{}"));
        Assert.Null(ConciergeTranscriptActivities.ReadRideCard("{\"rideCard\":{\"kind\":5}}"));
        Assert.Null(ConciergeTranscriptActivities.ReadRideCard("not json"));
    }

    /// <summary>A retry after Continue-As-New rebuilds the receipt from the stored row and must equal the original.</summary>
    [Fact]
    public void CardsWithTheSameRidesAreEqualAcrossListInstances()
    {
        var ride = ConciergeRideCards.Booked(Ride(Guid.NewGuid()))!.Rides[0];
        var original = new ConciergeRideCard(ConciergeRideCard.Booked, [ride]);
        var stored = new ConciergeRideCard(ConciergeRideCard.Booked, new List<ConciergeRideCardRide> { ride });

        Assert.Equal(original, stored);
        Assert.Equal(original.GetHashCode(), stored.GetHashCode());
        Assert.NotEqual(original, stored with { Kind = ConciergeRideCard.Scheduled });
        Assert.NotEqual(original, new ConciergeRideCard(ConciergeRideCard.Booked, [ride, ride]));
    }

    [Fact]
    public void AgUiCarrierIsAnInformationalCallAndAReadableResult()
    {
        var card = ConciergeRideCards.Scheduled([Ride(Guid.NewGuid()), Ride(Guid.NewGuid())])!;

        var (call, result) = AgenticConcierge.RideCardContents("agui:1", card);

        Assert.Equal(AgenticConcierge.RideCardTool, call.Name);
        Assert.True(call.InformationalOnly);
        Assert.Equal(call.CallId, result.CallId);
        Assert.Equal(card.Rides, AgenticConcierge.ReadRideCard(result)?.Rides);
        // Off the wire the result is a JsonElement or, from some clients, a string.
        var text = new FunctionResultContent(call.CallId, ((JsonElement)result.Result!).GetRawText());
        Assert.Equal(card.Rides, AgenticConcierge.ReadRideCard(text)?.Rides);
        Assert.Null(AgenticConcierge.ReadRideCard(new FunctionResultContent(call.CallId, "{\"kind\":\"booked\"}")));
        Assert.Null(AgenticConcierge.ReadRideCard(new FunctionResultContent(call.CallId, "nope")));
        Assert.Null(AgenticConcierge.ReadRideCard(null));
    }

    private static RideSummary Ride(Guid id, string dropoff = "Miami International Airport", string? driver = null) =>
        new(id, "Booked", "Hotel ZGY", dropoff, Pickup, driver, driver is null ? null : "ZGY Sedan", driver is null ? null : "ZGY-4821");
}
