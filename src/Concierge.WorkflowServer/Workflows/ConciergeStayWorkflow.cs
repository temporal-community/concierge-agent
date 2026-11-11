using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using Concierge.Contracts;
using Concierge.WorkflowServer.AI;
using Concierge.WorkflowServer.Activities;
using Concierge.WorkflowServer.Setup;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using TemporalCommunity.Extensions.AI;
using Temporalio.Extensions.WorkflowStreams;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace Concierge.WorkflowServer.Workflows;

/// <summary>
/// One durable workflow per eligible stay. Text and voice sessions for the stay share this
/// context; turns run one at a time in arrival order. Model, retrieval, and database work
/// run in activities. Completed turns are buffered here and flushed to PostgreSQL when the
/// configured batch size is reached or the configured interval expires, whichever comes first,
/// and also at interaction end, before Continue-As-New, and at stay expiry.
/// </summary>
[Workflow(ConciergeWorkflowNames.StayWorkflow)]
public sealed class ConciergeStayWorkflow
{
    public const string TurnConflictErrorType = ConciergeWorkflowNames.TurnConflictErrorType;
    public const string StayExpiredErrorType = ConciergeWorkflowNames.StayExpiredErrorType;
    public const string UnavailableErrorType = "ConciergeUnavailable";

    /// <summary>Provider-enforced output cap for each guest answer.</summary>
    public const int AnswerMaxOutputTokens = ConciergePromptBuilder.AnswerMaxOutputTokens;

    internal const int MaxToolRoundsPerTurn = 2;
    private const int MaxModelCallsPerTurn = 3;
    // Transient provider failures (a hung connection, a 5xx) get fresh attempts within the turn deadline.
    private const int ModelCallAttempts = 3;
    // A pending offer survives one intervening guest turn ("I need a moment") and lapses after the second.
    private const int OfferTurnLimit = 2;
    private const int MaxActivityToolResultUtf8Bytes = 64_000;
    private static readonly TimeSpan ActionTurnDeadline = TimeSpan.FromSeconds(180);
    // Per-attempt model allowance (provisional until warm-model latency is measured). AI:Chat
    // RequestTimeoutSeconds is validated to finish inside it; see ActivityCallBudget.
    internal static readonly TimeSpan ModelActivityTimeout = TimeSpan.FromSeconds(20);
    // Per-attempt retrieval allowance: database lookups plus one query embedding (provisional).
    // AI:Embeddings RequestTimeoutSeconds is validated to finish inside it.
    internal static readonly TimeSpan RetrievalActivityTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions ActionJsonOptions = new(JsonSerializerDefaults.Web);

    public const int FixedPromptAllowanceTokens = ConciergePromptBuilder.FixedPromptAllowanceTokens;
    public const int AnswerWorkingContextBudgetTokens = ConciergePromptBuilder.AnswerWorkingContextBudgetTokens;
    public const int ActionWorkingContextBudgetTokens = ConciergePromptBuilder.ActionWorkingContextBudgetTokens;
    public const int ContextEstimationHeadroomTokens = ConciergePromptBuilder.ContextEstimationHeadroomTokens;
    public const int StayMemoryBudgetTokens = ConciergePromptBuilder.StayMemoryBudgetTokens;
    public const int MaxStayMemoryItems = ConciergePromptBuilder.MaxStayMemoryItems;
    public const int RecentHistoryStorageBudgetTokens =
        ConciergePromptBuilder.RecentHistoryStorageBudgetTokens;

    /// <summary>
    /// Existing configuration validation uses this lower bound. Actual prompt construction
    /// enforces the 8,000-token working budget below this model context limit.
    /// </summary>
    public const int WorstCaseContextTokens = ConciergePromptBuilder.ActionWorkingContextBudgetTokens;

    internal const string ConciergeActionInstructions = ConciergePromptBuilder.ConciergeActionInstructions;
    internal const string ConciergeActionBaseInstructions = ConciergePromptBuilder.ConciergeActionBaseInstructions;
    internal const string ConciergeInstructions = ConciergePromptBuilder.ConciergeInstructions;

    // Guest-facing retrieval is retried briefly so a failed turn is reported promptly.
    // Temporal owns these retries; the embedding SDK client does not retry.
    private static readonly ActivityOptions RetrievalActivityOptions = new()
    {
        StartToCloseTimeout = RetrievalActivityTimeout,
        RetryPolicy = new RetryPolicy
        {
            InitialInterval = TimeSpan.FromSeconds(1),
            MaximumInterval = TimeSpan.FromSeconds(5),
            BackoffCoefficient = 2,
            MaximumAttempts = 3,
        },
    };

    // A flush that exhausts its retries keeps its turns pending for the next attempt.
    private static readonly ActivityOptions TranscriptActivityOptions = new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(1),
        RetryPolicy = new RetryPolicy
        {
            InitialInterval = TimeSpan.FromSeconds(1),
            MaximumInterval = TimeSpan.FromSeconds(30),
            BackoffCoefficient = 2,
            MaximumAttempts = 5,
        },
    };

    private readonly Dictionary<string, AcceptedTurn> acceptedTurns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TurnAttempt> admittedTurns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ConciergeVoiceRevision> voiceRevisions = new(StringComparer.Ordinal);
    // Fail closed rather than evict retry/supersession authority during a stay.
    internal const int MaxVoiceRevisionFamilies = 512;
    internal const int MaxRevisionsPerUtterance = 32;
    internal const int MaxVoiceRevisionStateUtf8Bytes = 512_000;
    private TurnAttempt? activeAttempt;
    private ConciergeVoiceRevisionCheckpoint? voiceRevisionCheckpoint;
    private readonly List<ConciergeTranscriptTurn> runTurns = [];
    private readonly List<ConciergeTranscriptTurn> recentTurns = [];
    private readonly List<ConciergeTranscriptTurn> pendingPersistence = [];
    private readonly List<ConciergeStayMemoryItem> stayMemory = [];
    private readonly WorkflowStream rideStream;
    private readonly Guid stayId;
    private readonly DateTimeOffset eligibilityDeadlineUtc;
    private readonly TimeSpan flushInterval;
    private readonly int flushBatchSize;
    // Zero for stays started before the idle rollover existed: their commands stay exactly as before.
    private readonly TimeSpan idleRollover;
    private DateTimeOffset runStartedUtc;
    private bool rolloverStarted;
    private bool hasEarlierRuns;
    private ConciergeVerifiedGuest? verifiedGuest;
    private ConciergeRideProposal? pendingRideProposal;
    private ConciergePlacePresentation? nearbyPlaces;
    private ConciergePlacePresentation? placeClarification;
    private DateTime actionTurnStartedUtc;
    private ConciergeRideCancellationProposal? pendingRideCancellationProposal;
    private ConciergeRideCancellationOutcome? lastConfirmedCancellation;
    // The offer a ConfirmRide attempt consumed on that turn; tells the next turn it is closed.
    private ConciergeRideProposal? consumedRideOffer;
    private long consumedRideOfferSequence = -1;
    private string? consumedRideOfferSpokenRequestKey;
    private ConciergeRideConfirmationOutcome? consumedRideConfirmationOutcome;
    private long nextSequence;
    private long nextArrival;
    private long nextToProcess;
    private bool flushInProgress;
    private long rideReadOffset;

    internal DateTimeOffset? ActiveActionDeadline { get; private set; }

    /// <summary>Restores the stay deadline, transcript sequence, and carried conversation context.</summary>
    [WorkflowInit]
    public ConciergeStayWorkflow(ConciergeStayWorkflowInput input)
    {
        stayId = input.StayId;
        eligibilityDeadlineUtc = input.EligibilityDeadlineUtc;
        flushInterval = TimeSpan.FromSeconds(Math.Max(1, input.TranscriptFlushIntervalSeconds));
        flushBatchSize = Math.Clamp(
            input.TranscriptFlushBatchSize, 1, ConciergePersistenceOptions.MaxFlushBatchSize);
        idleRollover = TimeSpan.FromSeconds(Math.Max(0, input.IdleRolloverSeconds));
        nextSequence = input.NextSequence;
        rideStream = new WorkflowStream(input.RideStreamState);
        rideReadOffset = Math.Max(0, input.RideReadOffset);
        hasEarlierRuns = input.NextSequence > 0;
        if (input.VerifiedGuest is { } guest && guest.StayId != stayId)
        {
            throw new ArgumentException("Verified guest must belong to this stay.", nameof(input));
        }

        verifiedGuest = input.VerifiedGuest;
        pendingRideProposal = input.PendingRideProposal;
        nearbyPlaces = RestorePlaces(input.NearbyPlaces);
        placeClarification = RestorePlaces(input.PlaceClarification);
        pendingRideCancellationProposal = input.PendingRideCancellationProposal;
        lastConfirmedCancellation = input.LastConfirmedCancellation;
        consumedRideOffer = input.ConsumedRideOffer;
        consumedRideOfferSequence = input.ConsumedRideOfferSequence;
        consumedRideOfferSpokenRequestKey = input.ConsumedRideOfferSpokenRequestKey;
        consumedRideConfirmationOutcome = input.ConsumedRideConfirmationOutcome;
        voiceRevisionCheckpoint = input.VoiceRevisionCheckpoint;
        foreach (var revision in input.VoiceRevisions ?? [])
        {
            voiceRevisions.Add(revision.Family, revision);
        }
        if (input.StayMemory is not null)
        {
            stayMemory.AddRange(input.StayMemory);
        }

        if (input.RecentTurns is not null)
        {
            recentTurns.AddRange(input.RecentTurns);
        }
    }

    private static ConciergePlacePresentation? RestorePlaces(ConciergePlacePresentation? state) =>
        state is null ? null : ConciergePlaceContinuity.Create(
            state.Places, state.ExpiresAtUtc, state.Sequence, state.PresentedCount, state.Category, state.FocusIndex);

    /// <summary>
    /// Keeps the stay's conversation alive until its eligibility deadline, flushing transcript batches and
    /// continuing as new when history grows. All pending turns are persisted before the workflow completes.
    /// </summary>
    [WorkflowRun]
    public async Task RunAsync(ConciergeStayWorkflowInput input)
    {
        runStartedUtc = Workflow.UtcNow;
        var persistedNextSequence = await Workflow.ExecuteActivityAsync(
            (ConciergeTranscriptActivities activities) =>
                activities.ReadNextSequenceAsync(new ReadNextTranscriptSequenceRequest(stayId)),
            TranscriptActivityOptions);
        if (persistedNextSequence > nextSequence)
        {
            nextSequence = persistedNextSequence;
            hasEarlierRuns = true;
        }

        while (Workflow.UtcNow < eligibilityDeadlineUtc)
        {
            if (pendingPersistence.Count == 0 && !Workflow.ContinueAsNewSuggested && !rolloverStarted && !RolloverDue())
            {
                await Workflow.WaitConditionAsync(
                    () => pendingPersistence.Count > 0 || Workflow.ContinueAsNewSuggested,
                    IdleWaitTimeout());
                continue;
            }

            if (pendingPersistence.Count > 0)
            {
                // Persist a partial batch at most one interval after it starts waiting; flush early
                // when the batch-size threshold is reached or Continue-As-New is suggested.
                bool FlushDue() =>
                    Workflow.ContinueAsNewSuggested
                    || rolloverStarted
                    || pendingPersistence.Count >= flushBatchSize;

                await Workflow.WaitConditionAsync(
                    FlushDue,
                    Min(flushInterval, RemainingUntilDeadline()));

                // After a failed flush, back off for an interval instead of retrying immediately.
                if (!await FlushPendingAsync() && FlushDue())
                {
                    await Workflow.DelayAsync(flushInterval);
                }
            }

            if ((Workflow.ContinueAsNewSuggested || rolloverStarted || RolloverDue()) && pendingPersistence.Count == 0)
            {
                // Continue between turns, only after every admitted handler and flush finishes. Once
                // pollers are detached the stream rejects new polls, so an idle rollover that has
                // started must finish even if a turn arrives meanwhile (ADR 0015).
                if (!Workflow.ContinueAsNewSuggested)
                {
                    // Only the idle rollover gets here without a suggestion; stays with it off never do.
                    rolloverStarted = true;
                }

                rideStream.DetachPollers();

                await Workflow.WaitConditionAsync(() => Workflow.AllHandlersFinished);
                if (pendingPersistence.Count == 0)
                {
                    var continueInput = new ConciergeStayWorkflowInput(
                        stayId,
                        eligibilityDeadlineUtc,
                        (int)flushInterval.TotalSeconds,
                        nextSequence,
                        recentTurns.ToArray(),
                        flushBatchSize,
                        stayMemory.ToArray(),
                        verifiedGuest,
                        await rideStream.CaptureStateForContinueAsNewAsync(),
                        rideReadOffset,
                        pendingRideProposal,
                        pendingRideCancellationProposal,
                        lastConfirmedCancellation,
                        consumedRideOffer,
                        consumedRideOfferSequence,
                        consumedRideOfferSpokenRequestKey,
                        consumedRideConfirmationOutcome,
                        nearbyPlaces,
                        placeClarification,
                        voiceRevisions.Values.ToArray(),
                        voiceRevisionCheckpoint,
                        (int)idleRollover.TotalSeconds);
                    throw Workflow.CreateContinueAsNewException(
                        (ConciergeStayWorkflow workflow) => workflow.RunAsync(
                            continueInput));
                }
            }
        }

        // Stay expiry: let admitted handlers finish, then never drop an unpersisted turn.
        rideStream.DetachPollers();

        await Workflow.WaitConditionAsync(() => Workflow.AllHandlersFinished);
        while (!await FlushPendingAsync())
        {
            await Workflow.DelayAsync(flushInterval);
        }
    }

    /// <summary>
    /// Ride-feed polls are Updates that stay in flight until the stay continues as new, and a subscriber
    /// that goes away (a closed tab, a restarted Web) never completes its poll. Temporal caps in-flight
    /// Updates per workflow, so an idle stay that still has waiting handlers (with no turn running, those
    /// are feed polls) rolls over once per interval; live subscribers follow the new run (ADR 0015).
    /// </summary>
    private bool RolloverDue() =>
        idleRollover > TimeSpan.Zero
        && Workflow.UtcNow - runStartedUtc >= idleRollover
        && activeAttempt is null
        && pendingPersistence.Count == 0
        && !Workflow.AllHandlersFinished;

    /// <summary>The idle wait: until the deadline, or with the rollover on, until it's next worth checking.</summary>
    private TimeSpan IdleWaitTimeout()
    {
        if (idleRollover <= TimeSpan.Zero)
        {
            return RemainingUntilDeadline();
        }

        var untilDue = runStartedUtc + idleRollover - Workflow.UtcNow;
        return Min(RemainingUntilDeadline(), untilDue > TimeSpan.Zero ? untilDue : idleRollover);
    }

    [WorkflowSignal(ConciergeStayWorkflowIds.TruncateRideActivityStreamSignal)]
    public Task TruncateRideActivityStream(TruncateRideActivityStreamRequest request)
    {
        var state = rideStream.GetState();
        var endOffset = state.BaseOffset + state.Log.Count;
        var beforeOffset = Math.Clamp(request.BeforeOffset, state.BaseOffset, endOffset);
        rideStream.Truncate(beforeOffset);
        return Task.CompletedTask;
    }

    [WorkflowUpdateValidator(nameof(AdvanceRideActivityReadOffsetAsync))]
    public void ValidateAdvanceRideActivityReadOffset(ConciergeRideReadOffsetUpdate request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SnapshotOffset < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "The ride activity snapshot offset cannot be negative.");
        }
    }

    /// <summary>
    /// Advances the stay's read cursor through the requested displayed snapshot only.
    /// A newer stream entry is not marked read just because it arrived after the snapshot.
    /// </summary>
    [WorkflowUpdate(ConciergeWorkflowNames.AdvanceRideActivityReadOffsetUpdate)]
    public Task<ConciergeRideReadState> AdvanceRideActivityReadOffsetAsync(
        ConciergeRideReadOffsetUpdate request)
    {
        var streamState = rideStream.GetState();
        var currentStreamOffset = streamState.BaseOffset + streamState.Log.Count;
        var requestedOffset = Math.Min(request.SnapshotOffset, currentStreamOffset);
        rideReadOffset = Math.Max(rideReadOffset, requestedOffset);
        return Task.FromResult(CreateRideActivityReadState(streamState));
    }

    /// <summary>Returns the current read cursor and retained subscription/snapshot offset bounds.</summary>
    [WorkflowQuery(ConciergeWorkflowNames.RideActivityReadStateQuery)]
    public ConciergeRideReadState GetRideActivityReadState() =>
        CreateRideActivityReadState(rideStream.GetState());

    private ConciergeRideReadState CreateRideActivityReadState(
        Temporalio.Extensions.WorkflowStreams.WorkflowStreamState streamState)
    {
        var snapshotOffset = streamState.BaseOffset + streamState.Log.Count;
        return new ConciergeRideReadState(
            rideReadOffset,
            streamState.BaseOffset,
            snapshotOffset);
    }

    /// <summary>
    /// Rejects malformed turns, turns without the stay's verified guest, and turns submitted after the
    /// stay's eligibility deadline before they enter history.
    /// </summary>
    [WorkflowUpdateValidator(nameof(SubmitTurnAsync))]
    public void ValidateSubmitTurn(ConciergeTurnSubmission request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TurnId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.UserMessage);
        if (request.VerifiedGuest is null)
        {
            throw new ArgumentException(
                "A turn requires the stay's verified guest profile; the guest must sign in again.",
                nameof(request));
        }

        if (request.VerifiedGuest.StayId != stayId)
        {
            throw new ArgumentException("Verified guest must belong to this stay.", nameof(request));
        }

        if (request.TurnId.Length > ConciergeTurnLimits.MaxTurnIdLength
            || !ConciergeTurnLimits.IsUserMessageWithinLimit(request.UserMessage)
            || !ConciergeTurnLimits.IsChannel(request.Channel))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Invalid turn.");
        }

        if (request.VoiceUtterance is { } utterance
            && (request.Channel != "voice"
                || utterance.Connection <= 0 || utterance.Ordinal <= 0
                || !StringComparer.Ordinal.Equals(utterance.TurnId(request.UserMessage), request.TurnId)))
        {
            throw new ArgumentException("Voice provenance must match the exact-text turn identifier.", nameof(request));
        }

        if (Workflow.UtcNow >= eligibilityDeadlineUtc)
        {
            throw new ApplicationFailureException(
                "The stay is no longer eligible.",
                errorType: StayExpiredErrorType,
                nonRetryable: true);
        }
    }

    /// <summary>
    /// Marks completed voice playback as interrupted without canceling the admitted answer work.
    /// The turn remains in the transcript with its complete answer text.
    /// </summary>
    [WorkflowUpdateValidator(nameof(MarkTurnInterruptedAsync))]
    public void ValidateMarkTurnInterrupted(ConciergeTurnInterruption request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TurnId);
        if (request.TurnId.Length > ConciergeTurnLimits.MaxTurnIdLength)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Invalid turn identifier.");
        }
    }

    [WorkflowUpdate(ConciergeWorkflowNames.MarkTurnInterruptedUpdate)]
    public async Task MarkTurnInterruptedAsync(ConciergeTurnInterruption request)
    {
        // Serialize the database update against a flush so a marker cannot land before an
        // insert of the same turn and then be overwritten by that insert.
        await Workflow.WaitConditionAsync(() => !flushInProgress);
        flushInProgress = true;
        try
        {
            await Workflow.ExecuteActivityAsync(
                (ConciergeTranscriptActivities activities) =>
                    activities.MarkInterruptedAsync(
                        new MarkTranscriptTurnInterruptedRequest(stayId, request.TurnId)),
                TranscriptActivityOptions);
            MarkInterrupted(runTurns, request.TurnId);
            MarkInterrupted(pendingPersistence, request.TurnId);
        }
        finally
        {
            flushInProgress = false;
        }
    }

    /// <summary>
    /// Answers one guest turn using retrieved hotel evidence. Turns are processed in arrival order; a repeated
    /// turn ID returns the original answer, or fails with a conflict when the message differs.
    /// </summary>
    [WorkflowUpdate(ConciergeWorkflowNames.SubmitTurnUpdate)]
    public async Task<ConciergeTurnResult> SubmitTurnAsync(ConciergeTurnSubmission request)
    {
        var fingerprint = Fingerprint(request.Channel, request.UserMessage, request.EnableEndChatFunction);
        RejectSupersededRetry(request);
        if (acceptedTurns.TryGetValue(request.TurnId, out var accepted))
        {
            return MatchOrReject(accepted, fingerprint);
        }

        if (admittedTurns.TryGetValue(request.TurnId, out var joined))
        {
            if (joined.Fingerprint != fingerprint)
            {
                throw new ApplicationFailureException("The turn identifier conflicts with an earlier request.",
                    errorType: TurnConflictErrorType, nonRetryable: true);
            }
            await Workflow.WaitConditionAsync(() => joined.Finished);
            if (joined.Failure is { } failure) throw failure;
            return joined.Result!;
        }

        // Revision authority changes before queue waiting. Caller disconnects never enter here.
        var supersedes = AdmitVoiceRevision(request);
        var attempt = new TurnAttempt(request.TurnId, fingerprint, Workflow.UtcNow + ActionTurnDeadline);
        admittedTurns.Add(request.TurnId, attempt);
        // Handlers start in update-delivery order; each waits for its arrival slot.
        var arrival = nextArrival++;
        await Workflow.WaitConditionAsync(() => arrival == nextToProcess);
        ConciergeVoiceRevisionCheckpoint? checkpoint = null;
        try
        {
            activeAttempt = attempt;
            ThrowIfSuperseded(attempt);
            if (hasEarlierRuns)
            {
                // Earlier runs flushed every turn before Continue-As-New.
                var persisted = await AwaitReplaceableAsync(Workflow.ExecuteActivityAsync(
                    (ConciergeTranscriptActivities activities) =>
                        activities.FindAsync(new FindTranscriptTurnRequest(stayId, request.TurnId)),
                    TranscriptActivityOptions), attempt);
                if (persisted is not null)
                {
                    if (persisted.Superseded) throw SupersededFailure();
                    accepted = new AcceptedTurn(
                        request.VoiceUtterance is { } retryUtterance
                            ? voiceRevisions[retryUtterance.Family].Fingerprint
                            : Fingerprint(persisted.Channel, persisted.GuestMessage),
                        new ConciergeTurnResult(
                            persisted.TurnId,
                            persisted.AssistantMessage,
                            persisted.Sequence,
                            request.VoiceUtterance is { } endChatUtterance
                                ? voiceRevisions[endChatUtterance.Family].EndChatFunctionCall : null,
                            persisted.RideCard));
                    acceptedTurns.Add(request.TurnId, accepted);
                    attempt.Result = MatchOrReject(accepted, fingerprint);
                    return attempt.Result;
                }
            }

            ThrowIfSuperseded(attempt);
            if (supersedes is not null)
            {
                // Only the most recent reversible commit can be withdrawn. A late revision
                // must not roll shared memory/proposals back over a later, unrelated turn.
                if (voiceRevisionCheckpoint is { } previous
                    && previous.TurnId == supersedes && previous.Sequence + 1 == nextSequence)
                {
                    RestoreReversibleState(previous);
                }
                recentTurns.RemoveAll(turn => turn.TurnId == supersedes);
                await MarkSupersededAsync(supersedes);
                ThrowIfSuperseded(attempt);
            }
            checkpoint = CaptureReversibleState(request.TurnId);
            var chatClient = new ChatClientBuilder(new WorkflowOnlyChatClient())
                .UseDurableExecution(options => options.TaskQueue = Workflow.Info.TaskQueue)
                .Build();
            // UseExisting ignores new start arguments. Refresh only from the server-authenticated
            // turn, inside its arrival slot so overlapping turns cannot exchange profiles.
            verifiedGuest = request.VerifiedGuest;

            var actionDeadline = attempt.Deadline;
            ActiveActionDeadline = actionDeadline;

            var receipt = request.VoiceUtterance is { } provenance
                ? voiceRevisions[provenance.Family].ActionReceipt : null;
            HotelRetrievalResult retrieval = new(HotelRetrievalPath.NoEvidence, []);
            try
            {
                if (receipt is null && TryGetRemainingActionTime(actionDeadline, out _))
                {
                    retrieval = await AwaitReplaceableAsync(Workflow.ExecuteActivityAsync(
                        (HotelKnowledgeActivities activities) =>
                            activities.RetrieveAsync(new HotelRetrievalRequest(request.UserMessage)),
                        ActivityOptionsWithDeadline(RetrievalActivityOptions, actionDeadline)), attempt);
                }
            }
            catch (ActivityFailureException)
            {
                // A retrieval failure must not fail the admitted guest update. Continue without
                // hotel evidence only if time remains for the bounded action path.
                retrieval = new HotelRetrievalResult(HotelRetrievalPath.NoEvidence, []);
            }

            var nextStayMemory = StayContinuityMemory.ApplyGuestMessage(
                stayMemory, request.UserMessage, nextSequence).ToArray();
            if (!CanFitStayMemory(nextStayMemory))
            {
                if (receipt is null)
                {
                    throw new ApplicationFailureException(
                        "The stay's guest-reported continuity has reached its safe storage limit.",
                        errorType: UnavailableErrorType,
                        nonRetryable: true);
                }
                // A context limit cannot hide the result of an already-dispatched action.
                nextStayMemory = stayMemory.ToArray();
            }

            IReadOnlyList<ConciergeTranscriptTurn> includedRecentTurns = recentTurns.Where(IsEffectiveTurn).ToArray();
            ActionToolLoopResult actionResult;
            if (receipt is not null)
            {
                actionResult = new(receipt, null);
            }
            else if (!TryGetRemainingActionTime(actionDeadline, out _))
            {
                actionResult = new(ActionDeadlineReply("answer"), null);
            }
            else
            {
                // A reply cut off at the cap (FinishReason.Length) is kept as returned.
                var answerPrompt = ConciergePromptBuilder.BuildMemoryTurn(
                    request.UserMessage,
                    retrieval,
                    nextStayMemory,
                    new ConciergePromptBuilder.PromptBudget(
                        ActionWorkingContextBudgetTokens,
                        ConciergeToolDefinitions.EstimateToolDefinitionTokens(request.EnableEndChatFunction),
                        ActionTools: true),
                    verifiedGuest,
                    includedRecentTurns);
                includedRecentTurns = answerPrompt.RetainedTurns;
                actionResult = await RunActionToolLoopAsync(chatClient, answerPrompt, request, actionDeadline);
            }
            if (!attempt.SideEffectDispatched) ThrowIfSuperseded(attempt);
            var answer = actionResult.Answer;
            var endChatCall = actionResult.EndChatFunctionCall;

            if (string.IsNullOrWhiteSpace(answer))
            {
                throw new ApplicationFailureException(
                    "The model returned an empty response.",
                    errorType: UnavailableErrorType,
                    nonRetryable: true);
            }

            if (actionResult.ConfirmedRequestSequence is { } confirmedRequestSequence)
            {
                nextStayMemory = StayContinuityMemory.RecordActionConfirmation(
                    attempt.Superseded ? stayMemory : nextStayMemory, confirmedRequestSequence).ToArray();
            }

            var turn = new ConciergeTranscriptTurn(
                request.TurnId,
                nextSequence++,
                request.Channel,
                request.UserMessage,
                answer,
                Workflow.UtcNow,
                Superseded: attempt.Superseded,
                RideCard: actionResult.RideCard);
            if (!attempt.Superseded || actionResult.ConfirmedRequestSequence is not null)
            {
                stayMemory.Clear();
                stayMemory.AddRange(nextStayMemory);
            }
            if (!attempt.Superseded)
            {
                recentTurns.Clear();
                recentTurns.AddRange(includedRecentTurns.Where(IsEffectiveTurn));
                // Prompts and Continue-As-New never use the card; keep their budget unchanged.
                recentTurns.Add(turn with { RideCard = null });
            }
            TrimRecentTurnsToStorageBudget();

            var result = new ConciergeTurnResult(
                turn.TurnId, answer, turn.Sequence, endChatCall, turn.RideCard, actionResult.FailureDetail);
            // An exact retry gets the answer, not the diagnostic, which also stays out of Continue-As-New.
            acceptedTurns.Add(turn.TurnId, new AcceptedTurn(fingerprint, result with { FailureDetail = null }));
            runTurns.Add(turn);
            if (attempt.SideEffectDispatched && request.VoiceUtterance is { } actionUtterance)
            {
                voiceRevisions[actionUtterance.Family] =
                    voiceRevisions[actionUtterance.Family] with
                    {
                        ActionReceipt = ConciergeToolResultInterpreter.Limit(answer, 2_048),
                    };
            }
            if (!attempt.Superseded && request.VoiceUtterance is { } completedUtterance)
            {
                voiceRevisions[completedUtterance.Family] =
                    voiceRevisions[completedUtterance.Family] with { EndChatFunctionCall = endChatCall };
            }
            voiceRevisionCheckpoint = !attempt.SideEffectDispatched && !attempt.Superseded
                && request.VoiceUtterance is not null
                    ? checkpoint with { Sequence = turn.Sequence } : null;

            // An offer survives one intervening turn and lapses after the second.
            if (pendingRideProposal is { } existingProposal
                && existingProposal.ProposedSequence + OfferTurnLimit <= turn.Sequence)
            {
                pendingRideProposal = null;
            }
            if (pendingRideCancellationProposal is { } existingCancellation
                && existingCancellation.ProposedSequence + OfferTurnLimit <= turn.Sequence)
            {
                pendingRideCancellationProposal = null;
            }

            pendingPersistence.Add(turn);
            if (attempt.Superseded) throw SupersededFailure();
            attempt.Result = result;
            return result;
        }
        catch (TurnSupersededException)
        {
            if (!attempt.SideEffectDispatched && checkpoint is not null)
            {
                RestoreReversibleState(checkpoint);
            }
            attempt.Failure = SupersededFailure();
            throw attempt.Failure;
        }
        catch (Exception exception)
        {
            attempt.Failure = exception;
            throw;
        }
        finally
        {
            attempt.Finished = true;
            activeAttempt = null;
            ActiveActionDeadline = null;
            nextToProcess++;
        }
    }

    private async Task<ActionToolLoopResult> RunActionToolLoopAsync(
        IChatClient chatClient,
        ConciergePromptBuilder.PromptBuildResult prompt,
        ConciergeTurnSubmission request,
        DateTimeOffset deadline)
    {
        var messages = prompt.Messages;
        var callIds = new HashSet<string>(StringComparer.Ordinal);
        actionTurnStartedUtc = Workflow.UtcNow;
        var modelCalls = 0;
        var toolRounds = 0;
        // From the ride service's decoded result, never the model's wording; the last card-producing
        // call wins. Only answers carry it: deadline, failure and limit replies describe no ride.
        ConciergeRideCard? rideCard = null;
        nearbyPlaces = ConciergePlaceContinuity.Current(nearbyPlaces, Workflow.UtcNow, nextSequence, 6);
        placeClarification = ConciergePlaceContinuity.Current(placeClarification, Workflow.UtcNow, nextSequence, 2);

        if (!prompt.FitActionContext(request.EnableEndChatFunction))
        {
            return new(
                "I couldn't safely fit the available context in this request, so I can't provide an answer or complete an action.",
                null);
        }

        // Spoken history alone ("No ride has been booked.") led models to re-propose instead of
        // confirming; the workflow owns offer state, so it states it explicitly.
        var rideOfferOpen = IsOfferOpen(pendingRideProposal?.ProposedSequence);
        var cancellationOfferOpen = IsOfferOpen(pendingRideCancellationProposal?.ProposedSequence);
        var currentUserIndex = messages.FindLastIndex(message => message.Role == ChatRole.User);
        messages.Insert(
            currentUserIndex < 0 ? messages.Count : currentUserIndex,
            new ChatMessage(
                ChatRole.System,
                RideOfferContextNote(
                    rideOfferOpen ? pendingRideProposal : null,
                    cancellationOfferOpen ? pendingRideCancellationProposal : null,
                    consumedRideOfferSequence + 1 == nextSequence ? consumedRideOffer : null,
                    nextSequence,
                    consumedRideOfferSequence + 1 == nextSequence
                        ? consumedRideConfirmationOutcome
                        : null)));
        if (lastConfirmedCancellation is { } cancellationOutcome)
        {
            messages.Insert(
                messages.FindLastIndex(message => message.Role == ChatRole.User),
                new ChatMessage(ChatRole.System, CancellationOutcomeContextNote(cancellationOutcome)));
        }
        if (nearbyPlaces is not null || placeClarification is not null)
        {
            messages.Insert(messages.FindLastIndex(message => message.Role == ChatRole.User),
                new ChatMessage(ChatRole.System, ConciergePlaceContinuity.Context(nearbyPlaces, placeClarification)));
        }
        if (!prompt.FitActionContext(request.EnableEndChatFunction))
        {
            return new(ActionContextLimitReply(), null);
        }

        ChatResponse response;
        try
        {
            if (await GetModelResponseBeforeDeadlineAsync(
                    chatClient, messages, request.EnableEndChatFunction, deadline) is not { } firstResponse)
            {
                return new(ActionDeadlineReply("answer"), null);
            }

            response = firstResponse;
            modelCalls++;
        }
        catch (ActivityFailureException exception)
        {
            return new(ModelFailureReply(exception, "answer"), null, FailureDetail: FailureDetail(exception));
        }

        while (true)
        {
            ThrowIfSuperseded(activeAttempt!);
            var calls = response.Messages
                .SelectMany(message => message.Contents)
                .OfType<FunctionCallContent>()
                .ToArray();
            if (calls.Length == 0)
            {
                // Only the first response: after a tool round the model is answering from results.
                if (toolRounds > 0
                    || !ConciergeToolCallPolicy.TrySynthesizeWeatherCalls(
                        request.UserMessage, "workflow-weather", out calls))
                {
                    return new(FirstAnswerText(response), null, RideCard: rideCard);
                }
            }
            else if (ConciergeToolCallPolicy.TryGetMultiDayWeather(calls, request.UserMessage, out var weatherPlan))
            {
                calls = ConciergeToolCallPolicy.ExpandToMultiDayWeather(calls, weatherPlan);
            }

            if (calls.Length is >= 2 and <= ConciergeToolCallPolicy.MaxParallelWeatherCalls
                && calls.All(pairCall =>
                    StringComparer.Ordinal.Equals(pairCall.Name, ConciergeToolDefinitions.GetWeather))
                && modelCalls < MaxModelCallsPerTurn
                && toolRounds < MaxToolRoundsPerTurn)
            {
                if (!TryGetRemainingActionTime(deadline, out var weatherTime))
                {
                    return new(ActionDeadlineReply(ConciergeToolDefinitions.GetWeather), null);
                }

                var pair = await AwaitReplaceableAsync(
                    RunWeatherPairAsync(calls, request, weatherTime, callIds), activeAttempt!);
                if (pair is null)
                {
                    return new(UnsafeActionReply, null);
                }

                if (pair.All(item => item.Result.RenderedAnswer is not null))
                {
                    return new(
                        string.Join(
                            StringComparer.Ordinal.Equals(request.Channel, "web") ? "\n\n" : " ",
                            pair.Select(item => item.Result.RenderedAnswer)),
                        null);
                }

                foreach (var (pairCall, pairResult) in pair)
                {
                    messages.Add(CreateFunctionCallMessage(pairCall));
                    messages.Add(CreateFunctionResultMessage(
                        pairCall.CallId,
                        ConciergeToolResultInterpreter.Limit(pairResult.CompactModelContext, 2_048)));
                }

                toolRounds++;
                if (!prompt.FitActionContext(request.EnableEndChatFunction))
                {
                    return new(ActionContextLimitReply(), null);
                }

                try
                {
                    if (await GetModelResponseBeforeDeadlineAsync(
                            chatClient, messages, request.EnableEndChatFunction, deadline) is not { } weatherResponse)
                    {
                        return new(ActionDeadlineReply(ConciergeToolDefinitions.GetWeather), null);
                    }

                    response = weatherResponse;
                    modelCalls++;
                }
                catch (ActivityFailureException exception)
                {
                    return new(ModelFailureReply(exception, "answer"), null, FailureDetail: FailureDetail(exception));
                }

                continue;
            }

            if (calls.Length != 1)
            {
                return new(UnsafeActionReply, null);
            }

            var call = calls[0];
            var callArguments = call.Arguments;
            if (!IsSafeFunctionCallId(call.CallId)
                || !callIds.Add(call.CallId)
                || call.Exception is not null
                || callArguments is null
                || !ArgumentsFit(callArguments))
            {
                return new(UnsafeActionReply, null);
            }

            if (StringComparer.Ordinal.Equals(call.Name, ConciergeEndChat.FunctionName))
            {
                if (!request.EnableEndChatFunction || callArguments.Count != 0 || toolRounds > 0)
                {
                    return new(UnsafeActionReply, null);
                }

                return new(
                    ConciergeEndChat.Farewell,
                    new ConciergeEndChatFunctionCall(call.CallId, call.Name, "{}"));
            }

            if (modelCalls >= MaxModelCallsPerTurn || toolRounds >= MaxToolRoundsPerTurn)
            {
                return new(ActionRoundLimitReply(call.Name), null);
            }

            if (!ConciergeToolCallPolicy.TryValidateArguments(call.Name, callArguments, out var validation))
            {
                return new(validation ?? UnsafeActionReply, null);
            }

            if (IsRideTool(call.Name)
                && !HasTrustedRideIdentity(verifiedGuest))
            {
                return new("I can't access ride actions because the verified stay details are unavailable. Please sign in again.", null);
            }

            if (call.Name is ConciergeToolDefinitions.ProposeRide or ConciergeToolDefinitions.ConfirmRide
                && IsRevisionOfConsumedBooking(request))
            {
                // Deepgram can extend a confirmation after its earlier callback committed. Its new
                // text still needs its own answer, but must not reopen the already-consumed booking.
                return new(ConsumedBookingRevisionReply(), null);
            }

            if (StringComparer.Ordinal.Equals(call.Name, ConciergeToolDefinitions.ConfirmRide)
                && !rideOfferOpen)
            {
                return new(NoRideOfferToConfirmReply, null);
            }

            if (StringComparer.Ordinal.Equals(call.Name, ConciergeToolDefinitions.ConfirmRideCancellation)
                && !cancellationOfferOpen)
            {
                return new(lastConfirmedCancellation is { } confirmedCancellation
                    ? CancellationOutcomeReply(confirmedCancellation) + " To cancel a different ride, please tell me which one."
                    : NoCancellationOfferToConfirmReply, null);
            }

            if (StringComparer.Ordinal.Equals(call.Name, ConciergeToolDefinitions.ProposeRide)
                && IsBareRideAcknowledgement(request.UserMessage)
                && IsDuplicateOfJustConsumedRideOffer(callArguments))
            {
                // Acknowledgements must not reopen the just-booked ride; explicit new requests remain model-driven.
                return new(DuplicateClosedRideProposalReply, null);
            }

            if (!TryGetRemainingActionTime(deadline, out var remaining))
            {
                return new(ActionDeadlineReply(call.Name), null);
            }

            var activityArguments = new Dictionary<string, object?>(callArguments, StringComparer.Ordinal);
            ConciergeCanonicalPlace? selectedDestination = null;
            if (StringComparer.Ordinal.Equals(call.Name, ConciergeToolDefinitions.GetWeather))
            {
                activityArguments.TryAdd("place", null);
                activityArguments.TryAdd("period", "today");
            }
            else if (StringComparer.Ordinal.Equals(call.Name, ConciergeToolDefinitions.ProposeRide))
            {
                activityArguments.TryAdd("pickup", null);
                activityArguments.TryAdd("pickupAt", null);
                nearbyPlaces = ConciergePlaceContinuity.Current(nearbyPlaces, Workflow.UtcNow, nextSequence, 6);
                placeClarification = ConciergePlaceContinuity.Current(placeClarification, Workflow.UtcNow, nextSequence, 2);
                var destination = callArguments["dropoff"]!.ToString()!;
                if (!ConciergePlaceContinuity.TrySelect(destination,
                        nearbyPlaces, placeClarification,
                        [pendingRideProposal?.Dropoff, consumedRideOffer?.Dropoff],
                        out selectedDestination))
                {
                    return new(ConciergePlaceContinuity.Clarify, null);
                }
                if (selectedDestination is not null)
                {
                    activityArguments[ConciergeToolDefinitions.TrustedDestinationKey] =
                        JsonSerializer.Serialize(selectedDestination, ActionJsonOptions);
                }
            }
            else if (StringComparer.Ordinal.Equals(call.Name, ConciergeToolDefinitions.FindNearbyPlaces))
            {
                activityArguments.TryAdd("forRideProposal", false);
                activityArguments.TryAdd("presentation", "alternatives");
            }
            AddTrustedRideContext(call.Name, activityArguments);
            AddTrustedWeatherContext(call.Name, activityArguments);
            // Purpose only steers workflow rendering; the ride service never sees it.
            if (StringComparer.Ordinal.Equals(call.Name, ConciergeToolDefinitions.GetRideHistory))
            {
                activityArguments.Remove("purpose");
            }
            messages.Add(CreateFunctionCallMessage(call));
            if (!prompt.FitActionContext(request.EnableEndChatFunction))
            {
                return new(ActionContextLimitReply(), null);
            }

            string compactResult;
            long? confirmedRequestSequence = null;
            ConciergeRideConfirmationOutcome? confirmedRideOutcome = null;
            var returnedAnswer = false;
            var guestFacingAnswer = string.Empty;
            try
            {
                var durableTool = ConciergeToolDefinitions.CreateDurableInvoker(
                    call.Name,
                    Min(ToolActivityTimeout(call.Name), remaining));
                ThrowIfSuperseded(activeAttempt!);
                var sideEffect = call.Name is ConciergeToolDefinitions.ConfirmRide
                    or ConciergeToolDefinitions.ConfirmRideCancellation;
                // This boundary precedes activity scheduling, not its HTTP acknowledgement.
                // Once crossed, neither revisions nor caller aborts may abandon the outcome.
                if (sideEffect) activeAttempt!.SideEffectDispatched = true;
                if (call.Name == ConciergeToolDefinitions.ProposeRide)
                {
                    // A replacement attempt closes the old offer even when its activity or result
                    // fails. A superseded reversible turn restores this state from its checkpoint.
                    pendingRideProposal = null;
                }
                var invocation = durableTool.InvokeAsync(new AIFunctionArguments(activityArguments)).AsTask();
                var result = sideEffect
                    ? await invocation
                    : await AwaitReplaceableAsync(invocation, activeAttempt!);
                if (!TryReadSerializedToolResult(result, out var serialized)
                    || Encoding.UTF8.GetByteCount(serialized) > MaxActivityToolResultUtf8Bytes)
                {
                    compactResult = ConciergeToolResultInterpreter.ToolFailureReply(call.Name);
                }
                else
                {
                    var interpretation = ConciergeToolResultInterpreter.WithProposalSequence(
                        ConciergeToolResultInterpreter.Decode(call.Name, serialized),
                        request.TurnId,
                        nextSequence);
                    // The model declares why it reads history; only "select" needs ids returned to it.
                    var selectRideFromHistory =
                        StringComparer.Ordinal.Equals(call.Name, ConciergeToolDefinitions.GetRideHistory)
                        && ConciergeToolCallPolicy.TryOptionalString(callArguments, "purpose", 16, out var historyPurpose)
                        && StringComparer.Ordinal.Equals(historyPurpose, "select");
                    var findNearbyForRideProposal =
                        StringComparer.Ordinal.Equals(call.Name, ConciergeToolDefinitions.FindNearbyPlaces)
                        && ConciergeToolCallPolicy.TryBoolean(callArguments, "forRideProposal", out var forRideProposal)
                        && forRideProposal
                        && interpretation.Decoded is ConciergeNearbyPlacesToolResult
                        {
                            Status: "Available",
                            Places.Count: > 0,
                        };
                    returnedAnswer = interpretation.ShouldReturnRenderedToolAnswer
                        && !selectRideFromHistory
                        && !findNearbyForRideProposal;
                    var interpretedResult = interpretation.Interpret(
                        request,
                        callArguments,
                        includeRideIdsForCancellation: selectRideFromHistory,
                        rideContext: new(
                            call.Name == ConciergeToolDefinitions.ConfirmRide ? pendingRideProposal : consumedRideOffer,
                            call.Name == ConciergeToolDefinitions.ConfirmRide
                                ? (interpretation.Decoded as ConciergeRideActionResult)?.Ride?.Id
                                : consumedRideConfirmationOutcome is { Confirmed: true } outcome ? outcome.RideId : null,
                            (nearbyPlaces?.Places ?? []).Concat(placeClarification?.Places ?? []).Distinct().ToArray()));
                    if (interpretation.Decoded is ConciergeNearbyPlacesToolResult { Status: "Available" } nearby)
                    {
                        var singular = callArguments.TryGetValue("presentation", out var presentation)
                            && presentation?.ToString() == "nearest";
                        nearbyPlaces = ConciergePlaceContinuity.Nearby(
                            nearby, Workflow.UtcNow, nextSequence, findNearbyForRideProposal ? 0 : singular ? 1 : 3);
                        placeClarification = null;
                    }
                    else if (interpretation.Decoded is ConciergeNearbyPlacesToolResult)
                    {
                        nearbyPlaces = null;
                        placeClarification = null;
                    }
                    if (interpretation.Decoded is ConciergeRideProposalToolResult { Status: "NeedsClarification" } ambiguous)
                    {
                        placeClarification = ConciergePlaceContinuity.Create(
                            ambiguous.CanonicalCandidates ?? [], Workflow.UtcNow.AddMinutes(5), nextSequence, 3);
                        if (ambiguous.ClarifyingPickup && nearbyPlaces is not null)
                        {
                            nearbyPlaces = nearbyPlaces with { PresentedCount = 0, FocusIndex = null };
                        }
                    }
                    compactResult = interpretedResult.CompactModelContext;
                    guestFacingAnswer = interpretedResult.GuestFacingText;
                    if (call.Name == ConciergeToolDefinitions.GetRideHistory
                        && lastConfirmedCancellation is { } recalledCancellation
                        && interpretation.Decoded is ConciergeRideActionResult { Message: null, Rides.Count: 0 }
                        && ConciergeToolCallPolicy.TryOptionalString(callArguments, "when", 16, out var historyWhen)
                        && historyWhen is null or "upcoming")
                    {
                        // Cancelled rides are intentionally absent from upcoming history. Keep that
                        // empty read from hiding a confirmed action, including in rendered replies.
                        compactResult += " " + CancellationOutcomeReply(recalledCancellation);
                        guestFacingAnswer = compactResult;
                    }
                    if (call.Name == ConciergeToolDefinitions.GetRideHistory
                        && !selectRideFromHistory
                        && ConciergeToolCallPolicy.TryOptionalString(callArguments, "when", 16, out var cardWhen)
                        && cardWhen is null or "upcoming"
                        && interpretation.Decoded is ConciergeRideActionResult { Message: null } upcoming
                        && ConciergeRideCards.Scheduled(upcoming.Rides) is { } scheduledCard)
                    {
                        rideCard = scheduledCard;
                    }
                    // A sentence saying "canceled" is not a confirmation. Bind the activity's
                    // explicit success to the workflow-owned offer before retaining its outcome.
                    if (call.Name == ConciergeToolDefinitions.ConfirmRideCancellation
                        && pendingRideCancellationProposal is { } cancelled
                        && interpretation.Decoded is ConciergeRideActionResult cancellationResult
                        && cancellationResult.ConfirmedCancellationRideId == cancelled.RideId)
                    {
                        lastConfirmedCancellation = new ConciergeRideCancellationOutcome(
                            cancelled.RideId, ConciergeToolResultInterpreter.Limit(cancelled.Dropoff, 200), cancelled.PickupAt,
                            request.TurnId, nextSequence);
                        confirmedRequestSequence = cancelled.ProposedSequence;
                    }
                    if (call.Name == ConciergeToolDefinitions.ConfirmRide
                        && pendingRideProposal is { } booked
                        && interpretation.Decoded is ConciergeRideActionResult
                        {
                            Message: null,
                            Ride: { Id: var bookedRideId } bookedRide,
                        }
                        && bookedRideId != Guid.Empty)
                    {
                        confirmedRequestSequence = booked.ProposedSequence;
                        confirmedRideOutcome = new(
                            true,
                            bookedRideId,
                            bookedRide.Status,
                            bookedRide.Pickup,
                            bookedRide.Dropoff,
                            bookedRide.PickupAt,
                            null,
                            request.TurnId,
                            nextSequence);
                        rideCard = ConciergeRideCards.Booked(bookedRide);
                    }
                    // A new offer replaces any other open offer.
                    if (interpretedResult.ProposedStateChange is { } proposal)
                    {
                        pendingRideProposal = proposal;
                        var clarificationFocus = selectedDestination is null
                            ? -1 : placeClarification?.Places.ToList().IndexOf(selectedDestination) ?? -1;
                        placeClarification = clarificationFocus >= 0
                            ? placeClarification! with { FocusIndex = clarificationFocus } : null;
                        if (selectedDestination is not null && nearbyPlaces is { } knownPlaces)
                        {
                            var focus = knownPlaces.Places.ToList().IndexOf(selectedDestination);
                            if (focus >= 0) nearbyPlaces = knownPlaces with { FocusIndex = focus };
                        }
                        pendingRideCancellationProposal = null;
                        if (lastConfirmedCancellation is { } previousCancellation
                            && proposal.PickupAtUtc == previousCancellation.PickupAt.ToUniversalTime()
                            && ConciergeToolResultInterpreter.HaveSameSpokenPlaceName(
                                proposal.Dropoff.Name, previousCancellation.Dropoff))
                        {
                            // A model may interpret "don't cancel" as wanting a replacement. Never
                            // present that offer as restoration. A shared spoken label is a reminder
                            // to mention the earlier outcome, never proof of identity or authority.
                            var newOffer = interpretation.Decoded is ConciergeRideProposalToolResult { ExistingRides.Count: > 0 }
                                ? compactResult
                                : $"Would you like a new booking for {ConciergeToolResultInterpreter.DescribeRideOffer(proposal)}? No new ride has been booked.";
                            compactResult = $"Your earlier booking for {ConciergeToolResultInterpreter.DescribeCancelledRide(previousCancellation)} is already cancelled. {newOffer}";
                            guestFacingAnswer = compactResult;
                        }
                    }
                    if (interpretedResult.ProposedCancellationStateChange is { } cancellationProposal)
                    {
                        pendingRideCancellationProposal = cancellationProposal;
                        pendingRideProposal = null;
                    }
                }
            }
            catch (ActivityFailureException)
            {
                // Weather retries transient failures; other tools run once. Any final failure,
                // including a provider timeout, gets the curated reply instead of failing the turn.
                compactResult = ConciergeToolResultInterpreter.ToolFailureReply(call.Name);
            }
            catch (JsonException)
            {
                compactResult = ConciergeToolResultInterpreter.ToolFailureReply(call.Name);
            }
            catch (ArgumentException)
            {
                compactResult = ConciergeToolResultInterpreter.ToolFailureReply(call.Name);
            }

            // Proposal outcomes are workflow-rendered, including failures; a model must not invent
            // a destination clarification or imply that an offer exists when the tool failed.
            if (StringComparer.Ordinal.Equals(call.Name, ConciergeToolDefinitions.ProposeRide)
                && !returnedAnswer)
            {
                returnedAnswer = true;
                guestFacingAnswer = ConciergeToolResultInterpreter.ToolFailureReply(call.Name);
            }

            compactResult = ConciergeToolResultInterpreter.Limit(compactResult, 2_048);
            messages.Add(CreateFunctionResultMessage(call.CallId, compactResult));
            toolRounds++;

            // A confirmation attempt consumes its offer whether it succeeds or returns an uncertain
            // outcome; history and status are the safe recovery tools.
            if (StringComparer.Ordinal.Equals(call.Name, ConciergeToolDefinitions.ConfirmRide))
            {
                consumedRideOffer = pendingRideProposal;
                consumedRideOfferSequence = nextSequence;
                consumedRideOfferSpokenRequestKey = request.Channel == "voice"
                    ? SpokenRequestKey(request.TurnId) : null;
                consumedRideConfirmationOutcome = confirmedRideOutcome
                    ?? new(
                        false,
                        null,
                        null,
                        null,
                        null,
                        null,
                        ConciergeToolResultInterpreter.Limit(compactResult, 1_000),
                        request.TurnId,
                        nextSequence);
                pendingRideProposal = null;
                pendingRideCancellationProposal = null;
            }
            if (StringComparer.Ordinal.Equals(call.Name, ConciergeToolDefinitions.ConfirmRideCancellation))
            {
                // A different ride's known success cannot resolve this attempted cancellation.
                // Keep a same-ride confirmation authoritative even if a later attempt is uncertain.
                if (pendingRideCancellationProposal is { } attemptedCancellation
                    && lastConfirmedCancellation?.RideId != attemptedCancellation.RideId)
                {
                    lastConfirmedCancellation = null;
                }
                pendingRideCancellationProposal = null;
                pendingRideProposal = null;
            }

            // Once dispatched, a confirmation may have committed even if its activity timed out.
            // Preserve its rendered result or curated uncertain outcome without another model call
            // or a generic deadline/context reply replacing it. Its offer has already been consumed.
            if (call.Name is ConciergeToolDefinitions.ConfirmRide
                or ConciergeToolDefinitions.ConfirmRideCancellation)
            {
                return new(returnedAnswer ? guestFacingAnswer : compactResult, null, confirmedRequestSequence, rideCard);
            }

            if (!prompt.FitActionContext(request.EnableEndChatFunction))
            {
                return new(ActionContextLimitReply(), null);
            }

            if (!TryGetRemainingActionTime(deadline, out _))
            {
                return new(ActionDeadlineReply(call.Name), null);
            }

            if (returnedAnswer)
            {
                return new(guestFacingAnswer, null, RideCard: rideCard);
            }

            try
            {
                if (await GetModelResponseBeforeDeadlineAsync(
                        chatClient, messages, request.EnableEndChatFunction, deadline) is not { } toolResponse)
                {
                    return new(ActionDeadlineReply(call.Name), null);
                }

                response = toolResponse;
                modelCalls++;
            }
            catch (ActivityFailureException exception)
            {
                return new(ModelFailureReply(exception, call.Name), null, FailureDetail: FailureDetail(exception));
            }
        }
    }

    /// <summary>
    /// Returns null when the turn deadline passes first. The outbound interceptor also bounds
    /// the durable activity's queueing, retries, and backoff with ScheduleToClose. Keep the timer
    /// so the guest need not wait for delivery of the activity timeout; the losing model call
    /// has no side effects and its result is never read.
    /// </summary>
    private async Task<ChatResponse?> GetModelResponseBeforeDeadlineAsync(
        IChatClient chatClient,
        List<ChatMessage> messages,
        bool enableEndChat,
        DateTimeOffset deadline)
    {
        if (!TryGetRemainingActionTime(deadline, out var remaining))
        {
            return null;
        }

        ThrowIfSuperseded(activeAttempt!);
        var modelCall = AwaitReplaceableAsync(
            chatClient.GetResponseAsync(messages, CreateActionModelOptions(enableEndChat, remaining)),
            activeAttempt!);
        using var timerCancellation = CancellationTokenSource.CreateLinkedTokenSource(Workflow.CancellationToken);
        var deadlineTimer = Workflow.DelayAsync(remaining, timerCancellation.Token);
        if (await Workflow.WhenAnyAsync(modelCall, deadlineTimer) != modelCall)
        {
            return null;
        }

        timerCancellation.Cancel();
        return await modelCall;
    }

    private ChatOptions CreateActionModelOptions(bool enableEndChat, TimeSpan remaining)
    {
        var options = new ChatOptions
        {
            MaxOutputTokens = AnswerMaxOutputTokens,
            AllowMultipleToolCalls = false,
        };
        options.AdditionalProperties ??= [];
        options.AdditionalProperties[ConciergeToolDefinitions.ModelOptionKey] = "true";
        // The model activity publishes this turn's partial text under its ID (ADR 0015). An activity
        // input only, so replay is unaffected.
        options.AdditionalProperties[ConciergeAnswerStream.ModelOptionKey] = activeAttempt!.TurnId;
        if (enableEndChat)
        {
            options.AdditionalProperties[ConciergeEndChat.ModelOptionKey] = "true";
        }

        // An equal share per attempt leaves room for retries; ScheduleToClose and the caller's
        // deadline timer bound the total, including backoff and queue time.
        var perAttempt = TimeSpan.FromTicks(
            Math.Max(remaining.Ticks / ModelCallAttempts, TimeSpan.FromMilliseconds(1).Ticks));
        options.WithActivityTimeout(Min(ModelActivityTimeout, perAttempt));
        options.WithMaxRetryAttempts(ModelCallAttempts);
        return options;
    }

    /// <summary>An offer made on sequence P can be confirmed on P+1 or P+2.</summary>
    private bool IsOfferOpen(long? proposedSequence) =>
        proposedSequence is { } proposed
        && nextSequence > proposed
        && nextSequence - proposed <= OfferTurnLimit;

    private bool IsDuplicateOfJustConsumedRideOffer(IDictionary<string, object?> arguments)
    {
        if (consumedRideOfferSequence + 1 != nextSequence
            || consumedRideOffer is not { } consumed
            || !ConciergeToolCallPolicy.TryOptionalString(arguments, "pickup", 200, out var pickup)
            || !ConciergeToolCallPolicy.TryOptionalString(arguments, "dropoff", 200, out var dropoff)
            || !TryBooleanArgument(arguments, "immediate", out var immediate)
            || !ConciergeToolCallPolicy.TryOptionalString(arguments, "pickupAt", 64, out var pickupAt)
            || !SameRidePlace(pickup ?? "Hotel ZGY", consumed.Pickup.Name)
            || !SameRidePlace(dropoff ?? string.Empty, consumed.Dropoff.Name)
            || immediate != consumed.Immediate)
        {
            return false;
        }

        if (immediate)
        {
            return pickupAt is null && consumed.PickupAtUtc is null;
        }

        return pickupAt is not null
            && consumed.PickupAtUtc is { } consumedPickupAt
            && DateTimeOffset.TryParse(
                pickupAt,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var requestedPickupAt)
            && requestedPickupAt == consumedPickupAt;
    }

    private bool IsRevisionOfConsumedBooking(ConciergeTurnSubmission request)
    {
        // Side-effect provenance must survive prompt/history trimming and Continue-As-New.
        return consumedRideOffer is not null
            && request.Channel == "voice"
            && SpokenRequestKey(request.TurnId) is { } key
            && StringComparer.Ordinal.Equals(key, consumedRideOfferSpokenRequestKey);
    }

    private string ConsumedBookingRevisionReply()
    {
        if (consumedRideConfirmationOutcome is { Confirmed: true } confirmed)
        {
            return ConciergeToolResultInterpreter.DescribeConfirmedRideBooking(confirmed);
        }

        if (consumedRideConfirmationOutcome is { Confirmed: false, Message: { Length: > 0 } uncertain })
        {
            return uncertain;
        }

        // Continue-As-New input from before outcome retention can still carry the authoritative
        // transcript reply for this exact consumed sequence. If it was pruned, report uncertainty.
        var transcript = runTurns.Concat(recentTurns)
            .LastOrDefault(turn => turn.Sequence == consumedRideOfferSequence);
        return !string.IsNullOrWhiteSpace(transcript?.AssistantMessage)
            ? transcript.AssistantMessage
            : "I couldn't verify whether the ride was booked. Please check ride status or history before trying again.";
    }

    private static string? SpokenRequestKey(string turnId)
    {
        // Match the server-generated voice session/connection/ordinal, not the text hash. Keep
        // full turn IDs for answer deduplication so revised questions still receive new answers.
        var parts = turnId.Split(':');
        return parts.Length == 5
            && parts[0] == "v"
            && Guid.TryParseExact(parts[1], "N", out _)
            && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var connection)
            && connection > 0
            && int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal)
            && ordinal > 0
            && parts[4].Length == 64
            && parts[4].All(Uri.IsHexDigit)
                ? string.Join(':', parts.Take(4))
                : null;
    }

    private static bool IsBareRideAcknowledgement(string message)
    {
        // This guard is deliberately an exact, small allowlist: it distinguishes a bare reply from
        // an explicit new booking request without deciding whether the model should select a tool.
        var normalized = new string(message
            .Select(character =>
                char.IsLetterOrDigit(character) || char.IsWhiteSpace(character)
                    ? char.ToLowerInvariant(character)
                    : ' ')
            .ToArray());
        var words = normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words) is
            "yes" or "yes please"
            or "yeah" or "yeah please"
            or "yep" or "yep please"
            or "sure" or "sure please"
            or "okay" or "okay please"
            or "ok" or "ok please";
    }

    private static bool SameRidePlace(string requested, string offered)
    {
        static string Normalize(string value) =>
            string.Join(
                ", ",
                value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Take(2));

        var requestedName = Normalize(requested);
        var offeredName = Normalize(offered);
        return StringComparer.OrdinalIgnoreCase.Equals(requestedName, offeredName)
            || offeredName.StartsWith(requestedName + ",", StringComparison.OrdinalIgnoreCase)
            || requestedName.StartsWith(offeredName + ",", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryBooleanArgument(
        IDictionary<string, object?> arguments,
        string key,
        out bool value)
    {
        value = false;
        if (!arguments.TryGetValue(key, out var raw))
        {
            return false;
        }

        if (raw is bool boolean)
        {
            value = boolean;
            return true;
        }

        if (raw is JsonElement { ValueKind: JsonValueKind.True })
        {
            value = true;
            return true;
        }

        if (raw is JsonElement { ValueKind: JsonValueKind.False })
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// States the workflow-owned offer, with its details, in the model context so the model can
    /// recognize an acceptance in any wording. The ride id stays workflow-owned.
    /// </summary>
    internal static string RideOfferContextNote(
        ConciergeRideProposal? rideOffer,
        ConciergeRideCancellationProposal? cancellationOffer,
        ConciergeRideProposal? justConsumedRideOffer,
        long nextSequence,
        ConciergeRideConfirmationOutcome? justConsumedRideOutcome = null)
    {
        static string Age(long proposedSequence, long next) =>
            next - proposedSequence == 1 ? "in your previous reply" : "two replies ago";

        if (rideOffer is { } ride)
        {
            return $"Ride offer state: you offered {ConciergeToolResultInterpreter.DescribeRideOffer(ride)} {Age(ride.ProposedSequence, nextSequence)}. Unbooked; accept unchanged: ConfirmRide({{}}). Changes/another ride: ProposeRide.";
        }

        if (cancellationOffer is { } cancellation)
        {
            return $"Cancellation offer state: you offered to cancel {ConciergeToolResultInterpreter.DescribeRideCancellationOffer(cancellation)} {Age(cancellation.ProposedSequence, nextSequence)}. Nothing is cancelled yet. If the guest's current message accepts this cancellation in any wording, call ConfirmRideCancellation with no arguments. Do not look up rides or propose this cancellation again unless they name a different ride.";
        }

        if (justConsumedRideOffer is { } consumed)
        {
            var outcome = justConsumedRideOutcome switch
            {
                { Confirmed: true } confirmed =>
                    $"Confirmed ({confirmed.Status}); receipt is in the prior answer.",
                { Confirmed: false } =>
                    "Outcome uncertain; don't claim success; advise checking ride status.",
                _ => "Outcome unavailable; don't claim success; check ride status.",
            };
            return $"Ride offer state: {ConciergeToolResultInterpreter.DescribeRideOffer(consumed)}; closed. {outcome} Don't repeat. New ride: ProposeRide; no open offer: no confirmation.";
        }

        return "Ride offer state: no ride or cancellation offer awaits confirmation. Do not call ConfirmRide or ConfirmRideCancellation.";
    }

    internal static string CancellationOutcomeContextNote(ConciergeRideCancellationOutcome outcome) =>
        $"Last confirmed cancellation: {ConciergeToolResultInterpreter.Limit(outcome.Dropoff, 50)}, {outcome.PickupAt.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture)}. 'Don't cancel' cannot undo it; report already cancelled. Rebook only on an explicit new ride request.";

    private static string CancellationOutcomeReply(ConciergeRideCancellationOutcome outcome) =>
        $"Your last confirmed cancellation was {ConciergeToolResultInterpreter.DescribeCancelledRide(outcome)}. That ride is already cancelled.";

    /// <summary>
    /// Anchors "today" to the turn start so parallel calls, and retries that run after hotel-local
    /// midnight, resolve the same calendar the guest asked about.
    /// </summary>
    private void AddTrustedWeatherContext(string toolName, IDictionary<string, object?> arguments)
    {
        if (StringComparer.Ordinal.Equals(toolName, ConciergeToolDefinitions.GetWeather))
        {
            arguments[ConciergeToolDefinitions.TrustedTurnStartedUtcKey] =
                actionTurnStartedUtc.ToString("O", CultureInfo.InvariantCulture);
        }
    }

    private void AddTrustedRideContext(string toolName, IDictionary<string, object?> arguments)
    {
        if (!IsRideTool(toolName) || verifiedGuest is not { } guest)
        {
            return;
        }

        arguments[ConciergeToolDefinitions.TrustedGuestIdKey] = guest.GuestId.ToString("D");
        arguments[ConciergeToolDefinitions.TrustedStayIdKey] = guest.StayId.ToString("D");
        arguments[ConciergeToolDefinitions.TrustedCheckoutDateKey] =
            guest.CheckoutDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (StringComparer.Ordinal.Equals(toolName, ConciergeToolDefinitions.ConfirmRide)
            && pendingRideProposal is { } proposal)
        {
            arguments[ConciergeToolDefinitions.TrustedProposalKey] =
                JsonSerializer.Serialize(proposal, ActionJsonOptions);
            arguments["proposalId"] = proposal.ProposalId.ToString("D");
        }

        if (StringComparer.Ordinal.Equals(toolName, ConciergeToolDefinitions.ConfirmRideCancellation)
            && pendingRideCancellationProposal is { } cancellationProposal)
        {
            arguments["rideId"] = cancellationProposal.RideId.ToString("D");
        }
    }

    private static bool ArgumentsFit(IDictionary<string, object?> arguments)
        => ConciergeToolCallPolicy.ArgumentsFit(arguments);

    private static bool TryReadSerializedToolResult(object? result, out string serialized)
    {
        switch (result)
        {
            case string text:
                serialized = text;
                return true;
            case JsonElement { ValueKind: JsonValueKind.String } element:
                serialized = element.GetString() ?? string.Empty;
                return serialized.Length > 0;
            default:
                serialized = string.Empty;
                return false;
        }
    }

    private static bool IsSafeFunctionCallId(string? callId) =>
        ConciergeToolCallPolicy.IsSafeFunctionCallId(callId);

    private static bool IsRideTool(string name) =>
        ConciergeToolCallPolicy.IsRideTool(name);

    private static bool HasTrustedRideIdentity(ConciergeVerifiedGuest? guest) =>
        ConciergeToolCallPolicy.HasTrustedRideIdentity(guest);

    private bool TryGetRemainingActionTime(DateTimeOffset deadline, out TimeSpan remaining)
    {
        remaining = deadline - Workflow.UtcNow;
        return remaining > TimeSpan.FromMilliseconds(1);
    }

    // The bounded model client ends a slow call just inside its activity attempt and reports a
    // TimeoutException, so Temporal records an application failure rather than an activity timeout.
    private static bool IsActivityTimeout(ActivityFailureException exception) =>
        exception.InnerException is TimeoutFailureException
            or ApplicationFailureException { ErrorType: nameof(TimeoutException) }
        || StringComparer.Ordinal.Equals(exception.RetryState.ToString(), "Timeout");

    private static ActivityOptions ActivityOptionsWithDeadline(
        ActivityOptions options,
        DateTimeOffset deadline) =>
        BoundActivityOptionsToDeadline(options, Workflow.UtcNow, deadline);

    internal static ActivityOptions BoundActivityOptionsToDeadline(
        ActivityOptions options,
        DateTimeOffset now,
        DateTimeOffset deadline)
    {
        var remaining = deadline - now;
        if (remaining <= TimeSpan.FromMilliseconds(1))
        {
            remaining = TimeSpan.FromMilliseconds(1);
        }

        // Preserve task queue, heartbeat, cancellation, summary, and other adapter options.
        var bounded = (ActivityOptions)options.Clone();
        bounded.StartToCloseTimeout = Min(options.StartToCloseTimeout ?? remaining, remaining);
        bounded.ScheduleToCloseTimeout = Min(options.ScheduleToCloseTimeout ?? remaining, remaining);
        return bounded;
    }

    // Per-attempt StartToClose for each tool, derived from the inner HTTP bounds and capped by the
    // remaining turn time (the 180-second turn deadline stays the safety ceiling). Provisional until
    // measured. Location requests: LocationProviderHttp.RequestTimeout (5 s) for connect, headers,
    // and body, no HTTP retries. Ride requests: ConciergeActionToolService.RideClientTimeout (11 s:
    // 10 s total request budget plus body buffering). Every value leaves at least the activity
    // cleanup margin (0.5 s) plus dispatch slack after the worst-case inner time.
    internal static TimeSpan ToolActivityTimeout(string name) => name switch
    {
        // Per attempt: at most one Geoapify place lookup plus one Open-Meteo request (10 s). Temporal
        // owns weather retries: three attempts with 1 s and 2 s backoff take at most about 39 s.
        ConciergeToolDefinitions.GetWeather => TimeSpan.FromSeconds(12),
        // One Geoapify Places request (5 s, single attempt); no geocoding step.
        ConciergeToolDefinitions.FindNearbyPlaces => TimeSpan.FromSeconds(8),
        // One named-place Geoapify Places request (5 s, single attempt).
        ConciergeToolDefinitions.GetPlaceOpeningHours => TimeSpan.FromSeconds(8),
        // Up to two place lookups plus one routing request (15 s), single attempt.
        ConciergeToolDefinitions.GetRoute => TimeSpan.FromSeconds(18),
        // GetRoute's budget plus one active-ride read (11 s) for additional-booking warnings.
        ConciergeToolDefinitions.ProposeRide => TimeSpan.FromSeconds(30),
        // One ride-service call: GETs retry inside the HTTP budget, DELETE is sent once (11 s).
        ConciergeToolDefinitions.GetRideStatus
            or ConciergeToolDefinitions.GetRideHistory
            or ConciergeToolDefinitions.ProposeRideCancellation
            or ConciergeToolDefinitions.ConfirmRideCancellation
            => TimeSpan.FromSeconds(12),
        // One POST, sent once (11 s). A timeout is reported as an unknown outcome, never resent.
        ConciergeToolDefinitions.ConfirmRide => TimeSpan.FromSeconds(15),
        _ => TimeSpan.FromSeconds(1),
    };

    private static string ActionDeadlineReply(string operation) =>
        operation is ConciergeToolDefinitions.ConfirmRide
            or ConciergeToolDefinitions.ConfirmRideCancellation
            or ConciergeToolDefinitions.ProposeRide
            or ConciergeToolDefinitions.ProposeRideCancellation
            ? "I couldn't finish the ride request before the request deadline. I haven't confirmed a booking; check ride history if you had already confirmed."
            : "I couldn't finish that live lookup before the request deadline, so I can't provide a reliable result.";

    // Model activities retry transient failures; this reply covers exhausted attempts so the guest
    // gets an answer instead of a failed turn.
    private static string ModelFailureReply(ActivityFailureException exception, string operation) =>
        IsActivityTimeout(exception)
            ? ActionDeadlineReply(operation)
            : operation is ConciergeToolDefinitions.ConfirmRide
                or ConciergeToolDefinitions.ConfirmRideCancellation
                ? "I couldn't reach the concierge service to finish that just now. Check your rides before trying again, in case the request went through."
                : "I couldn't reach the concierge service just now. Please try again in a moment.";

    private const int MaxFailureDetailLength = 4_000;

    /// <summary>
    /// The model call's failure chain (type, Temporal error type, message) behind a fallback reply. Built
    /// from history, so it replays identically; only Development pages show it.
    /// </summary>
    private static string FailureDetail(Exception exception)
    {
        var lines = new List<string>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            var errorType = current is ApplicationFailureException { ErrorType: { Length: > 0 } type } ? $" ({type})" : "";
            lines.Add($"{(lines.Count == 0 ? "" : "caused by ")}{current.GetType().Name}{errorType}: {current.Message}");
        }

        var detail = string.Join("\n", lines);
        return detail.Length > MaxFailureDetailLength ? detail[..MaxFailureDetailLength] : detail;
    }

    private const string UnsafeActionReply =
        "I couldn't safely process that action request. Please clarify what you would like me to do.";

    private static string ActionRoundLimitReply(string toolName) =>
        toolName is ConciergeToolDefinitions.ProposeRide
            or ConciergeToolDefinitions.ConfirmRide
            or ConciergeToolDefinitions.ProposeRideCancellation
            or ConciergeToolDefinitions.ConfirmRideCancellation
            ? "I couldn't finish the ride request within the allowed steps. No new ride was booked."
            : "I couldn't finish that live lookup within the allowed steps, so I can't provide a reliable result.";

    private static string ActionContextLimitReply() =>
        "I couldn't safely include the action details within the request context, so I can't provide a reliable result or complete an action.";

    private const string NoRideOfferToConfirmReply =
        "No ride was booked because there's no ride offer waiting for confirmation. Ask me to book the ride again and I'll set up a new one.";

    private const string DuplicateClosedRideProposalReply =
        "That ride offer is already closed, so I haven't prepared it again. If you'd like a new ride, please tell me the destination, pickup, or time.";

    private const string NoCancellationOfferToConfirmReply =
        "No new cancellation was submitted. There's no ride cancellation offer waiting for confirmation. Ask me to check the ride's current status before trying again.";

    private sealed record WeatherPairResult(string CompactModelContext, string? RenderedAnswer);

    /// <summary>
    /// Runs several GetWeather calls (e.g. one per day for "the next three days") in parallel as
    /// one tool round. Returns null when the calls are unsafe so the caller can refuse them.
    /// </summary>
    private async Task<IReadOnlyList<(FunctionCallContent Call, WeatherPairResult Result)>?> RunWeatherPairAsync(
        FunctionCallContent[] calls,
        ConciergeTurnSubmission request,
        TimeSpan remaining,
        HashSet<string> callIds)
    {
        foreach (var call in calls)
        {
            if (!IsSafeFunctionCallId(call.CallId)
                || !callIds.Add(call.CallId)
                || call.Exception is not null
                || call.Arguments is null
                || !ArgumentsFit(call.Arguments)
                || !ConciergeToolCallPolicy.TryValidateArguments(call.Name, call.Arguments, out _))
            {
                return null;
            }
        }

        var results = await Workflow.WhenAllAsync(
            calls.Select(call => RunWeatherCallAsync(call, request, remaining)));
        return calls.Zip(results).ToArray();
    }

    private async Task<WeatherPairResult> RunWeatherCallAsync(
        FunctionCallContent call,
        ConciergeTurnSubmission request,
        TimeSpan remaining)
    {
        var failure = new WeatherPairResult(
            ConciergeToolResultInterpreter.ToolFailureReply(ConciergeToolDefinitions.GetWeather), null);
        var arguments = new Dictionary<string, object?>(call.Arguments!, StringComparer.Ordinal);
        arguments.TryAdd("place", null);
        arguments.TryAdd("period", "today");
        AddTrustedWeatherContext(ConciergeToolDefinitions.GetWeather, arguments);
        try
        {
            var result = await ConciergeToolDefinitions.CreateDurableInvoker(
                    ConciergeToolDefinitions.GetWeather,
                    Min(ToolActivityTimeout(ConciergeToolDefinitions.GetWeather), remaining))
                .InvokeAsync(new AIFunctionArguments(arguments));
            if (!TryReadSerializedToolResult(result, out var serialized)
                || Encoding.UTF8.GetByteCount(serialized) > MaxActivityToolResultUtf8Bytes)
            {
                return failure;
            }

            var interpretation = ConciergeToolResultInterpreter.Decode(
                ConciergeToolDefinitions.GetWeather, serialized);
            var interpreted = interpretation.Interpret(request, call.Arguments!, includeRideIdsForCancellation: false);
            return new(
                interpreted.CompactModelContext,
                interpretation.ShouldReturnRenderedToolAnswer ? interpreted.GuestFacingText : null);
        }
        catch (ActivityFailureException)
        {
            return failure;
        }
        catch (JsonException)
        {
            return failure;
        }
        catch (ArgumentException)
        {
            return failure;
        }
    }

    private static ChatMessage CreateFunctionCallMessage(FunctionCallContent call) =>
        new(ChatRole.Assistant, [new FunctionCallContent(call.CallId, call.Name, call.Arguments)]);

    private static ChatMessage CreateFunctionResultMessage(string callId, string result) =>
        new(ChatRole.Tool, [new FunctionResultContent(callId, result)]);

    private sealed record ActionToolLoopResult(
        string AssistantMessage,
        ConciergeEndChatFunctionCall? EndChatFunctionCall,
        long? ConfirmedRequestSequence = null,
        ConciergeRideCard? RideCard = null,
        string? FailureDetail = null)
    {
        public string Answer => AssistantMessage;
    }

    /// <summary>Effective stay history. Superseded text remains immutable in the persisted audit.</summary>
    [WorkflowUpdate(ConciergeWorkflowNames.HistoryUpdate)]
    public async Task<ConciergeHistoryResult> GetHistoryAsync()
    {
        var persisted = await Workflow.ExecuteActivityAsync(
            (ConciergeTranscriptActivities activities) =>
                activities.ReadAsync(new ReadTranscriptRequest(stayId)),
            TranscriptActivityOptions);
        var turnsById = persisted.ToDictionary(turn => turn.TurnId, StringComparer.Ordinal);
        foreach (var turn in runTurns)
        {
            turnsById[turn.TurnId] = turn;
        }

        return new ConciergeHistoryResult(
            turnsById.Values.Where(IsEffectiveTurn).OrderBy(turn => turn.Sequence).ToArray());
    }

    private bool IsEffectiveTurn(ConciergeTranscriptTurn turn) =>
        !turn.Superseded
        && !voiceRevisions.Values.Any(revision => revision.SupersededTurnIds.Contains(turn.TurnId));

    private void RejectSupersededRetry(ConciergeTurnSubmission request)
    {
        if (request.VoiceUtterance is { } utterance
            && voiceRevisions.TryGetValue(utterance.Family, out var revision))
        {
            if (revision.SupersededTurnIds.Contains(request.TurnId)) throw SupersededFailure();
            if (revision.LatestTurnId == request.TurnId
                && revision.Fingerprint != Fingerprint(request.Channel, request.UserMessage, request.EnableEndChatFunction))
            {
                throw new ApplicationFailureException("The turn identifier conflicts with an earlier request.",
                    errorType: TurnConflictErrorType, nonRetryable: true);
            }
        }
    }

    private string? AdmitVoiceRevision(ConciergeTurnSubmission request)
    {
        if (request.VoiceUtterance is not { } utterance) return null;
        if (!voiceRevisions.TryGetValue(utterance.Family, out var revision))
        {
            if (voiceRevisions.Count >= MaxVoiceRevisionFamilies)
            {
                throw new ApplicationFailureException(
                    "This stay has reached its safe voice revision capacity. Please use web chat or contact the front desk.",
                    errorType: UnavailableErrorType, nonRetryable: true);
            }
            var initial = new ConciergeVoiceRevision(utterance.Family, request.TurnId, [],
                Fingerprint(request.Channel, request.UserMessage, request.EnableEndChatFunction));
            RequireRevisionCapacity(initial);
            voiceRevisions.Add(utterance.Family, initial);
            return null;
        }
        if (revision.LatestTurnId == request.TurnId) return null;
        if (revision.SupersededTurnIds.Count >= MaxRevisionsPerUtterance - 1)
        {
            throw new ApplicationFailureException(
                "This utterance has too many revisions. Please start a new question.",
                errorType: UnavailableErrorType, nonRetryable: true);
        }

        var replacement = revision with
        {
            LatestTurnId = request.TurnId,
            SupersededTurnIds = [.. revision.SupersededTurnIds, revision.LatestTurnId],
            Fingerprint = Fingerprint(request.Channel, request.UserMessage, request.EnableEndChatFunction),
            EndChatFunctionCall = null,
        };
        RequireRevisionCapacity(replacement);
        voiceRevisions[utterance.Family] = replacement;
        if (admittedTurns.TryGetValue(revision.LatestTurnId, out var obsolete))
        {
            obsolete.Superseded = true;
        }
        return revision.LatestTurnId;
    }

    private void RequireRevisionCapacity(ConciergeVoiceRevision candidate)
    {
        // Bound the CAN payload as well as record counts. Never evict a tombstone or receipt
        // to make room: doing so could reactivate an old hash or repeat an uncertain action.
        var state = voiceRevisions.Values.Where(revision => revision.Family != candidate.Family)
            .Append(candidate).ToArray();
        if (JsonSerializer.SerializeToUtf8Bytes(state).Length > MaxVoiceRevisionStateUtf8Bytes)
        {
            throw new ApplicationFailureException(
                "This stay has reached its safe voice revision capacity. Please use web chat or contact the front desk.",
                errorType: UnavailableErrorType, nonRetryable: true);
        }
    }

    private ConciergeVoiceRevisionCheckpoint CaptureReversibleState(string turnId) =>
        new(turnId, nextSequence, stayMemory.ToArray(), recentTurns.ToArray(),
            pendingRideProposal, pendingRideCancellationProposal, nearbyPlaces, placeClarification);

    private void RestoreReversibleState(ConciergeVoiceRevisionCheckpoint checkpoint)
    {
        stayMemory.Clear();
        stayMemory.AddRange(checkpoint.StayMemory);
        recentTurns.Clear();
        recentTurns.AddRange(checkpoint.RecentTurns.Where(IsEffectiveTurn));
        pendingRideProposal = checkpoint.RideProposal;
        pendingRideCancellationProposal = checkpoint.CancellationProposal;
        nearbyPlaces = checkpoint.NearbyPlaces;
        placeClarification = checkpoint.PlaceClarification;
    }

    private async Task MarkSupersededAsync(string turnId)
    {
        // Use the interruption marker's insert/update serialization: a flush cannot insert an
        // unmarked snapshot after the monotonic database update.
        await Workflow.WaitConditionAsync(() => !flushInProgress);
        flushInProgress = true;
        try
        {
            Mark(runTurns);
            Mark(pendingPersistence);
            await Workflow.ExecuteActivityAsync(
                (ConciergeTranscriptActivities activities) => activities.MarkSupersededAsync(
                    new MarkTranscriptTurnSupersededRequest(stayId, turnId)),
                TranscriptActivityOptions);
        }
        finally
        {
            flushInProgress = false;
        }
        void Mark(List<ConciergeTranscriptTurn> turns)
        {
            var index = turns.FindIndex(turn => turn.TurnId == turnId);
            if (index >= 0) turns[index] = turns[index] with { Superseded = true };
        }
    }

    private static ApplicationFailureException SupersededFailure() =>
        new("This voice request was replaced by revised speech. No replacement reply is available for this request.",
            errorType: UnavailableErrorType, nonRetryable: true);

    private static void ThrowIfSuperseded(TurnAttempt attempt)
    {
        if (attempt.Superseded) throw new TurnSupersededException();
    }

    private static async Task<T> AwaitReplaceableAsync<T>(Task<T> work, TurnAttempt attempt)
    {
        ThrowIfSuperseded(attempt);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Workflow.CancellationToken);
        var superseded = Workflow.WaitConditionAsync(() => attempt.Superseded, cancellation.Token);
        try
        {
            await Workflow.WhenAnyAsync(work, superseded);
            ThrowIfSuperseded(attempt);
            return await work;
        }
        finally
        {
            cancellation.Cancel();
        }
    }

    private sealed class TurnSupersededException : Exception;

    private sealed class TurnAttempt(string turnId, string fingerprint, DateTimeOffset deadline)
    {
        public string TurnId { get; } = turnId;
        public string Fingerprint { get; } = fingerprint;
        public DateTimeOffset Deadline { get; } = deadline;
        public bool Superseded { get; set; }
        public bool SideEffectDispatched { get; set; }
        public bool Finished { get; set; }
        public ConciergeTurnResult? Result { get; set; }
        public Exception? Failure { get; set; }
    }

    /// <summary>Waits for already-admitted turns and flushes their replies; stay memory remains.</summary>
    [WorkflowUpdate(ConciergeWorkflowNames.EndInteractionUpdate)]
    public async Task EndInteractionAsync()
    {
        // A fixed arrival boundary avoids waiting for this handler (or later interactions).
        var admittedTurns = nextArrival;
        await Workflow.WaitConditionAsync(() => nextToProcess >= admittedTurns);

        if (!await FlushPendingAsync())
        {
            throw new ApplicationFailureException(
                "The transcript could not be flushed.",
                errorType: UnavailableErrorType,
                nonRetryable: true);
        }
    }

    [WorkflowUpdateValidator(nameof(ResetStayAsync))]
    public void ValidateResetStay(ConciergeVerifiedGuest guest)
    {
        ArgumentNullException.ThrowIfNull(guest);
        if (guest.StayId != stayId || guest.GuestId == Guid.Empty || guest.CheckoutDate == default)
        {
            throw new ArgumentException(
                "A stay reset requires this stay's verified guest; the guest must sign in again.",
                nameof(guest));
        }
    }

    /// <summary>
    /// Guest-requested start-over: removes the stay's rides and transcript and clears every piece of
    /// conversation state. It takes a turn arrival slot, so earlier turns finish (and are then deleted)
    /// and later turns start from the empty state. Rides go first so their booking workflows stop
    /// publishing before the stream is cut; if that fails nothing has been reset.
    /// </summary>
    [WorkflowUpdate(ConciergeWorkflowNames.ResetStayUpdate)]
    public async Task ResetStayAsync(ConciergeVerifiedGuest guest)
    {
        var arrival = nextArrival++;
        await Workflow.WaitConditionAsync(() => arrival == nextToProcess);
        try
        {
            await Workflow.WaitConditionAsync(() => !flushInProgress);
            flushInProgress = true;
            try
            {
                try
                {
                    await Workflow.ExecuteActivityAsync(
                        (ConciergeStayResetActivities activities) => activities.PurgeRidesAsync(
                            new PurgeStayRidesRequest(guest.GuestId, stayId, guest.CheckoutDate)),
                        RideResetActivityOptions);
                }
                catch (ActivityFailureException)
                {
                    throw new ApplicationFailureException(
                        "Your rides could not be removed, so nothing was reset. Please try Start over again.",
                        errorType: ConciergeWorkflowNames.ResetIncompleteErrorType,
                        nonRetryable: true);
                }

                pendingPersistence.Clear();
                try
                {
                    // Commit the delete before rewinding the sequence: leftover rows would collide
                    // with the restarted sequence numbers on the next flush.
                    await Workflow.ExecuteActivityAsync(
                        (ConciergeTranscriptActivities activities) =>
                            activities.DeleteForStayAsync(new DeleteStayTranscriptRequest(stayId)),
                        TranscriptActivityOptions);
                }
                catch (ActivityFailureException)
                {
                    throw new ApplicationFailureException(
                        "Your rides were removed, but the conversation could not be cleared. Please try Start over again.",
                        errorType: ConciergeWorkflowNames.ResetIncompleteErrorType,
                        nonRetryable: true);
                }

                ClearConversationState();
            }
            finally
            {
                flushInProgress = false;
            }
        }
        finally
        {
            nextToProcess++;
        }
    }

    private static readonly ActivityOptions RideResetActivityOptions = new()
    {
        // Above the ride client's total request budget, so one attempt is never cut short.
        StartToCloseTimeout = ConciergeActionToolService.RideClientTimeout + TimeSpan.FromSeconds(5),
        RetryPolicy = new RetryPolicy
        {
            InitialInterval = TimeSpan.FromSeconds(1),
            MaximumInterval = TimeSpan.FromSeconds(5),
            BackoffCoefficient = 2,
            MaximumAttempts = 3,
        },
    };

    private void ClearConversationState()
    {
        runTurns.Clear();
        recentTurns.Clear();
        stayMemory.Clear();
        acceptedTurns.Clear();
        // Turns queued behind the reset already registered their attempt and voice revision;
        // keep those so they still run, now against the empty state.
        foreach (var finished in admittedTurns.Where(entry => entry.Value.Finished).Select(entry => entry.Key).ToArray())
        {
            admittedTurns.Remove(finished);
        }
        foreach (var family in voiceRevisions.Values
                     .Where(revision => !admittedTurns.ContainsKey(revision.LatestTurnId))
                     .Select(revision => revision.Family).ToArray())
        {
            voiceRevisions.Remove(family);
        }
        voiceRevisionCheckpoint = null;
        pendingRideProposal = null;
        pendingRideCancellationProposal = null;
        lastConfirmedCancellation = null;
        consumedRideOffer = null;
        consumedRideOfferSequence = -1;
        consumedRideOfferSpokenRequestKey = null;
        consumedRideConfirmationOutcome = null;
        nearbyPlaces = null;
        placeClarification = null;
        nextSequence = 0;
        hasEarlierRuns = false;

        // Offsets only move forward so subscribers' retained-window checks stay valid.
        var streamState = rideStream.GetState();
        var endOffset = streamState.BaseOffset + streamState.Log.Count;
        rideStream.Truncate(endOffset);
        rideReadOffset = endOffset;
    }

    internal static string Fingerprint(string channel, string userMessage, bool enableEndChatFunction = false) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            enableEndChatFunction
                ? $"{channel}\n{userMessage}\nend-chat-function"
                : $"{channel}\n{userMessage}")));

    internal static int EstimatePromptTokens(IReadOnlyList<ChatMessage> messages) =>
        ConciergePromptBuilder.EstimatePromptTokens(messages);

    // Responses-API models occasionally emit a second assistant output item in one response
    // (observed live: a ride proposal followed by a repeated capabilities greeting). Joining them
    // breaks the one-answer contract, so keep the first non-empty assistant message.
    private static string FirstAnswerText(ChatResponse response) =>
        response.Messages
            .Where(message => message.Role == ChatRole.Assistant)
            .Select(message => message.Text)
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text))
        ?? response.Text;

    private bool CanFitStayMemory(IReadOnlyList<ConciergeStayMemoryItem> memory) =>
        memory.Count <= MaxStayMemoryItems
        && StayContinuityMemory.EstimateTokens(StayContinuityMemory.Format(memory))
            <= StayMemoryBudgetTokens;

    internal static int EstimateRecentTurnTokens(ConciergeTranscriptTurn turn) =>
        ConciergePromptBuilder.EstimateRecentTurnTokens(turn);

    internal static int EstimateRecentHistoryTokens(
        IEnumerable<ConciergeTranscriptTurn> turns) =>
        ConciergePromptBuilder.EstimateRecentHistoryTokens(turns);

    private void TrimRecentTurnsToStorageBudget() =>
        ConciergePromptBuilder.TrimRecentTurnsToStorageBudget(recentTurns);

    private async Task<bool> FlushPendingAsync()
    {
        await Workflow.WaitConditionAsync(() => !flushInProgress);
        if (pendingPersistence.Count == 0)
        {
            return true;
        }

        flushInProgress = true;
        var batch = pendingPersistence.ToArray();
        try
        {
            await Workflow.ExecuteActivityAsync(
                (ConciergeTranscriptActivities activities) =>
                    activities.PersistAsync(new PersistTranscriptRequest(stayId, batch)),
                TranscriptActivityOptions);

            // Turns are only appended, and only this guarded method removes them.
            pendingPersistence.RemoveRange(0, batch.Length);
            return true;
        }
        catch (ActivityFailureException exception)
        {
            Workflow.Logger.LogWarning(
                "Concierge operation {Operation} failed. Cause types: {CauseTypes}.",
                nameof(FlushPendingAsync),
                ConciergeDiagnostics.ExceptionTypeChain(exception));
            return false;
        }
        finally
        {
            flushInProgress = false;
        }
    }

    private TimeSpan RemainingUntilDeadline()
    {
        var remaining = eligibilityDeadlineUtc - Workflow.UtcNow;
        return remaining > TimeSpan.FromMilliseconds(1) ? remaining : TimeSpan.FromMilliseconds(1);
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left < right ? left : right;

    private static ConciergeTurnResult MatchOrReject(AcceptedTurn accepted, string fingerprint)
    {
        if (!StringComparer.Ordinal.Equals(accepted.Fingerprint, fingerprint))
        {
            throw new ApplicationFailureException(
                "The turn identifier conflicts with an earlier request.",
                errorType: TurnConflictErrorType,
                nonRetryable: true);
        }

        return accepted.Result;
    }

    private static void MarkInterrupted(List<ConciergeTranscriptTurn> turns, string turnId)
    {
        var index = turns.FindIndex(turn =>
            StringComparer.Ordinal.Equals(turn.TurnId, turnId)
            && StringComparer.Ordinal.Equals(turn.Channel, "voice"));
        if (index >= 0 && !turns[index].Interrupted)
        {
            turns[index] = turns[index] with { Interrupted = true };
        }
    }

    private sealed record AcceptedTurn(string Fingerprint, ConciergeTurnResult Result);
}
