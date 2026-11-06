#:project ../../src/Concierge.WorkflowServer/Concierge.WorkflowServer.csproj
// Transitive OllamaSharp analyzer targets a newer compiler; it does not affect this check.

// Opt-in live check for ADR 0009 Step 2. Calls Geoapify and Open-Meteo with the same
// resolver and weather service the WorkflowServer uses. Never prints the API key.
using System.Runtime.CompilerServices;
using Concierge.WorkflowServer.Location;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

const string AppHostUserSecretsId = "4712ad72-9c78-4c3e-9cde-48b4a755764a";

var repoRoot = Path.GetFullPath(Path.Combine(ScriptDirectory(), "..", ".."));
var configuration = new ConfigurationBuilder()
    .AddJsonFile(Path.Combine(repoRoot, "src", "Concierge.AppHost", "appsettings.json"), optional: false)
    .AddUserSecrets(AppHostUserSecretsId)
    .Build();

var hotel = configuration.GetSection(HotelLocationOptions.SectionName).Get<HotelLocationOptions>()
    ?? throw new InvalidOperationException("Hotel settings are missing from src/Concierge.AppHost/appsettings.json.");
var geoapify = new GeoapifyOptions { ApiKey = configuration["Parameters:geoapify-api-key"] ?? string.Empty };

var validator = new LocationOptionsValidator();
foreach (var result in new[] { validator.Validate(null, hotel), validator.Validate(null, geoapify) })
{
    if (result.Failed)
    {
        Console.Error.WriteLine(
            $"{result.FailureMessage} Set the key with: aspire secret set \"Parameters:geoapify-api-key\" \"<key>\" --apphost src/Concierge.AppHost");
        return 2;
    }
}

// Same startup geocode the WorkflowServer runs before its workers start.
var services = new ServiceCollection();
services.AddSingleton(Options.Create(hotel));
services.AddSingleton(Options.Create(geoapify));
services.AddHotelLocation();
await using var provider = services.BuildServiceProvider();
var hotelLocation = provider.GetRequiredService<HotelLocation>();
hotelLocation.Initialize(await provider.GetRequiredService<HotelAddressGeocoder>().ResolveAsync());
Console.WriteLine($"Hotel:Address resolved to {hotelLocation.Coordinates.Latitude}, {hotelLocation.Coordinates.Longitude}");

// The production named client: no HTTP retries and a whole-request time bound.
var httpClient = provider.GetRequiredService<IHttpClientFactory>();
var resolver = new GeoapifyPlaceResolver(httpClient, hotelLocation, Options.Create(geoapify));
var weather = new OpenMeteoWeatherService(
    httpClient, resolver, Options.Create(hotel), hotelLocation, TimeProvider.System);

var failures = 0;

foreach (var query in new[] { "Miami", "South Beach" })
{
    var resolution = await resolver.ResolveAsync(query);
    var place = resolution.Place;
    var pass = resolution.Status == PlaceResolutionStatus.Resolved
        && place?.County?.Contains("Miami-Dade", StringComparison.OrdinalIgnoreCase) == true;
    Report(pass, $"Resolve \"{query}\"", resolution.Status == PlaceResolutionStatus.Resolved
        ? $"{place!.Formatted} ({place.County}; confidence {place.Confidence:0.##})"
        : $"needs clarification; {resolution.Candidates.Count} eligible candidate(s)");
}

var hotelTimeZone = TimeZoneInfo.FindSystemTimeZoneById(hotel.TimeZoneId);
var hotelToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, hotelTimeZone).DateTime);
var forecastResult = await weather.GetWeatherAsync(new WeatherRequest("South Beach", WeatherDay.Today));
var forecast = forecastResult.Forecast;
var forecastPass = forecastResult.Status == WeatherResultStatus.Available
    && forecast is not null
    && forecast.TemperatureUnit == "°F"
    && forecast.WindSpeedUnit == "mph"
    && forecast.TimeZone == hotel.TimeZoneId
    && forecast.LocalDate == hotelToday;
Report(forecastPass, "Forecast \"South Beach\" today", forecast is null
    ? $"status {forecastResult.Status}"
    : $"{forecast.LocalDate:yyyy-MM-dd} {forecast.TimeZone}; high {forecast.HighFahrenheit}{forecast.TemperatureUnit}, "
        + $"low {forecast.LowFahrenheit}{forecast.TemperatureUnit}, wind {forecast.MaxWindMph} {forecast.WindSpeedUnit} "
        + $"(expected local date {hotelToday:yyyy-MM-dd})");

Console.WriteLine(failures == 0 ? "Step 2 live check passed." : $"Step 2 live check failed: {failures} check(s).");
return failures == 0 ? 0 : 1;

void Report(bool pass, string name, string detail)
{
    if (!pass)
    {
        failures++;
    }

    Console.WriteLine($"{(pass ? "PASS" : "FAIL")}  {name}: {detail}");
}

static string ScriptDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
