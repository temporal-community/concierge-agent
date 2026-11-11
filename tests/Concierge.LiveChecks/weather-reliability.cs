#:project ../../src/Concierge.WorkflowServer/Concierge.WorkflowServer.csproj
// The transitive OllamaSharp generator targets a newer compiler; it is not used by this check.
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property PublishAot=false

// Opt-in acceptance check for hotel weather answers. For every turn it verifies that GetWeather
// ran this turn, that the forecast dates it returned are the dates the phrasing asks for (resolved
// against the hotel-local calendar), and that the answer reports this turn's forecasts: each
// expected day's part of the answer gives that day's returned high, low and conditions, and every
// temperature in the answer comes from this turn's results, so a reused earlier answer fails.
// Runs several fresh stays, repeating every phrasing within each, so both new and long
// conversations are covered. Exits 1 if any turn fails.
// Usage: dotnet run --file weather-reliability.cs [stays] [rounds per stay]   (default 5 x 4)
//        dotnet run --file weather-reliability.cs selftest   (checks the answer checker offline)
// Aspire resource environment values stay in memory and are never written to the console.
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Concierge.Contracts;
using Concierge.WorkflowServer.Workflows;
using Npgsql;
using TemporalCommunity.Extensions.AI;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;

if (args is ["selftest"])
{
    return SelfTest();
}

var repoRoot = Path.GetFullPath(Path.Combine(ScriptDirectory(), "..", ".."));
var stage = "reading Aspire resource metadata";
var stays = args.Length > 0 && int.TryParse(args[0], out var parsedStays) && parsedStays > 0 ? parsedStays : 5;
var rounds = args.Length > 1 && int.TryParse(args[1], out var parsedRounds) && parsedRounds > 0 ? parsedRounds : 4;
var hotelZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
// Expected forecast dates per phrasing, from the hotel-local date when the turn is sent.
(string Phrase, Func<DateOnly, DateOnly[]> Dates, string? Place)[] phrasings =
[
    ("What's the weather for the next three days starting tomorrow?", today => [today.AddDays(1), today.AddDays(2), today.AddDays(3)], null),
    ("What's the weather like today?", today => [today], null),
    ("What's the weather for the next two days?", today => [today, today.AddDays(1)], null),
    ("Will it rain tomorrow?", today => [today.AddDays(1)], null),
    ("What's the forecast for this weekend?", WeekendDates, null),
    ("What's the forecast for this weekend in Key West?", WeekendDates, "Key West"),
];
var failures = phrasings.ToDictionary(item => item.Phrase, _ => new Dictionary<string, int>());
var turns = 0;
try
{
    var runtime = await ReadRuntimeAsync(repoRoot);
    stage = "connecting to Temporal";
    var temporalClient = await TemporalClient.ConnectAsync(new TemporalClientConnectOptions
    {
        TargetHost = runtime.TemporalAddress,
        Namespace = runtime.TemporalNamespace,
        DataConverter = DurableAIDataConverter.Instance,
    });

    for (var stay = 1; stay <= stays; stay++)
    {
        stage = $"seeding stay {stay}";
        var familyName = $"Livecheck{Random.Shared.Next(100_000, 999_999)}";
        var roomNumber = string.Empty;
        var stayId = Guid.NewGuid();
        for (var attempt = 0; ; attempt++)
        {
            // Fictional 9xxx rooms; earlier runs may still hold a room, which the overlap constraint rejects.
            roomNumber = Random.Shared.Next(9000, 10_000).ToString(CultureInfo.InvariantCulture);
            try
            {
                await SeedGuestAsync(runtime.ConciergeConnectionString, stayId, familyName, roomNumber);
                break;
            }
            catch (PostgresException exception) when (exception.SqlState == "23P01" && attempt < 5)
            {
            }
        }

        var workflowId = ConciergeStayWorkflowIds.ForStay(stayId);
        // Only the local Aspire dev certificate is accepted, and only for loopback.
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (request, _, _, _) => request.RequestUri?.IsLoopback == true,
        };
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri(runtime.WebUrl),
            // Above the workflow's 180s action-turn deadline so slow turns surface the workflow reply.
            Timeout = TimeSpan.FromSeconds(200),
        };

        stage = $"verifying the guest session for stay {stay}";
        var sessionResponse = await http.PostAsJsonAsync(
            "/api/concierge/sessions", new ConciergeVerificationRequest(familyName, roomNumber));
        if (!sessionResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Session verification returned {(int)sessionResponse.StatusCode}: {await sessionResponse.Content.ReadAsStringAsync()}");
        }

        var session = (await sessionResponse.Content.ReadFromJsonAsync<ConciergeSessionResponse>())!;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Credential);

        var seenScheduled = new HashSet<long>();
        for (var round = 1; round <= rounds; round++)
        {
            foreach (var (phrase, datesFor, place) in phrasings)
            {
                stage = $"stay {stay} round {round} ({phrase})";
                turns++;
                var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, hotelZone).DateTime);
                var expected = datesFor(today);
                using var turnResponse = await PostTurnWithTransientRetryAsync(
                    http,
                    new ConciergeTurnRequest($"weather-rel-{stay}-{round}-{Guid.NewGuid():N}", phrase, "web"));
                var body = await turnResponse.Content.ReadAsStringAsync();
                if (!turnResponse.IsSuccessStatusCode)
                {
                    Fail(phrase, "http", $"{stay}.{round} HTTP {(int)turnResponse.StatusCode}");
                    continue;
                }

                var answer = JsonSerializer.Deserialize<ConciergeTurnResult>(body, JsonSerializerOptions.Web)!.AssistantMessage;
                var weather = (await ReadNewToolCallsAsync(temporalClient, workflowId, seenScheduled))
                    .Where(call => call.Name == "GetWeather")
                    .ToArray();
                var problem = Check(today, expected, weather, answer, place);
                if (problem is null)
                {
                    Console.WriteLine($"OK    {stay}.{round} {phrase} => {string.Join(", ", expected.Select(Label(today)))}");
                }
                else
                {
                    Fail(phrase, problem.Value.Category, $"{stay}.{round} {problem.Value.Detail} | periods [{string.Join(", ", weather.Select(call => call.Arguments))}] | {Clip(answer, 200)}");
                }
            }
        }

        stage = $"ending stay {stay}";
        await http.PostAsync("/api/concierge/end", null);
        try
        {
            await temporalClient.GetWorkflowHandle(workflowId).TerminateAsync("Weather reliability cleanup");
        }
        catch
        {
            Console.Error.WriteLine("WARN  Could not terminate the temporary stay workflow.");
        }
    }
}
catch (Exception exception)
{
    Console.Error.WriteLine($"FAIL  Weather reliability check during {stage}: {exception.GetType().Name}: {exception.Message}");
    return 1;
}

var perPhrase = stays * rounds;
Console.WriteLine();
Console.WriteLine($"{stays} fresh stays x {rounds} rounds = {perPhrase} turns per phrasing");
Console.WriteLine($"{"Phrasing",-64} {"Pass",5} {"Fail",5}  Failure categories");
foreach (var (phrase, _, _) in phrasings)
{
    var failed = failures[phrase].Values.Sum();
    Console.WriteLine($"{phrase,-64} {perPhrase - failed,5} {failed,5}  {string.Join(", ", failures[phrase].Select(item => $"{item.Key} {item.Value}"))}");
}

var totalFailed = failures.Values.Sum(item => item.Values.Sum());
Console.WriteLine($"{"Total",-64} {turns - totalFailed,5} {totalFailed,5}");
Console.WriteLine(totalFailed == 0 ? "PASS  Every turn fetched the requested dates and answered with their forecasts." : "FAIL  Acceptance requires every turn to pass.");
return totalFailed == 0 ? 0 : 1;

void Fail(string phrase, string category, string detail)
{
    failures[phrase][category] = failures[phrase].GetValueOrDefault(category) + 1;
    Console.WriteLine($"FAIL  [{category}] {phrase} {detail}");
}

// Mon-Fri: the coming Saturday and Sunday. Saturday: today and tomorrow. Sunday: today only,
// with Saturday reported as passed.
static DateOnly[] WeekendDates(DateOnly today) => today.DayOfWeek switch
{
    DayOfWeek.Sunday => [today],
    DayOfWeek.Saturday => [today, today.AddDays(1)],
    var weekday => [today.AddDays(DayOfWeek.Saturday - weekday), today.AddDays(DayOfWeek.Saturday - weekday + 1)],
};

static Func<DateOnly, string> Label(DateOnly today) => date =>
    date == today ? $"today ({date:ddd})" : date == today.AddDays(1) ? $"tomorrow ({date:ddd})" : date.ToString("dddd", CultureInfo.InvariantCulture);

static (string Category, string Detail)? Check(
    DateOnly today,
    DateOnly[] expected,
    ToolCall[] weather,
    string answer,
    string? expectedPlace = null)
{
    if (weather.Length == 0)
    {
        return ("no-tool", "GetWeather did not run this turn");
    }

    if (expectedPlace is not null && weather.Any(call => !UsesPlace(call.Arguments, expectedPlace)))
    {
        return ("wrong-place", $"GetWeather did not use the requested place '{expectedPlace}'");
    }

    if (weather.Any(call => call.Failed))
    {
        return ("activity-failed", "a GetWeather activity failed");
    }

    var results = weather.Select(call => ReadWeatherResult(call.Result)).ToArray();
    var forecastDates = results.Where(result => result.Status == "Available").Select(result => result.Date).ToHashSet();
    var expectedSet = expected.Select(date => (DateOnly?)date).ToHashSet();
    if (!forecastDates.SetEquals(expectedSet))
    {
        return ("wrong-dates", $"forecast dates [{string.Join(", ", forecastDates.Order())}] expected [{string.Join(", ", expected)}]");
    }

    var others = results.Where(result => result.Status != "Available").ToArray();
    if (others.Any(result => !(result.Status == "Elapsed" && result.Date == today.AddDays(-1))))
    {
        return ("unexpected-status", string.Join(", ", others.Select(result => $"{result.Status} {result.Date}")));
    }

    var available = results.Where(result => result.Status == "Available").ToArray();
    foreach (var date in expected)
    {
        var block = DayBlock(answer, date, today, expected);
        if (block is null)
        {
            return ("answer-missing-day", $"answer does not mention {date:dddd yyyy-MM-dd}");
        }

        // The part of the answer about this day must report this turn's forecast for it.
        var forecast = available.Single(result => result.Date == date);
        var stated = Temperatures(block);
        foreach (var (label, value) in new[] { ("high", forecast.High), ("low", forecast.Low) })
        {
            if (value is { } fahrenheit && !stated.Contains(Round(fahrenheit)))
            {
                return ("answer-wrong-forecast", $"{date:dddd} {label} {Round(fahrenheit)}°F not stated (stated [{string.Join(", ", stated)}])");
            }
        }

        if (!ConditionsMentioned(block, forecast.Conditions))
        {
            return ("answer-wrong-forecast", $"{date:dddd} conditions '{forecast.Conditions}' not stated");
        }
    }

    // A temperature from no forecast fetched this turn means content came from somewhere else,
    // such as an earlier answer.
    var fetched = available.SelectMany(result => new[] { result.High, result.Low })
        .OfType<double>().Select(Round).ToHashSet();
    var foreign = Temperatures(answer).Where(value => !fetched.Contains(value)).ToArray();
    if (foreign.Length > 0)
    {
        return ("answer-stale-content", $"answer states {string.Join(", ", foreign)}°F, not returned this turn");
    }

    // A day the guest did not ask about suggests an earlier answer was reused.
    var allowed = expected.Select(date => date.DayOfWeek).ToHashSet();
    if (today.DayOfWeek == DayOfWeek.Sunday)
    {
        allowed.Add(DayOfWeek.Saturday);
    }

    var extra = Enum.GetValues<DayOfWeek>()
        .Where(day => !allowed.Contains(day)
            && Regex.IsMatch(answer, $@"\b{day}\b", RegexOptions.IgnoreCase))
        .ToArray();
    if (extra.Length > 0)
    {
        return ("answer-extra-day", $"answer mentions {string.Join(", ", extra)}");
    }

    if (!expected.Contains(today) && Regex.IsMatch(answer, @"\b(?:today|tonight)\b", RegexOptions.IgnoreCase)
        && !answer.Contains("remaining weekend day", StringComparison.Ordinal))
    {
        return ("answer-extra-day", "answer mentions today");
    }

    if (!expected.Contains(today.AddDays(1)) && Regex.IsMatch(answer, @"\btomorrow\b", RegexOptions.IgnoreCase))
    {
        return ("answer-extra-day", "answer mentions tomorrow");
    }

    return null;
}

static bool UsesPlace(string arguments, string expectedPlace)
{
    try
    {
        using var document = JsonDocument.Parse(arguments);
        return document.RootElement.TryGetProperty("place", out var place)
            && place.ValueKind == JsonValueKind.String
            && place.GetString()!.Contains(expectedPlace, StringComparison.OrdinalIgnoreCase);
    }
    catch (JsonException)
    {
        return false;
    }
}

static int Round(double fahrenheit) => (int)Math.Round(fahrenheit, MidpointRounding.AwayFromZero);

static bool MentionsDay(string text, DateOnly date, DateOnly today) =>
    text.Contains(date.ToString("dddd", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
    || date == today && Regex.IsMatch(text, @"\b(?:today|tonight)\b", RegexOptions.IgnoreCase)
    || date == today.AddDays(1) && Regex.IsMatch(text, @"\btomorrow\b", RegexOptions.IgnoreCase);

// Sentences from the first one naming this day up to the next one naming another expected day.
// Rendered answers give one paragraph per day; model-written ones may combine days in a sentence.
static string? DayBlock(string answer, DateOnly date, DateOnly today, DateOnly[] expected)
{
    var segments = Regex.Split(answer, @"(?<=[.!?])\s+|\n+").Where(segment => segment.Trim().Length > 0).ToArray();
    var first = Array.FindIndex(segments, segment => MentionsDay(segment, date, today));
    if (first < 0)
    {
        return null;
    }

    var block = new StringBuilder(segments[first]);
    for (var index = first + 1; index < segments.Length; index++)
    {
        if (expected.Any(other => other != date && MentionsDay(segments[index], other, today)))
        {
            break;
        }

        block.Append(' ').Append(segments[index]);
    }

    return block.ToString();
}

static HashSet<int> Temperatures(string text) =>
    Regex.Matches(text, @"(?<!\d)(-?\d{1,3})\s*(?:°|degrees\b)", RegexOptions.IgnoreCase)
        .Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
        .ToHashSet();

// Open-Meteo condition labels ("rain showers", "partly cloudy", "overcast"); the model may say
// "showers" or "cloudy", so any distinctive word, or a close synonym, counts.
static bool ConditionsMentioned(string block, string? conditions)
{
    if (string.IsNullOrWhiteSpace(conditions))
    {
        return true;
    }

    string[] qualifiers = ["slight", "moderate", "heavy", "light", "dense", "mainly", "partly", "violent", "with", "sky"];
    var synonyms = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["overcast"] = ["cloud"],
        ["clear"] = ["sunny", "clear"],
        ["drizzle"] = ["drizzle", "rain"],
        ["thunderstorm"] = ["thunder", "storm"],
    };
    return conditions.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Where(word => !qualifiers.Contains(word, StringComparer.OrdinalIgnoreCase))
        .SelectMany(word => synonyms.TryGetValue(word, out var alternatives) ? [word, .. alternatives] : new[] { word })
        .Any(word => block.Contains(word.TrimEnd('s'), StringComparison.OrdinalIgnoreCase));
}

// Offline proof that the checker accepts a correct answer and rejects reused or wrong content.
static int SelfTest()
{
    var thursday = new DateOnly(2026, 10, 22);
    DateOnly[] weekend = [thursday.AddDays(2), thursday.AddDays(3)];
    ToolCall Result(DateOnly date, double high, double low, string conditions, string arguments = "{}") => new(
        "GetWeather", arguments,
        JsonSerializer.Serialize(new { result = JsonSerializer.Serialize(new { status = "Available", localDate = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), conditions, highFahrenheit = high, lowFahrenheit = low }) }),
        false);
    ToolCall[] calls = [Result(weekend[0], 81.2, 69.4, "rain"), Result(weekend[1], 79.6, 70.1, "rain showers")];
    (string Name, string Answer, string? Expected)[] cases =
    [
        ("rendered", "![rain](/images/weather/rain.svg)\n\nExpect rain on Saturday at Hotel ZGY, with a high around 81°F and a low near 69°F.\n\n![rain showers](/images/weather/rain.svg)\n\nExpect rain showers on Sunday at Hotel ZGY, with a high around 80°F and a low near 70°F.", null),
        ("model-combined", "Saturday brings rain with a high of 81° and a low of 69°. Sunday has showers, 80 degrees and a low of 70 degrees.", null),
        ("stale-earlier-answer", "Expect rain on Saturday at Hotel ZGY, with a high around 84°F and a low near 72°F. Expect rain showers on Sunday at Hotel ZGY, with a high around 83°F and a low near 71°F.", "answer-wrong-forecast"),
        ("days-swapped", "Expect rain showers on Saturday, high around 80°F and low near 70°F. Expect rain on Sunday, high around 81°F and low near 69°F.", "answer-wrong-forecast"),
        ("extra-earlier-temperature", "Expect rain on Saturday, high around 81°F and low near 69°F. Expect rain showers on Sunday, high around 80°F and low near 70°F. Today was 85°F.", "answer-stale-content"),
        ("names-days-only", "Saturday and Sunday look wet at Hotel ZGY.", "answer-wrong-forecast"),
        ("wrong-conditions", "Expect clear skies on Saturday, high around 81°F and low near 69°F. Expect rain showers on Sunday, high around 80°F and low near 70°F.", "answer-wrong-forecast"),
        ("wrong-named-place", "Saturday brings rain with a high of 81° and a low of 69°. Sunday has showers, 80 degrees and a low of 70 degrees.", "wrong-place"),
    ];
    var failed = 0;
    foreach (var (name, answer, expectedCategory) in cases)
    {
        var testCalls = name == "wrong-named-place"
            ? calls.Select(call => call with { Arguments = """{"place":"Hotel ZGY"}""" }).ToArray()
            : calls;
        var category = Check(thursday, weekend, testCalls, answer, name == "wrong-named-place" ? "Key West" : null)?.Category;
        var pass = category == expectedCategory;
        failed += pass ? 0 : 1;
        Console.WriteLine($"{(pass ? "OK  " : "FAIL")}  {name}: expected {expectedCategory ?? "pass"}, got {category ?? "pass"}");
    }

    return failed == 0 ? 0 : 1;
}

// Activity results are the curated tool JSON, possibly wrapped as a JSON string under "result".
static WeatherResult ReadWeatherResult(string payload)
{
    try
    {
        using var outer = JsonDocument.Parse(payload);
        var root = outer.RootElement;
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("result", out var wrapped)
            && wrapped.ValueKind == JsonValueKind.String)
        {
            return ReadWeatherResult(wrapped.GetString()!);
        }

        if (root.ValueKind == JsonValueKind.String)
        {
            return ReadWeatherResult(root.GetString()!);
        }

        string? Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        double? Number(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
        var date = Text("localDate");
        return new(
            Text("status"),
            date is null ? null : DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            Number("highFahrenheit"),
            Number("lowFahrenheit"),
            Text("conditions"));
    }
    catch (JsonException)
    {
        return new(null, null, null, null, null);
    }
}

static async Task<List<ToolCall>> ReadNewToolCallsAsync(
    ITemporalClient client,
    string workflowId,
    HashSet<long> seenScheduled)
{
    var history = await client.GetWorkflowHandle(workflowId).FetchHistoryAsync();
    var outcomes = new Dictionary<long, (string Result, bool Failed)>();
    foreach (var historyEvent in history.Events)
    {
        if (historyEvent.EventType == EventType.ActivityTaskCompleted)
        {
            var attributes = historyEvent.ActivityTaskCompletedEventAttributes;
            outcomes[attributes.ScheduledEventId] = (
                attributes.Result?.Payloads_.Count > 0 ? attributes.Result.Payloads_[0].Data.ToStringUtf8() : string.Empty,
                false);
        }
        else if (historyEvent.EventType == EventType.ActivityTaskFailed)
        {
            var attributes = historyEvent.ActivityTaskFailedEventAttributes;
            outcomes[attributes.ScheduledEventId] = (attributes.Failure?.Message ?? "failed", true);
        }
    }

    var calls = new List<ToolCall>();
    foreach (var historyEvent in history.Events.Where(item => item.EventType == EventType.ActivityTaskScheduled))
    {
        var attributes = historyEvent.ActivityTaskScheduledEventAttributes;
        if (attributes.ActivityType.Name != "TemporalCommunity.Extensions.AI.InvokeFunction"
            || !seenScheduled.Add(historyEvent.EventId))
        {
            continue;
        }

        using var input = JsonDocument.Parse(attributes.Input.Payloads_[0].Data.ToStringUtf8());
        var name = input.RootElement.GetProperty("functionName").GetString()!;
        var arguments = input.RootElement.TryGetProperty("arguments", out var args)
            ? StripTrusted(args)
            : string.Empty;
        var (result, failed) = outcomes.GetValueOrDefault(historyEvent.EventId, ("<no result>", true));
        calls.Add(new ToolCall(name, arguments, result, failed));
    }

    return calls;
}

static async Task<HttpResponseMessage> PostTurnWithTransientRetryAsync(
    HttpClient http,
    ConciergeTurnRequest request)
{
    for (var attempt = 0; ; attempt++)
    {
        var response = await http.PostAsJsonAsync("/api/concierge/turns", request);
        if (response.StatusCode != System.Net.HttpStatusCode.ServiceUnavailable || attempt == 1)
        {
            return response;
        }

        // The stable turn ID makes a retry safe if the server accepted the first request but
        // could not return its response. Do not retry persistent or non-503 failures.
        response.Dispose();
        Console.WriteLine("RETRY  Concierge returned HTTP 503; retrying the same idempotent turn once.");
        await Task.Delay(TimeSpan.FromSeconds(1));
    }
}

// Trusted identity values are injected by the workflow, not the model; keep them out of console output.
static string StripTrusted(JsonElement arguments)
{
    var visible = arguments.ValueKind == JsonValueKind.Object
        ? arguments.EnumerateObject()
            .Where(property => !property.Name.StartsWith("__", StringComparison.Ordinal))
            .ToDictionary(property => property.Name, property => property.Value)
        : [];
    return JsonSerializer.Serialize(visible);
}

static string Clip(string value, int length) =>
    value.Length <= length ? value : value[..length] + "…";

static async Task SeedGuestAsync(string connectionString, Guid stayId, string familyName, string roomNumber)
{
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
    // One transaction so a room-overlap retry does not leave an orphan guest row.
    await using var transaction = await connection.BeginTransactionAsync();
    var guestId = Guid.NewGuid();
    // The schema requires stay boundaries at hotel-local midnight; mirror the seeded fixtures.
    var hotel = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, hotel).DateTime);
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
        throw new InvalidOperationException(
            $"Aspire describe returned exit code {process.ExitCode}; start the AppHost with 'aspire start' first.");
    }
    if (string.IsNullOrWhiteSpace(json))
    {
        throw new InvalidOperationException(
            "Aspire describe returned no resource metadata; start the AppHost with 'aspire start' first.");
    }

    using var document = JsonDocument.Parse(json);
    var resources = document.RootElement.GetProperty("resources").EnumerateArray().ToArray();
    var workflowServer = Resource(resources, "workflow-server");
    var environment = workflowServer.GetProperty("environment");
    var web = Resource(resources, "web");
    var webUrl = web.GetProperty("urls").EnumerateArray()
        .Single(endpoint => endpoint.GetProperty("name").GetString() == "https")
        .GetProperty("url")
        .GetString()
        ?? throw new InvalidOperationException("The Web HTTPS endpoint is unavailable.");

    return new AspireRuntime(
        RequiredValue(environment, "ConnectionStrings__concierge"),
        RequiredValue(environment, "TEMPORAL_ADDRESS"),
        RequiredValue(environment, "TEMPORAL_NAMESPACE"),
        webUrl);
}

static JsonElement Resource(JsonElement[] resources, string name) =>
    resources.SingleOrDefault(resource =>
        resource.GetProperty("name").GetString() is { } resourceName
        && (resourceName == name || resourceName.StartsWith(name + "-", StringComparison.Ordinal)
            && !resourceName[(name.Length + 1)..].Contains('-')))
    is { ValueKind: JsonValueKind.Object } found
        ? found
        : throw new InvalidOperationException($"Aspire resource '{name}' is not running.");

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
    string ConciergeConnectionString,
    string TemporalAddress,
    string TemporalNamespace,
    string WebUrl);

sealed record ToolCall(string Name, string Arguments, string Result, bool Failed);
sealed record WeatherResult(string? Status, DateOnly? Date, double? High, double? Low, string? Conditions);
