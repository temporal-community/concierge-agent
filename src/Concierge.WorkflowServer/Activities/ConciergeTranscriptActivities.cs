using System.Text.Json;
using Concierge.WorkflowServer.Workflows;
using Concierge.Contracts;
using Microsoft.Extensions.Logging;
using Npgsql;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace Concierge.WorkflowServer.Activities;

/// <summary>Retry-safe transcript I/O keyed by stay ID and stable turn ID.</summary>
public sealed class ConciergeTranscriptActivities(NpgsqlDataSource dataSource)
{
    /// <summary>
    /// Inserts transcript turns in one transaction. Turns already stored are skipped, but a stored turn
    /// whose content differs fails the activity, so retries can never rewrite history.
    /// </summary>
    [Activity(nameof(PersistAsync))]
    public async Task PersistAsync(PersistTranscriptRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        foreach (var turn in request.Turns)
        {
            await using var insert = new NpgsqlCommand(
                """
                INSERT INTO transcript_turns (
                    transcript_turn_id,
                    stay_id,
                    turn_id,
                    turn_sequence,
                    channel,
                    guest_message,
                    assistant_message,
                    occurred_at_utc,
                    interrupted,
                    superseded,
                    metadata)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11::jsonb)
                ON CONFLICT (stay_id, turn_id) DO NOTHING;
                """,
                connection,
                transaction);
            insert.Parameters.AddWithValue(Guid.NewGuid());
            insert.Parameters.AddWithValue(request.StayId);
            insert.Parameters.AddWithValue(turn.TurnId);
            insert.Parameters.AddWithValue(turn.Sequence);
            insert.Parameters.AddWithValue(turn.Channel);
            insert.Parameters.AddWithValue(turn.GuestMessage);
            insert.Parameters.AddWithValue(turn.AssistantMessage);
            insert.Parameters.AddWithValue(turn.OccurredAtUtc);
            insert.Parameters.AddWithValue(turn.Interrupted);
            insert.Parameters.AddWithValue(turn.Superseded);
            insert.Parameters.AddWithValue(WriteMetadata(turn));

            if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                await EnsureExistingTurnMatchesAsync(
                    connection,
                    transaction,
                    request.StayId,
                    turn,
                    cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Returns every persisted turn for the stay in sequence order.
    /// </summary>
    [Activity(nameof(ReadAsync))]
    public async Task<IReadOnlyList<ConciergeTranscriptTurn>> ReadAsync(ReadTranscriptRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
        await using var command = dataSource.CreateCommand(
            """
            SELECT turn_id, turn_sequence, channel, guest_message,
                   assistant_message, occurred_at_utc, interrupted, superseded, metadata::text
            FROM transcript_turns
            WHERE stay_id = $1
            ORDER BY turn_sequence, turn_id;
            """);
        command.Parameters.AddWithValue(request.StayId);
        var turns = new List<ConciergeTranscriptTurn>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            turns.Add(ReadTurn(reader));
        }

        return turns;
    }

    /// <summary>Returns the next free sequence number for a stay's persisted transcript.</summary>
    [Activity(nameof(ReadNextSequenceAsync))]
    public async Task<long> ReadNextSequenceAsync(ReadNextTranscriptSequenceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
        await using var command = dataSource.CreateCommand(
            """
            SELECT COALESCE(MAX(turn_sequence) + 1, 0)
            FROM transcript_turns
            WHERE stay_id = $1;
            """);
        command.Parameters.AddWithValue(request.StayId);
        return (long)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("Could not read the next transcript sequence."));
    }

    /// <summary>
    /// Returns the persisted turn with the given turn ID, or <see langword="null"/> if it was never stored.
    /// Used after Continue-As-New to detect a retried turn from an earlier run.
    /// </summary>
    [Activity(nameof(FindAsync))]
    public async Task<ConciergeTranscriptTurn?> FindAsync(FindTranscriptTurnRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
        await using var command = dataSource.CreateCommand(
            """
            SELECT turn_id, turn_sequence, channel, guest_message,
                   assistant_message, occurred_at_utc, interrupted, superseded, metadata::text
            FROM transcript_turns
            WHERE stay_id = $1 AND turn_id = $2;
            """);
        command.Parameters.AddWithValue(request.StayId);
        command.Parameters.AddWithValue(request.TurnId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadTurn(reader) : null;
    }

    /// <summary>
    /// Marks a persisted voice turn as interrupted. This monotonic update is safe to retry and
    /// intentionally does nothing when the turn has not been persisted yet.
    /// </summary>
    [Activity(nameof(MarkInterruptedAsync))]
    public async Task MarkInterruptedAsync(MarkTranscriptTurnInterruptedRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
        await using var command = dataSource.CreateCommand(
            """
            UPDATE transcript_turns
            SET interrupted = TRUE
            WHERE stay_id = $1 AND turn_id = $2 AND channel = 'voice';
            """);
        command.Parameters.AddWithValue(request.StayId);
        command.Parameters.AddWithValue(request.TurnId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Deletes a stay's whole persisted transcript for a guest-requested reset. Idempotent.</summary>
    [Activity(nameof(DeleteForStayAsync))]
    public async Task DeleteForStayAsync(DeleteStayTranscriptRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var command = dataSource.CreateCommand(
            "DELETE FROM transcript_turns WHERE stay_id = $1;");
        command.Parameters.AddWithValue(request.StayId);
        await command.ExecuteNonQueryAsync(ActivityExecutionContext.Current.CancellationToken);
    }

    private static ConciergeTranscriptTurn ReadTurn(NpgsqlDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.GetBoolean(6),
            reader.GetBoolean(7),
            ReadRideCard(reader.GetString(8)));

    // The card rides in the turn's existing metadata column: no schema change, and the
    // retry-safety comparison (EnsureExistingTurnMatchesAsync) stays on the guest and answer text.
    private static readonly JsonSerializerOptions MetadataJson = new(JsonSerializerDefaults.Web);

    private sealed record TranscriptTurnMetadata(ConciergeRideCard? RideCard);

    internal static string WriteMetadata(ConciergeTranscriptTurn turn) =>
        turn.RideCard is null ? "{}" : JsonSerializer.Serialize(new TranscriptTurnMetadata(turn.RideCard), MetadataJson);

    /// <summary>A malformed card is dropped, never thrown: one bad row must not break the stay's history.</summary>
    internal static ConciergeRideCard? ReadRideCard(string metadata)
    {
        try
        {
            var card = JsonSerializer.Deserialize<TranscriptTurnMetadata>(metadata, MetadataJson)?.RideCard;
            return card is { Kind.Length: > 0, Rides: not null } ? card : null;
        }
        catch (JsonException exception)
        {
            if (ActivityExecutionContext.HasCurrent)
            {
                ActivityExecutionContext.Current.Logger.LogWarning(
                    "Ignored an unreadable ride card in transcript metadata. Cause types: {CauseTypes}.",
                    ConciergeDiagnostics.ExceptionTypeChain(exception));
            }

            return null;
        }
    }

    /// <summary>Monotonic audit marker; never rewrites guest text, answer text, or action outcomes.</summary>
    [Activity(nameof(MarkSupersededAsync))]
    public async Task MarkSupersededAsync(MarkTranscriptTurnSupersededRequest request)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE transcript_turns SET superseded = TRUE
            WHERE stay_id = $1 AND turn_id = $2 AND channel = 'voice';
            """);
        command.Parameters.AddWithValue(request.StayId);
        command.Parameters.AddWithValue(request.TurnId);
        await command.ExecuteNonQueryAsync(ActivityExecutionContext.Current.CancellationToken);
    }

    private static async Task EnsureExistingTurnMatchesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid stayId,
        ConciergeTranscriptTurn turn,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT turn_sequence, channel, guest_message, assistant_message
            FROM transcript_turns
            WHERE stay_id = $1 AND turn_id = $2;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(stayId);
        command.Parameters.AddWithValue(turn.TurnId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)
            || reader.GetInt64(0) != turn.Sequence
            || !StringComparer.Ordinal.Equals(reader.GetString(1), turn.Channel)
            || !StringComparer.Ordinal.Equals(reader.GetString(2), turn.GuestMessage)
            || !StringComparer.Ordinal.Equals(reader.GetString(3), turn.AssistantMessage))
        {
            // Retrying cannot resolve a conflicting stored row.
            throw new ApplicationFailureException(
                "A persisted transcript turn conflicts with its stable turn identifier.",
                errorType: "ConciergeTranscriptConflict",
                nonRetryable: true);
        }
    }
}
