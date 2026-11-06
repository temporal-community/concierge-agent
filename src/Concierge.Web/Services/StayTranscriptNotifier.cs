using Concierge.Contracts;

namespace Concierge.Web.Services;

/// <summary>What changed in a stay's transcript.</summary>
public enum StayTranscriptChangeKind
{
    /// <summary>A turn was committed or marked interrupted.</summary>
    TurnsChanged,

    /// <summary>The stay's conversation, memory and rides were cleared (Start over).</summary>
    StayReset,

    /// <summary>
    /// A turn was handed to the stay workflow and has no answer yet; <see cref="StayTranscriptChange.GuestMessage"/>
    /// carries the guest's words so another page can show "the room device is asking…". Its
    /// <see cref="TurnsChanged"/> follows when the workflow answers.
    /// </summary>
    TurnStarted,
}

/// <summary>A stay transcript change and the chat session that caused it.</summary>
public sealed record StayTranscriptChange(
    Guid StayId,
    Guid SourceSessionId,
    StayTranscriptChangeKind Kind,
    string? GuestMessage = null);

/// <summary>
/// In-process notification that a stay's transcript changed, so an open page can show turns another
/// client added (for example a room device calling <c>POST /api/concierge/turns</c>) without reloading.
/// Every turn enters through <see cref="ConciergeService"/>, which notifies after the stay workflow
/// accepts it; pages then re-read history, which stays the authority.
/// </summary>
/// <remarks>
/// Process-local: with more than one Web replica, a page only hears about turns submitted through its
/// own replica. A workflow query or a backplane would be needed for that (ADR 0014).
/// </remarks>
public sealed class StayTranscriptNotifier(ILogger<StayTranscriptNotifier> logger)
{
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, List<Subscription>> subscriptions = [];

    /// <summary>
    /// Calls <paramref name="onChange"/> for every change to <paramref name="stayId"/> until the returned
    /// subscription is disposed. The callback runs on the notifying request's thread and must not block.
    /// </summary>
    public IDisposable Subscribe(Guid stayId, Action<StayTranscriptChange> onChange)
    {
        ArgumentNullException.ThrowIfNull(onChange);
        var subscription = new Subscription(this, stayId, onChange);
        lock (gate)
        {
            if (!subscriptions.TryGetValue(stayId, out var list))
            {
                subscriptions[stayId] = list = [];
            }

            list.Add(subscription);
        }

        return subscription;
    }

    /// <summary>Notifies every current subscriber of <paramref name="stayId"/>. Never throws.</summary>
    public void Notify(Guid stayId, Guid sourceSessionId, StayTranscriptChangeKind kind, string? guestMessage = null)
    {
        Subscription[] targets;
        lock (gate)
        {
            if (!subscriptions.TryGetValue(stayId, out var list) || list.Count == 0)
            {
                return;
            }

            targets = [.. list];
        }

        var change = new StayTranscriptChange(stayId, sourceSessionId, kind, guestMessage);
        foreach (var target in targets)
        {
            try
            {
                target.Invoke(change);
            }
            catch (Exception exception)
            {
                // One broken page must not stop the others or fail the request that changed the stay.
                logger.LogWarning(
                    "A stay transcript subscriber failed for change {Kind}. Cause types: {CauseTypes}.",
                    kind,
                    ConciergeDiagnostics.ExceptionTypeChain(exception));
            }
        }
    }

    private void Remove(Subscription subscription)
    {
        lock (gate)
        {
            if (subscriptions.TryGetValue(subscription.StayId, out var list)
                && list.Remove(subscription)
                && list.Count == 0)
            {
                subscriptions.Remove(subscription.StayId);
            }
        }
    }

    private sealed class Subscription(
        StayTranscriptNotifier owner,
        Guid stayId,
        Action<StayTranscriptChange> callback) : IDisposable
    {
        private Action<StayTranscriptChange>? handler = callback;

        public Guid StayId { get; } = stayId;

        public void Invoke(StayTranscriptChange change) => Volatile.Read(ref handler)?.Invoke(change);

        public void Dispose()
        {
            // Clearing first also stops a notification already snapshotted outside the lock.
            if (Interlocked.Exchange(ref handler, null) is not null)
            {
                owner.Remove(this);
            }
        }
    }
}
