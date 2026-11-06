using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics;
using System.Net;
using System.Text;
using Concierge.WorkflowServer.AI;
using Concierge.WorkflowServer.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Temporalio.Testing;

namespace Concierge.Core.Tests;

/// <summary>
/// Proves retry ownership and timeout layering through the configured provider clients:
/// Temporal owns retries, and the bounded clients finish inside the enclosing activity attempt.
/// </summary>
public sealed class ModelCallBoundTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(AIProviderNames.Foundry, HttpStatusCode.InternalServerError)]
    [InlineData(AIProviderNames.Foundry, HttpStatusCode.ServiceUnavailable)]
    [InlineData(AIProviderNames.Foundry, HttpStatusCode.TooManyRequests)]
    [InlineData(AIProviderNames.Ollama, HttpStatusCode.InternalServerError)]
    [InlineData(AIProviderNames.Ollama, HttpStatusCode.ServiceUnavailable)]
    public async Task ChatClientSendsOneRequestPerCallBecauseTemporalOwnsRetries(
        string provider,
        HttpStatusCode status)
    {
        var handler = new ScriptedHandler(_ => Respond(status));
        var client = CreateChat(provider, handler);

        await Assert.ThrowsAsync<ClientResultException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hello")]));

        Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task FoundryEmbeddingClientSendsOneRequestPerCallBecauseTemporalOwnsRetries(
        HttpStatusCode status)
    {
        var handler = new ScriptedHandler(_ => Respond(status));
        using var generator = AIProviderServiceCollectionExtensions.CreateEmbeddingGenerator(
            EmbeddingOptions(AIProviderNames.Foundry),
            foundryTransport: new HttpClientPipelineTransport(new HttpClient(handler)));

        await Assert.ThrowsAsync<ClientResultException>(() => generator.GenerateAsync(["question"]));

        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task OllamaEmbeddingClientSendsOneRequestPerCall()
    {
        var handler = new ScriptedHandler(_ => Respond(HttpStatusCode.ServiceUnavailable));
        using var generator = AIProviderServiceCollectionExtensions.CreateEmbeddingGenerator(
            EmbeddingOptions(AIProviderNames.Ollama), handler);

        await Assert.ThrowsAnyAsync<Exception>(() => generator.GenerateAsync(["question"]));

        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task HangingChatRequestEndsAtTheInnerBoundAndCancelsTheHttpRequest()
    {
        var handler = new ScriptedHandler(_ => null);
        using var client = Bounded(CreateChat(AIProviderNames.Foundry, handler), TimeSpan.FromMilliseconds(300), 1);
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hello")]));

        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(250), Generous);
        Assert.True(await handler.CanceledRequest.Task.WaitAsync(Generous));
    }

    [Fact]
    public async Task HangingStreamingBodyEndsAtTheInnerBound()
    {
        // The durable chat activity streams; a provider that sends headers and then stalls the
        // body must still be cut off by the bound.
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StallingStream()),
        });
        using var client = Bounded(CreateChat(AIProviderNames.Ollama, handler), TimeSpan.FromMilliseconds(300), 1);
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Hello")]))
            {
            }
        });

        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(250), Generous);
    }

    [Fact]
    public async Task HangingEmbeddingRequestEndsAtTheInnerBound()
    {
        var handler = new ScriptedHandler(_ => null);
        using var generator = new BoundedEmbeddingGenerator(
            AIProviderServiceCollectionExtensions.CreateEmbeddingGenerator(
                EmbeddingOptions(AIProviderNames.Ollama), handler),
            TimeSpan.FromMilliseconds(300),
            1);
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(() => generator.GenerateAsync(["question"]));

        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(250), Generous);
        Assert.True(await handler.CanceledRequest.Task.WaitAsync(Generous));
    }

    [Fact]
    public async Task TimedOutChatCallReleasesItsOnlySlot()
    {
        var handler = new ScriptedHandler(request => request == 1 ? null : ResponsesOk());
        using var client = Bounded(CreateChat(AIProviderNames.Foundry, handler), TimeSpan.FromMilliseconds(300), 1);

        await Assert.ThrowsAsync<TimeoutException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "first")]));
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "second")])
            .WaitAsync(Generous);

        Assert.Equal("ok", response.Text);
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task SlotWaitCountsAgainstTheBound()
    {
        var handler = new ScriptedHandler(_ => null);
        using var client = Bounded(CreateChat(AIProviderNames.Foundry, handler), TimeSpan.FromSeconds(3), 1);
        var holder = client.GetResponseAsync([new ChatMessage(ChatRole.User, "holds the slot")]);
        await handler.FirstRequest.Task.WaitAsync(Generous);
        // The waiter's attempt allowance (400 ms less a 100 ms margin) ends while the holder still
        // has the only slot.
        var environment = new ActivityEnvironment
        {
            Info = AttemptStartedNow(TimeSpan.FromMilliseconds(400)),
        };
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(() => environment.RunAsync(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "waits for the slot")])));
        var waited = stopwatch.Elapsed;
        await Assert.ThrowsAsync<TimeoutException>(() => holder);

        // The waiter timed out while queued: it never reached the provider.
        Assert.InRange(waited, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(2));
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task ShortActivityAllowanceShortensTheConfiguredBound()
    {
        // Near the turn deadline the stay workflow gives a model attempt less than its usual
        // allowance; the call must end inside that attempt, not at the configured 18 seconds.
        var handler = new ScriptedHandler(_ => null);
        using var client = Bounded(CreateChat(AIProviderNames.Foundry, handler), TimeSpan.FromSeconds(18), 1);
        var environment = new ActivityEnvironment
        {
            Info = AttemptStartedNow(TimeSpan.FromMilliseconds(800)),
        };
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(() => environment.RunAsync(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hello")])));

        // 800 ms allowance less a quarter margin = 600 ms.
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5));
        Assert.True(await handler.CanceledRequest.Task.WaitAsync(Generous));
    }

    [Fact]
    public async Task ActivityCancellationCancelsTheCallAndReleasesTheSlot()
    {
        var handler = new ScriptedHandler(request => request == 1 ? null : ResponsesOk());
        using var client = Bounded(CreateChat(AIProviderNames.Foundry, handler), TimeSpan.FromSeconds(18), 1);
        var environment = new ActivityEnvironment
        {
            Info = AttemptStartedNow(TimeSpan.FromSeconds(20)),
        };

        // The durable chat activity passes its activity cancellation token to the client.
        var call = environment.RunAsync(
            () => client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "first")],
                cancellationToken: Temporalio.Activities.ActivityExecutionContext.Current.CancellationToken));
        await handler.FirstRequest.Task.WaitAsync(Generous);
        environment.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(Generous));
        Assert.True(await handler.CanceledRequest.Task.WaitAsync(Generous));
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "second")])
            .WaitAsync(Generous);
        Assert.Equal("ok", response.Text);
    }

    [Theory]
    [InlineData(18, 20_000, 0, 18_000)]
    [InlineData(18, 6_000, 0, 4_500)]
    [InlineData(18, 2_000, 0, 1_500)]
    [InlineData(8, 10_000, 0, 8_000)]
    [InlineData(8, 60_000, 0, 8_000)]
    // Retrieval's database lookups before the embedding count against the attempt.
    [InlineData(8, 10_000, 3_000, 5_000)]
    [InlineData(18, 20_000, 1_000, 17_000)]
    // Past the usable allowance: fail fast.
    [InlineData(8, 10_000, 9_000, 1)]
    // Worker clock behind the server's StartedTime: never more than the allowance less margin.
    [InlineData(18, 6_000, -5_000, 4_500)]
    public void ActivityBudgetIsTheShorterOfConfiguredAndRemainingAttemptTime(
        int configuredSeconds,
        int allowanceMs,
        int elapsedMs,
        int expectedMs)
    {
        var started = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

        var bound = ActivityCallBudget.Bound(
            TimeSpan.FromSeconds(configuredSeconds),
            started,
            TimeSpan.FromMilliseconds(allowanceMs),
            started.AddMilliseconds(elapsedMs));

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), bound);
    }

    [Fact]
    public async Task ActivityBudgetUsesTheCurrentAttemptAndIsUnchangedOutsideActivities()
    {
        var environment = new ActivityEnvironment
        {
            Info = ActivityEnvironment.DefaultInfo with
            {
                StartedTime = DateTime.UtcNow.AddSeconds(-4),
                StartToCloseTimeout = TimeSpan.FromSeconds(10),
            },
        };

        var bound = await environment.RunAsync(() => ActivityCallBudget.Bound(TimeSpan.FromSeconds(8)));

        Assert.InRange(bound, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4));
        Assert.Equal(TimeSpan.FromSeconds(8), ActivityCallBudget.Bound(TimeSpan.FromSeconds(8)));
    }

    [Fact]
    public async Task IngestionUsesItsOwnLongerBoundAndSharesTheRetrievalGate()
    {
        var handler = new ScriptedHandler(_ => null);
        using var generator = new BoundedEmbeddingGenerator(
            AIProviderServiceCollectionExtensions.CreateEmbeddingGenerator(
                EmbeddingOptions(AIProviderNames.Ollama), handler),
            TimeSpan.FromMilliseconds(200),
            1,
            ingestionTimeout: TimeSpan.FromMilliseconds(1_500));
        IEmbeddingGenerator<string, Embedding<float>> configured = generator;
        var ingestion = configured.ForIngestion();
        var stopwatch = Stopwatch.StartNew();

        var section = ingestion.GenerateAsync(["a whole section"]);
        await handler.FirstRequest.Task.WaitAsync(Generous);
        // Retrieval queues behind the ingestion call on the shared slot and ends at its own bound.
        await Assert.ThrowsAsync<TimeoutException>(() => configured.GenerateAsync(["question"]));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(1_000));
        await Assert.ThrowsAsync<TimeoutException>(() => section);

        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(1_400), Generous);
        Assert.Equal(1, handler.Requests);
        Assert.NotSame(configured, ingestion);
    }

    [Fact]
    public void ForIngestionFallsBackToAnUnboundedGenerator()
    {
        using var services = new ServiceCollection()
            .AddConciergeAIProviders(new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["AI:Chat:Provider"] = AIProviderNames.Ollama,
                    ["AI:Chat:Endpoint"] = "http://127.0.0.1:11434/",
                    ["AI:Chat:Model"] = "chat-model",
                    ["AI:Embeddings:Provider"] = AIProviderNames.Ollama,
                    ["AI:Embeddings:Endpoint"] = "http://127.0.0.1:11434/",
                    ["AI:Embeddings:Model"] = "embedding-model",
                    ["AI:Embeddings:Dimensions"] = "768",
                }).Build())
            .BuildServiceProvider();
        var configured = services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
        var unbounded = AIProviderServiceCollectionExtensions.CreateEmbeddingGenerator(
            EmbeddingOptions(AIProviderNames.Ollama));

        Assert.IsType<BoundedEmbeddingGenerator>(configured.ForIngestion());
        Assert.NotSame(configured, configured.ForIngestion());
        Assert.Same(unbounded, unbounded.ForIngestion());
        Assert.Equal(60, services.GetRequiredService<IOptions<EmbeddingProviderOptions>>().Value.IngestionRequestTimeoutSeconds);
    }

    [Fact]
    public void RequestTimeoutCeilingsSitInsideTheirActivityAllowances()
    {
        Assert.Equal(18, ChatProviderOptions.MaxRequestTimeoutSeconds);
        Assert.Equal(8, EmbeddingProviderOptions.MaxRequestTimeoutSeconds);
        Assert.True(
            TimeSpan.FromSeconds(ChatProviderOptions.MaxRequestTimeoutSeconds)
            < ConciergeStayWorkflow.ModelActivityTimeout);
        Assert.True(
            TimeSpan.FromSeconds(EmbeddingProviderOptions.MaxRequestTimeoutSeconds)
            < ConciergeStayWorkflow.RetrievalActivityTimeout);
        Assert.Equal(18, new ChatProviderOptions().RequestTimeoutSeconds);
        Assert.Equal(8, new EmbeddingProviderOptions().RequestTimeoutSeconds);
        Assert.Equal(298, EmbeddingProviderOptions.MaxIngestionRequestTimeoutSeconds);
        Assert.True(
            TimeSpan.FromSeconds(EmbeddingProviderOptions.MaxIngestionRequestTimeoutSeconds)
            < HotelSetupWorkflow.SetupActivityTimeout);
    }

    [Theory]
    [InlineData("Chat", "18", true)]
    [InlineData("Chat", "19", false)]
    [InlineData("Chat", "180", false)]
    [InlineData("Embeddings", "8", true)]
    [InlineData("Embeddings", "9", false)]
    [InlineData("Embeddings", "60", false)]
    [InlineData("Embeddings:Ingestion", "298", true)]
    [InlineData("Embeddings:Ingestion", "299", false)]
    [InlineData("Embeddings:Ingestion", "0", false)]
    public void RequestTimeoutMustFinishInsideTheActivityAttempt(
        string capability,
        string seconds,
        bool valid)
    {
        var values = new Dictionary<string, string?>
        {
            ["AI:Chat:Provider"] = AIProviderNames.Ollama,
            ["AI:Chat:Endpoint"] = "http://127.0.0.1:11434/",
            ["AI:Chat:Model"] = "chat-model",
            ["AI:Embeddings:Provider"] = AIProviderNames.Ollama,
            ["AI:Embeddings:Endpoint"] = "http://127.0.0.1:11434/",
            ["AI:Embeddings:Model"] = "embedding-model",
            ["AI:Embeddings:Dimensions"] = "768",
        };
        var key = capability == "Embeddings:Ingestion"
            ? "AI:Embeddings:IngestionRequestTimeoutSeconds"
            : $"AI:{capability}:RequestTimeoutSeconds";
        values[key] = seconds;
        using var services = new ServiceCollection()
            .AddConciergeAIProviders(new ConfigurationBuilder().AddInMemoryCollection(values).Build())
            .BuildServiceProvider();

        Action resolve = capability == "Chat"
            ? () => _ = services.GetRequiredService<IOptions<ChatProviderOptions>>().Value
            : () => _ = services.GetRequiredService<IOptions<EmbeddingProviderOptions>>().Value;

        if (valid)
        {
            resolve();
        }
        else
        {
            var error = Assert.Throws<OptionsValidationException>(resolve);
            Assert.Contains($"{key} is {seconds}", error.Message, StringComparison.Ordinal);
        }
    }

    private static IChatClient CreateChat(string provider, HttpMessageHandler handler) =>
        AIProviderServiceCollectionExtensions.CreateChatClient(
            new ChatProviderOptions
            {
                Provider = provider,
                Endpoint = provider == AIProviderNames.Foundry
                    ? "https://concierge.openai.azure.com"
                    : "http://127.0.0.1:11434",
                Model = "chat-model",
                ApiKey = provider == AIProviderNames.Foundry ? "test-key" : null,
            },
            new HttpClientPipelineTransport(new HttpClient(handler)));

    private static Temporalio.Activities.ActivityInfo AttemptStartedNow(TimeSpan startToClose) =>
        ActivityEnvironment.DefaultInfo with { StartedTime = DateTime.UtcNow, StartToCloseTimeout = startToClose };

    [Fact]
    public async Task AnErrorItemFromTheProviderIsAFailedCallStreamingOrNot()
    {
        var error = new ErrorContent("upstream connect error or disconnect/reset before headers") { ErrorCode = "server_error" };
        using var client = Bounded(new ErrorItemClient(error), TimeSpan.FromSeconds(5), 1);

        var streamed = await Assert.ThrowsAsync<ModelProviderErrorException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Hello")]))
            {
            }
        });
        var whole = await Assert.ThrowsAsync<ModelProviderErrorException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hello")]));

        Assert.Equal("The model provider returned an error instead of a reply (server_error): upstream connect error or disconnect/reset before headers", streamed.Message);
        Assert.Equal(streamed.Message, whole.Message);
        // The slot was released both times: a third call still gets one.
        await Assert.ThrowsAsync<ModelProviderErrorException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hello")]));
    }

    private sealed class ErrorItemClient(ErrorContent error) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [error])));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, [error]);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private static BoundedChatClient Bounded(IChatClient inner, TimeSpan timeout, int maxConcurrency) =>
        new(inner, timeout, maxConcurrency);

    private static EmbeddingProviderOptions EmbeddingOptions(string provider) => new()
    {
        Provider = provider,
        Endpoint = provider == AIProviderNames.Foundry
            ? "https://concierge.openai.azure.com/"
            : "http://127.0.0.1:11434/",
        Model = "embedding-model",
        ApiKey = provider == AIProviderNames.Foundry ? "test-key" : null,
        Dimensions = 768,
    };

    private static HttpResponseMessage Respond(HttpStatusCode status) =>
        new(status)
        {
            Content = new StringContent(
                """{"error":{"message":"provider failure","type":"server_error"}}""",
                Encoding.UTF8,
                "application/json"),
        };

    private static HttpResponseMessage ResponsesOk() =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {"id":"resp_test","object":"response","created_at":1791936000,"status":"completed",
                 "model":"chat-model","output":[{"id":"msg_test","type":"message","status":"completed",
                 "role":"assistant","content":[{"type":"output_text","text":"ok","annotations":[]}]}],
                 "usage":{"input_tokens":1,"output_tokens":1,"total_tokens":2}}
                """,
                Encoding.UTF8,
                "application/json"),
        };

    /// <summary>
    /// Counts requests and returns the scripted response for each (1-based) request; a null
    /// response hangs until the request is canceled.
    /// </summary>
    private sealed class ScriptedHandler(Func<int, HttpResponseMessage?> script) : HttpMessageHandler
    {
        private int requests;

        public int Requests => Volatile.Read(ref requests);

        public TaskCompletionSource FirstRequest { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> CanceledRequest { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var number = Interlocked.Increment(ref requests);
            FirstRequest.TrySetResult();
            if (script(number) is { } response)
            {
                return response;
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CanceledRequest.TrySetResult(true);
                throw;
            }

            throw new UnreachableException();
        }
    }

    /// <summary>A response body that never produces data until its read is canceled.</summary>
    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("Synchronous reads would hang without cancellation.");

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
