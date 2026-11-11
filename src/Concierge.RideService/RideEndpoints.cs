using System.Globalization;
using Concierge.RideService.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Concierge.RideService;

public static class RideEndpoints
{
    public static IEndpointRouteBuilder MapRideEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/rides", async (
            RideBookingRequest? request,
            RideBookingService service,
            HttpContext context,
            ILogger<RideBookingService> logger) =>
            await ExecuteAsync(
                () => service.BookAsync(request, context.RequestAborted),
                "book",
                logger));

        endpoints.MapGet("/rides/{id:guid}", async (
            Guid id,
            RideBookingService service,
            HttpContext context,
            ILogger<RideBookingService> logger) =>
            await ExecuteAsync(
                () => service.GetAsync(
                    id,
                    GetScope(context),
                    context.RequestAborted),
                "read",
                logger));

        endpoints.MapGet("/guests/{guestId:guid}/rides", async (
            Guid guestId,
            string? when,
            RideBookingService service,
            HttpContext context,
            ILogger<RideBookingService> logger) =>
            await ExecuteAsync(
                () => service.ListAsync(
                    guestId,
                    when,
                    GetScope(context),
                    context.RequestAborted),
                "list",
                logger));

        endpoints.MapDelete("/rides/{id:guid}", async (
            Guid id,
            RideBookingService service,
            HttpContext context,
            ILogger<RideBookingService> logger) =>
            await ExecuteNoContentAsync(
                () => service.DeleteAsync(
                    id,
                    GetScope(context),
                    context.RequestAborted),
                "cancel",
                logger));

        endpoints.MapPost("/stays/{stayId:guid}/rides/purge", async (
            Guid stayId,
            RideBookingService service,
            HttpContext context,
            ILogger<RideBookingService> logger) =>
            await ExecuteNoContentAsync(
                () => service.PurgeStayAsync(
                    stayId,
                    GetScope(context),
                    context.RequestAborted),
                "purge",
                logger));

        return endpoints;
    }

    private static VerifiedRideScope GetScope(HttpContext context)
    {
        if (!Guid.TryParse(context.Request.Headers["X-Verified-Guest-Id"], out var guestId)
            || guestId == Guid.Empty
            || !Guid.TryParse(context.Request.Headers["X-Verified-Stay-Id"], out var stayId)
            || stayId == Guid.Empty
            || !DateOnly.TryParseExact(
                context.Request.Headers["X-Verified-Checkout-Date"],
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var checkoutDate))
        {
            throw RideErrors.InvalidRequest(
                "Verified guest, stay, and checkout scope headers are required.");
        }

        return new VerifiedRideScope(guestId, stayId, checkoutDate);
    }

    private static async Task<IResult> ExecuteAsync<T>(
        Func<Task<T>> operation,
        string operationName,
        ILogger logger)
    {
        try
        {
            return Results.Ok(await operation());
        }
        catch (RideApiException exception)
        {
            return Results.Json(
                new RideApiError(exception.Code, exception.Message),
                statusCode: exception.StatusCode);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(
                "Ride API operation {Operation} failed. Cause types: {CauseTypes}.",
                operationName,
                ExceptionTypes(exception));
            return Results.Json(
                new RideApiError(
                    "unavailable",
                    "Ride service is temporarily unavailable. Please try again."),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> ExecuteNoContentAsync(
        Func<Task> operation,
        string operationName,
        ILogger logger)
    {
        try
        {
            await operation();
            return Results.NoContent();
        }
        catch (RideApiException exception)
        {
            return Results.Json(
                new RideApiError(exception.Code, exception.Message),
                statusCode: exception.StatusCode);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(
                "Ride API operation {Operation} failed. Cause types: {CauseTypes}.",
                operationName,
                ExceptionTypes(exception));
            return Results.Json(
                new RideApiError(
                    "unavailable",
                    "Ride service is temporarily unavailable. Please try again."),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static string ExceptionTypes(Exception exception) =>
        string.Join(" -> ", Generate(exception).Select(current => current.GetType().Name));

    private static IEnumerable<Exception> Generate(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            yield return current;
        }
    }
}
