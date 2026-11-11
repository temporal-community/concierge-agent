using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Concierge.Contracts;
using Concierge.WorkflowServer.AI;
using Concierge.Web.Services;
using Concierge.WorkflowServer.Workflows;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Temporalio.Client;
using Temporalio.Converters;
using Temporalio.Exceptions;
using Temporalio.Worker;
using TemporalCommunity.Extensions.AI;

namespace Concierge.Core.Tests;

/// <summary>Regressions using Web's real entry point, not the shared host's client registration.</summary>
public sealed class ProductionWebBackendTests(PostgreSqlFixture postgreSql)
    : IClassFixture<PostgreSqlFixture>
{
    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(600, 100, true)]
    [InlineData(0, 5, false)]
    [InlineData(601, 5, false)]
    [InlineData(30, 0, false)]
    [InlineData(30, 101, false)]
    public void TranscriptPersistenceConfigurationIsValidatedAtWebStartup(
        int flushIntervalSeconds,
        int flushBatchSize,
        bool valid)
    {
        using var factory = new WebApplicationFactory<ConciergeService>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Concierge:Persistence:FlushIntervalSeconds"] = flushIntervalSeconds.ToString(),
                    ["Concierge:Persistence:FlushBatchSize"] = flushBatchSize.ToString(),
                }));
        });

        if (!valid)
        {
            var failure = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
            Assert.Contains(
                flushIntervalSeconds is < 1 or > 600
                    ? "FlushIntervalSeconds must be between 1 and 600"
                    : "FlushBatchSize must be between 1 and 100",
                failure.Message);
            return;
        }

        using var http = factory.CreateClient();
        var options = factory.Services.GetRequiredService<IOptions<ConciergePersistenceOptions>>().Value;
        Assert.Equal(flushIntervalSeconds, options.FlushIntervalSeconds);
        Assert.Equal(flushBatchSize, options.FlushBatchSize);
    }

    [Fact]
    public async Task WebPersistenceConfigurationIsPassedToNewStayWorkflow()
    {
        await using var worker = await ConciergeTestHost.StartAsync(postgreSql);
        var today = ConciergeTestHost.HotelToday;
        var stayId = await worker.SeedStayAsync("Rivera", "412", today, today.AddDays(1));
        using var factory = CreateFactory(worker).WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Concierge:Persistence:FlushIntervalSeconds"] = "17",
                    ["Concierge:Persistence:FlushBatchSize"] = "3",
                    ["Concierge:Persistence:IdleRolloverSeconds"] = "120",
                })));
        using var http = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        using var verified = await http.PostAsJsonAsync(
            "/api/concierge/sessions", new ConciergeVerificationRequest("Rivera", "412"));
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
        var session = (await verified.Content.ReadFromJsonAsync<ConciergeSessionResponse>())!;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Credential);

        using var response = await http.PostAsJsonAsync(
            "/api/concierge/turns", new ConciergeTurnRequest("web-policy", "Breakfast?", "web"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var handle = worker.Environment.Client.GetWorkflowHandle($"concierge-stay-{stayId:N}");
        var history = await handle.FetchHistoryAsync();
        var input = await worker.Environment.Client.Options.DataConverter
            .ToSingleValueAsync<ConciergeStayWorkflowInput>(
                history.Events.First().WorkflowExecutionStartedEventAttributes.Input.Payloads_);

        Assert.Equal(stayId, input.StayId);
        Assert.Equal(17, input.TranscriptFlushIntervalSeconds);
        Assert.Equal(3, input.TranscriptFlushBatchSize);
        Assert.Equal(120, input.IdleRolloverSeconds);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(60, true)]
    [InlineData(86400, true)]
    [InlineData(59, false)]
    [InlineData(86401, false)]
    [InlineData(-1, false)]
    public void IdleRolloverIsOffOrBetweenAMinuteAndADay(int seconds, bool valid)
    {
        using var factory = new WebApplicationFactory<ConciergeService>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Concierge:Persistence:IdleRolloverSeconds"] = seconds.ToString() }));
        });

        if (!valid)
        {
            var failure = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
            Assert.Contains("IdleRolloverSeconds must be 0 (off) or between 60 and 86400", failure.Message);
            return;
        }

        using var http = factory.CreateClient();
        Assert.Equal(seconds, factory.Services.GetRequiredService<IOptions<ConciergePersistenceOptions>>().Value.IdleRolloverSeconds);
    }

    /// <summary>Worker results retain their deadline and authorize subsequent Web turns.</summary>
    [Fact]
    public async Task ProductionRegistrationReadsWorkerSessionAndTurnResults()
    {
        await using var worker = await ConciergeTestHost.StartAsync(postgreSql);
        var today = ConciergeTestHost.HotelToday;
        var stayId = await worker.SeedStayAsync("Rivera", "412", today, today.AddDays(1));
        using var factory = CreateFactory(worker);
        using var http = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });

        // No test converter or replacement ITemporalClient is installed in Web.
        Assert.NotSame(worker.Environment.Client, factory.Services.GetRequiredService<ITemporalClient>());
        var verified = await http.PostAsJsonAsync(
            "/api/concierge/sessions", new ConciergeVerificationRequest("Rivera", "412"));
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
        var session = (await verified.Content.ReadFromJsonAsync<ConciergeSessionResponse>())!;
        Assert.Equal(ConciergeTestHost.HotelMidnightUtc(today.AddDays(2)), session.ExpiresAtUtc);
        Assert.Equal(stayId, session.StayId);

        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Credential);
        var response = await http.PostAsJsonAsync(
            "/api/concierge/turns", new ConciergeTurnRequest("production", "Breakfast?", "web"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var turn = (await response.Content.ReadFromJsonAsync<ConciergeTurnResult>())!;
        Assert.Equal("production", turn.TurnId);
        Assert.Equal("Answer: Breakfast?", turn.AssistantMessage);
        Assert.Equal(0, turn.Sequence);
        Assert.Contains(worker.ChatClient.Requests[^1].Messages,
            message => message.Text.StartsWith("Verified guest context:", StringComparison.Ordinal)
                && message.Text.Contains("\"GivenName\":\"Test\"", StringComparison.Ordinal)
                && message.Text.Contains("\"FamilyName\":\"Rivera\"", StringComparison.Ordinal)
                && message.Text.Contains("\"RoomNumber\":\"412\"", StringComparison.Ordinal)
                && message.Text.Contains("\"Floor\":4", StringComparison.Ordinal));
    }

    /// <summary>Invalid identity has the exact requested guest-facing wording.</summary>
    [Fact]
    public async Task UnknownGuestSaysGuestNotFound()
    {
        await using var worker = await ConciergeTestHost.StartAsync(postgreSql);
        using var factory = CreateFactory(worker);
        using var http = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var response = await http.PostAsJsonAsync(
            "/api/concierge/sessions", new ConciergeVerificationRequest("Unknown", "412"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<ConciergeApiError>())!;
        Assert.Equal("guest_not_found", error.Code);
        Assert.Equal("Guest not found", error.Message);
    }

    /// <summary>
    /// Session operations run as Standalone Activities started by name, exactly as Web starts them,
    /// and malformed input fails fast as a non-retryable activity failure.
    /// </summary>
    [Fact]
    public async Task SessionCommandsRunAsStandaloneActivities()
    {
        await using var worker = await ConciergeTestHost.StartAsync(postgreSql);
        var today = ConciergeTestHost.HotelToday;
        var guestId = Guid.NewGuid();
        var stayId = await worker.SeedStayAsync("Rivera", "412", today, today.AddDays(1), guestId);
        var sessionId = Guid.NewGuid();
        var hash = SHA256.HashData(RandomNumberGenerator.GetBytes(32));
        var client = worker.Environment.Client;

        var verify = await client.StartActivityAsync(
            ConciergeActivityNames.VerifySession,
            [new VerifyConciergeSessionActivityRequest(sessionId, hash, "Rivera", "412")],
            SessionActivityOptions("verify"));
        var verified = await verify.GetResultAsync<ConciergeSessionContext?>();
        Assert.NotNull(verified);
        Assert.Equal(sessionId, verified.SessionId);
        Assert.Equal(stayId, verified.StayId);
        Assert.Equal(
            new ConciergeVerifiedGuest(stayId, "Test", "Rivera", "412", guestId, today.AddDays(1), today),
            verified.VerifiedGuest);
        Assert.Equal(ConciergeTestHost.HotelMidnightUtc(today.AddDays(2)), verified.EligibilityDeadlineUtc);
        Assert.Equal(ConciergeActivityNames.VerifySession, (await verify.DescribeAsync()).ActivityType);

        Assert.Equal(verified, await client.ExecuteActivityAsync<ConciergeSessionContext?>(
            ConciergeActivityNames.AuthenticateSession,
            [new AuthenticateConciergeSessionActivityRequest(hash)],
            SessionActivityOptions("authenticate")));
        await client.ExecuteActivityAsync(
            ConciergeActivityNames.InvalidateSession,
            [new InvalidateConciergeSessionActivityRequest(sessionId, stayId)],
            SessionActivityOptions("invalidate"));
        Assert.Null(await client.ExecuteActivityAsync<ConciergeSessionContext?>(
            ConciergeActivityNames.AuthenticateSession,
            [new AuthenticateConciergeSessionActivityRequest(hash)],
            SessionActivityOptions("authenticate")));

        var failed = await Assert.ThrowsAsync<ActivityFailedException>(() =>
            client.ExecuteActivityAsync<ConciergeSessionContext?>(
                ConciergeActivityNames.AuthenticateSession,
                [new AuthenticateConciergeSessionActivityRequest(new byte[31])],
                SessionActivityOptions("authenticate")));
        var cause = Assert.IsType<ApplicationFailureException>(failed.InnerException);
        Assert.Equal(ConciergeActivityNames.SessionInvalidRequestErrorType, cause.ErrorType);
        Assert.True(cause.NonRetryable);
    }

    private static StartActivityOptions SessionActivityOptions(string operation) =>
        new($"concierge-session-test-{operation}-{Guid.NewGuid():N}", ConciergeWorkflowNames.TaskQueue)
        {
            StartToCloseTimeout = TimeSpan.FromSeconds(20),
        };

    /// <summary>A model failure becomes the workflow's curated reply; neither logs nor the reply expose the cause.</summary>
    [Fact]
    public async Task ModelFailureReturnsCuratedReplyWithoutLoggingSensitiveValues()
    {
        await using var worker = await ConciergeTestHost.StartAsync(postgreSql);
        var today = ConciergeTestHost.HotelToday;
        await worker.SeedStayAsync("PrivateGuestName", "412", today, today.AddDays(1));
        var logs = new ServiceLogProvider();
        using var factory = CreateFactory(worker, logs);
        using var http = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var verified = await http.PostAsJsonAsync(
            "/api/concierge/sessions", new ConciergeVerificationRequest("PrivateGuestName", "412"));
        var session = (await verified.Content.ReadFromJsonAsync<ConciergeSessionResponse>())!;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Credential);
        worker.ChatClient.Fail = true;

        var response = await http.PostAsJsonAsync(
            "/api/concierge/turns", new ConciergeTurnRequest("private-turn", "PrivateGuestQuestion", "web"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEmpty(worker.ChatClient.Requests);
        var turn = (await response.Content.ReadFromJsonAsync<ConciergeTurnResult>())!;
        Assert.Equal(
            "I couldn't reach the concierge service just now. Please try again in a moment.",
            turn.AssistantMessage);
        Assert.All(logs.Exceptions, exception => Assert.Null(exception));
        Assert.All(
            new[] { "secret-internal-detail", "10.0.0.7", "PrivateGuestName", "PrivateGuestQuestion", session.Credential },
            value => Assert.All(logs.Entries, entry => Assert.DoesNotContain(value, entry, StringComparison.Ordinal)));
    }

    /// <summary>Tool failures retain internal diagnostics but return only the curated guest-safe result.</summary>
    [Fact]
    public async Task NonTimeoutToolActivityFailureReturnsSafeReplyAndLogsDiagnostic()
    {
        var logs = new ServiceLogProvider();
        await using var worker = await ConciergeTestHost.StartAsync(postgreSql, logProvider: logs);
        var today = ConciergeTestHost.HotelToday;
        var stayId = await worker.SeedStayAsync("PrivateGuestName", "412", today, today.AddDays(1));
        var handle = await worker.StartStayAsync(
            stayId,
            ConciergeTestHost.HotelMidnightUtc(today.AddDays(2)));
        const string expectedToolFailure =
            "I couldn't retrieve a reliable result from that tool. Please try again or clarify the request.";
        string? observedToolResult = null;
        worker.ActionTools.FailWeather = true;
        worker.ChatClient.ScriptedResponses.Enqueue(new ChatResponse(
            [
                new ChatMessage(
                    ChatRole.Assistant,
                    [
                        new FunctionCallContent(
                            "weather-failure",
                            ConciergeToolDefinitions.GetWeather,
                            new Dictionary<string, object?> { ["place"] = null, ["period"] = "today" }),
                    ]),
            ])
        {
            FinishReason = ChatFinishReason.ToolCalls,
        });
        worker.ChatClient.AnswerFactory = messages =>
        {
            var toolResult = messages.SelectMany(message => message.Contents)
                .OfType<FunctionResultContent>()
                .SingleOrDefault();
            if (toolResult is not null)
            {
                observedToolResult = toolResult.Result?.ToString();
            }

            return "I couldn't retrieve the weather just now.";
        };

        var result = await handle.ExecuteUpdateAsync(workflow => workflow.SubmitTurnAsync(
            new ConciergeTurnSubmission(
                "private-tool-turn",
                "PrivateGuestQuestion",
                "web",
                ConciergeTestHost.VerifiedGuest(stayId))));

        Assert.Equal("I couldn't retrieve the weather just now.", result.AssistantMessage);
        Assert.DoesNotContain("secret-internal-detail", result.AssistantMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("10.0.0.7", result.AssistantMessage, StringComparison.Ordinal);
        Assert.Equal(expectedToolFailure, observedToolResult);
        Assert.DoesNotContain("secret-internal-detail", observedToolResult, StringComparison.Ordinal);
        Assert.DoesNotContain("10.0.0.7", observedToolResult, StringComparison.Ordinal);
        Assert.Equal(ConciergeToolDefinitions.GetWeather, Assert.Single(worker.ActionTools.Calls).Name);

        var entry = Assert.Single(logs.Entries);
        Assert.Contains("GetWeather", entry, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", entry, StringComparison.Ordinal);
        Assert.Contains("secret-internal-detail", entry, StringComparison.Ordinal);
        Assert.Contains("10.0.0.7", entry, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(Assert.Single(logs.Exceptions));
    }

    /// <summary>Session activity failures are mapped and logged without exposing database diagnostics.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SessionFailureLogsSafeCauseAndReturnsGenericGuestError(bool verify)
    {
        await using var worker = await ConciergeTestHost.StartAsync(postgreSql);
        var today = ConciergeTestHost.HotelToday;
        await worker.SeedStayAsync("PrivateGuestName", "412", today, today.AddDays(1));
        var logs = new ServiceLogProvider();
        using var factory = CreateFactory(worker, logs);
        using var http = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var verified = await http.PostAsJsonAsync(
            "/api/concierge/sessions", new ConciergeVerificationRequest("PrivateGuestName", "412"));
        verified.EnsureSuccessStatusCode();
        var session = (await verified.Content.ReadFromJsonAsync<ConciergeSessionResponse>())!;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Credential);
        // Only the isolated test database is changed; subsequent session activities fail.
        await worker.ExecuteAsync("DROP TABLE concierge_sessions;");

        var response = verify
            ? await http.PostAsJsonAsync(
                "/api/concierge/sessions", new ConciergeVerificationRequest("PrivateGuestName", "412"))
            : await http.GetAsync("/api/concierge/history");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<ConciergeApiError>())!;
        Assert.Equal("unavailable", error.Code);
        var entry = Assert.Single(logs.Entries);
        Assert.All(logs.Exceptions, exception => Assert.Null(exception));
        Assert.Contains(verify ? "Verify" : "Authenticate", entry, StringComparison.Ordinal);
        Assert.Contains("ActivityFailedException", entry, StringComparison.Ordinal);
        Assert.Contains("PostgresException", entry, StringComparison.Ordinal);
        foreach (var value in new[] { "concierge_sessions", "PrivateGuestName", session.Credential })
        {
            Assert.DoesNotContain(value, entry, StringComparison.Ordinal);
            Assert.DoesNotContain(value, error.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>The stay's flush recovery logs safe diagnostics without losing completed replies.</summary>
    [Fact]
    public async Task StayFlushRecoveryLogDoesNotExposeDatabaseFailures()
    {
        var logs = new ServiceLogProvider();
        await using var worker = await ConciergeTestHost.StartAsync(
            postgreSql,
            flushIntervalSeconds: 600,
            logProvider: logs);
        var today = ConciergeTestHost.HotelToday;
        var stayId = await worker.SeedStayAsync("PrivateGuestName", "412", today, today.AddDays(1));
        var handle = await worker.StartStayAsync(stayId, ConciergeTestHost.HotelMidnightUtc(today.AddDays(2)));
        await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
            new ConciergeTurnSubmission(
                "private-turn-0", "PrivateGuestQuestion", "web", ConciergeTestHost.VerifiedGuest(stayId))));
        var answer = await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
            new ConciergeTurnSubmission(
                "private-last-turn", "PrivateGuestQuestion", "web", ConciergeTestHost.VerifiedGuest(stayId))));
        Assert.Equal("Answer: PrivateGuestQuestion", answer.AssistantMessage);
        await worker.ExecuteAsync("ALTER TABLE transcript_turns RENAME TO private_transcript_turns;");
        try
        {
            await Assert.ThrowsAsync<WorkflowUpdateFailedException>(() =>
                handle.ExecuteUpdateAsync(wf => wf.EndInteractionAsync()));
        }
        finally
        {
            await worker.ExecuteAsync("ALTER TABLE private_transcript_turns RENAME TO transcript_turns;");
        }

        await handle.ExecuteUpdateAsync(wf => wf.EndInteractionAsync());
        Assert.Equal(2, (await worker.ReadPersistedTurnIdsAsync(stayId)).Length);
        var entry = Assert.Single(logs.Entries);
        Assert.Contains("FlushPendingAsync", entry, StringComparison.Ordinal);
        Assert.Contains("PostgresException", entry, StringComparison.Ordinal);
        Assert.Contains("ActivityFailureException", entry, StringComparison.Ordinal);
        foreach (var value in new[]
            { "secret-internal-detail", "10.0.0.7", "PrivateGuestName", "PrivateGuestQuestion",
              "transcript_turns", "private-turn", stayId.ToString() })
        {
            Assert.DoesNotContain(value, entry, StringComparison.Ordinal);
        }
        Assert.All(logs.Exceptions, exception => Assert.Null(exception));

        var replayLogs = new ServiceLogProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(replayLogs));
        var replayer = new WorkflowReplayer(new WorkflowReplayerOptions
        {
            DataConverter = DurableAIDataConverter.Instance,
            LoggerFactory = loggerFactory,
        }.AddWorkflow<ConciergeStayWorkflow>());
        await replayer.ReplayWorkflowAsync(await handle.FetchHistoryAsync());
        Assert.Empty(replayLogs.Entries);
    }

    /// <summary>The HTTP fallback handlers redact unexpected failures from replacement service implementations.</summary>
    [Theory]
    [InlineData("/api/concierge/history", "ReadHistoryAsync")]
    [InlineData("/api/concierge/end", "EndAsync")]
    [InlineData("/api/concierge/reset", "ResetAsync")]
    public async Task EndpointFallbackLogsOnlyOperationAndCauseTypes(string path, string operation)
    {
        var logs = new ServiceLogProvider();
        using var factory = new WebApplicationFactory<ConciergeService>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                services.AddScoped<IConciergeService, FailingConciergeService>();
                services.AddLogging(logging => logging.AddProvider(logs));
            });
        });
        using var http = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "PrivateCredential");
        var response = path.EndsWith("/end", StringComparison.Ordinal) || path.EndsWith("/reset", StringComparison.Ordinal)
            ? await http.PostAsync(path, null)
            : await http.GetAsync(path);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var entry = Assert.Single(logs.Entries);
        Assert.Contains(operation, entry, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", entry, StringComparison.Ordinal);
        Assert.Contains("HttpRequestException", entry, StringComparison.Ordinal);
        Assert.All(logs.Exceptions, exception => Assert.Null(exception));
        foreach (var value in new[] { "secret-internal-detail", "10.0.0.7", "PrivateCredential" })
        {
            Assert.DoesNotContain(value, entry, StringComparison.Ordinal);
            Assert.DoesNotContain(value, body, StringComparison.Ordinal);
        }
    }

    private sealed class FailingConciergeService : IConciergeService
    {
        private static Exception Failure() => new InvalidOperationException(
            "secret-internal-detail PrivateCredential",
            new HttpRequestException("provider host 10.0.0.7"));

        /// <inheritdoc/>
        public Task<ConciergeSessionResponse> VerifyAsync(
            ConciergeVerificationRequest request, CancellationToken cancellationToken = default) => throw Failure();
        /// <inheritdoc/>
        public Task<ConciergeTurnResult> SubmitTurnAsync(
            string? bearerCredential, ConciergeTurnRequest request,
            CancellationToken cancellationToken = default) => throw Failure();
        /// <inheritdoc/>
        public Task<ConciergeHistoryResult> ReadHistoryAsync(
            string? bearerCredential, CancellationToken cancellationToken = default) => throw Failure();
        /// <inheritdoc/>
        public Task<ConciergeRideActivitySnapshot> ReadRideActivitySnapshotAsync(
            string? bearerCredential, CancellationToken cancellationToken = default) => throw Failure();
        /// <inheritdoc/>
        public IAsyncEnumerable<ConciergeRideActivityItem> StreamRideActivityAsync(
            string? bearerCredential, long fromOffset, CancellationToken cancellationToken = default) => throw Failure();
        /// <inheritdoc/>
        public Task<ConciergeRideReadState> MarkRideActivitySnapshotReadAsync(
            string? bearerCredential, long snapshotOffset, CancellationToken cancellationToken = default) => throw Failure();
        /// <inheritdoc/>
        public Task EndAsync(
            string? bearerCredential, CancellationToken cancellationToken = default) => throw Failure();
        /// <inheritdoc/>
        public Task ResetAsync(
            string? bearerCredential, CancellationToken cancellationToken = default) => throw Failure();
        /// <inheritdoc/>
        public Task<ConciergeVoiceAuthorization> AuthorizeVoiceAsync(
            string? bearerCredential, CancellationToken cancellationToken = default) => throw Failure();
        /// <inheritdoc/>
        public Task<ConciergeTurnResult> SubmitVoiceTurnAsync(
            ConciergeSessionContext session, string turnId, string userMessage,
            CancellationToken cancellationToken = default,
            bool enableEndChatFunction = false) => throw Failure();
        /// <inheritdoc/>
        public Task MarkVoiceTurnInterruptedAsync(
            ConciergeSessionContext session, string turnId,
            CancellationToken cancellationToken = default) => throw Failure();
    }

    private static WebApplicationFactory<ConciergeService> CreateFactory(
        ConciergeTestHost worker,
        ServiceLogProvider? logs = null) =>
        new WebApplicationFactory<ConciergeService>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                // Only redirect the production client to the test server; leave its converter untouched.
                services.PostConfigure<TemporalClientConnectOptions>(options =>
                {
                    options.TargetHost = worker.Environment.Client.Connection.Options.TargetHost;
                    options.Namespace = worker.Environment.Client.Options.Namespace;
                });
                if (logs is not null)
                {
                    services.AddLogging(logging => logging.AddProvider(logs));
                }
            });
        });

    private sealed class ServiceLogProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Entries { get; } = new();
        public ConcurrentQueue<Exception?> Exceptions { get; } = new();

        /// <inheritdoc/>
        public ILogger CreateLogger(string categoryName) =>
            new ServiceLogger(
                categoryName == typeof(ConciergeService).FullName
                || categoryName == typeof(IConciergeService).FullName
                || categoryName == "ConciergeActionToolActivity"
                || categoryName == $"Temporalio.Workflow:{ConciergeWorkflowNames.StayWorkflow}"
                    ? this : null);

        /// <inheritdoc/>
        public void Dispose() { }

        private sealed class ServiceLogger(ServiceLogProvider? provider) : ILogger
        {
            /// <inheritdoc/>
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            /// <inheritdoc/>
            public bool IsEnabled(LogLevel logLevel) => provider is not null && logLevel >= LogLevel.Warning;

            /// <inheritdoc/>
            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (provider is not null && IsEnabled(logLevel))
                {
                    // Passing a raw exception to logging would expose its message and stack.
                    provider.Exceptions.Enqueue(exception);
                    provider.Entries.Enqueue(formatter(state, exception) + (exception?.ToString() ?? ""));
                }
            }
        }
    }
}
