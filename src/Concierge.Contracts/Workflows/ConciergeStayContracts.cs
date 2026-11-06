using Temporalio.Extensions.WorkflowStreams;

namespace Concierge.Contracts;

/// <summary>Stable contract values for the one voice-only end-chat function.</summary>
public static class ConciergeEndChat
{
    public const string FunctionName = "end_chat";
    public const string ModelOptionKey = "concierge.voice.end_chat";
    public const string Farewell = "Goodbye, and thank you for staying with us.";
}

/// <summary>
/// Stay workflow input. The server binds the stay and absolute eligibility deadline from a
/// verified session; Continue-As-New carries both unchanged with the bounded conversation context.
/// </summary>
public sealed record ConciergeStayWorkflowInput(
    Guid StayId,
    DateTimeOffset EligibilityDeadlineUtc,
    int TranscriptFlushIntervalSeconds,
    long NextSequence = 0,
    IReadOnlyList<ConciergeTranscriptTurn>? RecentTurns = null,
    int TranscriptFlushBatchSize = ConciergePersistenceOptions.DefaultFlushBatchSize,
    IReadOnlyList<ConciergeStayMemoryItem>? StayMemory = null,
    ConciergeVerifiedGuest? VerifiedGuest = null,
    WorkflowStreamState? RideStreamState = null,
    long RideReadOffset = 0,
    ConciergeRideProposal? PendingRideProposal = null,
    ConciergeRideCancellationProposal? PendingRideCancellationProposal = null,
    ConciergeRideCancellationOutcome? LastConfirmedCancellation = null,
    ConciergeRideProposal? ConsumedRideOffer = null,
    long ConsumedRideOfferSequence = -1,
    string? ConsumedRideOfferSpokenRequestKey = null,
    ConciergeRideConfirmationOutcome? ConsumedRideConfirmationOutcome = null,
    ConciergePlacePresentation? NearbyPlaces = null,
    ConciergePlacePresentation? PlaceClarification = null,
    IReadOnlyList<ConciergeVoiceRevision>? VoiceRevisions = null,
    ConciergeVoiceRevisionCheckpoint? VoiceRevisionCheckpoint = null,
    int IdleRolloverSeconds = 0);

/// <summary>Activity-verified places, never model-supplied coordinates or identifiers.</summary>
public sealed record ConciergeCanonicalPlace(
    string Name, string? Address, string? ProviderId, double Latitude, double Longitude);

/// <summary>At most three records with absolute expiry and original turn provenance.</summary>
public sealed record ConciergePlacePresentation(
    IReadOnlyList<ConciergeCanonicalPlace> Places,
    DateTimeOffset ExpiresAtUtc,
    long Sequence,
    int PresentedCount,
    string? Category = null,
    int? FocusIndex = null);

/// <summary>
/// A route-reviewed ride offer that stays open for the next two guest turns, so one pause does not
/// expire it. All fields come from trusted session state and the route activity, not confirmation arguments.
/// </summary>
public sealed record ConciergeRideProposal(
    Guid ProposalId,
    long ProposedSequence,
    ConciergeRidePlace Pickup,
    ConciergeRidePlace Dropoff,
    bool Immediate,
    DateTimeOffset? PickupAtUtc,
    double RouteDurationSeconds);

/// <summary>
/// A specific, verified ride offered for cancellation. The workflow keeps it open for the next two
/// guest turns; model arguments cannot substitute another ride.
/// </summary>
public sealed record ConciergeRideCancellationProposal(
    Guid RideId,
    long ProposedSequence,
    string Status,
    string Pickup,
    string Dropoff,
    DateTimeOffset PickupAt);

/// <summary>
/// One confirmed, terminal cancellation retained for this stay. Turn provenance is for durable
/// bookkeeping, not evidence that two voice callbacks belong to the same utterance.
/// </summary>
public sealed record ConciergeRideCancellationOutcome(
    Guid RideId,
    string Dropoff,
    DateTimeOffset PickupAt,
    string TurnId,
    long Sequence);

/// <summary>
/// The authoritative result of one consumed ride-confirmation attempt. An uncertain result is
/// retained separately from the consumed offer so a revised voice utterance cannot imply success.
/// </summary>
public sealed record ConciergeRideConfirmationOutcome(
    bool Confirmed,
    Guid? RideId,
    string? Status,
    string? Pickup,
    string? Dropoff,
    DateTimeOffset? PickupAt,
    string? Message,
    string TurnId,
    long Sequence);

public sealed record ConciergeRidePlace(
    string Name,
    double Latitude,
    double Longitude);

/// <summary>
/// A bounded, guest-reported statement retained for one stay. It is linked to the stable
/// transcript sequence; a correction replaces the active item while the transcript keeps both.
/// </summary>
public sealed record ConciergeStayMemoryItem(
    string Category,
    string Subject,
    string Statement,
    long SourceSequence,
    long? SupersedesSequence = null);

/// <summary>VerifiedGuest is supplied by Web authentication, never bound from guest request JSON.</summary>
public sealed record ConciergeTurnSubmission(
    string TurnId,
    string UserMessage,
    string Channel,
    ConciergeVerifiedGuest? VerifiedGuest = null,
    bool EnableEndChatFunction = false,
    VoiceUtterance? VoiceUtterance = null);

/// <summary>
/// Immutable audit of a completed answer or dispatched action outcome. Superseded exchanges
/// are excluded from effective history; their original text and action evidence remain intact.
/// </summary>
public sealed record ConciergeTranscriptTurn(
    string TurnId,
    long Sequence,
    string Channel,
    string GuestMessage,
    string AssistantMessage,
    DateTimeOffset OccurredAtUtc,
    bool Interrupted = false,
    bool Superseded = false,
    ConciergeRideCard? RideCard = null);

public sealed record ConciergeTurnResult(
    string TurnId,
    string AssistantMessage,
    long Sequence,
    ConciergeEndChatFunctionCall? EndChatFunctionCall = null,
    ConciergeRideCard? RideCard = null);

/// <summary>
/// Structured ride data a turn's tool result produced, for clients that draw a card beside the answer
/// (the AG-UI page). Taken from the ride service's result, never from the model's wording. A snapshot as
/// of the turn: later status changes arrive through the ride activity feed, not here.
/// </summary>
public sealed record ConciergeRideCard(string Kind, IReadOnlyList<ConciergeRideCardRide> Rides)
{
    /// <summary>A ride the guest just confirmed.</summary>
    public const string Booked = "booked";

    /// <summary>The guest's upcoming rides, read from ride history.</summary>
    public const string Scheduled = "scheduled";

    /// <summary>Keeps the turn, its transcript row and the AG-UI event small.</summary>
    public const int MaxRides = 5;

    // Value equality over the rides: a record compares lists by reference, so a card read back from
    // the transcript row would never equal the one the turn first returned (retry receipts compare them).
    public bool Equals(ConciergeRideCard? other) =>
        other is not null
        && StringComparer.Ordinal.Equals(Kind, other.Kind)
        && Rides.SequenceEqual(other.Rides);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Kind, StringComparer.Ordinal);
        foreach (var ride in Rides)
        {
            hash.Add(ride);
        }

        return hash.ToHashCode();
    }
}

public sealed record ConciergeRideCardRide(
    Guid RideId,
    string Status,
    string Pickup,
    string Dropoff,
    DateTimeOffset PickupAtUtc,
    string? DriverName = null,
    string? Car = null,
    string? Plate = null);

/// <summary>The model-generated client-side end-chat call forwarded to Deepgram.</summary>
public sealed record ConciergeEndChatFunctionCall(string Id, string Name, string Arguments);

public sealed record ConciergeTurnInterruption(string TurnId);

public sealed record ConciergeHistoryResult(IReadOnlyList<ConciergeTranscriptTurn> Turns);

/// <summary>
/// Offset requested when the guest marks a displayed ride-activity snapshot as read.
/// SnapshotOffset is the exclusive end offset returned by the read-state query.
/// </summary>
public sealed record ConciergeRideReadOffsetUpdate(long SnapshotOffset);

/// <summary>
/// Ride-feed read cursor and offset boundaries for one stay. RetainedFromOffset is the first
/// retained global stream offset to subscribe from; SnapshotOffset is the exclusive end of the
/// snapshot, so entries published at or after it remain unread when that snapshot is marked read.
/// </summary>
public sealed record ConciergeRideReadState(
    long ReadOffset,
    long RetainedFromOffset,
    long SnapshotOffset);

public sealed record PersistTranscriptRequest(
    Guid StayId,
    IReadOnlyList<ConciergeTranscriptTurn> Turns);

public sealed record ReadTranscriptRequest(Guid StayId);

public sealed record ReadNextTranscriptSequenceRequest(Guid StayId);

public sealed record FindTranscriptTurnRequest(Guid StayId, string TurnId);

public sealed record MarkTranscriptTurnInterruptedRequest(Guid StayId, string TurnId);

public sealed record MarkTranscriptTurnSupersededRequest(Guid StayId, string TurnId);

public sealed record DeleteStayTranscriptRequest(Guid StayId);

public sealed record PurgeStayRidesRequest(Guid GuestId, Guid StayId, DateOnly CheckoutDate);
