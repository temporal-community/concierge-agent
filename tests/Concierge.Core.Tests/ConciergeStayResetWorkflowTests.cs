using Concierge.Contracts;
using Concierge.WorkflowServer.AI;
using Concierge.WorkflowServer.Workflows;
using Microsoft.Extensions.AI;
using Temporalio.Client;
using Temporalio.Exceptions;

namespace Concierge.Core.Tests;

public sealed class ConciergeStayResetWorkflowTests(PostgreSqlFixture postgreSql)
    : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task ResetDeletesTranscriptAndRidesAndRestartsTheConversation()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        var guest = ConciergeTestHost.VerifiedGuest(stayId);
        await SubmitAsync(handle, guest, "before-plain", "What time is breakfast?");
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "propose", ConciergeToolDefinitions.ProposeRide,
            new Dictionary<string, object?> { ["dropoff"] = "South Beach", ["immediate"] = true }));
        await SubmitAsync(handle, guest, "before-propose", "Book me a ride to South Beach.");
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "confirm", ConciergeToolDefinitions.ConfirmRide, new Dictionary<string, object?>()));
        await SubmitAsync(handle, guest, "before-confirm", "Yes, book it.");
        await handle.ExecuteUpdateAsync(workflow => workflow.EndInteractionAsync());
        Assert.Equal(3, (await host.ReadPersistedTurnIdsAsync(stayId)).Length);

        await handle.ExecuteUpdateAsync(workflow => workflow.ResetStayAsync(guest));

        Assert.Equal((guest.GuestId, stayId, guest.CheckoutDate), Assert.Single(host.ActionTools.RidePurges));
        Assert.Empty(await host.ReadPersistedTurnIdsAsync(stayId));
        Assert.Empty((await handle.ExecuteUpdateAsync(workflow => workflow.GetHistoryAsync())).Turns);

        var after = await SubmitAsync(handle, guest, "after-reset", "Hello again.");
        Assert.Equal(0, after.Sequence);
        var prompt = string.Join("\n", host.ChatClient.Requests[^1].Messages.Select(message => message.Text));
        Assert.DoesNotContain("What time is breakfast?", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("South Beach", prompt, StringComparison.Ordinal);

        await handle.ExecuteUpdateAsync(workflow => workflow.EndInteractionAsync());
        Assert.Equal(["after-reset"], await host.ReadPersistedTurnIdsAsync(stayId));

        // Repeating the reset is safe and leaves the stay empty again.
        await handle.ExecuteUpdateAsync(workflow => workflow.ResetStayAsync(guest));
        Assert.Empty(await host.ReadPersistedTurnIdsAsync(stayId));
        Assert.Equal(2, host.ActionTools.RidePurges.Count);
        await ReplayAsync(handle);
    }

    [Fact]
    public async Task ResetWaitsForAnAdmittedTurnAndThenDeletesIt()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        var guest = ConciergeTestHost.VerifiedGuest(stayId);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.ChatClient.Gate = gate;

        var inFlight = SubmitAsync(handle, guest, "in-flight", "What time is breakfast?");
        await host.ChatClient.WaitForCallAsync();
        var reset = handle.ExecuteUpdateAsync(workflow => workflow.ResetStayAsync(guest));
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.False(reset.IsCompleted);
        Assert.Empty(host.ActionTools.RidePurges);

        gate.SetResult();
        await inFlight;
        await reset;

        Assert.Empty(await host.ReadPersistedTurnIdsAsync(stayId));
        Assert.Empty((await handle.ExecuteUpdateAsync(workflow => workflow.GetHistoryAsync())).Turns);
        await ReplayAsync(handle);
    }

    [Fact]
    public async Task FailedRidePurgeResetsNothingAndReportsWhy()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        var guest = ConciergeTestHost.VerifiedGuest(stayId);
        await SubmitAsync(handle, guest, "kept", "What time is breakfast?");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            host.ActionTools.RidePurgeFailures.Enqueue(new InvalidOperationException("ride service down"));
        }

        var failure = await Assert.ThrowsAsync<WorkflowUpdateFailedException>(
            () => handle.ExecuteUpdateAsync(workflow => workflow.ResetStayAsync(guest)));

        var application = Assert.IsType<ApplicationFailureException>(failure.InnerException);
        Assert.Equal(ConciergeWorkflowNames.ResetIncompleteErrorType, application.ErrorType);
        Assert.Contains("nothing was reset", application.Message, StringComparison.Ordinal);
        var history = await handle.ExecuteUpdateAsync(workflow => workflow.GetHistoryAsync());
        Assert.Equal("kept", Assert.Single(history.Turns).TurnId);
        // The stay keeps working after a failed reset.
        Assert.Equal(1, (await SubmitAsync(handle, guest, "next", "Thanks.")).Sequence);
    }

    [Fact]
    public async Task ResetForAnotherStaysGuestIsRejectedBeforeHistory()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (_, handle) = await StartStayAsync(host);

        await Assert.ThrowsAsync<WorkflowUpdateFailedException>(() => handle.ExecuteUpdateAsync(
            workflow => workflow.ResetStayAsync(ConciergeTestHost.VerifiedGuest(Guid.NewGuid()))));

        Assert.Empty(host.ActionTools.RidePurges);
    }

    private static async Task<(Guid StayId, WorkflowHandle<ConciergeStayWorkflow> Handle)> StartStayAsync(
        ConciergeTestHost host)
    {
        var stayId = await host.SeedStayAsync(
            "Reset", "303", ConciergeTestHost.HotelToday, ConciergeTestHost.HotelToday.AddDays(1));
        var handle = await host.StartStayAsync(
            stayId, ConciergeTestHost.HotelMidnightUtc(ConciergeTestHost.HotelToday.AddDays(2)));
        return (stayId, handle);
    }

    private static Task<ConciergeTurnResult> SubmitAsync(
        WorkflowHandle<ConciergeStayWorkflow> handle,
        ConciergeVerifiedGuest guest,
        string turnId,
        string message) =>
        handle.ExecuteUpdateAsync(workflow => workflow.SubmitTurnAsync(
            new ConciergeTurnSubmission(turnId, message, "web", guest)));

    private static ChatResponse ToolCall(string callId, string name, IDictionary<string, object?> arguments) =>
        new([new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(callId, name, arguments)])]);

    private static async Task ReplayAsync(WorkflowHandle<ConciergeStayWorkflow> handle)
    {
        var replayer = new Temporalio.Worker.WorkflowReplayer(new Temporalio.Worker.WorkflowReplayerOptions
        {
            DataConverter = TemporalCommunity.Extensions.AI.DurableAIDataConverter.Instance,
        }.AddWorkflow<ConciergeStayWorkflow>());
        await replayer.ReplayWorkflowAsync(await handle.FetchHistoryAsync());
    }
}
