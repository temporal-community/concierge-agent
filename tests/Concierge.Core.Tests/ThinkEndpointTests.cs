using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Concierge.Contracts;
using Concierge.Web.Services;
using Concierge.Web.Voice;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Concierge.Core.Tests;

public sealed class ThinkEndpointTests : IDisposable
{
    private const int ThinkPort = 5443;
    private const string Path = "/v1/chat/completions";
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 15, 0, 0, TimeSpan.Zero);
    private static readonly ConciergeSessionContext Stay = new(Guid.NewGuid(), Guid.NewGuid(), Now.AddDays(2));

    private readonly FakeTimeProvider time = new(Now);
    private readonly FakeConcierge concierge = new();
    private readonly BodyTracker bodies = new();
    private readonly CapturedLogs logs = new();
    private readonly WebApplicationFactory<ConciergeService> factory;
    private readonly HttpClient http;
    private readonly VoiceSessionRegistry registry;

    public ThinkEndpointTests()
    {
        factory = new WebApplicationFactory<ConciergeService>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("THINK_PORT", ThinkPort.ToString());
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(time);
                services.RemoveAll<IConciergeService>();
                services.AddSingleton<IConciergeService>(concierge);
                services.AddSingleton<IStartupFilter>(new ThinkListenerFilter(bodies));
                services.AddLogging(logging => logging.AddProvider(logs));
            });
        });
        http = factory.CreateClient(new() { BaseAddress = new Uri("https://hotel.example") });
        registry = factory.Services.GetRequiredService<VoiceSessionRegistry>();
    }

    public void Dispose()
    {
        http.Dispose();
        factory.Dispose();
    }

    [Fact]
    public async Task RejectsMissingUnknownRevokedExpiredAndEndedBearersWithoutReadingBody()
    {
        var revoked = registry.Open(Stay, null);
        registry.Close(revoked);
        var ended = registry.Open(Stay with { SessionId = Guid.NewGuid() }, null);
        registry.EndSession(ended.Session.Stay.SessionId);
        var expired = registry.Open(Stay with { SessionId = Guid.NewGuid() }, null);
        time.Advance(VoiceSessionRegistry.ConnectionLifetime);

        foreach (var authorization in new[]
        {
            null, "Bearer", "Bearer not-a-voice-bearer", "Basic " + revoked.Bearer,
            "Bearer " + revoked.Bearer, "Bearer " + ended.Bearer, "Bearer " + expired.Bearer,
        })
        {
            using var response = await SendAsync(Json(Chat(("user", "Hello"))), authorization);

            await AssertOpenAIError(response, HttpStatusCode.Unauthorized, "invalid_request_error", "invalid_api_key");
        }

        Assert.Equal(0, bodies.BytesRead);
        Assert.Empty(concierge.Submissions);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OversizedBodyReturns413Json(bool sendContentLength)
    {
        var connection = registry.Open(Stay, null);
        var body = Chat(("user", new string('a', ThinkEndpoint.MaxBodyBytes)));
        HttpContent content = sendContentLength
            ? new StringContent(body, Encoding.UTF8, "application/json")
            : new StreamContent(new UnknownLengthStream(Encoding.UTF8.GetBytes(body)));

        using var response = await PostAsync(content, connection.Bearer);

        await AssertOpenAIError(response, HttpStatusCode.RequestEntityTooLarge, "invalid_request_error", "request_too_large");
        Assert.Empty(concierge.Submissions);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("[]")]
    [InlineData("""{"model":"concierge"}""")]
    [InlineData("""{"messages":[{"role":"system","content":"Be brief."},{"role":"assistant","content":"Hi"}]}""")]
    [InlineData("""{"messages":[{"role":"user","content":[{"type":"text","text":"Hi"}]}]}""")]
    [InlineData("""{"messages":[{"role":"user","content":"   "}]}""")]
    public async Task MalformedRequestsReturn400Json(string body)
    {
        var connection = registry.Open(Stay, null);

        using var response = await PostAsync(Json(body), connection.Bearer);

        await AssertOpenAIError(response, HttpStatusCode.BadRequest, "invalid_request_error", "invalid_request");
        Assert.Empty(concierge.Submissions);
    }

    [Fact]
    public async Task AnswersWithExactSseFramingAndSubmitsOnlyTheLatestUserMessage()
    {
        var connection = registry.Open(Stay, null);
        concierge.Answer = _ => Task.FromResult("Breakfast is served 7–10 \"daily\".");

        using var response = await PostAsync(Json(Chat(("system", "Ignored prompt"), ("user", "Is there a pool?"), ("assistant", "Yes."), ("user", " When is breakfast? "))),
            connection.Bearer);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        var turnId = ThinkRequest.TurnId(connection.Session.Id, connection.Number, 2, "When is breakfast?");
        var created = Now.ToUnixTimeSeconds();
        string Chunk(string delta, string finish) =>
            $$"""data: {"id":"{{turnId}}","object":"chat.completion.chunk","created":{{created}},"model":"deepgram-model","choices":[{"index":0,"delta":{{delta}},"finish_reason":{{finish}}}]}""" + "\n\n";
        Assert.Equal(
            Chunk("""{"role":"assistant"}""", "null")
            + Chunk("""{"content":"Breakfast is served 7\u201310 \u0022daily\u0022."}""", "null")
            + Chunk("{}", "\"stop\"")
            + "data: [DONE]\n\n",
            await response.Content.ReadAsStringAsync());

        var submission = Assert.Single(concierge.Submissions);
        Assert.Equal(turnId, submission.TurnId);
        Assert.Equal("When is breakfast?", submission.UserMessage);
        Assert.Same(Stay, submission.Session);
        Assert.True(submission.EnableEndChatFunction);
        // Admitted Temporal work must not depend on the HTTP request's lifetime.
        Assert.False(submission.CancellationToken.CanBeCanceled);

        // The relay binds this completed Think response to the next assistant playback event.
        connection.Session.StartReplyPlayback();
        var interruptionNumber = connection.Session.BeginPlaybackInterruption();
        Assert.True(connection.Session.TryCompletePlaybackInterruption(
            interruptionNumber, true, turnId, out var playbackTurnId));
        Assert.Equal(turnId, playbackTurnId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCanceledSseWriteRemovesItsPlaybackRegistration(bool cancelRequest)
    {
        var connection = registry.Open(Stay, null);
        var failedContext = new DefaultHttpContext();
        if (cancelRequest)
        {
            failedContext.RequestAborted = new CancellationToken(canceled: true);
        }
        else
        {
            failedContext.Response.Body = new FailedFlushStream();
        }

        if (cancelRequest)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ThinkEndpoint.WriteRegisteredSseAsync(
                    failedContext, connection.Session, "stale-turn", "model", 1, "Stale answer"));
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(() =>
                ThinkEndpoint.WriteRegisteredSseAsync(
                    failedContext, connection.Session, "stale-turn", "model", 1, "Stale answer"));
        }

        using var nextResponse = await PostAsync(Json(Chat(("user", "Next question"))), connection.Bearer);
        Assert.Equal(HttpStatusCode.OK, nextResponse.StatusCode);
        var nextTurnId = ThinkRequest.TurnId(connection.Session.Id, connection.Number, 1, "Next question");
        connection.Session.StartReplyPlayback();
        var interruptionNumber = connection.Session.BeginPlaybackInterruption();

        Assert.True(connection.Session.TryCompletePlaybackInterruption(
            interruptionNumber, true, nextTurnId, out var playbackTurnId));
        Assert.Equal(nextTurnId, playbackTurnId);
    }

    [Fact]
    public async Task EndChatToolCallIsForwardedAndMatchingResultGetsFarewellWithoutAnotherTurn()
    {
        using var traces = new EndChatTraceCapture();
        var connection = registry.Open(Stay, null);
        concierge.EndChatFunctionCall = new ConciergeEndChatFunctionCall(
            "call_end_chat_1", ConciergeEndChat.FunctionName, "{}");

        using var initial = await PostAsync(Json(Chat(("user", "Please end our chat."))), connection.Bearer);
        var initialSse = await initial.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        Assert.Contains("\"finish_reason\":\"tool_calls\"", initialSse, StringComparison.Ordinal);
        Assert.Contains("\"id\":\"call_end_chat_1\"", initialSse, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"end_chat\"", initialSse, StringComparison.Ordinal);
        Assert.Contains(":1:", Assert.Single(concierge.Submissions).TurnId, StringComparison.Ordinal);
        Assert.True(concierge.Submissions[0].EnableEndChatFunction);

        Assert.True(connection.ConfirmEndChatCall("call_end_chat_1"));
        using var followup = await PostAsync(Json(
            """{"model":"deepgram-model","stream":true,"messages":[{"role":"user","content":"Please end our chat."},{"role":"assistant","tool_calls":[{"id":"call_end_chat_1","type":"function","function":{"name":"end_chat","arguments":"{}"}}]},{"role":"tool","tool_call_id":"call_end_chat_1","name":"end_chat","content":"End chat confirmed."}]}"""),
            connection.Bearer);

        Assert.Equal(HttpStatusCode.OK, followup.StatusCode);
        Assert.Contains(ConciergeEndChat.Farewell, await followup.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Single(concierge.Submissions);
        Assert.True(connection.HasEndChatResult);
        foreach (var name in new[] { "call_registered", "tool_result_accepted", "farewell_returned" })
        {
            var eventName = "concierge.voice.end_chat." + name;
            var activity = traces.AssertEvent(eventName, connection.Session.Id);
            Assert.Equal("call_end_chat_1", activity.GetTagItem("concierge.voice.function_call_id"));
            Assert.Contains(logs.Entries, entry =>
                Equals(entry.Values.GetValueOrDefault("EndChatEvent"), eventName)
                && Equals(entry.Values["FunctionCallId"], "call_end_chat_1"));
        }
    }

    [Fact]
    public async Task EndChatToolResultMustMatchConfirmedCall()
    {
        var connection = registry.Open(Stay, null);
        Assert.True(connection.RegisterEndChatCall("expected", VoiceEndChatTests.AdmitTurn(connection)));
        Assert.True(connection.ConfirmEndChatCall("expected"));

        using var response = await PostAsync(Json(
            """{"model":"deepgram-model","messages":[{"role":"assistant","tool_calls":[{"id":"different","type":"function","function":{"name":"end_chat","arguments":"{}"}}]},{"role":"tool","tool_call_id":"different","name":"end_chat","content":"ok"}]}"""),
            connection.Bearer);

        await AssertOpenAIError(response, HttpStatusCode.BadRequest, "invalid_request_error", "invalid_request");
        Assert.False(connection.HasEndChatResult);
        Assert.Empty(concierge.Submissions);
    }

    [Fact]
    public void DeepgramSettingsUseFluxListenAndFluxSpeakDefaultsAndDeferEndChat()
    {
        using var settings = JsonDocument.Parse(VoiceAgentSettings.Build(
            new DeepgramVoiceOptions(),
            new Uri("https://think.example/v1/chat/completions"),
            "opaque-bearer",
            hasTurns: false));
        var function = settings.RootElement
            .GetProperty("agent").GetProperty("think").GetProperty("functions")[0];

        Assert.Equal(ConciergeEndChat.FunctionName, function.GetProperty("name").GetString());
        Assert.True(function.GetProperty("defer_until_eot").GetBoolean());
        Assert.False(function.TryGetProperty("endpoint", out _));
        Assert.Equal(JsonValueKind.Object, function.GetProperty("parameters").GetProperty("properties").ValueKind);

        var agent = settings.RootElement.GetProperty("agent");
        var listen = agent.GetProperty("listen").GetProperty("provider");
        Assert.Equal("flux-general-en", listen.GetProperty("model").GetString());
        Assert.Equal("v2", listen.GetProperty("version").GetString());
        var speak = agent.GetProperty("speak").GetProperty("provider");
        Assert.Equal("flux-hannah-en", speak.GetProperty("model").GetString());
        Assert.Equal("v2", speak.GetProperty("version").GetString());
        Assert.Equal(1.0, speak.GetProperty("speed").GetDouble());
    }

    [Fact]
    public void DeepgramNovaAndAuraOverridesKeepTheV1ProviderContract()
    {
        using var settings = JsonDocument.Parse(VoiceAgentSettings.Build(
            new DeepgramVoiceOptions
            {
                ListenModel = "nova-3",
                SpeakModel = "aura-2-thalia-en",
            },
            new Uri("https://think.example/v1/chat/completions"),
            "opaque-bearer",
            hasTurns: false));
        var agent = settings.RootElement.GetProperty("agent");
        var listen = agent.GetProperty("listen").GetProperty("provider");
        var speak = agent.GetProperty("speak").GetProperty("provider");

        Assert.Equal("nova-3", listen.GetProperty("model").GetString());
        Assert.Equal("v1", listen.GetProperty("version").GetString());
        Assert.Equal("aura-2-thalia-en", speak.GetProperty("model").GetString());
        Assert.Equal("v1", speak.GetProperty("version").GetString());
        Assert.Equal(1.0, speak.GetProperty("speed").GetDouble());
    }

    [Fact]
    public void DeepgramFluxSpeakOverrideKeepsTheV2ProviderContract()
    {
        using var settings = JsonDocument.Parse(VoiceAgentSettings.Build(
            new DeepgramVoiceOptions
            {
                SpeakModel = "flux-sienna-en",
                Speed = 1.2,
            },
            new Uri("https://think.example/v1/chat/completions"),
            "opaque-bearer",
            hasTurns: false));
        var speak = settings.RootElement.GetProperty("agent").GetProperty("speak").GetProperty("provider");

        Assert.Equal("flux-sienna-en", speak.GetProperty("model").GetString());
        Assert.Equal("v2", speak.GetProperty("version").GetString());
        Assert.Equal(1.2, speak.GetProperty("speed").GetDouble());
    }

    [Theory]
    [InlineData("flux-sienna-en", 0.5, true)]
    [InlineData("flux-sienna-en", 1.2, true)]
    [InlineData("flux-sienna-en", 0.55, true)]
    [InlineData("flux-sienna-en", 0.56, false)]
    [InlineData("aura-2-thalia-en", 0.7, true)]
    [InlineData("aura-2-thalia-en", 0.65, false)]
    [InlineData("aura:2-thalia-en", 1.5, true)]
    [InlineData("other", 0.5, true)]
    [InlineData("other", 1.5, true)]
    [InlineData("other", 1.51, false)]
    public void TtsSpeedValidationFollowsProviderFamily(string model, double speed, bool expected) =>
        Assert.Equal(expected, VoiceAgentSettings.IsValidSpeakSpeed(model, speed));

    [Fact]
    public void TtsSpeedValidationRejectsNonFiniteValues()
    {
        Assert.False(VoiceAgentSettings.IsValidSpeakSpeed("other", double.NaN));
        Assert.False(VoiceAgentSettings.IsValidSpeakSpeed("flux-sienna-en", double.PositiveInfinity));
    }

    [Fact]
    public async Task ReconnectUsesANewConnectionNumberInTheTurnId()
    {
        var first = registry.Open(Stay, null);
        var second = registry.Open(Stay, first.Session.Id);

        using var response = await PostAsync(Json(Chat(("user", "Hello"))), second.Bearer);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            ThinkRequest.TurnId(first.Session.Id, second.Number, 1, "Hello"),
            Assert.Single(concierge.Submissions).TurnId);
        Assert.True(ThinkRequest.TurnId(Guid.NewGuid(), int.MinValue, int.MinValue, "Hello").Length
            <= ConciergeTurnLimits.MaxTurnIdLength);
    }

    [Fact]
    public async Task AcceptedThinkTurnResetsTheVoiceIdleDeadline()
    {
        var connection = registry.Open(Stay, null);
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.True(connection.IsIdle(TimeSpan.FromMinutes(2)));

        using var response = await PostAsync(Json(Chat(("user", "Is breakfast served?"))), connection.Bearer);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(connection.IsIdle(TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public async Task ConcurrentRequestsWithTheSameTurnIdShareOneSubmission()
    {
        var connection = registry.Open(Stay, null);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        concierge.Answer = _ => release.Task;

        var first = PostAsync(Json(Chat(("user", "Late checkout?"))), connection.Bearer);
        await concierge.FirstSubmission.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var retry = PostAsync(Json(Chat(("user", "Late checkout?"))), connection.Bearer);
        await Task.Delay(100);
        release.SetResult("Until noon.");

        using var firstResponse = await first;
        using var retryResponse = await retry;
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retryResponse.StatusCode);
        Assert.Equal(await firstResponse.Content.ReadAsStringAsync(), await retryResponse.Content.ReadAsStringAsync());
        Assert.Single(concierge.Submissions);
    }

    [Fact]
    public async Task ConcurrentRequestsWithChangedTextAndTheSameOrdinalStartDistinctTurns()
    {
        var connection = registry.Open(Stay, null);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        concierge.Answer = _ => release.Task;

        var first = PostAsync(Json(Chat(("user", "Is breakfast served?"))), connection.Bearer);
        await concierge.FirstSubmission.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var resumed = PostAsync(Json(Chat(("user", "Is breakfast served every day?"))), connection.Bearer);
        var secondStarted = await Task.WhenAny(concierge.SecondSubmission.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        release.SetResult("Yes.");

        using var firstResponse = await first;
        using var resumedResponse = await resumed;

        Assert.Same(concierge.SecondSubmission.Task, secondStarted);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, resumedResponse.StatusCode);
        var submissions = concierge.Submissions;
        Assert.Equal(2, submissions.Count);
        Assert.NotEqual(submissions[0].TurnId, submissions[1].TurnId);
        Assert.Equal("Is breakfast served?", submissions[0].UserMessage);
        Assert.Equal("Is breakfast served every day?", submissions[1].UserMessage);
    }

    [Fact]
    public async Task CallerDisconnectLogsConnectionUserOrdinalAndTurnId()
    {
        var connection = registry.Open(Stay, null);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        concierge.Answer = _ => release.Task;
        using var abort = new CancellationTokenSource();

        var request = new HttpRequestMessage(HttpMethod.Post, Path)
        {
            Content = Json(Chat(("user", "Is there a pool?"), ("assistant", "Yes."), ("user", "Late checkout?"))),
        };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + connection.Bearer);
        var pending = http.SendAsync(request, abort.Token);
        await concierge.FirstSubmission.Task.WaitAsync(TimeSpan.FromSeconds(10));
        abort.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        CapturedLogs.CapturedLog? entry = null;
        for (var attempt = 0; attempt < 100 && entry is null; attempt++)
        {
            entry = logs.Entries.FirstOrDefault(log => log.Message.StartsWith("Think caller disconnected", StringComparison.Ordinal));
            if (entry is null)
            {
                await Task.Delay(50);
            }
        }

        release.SetResult("Until noon.");
        Assert.NotNull(entry);
        Assert.Equal(connection.Number, entry.Values["ConnectionNumber"]);
        Assert.Equal(2, entry.Values["UserOrdinal"]);
        Assert.Equal(
            ThinkRequest.TurnId(connection.Session.Id, connection.Number, 2, "Late checkout?"),
            entry.Values["TurnId"]);
        Assert.DoesNotContain("Late checkout?", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TurnIdsAreStableForIdenticalTextAndIsolateTextConnectionAndOrdinal()
    {
        var voiceSessionId = Guid.NewGuid();
        const string userMessage = "Is breakfast served every day?";
        var turnId = ThinkRequest.TurnId(voiceSessionId, 3, 7, userMessage);

        Assert.Equal(turnId, ThinkRequest.TurnId(voiceSessionId, 3, 7, userMessage));
        Assert.NotEqual(turnId, ThinkRequest.TurnId(voiceSessionId, 3, 7, "Is breakfast served?"));
        Assert.NotEqual(turnId, ThinkRequest.TurnId(voiceSessionId, 4, 7, userMessage));
        Assert.NotEqual(turnId, ThinkRequest.TurnId(voiceSessionId, 3, 8, userMessage));
        Assert.True(ThinkRequest.TurnId(Guid.NewGuid(), int.MinValue, int.MinValue, userMessage).Length
            <= ConciergeTurnLimits.MaxTurnIdLength);
    }

    [Fact]
    public async Task WorkflowRejectionReturnsJsonBeforeAnySseBytes()
    {
        var connection = registry.Open(Stay, null);
        concierge.Answer = _ => throw new ConciergeApiException(
            "turn_conflict", "The turn identifier was already used.", StatusCodes.Status409Conflict);

        using var response = await PostAsync(Json(Chat(("user", "Hello"))), connection.Bearer);

        await AssertOpenAIError(response, HttpStatusCode.Conflict, "invalid_request_error", "turn_conflict");
    }

    private Task<HttpResponseMessage> PostAsync(HttpContent content, string bearer) =>
        SendAsync(content, "Bearer " + bearer);

    private Task<HttpResponseMessage> SendAsync(HttpContent content, string? authorization)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Path) { Content = content };
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        return http.SendAsync(request);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static string Chat(params (string Role, string Content)[] messages) =>
        JsonSerializer.Serialize(new
        {
            model = "deepgram-model",
            stream = true,
            messages = messages.Select(message => new { role = message.Role, content = message.Content }),
        });

    private static async Task AssertOpenAIError(
        HttpResponseMessage response, HttpStatusCode status, string type, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = json.RootElement.GetProperty("error");
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
        Assert.Equal(type, error.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, error.GetProperty("param").ValueKind);
        Assert.Equal(code, error.GetProperty("code").GetString());
    }

    private sealed record Submission(
        ConciergeSessionContext Session,
        string TurnId,
        string UserMessage,
        CancellationToken CancellationToken,
        bool EnableEndChatFunction);

    private sealed class FakeConcierge : IConciergeService
    {
        private readonly Lock gate = new();
        private readonly List<Submission> submissions = [];

        public Func<string, Task<string>> Answer { get; set; } = message => Task.FromResult("Answer");
        public ConciergeEndChatFunctionCall? EndChatFunctionCall { get; set; }

        public TaskCompletionSource FirstSubmission { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondSubmission { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<Submission> Submissions
        {
            get
            {
                lock (gate)
                {
                    return submissions.ToList();
                }
            }
        }

        /// <summary>Records the voice turn and returns the arranged answer.</summary>
        public async Task<ConciergeTurnResult> SubmitVoiceTurnAsync(
            ConciergeSessionContext session,
            string turnId,
            string userMessage,
            CancellationToken cancellationToken = default,
            bool enableEndChatFunction = false)
        {
            lock (gate)
            {
                submissions.Add(new Submission(
                    session, turnId, userMessage, cancellationToken, enableEndChatFunction));
            }

            if (Submissions.Count >= 2)
            {
                SecondSubmission.TrySetResult();
            }

            FirstSubmission.TrySetResult();
            return EndChatFunctionCall is { } call
                ? new ConciergeTurnResult(turnId, ConciergeEndChat.Farewell, 1, call)
                : new ConciergeTurnResult(turnId, await Answer(userMessage), 1);
        }

        /// <summary>Not used by the Think handler.</summary>
        public Task<ConciergeVoiceAuthorization> AuthorizeVoiceAsync(
            string? bearerCredential, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>Not used by the Think handler.</summary>
        public Task<ConciergeSessionResponse> VerifyAsync(
            ConciergeVerificationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>Not used by the Think handler.</summary>
        public Task<ConciergeTurnResult> SubmitTurnAsync(
            string? bearerCredential, ConciergeTurnRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        /// <summary>Not used by the Think handler.</summary>
        public Task<ConciergeHistoryResult> ReadHistoryAsync(
            string? bearerCredential, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>Not used by the Think handler.</summary>
        public Task<ConciergeRideActivitySnapshot> ReadRideActivitySnapshotAsync(
            string? bearerCredential, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>Not used by the Think handler.</summary>
        public IAsyncEnumerable<ConciergeRideActivityItem> StreamRideActivityAsync(
            string? bearerCredential, long fromOffset, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        /// <summary>Not used by the Think handler.</summary>
        public Task<ConciergeRideReadState> MarkRideActivitySnapshotReadAsync(
            string? bearerCredential, long snapshotOffset, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        /// <summary>Not used by the Think handler.</summary>
        public Task EndAsync(string? bearerCredential, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        /// <summary>Not used by the Think handler.</summary>
        public Task MarkVoiceTurnInterruptedAsync(
            ConciergeSessionContext session, string turnId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class BodyTracker
    {
        private long bytesRead;

        public long BytesRead => Interlocked.Read(ref bytesRead);

        public void Add(int count) => Interlocked.Add(ref bytesRead, count);
    }

    private sealed class FailedFlushStream : MemoryStream
    {
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            Task.FromException(new IOException("SSE flush failed."));
    }

    // TestServer has no real listener: set the server-observed port and observe body reads.
    private sealed class ThinkListenerFilter(BodyTracker tracker) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, continuation) =>
            {
                context.Connection.LocalPort = ThinkPort;
                context.Request.Body = new TrackingStream(context.Request.Body, tracker);
                return continuation(context);
            });
            next(app);
        };
    }

    private sealed class TrackingStream(Stream inner, BodyTracker tracker) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            tracker.Add(read);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            tracker.Add(read);
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // Forces a request without Content-Length so the handler's streaming cap is what rejects it.
    private sealed class UnknownLengthStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}
