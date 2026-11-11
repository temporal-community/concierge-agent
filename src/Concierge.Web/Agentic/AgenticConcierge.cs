using System.Net.Http.Headers;
using System.Text.Json;
using AGUI.Client;
using Concierge.Contracts;
using Concierge.Web.Services;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Components.AI;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.AI;

namespace Concierge.Web.Agentic;

/// <summary>
/// Exposes the stay workflow as an AG-UI agent, which the guest chat page uses through
/// <c>Microsoft.AspNetCore.Components.AI</c> (ADR 0014 phase 2, ADR 0015). The packages are prerelease.
/// </summary>
public static class AgenticConcierge
{
    /// <summary>The AG-UI endpoint (HTTP POST, Server-Sent Events).</summary>
    public const string EndpointPath = "/agui/concierge";

    /// <summary>
    /// The agent's stable logical name. With no <c>AgentSessionStore</c> registered, sessions are not
    /// persisted; the stay workflow is the conversation's only store.
    /// </summary>
    public const string AgentName = "hotel-zgy-concierge";

    /// <summary>
    /// ChatPage's root classes, which the page's hand-composed chat must repeat: <c>sc-ai-root</c> carries
    /// every theme variable and the box-sizing reset, so without it the chat renders unstyled. Guarded by
    /// <c>AgenticPageRenderTests</c> against package changes.
    /// </summary>
    public const string ChatRootClass = "sc-ai-root sc-ai-chat-page";

    /// <summary>
    /// The tool name a ride card travels under. Not a tool anyone runs: the workflow already ran the
    /// ride tool, and this server-produced call and result only carry its data to the page, where
    /// <c>BlockRenderer&lt;FunctionInvocationContentBlock&gt;</c> draws it. AGUIChatClient marks such a
    /// call informational and the host does not re-invoke the chat client (spike, ADR 0015).
    /// </summary>
    public const string RideCardTool = "show_ride_card";

    /// <summary>
    /// Sent at the end of a streamed run when the committed answer doesn't extend the text already
    /// streamed (a tool reply, a fixed reply, a retry). Informational, like the ride card; the page
    /// reloads the conversation from history when it sees one. Never part of a restored thread.
    /// </summary>
    public const string AnswerCorrectionTool = "concierge_answer";

    /// <summary>The correction for a streamed turn: the committed answer, as the result.</summary>
    public static (FunctionCallContent Call, FunctionResultContent Result) AnswerCorrectionContents(
        string turnId, string answer)
    {
        var callId = $"{turnId}:answer";
        return (
            new FunctionCallContent(callId, AnswerCorrectionTool, new Dictionary<string, object?>()) { InformationalOnly = true },
            new FunctionResultContent(callId, answer));
    }

    /// <summary>
    /// Development only: the real failure behind a reply that reports one ("The concierge is temporarily
    /// unavailable", a model-failure fallback). Informational; drawn as an expander under the reply and
    /// never part of a restored thread.
    /// </summary>
    public const string ErrorDetailsTool = "concierge_error_details";

    /// <summary>The details call for a turn, with the failure text as the result.</summary>
    public static (FunctionCallContent Call, FunctionResultContent Result) ErrorDetailsContents(
        string turnId, string details)
    {
        var callId = $"{turnId}:error-details";
        return (
            new FunctionCallContent(callId, ErrorDetailsTool, new Dictionary<string, object?>()) { InformationalOnly = true },
            new FunctionResultContent(callId, details));
    }

    /// <summary>Shared by the server that writes a card's result and the component that reads it.</summary>
    public static JsonSerializerOptions RideCardJson { get; } = new(JsonSerializerDefaults.Web);

    /// <summary>The call and result for a turn's card; the same ids live and after a restore.</summary>
    public static (FunctionCallContent Call, FunctionResultContent Result) RideCardContents(
        string turnId, ConciergeRideCard card)
    {
        var callId = $"{turnId}:ride-card";
        var call = new FunctionCallContent(callId, RideCardTool, new Dictionary<string, object?> { ["kind"] = card.Kind })
        {
            InformationalOnly = true,
        };
        return (call, new FunctionResultContent(callId, JsonSerializer.SerializeToElement(card, RideCardJson)));
    }

    /// <summary>The card in a result, or null when it is missing or unreadable (the answer text still stands).</summary>
    public static ConciergeRideCard? ReadRideCard(FunctionResultContent? result)
    {
        try
        {
            var card = result?.Result switch
            {
                JsonElement element => element.Deserialize<ConciergeRideCard>(RideCardJson),
                string text => JsonSerializer.Deserialize<ConciergeRideCard>(text, RideCardJson),
                ConciergeRideCard typed => typed,
                _ => null,
            };
            return card is { Kind.Length: > 0, Rides.Count: > 0 } ? card : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Per-request agent selection: the guest's bearer credential travels in the Authorization header
    /// and is bound to this request's agent. No credential, no agent (404). The credential itself is
    /// validated by <see cref="IConciergeService"/> on the turn, exactly as for <c>POST /turns</c>.
    /// </summary>
    public static AIAgent? CreateAgent(HttpContext context)
    {
        if (!AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization, out var header)
            || !StringComparer.OrdinalIgnoreCase.Equals(header.Scheme, "Bearer")
            || string.IsNullOrWhiteSpace(header.Parameter))
        {
            return null;
        }

        var concierge = context.RequestServices.GetRequiredService<IConciergeService>();
        return new ChatClientAgent(
            new StayWorkflowChatClient(
                concierge,
                header.Parameter,
                context.RequestServices.GetService<AnswerStreamListener>(),
                // Registered only in Development.
                context.RequestServices.GetService<AgenticRunFailures>()),
            instructions: null,
            name: AgentName,
            description: "Hotel ZGY concierge for one verified stay.");
    }

    /// <summary>Builds the in-memory thread a page restores, from the stay's committed history.</summary>
    public static StayConversationThread ThreadFrom(Guid stayId, IReadOnlyList<ConciergeTranscriptTurn> turns)
    {
        var thread = new StayConversationThread(stayId);
        thread.ReplaceFrom(turns);
        return thread;
    }
}

/// <summary>Display metadata the stock MessageList has no notion of.</summary>
public sealed record StayMessageStamp(DateTimeOffset OccurredAtUtc, bool Interrupted);

/// <summary>
/// In-memory <see cref="IConversationThread"/> for one page. It is seeded from the stay's history and
/// then records the page's own turns; the stay workflow, not this thread, is the source of truth.
/// User messages are stored as user-role updates, which is how <see cref="UIAgent.RestoreAsync"/>
/// rebuilds them (verified against 0.1.0-preview.1.26466.103).
/// </summary>
/// <remarks>
/// It also keeps, per message id (the restored block's <c>Id</c>), the time and Interrupted flag the
/// page renders, and the order messages were first shown in, for day separators. Messages the page
/// sends itself get a time when first rendered: their ids are assigned by the UI agent, not by us.
/// </remarks>
public sealed class StayConversationThread(Guid stayId) : IConversationThread
{
    private readonly Lock gate = new();
    private readonly List<ChatResponseUpdate> committed = [];
    private readonly List<ChatResponseUpdate> current = [];
    private readonly Dictionary<string, StayMessageStamp> stamps = new(StringComparer.Ordinal);
    private readonly List<string> shownOrder = [];

    public string ThreadId { get; } = $"stay-{stayId:N}";

    public bool IsStateful => false;

    public string? ConversationId => null;

    /// <summary>
    /// Replaces everything with the stay's committed history (initial load, a live update from another
    /// client, or Start over). Turns the page sent itself are in that history too, under their turn ids.
    /// </summary>
    public void ReplaceFrom(IReadOnlyList<ConciergeTranscriptTurn> turns)
    {
        lock (gate)
        {
            committed.Clear();
            current.Clear();
            stamps.Clear();
            shownOrder.Clear();
            foreach (var turn in turns)
            {
                var stamp = new StayMessageStamp(turn.OccurredAtUtc, turn.Interrupted);
                var guestId = $"{turn.TurnId}:guest";
                var conciergeId = $"{turn.TurnId}:concierge";
                committed.Add(new ChatResponseUpdate(ChatRole.User, turn.GuestMessage) { MessageId = guestId });
                committed.Add(new ChatResponseUpdate(ChatRole.Assistant, turn.AssistantMessage)
                {
                    MessageId = conciergeId,
                    ResponseId = turn.TurnId,
                });
                if (turn.RideCard is { } card)
                {
                    // The shape AGUIChatClient delivers live: the call on the answer's message, then
                    // the result as a Tool message keyed by the call id.
                    var (call, result) = AgenticConcierge.RideCardContents(turn.TurnId, card);
                    committed.Add(new ChatResponseUpdate(ChatRole.Assistant, [call])
                    {
                        MessageId = conciergeId,
                        ResponseId = turn.TurnId,
                    });
                    committed.Add(new ChatResponseUpdate(ChatRole.Tool, [result])
                    {
                        MessageId = call.CallId,
                        ResponseId = turn.TurnId,
                    });
                }
                stamps[guestId] = stamp with { Interrupted = false };
                stamps[conciergeId] = stamp;
            }
        }
    }

    /// <summary>
    /// The message's stamp, created now if the thread has never seen it (the page's own new turns).
    /// Also records the order messages are shown in, which <see cref="StartsNewDay"/> relies on.
    /// </summary>
    public StayMessageStamp StampFor(string messageId, DateTimeOffset now)
    {
        lock (gate)
        {
            if (!stamps.TryGetValue(messageId, out var stamp))
            {
                stamps[messageId] = stamp = new StayMessageStamp(now, false);
            }

            if (!shownOrder.Contains(messageId))
            {
                shownOrder.Add(messageId);
            }

            return stamp;
        }
    }

    /// <summary>True for the first message shown, and for one on a different hotel day than the message before it.</summary>
    public bool StartsNewDay(string messageId, TimeZoneInfo hotelTimeZone)
    {
        lock (gate)
        {
            var index = shownOrder.IndexOf(messageId);
            if (index <= 0) return index == 0;
            return HotelDay(shownOrder[index - 1]) != HotelDay(messageId);

            DateOnly HotelDay(string id) => stamps.TryGetValue(id, out var stamp)
                ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(stamp.OccurredAtUtc, hotelTimeZone).DateTime)
                : default;
        }
    }

    public void AppendUserMessage(ChatMessage message)
    {
        lock (gate)
        {
            current.Add(new ChatResponseUpdate(message.Role, message.Contents) { MessageId = message.MessageId });
        }
    }

    public void AppendUpdate(ChatResponseUpdate update)
    {
        lock (gate)
        {
            current.Add(update);
        }
    }

    public void CompleteTurn()
    {
        lock (gate)
        {
            committed.AddRange(current);
            current.Clear();
        }
    }

    public IReadOnlyList<ChatResponseUpdate> GetUpdates()
    {
        lock (gate)
        {
            return [.. committed];
        }
    }
}

/// <summary>
/// The page's AG-UI client calls this app's own UI listener (never the Think listener, never the
/// public tunnel). Kestrel's bound addresses are read once the server has started.
/// </summary>
public sealed class AgenticLoopback(IServer server, IConfiguration configuration)
{
    private Uri? baseAddress;

    public Uri BaseAddress => baseAddress ??= Resolve();

    /// <summary>
    /// A client for one page and one guest. The caller owns it. No timeout: an AG-UI run is an SSE
    /// stream that stays open for the whole desk turn.
    /// </summary>
    public IChatClient CreateChatClient(string credential, out HttpClient http)
    {
        http = new HttpClient { BaseAddress = BaseAddress, Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return new RunErrorChatClient(new AGUIChatClient(new AGUIChatClientOptions(http, AgenticConcierge.EndpointPath)));
    }

    private Uri Resolve()
    {
        var thinkPort = int.TryParse(configuration["THINK_PORT"], out var port) ? port : (int?)null;
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
        foreach (var address in addresses.OrderByDescending(a => a.StartsWith("https", StringComparison.Ordinal)))
        {
            var normalized = address.Replace("://+", "://localhost", StringComparison.Ordinal)
                .Replace("://*", "://localhost", StringComparison.Ordinal)
                .Replace("://0.0.0.0", "://localhost", StringComparison.Ordinal)
                .Replace("://[::]", "://localhost", StringComparison.Ordinal);
            if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri) && uri.Port != thinkPort)
            {
                return uri;
            }
        }

        throw new InvalidOperationException(
            "The agentic page found no UI listener address to call its AG-UI endpoint; check ASPNETCORE_URLS.");
    }
}

/// <summary>
/// The AG-UI client reports a failed run (RUN_ERROR) as an <see cref="ErrorContent"/> item and then
/// completes normally. The Components.AI kit ignores that item, so the page would show no reply and no
/// error. Raising it puts the conversation in the kit's Error state: the send-error card and Retry.
/// </summary>
public sealed class RunErrorChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken);
        AgenticRunFailedException.ThrowIfError(response.Messages.SelectMany(message => message.Contents));
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            AgenticRunFailedException.ThrowIfError(update.Contents);
            yield return update;
        }
    }
}

/// <summary>A run the AG-UI endpoint reported as failed. Its message is the host's (generic) text.</summary>
public sealed class AgenticRunFailedException(string message) : Exception(message)
{
    public static void ThrowIfError(IEnumerable<AIContent> contents)
    {
        if (contents.OfType<ErrorContent>().FirstOrDefault() is { } error)
        {
            var code = error.ErrorCode is { Length: > 0 } value ? $" ({value})" : "";
            throw new AgenticRunFailedException($"The concierge's AG-UI run failed{code}: {error.Message}");
        }
    }
}
