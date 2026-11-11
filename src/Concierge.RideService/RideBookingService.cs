using Concierge.RideService.Data;
using Concierge.RideService.Workflows;
using Concierge.Contracts;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace Concierge.RideService;

public sealed class RideBookingService(
    RideRepository repository,
    ITemporalClient temporalClient,
    TimeProvider timeProvider,
    TimeZoneInfo hotelTimeZone,
    Microsoft.Extensions.Options.IOptions<RideSchedulingOptions> schedulingOptions)
{
    public async Task<RideResponse> BookAsync(
        RideBookingRequest? request,
        CancellationToken cancellationToken)
    {
        var idempotencyKey = request?.IdempotencyKey;
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200)
        {
            throw RideErrors.InvalidRequest(
                "IdempotencyKey is required and must be at most 200 characters.");
        }

        await using var admission = await repository.BeginAdmissionAsync(
            idempotencyKey,
            cancellationToken);

        var existing = await admission.FindAsync(idempotencyKey, cancellationToken);
        if (existing is not null)
        {
            if (request is not null && RideRepository.Matches(existing.Intent, request))
            {
                return existing.Response;
            }

            throw RideErrors.IdempotencyConflict();
        }

        ValidateRequest(request!);
        // PostgreSQL timestamps have microsecond precision; canonicalize before admission so
        // the first response and a later database-backed retry carry the identical timestamp.
        var bookedAt = RideRepository.ToUtcMicrosecondPrecision(timeProvider.GetUtcNow());
        var requestedPickup = request!.Immediate
            ? bookedAt
            : RideRepository.ToUtcMicrosecondPrecision(request.PickupAt!.Value);

        if (DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(bookedAt, hotelTimeZone).DateTime)
            > request.CheckoutDate)
        {
            throw RideErrors.InvalidRequest(
                $"The stay is no longer eligible for rides after checkout day {request.CheckoutDate:yyyy-MM-dd}.");
        }

        var minimumNotice = schedulingOptions.Value.MinimumAdvanceNoticeMinutes;
        if (!request.Immediate && requestedPickup < bookedAt.AddMinutes(minimumNotice))
        {
            var earliest = TimeZoneInfo.ConvertTime(bookedAt.AddMinutes(minimumNotice), hotelTimeZone);
            throw RideErrors.InvalidRequest(
                $"Scheduled pickups must be at least {minimumNotice} minutes from now; the earliest is "
                + $"{earliest:yyyy-MM-dd HH:mm zzz}.");
        }

        var cutoffUtc = repository.CheckoutCutoffUtc(request.CheckoutDate);
        if (requestedPickup >= cutoffUtc)
        {
            throw RideErrors.InvalidRequest(
                $"Pickup must be before the end of checkout day {request.CheckoutDate:yyyy-MM-dd}.");
        }

        var command = new RideBookingCommand(
            RideRepository.GetBookingId(idempotencyKey),
            idempotencyKey,
            request.GuestId,
            request.StayId,
            request.CheckoutDate,
            request.Pickup.Name,
            request.Pickup.Latitude,
            request.Pickup.Longitude,
            request.Dropoff.Name,
            request.Dropoff.Latitude,
            request.Dropoff.Longitude,
            request.Immediate,
            requestedPickup,
            bookedAt,
            request.RouteDurationSeconds,
            ConciergeStayWorkflowIds.ForStay(request.StayId));

        try
        {
            var workflowId = command.Id.ToString("N");
            var workflowOptions = new WorkflowOptions
            {
                Id = workflowId,
                TaskQueue = RideWorkflowNames.TaskQueue,
                IdReusePolicy = WorkflowIdReusePolicy.RejectDuplicate,
                IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting,
            };
            var operation = WithStartWorkflowOperation.Create<RideBookingWorkflow>(
                workflow => workflow.RunAsync(),
                workflowOptions);
            var response = await temporalClient.ExecuteUpdateWithStartWorkflowAsync<
                RideBookingWorkflow,
                RideResponse>(
                workflow => workflow.AdmitBookingAsync(command),
                new WorkflowUpdateWithStartOptions
                {
                    Id = $"admit-{command.Id:N}",
                    StartWorkflowOperation = operation,
                });
            await admission.CommitAsync(cancellationToken);
            return response;
        }
        catch (WorkflowAlreadyStartedException)
        {
            var persisted = await admission.FindAsync(idempotencyKey, cancellationToken);
            if (persisted is not null && RideRepository.Matches(persisted.Intent, request))
            {
                return persisted.Response;
            }

            throw RideErrors.ClosedWorkflowConflict();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The activity may have committed before a lost Temporal response. The database
            // remains the retry authority, not workflow state.
            var persisted = await admission.FindAsync(idempotencyKey, cancellationToken);
            if (persisted is not null)
            {
                if (RideRepository.Matches(persisted.Intent, request))
                {
                    return persisted.Response;
                }

                throw RideErrors.IdempotencyConflict();
            }

            throw;
        }
    }

    public async Task<RideResponse> GetAsync(
        Guid id,
        VerifiedRideScope scope,
        CancellationToken cancellationToken) =>
        await repository.GetOwnedAsync(id, scope.GuestId, scope.StayId, cancellationToken)
        ?? throw RideErrors.NotFound();

    public async Task<IReadOnlyList<RideResponse>> ListAsync(
        Guid pathGuestId,
        string? when,
        VerifiedRideScope scope,
        CancellationToken cancellationToken)
    {
        if (pathGuestId != scope.GuestId)
        {
            throw RideErrors.NotFound();
        }

        var upcoming = when switch
        {
            "upcoming" => true,
            "past" => false,
            _ => throw RideErrors.InvalidRequest("when must be either 'upcoming' or 'past'."),
        };
        return await repository.ListOwnedAsync(
            scope.GuestId,
            scope.StayId,
            upcoming,
            cancellationToken);
    }

    public async Task DeleteAsync(
        Guid id,
        VerifiedRideScope scope,
        CancellationToken cancellationToken)
    {
        var ride = await repository.GetOwnedAsync(id, scope.GuestId, scope.StayId, cancellationToken)
            ?? throw RideErrors.NotFound();
        if (ride.Status == "Cancelled")
        {
            return;
        }

        if (ride.Status is "PickedUp" or "Completed")
        {
            throw RideErrors.CancellationRejected();
        }

        try
        {
            var handle = temporalClient.GetWorkflowHandle<RideBookingWorkflow>(id.ToString("N"));
            var result = await handle.ExecuteUpdateAsync(
                workflow => workflow.CancelAsync(),
                new WorkflowUpdateOptions { Rpc = new() { CancellationToken = cancellationToken } });
            if (!result.Applied)
            {
                throw RideErrors.CancellationRejected();
            }
        }
        catch (WorkflowUpdateFailedException)
        {
            var current = await repository.GetOwnedAsync(
                id, scope.GuestId, scope.StayId, cancellationToken);
            if (current?.Status == "Cancelled")
            {
                return;
            }

            throw RideErrors.CancellationRejected();
        }
    }

    /// <summary>
    /// Removes every ride of one stay so a guest can start over. Booking workflows are terminated,
    /// not cancelled: cancellation refuses picked-up rides, and a surviving workflow would keep
    /// publishing status into the reset stay. Safe to repeat.
    /// </summary>
    public async Task PurgeStayAsync(
        Guid pathStayId,
        VerifiedRideScope scope,
        CancellationToken cancellationToken)
    {
        if (pathStayId != scope.StayId)
        {
            throw RideErrors.InvalidRequest("The stay in the path does not match the verified stay scope.");
        }

        var rideIds = await repository.ListStayRideIdsAsync(scope.GuestId, scope.StayId, cancellationToken);
        foreach (var rideId in rideIds)
        {
            try
            {
                await temporalClient.GetWorkflowHandle(rideId.ToString("N")).TerminateAsync(
                    "Stay reset by guest",
                    new WorkflowTerminateOptions { Rpc = new() { CancellationToken = cancellationToken } });
            }
            catch (RpcException exception) when (exception.Code == RpcException.StatusCode.NotFound)
            {
                // Already completed, cancelled, or terminated by an earlier purge attempt.
            }
        }

        await repository.DeleteStayRidesAsync(scope.GuestId, scope.StayId, cancellationToken);
    }

    private static void ValidateRequest(RideBookingRequest request)
    {
        if (request.GuestId == Guid.Empty || request.StayId == Guid.Empty)
        {
            throw RideErrors.InvalidRequest("GuestId and StayId must be non-empty identifiers.");
        }

        if (request.CheckoutDate == default || request.CheckoutDate == DateOnly.MaxValue)
        {
            throw RideErrors.InvalidRequest("CheckoutDate must be a valid hotel-local date.");
        }

        if (request.Pickup is null || request.Dropoff is null)
        {
            throw RideErrors.InvalidRequest("Pickup and Dropoff places are required.");
        }

        ValidatePlace(request.Pickup, "Pickup");
        ValidatePlace(request.Dropoff, "Dropoff");

        if (request.Immediate == request.PickupAt.HasValue)
        {
            throw RideErrors.InvalidRequest(
                "Set Immediate to true without PickupAt, or set Immediate to false with PickupAt.");
        }

        if (!double.IsFinite(request.RouteDurationSeconds)
            || request.RouteDurationSeconds is <= 0 or > 86_400)
        {
            throw RideErrors.InvalidRequest(
                "RouteDurationSeconds must be greater than zero and no more than 86400.");
        }
    }

    private static void ValidatePlace(RidePlaceRequest place, string label)
    {
        if (string.IsNullOrWhiteSpace(place.Name) || place.Name.Length > 200)
        {
            throw RideErrors.InvalidRequest($"{label}.Name is required and must be at most 200 characters.");
        }

        if (!double.IsFinite(place.Latitude) || place.Latitude is < -90 or > 90
            || !double.IsFinite(place.Longitude) || place.Longitude is < -180 or > 180)
        {
            throw RideErrors.InvalidRequest(
                $"{label} coordinates must be valid latitude and longitude values.");
        }
    }
}

public sealed record VerifiedRideScope(Guid GuestId, Guid StayId, DateOnly CheckoutDate);

public static class RideErrors
{
    public static RideApiException InvalidRequest(string message) =>
        new(StatusCodes.Status400BadRequest, "invalid_request", message);

    public static RideApiException IdempotencyConflict() =>
        new(
            StatusCodes.Status409Conflict,
            "idempotency_conflict",
            "This idempotency key was already used for a different ride request.");

    public static RideApiException ClosedWorkflowConflict() =>
        new(
            StatusCodes.Status409Conflict,
            "booking_workflow_conflict",
            "The booking workflow ID has already been used and cannot be reused.");

    public static RideApiException NotFound() =>
        new(StatusCodes.Status404NotFound, "not_found", "Ride not found.");

    public static RideApiException CancellationRejected() =>
        new(
            StatusCodes.Status409Conflict,
            "cancellation_rejected",
            "A ride cannot be cancelled after pickup.");
}
