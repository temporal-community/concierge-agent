using System.Collections.Concurrent;
using System.Text.Json;
using Concierge.WorkflowServer;
using Concierge.WorkflowServer.AI;
using Concierge.WorkflowServer.Activities;
using Concierge.WorkflowServer.Data;
using Concierge.WorkflowServer.Location;
using Concierge.WorkflowServer.Setup;
using Concierge.WorkflowServer.Workflows;
using Concierge.Contracts;
using Concierge.Web.Agentic;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Concierge.Web.Endpoints;
using Concierge.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TemporalCommunity.Extensions.AI;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;
using Temporalio.Testing;
using Temporalio.Worker;

namespace Concierge.Core.Tests;

/// <summary>
/// PostgreSQL + local Temporal + the Web concierge API and WorkflowServer worker, wired
/// like Program.cs but with a scripted chat client and zero-vector embeddings.
/// </summary>
internal sealed class ConciergeTestHost : IAsyncDisposable
{
    private readonly PostgreSqlTestDatabase database;

    private ConciergeTestHost(
        PostgreSqlTestDatabase database,
        NpgsqlDataSource dataSource,
        WorkflowEnvironment environment,
        WebApplication app,
        ScriptedChatClient chatClient,
        ScriptedActionToolBackend actionTools,
        int flushIntervalSeconds,
        int flushBatchSize)
    {
        this.database = database;
        DataSource = dataSource;
        Environment = environment;
        App = app;
        ChatClient = chatClient;
        ActionTools = actionTools;
        FlushIntervalSeconds = flushIntervalSeconds;
        FlushBatchSize = flushBatchSize;
        Http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
    }

    public NpgsqlDataSource DataSource { get; }

    public WorkflowEnvironment Environment { get; }

    public WebApplication App { get; }

    public ScriptedChatClient ChatClient { get; }

    public ScriptedActionToolBackend ActionTools { get; }

    public HttpClient Http { get; }

    public int FlushIntervalSeconds { get; }

    public int FlushBatchSize { get; }

    public static DateOnly HotelToday =>
        DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(
                DateTimeOffset.UtcNow,
                HotelTimeZone)
            .DateTime);

    public static IOptions<HotelLocationOptions> HotelOptions { get; } =
        Options.Create(new HotelLocationOptions { TimeZoneId = "America/New_York" });

    private static readonly TimeZoneInfo HotelTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    /// <summary>UTC instant of Hotel ZGY local midnight starting <paramref name="date"/>.</summary>
    public static DateTimeOffset HotelMidnightUtc(DateOnly date) =>
        new(TimeZoneInfo.ConvertTimeToUtc(
            date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified),
            HotelTimeZone));

    public static async Task<ConciergeTestHost> StartAsync(
        PostgreSqlFixture postgreSql,
        int flushIntervalSeconds = 30,
        IReadOnlyList<string>? devServerArgs = null,
        int flushBatchSize = ConciergePersistenceOptions.DefaultFlushBatchSize,
        ILoggerProvider? logProvider = null,
        bool timeSkipping = false,
        Action<TemporalWorkerOptions>? configureWorker = null,
        Func<IServiceProvider, IChatClient, IChatClient>? wrapChat = null,
        bool streamAnswers = false,
        bool agenticDiagnostics = false)
    {
        var database = await postgreSql.CreateDatabaseAsync();
        var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await new DatabaseMigrator(dataSource, NullLogger<DatabaseMigrator>.Instance)
            .MigrateAsync();

        var environment = timeSkipping
            ? await WorkflowEnvironment.StartTimeSkippingAsync()
            : await WorkflowEnvironment.StartLocalAsync(new()
            {
                DevServerOptions = new() { ExtraArgs = devServerArgs?.ToList() },
            });
        environment.Client.Options.DataConverter = DurableAIDataConverter.Instance;

        var chatClient = new ScriptedChatClient();
        var actionTools = new ScriptedActionToolBackend();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        if (logProvider is not null)
        {
            builder.Logging.AddProvider(logProvider);
        }
        builder.Services.AddSingleton<ITemporalClient>(environment.Client);
        builder.Services.AddSingleton(dataSource);
        builder.Services.AddSingleton(HotelOptions);
        builder.Services.AddSingleton<ConciergeSessionService>();
        builder.Services.Configure<ConciergePersistenceOptions>(
            options =>
            {
                options.FlushIntervalSeconds = flushIntervalSeconds;
                options.FlushBatchSize = flushBatchSize;
            });
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<Concierge.Web.Voice.VoiceSessionRegistry>();
        builder.Services.AddSingleton<StayTranscriptNotifier>();
        builder.Services.AddAGUIServer();
        if (agenticDiagnostics)
        {
            // Program.cs registers this in Development only.
            builder.Services.AddSingleton<AgenticRunFailures>();
        }
        builder.Services.AddScoped<IConciergeService, ConciergeService>();
        IChatClient boundedChat = new BoundedChatClient(chatClient, TimeSpan.FromMinutes(2), maxConcurrency: 4);
        if (streamAnswers)
        {
            // ADR 0015: the production wiring, publisher and listener on this test database.
            builder.Services.AddSingleton<AnswerStreamPublisher>();
            builder.Services.AddHostedService(services => services.GetRequiredService<AnswerStreamPublisher>());
            builder.Services.AddSingleton(services => new AnswerStreamListener(
                database.ConnectionString, services.GetRequiredService<ILogger<AnswerStreamListener>>()));
            builder.Services.AddHostedService(services => services.GetRequiredService<AnswerStreamListener>());
        }

        builder.Services.AddSingleton<IChatClient>(services =>
        {
            var model = streamAnswers
                ? new AnswerStreamPublishingChatClient(boundedChat, services.GetRequiredService<AnswerStreamPublisher>())
                : boundedChat;
            return wrapChat?.Invoke(services, model) ?? model;
        });
        builder.Services.AddSingleton<IConciergeActionToolBackend>(actionTools);
        builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            new ZeroEmbeddingGenerator());
        builder.Services.AddSingleton<IOptions<EmbeddingProviderOptions>>(
            Options.Create(new EmbeddingProviderOptions
            {
                Provider = AIProviderNames.Ollama,
                Model = "test-embedding",
                Dimensions = EmbeddingModelIdentity.MigratedSchemaDimensions,
            }));
        builder.Services.AddSingleton<EmbeddingModelIdentityStore>();
        builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);
        builder.Services.AddHostedTemporalWorker(TemporalWorkerConnection.TaskQueue)
            .ConfigureOptions(options =>
            {
                options.Interceptors =
                    [.. options.Interceptors ?? [], new ConciergeTurnDeadlineInterceptor()];
                configureWorker?.Invoke(options);
            })
            .AddDurableAI(options => options.RegisterDefaultWorkflow = false)
            .AddDurableTools(ConciergeToolDefinitions.CreateActivityTools())
            .AddWorkflow<ConciergeStayWorkflow>()
            .AddScopedActivities<HotelKnowledgeActivities>()
            .AddScopedActivities<ConciergeTranscriptActivities>()
            .AddScopedActivities<ConciergeStayResetActivities>()
            .AddScopedActivities<ConciergeSessionActivities>();

        var app = builder.Build();
        app.MapConciergeApi();
        app.MapAGUIServer(AgenticConcierge.EndpointPath, AgenticConcierge.CreateAgent);
        await app.StartAsync();
        if (streamAnswers)
        {
            await app.Services.GetRequiredService<AnswerStreamListener>().Listening.WaitAsync(TimeSpan.FromSeconds(30));
        }
        return new ConciergeTestHost(
            database,
            dataSource,
            environment,
            app,
            chatClient,
            actionTools,
            flushIntervalSeconds,
            flushBatchSize);
    }

    public async Task<Guid> SeedStayAsync(
        string familyName,
        string roomNumber,
        DateOnly checkInDate,
        DateOnly checkOutDate,
        Guid? knownGuestId = null)
    {
        var guestId = knownGuestId ?? Guid.NewGuid();
        var stayId = Guid.NewGuid();
        await ExecuteAsync(
            "INSERT INTO guests (guest_id, given_name, family_name) VALUES ($1, 'Test', $2);",
            guestId,
            familyName);
        await ExecuteAsync(
            """
            INSERT INTO stays (
                stay_id, external_stay_key, guest_id, room_number,
                check_in_utc, check_out_utc, eligibility_ends_at_utc)
            VALUES ($1, $2, $3, $4, $5, $6, $7);
            """,
            stayId,
            $"stay-{stayId:N}",
            guestId,
            roomNumber,
            HotelMidnightUtc(checkInDate),
            HotelMidnightUtc(checkOutDate),
            HotelMidnightUtc(checkOutDate.AddDays(1)));
        return stayId;
    }

    /// <summary>The verified guest profile the Web attaches to every turn for <paramref name="stayId"/>.</summary>
    public static ConciergeVerifiedGuest VerifiedGuest(
        Guid stayId,
        Guid? guestId = null,
        DateOnly? checkoutDate = null) =>
        new(
            stayId,
            "Test",
            "Guest",
            "101",
            guestId ?? new Guid(stayId.ToByteArray().Select(value => (byte)(value ^ 0x5A)).ToArray()),
            checkoutDate ?? HotelToday.AddDays(1));

    /// <summary>Starts the stay workflow exactly as the API does for a verified session.</summary>
    public async Task<WorkflowHandle<ConciergeStayWorkflow>> StartStayAsync(
        Guid stayId,
        DateTimeOffset deadline,
        int idleRolloverSeconds = 0)
    {
        var workflowId = $"concierge-stay-{stayId:N}";
        var input = new ConciergeStayWorkflowInput(
            stayId, deadline, FlushIntervalSeconds, 0, null, FlushBatchSize,
            IdleRolloverSeconds: idleRolloverSeconds);
        await Environment.Client.StartWorkflowAsync(
            (ConciergeStayWorkflow workflow) => workflow.RunAsync(input),
            new WorkflowOptions(workflowId, TemporalWorkerConnection.TaskQueue)
            {
                IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting,
            });

        // A handle without a run ID always targets the latest run after Continue-As-New.
        return Environment.Client.GetWorkflowHandle<ConciergeStayWorkflow>(workflowId);
    }

    public async Task<string[]> ReadPersistedTurnIdsAsync(Guid stayId)
    {
        await using var command = DataSource.CreateCommand(
            "SELECT turn_id FROM transcript_turns WHERE stay_id = $1 ORDER BY turn_sequence;");
        command.Parameters.AddWithValue(stayId);
        await using var reader = await command.ExecuteReaderAsync();
        var turnIds = new List<string>();
        while (await reader.ReadAsync())
        {
            turnIds.Add(reader.GetString(0));
        }

        return [.. turnIds];
    }

    public async Task ExecuteAsync(string sql, params object[] parameters)
    {
        await using var command = DataSource.CreateCommand(sql);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter);
        }

        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
        await Environment.ShutdownAsync();
        await DataSource.DisposeAsync();
        await database.DisposeAsync();
    }
}

/// <summary>Deterministic chat client. Answers "Answer: {last guest message}".</summary>
internal sealed class ScriptedChatClient : IChatClient
{
    private readonly ConcurrentQueue<RecordedChatRequest> requests = new();
    private readonly TaskCompletionSource firstCall = NewSource();

    public IReadOnlyList<RecordedChatRequest> Requests => [.. requests];

    /// <summary>When set, every model call waits for it to complete.</summary>
    public TaskCompletionSource? Gate { get; set; }

    /// <summary>When set, every model call throws this internal-looking failure.</summary>
    public bool Fail { get; set; }

    /// <summary>Returns a model-generated end_chat call for a guest answer when enabled.</summary>
    public bool ReturnEndChatFunctionCall { get; set; }

    public ConcurrentQueue<ChatResponse> ScriptedResponses { get; } = new();

    /// <summary>Overrides the deterministic answer text for isolation tests.</summary>
    public Func<IReadOnlyList<ChatMessage>, string>? AnswerFactory { get; set; }

    public Func<IReadOnlyList<ChatMessage>, ChatResponse>? ResponseFactory { get; set; }

    public Func<IReadOnlyList<ChatMessage>, Task>? BeforeResponse { get; set; }

    /// <summary>When set, answers report this finish reason (for example, Length).</summary>
    public ChatFinishReason? AnswerFinishReason { get; set; }

    public Task WaitForCallAsync() => firstCall.Task;

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = messages.ToArray();
        requests.Enqueue(new RecordedChatRequest(
            snapshot,
            options?.MaxOutputTokens,
            options?.Tools?.OfType<AIFunction>().Select(tool => tool.Name).ToArray() ?? [],
            options?.AdditionalProperties?.ContainsKey(ConciergeEndChat.ModelOptionKey) == true,
            options?.AdditionalProperties?.ContainsKey(ConciergeToolDefinitions.ModelOptionKey) == true,
            options?.AllowMultipleToolCalls));
        firstCall.TrySetResult();
        if (BeforeResponse is { } beforeResponse)
        {
            await beforeResponse(snapshot);
        }
        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        if (Fail)
        {
            throw new InvalidOperationException("secret-internal-detail: provider host 10.0.0.7");
        }

        var text = AnswerFactory?.Invoke(snapshot)
            ?? $"Answer: {snapshot.Last(message => message.Role == ChatRole.User).Text}";
        if (ResponseFactory is { } responseFactory)
        {
            return responseFactory(snapshot);
        }
        if (ReturnEndChatFunctionCall)
        {
            return new ChatResponse(
                [
                    new ChatMessage(ChatRole.Assistant,
                    [
                        new FunctionCallContent(
                            "call_end_chat_test",
                            ConciergeEndChat.FunctionName,
                            new Dictionary<string, object?>()),
                    ]),
                ])
            {
                FinishReason = ChatFinishReason.ToolCalls,
            };
        }

        if (ScriptedResponses.TryDequeue(out var scripted))
        {
            return scripted;
        }

        return new ChatResponse([new ChatMessage(ChatRole.Assistant, text)])
        {
            FinishReason = AnswerFinishReason ?? ChatFinishReason.Stop,
        };
    }

    /// <summary>
    /// When set, streamed answers arrive word by word with this delay between words, the way a
    /// provider streams; otherwise each response is one update.
    /// </summary>
    public TimeSpan? StreamWordDelay { get; set; }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
        {
            if (StreamWordDelay is not { } delay || update.Contents is not [TextContent { Text.Length: > 0 } text])
            {
                yield return update;
                continue;
            }

            foreach (var word in System.Text.RegularExpressions.Regex.Split(text.Text, @"(?<= )"))
            {
                await Task.Delay(delay, cancellationToken);
                yield return new ChatResponseUpdate(update.Role, word)
                {
                    MessageId = update.MessageId,
                    ResponseId = update.ResponseId,
                    FinishReason = update.FinishReason,
                };
            }
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    private static TaskCompletionSource NewSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed record RecordedChatRequest(
    IReadOnlyList<ChatMessage> Messages,
    int? MaxOutputTokens,
    IReadOnlyList<string> Tools,
    bool HasEndChatMarker,
    bool HasActionToolsMarker,
    bool? AllowMultipleToolCalls)
{
    public string LastUserText => Messages.Last(message => message.Role == ChatRole.User).Text;

    public IEnumerable<string> UserTexts =>
        Messages.Where(message => message.Role == ChatRole.User).Select(message => message.Text);
}

internal sealed record ScriptedActionToolCall(
    string Name,
    IReadOnlyDictionary<string, object?> Arguments,
    string? GuestId,
    string? StayId,
    string? CheckoutDate);

internal sealed class ScriptedActionToolBackend : IConciergeActionToolBackend
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> responses = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<ScriptedActionToolCall> calls = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public IReadOnlyList<ScriptedActionToolCall> Calls => [.. calls];

    public Func<string, Task>? BeforeResult { get; set; }

    public void QueueResult(string name, string result) =>
        responses.GetOrAdd(name, _ => new ConcurrentQueue<string>()).Enqueue(result);

    public async Task<string> GetWeatherAsync(string? place, string period, DateTimeOffset? asOfUtc, CancellationToken cancellationToken)
    {
        Record(ConciergeToolDefinitions.GetWeather, new Dictionary<string, object?> { ["place"] = place, ["period"] = period });
        WeatherAnchors.Enqueue(asOfUtc);
        if (BeforeResult is { } beforeResult) await beforeResult(ConciergeToolDefinitions.GetWeather);
        if (FailWeather)
        {
            throw new InvalidOperationException("secret-internal-detail: provider host 10.0.0.7");
        }

        if (WeatherFailures.TryDequeue(out var failure))
        {
            throw failure;
        }

        if (WeatherGate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        return Dequeue(ConciergeToolDefinitions.GetWeather, new ConciergeWeatherToolResult(
            "Available", null, [], place ?? "Hotel ZGY, Miami", new DateOnly(2026, 10, 20),
            "partly cloudy", 82, 70, 8, 10));
    }

    public TaskCompletionSource? WeatherGate { get; set; }

    public ConcurrentQueue<DateTimeOffset?> WeatherAnchors { get; } = new();

    public bool FailWeather { get; set; }

    /// <summary>Exceptions thrown by successive weather attempts before the scripted result.</summary>
    public ConcurrentQueue<Exception> WeatherFailures { get; } = new();

    public Task<string> GetRouteAsync(string[] places, CancellationToken cancellationToken)
    {
        Record(ConciergeToolDefinitions.GetRoute, new Dictionary<string, object?> { ["places"] = places });
        return Task.FromResult(Dequeue(ConciergeToolDefinitions.GetRoute, new ConciergeRouteToolResult(
            "Available", null, [], 5000, 720, "About 3.1 mi; around 12 minutes by car.",
            "3.1 mi · about 12 min by car.", ["Head north.", "Turn right."],
            ["Head north for two blocks.", "Turn right onto the avenue"], null, null)));
    }

    public Task<string> FindNearbyPlacesAsync(string category, CancellationToken cancellationToken)
    {
        Record(ConciergeToolDefinitions.FindNearbyPlaces, new Dictionary<string, object?> { ["category"] = category });
        return Task.FromResult(Dequeue(ConciergeToolDefinitions.FindNearbyPlaces, new ConciergeNearbyPlacesToolResult(
            "Available", category,
            [new ConciergeNearbyPlace("Scripted Pharmacy", "100 Main Street", 0.4, "scripted-pharmacy", 25.799, -80.198)])));
    }

    public Task<string> GetPlaceOpeningHoursAsync(string placeName, CancellationToken cancellationToken)
    {
        Record(ConciergeToolDefinitions.GetPlaceOpeningHours, new Dictionary<string, object?> { ["placeName"] = placeName });
        return Task.FromResult(Dequeue(ConciergeToolDefinitions.GetPlaceOpeningHours,
            new ConciergePlaceOpeningHoursToolResult(
                "Available", placeName, "17:30-21:30; Th 17:30-22:00; Fr,Sa 17:30-22:30", [], "Eastern Time")));
    }

    public async Task<string> ProposeRideAsync(
        string? pickup,
        string dropoff,
        bool immediate,
        string? pickupAt,
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        Record(ConciergeToolDefinitions.ProposeRide, new Dictionary<string, object?>(arguments)
        {
            ["pickup"] = pickup,
            ["dropoff"] = dropoff,
            ["immediate"] = immediate,
            ["pickupAt"] = pickupAt,
        });
        if (BeforeResult is { } beforeResult) await beforeResult(ConciergeToolDefinitions.ProposeRide);
        return Dequeue(ConciergeToolDefinitions.ProposeRide, new ConciergeRideProposalToolResult(
            "Available", null, [],
            new ConciergeRideProposalDraft(
                new ConciergeRidePlace(pickup ?? "Hotel ZGY", 25.7984634, -80.1956016),
                new ConciergeRidePlace(
                    $"{dropoff}, Miami Beach, Florida, United States of America",
                    25.7745,
                    -80.1329),
                immediate,
                immediate ? null : DateTimeOffset.Parse(pickupAt!),
                720),
            "About 3.1 mi; around 12 minutes by car.", null, ["Head north."]));
    }

    public ConcurrentQueue<(Guid GuestId, Guid StayId, DateOnly CheckoutDate)> RidePurges { get; } = new();

    /// <summary>Arranged failures for successive purge attempts before the purge succeeds.</summary>
    public ConcurrentQueue<Exception> RidePurgeFailures { get; } = new();

    public Task PurgeStayRidesAsync(Guid guestId, Guid stayId, DateOnly checkoutDate, CancellationToken cancellationToken)
    {
        if (RidePurgeFailures.TryDequeue(out var failure))
        {
            throw failure;
        }

        RidePurges.Enqueue((guestId, stayId, checkoutDate));
        return Task.CompletedTask;
    }

    public async Task<string> ConfirmRideAsync(
        string proposalId,
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        Record(ConciergeToolDefinitions.ConfirmRide, arguments);
        if (BeforeResult is { } beforeResult) await beforeResult(ConciergeToolDefinitions.ConfirmRide);
        return Dequeue(ConciergeToolDefinitions.ConfirmRide, new ConciergeRideActionResult(
            true,
            null,
            new RideSummary(Guid.Parse(proposalId), "Booked", "Hotel ZGY", "South Beach, Miami Beach, Florida, United States of America",
                DateTimeOffset.Parse("2026-10-20T12:00:00-04:00"), null, null, null),
            []));
    }

    public Task<string> GetRideStatusAsync(
        string rideId,
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        Record(ConciergeToolDefinitions.GetRideStatus, arguments);
        return Task.FromResult(Dequeue(ConciergeToolDefinitions.GetRideStatus, new ConciergeRideActionResult(
            true, null,
            new RideSummary(Guid.Parse(rideId), "DriverAssigned", "Hotel ZGY", "South Beach, Miami Beach, Florida, United States of America",
                DateTimeOffset.Parse("2026-10-20T12:00:00-04:00"), "Jordan", "Blue sedan", "TEST"),
            [])));
    }

    public Task<string> GetRideHistoryAsync(
        string when,
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        Record(ConciergeToolDefinitions.GetRideHistory, arguments);
        return Task.FromResult(Dequeue(ConciergeToolDefinitions.GetRideHistory, new ConciergeRideActionResult(
            true, null, null, [])));
    }

    public async Task<string> CancelRideAsync(
        string rideId,
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        // ConfirmRideCancellation is the only workflow tool that reaches the backend cancel call.
        Record(ConciergeToolDefinitions.ConfirmRideCancellation, arguments);
        if (BeforeResult is { } beforeResult) await beforeResult(ConciergeToolDefinitions.ConfirmRideCancellation);
        return Dequeue(ConciergeToolDefinitions.ConfirmRideCancellation, new ConciergeRideActionResult(
            true, "Your ride is canceled.", null, [], Guid.Parse(rideId)));
    }

    private string Dequeue<T>(string name, T fallback) =>
        responses.TryGetValue(name, out var queue) && queue.TryDequeue(out var result)
            ? result
            : JsonSerializer.Serialize(fallback, JsonOptions);

    private void Record(string name, IEnumerable<KeyValuePair<string, object?>> arguments)
    {
        var dictionary = arguments.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        calls.Enqueue(new ScriptedActionToolCall(
            name,
            dictionary,
            Read(ConciergeToolDefinitions.TrustedGuestIdKey),
            Read(ConciergeToolDefinitions.TrustedStayIdKey),
            Read(ConciergeToolDefinitions.TrustedCheckoutDateKey)));

        string? Read(string key) =>
            dictionary.TryGetValue(key, out var value)
                ? value switch
                {
                    string text => text,
                    JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                    _ => null,
                }
                : null;
    }

}

internal sealed class ZeroEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(
            new GeneratedEmbeddings<Embedding<float>>(
                values.Select(_ => new Embedding<float>(
                    new float[EmbeddingModelIdentity.MigratedSchemaDimensions]))));

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
