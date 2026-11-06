using System.Buffers.Text;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Concierge.Contracts;
using Concierge.Web.Voice;
using Microsoft.Extensions.Options;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Extensions.WorkflowStreams;

namespace Concierge.Web.Services;

/// <summary>Authorizes guest sessions and delegates conversation operations to Temporal.</summary>
public sealed class ConciergeService(
    ITemporalClient temporalClient,
    IOptions<ConciergePersistenceOptions> persistenceOptions,
    VoiceSessionRegistry voiceSessions,
    StayTranscriptNotifier transcriptNotifier,
    ILogger<ConciergeService> logger) : IConciergeService
{
    private static readonly TimeSpan InitialRideStreamRetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaximumRideStreamRetryDelay = TimeSpan.FromSeconds(5);

    /// <inheritdoc/>
    public async Task<ConciergeSessionResponse> VerifyAsync(
        ConciergeVerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw InvalidRequest("Provide a verification request with LastName and RoomNumber.");
        }

        if (string.IsNullOrWhiteSpace(request.LastName) || request.LastName.Trim().Length > ConciergeVerifiedGuest.MaxNameLength)
        {
            throw InvalidRequest($"Enter LastName (up to {ConciergeVerifiedGuest.MaxNameLength} characters).");
        }

        if (!RoomNumberFormat.IsValid(request.RoomNumber))
        {
            throw InvalidRequest("Enter RoomNumber as 1–4 ASCII digits with no leading zero.");
        }

        var bearerBytes = RandomNumberGenerator.GetBytes(32);
        var credential = Base64Url.EncodeToString(bearerBytes);
        var credentialSha256 = SHA256.HashData(bearerBytes);
        var sessionId = Guid.NewGuid();
        var verified = await ExecuteSessionActivityAsync<ConciergeSessionContext?>(
            ConciergeActivityNames.VerifySession,
            SessionOperation.Verify,
            sessionId,
            new VerifyConciergeSessionActivityRequest(
                sessionId,
                credentialSha256,
                request.LastName,
                request.RoomNumber),
            cancellationToken);
        if (verified is null)
        {
            throw new ConciergeApiException(
                "guest_not_found",
                "Guest not found",
                StatusCodes.Status404NotFound);
        }

        return new ConciergeSessionResponse(
            credential, verified.EligibilityDeadlineUtc, verified.StayId, verified.SessionId);
    }

    /// <inheritdoc/>
    public async Task<ConciergeTurnResult> SubmitTurnAsync(
        string? bearerCredential,
        ConciergeTurnRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateTurn(request);
        var session = await AuthenticateAsync(bearerCredential, cancellationToken);
        return await ExecuteTurnAsync(session, request, authenticatedVoice: false, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<ConciergeVoiceAuthorization> AuthorizeVoiceAsync(
        string? bearerCredential,
        CancellationToken cancellationToken = default)
    {
        var session = await AuthenticateAsync(bearerCredential, cancellationToken);
        // Every voice connection only submits turns, so refuse it before it starts the stay workflow.
        RequireVerifiedGuest(session);
        try
        {
            var workflow = await GetStayWorkflowAsync(session, cancellationToken);
            var history = await workflow.ExecuteUpdateAsync<ConciergeHistoryResult>(
                ConciergeWorkflowNames.HistoryUpdate,
                [],
                UpdateOptions(cancellationToken));
            return new ConciergeVoiceAuthorization(session, history.Turns.Count > 0);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw MapTemporalFailure(exception, nameof(AuthorizeVoiceAsync));
        }
    }

    /// <inheritdoc/>
    public async Task<ConciergeTurnResult> SubmitVoiceTurnAsync(
        ConciergeSessionContext session,
        string turnId,
        string userMessage,
        CancellationToken cancellationToken = default,
        bool enableEndChatFunction = false)
    {
        ArgumentNullException.ThrowIfNull(session);
        var request = new ConciergeTurnRequest(turnId, userMessage, "voice");
        ValidateTurn(request);
        // Every delivery reaches workflow revision authority. Reusing the RPC update ID would
        // return Temporal's immutable cached reply even after this exact turn was superseded.
        // The workflow joins by the full text-hashed TurnId, including across Web replicas.
        return await ExecuteTurnAsync(session, request, authenticatedVoice: true, cancellationToken, enableEndChatFunction);
    }

    /// <inheritdoc/>
    public async Task MarkVoiceTurnInterruptedAsync(
        ConciergeSessionContext session,
        string turnId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(turnId) || turnId.Length > ConciergeTurnLimits.MaxTurnIdLength)
        {
            throw new ConciergeApiException(
                "invalid_request",
                $"Provide a nonblank TurnId of at most {ConciergeTurnLimits.MaxTurnIdLength} characters.",
                StatusCodes.Status400BadRequest);
        }

        try
        {
            var workflow = await GetStayWorkflowAsync(session, cancellationToken);
            await workflow.ExecuteUpdateAsync(
                ConciergeWorkflowNames.MarkTurnInterruptedUpdate,
                [new ConciergeTurnInterruption(turnId)],
                UpdateOptions(cancellationToken));
            transcriptNotifier.Notify(session.StayId, session.SessionId, StayTranscriptChangeKind.TurnsChanged);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw MapTemporalFailure(exception, nameof(MarkVoiceTurnInterruptedAsync));
        }
    }

    private async Task<ConciergeTurnResult> ExecuteTurnAsync(
        ConciergeSessionContext session,
        ConciergeTurnRequest request,
        bool authenticatedVoice,
        CancellationToken cancellationToken,
        bool enableEndChatFunction = false)
    {
        // Outside the try below, which would map this rejection to 503.
        RequireVerifiedGuest(session);
        try
        {
            var workflow = await GetStayWorkflowAsync(session, cancellationToken);
            var options = UpdateOptions(cancellationToken);
            // Lets another open page show the question while the desk is still working on it.
            transcriptNotifier.Notify(
                session.StayId, session.SessionId, StayTranscriptChangeKind.TurnStarted, request.UserMessage);
            // Generated RPC IDs let both retries and revisions reach the workflow's TurnId fence.
            var result = await workflow.ExecuteUpdateAsync<ConciergeTurnResult>(
                ConciergeWorkflowNames.SubmitTurnUpdate,
                [new ConciergeTurnSubmission(
                request.TurnId, request.UserMessage, request.Channel, session.VerifiedGuest, enableEndChatFunction,
                authenticatedVoice ? VoiceUtterance.FromTurnId(request.TurnId, request.UserMessage) : null)],
                options);
            // Other open pages for this stay (another tab, a room device's guest) show it without
            // reloading. An exact retry that joins an admitted turn notifies again; pages coalesce.
            transcriptNotifier.Notify(session.StayId, session.SessionId, StayTranscriptChangeKind.TurnsChanged);
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw MapTemporalFailure(exception, nameof(SubmitTurnAsync));
        }
    }

    /// <summary>
    /// Every turn requires a verified guest profile before the stay workflow is called.
    /// </summary>
    private static void RequireVerifiedGuest(ConciergeSessionContext session)
    {
        if (session.VerifiedGuest is null)
        {
            throw new ConciergeApiException(
                "unauthorized",
                "Your guest profile is missing from this session. Sign in again with your last name and room number to continue.",
                StatusCodes.Status401Unauthorized);
        }
    }

    private static void ValidateTurn(ConciergeTurnRequest? request)
    {
        if (request is null)
        {
            throw InvalidRequest("Provide a turn request with TurnId, UserMessage, and Channel.");
        }

        if (string.IsNullOrWhiteSpace(request.TurnId)
            || request.TurnId.Length > ConciergeTurnLimits.MaxTurnIdLength)
        {
            throw InvalidRequest(
                $"Provide a nonblank TurnId of at most {ConciergeTurnLimits.MaxTurnIdLength} characters.");
        }

        if (string.IsNullOrWhiteSpace(request.UserMessage)
            || !ConciergeTurnLimits.IsUserMessageWithinLimit(request.UserMessage))
        {
            throw InvalidRequest(
                $"Enter a nonblank UserMessage of at most {ConciergeTurnLimits.MaxUserMessageUtf8Bytes} UTF-8 bytes.");
        }

        if (!ConciergeTurnLimits.IsChannel(request.Channel))
        {
            throw InvalidRequest("Set Channel to 'web' or 'voice' (case-sensitive).");
        }
    }

    /// <inheritdoc/>
    public async Task<ConciergeHistoryResult> ReadHistoryAsync(
        string? bearerCredential,
        CancellationToken cancellationToken = default)
    {
        var session = await AuthenticateAsync(bearerCredential, cancellationToken);
        try
        {
            var workflow = await GetStayWorkflowAsync(session, cancellationToken);
            return await workflow.ExecuteUpdateAsync<ConciergeHistoryResult>(
                ConciergeWorkflowNames.HistoryUpdate,
                [],
                UpdateOptions(cancellationToken));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw MapTemporalFailure(exception, nameof(ReadHistoryAsync));
        }
    }

    /// <inheritdoc/>
    public async Task<ConciergeRideActivitySnapshot> ReadRideActivitySnapshotAsync(
        string? bearerCredential,
        CancellationToken cancellationToken = default)
    {
        var session = await AuthenticateAsync(bearerCredential, cancellationToken);
        RequireVerifiedGuest(session);
        try
        {
            var workflow = await GetRideActivityWorkflowAsync(
                session, cancellationToken, nameof(ReadRideActivitySnapshotAsync));
            var readState = await workflow.QueryAsync<ConciergeRideReadState>(
                ConciergeWorkflowNames.RideActivityReadStateQuery,
                [],
                QueryOptions(cancellationToken));
            ValidateRideReadState(readState);

            var firstOffset = Math.Max(
                readState.RetainedFromOffset,
                readState.SnapshotOffset - ConciergeRideActivityWindow.MaxEntries);
            var expectedOffset = firstOffset;
            var entries = new List<ConciergeRideActivityItem>(
                checked((int)(readState.SnapshotOffset - firstOffset)));
            if (firstOffset < readState.SnapshotOffset)
            {
                await using var streamClient = new WorkflowStreamClient(
                    temporalClient,
                    ConciergeStayWorkflowIds.ForStay(session.StayId));
                await using var subscription = streamClient
                    .GetTopic<RideActivityEntry>(RideActivityFeed.Topic)
                    .SubscribeAsync(firstOffset)
                    .GetAsyncEnumerator(cancellationToken);

                while (expectedOffset < readState.SnapshotOffset)
                {
                    if (!await subscription.MoveNextAsync())
                    {
                        throw Unavailable();
                    }

                    var item = subscription.Current;
                    if (item.Offset != expectedOffset || item.Offset >= readState.SnapshotOffset)
                    {
                        // The retained window changed between the query and subscription. Never
                        // return a success-shaped partial snapshot.
                        LogRideActivityFailure(nameof(ReadRideActivitySnapshotAsync), "snapshot_window_changed");
                        throw Unavailable();
                    }

                    entries.Add(new ConciergeRideActivityItem(item.Offset, item.Value));
                    expectedOffset++;
                }
            }

            return new ConciergeRideActivitySnapshot(readState, entries);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not ConciergeApiException)
        {
            throw MapTemporalFailure(exception, nameof(ReadRideActivitySnapshotAsync));
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ConciergeRideActivityItem> StreamRideActivityAsync(
        string? bearerCredential,
        long fromOffset,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (fromOffset < 0)
        {
            throw InvalidRequest("The ride activity offset cannot be negative.");
        }

        var session = await AuthenticateAsync(bearerCredential, cancellationToken);
        RequireVerifiedGuest(session);
        var nextOffset = fromOffset;
        var retryDelay = InitialRideStreamRetryDelay;
        var isReconnect = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (isReconnect)
            {
                try
                {
                    // Re-authenticate before reconnecting so revoked or expired credentials cannot
                    // silently keep an old subscription alive.
                    var reauthenticated = await AuthenticateAsync(bearerCredential, cancellationToken);
                    RequireSameAuthenticatedSession(session, reauthenticated);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // Credential/session failures (401/403) are terminal. Temporary failures —
                    // transient RPC errors, or session-activity failures already mapped to 503 —
                    // share the stream's capped backoff and retry reauthentication.
                    if (!IsRetryableRideStreamReauthenticationFailure(exception))
                    {
                        throw exception is ConciergeApiException apiException
                            ? apiException
                            : MapTemporalFailure(exception, nameof(StreamRideActivityAsync));
                    }

                    LogRideStreamRetry(exception);
                    await Task.Delay(retryDelay, cancellationToken);
                    retryDelay = NextRideStreamRetryDelay(retryDelay);
                    continue;
                }
            }
            isReconnect = true;

            WorkflowStreamClient streamClient;
            IAsyncEnumerator<WorkflowStreamItem<RideActivityEntry>> subscription;
            try
            {
                _ = await GetRideActivityWorkflowAsync(
                    session, cancellationToken, nameof(StreamRideActivityAsync));
                streamClient = new WorkflowStreamClient(
                    temporalClient,
                    ConciergeStayWorkflowIds.ForStay(session.StayId));
                try
                {
                    subscription = streamClient
                        .GetTopic<RideActivityEntry>(RideActivityFeed.Topic)
                        .SubscribeAsync(nextOffset)
                        .GetAsyncEnumerator(cancellationToken);
                }
                catch
                {
                    await streamClient.DisposeAsync();
                    throw;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not ConciergeApiException)
            {
                if (!IsTransientRideStreamFailure(exception))
                {
                    throw MapTemporalFailure(exception, nameof(StreamRideActivityAsync));
                }

                LogRideStreamRetry(exception);
                await Task.Delay(retryDelay, cancellationToken);
                retryDelay = NextRideStreamRetryDelay(retryDelay);
                continue;
            }

            Exception? retryFailure = null;
            try
            {
                while (true)
                {
                    bool hasNext;
                    try
                    {
                        hasNext = await subscription.MoveNextAsync();
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException and not ConciergeApiException)
                    {
                        if (!IsTransientRideStreamFailure(exception))
                        {
                            throw MapTemporalFailure(exception, nameof(StreamRideActivityAsync));
                        }

                        retryFailure = exception;
                        break;
                    }

                    if (!hasNext)
                    {
                        throw RideActivityStreamEnded(session, nameof(StreamRideActivityAsync));
                    }

                    var item = subscription.Current;
                    if (item.Offset < nextOffset)
                    {
                        continue;
                    }
                    if (item.Offset != nextOffset)
                    {
                        LogRideActivityFailure(nameof(StreamRideActivityAsync), "stream_offset_gap");
                        throw Unavailable();
                    }

                    nextOffset = checked(item.Offset + 1);
                    retryDelay = InitialRideStreamRetryDelay;
                    yield return new ConciergeRideActivityItem(item.Offset, item.Value);
                }
            }
            finally
            {
                try
                {
                    await subscription.DisposeAsync();
                }
                finally
                {
                    await streamClient.DisposeAsync();
                }
            }

            if (retryFailure is not null)
            {
                LogRideStreamRetry(retryFailure);
                await Task.Delay(retryDelay, cancellationToken);
                retryDelay = NextRideStreamRetryDelay(retryDelay);
            }
        }
    }

    /// <inheritdoc/>
    public async Task<ConciergeRideReadState> MarkRideActivitySnapshotReadAsync(
        string? bearerCredential,
        long snapshotOffset,
        CancellationToken cancellationToken = default)
    {
        if (snapshotOffset < 0)
        {
            throw InvalidRequest("The ride activity snapshot offset cannot be negative.");
        }

        var session = await AuthenticateAsync(bearerCredential, cancellationToken);
        RequireVerifiedGuest(session);
        try
        {
            var workflow = await GetRideActivityWorkflowAsync(
                session, cancellationToken, nameof(MarkRideActivitySnapshotReadAsync));
            return await workflow.ExecuteUpdateAsync<ConciergeRideReadState>(
                ConciergeWorkflowNames.AdvanceRideActivityReadOffsetUpdate,
                [new ConciergeRideReadOffsetUpdate(snapshotOffset)],
                UpdateOptions(cancellationToken));
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not ConciergeApiException)
        {
            throw MapTemporalFailure(exception, nameof(MarkRideActivitySnapshotReadAsync));
        }
    }

    /// <inheritdoc/>
    public async Task EndAsync(
        string? bearerCredential,
        CancellationToken cancellationToken = default)
    {
        var session = await AuthenticateAsync(bearerCredential, cancellationToken);
        // Voice stops admitting new turns as soon as End starts, even if the flush below fails.
        voiceSessions.EndSession(session.SessionId);
        try
        {
            var workflow = await GetStayWorkflowAsync(session, cancellationToken);
            await workflow.ExecuteUpdateAsync(
                ConciergeWorkflowNames.EndInteractionUpdate,
                [],
                UpdateOptions(cancellationToken));

            // The update is idempotent, so a retry after an ambiguous response is safe.
            await ExecuteSessionActivityAsync(
                ConciergeActivityNames.InvalidateSession,
                SessionOperation.Invalidate,
                session.SessionId,
                new InvalidateConciergeSessionActivityRequest(session.SessionId, session.StayId),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not ConciergeApiException)
        {
            throw MapTemporalFailure(exception, nameof(EndAsync));
        }
    }

    /// <inheritdoc/>
    public async Task ResetAsync(
        string? bearerCredential,
        CancellationToken cancellationToken = default)
    {
        var session = await AuthenticateAsync(bearerCredential, cancellationToken);
        RequireVerifiedGuest(session);
        // A voice turn admitted after the reset would otherwise speak from the cleared context.
        voiceSessions.EndSession(session.SessionId, VoiceRevocation.Reset);
        try
        {
            var workflow = await GetStayWorkflowAsync(session, cancellationToken);
            await workflow.ExecuteUpdateAsync(
                ConciergeWorkflowNames.ResetStayUpdate,
                [session.VerifiedGuest!],
                UpdateOptions(cancellationToken));
            // Other credentials for the stay no longer keep a stale transcript until they reload (ADR 0010).
            transcriptNotifier.Notify(session.StayId, session.SessionId, StayTranscriptChangeKind.StayReset);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not ConciergeApiException)
        {
            throw MapTemporalFailure(exception, nameof(ResetAsync));
        }
    }

    private async Task<ConciergeSessionContext> AuthenticateAsync(
        string? bearerCredential,
        CancellationToken cancellationToken)
    {
        if (!TryHashCredential(bearerCredential, out var credentialSha256))
        {
            throw Unauthorized();
        }

        // The ID only correlates this request; the credential digest selects the session.
        var requestId = Guid.NewGuid();
        return await ExecuteSessionActivityAsync<ConciergeSessionContext?>(
                ConciergeActivityNames.AuthenticateSession,
                SessionOperation.Authenticate,
                requestId,
                new AuthenticateConciergeSessionActivityRequest(credentialSha256),
                cancellationToken)
            ?? throw Unauthorized();
    }

    /// <summary>Runs a session activity that returns <typeparamref name="TResult"/>.</summary>
    private Task<TResult> ExecuteSessionActivityAsync<TResult>(
        string activityName,
        SessionOperation operation,
        Guid sessionId,
        object request,
        CancellationToken cancellationToken) =>
        RunSessionActivityAsync(
            activityName,
            operation,
            sessionId,
            request,
            (handle, rpcOptions) => handle.GetResultAsync<TResult>(rpcOptions),
            cancellationToken);

    /// <summary>Runs a session activity that has no result.</summary>
    private Task ExecuteSessionActivityAsync(
        string activityName,
        SessionOperation operation,
        Guid sessionId,
        object request,
        CancellationToken cancellationToken) =>
        RunSessionActivityAsync(
            activityName,
            operation,
            sessionId,
            request,
            async (handle, rpcOptions) =>
            {
                await handle.GetResultAsync(rpcOptions);
                return true;
            },
            cancellationToken);

    /// <summary>
    /// Runs one session operation as a Temporal Standalone Activity and waits for its result.
    /// Every call gets a unique activity ID, so the default ID reuse/conflict policies never attach
    /// one request to another request's execution.
    /// </summary>
    private async Task<TResult> RunSessionActivityAsync<TResult>(
        string activityName,
        SessionOperation operation,
        Guid sessionId,
        object request,
        Func<ActivityHandle, RpcOptions, Task<TResult>> getResultAsync,
        CancellationToken cancellationToken)
    {
        var operationName = operation.ToString();
        var activityId =
            $"concierge-session-{sessionId:N}-{operationName.ToLowerInvariant()}-{Guid.NewGuid():N}";
        try
        {
            var handle = await temporalClient.StartActivityAsync(
                activityName,
                [request],
                new StartActivityOptions(activityId, TemporalWorkerConnection.TaskQueue)
                {
                    StartToCloseTimeout = TimeSpan.FromSeconds(20),
                    RetryPolicy = new RetryPolicy
                    {
                        InitialInterval = TimeSpan.FromMilliseconds(500),
                        MaximumInterval = TimeSpan.FromSeconds(2),
                        MaximumAttempts = 3,
                    },
                    Rpc = new RpcOptions { CancellationToken = cancellationToken },
                });

            return await getResultAsync(handle, new RpcOptions { CancellationToken = cancellationToken });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw MapTemporalFailure(exception, operationName);
        }
    }

    private async Task<WorkflowHandle> GetStayWorkflowAsync(
        ConciergeSessionContext session,
        CancellationToken cancellationToken)
    {
        var workflowId = $"concierge-stay-{session.StayId:N}";
        // Start (or join, via UseExisting) only to guarantee the stay workflow exists. Do not use the
        // returned handle: it pins FirstExecutionRunId to the *current* run ID, and after Continue-As-New
        // the current run's first-execution run ID is the chain's original run, so updates sent through
        // that handle fail with "workflow execution not found". An unpinned handle targets the latest run.
        await temporalClient.StartWorkflowAsync(
            ConciergeWorkflowNames.StayWorkflow,
            [
                new ConciergeStayWorkflowInput(
                    session.StayId,
                    session.EligibilityDeadlineUtc,
                    persistenceOptions.Value.FlushIntervalSeconds,
                    TranscriptFlushBatchSize: persistenceOptions.Value.FlushBatchSize,
                    VerifiedGuest: session.VerifiedGuest,
                    IdleRolloverSeconds: persistenceOptions.Value.IdleRolloverSeconds),
            ],
            new WorkflowOptions(workflowId, TemporalWorkerConnection.TaskQueue)
            {
                IdReusePolicy = WorkflowIdReusePolicy.AllowDuplicate,
                IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting,
                Rpc = new RpcOptions { CancellationToken = cancellationToken },
            });
        return temporalClient.GetWorkflowHandle(workflowId);
    }

    /// <summary>
    /// Ensures a stay feed workflow exists without resurrecting a closed execution. An absent
    /// workflow is started for a newly verified stay; a closed workflow is an explicit failure.
    /// </summary>
    private async Task<WorkflowHandle> GetRideActivityWorkflowAsync(
        ConciergeSessionContext session,
        CancellationToken cancellationToken,
        string operation)
    {
        var workflowId = ConciergeStayWorkflowIds.ForStay(session.StayId);
        var workflow = temporalClient.GetWorkflowHandle(workflowId);
        try
        {
            var description = await workflow.DescribeAsync(
                new WorkflowDescribeOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } });
            if (description.Status != WorkflowExecutionStatus.Running)
            {
                throw RideActivityStreamEnded(session, operation);
            }

            return workflow;
        }
        catch (RpcException exception) when (exception.Code == RpcException.StatusCode.NotFound)
        {
            // Starting is needed for a signed-in guest who has not yet sent a chat turn. The
            // existence check above prevents a closed stay workflow from being restarted.
            await GetStayWorkflowAsync(session, cancellationToken);
            workflow = temporalClient.GetWorkflowHandle(workflowId);
            var description = await workflow.DescribeAsync(
                new WorkflowDescribeOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } });
            if (description.Status != WorkflowExecutionStatus.Running)
            {
                throw RideActivityStreamEnded(session, operation);
            }

            return workflow;
        }
    }

    private static WorkflowUpdateOptions UpdateOptions(CancellationToken cancellationToken) =>
        new() { Rpc = new RpcOptions { CancellationToken = cancellationToken } };

    private static WorkflowQueryOptions QueryOptions(CancellationToken cancellationToken) =>
        new() { Rpc = new RpcOptions { CancellationToken = cancellationToken } };

    private static void ValidateRideReadState(ConciergeRideReadState state)
    {
        if (state.ReadOffset < 0
            || state.RetainedFromOffset < 0
            || state.SnapshotOffset < state.RetainedFromOffset
            || state.ReadOffset > state.SnapshotOffset)
        {
            throw new InvalidOperationException("The stay workflow returned invalid ride activity offsets.");
        }
    }

    private static void RequireSameAuthenticatedSession(
        ConciergeSessionContext expected,
        ConciergeSessionContext actual)
    {
        RequireVerifiedGuest(actual);
        if (actual.SessionId != expected.SessionId || actual.StayId != expected.StayId)
        {
            throw Unauthorized();
        }
    }

    private static TimeSpan NextRideStreamRetryDelay(TimeSpan current) =>
        TimeSpan.FromMilliseconds(Math.Min(
            current.TotalMilliseconds * 2,
            MaximumRideStreamRetryDelay.TotalMilliseconds));

    private static bool IsTransientRideStreamFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is RpcException rpcException
                && rpcException.Code is RpcException.StatusCode.Unavailable
                    or RpcException.StatusCode.DeadlineExceeded
                    or RpcException.StatusCode.ResourceExhausted
                    or RpcException.StatusCode.Aborted)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsRetryableRideStreamReauthenticationFailure(Exception exception) =>
        exception is ConciergeApiException apiException
            ? apiException.StatusCode == StatusCodes.Status503ServiceUnavailable
            : IsTransientRideStreamFailure(exception);

    private void LogRideStreamRetry(Exception exception) =>
        logger.LogWarning(
            "Concierge Temporal operation {Operation} encountered a transient stream failure. Cause types: {CauseTypes}.",
            nameof(StreamRideActivityAsync),
            ConciergeDiagnostics.ExceptionTypeChain(exception));

    private void LogRideActivityFailure(string operation, string reason) =>
        logger.LogWarning(
            "Concierge Temporal operation {Operation} failed. Reason: {Reason}.",
            operation,
            reason);

    private ConciergeApiException RideActivityStreamEnded(
        ConciergeSessionContext session,
        string operation)
    {
        LogRideActivityFailure(operation, "stay_workflow_closed");
        return session.EligibilityDeadlineUtc <= DateTimeOffset.UtcNow
            ? Unauthorized()
            : Unavailable();
    }

    private static bool TryHashCredential(string? credential, out byte[] hash)
    {
        hash = [];
        if (string.IsNullOrWhiteSpace(credential) || credential.Length > 128)
        {
            return false;
        }

        try
        {
            var bytes = Base64Url.DecodeFromChars(credential);
            if (bytes.Length != 32)
            {
                return false;
            }

            hash = SHA256.HashData(bytes);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private ConciergeApiException MapTemporalFailure(Exception exception, string operation)
    {
        // Answer "which operation failed?" and "what exception types caused it?"
        // without logging exception messages, stacks, payloads, or guest/session identifiers.
        logger.LogWarning(
            "Concierge Temporal operation {Operation} failed. Cause types: {CauseTypes}.",
            operation,
            ConciergeDiagnostics.ExceptionTypeChain(exception));

        return (exception as WorkflowUpdateFailedException)?.InnerException is ApplicationFailureException failure
            ? failure.ErrorType switch
            {
                ConciergeWorkflowNames.TurnConflictErrorType => new ConciergeApiException(
                    "turn_conflict",
                    "The turn identifier was already used.",
                    StatusCodes.Status409Conflict),
                ConciergeWorkflowNames.StayExpiredErrorType => Unauthorized(),
                // Workflow-authored guidance that says which part of the reset did not happen.
                ConciergeWorkflowNames.ResetIncompleteErrorType => new ConciergeApiException(
                    "reset_incomplete",
                    failure.Message,
                    StatusCodes.Status503ServiceUnavailable),
                _ => Unavailable(),
            }
            : Unavailable();
    }

    private enum SessionOperation
    {
        Verify,
        Authenticate,
        Invalidate,
    }

    private static ConciergeApiException InvalidRequest(string message) =>
        new("invalid_request", message, StatusCodes.Status400BadRequest);

    private static ConciergeApiException Unauthorized() =>
        new("unauthorized", "Authenticate again to continue.", StatusCodes.Status401Unauthorized);

    private static ConciergeApiException Unavailable() =>
        new(
            "unavailable",
            "The concierge is temporarily unavailable. Please try again.",
            StatusCodes.Status503ServiceUnavailable);
}
