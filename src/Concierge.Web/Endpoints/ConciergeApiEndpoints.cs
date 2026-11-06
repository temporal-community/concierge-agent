using Concierge.Contracts;
using Concierge.Web.Services;

namespace Concierge.Web.Endpoints;

public static class ConciergeApiEndpoints
{
    /// <summary>
    /// Maps the guest concierge HTTP API under <c>/api/concierge</c>: <c>POST /sessions</c>,
    /// <c>POST /turns</c>, <c>GET /history</c>, and <c>POST /end</c>. Expected failures return
    /// <see cref="ConciergeApiError"/> with the matching status code; unexpected failures return 503.
    /// </summary>
    public static IEndpointRouteBuilder MapConciergeApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/concierge");
        api.MapPost("/sessions", async (
            ConciergeVerificationRequest? request,
            IConciergeService concierge,
            HttpContext context,
            ILogger<IConciergeService> logger) =>
                await ExecuteAsync(
                    () => concierge.VerifyAsync(request!, context.RequestAborted),
                    nameof(IConciergeService.VerifyAsync),
                    logger));

        api.MapPost("/turns", async (
            ConciergeTurnRequest? request,
            IConciergeService concierge,
            HttpContext context,
            ILogger<IConciergeService> logger) =>
                await ExecuteAsync(
                    () => concierge.SubmitTurnAsync(
                        GetBearerCredential(context),
                        request!,
                        context.RequestAborted),
                    nameof(IConciergeService.SubmitTurnAsync),
                    logger));

        api.MapGet("/history", async (
            IConciergeService concierge,
            HttpContext context,
            ILogger<IConciergeService> logger) =>
                await ExecuteAsync(
                    () => concierge.ReadHistoryAsync(
                        GetBearerCredential(context),
                        context.RequestAborted),
                    nameof(IConciergeService.ReadHistoryAsync),
                    logger));

        api.MapPost("/end", async (
            IConciergeService concierge,
            HttpContext context,
            ILogger<IConciergeService> logger) =>
                await ExecuteNoContentAsync(
                    async () =>
                    {
                        await concierge.EndAsync(
                            GetBearerCredential(context),
                            context.RequestAborted);
                    },
                    nameof(IConciergeService.EndAsync),
                    logger));

        api.MapPost("/reset", async (
            IConciergeService concierge,
            HttpContext context,
            ILogger<IConciergeService> logger) =>
                await ExecuteNoContentAsync(
                    async () =>
                    {
                        await concierge.ResetAsync(
                            GetBearerCredential(context),
                            context.RequestAborted);
                    },
                    nameof(IConciergeService.ResetAsync),
                    logger));

        return endpoints;
    }

    private static string? GetBearerCredential(HttpContext context) =>
        System.Net.Http.Headers.AuthenticationHeaderValue.TryParse(
            context.Request.Headers.Authorization,
            out var header)
        && StringComparer.OrdinalIgnoreCase.Equals(header.Scheme, "Bearer")
            ? header.Parameter
            : null;

    private static async Task<IResult> ExecuteAsync<T>(
        Func<Task<T>> operation,
        string operationName,
        ILogger logger)
    {
        try
        {
            return Results.Ok(await operation());
        }
        catch (ConciergeApiException exception)
        {
            return Results.Json(
                new ConciergeApiError(exception.Code, exception.Message),
                statusCode: exception.StatusCode);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(
                "Concierge API operation {Operation} failed. Cause types: {CauseTypes}.",
                operationName,
                ConciergeDiagnostics.ExceptionTypeChain(exception));
            return Results.Json(
                new ConciergeApiError(
                    "unavailable",
                    "The concierge is temporarily unavailable. Please try again."),
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
        catch (ConciergeApiException exception)
        {
            return Results.Json(
                new ConciergeApiError(exception.Code, exception.Message),
                statusCode: exception.StatusCode);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(
                "Concierge API operation {Operation} failed. Cause types: {CauseTypes}.",
                operationName,
                ConciergeDiagnostics.ExceptionTypeChain(exception));
            return Results.Json(
                new ConciergeApiError(
                    "unavailable",
                    "The concierge is temporarily unavailable. Please try again."),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
