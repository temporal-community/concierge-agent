using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Concierge.Contracts;

namespace Concierge.Web.Voice;

/// <summary>Why a voice connection's Think bearer stopped being valid.</summary>
public enum VoiceRevocation
{
    None,
    Closed,
    Expired,
    Replaced,
    Ended,
    // The stay was reset while voice ran; the chat credential stays valid, so the browser can reconnect.
    Reset,
}

/// <summary>
/// In-process owner of voice sessions and their short-lived Think bearers. A voice session spans
/// manual reconnects for one chat session; each Deepgram connection gets its own bearer and number.
/// </summary>
public sealed class VoiceSessionRegistry(
    TimeProvider timeProvider, ILogger<VoiceSessionRegistry>? logger = null)
{
    /// <summary>
    /// Deepgram cannot rotate Think headers on an open connection, so the bearer must outlive the
    /// connection; the relay closes the connection when it expires and Reconnect mints a new one.
    /// </summary>
    public static readonly TimeSpan ConnectionLifetime = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<Guid, VoiceSession> sessions = new();
    private readonly ConcurrentDictionary<string, VoiceConnection> connections = new(StringComparer.Ordinal);

    /// <summary>
    /// Opens a new Deepgram connection for <paramref name="stay"/>. Reuses the voice session named by
    /// <paramref name="voiceSessionId"/> only when it belongs to the same chat session; any previous
    /// connection in that voice session is revoked.
    /// </summary>
    public VoiceConnection Open(ConciergeSessionContext stay, Guid? voiceSessionId)
    {
        ArgumentNullException.ThrowIfNull(stay);
        var now = timeProvider.GetUtcNow();
        RemoveAbandonedSessions(now);

        VoiceSession session;
        if (voiceSessionId is { } id
            && sessions.TryGetValue(id, out var existing)
            && existing.Stay.SessionId == stay.SessionId)
        {
            session = existing;
            // A reconnect gives the guest a full idle timeout again (user decision 2026-10-18).
            session.RestartIdleClock(now);
        }
        else
        {
            session = sessions.GetOrAdd(Guid.NewGuid(), newId => new VoiceSession(newId, stay, now));
        }

        var bearer = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var expiresAt = Min(now + ConnectionLifetime, stay.EligibilityDeadlineUtc);
        var lifetime = expiresAt > now ? expiresAt - now : TimeSpan.Zero;

        VoiceConnection connection;
        VoiceConnection? previous;
        lock (session.Gate)
        {
            previous = session.Active;
            connection = new VoiceConnection(
                session, ++session.ConnectionCount, KeyOf(bearer), bearer, expiresAt, lifetime, timeProvider);
            session.Active = connection;
        }

        previous?.Revoke(VoiceRevocation.Replaced);
        connections[connection.Key] = connection;
        // Runs immediately if the stay deadline has already passed.
        connection.Revoked.Register(() => Forget(connection));
        return connection;
    }

    /// <summary>Returns the live connection that owns <paramref name="bearer"/>, if any.</summary>
    public bool TryAuthenticate(string? bearer, [NotNullWhen(true)] out VoiceConnection? connection)
    {
        connection = null;
        if (string.IsNullOrEmpty(bearer) || bearer.Length > 128
            || !connections.TryGetValue(KeyOf(bearer), out var found))
        {
            return false;
        }

        // The expiry timer may not have fired yet; the deadline is authoritative.
        if (timeProvider.GetUtcNow() >= found.ExpiresAtUtc)
        {
            found.Revoke(VoiceRevocation.Expired);
            return false;
        }

        if (found.Revocation != VoiceRevocation.None)
        {
            return false;
        }

        connection = found;
        return true;
    }

    /// <summary>Revokes the connection's bearer because its relay closed.</summary>
    public void Close(VoiceConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        connection.Revoke(VoiceRevocation.Closed);
    }

    /// <summary>
    /// Calls <paramref name="onClosed"/> with the connection number when the active connection of
    /// voice session <paramref name="voiceSessionId"/> goes away because its relay closed or its
    /// bearer expired. Replaced and ended connections are not reported. The callback runs on the
    /// revoking thread and must not block. Subscription succeeds only for its owning chat session.
    /// </summary>
    public bool TrySubscribeConnectionClosed(
        Guid chatSessionId,
        Guid voiceSessionId,
        Action<int> onClosed,
        out IDisposable subscription)
    {
        ArgumentNullException.ThrowIfNull(onClosed);
        subscription = ClosedSubscription.None;
        if (!TryGetOwnedSession(chatSessionId, voiceSessionId, out var session))
        {
            return false;
        }

        var closedSubscription = new ClosedSubscription(session, onClosed);
        lock (session.Gate)
        {
            session.ClosedSubscribers.Add(closedSubscription);
        }

        // The session may have been removed between the lookup and the add; never keep a
        // subscriber on a session the registry no longer owns.
        if (!sessions.TryGetValue(voiceSessionId, out var current) || !ReferenceEquals(current, session))
        {
            closedSubscription.Dispose();
            return false;
        }

        subscription = closedSubscription;
        return true;
    }

    public bool TrySubscribeTurns(
        Guid chatSessionId,
        Guid voiceSessionId,
        Action<VoiceTurnProgress> onTurn,
        out IDisposable subscription)
    {
        ArgumentNullException.ThrowIfNull(onTurn);
        subscription = ClosedSubscription.None;
        if (!TryGetOwnedSession(chatSessionId, voiceSessionId, out var session))
        {
            return false;
        }

        var turnSubscription = session.SubscribeTurns(progress =>
        {
            try
            {
                onTurn(progress);
            }
            catch (Exception exception)
            {
                // Presentation failures must not fail admitted Temporal work.
                logger?.LogWarning(exception,
                    "Voice transcript subscriber failed for session {VoiceSessionId}.", voiceSessionId);
            }
        });
        if (!sessions.TryGetValue(voiceSessionId, out var current) || !ReferenceEquals(current, session))
        {
            turnSubscription.Dispose();
            return false;
        }

        subscription = turnSubscription;
        return true;
    }

    private bool TryGetOwnedSession(
        Guid chatSessionId,
        Guid voiceSessionId,
        [NotNullWhen(true)] out VoiceSession? session)
    {
        session = null;
        if (chatSessionId == Guid.Empty
            || !sessions.TryGetValue(voiceSessionId, out var found)
            || found.Stay.SessionId != chatSessionId)
        {
            return false;
        }

        session = found;
        return true;
    }

    /// <summary>
    /// Revokes every voice bearer of a chat session and forgets its voice sessions. Admitted turns
    /// keep running; only new Think requests are refused.
    /// </summary>
    public void EndSession(Guid chatSessionId, VoiceRevocation reason = VoiceRevocation.Ended)
    {
        foreach (var session in sessions.Values.Where(session => session.Stay.SessionId == chatSessionId))
        {
            sessions.TryRemove(session.Id, out _);
            VoiceConnection? active;
            lock (session.Gate)
            {
                active = session.Active;
                session.ClosedSubscribers.Clear();
            }

            active?.Revoke(reason);
        }
    }

    internal int SessionCount => sessions.Count;

    internal int ClosedSubscriberCount(Guid voiceSessionId)
    {
        if (!sessions.TryGetValue(voiceSessionId, out var session))
        {
            return 0;
        }

        lock (session.Gate)
        {
            return session.ClosedSubscribers.Count;
        }
    }

    private void Forget(VoiceConnection connection)
    {
        connections.TryRemove(new KeyValuePair<string, VoiceConnection>(connection.Key, connection));
        var session = connection.Session;
        // Token callbacks run in reverse registration order, so for a lifetime-timer expiry this
        // runs before the constructor's callback has recorded why; record it here.
        var reason = connection.ResolveRevocation();
        ClosedSubscription[] notify = [];
        lock (session.Gate)
        {
            if (ReferenceEquals(session.Active, connection))
            {
                session.Active = null;
                session.IdleSinceUtc = timeProvider.GetUtcNow();
                if (reason is VoiceRevocation.Closed or VoiceRevocation.Expired)
                {
                    notify = [.. session.ClosedSubscribers];
                }
            }
        }

        foreach (var subscription in notify)
        {
            try
            {
                subscription.Invoke(connection.Number);
            }
            catch (Exception exception)
            {
                // A faulty subscriber must not break revocation or the other subscribers.
                logger?.LogWarning(exception,
                    "Voice connection-closed subscriber failed for session {VoiceSessionId} (connection {ConnectionNumber}).",
                    session.Id, connection.Number);
            }
        }
    }

    // A guest who closes the tab without ending chat leaves a voice session with no connection.
    // Keep it long enough for a manual Reconnect, then drop it.
    private void RemoveAbandonedSessions(DateTimeOffset now)
    {
        foreach (var session in sessions.Values)
        {
            lock (session.Gate)
            {
                if (session.Active is null && session.InFlightCount == 0
                    && now - session.IdleSinceUtc > ConnectionLifetime)
                {
                    sessions.TryRemove(session.Id, out _);
                    session.ClosedSubscribers.Clear();
                }
            }
        }
    }

    private static string KeyOf(string bearer) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bearer)));

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;

    internal sealed class ClosedSubscription : IDisposable
    {
        public static readonly IDisposable None = new NoSubscription();

        private readonly VoiceSession session;
        private Action<int>? onClosed;

        public ClosedSubscription(VoiceSession session, Action<int> onClosed)
        {
            this.session = session;
            this.onClosed = onClosed;
        }

        public void Invoke(int connectionNumber) => Volatile.Read(ref onClosed)?.Invoke(connectionNumber);

        public void Dispose()
        {
            // Clearing the callback first stops a notification already snapshotted outside the lock.
            if (Interlocked.Exchange(ref onClosed, null) is null)
            {
                return;
            }

            lock (session.Gate)
            {
                session.ClosedSubscribers.Remove(this);
            }
        }

        private sealed class NoSubscription : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}

/// <summary>One guest voice mode: the chat session it belongs to and its in-flight Think turns.</summary>
public sealed class VoiceSession
{
    private readonly Dictionary<string, Task<ConciergeTurnResult>> inFlight = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VoiceTurnProgress> pendingTranscripts = new(StringComparer.Ordinal);
    private event Action<VoiceTurnProgress>? TurnProgress;
    private readonly LinkedList<ReplyRegistration> repliesAwaitingPlayback = [];
    private readonly HashSet<string> playbackTurnIds = new(StringComparer.Ordinal);
    private readonly HashSet<int> playbackInterruptions = [];
    private readonly object activityGate = new();
    private DateTimeOffset lastSpeechUtc;
    private bool replyInProgress;
    private string? activePlaybackTurnId;
    private int playbackInterruptionNumber;

    public VoiceSession(Guid id, ConciergeSessionContext stay)
        : this(id, stay, DateTimeOffset.UtcNow)
    {
    }

    internal VoiceSession(Guid id, ConciergeSessionContext stay, DateTimeOffset initialSpeechUtc)
    {
        Id = id;
        Stay = stay;
        lastSpeechUtc = initialSpeechUtc;
    }

    public Guid Id { get; }

    public ConciergeSessionContext Stay { get; }

    internal object Gate { get; } = new();

    internal int ConnectionCount { get; set; }

    internal VoiceConnection? Active { get; set; }

    internal DateTimeOffset IdleSinceUtc { get; set; }

    /// <summary>Connection-closed subscribers; guarded by <see cref="Gate"/>.</summary>
    internal List<VoiceSessionRegistry.ClosedSubscription> ClosedSubscribers { get; } = [];

    internal int InFlightCount
    {
        get
        {
            lock (Gate)
            {
                return inFlight.Count;
            }
        }
    }

    internal DateTimeOffset LastSpeechUtc
    {
        get
        {
            lock (activityGate)
            {
                return lastSpeechUtc;
            }
        }
    }

    internal bool ReplyInProgress
    {
        get
        {
            lock (activityGate)
            {
                return replyInProgress;
            }
        }
    }

    internal void RecordSpeech(DateTimeOffset atUtc)
    {
        lock (activityGate)
        {
            lastSpeechUtc = atUtc;
        }
    }

    /// <summary>Starts the idle timeout over from <paramref name="atUtc"/>; reply state is unchanged.</summary>
    internal void RestartIdleClock(DateTimeOffset atUtc)
    {
        lock (activityGate)
        {
            lastSpeechUtc = atUtc;
        }
    }

    internal void RecordReplyStarted()
    {
        lock (activityGate)
        {
            replyInProgress = true;
        }
    }

    internal void RecordReplyCompleted()
    {
        lock (activityGate)
        {
            replyInProgress = false;
        }
    }

    internal bool IsIdle(DateTimeOffset nowUtc, TimeSpan timeout)
    {
        lock (activityGate)
        {
            return !replyInProgress && nowUtc - lastSpeechUtc >= timeout;
        }
    }

    internal bool TryWhileIdle(DateTimeOffset nowUtc, TimeSpan timeout, Func<bool> claim)
    {
        lock (activityGate)
        {
            return !replyInProgress
                && nowUtc - lastSpeechUtc >= timeout
                && claim();
        }
    }

    /// <summary>
    /// Registers the completed Think reply before it is returned to Deepgram. The assistant's
    /// ConversationText event binds that exact turn ID to subsequent browser playback.
    /// </summary>
    internal ReplyRegistration RegisterReplyForPlayback(string turnId)
    {
        lock (activityGate)
        {
            var registration = new ReplyRegistration(turnId);
            registration.PendingNode = repliesAwaitingPlayback.AddLast(registration);
            return registration;
        }
    }

    /// <summary>Removes an unwritten reply without disturbing a registration already bound to playback.</summary>
    internal void CancelReplyForPlayback(ReplyRegistration registration)
    {
        lock (activityGate)
        {
            if (registration.PendingNode is { List: var list } node
                && ReferenceEquals(list, repliesAwaitingPlayback))
            {
                repliesAwaitingPlayback.Remove(node);
                registration.PendingNode = null;
            }
        }
    }

    /// <summary>Binds the next completed Think reply to the assistant response starting playback.</summary>
    internal void StartReplyPlayback()
    {
        lock (activityGate)
        {
            if (repliesAwaitingPlayback.First is { } first)
            {
                repliesAwaitingPlayback.RemoveFirst();
                first.Value.PendingNode = null;
                activePlaybackTurnId = first.Value.TurnId;
                playbackTurnIds.Add(first.Value.TurnId);
            }
            else
            {
                activePlaybackTurnId = null;
            }
        }
    }

    /// <summary>The turn token attached to audio currently being received from Deepgram.</summary>
    internal string? ActiveReplyPlaybackToken
    {
        get
        {
            lock (activityGate)
            {
                return activePlaybackTurnId;
            }
        }
    }

    /// <summary>
    /// Creates a session-wide interruption number, unique across reconnects. The browser later
    /// identifies the rendered reply by the token attached to its audio.
    /// </summary>
    internal int BeginPlaybackInterruption()
    {
        lock (activityGate)
        {
            var interruptionNumber = checked(++playbackInterruptionNumber);
            playbackInterruptions.Add(interruptionNumber);
            activePlaybackTurnId = null;
            return interruptionNumber;
        }
    }

    /// <summary>Consumes one worklet acknowledgement and validates its rendered turn token.</summary>
    internal bool TryCompletePlaybackInterruption(
        int interruptionNumber,
        bool bargeIn,
        string? playbackToken,
        out string? turnId)
    {
        lock (activityGate)
        {
            if (playbackInterruptions.Remove(interruptionNumber)
                && bargeIn
                && playbackToken is not null
                && playbackTurnIds.Contains(playbackToken))
            {
                turnId = playbackToken;
                return true;
            }

            turnId = null;
            return false;
        }
    }

    /// <summary>
    /// Joins the in-flight turn with <paramref name="turnId"/> or starts it with <paramref name="submit"/>.
    /// Deepgram can retry a Think call with the same history, so one ID must not submit twice.
    /// </summary>
    public Task<ConciergeTurnResult> JoinOrStart(
        string turnId,
        Func<Task<ConciergeTurnResult>> submit,
        string? userMessage = null)
    {
        ArgumentNullException.ThrowIfNull(submit);
        lock (Gate)
        {
            if (inFlight.TryGetValue(turnId, out var running))
            {
                return running;
            }

            var task = RunAsync(turnId, submit, userMessage);
            inFlight[turnId] = task;
            return task;
        }
    }

    private async Task<ConciergeTurnResult> RunAsync(
        string turnId, Func<Task<ConciergeTurnResult>> submit, string? userMessage)
    {
        // Yielding keeps submission work out of the caller's lock and guarantees the entry is
        // registered before the finally block can remove it.
        await Task.Yield();
        try
        {
            var task = submit();
            if (userMessage is not null)
            {
                NotifyTurn(new VoiceTurnProgress(turnId, userMessage, DateTimeOffset.UtcNow));
            }
            return await task;
        }
        finally
        {
            VoiceTurnProgress? finished;
            lock (Gate)
            {
                inFlight.Remove(turnId);
                pendingTranscripts.Remove(turnId, out finished);
            }
            if (finished is not null)
            {
                NotifyTurn(finished with { Finished = true });
            }
        }
    }

    // Only authenticated Think requests enter here. These are pending presentation state,
    // not persisted transcript turns; history remains the authority for completed replies.
    private void NotifyTurn(VoiceTurnProgress progress)
    {
        Action<VoiceTurnProgress>? notify;
        lock (Gate)
        {
            if (!progress.Finished) pendingTranscripts[progress.TurnId] = progress;
            notify = TurnProgress;
        }
        notify?.Invoke(progress);
    }

    internal IDisposable SubscribeTurns(Action<VoiceTurnProgress> onTurn)
    {
        lock (Gate)
        {
            TurnProgress += onTurn;
            // Queue the snapshot before a concurrent completion can notify this subscriber.
            foreach (var progress in pendingTranscripts.Values) onTurn(progress);
        }
        return new TurnSubscription(this, onTurn);
    }

    private sealed class TurnSubscription(VoiceSession session, Action<VoiceTurnProgress> callback) : IDisposable
    {
        private Action<VoiceTurnProgress>? handler = callback;
        public void Dispose()
        {
            lock (session.Gate)
            {
                if (handler is not null) session.TurnProgress -= handler;
                handler = null;
            }
        }
    }

    internal sealed class ReplyRegistration(string turnId)
    {
        internal string TurnId { get; } = turnId;

        internal LinkedListNode<ReplyRegistration>? PendingNode { get; set; }
    }
}

public sealed record VoiceTurnProgress(
    string TurnId, string UserMessage, DateTimeOffset SubmittedAtUtc, bool Finished = false);

/// <summary>One Deepgram connection: its Think bearer, number, and revocation signal.</summary>
public sealed class VoiceConnection
{
    private readonly CancellationTokenSource revocation;
    private readonly TimeProvider timeProvider;
    private readonly object endChatGate = new();
    private readonly Dictionary<string, string> endChatTurns = new(StringComparer.Ordinal);
    private readonly HashSet<string> supersededEndChatTurns = new(StringComparer.Ordinal);
    private string? endChatCallId;
    private string? endChatTurnId;
    private bool endChatConfirmed;
    private bool endChatCancelled;
    private bool endChatResultReceived;
    private bool endChatFlowClaimed;
    private int revokedAs;

    internal VoiceConnection(
        VoiceSession session,
        int number,
        string key,
        string bearer,
        DateTimeOffset expiresAtUtc,
        TimeSpan lifetime,
        TimeProvider timeProvider)
    {
        Session = session;
        Number = number;
        Key = key;
        Bearer = bearer;
        ExpiresAtUtc = expiresAtUtc;
        this.timeProvider = timeProvider;
        revocation = new CancellationTokenSource(lifetime, timeProvider);
        // The timer cancels the token directly; record why so the relay can tell the guest.
        revocation.Token.Register(() => Interlocked.CompareExchange(
            ref revokedAs, (int)VoiceRevocation.Expired, (int)VoiceRevocation.None));
    }

    public VoiceSession Session { get; }

    /// <summary>1-based connection number within the voice session.</summary>
    public int Number { get; }

    /// <summary>Opaque Think bearer. Only the Deepgram Settings message may carry it.</summary>
    public string Bearer { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    /// <summary>Cancelled when the bearer is revoked or expires.</summary>
    public CancellationToken Revoked => revocation.Token;

    public VoiceRevocation Revocation => (VoiceRevocation)Volatile.Read(ref revokedAs);

    internal DateTimeOffset LastSpeechUtc
        => Session.LastSpeechUtc;

    internal bool ReplyInProgress
        => Session.ReplyInProgress;

    internal void RecordSpeech() => Session.RecordSpeech(timeProvider.GetUtcNow());

    internal void RecordReplyStarted() => Session.RecordReplyStarted();

    internal void RecordReplyCompleted() => Session.RecordReplyCompleted();

    internal bool IsIdle(TimeSpan timeout) => Session.IsIdle(timeProvider.GetUtcNow(), timeout);

    internal string? EndChatCallId
    {
        get
        {
            lock (endChatGate)
            {
                return endChatCallId;
            }
        }
    }

    internal void AdmitEndChatTurn(string turnId, string userMessage)
    {
        var identity = VoiceUtterance.FromTurnId(turnId, userMessage);
        if (identity is null || identity.SessionId != Session.Id || identity.Connection != Number)
        {
            throw new ArgumentException("Use this connection's server-generated voice turn ID.", nameof(turnId));
        }

        lock (endChatGate)
        {
            // An exact retry of superseded text must never reactivate its callback authority.
            if (supersededEndChatTurns.Contains(turnId))
            {
                return;
            }

            if (endChatTurns.TryGetValue(identity.Family, out var previous)
                && !StringComparer.Ordinal.Equals(previous, turnId))
            {
                supersededEndChatTurns.Add(previous);
                if (StringComparer.Ordinal.Equals(endChatTurnId, previous))
                {
                    CancelEndChatCall(endChatCallId!);
                }
            }

            endChatTurns[identity.Family] = turnId;
        }
    }

    internal bool RegisterEndChatCall(string callId, string turnId)
    {
        if (string.IsNullOrWhiteSpace(callId))
        {
            return false;
        }

        lock (endChatGate)
        {
            // Admission and registration share the gate: an old model completion cannot race
            // a revision and restore an already revoked call.
            if (!endChatTurns.ContainsValue(turnId))
            {
                return false;
            }

            if (endChatCallId is not null)
            {
                if (StringComparer.Ordinal.Equals(endChatCallId, callId))
                {
                    return !endChatCancelled && StringComparer.Ordinal.Equals(endChatTurnId, turnId);
                }

                if (!endChatCancelled)
                {
                    return false;
                }
            }

            endChatCallId = callId;
            endChatTurnId = turnId;
            endChatCancelled = false;
            endChatConfirmed = false;
            endChatResultReceived = false;
            endChatFlowClaimed = false;
            return true;
        }
    }

    internal bool ConfirmEndChatCall(string callId)
    {
        lock (endChatGate)
        {
            if (endChatCancelled || !StringComparer.Ordinal.Equals(endChatCallId, callId))
            {
                return false;
            }

            endChatConfirmed = true;
            return true;
        }
    }

    internal bool CancelEndChatCall(string callId)
    {
        lock (endChatGate)
        {
            if (!StringComparer.Ordinal.Equals(endChatCallId, callId))
            {
                return false;
            }

            endChatCancelled = true;
            endChatResultReceived = false;
            endChatResultReceivedAtUtc = null;
            endChatFlowClaimed = false;
            return true;
        }
    }

    internal bool CancelPendingEndChatFarewell()
    {
        lock (endChatGate)
        {
            if (endChatCallId is null || endChatCancelled
                || (!endChatConfirmed && !endChatResultReceived))
            {
                return false;
            }

            endChatCancelled = true;
            endChatResultReceived = false;
            endChatResultReceivedAtUtc = null;
            endChatFlowClaimed = false;
            return true;
        }
    }

    internal bool AcceptEndChatToolResult(string callId)
    {
        lock (endChatGate)
        {
            if (!endChatConfirmed || endChatCancelled
                || !StringComparer.Ordinal.Equals(endChatCallId, callId))
            {
                return false;
            }

            if (!endChatResultReceived)
            {
                endChatResultReceived = true;
                endChatResultReceivedAtUtc = timeProvider.GetUtcNow();
            }
            return true;
        }
    }

    private DateTimeOffset? endChatResultReceivedAtUtc;

    internal DateTimeOffset? EndChatResultReceivedAtUtc
    {
        get
        {
            lock (endChatGate)
            {
                return endChatResultReceived ? endChatResultReceivedAtUtc : null;
            }
        }
    }

    internal bool HasEndChatResult
    {
        get
        {
            lock (endChatGate)
            {
                return endChatResultReceived;
            }
        }
    }

    internal bool TryClaimEndChatFlow(bool farewellOnly = false, TimeSpan? idleTimeout = null)
    {
        lock (endChatGate)
        {
            if (endChatFlowClaimed || (farewellOnly && (!endChatResultReceived || endChatCancelled))
                || (!farewellOnly && endChatResultReceived))
            {
                return false;
            }

            if (idleTimeout is { } timeout)
            {
                return Session.TryWhileIdle(timeProvider.GetUtcNow(), timeout, () =>
                {
                    endChatFlowClaimed = true;
                    return true;
                });
            }

            endChatFlowClaimed = true;
            return true;
        }
    }

    internal string Key { get; }

    /// <summary>Returns why the connection was revoked, treating an unrecorded cancellation as expiry.</summary>
    internal VoiceRevocation ResolveRevocation()
    {
        if (revocation.IsCancellationRequested)
        {
            Interlocked.CompareExchange(ref revokedAs, (int)VoiceRevocation.Expired, (int)VoiceRevocation.None);
        }

        return Revocation;
    }

    internal void Revoke(VoiceRevocation reason)
    {
        if (Interlocked.CompareExchange(ref revokedAs, (int)reason, (int)VoiceRevocation.None)
            == (int)VoiceRevocation.None)
        {
            revocation.Cancel();
        }
    }
}
