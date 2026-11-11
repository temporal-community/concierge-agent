#pragma warning disable BL0006 // Renderer internals are the point of this test harness.
using Concierge.Contracts;
using Concierge.Web.Agentic;
using Concierge.Web.Components.Agentic;
using Concierge.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.AI;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

namespace Concierge.Core.Tests;

/// <summary>ADR 0015: the stay history must actually render inside ChatPage.</summary>
public sealed class AgenticPageRenderTests
{
    [Fact]
    public async Task RestoredStayHistoryRendersInsideChatPage()
    {
        var now = DateTimeOffset.UtcNow;
        var thread = AgenticConcierge.ThreadFrom(Guid.NewGuid(),
        [
            new ConciergeTranscriptTurn("dev:1", 0, "voice", "When does the pool open?", "Eight in the morning.", now),
        ]);
        using var agent = new UIAgent(new NoChatClient(), options => options.Thread = thread);
        var renderer = new PageRenderer();
        var host = new ChatHost { Agent = agent };
        var id = renderer.Attach(host);

        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderRoot(id));
        await Task.Delay(300);

        var text = string.Join(" | ", renderer.Text(id));
        Assert.NotNull(Probe.Seen);
        Assert.Single(Probe.Seen.Turns);
        Assert.Contains("When does the pool open?", text);
        Assert.Contains("Eight in the morning.", text);
    }

    /// <summary>
    /// The page composes ChatPage's parts by hand, so it must repeat ChatPage's root classes; a package
    /// update that changes them would otherwise leave the chat unstyled, as it was before sc-ai-root.
    /// </summary>
    [Fact]
    public async Task HandComposedChatUsesChatPagesRootClasses()
    {
        using var agent = new UIAgent(new NoChatClient(), options => options.Thread = AgenticConcierge.ThreadFrom(Guid.NewGuid(), []));
        var renderer = new PageRenderer();
        var id = renderer.Attach(new RealChatPageHost { Agent = agent });
        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderRoot(id));

        var root = renderer.Elements(id).First(line => line.TrimStart().StartsWith("<div", StringComparison.Ordinal));
        Assert.Contains($"class={AgenticConcierge.ChatRootClass}>", root);
    }

    private static readonly DateTimeOffset MondayMorning = new(2026, 11, 2, 14, 5, 0, TimeSpan.Zero); // 9:05 AM in New York

    [Fact]
    public async Task MessagesUseHomesBubbleMarkupAndHotelTime()
    {
        var (renderer, id, _) = await RenderComposedAsync(
        [
            new ConciergeTranscriptTurn("t1", 0, "web", "When does the pool open?", "Eight in the morning.", MondayMorning),
        ]);

        var elements = renderer.Elements(id);
        var text = Normalized(renderer.Text(id));
        Assert.Contains(elements, line => line.Contains("class=message-row guest-row>"));
        Assert.Contains(elements, line => line.Contains("class=message-row concierge-row>"));
        Assert.Contains(elements, line => line.Contains("class=message-bubble guest-bubble>"));
        Assert.Contains(elements, line => line.Contains("class=message-bubble concierge-bubble>"));
        Assert.Contains("GUEST", text);
        Assert.Contains("CONCIERGE", text);
        Assert.Contains("9:05 AM", text);
    }

    [Fact]
    public async Task ConciergeMarkdownRendersThroughHomesSanitizedRenderer()
    {
        const string answer = "Amenities:\n\n- Pool\n- Gym\n\n[Spa menu](https://example.com/spa) and [bad](javascript:alert(1))";
        var (renderer, id, _) = await RenderComposedAsync(
        [
            new ConciergeTranscriptTurn("t1", 0, "web", "What amenities are there? - **really**", answer, MondayMorning),
        ]);

        var markup = string.Join("\n", renderer.Text(id));
        Assert.Contains(renderer.Elements(id), line => line.Contains("class=assistant-response>"));
        Assert.Contains("<li>Pool</li>", markup);
        Assert.Contains("href=\"https://example.com/spa\"", markup);
        Assert.DoesNotContain("javascript:", markup, StringComparison.OrdinalIgnoreCase);
        // Guest text stays plain: no Markdown, encoded by Razor.
        Assert.DoesNotContain("<strong>really</strong>", markup);
        Assert.Contains("What amenities are there? - **really**", markup);
    }

    [Fact]
    public async Task DateSeparatorsMarkEachHotelDayAndInterruptedRepliesAreFlagged()
    {
        var (renderer, id, _) = await RenderComposedAsync(
        [
            new ConciergeTranscriptTurn("t1", 0, "web", "Pool hours?", "Eight to ten.", MondayMorning),
            new ConciergeTranscriptTurn("t2", 1, "web", "Gym hours?", "Always open.", MondayMorning.AddMinutes(5)),
            new ConciergeTranscriptTurn("t3", 2, "voice", "Book a ride", "Booking your", MondayMorning.AddDays(1), Interrupted: true),
        ]);

        var elements = renderer.Elements(id);
        var text = Normalized(renderer.Text(id));
        Assert.Equal(2, elements.Count(line => line.Contains("class=date-separator>")));
        Assert.Contains("Nov 02", text);
        Assert.Contains("Nov 03", text);
        // Razor emits the static indicator span as one markup frame.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "class=\"interrupted-indicator\">Interrupted"));
    }

    [Fact]
    public async Task SuggestionChipSendsATurnOverTheAgentWhenTheStayIsEmpty()
    {
        var chat = new ReplyingChatClient("The pool opens at 8 AM.");
        var (renderer, id, _) = await RenderComposedAsync([], chat);

        var chips = renderer.ClickHandlers(id, "agentic-suggestion-chip");
        Assert.Equal(ConciergeGuestUi.Suggestions.Count, chips.Count);
        await renderer.Dispatcher.InvokeAsync(() => renderer.DispatchEventAsync(chips[0], null, new MouseEventArgs()));
        await WaitUntil(() => Normalized(renderer.Text(id)).Contains("The pool opens at 8 AM."));

        Assert.Equal(ConciergeGuestUi.Suggestions[0], chat.LastUserText);
        var text = Normalized(renderer.Text(id));
        Assert.Contains(ConciergeGuestUi.Suggestions[0], text);
        Assert.Empty(renderer.ClickHandlers(id, "agentic-suggestion-chip"));
    }

    /// <summary>
    /// A device turn re-reads history into the same thread and re-restores the same context; only
    /// MessageList is re-keyed, so the boundary, agent and composer survive the update.
    /// </summary>
    [Fact]
    public async Task ReRestoreWithReKeyedListShowsAnAppendedTurnInTheSameBoundary()
    {
        IReadOnlyList<ConciergeTranscriptTurn> first =
        [
            new ConciergeTranscriptTurn("t1", 0, "web", "Pool hours?", "Eight to ten.", MondayMorning),
        ];
        var (renderer, id, host) = await RenderComposedAsync(first);
        var boundaryBefore = renderer.ComponentIds<AgentBoundary>(id).Single();
        Assert.NotNull(host.Restored);

        host.Thread.ReplaceFrom(
        [
            .. first,
            new ConciergeTranscriptTurn("dev:2", 1, "voice", "Is the gym open?", "Around the clock.", MondayMorning.AddMinutes(3)),
        ]);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await host.Restored!.RestoreAsync(CancellationToken.None);
            host.ListKey++;
            host.Refresh();
        });

        var text = Normalized(renderer.Text(id));
        Assert.Contains("Eight to ten.", text);
        Assert.Contains("Around the clock.", text);
        Assert.Equal(boundaryBefore, renderer.ComponentIds<AgentBoundary>(id).Single());
    }

    [Fact]
    public async Task FooterShowsAndClearsTheRoomDevicesPendingQuestionOnPageReRender()
    {
        var (renderer, id, host) = await RenderComposedAsync(
        [
            new ConciergeTranscriptTurn("t1", 0, "web", "Pool hours?", "Eight to ten.", MondayMorning),
        ]);
        Assert.DoesNotContain("The room device is asking", Normalized(renderer.Text(id)));

        host.PendingDeviceQuestion = "Is the spa open late?";
        await renderer.Dispatcher.InvokeAsync(host.Refresh);
        Assert.Contains("The room device is asking: “Is the spa open late?”", string.Concat(renderer.Text(id)));

        host.PendingDeviceQuestion = null;
        await renderer.Dispatcher.InvokeAsync(host.Refresh);
        Assert.DoesNotContain("The room device is asking", Normalized(renderer.Text(id)));
    }

    private static readonly ConciergeRideCard BookedCard = new(
        ConciergeRideCard.Booked,
        [
            new ConciergeRideCardRide(
                Guid.NewGuid(), "DriverAssigned", "Hotel ZGY", "Miami International Airport",
                new DateTimeOffset(2026, 11, 6, 14, 0, 0, TimeSpan.Zero), "Alex Rivera", "ZGY Sedan", "ZGY-4821"),
        ]);

    [Fact]
    public async Task RestoredRideCardRendersUnderTheConciergeAnswerInHotelTime()
    {
        var (renderer, id, _) = await RenderComposedAsync(
        [
            new ConciergeTranscriptTurn("t1", 0, "web", "Book the airport ride", "Your ride is booked.", MondayMorning,
                RideCard: BookedCard),
        ]);

        var elements = renderer.Elements(id);
        var text = Normalized(renderer.Text(id));
        var bubble = elements.FindIndex(line => line.Contains("class=message-bubble concierge-bubble>"));
        var card = elements.FindIndex(line => line.Contains("class=ride-card>"));
        Assert.True(bubble >= 0 && card > bubble, string.Join("\n", elements));
        Assert.Contains("Ride booked", text);
        Assert.Contains("Miami International Airport", text);
        Assert.Contains("Driver assigned", text);
        Assert.Contains("Fri Nov 6 · 9:00 AM", text); // 14:00 UTC in New York
        Assert.Contains("Pickup: Hotel ZGY", text);
        Assert.Contains("Alex Rivera · ZGY Sedan · ZGY-4821", text);
    }

    [Fact]
    public async Task LiveRideCardInTheShapeAgUiDeliversRenders()
    {
        var scheduled = new ConciergeRideCard(ConciergeRideCard.Scheduled, [BookedCard.Rides[0], BookedCard.Rides[0] with { RideId = Guid.NewGuid() }]);
        var (renderer, id, host) = await RenderComposedAsync([], new CardChatClient("You have two rides.", scheduled));

        await renderer.Dispatcher.InvokeAsync(() => host.Restored!.SendMessageAsync("What rides do I have?", CancellationToken.None));
        await WaitUntil(() => Normalized(renderer.Text(id)).Contains("Your scheduled rides"));

        Assert.Equal(2, renderer.Elements(id).Count(line => line.Contains("class=ride-card__ride>")));
        Assert.Contains("You have two rides.", Normalized(renderer.Text(id)));
    }

    [Fact]
    public async Task UnreadableRideCardDrawsNothingAndKeepsTheAnswer()
    {
        var thread = new StayConversationThread(Guid.NewGuid());
        thread.AppendUserMessage(new ChatMessage(ChatRole.User, "Rides?") { MessageId = "t1:guest" });
        thread.AppendUpdate(new ChatResponseUpdate(ChatRole.Assistant, "You have one ride.") { MessageId = "t1:concierge", ResponseId = "t1" });
        thread.AppendUpdate(new ChatResponseUpdate(ChatRole.Assistant,
            [new FunctionCallContent("t1:ride-card", AgenticConcierge.RideCardTool) { InformationalOnly = true }]) { MessageId = "t1:concierge", ResponseId = "t1" });
        thread.AppendUpdate(new ChatResponseUpdate(ChatRole.Tool,
            [new FunctionResultContent("t1:ride-card", "{\"kind\":\"booked\",\"rides\":\"oops\"}")]) { MessageId = "t1:ride-card", ResponseId = "t1" });
        thread.CompleteTurn();
        using var agent = new UIAgent(new NoChatClient(), options => options.Thread = thread);
        var renderer = new PageRenderer();
        var host = new ComposedChatHost { Agent = agent, Thread = thread };
        var id = renderer.Attach(host);
        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderRoot(id));

        Assert.DoesNotContain(renderer.Elements(id), line => line.Contains("ride-card"));
        Assert.Contains("You have one ride.", Normalized(renderer.Text(id)));
    }

    /// <summary>Streamed text deltas with one message ID grow one concierge bubble (ADR 0015 streaming).</summary>
    /// <summary>
    /// A failed run shows the generic card. In Development the page passes the server's recorded
    /// exception, and the card expands to it plus the page's own; otherwise there is no expander.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AFailedRunShowsErrorDetailsOnlyWhenThePageSuppliesThem(bool development)
    {
        var (renderer, id, host) = await RenderComposedAsync([], new FailingChatClient("loopback refused"));
        if (development)
        {
            host.FailureDetails = () => "System.InvalidOperationException: server boom";
            await renderer.Dispatcher.InvokeAsync(host.Refresh);
        }

        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await host.Restored!.SendMessageAsync("Pool?", CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                // Whether the kit rethrows doesn't matter here; the card is what the guest sees.
            }
        });
        await WaitUntil(() => Normalized(renderer.Text(id)).Contains("We couldn’t send that message."));

        var text = string.Join("\n", renderer.Text(id));
        if (development)
        {
            Assert.Contains("Error details (development)", text);
            Assert.Contains("Server:\nSystem.InvalidOperationException: server boom", text);
            Assert.Contains("Page:\n", text);
            Assert.Contains("loopback refused", text);
            Assert.Contains(renderer.Elements(id), line => line.Contains("class=turn-error__details"));
        }
        else
        {
            Assert.DoesNotContain("Error details", text);
            Assert.DoesNotContain("loopback refused", text);
        }
    }

    /// <summary>
    /// The AG-UI client reports a server failure as an ErrorContent item, which the kit ignores. Through
    /// RunErrorChatClient (the page's wiring) it is the send-error card instead of a silent no-reply.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AServerFailureShowsTheSendErrorCardOnlyThroughRunErrorChatClient(bool wrapped)
    {
        IChatClient chat = wrapped ? new RunErrorChatClient(new ErrorContentChatClient()) : new ErrorContentChatClient();
        var (renderer, id, host) = await RenderComposedAsync([], chat);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await host.Restored!.SendMessageAsync("Pool?", CancellationToken.None);
            }
            catch (AgenticRunFailedException)
            {
                // Whether the kit rethrows doesn't matter here; the card is what the guest sees.
            }
        });

        if (wrapped)
        {
            await WaitUntil(() => Normalized(renderer.Text(id)).Contains("We couldn’t send that message."));
            Assert.Equal(ConversationStatus.Error, host.Restored!.Status);
            Assert.Equal(
                "The concierge's AG-UI run failed (StreamingError): An error occurred while streaming the agent response.",
                Assert.IsType<AgenticRunFailedException>(host.Restored.Error).Message);
        }
        else
        {
            // The kit's behaviour that the wrapper exists for: no card, no reply.
            await Task.Delay(200);
            Assert.DoesNotContain("We couldn’t send that message.", Normalized(renderer.Text(id)));
            Assert.NotEqual(ConversationStatus.Error, host.Restored!.Status);
        }
    }

    private sealed class ErrorContentChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new ErrorContent("An error occurred while streaming the agent response.") { ErrorCode = "StreamingError" }]) { MessageId = "m", ResponseId = "r" };
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>Development: a failure reply's details call draws a collapsed expander under the reply.</summary>
    [Fact]
    public async Task AnErrorDetailsCallDrawsAnExpanderUnderTheReply()
    {
        var (renderer, id, host) = await RenderComposedAsync([], new DetailsChatClient());
        await renderer.Dispatcher.InvokeAsync(() => host.Restored!.SendMessageAsync("Pool?", CancellationToken.None));
        await WaitUntil(() => Normalized(renderer.Text(id)).Contains("Error details (development)"));

        var text = string.Join("\n", renderer.Text(id));
        Assert.Contains("The concierge is temporarily unavailable.", text);
        Assert.Contains("System.InvalidOperationException: Temporal is unreachable.", text);
        Assert.Contains(renderer.Elements(id), line => line.Contains("class=agentic-error-details-row"));
    }

    private sealed class DetailsChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var (call, result) = AgenticConcierge.ErrorDetailsContents("agui:1", "System.InvalidOperationException: Temporal is unreachable.");
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "The concierge is temporarily unavailable.") { MessageId = "agui:1", ResponseId = "run-1" };
            yield return new ChatResponseUpdate(ChatRole.Assistant, [call]) { MessageId = "agui:1", ResponseId = "run-1" };
            yield return new ChatResponseUpdate(ChatRole.Tool, [result]) { MessageId = call.CallId, ResponseId = "run-1" };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task StreamedDeltasGrowOneConciergeBubble()
    {
        var chat = new SteppedChatClient(["The pool ", "opens at ", "8 a.m."]);
        var (renderer, id, host) = await RenderComposedAsync([], chat);

        var send = renderer.Dispatcher.InvokeAsync(() => host.Restored!.SendMessageAsync("Pool?", CancellationToken.None));
        var expected = "";
        foreach (var part in chat.Parts)
        {
            expected += part;
            chat.Step.Release();
            await WaitUntil(() => Normalized(renderer.Text(id)).Contains(expected.TrimEnd()));
            Assert.Single(renderer.Elements(id), line => line.Contains("class=message-bubble concierge-bubble>"));
        }

        chat.Step.Release();
        await send;
        Assert.Single(renderer.Elements(id), line => line.Contains("class=message-bubble concierge-bubble>"));
        Assert.Contains("The pool opens at 8 a.m.", Normalized(renderer.Text(id)));
    }

    /// <summary>
    /// ADR 0015: a correction draws nothing and reports its call ID, which the page turns into one reload
    /// from history.
    /// </summary>
    [Fact]
    public async Task ACorrectionDrawsNothingAndReportsItsCallId()
    {
        var (renderer, id, host) = await RenderComposedAsync([], new CorrectingChatClient("Let me check", "agui:c", "Rain, 82°F."));

        await renderer.Dispatcher.InvokeAsync(() => host.Restored!.SendMessageAsync("Weather?", CancellationToken.None));
        await WaitUntil(() => host.Corrections.Count > 0);

        await Task.Delay(200);
        Assert.Equal(["agui:c:answer"], host.Corrections);
        Assert.Contains("Let me check", Normalized(renderer.Text(id)));
        Assert.DoesNotContain("Rain, 82°F.", Normalized(renderer.Text(id)));
        Assert.DoesNotContain(renderer.Elements(id), line => line.Contains(AgenticConcierge.AnswerCorrectionTool));
    }

    private sealed class CorrectingChatClient(string streamed, string turnId, string committed) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var (call, result) = AgenticConcierge.AnswerCorrectionContents(turnId, committed);
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, streamed) { MessageId = turnId, ResponseId = turnId };
            yield return new ChatResponseUpdate(ChatRole.Assistant, [call]) { MessageId = turnId, ResponseId = turnId };
            yield return new ChatResponseUpdate(ChatRole.Tool, [result]) { MessageId = call.CallId, ResponseId = turnId };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>The input's voice controls follow the AgenticUI sample: mic to start and end, speaker to mute replies.</summary>
    [Fact]
    public async Task VoiceInputControlsFollowTheVoiceState()
    {
        var registry = new Concierge.Web.Voice.VoiceSessionRegistry(TimeProvider.System);
        var stay = new ConciergeSessionContext(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(1));
        var host = new VoiceInputHost();
        var renderer = new PageRenderer();
        var id = renderer.Attach(host);
        host.Voice = new AgenticVoiceController(registry, new SilentVoice(), NullLogger.Instance,
            callback => renderer.Dispatcher.InvokeAsync(callback), () => _ = renderer.Dispatcher.InvokeAsync(host.Refresh),
            () => Task.CompletedTask, () => Task.CompletedTask, _ => Task.CompletedTask);
        host.CanStart = false;
        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderRoot(id));

        var mic = renderer.Attributes(id, "voice-input__mic")!;
        Assert.Equal("Start voice", mic["aria-label"]);
        Assert.True(mic.ContainsKey("disabled"), "A turn in flight disables Start voice.");
        Assert.Null(renderer.Attributes(id, "voice-input__meter--speaker"));
        Assert.Null(renderer.Attributes(id, "voice-status-row"));
        Assert.DoesNotContain("Listening", Normalized(renderer.Text(id)));

        await host.Voice.StartAsync("credential", stay.SessionId);
        var connection = registry.Open(stay, null);
        await host.Voice.OnVoiceSession(connection.Session.Id.ToString());
        await host.Voice.OnVoiceState("listening", null, 0);
        await renderer.Dispatcher.InvokeAsync(host.Refresh);
        Assert.Equal("listening", renderer.Attributes(id, "voice-input")!["data-state"]);
        Assert.Equal("listening", renderer.Attributes(id, "voice-status-row")!["data-state"]);
        Assert.Equal("End voice", renderer.Attributes(id, "voice-input__mic")!["aria-label"]);
        Assert.Equal(["End voice", "Mute spoken replies"],
            renderer.AllAttributes(id, "voice-input__button").Select(button => button["aria-label"] as string));
        Assert.Contains("Listening", Normalized(renderer.Text(id)));

        // What the guest said shows above the input until the turn finishes.
        var reply = new TaskCompletionSource<ConciergeTurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var turn = connection.Session.JoinOrStart("v:1", () => reply.Task, "Is the pool open?");
        await WaitUntil(() => Normalized(renderer.Text(id)).Contains("You said: “Is the pool open?”"));
        Assert.Equal("status", renderer.Attributes(id, "voice-input__bubble")!["role"]);
        reply.SetResult(new ConciergeTurnResult("v:1", "Yes.", 1));
        await turn;
        await WaitUntil(() => !Normalized(renderer.Text(id)).Contains("You said"));

        await host.Voice.OnVoiceState("paused", null, 0);
        await renderer.Dispatcher.InvokeAsync(host.Refresh);
        Assert.Equal("Resume voice", renderer.Attributes(id, "voice-input__mic")!["aria-label"]);
        Assert.Null(renderer.Attributes(id, "voice-input__meter--speaker"));

        await host.Voice.OnVoiceState("disconnected", "Voice is unavailable right now.", 4503);
        await renderer.Dispatcher.InvokeAsync(host.Refresh);
        Assert.Equal("Reconnect voice", renderer.Attributes(id, "voice-input__mic")!["aria-label"]);
        Assert.Equal("alert", renderer.Attributes(id, "voice-input__bubble--error")!["role"]);
        Assert.Contains("Voice is unavailable right now.", Normalized(renderer.Text(id)));

    }

    /// <summary>
    /// The kit facts the input layout relies on (0.1.0-preview.1.26466.103): LeadingActions renders as a
    /// child of .sc-ai-input next to the text and keeps Send, which agentic.css then orders after the text;
    /// TrailingActions replaces Send, so the voice controls can't go there.
    /// </summary>
    [Fact]
    public async Task LeadingActionsKeepSendAndTrailingActionsReplaceIt()
    {
        using var agent = new UIAgent(new NoChatClient(), options => options.Thread = new StayConversationThread(Guid.NewGuid()));
        var renderer = new PageRenderer();

        var leading = renderer.Attach(new InputHost { Agent = agent, Slot = nameof(MessageInput.LeadingActions) });
        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderRoot(leading));
        var elements = renderer.Elements(leading);
        var marker = elements.FindIndex(line => line.Contains("actions-marker"));
        Assert.True(marker > elements.FindIndex(line => line.Contains("class=sc-ai-input>")), string.Join("\n", elements));
        Assert.True(marker < elements.FindIndex(line => line.Contains("sc-ai-input__body")), string.Join("\n", elements));
        Assert.Contains(elements, line => line.Contains("sc-ai-input__send"));

        var trailing = renderer.Attach(new InputHost { Agent = agent, Slot = nameof(MessageInput.TrailingActions) });
        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderRoot(trailing));
        Assert.DoesNotContain(renderer.Elements(trailing), line => line.Contains("sc-ai-input__send"));
    }

    private sealed class InputHost : ComponentBase
    {
        public UIAgent Agent { get; set; } = default!;

        public string Slot { get; set; } = nameof(MessageInput.LeadingActions);

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<AgentBoundary>(0);
            builder.AddComponentParameter(1, nameof(AgentBoundary.Agent), Agent);
            builder.AddComponentParameter(2, nameof(AgentBoundary.ChildContent), (RenderFragment)(inner =>
            {
                inner.OpenComponent<MessageInput>(0);
                inner.AddComponentParameter(1, Slot, (RenderFragment)(actions =>
                {
                    actions.OpenElement(0, "span");
                    actions.AddAttribute(1, "class", "actions-marker");
                    actions.CloseElement();
                }));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }

    private sealed class VoiceInputHost : ComponentBase
    {
        public AgenticVoiceController? Voice { get; set; }

        public bool CanStart { get; set; }

        public void Refresh() => StateHasChanged();

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            // As on the page: the status row above the input, then the input's voice buttons.
            builder.OpenComponent<VoiceStatus>(0);
            builder.AddComponentParameter(1, nameof(VoiceStatus.Voice), Voice);
            builder.CloseComponent();
            builder.OpenComponent<VoiceInputControls>(2);
            builder.AddComponentParameter(3, nameof(VoiceInputControls.Voice), Voice);
            builder.AddComponentParameter(4, nameof(VoiceInputControls.CanStart), CanStart);
            builder.CloseComponent();
        }
    }

    private sealed class SilentVoice : IVoiceInterop
    {
        public Task StartAsync(string credential, string? voiceSessionId, DotNetObjectReference<AgenticVoiceController> callbacks) => Task.CompletedTask;
        public Task ResumeAsync() => Task.CompletedTask;
        public Task<bool?> SetMutedAsync(bool muted) => Task.FromResult<bool?>(muted);
        public Task<bool?> SetSpeakerMutedAsync(bool muted) => Task.FromResult<bool?>(muted);
        public Task StopAsync() => Task.CompletedTask;
    }

    /// <summary>Yields one text delta per <see cref="Step"/> release, as the AG-UI client delivers them.</summary>
    private sealed class SteppedChatClient(string[] parts) : IChatClient
    {
        public string[] Parts => parts;

        public SemaphoreSlim Step { get; } = new(0);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var part in parts)
            {
                await Step.WaitAsync(cancellationToken);
                yield return new ChatResponseUpdate(ChatRole.Assistant, part) { MessageId = "agui:s:concierge", ResponseId = "run-s" };
            }

            await Step.WaitAsync(cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class FailingChatClient(string message) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new InvalidOperationException(message);
#pragma warning disable CS0162 // An iterator needs a yield.
            yield break;
#pragma warning restore CS0162
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>Replies the way AGUIChatClient delivers a ride card (spike, ADR 0015).</summary>
    private sealed class CardChatClient(string reply, ConciergeRideCard card) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var (call, result) = AgenticConcierge.RideCardContents("agui:card", card);
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, reply) { MessageId = "agui:card", ResponseId = "run-card" };
            yield return new ChatResponseUpdate(ChatRole.Assistant, [call]) { MessageId = "agui:card", ResponseId = "run-card" };
            yield return new ChatResponseUpdate(ChatRole.Tool, [result]) { MessageId = call.CallId, ResponseId = "run-card" };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private static async Task<(PageRenderer Renderer, int Id, ComposedChatHost Host)> RenderComposedAsync(
        IReadOnlyList<ConciergeTranscriptTurn> turns, IChatClient? chat = null)
    {
        var thread = AgenticConcierge.ThreadFrom(Guid.NewGuid(), turns);
        var agent = new UIAgent(chat ?? new NoChatClient(), options => options.Thread = thread);
        var renderer = new PageRenderer();
        var host = new ComposedChatHost { Agent = agent, Thread = thread };
        var id = renderer.Attach(host);
        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderRoot(id));
        await WaitUntil(() => host.Restored is not null);
        return (renderer, id, host);
    }

    // Collapses Razor's whitespace text frames so assertions read like the rendered line.
    private static string Normalized(IEnumerable<string> text) =>
        System.Text.RegularExpressions.Regex.Replace(string.Join(" ", text), "\\s+", " ");

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    /// <summary>Agentic.razor's chat composition, minus the page shell and services.</summary>
    private sealed class ComposedChatHost : ComponentBase
    {
        public UIAgent Agent { get; set; } = default!;

        public StayConversationThread Thread { get; set; } = default!;

        public string? PendingDeviceQuestion { get; set; }

        public Func<string?>? FailureDetails { get; set; }

        public int ListKey { get; set; }

        public AgentContext? Restored { get; private set; }

        public List<string> Corrections { get; } = [];

        public void Refresh() => StateHasChanged();

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<AgentBoundary>(0);
            builder.AddComponentParameter(1, nameof(AgentBoundary.Agent), Agent);
            builder.AddComponentParameter(2, nameof(AgentBoundary.ChildContent), (RenderFragment)(inner =>
            {
                inner.OpenComponent<RestoreConversation>(0);
                inner.AddComponentParameter(1, nameof(RestoreConversation.Restored),
                    EventCallback.Factory.Create<AgentContext>(this, context => Restored = context));
                inner.CloseComponent();

                inner.OpenComponent<MessageList>(2);
                inner.SetKey(ListKey);
                inner.AddComponentParameter(3, nameof(MessageList.ChildContent), (RenderFragment)(list =>
                {
                    list.OpenComponent<BlockRenderer<RichContentBlock>>(0);
                    list.AddComponentParameter(1, nameof(BlockRenderer<RichContentBlock>.ChildContent), (RenderFragment<RichContentBlock>)(block => message =>
                    {
                        message.OpenComponent<StayMessage>(0);
                        message.AddComponentParameter(1, nameof(StayMessage.Block), block);
                        message.AddComponentParameter(2, nameof(StayMessage.Thread), Thread);
                        message.CloseComponent();
                    }));
                    list.CloseComponent();

                    list.OpenComponent<BlockRenderer<FunctionInvocationContentBlock>>(2);
                    list.AddComponentParameter(3, nameof(BlockRenderer<FunctionInvocationContentBlock>.When),
                        (Func<FunctionInvocationContentBlock, bool>)(tool => tool.ToolName == AgenticConcierge.RideCardTool));
                    list.AddComponentParameter(4, nameof(BlockRenderer<FunctionInvocationContentBlock>.ChildContent),
                        (RenderFragment<FunctionInvocationContentBlock>)(tool => card =>
                        {
                            card.OpenComponent<RideCard>(0);
                            card.AddComponentParameter(1, nameof(RideCard.Block), tool);
                            card.CloseComponent();
                        }));
                    list.CloseComponent();

                    list.OpenComponent<BlockRenderer<FunctionInvocationContentBlock>>(8);
                    list.AddComponentParameter(9, nameof(BlockRenderer<FunctionInvocationContentBlock>.When),
                        (Func<FunctionInvocationContentBlock, bool>)(tool => tool.ToolName == AgenticConcierge.ErrorDetailsTool));
                    list.AddComponentParameter(10, nameof(BlockRenderer<FunctionInvocationContentBlock>.ChildContent),
                        (RenderFragment<FunctionInvocationContentBlock>)(tool => details =>
                        {
                            details.OpenComponent<ErrorDetails>(0);
                            details.AddComponentParameter(1, nameof(ErrorDetails.Block), tool);
                            details.CloseComponent();
                        }));
                    list.CloseComponent();

                    list.OpenComponent<BlockRenderer<FunctionInvocationContentBlock>>(5);
                    list.AddComponentParameter(6, nameof(BlockRenderer<FunctionInvocationContentBlock>.When),
                        (Func<FunctionInvocationContentBlock, bool>)(tool => tool.ToolName == AgenticConcierge.AnswerCorrectionTool));
                    list.AddComponentParameter(7, nameof(BlockRenderer<FunctionInvocationContentBlock>.ChildContent),
                        (RenderFragment<FunctionInvocationContentBlock>)(tool => correction =>
                        {
                            correction.OpenComponent<AnswerCorrection>(0);
                            correction.AddComponentParameter(1, nameof(AnswerCorrection.Block), tool);
                            correction.AddComponentParameter(2, nameof(AnswerCorrection.Corrected),
                                EventCallback.Factory.Create<string>(this, callId => Corrections.Add(callId)));
                            correction.CloseComponent();
                        }));
                    list.CloseComponent();
                }));
                inner.AddComponentParameter(4, nameof(MessageList.EmptyContent), (RenderFragment)(empty =>
                {
                    empty.OpenComponent<SuggestionChips>(0);
                    empty.CloseComponent();
                }));
                inner.AddComponentParameter(5, nameof(MessageList.Footer), (RenderFragment<AgentContext>)(context => footer =>
                {
                    footer.OpenComponent<ChatFooter>(0);
                    footer.AddComponentParameter(1, nameof(ChatFooter.Context), context);
                    footer.AddComponentParameter(2, nameof(ChatFooter.DeviceQuestion), PendingDeviceQuestion);
                    footer.AddComponentParameter(3, nameof(ChatFooter.FailureDetails), FailureDetails);
                    footer.CloseComponent();
                }));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }

    private sealed class ReplyingChatClient(string reply) : IChatClient
    {
        public string? LastUserText { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            LastUserText = messages.Last(message => message.Role == ChatRole.User).Text;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply) { MessageId = "reply-1" }));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastUserText = messages.Last(message => message.Role == ChatRole.User).Text;
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, reply) { MessageId = "reply-1", ResponseId = "run-1" };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class RealChatPageHost : ComponentBase
    {
        public UIAgent Agent { get; set; } = default!;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<ChatPage>(0);
            builder.AddComponentParameter(1, nameof(ChatPage.Agent), Agent);
            builder.CloseComponent();
        }
    }

    private sealed class ChatHost : ComponentBase
    {
        public UIAgent Agent { get; set; } = default!;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            // The page's composition: the restore is an earlier sibling of MessageList in the boundary.
            builder.OpenComponent<AgentBoundary>(0);
            builder.AddComponentParameter(1, nameof(AgentBoundary.Agent), Agent);
            builder.AddComponentParameter(2, nameof(AgentBoundary.ChildContent), (RenderFragment)(inner =>
            {
                inner.OpenComponent<RestoreConversation>(0);
                inner.CloseComponent();
                inner.OpenComponent<Probe>(1);
                inner.CloseComponent();
                inner.OpenComponent<MessageList>(2);
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }

    private sealed class Probe : ComponentBase
    {
        public static AgentContext? Seen;

        [CascadingParameter]
        public AgentContext? Context { get; set; }

        protected override void OnParametersSet() => Seen = Context;
    }

    private sealed class PageRenderer() : Renderer(Services(), NullLoggerFactory.Instance)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

        public int Attach(IComponent component) => AssignRootComponentId(component);

        public Task RenderRoot(int id) => RenderRootComponentAsync(id);

        public List<string> Text(int id)
        {
            var output = new List<string>();
            Walk(id);
            return output;

            void Walk(int componentId)
            {
                var frames = GetCurrentRenderTreeFrames(componentId);
                for (var index = 0; index < frames.Count; index++)
                {
                    var frame = frames.Array[index];
                    if (frame.FrameType == RenderTreeFrameType.Text) output.Add(frame.TextContent);
                    if (frame.FrameType == RenderTreeFrameType.Markup) output.Add(frame.MarkupContent);
                    if (frame.FrameType == RenderTreeFrameType.Component) Walk(frame.ComponentId);
                }
            }
        }

        public List<string> Elements(int id)
        {
            var output = new List<string>();
            Walk(id, 0);
            return output;

            void Walk(int componentId, int depth)
            {
                var frames = GetCurrentRenderTreeFrames(componentId);
                for (var index = 0; index < frames.Count; index++)
                {
                    var frame = frames.Array[index];
                    if (frame.FrameType == RenderTreeFrameType.Element)
                    {
                        var attributes = new List<string>();
                        for (var a = index + 1; a < frames.Count && frames.Array[a].FrameType == RenderTreeFrameType.Attribute; a++)
                        {
                            var attribute = frames.Array[a];
                            if (attribute.AttributeName is "class" or "role" or "data-theme" or "style") attributes.Add($"{attribute.AttributeName}={attribute.AttributeValue}");
                        }
                        output.Add(new string(' ', depth * 2) + "<" + frame.ElementName + " " + string.Join(" ", attributes) + ">");
                    }
                    if (frame.FrameType == RenderTreeFrameType.Component)
                    {
                        output.Add(new string(' ', depth * 2) + "[" + frame.ComponentType.Name + "]");
                        Walk(frame.ComponentId, depth + 1);
                    }
                }
            }
        }

        /// <summary>All attributes of the first element with class <paramref name="className"/>, or null.</summary>
        public Dictionary<string, object?>? Attributes(int id, string className) => AllAttributes(id, className).FirstOrDefault();

        /// <summary>The attributes of every element with class <paramref name="className"/>, in render order.</summary>
        public List<Dictionary<string, object?>> AllAttributes(int id, string className)
        {
            var output = new List<Dictionary<string, object?>>();
            Walk(id);
            return output;

            void Walk(int componentId)
            {
                var frames = GetCurrentRenderTreeFrames(componentId);
                for (var index = 0; index < frames.Count; index++)
                {
                    var frame = frames.Array[index];
                    if (frame.FrameType == RenderTreeFrameType.Component) Walk(frame.ComponentId);
                    if (frame.FrameType != RenderTreeFrameType.Element) continue;

                    var attributes = new Dictionary<string, object?>(StringComparer.Ordinal);
                    for (var a = index + 1; a < frames.Count && frames.Array[a].FrameType == RenderTreeFrameType.Attribute; a++)
                    {
                        attributes[frames.Array[a].AttributeName] = frames.Array[a].AttributeValue;
                    }

                    if (attributes.GetValueOrDefault("class") is string value && value.Split(' ').Contains(className)) output.Add(attributes);
                }
            }
        }

        /// <summary>onclick handler ids of elements whose class contains <paramref name="className"/>, in render order.</summary>
        public List<ulong> ClickHandlers(int id, string className)
        {
            var output = new List<ulong>();
            Walk(id);
            return output;

            void Walk(int componentId)
            {
                var frames = GetCurrentRenderTreeFrames(componentId);
                for (var index = 0; index < frames.Count; index++)
                {
                    var frame = frames.Array[index];
                    if (frame.FrameType == RenderTreeFrameType.Component) Walk(frame.ComponentId);
                    if (frame.FrameType != RenderTreeFrameType.Element) continue;

                    var matches = false;
                    ulong handler = 0;
                    for (var a = index + 1; a < frames.Count && frames.Array[a].FrameType == RenderTreeFrameType.Attribute; a++)
                    {
                        var attribute = frames.Array[a];
                        if (attribute.AttributeName == "class" && attribute.AttributeValue is string value && value.Contains(className, StringComparison.Ordinal)) matches = true;
                        if (attribute.AttributeName == "onclick") handler = attribute.AttributeEventHandlerId;
                    }
                    if (matches && handler != 0) output.Add(handler);
                }
            }
        }

        public List<int> ComponentIds<TComponent>(int id)
        {
            var output = new List<int>();
            Walk(id);
            return output;

            void Walk(int componentId)
            {
                var frames = GetCurrentRenderTreeFrames(componentId);
                for (var index = 0; index < frames.Count; index++)
                {
                    var frame = frames.Array[index];
                    if (frame.FrameType != RenderTreeFrameType.Component) continue;
                    if (frame.ComponentType == typeof(TComponent)) output.Add(frame.ComponentId);
                    Walk(frame.ComponentId);
                }
            }
        }

        protected override void HandleException(Exception exception) => throw exception;

        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;

        private static IServiceProvider Services() => new ServiceCollection()
            .AddSingleton<IJSRuntime, NoJavaScript>()
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .BuildServiceProvider();
    }

    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult(default(TValue)!);
    }

    private sealed class NoChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
