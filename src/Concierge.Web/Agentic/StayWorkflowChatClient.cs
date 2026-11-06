using System.Runtime.CompilerServices;
using Concierge.Contracts;
using Concierge.Web.Services;
using Microsoft.Extensions.AI;

namespace Concierge.Web.Agentic;

/// <summary>
/// An <see cref="IChatClient"/> whose "model" is the guest's stay workflow. It submits the latest user
/// message as a concierge turn with the guest's credential and returns the workflow's answer, so the
/// AG-UI endpoint gets the same tools, memory, rides and guardrails as every other channel.
/// </summary>
/// <remarks>
/// <para>
/// AG-UI clients resend the whole conversation on every run. Only the latest user message is used: the
/// stay workflow owns conversation memory, exactly as for the Think endpoint.
/// </para>
/// <para>
/// Streaming (ADR 0015): while the turn runs, partial text from the model activity arrives through
/// <see cref="AnswerStreamListener"/> and is forwarded only while it extends what was already sent.
/// That text is provisional. The committed answer wins: when it extends the streamed text the rest is
/// sent, and otherwise an informational <see cref="AgenticConcierge.AnswerCorrectionTool"/> call tells
/// the page to reload the conversation from history.
/// </para>
/// </remarks>
public sealed class StayWorkflowChatClient(
    IConciergeService concierge, string credential, AnswerStreamListener? answerStream = null) : IChatClient
{
    /// <summary>Channel sent with each turn. "web" selects the written answer style.</summary>
    public const string Channel = "web";

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        List<ChatResponseUpdate> updates = [];
        await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            updates.Add(update);
        }

        return updates.ToChatResponse();
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string text;
        ConciergeApiException? invalid = null;
        try
        {
            text = LatestUserText(messages);
        }
        catch (ConciergeApiException exception)
        {
            text = "";
            invalid = exception;
        }

        if (invalid is not null)
        {
            yield return Rejected(invalid);
            yield break;
        }

        // A fresh id per run: an AG-UI retry is a new run, and the page already guards double sends.
        var turnId = $"agui:{Guid.NewGuid():N}";
        // Before submitting: NOTIFY has no replay.
        using var stream = answerStream?.Subscribe(turnId);
        var submit = SubmitAsync(turnId, text, cancellationToken);
        var sent = new StreamedText();
        if (stream is not null)
        {
            var reader = stream.Reader;
            while (true)
            {
                var next = reader.WaitToReadAsync(cancellationToken).AsTask();
                if (await Task.WhenAny(submit, next) == submit || !await next)
                {
                    break;
                }

                while (reader.TryRead(out var message))
                {
                    if (sent.Extend(message) is { } delta)
                    {
                        yield return Update(turnId, [new TextContent(delta)]);
                    }
                }
            }
        }

        var (result, failure) = await submit;
        if (failure is not null)
        {
            // It is not a stay turn: the workflow never committed it.
            yield return sent.Text.Length == 0
                ? Rejected(failure)
                : Update(turnId, AnswerCorrection(turnId, failure.Message));
            yield break;
        }

        List<AIContent> contents = [];
        if (result!.AssistantMessage.StartsWith(sent.Text, StringComparison.Ordinal))
        {
            if (result.AssistantMessage.Length > sent.Text.Length)
            {
                contents.Add(new TextContent(result.AssistantMessage[sent.Text.Length..]));
            }
        }
        else
        {
            contents.AddRange(AnswerCorrection(turnId, result.AssistantMessage));
        }

        if (result.RideCard is { } card)
        {
            var (call, cardResult) = AgenticConcierge.RideCardContents(result.TurnId, card);
            contents.Add(call);
            contents.Add(cardResult);
        }

        if (contents.Count > 0)
        {
            yield return Update(turnId, contents);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }

    internal static string LatestUserText(IEnumerable<ChatMessage> messages)
    {
        var latest = messages.LastOrDefault(message => message.Role == ChatRole.User)?.Text?.Trim();
        if (string.IsNullOrEmpty(latest))
        {
            throw new ConciergeApiException(
                "invalid_request",
                "Send a conversation whose latest user message has text.",
                StatusCodes.Status400BadRequest);
        }

        return latest;
    }

    private async Task<(ConciergeTurnResult? Result, ConciergeApiException? Failure)> SubmitAsync(
        string turnId, string text, CancellationToken cancellationToken)
    {
        try
        {
            return (await concierge.SubmitTurnAsync(
                credential, new ConciergeTurnRequest(turnId, text, Channel), cancellationToken), null);
        }
        catch (ConciergeApiException exception)
        {
            return (null, exception);
        }
    }

    /// <summary>
    /// Thrown, a failure reaches the client only as the host's generic "An error occurred while
    /// streaming the agent response."; as ErrorContent the 1.27 AG-UI host drops it entirely (both
    /// observed in AgenticConciergeTests). As text, the guest sees the concierge's own actionable message
    /// ("Authenticate again to continue.", the message-size limit, ...).
    /// </summary>
    private static ChatResponseUpdate Rejected(ConciergeApiException exception) =>
        new(ChatRole.Assistant, exception.Message);

    private static List<AIContent> AnswerCorrection(string turnId, string answer)
    {
        var (call, result) = AgenticConcierge.AnswerCorrectionContents(turnId, answer);
        return [call, result];
    }

    private static ChatResponseUpdate Update(string turnId, IList<AIContent> contents) =>
        new(ChatRole.Assistant, contents) { MessageId = turnId, ResponseId = turnId };

    /// <summary>
    /// The text already sent for a turn. Only the first model call is followed, and only while each
    /// message extends it: a retry or a later call starts its text over, which can't be taken back on
    /// the wire, so streaming stops there and the committed answer settles it.
    /// </summary>
    internal sealed class StreamedText
    {
        private string? call;
        private bool frozen;

        public string Text { get; private set; } = "";

        /// <summary>The new text to send, or null.</summary>
        public string? Extend(ConciergeAnswerStreamMessage message)
        {
            if (frozen)
            {
                return null;
            }

            call ??= message.Call;
            if (!StringComparer.Ordinal.Equals(call, message.Call)
                || !message.Text.StartsWith(Text, StringComparison.Ordinal))
            {
                frozen = true;
                return null;
            }

            if (message.Text.Length == Text.Length)
            {
                return null;
            }

            var delta = message.Text[Text.Length..];
            Text = message.Text;
            return delta;
        }
    }
}
