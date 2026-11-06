using System.Reflection;
using System.Runtime.CompilerServices;
using Concierge.Contracts;
using Concierge.Web.Components.Pages;
using Concierge.Web.Services;
using Concierge.Web.Voice;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace Concierge.Core.Tests;

public sealed class HomeSessionTests
{
    [Fact]
    public async Task VoiceRequestAppearsBeforeAnswerAndCompletionRefreshesWithoutPlayback()
    {
        await using var harness = new Harness();
        var connection = harness.StartListening();
        await harness.VoiceSession(connection.Session.Id.ToString());
        var reply = new TaskCompletionSource<ConciergeTurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = connection.Session.JoinOrStart("voice-pending", () => reply.Task, "Cancel my ride, please.");
        await harness.WaitUntil(() => harness.RenderedContent().Text.Contains("Cancel my ride, please."));
        Assert.False(request.IsCompleted);
        Assert.Equal("listening", harness.Get<string>("voiceState"));
        var duplicate = connection.Session.JoinOrStart("voice-pending",
            () => throw new InvalidOperationException("A retry must join."), "Cancel my ride, please.");
        Assert.Same(request, duplicate);
        Assert.Single(harness.RenderedContent().Text, text => text == "Cancel my ride, please.");

        harness.Service.History = _ => Task.FromResult(new ConciergeHistoryResult([
            new ConciergeTranscriptTurn("voice-pending", 1, "voice", "Cancel my ride, please.",
                "Your ride is cancelled.", DateTimeOffset.UtcNow)]));
        reply.SetResult(new ConciergeTurnResult("voice-pending", "Your ride is cancelled.", 1));
        await request;
        await harness.WaitUntil(() => harness.Get<List<ConciergeTranscriptTurn>>("turns").Count == 1);
        Assert.Single(harness.RenderedContent().Text, text => text == "Cancel my ride, please.");
        var (text, markup) = harness.RenderedContent();
        Assert.Contains("Your ride is cancelled.", string.Concat(text.Concat(markup)));
        Assert.Equal("listening", harness.Get<string>("voiceState"));
    }

    [Fact]
    public async Task FailedVoiceTurnClearsPendingTextWithoutInventingReply()
    {
        await using var harness = new Harness();
        var connection = harness.StartListening();
        await harness.VoiceSession(connection.Session.Id.ToString());
        var reply = new TaskCompletionSource<ConciergeTurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = connection.Session.JoinOrStart("failed-voice", () => reply.Task, "Unanswered question");
        await harness.WaitUntil(() => harness.RenderedContent().Text.Contains("Unanswered question"));
        reply.SetException(new InvalidOperationException("Unavailable"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => request);
        await harness.WaitUntil(() => !harness.RenderedContent().Text.Contains("Unanswered question"));
        Assert.Empty(harness.Get<List<ConciergeTranscriptTurn>>("turns"));
    }

    [Fact]
    public async Task ReconnectReplaysPendingVoiceRequestButOldSessionCannotLeakText()
    {
        await using var harness = new Harness();
        var connection = harness.StartListening();
        var reply = new TaskCompletionSource<ConciergeTurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource();
        Assert.True(harness.Voice.TrySubscribeTurns(
            connection.Session.Stay.SessionId,
            connection.Session.Id,
            _ => started.TrySetResult(),
            out var observer));
        using var observerSubscription = observer;
        var request = connection.Session.JoinOrStart("reconnect-pending", () => reply.Task, "My pending question");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await harness.VoiceSession(connection.Session.Id.ToString());
        await harness.WaitUntil(() => harness.RenderedContent().Text.Contains("My pending question"));
        var other = harness.StartListening();
        await harness.VoiceSession(other.Session.Id.ToString());
        await harness.Call("StateHasChanged");
        Assert.DoesNotContain("My pending question", harness.RenderedContent().Text);
        reply.SetResult(new ConciergeTurnResult("reconnect-pending", "Old reply", 1));
        await request;
        await harness.Call("StateHasChanged");
        Assert.DoesNotContain("My pending question", harness.RenderedContent().Text);
        Assert.DoesNotContain("Old reply", harness.RenderedContent().Text);
    }

    [Fact]
    public async Task IdleWarningIsVisibleAndAccessibleWithoutReplacingTheVoiceState()
    {
        await using var harness = new Harness();
        harness.StartListening();
        await harness.VoiceIdleWarning(true);
        var (text, markup) = harness.RenderedContent();
        var content = string.Concat(text.Concat(markup));
        Assert.Contains("Chat ending soon", content);
        Assert.Contains("30 seconds", content);
        Assert.Contains("Speak to continue.", content);
        Assert.Equal("listening", harness.Get<string>("voiceState"));
        Assert.Contains(harness.RenderedElements(), element =>
            Equals(element.Attributes.GetValueOrDefault("role"), "status")
            && Equals(element.Attributes.GetValueOrDefault("aria-atomic"), "true"));
        await harness.VoiceIdleWarning(false);
        (text, markup) = harness.RenderedContent();
        Assert.DoesNotContain("Chat ending soon", string.Concat(text.Concat(markup)));
    }

    [Theory]
    [InlineData("listening", true, "Unmute your microphone and speak to continue.")]
    [InlineData("paused", false, "Resume voice and speak to continue.")]
    public async Task IdleWarningExplainsHowToResumeSpeech(string state, bool muted, string guidance)
    {
        await using var harness = new Harness();
        harness.StartListening();
        harness.Set("voiceState", state);
        harness.Set("voiceMuted", muted);
        await harness.VoiceIdleWarning(true);
        var (text, markup) = harness.RenderedContent();
        Assert.Contains(guidance, string.Concat(text.Concat(markup)));
    }

    [Theory]
    [InlineData("ConnectVoiceAsync")]
    [InlineData("EndVoiceAsync")]
    public async Task ReconnectAndStopClearIdleWarning(string action)
    {
        await using var harness = new Harness();
        harness.StartListening();
        await harness.VoiceIdleWarning(true);
        await harness.Call(action);
        await harness.Call("StateHasChanged");
        var (text, markup) = harness.RenderedContent();
        Assert.DoesNotContain("Chat ending soon", string.Concat(text.Concat(markup)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdleEndReasonIsVisibleAndDoesNotClaimSuccessWhenEndingFails(bool failure)
    {
        await using var harness = new Harness();
        harness.StartListening();
        await harness.VoiceIdleWarning(true);
        if (failure)
        {
            harness.Service.End = _ => throw new ConciergeApiException("unavailable", "Try again.", 503);
        }
        await harness.VoiceEndRequested("idle_timeout");
        var (text, markup) = harness.RenderedContent();
        var content = string.Concat(text.Concat(markup));
        Assert.DoesNotContain("Chat ending soon", content);
        Assert.Equal(!failure, harness.Get<bool>("chatEnded"));
        if (failure)
        {
            Assert.Contains("We couldn’t end the chat.", content);
            Assert.DoesNotContain("Chat ended due to inactivity", content);
        }
        else
        {
            Assert.Contains("Chat ended due to inactivity", content);
            Assert.Contains("Verify your stay again", content);
            await harness.Call("VerifyStayAgain");
            Assert.False(harness.Get<bool>("chatEndedForInactivity"));
        }
    }

    [Fact]
    public async Task DisconnectCannotOverwriteAnIdleEndInProgress()
    {
        await using var harness = new Harness();
        var connection = harness.StartListening();
        await harness.VoiceSession(connection.Session.Id.ToString());
        var ended = new TaskCompletionSource();
        harness.Service.End = _ => ended.Task;
        var ending = harness.VoiceEndRequested("idle_timeout");
        await harness.VoiceState("disconnected", "Voice disconnected.", 4000);
        await harness.Call("ApplyVoiceRelayClosedAsync", connection.Session.Id.ToString(), 1);
        await harness.VoiceIdleWarning(true);
        Assert.Equal("Stopped (inactive)", harness.Get<string>("VoiceStateLabel"));
        Assert.False(harness.Get<bool>("voiceIdleWarning"));
        ended.SetResult();
        await ending;
        Assert.True(harness.Get<bool>("chatEndedForInactivity"));
    }

    [Fact]
    public async Task CircuitLossDuringFirstRenderStopsWithoutFaulting()
    {
        await using var harness = new Harness();
        harness.Set("ready", false);
        harness.JavaScript.Disconnected.Add("conciergeChat.loadSession");

        await harness.Call("OnAfterRenderAsync", true);

        Assert.False(harness.Get<bool>("ready"));
        Assert.DoesNotContain("conciergeChat.clearSession", harness.JavaScript.Calls);
    }

    [Fact]
    public async Task CircuitLossWhileStoppingVoiceStillEndsTheChat()
    {
        await using var harness = new Harness();
        harness.SetSession("current", Guid.NewGuid());
        harness.Set("voiceMode", true);
        harness.JavaScript.Disconnected.Add("conciergeVoice.stop");
        var endCalls = 0;
        harness.Service.End = _ =>
        {
            endCalls++;
            return Task.CompletedTask;
        };

        await harness.Call("EndChatAsync");

        Assert.Equal(1, endCalls);
        Assert.True(harness.Get<bool>("chatEnded"));
        Assert.Equal("disconnected", harness.Get<string>("voiceState"));
    }

    [Fact]
    public async Task CircuitLossWhileMutingKeepsTheLastKnownMuteState()
    {
        await using var harness = new Harness();
        harness.Set("voiceMuted", true);
        harness.JavaScript.Disconnected.Add("conciergeVoice.setMuted");

        await harness.Call("ToggleVoiceMuteAsync");

        Assert.True(harness.Get<bool>("voiceMuted"));
    }

    [Fact]
    public async Task ScriptFailuresStillPropagate()
    {
        await using var harness = new Harness();
        harness.JavaScript.Failures["conciergeChat.loadTheme"] = new JSException("loadTheme failed");

        await Assert.ThrowsAsync<JSException>(() => harness.Call("OnAfterRenderAsync", true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VoiceEndCallbackDistinguishesCompletedAndFailedInteraction(bool failure)
    {
        using var traces = new EndChatTraceCapture();
        using var logs = new CapturedLogs();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        await using var harness = new Harness();
        var voiceSessionId = Guid.NewGuid();
        harness.SetSession("private-credential", Guid.NewGuid());
        harness.Set("Logger", loggerFactory.CreateLogger<Home>());
        harness.Set("voiceMode", true);
        harness.Set("voiceSessionId", voiceSessionId.ToString());
        if (failure)
        {
            harness.Service.End = _ => throw new ConciergeApiException("unavailable", "private-error", 503);
        }

        await harness.VoiceEndRequested();

        traces.AssertEvent("concierge.voice.end_chat.browser_callback_received", voiceSessionId);
        var eventName = failure ? "completion_failed" : "completed";
        var activity = traces.AssertEvent("concierge.voice.end_chat." + eventName, voiceSessionId);
        Assert.Equal(failure ? System.Diagnostics.ActivityStatusCode.Error : System.Diagnostics.ActivityStatusCode.Unset,
            activity.Status);
        Assert.Equal(!failure, harness.Get<bool>("chatEnded"));
        var rendered = harness.RenderedContent();
        Assert.Contains(failure ? "We couldn’t end the chat." : "Chat ended",
            string.Concat(rendered.Text.Concat(rendered.Markup)));
        Assert.Contains(logs.Entries, entry =>
            Equals(entry.Values.GetValueOrDefault("EndChatEvent"), "concierge.voice.end_chat." + eventName));
        Assert.DoesNotContain(logs.Entries, entry =>
            entry.Message.Contains("private-credential", StringComparison.Ordinal)
            || entry.Message.Contains("private-error", StringComparison.Ordinal));
    }

    /// <summary>Preserves retry IDs and drafts only for the verified original stay.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReverificationOnlyRecoversTheSameStay(bool sameStay)
    {
        await using var harness = new Harness();
        var stay = Guid.NewGuid();
        harness.SetSession("old", stay);
        harness.Set("pending", new PendingTurnState("retry-id", "Old guest message", "web", DateTimeOffset.UtcNow));
        harness.Set("draft", "Old guest draft");
        await harness.Call("VerifyAgain");
        harness.Set("lastName", "Guest");
        harness.Set("roomNumber", "101");
        harness.Service.Verification = SessionResponse("new", sameStay ? stay : Guid.NewGuid());
        await harness.Call("VerifyAsync");

        Assert.Equal(sameStay ? "Old guest draft" : "", harness.Get<string>("draft"));
        Assert.Equal(sameStay ? 1 : 0, harness.Service.Submissions.Count);
        if (sameStay)
        {
            Assert.Equal("retry-id", Assert.Single(harness.Service.Submissions).TurnId);
        }
    }

    /// <summary>Rejects both successful and unauthorized history from an earlier sign-in.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldHistoryCannotChangeANewSession(bool unauthorized)
    {
        await using var harness = new Harness();
        harness.SetSession("old", Guid.NewGuid());
        var response = new TaskCompletionSource<ConciergeHistoryResult>();
        harness.Service.History = credential => credential == "old"
            ? response.Task
            : Task.FromResult(History("new-turn"));
        var oldRead = harness.Call("LoadHistoryAndReconcileAsync", false);
        await harness.Call("VerifyAgain");
        harness.Set("lastName", "Guest");
        harness.Set("roomNumber", "101");
        harness.Service.Verification = SessionResponse("new", Guid.NewGuid());
        await harness.Call("VerifyAsync");
        if (unauthorized)
        {
            response.SetException(new ConciergeApiException("unauthorized", "Expired", 401));
        }
        else
        {
            response.SetResult(History("old-turn"));
        }
        await oldRead;

        Assert.Equal("new-turn", Assert.Single(harness.Get<List<ConciergeTranscriptTurn>>("turns")).TurnId);
        Assert.False(harness.Get<bool>("hasExpired"));
    }

    [Fact]
    public async Task RepeatedSpeakingNotificationsDoNotRefreshHistoryButDisconnectStillRecovers()
    {
        await using var harness = new Harness();
        var connection = harness.StartListening();
        await harness.VoiceSession(connection.Session.Id.ToString());
        var historyReads = 0;
        harness.Service.History = _ =>
        {
            Interlocked.Increment(ref historyReads);
            return Task.FromResult(History("recovered"));
        };

        for (var index = 0; index < 12; index++)
        {
            await harness.VoiceState("speaking", "Speaking", 0);
        }

        Assert.Equal(0, Volatile.Read(ref historyReads));
        await harness.VoiceState("disconnected", "Voice disconnected.", 1011);

        Assert.Equal(1, Volatile.Read(ref historyReads));
        Assert.Equal("recovered", Assert.Single(harness.Get<List<ConciergeTranscriptTurn>>("turns")).TurnId);
    }

    [Fact]
    public async Task CompletionDuringHistoryReadQueuesAnotherReadInsteadOfLosingRefresh()
    {
        await using var harness = new Harness();
        harness.SetSession("credential", Guid.NewGuid());
        var firstResponse = new TaskCompletionSource<ConciergeHistoryResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondResponse = new TaskCompletionSource<ConciergeHistoryResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var historyReads = 0;
        harness.Service.History = _ => Interlocked.Increment(ref historyReads) switch
        {
            1 => firstResponse.Task,
            2 => secondResponse.Task,
            _ => throw new InvalidOperationException("Unexpected history read."),
        };

        var firstCompletion = harness.Call("LoadHistoryAndReconcileAsync", false);
        await harness.WaitUntil(() => Volatile.Read(ref historyReads) == 1);
        var secondCompletion = harness.Call("LoadHistoryAndReconcileAsync", false);
        firstResponse.SetResult(History("older-snapshot"));
        await harness.WaitUntil(() => Volatile.Read(ref historyReads) == 2);
        secondResponse.SetResult(History("latest-snapshot"));
        await Task.WhenAll(firstCompletion, secondCompletion);

        Assert.Equal(2, Volatile.Read(ref historyReads));
        Assert.Equal(
            "latest-snapshot",
            Assert.Single(harness.Get<List<ConciergeTranscriptTurn>>("turns")).TurnId);
    }

    /// <summary>Prevents a history response from repopulating an ended interaction.</summary>
    [Fact]
    public async Task EndInvalidatesOutstandingHistory()
    {
        await using var harness = new Harness();
        harness.SetSession("old", Guid.NewGuid());
        var response = new TaskCompletionSource<ConciergeHistoryResult>();
        harness.Service.History = _ => response.Task;
        var oldRead = harness.Call("LoadHistoryAndReconcileAsync", false);
        await harness.Call("EndInteractionAsync");
        response.SetResult(History("late-turn"));
        await oldRead;
        Assert.Empty(harness.Get<List<ConciergeTranscriptTurn>>("turns"));
        Assert.True(harness.Get<bool>("chatEnded"));
    }

    /// <summary>Rejects an earlier credential's turn completion and failure.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldSubmissionCannotChangeANewSession(bool unauthorized)
    {
        await using var harness = new Harness();
        harness.SetSession("old", Guid.NewGuid());
        harness.Set("pending", new PendingTurnState("old-turn", "Old", "web", DateTimeOffset.UtcNow));
        var response = new TaskCompletionSource<ConciergeTurnResult>();
        var submitted = false;
        harness.Service.Submission = request =>
        {
            if (submitted) return Task.FromResult(new ConciergeTurnResult(request.TurnId, "Reply", 1));
            submitted = true;
            return response.Task;
        };
        var oldSubmit = harness.Call("SubmitPendingAsync");
        await harness.Call("VerifyAgain");
        harness.Set("lastName", "Guest");
        harness.Set("roomNumber", "101");
        harness.Service.Verification = SessionResponse("new", Guid.NewGuid());
        await harness.Call("VerifyAsync");
        if (unauthorized)
        {
            response.SetException(new ConciergeApiException("unauthorized", "Expired", 401));
        }
        else
        {
            response.SetResult(new ConciergeTurnResult("old-turn", "Old reply", 1));
        }
        await oldSubmit;
        Assert.False(harness.Get<bool>("hasExpired"));
        Assert.False(harness.Get<bool>("isWorking"));
        Assert.Null(harness.Get<object?>("turnError"));
    }

    /// <summary>Reconnects the replaced transcript on cancel and disconnects on end.</summary>
    [Fact]
    public async Task CancelRewiresTranscriptAndEndDisconnectsIt()
    {
        await using var harness = new Harness();
        harness.SetSession("old", Guid.NewGuid());
        harness.Set("draft", "Keep this");
        var rideFeedCancellation = new CancellationTokenSource();
        harness.Set("rideActivityCancellation", rideFeedCancellation);
        harness.Set("rideActivityCredential", "old");
        await harness.Call("OnAfterRenderAsync", false);
        Assert.Equal(1, harness.JavaScript.Calls.Count(call => call == "conciergeChat.wireTranscript"));
        await harness.Call("EndChatAsync");
        Assert.True(rideFeedCancellation.IsCancellationRequested);
        await harness.Call("OnAfterRenderAsync", false);
        Assert.Contains("conciergeChat.unwireTranscript", harness.JavaScript.Calls);
        await harness.Call("CancelEndChat");
        await harness.WaitUntil(() => harness.Service.RideStreamFromOffset == 0);
        Assert.Equal(0, harness.Service.RideStreamFromOffset);
        await harness.Call("OnAfterRenderAsync", false);
        Assert.Equal(2, harness.JavaScript.Calls.Count(call => call == "conciergeChat.wireTranscript"));
        await harness.Call("EndInteractionAsync");
        await harness.Call("OnAfterRenderAsync", false);
        Assert.Equal(2, harness.JavaScript.Calls.Count(call => call == "conciergeChat.unwireTranscript"));
    }

    [Theory]
    [InlineData("listening")]
    [InlineData("paused")]
    [InlineData("disconnected")]
    public async Task EndVoiceStopsVoiceAndKeepsTheChat(string state)
    {
        await using var harness = new Harness();
        harness.StartListening();
        harness.Set("voiceState", state);
        harness.Set("voiceSessionId", "voice-session");
        harness.Service.History = _ => Task.FromResult(History("latest-voice-turn"));

        await harness.Call("EndVoiceAsync");

        Assert.False(harness.Get<bool>("voiceMode"));
        Assert.Null(harness.Get<string?>("voiceSessionId"));
        Assert.False(harness.Get<bool>("chatEnded"));
        Assert.NotNull(harness.Get<ChatSessionState?>("session"));
        Assert.Contains("conciergeVoice.stop", harness.JavaScript.Calls);
        Assert.Equal("latest-voice-turn", Assert.Single(harness.Get<List<ConciergeTranscriptTurn>>("turns")).TurnId);
    }

    /// <summary>Does not accept verification or end responses after another sign-in starts.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldSessionTransitionCannotReplaceANewSession(bool ending)
    {
        await using var harness = new Harness();
        harness.SetSession("old", Guid.NewGuid());
        var verification = new TaskCompletionSource<ConciergeSessionResponse>();
        var end = new TaskCompletionSource();
        harness.Service.Verify = _ => verification.Task;
        harness.Service.End = _ => end.Task;
        harness.Set("lastName", "Old");
        harness.Set("roomNumber", "101");
        var oldTransition = harness.Call(ending ? "EndInteractionAsync" : "VerifyAsync");
        await harness.Call("VerifyAgain");
        harness.Service.Verify = null;
        harness.Service.Verification = SessionResponse("new", Guid.NewGuid());
        harness.Set("lastName", "New");
        harness.Set("roomNumber", "102");
        await harness.Call("VerifyAsync");
        verification.SetResult(SessionResponse("stale", Guid.NewGuid()));
        end.SetResult();
        await oldTransition;
        Assert.Equal("new", harness.Get<ChatSessionState>("session").Credential);
        Assert.False(harness.Get<bool>("chatEnded"));
        Assert.False(harness.Get<bool>("isVerifying"));
        Assert.False(harness.Get<bool>("isEnding"));
    }

    /// <summary>Drops legacy unbound recovery even if verification cannot supply an identity.</summary>
    [Fact]
    public async Task UnknownStayIdentityNeverReplaysPending()
    {
        await using var harness = new Harness();
        harness.SetSession("old", Guid.Empty);
        harness.Set("pending", new PendingTurnState("old-turn", "Old", "web", DateTimeOffset.UtcNow));
        harness.Set("draft", "Old draft");
        await harness.Call("VerifyAgain");
        harness.Set("lastName", "Guest");
        harness.Set("roomNumber", "101");
        harness.Service.Verification = SessionResponse("new", Guid.Empty);
        await harness.Call("VerifyAsync");
        Assert.Empty(harness.Service.Submissions);
        Assert.Equal("", harness.Get<string>("draft"));
    }

    /// <summary>Reconciliation clears an already committed same-stay retry without resubmitting.</summary>
    [Fact]
    public async Task SameStayCommittedPendingIsNotResubmitted()
    {
        await using var harness = new Harness();
        var stay = Guid.NewGuid();
        harness.SetSession("old", stay);
        harness.Set("pending", new PendingTurnState("committed", "Old", "web", DateTimeOffset.UtcNow));
        harness.Set("draft", "Keep draft");
        await harness.Call("VerifyAgain");
        harness.Set("lastName", "Guest");
        harness.Set("roomNumber", "101");
        harness.Service.Verification = SessionResponse("new", stay);
        harness.Service.History = _ => Task.FromResult(History("committed"));
        await harness.Call("VerifyAsync");
        Assert.Empty(harness.Service.Submissions);
        Assert.Null(harness.Get<object?>("pending"));
        Assert.Equal("Keep draft", harness.Get<string>("draft"));
    }

    [Fact]
    public async Task StartOverClearsTheChatAndKeepsTheGuestSignedIn()
    {
        await using var harness = new Harness();
        harness.SetSession("current", Guid.NewGuid());
        harness.Set("turns", History("before-reset").Turns);
        harness.Set("draft", "Unsent words");

        await harness.Call("StartOverAsync");
        Assert.True(harness.Get<bool>("showResetConfirmation"));
        Assert.Equal(0, harness.Service.ResetCalls);

        await harness.Call("ResetStayAsync");

        Assert.Equal(1, harness.Service.ResetCalls);
        Assert.Empty(harness.Get<List<ConciergeTranscriptTurn>>("turns"));
        Assert.Equal(string.Empty, harness.Get<string>("draft"));
        Assert.Equal("current", harness.Get<ChatSessionState>("session").Credential);
        Assert.False(harness.Get<bool>("showResetConfirmation"));
        Assert.False(harness.Get<bool>("isResetting"));
        Assert.False(harness.Get<bool>("chatEnded"));
        Assert.Contains("conciergeChat.saveSession", harness.JavaScript.Calls);
        Assert.DoesNotContain("conciergeChat.clearSession", harness.JavaScript.Calls);
    }

    [Fact]
    public async Task FailedStartOverKeepsTheChatAndShowsWhatDidNotHappen()
    {
        await using var harness = new Harness();
        harness.SetSession("current", Guid.NewGuid());
        harness.Set("turns", History("kept").Turns);
        const string guidance = "Your rides were removed, but the conversation could not be cleared. Please try Start over again.";
        harness.Service.Reset = _ => Task.FromException(new ConciergeApiException("reset_incomplete", guidance, 503));

        await harness.Call("StartOverAsync");
        await harness.Call("ResetStayAsync");

        Assert.Equal(guidance, harness.Get<string>("resetError"));
        Assert.True(harness.Get<bool>("showResetConfirmation"));
        Assert.False(harness.Get<bool>("isResetting"));
        Assert.Equal("kept", Assert.Single(harness.Get<List<ConciergeTranscriptTurn>>("turns")).TurnId);

        await harness.Call("CancelResetStay");
        Assert.False(harness.Get<bool>("showResetConfirmation"));
        Assert.Null(harness.Get<string?>("resetError"));
        Assert.Equal("current", harness.Get<ChatSessionState>("session").Credential);
    }

    [Fact]
    public async Task UnauthorizedStartOverMarksTheSessionExpired()
    {
        await using var harness = new Harness();
        harness.SetSession("current", Guid.NewGuid());
        harness.Service.Reset = _ => Task.FromException(
            new ConciergeApiException("unauthorized", "Authenticate again to continue.", 401));

        await harness.Call("StartOverAsync");
        await harness.Call("ResetStayAsync");

        Assert.True(harness.Get<bool>("hasExpired"));
        Assert.False(harness.Get<bool>("showResetConfirmation"));
    }

    [Fact]
    public async Task StartOverInvalidatesOutstandingHistory()
    {
        await using var harness = new Harness();
        harness.SetSession("current", Guid.NewGuid());
        var response = new TaskCompletionSource<ConciergeHistoryResult>();
        harness.Service.History = _ => response.Task;
        var oldRead = harness.Call("LoadHistoryAndReconcileAsync", false);

        await harness.Call("StartOverAsync");
        await harness.Call("ResetStayAsync");
        response.SetResult(History("late-turn"));
        await oldRead;

        Assert.Empty(harness.Get<List<ConciergeTranscriptTurn>>("turns"));
    }

    /// <summary>Offers same-ID recovery after failed End discards an outstanding reply callback.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FailedEndKeepsPendingReplyRecoverable(bool replyCompleted, bool unexpectedFailure)
    {
        await using var harness = new Harness();
        harness.SetSession("current", Guid.NewGuid());
        var pending = new PendingTurnState("pending-id", "Original message", "web", DateTimeOffset.UtcNow);
        harness.Set("pending", pending);
        harness.Set("draft", "Separate draft");
        var response = new TaskCompletionSource<ConciergeTurnResult>();
        harness.Service.Submission = _ => response.Task;
        var submission = harness.Call("SubmitPendingAsync");
        await harness.Call("EndChatAsync");
        harness.Service.End = _ => Task.FromException(unexpectedFailure
            ? new InvalidOperationException("Connection failed")
            : new ConciergeApiException("unavailable", "End unavailable", 503));
        await harness.Call("EndInteractionAsync");
        if (replyCompleted)
        {
            response.SetResult(new ConciergeTurnResult(pending.TurnId, "Reply", 1));
            await submission;
        }

        await harness.Call("CancelEndChat");
        Assert.False(harness.Get<bool>("showEndConfirmation"));
        Assert.False(harness.Get<bool>("isWorking"));
        Assert.False(harness.Get<bool>("isEnding"));
        Assert.Contains("Retry", harness.Get<string>("turnError"));
        Assert.Equal(pending, harness.Get<PendingTurnState>("pending"));
        Assert.Equal("Separate draft", harness.Get<string>("draft"));

        harness.Service.History = _ => Task.FromResult(replyCompleted
            ? History(pending.TurnId)
            : new ConciergeHistoryResult([]));
        var retry = harness.Call("RetryAsync");
        if (!replyCompleted)
        {
            await harness.WaitUntil(() => harness.Service.Submissions.Count == 2);
            Assert.Equal(2, harness.Service.Submissions.Count);
            harness.Service.History = _ => Task.FromResult(History(pending.TurnId));
            response.SetResult(new ConciergeTurnResult(pending.TurnId, "Reply", 1));
            await submission;
        }
        await retry;

        Assert.All(harness.Service.Submissions, request =>
            Assert.Equal(new ConciergeTurnRequest(pending.TurnId, pending.UserMessage, pending.Channel), request));
        Assert.Equal(replyCompleted ? 1 : 2, harness.Service.Submissions.Count);
        Assert.Null(harness.Get<object?>("pending"));
        Assert.Null(harness.Get<object?>("turnError"));
        Assert.False(harness.Get<bool>("isWorking"));
        Assert.Equal("Separate draft", harness.Get<string>("draft"));
        Assert.Equal(pending.TurnId, Assert.Single(harness.Get<List<ConciergeTranscriptTurn>>("turns")).TurnId);
    }

    /// <summary>Renders assistant Markdown while guest and pending guest text stay literal.</summary>
    [Fact]
    public async Task OnlyAssistantRepliesRenderMarkdown()
    {
        await using var harness = new Harness();
        harness.SetSession("current", Guid.NewGuid());
        harness.Set("turns", new List<ConciergeTranscriptTurn>
        {
            new("turn-1", 1, "web", "**x** <b>y</b>", "**Gym** <b>z</b>", DateTimeOffset.UtcNow)
        });
        harness.Set("pending", new PendingTurnState("turn-2", "**p** <i>q</i>", "web", DateTimeOffset.UtcNow));

        await harness.Call("StateHasChanged");
        var (text, markup) = harness.RenderedContent();

        Assert.Contains("**x** <b>y</b>", text);
        Assert.Contains("**p** <i>q</i>", text);
        Assert.Contains("<p><strong>Gym</strong> &lt;b&gt;z&lt;/b&gt;</p>", markup);
        Assert.DoesNotContain(markup, content => content.Contains("<b>", StringComparison.Ordinal) || content.Contains("**x**", StringComparison.Ordinal) || content.Contains("**p**", StringComparison.Ordinal));
    }

    /// <summary>Places voice actions and status in the header while retaining the chat composer.</summary>
    [Fact]
    public async Task ActiveVoiceControlsStayInHeaderInKeyboardOrderAndComposerRemainsAvailable()
    {
        await using var harness = new Harness();
        harness.StartListening();
        await harness.Call("StateHasChanged");

        var elements = harness.RenderedElements();
        var header = elements.First(element => element.Tag == "header");
        var controls = elements
            .Where(element => element.Index > header.Index && element.Index < header.EndIndex)
            .Where(element => element.Tag == "button")
            .ToArray();

        Assert.Equal(5, controls.Length);
        Assert.Equal(
            ["Activity", "Mute microphone", "End voice", "Start over", "End chat"],
            controls.Select(element => element.Attributes.GetValueOrDefault("aria-label")));
        Assert.Equal("ride-activity-panel", controls[0].Attributes.GetValueOrDefault("aria-controls"));
        // The visible label switches Mute/Unmute, so the name carries state; aria-pressed would double it.
        Assert.False(controls[1].Attributes.ContainsKey("aria-pressed"));
        Assert.All(controls, element => Assert.False(element.Attributes.ContainsKey("tabindex")));
        Assert.Contains(elements, element =>
            element.Tag == "div"
            && element.Index > header.Index && element.Index < header.EndIndex
            && Equals(element.Attributes.GetValueOrDefault("role"), "status")
            && Equals(element.Attributes.GetValueOrDefault("aria-live"), "polite"));
        Assert.Contains(elements, element => element.Tag == "textarea");
        Assert.DoesNotContain(elements, element =>
            element.Tag == "section"
            && Equals(element.Attributes.GetValueOrDefault("aria-label"), "Voice conversation"));
        Assert.DoesNotContain(elements, element =>
            Equals(element.Attributes.GetValueOrDefault("aria-label"), "Disconnect voice"));
        // Razor can coalesce static SVGs into markup frames instead of element frames.
        var (_, markup) = harness.RenderedContent();
        Assert.Contains(markup, item =>
            item.Contains("<svg", StringComparison.Ordinal)
            && item.Contains("aria-hidden=\"true\"", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("disconnected", "Voice stopped.", "Disconnected")]
    [InlineData("disconnected", "Voice service is unavailable.", "Disconnected")]
    [InlineData("paused", "Voice paused while this page was hidden.", "Paused")]
    [InlineData("listening", "Microphone ready.", "Listening")]
    public async Task VoiceStatusShowsOnlyTheConciseState(string state, string diagnostic, string label)
    {
        await using var harness = new Harness();
        harness.StartListening();
        await harness.VoiceState(state, diagnostic, 0);
        await harness.Call("StateHasChanged");

        var (text, markup) = harness.RenderedContent();
        var content = string.Concat(text.Concat(markup));
        Assert.Contains(label, content);
        Assert.DoesNotContain(diagnostic, content);
        Assert.Contains(harness.RenderedElements(), element =>
            element.Tag == "textarea" && !element.Attributes.ContainsKey("disabled"));
    }

    [Fact]
    public async Task MutedVoiceControlNamesTheUnmuteAction()
    {
        await using var harness = new Harness();
        harness.StartListening();
        harness.Set("voiceMuted", true);
        await harness.Call("StateHasChanged");

        var elements = harness.RenderedElements();
        var header = elements.First(element => element.Tag == "header");
        var muteButton = Assert.Single(elements, element =>
            element.Tag == "button"
            && element.Index > header.Index && element.Index < header.EndIndex
            && Equals(element.Attributes.GetValueOrDefault("aria-label"), "Unmute microphone"));

        Assert.False(muteButton.Attributes.ContainsKey("aria-pressed"));
        Assert.DoesNotContain(elements, element =>
            Equals(element.Attributes.GetValueOrDefault("aria-label"), "Mute microphone"));
    }

    [Theory]
    [InlineData("listening")]
    [InlineData("paused")]
    [InlineData("disconnected")]
    public async Task VoiceToolbarRetainsTheThemePickerAfterTheSessionActions(string state)
    {
        await using var harness = new Harness();
        harness.StartListening();
        harness.Set("voiceState", state);
        await harness.Call("StateHasChanged");

        var elements = harness.RenderedElements();
        var header = elements.First(element => element.Tag == "header");
        var headerActions = Assert.Single(elements, element =>
            element.Tag == "div"
            && Equals(element.Attributes.GetValueOrDefault("class"), "header-actions voice-header-actions"));
        var controls = Assert.Single(elements, element =>
            element.Tag == "div"
            && Equals(element.Attributes.GetValueOrDefault("class"), "voice-header-controls has-voice-mode"));
        var themePicker = Assert.Single(elements, element =>
            element.Tag == "label"
            && Equals(element.Attributes.GetValueOrDefault("class"), "theme-picker compact-theme-picker"));
        var themeSelect = Assert.Single(elements, element =>
            element.Tag == "select"
            && Equals(element.Attributes.GetValueOrDefault("aria-label"), "Color theme"));
        var endChat = Assert.Single(elements, element =>
            element.Tag == "button"
            && Equals(element.Attributes.GetValueOrDefault("aria-label"), "End chat"));
        var endVoice = Assert.Single(elements, element =>
            element.Tag == "button"
            && Equals(element.Attributes.GetValueOrDefault("aria-label"), "End voice"));
        var startOver = Assert.Single(elements, element =>
            element.Tag == "button"
            && Equals(element.Attributes.GetValueOrDefault("aria-label"), "Start over"));
        Assert.True(endVoice.Index < endChat.Index);
        Assert.Equal(startOver.Index, endVoice.EndIndex);
        // Start over sits directly between End voice and End chat among the toolbar's children.
        var siblings = elements.Where(element => element.ParentIndex == controls.Index).ToList();
        Assert.Equal(siblings.IndexOf(startOver) + 1, siblings.IndexOf(endChat));
        Assert.Equal(controls.Index, startOver.ParentIndex);
        Assert.Equal(header.Index, headerActions.ParentIndex);
        Assert.Equal(headerActions.Index, controls.ParentIndex);
        Assert.Equal(controls.Index, endVoice.ParentIndex);
        Assert.Equal(controls.Index, endChat.ParentIndex);
        Assert.Equal(controls.Index, themePicker.ParentIndex);
        Assert.True(themePicker.Index > endChat.Index);
        Assert.Equal(themePicker.Index, themeSelect.ParentIndex);
        Assert.False(themeSelect.Attributes.ContainsKey("disabled"));
        var options = elements
            .Where(element => element.Tag == "option" && element.ParentIndex == themeSelect.Index)
            .Select(element => element.Attributes.GetValueOrDefault("value"));
        Assert.Equal(["system", "light", "dark"], options);
    }

    [Fact]
    public async Task VerificationAndEndedViewsShareFocusedPageChrome()
    {
        await using var verificationHarness = new Harness();
        await verificationHarness.Call("StateHasChanged");

        var verificationElements = verificationHarness.RenderedElements();
        var verificationHeader = Assert.Single(verificationElements, element => element.Tag == "header");
        var verificationControls = Assert.Single(verificationElements, element =>
            element.Tag == "div"
            && element.Index > verificationHeader.Index && element.Index < verificationHeader.EndIndex
            && Equals(element.Attributes.GetValueOrDefault("class"), "voice-header-controls"));
        var verificationPicker = Assert.Single(verificationElements, element =>
            element.Tag == "label"
            && element.Index > verificationControls.Index && element.Index < verificationControls.EndIndex
            && Equals(element.Attributes.GetValueOrDefault("class"), "theme-picker compact-theme-picker"));
        var verificationSelect = Assert.Single(verificationElements, element =>
            element.Tag == "select"
            && element.Index > verificationPicker.Index && element.Index < verificationPicker.EndIndex);
        var verificationMain = Assert.Single(verificationElements, element =>
            element.Tag == "main" && Equals(element.Attributes.GetValueOrDefault("class"), "main-content focus-main"));

        Assert.Equal("site-header", verificationHeader.Attributes.GetValueOrDefault("class"));
        Assert.Equal("Chat controls", verificationControls.Attributes.GetValueOrDefault("aria-label"));
        Assert.Equal("Color theme", verificationSelect.Attributes.GetValueOrDefault("aria-label"));
        Assert.Contains("Verify your stay", string.Concat(verificationHarness.RenderedContent().Text));
        Assert.Contains("Verify and continue", string.Concat(verificationHarness.RenderedContent().Text));
        Assert.Contains(verificationHarness.RenderedContent().Markup, markup =>
            markup.Contains("class=\"theme-icon\"", StringComparison.Ordinal));
        Assert.Equal(
            ["system", "light", "dark"],
            verificationElements
                .Where(element => element.Tag == "option"
                    && element.Index > verificationSelect.Index && element.Index < verificationSelect.EndIndex)
                .Select(element => element.Attributes.GetValueOrDefault("value")));

        await using var harness = new Harness();
        harness.SetSession("current", Guid.NewGuid());
        harness.Set("draft", "Save my draft.");
        await harness.Call("EndChatAsync");
        await harness.Call("StateHasChanged");

        var confirmationElements = harness.RenderedElements();
        var confirmation = Assert.Single(confirmationElements, element =>
            element.Tag == "section" && Equals(element.Attributes.GetValueOrDefault("role"), "alertdialog"));
        Assert.Equal("true", confirmation.Attributes.GetValueOrDefault("aria-modal"));
        var (confirmationText, confirmationMarkup) = harness.RenderedContent();
        var confirmationContent = string.Concat(confirmationText.Concat(confirmationMarkup));
        Assert.Contains("End this chat?", confirmationContent);
        Assert.Contains("Keep chatting", confirmationContent);

        harness.Service.End = _ => throw new ConciergeApiException("unavailable", "Try again.", 503);
        await harness.Call("EndInteractionAsync");
        await harness.Call("StateHasChanged");
        var failedEndElements = harness.RenderedElements();
        Assert.Contains(failedEndElements, element =>
            element.Tag == "section"
            && Equals(element.Attributes.GetValueOrDefault("role"), "alertdialog")
            && Equals(element.Attributes.GetValueOrDefault("aria-modal"), "true"));
        Assert.Contains(failedEndElements, element =>
            element.Tag == "div" && Equals(element.Attributes.GetValueOrDefault("role"), "alert"));
        Assert.Equal("Try again.", harness.Get<string>("endError"));

        harness.Service.End = _ => Task.CompletedTask;
        await harness.Call("EndInteractionAsync");
        await harness.Call("StateHasChanged");

        var elements = harness.RenderedElements();
        var header = Assert.Single(elements, element => element.Tag == "header");
        var controls = Assert.Single(elements, element =>
            element.Tag == "div"
            && element.Index > header.Index && element.Index < header.EndIndex
            && Equals(element.Attributes.GetValueOrDefault("class"), "voice-header-controls"));
        var picker = Assert.Single(elements, element =>
            element.Tag == "label"
            && element.Index > controls.Index && element.Index < controls.EndIndex
            && Equals(element.Attributes.GetValueOrDefault("class"), "theme-picker compact-theme-picker"));
        var select = Assert.Single(elements, element =>
            element.Tag == "select"
            && element.Index > picker.Index && element.Index < picker.EndIndex);
        var main = Assert.Single(elements, element =>
            element.Tag == "main" && Equals(element.Attributes.GetValueOrDefault("class"), "main-content focus-main"));

        Assert.Equal(verificationHeader.Attributes.GetValueOrDefault("class"), header.Attributes.GetValueOrDefault("class"));
        Assert.Equal(verificationControls.Attributes.GetValueOrDefault("class"), controls.Attributes.GetValueOrDefault("class"));
        Assert.Equal(verificationControls.Attributes.GetValueOrDefault("aria-label"), controls.Attributes.GetValueOrDefault("aria-label"));
        Assert.Equal(verificationPicker.Attributes.GetValueOrDefault("class"), picker.Attributes.GetValueOrDefault("class"));
        Assert.Equal("Color theme", select.Attributes.GetValueOrDefault("aria-label"));
        Assert.False(select.Attributes.ContainsKey("disabled"));
        Assert.Equal(verificationMain.Attributes.GetValueOrDefault("class"), main.Attributes.GetValueOrDefault("class"));
        Assert.Contains("Chat ended", string.Concat(harness.RenderedContent().Text));
        Assert.Single(elements, element =>
            element.Tag == "button"
            && Equals(element.Attributes.GetValueOrDefault("class"), "primary-button focus-card-action"));
        Assert.Contains(harness.RenderedContent().Markup, markup =>
            markup.Contains("Verify stay again", StringComparison.Ordinal));
        Assert.Contains(harness.RenderedContent().Markup, markup =>
            markup.Contains("class=\"theme-icon\"", StringComparison.Ordinal));
        Assert.Equal(
            ["system", "light", "dark"],
            elements
                .Where(element => element.Tag == "option"
                    && element.Index > select.Index && element.Index < select.EndIndex)
                .Select(element => element.Attributes.GetValueOrDefault("value")));
    }

    [Theory]
    [InlineData("paused", "Resume voice")]
    [InlineData("disconnected", "Reconnect voice")]
    public async Task PausedAndDisconnectedVoiceExposeOnlyTheApplicableReconnectAction(string state, string action)
    {
        await using var harness = new Harness();
        harness.StartListening();
        harness.Set("voiceState", state);
        await harness.Call("StateHasChanged");

        var elements = harness.RenderedElements();
        var header = elements.First(element => element.Tag == "header");
        var controls = elements
            .Where(element => element.Index > header.Index && element.Index < header.EndIndex)
            .Where(element => element.Tag == "button")
            .ToArray();

        Assert.Equal(5, controls.Length);
        Assert.Equal(
            ["Activity", action, "End voice", "Start over", "End chat"],
            controls.Select(element => element.Attributes.GetValueOrDefault("aria-label")));
        Assert.Equal("ride-activity-panel", controls[0].Attributes.GetValueOrDefault("aria-controls"));
        Assert.DoesNotContain(controls, element => Equals(element.Attributes.GetValueOrDefault("aria-label"), "Mute microphone"));
    }

    /// <summary>Shows the interruption marker only on interrupted assistant replies.</summary>
    [Fact]
    public async Task InterruptedTranscriptMarkerAppearsOnlyForInterruptedAssistantReplies()
    {
        await using var harness = new Harness();
        harness.SetSession("current", Guid.NewGuid());
        harness.Set("turns", new List<ConciergeTranscriptTurn>
        {
            new("interrupted-turn", 1, "voice", "Interrupted guest", "Interrupted answer", DateTimeOffset.UtcNow, Interrupted: true),
            new("ordinary-turn", 2, "web", "Ordinary guest", "Ordinary answer", DateTimeOffset.UtcNow)
        });

        await harness.Call("StateHasChanged");
        var (text, markup) = harness.RenderedContent();
        var messages = harness.Get<IEnumerable<object>>("Messages").ToArray();
        var interruptedFlags = messages
            .Select(message => (bool)message.GetType().GetProperty("Interrupted")!.GetValue(message)!)
            .ToArray();
        const string indicator = "<span class=\"interrupted-indicator\">Interrupted</span>";
        var indicatorCount = text.Count(item => item == "Interrupted")
            + markup.Sum(item => item.Split(indicator, StringSplitOptions.None).Length - 1);

        Assert.Equal(new[] { false, true, false, false }, interruptedFlags);
        Assert.Equal(1, indicatorCount);
    }

    /// <summary>Opening ride activity reads one snapshot, leaving later entries unread.</summary>
    [Fact]
    public async Task OpeningRideActivityReadsOnlyTheDisplayedSnapshot()
    {
        await using var harness = new Harness();
        harness.SetSession("current", Guid.NewGuid());
        var rideId = Guid.NewGuid();
        var bookedAt = new DateTimeOffset(2026, 10, 24, 16, 0, 0, TimeSpan.Zero);
        var completedAt = bookedAt.AddMinutes(30);
        var entries = harness.Get<List<RideActivityEntry>>("rideActivityEntries");
        entries.Add(new RideActivityEntry(
            rideId, 1, "Booked", bookedAt, "Hotel ZGY", "Museum", bookedAt.AddHours(1)));
        entries.Add(new RideActivityEntry(
            rideId, 2, "DriverAssigned", bookedAt.AddMinutes(10), "Hotel ZGY", "Museum",
            bookedAt.AddHours(1), "Jordan Lee", "Black sedan", "ZGY 204"));
        entries.Add(new RideActivityEntry(
            rideId, 3, "Completed", completedAt, "Hotel ZGY", "Museum", bookedAt.AddHours(1)));
        entries.Add(new RideActivityEntry(
            rideId, 4, "PickedUp", completedAt.AddMinutes(5), "Hotel ZGY", "Museum", bookedAt.AddHours(1)));
        entries.Add(new RideActivityEntry(
            rideId, 5, "Cancelled", completedAt.AddMinutes(10), "Hotel ZGY", "Museum", bookedAt.AddHours(1)));
        harness.Service.RideSnapshot = _ => Task.FromResult(new ConciergeRideActivitySnapshot(
            new ConciergeRideReadState(harness.Service.RideReadOffset, 0, entries.Count),
            entries.Select((entry, index) => new ConciergeRideActivityItem(index, entry)).ToArray()));
        harness.Set("rideActivityCancellation", new CancellationTokenSource());

        await harness.Call("ToggleActivity");
        var readKeys = harness.Get<HashSet<string>>("rideActivityReadEntryKeys");
        Assert.Equal(5, readKeys.Count);
        Assert.Equal(5, harness.Service.MarkedRideSnapshotOffset);

        var lateEntry = new RideActivityEntry(
            rideId, 6, "Booked", completedAt.AddMinutes(15), "Hotel ZGY", "Museum", bookedAt.AddHours(1));
        var window = harness.Get<ConciergeRideActivityWindow?>("rideActivityWindow")!;
        Assert.True(window.Apply(new ConciergeRideActivityItem(5, lateEntry)));
        await harness.Call("SynchronizeRideActivityEntries");
        await harness.Call("RefreshRideActivityReadKeys");
        Assert.DoesNotContain($"{rideId:N}:6", readKeys);
        Assert.Equal(1, harness.Get<int>("UnreadRideActivityCount"));

        await harness.Call("StateHasChanged");
        var (text, markup) = harness.RenderedContent();
        var renderedText = string.Concat(text.Concat(markup));
        Assert.True(renderedText.IndexOf("Completed", StringComparison.Ordinal)
            < renderedText.IndexOf("Driver assigned", StringComparison.Ordinal));
        Assert.True(renderedText.IndexOf("Driver assigned", StringComparison.Ordinal)
            < renderedText.LastIndexOf("Booked", StringComparison.Ordinal));
        Assert.Contains("Picked up", renderedText);
        Assert.Contains("Cancelled", renderedText);
        Assert.Contains("Jordan Lee", text);
        Assert.Contains("Black sedan", text);
        Assert.Contains("ZGY 204", text);
        Assert.Contains("12:30 PM", string.Concat(text));
        Assert.Contains("Updates for your stay · Miami time (ET)", renderedText);
        Assert.DoesNotContain("YOUR RIDES", renderedText);
        Assert.DoesNotContain(text, item => item.Contains("New", StringComparison.Ordinal));
        Assert.DoesNotContain(text, item => item.Contains("Close", StringComparison.Ordinal));
        Assert.Contains("Unread", renderedText);
        Assert.Contains(harness.RenderedElements(), element =>
            Equals(element.Attributes.GetValueOrDefault("aria-label"), "Close activity"));
    }

    [Fact]
    public async Task EmptyRideActivityUsesApprovedCopy()
    {
        await using var harness = new Harness();
        harness.SetSession("current", Guid.NewGuid());

        await harness.Call("StateHasChanged");

        var (text, markup) = harness.RenderedContent();
        var renderedText = string.Concat(text.Concat(markup));
        Assert.Contains("No activity yet", renderedText);
        Assert.Contains("Ride updates will appear here when you book a ride with the concierge.", renderedText);
    }

    [Theory]
    [InlineData("Booked", true, "2026-10-24T17:20:00Z", "Oct 24, 1:20 PM")]
    [InlineData("Booked", true, "2026-01-24T17:20:00Z", "Jan 24, 12:20 PM")]
    [InlineData("Booked", false, "2026-10-24T17:20:00Z", null)]
    [InlineData("DriverAssigned", true, "2026-10-24T17:20:00Z", null)]
    public async Task BookedActivityShowsOnlyRealDestinationArrivalInMiamiTime(
        string status, bool hasArrival, string arrival, string? expected)
    {
        await using var harness = new Harness();
        harness.SetSession("current", Guid.NewGuid());
        var changedAt = DateTimeOffset.Parse("2026-10-24T16:00:00Z");
        harness.Get<List<RideActivityEntry>>("rideActivityEntries").Add(new RideActivityEntry(
            Guid.NewGuid(), 1, status, changedAt, "Hotel ZGY", "Museum", changedAt.AddHours(1),
            ArrivalAtUtc: hasArrival ? DateTimeOffset.Parse(arrival) : null));

        await harness.Call("StateHasChanged");

        var (text, markup) = harness.RenderedContent();
        var rendered = string.Concat(text.Concat(markup));
        if (expected is null)
        {
            Assert.DoesNotContain("Estimated destination arrival", rendered);
            Assert.DoesNotContain("1:00 PM", rendered); // Never substitute the pickup time.
        }
        else
        {
            Assert.Contains("Estimated destination arrival", rendered);
            Assert.Contains(expected, rendered);
            Assert.Contains(harness.RenderedElements(), element => element.Tag == "time"
                && Equals(element.Attributes.GetValueOrDefault("datetime"),
                    DateTimeOffset.Parse(arrival).ToString("O", System.Globalization.CultureInfo.InvariantCulture)));
        }
    }

    [Fact]
    public async Task ActivityButtonIsFirstAndCapsItsUnreadBadge()
    {
        await using var harness = new Harness();
        harness.SetSession("current", Guid.NewGuid());
        var rideId = Guid.NewGuid();
        var entries = Enumerable.Range(1, 12)
            .Select(sequence => RideEntry(rideId, sequence, DateTimeOffset.UtcNow.AddMinutes(-sequence)))
            .ToArray();
        harness.Get<List<RideActivityEntry>>("rideActivityEntries").AddRange(entries);
        harness.Set("rideActivityWindow", new ConciergeRideActivityWindow(
            new ConciergeRideActivitySnapshot(
                new ConciergeRideReadState(0, 0, entries.Length),
                entries.Select((entry, offset) => new ConciergeRideActivityItem(offset, entry)).ToArray())));
        Assert.Equal(12, harness.Get<int>("UnreadRideActivityCount"));

        await harness.Call("StateHasChanged");

        var elements = harness.RenderedElements();
        Assert.Equal("Activity, 12 unread ride updates", harness.Get<string>("ActivityButtonAccessibleName"));
        var buttonNames = elements.Where(element => element.Tag == "button")
            .Select(element => element.Attributes.GetValueOrDefault("aria-label")?.ToString())
            .ToArray();
        Assert.Contains("Activity, 12 unread ride updates", buttonNames);
        var activity = Assert.Single(elements, element =>
            element.Tag == "button"
            && element.Attributes.GetValueOrDefault("aria-label") is string name
            && name.StartsWith("Activity", StringComparison.Ordinal));
        Assert.Equal("Activity, 12 unread ride updates", activity.Attributes.GetValueOrDefault("aria-label"));
        var endChat = Assert.Single(elements, element =>
            Equals(element.Attributes.GetValueOrDefault("aria-label"), "End chat"));
        Assert.True(activity.Index < endChat.Index);
        var (_, markup) = harness.RenderedContent();
        Assert.Contains(elements, element => element.Tag == "span"
            && Equals(element.Attributes.GetValueOrDefault("class"), "activity-unread-badge"));
        Assert.Contains("9+", string.Concat(harness.RenderedContent().Text));
    }

    [Fact]
    public async Task ActivityDrawerUsesOpenStateMotionAndReducedMotionOverride()
    {
        await using var harness = new Harness();
        harness.SetSession("current", Guid.NewGuid());
        await harness.Call("StateHasChanged");
        var closedDrawer = Assert.Single(harness.RenderedElements(), element => element.Tag == "aside");
        var closedClass = Assert.IsType<string>(closedDrawer.Attributes.GetValueOrDefault("class"));
        Assert.StartsWith("ride-activity-drawer", closedClass, StringComparison.Ordinal);
        Assert.DoesNotContain("is-open", closedClass, StringComparison.Ordinal);

        await harness.Call("ToggleActivity");
        Assert.Contains(harness.RenderedElements(), element =>
            element.Tag == "aside"
            && Equals(element.Attributes.GetValueOrDefault("class"), "ride-activity-drawer is-open"));

        var css = File.ReadAllText(Path.Combine(FindRepositoryRoot(),
            "src", "Concierge.Web", "Components", "Shared", "RideActivityPanel.razor.css"));
        Assert.Contains("transition:", css, StringComparison.Ordinal);
        Assert.Contains(".ride-activity-drawer.is-open", css, StringComparison.Ordinal);
        Assert.Contains("@media (prefers-reduced-motion: reduce)", css, StringComparison.Ordinal);
        Assert.Contains("transition: none", css, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 700px)", css, StringComparison.Ordinal);
        Assert.Contains("position: absolute", css, StringComparison.Ordinal);
        // Scoped .razor.css only reaches the browser through the generated bundle link.
        var appShell = File.ReadAllText(Path.Combine(FindRepositoryRoot(),
            "src", "Concierge.Web", "Components", "App.razor"));
        Assert.Contains("@Assets[\"Concierge.Web.styles.css\"]", appShell, StringComparison.Ordinal);
        var appCss = File.ReadAllText(Path.Combine(FindRepositoryRoot(),
            "src", "Concierge.Web", "wwwroot", "css", "app.css"));
        Assert.Contains(".voice-site-header .voice-header-controls.has-voice-mode", appCss, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: repeat(3, minmax(0, 1fr))", appCss, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EscapeBubblesFromActivityTriggerToPageShellAndClosesDrawer()
    {
        await using var harness = new Harness();
        harness.SetSession("current", Guid.NewGuid());
        await harness.Call("ToggleActivity");
        Assert.True(harness.Get<bool>("activityOpen"));

        var elements = harness.RenderedElements();
        var pageShell = Assert.Single(elements, element =>
            element.Tag == "div"
            && Equals(element.Attributes.GetValueOrDefault("class"), "page-shell"));
        var pageShellKeyDown = Assert.IsType<Func<KeyboardEventArgs, Task>>(
            pageShell.Attributes.GetValueOrDefault("onkeydown"));
        Assert.Contains(elements, element =>
            element.Tag == "button"
            && Equals(element.Attributes.GetValueOrDefault("aria-controls"), "ride-activity-panel"));

        // The trigger retains focus after opening, so its Escape keydown bubbles to the page shell.
        await harness.DispatchKeyDown(pageShellKeyDown, new KeyboardEventArgs { Key = "Escape" });

        Assert.False(harness.Get<bool>("activityOpen"));
    }

    [Fact]
    public void ActivityTimestampsUseTimeTodayAndDateForEarlierMiamiDays()
    {
        var panelType = typeof(Home).Assembly.GetType("Concierge.Web.Components.Shared.RideActivityPanel")!;
        var formatter = panelType.GetMethod("FormatTimestamp", BindingFlags.Static | BindingFlags.NonPublic)!;
        var miami = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var nowInMiami = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, miami);

        var today = (string)formatter.Invoke(null, [nowInMiami])!;
        var yesterday = (string)formatter.Invoke(null, [nowInMiami.AddDays(-1)])!;

        Assert.Equal(nowInMiami.ToString("h:mm tt", System.Globalization.CultureInfo.InvariantCulture), today);
        Assert.Equal(nowInMiami.AddDays(-1).ToString("MMM d, h:mm tt",
            System.Globalization.CultureInfo.InvariantCulture), yesterday);
        Assert.DoesNotContain(",", today);
        Assert.Contains(",", yesterday);
    }

    [Fact]
    public async Task EndingChatAwaitsRideStreamCancellationCleanup()
    {
        await using var harness = new Harness();
        harness.SetSession("current", Guid.NewGuid());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanedUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Service.RideStream = (_, _, cancellationToken) =>
            WaitingRideItems(cancellationToken, started, cleanedUp);

        await harness.Call("StartRideActivityFeedAsync", 0, "current");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var activeStream = harness.Get<Task?>("rideActivityStreamTask")!;

        await harness.Call("EndChatAsync");

        Assert.True(activeStream.IsCompleted);
        await cleanedUp.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(harness.Get<Task?>("rideActivityStreamTask"));
    }

    /// <summary>Starts live updates at the snapshot end and keeps the bounded, deduplicated list.</summary>
    [Fact]
    public async Task RideActivityFeedStartsAtSnapshotOffsetAndKeepsOnlyNewestTwentyFive()
    {
        await using var harness = new Harness();
        harness.SetSession("current", Guid.NewGuid());

        var rideId = Guid.NewGuid();
        var retained = Enumerable.Range(0, 30)
            .Select(index => new ConciergeRideActivityItem(
                index,
                RideEntry(rideId, index + 1, DateTimeOffset.UnixEpoch.AddMinutes(index))))
            .ToArray();
        harness.Service.RideSnapshot = _ => Task.FromResult(new ConciergeRideActivitySnapshot(
            new ConciergeRideReadState(0, 0, 30),
            retained.TakeLast(25).ToArray()));

        var arrivals = Enumerable.Range(30, 30)
            .Select(index => new ConciergeRideActivityItem(
                index,
                RideEntry(rideId, index + 1, DateTimeOffset.UnixEpoch.AddMinutes(index))))
            .ToList();
        // A publisher retry uses a new stream offset but the same ride/sequence application key.
        arrivals.Add(new ConciergeRideActivityItem(
            60,
            RideEntry(rideId, 61, DateTimeOffset.UnixEpoch.AddMinutes(60))));
        arrivals.Add(new ConciergeRideActivityItem(
            61,
            RideEntry(rideId, 61, DateTimeOffset.UnixEpoch.AddMinutes(60))));
        harness.Service.RideStream = (_, _, _) => RideItems(arrivals);

        await harness.Call("StartRideActivityFeedAsync", 0, "current");
        await harness.WaitUntil(() => harness.Get<Task?>("rideActivityStreamTask") is null);

        Assert.Equal(30, harness.Service.RideStreamFromOffset);
        var displayed = harness.Get<List<RideActivityEntry>>("rideActivityEntries");
        Assert.Equal(25, displayed.Count);
        Assert.Contains(displayed, entry => entry.Sequence == 61);
        Assert.DoesNotContain(displayed, entry => entry.Sequence == 35);
        Assert.Single(displayed, entry => entry.Sequence == 61);
    }

    /// <summary>Shows Reconnect when the relay closes while the browser's report was lost.</summary>
    [Fact]
    public async Task ServerRelayClosureDisconnectsVoiceWhenTheBrowserReportWasLost()
    {
        using var logs = new CapturedLogs();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        await using var harness = new Harness();
        harness.Set("Logger", loggerFactory.CreateLogger<Home>());
        var connection = harness.StartListening();
        await harness.VoiceSession(connection.Session.Id.ToString());

        harness.Voice.Close(connection);
        await harness.WaitUntil(() => harness.Get<string>("voiceState") == "disconnected");

        Assert.Equal("Voice disconnected.", harness.Get<string>("voiceMessage"));
        await harness.Call("StateHasChanged");
        var (text, markup) = harness.RenderedContent();
        var content = text.Concat(markup).ToList();
        Assert.Contains(content, item => item.Contains("Reconnect", StringComparison.Ordinal));
        Assert.Contains(content, item => item.Contains("Disconnected", StringComparison.Ordinal));
        Assert.DoesNotContain(content, item => item.Contains("Voice disconnected.", StringComparison.Ordinal));
        Assert.Contains(content, item => item.Contains("End chat", StringComparison.Ordinal));
        Assert.Contains(logs.Entries, entry =>
            entry.Message.StartsWith("Voice relay closure applied by server", StringComparison.Ordinal)
            && Equals(entry.Values.GetValueOrDefault("VoiceState"), "listening")
            && Equals(entry.Values.GetValueOrDefault("ConnectionNumber"), 1));
        Assert.DoesNotContain("conciergeVoice.stop", harness.JavaScript.Calls);

        // The browser's late report of the same closure still applies cleanly.
        await harness.VoiceState("disconnected", "Voice disconnected.", 4000);
        Assert.Equal("disconnected", harness.Get<string>("voiceState"));
    }

    /// <summary>Ignores closures for a voice session the page no longer shows.</summary>
    [Fact]
    public async Task ServerRelayClosureForAnotherVoiceSessionIsIgnored()
    {
        await using var harness = new Harness();
        var stale = harness.StartListening();
        await harness.VoiceSession(stale.Session.Id.ToString());
        var current = harness.Voice.Open(stale.Session.Stay, null);
        await harness.VoiceSession(current.Session.Id.ToString());

        harness.Voice.Close(stale);
        await harness.Call("ApplyVoiceRelayClosedAsync", stale.Session.Id.ToString(), 1);

        Assert.Equal("listening", harness.Get<string>("voiceState"));
        Assert.Null(harness.Get<string?>("voiceMessage"));
    }

    [Fact]
    public async Task ForeignVoiceSessionIsRejectedWithoutSubscribingOrDeliveringTurns()
    {
        using var logs = new CapturedLogs();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        await using var harness = new Harness();
        harness.Set("Logger", loggerFactory.CreateLogger<Home>());
        var ownConnection = harness.StartListening();
        var foreignConnection = harness.Voice.Open(
            new ConciergeSessionContext(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(1)),
            null);

        await harness.VoiceSession(foreignConnection.Session.Id.ToString());
        harness.Voice.Close(foreignConnection);
        await foreignConnection.Session.JoinOrStart(
            "foreign-turn", () => Task.FromResult(new ConciergeTurnResult("foreign-turn", "Foreign reply", 1)),
            "Foreign guest text");

        Assert.Null(harness.Get<string?>("voiceSessionId"));
        Assert.Equal("disconnected", harness.Get<string>("voiceState"));
        Assert.Contains(logs.Entries, entry =>
            entry.Message.StartsWith("Rejected voice-session subscription", StringComparison.Ordinal)
            && entry.Message.Contains("not owned by active chat session", StringComparison.Ordinal)
            && entry.Values.ContainsKey("VoiceSessionId")
            && Equals(entry.Values.GetValueOrDefault("ChatSessionId"), ownConnection.Session.Stay.SessionId));
        var content = harness.RenderedContent().Text;
        Assert.DoesNotContain("Foreign guest text", content);
        Assert.DoesNotContain("Foreign reply", content);
    }

    /// <summary>Keeps the browser's more specific disconnect message.</summary>
    [Fact]
    public async Task ServerRelayClosureDoesNotOverwriteABrowserDisconnect()
    {
        await using var harness = new Harness();
        var connection = harness.StartListening();
        await harness.VoiceSession(connection.Session.Id.ToString());
        await harness.VoiceState("disconnected", "Voice service is unavailable.", 1011);

        harness.Voice.Close(connection);
        await harness.Call("ApplyVoiceRelayClosedAsync", connection.Session.Id.ToString(), 1);

        Assert.Equal("disconnected", harness.Get<string>("voiceState"));
        Assert.Equal("Voice service is unavailable.", harness.Get<string>("voiceMessage"));
    }

    private static ConciergeSessionResponse SessionResponse(string credential, Guid stay)
        => new(credential, DateTimeOffset.UtcNow.AddHours(1), stay, Guid.NewGuid());

    /// <summary>A turn another client (a room device) adds to the stay shows up without a page action.</summary>
    [Fact]
    public async Task AnotherClientsTurnRefreshesHistoryInTheBackgroundAndCoalesces()
    {
        await using var harness = new Harness();
        var stay = Guid.NewGuid();
        var own = Guid.NewGuid();
        harness.SetSession("credential", stay, own);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        harness.Service.History = async _ =>
        {
            var read = Interlocked.Increment(ref reads);
            if (read == 1) return new ConciergeHistoryResult([]);
            if (read == 2) await gate.Task;
            return History("device-turn");
        };
        await harness.Call("LoadHistoryAndReconcileAsync", false);
        Assert.Equal(1, Volatile.Read(ref reads));
        // A visible error and the idle state must survive a refresh the guest did not ask for.
        harness.Set("model", harness.Get<ChatSessionModel>("model").SetTurnError("Your last message failed."));

        var device = Guid.NewGuid();
        harness.Transcripts.Notify(stay, device, StayTranscriptChangeKind.TurnsChanged);
        await harness.WaitUntil(() => Volatile.Read(ref reads) == 2);
        // Bursts while a read is in flight collapse into one more read.
        harness.Transcripts.Notify(stay, device, StayTranscriptChangeKind.TurnsChanged);
        harness.Transcripts.Notify(stay, device, StayTranscriptChangeKind.TurnsChanged);
        await Task.Delay(50);
        gate.SetResult();
        await harness.WaitUntil(() => Volatile.Read(ref reads) == 3
            && harness.Get<List<ConciergeTranscriptTurn>>("turns").Count == 1);
        await Task.Delay(50);

        Assert.Equal(3, Volatile.Read(ref reads));
        Assert.Equal("device-turn", Assert.Single(harness.Get<List<ConciergeTranscriptTurn>>("turns")).TurnId);
        Assert.Equal("Your last message failed.", harness.Get<string?>("turnError"));
        Assert.False(harness.Get<bool>("isWorking"));
    }

    [Fact]
    public async Task OwnSessionOtherStayAndStartedTurnNotificationsDoNotRefresh()
    {
        await using var harness = new Harness();
        var stay = Guid.NewGuid();
        var own = Guid.NewGuid();
        harness.SetSession("credential", stay, own);
        var reads = 0;
        harness.Service.History = _ =>
        {
            Interlocked.Increment(ref reads);
            return Task.FromResult(History("turn"));
        };
        await harness.Call("LoadHistoryAndReconcileAsync", false);

        harness.Transcripts.Notify(stay, own, StayTranscriptChangeKind.TurnsChanged);
        harness.Transcripts.Notify(Guid.NewGuid(), Guid.NewGuid(), StayTranscriptChangeKind.TurnsChanged);
        // A started turn has nothing in history yet; only its TurnsChanged is worth a read.
        harness.Transcripts.Notify(stay, Guid.NewGuid(), StayTranscriptChangeKind.TurnStarted, "Where is breakfast?");
        await Task.Delay(100);

        Assert.Equal(1, Volatile.Read(ref reads));
    }

    [Theory]
    [InlineData("EndChatAsync")]
    [InlineData("StartOverAsync")]
    public async Task LeavingTheConversationStopsListeningForOtherClients(string action)
    {
        await using var harness = new Harness();
        var stay = Guid.NewGuid();
        harness.SetSession("credential", stay, Guid.NewGuid());
        var reads = 0;
        harness.Service.History = _ =>
        {
            Interlocked.Increment(ref reads);
            return Task.FromResult(History("turn"));
        };
        await harness.Call("LoadHistoryAndReconcileAsync", false);

        await harness.Call(action);
        harness.Transcripts.Notify(stay, Guid.NewGuid(), StayTranscriptChangeKind.TurnsChanged);
        await Task.Delay(100);

        Assert.Equal(1, Volatile.Read(ref reads));
    }

    [Fact]
    public async Task AnotherClientsResetEmptiesTheTranscriptAndRestartsTheRideFeed()
    {
        await using var harness = new Harness();
        var stay = Guid.NewGuid();
        harness.SetSession("credential", stay, Guid.NewGuid());
        var cleared = false;
        harness.Service.History = _ => Task.FromResult(Volatile.Read(ref cleared)
            ? new ConciergeHistoryResult([])
            : History("before-reset"));
        var snapshots = 0;
        harness.Service.RideSnapshot = _ =>
        {
            Interlocked.Increment(ref snapshots);
            return Task.FromResult(new ConciergeRideActivitySnapshot(new ConciergeRideReadState(0, 0, 0), []));
        };
        await harness.Call("LoadHistoryAndReconcileAsync", false);
        await harness.WaitUntil(() => Volatile.Read(ref snapshots) == 1);
        Assert.Single(harness.Get<List<ConciergeTranscriptTurn>>("turns"));

        Volatile.Write(ref cleared, true);
        harness.Transcripts.Notify(stay, Guid.NewGuid(), StayTranscriptChangeKind.StayReset);

        await harness.WaitUntil(() => harness.Get<List<ConciergeTranscriptTurn>>("turns").Count == 0
            && Volatile.Read(ref snapshots) == 2);
        Assert.Empty(harness.Get<List<ConciergeTranscriptTurn>>("turns"));
    }

    private static ConciergeHistoryResult History(string id) =>
        new([new ConciergeTranscriptTurn(id, 1, "web", "Guest", "Reply", DateTimeOffset.UtcNow)]);

    private static RideActivityEntry RideEntry(Guid rideId, int sequence, DateTimeOffset changedAt) =>
        new(rideId, sequence, "Booked", changedAt, "Hotel", "Museum", changedAt.AddHours(1));

    private static async IAsyncEnumerable<ConciergeRideActivityItem> RideItems(
        IEnumerable<ConciergeRideActivityItem> items)
    {
        foreach (var item in items)
        {
            yield return item;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<ConciergeRideActivityItem> WaitingRideItems(
        [EnumeratorCancellation] CancellationToken cancellationToken,
        TaskCompletionSource started,
        TaskCompletionSource cleanedUp)
    {
        try
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        finally
        {
            cleanedUp.TrySetResult();
        }

        yield break;
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Concierge.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate Concierge.slnx from the test output directory.");
    }

#pragma warning disable BL0006 // The fixture intentionally uses Blazor's renderer instead of a new test dependency.
    private sealed class Harness : IAsyncDisposable
    {
        private readonly Home home = new();
        private readonly TestRenderer renderer = new();
        private readonly int componentId;
        public FakeService Service { get; } = new();
        public FakeJavaScript JavaScript { get; } = new();
        public VoiceSessionRegistry Voice { get; } = new(TimeProvider.System);
        public StayTranscriptNotifier Transcripts { get; } = new(NullLogger<StayTranscriptNotifier>.Instance);

        /// <summary>Attaches Home without triggering implicit browser callbacks.</summary>
        public Harness()
        {
            Set("Concierge", Service);
            Set("JavaScript", JavaScript);
            Set("Logger", NullLogger<Home>.Instance);
            Set("VoiceSessions", Voice);
            Set("TranscriptNotifier", Transcripts);
            Set("ready", true);
            componentId = renderer.Attach(home);
        }

        /// <summary>Reads encoded text and raw markup from Home's current render tree, including child components.</summary>
        public (List<string> Text, List<string> Markup) RenderedContent()
        {
            var frames = renderer.Frames(componentId);
            return (
                frames.Where(frame => frame.FrameType == RenderTreeFrameType.Text).Select(frame => frame.TextContent).ToList(),
                frames.Where(frame => frame.FrameType == RenderTreeFrameType.Markup).Select(frame => frame.MarkupContent).ToList());
        }

        /// <summary>Returns elements in document order with their rendered attributes and text.</summary>
        public List<RenderedElement> RenderedElements()
        {
            var frames = renderer.Frames(componentId);
            var parentIndices = renderer.ElementParentIndices(componentId);
            var elements = new List<RenderedElement>();
            for (var index = 0; index < frames.Count; index++)
            {
                var frame = frames[index];
                if (frame.FrameType != RenderTreeFrameType.Element)
                {
                    continue;
                }

                var endIndex = index + frame.ElementSubtreeLength;
                var attributes = new Dictionary<string, object>();
                for (var attributeIndex = index + 1;
                     attributeIndex < frames.Count
                     && frames[attributeIndex].FrameType == RenderTreeFrameType.Attribute;
                     attributeIndex++)
                {
                    var attribute = frames[attributeIndex];
                    attributes[attribute.AttributeName] = attribute.AttributeValue;
                }
                elements.Add(new RenderedElement(frame.ElementName, attributes, index, endIndex, parentIndices[index]));
            }

            return elements;
        }

        /// <summary>Seeds a verified session for the test.</summary>
        public void SetSession(string credential, Guid stay, Guid sessionId = default)
        {
            var session = new ChatSessionState
            {
                Credential = credential,
                StayId = stay,
                SessionId = sessionId,
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1)
            };
            Set("session", session);
        }

        public sealed record RenderedElement(
            string Tag,
            Dictionary<string, object> Attributes,
            int Index,
            int EndIndex,
            int? ParentIndex);

        /// <summary>Sets private component state without adding production test surfaces.</summary>
        public void Set(string name, object value)
        {
            var property = typeof(Home).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (property?.SetMethod is not null)
            {
                property.SetValue(home, value);
                return;
            }

            if (property is not null)
            {
                var transition = name switch
                {
                    "session" => ("Restore", new[] { value, (object)DateTimeOffset.UtcNow }),
                    "pending" => ("StartTurn", new[] { value, (object)false }),
                    "draft" => ("SetDraft", new[] { value }),
                    "turns" => ("ReconcileHistory", new[] { value }),
                    _ => throw new InvalidOperationException($"No state transition is available for '{name}'.")
                };
                var modelField = typeof(Home).GetField("model", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var model = modelField.GetValue(home)!;
                var modelTransition = model.GetType().GetMethod(transition.Item1)!;
                modelField.SetValue(home, modelTransition.Invoke(model, transition.Item2));
                return;
            }

            typeof(Home).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(home, value);
        }

        /// <summary>Reads private state for focused assertions.</summary>
        public T Get<T>(string name)
        {
            var property = typeof(Home).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic);
            var value = property is not null
                ? property.GetValue(home)
                : typeof(Home).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(home);
            return (T)value!;
        }

        /// <summary>Runs a component event on its renderer dispatcher with a bounded timeout.</summary>
        public Task Call(string name, params object[] args) =>
            renderer.Dispatcher.InvokeAsync(async () =>
            {
                var result = typeof(Home).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(home, args);
                if (result is Task task) await task;
            }).WaitAsync(TimeSpan.FromSeconds(10));

        public Task VoiceEndRequested(string? reason = null) =>
            renderer.Dispatcher.InvokeAsync(() => home.OnVoiceEndRequested(reason));

        public Task VoiceIdleWarning(bool active) =>
            renderer.Dispatcher.InvokeAsync(() => home.OnVoiceIdleWarning(active));

        public Task DispatchKeyDown(Func<KeyboardEventArgs, Task> callback, KeyboardEventArgs args) =>
            renderer.Dispatcher.InvokeAsync(() => callback(args));

        /// <summary>Seeds a verified chat in voice mode with an open relay connection.</summary>
        public VoiceConnection StartListening()
        {
            var chatSessionId = Guid.NewGuid();
            var stayId = Guid.NewGuid();
            SetSession("credential", stayId, chatSessionId);
            Set("voiceMode", true);
            Set("voiceState", "listening");
            return Voice.Open(new ConciergeSessionContext(chatSessionId, stayId, DateTimeOffset.UtcNow.AddDays(1)), null);
        }

        public Task VoiceState(string state, string? message, int closeCode) =>
            renderer.Dispatcher.InvokeAsync(() => home.OnVoiceState(state, message, closeCode));

        public Task VoiceSession(string id) => renderer.Dispatcher.InvokeAsync(() => home.OnVoiceSession(id));

        /// <summary>Waits for work queued onto the component dispatcher from another thread.</summary>
        public async Task WaitUntil(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!await renderer.Dispatcher.InvokeAsync(condition))
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition was not met.");
                await Task.Delay(10);
            }
        }

        /// <summary>Disposes the attached component and renderer.</summary>
        public ValueTask DisposeAsync() => renderer.DisposeAsync();
    }

    // Suppress automatic after-render callbacks so each interop lifecycle step is deterministic.
    private sealed class TestRenderer() : Renderer(new EmptyServices(), NullLoggerFactory.Instance)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        /// <summary>Attaches the component without an initial render.</summary>
        public int Attach(IComponent component) => AssignRootComponentId(component);
        /// <summary>Flattens a component's current frames and its descendants' frames.</summary>
        public List<RenderTreeFrame> Frames(int componentId)
        {
            var current = GetCurrentRenderTreeFrames(componentId);
            var frames = new List<RenderTreeFrame>();
            for (var index = 0; index < current.Count; index++)
            {
                var frame = current.Array[index];
                frames.Add(frame);
                if (frame.FrameType == RenderTreeFrameType.Component) frames.AddRange(Frames(frame.ComponentId));
            }
            return frames;
        }
        public Dictionary<int, int?> ElementParentIndices(int componentId)
        {
            var parentIndices = new Dictionary<int, int?>();
            var flattenedIndex = 0;
            Append(componentId, null);
            return parentIndices;

            void Append(int id, int? parentIndex)
            {
                var current = GetCurrentRenderTreeFrames(id);
                AppendRange(current.Array, 0, current.Count, parentIndex);
            }

            void AppendRange(RenderTreeFrame[] frames, int start, int end, int? parentIndex)
            {
                for (var index = start; index < end;)
                {
                    var frame = frames[index];
                    var renderIndex = flattenedIndex++;
                    if (frame.FrameType == RenderTreeFrameType.Element)
                    {
                        parentIndices.Add(renderIndex, parentIndex);
                        var subtreeEnd = index + frame.ElementSubtreeLength;
                        AppendRange(frames, index + 1, subtreeEnd, renderIndex);
                        index = subtreeEnd;
                        continue;
                    }

                    if (frame.FrameType == RenderTreeFrameType.Component)
                    {
                        Append(frame.ComponentId, parentIndex);
                    }

                    index++;
                }
            }
        }
        protected override void HandleException(Exception exception) => throw exception;
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.FromCanceled(new CancellationToken(true));
    }
#pragma warning restore BL0006

    private sealed class EmptyServices : IServiceProvider
    {
        /// <summary>Provides no services; test dependencies are injected explicitly.</summary>
        public object? GetService(Type serviceType) => null;
    }

    private sealed class FakeJavaScript : IJSRuntime
    {
        public List<string> Calls { get; } = [];
        /// <summary>Identifiers that throw as if the Blazor Server circuit dropped mid-call.</summary>
        public HashSet<string> Disconnected { get; } = new(StringComparer.Ordinal);
        /// <summary>Identifiers that throw the given exception, e.g. a JSException from a script defect.</summary>
        public Dictionary<string, Exception> Failures { get; } = new(StringComparer.Ordinal);
        // BL0016 treats every member call on an IJSRuntime implementation as browser interop, including
        // this fake's own helper. Nothing here crosses a circuit, so there is nothing to guard.
#pragma warning disable BL0016
        /// <summary>Records browser interop with default cancellation.</summary>
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            Respond<TValue>(identifier);
        /// <summary>Records browser interop and returns a deterministic scroll position.</summary>
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            Respond<TValue>(identifier);
#pragma warning restore BL0016

        private ValueTask<TValue> Respond<TValue>(string identifier)
        {
            Calls.Add(identifier);
            if (Disconnected.Contains(identifier))
            {
                return ValueTask.FromException<TValue>(new JSDisconnectedException("Circuit disconnected."));
            }

            if (Failures.TryGetValue(identifier, out var failure))
            {
                return ValueTask.FromException<TValue>(failure);
            }

            return ValueTask.FromResult(identifier == "conciergeChat.isNearBottom" ? (TValue)(object)true : default!);
        }
    }

    private sealed class FakeService : IConciergeService
    {
        public ConciergeSessionResponse Verification { get; set; } = SessionResponse("new", Guid.NewGuid());
        public Func<ConciergeVerificationRequest, Task<ConciergeSessionResponse>>? Verify { get; set; }
        public Func<string?, Task> End { get; set; } = _ => Task.CompletedTask;
        public Func<string?, Task<ConciergeHistoryResult>> History { get; set; } = _ => Task.FromResult(new ConciergeHistoryResult([]));
        public Func<string?, Task<ConciergeRideActivitySnapshot>> RideSnapshot { get; set; } =
            _ => Task.FromResult(new ConciergeRideActivitySnapshot(
                new ConciergeRideReadState(0, 0, 0),
                []));
        public long MarkedRideSnapshotOffset { get; private set; } = -1;
        public long RideReadOffset { get; private set; }
        public Func<string?, long, CancellationToken, IAsyncEnumerable<ConciergeRideActivityItem>> RideStream { get; set; } =
            (_, _, _) => RideItems([]);
        public long RideStreamFromOffset { get; private set; } = -1;
        public Func<ConciergeTurnRequest, Task<ConciergeTurnResult>> Submission { get; set; } =
            request => Task.FromResult(new ConciergeTurnResult(request.TurnId, "Reply", 1));
        public List<ConciergeTurnRequest> Submissions { get; } = [];
        /// <summary>Returns the arranged verification response.</summary>
        public Task<ConciergeSessionResponse> VerifyAsync(ConciergeVerificationRequest request, CancellationToken cancellationToken = default) =>
            Verify?.Invoke(request) ?? Task.FromResult(Verification);
        /// <summary>Returns the arranged history task for this credential.</summary>
        public Task<ConciergeHistoryResult> ReadHistoryAsync(string? bearerCredential, CancellationToken cancellationToken = default) =>
            History(bearerCredential);
        /// <summary>Ride activity is outside these component tests.</summary>
        public Task<ConciergeRideActivitySnapshot> ReadRideActivitySnapshotAsync(
            string? bearerCredential, CancellationToken cancellationToken = default) =>
            RideSnapshot(bearerCredential);
        /// <summary>Returns the arranged ride stream and records the exclusive subscription offset.</summary>
        public IAsyncEnumerable<ConciergeRideActivityItem> StreamRideActivityAsync(
            string? bearerCredential, long fromOffset, CancellationToken cancellationToken = default)
        {
            RideStreamFromOffset = fromOffset;
            return RideStream(bearerCredential, fromOffset, cancellationToken);
        }
        /// <summary>Advances the arranged read cursor through the requested snapshot.</summary>
        public Task<ConciergeRideReadState> MarkRideActivitySnapshotReadAsync(
            string? bearerCredential, long snapshotOffset, CancellationToken cancellationToken = default)
        {
            MarkedRideSnapshotOffset = snapshotOffset;
            RideReadOffset = Math.Max(RideReadOffset, snapshotOffset);
            return Task.FromResult(new ConciergeRideReadState(RideReadOffset, 0, snapshotOffset));
        }
        /// <summary>Records the retry identity and returns the arranged turn task.</summary>
        public Task<ConciergeTurnResult> SubmitTurnAsync(string? bearerCredential, ConciergeTurnRequest request, CancellationToken cancellationToken = default)
        {
            Submissions.Add(request);
            return Submission(request);
        }
        /// <summary>Completes the interaction without external services.</summary>
        public Task EndAsync(string? bearerCredential, CancellationToken cancellationToken = default) => End(bearerCredential);
        public Func<string?, Task> Reset { get; set; } = _ => Task.CompletedTask;
        public int ResetCalls { get; private set; }
        /// <summary>Counts reset requests and returns the arranged task.</summary>
        public Task ResetAsync(string? bearerCredential, CancellationToken cancellationToken = default)
        {
            ResetCalls++;
            return Reset(bearerCredential);
        }
        /// <summary>Voice is outside these component tests.</summary>
        public Task<ConciergeVoiceAuthorization> AuthorizeVoiceAsync(string? bearerCredential, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        /// <summary>Voice is outside these component tests.</summary>
        public Task<ConciergeTurnResult> SubmitVoiceTurnAsync(
            ConciergeSessionContext session,
            string turnId,
            string userMessage,
            CancellationToken cancellationToken = default,
            bool enableEndChatFunction = false) =>
            throw new NotSupportedException();
        /// <summary>Voice is outside these component tests.</summary>
        public Task MarkVoiceTurnInterruptedAsync(
            ConciergeSessionContext session,
            string turnId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
