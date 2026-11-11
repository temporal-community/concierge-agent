using System.Collections.Concurrent;
using System.Threading.Channels;
using Concierge.Contracts;
using Npgsql;

namespace Concierge.Web.Services;

/// <summary>
/// Receives partial answer text that the model activity publishes over Postgres LISTEN/NOTIFY
/// (ADR 0015) and hands it to whoever is waiting on that turn. One connection per Web instance.
/// </summary>
/// <remarks>
/// Best-effort: NOTIFY has no replay, so text sent while this is reconnecting is lost, and the turn's
/// committed answer fills the gap. Without the <c>concierge</c> connection string streaming is off and
/// <see cref="Subscribe"/> returns null.
/// </remarks>
public sealed class AnswerStreamListener : BackgroundService
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(5);

    private readonly string? connectionString;
    private readonly ILogger<AnswerStreamListener> logger;
    private readonly ConcurrentDictionary<string, Subscription> subscriptions = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource listening = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public AnswerStreamListener(IConfiguration configuration, ILogger<AnswerStreamListener> logger)
        : this(configuration.GetConnectionString("concierge"), logger)
    {
    }

    internal AnswerStreamListener(string? connectionString, ILogger<AnswerStreamListener> logger)
    {
        this.connectionString = string.IsNullOrWhiteSpace(connectionString) ? null : connectionString;
        this.logger = logger;
    }

    public bool Enabled => connectionString is not null;

    /// <summary>Completes once the first LISTEN is in place (tests wait on it).</summary>
    public Task Listening => listening.Task;

    /// <summary>
    /// Starts collecting messages for <paramref name="turnId"/>. Subscribe before submitting the turn,
    /// since earlier messages aren't replayed. Null when streaming is off.
    /// </summary>
    public AnswerStreamSubscription? Subscribe(string turnId)
    {
        if (!Enabled)
        {
            return null;
        }

        var subscription = new Subscription(this, turnId);
        subscriptions[turnId] = subscription;
        return subscription;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (connectionString is null)
        {
            logger.LogInformation("Answer streaming is off: no 'concierge' connection string. Answers arrive when each turn completes.");
            return;
        }

        var backoff = TimeSpan.FromMilliseconds(250);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(stoppingToken);
                connection.Notification += (_, notification) => Dispatch(notification.Payload);
                await using (var command = new NpgsqlCommand($"LISTEN {ConciergeAnswerStream.Channel};", connection))
                {
                    await command.ExecuteNonQueryAsync(stoppingToken);
                }

                listening.TrySetResult();
                backoff = TimeSpan.FromMilliseconds(250);
                while (true)
                {
                    await connection.WaitAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "Answer streaming listener lost its Postgres connection ({ExceptionType}); retrying in {Backoff}. Answers still arrive when each turn completes.",
                    exception.GetType().Name,
                    backoff);
                await Task.Delay(backoff, stoppingToken);
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
            }
        }
    }

    internal void Dispatch(string payload)
    {
        if (ConciergeAnswerStream.Read(payload) is { } message
            && subscriptions.TryGetValue(message.TurnId, out var subscription))
        {
            subscription.Writer.TryWrite(message);
        }
    }

    private sealed class Subscription(AnswerStreamListener owner, string turnId) : AnswerStreamSubscription
    {
        // Each message is the call's full text so far, so only the newest matters.
        private readonly Channel<ConciergeAnswerStreamMessage> channel = Channel.CreateBounded<ConciergeAnswerStreamMessage>(
            new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

        public ChannelWriter<ConciergeAnswerStreamMessage> Writer => channel.Writer;

        public override ChannelReader<ConciergeAnswerStreamMessage> Reader => channel.Reader;

        public override void Dispose()
        {
            ((ICollection<KeyValuePair<string, Subscription>>)owner.subscriptions).Remove(new(turnId, this));
            channel.Writer.TryComplete();
        }
    }
}

/// <summary>Partial text for one turn, newest last. Dispose when the turn completes.</summary>
public abstract class AnswerStreamSubscription : IDisposable
{
    public abstract ChannelReader<ConciergeAnswerStreamMessage> Reader { get; }

    public abstract void Dispose();
}
