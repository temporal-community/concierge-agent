using System.Collections.Concurrent;
using Concierge.Contracts;
using Concierge.Web.Services;
using Concierge.Web.Voice;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

namespace Concierge.Core.Tests;

/// <summary>
/// ADR 0015: browser voice on the AG-UI page, with a real <see cref="VoiceSessionRegistry"/> and a fake
/// voice.js. Mirrors the voice cases in <see cref="HomeSessionTests"/>.
/// </summary>
public sealed class AgenticVoiceControllerTests
{
    [Fact]
    public async Task StartRunsVoiceJsAndASpokenTurnShowsUntilItFinishes()
    {
        await using var harness = new Harness();
        var connection = await harness.ListenAsync();
        Assert.Equal(["start credential "], harness.Js.Calls);

        var reply = new TaskCompletionSource<ConciergeTurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var turn = connection.Session.JoinOrStart("v:1", () => reply.Task, "Book a ride to the airport");
        await harness.WaitUntil(() => harness.Voice.PendingTranscript == "Book a ride to the airport");
        Assert.Equal(0, harness.TurnsChanged);

        reply.SetResult(new ConciergeTurnResult("v:1", "When should it pick you up?", 1));
        await turn;
        await harness.WaitUntil(() => harness.TurnsChanged == 1);
        Assert.Null(harness.Voice.PendingTranscript);
    }

    [Fact]
    public async Task AFailedSpokenTurnClearsItsTextAndStillRefreshes()
    {
        await using var harness = new Harness();
        var connection = await harness.ListenAsync();
        var reply = new TaskCompletionSource<ConciergeTurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var turn = connection.Session.JoinOrStart("v:2", () => reply.Task, "Unanswered");
        await harness.WaitUntil(() => harness.Voice.PendingTranscript is not null);

        reply.SetException(new InvalidOperationException("Unavailable"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => turn);

        await harness.WaitUntil(() => harness.Voice.PendingTranscript is null);
        Assert.Equal(1, harness.TurnsChanged);
    }

    [Fact]
    public async Task ServerRelayClosureDisconnectsAndRefreshesButKeepsABrowserDisconnect()
    {
        await using var harness = new Harness();
        var connection = await harness.ListenAsync();

        harness.Registry.Close(connection);
        await harness.WaitUntil(() => harness.Voice.State == "disconnected");
        Assert.Equal("Voice disconnected.", harness.Voice.Message);
        Assert.Equal(1, harness.TurnsChanged);
        Assert.True(harness.Voice.Mode);

        var second = await harness.ReconnectAsync();
        await harness.Voice.OnVoiceState("disconnected", "Voice service is unavailable.", 1011);
        harness.Registry.Close(second);
        await Task.Delay(100);
        Assert.Equal("Voice service is unavailable.", harness.Voice.Message);
    }

    [Fact]
    public async Task UnauthorizedCloseExpiresTheSessionAndStopsVoice()
    {
        await using var harness = new Harness();
        await harness.ListenAsync();

        await harness.Voice.OnVoiceState("disconnected", "Your session ended.", (int)VoiceCloseCodes.Unauthorized);

        Assert.Equal(1, harness.Expired);
        Assert.False(harness.Voice.Mode);
        Assert.Contains("stop", harness.Js.Calls);
    }

    [Fact]
    public async Task ASpokenFarewellOrIdleTimeoutRunsEndChatWithItsReason()
    {
        await using var harness = new Harness();
        await harness.ListenAsync();
        await harness.Voice.OnVoiceIdleWarning(true);
        Assert.True(harness.Voice.IdleWarning);

        await harness.Voice.OnVoiceEndRequested("idle_timeout");

        Assert.Equal(["idle_timeout"], harness.EndReasons);
        Assert.False(harness.Voice.IdleWarning);

        await using var other = new Harness();
        await other.ListenAsync();
        await other.Voice.OnVoiceEndRequested("anything else");
        Assert.Equal(["farewell"], other.EndReasons);
    }

    [Fact]
    public async Task EndVoiceReturnsToTextAndResetStopsWithoutRefreshing()
    {
        await using var harness = new Harness();
        await harness.ListenAsync();

        await harness.Voice.EndVoiceAsync();
        Assert.False(harness.Voice.Mode);
        Assert.Equal(1, harness.TurnsChanged);
        Assert.Contains("stop", harness.Js.Calls);

        await harness.ListenAsync();
        await harness.Voice.ResetAsync();
        Assert.False(harness.Voice.Mode);
        Assert.Equal(1, harness.TurnsChanged);
    }

    [Fact]
    public async Task TurnsFromAStoppedVoiceSessionAreIgnored()
    {
        await using var harness = new Harness();
        var connection = await harness.ListenAsync();
        await harness.Voice.ResetAsync();

        await connection.Session.JoinOrStart(
            "v:late", () => Task.FromResult(new ConciergeTurnResult("v:late", "Late", 1)), "Late question");
        await Task.Delay(100);

        Assert.Null(harness.Voice.PendingTranscript);
        Assert.Equal(0, harness.TurnsChanged);
    }

    [Fact]
    public async Task AVoiceSessionOwnedByAnotherChatIsRejected()
    {
        await using var harness = new Harness();
        await harness.Voice.StartAsync("credential", harness.Stay.SessionId);
        var foreign = harness.Registry.Open(
            new ConciergeSessionContext(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(1)), null);

        await harness.Voice.OnVoiceSession(foreign.Session.Id.ToString());
        await foreign.Session.JoinOrStart(
            "foreign", () => Task.FromResult(new ConciergeTurnResult("foreign", "Foreign reply", 1)), "Foreign text");

        Assert.Null(harness.Voice.VoiceSessionId);
        Assert.Equal("disconnected", harness.Voice.State);
        Assert.Null(harness.Voice.PendingTranscript);
        Assert.Equal(0, harness.TurnsChanged);
    }

    [Fact]
    public async Task AJavaScriptFailureOnStartIsShownAndMuteFollowsTheBrowser()
    {
        await using var harness = new Harness();
        harness.Js.FailStart = true;
        await harness.Voice.StartAsync("credential", harness.Stay.SessionId);
        Assert.Equal("disconnected", harness.Voice.State);
        Assert.StartsWith("Voice couldn't start", harness.Voice.Message, StringComparison.Ordinal);

        harness.Js.FailStart = false;
        await harness.Voice.ConnectAsync();
        await harness.Voice.ToggleMuteAsync();
        Assert.True(harness.Voice.Muted);
        harness.Js.Running = false;
        await harness.Voice.ToggleMuteAsync();
        Assert.True(harness.Voice.Muted);
    }

    [Fact]
    public async Task TheInputMicEndsResumesOrReconnectsByStateAndTheSpeakerTogglesReplies()
    {
        await using var harness = new Harness();
        await harness.Voice.MicActionAsync();
        Assert.Empty(harness.Js.Calls);

        await harness.ListenAsync();
        await harness.Voice.ToggleSpeakerAsync();
        Assert.True(harness.Voice.SpeakerMuted);
        await harness.Voice.ToggleSpeakerAsync();
        Assert.False(harness.Voice.SpeakerMuted);

        await harness.Voice.OnVoiceState("paused", null, 0);
        await harness.Voice.MicActionAsync();
        Assert.Contains("resume", harness.Js.Calls);

        await harness.Voice.OnVoiceState("disconnected", "Voice disconnected.", 4000);
        var starts = harness.Js.Calls.Count(call => call.StartsWith("start", StringComparison.Ordinal));
        await harness.Voice.MicActionAsync();
        Assert.Equal(starts + 1, harness.Js.Calls.Count(call => call.StartsWith("start", StringComparison.Ordinal)));
        Assert.Equal("connecting", harness.Voice.State);

        await harness.Voice.OnVoiceState("listening", null, 0);
        await harness.Voice.MicActionAsync();
        Assert.False(harness.Voice.Mode);
        Assert.Equal(["speaker True", "speaker False"], harness.Js.Calls.Where(call => call.StartsWith("speaker", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task DisposeStopsVoiceAndLaterCallbacksAreIgnored()
    {
        var harness = new Harness();
        await harness.ListenAsync();

        await harness.Voice.DisposeAsync();
        await harness.Voice.OnVoiceState("listening", null, 0);
        await harness.Voice.OnVoiceEndRequested("farewell");

        Assert.Contains("stop", harness.Js.Calls);
        Assert.Empty(harness.EndReasons);
        Assert.False(harness.Voice.Mode);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public Harness()
        {
            Voice = new AgenticVoiceController(
                Registry, Js, NullLogger.Instance,
                callback => callback(),
                () => { },
                () =>
                {
                    Interlocked.Increment(ref turnsChanged);
                    return Task.CompletedTask;
                },
                () =>
                {
                    Interlocked.Increment(ref expired);
                    return Task.CompletedTask;
                },
                reason =>
                {
                    EndReasons.Enqueue(reason);
                    return Task.CompletedTask;
                });
        }

        private int turnsChanged;
        private int expired;

        public VoiceSessionRegistry Registry { get; } = new(TimeProvider.System);

        public FakeVoiceJs Js { get; } = new();

        public ConciergeSessionContext Stay { get; } = new(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(1));

        public AgenticVoiceController Voice { get; }

        public ConcurrentQueue<string> EndReasons { get; } = new();

        public int TurnsChanged => Volatile.Read(ref turnsChanged);

        public int Expired => Volatile.Read(ref expired);

        /// <summary>Starts voice, then plays the relay's part: a connection and voice.js's session and state reports.</summary>
        public async Task<VoiceConnection> ListenAsync()
        {
            await Voice.StartAsync("credential", Stay.SessionId);
            return await AttachAsync(null);
        }

        public async Task<VoiceConnection> ReconnectAsync()
        {
            await Voice.ConnectAsync();
            return await AttachAsync(Guid.Parse(Voice.VoiceSessionId ?? Guid.Empty.ToString()));
        }

        private async Task<VoiceConnection> AttachAsync(Guid? voiceSession)
        {
            var connection = Registry.Open(Stay, voiceSession);
            await Voice.OnVoiceSession(connection.Session.Id.ToString());
            await Voice.OnVoiceState("listening", null, 0);
            return connection;
        }

        public async Task WaitUntil(Func<bool> condition)
        {
            for (var attempt = 0; attempt < 300 && !condition(); attempt++)
            {
                await Task.Delay(10);
            }

            Assert.True(condition());
        }

        public ValueTask DisposeAsync() => Voice.DisposeAsync();
    }

    private sealed class FakeVoiceJs : IVoiceInterop
    {
        public ConcurrentQueue<string> Calls { get; } = new();

        public bool FailStart { get; set; }

        public bool Running { get; set; }

        public Task StartAsync(string credential, string? voiceSessionId, DotNetObjectReference<AgenticVoiceController> callbacks)
        {
            if (FailStart)
            {
                throw new JSException("NotAllowedError: microphone permission denied");
            }

            Running = true;
            Calls.Enqueue($"start {credential} {voiceSessionId}");
            return Task.CompletedTask;
        }

        public Task ResumeAsync()
        {
            Calls.Enqueue("resume");
            return Task.CompletedTask;
        }

        public Task<bool?> SetMutedAsync(bool muted) => Task.FromResult<bool?>(Running ? muted : null);

        public Task<bool?> SetSpeakerMutedAsync(bool muted)
        {
            Calls.Enqueue($"speaker {muted}");
            return Task.FromResult<bool?>(muted);
        }

        public Task StopAsync()
        {
            Running = false;
            Calls.Enqueue("stop");
            return Task.CompletedTask;
        }
    }
}
