using System.Text;

namespace Concierge.Contracts;

public sealed record ConciergeVerificationRequest(string LastName, string RoomNumber);

public sealed record ConciergeSessionResponse(
    string Credential,
    DateTimeOffset ExpiresAtUtc,
    Guid StayId,
    Guid SessionId);

public sealed record ConciergeTurnRequest(string TurnId, string UserMessage, string Channel);

public sealed record ConciergeApiError(string Code, string Message);

/// <summary>
/// A failure with a guest-facing <see cref="Exception.Message"/>. The API sends only the code and message;
/// <paramref name="innerException"/> is the real cause, kept in memory for Development diagnostics.
/// </summary>
public sealed class ConciergeApiException(
    string code,
    string message,
    int statusCode,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;

    public int StatusCode { get; } = statusCode;
}

public static class ConciergeTurnLimits
{
    public const int MaxTurnIdLength = 128;
    public const int MaxUserMessageUtf8Bytes = 2000;

    /// <summary>
    /// Returns whether <paramref name="message"/> fits the UTF-8 byte budget enforced by the API and the stay workflow.
    /// </summary>
    public static bool IsUserMessageWithinLimit(string message) =>
        Encoding.UTF8.GetByteCount(message) <= MaxUserMessageUtf8Bytes;

    /// <summary>
    /// Returns whether <paramref name="channel"/> is a supported channel (<c>web</c> or <c>voice</c>); matching is case-sensitive.
    /// </summary>
    public static bool IsChannel(string? channel) =>
        StringComparer.Ordinal.Equals(channel, "web")
        || StringComparer.Ordinal.Equals(channel, "voice");
}

public sealed record ConciergeSessionContext(
    Guid SessionId,
    Guid StayId,
    DateTimeOffset EligibilityDeadlineUtc,
    ConciergeVerifiedGuest? VerifiedGuest = null);

/// <summary>
/// Authoritative database identity, forwarded only by the authenticated server path.
/// The stay binding prevents accidental reuse in another stay's answer context.
/// </summary>
public sealed record ConciergeVerifiedGuest(
    Guid StayId,
    string GivenName,
    string FamilyName,
    string RoomNumber,
    Guid GuestId = default,
    DateOnly CheckoutDate = default,
    DateOnly CheckInDate = default)
{
    /// <summary>
    /// Longest given or family name, in characters. Verification input and seeded guests share it,
    /// and the action-turn prompt budget is sized for both names at this length.
    /// </summary>
    public const int MaxNameLength = 100;

    /// <summary>Hotel room convention; short or invalid room numbers have no inferred floor.</summary>
    public int? Floor => RoomNumberFormat.IsValid(RoomNumber)
        ? RoomNumber.Length switch
        {
            3 => RoomNumber[0] - '0',
            4 => (RoomNumber[0] - '0') * 10 + RoomNumber[1] - '0',
            _ => null,
        }
        : null;

    /// <summary>Nights booked, from hotel-local check-in and checkout dates; null when either is unknown.</summary>
    public int? Nights => CheckInDate != default && CheckoutDate > CheckInDate
        ? CheckoutDate.DayNumber - CheckInDate.DayNumber
        : null;
}

public sealed record VerifyConciergeSessionActivityRequest(
    Guid SessionId,
    byte[] CredentialSha256,
    string LastName,
    string RoomNumber);

public sealed record AuthenticateConciergeSessionActivityRequest(byte[] CredentialSha256);

public sealed record InvalidateConciergeSessionActivityRequest(Guid SessionId, Guid StayId);

/// <summary>
/// Session activity type names. Web starts these as Temporal Standalone Activities by name because it
/// cannot reference the WorkflowServer activity types.
/// </summary>
public static class ConciergeActivityNames
{
    public const string VerifySession = "VerifyAsync";
    public const string AuthenticateSession = "AuthenticateAsync";
    public const string InvalidateSession = "InvalidateAsync";
    public const string SessionInvalidRequestErrorType = "ConciergeSessionInvalidRequest";
}

public static class ConciergeWorkflowNames
{
    public const string TaskQueue = "Concierge.WorkflowServer-tq";
    public const string StayWorkflow = "Concierge.Stay";
    public const string SubmitTurnUpdate = "SubmitTurn";
    public const string MarkTurnInterruptedUpdate = "MarkTurnInterrupted";
    public const string HistoryUpdate = "History";
    public const string EndInteractionUpdate = "EndInteraction";
    public const string ResetStayUpdate = "ResetStay";
    public const string RideActivityReadStateQuery = "RideActivityReadState";
    public const string AdvanceRideActivityReadOffsetUpdate = "AdvanceRideActivityReadOffset";
    public const string TurnConflictErrorType = "ConciergeTurnConflict";
    public const string StayExpiredErrorType = "ConciergeStayExpired";
    public const string ResetIncompleteErrorType = "ConciergeResetIncomplete";
}

public sealed class ConciergePersistenceOptions
{
    public const string SectionName = "Concierge:Persistence";

    public const int DefaultFlushBatchSize = 5;
    public const int MaxFlushBatchSize = 100;

    public int FlushIntervalSeconds { get; set; } = 30;

    public int FlushBatchSize { get; set; } = DefaultFlushBatchSize;

    /// <summary>
    /// How long an idle stay waits before it continues as new while ride-feed polls are waiting, which
    /// releases polls abandoned by closed tabs and restarted Web instances (ADR 0015). 0 turns it off.
    /// </summary>
    public int IdleRolloverSeconds { get; set; } = 900;
}
