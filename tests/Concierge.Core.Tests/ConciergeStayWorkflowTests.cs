using Concierge.WorkflowServer.AI;
using Concierge.WorkflowServer.Workflows;
using Concierge.Contracts;
using Microsoft.Extensions.AI;
using Npgsql;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Converters;
using Temporalio.Exceptions;
using Temporalio.Extensions.WorkflowStreams;
using Temporalio.Worker;
using TemporalCommunity.Extensions.AI;

namespace Concierge.Core.Tests;

public sealed class ConciergeStayWorkflowTests(PostgreSqlFixture postgreSql)
    : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task TurnsAreIdempotentSharedAcrossChannelsAndFlushedOnEnd()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        var firstRequest = Turn(stayId, "shared-1", "Where is breakfast?", "web");

        var first = await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(firstRequest));
        var replay = await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(firstRequest));
        Assert.Equal(first, replay);
        Assert.Single(host.ChatClient.Requests);

        var changed = firstRequest with { UserMessage = "Different question" };
        var conflict = await Assert.ThrowsAsync<WorkflowUpdateFailedException>(() =>
            handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(changed)));
        Assert.Equal(
            ConciergeStayWorkflow.TurnConflictErrorType,
            Assert.IsType<ApplicationFailureException>(conflict.InnerException).ErrorType);

        var second = await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
            Turn(stayId, "shared-2", "And what time?", "voice")));
        Assert.Equal(0, first.Sequence);
        Assert.Equal(1, second.Sequence);

        var voiceRequest = host.ChatClient.Requests[^1];
        var instructions = voiceRequest.Messages[0];
        Assert.Equal(ChatRole.System, instructions.Role);
        Assert.Equal(ConciergeStayWorkflow.ConciergeActionBaseInstructions, instructions.Text);
        Assert.Contains("Use only the current hotel evidence", instructions.Text, StringComparison.Ordinal);
        Assert.Contains("same service or facility in other words", instructions.Text, StringComparison.Ordinal);
        Assert.Contains("related topics are not evidence", instructions.Text, StringComparison.Ordinal);
        Assert.Contains("Invent no facts, procedures, policies", instructions.Text, StringComparison.Ordinal);
        Assert.Contains("Refer missing hotel facts to staff", instructions.Text, StringComparison.Ordinal);
        Assert.Contains("Use tools for live information even without hotel evidence",
            instructions.Text, StringComparison.Ordinal);
        Assert.Contains("Earlier turns, summaries, and guest-reported details are continuity only",
            instructions.Text, StringComparison.Ordinal);
        Assert.Contains(firstRequest.UserMessage, voiceRequest.UserTexts);
        Assert.Contains(
            voiceRequest.Messages,
            message => message.Text.Contains("Current Hotel ZGY evidence", StringComparison.Ordinal));

        // Replies and history are available before any flush.
        var pending = await handle.ExecuteUpdateAsync(wf => wf.GetHistoryAsync());
        Assert.Equal(["shared-1", "shared-2"], pending.Turns.Select(turn => turn.TurnId));
        Assert.Empty(await host.ReadPersistedTurnIdsAsync(stayId));

        await handle.ExecuteUpdateAsync(wf => wf.EndInteractionAsync());
        Assert.Equal(["shared-1", "shared-2"], await host.ReadPersistedTurnIdsAsync(stayId));

        var combined = await handle.ExecuteUpdateAsync(wf => wf.GetHistoryAsync());
        Assert.Equal(["shared-1", "shared-2"], combined.Turns.Select(turn => turn.TurnId));
        await handle.ExecuteUpdateAsync(wf => wf.EndInteractionAsync());
        Assert.Equal(2, (await host.ReadPersistedTurnIdsAsync(stayId)).Length);
    }

    [Fact]
    public async Task InterruptionPreservesAnswerAndMarksPendingAndAlreadyFlushedVoiceTurns()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql, flushIntervalSeconds: 600);
        var (stayId, handle) = await StartStayAsync(host);

        var pendingAnswer = await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
            Turn(stayId, "voice-pending", "Where is the pool?", "voice")));
        await handle.ExecuteUpdateAsync(wf => wf.MarkTurnInterruptedAsync(
            new ConciergeTurnInterruption("voice-pending")));

        var pendingHistory = await handle.ExecuteUpdateAsync(wf => wf.GetHistoryAsync());
        var pendingTurn = Assert.Single(pendingHistory.Turns);
        Assert.Equal(pendingAnswer.AssistantMessage, pendingTurn.AssistantMessage);
        Assert.True(pendingTurn.Interrupted);
        Assert.Null(await ReadPersistedInterruptionAsync(host, stayId, "voice-pending"));

        await handle.ExecuteUpdateAsync(wf => wf.EndInteractionAsync());
        Assert.True(await ReadPersistedInterruptionAsync(host, stayId, "voice-pending"));
        Assert.Equal(
            pendingAnswer.AssistantMessage,
            await ReadPersistedAnswerAsync(host, stayId, "voice-pending"));

        var flushedAnswer = await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
            Turn(stayId, "voice-flushed", "Where is breakfast?", "voice")));
        await handle.ExecuteUpdateAsync(wf => wf.EndInteractionAsync());
        Assert.False(await ReadPersistedInterruptionAsync(host, stayId, "voice-flushed"));

        // A late/retried interruption report updates the existing row instead of relying on a flush.
        await handle.ExecuteUpdateAsync(wf => wf.MarkTurnInterruptedAsync(
            new ConciergeTurnInterruption("voice-flushed")));
        await handle.ExecuteUpdateAsync(wf => wf.MarkTurnInterruptedAsync(
            new ConciergeTurnInterruption("voice-flushed")));
        var history = await handle.ExecuteUpdateAsync(wf => wf.GetHistoryAsync());
        var flushedTurn = history.Turns.Single(turn => turn.TurnId == "voice-flushed");
        Assert.Equal(flushedAnswer.AssistantMessage, flushedTurn.AssistantMessage);
        Assert.True(flushedTurn.Interrupted);
        Assert.True(await ReadPersistedInterruptionAsync(host, stayId, "voice-flushed"));
        Assert.Equal(
            flushedAnswer.AssistantMessage,
            await ReadPersistedAnswerAsync(host, stayId, "voice-flushed"));

        await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
            Turn(stayId, "web-turn", "Is parking available?", "web")));
        await handle.ExecuteUpdateAsync(wf => wf.MarkTurnInterruptedAsync(
            new ConciergeTurnInterruption("web-turn")));
        var webTurn = (await handle.ExecuteUpdateAsync(wf => wf.GetHistoryAsync()))
            .Turns.Single(turn => turn.TurnId == "web-turn");
        Assert.False(webTurn.Interrupted);
        await handle.ExecuteUpdateAsync(wf => wf.EndInteractionAsync());
        Assert.False(await ReadPersistedInterruptionAsync(host, stayId, "web-turn"));
    }

    [Fact]
    public async Task InterruptionUpdateDoesNotCancelAnAdmittedModelTurn()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql, flushIntervalSeconds: 600);
        var (stayId, handle) = await StartStayAsync(host);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.ChatClient.Gate = gate;
        var submitted = await handle.StartUpdateAsync(
            wf => wf.SubmitTurnAsync(Turn(stayId, "admitted-voice", "Still working?", "voice")),
            new(WorkflowUpdateStage.Accepted));
        await host.ChatClient.WaitForCallAsync().WaitAsync(TimeSpan.FromSeconds(15));

        // The report cannot cancel or truncate work already admitted to the workflow.
        await handle.ExecuteUpdateAsync(wf => wf.MarkTurnInterruptedAsync(
            new ConciergeTurnInterruption("admitted-voice")));
        Assert.False(submitted.GetResultAsync().IsCompleted);
        gate.TrySetResult();

        var result = await submitted.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("Answer: Still working?", result.AssistantMessage);
        var history = await handle.ExecuteUpdateAsync(wf => wf.GetHistoryAsync());
        var turn = Assert.Single(history.Turns);
        Assert.Equal(result.AssistantMessage, turn.AssistantMessage);
        Assert.False(turn.Interrupted);
        await handle.ExecuteUpdateAsync(wf => wf.EndInteractionAsync());
        Assert.False(await ReadPersistedInterruptionAsync(host, stayId, "admitted-voice"));
    }

    [Fact]
    public async Task AddingInterruptionColumnPreservesExistingTranscriptRows()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        var result = await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
            Turn(stayId, "legacy-turn", "Is breakfast served?", "voice")));
        await handle.ExecuteUpdateAsync(wf => wf.EndInteractionAsync());

        // Recreate a pre-Step-8 transcript table shape with a persisted row.
        await host.ExecuteAsync("ALTER TABLE transcript_turns DROP COLUMN interrupted;");
        await new Concierge.WorkflowServer.Data.DatabaseMigrator(
            host.DataSource,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<
                Concierge.WorkflowServer.Data.DatabaseMigrator>.Instance).MigrateAsync();

        Assert.False(await ReadPersistedInterruptionAsync(host, stayId, "legacy-turn"));
        Assert.Equal(result.AssistantMessage, await ReadPersistedAnswerAsync(host, stayId, "legacy-turn"));
    }

    [Fact]
    public async Task CompletedTurnsFlushOnConfiguredInterval()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql, flushIntervalSeconds: 1);
        var (stayId, handle) = await StartStayAsync(host);
        await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
            Turn(stayId, "timed-1", "Where is the pool?", "web")));

        await WaitUntilAsync(
            async () => (await host.ReadPersistedTurnIdsAsync(stayId)).Length > 0,
            TimeSpan.FromSeconds(15));
        Assert.Equal(["timed-1"], await host.ReadPersistedTurnIdsAsync(stayId));
    }

    [Fact]
    public async Task CompletedTurnsFlushWhenBatchSizeIsReachedBeforeInterval()
    {
        await using var host = await ConciergeTestHost.StartAsync(
            postgreSql, flushIntervalSeconds: 600, flushBatchSize: 2);
        var (stayId, handle) = await StartStayAsync(host);

        await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
            Turn(stayId, "batch-1", "Where is the pool?", "web")));
        // Below the threshold the batch waits for the 600-second interval.
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Empty(await host.ReadPersistedTurnIdsAsync(stayId));

        await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
            Turn(stayId, "batch-2", "Where is breakfast?", "voice")));
        await WaitUntilAsync(
            async () => (await host.ReadPersistedTurnIdsAsync(stayId)).Length == 2,
            TimeSpan.FromSeconds(15));
        Assert.Equal(["batch-1", "batch-2"], await host.ReadPersistedTurnIdsAsync(stayId));
    }

    /// <summary>End waits for already-admitted turns, persists their replies, and remains safe to repeat.</summary>
    [Fact]
    public async Task EndWaitsForAdmittedTurnsBeforeFlushing()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql, flushIntervalSeconds: 600);
        var (stayId, handle) = await StartStayAsync(host);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.ChatClient.Gate = gate;
        var first = await handle.StartUpdateAsync(
            wf => wf.SubmitTurnAsync(Turn(stayId, "end-1", "First?", "web")),
            new(WorkflowUpdateStage.Accepted));
        await host.ChatClient.WaitForCallAsync().WaitAsync(TimeSpan.FromSeconds(15));
        var second = await handle.StartUpdateAsync(
            wf => wf.SubmitTurnAsync(Turn(stayId, "end-2", "Second?", "voice")),
            new(WorkflowUpdateStage.Accepted));
        var end = await handle.StartUpdateAsync(
            wf => wf.EndInteractionAsync(), new(WorkflowUpdateStage.Accepted));
        var endResult = end.GetResultAsync();
        var repeatedEnd = await handle.StartUpdateAsync(
            wf => wf.EndInteractionAsync(), new(WorkflowUpdateStage.Accepted));
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() =>
                endResult.WaitAsync(TimeSpan.FromSeconds(1)));
        }
        finally
        {
            gate.TrySetResult();
        }

        await first.GetResultAsync();
        await second.GetResultAsync();
        await endResult.WaitAsync(TimeSpan.FromSeconds(15));
        await repeatedEnd.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(["end-1", "end-2"], await host.ReadPersistedTurnIdsAsync(stayId));
        await handle.ExecuteUpdateAsync(wf => wf.EndInteractionAsync());
        Assert.Equal(["end-1", "end-2"], await host.ReadPersistedTurnIdsAsync(stayId));

        var replayer = new WorkflowReplayer(new WorkflowReplayerOptions
        {
            DataConverter = DurableAIDataConverter.Instance,
        }.AddWorkflow<ConciergeStayWorkflow>());
        await replayer.ReplayWorkflowAsync(await handle.FetchHistoryAsync());
    }

    [Fact]
    public async Task SimultaneousTurnsRunInArrivalOrderWithSharedContext()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.ChatClient.Gate = gate;

        var firstUpdate = await handle.StartUpdateAsync(
            wf => wf.SubmitTurnAsync(Turn(stayId, "arrive-1", "First?", "web")),
            new(WorkflowUpdateStage.Accepted));
        await host.ChatClient.WaitForCallAsync();
        var secondUpdate = await handle.StartUpdateAsync(
            wf => wf.SubmitTurnAsync(Turn(stayId, "arrive-2", "Second?", "voice")),
            new(WorkflowUpdateStage.Accepted));

        // The second turn waits for the first; it has not reached the model.
        Assert.Single(host.ChatClient.Requests);
        gate.SetResult();

        var first = await firstUpdate.GetResultAsync();
        var second = await secondUpdate.GetResultAsync();
        Assert.Equal(0, first.Sequence);
        Assert.Equal(1, second.Sequence);
        Assert.Equal(["First?", "Second?"], host.ChatClient.Requests.Select(r => r.LastUserText));
        Assert.Contains("First?", host.ChatClient.Requests[1].UserTexts);
    }

    [Fact]
    public async Task GuestAllergyRemainsAvailableAfterMoreThanSixSmallTurns()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        var allergyTurn = Turn(stayId,
            "allergy", "I am allergic to shellfish.", "web");
        var original = await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(allergyTurn));
        Assert.Contains(
            host.ChatClient.Requests[0].Messages,
            message => message.Role == ChatRole.System
                && message.Text.Contains("shellfish", StringComparison.Ordinal));
        var modelCallCount = host.ChatClient.Requests.Count;
        Assert.Equal(original, await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(allergyTurn)));
        Assert.Equal(modelCallCount, host.ChatClient.Requests.Count);
        const int turnCount = 9;
        for (var index = 0; index < turnCount; index++)
        {
            await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
                Turn(stayId, $"t-{index}", $"Question {index}", "web")));
            Assert.Contains(
                host.ChatClient.Requests[^1].Messages,
                message => message.Role == ChatRole.System
                    && message.Text.Contains("shellfish", StringComparison.Ordinal));
        }

        await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
            Turn(stayId, "recall-allergy", "What allergy did I report?", "voice")));
        var answers = host.ChatClient.Requests;
        var last = answers[^1];
        Assert.Contains(
            last.Messages,
            message => message.Role == ChatRole.System
                && message.Text.Contains("Guest-reported allergy", StringComparison.Ordinal)
                && message.Text.Contains("shellfish", StringComparison.Ordinal)
                && message.Text.Contains("transcript sequence 0", StringComparison.Ordinal));

        await handle.ExecuteUpdateAsync(wf => wf.EndInteractionAsync());
        await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
            Turn(stayId,
                "recall-after-interaction", "What allergy did I report?", "web")));
        Assert.Contains(
            host.ChatClient.Requests[^1].Messages,
            message => message.Role == ChatRole.System
                && message.Text.Contains("shellfish", StringComparison.Ordinal));
        await handle.ExecuteUpdateAsync(wf => wf.EndInteractionAsync());
        Assert.Equal(turnCount + 3, (await host.ReadPersistedTurnIdsAsync(stayId)).Length);
    }

    [Fact]
    public async Task AllergyCorrectionSupersedesActiveMemoryButKeepsBothTranscriptTurns()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
            Turn(stayId, "allergy-original", "I am allergic to shellfish.", "web")));
        await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
            Turn(stayId,
                "allergy-correction", "I have no allergy to shellfish.", "web")));
        await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
            Turn(stayId, "allergy-recall", "What allergy did I report?", "voice")));

        var memory = Assert.Single(
            host.ChatClient.Requests[^1].Messages,
            message => message.Role == ChatRole.System
                && message.Text.Contains("Guest-reported allergy", StringComparison.Ordinal));
        Assert.Contains("no allergy to shellfish", memory.Text, StringComparison.Ordinal);
        Assert.Contains("transcript sequence 1, supersedes sequence 0", memory.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("reports allergic to shellfish", memory.Text, StringComparison.Ordinal);

        await handle.ExecuteUpdateAsync(wf => wf.EndInteractionAsync());
        var history = await handle.ExecuteUpdateAsync(wf => wf.GetHistoryAsync());
        Assert.Equal(
            ["I am allergic to shellfish.", "I have no allergy to shellfish."],
            history.Turns.Take(2).Select(turn => turn.GuestMessage));
        Assert.Equal(3, (await host.ReadPersistedTurnIdsAsync(stayId)).Length);
    }

    [Fact]
    public async Task QuestionsAndAssistantAnswersDoNotBecomeGuestMemory()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        host.ChatClient.AnswerFactory = _ => "You told me you are allergic to shellfish.";

        await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
            Turn(stayId, "allergy-question", "Am I allergic to shellfish?", "web")));
        await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
            Turn(stayId, "memory-check", "What do you remember?", "web")));

        Assert.DoesNotContain(
            host.ChatClient.Requests[^1].Messages,
            message => message.Role == ChatRole.System
                && message.Text.Contains("Guest-reported allergy", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailedAnswerDoesNotDiscardPreviouslyRecordedStayMemory()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
            Turn(stayId, "remember-allergy", "I am allergic to shellfish.", "web")));

        host.ChatClient.Fail = true;
        var failed = await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
            Turn(stayId, "failed-followup", "Can you help?", "web")));
        Assert.Equal(ModelUnavailableReply, failed.AssistantMessage);

        host.ChatClient.Fail = false;
        await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
            Turn(stayId, "retry-followup", "What allergy did I report?", "web")));
        Assert.Contains(
            host.ChatClient.Requests[^1].Messages,
            message => message.Role == ChatRole.System
                && message.Text.Contains("shellfish", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnresolvedGuestRequestPersistsWithoutClaimingCompletion()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);

        await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
            Turn(stayId, "request-towels", "Please send extra towels.", "web")));
        await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
            Turn(stayId, "request-status", "Did you send them?", "web")));

        Assert.Contains(
            host.ChatClient.Requests[^1].Messages,
            message => message.Role == ChatRole.System
                && message.Text.Contains("unresolved-request", StringComparison.Ordinal)
                && message.Text.Contains("remains unresolved", StringComparison.Ordinal)
                && message.Text.Contains("no system confirmation", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StayMemoryDoesNotCrossStayWorkflowBoundaries()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, firstStay) = await StartStayAsync(host);
        var secondStayId = await host.SeedStayAsync(
            "Other",
            "102",
            ConciergeTestHost.HotelToday,
            ConciergeTestHost.HotelToday.AddDays(1));
        var secondStay = await host.StartStayAsync(
            secondStayId,
            ConciergeTestHost.HotelMidnightUtc(ConciergeTestHost.HotelToday.AddDays(2)));

        await firstStay.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
            Turn(stayId, "stay-one-allergy", "I am allergic to shellfish.", "web")));
        await secondStay.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
            Turn(secondStayId, "stay-two-query", "What do you remember?", "web")));

        Assert.DoesNotContain(
            host.ChatClient.Requests[^1].Messages,
            message => message.Role == ChatRole.System
                && message.Text.Contains("shellfish", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WorkingHistoryIsBudgetSizedAndLongTurnsPruneOldCompletePairs()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        const int smallTurnCount = 12;
        for (var index = 0; index < smallTurnCount; index++)
        {
            var message = $"small-{index}";
            await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
                Turn(stayId, $"small-{index}", message, "web")));
        }

        const string smallCurrentMessage = "What should I know now?";
        await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
            Turn(stayId, "small-current", smallCurrentMessage, "web")));

        var smallAnswer = host.ChatClient.Requests[^1];
        Assert.Equal(smallCurrentMessage, smallAnswer.LastUserText);
        Assert.Equal(
            smallTurnCount,
            smallAnswer.Messages.Count(message =>
                message.Role == ChatRole.User
                && message.Text.StartsWith("small-", StringComparison.Ordinal)));

        for (var index = 0; index < 12; index++)
        {
            var message = $"medium-{index}: {new string((char)('a' + index), 140)}";
            await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
                Turn(stayId, $"medium-{index}", message, "web")));
        }

        var longMessage = $"long-request: {new string('x', 1_880)}";
        await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
            Turn(stayId, "long-request", longMessage, "web")));
        var longAnswer = host.ChatClient.Requests[^1];
        Assert.Equal(longMessage, longAnswer.LastUserText);
        // The 21,000-token action budget holds all stored history here, so pruning comes from the
        // recent-history storage cap; the after-long turn below asserts that the long pair evicts medium-0.
        // Pruning drops the oldest complete pairs first, so survivors are a contiguous newest suffix.
        var retainedMedium = longAnswer.UserTexts
            .Where(text => text.StartsWith("medium-", StringComparison.Ordinal))
            .Select(text => int.Parse(text["medium-".Length..text.IndexOf(':')]))
            .ToArray();
        Assert.NotEmpty(retainedMedium);
        Assert.Equal(Enumerable.Range(12 - retainedMedium.Length, retainedMedium.Length), retainedMedium);

        var conversation = longAnswer.Messages
            .Where(message => message.Role == ChatRole.User || message.Role == ChatRole.Assistant)
            .ToArray();
        Assert.Equal(ChatRole.User, conversation[^1].Role);
        Assert.Equal(longMessage, conversation[^1].Text);
        Assert.Equal(0, (conversation.Length - 1) % 2);
        for (var index = 0; index < conversation.Length - 1; index += 2)
        {
            Assert.Equal(ChatRole.User, conversation[index].Role);
            Assert.Equal(ChatRole.Assistant, conversation[index + 1].Role);
        }

        Assert.True(
            ConciergeStayWorkflow.EstimatePromptTokens(longAnswer.Messages)
            + ConciergeToolDefinitions.EstimateToolDefinitionTokens(includeEndChat: false)
            + ConciergeStayWorkflow.AnswerMaxOutputTokens
            + ConciergeStayWorkflow.ContextEstimationHeadroomTokens
            <= ConciergeStayWorkflow.ActionWorkingContextBudgetTokens);
        await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
            Turn(stayId, "after-long", "Anything else?", "web")));
        var afterLong = host.ChatClient.Requests[^1];
        Assert.DoesNotContain(
            afterLong.UserTexts,
            text => text.StartsWith("medium-0:", StringComparison.Ordinal));
        var afterLongConversation = afterLong.Messages
            .Where(message => message.Role == ChatRole.User || message.Role == ChatRole.Assistant)
            .ToArray();
        Assert.Equal(0, (afterLongConversation.Length - 1) % 2);
    }

    [Fact]
    public async Task ContinueAsNewKeepsDeadlineTranscriptAndTurnIdempotency()
    {
        await using var host = await ConciergeTestHost.StartAsync(
            postgreSql,
            devServerArgs:
            [
                "--dynamic-config-value",
                "limit.historyCount.suggestContinueAsNew=160",
            ],
            flushBatchSize: 7);
        var stayId = await SeedStayAsync(host);
        var deadline = ConciergeTestHost.HotelMidnightUtc(
            ConciergeTestHost.HotelToday.AddDays(2));
        var handle = await host.StartStayAsync(stayId, deadline);
        var firstRunId = (await handle.DescribeAsync()).RunId;
        await using var streamPublisher = new WorkflowStreamClient(host.Environment.Client, handle.Id);
        await using var streamSubscriber = new WorkflowStreamClient(host.Environment.Client, handle.Id);
        await using var subscription = streamSubscriber
            .GetTopic<RideActivityEntry>(RideActivityFeed.Topic)
            .SubscribeAsync(0)
            .GetAsyncEnumerator();
        var firstEntryRead = subscription.MoveNextAsync().AsTask();
        var firstEntry = new RideActivityEntry(
            Guid.NewGuid(),
            1,
            "Booked",
            DateTimeOffset.UtcNow,
            "Hotel ZGY",
            "Miami Beach",
            DateTimeOffset.UtcNow.AddMinutes(45));
        streamPublisher.GetTopic<RideActivityEntry>(RideActivityFeed.Topic)
            .Publish(firstEntry, forceFlush: true);
        await streamPublisher.FlushAsync();
        var firstSnapshot = await handle.QueryAsync(wf => wf.GetRideActivityReadState());
        Assert.Equal(new ConciergeRideReadState(0, 0, 1), firstSnapshot);
        var firstSnapshotRead = await handle.ExecuteUpdateAsync(wf =>
            wf.AdvanceRideActivityReadOffsetAsync(
                new ConciergeRideReadOffsetUpdate(firstSnapshot.SnapshotOffset)));
        Assert.Equal(1, firstSnapshotRead.ReadOffset);
        Assert.True(await firstEntryRead.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(firstEntry, subscription.Current.Value);
        var nextEntryRead = subscription.MoveNextAsync().AsTask();
        var verifiedGuest = new ConciergeVerifiedGuest(stayId, "Cecil", "Phillip", "112");

        var submitted = new List<ConciergeTurnResult>();
        for (var index = 0; index < 12 && (await handle.DescribeAsync()).RunId == firstRunId; index++)
        {
            var guestMessage = index == 0 ? "I am allergic to shellfish." : $"Question {index}";
            submitted.Add(await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
                new ConciergeTurnSubmission($"can-{index}", guestMessage, "web", verifiedGuest))));
            await WaitUntilAsync(
                async () => (await handle.DescribeAsync()).RunId != firstRunId,
                TimeSpan.FromSeconds(2),
                throwOnTimeout: false);
        }

        Assert.NotEqual(firstRunId, (await handle.DescribeAsync()).RunId);
        var continuedEntry = firstEntry with
        {
            RideId = Guid.NewGuid(),
            Sequence = 2,
            Status = "DriverAssigned",
        };
        streamPublisher.GetTopic<RideActivityEntry>(RideActivityFeed.Topic)
            .Publish(continuedEntry, forceFlush: true);
        await streamPublisher.FlushAsync();
        Assert.True(await nextEntryRead.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(continuedEntry, subscription.Current.Value);
        var continued = await ReadRunInputAsync(host, handle);
        Assert.Equal(deadline, continued.EligibilityDeadlineUtc);
        Assert.Equal(stayId, continued.StayId);
        Assert.Equal(verifiedGuest, continued.VerifiedGuest);
        Assert.Equal(submitted.Count, continued.NextSequence);
        Assert.Equal(7, continued.TranscriptFlushBatchSize);
        Assert.NotNull(continued.RideStreamState);
        Assert.Single(continued.RideStreamState.Log);
        Assert.Equal(1, continued.RideReadOffset);
        Assert.Equal(
            new ConciergeRideReadState(1, 0, 2),
            await handle.QueryAsync(wf => wf.GetRideActivityReadState()));
        Assert.True(continued.RecentTurns!.Count > 6);
        Assert.True(
            ConciergeStayWorkflow.EstimateRecentHistoryTokens(continued.RecentTurns)
            <= ConciergeStayWorkflow.RecentHistoryStorageBudgetTokens);
        var carriedMemory = Assert.Single(continued.StayMemory!);
        Assert.Equal("allergy", carriedMemory.Category);
        Assert.Contains("shellfish", carriedMemory.Subject, StringComparison.Ordinal);

        // Every earlier-run turn was flushed before Continue-As-New.
        var expectedIds = submitted.Select(turn => turn.TurnId).ToArray();
        Assert.Equal(expectedIds, await host.ReadPersistedTurnIdsAsync(stayId));

        // A retried turn from the earlier run returns its original result without a model call.
        var modelCalls = host.ChatClient.Requests.Count;
        var replay = await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
            new ConciergeTurnSubmission("can-0", "I am allergic to shellfish.", "web", verifiedGuest)));
        Assert.Equal(submitted[0], replay);
        var conflict = await Assert.ThrowsAsync<WorkflowUpdateFailedException>(() =>
            handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
                new ConciergeTurnSubmission("can-0", "Changed", "web", verifiedGuest))));
        Assert.Equal(
            ConciergeStayWorkflow.TurnConflictErrorType,
            Assert.IsType<ApplicationFailureException>(conflict.InnerException).ErrorType);
        Assert.Equal(modelCalls, host.ChatClient.Requests.Count);

        var next = await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
            new ConciergeTurnSubmission("after-can", "Anything else?", "voice", verifiedGuest)));
        Assert.Equal(submitted.Count, next.Sequence);
        // Recent context crossed Continue-As-New with the carried input.
        Assert.Contains(
            $"Question {submitted.Count - 1}",
            host.ChatClient.Requests[^1].UserTexts);
        Assert.Contains(
            host.ChatClient.Requests[^1].Messages,
            message => message.Role == ChatRole.System
                && message.Text.Contains("shellfish", StringComparison.Ordinal));
        Assert.Contains(
            host.ChatClient.Requests[^1].Messages,
            message => message.Text.StartsWith("Verified guest context:", StringComparison.Ordinal)
                && message.Text.Contains("Cecil", StringComparison.Ordinal));

        string[] allIds = [.. expectedIds, "after-can"];
        var history = await handle.ExecuteUpdateAsync(wf => wf.GetHistoryAsync());
        Assert.Equal(allIds, history.Turns.Select(turn => turn.TurnId));
        await handle.ExecuteUpdateAsync(wf => wf.EndInteractionAsync());
        Assert.Equal(allIds, await host.ReadPersistedTurnIdsAsync(stayId));
        var replayer = new WorkflowReplayer(new WorkflowReplayerOptions
        {
            DataConverter = DurableAIDataConverter.Instance,
        }.AddWorkflow<ConciergeStayWorkflow>());
        await replayer.ReplayWorkflowAsync(await handle.FetchHistoryAsync());
        await replayer.ReplayWorkflowAsync(await host.Environment.Client
            .GetWorkflowHandle<ConciergeStayWorkflow>(handle.Id, firstRunId).FetchHistoryAsync());
    }

    [Fact]
    public async Task RideReadOffsetIsForwardOnlyAndLimitedToDisplayedSnapshot()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        await using var publisher = new WorkflowStreamClient(host.Environment.Client, handle.Id);
        await using var subscriber = new WorkflowStreamClient(host.Environment.Client, handle.Id);

        var firstEntry = RideEntry(1);
        publisher.GetTopic<RideActivityEntry>(RideActivityFeed.Topic)
            .Publish(firstEntry, forceFlush: true);
        await publisher.FlushAsync();

        var displayedSnapshot = await handle.QueryAsync(wf => wf.GetRideActivityReadState());
        Assert.Equal(new ConciergeRideReadState(0, 0, 1), displayedSnapshot);
        await using var firstSubscription = subscriber
            .GetTopic<RideActivityEntry>(RideActivityFeed.Topic)
            .SubscribeAsync(displayedSnapshot.RetainedFromOffset)
            .GetAsyncEnumerator();
        Assert.True(await firstSubscription.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(firstEntry, firstSubscription.Current.Value);
        Assert.Equal(displayedSnapshot.RetainedFromOffset, firstSubscription.Current.Offset);

        // This entry arrives after the snapshot was obtained and must remain unread.
        var laterEntry = RideEntry(2);
        publisher.GetTopic<RideActivityEntry>(RideActivityFeed.Topic)
            .Publish(laterEntry, forceFlush: true);
        await publisher.FlushAsync();

        var marked = await handle.ExecuteUpdateAsync(wf =>
            wf.AdvanceRideActivityReadOffsetAsync(
                new ConciergeRideReadOffsetUpdate(displayedSnapshot.SnapshotOffset)));
        Assert.Equal(new ConciergeRideReadState(1, 0, 2), marked);

        // Replaying the same Update is idempotent, and an older displayed snapshot cannot regress it.
        var duplicate = await handle.ExecuteUpdateAsync(wf =>
            wf.AdvanceRideActivityReadOffsetAsync(
                new ConciergeRideReadOffsetUpdate(displayedSnapshot.SnapshotOffset)));
        var older = await handle.ExecuteUpdateAsync(wf =>
            wf.AdvanceRideActivityReadOffsetAsync(new ConciergeRideReadOffsetUpdate(0)));
        Assert.Equal(marked, duplicate);
        Assert.Equal(marked, older);
        Assert.Equal(marked, await handle.QueryAsync(wf => wf.GetRideActivityReadState()));

        // The retained start is a global offset and is safe to use as the subscription start.
        await handle.SignalAsync(
            ConciergeStayWorkflowIds.TruncateRideActivityStreamSignal,
            [new TruncateRideActivityStreamRequest(1)]);
        var retainedSnapshot = await handle.QueryAsync(wf => wf.GetRideActivityReadState());
        Assert.Equal(new ConciergeRideReadState(1, 1, 2), retainedSnapshot);
        await using var retainedSubscription = subscriber
            .GetTopic<RideActivityEntry>(RideActivityFeed.Topic)
            .SubscribeAsync(retainedSnapshot.RetainedFromOffset)
            .GetAsyncEnumerator();
        Assert.True(await retainedSubscription.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(laterEntry, retainedSubscription.Current.Value);
        Assert.Equal(retainedSnapshot.RetainedFromOffset, retainedSubscription.Current.Offset);

        // A future offset is capped to the stream end observed by the Update.
        var capped = await handle.ExecuteUpdateAsync(wf =>
            wf.AdvanceRideActivityReadOffsetAsync(
                new ConciergeRideReadOffsetUpdate(long.MaxValue)));
        Assert.Equal(new ConciergeRideReadState(2, 1, 2), capped);
        await handle.ExecuteUpdateAsync(wf => wf.EndInteractionAsync());
    }

    [Fact]
    public async Task StayExpiryFlushesPendingTurnsAndEndsWithoutGrace()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var stayId = await SeedStayAsync(host);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(6);
        var handle = await host.StartStayAsync(stayId, deadline);
        await using var subscriberClient = new WorkflowStreamClient(host.Environment.Client, handle.Id);
        await using var subscription = subscriberClient
            .GetTopic<RideActivityEntry>(RideActivityFeed.Topic)
            .SubscribeAsync(0)
            .GetAsyncEnumerator();
        var pendingSubscription = subscription.MoveNextAsync().AsTask();
        await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
            Turn(stayId, "expiring", "Late checkout?", "web")));

        await handle.GetResultAsync();
        Assert.True(DateTimeOffset.UtcNow >= deadline);
        Assert.Equal(["expiring"], await host.ReadPersistedTurnIdsAsync(stayId));
        Assert.False(await pendingSubscription.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task AnswerRequestsCarryTheOutputCapAndLengthCutoffKeepsReturnedText()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        host.ChatClient.AnswerFinishReason = ChatFinishReason.Length;

        var result = await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
            Turn(stayId, "cut-off", "Describe every amenity.", "web")));

        var answerRequest = Assert.Single(host.ChatClient.Requests);
        Assert.Equal(1024, answerRequest.MaxOutputTokens);
        Assert.Equal(ConciergeStayWorkflow.AnswerMaxOutputTokens, answerRequest.MaxOutputTokens);
        Assert.Equal("Answer: Describe every amenity.", result.AssistantMessage);
        await handle.ExecuteUpdateAsync(wf => wf.EndInteractionAsync());
        Assert.Equal(["cut-off"], await host.ReadPersistedTurnIdsAsync(stayId));
    }

    [Fact]
    public async Task MessageOverTheUtf8ByteLimitIsRejectedBeforeTheModel()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);

        // 700 three-byte characters: 700 chars passes a character count, 2,100 bytes does not.
        var multiByte = new string('\u20AC', 700);
        await Assert.ThrowsAsync<WorkflowUpdateFailedException>(() =>
            handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
                Turn(stayId, "too-big", multiByte, "web"))));
        Assert.Empty(host.ChatClient.Requests);

        var atLimit = new string('a', ConciergeTurnLimits.MaxUserMessageUtf8Bytes);
        await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
            Turn(stayId, "at-limit", atLimit, "web")));
        Assert.Single(host.ChatClient.Requests);
    }

    private static async Task<(Guid StayId, WorkflowHandle<ConciergeStayWorkflow> Handle)> StartStayAsync(
        ConciergeTestHost host)
    {
        var stayId = await SeedStayAsync(host);
        var deadline = ConciergeTestHost.HotelMidnightUtc(
            ConciergeTestHost.HotelToday.AddDays(2));
        return (stayId, await host.StartStayAsync(stayId, deadline));
    }

    /// <summary>A turn carrying a verified guest bound to <paramref name="stayId"/>, as the Web sends it.</summary>
    private const string ModelUnavailableReply =
        "I couldn't reach the concierge service just now. Please try again in a moment.";

    private static ConciergeTurnSubmission Turn(Guid stayId, string turnId, string message, string channel) =>
        new(turnId, message, channel, ConciergeTestHost.VerifiedGuest(stayId));

    private static RideActivityEntry RideEntry(int sequence) =>
        new(
            Guid.NewGuid(),
            sequence,
            sequence == 1 ? "Booked" : "DriverAssigned",
            DateTimeOffset.UtcNow,
            "Hotel ZGY",
            "Miami Beach",
            DateTimeOffset.UtcNow.AddMinutes(45));

    /// <summary>
    /// ADR 0015: a ride-feed poll whose subscriber went away stays an in-flight Update until the stay
    /// continues as new. An idle stay with waiting polls rolls over once per interval, which completes the
    /// abandoned poll, and a live subscriber carries on in the new run.
    /// </summary>
    [Fact]
    public async Task IdleRolloverReleasesAnAbandonedPollAndKeepsLiveSubscribers()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql, flushIntervalSeconds: 1);
        var stayId = await SeedStayAsync(host);
        var handle = await host.StartStayAsync(stayId, ConciergeTestHost.HotelMidnightUtc(ConciergeTestHost.HotelToday.AddDays(2)), idleRolloverSeconds: 2);
        var firstRunId = (await handle.DescribeAsync()).RunId;

        // A subscriber that gives up mid-poll, as a closed tab does: its accepted Update stays behind.
        using (var gone = new CancellationTokenSource())
        {
            await using var abandoned = new WorkflowStreamClient(host.Environment.Client, handle.Id);
            await using var poll = abandoned.GetTopic<RideActivityEntry>(RideActivityFeed.Topic).SubscribeAsync(0)
                .GetAsyncEnumerator(gone.Token);
            var waiting = poll.MoveNextAsync().AsTask();
            await WaitUntilAsync(async () => OpenUpdates(await handle.FetchHistoryAsync()) >= 1, TimeSpan.FromSeconds(10));
            gone.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        }

        Assert.Equal(1, OpenUpdates(await handle.FetchHistoryAsync()));
        await using var live = new WorkflowStreamClient(host.Environment.Client, handle.Id);
        await using var subscription = live.GetTopic<RideActivityEntry>(RideActivityFeed.Topic).SubscribeAsync(0).GetAsyncEnumerator();
        var next = subscription.MoveNextAsync().AsTask();

        await WaitUntilAsync(async () => (await handle.DescribeAsync()).RunId != firstRunId, TimeSpan.FromSeconds(20));
        var firstRun = host.Environment.Client.GetWorkflowHandle<ConciergeStayWorkflow>(handle.Id, firstRunId);
        Assert.Equal(0, OpenUpdates(await firstRun.FetchHistoryAsync()));
        Assert.Equal(2, (await ReadRunInputAsync(host, handle)).IdleRolloverSeconds);

        await using var publisher = new WorkflowStreamClient(host.Environment.Client, handle.Id);
        var entry = new RideActivityEntry(Guid.NewGuid(), 1, "Booked", DateTimeOffset.UtcNow, "Hotel ZGY", "Airport", DateTimeOffset.UtcNow.AddHours(1));
        publisher.GetTopic<RideActivityEntry>(RideActivityFeed.Topic).Publish(entry, forceFlush: true);
        await publisher.FlushAsync();
        Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(entry, subscription.Current.Value);

        var guest = new ConciergeVerifiedGuest(stayId, "Cecil", "Phillip", "112");
        var turn = await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(new ConciergeTurnSubmission("after-rollover", "Pool hours?", "web", guest)));
        Assert.Equal("after-rollover", Assert.Single((await handle.ExecuteUpdateAsync(wf => wf.GetHistoryAsync())).Turns).TurnId);
        Assert.Equal(0, turn.Sequence);

        var replayer = new WorkflowReplayer(new WorkflowReplayerOptions
        {
            DataConverter = DurableAIDataConverter.Instance,
        }.AddWorkflow<ConciergeStayWorkflow>());
        await replayer.ReplayWorkflowAsync(await firstRun.FetchHistoryAsync());
        await replayer.ReplayWorkflowAsync(await handle.FetchHistoryAsync());
    }

    /// <summary>Nothing waiting, nothing to release: an idle stay without subscribers keeps its run.</summary>
    [Fact]
    public async Task IdleRolloverSkipsAStayWithNothingWaiting()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql, flushIntervalSeconds: 1);
        var stayId = await SeedStayAsync(host);
        var handle = await host.StartStayAsync(stayId, ConciergeTestHost.HotelMidnightUtc(ConciergeTestHost.HotelToday.AddDays(2)), idleRolloverSeconds: 2);
        var runId = (await handle.DescribeAsync()).RunId;

        await Task.Delay(TimeSpan.FromSeconds(7));

        Assert.Equal(runId, (await handle.DescribeAsync()).RunId);
    }

    /// <summary>A turn in progress holds the rollover back; it happens once the turn is persisted.</summary>
    [Fact]
    public async Task IdleRolloverWaitsForARunningTurn()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql, flushIntervalSeconds: 1);
        var stayId = await SeedStayAsync(host);
        var handle = await host.StartStayAsync(stayId, ConciergeTestHost.HotelMidnightUtc(ConciergeTestHost.HotelToday.AddDays(2)), idleRolloverSeconds: 2);
        var runId = (await handle.DescribeAsync()).RunId;
        using var stopListening = new CancellationTokenSource();
        await using var live = new WorkflowStreamClient(host.Environment.Client, handle.Id);
        await using var subscription = live.GetTopic<RideActivityEntry>(RideActivityFeed.Topic).SubscribeAsync(0)
            .GetAsyncEnumerator(stopListening.Token);
        var listening = subscription.MoveNextAsync().AsTask();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.ChatClient.Gate = gate;

        var guest = new ConciergeVerifiedGuest(stayId, "Cecil", "Phillip", "112");
        var turn = handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(new ConciergeTurnSubmission("slow", "Breakfast?", "web", guest)));
        await host.ChatClient.WaitForCallAsync().WaitAsync(TimeSpan.FromSeconds(20));
        await Task.Delay(TimeSpan.FromSeconds(5));
        Assert.Equal(runId, (await handle.DescribeAsync()).RunId);

        gate.SetResult();
        Assert.Equal("slow", (await turn).TurnId);
        await WaitUntilAsync(async () => (await handle.DescribeAsync()).RunId != runId, TimeSpan.FromSeconds(20));
        Assert.Equal(["slow"], await host.ReadPersistedTurnIdsAsync(stayId));

        // An enumerator can't be disposed while a move is pending: stop listening first.
        stopListening.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listening);
    }

    /// <summary>
    /// ADR 0015 (2026-11-06): Azure's front end reset the upstream connection and the provider answered with
    /// an error item instead of a reply. That item now fails the model call, so Temporal retries it.
    /// </summary>
    [Fact]
    public async Task AProviderErrorItemIsRetriedInsteadOfFailingTheTurn()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql, flushIntervalSeconds: 1);
        var (stayId, handle) = await StartStayAsync(host);
        host.ChatClient.ScriptedResponses.Enqueue(ProviderError());
        host.ChatClient.AnswerFactory = _ => "Breakfast is 7 to 10.";

        var result = await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(Turn(stayId, "retried", "Breakfast?", "web")));

        Assert.Equal("Breakfast is 7 to 10.", result.AssistantMessage);
        Assert.Equal(2, host.ChatClient.Requests.Count);
    }

    /// <summary>When every attempt fails the guest gets the actionable reply, not a failed turn.</summary>
    [Fact]
    public async Task ProviderErrorsOnEveryAttemptGetTheModelFailureReply()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql, flushIntervalSeconds: 1);
        var (stayId, handle) = await StartStayAsync(host);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            host.ChatClient.ScriptedResponses.Enqueue(ProviderError());
        }

        var result = await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(Turn(stayId, "exhausted", "Breakfast?", "web")));

        Assert.Equal("I couldn't reach the concierge service just now. Please try again in a moment.", result.AssistantMessage);
        Assert.Equal(3, host.ChatClient.Requests.Count);
        // The failure behind the fallback, for Development pages; an exact retry gets only the answer.
        Assert.Contains("ModelProviderErrorException", result.FailureDetail);
        Assert.Contains("upstream connect error or disconnect/reset before headers", result.FailureDetail);
        var retried = await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(Turn(stayId, "exhausted", "Breakfast?", "web")));
        Assert.Equal(result.AssistantMessage, retried.AssistantMessage);
        Assert.Null(retried.FailureDetail);
    }

    private static ChatResponse ProviderError() =>
        new(new ChatMessage(ChatRole.Assistant,
            [new ErrorContent("upstream connect error or disconnect/reset before headers. reset reason: connection termination")]));

    /// <summary>Accepted Updates in this run's history that never completed.</summary>
    private static int OpenUpdates(WorkflowHistory history)
    {
        var accepted = history.Events
            .Where(e => e.WorkflowExecutionUpdateAcceptedEventAttributes is not null)
            .Select(e => e.WorkflowExecutionUpdateAcceptedEventAttributes.ProtocolInstanceId)
            .ToHashSet(StringComparer.Ordinal);
        accepted.ExceptWith(history.Events
            .Where(e => e.WorkflowExecutionUpdateCompletedEventAttributes is not null)
            .Select(e => e.WorkflowExecutionUpdateCompletedEventAttributes.Meta.UpdateId));
        return accepted.Count;
    }

    private static Task<Guid> SeedStayAsync(ConciergeTestHost host) =>
        host.SeedStayAsync(
            "Guest",
            "101",
            ConciergeTestHost.HotelToday,
            ConciergeTestHost.HotelToday.AddDays(1));

    private static async Task<bool?> ReadPersistedInterruptionAsync(
        ConciergeTestHost host,
        Guid stayId,
        string turnId)
    {
        await using var command = host.DataSource.CreateCommand(
            "SELECT interrupted FROM transcript_turns WHERE stay_id = $1 AND turn_id = $2;");
        command.Parameters.AddWithValue(stayId);
        command.Parameters.AddWithValue(turnId);
        return await command.ExecuteScalarAsync() as bool?;
    }

    private static async Task<string?> ReadPersistedAnswerAsync(
        ConciergeTestHost host,
        Guid stayId,
        string turnId)
    {
        await using var command = host.DataSource.CreateCommand(
            "SELECT assistant_message FROM transcript_turns WHERE stay_id = $1 AND turn_id = $2;");
        command.Parameters.AddWithValue(stayId);
        command.Parameters.AddWithValue(turnId);
        return await command.ExecuteScalarAsync() as string;
    }

    private static async Task<ConciergeStayWorkflowInput> ReadRunInputAsync(
        ConciergeTestHost host,
        WorkflowHandle<ConciergeStayWorkflow> handle)
    {
        var history = await handle.FetchHistoryAsync();
        var started = history.Events.First().WorkflowExecutionStartedEventAttributes;
        return await host.Environment.Client.Options.DataConverter
            .ToSingleValueAsync<ConciergeStayWorkflowInput>(started.Input.Payloads_);
    }

    private static async Task WaitUntilAsync(
        Func<Task<bool>> condition,
        TimeSpan timeout,
        bool throwOnTimeout = true)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                if (throwOnTimeout)
                {
                    throw new TimeoutException("Condition was not met in time.");
                }

                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
    }
}
