using Concierge.Contracts;
using Concierge.WorkflowServer.AI;
using Temporalio.Activities;

namespace Concierge.WorkflowServer.Activities;

/// <summary>Side effects of a guest-requested stay reset that live outside the workflow server's database.</summary>
public sealed class ConciergeStayResetActivities(IConciergeActionToolBackend backend)
{
    /// <summary>Terminates and deletes every ride of the stay. The ride service makes it idempotent.</summary>
    [Activity(nameof(PurgeRidesAsync))]
    public Task PurgeRidesAsync(PurgeStayRidesRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return backend.PurgeStayRidesAsync(
            request.GuestId,
            request.StayId,
            request.CheckoutDate,
            ActivityExecutionContext.Current.CancellationToken);
    }
}
