using Concierge.Web.Voice;
using Microsoft.JSInterop;

namespace Concierge.Web.Services;

/// <summary>
/// Browser voice for the guest chat page, UI-free (ADR 0015): <c>voice.js</c> owns the microphone,
/// playback and relay socket and reports back through the four <see cref="JSInvokableAttribute"/>
/// callbacks; the server-side <see cref="VoiceSessionRegistry"/> reports turns and relay closure.
/// </summary>
/// <remarks>
/// Voice turns carry the page's own chat session, so the transcript notifier skips them; the registry's
/// turn subscription is how this page learns a spoken turn finished.
/// </remarks>
/// <param name="dispatch">Runs a callback on the component's renderer (ComponentBase.InvokeAsync).</param>
/// <param name="changed">Requests a re-render; called on the dispatcher.</param>
/// <param name="turnsChanged">A spoken turn finished or the relay closed: re-read history.</param>
/// <param name="expired">The credential is no longer valid (close code 4401).</param>
/// <param name="endRequested">A spoken farewell or the idle timeout ended the chat ("farewell" or "idle_timeout").</param>
public sealed class AgenticVoiceController(
    VoiceSessionRegistry registry,
    IVoiceInterop voice,
    ILogger logger,
    Func<Func<Task>, Task> dispatch,
    Action changed,
    Func<Task> turnsChanged,
    Func<Task> expired,
    Func<string, Task> endRequested) : IAsyncDisposable
{
    private readonly Dictionary<string, VoiceTurnProgress> pending = new(StringComparer.Ordinal);
    private DotNetObjectReference<AgenticVoiceController>? reference;
    private IDisposable? closedSubscription;
    private IDisposable? turnSubscription;
    private string? credential;
    private Guid chatSessionId;
    private int generation;
    private bool disposed;

    /// <summary>Voice was started and not ended; the header shows voice controls.</summary>
    public bool Mode { get; private set; }

    public string State { get; private set; } = "disconnected";

    public string? Message { get; private set; }

    public bool Muted { get; private set; }

    /// <summary>Spoken replies are silenced; the guest still sees them. Kept across reconnects.</summary>
    public bool SpeakerMuted { get; private set; }

    public bool IdleWarning { get; private set; }

    /// <summary>"farewell" or "idle_timeout" once voice ended the chat.</summary>
    public string? EndReason { get; private set; }

    public string? VoiceSessionId { get; private set; }

    public bool Active => State is not ("disconnected" or "paused");

    public string StateLabel => State switch
    {
        "connecting" => "Connecting…",
        "listening" => "Listening",
        "thinking" => "Thinking…",
        "speaking" => "Speaking",
        "paused" => "Paused",
        _ when EndReason == "idle_timeout" => "Stopped (inactive)",
        _ => "Disconnected",
    };

    /// <summary>What the guest said in a spoken turn that hasn't finished, newest last.</summary>
    public string? PendingTranscript =>
        pending.Values.OrderBy(turn => turn.SubmittedAtUtc).LastOrDefault()?.UserMessage;

    /// <summary>Starts voice for a verified chat. Must run from a guest click so audio may start.</summary>
    public async Task StartAsync(string guestCredential, Guid guestChatSessionId)
    {
        if (disposed || Mode)
        {
            return;
        }

        credential = guestCredential;
        chatSessionId = guestChatSessionId;
        Mode = true;
        VoiceSessionId = null;
        await ConnectAsync();
    }

    /// <summary>Opens a new relay connection; reuses the voice session so turn IDs stay unique.</summary>
    public async Task ConnectAsync()
    {
        if (disposed || !Mode || credential is null)
        {
            return;
        }

        Muted = false;
        IdleWarning = false;
        EndReason = null;
        reference ??= DotNetObjectReference.Create(this);
        // The previous connection's closure is no longer news; OnVoiceSession watches the new one.
        StopWatching();
        State = "connecting";
        Message = null;
        try
        {
            await voice.StartAsync(credential, VoiceSessionId, reference);
        }
        catch (JSException exception)
        {
            logger.LogWarning("Voice could not start in the browser ({ExceptionType}).", exception.GetType().Name);
            State = "disconnected";
            Message = "Voice couldn't start in this browser. Check microphone access and try again.";
        }
    }

    public Task ResumeAsync() => voice.ResumeAsync();

    /// <summary>
    /// The input's mic button: starts nothing (the page starts voice with the guest's credential), ends
    /// a live session, resumes a paused one, and reconnects a dropped one.
    /// </summary>
    public Task MicActionAsync() => !Mode
        ? Task.CompletedTask
        : Active ? EndVoiceAsync()
        : State == "paused" ? ResumeAsync()
        : ConnectAsync();

    public async Task ToggleSpeakerAsync()
    {
        if (await voice.SetSpeakerMutedAsync(!SpeakerMuted) is { } muted)
        {
            SpeakerMuted = muted;
        }
    }

    public async Task ToggleMuteAsync()
    {
        if (await voice.SetMutedAsync(!Muted) is { } muted)
        {
            Muted = muted;
        }
    }

    /// <summary>Stops the browser's voice and leaves voice mode on, so Reconnect is offered.</summary>
    public async Task StopAsync(string? message)
    {
        IdleWarning = false;
        if (!Mode)
        {
            return;
        }

        Muted = false;
        StopWatching();
        await voice.StopAsync();
        State = "disconnected";
        Message = message;
    }

    /// <summary>End voice: back to text only. Turns spoken so far are re-read.</summary>
    public async Task EndVoiceAsync()
    {
        if (!Mode)
        {
            return;
        }

        await StopAsync("Voice stopped.");
        Mode = false;
        VoiceSessionId = null;
        await turnsChanged();
    }

    /// <summary>Stops voice and forgets it: sign-out, End chat, Start over, leaving the page.</summary>
    public async Task ResetAsync()
    {
        await StopAsync(null);
        Mode = false;
        VoiceSessionId = null;
        EndReason = null;
        credential = null;
        chatSessionId = Guid.Empty;
        generation++;
    }

    [JSInvokable]
    public Task OnVoiceSession(string id) =>
        dispatch(() =>
        {
            if (disposed || !Mode)
            {
                VoiceSessionId = null;
                StopWatching();
                return Task.CompletedTask;
            }

            VoiceSessionId = id;
            if (Watch(id))
            {
                Message = null;
                return Task.CompletedTask;
            }

            VoiceSessionId = null;
            State = "disconnected";
            Message = "Voice could not attach to this chat. Verify the stay and reconnect.";
            changed();
            return Task.CompletedTask;
        });

    /// <summary>The relay, not a browser timer, decides whether the chat is nearly idle.</summary>
    [JSInvokable]
    public Task OnVoiceIdleWarning(bool active) =>
        dispatch(() =>
        {
            if (!disposed && Mode && EndReason is null)
            {
                IdleWarning = active;
                changed();
            }

            return Task.CompletedTask;
        });

    [JSInvokable]
    public Task OnVoiceState(string state, string? message, int closeCode) =>
        dispatch(async () =>
        {
            if (disposed || !Mode || EndReason is not null)
            {
                return;
            }

            State = state;
            Message = message;
            if (state == "disconnected")
            {
                Muted = false;
                IdleWarning = false;
            }
            else if (state is "thinking" or "speaking")
            {
                IdleWarning = false;
            }

            if (state == "disconnected" && closeCode == (int)VoiceCloseCodes.Unauthorized)
            {
                await ResetAsync();
                await expired();
                return;
            }

            // The registry's Finished callback is authoritative; this covers a circuit that missed it.
            if (state == "disconnected")
            {
                await turnsChanged();
            }

            changed();
        });

    /// <summary>A confirmed spoken farewell or the idle timeout: the page runs its End chat flow.</summary>
    [JSInvokable]
    public Task OnVoiceEndRequested(string? reason = null) =>
        dispatch(async () =>
        {
            VoiceTelemetry.RecordEndChat(logger, "browser_callback_received", VoiceSessionId, "browser_end_requested");
            if (disposed || !Mode || credential is null)
            {
                VoiceTelemetry.RecordEndChat(logger, "browser_callback_ignored", VoiceSessionId, "inactive_or_already_ending");
                return;
            }

            EndReason = reason == "idle_timeout" ? reason : "farewell";
            IdleWarning = false;
            await endRequested(EndReason);
            changed();
        });

    /// <summary>
    /// Follows the relay's closure and turns for <paramref name="id"/>. voice.js also reports closure,
    /// but that call is lost if the circuit is down when the socket drops; the relay runs in this process.
    /// </summary>
    private bool Watch(string id)
    {
        StopWatching();
        if (!Guid.TryParse(id, out var voiceSession) || chatSessionId == Guid.Empty)
        {
            logger.LogWarning("Rejected voice-session subscription: malformed voice session or no verified chat session.");
            return false;
        }

        var watched = generation;
        if (!registry.TrySubscribeConnectionClosed(chatSessionId, voiceSession,
                // Runs on the relay's thread; queue onto the circuit without blocking it.
                connection => _ = dispatch(() => OnRelayClosedAsync(watched, id, connection)),
                out var closed))
        {
            logger.LogWarning("Rejected voice-session subscription {VoiceSessionId}: not owned by this chat session.", id);
            return false;
        }

        if (!registry.TrySubscribeTurns(chatSessionId, voiceSession,
                progress => _ = dispatch(() => OnTurnAsync(watched, id, progress)),
                out var turns))
        {
            closed.Dispose();
            logger.LogWarning("Rejected voice transcript subscription {VoiceSessionId}: not owned by this chat session.", id);
            return false;
        }

        closedSubscription = closed;
        turnSubscription = turns;
        return true;
    }

    private async Task OnTurnAsync(int watched, string id, VoiceTurnProgress progress)
    {
        if (disposed || watched != generation || !Mode || !StringComparer.Ordinal.Equals(VoiceSessionId, id))
        {
            return;
        }

        if (progress.Finished)
        {
            pending.Remove(progress.TurnId);
            await turnsChanged();
        }
        else
        {
            pending[progress.TurnId] = progress;
        }

        changed();
    }

    private async Task OnRelayClosedAsync(int watched, string id, int connection)
    {
        if (disposed || watched != generation || !Mode || EndReason is not null
            || !StringComparer.Ordinal.Equals(VoiceSessionId, id) || State == "disconnected")
        {
            // The browser's own report is more specific (e.g. 4401 expiry); keep it.
            return;
        }

        logger.LogInformation(
            "Voice relay closure applied by server for session {VoiceSessionId} (connection {ConnectionNumber}).", id, connection);
        State = "disconnected";
        Muted = false;
        IdleWarning = false;
        Message = "Voice disconnected.";
        // No JS here: the circuit may be down. Reconnect's start ends any stale browser voice.
        await turnsChanged();
        changed();
    }

    private void StopWatching()
    {
        closedSubscription?.Dispose();
        closedSubscription = null;
        turnSubscription?.Dispose();
        turnSubscription = null;
        pending.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        await ResetAsync();
        disposed = true;
        reference?.Dispose();
        reference = null;
    }
}

/// <summary>The page's calls into <c>window.conciergeVoice</c> (voice.js).</summary>
public interface IVoiceInterop
{
    Task StartAsync(string credential, string? voiceSessionId, DotNetObjectReference<AgenticVoiceController> callbacks);

    Task ResumeAsync();

    /// <summary>The new muted state, or null when no voice is running.</summary>
    Task<bool?> SetMutedAsync(bool muted);

    /// <summary>Silences or restores spoken replies; null when the browser can't be reached.</summary>
    Task<bool?> SetSpeakerMutedAsync(bool muted);

    Task StopAsync();
}

/// <summary>
/// <see cref="IVoiceInterop"/> over <see cref="IJSRuntime"/>. A lost circuit already closed the voice
/// socket, so disconnects are ignored; a JavaScript failure on start is passed on so the page can say so.
/// </summary>
public sealed class JSVoiceInterop(IJSRuntime js) : IVoiceInterop
{
    public async Task StartAsync(string credential, string? voiceSessionId, DotNetObjectReference<AgenticVoiceController> callbacks)
    {
        try
        {
            await js.InvokeVoidAsync("conciergeVoice.start", credential, voiceSessionId, callbacks);
        }
        catch (JSDisconnectedException)
        {
        }
    }

    public async Task ResumeAsync()
    {
        try
        {
            await js.InvokeVoidAsync("conciergeVoice.resume");
        }
        catch (Exception exception) when (exception is JSDisconnectedException or JSException or InvalidOperationException)
        {
        }
    }

    public async Task<bool?> SetMutedAsync(bool muted)
    {
        try
        {
            return await js.InvokeAsync<bool>("conciergeVoice.setMuted", muted);
        }
        catch (Exception exception) when (exception is JSDisconnectedException or JSException or InvalidOperationException)
        {
            return null;
        }
    }

    public async Task<bool?> SetSpeakerMutedAsync(bool muted)
    {
        try
        {
            return await js.InvokeAsync<bool>("conciergeVoice.setSpeakerMuted", muted);
        }
        catch (Exception exception) when (exception is JSDisconnectedException or JSException or InvalidOperationException)
        {
            return null;
        }
    }

    public async Task StopAsync()
    {
        try
        {
            await js.InvokeVoidAsync("conciergeVoice.stop");
        }
        catch (Exception exception) when (exception is JSDisconnectedException or JSException or InvalidOperationException)
        {
        }
    }
}
