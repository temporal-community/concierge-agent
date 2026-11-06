using Concierge.Contracts;
using Concierge.Web.Services;

namespace Concierge.Web.Components.Pages;

/// <summary>Pure client-side transitions for recovering and ending a chat interaction.</summary>
internal sealed record ChatSessionModel(
    ChatSessionState? Session,
    PendingTurnState? Pending,
    string Draft,
    Guid RecoveryStayId,
    int Version,
    List<ConciergeTranscriptTurn> Turns,
    bool HasExpired,
    bool ShowExpiredStrip,
    bool ShowEndConfirmation,
    bool IsEnding,
    bool ChatEnded,
    string? TurnError,
    string? EndError)
{
    public static ChatSessionModel Initial { get; } =
        new(null, null, string.Empty, Guid.Empty, 0, [], false, false, false, false, false, null, null);

    public bool ShowResetConfirmation { get; init; }

    public bool IsResetting { get; init; }

    public string? ResetError { get; init; }

    public bool HasEndContent => Draft.Length != 0 || Pending is not null;

    public bool IsCurrent(int version) => version == Version;

    public ChatSessionModel InvalidateOutstandingWork() =>
        this with { Version = Version + 1 };

    public ChatSessionModel Restore(ChatSessionState? stored, DateTimeOffset now)
    {
        if (stored is null)
        {
            return this with { Session = null };
        }

        var hasCredential = !string.IsNullOrWhiteSpace(stored.Credential);
        var expired = hasCredential && stored.ExpiresAtUtc is { } expiration && expiration <= now;
        return this with
        {
            Session = hasCredential ? CloneSession(stored, stored.Pending, stored.Draft ?? string.Empty) : null,
            Pending = hasCredential ? stored.Pending : null,
            Draft = stored.Draft ?? string.Empty,
            RecoveryStayId = stored.StayId,
            HasExpired = expired,
            ShowExpiredStrip = expired
        };
    }

    public ChatSessionModel BeginVerification() =>
        this with
        {
            Version = Version + 1,
            RecoveryStayId = Session?.StayId ?? RecoveryStayId,
            HasExpired = false,
            ShowExpiredStrip = false,
            Session = null,
            IsEnding = false,
            ShowResetConfirmation = false,
            IsResetting = false,
            ResetError = null
        };

    public ChatSessionModel VerificationSucceeded(ConciergeSessionResponse response, DateTimeOffset now)
    {
        var recoverSameStay = response.StayId != Guid.Empty && response.StayId == RecoveryStayId;
        var recoveredPending = recoverSameStay ? Pending : null;
        var recoveredDraft = recoverSameStay ? Draft : string.Empty;
        var verifiedSession = new ChatSessionState
        {
            StayId = response.StayId,
            SessionId = response.SessionId,
            Credential = response.Credential,
            ExpiresAtUtc = response.ExpiresAtUtc,
            Pending = recoveredPending,
            Draft = recoveredDraft
        };
        var expired = response.ExpiresAtUtc <= now;

        return this with
        {
            Version = Version + 1,
            Session = verifiedSession,
            Pending = recoveredPending,
            Draft = recoveredDraft,
            RecoveryStayId = response.StayId,
            Turns = [],
            HasExpired = expired,
            ShowExpiredStrip = expired,
            ShowEndConfirmation = false,
            IsEnding = false,
            ChatEnded = false,
            TurnError = null,
            EndError = null,
            ShowResetConfirmation = false,
            IsResetting = false,
            ResetError = null
        };
    }

    public ChatSessionModel BeginStayVerification() =>
        this with
        {
            Version = Version + 1,
            Session = null,
            Pending = null,
            Draft = string.Empty,
            RecoveryStayId = Guid.Empty,
            Turns = [],
            HasExpired = false,
            ShowExpiredStrip = false,
            ShowEndConfirmation = false,
            IsEnding = false,
            ChatEnded = false,
            TurnError = null,
            EndError = null,
            ShowResetConfirmation = false,
            IsResetting = false,
            ResetError = null
        };

    public ChatSessionModel ReconcileHistory(IReadOnlyList<ConciergeTranscriptTurn> history)
    {
        var reconciledTurns = history.ToList();
        var pendingWasCommitted = Pending is not null
            && reconciledTurns.Any(turn => turn.TurnId == Pending.TurnId);
        var nextPending = pendingWasCommitted ? null : Pending;
        var nextSession = Session is null
            ? null
            : CloneSession(Session, pendingWasCommitted ? null : Session.Pending, Draft);

        return this with
        {
            Turns = reconciledTurns,
            Pending = nextPending,
            Session = nextSession,
            TurnError = pendingWasCommitted ? null : TurnError
        };
    }

    public ChatSessionModel SetDraft(string value)
    {
        var nextDraft = value ?? string.Empty;
        return this with
        {
            Draft = nextDraft,
            Session = Session is null ? null : CloneSession(Session, Pending, nextDraft)
        };
    }

    public ChatSessionModel StartTurn(PendingTurnState turn, bool clearDraft)
    {
        var nextDraft = clearDraft ? string.Empty : Draft;
        return this with
        {
            Pending = turn,
            Draft = nextDraft,
            Session = Session is null ? null : CloneSession(Session, turn, nextDraft),
            TurnError = null
        };
    }

    public ChatSessionModel SetTurnError(string? error) =>
        this with { TurnError = error };

    public ChatSessionModel MarkExpired() =>
        this with
        {
            Version = Version + 1,
            HasExpired = true,
            ShowExpiredStrip = true,
            IsEnding = false,
            ShowResetConfirmation = false,
            IsResetting = false,
            TurnError = null
        };

    public ChatSessionModel RequestEndConfirmation() =>
        this with { ShowEndConfirmation = true, EndError = null };

    public ChatSessionModel CancelEnd() =>
        this with { ShowEndConfirmation = false, EndError = null };

    public ChatSessionModel HideEndConfirmation() =>
        this with { ShowEndConfirmation = false };

    public ChatSessionModel BeginEnd() =>
        this with { Version = Version + 1, IsEnding = true };

    public ChatSessionModel EndFailed(string error)
    {
        var retryError = Pending is null
            ? TurnError
            : "The reply may still be processing. Retry to check the same message.";
        return this with
        {
            IsEnding = false,
            EndError = error,
            TurnError = retryError
        };
    }

    public ChatSessionModel FinishEndAttempt() =>
        this with
        {
            IsEnding = false,
            TurnError = EndError is not null && Pending is not null
                ? "The reply may still be processing. Retry to check the same message."
                : TurnError
        };

    public ChatSessionModel CompleteEnd() =>
        this with
        {
            Session = null,
            RecoveryStayId = Guid.Empty,
            Pending = null,
            Draft = string.Empty,
            TurnError = null,
            EndError = null,
            ShowExpiredStrip = false,
            HasExpired = false,
            ShowEndConfirmation = false,
            IsEnding = false,
            ChatEnded = true,
            ShowResetConfirmation = false,
            IsResetting = false,
            ResetError = null
        };

    public ChatSessionModel RequestResetConfirmation() =>
        this with { ShowResetConfirmation = true, ResetError = null, ShowEndConfirmation = false };

    public ChatSessionModel CancelReset() =>
        this with { ShowResetConfirmation = false, ResetError = null };

    /// <summary>Supersedes outstanding history refreshes so none can repaint the cleared chat.</summary>
    public ChatSessionModel BeginReset() =>
        this with { Version = Version + 1, IsResetting = true, ResetError = null };

    public ChatSessionModel ResetFailed(string error) =>
        this with { IsResetting = false, ResetError = error };

    public ChatSessionModel FinishResetAttempt() =>
        this with { IsResetting = false };

    /// <summary>The server deleted the transcript, so local turns, the pending reply and the draft go too.</summary>
    public ChatSessionModel CompleteReset() =>
        this with
        {
            Turns = [],
            Pending = null,
            Draft = string.Empty,
            Session = Session is null ? null : CloneSession(Session, null, string.Empty),
            TurnError = null,
            ShowResetConfirmation = false,
            IsResetting = false,
            ResetError = null
        };

    private static ChatSessionState CloneSession(
        ChatSessionState source,
        PendingTurnState? pending,
        string draft) =>
        new()
        {
            StayId = source.StayId,
            SessionId = source.SessionId,
            Credential = source.Credential,
            ExpiresAtUtc = source.ExpiresAtUtc,
            Pending = pending,
            Draft = draft
        };
}
