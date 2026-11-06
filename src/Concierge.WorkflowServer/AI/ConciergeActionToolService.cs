using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Concierge.Contracts;
using Concierge.WorkflowServer.Location;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace Concierge.WorkflowServer.AI;

/// <summary>
/// Executes the durable tool activities. Only curated application results are returned; raw
/// Geoapify/Open-Meteo payloads and RideService error bodies never enter the model context.
/// </summary>
public interface IConciergeActionToolBackend
{
    Task<string> GetWeatherAsync(string? place, string period, DateTimeOffset? asOfUtc, CancellationToken cancellationToken);
    Task<string> GetRouteAsync(string[] places, CancellationToken cancellationToken);
    Task<string> FindNearbyPlacesAsync(string category, CancellationToken cancellationToken);
    Task<string> GetPlaceOpeningHoursAsync(string placeName, CancellationToken cancellationToken);
    Task<string> ProposeRideAsync(
        string? pickup,
        string dropoff,
        bool immediate,
        string? pickupAt,
        AIFunctionArguments arguments,
        CancellationToken cancellationToken);
    Task<string> ConfirmRideAsync(
        string proposalId,
        AIFunctionArguments arguments,
        CancellationToken cancellationToken);
    Task<string> GetRideStatusAsync(
        string rideId,
        AIFunctionArguments arguments,
        CancellationToken cancellationToken);
    Task<string> GetRideHistoryAsync(
        string when,
        AIFunctionArguments arguments,
        CancellationToken cancellationToken);
    Task<string> CancelRideAsync(
        string rideId,
        AIFunctionArguments arguments,
        CancellationToken cancellationToken);
    /// <summary>Deletes every ride of the verified stay; throws unless the ride service confirms.</summary>
    Task PurgeStayRidesAsync(
        Guid guestId,
        Guid stayId,
        DateOnly checkoutDate,
        CancellationToken cancellationToken);
}

public sealed class ConciergeActionToolService(
    OpenMeteoWeatherService weatherService,
    GeoapifyDrivingRouteService routeService,
    GeoapifyNearbyPlacesService nearbyPlacesService,
    GeoapifyPlaceOpeningHoursService placeOpeningHoursService,
    IHttpClientFactory httpClientFactory,
    TimeProvider timeProvider,
    Microsoft.Extensions.Options.IOptions<HotelLocationOptions> hotelOptions,
    Microsoft.Extensions.Options.IOptions<RideSchedulingOptions> schedulingOptions) : IConciergeActionToolBackend
{
    // RideService stores UTC; guests should hear hotel-local pickup times.
    private readonly TimeZoneInfo hotelTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById(hotelOptions.Value.TimeZoneId);

    public const string RideClientName = "ConciergeRideService";

    // Provisional ride-client budget; keep ConciergeStayWorkflow.ToolActivityTimeout above it.
    // Reads: 3 attempts x 3 s + 2 x 0.5 s constant backoff = 10 s = RideRequestTotalTimeout.
    internal static readonly TimeSpan RideReadAttemptTimeout = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan RideReadRetryDelay = TimeSpan.FromMilliseconds(500);
    internal const int RideReadMaxRetries = 2;
    internal static readonly TimeSpan RideRequestTotalTimeout = TimeSpan.FromSeconds(10);
    // HttpClient.Timeout also covers buffering the response body after the resilience pipeline.
    internal static readonly TimeSpan RideClientTimeout = RideRequestTotalTimeout + TimeSpan.FromSeconds(1);

    /// <summary>
    /// Registers the ride-service client with method-specific policies on one pipeline.
    /// Safe reads (GET) retry transient failures (5xx, 408, 429, network, attempt timeout) within
    /// <see cref="RideRequestTotalTimeout"/>. Booking (POST) and cancellation (DELETE) are sent
    /// exactly once with the whole budget as their single attempt: an automatic resend could repeat
    /// a side effect, and a timeout must surface as an unknown outcome, not a retry.
    /// </summary>
    public static void AddRideServiceClient(IServiceCollection services)
    {
        services.AddHttpClient(
                RideClientName,
                client =>
                {
                    client.BaseAddress = new Uri("http://ride-service");
                    client.Timeout = RideClientTimeout;
                })
            .AddStandardResilienceHandler(options =>
            {
                options.TotalRequestTimeout.Timeout = RideRequestTotalTimeout;
                options.AttemptTimeout.Timeout = RideReadAttemptTimeout;
                // Unsafe methods get one attempt bounded only by the total request timeout.
                options.AttemptTimeout.TimeoutGenerator = args => ValueTask.FromResult(
                    args.Context.GetRequestMessage()?.Method == HttpMethod.Get
                        ? options.AttemptTimeout.Timeout
                        : options.TotalRequestTimeout.Timeout);
                options.Retry.MaxRetryAttempts = RideReadMaxRetries;
                options.Retry.BackoffType = DelayBackoffType.Constant;
                options.Retry.Delay = RideReadRetryDelay;
                options.Retry.UseJitter = false;
                options.Retry.DisableForUnsafeHttpMethods();
            });
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public async Task<string> GetWeatherAsync(
        string? place,
        string period,
        DateTimeOffset? asOfUtc,
        CancellationToken cancellationToken)
    {
        if (!TryWeatherDay(period, out var day, out var daysAhead, out var startWeekday, out var weekendDay))
        {
            throw new ArgumentException(
                "Weather period must be current, today, tonight, tomorrow, a weekday name, day+2 through day+6, {weekday}+0 through {weekday}+6, or weekend+0/weekend+1.");
        }

        var result = await weatherService.GetWeatherAsync(
            new WeatherRequest(NormalizePlace(place), day, daysAhead, startWeekday, weekendDay, asOfUtc),
            cancellationToken);
        var weatherCode = result.Forecast?.WeatherCode ?? result.Current?.WeatherCode;
        var output = new ConciergeWeatherToolResult(
            result.Status.ToString(),
            result.ClarificationQuery,
            result.Candidates.Take(4).Select(candidate => Limit(candidate.Formatted, 160)).ToArray(),
            result.Location?.Name is { } location ? Limit(location, 160) : null,
            result.Forecast?.LocalDate ?? result.RequestedDate,
            weatherCode is { } code ? DescribeWeatherCode(code) : null,
            result.Forecast?.HighFahrenheit,
            result.Forecast?.LowFahrenheit,
            result.Forecast?.MaxWindMph,
            result.Forecast?.PrecipitationProbability,
            result.Current?.TemperatureFahrenheit,
            result.Current?.ApparentTemperatureFahrenheit,
            result.Current?.RelativeHumidityPercent,
            result.Current?.WindSpeedMph,
            result.Current?.PrecipitationInches,
            result.Current?.ObservedAt);
        return JsonSerializer.Serialize(output, JsonOptions);
    }

    public async Task<string> GetRouteAsync(
        string[] places,
        CancellationToken cancellationToken)
    {
        if (places is null || places.Length is < 1 or > 2
            || places.Any(place => string.IsNullOrWhiteSpace(place) || place.Length > 200))
        {
            throw new ArgumentException("Provide one or two place names, each no longer than 200 characters.");
        }

        var result = await routeService.GetRouteAsync(new DrivingRouteRequest(places), cancellationToken);
        var route = result.Route;
        var output = new ConciergeRouteToolResult(
            result.Status.ToString(),
            result.ClarificationQuery,
            result.Candidates.Take(4).Select(candidate => Limit(candidate.Formatted, 160)).ToArray(),
            route?.DistanceMeters,
            route?.EstimatedDurationSeconds,
            route?.VoiceSummary,
            route?.WebSummary,
            route?.VoiceInstructions.Take(3).Select(instruction => Limit(instruction, 120)).ToArray() ?? [],
            route?.WebInstructions.Take(100).Select(instruction => Limit(instruction, 500)).ToArray() ?? [],
            result.Origin,
            result.Destination);
        return JsonSerializer.Serialize(output, JsonOptions);
    }

    public async Task<string> FindNearbyPlacesAsync(
        string category,
        CancellationToken cancellationToken)
    {
        if (!NearbyPlaceCategories.TryGet(category, out var placeCategory))
        {
            throw new ArgumentException("Nearby place category is invalid.");
        }

        var places = await nearbyPlacesService.FindAsync(placeCategory, cancellationToken);
        var output = new ConciergeNearbyPlacesToolResult(
            places.Count == 0 ? "NoResults" : "Available",
            placeCategory.Key,
            places.Select(place => new ConciergeNearbyPlace(
                    Limit(place.Name, 120),
                    place.Address is null ? null : Limit(place.Address, 160),
                    Math.Round(place.DistanceMeters / MetersPerMile, 1, MidpointRounding.AwayFromZero),
                    place.ProviderId is null ? null : Limit(place.ProviderId, 256),
                    place.Latitude,
                    place.Longitude))
                .ToArray());
        return JsonSerializer.Serialize(output, JsonOptions);
    }

    public async Task<string> GetPlaceOpeningHoursAsync(
        string placeName,
        CancellationToken cancellationToken)
    {
        var result = await placeOpeningHoursService.FindAsync(placeName, cancellationToken);
        var output = new ConciergePlaceOpeningHoursToolResult(
            result.Status.ToString(),
            result.PlaceName is null ? null : Limit(result.PlaceName, 120),
            result.OpeningHours is null ? null : Limit(result.OpeningHours, 500),
            result.Candidates.Take(3).Select(candidate => Limit(candidate, 160)).ToArray(),
            // Resolved here so the recorded result, not host time zone data, drives workflow replay.
            OpeningHoursFormatter.TimeZoneLabel(hotelTimeZone));
        return JsonSerializer.Serialize(output, JsonOptions);
    }

    private const double MetersPerMile = 1609.344;

    public async Task<string> ProposeRideAsync(
        string? pickup,
        string dropoff,
        bool immediate,
        string? pickupAt,
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dropoff) || dropoff.Length > 200
            || pickup is { Length: > 200 }
            || pickupAt is { Length: > 64 }
            || immediate == (pickupAt is not null))
        {
            throw new ArgumentException("Ride pickup, drop-off, and timing are invalid.");
        }

        if (!TryGetTrustedIdentity(arguments, out var identity))
        {
            throw new ArgumentException("The verified ride scope is unavailable.");
        }

        DateTimeOffset? pickupAtUtc = null;
        DateTimeOffset? adjustedPickupLocal = null;
        if (!immediate)
        {
            var minimumNotice = schedulingOptions.Value.MinimumAdvanceNoticeMinutes;
            var nowUtc = timeProvider.GetUtcNow().ToUniversalTime();
            var earliestUtc = EarliestScheduledPickupUtc(nowUtc, minimumNotice);
            var cutoffUtc = CheckoutCutoffUtc(identity.CheckoutDate);
            if (!TryParseExplicitOffset(pickupAt!, out var parsed)
                || parsed < nowUtc - ScheduleClockTolerance)
            {
                return InvalidSchedule(
                    $"Scheduled pickups must be at least {minimumNotice} minutes from now. Ask for a later time.",
                    minimumNotice,
                    earliestUtc < cutoffUtc ? ToHotelTime(earliestUtc) : null,
                    earliestUtc < cutoffUtc ? null : identity.CheckoutDate);
            }

            var requestedUtc = parsed.ToUniversalTime();
            if (requestedUtc < earliestUtc)
            {
                // Near-term requests ("leave in five minutes") are moved, not refused: the guest
                // hears and confirms the adjusted time before anything is booked.
                requestedUtc = earliestUtc;
                adjustedPickupLocal = ToHotelTime(earliestUtc);
            }

            // Mirrors RideService's checkout-day rule so a proposal is never shifted past it.
            if (requestedUtc >= cutoffUtc)
            {
                return InvalidSchedule(
                    $"Scheduled pickups must be before the end of checkout day {identity.CheckoutDate:yyyy-MM-dd}.",
                    minimumNotice,
                    earliestUtc < cutoffUtc ? ToHotelTime(earliestUtc) : null,
                    identity.CheckoutDate);
            }

            pickupAtUtc = requestedUtc;
        }

        var routePlaces = string.IsNullOrWhiteSpace(pickup)
            ? new[] { dropoff.Trim() }
            : new[] { pickup.Trim(), dropoff.Trim() };
        var route = await routeService.GetRouteAsync(
            new DrivingRouteRequest(routePlaces, TrustedDestination(arguments)),
            cancellationToken);
        if (route.Status != DrivingRouteStatus.Available || route.Route is null
            || route.Origin is null || route.Destination is null)
        {
            return JsonSerializer.Serialize(
                new ConciergeRideProposalToolResult(
                    "NeedsClarification",
                    $"No proposal was made. Could not find \"{Limit(route.ClarificationQuery ?? string.Empty, 120)}\"; ask the guest which place they mean.",
                    route.Candidates.Take(3).Select(candidate => Limit(candidate.Formatted, 160)).ToArray(),
                    null,
                    null,
                    null,
                    null,
                    CanonicalCandidates: route.ClarifyingOrigin ? [] :
                        route.Candidates.Take(3).Select(CanonicalCandidate).ToArray(),
                    ClarifyingPickup: route.ClarifyingOrigin),
                JsonOptions);
        }

        var pickupName = routePlaces.Length == 2
            ? PreferRequestedPlaceName(route.Origin.Name, routePlaces[0])
            : route.Origin.Name;
        var pickupPlace = new ConciergeRidePlace(
            Limit(pickupName, 200), route.Origin.Latitude, route.Origin.Longitude);
        var dropoffPlace = new ConciergeRidePlace(
            Limit(TrustedDestination(arguments) is not null
                ? route.Destination.Name
                : PreferRequestedPlaceName(route.Destination.Name, routePlaces[^1]), 200),
            route.Destination.Latitude,
            route.Destination.Longitude);
        // The ride service supplies warning context, not a destination-wide booking lock.
        // Explicit new requests still get an offer, even when pickup times overlap.
        var activeRides = await TryGetUpcomingRidesAsync(identity, cancellationToken);
        if (activeRides is null)
        {
            return JsonSerializer.Serialize(
                new ConciergeRideProposalToolResult(
                    "ActiveRidesUnavailable",
                    "No proposal was made because existing rides could not be checked.",
                    [],
                    null,
                    null,
                    null,
                    null),
                JsonOptions);
        }

        var existingRides = activeRides
            .Where(ride => ride.Status is "Booked" or "DriverAssigned" or "PickedUp"
                && IsSameDestination(ride, dropoffPlace))
            .Take(3)
            .Select(Summarize)
            .ToArray();

        var proposal = new ConciergeRideProposalDraft(
            pickupPlace,
            dropoffPlace,
            immediate,
            pickupAtUtc,
            route.Route.EstimatedDurationSeconds);
        var success = new ConciergeRideProposalToolResult(
            "Available",
            null,
            [],
            proposal,
            Limit(route.Route.VoiceSummary, 240),
            Limit(route.Route.WebSummary, 240),
            route.Route.VoiceInstructions.Take(3).Select(instruction => Limit(instruction, 120)).ToArray(),
            ExistingRides: existingRides,
            EarliestPickupAt: adjustedPickupLocal,
            PickupAdjusted: adjustedPickupLocal is not null);
        return JsonSerializer.Serialize(success, JsonOptions);
    }

    // The prompt clock is minute-truncated (HH:mm), so a model computing "now + N minutes" can
    // land up to a minute behind the real clock; such requests are still meant for the near future.
    internal static readonly TimeSpan ScheduleClockTolerance = TimeSpan.FromMinutes(1);

    // RideService re-checks minimum notice when the guest confirms, so the proposal leaves room
    // for the confirmation round trip (guest reply + model turn) instead of expiring en route.
    internal static readonly TimeSpan ScheduleConfirmationGrace = TimeSpan.FromMinutes(2);

    /// <summary>now + minimum notice + confirmation grace, rounded up to a whole minute.</summary>
    internal static DateTimeOffset EarliestScheduledPickupUtc(DateTimeOffset nowUtc, int minimumNoticeMinutes)
    {
        var earliest = nowUtc.ToUniversalTime().AddMinutes(minimumNoticeMinutes) + ScheduleConfirmationGrace;
        var remainder = earliest.Ticks % TimeSpan.TicksPerMinute;
        return remainder == 0 ? earliest : earliest.AddTicks(TimeSpan.TicksPerMinute - remainder);
    }

    // Same rule as RideRepository.CheckoutCutoffUtc: pickups must precede hotel-local midnight
    // that ends checkout day.
    private DateTimeOffset CheckoutCutoffUtc(DateOnly checkoutDate)
    {
        var nextLocalMidnight = checkoutDate.AddDays(1)
            .ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(nextLocalMidnight, hotelTimeZone));
    }

    private DateTimeOffset ToHotelTime(DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, hotelTimeZone);

    private static string InvalidSchedule(
        string message,
        int minimumNotice,
        DateTimeOffset? earliestPickupLocal,
        DateOnly? checkoutCutoffDate) =>
        JsonSerializer.Serialize(
            new ConciergeRideProposalToolResult(
                "InvalidSchedule",
                message,
                [],
                null,
                null,
                null,
                null,
                minimumNotice,
                EarliestPickupAt: earliestPickupLocal,
                CheckoutCutoffDate: checkoutCutoffDate),
            JsonOptions);

    // Geocoders can return slightly different points for one place. A shared leading label only
    // counts nearby, so another branch of a chain across town does not trigger a warning.
    private const double SameDestinationMeters = 150;
    private const double SameLabelMeters = 1_000;

    private static bool IsSameDestination(RideResponseDto ride, ConciergeRidePlace dropoff)
    {
        var distance = DistanceMeters(ride.DropoffLatitude, ride.DropoffLongitude, dropoff.Latitude, dropoff.Longitude);
        return distance <= SameDestinationMeters
            || (distance <= SameLabelMeters
                && StringComparer.OrdinalIgnoreCase.Equals(LeadingLabel(ride.DropoffName), LeadingLabel(dropoff.Name)));
    }

    private static string LeadingLabel(string name) =>
        name.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;

    private static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double EarthRadiusMeters = 6_371_000;
        var dLat = double.DegreesToRadians(lat2 - lat1);
        var dLon = double.DegreesToRadians(lon2 - lon1);
        var a = Math.Pow(Math.Sin(dLat / 2), 2)
            + Math.Cos(double.DegreesToRadians(lat1)) * Math.Cos(double.DegreesToRadians(lat2))
            * Math.Pow(Math.Sin(dLon / 2), 2);
        return 2 * EarthRadiusMeters * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    private async Task<RideResponseDto[]?> TryGetUpcomingRidesAsync(
        VerifiedRideIdentity identity,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(
            HttpMethod.Get,
            $"/guests/{identity.GuestId:D}/rides?when=upcoming");
        AddVerifiedScope(message, identity);
        // Transport failures fail the activity; the workflow then renders the curated failure reply.
        using var response = await RideClient().SendAsync(message, cancellationToken);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<RideResponseDto[]>(JsonOptions, cancellationToken) ?? []
            : null;
    }

    public async Task<string> ConfirmRideAsync(
        string proposalId,
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(proposalId, out var parsedProposalId) || parsedProposalId == Guid.Empty
            || !TryGetTrustedIdentity(arguments, out var identity)
            || !TryGetTrustedValue(arguments, ConciergeToolDefinitions.TrustedProposalKey, out var proposalJson))
        {
            throw new ArgumentException("The trusted ride proposal is unavailable.");
        }

        var proposal = JsonSerializer.Deserialize<ConciergeRideProposal>(proposalJson, JsonOptions);
        if (proposal is null || proposal.ProposalId != parsedProposalId
            || proposal.Pickup is null || proposal.Dropoff is null
            || !double.IsFinite(proposal.RouteDurationSeconds)
            || proposal.RouteDurationSeconds is <= 0 or > 86_400)
        {
            throw new ArgumentException("The ride proposal is invalid.");
        }

        var request = new RideBookingRequestDto(
            identity.GuestId,
            identity.StayId,
            identity.CheckoutDate,
            new RidePlaceRequestDto(proposal.Pickup.Name, proposal.Pickup.Latitude, proposal.Pickup.Longitude),
            new RidePlaceRequestDto(proposal.Dropoff.Name, proposal.Dropoff.Latitude, proposal.Dropoff.Longitude),
            proposal.Immediate,
            proposal.PickupAtUtc,
            $"concierge-{identity.StayId:N}-{proposal.ProposalId:N}",
            proposal.RouteDurationSeconds);
        using var message = new HttpRequestMessage(HttpMethod.Post, "/rides")
        {
            Content = JsonContent.Create(request, options: JsonOptions),
        };
        AddVerifiedScope(message, identity);
        using var response = await RideClient().SendAsync(message, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorCode = await ReadRideErrorCodeAsync(response, cancellationToken);
            return JsonSerializer.Serialize(
                new ConciergeRideActionResult(
                    true,
                    errorCode == "invalid_request"
                        ? "The proposed pickup time is no longer eligible. No booking was confirmed."
                        : "The ride service did not confirm the outcome. Check ride history before trying again.",
                    null,
                    []),
                JsonOptions);
        }

        var ride = await response.Content.ReadFromJsonAsync<RideResponseDto>(JsonOptions, cancellationToken);
        if (ride is null)
        {
            return JsonSerializer.Serialize(
                new ConciergeRideActionResult(
                    true,
                    "The ride service did not confirm the outcome. Check ride history before trying again.",
                    null,
                    []),
                JsonOptions);
        }

        return JsonSerializer.Serialize(
            new ConciergeRideActionResult(
                true,
                null,
                Summarize(ride),
                []),
            JsonOptions);
    }

    public async Task<string> GetRideStatusAsync(
        string rideId,
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(rideId, out var id) || id == Guid.Empty
            || !TryGetTrustedIdentity(arguments, out var identity))
        {
            throw new ArgumentException("Ride identifier or verified scope is invalid.");
        }

        using var message = new HttpRequestMessage(HttpMethod.Get, $"/rides/{id:D}");
        AddVerifiedScope(message, identity);
        using var response = await RideClient().SendAsync(message, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return JsonSerializer.Serialize(
                new ConciergeRideActionResult(
                    true,
                    response.StatusCode == HttpStatusCode.NotFound
                        ? "That ride was not found for this stay."
                        : "Ride status is temporarily unavailable. Please try again later.",
                    null,
                    []),
                JsonOptions);
        }

        var ride = await response.Content.ReadFromJsonAsync<RideResponseDto>(JsonOptions, cancellationToken);
        return JsonSerializer.Serialize(
            new ConciergeRideActionResult(
                true,
                ride is null ? "Ride status is unavailable." : null,
                ride is null ? null : Summarize(ride),
                []),
            JsonOptions);
    }

    public async Task<string> GetRideHistoryAsync(
        string when,
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        if (when is not ("past" or "upcoming")
            || !TryGetTrustedIdentity(arguments, out var identity))
        {
            throw new ArgumentException("History period or verified scope is invalid.");
        }

        using var message = new HttpRequestMessage(
            HttpMethod.Get,
            $"/guests/{identity.GuestId:D}/rides?when={when}");
        AddVerifiedScope(message, identity);
        using var response = await RideClient().SendAsync(message, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return JsonSerializer.Serialize(
                new ConciergeRideActionResult(
                    true,
                    "Ride history is temporarily unavailable. Please try again later.",
                    null,
                    []),
                JsonOptions);
        }

        var rides = await response.Content.ReadFromJsonAsync<RideResponseDto[]>(JsonOptions, cancellationToken) ?? [];
        return JsonSerializer.Serialize(
            new ConciergeRideActionResult(
                true,
                null,
                null,
                rides.Take(10).Select(Summarize).ToArray()),
            JsonOptions);
    }

    public async Task<string> CancelRideAsync(
        string rideId,
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(rideId, out var id) || id == Guid.Empty
            || !TryGetTrustedIdentity(arguments, out var identity))
        {
            throw new ArgumentException("Ride identifier or verified scope is invalid.");
        }

        using var message = new HttpRequestMessage(HttpMethod.Delete, $"/rides/{id:D}");
        AddVerifiedScope(message, identity);
        using var response = await RideClient().SendAsync(message, cancellationToken);
        // The service returns 204 only after cancellation is committed; 202 would not confirm it.
        if (response.StatusCode != HttpStatusCode.NoContent)
        {
            return JsonSerializer.Serialize(
                new ConciergeRideActionResult(
                    true,
                    response.StatusCode switch
                    {
                        HttpStatusCode.NotFound => "That ride was not found for this stay.",
                        HttpStatusCode.Conflict => "This ride can no longer be cancelled.",
                        _ => "The ride service did not confirm cancellation. Check ride status before trying again.",
                    },
                    null,
                    []),
                JsonOptions);
        }

        return JsonSerializer.Serialize(
            new ConciergeRideActionResult(
                true,
                "Your ride is canceled.",
                null,
                [],
                ConfirmedCancellationRideId: id),
            JsonOptions);
    }

    public async Task PurgeStayRidesAsync(
        Guid guestId,
        Guid stayId,
        DateOnly checkoutDate,
        CancellationToken cancellationToken)
    {
        if (guestId == Guid.Empty || stayId == Guid.Empty || checkoutDate == default)
        {
            throw new ArgumentException("A verified guest, stay, and checkout date are required to purge rides.");
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, $"/stays/{stayId:D}/rides/purge");
        AddVerifiedScope(message, new VerifiedRideIdentity(guestId, stayId, checkoutDate));
        using var response = await RideClient().SendAsync(message, cancellationToken);
        if (response.StatusCode != HttpStatusCode.NoContent)
        {
            // Status only: ride-service error bodies are not echoed into workflow history.
            throw new InvalidOperationException(
                $"The ride service did not confirm the stay ride purge (HTTP {(int)response.StatusCode}).");
        }
    }

    private HttpClient RideClient() =>
        httpClientFactory.CreateClient(RideClientName);

    private RideSummary Summarize(RideResponseDto response) =>
        new(
            response.Id,
            Limit(response.Status, 40),
            Limit(response.PickupName, 120),
            Limit(response.DropoffName, 120),
            TimeZoneInfo.ConvertTime(response.PickupAt, hotelTimeZone),
            response.DriverName is null ? null : Limit(response.DriverName, 80),
            response.Car is null ? null : Limit(response.Car, 80),
            response.Plate is null ? null : Limit(response.Plate, 32),
            EstimateArrival(response) is { } arrival ? TimeZoneInfo.ConvertTime(arrival, hotelTimeZone) : null);

    // The simulator completes a ride one route duration after pickup, so the same estimate
    // answers "is my ride almost there?" without exposing driver details.
    private static DateTimeOffset? EstimateArrival(RideResponseDto response)
        => RideArrivalEstimate.Calculate(
            response.PickupAt, response.RouteDurationSeconds, response.PickedUpAtUtc, response.CompletedAtUtc);

    private static void AddVerifiedScope(HttpRequestMessage request, VerifiedRideIdentity identity)
    {
        request.Headers.Add("X-Verified-Guest-Id", identity.GuestId.ToString("D"));
        request.Headers.Add("X-Verified-Stay-Id", identity.StayId.ToString("D"));
        request.Headers.Add(
            "X-Verified-Checkout-Date",
            identity.CheckoutDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    private static bool TryGetTrustedIdentity(
        AIFunctionArguments arguments,
        out VerifiedRideIdentity identity)
    {
        identity = default!;
        if (!TryGetTrustedValue(arguments, ConciergeToolDefinitions.TrustedGuestIdKey, out var guestText)
            || !Guid.TryParse(guestText, out var guestId)
            || guestId == Guid.Empty
            || !TryGetTrustedValue(arguments, ConciergeToolDefinitions.TrustedStayIdKey, out var stayText)
            || !Guid.TryParse(stayText, out var stayId)
            || stayId == Guid.Empty
            || !TryGetTrustedValue(arguments, ConciergeToolDefinitions.TrustedCheckoutDateKey, out var dateText)
            || !DateOnly.TryParseExact(
                dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var checkoutDate))
        {
            return false;
        }

        identity = new VerifiedRideIdentity(guestId, stayId, checkoutDate);
        return true;
    }

    private static DrivingRouteEndpoint? TrustedDestination(AIFunctionArguments arguments)
    {
        if (!TryGetTrustedValue(arguments, ConciergeToolDefinitions.TrustedDestinationKey, out var json))
        {
            return null;
        }

        var place = JsonSerializer.Deserialize<ConciergeCanonicalPlace>(json, JsonOptions)
            ?? throw new ArgumentException("Verified destination is invalid.");
        return new DrivingRouteEndpoint(
            place.Address is null ? place.Name : $"{place.Name}, {place.Address}",
            place.Latitude, place.Longitude);
    }

    private static ConciergeCanonicalPlace CanonicalCandidate(ResolvedPlace candidate)
    {
        var address = candidate.Formatted.StartsWith(candidate.Name + ", ", StringComparison.OrdinalIgnoreCase)
            ? candidate.Formatted[(candidate.Name.Length + 2)..]
            : candidate.Formatted;
        return new(Limit(candidate.Name, 120), Limit(address, 160), Limit(candidate.PlaceId, 256),
            candidate.Latitude, candidate.Longitude);
    }

    private static bool TryGetTrustedValue(
        AIFunctionArguments arguments,
        string key,
        out string value)
    {
        value = string.Empty;
        if (!arguments.TryGetValue(key, out var raw))
        {
            return false;
        }

        switch (raw)
        {
            case string text:
                value = text;
                return true;
            case JsonElement { ValueKind: JsonValueKind.String } element:
                value = element.GetString() ?? string.Empty;
                return value.Length > 0;
            default:
                return false;
        }
    }

    private static async Task<string?> ReadRideErrorCodeAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<RideApiErrorDto>(JsonOptions, cancellationToken);
            return error?.Code;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryWeatherDay(
        string value,
        out WeatherDay day,
        out int? daysAhead,
        out DayOfWeek? startWeekday,
        out int? weekendDay)
    {
        daysAhead = null;
        startWeekday = null;
        weekendDay = null;
        if (ConciergeToolCallPolicy.TryParseWeatherWeekendPeriod(value, out var weekend))
        {
            day = WeatherDay.Today;
            weekendDay = weekend;
            return true;
        }

        if (ConciergeToolCallPolicy.TryParseWeatherDayOffset(value, out var offset))
        {
            day = WeatherDay.Today;
            daysAhead = offset;
            return true;
        }

        if (ConciergeToolCallPolicy.TryParseWeatherWeekdayPeriod(value, out var weekday, out offset))
        {
            day = WeatherDay.Today;
            daysAhead = offset;
            startWeekday = weekday;
            return true;
        }

        day = value switch
        {
            "current" or "now" or "right now" => WeatherDay.Current,
            "today" => WeatherDay.Today,
            "tonight" => WeatherDay.Tonight,
            "tomorrow" => WeatherDay.Tomorrow,
            _ => (WeatherDay)(-1),
        };
        return Enum.IsDefined(day);
    }

    private static string? NormalizePlace(string? place) =>
        string.IsNullOrWhiteSpace(place) ? null : place.Trim();

    private static bool TryParseExplicitOffset(string value, out DateTimeOffset result)
    {
        result = default;
        var trimmed = value.Trim();
        var hasZone = trimmed.EndsWith('Z')
            || trimmed.Length >= 6
            && (trimmed[^6] == '+' || trimmed[^6] == '-')
            && trimmed[^3] == ':';
        return hasZone
            && DateTimeOffset.TryParse(
                trimmed,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out result);
    }

    private static string DescribeWeatherCode(int code) => code switch
    {
        0 => "clear sky",
        1 => "mostly clear",
        2 => "partly cloudy",
        3 => "overcast",
        45 or 48 => "fog",
        51 or 53 or 55 or 56 or 57 => "drizzle",
        61 or 63 or 65 or 66 or 67 => "rain",
        71 or 73 or 75 or 77 => "snow",
        80 or 81 or 82 => "rain showers",
        85 or 86 => "snow showers",
        95 => "thunderstorm",
        96 or 99 => "thunderstorm with hail",
        _ => "conditions unavailable",
    };

    private static string Limit(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength].TrimEnd() + "…";

    /// <summary>
    /// Geocoders can generalize a neighborhood to its city/postcode ("South Beach" became
    /// "Miami Beach, FL 33139"); keep the guest's name unless the resolved one still contains it.
    /// </summary>
    internal static string PreferRequestedPlaceName(string? resolved, string requested)
    {
        var requestedHead = requested.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(resolved)
            || (requestedHead is not null && !resolved.Contains(requestedHead, StringComparison.OrdinalIgnoreCase))
            ? requested.Trim()
            : resolved;
    }

    private sealed record VerifiedRideIdentity(Guid GuestId, Guid StayId, DateOnly CheckoutDate);

    private sealed record RidePlaceRequestDto(string Name, double Latitude, double Longitude);
    private sealed record RideBookingRequestDto(
        Guid GuestId,
        Guid StayId,
        DateOnly CheckoutDate,
        RidePlaceRequestDto Pickup,
        RidePlaceRequestDto Dropoff,
        bool Immediate,
        DateTimeOffset? PickupAt,
        string IdempotencyKey,
        double RouteDurationSeconds);

    private sealed record RideResponseDto(
        Guid Id,
        Guid GuestId,
        Guid StayId,
        string PickupName,
        double PickupLatitude,
        double PickupLongitude,
        string DropoffName,
        double DropoffLatitude,
        double DropoffLongitude,
        bool Immediate,
        DateTimeOffset PickupAt,
        DateTimeOffset BookedAt,
        string Status,
        double RouteDurationSeconds = 1800,
        string StayWorkflowId = "",
        string? DriverName = null,
        string? Car = null,
        string? Plate = null,
        DateTimeOffset? PickedUpAtUtc = null,
        DateTimeOffset? CompletedAtUtc = null,
        DateTimeOffset? CancelledAtUtc = null);

    private sealed record RideApiErrorDto(string Code, string Message);
}

public sealed record ConciergeWeatherToolResult(
    string Status,
    string? ClarificationQuery,
    IReadOnlyList<string> Candidates,
    string? Location,
    DateOnly? LocalDate,
    string? Conditions,
    double? HighFahrenheit,
    double? LowFahrenheit,
    double? MaxWindMph,
    int? PrecipitationProbability,
    double? CurrentTemperatureFahrenheit = null,
    double? ApparentTemperatureFahrenheit = null,
    int? RelativeHumidityPercent = null,
    double? CurrentWindMph = null,
    double? CurrentPrecipitationInches = null,
    DateTimeOffset? ObservedAt = null);

public sealed record ConciergeRouteToolResult(
    string Status,
    string? ClarificationQuery,
    IReadOnlyList<string> Candidates,
    double? DistanceMeters,
    double? EstimatedDurationSeconds,
    string? VoiceSummary,
    string? WebSummary,
    IReadOnlyList<string> VoiceInstructions,
    IReadOnlyList<string> WebInstructions,
    DrivingRouteEndpoint? Origin,
    DrivingRouteEndpoint? Destination);

/// <summary>Closest named places of one category, nearest first; Status is Available or NoResults.</summary>
public sealed record ConciergeNearbyPlacesToolResult(
    string Status,
    string Category,
    IReadOnlyList<ConciergeNearbyPlace> Places);

public sealed record ConciergeNearbyPlace(
    string Name, string? Address, double DistanceMiles,
    string? ProviderId = null, double? Latitude = null, double? Longitude = null);

/// <summary>
/// One named place's opening hours; absent provider hours use NotListed rather than nearby results.
/// TimeZoneName labels the hotel's zone (e.g. "Eastern Time"); null in older histories or when the
/// host has no short name.
/// </summary>
public sealed record ConciergePlaceOpeningHoursToolResult(
    string Status,
    string? PlaceName,
    string? OpeningHours,
    IReadOnlyList<string> Candidates,
    string? TimeZoneName = null);

public sealed record ConciergeRideProposalDraft(
    ConciergeRidePlace Pickup,
    ConciergeRidePlace Dropoff,
    bool Immediate,
    DateTimeOffset? PickupAtUtc,
    double RouteDurationSeconds);

public sealed record ConciergeRideProposalToolResult(
    string Status,
    string? Message,
    IReadOnlyList<string> Candidates,
    ConciergeRideProposalDraft? Proposal,
    string? RouteSummary,
    string? WebSummary,
    IReadOnlyList<string>? Instructions,
    int? MinimumAdvanceNoticeMinutes = null,
    IReadOnlyList<RideSummary>? ExistingRides = null,
    // Hotel-local earliest bookable scheduled pickup, recorded so replies replay deterministically.
    // Set on InvalidSchedule (when one exists before checkout cutoff) and on adjusted proposals.
    DateTimeOffset? EarliestPickupAt = null,
    // True when the requested pickup was too soon and the proposal carries EarliestPickupAt instead.
    bool PickupAdjusted = false,
    // Set when the schedule failed because pickups must precede the end of this checkout day.
    DateOnly? CheckoutCutoffDate = null,
    IReadOnlyList<ConciergeCanonicalPlace>? CanonicalCandidates = null,
    bool ClarifyingPickup = false);

public sealed record ConciergeRideActionResult(
    bool Simulated,
    string? Message,
    RideSummary? Ride,
    IReadOnlyList<RideSummary> Rides,
    Guid? ConfirmedCancellationRideId = null);

public sealed record RideSummary(
    Guid Id,
    string Status,
    string Pickup,
    string Dropoff,
    DateTimeOffset PickupAt,
    string? DriverName,
    string? Car,
    string? Plate,
    DateTimeOffset? ArrivalAt = null);
