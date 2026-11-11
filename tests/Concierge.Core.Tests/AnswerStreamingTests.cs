using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Concierge.Contracts;
using Concierge.Web.Agentic;
using Concierge.Web.Services;
using Concierge.WorkflowServer.AI;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Concierge.Core.Tests;

/// <summary>ADR 0015: partial answer text from the model activity to the AG-UI page.</summary>
public sealed class AnswerStreamingTests
{
    private static readonly TimeSpan Spaced = AnswerStreamPublishingChatClient.PublishInterval * 2;

    [Fact]
    public async Task PublisherTakesTheMarkerAndPublishesTheGrowingFullText()
    {
        var model = new WordModel(["The pool ", "opens at ", "8 a.m."], Spaced);
        var sink = new Sink();
        using var client = new AnswerStreamPublishingChatClient(model, sink);

        var streamed = await Collect(client.GetStreamingResponseAsync([new(ChatRole.User, "Pool?")], Marked("t-1")));

        Assert.Equal("The pool opens at 8 a.m.", streamed);
        Assert.False(model.Options!.AdditionalProperties!.ContainsKey(ConciergeAnswerStream.ModelOptionKey));
        Assert.Equal(["The pool ", "The pool opens at ", "The pool opens at 8 a.m."], sink.Messages.Select(message => message.Text));
        Assert.All(sink.Messages, message => Assert.Equal("t-1", message.TurnId));
        Assert.Single(sink.Messages.Select(message => message.Call).Distinct());
        Assert.Equal([0L, 1L, 2L], sink.Messages.Select(message => message.Sequence));
    }

    [Fact]
    public async Task FastDeltasAreBatchedAndTheLastTextAlwaysGoes()
    {
        var sink = new Sink();
        using var client = new AnswerStreamPublishingChatClient(new WordModel(["a ", "b ", "c ", "d"], TimeSpan.Zero), sink);

        await Collect(client.GetStreamingResponseAsync([new(ChatRole.User, "Hi")], Marked("t-1")));

        Assert.InRange(sink.Messages.Count, 1, 2);
        Assert.Equal("a b c d", sink.Messages[^1].Text);
    }

    [Fact]
    public async Task WithoutAMarkerTheCallPassesThroughUnpublished()
    {
        var sink = new Sink();
        using var client = new AnswerStreamPublishingChatClient(new WordModel(["Hello"], TimeSpan.Zero), sink);

        Assert.Equal("Hello", await Collect(client.GetStreamingResponseAsync([new(ChatRole.User, "Hi")], new ChatOptions())));
        Assert.Empty(sink.Messages);
    }

    [Fact]
    public void TheMarkerIsReadFromTheDurableSerializersJsonElementAndRemoved()
    {
        var options = new ChatOptions { AdditionalProperties = new() { [ConciergeAnswerStream.ModelOptionKey] = JsonSerializer.SerializeToElement("t-9") } };

        Assert.Equal("t-9", AnswerStreamPublishingChatClient.TakeTurnId(options));
        Assert.Empty(options.AdditionalProperties);
        Assert.Null(AnswerStreamPublishingChatClient.TakeTurnId(new ChatOptions { AdditionalProperties = new() { [ConciergeAnswerStream.ModelOptionKey] = 5 } }));
        Assert.Null(AnswerStreamPublishingChatClient.TakeTurnId(null));
    }

    [Fact]
    public async Task PublishingStopsAtTheNotifyPayloadLimitButTheStreamDoesNot()
    {
        var sink = new Sink();
        var parts = Enumerable.Range(0, 10).Select(_ => new string('x', 1000)).ToArray();
        using var client = new AnswerStreamPublishingChatClient(new WordModel(parts, Spaced), sink);

        var streamed = await Collect(client.GetStreamingResponseAsync([new(ChatRole.User, "Hi")], Marked("t-1")));

        Assert.Equal(10_000, streamed.Length);
        Assert.NotEmpty(sink.Messages);
        Assert.All(sink.Messages, message => Assert.True(ConciergeAnswerStream.ByteCount(message) <= ConciergeAnswerStream.MaxPayloadBytes));
        Assert.True(sink.Messages[^1].Text.Length < 7500);
    }

    [Fact]
    public async Task AnUnreachableDatabaseDoesNotStopThePublisher()
    {
        // Nothing listens on port 1: every NOTIFY fails fast.
        await using var dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Username=x;Password=x;Database=x;Timeout=1");
        using var publisher = new AnswerStreamPublisher(dataSource, NullLogger<AnswerStreamPublisher>.Instance);
        await publisher.StartAsync(CancellationToken.None);

        publisher.Offer(new ConciergeAnswerStreamMessage("t", "c", 0, "a"));
        publisher.Offer(new ConciergeAnswerStreamMessage("t", "c", 1, "ab"));
        await Task.Delay(TimeSpan.FromSeconds(2));

        Assert.False(publisher.ExecuteTask!.IsCompleted);
        await publisher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void MessagesRoundTripAndBadPayloadsAreIgnored()
    {
        var message = new ConciergeAnswerStreamMessage("agui:1", "3:1", 4, "Hi \"there\"");

        Assert.Equal(message, ConciergeAnswerStream.Read(ConciergeAnswerStream.Write(message)));
        Assert.Null(ConciergeAnswerStream.Read("not json"));
        Assert.Null(ConciergeAnswerStream.Read("{\"t\":\"\",\"c\":\"1\",\"s\":0,\"x\":\"a\"}"));
        Assert.Null(ConciergeAnswerStream.Read("{\"t\":\"a\",\"c\":\"1\",\"s\":0}"));
    }

    [Fact]
    public void StreamedTextFollowsOneCallAndFreezesWhenTextStartsOver()
    {
        var sent = new StayWorkflowChatClient.StreamedText();

        Assert.Equal("The ", sent.Extend(Message("1:1", "The ")));
        Assert.Null(sent.Extend(Message("1:1", "The ")));
        Assert.Equal("pool", sent.Extend(Message("1:1", "The pool")));
        // A retry starts over: nothing more is sent, even if it later grows past the old text.
        Assert.Null(sent.Extend(Message("1:2", "The")));
        Assert.Null(sent.Extend(Message("1:1", "The pool opens")));
        Assert.Equal("The pool", sent.Text);

        var rewritten = new StayWorkflowChatClient.StreamedText();
        rewritten.Extend(Message("1:1", "Let me"));
        Assert.Null(rewritten.Extend(Message("1:1", "Sure")));
    }

    [Fact]
    public async Task AdapterForwardsDeltasThenOnlyTheRestOfTheCommittedAnswer()
    {
        var (listener, concierge) = Stack();
        using var client = new StayWorkflowChatClient(concierge, "credential", listener);

        var run = Collect(client.GetStreamingResponseAsync([new(ChatRole.User, "Pool?")]), out var updates);
        var turnId = await concierge.Submitted.Task;
        await Deliver(listener, turnId, updates, "The pool ", "The pool opens at ");
        concierge.Complete(new ConciergeTurnResult(turnId, "The pool opens at 8 a.m.", 0));
        await run;

        Assert.Equal(["The pool ", "opens at ", "8 a.m."], updates.Select(update => update.Text));
        Assert.All(updates, update => Assert.Equal(turnId, update.MessageId));
        Assert.DoesNotContain(updates.SelectMany(update => update.Contents), content => content is FunctionCallContent);
    }

    [Fact]
    public async Task ADifferentCommittedAnswerArrivesAsACorrection()
    {
        var (listener, concierge) = Stack();
        using var client = new StayWorkflowChatClient(concierge, "credential", listener);

        var run = Collect(client.GetStreamingResponseAsync([new(ChatRole.User, "Weather?")]), out var updates);
        var turnId = await concierge.Submitted.Task;
        await Deliver(listener, turnId, updates, "Let me check");
        concierge.Complete(new ConciergeTurnResult(turnId, "Rain, 82°F.", 0));
        await run;

        Assert.Equal("Let me check", updates[0].Text);
        var call = Assert.Single(updates.SelectMany(update => update.Contents).OfType<FunctionCallContent>());
        Assert.Equal(AgenticConcierge.AnswerCorrectionTool, call.Name);
        Assert.True(call.InformationalOnly);
        var result = Assert.Single(updates.SelectMany(update => update.Contents).OfType<FunctionResultContent>());
        Assert.Equal("Rain, 82°F.", result.Result);
    }

    [Fact]
    public async Task WithoutAListenerTheAnswerIsOneUpdateWithItsRideCard()
    {
        var concierge = new FakeTurns();
        using var client = new StayWorkflowChatClient(concierge, "credential");
        var card = new ConciergeRideCard(ConciergeRideCard.Booked,
            [new ConciergeRideCardRide(Guid.NewGuid(), "Booked", "Hotel ZGY", "Airport", DateTimeOffset.UtcNow)]);

        var run = Collect(client.GetStreamingResponseAsync([new(ChatRole.User, "Book it")]), out var updates);
        var turnId = await concierge.Submitted.Task;
        concierge.Complete(new ConciergeTurnResult(turnId, "Booked.", 0, RideCard: card));
        await run;

        var update = Assert.Single(updates);
        Assert.Equal("Booked.", update.Text);
        Assert.Equal(AgenticConcierge.RideCardTool, Assert.Single(update.Contents.OfType<FunctionCallContent>()).Name);
    }

    [Fact]
    public async Task AFailureAfterStreamingIsACorrectionAndBeforeIsPlainText()
    {
        var (listener, concierge) = Stack();
        using var client = new StayWorkflowChatClient(concierge, "credential", listener);
        var run = Collect(client.GetStreamingResponseAsync([new(ChatRole.User, "Pool?")]), out var updates);
        var turnId = await concierge.Submitted.Task;
        await Deliver(listener, turnId, updates, "The pool");
        concierge.Fail(new ConciergeApiException("unavailable", "The concierge is unavailable. Try again.", 503));
        await run;
        Assert.Equal("The concierge is unavailable. Try again.",
            Assert.Single(updates.SelectMany(update => update.Contents).OfType<FunctionResultContent>()).Result);

        var quiet = new FakeTurns();
        using var plain = new StayWorkflowChatClient(quiet, "credential", listener);
        var second = Collect(plain.GetStreamingResponseAsync([new(ChatRole.User, "Pool?")]), out var only);
        await quiet.Submitted.Task;
        quiet.Fail(new ConciergeApiException("unauthorized", "Authenticate again to continue.", 401));
        await second;
        Assert.Equal("Authenticate again to continue.", Assert.Single(only).Text);
    }

    /// <summary>
    /// Development: an unexpected failure is kept for the page (the AG-UI host only sends generic text)
    /// and still fails the run; the next run clears it. Concierge API failures are answers, not failures.
    /// </summary>
    [Fact]
    public async Task AnUnexpectedFailureIsRecordedForTheCredentialAndTheNextRunClearsIt()
    {
        var failures = new AgenticRunFailures();
        var broken = new FakeTurns();
        using var client = new StayWorkflowChatClient(broken, "credential", failures: failures);
        var run = Collect(client.GetStreamingResponseAsync([new(ChatRole.User, "Pool?")]), out _);
        await broken.Submitted.Task;
        broken.Fail(new InvalidOperationException("Temporal is unreachable."));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => run);
        Assert.Equal("Temporal is unreachable.", thrown.Message);
        Assert.StartsWith("System.InvalidOperationException: Temporal is unreachable.", failures.Latest("credential"));
        Assert.Null(failures.Latest("another-credential"));

        var rejected = new FakeTurns();
        using var answered = new StayWorkflowChatClient(rejected, "credential", failures: failures);
        var second = Collect(answered.GetStreamingResponseAsync([new(ChatRole.User, "Pool?")]), out var only);
        await rejected.Submitted.Task;
        rejected.Fail(new ConciergeApiException("unavailable", "The concierge is unavailable. Try again.", 503));
        await second;

        Assert.Equal("The concierge is unavailable. Try again.", Assert.Single(only).Text);
        Assert.Null(failures.Latest("credential"));
    }

    /// <summary>
    /// Through the real AG-UI host: the guest's client gets only the host's generic error (as ErrorContent,
    /// raised by RunErrorChatClient), and the Development store holds the server exception the page shows.
    /// </summary>
    [Fact]
    public async Task OverAgUiTheClientGetsGenericTextAndTheStoreHoldsTheServerException()
    {
        var turns = new FakeTurns();
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddAGUIServer();
        builder.Services.AddSingleton<IConciergeService>(turns);
        builder.Services.AddSingleton<AgenticRunFailures>();
        await using var app = builder.Build();
        app.MapAGUIServer(AgenticConcierge.EndpointPath, AgenticConcierge.CreateAgent);
        await app.StartAsync();

        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()), Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "guest-credential");
        // The page's wiring (AgenticLoopback): the run's ErrorContent becomes an exception.
        using var agui = new RunErrorChatClient(
            new AGUI.Client.AGUIChatClient(new AGUI.Client.AGUIChatClientOptions(http, AgenticConcierge.EndpointPath)));
        var run = agui.GetResponseAsync([new ChatMessage(ChatRole.User, "Pool?")]);
        await turns.Submitted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        turns.Fail(new InvalidOperationException("Temporal is unreachable."));

        var thrown = await Assert.ThrowsAsync<AgenticRunFailedException>(() => run);
        Assert.Equal(
            "The concierge's AG-UI run failed (StreamingError): An error occurred while streaming the agent response.",
            thrown.Message);
        Assert.StartsWith("System.InvalidOperationException: Temporal is unreachable.",
            app.Services.GetRequiredService<AgenticRunFailures>().Latest("guest-credential"));
    }

    /// <summary>
    /// A reply that reports a failure (the API's "unavailable", a model-failure fallback) carries the
    /// cause as an error-details call in Development, and nothing extra otherwise.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AFailureReplyCarriesItsCauseOnlyInDevelopment(bool development)
    {
        var failures = development ? new AgenticRunFailures() : null;

        var rejected = new FakeTurns();
        using var rejecting = new StayWorkflowChatClient(rejected, "credential", failures: failures);
        var first = Collect(rejecting.GetStreamingResponseAsync([new(ChatRole.User, "Pool?")]), out var rejectedUpdates);
        await rejected.Submitted.Task;
        rejected.Fail(new ConciergeApiException("unavailable", "The concierge is temporarily unavailable. Please try again.", 503,
            new InvalidOperationException("Temporal is unreachable.")));
        await first;

        var fallback = new FakeTurns();
        using var answering = new StayWorkflowChatClient(fallback, "credential", failures: failures);
        var second = Collect(answering.GetStreamingResponseAsync([new(ChatRole.User, "Pool?")]), out var fallbackUpdates);
        var turnId = await fallback.Submitted.Task;
        fallback.Complete(new ConciergeTurnResult(turnId, "I couldn't reach the concierge service just now.", 0,
            FailureDetail: "ActivityFailureException: Activity task failed\ncaused by ApplicationFailureException: upstream reset"));
        await second;

        Assert.Equal("The concierge is temporarily unavailable. Please try again.", string.Concat(rejectedUpdates.Select(u => u.Text)));
        Assert.Equal("I couldn't reach the concierge service just now.", string.Concat(fallbackUpdates.Select(u => u.Text)));
        var rejectedDetails = Details(rejectedUpdates);
        var fallbackDetails = Details(fallbackUpdates);
        if (development)
        {
            Assert.Contains("System.InvalidOperationException: Temporal is unreachable.", rejectedDetails);
            Assert.Equal("ActivityFailureException: Activity task failed\ncaused by ApplicationFailureException: upstream reset", fallbackDetails);
        }
        else
        {
            Assert.Null(rejectedDetails);
            Assert.Null(fallbackDetails);
        }
    }

    private static string? Details(List<ChatResponseUpdate> updates)
    {
        var contents = updates.SelectMany(update => update.Contents).ToList();
        return contents.OfType<FunctionCallContent>().SingleOrDefault(call => call.Name == AgenticConcierge.ErrorDetailsTool) is { } call
            ? contents.OfType<FunctionResultContent>().Single(result => result.CallId == call.CallId).Result as string
            : null;
    }

    private static ChatOptions Marked(string turnId) =>
        new() { AdditionalProperties = new() { [ConciergeAnswerStream.ModelOptionKey] = turnId } };

    private static ConciergeAnswerStreamMessage Message(string call, string text) => new("t", call, 0, text);

    private static (AnswerStreamListener Listener, FakeTurns Concierge) Stack() =>
        (new AnswerStreamListener("Host=unused", NullLogger<AnswerStreamListener>.Instance), new FakeTurns());

    /// <summary>Dispatches each full text and waits until the adapter has forwarded something for it.</summary>
    private static async Task Deliver(AnswerStreamListener listener, string turnId, List<ChatResponseUpdate> updates, params string[] texts)
    {
        for (var index = 0; index < texts.Length; index++)
        {
            var before = updates.Count;
            listener.Dispatch(ConciergeAnswerStream.Write(new ConciergeAnswerStreamMessage(turnId, "1:1", index, texts[index])));
            for (var attempt = 0; attempt < 200 && updates.Count == before; attempt++)
            {
                await Task.Delay(10);
            }
        }
    }

    private static async Task<string> Collect(IAsyncEnumerable<ChatResponseUpdate> updates)
    {
        var text = "";
        await foreach (var update in updates)
        {
            text += update.Text;
        }

        return text;
    }

    private static Task Collect(IAsyncEnumerable<ChatResponseUpdate> stream, out List<ChatResponseUpdate> updates)
    {
        var list = new List<ChatResponseUpdate>();
        updates = list;
        return Task.Run(async () =>
        {
            await foreach (var update in stream)
            {
                lock (list)
                {
                    list.Add(update);
                }
            }
        });
    }

    private sealed class Sink : IAnswerStreamSink
    {
        private readonly ConcurrentQueue<ConciergeAnswerStreamMessage> messages = new();

        public IReadOnlyList<ConciergeAnswerStreamMessage> Messages => [.. messages];

        public void Offer(ConciergeAnswerStreamMessage message) => messages.Enqueue(message);
    }

    private sealed class WordModel(string[] words, TimeSpan delay) : IChatClient
    {
        public ChatOptions? Options { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Options = options;
            foreach (var word in words)
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken);
                }

                yield return new ChatResponseUpdate(ChatRole.Assistant, word);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>Submits block until the test completes or fails them.</summary>
    private sealed class FakeTurns : IConciergeService
    {
        private readonly TaskCompletionSource<ConciergeTurnResult> result = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<string> Submitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Complete(ConciergeTurnResult turn) => result.SetResult(turn);

        public void Fail(Exception exception) => result.SetException(exception);

        public Task<ConciergeTurnResult> SubmitTurnAsync(string? bearerCredential, ConciergeTurnRequest request, CancellationToken cancellationToken = default)
        {
            Submitted.TrySetResult(request.TurnId);
            return result.Task;
        }

        public Task<ConciergeSessionResponse> VerifyAsync(ConciergeVerificationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ConciergeHistoryResult> ReadHistoryAsync(string? bearerCredential, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task EndAsync(string? bearerCredential, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ResetAsync(string? bearerCredential, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ConciergeVoiceAuthorization> AuthorizeVoiceAsync(string? bearerCredential, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ConciergeTurnResult> SubmitVoiceTurnAsync(ConciergeSessionContext session, string turnId, string userMessage, CancellationToken cancellationToken = default, bool enableEndChatFunction = false) => throw new NotSupportedException();
        public Task MarkVoiceTurnInterruptedAsync(ConciergeSessionContext session, string turnId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ConciergeRideActivitySnapshot> ReadRideActivitySnapshotAsync(string? bearerCredential, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<ConciergeRideActivityItem> StreamRideActivityAsync(string? bearerCredential, long fromOffset, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ConciergeRideReadState> MarkRideActivitySnapshotReadAsync(string? bearerCredential, long snapshotOffset, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
