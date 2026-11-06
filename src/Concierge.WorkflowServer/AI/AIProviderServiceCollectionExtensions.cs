using Azure.AI.OpenAI;
using Azure.Identity;
using Concierge.Contracts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OllamaSharp.Models;
using OpenAI;
using OpenAI.Responses;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;

namespace Concierge.WorkflowServer.AI;

#pragma warning disable OPENAI001 // The Responses SDK and its MEAI adapter are experimental.

public static class AIProviderServiceCollectionExtensions
{
    /// <summary>
    /// Registers the chat client and embedding generator for the configured Foundry or Ollama provider,
    /// validating <c>AI:Chat</c> and <c>AI:Embeddings</c> at startup. Both clients are wrapped with the
    /// configured per-request timeout and concurrency limit.
    /// </summary>
    public static IServiceCollection AddConciergeAIProviders(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<AIProviderOptionsValidator>();
        services.AddSingleton<IValidateOptions<ChatProviderOptions>>(
            provider => provider.GetRequiredService<AIProviderOptionsValidator>());
        services.AddSingleton<IValidateOptions<EmbeddingProviderOptions>>(
            provider => provider.GetRequiredService<AIProviderOptionsValidator>());

        services.AddOptions<ChatProviderOptions>()
            .Bind(configuration.GetSection(ChatProviderOptions.SectionName))
            .ValidateOnStart();

        services.AddOptions<EmbeddingProviderOptions>()
            .Bind(configuration.GetSection(EmbeddingProviderOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<IChatClient>(services =>
        {
            var options = services.GetRequiredService<IOptions<ChatProviderOptions>>().Value;
            var inner = CreateChatClient(options);
            var hotelTimeZoneId = services.GetService<IOptions<Location.HotelLocationOptions>>()?.Value.TimeZoneId;
            var bounded = new BoundedChatClient(
                inner,
                TimeSpan.FromSeconds(options.RequestTimeoutSeconds),
                options.MaxConcurrentRequests,
                services.GetService<TimeProvider>() ?? TimeProvider.System,
                string.IsNullOrWhiteSpace(hotelTimeZoneId) ? null : TimeZoneInfo.FindSystemTimeZoneById(hotelTimeZoneId));
            // Outside the bound: publishing partial text must never count against the model call.
            return new AnswerStreamPublishingChatClient(bounded, services.GetService<AnswerStreamPublisher>());
        });
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(services =>
        {
            var options = services.GetRequiredService<IOptions<EmbeddingProviderOptions>>().Value;
            var inner = CreateEmbeddingGenerator(options);
            return new BoundedEmbeddingGenerator(
                inner,
                TimeSpan.FromSeconds(options.RequestTimeoutSeconds),
                options.MaxConcurrentRequests,
                TimeSpan.FromSeconds(options.IngestionRequestTimeoutSeconds));
        });

        return services;
    }

    internal static IChatClient CreateChatClient(
        ChatProviderOptions options,
        PipelineTransport? transport = null)
    {
        var clientOptions = new ResponsesClientOptions
        {
            Endpoint = AIProviderEndpoints.NormalizeResponsesEndpoint(options.Provider, options.Endpoint),
            // The outer bounded client owns the request deadline.
            NetworkTimeout = Timeout.InfiniteTimeSpan,
            // Temporal owns chat retries (DurableAI RetryPolicy); the SDK default would send up
            // to three extra requests, with backoff, inside each Temporal attempt.
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
        };
        if (transport is not null)
        {
            clientOptions.Transport = transport;
        }

        var isOllama = IsOllama(options.Provider);
        var responseClient = isOllama || !string.IsNullOrEmpty(options.ApiKey)
            ? new ResponsesClient(
                new ApiKeyCredential(isOllama ? "ollama" : options.ApiKey!),
                clientOptions)
            : new ResponsesClient(
                new BearerTokenPolicy(
                    new DefaultAzureCredential(),
                    "https://ai.azure.com/.default"),
                clientOptions);

        var effort = string.Equals(options.ReasoningEffort, "none", StringComparison.OrdinalIgnoreCase)
            ? ReasoningEffort.None
            : ReasoningEffort.Low;

        return new ChatClientBuilder(responseClient.AsIChatClient(options.Model))
            .ConfigureOptions(chatOptions =>
            {
                // The Temporal stay workflow provides complete history and owns conversation state.
                if (chatOptions.ConversationId is not null
                    || chatOptions.ContinuationToken is not null
                    || chatOptions.AllowBackgroundResponses == true
                    || chatOptions.RawRepresentationFactory is not null)
                {
                    throw new ArgumentException(
                        "The concierge Responses client accepts stateless chat options only.");
                }

                chatOptions.Reasoning ??= new ReasoningOptions { Effort = effort };
                // Foundry reasoning requests reject temperature, including zero; Ollama accepts it.
                if (!isOllama)
                {
                    chatOptions.Temperature = null;
                }
                chatOptions.RawRepresentationFactory =
                    _ => new CreateResponseOptions { StoredOutputEnabled = false };
            })
            // Inside the options step so spans carry the effective options; sensitive data stays off.
            .UseOpenTelemetry()
            .Build();
    }

#pragma warning restore OPENAI001

    /// <summary>
    /// Ollama <c>keep_alive</c> sent on every embedding request so the host never unloads the
    /// embedding model (its default is five idle minutes, costing ~3 s on the next retrieval).
    /// OllamaSharp maps <see cref="EmbeddingGenerationOptions.AdditionalProperties"/>["keep_alive"]
    /// (string only) to <c>EmbedRequest.KeepAlive</c>. Ollama parses strings with Go's
    /// <c>time.ParseDuration</c>, so a unit is required: <c>"-1"</c> is rejected with HTTP 400,
    /// while any negative duration such as <c>"-1m"</c> means "keep loaded indefinitely".
    /// </summary>
    internal const string OllamaEmbeddingKeepAlive = "-1m";

    internal static IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(
        EmbeddingProviderOptions options,
        HttpMessageHandler? ollamaHandler = null,
        PipelineTransport? foundryTransport = null)
    {
        var isOllama = IsOllama(options.Provider);
        IEmbeddingGenerator<string, Embedding<float>> provider = isOllama
            ? CreateOllamaClient(options, ollamaHandler)
            : CreateAzureClient(options, foundryTransport)
                .GetEmbeddingClient(options.Model)
                .AsIEmbeddingGenerator(options.Dimensions);

        var builder = new EmbeddingGeneratorBuilder<string, Embedding<float>>(provider);
        if (isOllama)
        {
            // Applied to a clone of the caller's options; an explicit caller value wins.
            builder.ConfigureOptions(embeddingOptions =>
            {
                embeddingOptions.AdditionalProperties ??= [];
                embeddingOptions.AdditionalProperties.TryAdd("keep_alive", OllamaEmbeddingKeepAlive);
            });
        }

        // Sensitive data stays off: spans and metrics carry model and usage, never input text.
        return builder
            .UseOpenTelemetry()
            .Build();
    }

    private static AzureOpenAIClient CreateAzureClient(
        AIProviderOptions options,
        PipelineTransport? transport = null)
    {
        var clientOptions = new AzureOpenAIClientOptions
        {
            // The outer bounded generator owns the request deadline.
            NetworkTimeout = Timeout.InfiniteTimeSpan,
            // Temporal owns embedding retries: SetupActivities.GenerateEmbeddingsAsync (ingestion)
            // and HotelKnowledgeActivities.RetrieveAsync (retrieval). The Azure default retries
            // three times and honors Retry-After, which could outlast the activity allowance.
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
        };
        if (transport is not null)
        {
            clientOptions.Transport = transport;
        }

        var endpoint = new Uri(options.Endpoint);

        return !string.IsNullOrEmpty(options.ApiKey)
            ? new AzureOpenAIClient(
                endpoint,
                new ApiKeyCredential(options.ApiKey),
                clientOptions)
            : new AzureOpenAIClient(
                endpoint,
                new DefaultAzureCredential(),
                clientOptions);
    }

    private static OllamaApiClient CreateOllamaClient(
        AIProviderOptions options,
        HttpMessageHandler? handler = null)
    {
        // Embeddings retain the existing Ollama API; only chat moves to Responses.
        // The outer bounded generator owns the deadline. OllamaSharp does not retry.
        var httpClient = new HttpClient(handler ?? new HttpClientHandler())
        {
            BaseAddress = new Uri(options.Endpoint),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        return new OllamaApiClient(httpClient, options.Model);
    }

    private static bool IsOllama(string provider) =>
        StringComparer.OrdinalIgnoreCase.Equals(provider, AIProviderNames.Ollama);
}

/// <summary>
/// Keeps a bounded model or embedding call inside the Temporal activity attempt that runs it.
/// The bound covers the concurrency-slot wait, the request, the response-body read and any
/// streaming enumeration. Inside an activity it is the shorter of the configured request timeout
/// and the attempt's StartToClose allowance less a completion margin, so the per-attempt share the
/// stay workflow shrinks near its turn deadline cancels the provider call, releases the slot and
/// disposes the HTTP request before Temporal times the attempt out. The activity cancellation
/// token remains linked as a second stop. Outside an activity (warm-up, evaluation) the
/// configured timeout applies unchanged.
/// </summary>
internal static class ActivityCallBudget
{
    /// <summary>Time reserved for the activity to finish its other work and report a result.</summary>
    internal static readonly TimeSpan CompletionMargin = TimeSpan.FromSeconds(2);

    internal static TimeSpan Bound(TimeSpan configured)
    {
        if (!Temporalio.Activities.ActivityExecutionContext.HasCurrent)
        {
            return configured;
        }

        var info = Temporalio.Activities.ActivityExecutionContext.Current.Info;
        return Bound(configured, info.StartedTime, info.StartToCloseTimeout, DateTime.UtcNow);
    }

    /// <summary>
    /// Bounds a call to the time left in the current attempt, measured from the attempt's start, so
    /// work already done in the activity (retrieval's database lookups) counts against it. The margin
    /// keeps headroom for work after the call (retrieval's lexical and vector searches) and for
    /// reporting the result; short near-deadline allowances keep three quarters rather than zero.
    /// </summary>
    internal static TimeSpan Bound(
        TimeSpan configured,
        DateTime attemptStartedUtc,
        TimeSpan? startToClose,
        DateTime utcNow)
    {
        if (startToClose is not { } allowance || allowance <= TimeSpan.Zero)
        {
            return configured;
        }

        var margin = TimeSpan.FromTicks(Math.Min(CompletionMargin.Ticks, allowance.Ticks / 4));
        var elapsed = utcNow - attemptStartedUtc.ToUniversalTime();
        // Clamp clock skew between the Temporal server (StartedTime) and this worker to [0, allowance].
        elapsed = elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed > allowance ? allowance : elapsed;
        var remaining = allowance - margin - elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            // Already past the usable allowance: fail fast with the bounded client's timeout.
            return TimeSpan.FromMilliseconds(1);
        }

        return remaining < configured ? remaining : configured;
    }

    /// <summary>
    /// Runs <paramref name="call"/> and reports expiry of the bound as <see cref="TimeoutException"/>,
    /// so the workflow sees a timeout (and gives its deadline reply) rather than a generic failure.
    /// Caller cancellation still surfaces as <see cref="OperationCanceledException"/>.
    /// </summary>
    internal static async Task<T> RunAsync<T>(
        string client,
        TimeSpan bound,
        CancellationTokenSource timeoutSource,
        CancellationToken callerToken,
        Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (OperationCanceledException exception) when (IsOwnTimeout(timeoutSource, callerToken))
        {
            throw Exceeded(client, bound, exception);
        }
    }

    internal static bool IsOwnTimeout(CancellationTokenSource timeoutSource, CancellationToken callerToken) =>
        timeoutSource.IsCancellationRequested && !callerToken.IsCancellationRequested;

    internal static TimeoutException Exceeded(string client, TimeSpan bound, Exception inner) =>
        new(
            $"The {client} provider call, including its concurrency-slot wait, did not finish within "
            + $"{bound.TotalSeconds:0.###} s.",
            inner);

    /// <summary>Largest configured request timeout, in whole seconds, that fits an activity allowance.</summary>
    internal static int MaxRequestTimeoutSeconds(TimeSpan activityAllowance) =>
        (int)Math.Floor((activityAllowance - CompletionMargin).TotalSeconds);
}

internal sealed class BoundedChatClient(
    IChatClient inner,
    TimeSpan timeout,
    int maxConcurrency,
    TimeProvider? timeProvider = null,
    TimeZoneInfo? hotelTimeZone = null) : DelegatingChatClient(inner)
{
    private readonly SemaphoreSlim gate = new(maxConcurrency, maxConcurrency);

    /// <inheritdoc/>
    /// <remarks>
    /// Waits for a concurrency slot and applies the bound (see <see cref="ActivityCallBudget"/>) to the
    /// wait and the call; expiry surfaces as <see cref="TimeoutException"/>.
    /// </remarks>
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options = PrepareEndChatFunction(options);
        messages = AddHotelTimeContext(messages, options);
        var bound = ActivityCallBudget.Bound(timeout);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(bound);
        return await ActivityCallBudget.RunAsync("chat", bound, timeoutSource, cancellationToken, async () =>
        {
            await ConciergeAITelemetry.WaitForSlotAsync(gate, "chat", timeoutSource.Token);
            try
            {
                var response = await InnerClient.GetResponseAsync(messages, options, timeoutSource.Token);
                ModelProviderErrorException.ThrowIfError(response.Messages.SelectMany(message => message.Contents));
                return response;
            }
            finally
            {
                gate.Release();
            }
        });
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Holds a concurrency slot for the whole stream; the bound covers the slot wait and the entire
    /// enumeration, including response-body reads, and its expiry surfaces as <see cref="TimeoutException"/>.
    /// </remarks>
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        options = PrepareEndChatFunction(options);
        messages = AddHotelTimeContext(messages, options);
        var bound = ActivityCallBudget.Bound(timeout);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(bound);
        await ActivityCallBudget.RunAsync("chat", bound, timeoutSource, cancellationToken, async () =>
        {
            await ConciergeAITelemetry.WaitForSlotAsync(gate, "chat", timeoutSource.Token);
            return true;
        });
        try
        {
            await using var updates = InnerClient
                .GetStreamingResponseAsync(messages, options, timeoutSource.Token)
                .GetAsyncEnumerator(timeoutSource.Token);
            while (true)
            {
                try
                {
                    if (!await updates.MoveNextAsync())
                    {
                        break;
                    }
                }
                catch (OperationCanceledException exception)
                    when (ActivityCallBudget.IsOwnTimeout(timeoutSource, cancellationToken))
                {
                    throw ActivityCallBudget.Exceeded("chat", bound, exception);
                }

                // A provider fault can arrive as an error item in a successful stream; as a failure,
                // the durable activity's retry policy gets a chance at it.
                ModelProviderErrorException.ThrowIfError(updates.Current.Contents);
                yield return updates.Current;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Gives action turns the hotel-local clock so relative ride times ("tomorrow at 10 am") resolve
    /// to the right date and offset. Added here rather than in the workflow because wall-clock time
    /// is nondeterministic; the model activity result records whatever the model produced.
    /// Inserted after the static system prompts to keep the cacheable prefix stable.
    /// </summary>
    internal IEnumerable<ChatMessage> AddHotelTimeContext(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        if (timeProvider is null || hotelTimeZone is null
            || options?.Tools?.Any(tool => tool.Name == ConciergeToolDefinitions.ProposeRide) != true)
        {
            return messages;
        }

        var now = TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), hotelTimeZone);
        var context = new ChatMessage(
            ChatRole.System,
            string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"Hotel-local now: {now:dddd yyyy-MM-dd HH:mm} (UTC{now:zzz}). Resolve relative dates and times in hotel-local time and send pickupAt with that UTC offset."));
        var list = messages.ToList();
        var firstNonSystem = list.FindIndex(message => message.Role != ChatRole.System);
        list.Insert(firstNonSystem < 0 ? list.Count : firstNonSystem, context);
        return list;
    }

    /// <summary>
    /// Restores the voice-only function declaration after Temporal's durable activity serializer
    /// carried its marker in AdditionalProperties. The function is exposed to the model only; the
    /// activity does not execute it, so the workflow can return the client-side call to Deepgram.
    /// </summary>
    internal static ChatOptions? PrepareEndChatFunction(ChatOptions? options)
    {
        if (options?.AdditionalProperties is not { } properties)
        {
            return options;
        }

        // Normally taken by AnswerStreamPublishingChatClient; never forwarded to the provider.
        properties.Remove(ConciergeAnswerStream.ModelOptionKey);
        var includeEndChat = properties.Remove(ConciergeEndChat.ModelOptionKey, out var endChatMarker)
            && IsEndChatFunctionMarker(endChatMarker);
        var includeActionTools = properties.Remove(ConciergeToolDefinitions.ModelOptionKey, out var toolsMarker)
            && IsEndChatFunctionMarker(toolsMarker);
        if (!includeEndChat && !includeActionTools)
        {
            return options;
        }

        options.Tools = includeActionTools
            ? ConciergeToolDefinitions.CreateModelTools(includeEndChat).Cast<AITool>().ToList()
            :
            [
                AIFunctionFactory.Create(
                    (Func<Task>)(static () => Task.CompletedTask),
                    new AIFunctionFactoryOptions
                    {
                        Name = ConciergeEndChat.FunctionName,
                        Description = "End the guest's chat only when they clearly and explicitly ask to end the conversation.",
                    }),
            ];
        return options;
    }

    private static bool IsEndChatFunctionMarker(object? marker) =>
        marker switch
        {
            string value => StringComparer.Ordinal.Equals(value, "true"),
            JsonElement { ValueKind: JsonValueKind.String } value =>
                StringComparer.Ordinal.Equals(value.GetString(), "true"),
            _ => false,
        };
}

/// <summary>
/// Bounds embedding calls with a per-request timeout and a concurrency limit. Retrieval uses the
/// short <see cref="AIProviderOptions.RequestTimeoutSeconds"/>; ingestion uses <see cref="Ingestion"/>,
/// a view with the longer ingestion timeout that shares the same concurrency gate and provider.
/// </summary>
internal sealed class BoundedEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> inner;
    private readonly TimeSpan timeout;
    private readonly SemaphoreSlim gate;
    private readonly bool ownsResources;

    public BoundedEmbeddingGenerator(
        IEmbeddingGenerator<string, Embedding<float>> inner,
        TimeSpan timeout,
        int maxConcurrency,
        TimeSpan? ingestionTimeout = null)
    {
        this.inner = inner;
        this.timeout = timeout;
        gate = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        ownsResources = true;
        Ingestion = new BoundedEmbeddingGenerator(inner, ingestionTimeout ?? timeout, gate);
    }

    private BoundedEmbeddingGenerator(
        IEmbeddingGenerator<string, Embedding<float>> inner,
        TimeSpan timeout,
        SemaphoreSlim gate)
    {
        this.inner = inner;
        this.timeout = timeout;
        this.gate = gate;
        ownsResources = false;
        Ingestion = this;
    }

    /// <summary>Same provider and gate, bounded by the ingestion timeout.</summary>
    public BoundedEmbeddingGenerator Ingestion { get; }

    /// <inheritdoc/>
    /// <remarks>
    /// Waits for a concurrency slot and applies the bound (see <see cref="ActivityCallBudget"/>) to the
    /// wait and the call; expiry surfaces as <see cref="TimeoutException"/>.
    /// </remarks>
    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var bound = ActivityCallBudget.Bound(timeout);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(bound);
        return await ActivityCallBudget.RunAsync("embeddings", bound, timeoutSource, cancellationToken, async () =>
        {
            await ConciergeAITelemetry.WaitForSlotAsync(gate, "embeddings", timeoutSource.Token);
            try
            {
                return await inner.GenerateAsync(values, options, timeoutSource.Token);
            }
            finally
            {
                gate.Release();
            }
        });
    }

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this)
            ? this
            : inner.GetService(serviceType, serviceKey);

    /// <summary>
    /// Disposes the concurrency gate and the wrapped generator; the ingestion view owns neither.
    /// </summary>
    public void Dispose()
    {
        if (!ownsResources)
        {
            return;
        }

        gate.Dispose();
        inner.Dispose();
    }
}

public static class EmbeddingGeneratorBoundExtensions
{
    /// <summary>
    /// Returns the generator bounded for knowledge ingestion (AI:Embeddings:IngestionRequestTimeoutSeconds),
    /// or <paramref name="generator"/> itself when it is not the configured bounded generator.
    /// </summary>
    public static IEmbeddingGenerator<string, Embedding<float>> ForIngestion(
        this IEmbeddingGenerator<string, Embedding<float>> generator) =>
        generator.GetService<BoundedEmbeddingGenerator>()?.Ingestion ?? generator;
}

/// <summary>
/// The provider answered with an error item instead of a reply, for example Azure's front end resetting
/// the upstream connection ("upstream connect error ... connection termination", 2026-11-06, ADR 0015).
/// Returned as content, the durable activity would succeed with no answer and the turn would fail as an
/// empty response; thrown, Temporal retries the call and exhausted retries get the workflow's
/// actionable model-failure reply.
/// </summary>
internal sealed class ModelProviderErrorException(string message) : Exception(message)
{
    private const int MaxDetailLength = 200;

    public static void ThrowIfError(IEnumerable<AIContent> contents)
    {
        if (contents.OfType<ErrorContent>().FirstOrDefault() is not { } error)
        {
            return;
        }

        var detail = string.IsNullOrWhiteSpace(error.Message) ? "(no message)" : error.Message.Trim();
        if (detail.Length > MaxDetailLength)
        {
            detail = detail[..MaxDetailLength];
        }

        throw new ModelProviderErrorException(
            $"The model provider returned an error instead of a reply{(error.ErrorCode is { Length: > 0 } code ? $" ({code})" : "")}: {detail}");
    }
}
