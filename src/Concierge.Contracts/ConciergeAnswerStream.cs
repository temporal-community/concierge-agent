using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Concierge.Contracts;

/// <summary>
/// Partial answer text published from the model activity over Postgres LISTEN/NOTIFY (ADR 0015).
/// Provisional and best-effort: the turn's committed answer always wins, and nothing here enters
/// Temporal history.
/// </summary>
public static class ConciergeAnswerStream
{
    /// <summary>The Postgres NOTIFY channel.</summary>
    public const string Channel = "concierge_answer_stream";

    /// <summary>Carries the turn ID from the workflow to the model activity in ChatOptions.AdditionalProperties.</summary>
    public const string ModelOptionKey = "concierge.answerStream";

    /// <summary>Postgres rejects NOTIFY payloads of 8000 bytes or more; stay clear of it.</summary>
    public const int MaxPayloadBytes = 7500;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Write(ConciergeAnswerStreamMessage message) => JsonSerializer.Serialize(message, Json);

    public static int ByteCount(ConciergeAnswerStreamMessage message) => Encoding.UTF8.GetByteCount(Write(message));

    /// <summary>Null for anything that isn't a well-formed message; a bad payload must not break listeners.</summary>
    public static ConciergeAnswerStreamMessage? Read(string payload)
    {
        try
        {
            var message = JsonSerializer.Deserialize<ConciergeAnswerStreamMessage>(payload, Json);
            return message is { TurnId.Length: > 0, Call.Length: > 0, Text: not null } ? message : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// The full text so far of one model call. <paramref name="Call"/> identifies the activity and attempt,
/// so a retry or a second model call in the same turn is a different call whose text starts over.
/// </summary>
public sealed record ConciergeAnswerStreamMessage(
    [property: JsonPropertyName("t")] string TurnId,
    [property: JsonPropertyName("c")] string Call,
    [property: JsonPropertyName("s")] long Sequence,
    [property: JsonPropertyName("x")] string Text);
