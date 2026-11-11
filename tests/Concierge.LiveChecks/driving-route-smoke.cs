#:project ../../src/Concierge.WorkflowServer/Concierge.WorkflowServer.csproj
// Transitive OllamaSharp analyzer targets a newer compiler; it does not affect this check.

// Opt-in live check for ADR 0009 Step 3. Never prints the API key.
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
foreach (var validation in new[] { validator.Validate(null, hotel), validator.Validate(null, geoapify) })
{
    if (validation.Failed)
    {
        Console.Error.WriteLine(
            $"{validation.FailureMessage} Set the key with: aspire secret set \"Parameters:geoapify-api-key\" \"<key>\" --apphost src/Concierge.AppHost");
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
var routeService = new GeoapifyDrivingRouteService(
    httpClient,
    resolver,
    Options.Create(hotel),
    hotelLocation,
    Options.Create(geoapify));
var result = await routeService.GetRouteAsync(new DrivingRouteRequest(["South Beach"]));
var route = result.Route;
var pass = result.Status == DrivingRouteStatus.Available
    && route is not null
    && route.DistanceMeters > 0
    && route.EstimatedDurationSeconds > 0
    && route.WebInstructions.Count > 0 && route.VoiceInstructions.Count is > 0 and <= 3
    && route.WebSummary.Contains("no live traffic", StringComparison.Ordinal);

if (pass)
{
    Console.WriteLine(
        $"PASS  Hotel-to-South Beach driving route: {route!.VoiceSummary} "
        + $"{route.WebInstructions.Count} web step(s), {route.VoiceInstructions.Count} voice step(s); {route.WebSummary}");
}
else
{
    Console.Error.WriteLine(
        $"FAIL  Hotel-to-South Beach route returned status {result.Status} "
        + $"with {route?.WebInstructions.Count ?? 0} instructions.");
}

return pass ? 0 : 1;

static string ScriptDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
