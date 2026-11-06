using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Concierge.Contracts;
using Microsoft.Extensions.AI;
using Npgsql;
using Temporalio.Activities;

namespace Concierge.WorkflowServer.AI;

/// <summary>
/// Publishes a turn's partial answer text while the durable model activity streams it (ADR 0015).
/// The workflow marks each model call with the turn ID; with no marker this is a pass-through. The
/// stream handed back to the activity is unchanged, and publishing never fails or slows the call.
/// </summary>
/// <remarks>
/// Each message carries the call's full text so far, so a dropped or late message costs nothing but
/// latency. The call ID is the activity ID plus attempt: a retry or a later model call in the same
/// turn is a different call, which listeners treat as text they can't append to.
/// </remarks>
internal sealed class AnswerStreamPublishingChatClient(IChatClient inner, IAnswerStreamSink? publisher)
    : DelegatingChatClient(inner)
{
    /// <summary>Partial text is published at most this often per call; the last text always goes.</summary>
    internal static readonly TimeSpan PublishInterval = TimeSpan.FromMilliseconds(75);

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // The durable activity streams (spike S1, ADR 0015); a non-streaming call only drops the marker.
        TakeTurnId(options);
        return base.GetResponseAsync(messages, options, cancellationToken);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var turnId = TakeTurnId(options);
        if (turnId is null || publisher is null)
        {
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                yield return update;
            }

            yield break;
        }

        var call = CallId();
        var text = new StringBuilder();
        var sequence = 0L;
        var published = 0;
        var lastPublished = 0L;
        var stopped = false;
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            if (!stopped && update.Role != ChatRole.Tool && update.Text is { Length: > 0 } delta)
            {
                text.Append(delta);
                if (Stopwatch.GetElapsedTime(lastPublished) >= PublishInterval)
                {
                    stopped = !Publish();
                }
            }

            yield return update;
        }

        if (!stopped && text.Length > published)
        {
            Publish();
        }

        bool Publish()
        {
            var message = new ConciergeAnswerStreamMessage(turnId, call, sequence++, text.ToString());
            if (ConciergeAnswerStream.ByteCount(message) > ConciergeAnswerStream.MaxPayloadBytes)
            {
                // Listeners keep what they have; the committed answer completes it.
                return false;
            }

            publisher.Offer(message);
            published = text.Length;
            lastPublished = Stopwatch.GetTimestamp();
            return true;
        }
    }

    /// <summary>Removes the marker so the provider never sees it, and returns the turn ID it carried.</summary>
    internal static string? TakeTurnId(ChatOptions? options)
    {
        if (options?.AdditionalProperties is not { } properties
            || !properties.Remove(ConciergeAnswerStream.ModelOptionKey, out var marker))
        {
            return null;
        }

        // Temporal's durable activity serializer delivers the value as a JsonElement.
        var turnId = marker switch
        {
            string value => value,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(turnId) ? null : turnId;
    }

    private static string CallId() =>
        ActivityExecutionContext.HasCurrent
            ? $"{ActivityExecutionContext.Current.Info.ActivityId}:{ActivityExecutionContext.Current.Info.Attempt}"
            : Guid.NewGuid().ToString("N");
}

/// <summary>Takes partial answer text without blocking; never throws.</summary>
internal interface IAnswerStreamSink
{
    void Offer(ConciergeAnswerStreamMessage message);
}

/// <summary>
/// Sends partial answer text with <c>pg_notify</c> from one background loop, in order. Bounded and
/// lossy by design: when Postgres falls behind, older messages are dropped (each carries the full text
/// so far), and failures are logged by type only, since the payload is guest conversation text.
/// </summary>
internal sealed class AnswerStreamPublisher(NpgsqlDataSource dataSource, ILogger<AnswerStreamPublisher> logger)
    : BackgroundService, IAnswerStreamSink
{
    private readonly Channel<ConciergeAnswerStreamMessage> queue = Channel.CreateBounded<ConciergeAnswerStreamMessage>(
        new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    public void Offer(ConciergeAnswerStreamMessage message) => queue.Writer.TryWrite(message);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failing = false;
        await foreach (var message in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await using var command = dataSource.CreateCommand("SELECT pg_notify($1, $2);");
                command.Parameters.Add(new NpgsqlParameter { Value = ConciergeAnswerStream.Channel });
                command.Parameters.Add(new NpgsqlParameter { Value = ConciergeAnswerStream.Write(message) });
                await command.ExecuteNonQueryAsync(stoppingToken);
                if (failing)
                {
                    failing = false;
                    logger.LogInformation("Answer streaming resumed.");
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                if (!failing)
                {
                    failing = true;
                    logger.LogWarning(
                        "Answer streaming paused: publishing partial text failed ({ExceptionType}). Answers still arrive when each turn completes.",
                        exception.GetType().Name);
                }
            }
        }
    }
}
