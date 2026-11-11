#:project ../../src/Concierge.RideService/Concierge.RideService.csproj
#:project ../../src/Concierge.WorkflowServer/Concierge.WorkflowServer.csproj
// The transitive OllamaSharp generator targets a newer compiler; it is not used by this check.
#:property JsonSerializerIsReflectionEnabledByDefault=true

// Opt-in Step 5 check against the already-running Aspire AppHost. Aspire resource
// environment values stay in memory and are never written to the console.
using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Concierge.Contracts;
using Concierge.RideService;
using Concierge.WorkflowServer.Workflows;
using Npgsql;
using TemporalCommunity.Extensions.AI;
using Temporalio.Client;
using Temporalio.Extensions.WorkflowStreams;

var repoRoot = Path.GetFullPath(Path.Combine(ScriptDirectory(), "..", ".."));
var stage = "reading Aspire resource metadata";
try
{
    var runtime = await ReadRuntimeAsync(repoRoot);
    stage = "connecting to the running Aspire dependencies";
    var hotelTimeZone = TimeZoneInfo.FindSystemTimeZoneById(runtime.HotelTimeZoneId);
    var checkoutDate = DateOnly.FromDateTime(
        TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, hotelTimeZone).DateTime).AddDays(2);
    var guestId = Guid.NewGuid();
    var stayId = Guid.NewGuid();
    var stayWorkflowId = ConciergeStayWorkflowIds.ForStay(stayId);
    var request = new RideBookingRequest(
        guestId,
        stayId,
        checkoutDate,
        new RidePlaceRequest("Hotel ZGY", 25.7617, -80.1918),
        new RidePlaceRequest("South Beach", 25.7826, -80.1341),
        Immediate: true,
        PickupAt: null,
        IdempotencyKey: $"step5-aspire-{Guid.NewGuid():N}",
        RouteDurationSeconds: 0.25);
    var expectedStatuses = new[] { "Booked", "DriverAssigned", "PickedUp", "Completed" };
    var stayStarted = false;
    ITemporalClient? temporalClient = null;

    try
    {
        using var ridesDataSource = NpgsqlDataSource.Create(runtime.RidesConnectionString);
        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri(runtime.RideServiceUrl),
            Timeout = TimeSpan.FromSeconds(20),
        };
        temporalClient = await TemporalClient.ConnectAsync(new TemporalClientConnectOptions
        {
            TargetHost = runtime.TemporalAddress,
            Namespace = runtime.TemporalNamespace,
            DataConverter = DurableAIDataConverter.Instance,
        });

        stage = "starting the temporary stay workflow";
        await temporalClient.StartWorkflowAsync(
            (ConciergeStayWorkflow workflow) => workflow.RunAsync(
                new ConciergeStayWorkflowInput(
                    stayId,
                    DateTimeOffset.UtcNow.AddMinutes(2),
                    TranscriptFlushIntervalSeconds: 60)),
            new WorkflowOptions(stayWorkflowId, TemporalWorkerConnection.TaskQueue));
        stayStarted = true;

        stage = "subscribing the test client to the stay stream";
        await using var streamClient = new WorkflowStreamClient(temporalClient, stayWorkflowId);
        await using var subscription = streamClient
            .GetTopic<RideActivityEntry>(RideActivityFeed.Topic)
            .SubscribeAsync(0)
            .GetAsyncEnumerator();
        var nextEntry = subscription.MoveNextAsync().AsTask();

        stage = "booking an immediate test ride";
        using var response = await httpClient.PostAsJsonAsync(
            "/rides",
            request,
            Step5JsonContext.Default.RideBookingRequest);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Ride booking returned HTTP {(int)response.StatusCode}.");
        }

        var ride = await response.Content.ReadFromJsonAsync(Step5JsonContext.Default.RideResponse)
            ?? throw new InvalidOperationException("Ride booking response was empty.");
        if (ride.Id == Guid.Empty)
        {
            throw new InvalidOperationException("Ride booking response contained an empty ride identifier.");
        }

        stage = "observing the database-backed ride status transitions";
        var (eventStatuses, databaseStatus) = await WaitForDatabaseCompletionAsync(
            ridesDataSource,
            ride.Id,
            TimeSpan.FromSeconds(30));
        if (!eventStatuses.SequenceEqual(expectedStatuses, StringComparer.Ordinal)
            || !string.Equals(databaseStatus, "Completed", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Observed ride_events [{string.Join(", ", eventStatuses)}] and rides.status "
                + $"'{databaseStatus ?? "missing"}'.");
        }

        stage = "consuming the test subscriber stream entries";
        var received = new List<RideActivityEntry>(expectedStatuses.Length);
        for (var index = 0; index < expectedStatuses.Length; index++)
        {
            if (!await nextEntry.WaitAsync(TimeSpan.FromSeconds(20)))
            {
                throw new InvalidOperationException("The stay stream closed before all ride entries arrived.");
            }

            received.Add(subscription.Current.Value);
            if (index < expectedStatuses.Length - 1)
            {
                nextEntry = subscription.MoveNextAsync().AsTask();
            }
        }

        if (!received.Select(entry => entry.Status)
                .SequenceEqual(expectedStatuses, StringComparer.Ordinal)
            || received.Any(entry => entry.RideId != ride.Id)
            || !received.Select(entry => entry.Sequence).SequenceEqual([1, 2, 3, 4]))
        {
            throw new InvalidOperationException("The test subscriber received unexpected ride stream entries.");
        }

        Console.WriteLine(
            $"PASS  Aspire database transitions: ride_events {string.Join(" -> ", eventStatuses)}; "
            + $"rides.status={databaseStatus}.");
        Console.WriteLine(
            $"PASS  Test subscriber: {received.Count} RideActivityEntry values, sequences 1–4, "
            + $"statuses {string.Join(" -> ", received.Select(entry => entry.Status))}.");
        Console.WriteLine("Step 5 Aspire check passed.");
    }
    finally
    {
        if (stayStarted)
        {
            try
            {
                await temporalClient!.GetWorkflowHandle(stayWorkflowId)
                    .TerminateAsync("Step 5 live-check cleanup");
            }
            catch
            {
                Console.Error.WriteLine(
                    "WARN  Could not terminate the temporary stay workflow; it will expire within two minutes.");
            }
        }
    }
}
catch (Exception exception)
{
    Console.Error.WriteLine($"FAIL  Step 5 Aspire check during {stage} ({exception.GetType().Name}).");
    return 1;
}

return 0;

static async Task<(string[] EventStatuses, string? DatabaseStatus)> WaitForDatabaseCompletionAsync(
    NpgsqlDataSource dataSource,
    Guid rideId,
    TimeSpan timeout)
{
    var deadline = DateTimeOffset.UtcNow + timeout;
    string[] statuses = [];
    string? databaseStatus = null;
    while (DateTimeOffset.UtcNow < deadline)
    {
        statuses = await ReadEventStatusesAsync(dataSource, rideId, CancellationToken.None);
        databaseStatus = await ReadCurrentStatusAsync(dataSource, rideId, CancellationToken.None);
        if (statuses.Length == 4 && databaseStatus == "Completed")
        {
            return (statuses, databaseStatus);
        }

        await Task.Delay(TimeSpan.FromMilliseconds(50));
    }

    return (statuses, databaseStatus);
}

static async Task<string[]> ReadEventStatusesAsync(
    NpgsqlDataSource dataSource,
    Guid rideId,
    CancellationToken cancellationToken)
{
    await using var command = dataSource.CreateCommand(
        "SELECT status FROM ride_events WHERE ride_id = $1 ORDER BY sequence;");
    command.Parameters.AddWithValue(rideId);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    var statuses = new List<string>();
    while (await reader.ReadAsync(cancellationToken))
    {
        statuses.Add(reader.GetString(0));
    }

    return [.. statuses];
}

static async Task<string?> ReadCurrentStatusAsync(
    NpgsqlDataSource dataSource,
    Guid rideId,
    CancellationToken cancellationToken)
{
    await using var command = dataSource.CreateCommand("SELECT status FROM rides WHERE id = $1;");
    command.Parameters.AddWithValue(rideId);
    return (string?)await command.ExecuteScalarAsync(cancellationToken);
}

static async Task<AspireRuntime> ReadRuntimeAsync(string repoRoot)
{
    var appHost = Path.Combine(repoRoot, "src", "Concierge.AppHost", "Concierge.AppHost.csproj");
    var startInfo = new ProcessStartInfo("aspire")
    {
        WorkingDirectory = repoRoot,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    foreach (var argument in new[]
    {
        "describe", "--apphost", appHost, "--format", "Json", "--non-interactive", "--nologo",
    })
    {
        startInfo.ArgumentList.Add(argument);
    }

    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("Could not start the Aspire CLI.");
    var outputTask = process.StandardOutput.ReadToEndAsync();
    var errorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    var json = await outputTask;
    _ = await errorTask;
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException($"Aspire describe returned exit code {process.ExitCode}.");
    }

    using var document = JsonDocument.Parse(json);
    var resources = document.RootElement.GetProperty("resources").EnumerateArray().ToArray();
    var rideService = resources.Single(resource =>
        resource.GetProperty("name").GetString()!.StartsWith("ride-service-", StringComparison.Ordinal));
    var environment = rideService.GetProperty("environment");
    var ridesConnectionString = RequiredValue(environment, "ConnectionStrings__rides");
    var temporalAddress = RequiredValue(environment, "TEMPORAL_ADDRESS");
    var temporalNamespace = RequiredValue(environment, "TEMPORAL_NAMESPACE");
    var hotelTimeZoneId = RequiredValue(environment, "Hotel__TimeZoneId");
    var rideServiceUrl = rideService.GetProperty("urls").EnumerateArray()
        .Single(endpoint => endpoint.GetProperty("name").GetString() == "http")
        .GetProperty("url")
        .GetString()
        ?? throw new InvalidOperationException("The RideService HTTP endpoint is unavailable.");

    return new AspireRuntime(
        ridesConnectionString,
        temporalAddress,
        temporalNamespace,
        hotelTimeZoneId,
        rideServiceUrl);
}

static string RequiredValue(JsonElement element, string name)
{
    if (!element.TryGetProperty(name, out var property)
        || property.ValueKind != JsonValueKind.String
        || string.IsNullOrWhiteSpace(property.GetString()))
    {
        throw new InvalidOperationException($"Aspire resource setting '{name}' is unavailable.");
    }

    return property.GetString()!;
}

static string ScriptDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

sealed record AspireRuntime(
    string RidesConnectionString,
    string TemporalAddress,
    string TemporalNamespace,
    string HotelTimeZoneId,
    string RideServiceUrl);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(RideBookingRequest))]
[JsonSerializable(typeof(RideResponse))]
partial class Step5JsonContext : JsonSerializerContext
{
}
