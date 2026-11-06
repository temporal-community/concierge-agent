using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Concierge.Contracts;
using Concierge.Web.Services;

namespace Concierge.Web.Voice;

/// <summary>
/// The OpenAI-compatible <c>POST /v1/chat/completions</c> handler that Deepgram's custom Think
/// provider calls. It submits only the latest user message as a voice turn to the stay workflow and
/// returns the completed answer as one SSE chunk sequence.
/// </summary>
internal static class ThinkEndpoint
{
    public const int MaxBodyBytes = 64 * 1024;

    public static async Task HandleAsync(HttpContext context)
    {
        var requestStarted = Stopwatch.GetTimestamp();
        var services = context.RequestServices;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ThinkEndpoint).FullName!);

        // Authenticate before touching the body so unauthenticated callers cannot make us buffer anything.
        if (!services.GetRequiredService<VoiceSessionRegistry>().TryAuthenticate(GetBearer(context), out var connection))
        {
            logger.LogInformation("Think request rejected: missing, unknown, revoked, or expired voice bearer.");
            await WriteErrorAsync(context, StatusCodes.Status401Unauthorized,
                "Provide the voice session's bearer credential in the Authorization header.",
                "invalid_request_error", "invalid_api_key");
            VoiceTelemetry.RecordThink(requestStarted, "unauthorized");
            return;
        }

        var body = await ReadBodyAsync(context);
        if (body is null)
        {
            logger.LogInformation("Think request rejected: body exceeds {MaxBodyBytes} bytes.", MaxBodyBytes);
            await WriteErrorAsync(context, StatusCodes.Status413PayloadTooLarge,
                $"The request body must be at most {MaxBodyBytes} bytes.",
                "invalid_request_error", "request_too_large");
            VoiceTelemetry.RecordThink(requestStarted, "too_large");
            return;
        }

        if (!ThinkRequest.TryParse(body, out var request, out var parseError))
        {
            logger.LogInformation("Think request rejected: {ParseError}", parseError);
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, parseError,
                "invalid_request_error", "invalid_request");
            VoiceTelemetry.RecordThink(requestStarted, "invalid");
            return;
        }

        var session = connection.Session;
        if (request.EndChatToolResultId is { } callId)
        {
            if (!connection.AcceptEndChatToolResult(callId))
            {
                VoiceTelemetry.RecordEndChat(logger, "tool_result_rejected", connection, "unconfirmed_or_cancelled_call", callId);
                await WriteErrorAsync(context, StatusCodes.Status400BadRequest,
                    "The end-chat function result does not match a confirmed request.",
                    "invalid_request_error", "invalid_request");
                VoiceTelemetry.RecordThink(requestStarted, "invalid");
                return;
            }

            VoiceTelemetry.RecordEndChat(logger, "tool_result_accepted", connection, "confirmed_call", callId);
            var createdAt = services.GetRequiredService<TimeProvider>().GetUtcNow().ToUnixTimeSeconds();
            var farewellTurnId = ThinkRequest.TurnId(
                session.Id, connection.Number, request.UserOrdinal, request.UserMessage);
            await WriteRegisteredSseAsync(
                context,
                session,
                farewellTurnId,
                request.Model,
                createdAt,
                ConciergeEndChat.Farewell);
            VoiceTelemetry.RecordEndChat(logger, "farewell_returned", connection, "tool_result", callId);
            VoiceTelemetry.RecordThink(requestStarted, "answered");
            return;
        }

        connection.RecordSpeech();
        var turnId = ThinkRequest.TurnId(session.Id, connection.Number, request.UserOrdinal, request.UserMessage);
        connection.AdmitEndChatTurn(turnId, request.UserMessage);
        var concierge = services.GetRequiredService<IConciergeService>();
        var started = Stopwatch.GetTimestamp();
        // Logical turn identity remains stable across distinct RPC deliveries and exact retries.
        Activity.Current?
            .SetTag("concierge.voice.turn_id", turnId)
            .SetTag("concierge.voice.connection", connection.Number)
            .SetTag("concierge.voice.user_ordinal", request.UserOrdinal);

        // ADR 0015 measurement spike, off by default: speak the model's partial text as it streams.
        // Each message is the full text so far, so a request that joins a running turn catches up.
        using var answerStream = services.GetRequiredService<IConfiguration>().GetValue<bool>(StreamThinkSetting)
            ? services.GetService<AnswerStreamListener>()?.Subscribe(turnId)
            : null;

        // The shared task outlives this request: a Deepgram abort or retry must not cancel or
        // resubmit admitted Temporal work. ConciergeService holds no disposable scoped state.
        var turn = session.JoinOrStart(turnId, () => concierge.SubmitVoiceTurnAsync(
            session.Stay, turnId, request.UserMessage, CancellationToken.None, enableEndChatFunction: true),
            request.UserMessage);

        var created = services.GetRequiredService<TimeProvider>().GetUtcNow().ToUnixTimeSeconds();
        StreamedThink? streamed = null;
        ConciergeTurnResult result;
        try
        {
            if (answerStream is not null)
            {
                streamed = new StreamedThink(context, session, turnId, request.Model, created);
                await streamed.FollowAsync(turn, answerStream.Reader, context.RequestAborted);
            }

            result = await turn.WaitAsync(context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            logger.LogInformation(
                "Think caller disconnected after {ElapsedMs:F0} ms (connection {ConnectionNumber}, user message {UserOrdinal}, turn {TurnId}); "
                + "the voice turn continues in Temporal.",
                Stopwatch.GetElapsedTime(started).TotalMilliseconds, connection.Number, request.UserOrdinal, turnId);
            VoiceTelemetry.RecordThink(requestStarted, "caller_disconnected");
            return;
        }
        catch (Exception exception) when (streamed?.Started == true && exception is not OperationCanceledException)
        {
            // Headers and text are already out; the guest heard part of an answer that never committed.
            logger.LogWarning(
                "Think streamed text for a voice turn that then failed. Cause types: {CauseTypes}.",
                ConciergeDiagnostics.ExceptionTypeChain(exception));
            await streamed.FinishAsync(null);
            VoiceTelemetry.RecordThink(requestStarted, "failed");
            return;
        }
        catch (ConciergeApiException exception)
        {
            logger.LogWarning("Think voice turn failed with {Code} ({StatusCode}).", exception.Code, exception.StatusCode);
            await WriteErrorAsync(context, exception.StatusCode, exception.Message,
                exception.StatusCode >= 500 ? "server_error" : "invalid_request_error", exception.Code);
            VoiceTelemetry.RecordThink(requestStarted, "failed");
            return;
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Think voice turn failed. Cause types: {CauseTypes}.",
                ConciergeDiagnostics.ExceptionTypeChain(exception));
            await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable,
                "The concierge is temporarily unavailable. Please try again.", "server_error", "unavailable");
            VoiceTelemetry.RecordThink(requestStarted, "unavailable");
            return;
        }

        logger.LogInformation(
            "Think voice turn answered in {ElapsedMs:F0} ms (connection {ConnectionNumber}, user message {UserOrdinal}).",
            Stopwatch.GetElapsedTime(started).TotalMilliseconds, connection.Number, request.UserOrdinal);

        if (streamed?.Started == true)
        {
            var completed = await streamed.FinishAsync(result.EndChatFunctionCall is null ? result.AssistantMessage : null);
            logger.LogInformation(
                "Think streamed a voice turn (connection {ConnectionNumber}, user message {UserOrdinal}); committed answer {Outcome}.",
                connection.Number, request.UserOrdinal, completed ? "extended the spoken text" : "differed from the spoken text");
            VoiceTelemetry.RecordThink(requestStarted, "answered");
            return;
        }

        if (result.EndChatFunctionCall is { } endChatCall)
        {
            if (!StringComparer.Ordinal.Equals(endChatCall.Name, ConciergeEndChat.FunctionName)
                || !connection.RegisterEndChatCall(endChatCall.Id, turnId))
            {
                VoiceTelemetry.RecordEndChat(logger, "call_rejected", connection, "invalid_or_conflicting_call", endChatCall.Id);
                await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable,
                    "The concierge could not complete this request. Please try again.",
                    "server_error", "unavailable");
                VoiceTelemetry.RecordThink(requestStarted, "unavailable");
                return;
            }

            VoiceTelemetry.RecordEndChat(logger, "call_registered", connection, "model_decision", endChatCall.Id);
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            await context.Response.WriteAsync(
                ThinkRequest.SseFunctionCallFrames(turnId, request.Model, created, endChatCall),
                context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            VoiceTelemetry.RecordThink(requestStarted, "answered");
            return;
        }

        // Register before Deepgram can receive the response, so the subsequent assistant event
        // binds playback to this accepted Temporal turn rather than inferring from its ordinal.
        await WriteRegisteredSseAsync(context, session, turnId, request.Model, created, result.AssistantMessage);
        VoiceTelemetry.RecordThink(requestStarted, "answered");
    }

    /// <summary>Config switch for the ADR 0015 voice streaming measurement. Off unless set to true.</summary>
    public const string StreamThinkSetting = "Voice:StreamThink";

    /// <summary>
    /// Writes a voice turn's partial text as SSE content chunks while the turn runs (ADR 0015, measurement
    /// only). Spoken text can't be taken back: when the committed answer doesn't extend it, nothing more
    /// is sent and the mismatch is logged.
    /// </summary>
    private sealed class StreamedThink(HttpContext context, VoiceSession session, string turnId, string model, long created)
    {
        private readonly Concierge.Web.Agentic.StayWorkflowChatClient.StreamedText text = new();
        private VoiceSession.ReplyRegistration? registration;

        public bool Started => registration is not null;

        public async Task FollowAsync(
            Task<ConciergeTurnResult> turn,
            System.Threading.Channels.ChannelReader<ConciergeAnswerStreamMessage> reader,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                var next = reader.WaitToReadAsync(cancellationToken).AsTask();
                if (await Task.WhenAny(turn, next) == turn || !await next)
                {
                    return;
                }

                while (reader.TryRead(out var message))
                {
                    if (text.Extend(message) is { } delta)
                    {
                        await WriteAsync(ThinkRequest.SseContentFrame(turnId, model, created, delta));
                    }
                }
            }
        }

        /// <summary>Sends the rest of <paramref name="answer"/> when it extends what was spoken, then ends the stream.</summary>
        public async Task<bool> FinishAsync(string? answer)
        {
            var extends = answer is not null && answer.StartsWith(text.Text, StringComparison.Ordinal);
            if (extends && answer!.Length > text.Text.Length)
            {
                await WriteAsync(ThinkRequest.SseContentFrame(turnId, model, created, answer[text.Text.Length..]));
            }

            await WriteAsync(ThinkRequest.SseStopFrames(turnId, model, created));
            return extends;
        }

        private async Task WriteAsync(string frames)
        {
            if (registration is null)
            {
                // Register before Deepgram can receive any text, as for a whole answer.
                registration = session.RegisterReplyForPlayback(turnId);
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "text/event-stream";
                context.Response.Headers.CacheControl = "no-cache";
                frames = ThinkRequest.SseRoleFrame(turnId, model, created) + frames;
            }

            try
            {
                await context.Response.WriteAsync(frames, context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }
            catch
            {
                // Deepgram went away mid-answer; nothing more of this reply will play.
                session.CancelReplyForPlayback(registration);
                throw;
            }
        }
    }

    internal static async Task WriteRegisteredSseAsync(
        HttpContext context,
        VoiceSession session,
        string turnId,
        string model,
        long created,
        string answer)
    {
        var registration = session.RegisterReplyForPlayback(turnId);
        var writeCompleted = false;
        try
        {
            await WriteSseAsync(context, turnId, model, created, answer);
            writeCompleted = true;
        }
        finally
        {
            if (!writeCompleted)
            {
                session.CancelReplyForPlayback(registration);
            }
        }
    }

    private static async Task WriteSseAsync(
        HttpContext context, string turnId, string model, long created, string answer)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        await context.Response.WriteAsync(
            ThinkRequest.SseFrames(turnId, model, created, answer),
            context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }

    private static string? GetBearer(HttpContext context) =>
        AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization, out var header)
        && StringComparer.OrdinalIgnoreCase.Equals(header.Scheme, "Bearer")
            ? header.Parameter
            : null;

    /// <summary>Reads at most <see cref="MaxBodyBytes"/>; returns <see langword="null"/> when the body is larger.</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpContext context)
    {
        if (context.Request.ContentLength > MaxBodyBytes)
        {
            return null;
        }

        var buffer = new byte[MaxBodyBytes + 1];
        var count = 0;
        int read;
        while (count < buffer.Length
            && (read = await context.Request.Body.ReadAsync(buffer.AsMemory(count), context.RequestAborted)) > 0)
        {
            count += read;
        }

        return count > MaxBodyBytes ? null : buffer[..count];
    }

    private static Task WriteErrorAsync(HttpContext context, int statusCode, string message, string type, string code)
    {
        context.Response.StatusCode = statusCode;
        return context.Response.WriteAsJsonAsync(
            new OpenAIErrorResponse(new OpenAIError(message, type, null, code)),
            context.RequestAborted);
    }

    private sealed record OpenAIErrorResponse(OpenAIError Error);

    private sealed record OpenAIError(string Message, string Type, string? Param, string Code);
}

/// <summary>The parts of an OpenAI chat completions request that a voice turn uses.</summary>
internal sealed record ThinkRequest(
    string UserMessage,
    int UserOrdinal,
    string Model,
    string? EndChatToolResultId = null)
{
    private const string DefaultModel = "concierge";

    /// <summary>
    /// Takes only the latest user message; the rest of Deepgram's history is ignored because the stay
    /// workflow owns conversation memory. The user-message count identifies the turn within a connection.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> body, out ThinkRequest request, out string error)
    {
        request = null!;
        error = string.Empty;
        try
        {
            var reader = new Utf8JsonReader(body);
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("messages", out var messages)
                || messages.ValueKind != JsonValueKind.Array)
            {
                error = "Send a JSON object with a 'messages' array.";
                return false;
            }

            if (!TryParseEndChatToolResult(messages, out var toolResultId, out error))
            {
                return false;
            }

            var ordinal = 0;
            JsonElement latest = default;
            foreach (var message in messages.EnumerateArray())
            {
                if (message.ValueKind == JsonValueKind.Object
                    && message.TryGetProperty("role", out var role)
                    && role.ValueKind == JsonValueKind.String
                    && role.ValueEquals("user"))
                {
                    ordinal++;
                    latest = message;
                }
            }

            if (ordinal == 0 && toolResultId is null)
            {
                error = "Include at least one message with role 'user'.";
                return false;
            }

            string userMessage;
            if (ordinal == 0)
            {
                userMessage = string.Empty;
            }
            else if (!latest.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(content.GetString()))
            {
                error = "The latest 'user' message must have nonblank string 'content'.";
                return false;
            }
            else
            {
                userMessage = content.GetString()!.Trim();
            }

            var model = root.TryGetProperty("model", out var modelValue)
                && modelValue.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(modelValue.GetString())
                    ? modelValue.GetString()!
                    : DefaultModel;
            request = new ThinkRequest(userMessage, ordinal, model, toolResultId);
            return true;
        }
        catch (JsonException)
        {
            error = "Send a valid JSON request body.";
            return false;
        }
    }

    private static bool TryParseEndChatToolResult(
        JsonElement messages, out string? resultCallId, out string error)
    {
        resultCallId = null;
        error = string.Empty;
        string? functionCallId = null;
        var resultCount = 0;

        foreach (var message in messages.EnumerateArray())
        {
            if (message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("role", out var role)
                || role.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            if (role.ValueEquals("assistant")
                && message.TryGetProperty("tool_calls", out var calls))
            {
                if (calls.ValueKind != JsonValueKind.Array || calls.GetArrayLength() != 1)
                {
                    error = "The end-chat function request must contain exactly one tool call.";
                    return false;
                }

                var call = calls[0];
                var function = call.ValueKind == JsonValueKind.Object
                    && call.TryGetProperty("function", out var parsedFunction)
                    ? parsedFunction
                    : default;
                if (function.ValueKind != JsonValueKind.Object
                    || !function.TryGetProperty("name", out var functionName)
                    || functionName.ValueKind != JsonValueKind.String
                    || !StringComparer.Ordinal.Equals(functionName.GetString(), ConciergeEndChat.FunctionName))
                {
                    error = "Only the end-chat function is supported.";
                    return false;
                }

                if (!call.TryGetProperty("id", out var id)
                    || id.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(id.GetString())
                    || id.GetString()!.Length > 128
                    || !function.TryGetProperty("arguments", out var arguments)
                    || !IsEmptyObjectArguments(arguments))
                {
                    error = "The end-chat function call is malformed.";
                    return false;
                }

                functionCallId = id.GetString();
            }
            else if (role.ValueEquals("tool"))
            {
                if (!message.TryGetProperty("tool_call_id", out var id)
                    || id.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(id.GetString())
                    || !message.TryGetProperty("name", out var name)
                    || name.ValueKind != JsonValueKind.String
                    || !StringComparer.Ordinal.Equals(name.GetString(), ConciergeEndChat.FunctionName)
                    || !message.TryGetProperty("content", out var content)
                    || content.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                resultCallId = id.GetString();
                resultCount++;
            }
        }

        if (functionCallId is null && resultCount == 0)
        {
            return true;
        }

        if (functionCallId is null || resultCount != 1
            || !StringComparer.Ordinal.Equals(functionCallId, resultCallId))
        {
            error = "The end-chat function result must match its assistant tool call.";
            return false;
        }

        return true;
    }

    private static bool IsEmptyObjectArguments(JsonElement arguments)
    {
        if (arguments.ValueKind == JsonValueKind.String)
        {
            try
            {
                using var parsed = JsonDocument.Parse(arguments.GetString()!);
                arguments = parsed.RootElement.Clone();
            }
            catch (JsonException)
            {
                return false;
            }
        }

        return arguments.ValueKind == JsonValueKind.Object
            && !arguments.EnumerateObject().Any();
    }

    /// <summary>
    /// <c>v:{voiceSessionId}:{connectionNumber}:{userOrdinal}:{messageHash}</c>. The message hash separates
    /// resumed speech that changes the latest text without changing its ordinal; the connection number keeps
    /// IDs unique across manual reconnects.
    /// </summary>
    public static string TurnId(Guid voiceSessionId, int connectionNumber, int userOrdinal, string userMessage)
    {
        return new VoiceUtterance(voiceSessionId, connectionNumber, userOrdinal).TurnId(userMessage);
    }

    public static string SseRoleFrame(string id, string model, long created) =>
        ChunkFrame(id, model, created, new { role = "assistant" }, null);

    public static string SseContentFrame(string id, string model, long created, string content) =>
        ChunkFrame(id, model, created, new { content }, null);

    public static string SseStopFrames(string id, string model, long created) =>
        ChunkFrame(id, model, created, new { }, "stop") + "data: [DONE]\n\n";

    private static string ChunkFrame(string id, string model, long created, object delta, string? finishReason) =>
        "data: " + JsonSerializer.Serialize(new
        {
            id,
            @object = "chat.completion.chunk",
            created,
            model,
            choices = new[] { new { index = 0, delta, finish_reason = finishReason } },
        }) + "\n\n";

    /// <summary>Assistant-role chunk, one content chunk, the stop chunk, then <c>[DONE]</c>.</summary>
    public static string SseFrames(string id, string model, long created, string answer)
    {
        string Frame(object delta, string? finishReason) =>
            "data: " + JsonSerializer.Serialize(new
            {
                id,
                @object = "chat.completion.chunk",
                created,
                model,
                choices = new[] { new { index = 0, delta, finish_reason = finishReason } },
            }) + "\n\n";

        return Frame(new { role = "assistant" }, null)
            + Frame(new { content = answer }, null)
            + Frame(new { }, "stop")
            + "data: [DONE]\n\n";
    }

    /// <summary>Streams the actual model-generated end-chat tool call for Deepgram's client handler.</summary>
    public static string SseFunctionCallFrames(
        string id, string model, long created, ConciergeEndChatFunctionCall functionCall)
    {
        string Frame(object delta, string? finishReason) =>
            "data: " + JsonSerializer.Serialize(new
            {
                id,
                @object = "chat.completion.chunk",
                created,
                model,
                choices = new[] { new { index = 0, delta, finish_reason = finishReason } },
            }) + "\n\n";

        return Frame(new { role = "assistant" }, null)
            + Frame(new
            {
                tool_calls = new[]
                {
                    new
                    {
                        index = 0,
                        id = functionCall.Id,
                        type = "function",
                        function = new { name = functionCall.Name, arguments = functionCall.Arguments },
                    },
                },
            }, null)
            + Frame(new { }, "tool_calls")
            + "data: [DONE]\n\n";
    }
}
