using Concierge.Contracts;
using Concierge.Web.Agentic;
using Concierge.Web.Components.Shared;
using Concierge.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.AI;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.AI;
using Microsoft.JSInterop;

namespace Concierge.Web.Components.Pages;

/// <summary>
/// The guest chat page, at / and /agentic (ADR 0015). The chat is a Components.AI <see cref="UIAgent"/> whose
/// <see cref="IChatClient"/> is an AG-UI client of this app's own <c>/agui/concierge</c> endpoint; the
/// stay workflow stays the source of truth. Turns other clients add (the room device) appear live.
/// </summary>
public sealed partial class Agentic : IAsyncDisposable
{
    /// <summary>How long "The room device is asking…" may show without its answer arriving.</summary>
    internal static readonly TimeSpan DeviceQuestionTimeout = TimeSpan.FromSeconds(60);

    [Inject] private IConciergeService Concierge { get; set; } = default!;
    [Inject] private AgenticLoopback Loopback { get; set; } = default!;
    [Inject] private StayTranscriptNotifier TranscriptNotifier { get; set; } = default!;
    [Inject] private Voice.VoiceSessionRegistry VoiceSessions { get; set; } = default!;
    [Inject] private IJSRuntime JavaScript { get; set; } = default!;
    [Inject] private ILogger<Agentic> Logger { get; set; } = default!;
    [Inject] private IServiceProvider Services { get; set; } = default!;

    /// <summary>What the page keeps in sessionStorage (its own key; see agentic.js).</summary>
    internal sealed record StoredSession(string Credential, Guid StayId, Guid SessionId, DateTimeOffset ExpiresAtUtc);

    private bool ready;
    private string theme = ThemeChoice.Default;
    private string lastName = string.Empty;
    private string roomNumber = string.Empty;
    private string? verificationError;
    private bool isVerifying;

    private StoredSession? session;

    /// <summary>Development only (null otherwise): the failed run's server exception for the error card.</summary>
    private Func<string?>? FailureDetails => Services.GetService<AgenticRunFailures>() is { } failures
        ? () => session is { } current ? failures.Latest(current.Credential) : null
        : null;
    private StayConversationThread? thread;
    private UIAgent? agent;
    private IChatClient? chatClient;
    private HttpClient? http;
    private AgentContext? context;
    private RideActivityTracker? rides;
    private AgenticVoiceController? voice;
    private IDisposable? transcriptSubscription;
    private int agentGeneration;
    private int listGeneration;
    private bool runInFlight;
    private bool refreshPending;
    private readonly HashSet<string> handledCorrections = new(StringComparer.Ordinal);
    private string? deviceQuestion;
    private CancellationTokenSource? deviceQuestionTimeout;

    private ElementReference transcriptElement;
    private ElementReference followedTranscript;
    private bool followPending;

    private bool showEndConfirmation;
    private bool isEnding;
    private string? endError;
    private bool chatEnded;
    private bool chatEndedForInactivity;
    private bool showResetConfirmation;
    private bool isResetting;
    private string? resetError;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            theme = ThemeChoice.Normalize(await TryInvokeAsync<string>("conciergeChat.loadTheme"));
            await TryInvokeVoidAsync("conciergeChat.applyTheme", theme);
            var stored = await TryInvokeAsync<StoredSession>("conciergeAgentic.loadSession");
            if (stored is not null && stored.ExpiresAtUtc > DateTimeOffset.UtcNow && !string.IsNullOrEmpty(stored.Credential))
            {
                await StartSessionAsync(stored);
            }
            else if (stored is not null)
            {
                await TryInvokeVoidAsync("conciergeAgentic.clearSession");
            }

            ready = true;
            StateHasChanged();
            return;
        }

        if (followPending && agent is not null)
        {
            followPending = false;
            if (!followedTranscript.Equals(transcriptElement))
            {
                await TryInvokeVoidAsync("conciergeAgentic.unfollowTranscript", followedTranscript);
                followedTranscript = transcriptElement;
                await TryInvokeVoidAsync("conciergeAgentic.followTranscript", transcriptElement);
            }
        }
    }

    private async Task VerifyAsync()
    {
        var last = lastName.Trim();
        var room = roomNumber.Trim();
        if (last.Length == 0 || room.Length == 0)
        {
            verificationError = "Enter your last name and room number to continue.";
            return;
        }

        isVerifying = true;
        verificationError = null;
        try
        {
            var response = await Concierge.VerifyAsync(new ConciergeVerificationRequest(last, room));
            var stored = new StoredSession(response.Credential, response.StayId, response.SessionId, response.ExpiresAtUtc);
            await TryInvokeVoidAsync("conciergeAgentic.saveSession", stored);
            await StartSessionAsync(stored);
        }
        catch (ConciergeApiException exception)
        {
            verificationError = exception.Message;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Logger.LogInformation(
                "AG-UI preview could not verify the stay. Cause types: {CauseTypes}.",
                ConciergeDiagnostics.ExceptionTypeChain(exception));
            verificationError = "The concierge service is unavailable. Please try again.";
        }
        finally
        {
            isVerifying = false;
        }
    }

    /// <summary>Loads the stay's history into a new agent, then follows other clients and the ride feed.</summary>
    private async Task StartSessionAsync(StoredSession stored)
    {
        ConciergeHistoryResult history;
        try
        {
            history = await Concierge.ReadHistoryAsync(stored.Credential);
        }
        catch (ConciergeApiException exception) when (IsUnauthorized(exception))
        {
            await TryInvokeVoidAsync("conciergeAgentic.clearSession");
            verificationError = "Your session ended. Verify your stay again to continue.";
            return;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Thrown out of the first render, this would end the circuit and leave a dead page. The
            // stored session stays, so a reload or a new verification tries again.
            Logger.LogInformation(
                "AG-UI preview could not load the stay conversation. Cause types: {CauseTypes}.",
                ConciergeDiagnostics.ExceptionTypeChain(exception));
            verificationError = exception is ConciergeApiException { StatusCode: < 500 } api
                ? api.Message
                : "The concierge couldn't load your conversation. Try again in a moment.";
            return;
        }

        await TearDownSessionAsync();
        session = stored;
        thread = AgenticConcierge.ThreadFrom(stored.StayId, history.Turns);
        chatClient = Loopback.CreateChatClient(stored.Credential, out var client);
        http = client;
        agent = new UIAgent(chatClient, options => options.Thread = thread);
        agentGeneration++;
        followPending = true;

        var ownSession = stored.SessionId;
        var generation = agentGeneration;
        transcriptSubscription = TranscriptNotifier.Subscribe(stored.StayId, change =>
        {
            // This page's own AG-UI turns are already in its conversation.
            if (change.SourceSessionId == ownSession) return;
            _ = InvokeAsync(() => OnOtherClientAsync(generation, change));
        });

        rides = new RideActivityTracker(Concierge, Logger, InvokeAsync, StateHasChanged, OnSessionExpiredAsync);
        await rides.StartAsync(stored.Credential);
        voice = new AgenticVoiceController(
            VoiceSessions, new JSVoiceInterop(JavaScript), Logger, InvokeAsync, StateHasChanged,
            RefreshFromHistoryAsync, OnSessionExpiredAsync, OnVoiceEndedChatAsync);
    }

    /// <summary>Not while the page's own turn runs or the chat is ending or starting over.</summary>
    private bool CanStartVoice =>
        session is not null && voice is { Mode: false } && !runInFlight
        && !isEnding && !isResetting && !showEndConfirmation && !showResetConfirmation;

    /// <summary>The input's mic button: start voice, or end, resume or reconnect the session.</summary>
    private async Task OnMicAsync()
    {
        if (voice is null || session is null)
        {
            return;
        }

        if (!voice.Mode)
        {
            if (CanStartVoice)
            {
                await voice.StartAsync(session.Credential, session.SessionId);
            }

            return;
        }

        await voice.MicActionAsync();
    }

    private Task OnSpeakerAsync() => voice?.ToggleSpeakerAsync() ?? Task.CompletedTask;

    /// <summary>A spoken farewell or the idle timeout: the End chat flow, without its confirmation.</summary>
    private async Task OnVoiceEndedChatAsync(string reason)
    {
        await EndChatCoreAsync(forInactivity: reason == "idle_timeout");
    }

    private async Task OnOtherClientAsync(int generation, StayTranscriptChange change)
    {
        if (generation != agentGeneration || session is null)
        {
            return;
        }

        switch (change.Kind)
        {
            case StayTranscriptChangeKind.TurnStarted:
                ShowDeviceQuestion(change.GuestMessage);
                break;
            case StayTranscriptChangeKind.StayReset:
                ShowDeviceQuestion(null);
                if (rides is not null)
                {
                    await rides.ResetAsync();
                    await rides.StartAsync(session.Credential);
                }

                await RefreshFromHistoryAsync();
                break;
            default:
                ShowDeviceQuestion(null);
                await RefreshFromHistoryAsync();
                break;
        }

        StateHasChanged();
    }

    private void ShowDeviceQuestion(string? question)
    {
        deviceQuestionTimeout?.Cancel();
        deviceQuestionTimeout?.Dispose();
        deviceQuestionTimeout = null;
        deviceQuestion = string.IsNullOrWhiteSpace(question) ? null : question.Trim();
        if (deviceQuestion is null)
        {
            return;
        }

        var timeout = new CancellationTokenSource();
        deviceQuestionTimeout = timeout;
        _ = Task.Delay(DeviceQuestionTimeout, timeout.Token).ContinueWith(
            _ => InvokeAsync(() =>
            {
                if (ReferenceEquals(deviceQuestionTimeout, timeout))
                {
                    deviceQuestion = null;
                    StateHasChanged();
                }
            }),
            TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    /// <summary>
    /// Brings the conversation up to date with the stay's history without rebuilding the agent: the
    /// same thread is refilled, the context re-restored, and only MessageList re-keyed. Waits while the
    /// page's own run is in flight, since restoring then would drop its answer.
    /// </summary>
    private async Task RefreshFromHistoryAsync()
    {
        if (session is null || thread is null)
        {
            return;
        }

        if (runInFlight)
        {
            refreshPending = true;
            return;
        }

        try
        {
            var history = await Concierge.ReadHistoryAsync(session.Credential);
            if (runInFlight)
            {
                refreshPending = true;
                return;
            }

            thread.ReplaceFrom(history.Turns);
            if (context is not null)
            {
                await context.RestoreAsync(CancellationToken.None);
                listGeneration++;
            }
        }
        catch (ConciergeApiException exception) when (IsUnauthorized(exception))
        {
            await OnSessionExpiredAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The conversation stays as it is; the next change re-reads it.
            Logger.LogInformation(
                "AG-UI preview could not refresh the stay conversation. Cause types: {CauseTypes}.",
                ConciergeDiagnostics.ExceptionTypeChain(exception));
        }
    }

    private void OnRestored(AgentContext restored) => context = restored;

    /// <summary>
    /// A streamed answer was provisional and the committed one differs (ADR 0015): reload from history,
    /// once per correction. The run is still in flight here, so the reload waits for it to finish.
    /// </summary>
    private async Task OnAnswerCorrectedAsync(string callId)
    {
        if (handledCorrections.Add(callId))
        {
            await RefreshFromHistoryAsync();
        }
    }

    private async Task OnBusyChangedAsync(bool isBusy)
    {
        runInFlight = isBusy;
        if (!isBusy && refreshPending)
        {
            refreshPending = false;
            await RefreshFromHistoryAsync();
        }
    }

    private Task ToggleActivityAsync() => rides?.ToggleOpenAsync() ?? Task.CompletedTask;

    private void CloseActivity() => rides?.Close();

    private void HandlePageKeyDown(KeyboardEventArgs args)
    {
        if (rides?.IsOpen == true && StringComparer.Ordinal.Equals(args.Key, "Escape"))
        {
            rides.Close();
        }
    }

    private async Task ChangeThemeAsync(ChangeEventArgs args)
    {
        theme = ThemeChoice.Normalize(args.Value?.ToString());
        await TryInvokeVoidAsync("conciergeChat.applyTheme", theme);
    }

    private async Task EndChatAsync()
    {
        // The guest is leaving the conversation; voice must not keep listening behind the dialog.
        rides?.Close();
        if (voice is not null)
        {
            await voice.StopAsync("Voice stopped.");
        }

        endError = null;
        showEndConfirmation = true;
    }

    private Task CancelEndAsync()
    {
        showEndConfirmation = false;
        followPending = true;
        return Task.CompletedTask;
    }

    private Task EndInteractionAsync() => EndChatCoreAsync(forInactivity: false);

    private async Task EndChatCoreAsync(bool forInactivity)
    {
        if (session is null || isEnding) return;
        isEnding = true;
        endError = null;
        try
        {
            if (voice is not null)
            {
                await voice.ResetAsync();
            }

            await Concierge.EndAsync(session.Credential);
            chatEndedForInactivity = forInactivity;
            await TearDownSessionAsync();
            await TryInvokeVoidAsync("conciergeAgentic.clearSession");
            showEndConfirmation = false;
            chatEnded = true;
        }
        catch (ConciergeApiException exception) when (IsUnauthorized(exception))
        {
            showEndConfirmation = false;
            await OnSessionExpiredAsync();
        }
        catch (ConciergeApiException exception)
        {
            endError = exception.Message;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Logger.LogInformation(
                "AG-UI preview could not end the chat. Cause types: {CauseTypes}.",
                ConciergeDiagnostics.ExceptionTypeChain(exception));
            endError = "We couldn’t end the chat. Check your connection and try again.";
        }
        finally
        {
            isEnding = false;
        }
    }

    private void VerifyStayAgain()
    {
        chatEnded = false;
        chatEndedForInactivity = false;
        verificationError = null;
    }

    private async Task StartOverAsync()
    {
        rides?.Close();
        if (voice is not null)
        {
            await voice.StopAsync("Voice stopped.");
        }

        resetError = null;
        showResetConfirmation = true;
    }

    private Task CancelResetAsync()
    {
        showResetConfirmation = false;
        followPending = true;
        return Task.CompletedTask;
    }

    private async Task ResetStayAsync()
    {
        if (session is null) return;
        isResetting = true;
        resetError = null;
        try
        {
            // Before the server reset, which revokes voice with a plain disconnect (4000), not 4401.
            if (voice is not null)
            {
                await voice.ResetAsync();
            }

            await Concierge.ResetAsync(session.Credential);
            if (rides is not null)
            {
                await rides.ResetAsync();
                await rides.StartAsync(session.Credential);
            }

            // The notifier does not echo this page's own reset back to it.
            ShowDeviceQuestion(null);
            await RefreshFromHistoryAsync();
            showResetConfirmation = false;
            followPending = true;
        }
        catch (ConciergeApiException exception) when (IsUnauthorized(exception))
        {
            showResetConfirmation = false;
            await OnSessionExpiredAsync();
        }
        catch (ConciergeApiException exception)
        {
            resetError = exception.Message;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Logger.LogInformation(
                "AG-UI preview could not start over. Cause types: {CauseTypes}.",
                ConciergeDiagnostics.ExceptionTypeChain(exception));
            resetError = "We couldn’t start over. Check your connection and try again.";
        }
        finally
        {
            isResetting = false;
        }
    }

    private async Task OnSessionExpiredAsync()
    {
        await TearDownSessionAsync();
        await TryInvokeVoidAsync("conciergeAgentic.clearSession");
        verificationError = "Your session ended. Verify your stay again to continue.";
        StateHasChanged();
    }

    private async Task TearDownSessionAsync()
    {
        transcriptSubscription?.Dispose();
        transcriptSubscription = null;
        if (voice is not null)
        {
            await voice.DisposeAsync();
            voice = null;
        }

        if (rides is not null)
        {
            await rides.DisposeAsync();
            rides = null;
        }

        ShowDeviceQuestion(null);
        agent?.Dispose();
        chatClient?.Dispose();
        http?.Dispose();
        agent = null;
        chatClient = null;
        http = null;
        context = null;
        thread = null;
        session = null;
        runInFlight = false;
        refreshPending = false;
    }

    public async ValueTask DisposeAsync()
    {
        await TryInvokeVoidAsync("conciergeAgentic.unfollowTranscript", followedTranscript);
        await TearDownSessionAsync();
    }

    private static bool IsUnauthorized(ConciergeApiException exception) =>
        exception.StatusCode == StatusCodes.Status401Unauthorized
        || StringComparer.Ordinal.Equals(exception.Code, "unauthorized");

    private async Task TryInvokeVoidAsync(string identifier, params object?[] args)
    {
        try
        {
            await JavaScript.InvokeVoidAsync(identifier, args);
        }
        catch (JSDisconnectedException)
        {
        }
        catch (InvalidOperationException)
        {
            // Prerendering: no browser yet.
        }
    }

    private async Task<T?> TryInvokeAsync<T>(string identifier, params object?[] args)
    {
        try
        {
            return await JavaScript.InvokeAsync<T?>(identifier, args);
        }
        catch (JSDisconnectedException)
        {
            return default;
        }
        catch (InvalidOperationException)
        {
            return default;
        }
        catch (JSException)
        {
            return default;
        }
    }
}
