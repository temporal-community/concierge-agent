using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AGUI.Client;
using Concierge.Contracts;
using Concierge.Web.Agentic;
using Concierge.Web.Services;
using Microsoft.AspNetCore.Components.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Core.Tests;

/// <summary>PROTOTYPE (ADR 0014 phase 2): the stay workflow behind an AG-UI endpoint.</summary>
public sealed class AgenticConciergeTests(PostgreSqlFixture postgreSql) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task AgUiClientRunsATurnOnTheStayWorkflowAndOtherPagesAreNotified()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var today = ConciergeTestHost.HotelToday;
        var stay = await host.SeedStayAsync("Rivera", "412", today, today.AddDays(2));
        var session = await VerifyAsync(host, "Rivera", "412");
        host.ChatClient.AnswerFactory = _ => "The pool opens at 8:00 a.m.";
        var changes = new ConcurrentQueue<StayTranscriptChange>();
        using var _ = host.App.Services.GetRequiredService<StayTranscriptNotifier>().Subscribe(stay, changes.Enqueue);

        using var http = new HttpClient { BaseAddress = host.Http.BaseAddress, Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Credential);
        using var agui = new AGUIChatClient(new AGUIChatClientOptions(http, AgenticConcierge.EndpointPath));

        // AG-UI resends the conversation; only the latest user message becomes a turn.
        var response = await agui.GetResponseAsync(
        [
            new ChatMessage(ChatRole.User, "Hello"),
            new ChatMessage(ChatRole.Assistant, "Hi, how can I help?"),
            new ChatMessage(ChatRole.User, "When does the pool open?"),
        ]);

        Assert.Equal("The pool opens at 8:00 a.m.", response.Text);
        Assert.Equal("When does the pool open?", host.ChatClient.Requests[^1].LastUserText);
        var turn = Assert.Single((await HistoryAsync(host, session.Credential)).Turns);
        Assert.Equal("When does the pool open?", turn.GuestMessage);
        Assert.Equal(StayWorkflowChatClient.Channel, turn.Channel);
        Assert.StartsWith("agui:", turn.TurnId, StringComparison.Ordinal);
        Assert.Equal(
            [
                new StayTranscriptChange(stay, session.SessionId, StayTranscriptChangeKind.TurnStarted, "When does the pool open?"),
                new StayTranscriptChange(stay, session.SessionId, StayTranscriptChangeKind.TurnsChanged),
            ],
            changes);
    }

    /// <summary>
    /// ADR 0015: an upcoming-rides answer crosses AG-UI as text plus an informational show_ride_card call
    /// and its result; the host must not re-run the chat client (one tool call, one stay turn).
    /// </summary>
    [Fact]
    public async Task UpcomingRidesTurnCarriesARideCardOverAgUiWithoutASecondTurn()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var today = ConciergeTestHost.HotelToday;
        await host.SeedStayAsync("Rivera", "412", today, today.AddDays(2));
        var session = await VerifyAsync(host, "Rivera", "412");
        var rideId = Guid.NewGuid();
        var pickupAt = DateTimeOffset.Parse("2026-10-21T10:00:00-04:00");
        host.ActionTools.QueueResult(
            Concierge.WorkflowServer.AI.ConciergeToolDefinitions.GetRideHistory,
            System.Text.Json.JsonSerializer.Serialize(
                new Concierge.WorkflowServer.AI.ConciergeRideActionResult(true, null, null,
                [
                    new Concierge.WorkflowServer.AI.RideSummary(
                        rideId, "Booked", "Hotel ZGY", "South Beach, Miami Beach, Florida", pickupAt, null, null, null),
                ]),
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        host.ChatClient.ScriptedResponses.Enqueue(new ChatResponse(new ChatMessage(ChatRole.Assistant,
        [
            new FunctionCallContent("history-1", Concierge.WorkflowServer.AI.ConciergeToolDefinitions.GetRideHistory,
                new Dictionary<string, object?> { ["when"] = "upcoming" }),
        ])));
        host.ChatClient.AnswerFactory = _ => "You have one upcoming ride.";

        using var http = new HttpClient { BaseAddress = host.Http.BaseAddress, Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Credential);
        using var agui = new AGUIChatClient(new AGUIChatClientOptions(http, AgenticConcierge.EndpointPath));
        var contents = new List<AIContent>();
        await foreach (var update in agui.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "What rides do I have scheduled?")]))
        {
            contents.AddRange(update.Contents);
        }

        Assert.Contains(contents, content => content is TextContent { Text.Length: > 0 });
        var call = Assert.Single(contents.OfType<FunctionCallContent>());
        Assert.Equal(AgenticConcierge.RideCardTool, call.Name);
        Assert.True(call.InformationalOnly);
        var card = AgenticConcierge.ReadRideCard(Assert.Single(contents.OfType<FunctionResultContent>(), result => result.CallId == call.CallId));
        Assert.Equal(ConciergeRideCard.Scheduled, card?.Kind);
        Assert.Equal(rideId, Assert.Single(card!.Rides).RideId);
        Assert.Equal(pickupAt, card.Rides[0].PickupAtUtc);

        Assert.Single(host.ActionTools.Calls);
        var turn = Assert.Single((await HistoryAsync(host, session.Credential)).Turns);
        Assert.Equal(rideId, turn.RideCard?.Rides.Single().RideId);
    }

    /// <summary>
    /// ADR 0015: the model activity's partial text crosses Postgres NOTIFY and AG-UI while the turn
    /// runs, and what the page ends up with is exactly the committed answer, from one stay turn.
    /// </summary>
    [Fact]
    public async Task AnswerStreamsOverAgUiWhileTheTurnRunsAndEndsAsTheCommittedAnswer()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql, streamAnswers: true);
        var today = ConciergeTestHost.HotelToday;
        await host.SeedStayAsync("Rivera", "412", today, today.AddDays(2));
        var session = await VerifyAsync(host, "Rivera", "412");
        const string answer = "The pool opens at 8 a.m. and closes at 10 p.m. every day.";
        host.ChatClient.AnswerFactory = _ => answer;
        host.ChatClient.StreamWordDelay = TimeSpan.FromMilliseconds(120);

        var (updates, completedAt) = await RunAsync(host, session.Credential, "When does the pool open?");

        var texts = updates.Where(update => update.Update.Text.Length > 0).ToList();
        Assert.True(texts.Count > 2, $"Expected the answer in several parts, got {texts.Count}.");
        Assert.True(texts[0].At < completedAt - TimeSpan.FromMilliseconds(500), "The first text arrived only at the end of the run.");
        Assert.Equal(answer, string.Concat(texts.Select(update => update.Update.Text)));
        Assert.DoesNotContain(updates.SelectMany(update => update.Update.Contents), content => content is FunctionCallContent);
        var turn = Assert.Single((await HistoryAsync(host, session.Credential)).Turns);
        Assert.Equal(answer, turn.AssistantMessage);
    }

    /// <summary>
    /// ADR 0015: text the model streams alongside a tool call is dropped by the workflow. The page gets
    /// that provisional text, then a correction carrying the committed answer.
    /// </summary>
    [Fact]
    public async Task TextStreamedBeforeAToolCallEndsWithACorrection()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql, streamAnswers: true);
        var today = ConciergeTestHost.HotelToday;
        await host.SeedStayAsync("Rivera", "412", today, today.AddDays(2));
        var session = await VerifyAsync(host, "Rivera", "412");
        host.ActionTools.QueueResult(
            Concierge.WorkflowServer.AI.ConciergeToolDefinitions.GetRideHistory,
            System.Text.Json.JsonSerializer.Serialize(
                new Concierge.WorkflowServer.AI.ConciergeRideActionResult(true, null, null, []),
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        host.ChatClient.ScriptedResponses.Enqueue(new ChatResponse(new ChatMessage(ChatRole.Assistant,
        [
            new TextContent("Let me look that up for you. "),
            new FunctionCallContent("history-1", Concierge.WorkflowServer.AI.ConciergeToolDefinitions.GetRideHistory,
                new Dictionary<string, object?> { ["when"] = "upcoming" }),
        ])));
        host.ChatClient.AnswerFactory = _ => "You have no upcoming rides.";
        host.ChatClient.StreamWordDelay = TimeSpan.FromMilliseconds(100);

        var (updates, _) = await RunAsync(host, session.Credential, "What rides do I have?");

        var contents = updates.SelectMany(update => update.Update.Contents).ToList();
        Assert.StartsWith("Let me", string.Concat(contents.OfType<TextContent>().Select(text => text.Text)), StringComparison.Ordinal);
        var call = Assert.Single(contents.OfType<FunctionCallContent>());
        Assert.Equal(AgenticConcierge.AnswerCorrectionTool, call.Name);
        // The workflow's own reply for an empty history, not the model's text: the case a correction exists for.
        var committed = Assert.Single((await HistoryAsync(host, session.Credential)).Turns).AssistantMessage;
        Assert.DoesNotContain("Let me", committed, StringComparison.Ordinal);
        Assert.Equal(committed,
            Assert.Single(contents.OfType<FunctionResultContent>(), result => result.CallId == call.CallId).Result?.ToString()?.Trim('"'));
    }

    private static async Task<(List<(ChatResponseUpdate Update, TimeSpan At)> Updates, TimeSpan CompletedAt)> RunAsync(
        ConciergeTestHost host, string credential, string message)
    {
        using var http = new HttpClient { BaseAddress = host.Http.BaseAddress, Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        using var agui = new AGUIChatClient(new AGUIChatClientOptions(http, AgenticConcierge.EndpointPath));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var updates = new List<(ChatResponseUpdate, TimeSpan)>();
        await foreach (var update in agui.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, message)]))
        {
            updates.Add((update, clock.Elapsed));
        }

        return (updates, clock.Elapsed);
    }

    [Fact]
    public async Task AgUiEndpointWithoutACredentialHasNoAgentAndAnInvalidOneGetsTheActionableError()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var today = ConciergeTestHost.HotelToday;
        await host.SeedStayAsync("Rivera", "412", today, today.AddDays(2));

        var anonymous = await host.Http.PostAsync(
            AgenticConcierge.EndpointPath,
            JsonContent.Create(new { threadId = "t", runId = "r", messages = Array.Empty<object>() }));
        Assert.Equal(HttpStatusCode.NotFound, anonymous.StatusCode);

        using var http = new HttpClient { BaseAddress = host.Http.BaseAddress, Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "forged-credential");
        using var agui = new AGUIChatClient(new AGUIChatClientOptions(http, AgenticConcierge.EndpointPath));
        // The concierge's own actionable message reaches the guest, and nothing reached the workflow.
        var rejected = await agui.GetResponseAsync([new ChatMessage(ChatRole.User, "Hi")]);
        Assert.Equal("Authenticate again to continue.", rejected.Text);
        Assert.Empty(host.ChatClient.Requests);
    }

    [Fact]
    public async Task StayHistoryRestoresAsAlternatingGuestAndConciergeBlocks()
    {
        var now = DateTimeOffset.UtcNow;
        var thread = AgenticConcierge.ThreadFrom(Guid.NewGuid(),
        [
            new ConciergeTranscriptTurn("dev:1", 0, "voice", "When does the pool open?", "8 a.m.", now),
            new ConciergeTranscriptTurn("web-2", 1, "web", "Weather tomorrow?", "Rain, 82°F.", now),
        ]);
        using var agent = new UIAgent(new NoChatClient(), options => options.Thread = thread);

        var blocks = await agent.RestoreAsync(CancellationToken.None);

        Assert.Equal(
            ["user:When does the pool open?", "assistant:8 a.m.", "user:Weather tomorrow?", "assistant:Rain, 82°F."],
            blocks.Select(block => $"{block.Role}:{(block as RichContentBlock)?.RawText}"));
    }

    [Fact]
    public void LatestUserTextIgnoresEarlierTurnsAndRejectsEmptyInput()
    {
        Assert.Equal("Second", StayWorkflowChatClient.LatestUserText(
        [
            new ChatMessage(ChatRole.User, "First"),
            new ChatMessage(ChatRole.Assistant, "Reply"),
            new ChatMessage(ChatRole.User, "  Second "),
        ]));
        var error = Assert.Throws<ConciergeApiException>(() => StayWorkflowChatClient.LatestUserText(
            [new ChatMessage(ChatRole.Assistant, "Only the concierge spoke.")]));
        Assert.Equal(400, error.StatusCode);
    }

    private static async Task<ConciergeSessionResponse> VerifyAsync(ConciergeTestHost host, string lastName, string room)
    {
        var response = await host.Http.PostAsJsonAsync(
            "/api/concierge/sessions", new ConciergeVerificationRequest(lastName, room));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ConciergeSessionResponse>())!;
    }

    private static async Task<ConciergeHistoryResult> HistoryAsync(ConciergeTestHost host, string credential)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/concierge/history");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        var response = await host.Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ConciergeHistoryResult>())!;
    }

    private sealed class NoChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
