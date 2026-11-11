#:project ../../src/Concierge.WorkflowServer/Concierge.WorkflowServer.csproj
#:project ../../src/Concierge.RideService/Concierge.RideService.csproj
#:project ../../src/Concierge.Web/Concierge.Web.csproj
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property PublishAot=false
#:property AssemblyName=Concierge.Core.Tests

// Opt-in real-model replay. A fresh Temporal namespace isolates current workers from old histories.
// Uses existing Aspire credentials only in memory; adds fictional guest/ride rows, never deletes data.
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Concierge.Contracts;
using Concierge.RideService;
using Concierge.RideService.Data;
using Concierge.RideService.Workflows;
using Concierge.Web.Components.Shared;
using Concierge.Web.Endpoints;
using Concierge.Web.Services;
using Concierge.Web.Voice;
using Concierge.WorkflowServer.Activities;
using Concierge.WorkflowServer.AI;
using Concierge.WorkflowServer.Data;
using Concierge.WorkflowServer.Location;
using Concierge.WorkflowServer.Setup;
using Concierge.WorkflowServer.Workflows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using TemporalCommunity.Extensions.AI;
using Temporalio.Api.WorkflowService.V1;
using Temporalio.Client;
using Temporalio.Converters;
using Temporalio.Extensions.Hosting;
using Temporalio.Extensions.WorkflowStreams;

if (args.Length < 1)
{
    Console.Error.WriteLine(
        "Usage: dotnet run tests/Concierge.LiveChecks/revised-voice-booking.cs -- <artifact-directory> [--full-tail] [--source-context <file>]");
    return 2;
}
var artifacts = Path.GetFullPath(args[0]);
var fullTail = false;
string? sourceContextPath = null;
for (var index = 1; index < args.Length; index++)
{
    switch (args[index])
    {
        case "--full-tail" when !fullTail:
            fullTail = true;
            break;
        case "--source-context" when sourceContextPath is null
            && index + 1 < args.Length
            && !args[index + 1].StartsWith("--", StringComparison.Ordinal):
            sourceContextPath = Path.GetFullPath(args[++index]);
            break;
        default:
            Console.Error.WriteLine(
                "Usage: dotnet run tests/Concierge.LiveChecks/revised-voice-booking.cs -- <artifact-directory> [--full-tail] [--source-context <file>]");
            return 2;
    }
}
Directory.CreateDirectory(artifacts);
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
var report = new List<object>();
async Task Save(string name, object value) =>
    await File.WriteAllTextAsync(Path.Combine(artifacts, name), JsonSerializer.Serialize(value, json));
var stage = "reading runtime configuration";
WebApplication? web = null;
WebApplication? rides = null;
ITemporalClient? client = null;
var temporaryWorkflows = new HashSet<string>();
try
{
    var psi = new ProcessStartInfo("aspire")
    {
        RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
    };
    foreach (var arg in new[] { "describe", "--format", "Json", "--non-interactive", "--nologo" })
        psi.ArgumentList.Add(arg);
    using var process = Process.Start(psi)!;
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    var metadata = await stdout;
    _ = await stderr;
    Require(process.ExitCode == 0, "Aspire metadata unavailable; leave AppHost running.");
    using var doc = JsonDocument.Parse(metadata);
    var resources = doc.RootElement.GetProperty("resources").EnumerateArray().ToArray();
    JsonElement Resource(string prefix) => resources.Single(r =>
        r.GetProperty("name").GetString()!.StartsWith(prefix + "-", StringComparison.Ordinal));
    var env = Resource("workflow-server").GetProperty("environment");
    string Value(JsonElement source, string key) => source.GetProperty(key).GetString()!;
    var config = env.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String)
        .ToDictionary(p => p.Name.Replace("__", ":"), p => p.Value.GetString());
    // Aspire describe redacts this parameter as an empty string. Read only that configured
    // parameter from the AppHost's local secrets provider; never print or persist it.
    if (string.IsNullOrWhiteSpace(config.GetValueOrDefault("Geoapify:ApiKey")))
    {
        var project = System.Xml.Linq.XDocument.Load("src/Concierge.AppHost/Concierge.AppHost.csproj");
        var secretId = project.Descendants("UserSecretsId").Single().Value;
        var secrets = new ConfigurationBuilder().AddUserSecrets(secretId).Build();
        config["Geoapify:ApiKey"] = secrets["Parameters:geoapify-api-key"];
    }
    Require(!string.IsNullOrWhiteSpace(config.GetValueOrDefault("Geoapify:ApiKey")),
        "Configured Geoapify parameter unavailable; no live proposal can run.");
    var address = Value(env, "TEMPORAL_ADDRESS");
    var originalNamespace = Value(env, "TEMPORAL_NAMESPACE");
    using var conciergeDb = NpgsqlDataSource.Create(Value(env, "ConnectionStrings__concierge"));
    using var ridesDb = NpgsqlDataSource.Create(
        Value(Resource("ride-service").GetProperty("environment"), "ConnectionStrings__rides"));
    var sourceClient = await TemporalClient.ConnectAsync(new TemporalClientConnectOptions(address)
    {
        Namespace = originalNamespace, DataConverter = DurableAIDataConverter.Instance,
    });
    const string originalId = "concierge-stay-10e544bbdeb353d49295f90b1787c7b3";
    stage = "capturing original read-only evidence";
    var converter = DurableAIDataConverter.Instance;
    var submissions = new List<ConciergeTurnSubmission>();
    var replies = new List<ConciergeTurnResult>();
    var priorFromContext = Array.Empty<ConciergeTranscriptTurn>();
    ConciergeRideProposal? originalProposal = null;
    if (sourceContextPath is not null)
    {
        using var source = JsonDocument.Parse(await File.ReadAllTextAsync(sourceContextPath));
        var root = source.RootElement;
        submissions.AddRange(root.GetProperty("captured").Deserialize<ConciergeTurnSubmission[]>(json)
            ?? throw new InvalidOperationException("The source context has no captured turns."));
        replies.AddRange(root.GetProperty("replies").Deserialize<ConciergeTurnResult[]>(json)
            ?? throw new InvalidOperationException("The source context has no captured replies."));
        priorFromContext = root.GetProperty("prior").Deserialize<ConciergeTranscriptTurn[]>(json)
            ?? throw new InvalidOperationException("The source context has no prior transcript.");
        originalProposal = root.GetProperty("originalProposal").Deserialize<ConciergeRideProposal>(json)
            ?? throw new InvalidOperationException("The source context has no trusted ride proposal.");
    }
    else
    {
        var old = await sourceClient.GetWorkflowHandle(originalId, "59e6a9b0-c916-4d12-b806-69e931c5b964")
            .FetchHistoryAsync();
        var latest = await sourceClient.GetWorkflowHandle(originalId).FetchHistoryAsync();
        foreach (var e in old.Events.Concat(latest.Events))
        {
            var input = e.WorkflowExecutionUpdateAcceptedEventAttributes?.AcceptedRequest?.Input?.Args;
            if (input?.Payloads_.Count > 0)
            {
                var item = await converter.ToSingleValueAsync<JsonElement>(input.Payloads_);
                if (item.TryGetProperty("userMessage", out _))
                    submissions.Add(item.Deserialize<ConciergeTurnSubmission>(json)!);
            }
            var output = e.WorkflowExecutionUpdateCompletedEventAttributes?.Outcome?.Success;
            if (output?.Payloads_.Count > 0)
            {
                var item = await converter.ToSingleValueAsync<JsonElement>(output.Payloads_);
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("assistantMessage", out _))
                    replies.Add(item.Deserialize<ConciergeTurnResult>(json)!);
            }
        }

        foreach (var e in old.Events.Where(e => e.ActivityTaskScheduledEventAttributes?.ActivityType.Name
            == "TemporalCommunity.Extensions.AI.InvokeFunction"))
        {
            var item = await converter.ToSingleValueAsync<JsonElement>(e.ActivityTaskScheduledEventAttributes.Input.Payloads_);
            if (item.GetProperty("functionName").GetString() == "ConfirmRide"
                && item.GetProperty("arguments").TryGetProperty(ConciergeToolDefinitions.TrustedProposalKey, out var prop)
                && e.EventId > 1999 && e.EventId < 2024)
                originalProposal = JsonSerializer.Deserialize<ConciergeRideProposal>(prop.GetString()!, json);
        }
    }
    const string prefix = "v:6557d53079fd4a868808bf7f860c8135:1:";
    var captured = submissions.Where(s => s.TurnId.StartsWith(prefix)).DistinctBy(s => s.TurnId).ToArray();
    Require(originalProposal is not null, "Original trusted offer was not captured.");
    var first = captured.Single(s => s.TurnId.StartsWith(prefix + "11:") && s.UserMessage == "Okay. Can you book the ride?");
    var revision = captured.Single(s => s.TurnId.StartsWith(prefix + "11:") && s != first);
    var scheduled = captured.Single(s => s.TurnId.StartsWith(prefix + "12:"));
    var followup = captured.Single(s => s.TurnId.StartsWith(prefix + "13:"));
    var firstResult = replies.First(r => r.TurnId == first.TurnId);
    var prior = priorFromContext.Length > 0
        ? priorFromContext
        : replies.Where(r => r.Sequence < firstResult.Sequence)
            .DistinctBy(r => r.TurnId).OrderBy(r => r.Sequence).TakeLast(8)
            .Select(r =>
            {
                var s = submissions.First(s => s.TurnId == r.TurnId);
                return new ConciergeTranscriptTurn(r.TurnId, r.Sequence, s.Channel, s.UserMessage,
                    r.AssistantMessage, DateTimeOffset.UtcNow.AddMinutes(-5));
            }).ToArray();
    await Save("original-context.json", new
    {
        originalId, originalRun = "59e6a9b0-c916-4d12-b806-69e931c5b964",
        captured, replies = replies.Where(r => captured.Any(s => s.TurnId == r.TurnId)),
        prior, originalProposal,
    });
    stage = "creating isolated namespace";
    var ns = "voice-revision-live-" + Guid.NewGuid().ToString("N");
    await sourceClient.WorkflowService.RegisterNamespaceAsync(new RegisterNamespaceRequest
    {
        Namespace = ns,
        WorkflowExecutionRetentionPeriod = Google.Protobuf.WellKnownTypes.Duration.FromTimeSpan(TimeSpan.FromDays(1)),
    });
    client = await TemporalClient.ConnectAsync(new TemporalClientConnectOptions(address)
    {
        Namespace = ns, DataConverter = converter,
    });
    Console.WriteLine($"NAMESPACE {ns}");
    Console.WriteLine($"MODEL {config.GetValueOrDefault("AI:Chat:Provider")} / {config.GetValueOrDefault("AI:Chat:Model")}");
    await Save("run.json", new
    {
        ns, address, provider = config.GetValueOrDefault("AI:Chat:Provider"),
        model = config.GetValueOrDefault("AI:Chat:Model"), start = DateTimeOffset.UtcNow,
        fullTail, azureTenant = Environment.GetEnvironmentVariable("AZURE_TENANT_ID"),
        command = "dotnet run tests/Concierge.LiveChecks/revised-voice-booking.cs -- <artifact-directory>",
        isolation = "New namespace, current source workers and real HTTP endpoints, existing model and databases",
    });

    var hotelZone = TimeZoneInfo.FindSystemTimeZoneById(Value(env, "Hotel__TimeZoneId"));
    stage = "starting isolated RideService";
    var rideBuilder = WebApplication.CreateBuilder();
    rideBuilder.WebHost.UseUrls("http://127.0.0.1:0");
    rideBuilder.Logging.ClearProviders();
    rideBuilder.Services.AddSingleton<ITemporalClient>(client);
    rideBuilder.Services.AddSingleton(ridesDb);
    rideBuilder.Services.AddSingleton(TimeProvider.System);
    rideBuilder.Services.AddSingleton(hotelZone);
    rideBuilder.Services.Configure<RideSchedulingOptions>(rideBuilder.Configuration.GetSection(RideSchedulingOptions.SectionName));
    rideBuilder.Services.AddSingleton<RideRepository>();
    rideBuilder.Services.AddSingleton<RideBookingService>();
    rideBuilder.Services.AddHostedTemporalWorker(RideWorkflowNames.TaskQueue)
        .AddWorkflow<RideBookingWorkflow>().AddScopedActivities<RideBookingActivities>()
        .AddScopedActivities<RideStreamPublishingActivities>();
    rides = rideBuilder.Build();
    rides.MapRideEndpoints();
    await rides.StartAsync();

    stage = "building isolated Web and WorkflowServer";
    var builder = WebApplication.CreateBuilder();
    builder.Configuration.AddInMemoryCollection(config);
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Logging.ClearProviders();
    builder.Logging.AddSimpleConsole();
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
    builder.Services.AddSingleton<ITemporalClient>(client);
    builder.Services.AddSingleton(conciergeDb);
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddConciergeAIProviders(builder.Configuration);
    builder.Services.Configure<HotelLocationOptions>(builder.Configuration.GetSection("Hotel"));
    builder.Services.Configure<GeoapifyOptions>(builder.Configuration.GetSection("Geoapify"));
    builder.Services.Configure<RideSchedulingOptions>(builder.Configuration.GetSection(RideSchedulingOptions.SectionName));
    builder.Services.Configure<ConciergePersistenceOptions>(o => { o.FlushIntervalSeconds = 60; o.FlushBatchSize = 1; });
    builder.Services.AddHotelLocation();
    builder.Services.AddSingleton<GeoapifyPlaceResolver>();
    builder.Services.AddSingleton<OpenMeteoWeatherService>();
    builder.Services.AddSingleton<GeoapifyDrivingRouteService>();
    builder.Services.AddSingleton<GeoapifyNearbyPlacesService>();
    builder.Services.AddSingleton<GeoapifyPlaceOpeningHoursService>();
    ConciergeActionToolService.AddRideServiceClient(builder.Services);
    builder.Services.AddHttpClient(ConciergeActionToolService.RideClientName,
        c => c.BaseAddress = new Uri(rides.Urls.First()));
    builder.Services.AddScoped<IConciergeActionToolBackend, ConciergeActionToolService>();
    builder.Services.AddSingleton<EmbeddingModelIdentityStore>();
    builder.Services.AddSingleton<ConciergeSessionService>();
    builder.Services.AddSingleton<VoiceSessionRegistry>();
    builder.Services.AddSingleton<StayTranscriptNotifier>();
    builder.Services.AddScoped<IConciergeService, ConciergeService>();
    builder.Services.AddHostedTemporalWorker(TemporalWorkerConnection.TaskQueue)
        .ConfigureOptions(o => o.Interceptors = [new ConciergeTurnDeadlineInterceptor()])
        .AddDurableAI(o =>
        {
            o.RegisterDefaultWorkflow = false;
            o.ActivityTimeout = TimeSpan.FromSeconds(20);
            o.RetryPolicy = new() { MaximumAttempts = 3, InitialInterval = TimeSpan.FromSeconds(1), MaximumInterval = TimeSpan.FromSeconds(2) };
        })
        .AddDurableTools(ConciergeToolDefinitions.CreateActivityTools())
        .AddWorkflow<ConciergeStayWorkflow>()
        .AddScopedActivities<HotelKnowledgeActivities>()
        .AddScopedActivities<ConciergeTranscriptActivities>()
        .AddScopedActivities<ConciergeSessionActivities>();
    web = builder.Build();
    web.MapConciergeApi();
    // This replay begins with the route-reviewed historical offer, not a new address lookup.
    // Keep those captured coordinates rather than making unrelated startup geocoding a gate.
    web.Services.GetRequiredService<HotelLocation>().Initialize(new HotelCoordinates(
        originalProposal!.Pickup.Latitude, originalProposal.Pickup.Longitude));
    stage = "starting isolated Web and WorkflowServer";
    await web.StartAsync();

    // Bounded attempts: model-selected ride action on the revision is required to exercise the guard.
    var guardVerified = false;
    for (var attempt = 1; attempt <= 3 && !guardVerified; attempt++)
    {
        stage = $"live replay {attempt}";
        var stayId = Guid.NewGuid();
        var guestId = Guid.NewGuid();
        var family = "Live" + Guid.NewGuid().ToString("N")[..10];
        var room = Random.Shared.Next(9000, 10000).ToString();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, hotelZone).DateTime);
        DateTimeOffset Midnight(DateOnly date) => new(TimeZoneInfo.ConvertTimeToUtc(
            date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), hotelZone));
        await using (var connection = await conciergeDb.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using var command = new NpgsqlCommand("""
                WITH new_guest AS (
                    INSERT INTO guests (guest_id, given_name, family_name) VALUES ($1, 'Live', $2)
                    RETURNING guest_id
                )
                INSERT INTO stays (stay_id, external_stay_key, guest_id, room_number,
                    check_in_utc, check_out_utc, eligibility_ends_at_utc)
                SELECT $3, $4, guest_id, $5, $6, $7, $8 FROM new_guest;
                """, connection, transaction);
            foreach (var p in new object[] { guestId, family, stayId, $"livecheck-{stayId:N}",
                room, Midnight(today), Midnight(today.AddDays(1)), Midnight(today.AddDays(2)) })
                command.Parameters.AddWithValue(p);
            await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }
        var workflowId = ConciergeStayWorkflowIds.ForStay(stayId);
        var proposal = originalProposal! with { ProposalId = Guid.NewGuid() };
        var workflowInput = new ConciergeStayWorkflowInput(
            stayId, DateTimeOffset.UtcNow.AddMinutes(15), 60,
            NextSequence: firstResult.Sequence, RecentTurns: prior, TranscriptFlushBatchSize: 1,
            PendingRideProposal: proposal);
        await client.StartWorkflowAsync((ConciergeStayWorkflow w) => w.RunAsync(workflowInput),
            new WorkflowOptions(workflowId, TemporalWorkerConnection.TaskQueue));
        temporaryWorkflows.Add(workflowId);
        Console.WriteLine($"WORKFLOW {workflowId}");
        using var http = new HttpClient { BaseAddress = new Uri(web.Urls.First()), Timeout = TimeSpan.FromSeconds(200) };
        var auth = await http.PostAsJsonAsync("/api/concierge/sessions", new ConciergeVerificationRequest(family, room));
        Require(auth.IsSuccessStatusCode, $"Verification HTTP {(int)auth.StatusCode}");
        var session = (await auth.Content.ReadFromJsonAsync<ConciergeSessionResponse>())!;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Credential);
        var turns = new List<object>();
        async Task<ConciergeTurnResult> Turn(ConciergeTurnSubmission s)
        {
            var response = await http.PostAsJsonAsync("/api/concierge/turns",
                new ConciergeTurnRequest(s.TurnId, s.UserMessage, "voice"));
            Require(response.IsSuccessStatusCode, $"Turn HTTP {(int)response.StatusCode}");
            var result = (await response.Content.ReadFromJsonAsync<ConciergeTurnResult>())!;
            turns.Add(new { s.TurnId, s.UserMessage, result.AssistantMessage, result.Sequence });
            Console.WriteLine($"GUEST {s.UserMessage}\nREPLY {result.AssistantMessage}");
            await Save($"transcript-{attempt}.json", turns);
            return result;
        }
        var confirmed = await Turn(first);
        Require(confirmed.AssistantMessage.StartsWith("You're all set", StringComparison.Ordinal), "Initial booking not confirmed.");
        var revisionHistoryBefore = await client.GetWorkflowHandle(workflowId).FetchHistoryAsync();
        var revisionStartEventId = revisionHistoryBefore.Events.Last().EventId;
        var revised = await Turn(revision);
        var receiptReturned = revised.AssistantMessage.Contains("already processed", StringComparison.Ordinal)
            || (revised.AssistantMessage.Contains("Wayan Miami", StringComparison.Ordinal)
                && (revised.AssistantMessage.Contains("booked", StringComparison.OrdinalIgnoreCase)
                    || revised.AssistantMessage.Contains("in progress", StringComparison.OrdinalIgnoreCase)));
        Require(!revised.AssistantMessage.Contains("No ride has been booked", StringComparison.Ordinal), "Revision reopened offer.");
        var revisionHistory = await client.GetWorkflowHandle(workflowId).FetchHistoryAsync();
        await File.WriteAllTextAsync(Path.Combine(artifacts, $"revision-history-{attempt}.json"), revisionHistory.ToJson());
        var revisionProposals = 0;
        foreach (var e in revisionHistory.Events.Where(e => e.EventId > revisionStartEventId
            && e.ActivityTaskScheduledEventAttributes?.ActivityType.Name
                == "TemporalCommunity.Extensions.AI.InvokeFunction"))
        {
            var tool = await converter.ToSingleValueAsync<JsonElement>(e.ActivityTaskScheduledEventAttributes.Input.Payloads_);
            if (tool.GetProperty("functionName").GetString() == "ProposeRide") revisionProposals++;
        }
        Require(revisionProposals == 0, "Revision dispatched ProposeRide instead of guarding.");
        if (fullTail) await Turn(scheduled);
        await Turn(followup);
        var history = await client.GetWorkflowHandle(workflowId).FetchHistoryAsync();
        await File.WriteAllTextAsync(Path.Combine(artifacts, $"stay-history-{attempt}.json"), history.ToJson());
        var scheduledEvents = history.Events.Where(e => e.ActivityTaskScheduledEventAttributes is not null).ToArray();
        var toolNames = new List<string>();
        foreach (var e in scheduledEvents.Where(e => e.ActivityTaskScheduledEventAttributes.ActivityType.Name
            == "TemporalCommunity.Extensions.AI.InvokeFunction"))
        {
            var tool = await converter.ToSingleValueAsync<JsonElement>(e.ActivityTaskScheduledEventAttributes.Input.Payloads_);
            toolNames.Add(tool.GetProperty("functionName").GetString()!);
        }
        var modelCalls = new List<object>();
        foreach (var e in history.Events.Where(e => e.ActivityTaskCompletedEventAttributes is not null))
        {
            var a = e.ActivityTaskCompletedEventAttributes;
            var schedule = scheduledEvents.FirstOrDefault(s => s.EventId == a.ScheduledEventId);
            if (schedule?.ActivityTaskScheduledEventAttributes.ActivityType.Name != "TemporalCommunity.Extensions.AI.GetResponse") continue;
            var result = await converter.ToSingleValueAsync<ChatResponse>(a.Result.Payloads_);
            var calls = result.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>()
                .Select(c => new { c.Name, c.Arguments }).ToArray();
            modelCalls.Add(new { scheduledEventId = a.ScheduledEventId, completedEventId = e.EventId, calls, result.Text });
        }
        await Save($"model-calls-{attempt}.json", modelCalls);
        await using var query = ridesDb.CreateCommand(
            "SELECT id, pickup_at_utc, route_duration_seconds FROM rides WHERE stay_id=$1;");
        query.Parameters.AddWithValue(stayId);
        var rows = new List<(Guid Id, DateTimeOffset Pickup, double Duration)>();
        await using (var reader = await query.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                rows.Add((reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetDouble(2)));
        Require(rows.Count > 0, "Expected a persisted booking.");
        foreach (var row in rows) temporaryWorkflows.Add(row.Id.ToString("N"));
        await using var stream = new WorkflowStreamClient(client, workflowId);
        await using var subscription = stream.GetTopic<RideActivityEntry>(RideActivityFeed.Topic)
            .SubscribeAsync(0).GetAsyncEnumerator();
        Require(await subscription.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15)), "Missing Booked event.");
        var booked = subscription.Current.Value;
        var rideId = booked.RideId;
        Require(booked.Status == "Booked" && booked.RideId == rideId, "Unexpected first feed entry.");
        var bookedRow = rows.Single(r => r.Id == rideId);
        var expectedArrival = bookedRow.Pickup.AddSeconds(bookedRow.Duration);
        Require(booked.ArrivalAtUtc == expectedArrival, "Booked arrival differs from persisted pickup + route duration.");
        await Save($"booked-{attempt}.json", booked);
        var rideHistory = await client.GetWorkflowHandle(rideId.ToString("N")).FetchHistoryAsync();
        await File.WriteAllTextAsync(Path.Combine(artifacts, $"ride-history-{attempt}.json"), rideHistory.ToJson());
        var stores = rideHistory.Events.Count(e => e.ActivityTaskScheduledEventAttributes?.ActivityType.Name
            == RideWorkflowNames.StoreBookingActivity);
        Require(stores == 1, $"Expected one durable store activity, got {stores}.");
        await using var renderer = new HtmlRenderer(web.Services, web.Services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<RideActivityPanel>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Entries"] = new[] { booked }, ["IsOpen"] = true,
            }))).ToHtmlString());
        await File.WriteAllTextAsync(Path.Combine(artifacts, $"panel-{attempt}.html"), html);
        var decodedHtml = System.Net.WebUtility.HtmlDecode(html);
        Require(decodedHtml.Contains("Estimated destination arrival:", StringComparison.Ordinal)
            && decodedHtml.Contains(expectedArrival.ToUniversalTime().ToString("O"), StringComparison.Ordinal), "Rendered panel missing actual Booked ETA.");
        var miamiArrival = TimeZoneInfo.ConvertTime(expectedArrival, hotelZone);
        var confirms = toolNames.Count(n => n == "ConfirmRide");
        Console.WriteLine($"COUNTS ConfirmRide={confirms} firstRideStoreBooking={stores} rides={rows.Count}; ETA {expectedArrival:O} / Miami {miamiArrival:O}");
        var guarded = receiptReturned && revisionProposals == 0;
        report.Add(new { attempt, workflowId, rideId, rideIds = rows.Select(r => r.Id), guarded,
            revisionProposals, confirms, toolNames, stores, rows = rows.Count,
            booked.ArrivalAtUtc, expectedArrival, miamiArrival, componentRendered = true, browserTested = false });
        await Save("assertions.json", report);
        Require(confirms == 1 && rows.Count == 1,
            $"One-booking gate failed: actual ConfirmRide={confirms}, rides={rows.Count}.");
        guardVerified = guarded;
        if (!guarded) Console.WriteLine("INCONCLUSIVE guard branch: model did not request ProposeRide/ConfirmRide on revision; trying another fresh stay.");
    }
    Require(guardVerified, "Three real-model runs did not exercise the guard; no guard verification claim.");
    Console.WriteLine("PASS same-ordinal guard, one durable booking, live Booked ETA, actual component HTML render.");
    if (!fullTail)
        Console.WriteLine($"NOTE original separate ordinal-12 request was captured but not submitted; it is a new scheduling request, not a revision. Text: {scheduled.UserMessage}");
    await Save("outcome.json", new { passed = true, fullTail,
        scope = fullTail ? "full captured booking tail: 11, revised 11, 12, 13" : "same ordinal revision followed directly by captured ordinal-13 confirmation",
        omittedSeparateRequest = fullTail ? null : scheduled, browserTested = false });
    return 0;
}
catch (Exception e)
{
    // Do not print credential-bearing provider/transport exception messages.
    Console.Error.WriteLine($"FAIL during {stage}: {e.GetType().Name}");
    Console.Error.WriteLine(e.StackTrace);
    if (e is PostgresException pg) Console.Error.WriteLine($"Postgres {pg.SqlState}: {pg.MessageText}");
    await Save("outcome.json", new { passed = false, stage, exceptionType = e.GetType().Name,
        assertion = e is CheckFailure ? e.Message : null });
    if (e is CheckFailure) Console.Error.WriteLine(e.Message);
    return 1;
}
finally
{
    // Only fresh live-check workflows, never the source stay/rides.
    if (client is not null)
        foreach (var id in temporaryWorkflows)
        {
            try { await client.GetWorkflowHandle(id).TerminateAsync("Bounded isolated live-check finished"); }
            catch { /* Closed ride workflows need no termination. */ }
        }
    if (web is not null) { await web.StopAsync(); await web.DisposeAsync(); }
    if (rides is not null) { await rides.StopAsync(); await rides.DisposeAsync(); }
    Console.WriteLine("CLEANUP isolated hosts/workers stopped; original AppHost and data untouched.");
}

static void Require(bool condition, string message)
{
    if (!condition) throw new CheckFailure(message);
}
sealed class CheckFailure(string message) : Exception(message);
