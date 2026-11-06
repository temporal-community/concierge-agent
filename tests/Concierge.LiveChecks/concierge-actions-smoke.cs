#:project ../../src/Concierge.WorkflowServer/Concierge.WorkflowServer.csproj
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property PublishAot=false

// Opt-in real-model gate. selftest never discovers Aspire, opens a database, or submits a turn.
// Each ordered group owns a disposable verified stay; only real conversational dependencies share it.
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Concierge.Contracts;
using Concierge.WorkflowServer.AI;
using Concierge.WorkflowServer.Workflows;
using Npgsql;
using TemporalCommunity.Extensions.AI;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.History.V1;
using Temporalio.Client;

if (args is ["selftest"])
    return await RunSelfTestsAsync();

var definitions = DefineGroups(HotelToday());
if (args.Length > 1 || args.Length == 1 && args[0] is not (
    "nearest" or "nearby" or "weather-now" or "place-continuity" or "immediate-status" or "ride-conversation" or "voice-same-ordinal-revision"))
{
    Console.Error.WriteLine("Usage: dotnet run --file tests/Concierge.LiveChecks/concierge-actions-smoke.cs [nearest|nearby|weather-now|place-continuity|immediate-status|ride-conversation|voice-same-ordinal-revision|selftest]");
    return 2;
}

var report = new CheckReport();
try
{
    var runtime = await ReadRuntimeAsync(Path.GetFullPath(Path.Combine(ScriptDirectory(), "..", "..")));
    var client = await TemporalClient.ConnectAsync(new TemporalClientConnectOptions
    {
        TargetHost = runtime.TemporalAddress,
        Namespace = runtime.TemporalNamespace,
        DataConverter = DurableAIDataConverter.Instance,
    });
    foreach (var group in SelectGroups(definitions, args.FirstOrDefault()))
        await RunLiveGroupAsync(group, runtime, client, report);
}
catch (Exception exception)
{
    report.Fail($"Live gate setup: {exception.GetType().Name}: {exception.Message}");
}

Console.WriteLine(report.Summary);
foreach (var failure in report.Failures)
    Console.Error.WriteLine($"FAIL  {failure}");
Console.WriteLine(report.Success ? "Step 6 live check passed." : "Step 6 live check did not pass.");
return report.Success ? 0 : 1;

static DateOnly HotelToday() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(
    DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/New_York")).DateTime);

static ScenarioGroup[] DefineGroups(DateOnly today)
{
    Scenario Offer(string message, string key = "offer", string? requires = null, bool warning = false,
        DateOnly? pickupDate = null, string destination = "South Beach") =>
        new(message, ["ProposeRide"], ["No ride has been booked"],
            Requires: requires, Provides: key, WarnExisting: warning, Destination: destination,
            PickupDate: pickupDate, PickupTime: pickupDate is null ? null : new TimeOnly(10, 0));
    Scenario Confirm(string message = "Yes, confirm that ride.", string offer = "offer", string booked = "booked") =>
        new(message, ["ConfirmRide"], ["all set|booked|pickup"], Requires: offer, Provides: booked);
    Scenario Weather(string message, int start, int count = 1, string? place = null, bool current = false) =>
        new(message, Enumerable.Repeat("GetWeather", count).ToArray(), [],
            WeatherDates: Enumerable.Range(start, count).Select(today.AddDays).ToArray(),
            Place: place, CurrentWeather: current);
    const string RideRead = "GetRideStatus|GetRideHistory";
    Scenario[] BookingThenAck(string acknowledgement) =>
    [
        Offer("Okay. Um, could you book me a ride to South Beach?"),
        Confirm("Book it for right now."),
        // Read-only status/history is the only alternative; any read must return the stored booked ride.
        new(acknowledgement, [], [], Requires: "booked", ClosedBooking: true,
            MatchRide: "booked", ReadAlternative: [RideRead]),
    ];

    return
    [
        new("information",
        [
            new("What can you help with?", [], ["weather", "direction|route|travel", "ride", "nearby|near"]),
            new("How long is my stay and what day am I checking out?", [],
                ["one night|1 night", today.AddDays(1).DayOfWeek.ToString()]),
            new("How much is a ride?", [], ["complimentary|no charge|no cost"], Fare: true),
        ]),
        // Preserve the weather/location prefix that exposed the movie-theater regression.
        // Focused modes select these exact scenario objects, not a second hand-maintained chain.
        new("nearby",
        [
            new("What can you help with?", [], ["weather", "ride", "nearby|near"]),
            Weather("What's the weather like today?", 0),
            Weather("What's the weather like right now?", 0, current: true),
            Weather("Will it rain in Fort Lauderdale tomorrow?", 1, place: "Fort Lauderdale"),
            new("How long is the drive to South Beach?", ["GetRoute"], ["South Beach"]),
            new("What is the nearest pharmacy?", ["FindNearbyPlaces"], ["pharmac", "mile"], NearbyCategory: "pharmacy"),
            new("What is the nearest movie theater?", ["FindNearbyPlaces"], ["theater|cinema", "mile"], NearbyCategory: "cinema"),
            new("What are Wayan Miami's hours?", ["GetPlaceOpeningHours"], ["Wayan"]),
            new("How long is the drive to South Beach, Miami Beach, FL 33139?", ["GetRoute"], ["South Beach"]),
        ]),
        new("weather-dates",
        [
            Weather("What's the weather for the next two days?", 0, 2),
            Weather("what's the weather for the next 3 days in plantation?", 0, 3, "Plantation"),
            Weather("What's the weather for the next three days starting tomorrow?", 1, 3),
            Weather($"What's the weather for the next three days starting {today.AddDays(2).DayOfWeek}?", 2, 3),
        ]),
        new("immediate-status",
        [
            Offer("Please book me a ride to South Beach, Miami Beach, Florida 33139 right now."),
            Confirm("yes, confirm"),
            // A status question may legitimately select history, not manufacture another offer.
            new("Is my ride almost here?", ["GetRideStatus|GetRideHistory"], [], Requires: "booked", MatchRide: "booked"),
        ]),
        new("ride-conversation",
        [
            new("What is the nearest pharmacy you recommend?", ["FindNearbyPlaces"], [],
                Provides: "recommendation", NearbyCategory: "pharmacy", FocusedRecommendation: true, VoiceOrdinal: 1),
            // Keep the actual deictic follow-up unchanged; only the expected endpoint is bound to evidence.
            new("Could you book me a ride there right now?", ["ProposeRide"], ["No ride has been booked"],
                Requires: "recommendation", Provides: "offer", ContinuityOffer: true, VoiceOrdinal: 2),
            Confirm("Yes, book it now.") with { VoiceOrdinal = 3 },
            new("Is my ride almost here?", [RideRead], [], Requires: "booked",
                Provides: "status", MatchRide: "booked", VoiceOrdinal: 4),
            new("Where is the hotel gym, and when is it open?", [], ["fourth|4th", "24"],
                Requires: "status", Provides: "hotel-answer", VoiceOrdinal: 5),
            // This API gate ends the interaction explicitly, not via a simulated Deepgram function.
            new("End chat", [], [], Requires: "hotel-answer", EndSession: true),
        ]),
        new("scheduled-cancellation",
        [
            Offer("Please book me a ride to South Beach tomorrow at 10 am.", pickupDate: today.AddDays(1)),
            Confirm("confirm ride"),
            new("Show me my upcoming rides.", ["GetRideHistory"], [], Requires: "booked", MatchRide: "booked"),
            new("Cancel my ride tomorrow at 10 AM, please.", ["GetRideHistory", "ProposeRideCancellation"],
                ["Would you like me to cancel it"], Requires: "booked", Provides: "cancel-offer", MatchRide: "booked"),
            new("Yes, cancel that one.", ["ConfirmRideCancellation"], ["cancel"],
                Requires: "cancel-offer", Provides: "cancelled", MatchRide: "booked"),
        ]),
        new("deferred-acceptance",
        [
            Offer("Book me a ride to South Beach."),
            new("I need a moment to think.", [], [], Requires: "offer", Provides: "pause"),
            // The pause must have completed, but the proposal itself remains the booking prerequisite.
            new("I'd like to go ahead with that ride.", ["ConfirmRide"], ["all set|booked|pickup"],
                Requires: "pause", Provides: "booked", MatchOffer: "offer"),
        ]),
        new("missing-destination",
        [
            new("Okay. Um, could you book me a ride?", [], []),
            Offer("To South Beach, right now."),
            Confirm("That sounds good; please arrange it."),
        ]),
        new("postbooking-yes", BookingThenAck("Yes, please.")),
        new("postbooking-thanks", BookingThenAck("Thanks.")),
        new("voice-same-ordinal-revision",
        [
            Offer("Book me a ride to South Beach.") with { VoiceOrdinal = 1 },
            Confirm() with { VoiceOrdinal = 2 },
            // A changed hash is a new answer, not permission to repeat this spoken booking.
            new("Yes, actually book me an additional separate ride to South Beach.", [], ["South Beach", "booked|progress|complete"],
                Requires: "booked", VoiceOrdinal: 2, ClosedBooking: true,
                MatchRide: "booked", ReadAlternative: [RideRead]),
        ]),
        new("explicit-new-same-destination",
        [
            // Scheduled rides stay active without depending on the simulator's lifecycle duration.
            Offer("Please book me a ride to South Beach tomorrow at 10 am.", pickupDate: today.AddDays(1)),
            Confirm(),
            Offer("I want an additional, separate ride to South Beach tomorrow at 10 am, not the status of my existing ride. Please offer the new booking first.",
                "second-offer", "booked", warning: true, pickupDate: today.AddDays(1)),
            new("Yes, book that additional ride.", ["ConfirmRide"], ["all set|booked|pickup"],
                Requires: "second-offer", Provides: "second-booked", DifferentRide: "booked"),
        ]),
        new("nearest",
        [
            new("Please book me a ride to the nearest pharmacy.", ["FindNearbyPlaces", "ProposeRide"],
                ["No ride has been booked"], Provides: "offer", NearbyCategory: "pharmacy", NearestProposal: true),
            Confirm(),
        ]),
        new("place-continuity",
        [
            new("What coffee shops nearby do you recommend? Please give me a few alternatives.",
                ["FindNearbyPlaces"], [], Provides: "coffee-options", NearbyCategory: "cafe", NearbyAlternatives: true),
            new("I choose the named non-first option.", [], [], Requires: "coffee-options",
                Provides: "coffee-selection", ContinuitySelection: true),
            new("Please offer me a ride to that coffee shop.", ["ProposeRide"], ["No ride has been booked"],
                Requires: "coffee-selection", ContinuityOffer: true),
        ]),
        new("named-address-booking",
        [
            Offer("Book me a ride to Wayan Miami, 50 Northwest 24th Street.", destination: "Wayan"),
            Confirm(),
        ]),
        // Safe clarification is not counted as successful booking coverage.
        new("named-address-safe-resolution",
        [
            new("Book me a ride to Walgreens, 3103 Biscayne Boulevard.", ["ProposeRide"],
                ["No ride has been booked"], SafeResolution: true),
        ]),
    ];
}

static ScenarioGroup[] SelectGroups(ScenarioGroup[] groups, string? mode) => mode switch
{
    null => groups,
    "weather-now" => [new("weather-now", groups.First(g => g.Name == "nearby").Scenarios
        .Where((_, index) => index is 0 or 2).ToArray())],
    "place-continuity" => groups.Where(group => group.Name == mode).ToArray(),
    _ => groups.Where(group => group.Name == mode).ToArray(),
};

static async Task RunLiveGroupAsync(
    ScenarioGroup group, AspireRuntime runtime, ITemporalClient client, CheckReport report)
{
    Console.WriteLine($"\nGROUP  {group.Name}");
    var stayId = Guid.NewGuid();
    var familyName = $"Livecheck{Guid.NewGuid():N}";
    var workflowId = ConciergeStayWorkflowIds.ForStay(stayId);
    // Only the local Aspire dev certificate is accepted, and only for loopback.
    using var handler = new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (request, _, _, _) => request.RequestUri?.IsLoopback == true,
    };
    using var http = new HttpClient(handler) { BaseAddress = new Uri(runtime.WebUrl), Timeout = TimeSpan.FromSeconds(200) };
    var seeded = false;
    var sessionEnded = false;
    var conversationEvidence = new List<TurnEvidence>();
    try
    {
        string room;
        for (var attempt = 0; ; attempt++)
        {
            room = Random.Shared.Next(9000, 10_000).ToString(CultureInfo.InvariantCulture);
            try
            {
                await SeedGuestAsync(runtime.ConciergeConnectionString, stayId, familyName, room);
                seeded = true;
                break;
            }
            // Room allocation is not a model/action retry; the seed transaction rolls back.
            catch (PostgresException exception) when (exception.SqlState == "23P01" && attempt < 5) { }
        }
        using var verification = await http.PostAsJsonAsync(
            "/api/concierge/sessions", new ConciergeVerificationRequest(familyName, room));
        verification.EnsureSuccessStatusCode();
        var session = await verification.Content.ReadFromJsonAsync<ConciergeSessionResponse>()
            ?? throw new InvalidOperationException("Verification returned no session.");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Credential);
        Console.WriteLine($"WORKFLOW  {workflowId}");
        await RunGroupAsync(group, async scenario =>
        {
            if (scenario.EndSession)
            {
                using var end = await http.PostAsync("/api/concierge/end", null);
                if (end.StatusCode != System.Net.HttpStatusCode.NoContent)
                    return TurnEvidence.Uncertain($"End chat returned HTTP {(int)end.StatusCode}");
                sessionEnded = true;
                using var historyAfterEnd = await http.GetAsync("/api/concierge/history");
                if (historyAfterEnd.StatusCode != System.Net.HttpStatusCode.Unauthorized)
                    return TurnEvidence.Uncertain($"Ended credential still accepted: HTTP {(int)historyAfterEnd.StatusCode}");
                Console.WriteLine("END    Session ended; credential rejected.");
                return new TurnEvidence("Session ended.", [], 0, 0, "", 0, 0);
            }
            var turnId = TurnId(stayId, scenario);
            var stopwatch = Stopwatch.StartNew();
            // Never resend an uncertain action, even with the same id.
            using var response = await http.PostAsJsonAsync("/api/concierge/turns",
                new ConciergeTurnRequest(turnId, scenario.Message, scenario.VoiceOrdinal is null ? "web" : "voice"));
            if (!response.IsSuccessStatusCode)
                return TurnEvidence.Uncertain($"HTTP {(int)response.StatusCode}");
            var result = await response.Content.ReadFromJsonAsync<ConciergeTurnResult>()
                ?? throw new InvalidOperationException("Turn returned no result.");
            if (result.TurnId != turnId)
                return TurnEvidence.Uncertain("HTTP result belongs to a different turn.");
            var description = await client.GetWorkflowHandle(workflowId).DescribeAsync();
            var runId = description.RunId;
            var history = await client.GetWorkflowHandle(workflowId, runId).FetchHistoryAsync();
            var evidence = ReadTurnEvidence(history.Events, runId, turnId, result.AssistantMessage);
            conversationEvidence.Add(evidence);
            Console.WriteLine($"GUEST  {scenario.Message}");
            foreach (var call in evidence.Calls)
                Console.WriteLine($"TOOL   {call.Name} {Clip(call.Arguments, 160)} => {Clip(call.Result, 220)}");
            Console.WriteLine($"AGENT  {result.AssistantMessage} ({stopwatch.Elapsed.TotalSeconds:F1}s)");
            Console.WriteLine($"BUDGET run={runId} events={evidence.StartEventId}..{evidence.EndEventId} models={evidence.ModelCalls} tool-rounds={evidence.ToolRounds}");
            return evidence;
        }, report);
        if (group.Name == "ride-conversation")
        {
            var bookings = conversationEvidence.SelectMany(turn => turn.Calls)
                .Where(call => call.Name == "ConfirmRide").ToArray();
            if (bookings.Length != 1)
                report.Fail($"{group.Name}: expected exactly one ConfirmRide in Temporal turn histories, got {bookings.Length}");
            var booked = bookings.Length == 1 ? (Decode(bookings[0]).Value as ConciergeRideActionResult)?.Ride : null;
            var draft = conversationEvidence.SelectMany(turn => turn.Calls).Where(call => call.Name == "ProposeRide")
                .Select(call => (Decode(call).Value as ConciergeRideProposalToolResult)?.Proposal).FirstOrDefault();
            await CheckSinglePersistedBookingAsync(runtime.RideConnectionString, stayId, booked, draft, report);
            if (!sessionEnded) report.Fail($"{group.Name}: required end-chat endpoint was not reached");
        }
    }
    catch (Exception exception)
    {
        report.Fail($"{group.Name}: setup/runner failed: {exception.GetType().Name}: {exception.Message}");
    }
    finally
    {
        // Every operation is scoped to the ID seeded above, never the user's AppHost or other stays.
        if (seeded)
        {
            try
            {
                if (!sessionEnded && http.DefaultRequestHeaders.Authorization is not null)
                {
                    using var end = await http.PostAsync("/api/concierge/end", null);
                    if (!end.IsSuccessStatusCode)
                        report.Fail($"{group.Name}: ending temporary stay returned HTTP {(int)end.StatusCode}");
                }
            }
            catch (Exception exception) { report.Fail($"{group.Name}: end-session cleanup: {exception.Message}"); }
            try
            {
                await client.GetWorkflowHandle(workflowId).TerminateAsync("Step 6 temporary group cleanup");
            }
            catch (Temporalio.Exceptions.RpcException exception) when (
                exception.Code == Temporalio.Exceptions.RpcException.StatusCode.NotFound) { }
            catch (Exception exception) { report.Fail($"{group.Name}: workflow cleanup: {exception.Message}"); }
            // Retain the ended stay/ride rows as evidence; do not delete foreign-key-connected history.
        }
    }
}

static async Task CheckSinglePersistedBookingAsync(
    string connectionString, Guid stayId, RideSummary? booked, ConciergeRideProposalDraft? draft, CheckReport report)
{
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand(
        """
        SELECT id, pickup_name, dropoff_name, is_immediate,
            (SELECT count(*) FROM ride_events e WHERE e.ride_id = r.id AND e.status = 'Booked'),
            dropoff_latitude, dropoff_longitude
        FROM rides r WHERE stay_id = $1;
        """, connection);
    command.Parameters.AddWithValue(stayId);
    await using var reader = await command.ExecuteReaderAsync();
    var rows = 0;
    while (await reader.ReadAsync())
    {
        rows++;
        if (booked is null || reader.GetGuid(0) != booked.Id || reader.GetString(1) != booked.Pickup
            || reader.GetString(2) != booked.Dropoff || !reader.GetBoolean(3) || reader.GetInt64(4) != 1
            || draft is null || Math.Abs(reader.GetDouble(5) - draft.Dropoff.Latitude) > 0.0001
            || Math.Abs(reader.GetDouble(6) - draft.Dropoff.Longitude) > 0.0001)
            report.Fail("ride-conversation: persisted booking/Booked event does not match the confirmed immediate ride");
    }
    if (rows != 1) report.Fail($"ride-conversation: expected exactly one persisted ride for the fictional stay, got {rows}");
    else Console.WriteLine("HISTORY  One persisted ride; booking facts and single Booked event checked.");
}

// This same runner is exercised offline with actual encoded activity outcomes.
static async Task RunGroupAsync(
    ScenarioGroup group, Func<Scenario, Task<TurnEvidence>> send, CheckReport report)
{
    var state = new Dictionary<string, Prerequisite>(StringComparer.Ordinal);
    string? uncertain = null;
    foreach (var scenario in group.Scenarios)
    {
        var activeScenario = scenario;
        if (scenario.ContinuitySelection)
        {
            var source = scenario.Requires is { } selectionKey && state.TryGetValue(selectionKey, out var selectionState)
                ? selectionState : null;
            var selected = source is { Valid: true }
                ? SelectSpokenNonFirstUniquePlace(source.NearbyPlaces, source.NearbyAnswer) : null;
            if (selected is null)
            {
                var selectionLabel = $"{group.Name}: {scenario.Message}";
                var beforeSelectionFailure = report.FailureCount;
                report.Fail($"{selectionLabel}: no uniquely named, spoken non-first nearby result is available for selection");
                if (scenario.Provides is { } failedKey) state[failedKey] = new(false);
                report.CompleteScenario(beforeSelectionFailure);
                continue;
            }
            activeScenario = scenario with
            {
                Message = $"I choose {selected.Name}, but don't book anything yet.",
                SelectedNearbyPlace = selected,
            };
        }
        else if (scenario.ContinuityOffer)
        {
            var source = scenario.Requires is { } offerKey && state.TryGetValue(offerKey, out var offerState)
                ? offerState : null;
            if (source?.SelectedNearbyPlace is not { } selected)
            {
                var offerLabel = $"{group.Name}: {scenario.Message}";
                var beforeOfferFailure = report.FailureCount;
                report.Fail($"{offerLabel}: selected canonical nearby place was not handed off");
                if (scenario.Provides is { } failedKey) state[failedKey] = new(false);
                report.CompleteScenario(beforeOfferFailure);
                continue;
            }
            activeScenario = scenario with
            {
                SelectedNearbyPlace = selected,
            };
        }
        var label = $"{group.Name}: {activeScenario.Message}";
        var blocked = uncertain;
        if (blocked is null && scenario.Requires is { } required
            && (!state.TryGetValue(required, out var prerequisite) || !prerequisite.Valid))
            blocked = $"required state '{required}' is absent or invalid";
        if (blocked is not null)
        {
            report.Block($"{label}: {blocked}");
            if (scenario.Provides is { } key) state[key] = new(false);
            continue;
        }
        var before = report.FailureCount;
        TurnEvidence evidence;
        try { evidence = await send(activeScenario); }
        catch (Exception exception) { evidence = TurnEvidence.Uncertain($"{exception.GetType().Name}: {exception.Message}"); }
        if (evidence.Error is not null)
        {
            // The workflow may still be running. Do not attribute its late activities to another turn.
            uncertain = $"previous turn outcome/boundary uncertain ({evidence.Error}); group quarantined";
            report.Fail($"{label}: {uncertain}");
            if (scenario.Provides is { } key) state[key] = new(false);
        }
        else
        {
            Prerequisite outcome;
            try { outcome = ValidateTurn(activeScenario, evidence, state, report, label); }
            catch (Exception exception)
            {
                report.Fail($"{label}: invalid evidence/validation failed ({exception.GetType().Name}: {exception.Message})");
                outcome = new(false);
            }
            if (scenario.Provides is { } key) state[key] = outcome;
        }
        report.CompleteScenario(before);
    }
}

static Prerequisite ValidateTurn(
    Scenario scenario, TurnEvidence evidence, Dictionary<string, Prerequisite> state, CheckReport report, string label)
{
    void Check(bool condition, string message) { if (!condition) report.Fail($"{label}: {message}"); }
    var calls = evidence.Calls;
    var beforeStateChecks = report.FailureCount;
    if (scenario.EndSession)
    {
        Check(evidence.ModelCalls == 0 && evidence.ToolRounds == 0 && calls.Count == 0
            && evidence.Answer == "Session ended.", "end-chat endpoint/credential evidence is invalid");
        return new(report.FailureCount == beforeStateChecks);
    }
    Check(evidence.ModelCalls is >= 1 and <= 3, $"model calls {evidence.ModelCalls}, expected 1..3");
    Check(evidence.ToolRounds <= 2, $"tool rounds {evidence.ToolRounds}, maximum 2");
    if (scenario.NearbyAlternatives)
        Check(evidence.ModelCalls == 1 && evidence.ToolRounds == 1,
            $"nearby alternatives budget is one model call and one tool round, got {evidence.ModelCalls} and {evidence.ToolRounds}");
    if (scenario.ContinuitySelection)
        Check(evidence.ModelCalls == 1 && calls.Count == 0 && evidence.ToolRounds == 0,
            $"known-place selection budget is one model call and no tools, got {evidence.ModelCalls}, {calls.Count}, {evidence.ToolRounds}");
    if (scenario.ContinuityOffer)
        Check(evidence.ModelCalls == 1 && evidence.ToolRounds == 1,
            $"canonical offer budget is one model call and one tool round, got {evidence.ModelCalls} and {evidence.ToolRounds}");
    if (scenario.NearbyCategory is not null && !scenario.NearestProposal)
        Check(evidence.ModelCalls == 1, "ordinary nearby lookup must use exactly one model call");
    // An explicit read-only alternative applies only on an exact match; otherwise the primary
    // shape is reported. It never admits side-effect tools and requires a verified matching ride.
    var readAlternative = scenario.ReadAlternative is { } alternative && calls.Count == alternative.Length
        && calls.Select((c, i) => alternative[i].Split('|').Contains(c.Name)).All(ok => ok);
    if (readAlternative)
        Check(scenario.ReadAlternative!.All(t => t.Split('|').All(name => name is "GetRideStatus" or "GetRideHistory"))
            && scenario.MatchRide is { } readMatch && state.TryGetValue(readMatch, out var readRide) && readRide.Ride is not null,
            "read-only alternative lacks a read-only definition or verified stored ride");
    var tools = readAlternative ? scenario.ReadAlternative! : scenario.Tools;
    Check(calls.Count == tools.Length,
        $"ordered tool cardinality: expected [{string.Join(", ", tools)}], got [{string.Join(", ", calls.Select(c => c.Name))}]");
    for (var index = 0; index < Math.Min(calls.Count, tools.Length); index++)
        Check(tools[index].Split('|').Contains(calls[index].Name),
            $"tool {index + 1}: expected {tools[index]}, got {calls[index].Name}");
    // A duplicate is a reported assertion, never SingleOrDefault's exception. Multi-day weather
    // intentionally repeats the same tool; all other definitions expect each tool at most once.
    foreach (var duplicate in calls.GroupBy(c => c.Name).Where(g =>
        g.Count() > tools.Count(t => t.Split('|').Contains(g.Key))))
        Check(false, $"duplicate/unexpected tool {duplicate.Key} ({duplicate.Count()} calls)");

    var decoded = calls.Select(Decode).ToArray();
    for (var index = 0; index < calls.Count; index++)
        Check(decoded[index].Error is null, $"{calls[index].Name}: {decoded[index].Error}");
    var values = decoded.Select(d => d.Value).ToArray();
    var proposal = values.OfType<ConciergeRideProposalToolResult>().FirstOrDefault();
    var actions = values.OfType<ConciergeRideActionResult>().ToArray();
    var booking = calls.Select((c, i) => (c, i)).Where(x => x.c.Name == "ConfirmRide")
        .Select(x => values[x.i] as ConciergeRideActionResult).FirstOrDefault()?.Ride;
    var cancellation = actions.FirstOrDefault(a => a.ConfirmedCancellationRideId is not null)?.ConfirmedCancellationRideId;
    var offer = scenario.MatchOffer ?? scenario.Requires;
    state.TryGetValue(offer ?? "", out var offered);
    RideSummary? expectedRide = null;
    if (scenario.MatchRide is { } match && state.TryGetValue(match, out var matched))
        expectedRide = matched.Ride;
    if (scenario.DifferentRide is { } different && state.TryGetValue(different, out var earlier))
        Check(booking is not null && booking.Id != earlier.Ride?.Id, "additional booking must have a different ride ID");
    for (var index = 0; index < calls.Count; index++)
    {
        var call = calls[index];
        switch (values[index])
        {
            case ConciergeRideProposalToolResult p:
                if (scenario.SafeResolution)
                {
                    Check(p.Status == "Available" && ValidProposal(p.Proposal)
                        || p.Status == "NeedsClarification" && p.Proposal is null && !string.IsNullOrWhiteSpace(p.Message),
                        $"safe named-address resolution requires a real offer or explicit clarification, got {p.Status}");
                }
                else
                    Check(p.Status == "Available" && ValidProposal(p.Proposal),
                        $"proposal must be Available with a non-null valid draft, got {p.Status}");
                if (p.Proposal is { } draft && ValidProposal(draft))
                {
                    using var arguments = JsonDocument.Parse(call.Arguments);
                    var immediate = arguments.RootElement.GetProperty("immediate").GetBoolean();
                    Check(draft.Immediate == immediate, "proposal timing disagrees with requested timing");
                    if (scenario.Destination is { } destination)
                        Check(draft.Dropoff.Name.Contains(destination, StringComparison.OrdinalIgnoreCase),
                            $"proposal does not resolve requested destination {destination}");
                    if (scenario.PickupDate is { } pickupDate)
                    {
                        var local = draft.PickupAtUtc is { } scheduledAt ? HotelTime(scheduledAt) : (DateTimeOffset?)null;
                        Check(!draft.Immediate && local is { } localAt && DateOnly.FromDateTime(localAt.DateTime) == pickupDate
                            && TimeOnly.FromDateTime(localAt.DateTime) == scenario.PickupTime,
                            "proposal does not match the guest's requested local date and time");
                    }
                    if (!immediate && arguments.RootElement.TryGetProperty("pickupAt", out var pickupAt)
                        && DateTimeOffset.TryParse(pickupAt.GetString(), out var requestedPickup))
                        Check(draft.PickupAtUtc == requestedPickup || p.PickupAdjusted && p.EarliestPickupAt == draft.PickupAtUtc,
                            "scheduled proposal does not preserve requested or explicitly adjusted pickup");
                }
                if (scenario.WarnExisting)
                {
                    var matches = p.ExistingRides ?? [];
                    var priorId = offered?.Ride?.Id;
                    Check(priorId is not null && matches.Count is > 0 and <= 3 && matches.Any(r => r.Id == priorId),
                        "new offer omits the matching active ride or exceeds the bounded warning list");
                }
                break;
            case ConciergeRideActionResult action:
                if (call.Name == "ConfirmRide")
                {
                    Check(action.Message is null && ValidRide(action.Ride), "booking lacks a successful ride with a nonempty ID");
                    if (action.Ride is { } ride && offered?.Proposal is { } bookedDraft)
                    {
                        Check(ride.Dropoff == bookedDraft.Dropoff.Name && ride.Pickup == bookedDraft.Pickup.Name,
                            "booking facts do not match the offered route");
                        if (!bookedDraft.Immediate)
                            Check(ride.PickupAt == bookedDraft.PickupAtUtc, "booking pickup does not match the offered schedule");
                    }
                    else
                        Check(false, "booking has no actual prerequisite proposal to compare");
                }
                else if (call.Name == "ConfirmRideCancellation")
                    Check(expectedRide is not null && action.ConfirmedCancellationRideId == expectedRide.Id,
                        "cancellation outcome does not confirm the selected ride");
                else
                {
                    Check(action.Message is null, $"ride read/offer failed: {action.Message}");
                    var rides = action.Rides ?? [];
                    if (expectedRide is not null)
                        Check(action.Ride?.Id == expectedRide.Id || rides.Any(r => r.Id == expectedRide.Id),
                            "ride read/offer does not contain the booked ride");
                    if (call.Name == "ProposeRideCancellation")
                    {
                        Check(ValidRide(action.Ride) && action.Ride!.Status is "Booked" or "DriverAssigned",
                            "cancellation offer lacks a cancellable ride");
                        if (expectedRide is not null)
                            Check(action.Ride?.Id == expectedRide.Id, "cancellation offer selected a different ride");
                    }
                    else
                        Check(ValidRide(action.Ride) || rides.Count > 0 && rides.All(ValidRide),
                            "ride status/history has no valid returned rides");
                }
                break;
            case ConciergeWeatherToolResult weather:
                Check(weather.Status == "Available" && weather.LocalDate is not null
                    && !string.IsNullOrWhiteSpace(weather.Location) && !string.IsNullOrWhiteSpace(weather.Conditions),
                    $"weather lacks available dated/place conditions ({weather.Status})");
                if (scenario.CurrentWeather)
                {
                    Check(ArgumentString(call, "period") == "current", "current weather did not request period=current");
                    Check(weather.CurrentTemperatureFahrenheit is not null && weather.ObservedAt is not null,
                        "current weather lacks an actual observation");
                }
                else
                    Check(weather.HighFahrenheit is not null && weather.LowFahrenheit is not null,
                        "forecast lacks returned high/low");
                if (scenario.Place is not null)
                    Check(weather.Location?.Contains(scenario.Place, StringComparison.OrdinalIgnoreCase) == true,
                        $"weather returned wrong place (expected {scenario.Place})");
                break;
            case ConciergeRouteToolResult route:
                Check(route.Status == "Available" && route.DistanceMeters is > 0 && route.EstimatedDurationSeconds is > 0
                    && route.Origin is not null && route.Destination is not null && !string.IsNullOrWhiteSpace(route.WebSummary),
                    "route lacks available endpoints, distance, duration and summary");
                break;
            case ConciergeNearbyPlacesToolResult nearby:
                var nearbyPlaces = nearby.Places ?? [];
                if (scenario.FocusedRecommendation)
                {
                    Check(ArgumentString(call, "presentation") == "nearest",
                        "singular recommendation did not request presentation=nearest");
                    Check(nearbyPlaces.Count > 0 && nearbyPlaces[0].Latitude is not null
                        && nearbyPlaces[0].Longitude is not null && !string.IsNullOrWhiteSpace(nearbyPlaces[0].Address)
                        && nearbyPlaces.Count(place => place.Name == nearbyPlaces[0].Name) == 1,
                        "recommendation lacks a unique structured endpoint with address and coordinates");
                }
                Check(nearby.Status == "Available" && nearby.Category == scenario.NearbyCategory
                    && nearbyPlaces.Count > 0 && nearbyPlaces.All(p => !string.IsNullOrWhiteSpace(p.Name)
                        && double.IsFinite(p.DistanceMiles) && p.DistanceMiles >= 0)
                    && nearbyPlaces.Select(p => p.DistanceMiles).SequenceEqual(nearbyPlaces.Select(p => p.DistanceMiles).Order()),
                    "nearby result lacks available named places of requested category");
                if (scenario.NearbyAlternatives)
                {
                    var spokenNames = nearbyPlaces.Where(place => !string.IsNullOrWhiteSpace(place.Name))
                        .Select(place => place.Name.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Where(name => evidence.Answer.Contains(name, StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    Check(spokenNames.Length >= 2,
                        "coffee alternatives turn did not present at least two distinct structured nearby names");
                    Check(SelectSpokenNonFirstUniquePlace(nearby, evidence.Answer) is not null,
                        "coffee alternatives did not present a uniquely named non-first structured result");
                }
                if (scenario.NearestProposal)
                {
                    using var arguments = JsonDocument.Parse(call.Arguments);
                    Check(arguments.RootElement.TryGetProperty("forRideProposal", out var flag) && flag.ValueKind == JsonValueKind.True,
                        "nearest ride lookup did not set forRideProposal=true");
                    Check(proposal?.Proposal is { } nearestDraft && nearbyPlaces.Count > 0
                        && !string.IsNullOrWhiteSpace(nearbyPlaces[0].Name)
                        && nearestDraft.Dropoff.Name.Contains(nearbyPlaces[0].Name, StringComparison.OrdinalIgnoreCase),
                        "proposal does not target the nearest returned place");
                }
                break;
            case ConciergePlaceOpeningHoursToolResult hours:
                Check(hours.Status == "Available" && !string.IsNullOrWhiteSpace(hours.PlaceName)
                    && !string.IsNullOrWhiteSpace(hours.OpeningHours), "named opening hours are not available");
                break;
        }
    }
    if (scenario.ContinuitySelection)
        Check(scenario.SelectedNearbyPlace is { } selected
            && evidence.Answer.Contains(selected.Name, StringComparison.OrdinalIgnoreCase),
            "selection turn did not acknowledge the selected structured place by name");
    if (scenario.ContinuityOffer)
    {
        var selected = scenario.Requires is { } selectedKey && state.TryGetValue(selectedKey, out var selectedState)
            ? selectedState.SelectedNearbyPlace : null;
        var draftPlace = proposal?.Proposal?.Dropoff;
        Check(selected is not null && draftPlace is not null
            && (StringComparer.OrdinalIgnoreCase.Equals(draftPlace.Name, selected.Name.Trim())
                || StringComparer.OrdinalIgnoreCase.Equals(draftPlace.Name, $"{selected.Name}, {selected.Address}")),
            "ride proposal destination does not match the selected canonical nearby place");
        if (selected is { Latitude: { } latitude, Longitude: { } longitude } && draftPlace is not null)
            Check(Math.Abs(draftPlace.Latitude - latitude) <= 0.0001
                && Math.Abs(draftPlace.Longitude - longitude) <= 0.0001,
                "ride proposal coordinates do not match the selected nearby result");
        var proposalCall = calls.FirstOrDefault(call => call.Name == "ProposeRide");
        var requestedDropoff = proposalCall is null ? null : ArgumentString(proposalCall, "dropoff");
        Check(selected is not null && IsCanonicalPlaceInput(requestedDropoff, selected),
            "ProposeRide did not receive the selected place name/address; distance prose is not a canonical handoff");
        Check(!Regex.IsMatch(requestedDropoff ?? "", @"\b\d+(?:\.\d+)?\s*(?:miles?|mi)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            "ProposeRide destination contains distance prose");
        Check(evidence.Answer.Contains("No ride has been booked", StringComparison.OrdinalIgnoreCase),
            "ride offer omitted the no-booking-before-confirmation guard");
        if (selected?.Address is { } address)
            Check(evidence.Answer.Contains(address, StringComparison.OrdinalIgnoreCase),
                "ride offer omitted the selected branch address");
        Check(evidence.Answer.TrimEnd().EndsWith('?')
            && Regex.IsMatch(evidence.Answer, @"\b(?:shall i|would you like me to|should i|can i)\b.*\b(?:book|arrange|confirm)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            "ride offer did not end with an explicit confirmation question");
        if (proposal?.ExistingRides is { Count: > 0 })
            Check(evidence.Answer.Contains("already", StringComparison.OrdinalIgnoreCase)
                || evidence.Answer.Contains("existing", StringComparison.OrdinalIgnoreCase),
                "ride offer omitted the active-ride warning");
    }
    if (scenario.WeatherDates is { } dates)
        Check(values.OfType<ConciergeWeatherToolResult>().Select(w => w.LocalDate).SequenceEqual(dates.Select(d => (DateOnly?)d)),
            $"weather dates/order differ from {string.Join(", ", dates)}");

    // State validity is deliberately captured BEFORE presentation assertions. A formatting bug
    // should fail the gate, but must not hide valid booking/cancellation coverage downstream.
    var stateValid = report.FailureCount == beforeStateChecks;
    state.TryGetValue(scenario.Requires ?? "", out var priorState);
    var resulting = new Prerequisite(stateValid, proposal?.Proposal ?? offered?.Proposal,
        booking ?? expectedRide ?? offered?.Ride, cancellation,
        scenario.NearbyAlternatives || scenario.FocusedRecommendation
            ? values.OfType<ConciergeNearbyPlacesToolResult>().FirstOrDefault() : priorState?.NearbyPlaces,
        scenario.NearbyAlternatives || scenario.FocusedRecommendation ? evidence.Answer : priorState?.NearbyAnswer,
        scenario.FocusedRecommendation ? values.OfType<ConciergeNearbyPlacesToolResult>().FirstOrDefault()?.Places.FirstOrDefault()
            : scenario.SelectedNearbyPlace ?? priorState?.SelectedNearbyPlace);
    foreach (var phrase in scenario.Mentions)
        Check(phrase.Split('|').Any(option => evidence.Answer.Contains(option, StringComparison.OrdinalIgnoreCase)),
            $"answer does not mention '{phrase}'");
    if (stateValid)
    {
        CheckAnswerFacts(scenario, calls, values, evidence.Answer, Check, expectedRide?.Id,
            offered?.Proposal ?? matchedProposal(scenario.MatchRide));
        if (scenario.VoiceOrdinal is not null && resulting.SelectedNearbyPlace is { } spokenPlace
            && calls.Any(call => call.Name is "ConfirmRide" or "GetRideStatus" or "GetRideHistory"))
        {
            Check(evidence.Answer.Contains(spokenPlace.Name, StringComparison.OrdinalIgnoreCase),
                "spoken receipt/status omitted the trusted venue name");
            Check(string.IsNullOrWhiteSpace(spokenPlace.Address)
                || !evidence.Answer.Contains(spokenPlace.Address, StringComparison.OrdinalIgnoreCase),
                "spoken receipt/status repeated the address already established in the offer");
            if (resulting.Proposal?.Immediate == true && resulting.Ride is { } spokenRide)
                Check(!evidence.Answer.Contains(HotelTime(spokenRide.PickupAt).ToString("dddd, MMMM d", CultureInfo.InvariantCulture),
                    StringComparison.OrdinalIgnoreCase), "spoken immediate receipt/status repeated the full pickup date");
        }
    }
    else
        report.BlockedCheck($"{label}: returned-fact presentation assertions blocked by invalid action outcome");
    if (scenario.Fare)
        Check(!evidence.Answer.TrimEnd().EndsWith('?')
            && !evidence.Answer.Contains("let me know if", StringComparison.OrdinalIgnoreCase)
            && !evidence.Answer.Contains("would you like", StringComparison.OrdinalIgnoreCase), "fare reply adds unsolicited follow-up");
    if (scenario.ClosedBooking)
        Check(!evidence.Answer.Contains("No ride has been booked", StringComparison.OrdinalIgnoreCase),
            "closed-booking acknowledgement/revision falsely describes a fresh offer");
    foreach (var forbidden in new[] { "+00:00", "UTC", "couldn't safely fit", "within the allowed steps", "simulat", "United States of America" })
        Check(!evidence.Answer.Contains(forbidden, StringComparison.OrdinalIgnoreCase), $"answer contains '{forbidden}'");
    Check(!Regex.IsMatch(evidence.Answer, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-"), "answer exposes an opaque ID");
    return resulting;

    ConciergeRideProposalDraft? matchedProposal(string? key) =>
        key is not null && state.TryGetValue(key, out var stored) ? stored.Proposal : null;
}

static bool ValidProposal(ConciergeRideProposalDraft? p) => p is not null
    && ValidPlace(p.Pickup) && ValidPlace(p.Dropoff)
    && double.IsFinite(p.RouteDurationSeconds) && p.RouteDurationSeconds > 0
    && (p.Immediate ? p.PickupAtUtc is null : p.PickupAtUtc is not null);
static bool ValidRide(RideSummary? r) => r is not null && r.Id != Guid.Empty
    && r.Status is "Booked" or "DriverAssigned" or "PickedUp" or "Completed" or "Cancelled"
    && !string.IsNullOrWhiteSpace(r.Pickup) && !string.IsNullOrWhiteSpace(r.Dropoff) && r.PickupAt != default;
static bool ValidPlace(ConciergeRidePlace? p) => p is not null && !string.IsNullOrWhiteSpace(p.Name)
    && double.IsFinite(p.Latitude) && p.Latitude is >= -90 and <= 90
    && double.IsFinite(p.Longitude) && p.Longitude is >= -180 and <= 180;
static ConciergeNearbyPlace? SelectSpokenNonFirstUniquePlace(
    ConciergeNearbyPlacesToolResult? nearby, string? answer)
{
    if (nearby?.Places is null || string.IsNullOrWhiteSpace(answer)) return null;
    return nearby.Places.Select((place, index) => (place, index))
        .Where(item => item.index > 0 && !string.IsNullOrWhiteSpace(item.place.Name))
        .Where(item => nearby.Places.Count(other => !string.IsNullOrWhiteSpace(other.Name)
            && StringComparer.OrdinalIgnoreCase.Equals(other.Name.Trim(), item.place.Name.Trim())) == 1)
        .Where(item => answer.Contains(item.place.Name.Trim(), StringComparison.OrdinalIgnoreCase))
        .Select(item => item.place)
        .FirstOrDefault();
}
static bool IsCanonicalPlaceInput(string? input, ConciergeNearbyPlace place)
{
    if (string.IsNullOrWhiteSpace(input)) return false;
    var value = input.Trim();
    return StringComparer.OrdinalIgnoreCase.Equals(value, place.Name.Trim())
        || place.Address is not null && (StringComparer.OrdinalIgnoreCase.Equals(value, place.Address.Trim())
            || StringComparer.OrdinalIgnoreCase.Equals(value, $"{place.Name}, {place.Address}".Trim()));
}

static void CheckAnswerFacts(Scenario scenario, IReadOnlyList<ToolCall> calls, object?[] values,
    string answer, Action<bool, string> check, Guid? expectedRideId = null, ConciergeRideProposalDraft? offered = null)
{
    void Contains(string? fact, string name) =>
        check(!string.IsNullOrWhiteSpace(fact) && answer.Contains(fact, StringComparison.OrdinalIgnoreCase), $"answer omits returned {name}: {fact}");
    void Number(double? value, string name)
    {
        if (value is not { } number) return;
        var rounded = Math.Round(number, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
        check(Regex.IsMatch(answer, $@"(?<![\d]){Regex.Escape(rounded)}(?![\d])"), $"answer omits returned {name} ({rounded})");
    }
    for (var index = 0; index < values.Length; index++)
    {
        switch (values[index])
        {
            case ConciergeWeatherToolResult w:
                Contains(ShortPlace(w.Location), "weather location");
                Contains(w.Conditions, "weather conditions");
                if (scenario.CurrentWeather) Number(w.CurrentTemperatureFahrenheit, "observed temperature");
                else { Number(w.HighFahrenheit, "forecast high"); Number(w.LowFahrenheit, "forecast low"); }
                break;
            case ConciergeRouteToolResult r:
                Number(r.DistanceMeters / 1609.344, "route miles");
                Number(r.EstimatedDurationSeconds is { } seconds ? Math.Max(1, Math.Ceiling(seconds / 60)) : null, "route minutes");
                Contains(ShortPlace(r.Destination?.Name), "route destination");
                break;
            case ConciergeNearbyPlacesToolResult n when !scenario.NearestProposal:
                Contains(n.Places[0].Name, "nearest place name");
                Contains(n.Places[0].DistanceMiles < 0.1 ? "less than 0.1" : n.Places[0].DistanceMiles.ToString("0.0", CultureInfo.InvariantCulture), "nearest distance");
                break;
            case ConciergePlaceOpeningHoursToolResult h:
                Contains(h.PlaceName, "opening-hours place");
                // Assert rendered provider hours, not a hardcoded restaurant schedule.
                var times = Regex.Matches(h.OpeningHours!, @"\b(\d{2}):(\d{2})\b");
                if (times.Count == 0)
                    check(answer.Contains(h.OpeningHours!, StringComparison.OrdinalIgnoreCase)
                        || h.OpeningHours == "24/7" && answer.Contains("24", StringComparison.Ordinal), "answer omits returned hours");
                foreach (Match time in times)
                {
                    var hour = int.Parse(time.Groups[1].Value, CultureInfo.InvariantCulture);
                    var minute = int.Parse(time.Groups[2].Value, CultureInfo.InvariantCulture);
                    var clock = new TimeOnly(hour % 24, minute).ToString(minute == 0 ? "h tt" : "h:mm tt", CultureInfo.InvariantCulture);
                    check(answer.Contains(clock, StringComparison.OrdinalIgnoreCase)
                        || answer.Contains(time.Value, StringComparison.Ordinal), $"answer omits provider opening time {clock}");
                }
                break;
            case ConciergeRideProposalToolResult { Proposal: { } p }:
                Contains(ShortPlace(p.Dropoff.Name), "offered destination");
                if (!p.Immediate) Contains(p.PickupAtUtc is { } at ? LocalTime(at) : null, "offered pickup time");
                if (scenario.WarnExisting)
                    check(answer.Contains("already", StringComparison.OrdinalIgnoreCase)
                        || answer.Contains("existing", StringComparison.OrdinalIgnoreCase), "answer omits active-ride warning");
                break;
            // A read that only selects the ride for a later cancellation offer in the same turn is
            // intermediate; the guest-facing facts are the offer's, checked on that later call.
            case ConciergeRideActionResult when calls[index].Name is "GetRideStatus" or "GetRideHistory"
                && calls.Skip(index + 1).Any(c => c.Name == "ProposeRideCancellation"):
                break;
            case ConciergeRideActionResult a:
                // Render-check the ride the scenario is about, not whichever history row sorts first.
                var ride = (expectedRideId is { } wanted
                    ? (a.Ride?.Id == wanted ? a.Ride : a.Rides?.FirstOrDefault(r => r.Id == wanted)) : null)
                    ?? a.Ride ?? a.Rides?.FirstOrDefault();
                if (ride is not null)
                {
                    Contains(ShortPlace(ride.Dropoff), "ride destination");
                    var immediateVoice = scenario.VoiceOrdinal is not null && offered?.Immediate == true;
                    if (calls[index].Name == "ConfirmRide")
                        Contains(immediateVoice ? "now" : LocalTime(ride.PickupAt), "booked pickup");
                    if (calls[index].Name is "GetRideStatus" or "GetRideHistory")
                    {
                        var status = ride.Status switch
                        {
                            "Booked" => "booked",
                            "DriverAssigned" => "driver assigned",
                            "PickedUp" => "in progress",
                            "Completed" => "complete",
                            "Cancelled" => "canceled",
                            _ => ride.Status,
                        };
                        Contains(status, "ride status");
                        if (ride.Status is "PickedUp" or "Completed" && ride.ArrivalAt is { } arrival)
                            Contains(LocalTime(arrival), "arrival estimate");
                        if (!immediateVoice && ride.Status is "Booked" or "DriverAssigned")
                            Contains(LocalTime(ride.PickupAt), "scheduled pickup");
                    }
                }
                break;
        }
    }
    if (values.Any(v => v is ConciergeWeatherToolResult or ConciergeRouteToolResult))
        check(Regex.IsMatch(answer.Trim(), @"[.!?]$"), "weather/route reply is not a complete sentence");
}

static DateTimeOffset HotelTime(DateTimeOffset time) => TimeZoneInfo.ConvertTime(
    time, TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
static string LocalTime(DateTimeOffset time) => HotelTime(time).ToString("h:mm tt", CultureInfo.InvariantCulture);
static string? ShortPlace(string? place) => place?.Split(',')[0].Trim();
static string TurnId(Guid session, Scenario scenario) => scenario.VoiceOrdinal is { } ordinal
    ? $"v:{session:N}:1:{ordinal}:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(scenario.Message))).ToLowerInvariant()}"
    : $"step6-{Guid.NewGuid():N}";

static string? ArgumentString(ToolCall call, string key)
{
    using var arguments = JsonDocument.Parse(call.Arguments);
    return arguments.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

static DecodedTool Decode(ToolCall call)
{
    if (call.Failed) return new(null, null, "activity failed or has no completed outcome");
    try
    {
        using var envelope = JsonDocument.Parse(call.Result);
        var root = envelope.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("result", out var payload)
            || payload.ValueKind != JsonValueKind.String)
            return new(null, null, "InvokeFunction envelope requires a string result");
        var json = payload.GetString()!;
        object? value = call.Name switch
        {
            "ProposeRide" => JsonSerializer.Deserialize<ConciergeRideProposalToolResult>(json, JsonSerializerOptions.Web),
            "ConfirmRide" or "GetRideHistory" or "GetRideStatus" or "ProposeRideCancellation" or "ConfirmRideCancellation" =>
                JsonSerializer.Deserialize<ConciergeRideActionResult>(json, JsonSerializerOptions.Web),
            "GetWeather" => JsonSerializer.Deserialize<ConciergeWeatherToolResult>(json, JsonSerializerOptions.Web),
            "GetRoute" => JsonSerializer.Deserialize<ConciergeRouteToolResult>(json, JsonSerializerOptions.Web),
            "FindNearbyPlaces" => JsonSerializer.Deserialize<ConciergeNearbyPlacesToolResult>(json, JsonSerializerOptions.Web),
            "GetPlaceOpeningHours" => JsonSerializer.Deserialize<ConciergePlaceOpeningHoursToolResult>(json, JsonSerializerOptions.Web),
            _ => null,
        };
        return new(value, json, value is null ? "null or unsupported tool result" : null);
    }
    catch (Exception exception) when (exception is JsonException or NotSupportedException)
    {
        return new(null, null, $"malformed tool result ({exception.GetType().Name})");
    }
}

// Use the admitted turn and its completed update, not a process-wide set of unseen activity IDs.
// Pin the run as well: event IDs restart after Continue-As-New. An absent boundary fails closed.
static TurnEvidence ReadTurnEvidence(IEnumerable<HistoryEvent> source, string runId, string turnId, string answer)
{
    var events = source.OrderBy(e => e.EventId).ToArray();
    var accepted = events.Where(e =>
    {
        var payloads = e.WorkflowExecutionUpdateAcceptedEventAttributes?.AcceptedRequest?.Input?.Args?.Payloads_;
        if (payloads is not { Count: > 0 }) return false;
        using var json = JsonDocument.Parse(payloads[0].Data.ToStringUtf8());
        return json.RootElement.TryGetProperty("turnId", out var id) && id.GetString() == turnId;
    }).ToArray();
    if (accepted.Length != 1)
        return TurnEvidence.Uncertain($"run {runId}: expected one accepted update for {turnId}, got {accepted.Length}");
    var start = accepted[0].EventId;
    var completed = events.Where(e => e.WorkflowExecutionUpdateCompletedEventAttributes?.AcceptedEventId == start).ToArray();
    if (completed.Length != 1 || completed[0].WorkflowExecutionUpdateCompletedEventAttributes.Outcome?.Success is null)
        return TurnEvidence.Uncertain($"run {runId}: turn lacks a successful completed update");
    var end = completed[0].EventId;
    var window = events.Where(e => e.EventId > start && e.EventId < end).ToArray();
    var scheduled = window.Where(e => e.EventType == EventType.ActivityTaskScheduled).ToArray();
    var calls = new List<ToolCall>();
    foreach (var e in scheduled.Where(e =>
        e.ActivityTaskScheduledEventAttributes.ActivityType.Name == "TemporalCommunity.Extensions.AI.InvokeFunction"))
    {
        var attributes = e.ActivityTaskScheduledEventAttributes;
        using var input = JsonDocument.Parse(attributes.Input.Payloads_[0].Data.ToStringUtf8());
        var name = input.RootElement.GetProperty("functionName").GetString()!;
        var arguments = input.RootElement.TryGetProperty("arguments", out var args) ? StripTrusted(args) : "{}";
        var outcome = window.FirstOrDefault(item =>
            item.ActivityTaskCompletedEventAttributes?.ScheduledEventId == e.EventId);
        var payload = outcome?.ActivityTaskCompletedEventAttributes?.Result?.Payloads_;
        calls.Add(new(name, arguments, payload is { Count: > 0 } ? payload[0].Data.ToStringUtf8() : "", payload is not { Count: > 0 }));
    }
    var toolRounds = scheduled.Where(e => e.ActivityTaskScheduledEventAttributes.ActivityType.Name
            == "TemporalCommunity.Extensions.AI.InvokeFunction")
        .Select(e => e.ActivityTaskScheduledEventAttributes.WorkflowTaskCompletedEventId).Distinct().Count();
    var models = scheduled.Count(e => e.ActivityTaskScheduledEventAttributes.ActivityType.Name
        == "TemporalCommunity.Extensions.AI.GetResponse");
    return new(answer, calls, models, toolRounds, runId, start, end);
}

static string StripTrusted(JsonElement arguments) => JsonSerializer.Serialize(
    arguments.EnumerateObject().Where(p => !p.Name.StartsWith("__", StringComparison.Ordinal))
        .ToDictionary(p => p.Name, p => p.Value));
static string Clip(string value, int length) => value.Length <= length ? value : value[..length] + "…";

static async Task<int> RunSelfTestsAsync()
{
    var passed = 0;
    var failed = 0;
    async Task Test(string name, Func<Task> test)
    {
        try { await test(); passed++; Console.WriteLine($"PASS  {name}"); }
        catch (Exception exception) { failed++; Console.Error.WriteLine($"FAIL  {name}: {exception.Message}"); }
    }
    void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    ToolCall Encoded(string name, object? result, string arguments = "{}") =>
        new(name, arguments, JsonSerializer.Serialize(new { result = JsonSerializer.Serialize(result, JsonSerializerOptions.Web), hasStateReplacement = false }), false);
    var draft = new ConciergeRideProposalDraft(new("Hotel ZGY", 25, -80), new("South Beach", 25.7, -80.1), true, null, 600);
    var ride = new RideSummary(Guid.NewGuid(), "Booked", "Hotel ZGY", "South Beach",
        new DateTimeOffset(2026, 10, 30, 14, 0, 0, TimeSpan.Zero), null, null, null);
    ToolCall Proposal(string status = "Available", ConciergeRideProposalDraft? proposal = null) =>
        Encoded("ProposeRide", new ConciergeRideProposalToolResult(status, null, [], proposal, "route", "route", []), """{"immediate":true}""");
    TurnEvidence Evidence(params ToolCall[] calls) => new("No ride has been booked to South Beach.", calls, 1, calls.Length == 0 ? 0 : 1, "offline-run", 10, 30);
    Scenario[] Chain() =>
    [
        new("offer", ["ProposeRide"], [], Provides: "offer"),
        new("confirm", ["ConfirmRide"], [], Requires: "offer", Provides: "booked"),
        new("cancel offer", ["ProposeRideCancellation"], [], Requires: "booked", Provides: "cancel"),
        new("cancel", ["ConfirmRideCancellation"], [], Requires: "cancel", MatchRide: "booked"),
    ];
    await Test("Available draft unlocks confirm; presentation failure is not state failure", async () =>
    {
        var report = new CheckReport();
        var sent = 0;
        await RunGroupAsync(new("valid", Chain()[..2]), scenario =>
        {
            sent++;
            return Task.FromResult(scenario.Message == "offer"
                ? Evidence(Proposal(proposal: draft)) with { Answer = "formatting defect" }
                : Evidence(Encoded("ConfirmRide", new ConciergeRideActionResult(true, null, ride, []))) with
                    { Answer = "South Beach pickup at 10:00 AM." });
        }, report);
        Require(sent == 2 && report.Skipped == 0 && report.FailureCount > 0, "valid proposal was blocked by presentation failure");
    });
    foreach (var status in new[] { "Unavailable", "InvalidSchedule", "AlreadyBooked", "ActiveRidesUnavailable", "NeedsClarification", "Available" })
        await Test($"{status} without a draft blocks booking and cancellation", async () =>
        {
            var report = new CheckReport();
            var sent = 0;
            await RunGroupAsync(new("no-offer", Chain()), _ => { sent++; return Task.FromResult(Evidence(Proposal(status))); }, report);
            Require(sent == 1 && report.Failed == 1 && report.Skipped == 3 && !report.Success, "missing proposal admitted descendants or greened");
        });
    foreach (var bad in new[]
    {
        new ToolCall("ProposeRide", "{}", """{"result":"null","hasStateReplacement":false}""", false),
        new ToolCall("ProposeRide", "{}", """{"result":"{broken","hasStateReplacement":false}""", false),
        new ToolCall("ProposeRide", "{}", """{"status":"Available","proposal":{}}""", false),
        new ToolCall("ProposeRide", "{}", """{"result":null}""", false),
        new ToolCall("ProposeRide", "{}", """{"error":"Available"}""", false),
        new ToolCall("ProposeRide", "{}", "activity exception", true),
    })
        await Test($"decode fails closed: {bad.Result}", async () =>
        {
            var report = new CheckReport();
            var sent = 0;
            await RunGroupAsync(new("malformed", Chain()), _ => { sent++; return Task.FromResult(Evidence(bad)); }, report);
            Require(sent == 1 && report.Skipped == 3 && !report.Success, "malformed/error result admitted confirmation");
        });
    await Test("missing booking blocks cancellation; independent groups still run", async () =>
    {
        var report = new CheckReport();
        var sent = 0;
        await RunGroupAsync(new("booking-failure", Chain()), scenario =>
        {
            sent++;
            return Task.FromResult(scenario.Message == "offer" ? Evidence(Proposal(proposal: draft))
                : Evidence(Encoded("ConfirmRide", new ConciergeRideActionResult(true, "uncertain", null, []))));
        }, report);
        await RunGroupAsync(new("independent", [new("hello", [], [])]), _ =>
        {
            sent++;
            return Task.FromResult(Evidence() with { Answer = "Hello." });
        }, report);
        Require(sent == 3 && report.Skipped == 2 && !report.Success, "failed booking leaked into cancellation or stopped independent group");
    });
    await Test("empty ride ID is not a booking", async () =>
    {
        var report = new CheckReport();
        await RunGroupAsync(new("empty-id", Chain()), scenario => Task.FromResult(scenario.Message == "offer"
            ? Evidence(Proposal(proposal: draft))
            : Evidence(Encoded("ConfirmRide", new ConciergeRideActionResult(true, null, ride with { Id = Guid.Empty }, [])))), report);
        Require(report.Skipped == 2 && !report.Success, "empty ID unlocked cancellation");
    });
    await Test("complete decoded offer, booking, cancellation offer and outcome can green", async () =>
    {
        var report = new CheckReport();
        await RunGroupAsync(new("positive-chain", Chain()), scenario => Task.FromResult(scenario.Message switch
        {
            "offer" => Evidence(Proposal(proposal: draft)),
            "confirm" => Evidence(Encoded("ConfirmRide", new ConciergeRideActionResult(true, null, ride, []))) with
                { Answer = "South Beach pickup at 10:00 AM." },
            "cancel offer" => Evidence(Encoded("ProposeRideCancellation", new ConciergeRideActionResult(true, null, ride, []))) with
                { Answer = "Cancel your South Beach ride?" },
            _ => Evidence(Encoded("ConfirmRideCancellation", new ConciergeRideActionResult(true, "Your ride is canceled.", null, [], ride.Id))) with
                { Answer = "Your ride is canceled." },
        }), report);
        Require(report.Success && report.Passed == 4, $"positive chain failed: {string.Join("; ", report.Failures)}");
    });
    await Test("missing cancellation offer blocks cancellation confirmation", async () =>
    {
        var report = new CheckReport();
        var sent = 0;
        await RunGroupAsync(new("no-cancel-offer", Chain()), scenario =>
        {
            sent++;
            return Task.FromResult(scenario.Message switch
            {
                "offer" => Evidence(Proposal(proposal: draft)),
                "confirm" => Evidence(Encoded("ConfirmRide", new ConciergeRideActionResult(true, null, ride, []))),
                _ => Evidence(Encoded("ProposeRideCancellation", new ConciergeRideActionResult(true, "unavailable", null, []))),
            });
        }, report);
        Require(sent == 3 && report.Skipped == 1 && !report.Success, "absent cancellation offer unlocked a side effect");
    });
    await Test("unconfirmed cancellation text is not a cancellation outcome", async () =>
    {
        var report = new CheckReport();
        var scenario = new Scenario("cancel", ["ConfirmRideCancellation"], [], Requires: "cancel", MatchRide: "booked");
        var state = new Dictionary<string, Prerequisite>
        {
            ["cancel"] = new(true, Ride: ride),
            ["booked"] = new(true, Ride: ride),
        };
        var result = ValidateTurn(scenario, Evidence(Encoded("ConfirmRideCancellation",
            new ConciergeRideActionResult(true, "canceled", null, []))), state, report, "cancel");
        Require(!result.Valid && !report.Success, "cancellation message alone was accepted");
        await Task.CompletedTask;
    });
    foreach (var status in new[] { "Unavailable", "InvalidSchedule", "AlreadyBooked" })
        await Test($"a non-null draft cannot override status {status}", async () =>
        {
            var report = new CheckReport();
            await RunGroupAsync(new("wrong-status", Chain()[..2]), _ =>
                Task.FromResult(Evidence(Proposal(status, draft))), report);
            Require(report.Skipped == 1 && !report.Success, "status was not checked exactly");
        });
    await Test("safe named-address clarification is distinct from booking coverage", async () =>
    {
        var clarification = Encoded("ProposeRide", new ConciergeRideProposalToolResult(
            "NeedsClarification", "Which location?", ["one", "two"], null, null, null, null));
        var groups = DefineGroups(new(2026, 10, 30));
        var report = new CheckReport();
        await RunGroupAsync(groups.First(g => g.Name == "named-address-safe-resolution"), _ =>
            Task.FromResult(Evidence(clarification) with { Answer = "Which location? No ride has been booked." }), report);
        Require(report.Success, "explicit safe clarification should pass the safety-only case");
        var bookingReport = new CheckReport();
        await RunGroupAsync(groups.First(g => g.Name == "named-address-booking"), _ =>
            Task.FromResult(Evidence(clarification)), bookingReport);
        Require(!bookingReport.Success && bookingReport.Skipped == 1, "clarification counted as successful booking coverage");
    });
    await Test("duplicate tools fail explicitly without aborting subsequent independent scenarios", async () =>
    {
        var report = new CheckReport();
        var sent = 0;
        await RunGroupAsync(new("duplicates", [Chain()[0], new("hello", [], [])]), scenario =>
        {
            sent++;
            return Task.FromResult(scenario.Message == "offer" ? Evidence(Proposal(proposal: draft), Proposal(proposal: draft)) : Evidence());
        }, report);
        Require(sent == 2 && report.Failures.Any(f => f.Contains("duplicate/unexpected tool")), "duplicate was not an explicit assertion");
    });
    await Test("uncertain HTTP quarantines group, not next group's turn boundary", async () =>
    {
        var report = new CheckReport();
        var sent = 0;
        await RunGroupAsync(new("uncertain", [new("first", [], []), new("second", [], [])]), _ =>
        {
            sent++;
            return Task.FromResult(TurnEvidence.Uncertain("HTTP 503; may still execute"));
        }, report);
        await RunGroupAsync(new("fresh", [new("fresh", [], [])]), _ =>
        {
            sent++;
            return Task.FromResult(Evidence());
        }, report);
        Require(sent == 2 && report.Skipped == 1 && report.Passed == 1 && !report.Success, "uncertain turn contaminated a later send");
    });
    await Test("blocked required coverage alone cannot green", () =>
    {
        var report = new CheckReport();
        report.Block("required gate not sent");
        Require(!report.Success, "blocked-only report greened");
        report.BlockedCheck("required fact assertion not evaluated");
        Require(!report.Success, "blocked assertion greened");
        return Task.CompletedTask;
    });
    await Test("place continuity validates structured non-first selection and canonical ride handoff", async () =>
    {
        // These deterministic fixtures exercise the validator only; they do not represent real-model coverage.
        var places = new ConciergeNearbyPlacesToolResult("Available", "cafe",
        [
            new("First Coffee", "10 First Street", 0.1, "provider-first", 25.01, -80.01),
            new("Second Coffee", "20 Second Street", 0.2, "provider-second", 25.02, -80.02),
            new("Third Coffee", "30 Third Street", 0.3, "provider-third", 25.03, -80.03),
        ]);
        var groups = DefineGroups(new(2026, 10, 30));
        var continuity = groups.Single(group => group.Name == "place-continuity");
        ToolCall NearbyCall(ConciergeNearbyPlacesToolResult result) => Encoded("FindNearbyPlaces", result);
        ToolCall OfferCall(ConciergeNearbyPlace place) => Encoded("ProposeRide",
            new ConciergeRideProposalToolResult("Available", null, [],
                new(new("Hotel ZGY", 25, -80), new($"{place.Name}, {place.Address}", place.Latitude!.Value, place.Longitude!.Value),
                    true, null, 600), "route", "route", []),
            JsonSerializer.Serialize(new { immediate = true, dropoff = place.Name }));
        TurnEvidence For(Scenario scenario, string? mode = null)
        {
            if (scenario.NearbyAlternatives)
                return Evidence(NearbyCall(places)) with
                {
                    ModelCalls = 1, ToolRounds = 1,
                    Answer = "First Coffee is 0.1 miles away; Second Coffee is 0.2 miles away. Which do you prefer?",
                };
            if (scenario.ContinuitySelection)
                return Evidence() with { ModelCalls = 1, ToolRounds = 0, Answer = mode == "missing" ? "Got it." : "Second Coffee, understood." };
            Require(scenario.Message == "Please offer me a ride to that coffee shop.",
                "continuity runner replaced the natural reference with an explicit destination");
            var selected = scenario.SelectedNearbyPlace ?? places.Places[1];
            var offer = OfferCall(mode == "wrong-branch" ? places.Places[0] : selected);
            return Evidence(mode == "missing-handoff" ? [] :
                    mode == "redundant-lookup" ? [NearbyCall(places), offer] : [offer]) with
            {
                ModelCalls = mode == "missing-handoff" ? 1 : mode == "redundant-lookup" ? 3 : 1,
                ToolRounds = mode == "missing-handoff" ? 0 : mode == "redundant-lookup" ? 2 : 1,
                Answer = mode == "missing-handoff"
                    ? "I need the street address before I can offer that ride. No ride has been booked."
                    : "Second Coffee, 20 Second Street is 0.2 miles away. No ride has been booked. Shall I arrange that ride now?",
            };
        }
        async Task<CheckReport> Run(string? mode = null)
        {
            var report = new CheckReport();
            await RunGroupAsync(continuity, scenario => Task.FromResult(For(scenario, mode)), report);
            return report;
        }
        var valid = await Run();
        Require(valid.Success && valid.Passed == 3,
            $"valid structured-place handoff failed: {string.Join("; ", valid.Failures)}");
        foreach (var (name, mode) in new[]
        {
            ("wrong branch", "wrong-branch"),
            ("redundant nearby lookup", "redundant-lookup"),
            ("missing selection handoff", "missing"),
            ("missing ride-offer handoff", "missing-handoff"),
        })
        {
            var failure = await Run(mode);
            Require(!failure.Success && failure.Failures.Count > 0,
                $"negative continuity selftest '{name}' greened");
        }
        await Task.CompletedTask;
    });
    await Test("representative ride conversation keeps real 'there', short receipts, one confirmation and explicit end", async () =>
    {
        var group = DefineGroups(new(2026, 10, 30)).Single(group => group.Name == "ride-conversation");
        var place = new ConciergeNearbyPlace("Healthi Rx Pharmacy", "2318 Northeast 2nd Avenue",
            0.1, "healthi", 25.799, -80.191);
        var endpoint = new ConciergeRidePlace($"{place.Name}, {place.Address}", place.Latitude!.Value, place.Longitude!.Value);
        var pharmacyDraft = draft with { Dropoff = endpoint, RouteDurationSeconds = 180 };
        var pharmacyRide = ride with { Dropoff = endpoint.Name };
        async Task<CheckReport> Run(string? fault = null)
        {
            var report = new CheckReport();
            await RunGroupAsync(group, scenario =>
            {
                if (scenario.FocusedRecommendation)
                    return Task.FromResult(Evidence(Encoded("FindNearbyPlaces",
                        new ConciergeNearbyPlacesToolResult("Available", "pharmacy", [place]),
                        """{"category":"pharmacy","presentation":"nearest"}""")) with
                        { Answer = $"{place.Name}, {place.Address} is 0.1 miles away." });
                if (scenario.ContinuityOffer)
                {
                    Require(scenario.Message == "Could you book me a ride there right now?",
                        "representative gate replaced 'there' with a named destination");
                    var offeredDraft = fault == "wrong-branch" ? pharmacyDraft with
                        { Dropoff = endpoint with { Latitude = 25.9 } } : pharmacyDraft;
                    return Task.FromResult(Evidence(Encoded("ProposeRide",
                        new ConciergeRideProposalToolResult("Available", null, [], offeredDraft, null, null, []),
                        JsonSerializer.Serialize(new { dropoff = endpoint.Name, immediate = true }))) with
                        { Answer = $"{endpoint.Name} is about a 3-minute ride from Hotel ZGY. It's complimentary. No ride has been booked. Shall I book it now?" });
                }
                if (scenario.Tools.Contains("ConfirmRide"))
                {
                    var call = Encoded("ConfirmRide", new ConciergeRideActionResult(true, null, pharmacyRide, []));
                    return Task.FromResult(Evidence(fault == "duplicate-confirm" ? [call, call] : [call]) with
                    {
                        Answer = fault == "repeated-address"
                            ? $"You're all set. A car will pick you up at Hotel ZGY now and take you to {endpoint.Name}."
                            : $"You're all set. A car will pick you up at Hotel ZGY now and take you to {place.Name}.",
                    });
                }
                if (scenario.MatchRide is not null)
                    return Task.FromResult(Evidence(Encoded("GetRideHistory",
                        new ConciergeRideActionResult(true, null, null,
                            [fault == "wrong-status-ride" ? pharmacyRide with { Id = Guid.NewGuid() } : pharmacyRide]))) with
                        { Answer = $"Your ride to {place.Name} is booked." });
                if (scenario.EndSession)
                    return Task.FromResult(fault == "end-failed" ? TurnEvidence.Uncertain("HTTP 503")
                        : new TurnEvidence("Session ended.", [], 0, 0, "", 0, 0));
                return Task.FromResult(Evidence(fault == "unrelated-rebooks"
                        ? [Encoded("ProposeRide", new ConciergeRideProposalToolResult("Available", null, [], pharmacyDraft, null, null, []),
                            JsonSerializer.Serialize(new { dropoff = endpoint.Name, immediate = true }))] : []) with
                    { Answer = "The gym is on the fourth floor and open 24 hours daily." });
            }, report);
            return report;
        }
        var valid = await Run();
        Require(valid.Success && valid.Passed == 6,
            $"representative conversation validator failed: {string.Join("; ", valid.Failures)}");
        foreach (var fault in new[] { "wrong-branch", "duplicate-confirm", "repeated-address", "wrong-status-ride", "end-failed", "unrelated-rebooks" })
        {
            var rejected = await Run(fault);
            Require(!rejected.Success && rejected.Failures.Count > 0, $"representative negative '{fault}' greened");
        }
    });
    await Test("focused/default definitions share scenarios and preserve cinema prefix", () =>
    {
        var groups = DefineGroups(new(2026, 10, 30));
        foreach (var mode in new[] { "nearest", "nearby", "weather-now", "place-continuity", "immediate-status", "ride-conversation", "voice-same-ordinal-revision" })
        {
            var selected = SelectGroups(groups, mode);
            Require(selected.Length == 1 && selected[0].Scenarios.All(s => groups.Any(g => g.Scenarios.Any(original => ReferenceEquals(s, original)))),
                $"{mode} copied or diverged from default definitions");
        }
        var nearby = groups.First(g => g.Name == "nearby").Scenarios;
        Require(nearby[6].NearbyCategory == "cinema" && nearby.Take(6).SelectMany(s => s.Tools).SequenceEqual(
            ["GetWeather", "GetWeather", "GetWeather", "GetRoute", "FindNearbyPlaces"]), "cinema lost its original prefix");
        Require(groups.Count(g => g.Name.StartsWith("postbooking-")) == 2, "acknowledgements do not have separate setups");
        var revision = groups.First(g => g.Name == "voice-same-ordinal-revision").Scenarios;
        var voiceSession = Guid.NewGuid();
        var originalId = TurnId(voiceSession, revision[1]).Split(':');
        var revisedId = TurnId(voiceSession, revision[2]).Split(':');
        Require(originalId.Take(4).SequenceEqual(revisedId.Take(4)) && originalId[4] != revisedId[4]
            && revisedId[4].Length == 64 && revision[2].Tools.Length == 0,
            "voice revision does not exercise same ordinal, different text hash without dispatch");
        return Task.CompletedTask;
    });
    await Test("warned additional offer and distinct later booking have valid prerequisites", () =>
    {
        var report = new CheckReport();
        var state = new Dictionary<string, Prerequisite> { ["booked"] = new(true, draft, ride) };
        var scenario = new Scenario("additional offer", ["ProposeRide"], [], Requires: "booked", Provides: "second-offer", WarnExisting: true);
        var offered = Encoded("ProposeRide", new ConciergeRideProposalToolResult(
            "Available", null, [], draft, "route", "route", [], ExistingRides: [ride]), """{"immediate":true}""");
        state["second-offer"] = ValidateTurn(scenario, Evidence(offered) with
            { Answer = "You already have an active ride to South Beach. This is an additional ride. No ride has been booked." }, state, report, "additional");
        ValidateTurn(new("confirm", ["ConfirmRide"], [], Requires: "second-offer", DifferentRide: "booked"),
            Evidence(Encoded("ConfirmRide", new ConciergeRideActionResult(true, null, ride with { Id = Guid.NewGuid() }, []))) with
                { Answer = "South Beach pickup at 10:00 AM." }, state, report, "confirmation");
        Require(report.Success && state["second-offer"].Valid, string.Join("; ", report.Failures));
        var missingWarning = new CheckReport();
        ValidateTurn(scenario, Evidence(Proposal(proposal: draft)), state, missingWarning, "no-warning");
        Require(!missingWarning.Success, "additional offer omitted existing ride evidence but greened");
        return Task.CompletedTask;
    });
    await Test("post-booking acknowledgement admits only a verified read-only lookup of the stored ride", () =>
    {
        var groups = DefineGroups(new(2026, 10, 30));
        var other = ride with { Id = Guid.NewGuid() };
        var inProgress = ride with { Status = "PickedUp", ArrivalAt = ride.PickupAt.AddMinutes(18) };
        ToolCall History(params RideSummary[] rides) => Encoded("GetRideHistory",
            new ConciergeRideActionResult(true, null, null, rides), """{"when":"upcoming"}""");
        const string rendered = "Here are your upcoming rides:\n\n1. Your ride to South Beach, Miami Beach is in progress and should arrive around 10:18 AM hotel time.";
        foreach (var name in new[] { "postbooking-yes", "postbooking-thanks", "voice-same-ordinal-revision" })
        {
            var ack = groups.First(g => g.Name == name).Scenarios[^1];
            Dictionary<string, Prerequisite> State() => new() { ["booked"] = new(true, draft, ride) };
            Prerequisite Run(TurnEvidence evidence, CheckReport report, Scenario? scenario = null) =>
                ValidateTurn(scenario ?? ack, evidence, State(), report, name);
            // Actual live shape: "Yes, please." -> GetRideHistory(upcoming) rendering the in-progress booked ride.
            var read = new CheckReport();
            Run(Evidence(History(other, inProgress)) with { Answer = rendered }, read);
            Require(read.Success, $"{name}: verified read-only status was rejected: {string.Join("; ", read.Failures)}");
            var none = new CheckReport();
            Run(Evidence() with { Answer = "You're welcome. Your South Beach ride is booked." }, none);
            Require(none.Success, $"{name}: no-tool acknowledgement rejected: {string.Join("; ", none.Failures)}");
            var status = new CheckReport();
            Run(Evidence(Encoded("GetRideStatus", new ConciergeRideActionResult(true, null, inProgress, []))) with { Answer = rendered }, status);
            Require(status.Success, $"{name}: verified status read rejected: {string.Join("; ", status.Failures)}");
            foreach (var (label, bad) in new (string, TurnEvidence)[]
            {
                ("unrelated history ride", Evidence(History(other with { Status = "PickedUp", ArrivalAt = inProgress.ArrivalAt })) with { Answer = rendered }),
                ("read facts not rendered", Evidence(History(inProgress)) with { Answer = "Your ride to South Beach is booked." }),
                ("new offer", Evidence(Proposal(proposal: draft)) with { Answer = rendered }),
                ("new booking", Evidence(Encoded("ConfirmRide", new ConciergeRideActionResult(true, null, other, []))) with { Answer = rendered }),
                ("cancellation offer", Evidence(Encoded("ProposeRideCancellation", new ConciergeRideActionResult(true, null, ride, []))) with { Answer = rendered }),
                ("read then side effect", Evidence(History(inProgress), Proposal(proposal: draft)) with { Answer = rendered }),
                ("repeated read", Evidence(History(inProgress), History(inProgress)) with { Answer = rendered }),
                ("unrelated tool", Evidence(Encoded("GetRoute", null)) with { Answer = rendered }),
            })
            {
                var failure = new CheckReport();
                try { Run(bad, failure); } catch (Exception) { failure.Fail("threw"); }
                Require(!failure.Success, $"{name}: {label} greened");
            }
            var unverified = new CheckReport();
            Run(Evidence(History(inProgress)) with { Answer = rendered }, unverified, ack with { MatchRide = null });
            Require(!unverified.Success, $"{name}: read alternative without a stored ride to verify greened");
        }
        return Task.CompletedTask;
    });
    await Test("cancellation-offer selection read is intermediate; offer must select the booked ride", () =>
    {
        var cancelOffer = DefineGroups(new(2026, 10, 30)).First(g => g.Name == "scheduled-cancellation").Scenarios[3];
        var other = ride with { Id = Guid.NewGuid(), PickupAt = ride.PickupAt.AddHours(3) };
        TurnEvidence Shape(RideSummary selected) => Evidence(
            Encoded("GetRideHistory", new ConciergeRideActionResult(true, null, null, [ride, other]), """{"when":"upcoming"}"""),
            Encoded("ProposeRideCancellation", new ConciergeRideActionResult(true, null, selected, []), $$"""{"rideId":"{{selected.Id}}"}""")) with
        {
            ModelCalls = 2, ToolRounds = 2,
            Answer = "I found your ride to South Beach, Miami Beach, scheduled for Friday, October 30 at 10:00 AM hotel time. Would you like me to cancel it? No cancellation has been made.",
        };
        var report = new CheckReport();
        var result = ValidateTurn(cancelOffer, Shape(ride), new() { ["booked"] = new(true, draft, ride) }, report, "cancel offer");
        Require(report.Success && result.Valid && result.Ride?.Id == ride.Id,
            $"actual cancellation-offer shape rejected: {string.Join("; ", report.Failures)}");
        var wrong = new CheckReport();
        var wrongResult = ValidateTurn(cancelOffer, Shape(other), new() { ["booked"] = new(true, draft, ride) }, wrong, "wrong selection");
        Require(!wrong.Success && !wrongResult.Valid && wrong.Failures.Any(f => f.Contains("different ride")),
            "cancellation offer for a different ride greened or still unlocked confirmation");
        var standalone = new CheckReport();
        ValidateTurn(new("Show me my upcoming rides.", ["GetRideHistory"], [], Requires: "booked", MatchRide: "booked"),
            Evidence(Encoded("GetRideHistory", new ConciergeRideActionResult(true, null, null, [ride]))) with
                { Answer = "Your ride to South Beach is scheduled for 10:00 AM." }, new() { ["booked"] = new(true, draft, ride) }, standalone, "history");
        Require(!standalone.Success && standalone.Failures.Any(f => f.Contains("ride status")),
            "a guest-facing history read no longer requires its rendered status");
        return Task.CompletedTask;
    });
    await Test("weather dates, current observation and returned numeric facts are asserted", () =>
    {
        var day = new DateOnly(2026, 10, 30);
        var weather = new ConciergeWeatherToolResult("Available", null, [], "Plantation, FL", day,
            "partly cloudy", 82.5, 70.5, 10, 20);
        var scenario = new Scenario("weather", ["GetWeather"], [], WeatherDates: [day], Place: "Plantation");
        var good = Evidence(Encoded("GetWeather", weather)) with
            { Answer = "Expect partly cloudy skies in Plantation, high 83°F and low 71°F." };
        var report = new CheckReport();
        ValidateTurn(scenario, good, [], report, "weather");
        Require(report.Success, string.Join("; ", report.Failures));
        foreach (var bad in new[]
        {
            good with { Calls = [Encoded("GetWeather", weather with { LocalDate = day.AddDays(1) })] },
            good with { Answer = "Expect partly cloudy skies in Plantation, high 80°F and low 60°F." },
        })
        {
            var failure = new CheckReport();
            ValidateTurn(scenario, bad, [], failure, "wrong weather");
            Require(!failure.Success, "wrong dates/returned facts greened");
        }
        var current = new CheckReport();
        ValidateTurn(scenario with { CurrentWeather = true }, good with
            { Calls = [Encoded("GetWeather", weather, """{"period":"current"}""")] }, [], current, "current");
        Require(!current.Success, "forecast accepted as current observation");
        return Task.CompletedTask;
    });
    await Test("route and opening-hours assertions use returned facts rather than provider constants", () =>
    {
        var route = new ConciergeRouteToolResult("Available", null, [], 3218.688, 121,
            "provider summary", "provider summary", [], [], new("Hotel ZGY", 25, -80), new("South Beach", 25.7, -80.1));
        var report = new CheckReport();
        ValidateTurn(new("route", ["GetRoute"], []), Evidence(Encoded("GetRoute", route)) with
            { Answer = "South Beach is about 2 miles away, roughly 3 minutes by car." }, [], report, "route");
        ValidateTurn(new("hours", ["GetPlaceOpeningHours"], []), Evidence(Encoded("GetPlaceOpeningHours",
            new ConciergePlaceOpeningHoursToolResult("Available", "Wayan Miami", "Mo-Su 17:30-22:00", []))) with
            { Answer = "Wayan Miami is open 5:30 PM–10 PM." }, [], report, "hours");
        Require(report.Success, string.Join("; ", report.Failures));
        return Task.CompletedTask;
    });
    await Test("model/tool budgets and ordinary-nearby one-model requirement cannot green", () =>
    {
        foreach (var bad in new[] { Evidence() with { ModelCalls = 4 }, Evidence() with { ToolRounds = 3 } })
        {
            var report = new CheckReport();
            ValidateTurn(new("hello", [], []), bad, [], report, "budget");
            Require(!report.Success, "exceeded budget greened");
        }
        var nearby = new CheckReport();
        ValidateTurn(new("pharmacy", ["FindNearbyPlaces"], [], NearbyCategory: "pharmacy"),
            Evidence(Encoded("FindNearbyPlaces", new ConciergeNearbyPlacesToolResult("Available", "pharmacy",
                [new("Pharmacy A", null, 0.3)]))) with { ModelCalls = 2, Answer = "Pharmacy A is 0.3 miles away." }, [], nearby, "nearby");
        Require(!nearby.Success, "ordinary nearby used a second model round");
        return Task.CompletedTask;
    });
    await Test("history attribution excludes prior and late activities, including reused event IDs across runs", () =>
    {
        Temporalio.Api.Common.V1.Payloads Payload(string json) => new()
        {
            Payloads_ = { new Temporalio.Api.Common.V1.Payload { Data = Google.Protobuf.ByteString.CopyFromUtf8(json) } },
        };
        HistoryEvent Accepted(long id, string turn) => new()
        {
            EventId = id,
            EventType = EventType.WorkflowExecutionUpdateAccepted,
            WorkflowExecutionUpdateAcceptedEventAttributes = new()
            {
                AcceptedRequest = new() { Input = new() { Args = Payload(JsonSerializer.Serialize(new { turnId = turn })) } },
            },
        };
        HistoryEvent Completed(long id, long accepted) => new()
        {
            EventId = id,
            EventType = EventType.WorkflowExecutionUpdateCompleted,
            WorkflowExecutionUpdateCompletedEventAttributes = new()
            {
                AcceptedEventId = accepted,
                Outcome = new() { Success = Payload("{}") },
            },
        };
        HistoryEvent Scheduled(long id, string activity, string function = "ProposeRide") => new()
        {
            EventId = id,
            EventType = EventType.ActivityTaskScheduled,
            ActivityTaskScheduledEventAttributes = new()
            {
                ActivityType = new() { Name = $"TemporalCommunity.Extensions.AI.{activity}" },
                WorkflowTaskCompletedEventId = 11,
                Input = Payload(JsonSerializer.Serialize(new { functionName = function, arguments = new { immediate = true } })),
            },
        };
        HistoryEvent ActivityCompleted(long id, long scheduled, string result) => new()
        {
            EventId = id,
            EventType = EventType.ActivityTaskCompleted,
            ActivityTaskCompletedEventAttributes = new() { ScheduledEventId = scheduled, Result = Payload(result) },
        };
        HistoryEvent[] events =
        [
            Accepted(1, "previous"), Scheduled(2, "InvokeFunction", "ConfirmRide"), Completed(4, 1),
            Accepted(10, "target"), Scheduled(12, "GetResponse"), Scheduled(15, "InvokeFunction"),
            ActivityCompleted(16, 15, Proposal(proposal: draft).Result), Completed(20, 10),
            Scheduled(21, "InvokeFunction", "ConfirmRide"), Scheduled(22, "GetResponse"),
        ];
        var evidence = ReadTurnEvidence(events, "run-A", "target", "answer");
        Require(evidence.Error is null && evidence.Calls.Count == 1 && evidence.Calls[0].Name == "ProposeRide"
            && evidence.ModelCalls == 1 && evidence.ToolRounds == 1 && evidence.StartEventId == 10 && evidence.EndEventId == 20,
            "turn evidence included another turn's activities");
        Require(Decode(evidence.Calls[0]).Value is ConciergeRideProposalToolResult { Status: "Available", Proposal: not null },
            "actual history envelope was not decoded");
        var reused = ReadTurnEvidence(events, "run-B", "target", "answer");
        Require(reused.RunId == "run-B" && reused.Calls.Count == 1, "unseen-event tracking dropped reused IDs in another run");
        Require(ReadTurnEvidence(events.Where(e => e.EventId != 20), "run-A", "target", "").Error is not null,
            "incomplete update boundary was attributed");
        Require(ReadTurnEvidence(events.Append(Accepted(23, "target")), "run-A", "target", "").Error is not null,
            "duplicate accepted turn was silently selected");
        return Task.CompletedTask;
    });
    Console.WriteLine($"Offline selftests: Passed: {passed}, Failed: {failed}");
    return failed == 0 ? 0 : 1;
}

static async Task SeedGuestAsync(string connectionString, Guid stayId, string familyName, string roomNumber)
{
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
    await using var transaction = await connection.BeginTransactionAsync();
    var guestId = Guid.NewGuid();
    var hotel = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    var today = HotelToday();
    DateTimeOffset Midnight(DateOnly date) => new(TimeZoneInfo.ConvertTimeToUtc(
        date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), hotel));
    await using (var command = new NpgsqlCommand(
        "INSERT INTO guests (guest_id, given_name, family_name) VALUES ($1, 'Live', $2);", connection, transaction))
    {
        command.Parameters.AddWithValue(guestId);
        command.Parameters.AddWithValue(familyName);
        await command.ExecuteNonQueryAsync();
    }
    await using (var command = new NpgsqlCommand(
        """
        INSERT INTO stays (
            stay_id, external_stay_key, guest_id, room_number,
            check_in_utc, check_out_utc, eligibility_ends_at_utc)
        VALUES ($1, $2, $3, $4, $5, $6, $7);
        """, connection, transaction))
    {
        command.Parameters.AddWithValue(stayId);
        command.Parameters.AddWithValue($"livecheck-{stayId:N}");
        command.Parameters.AddWithValue(guestId);
        command.Parameters.AddWithValue(roomNumber);
        command.Parameters.AddWithValue(Midnight(today));
        command.Parameters.AddWithValue(Midnight(today.AddDays(1)));
        command.Parameters.AddWithValue(Midnight(today.AddDays(2)));
        await command.ExecuteNonQueryAsync();
    }
    await transaction.CommitAsync();
}

static async Task<AspireRuntime> ReadRuntimeAsync(string repoRoot)
{
    var startInfo = new ProcessStartInfo("aspire")
    {
        WorkingDirectory = repoRoot, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
    };
    foreach (var argument in new[]
    {
        "describe", "--apphost", Path.Combine(repoRoot, "src", "Concierge.AppHost", "Concierge.AppHost.csproj"),
        "--format", "Json", "--non-interactive", "--nologo",
    })
        startInfo.ArgumentList.Add(argument);
    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start Aspire CLI.");
    var outputTask = process.StandardOutput.ReadToEndAsync();
    var errorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    var json = await outputTask;
    _ = await errorTask; // Resource environment values stay in memory, never console or files.
    if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(json))
        throw new InvalidOperationException("Aspire describe failed; the owner must start AppHost before running the live gate.");
    using var document = JsonDocument.Parse(json);
    var resources = document.RootElement.GetProperty("resources").EnumerateArray().ToArray();
    var environment = Resource(resources, "workflow-server").GetProperty("environment");
    var webUrl = Resource(resources, "web").GetProperty("urls").EnumerateArray()
        .Single(endpoint => endpoint.GetProperty("name").GetString() == "https").GetProperty("url").GetString()!;
    return new(RequiredValue(environment, "ConnectionStrings__concierge"), RequiredValue(environment, "TEMPORAL_ADDRESS"),
        RequiredValue(environment, "TEMPORAL_NAMESPACE"), webUrl,
        RequiredValue(Resource(resources, "ride-service").GetProperty("environment"), "ConnectionStrings__rides"));
}
static JsonElement Resource(JsonElement[] resources, string name) =>
    resources.SingleOrDefault(resource => resource.GetProperty("name").GetString() is { } resourceName
        && (resourceName == name || resourceName.StartsWith(name + "-", StringComparison.Ordinal)
            && !resourceName[(name.Length + 1)..].Contains('-'))) is { ValueKind: JsonValueKind.Object } found
        ? found : throw new InvalidOperationException($"Aspire resource '{name}' is not running.");
static string RequiredValue(JsonElement element, string name) =>
    element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(property.GetString())
        ? property.GetString()! : throw new InvalidOperationException($"Aspire resource setting '{name}' is unavailable.");
static string ScriptDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

sealed record AspireRuntime(
    string ConciergeConnectionString, string TemporalAddress, string TemporalNamespace, string WebUrl, string RideConnectionString);
sealed record ScenarioGroup(string Name, Scenario[] Scenarios);
sealed record Scenario(
    string Message, string[] Tools, string[] Mentions,
    string? Requires = null, string? Provides = null,
    string? MatchRide = null, string? MatchOffer = null, string? DifferentRide = null,
    string? NearbyCategory = null, bool NearestProposal = false,
    DateOnly[]? WeatherDates = null, string? Place = null, bool CurrentWeather = false,
    bool WarnExisting = false, bool SafeResolution = false, bool Fare = false,
    string? Destination = null, DateOnly? PickupDate = null, TimeOnly? PickupTime = null,
    int? VoiceOrdinal = null, bool ClosedBooking = false, string[]? ReadAlternative = null,
    bool NearbyAlternatives = false, bool ContinuitySelection = false, bool ContinuityOffer = false,
    ConciergeNearbyPlace? SelectedNearbyPlace = null, bool FocusedRecommendation = false, bool EndSession = false);
sealed record Prerequisite(
    bool Valid, ConciergeRideProposalDraft? Proposal = null, RideSummary? Ride = null, Guid? Cancelled = null,
    ConciergeNearbyPlacesToolResult? NearbyPlaces = null, string? NearbyAnswer = null,
    ConciergeNearbyPlace? SelectedNearbyPlace = null);
sealed record ToolCall(string Name, string Arguments, string Result, bool Failed);
sealed record DecodedTool(object? Value, string? Json, string? Error);
sealed record TurnEvidence(
    string Answer, IReadOnlyList<ToolCall> Calls, int ModelCalls, int ToolRounds,
    string RunId, long StartEventId, long EndEventId, string? Error = null)
{
    public static TurnEvidence Uncertain(string error) => new("", [], 0, 0, "", 0, 0, error);
}
sealed class CheckReport
{
    public List<string> Failures { get; } = [];
    public int Passed { get; private set; }
    public int Failed { get; private set; }
    public int Skipped { get; private set; }
    public int BlockedChecks { get; private set; }
    public int FailureCount => Failures.Count;
    public bool Success => Failed == 0 && Failures.Count == 0 && Skipped == 0 && BlockedChecks == 0;
    public string Summary => $"Passed: {Passed}, Failed: {Failed}, Blocked: {Skipped}, Blocked checks: {BlockedChecks}";
    public void Fail(string message) => Failures.Add(message);
    public void CompleteScenario(int before) { if (before == FailureCount) Passed++; else Failed++; }
    public void Block(string message) { Skipped++; Console.WriteLine($"BLOCKED  {message}"); }
    public void BlockedCheck(string message) { BlockedChecks++; Console.WriteLine($"BLOCKED  {message}"); }
}
