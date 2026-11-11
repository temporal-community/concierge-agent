using Concierge.Contracts;

namespace Concierge.Web.Services;

/// <summary>
/// In-process concierge operations shared by the HTTP routes and Interactive Server UI.
/// Throws <see cref="ConciergeApiException"/> for expected API failures.
/// </summary>
public interface IConciergeService
{
    /// <summary>
    /// Verifies a guest by last name and room number and issues a new bearer credential for that stay.
    /// Throws <see cref="ConciergeApiException"/> with 400 for invalid input and 404 when no eligible stay matches.
    /// </summary>
    Task<ConciergeSessionResponse> VerifyAsync(
        ConciergeVerificationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Submits a guest message to the stay workflow and returns the concierge reply.
    /// Reusing <see cref="ConciergeTurnRequest.TurnId"/> with the same message returns the original reply;
    /// reusing it with a different message throws a 409 <see cref="ConciergeApiException"/>.
    /// Throws a 401 <see cref="ConciergeApiException"/> when the session has no verified guest profile.
    /// </summary>
    Task<ConciergeTurnResult> SubmitTurnAsync(
        string? bearerCredential,
        ConciergeTurnRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the stay transcript, including turns not yet flushed to PostgreSQL.
    /// Throws a 401 <see cref="ConciergeApiException"/> when the credential is invalid or expired.
    /// </summary>
    Task<ConciergeHistoryResult> ReadHistoryAsync(
        string? bearerCredential,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the newest 25 retained ride entries and the authenticated stay's current read state.
    /// The result's snapshot offset is exclusive and is the starting offset for live updates.
    /// Throws a 401 <see cref="ConciergeApiException"/> when the credential is invalid or expired.
    /// </summary>
    Task<ConciergeRideActivitySnapshot> ReadRideActivitySnapshotAsync(
        string? bearerCredential,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams ride entries from an exclusive snapshot offset. Reconnects transient stream failures
    /// while preserving the next unread offset; cancellation disposes the stream subscription and client.
    /// Throws a 401 <see cref="ConciergeApiException"/> when the credential is invalid or expires.
    /// </summary>
    IAsyncEnumerable<ConciergeRideActivityItem> StreamRideActivityAsync(
        string? bearerCredential,
        long fromOffset,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Advances the authenticated stay's read offset through the displayed snapshot only.
    /// Offsets are forward-only; entries published after the supplied snapshot remain unread.
    /// </summary>
    Task<ConciergeRideReadState> MarkRideActivitySnapshotReadAsync(
        string? bearerCredential,
        long snapshotOffset,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Flushes pending transcript turns and invalidates the credential. Stay memory is kept, so the
    /// guest can verify again to continue the same conversation. Also revokes every voice bearer of the session.
    /// </summary>
    Task EndAsync(
        string? bearerCredential,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts the stay over: deletes its rides and transcript and clears the stay's conversation
    /// memory. The credential stays valid. Ends this session's voice call first.
    /// </summary>
    Task ResetAsync(
        string? bearerCredential,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Authenticates a chat credential for voice and reports whether the stay already has turns.
    /// Throws a 401 <see cref="ConciergeApiException"/> when the credential is invalid or expired, or
    /// when the session has no verified guest profile.
    /// </summary>
    Task<ConciergeVoiceAuthorization> AuthorizeVoiceAsync(
        string? bearerCredential,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Submits a voice turn for an already authorized session through the same stay workflow update as text.
    /// Exact retries join the same workflow attempt. A superseded voice attempt remains terminal;
    /// its retry cannot reactivate it or return an obsolete conversational reply.
    /// Throws a 401 <see cref="ConciergeApiException"/> when the session has no verified guest profile.
    /// </summary>
    Task<ConciergeTurnResult> SubmitVoiceTurnAsync(
        ConciergeSessionContext session,
        string turnId,
        string userMessage,
        CancellationToken cancellationToken = default,
        bool enableEndChatFunction = false);

    /// <summary>
    /// Marks a confirmed browser-rendered voice interruption through the stay workflow.
    /// The operation is idempotent and never cancels the admitted turn.
    /// </summary>
    Task MarkVoiceTurnInterruptedAsync(
        ConciergeSessionContext session,
        string turnId,
        CancellationToken cancellationToken = default);
}

/// <summary>A chat session authorized for voice; <paramref name="HasTurns"/> selects the greeting.</summary>
public sealed record ConciergeVoiceAuthorization(ConciergeSessionContext Session, bool HasTurns);

/// <summary>A ride-stream value paired with its stay-global offset.</summary>
public sealed record ConciergeRideActivityItem(long Offset, RideActivityEntry Entry);

/// <summary>The bounded initial display window and read state for one authenticated stay.</summary>
public sealed record ConciergeRideActivitySnapshot(
    ConciergeRideReadState ReadState,
    IReadOnlyList<ConciergeRideActivityItem> Entries);
