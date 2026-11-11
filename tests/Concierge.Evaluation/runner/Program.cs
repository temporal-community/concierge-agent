// Evaluation-only runner for the Hotel ZGY evaluation package.
// Not a product surface: it is not in Concierge.slnx, adds no endpoints/options, and
// exists only to reproduce the comparison runs recorded under results/.
//
// It ingests knowledge/hotel-zgy with the product setup workflow and real Ollama
// embeddings into a throwaway pgvector container, then runs lexical-only, vector-only,
// and combined (production RetrieveAsync) retrieval for every case, and generates one
// answer per case from the combined evidence using the exact production grounding prompt.

using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Concierge.AppHost;
using Concierge.Evaluation;
using Concierge.WorkflowServer.Activities;
using Concierge.WorkflowServer.AI;
using Concierge.WorkflowServer.Data;
using Concierge.WorkflowServer.Setup;
using Concierge.WorkflowServer.Workflows;
using DotNet.Testcontainers.Builders;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;
using Temporalio.Testing;
using Testcontainers.PostgreSql;

const string PackageRelativePath = "tests/Concierge.Evaluation/hotel-zgy-eval.json";
const string ResultsRelativeRoot = "tests/Concierge.Evaluation/results";
const string TaskQueue = "Concierge.Evaluation.HotelZgy";
const int FinalK = 5;

var repoRoot = FindRepositoryRoot();
var packagePath = Path.Combine(repoRoot, PackageRelativePath);
var packageBytes = File.ReadAllBytes(packagePath);
var package = JsonNode.Parse(packageBytes)!;
var startedUtc = DateTimeOffset.UtcNow;
var runDate = DateTime.Now.ToString("yyyy-MM-dd");
if (args.Length != 0
    && (args.Length != 2
        || args[0] != "--run-id"
        || args[1].Length == 0
        || args[1].Any(character =>
            !(character is >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '-'))))
{
    return Fail("Usage: --run-id <lowercase-letters-digits-hyphens>");
}

var runId = args.Length == 2 ? args[1] : null;

// 1. Corpus snapshot gate: every Markdown file under the root must match the recorded hashes.
var snapshot = package["corpusSnapshot"]!;
var corpusRoot = Path.Combine(repoRoot, snapshot["root"]!.GetValue<string>());
var snapshotFiles = snapshot["files"]!.AsArray().ToDictionary(
    file => file!["path"]!.GetValue<string>(),
    file => file!["sha256"]!.GetValue<string>(),
    StringComparer.Ordinal);
var actualFiles = Directory
    .EnumerateFiles(corpusRoot, "*.md", SearchOption.AllDirectories)
    .ToDictionary(
        path => Path.GetRelativePath(repoRoot, path).Replace('\\', '/'),
        path => Sha256(File.ReadAllBytes(path)),
        StringComparer.Ordinal);
var corpusMismatches = snapshotFiles.Keys.Union(actualFiles.Keys)
    .Where(path => !snapshotFiles.TryGetValue(path, out var expected)
        || !actualFiles.TryGetValue(path, out var actual)
        || expected != actual)
    .ToArray();
if (corpusMismatches.Length > 0)
{
    return Fail("Corpus does not match the recorded snapshot: " + string.Join(", ", corpusMismatches));
}

Console.WriteLine($"Corpus verified: {actualFiles.Count} files match the recorded snapshot.");

var outputDirectory = Path.Combine(
    repoRoot,
    ResultsRelativeRoot,
    runId is null ? runDate : $"{runDate}-{runId}");
var outputPath = Path.Combine(outputDirectory, "run.json");
if (File.Exists(outputPath))
{
    return Fail($"{outputPath} already exists; raw results are not overwritten.");
}

// 2. Fresh pgvector database (removed on exit) with the product migrations.
await using var container = new PostgreSqlBuilder(PostgreSqlContainerImage.FullName)
    .WithDatabase("concierge_eval")
    .WithUsername("postgres")
    .WithPassword("postgres")
    .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("pg_isready"))
    .Build();
await container.StartAsync();
await using var dataSource = NpgsqlDataSource.Create(container.GetConnectionString());
await new DatabaseMigrator(dataSource, NullLogger<DatabaseMigrator>.Instance).MigrateAsync();

// 3. Product AI provider registration with the central AppHost Development defaults.
await using var temporal = await WorkflowEnvironment.StartLocalAsync();
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    ContentRootPath = repoRoot,
    EnvironmentName = "Development",
});
builder.Configuration.Sources.Clear();
builder.Configuration.AddEvaluationDefaults(repoRoot);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.AddConciergeAIProviders(builder.Configuration);
builder.Services.Configure<SetupOptions>(builder.Configuration.GetSection(SetupOptions.SectionName));
builder.Services.AddSingleton(dataSource);
builder.Services.AddSingleton<ITemporalClient>(temporal.Client);
builder.Services.AddSingleton<EmbeddingModelIdentityStore>();
builder.Services.AddSingleton<KnowledgeMarkdownCatalog>();
builder.Services.AddHostedTemporalWorker(TaskQueue)
    .AddWorkflow<HotelSetupWorkflow>()
    .AddScopedActivities<SetupActivities>();
using var host = builder.Build();
await host.StartAsync();

var embeddingOptions = host.Services.GetRequiredService<IOptions<EmbeddingProviderOptions>>();
var chatOptions = host.Services.GetRequiredService<IOptions<ChatProviderOptions>>().Value;
var setupOptions = host.Services.GetRequiredService<IOptions<SetupOptions>>().Value;
var embeddingGenerator = host.Services
    .GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
var chatClient = host.Services.GetRequiredService<IChatClient>();
var embeddingIdentity = new EmbeddingModelIdentity(
    embeddingOptions.Value.Provider,
    embeddingOptions.Value.Model,
    embeddingOptions.Value.Dimensions);
// Only Ollama exposes these provenance APIs; never send them to Foundry.
JsonNode modelIdentities;
if (string.Equals(chatOptions.Provider, AIProviderNames.Ollama, StringComparison.OrdinalIgnoreCase)
    && string.Equals(embeddingOptions.Value.Provider, AIProviderNames.Ollama, StringComparison.OrdinalIgnoreCase)
    && chatOptions.Endpoint == embeddingOptions.Value.Endpoint)
{
    // Keep the existing provenance shape for the default single Ollama service.
    modelIdentities = await ReadOllamaIdentitiesAsync(
        chatOptions.Endpoint, [chatOptions.Model, embeddingOptions.Value.Model]);
}
else
{
    var identities = new JsonObject();
    foreach (var (capability, options) in new (string, AIProviderOptions)[]
    {
        ("chat", chatOptions), ("embeddings", embeddingOptions.Value),
    })
    {
        if (string.Equals(options.Provider, AIProviderNames.Ollama, StringComparison.OrdinalIgnoreCase))
        {
            identities[capability] = await ReadOllamaIdentitiesAsync(options.Endpoint, [options.Model]);
        }
    }
    modelIdentities = identities;
}

// 4. Ingest with the product setup workflow and activities.
var ingestionTimer = Stopwatch.StartNew();
var setupRequest = new SetupWorkflowRequest(
    Guid.NewGuid().ToString("N"),
    new SeedDemoDataRequest(
        setupOptions.OverwriteExistingSeedRecords,
        setupOptions.Guests,
        setupOptions.Stays),
    embeddingIdentity);
var setupHandle = await temporal.Client.StartWorkflowAsync(
    (HotelSetupWorkflow workflow) => workflow.RunAsync(setupRequest),
    new WorkflowOptions($"hotel-zgy-eval-setup-{Guid.NewGuid():N}", TaskQueue));
var setupOutcome = await setupHandle.GetResultAsync();
ingestionTimer.Stop();
Console.WriteLine(
    $"Ingested {setupOutcome.SourceCount} sources, {setupOutcome.SectionCount} sections, "
    + $"{setupOutcome.ChunkCount} chunks in {ingestionTimer.Elapsed.TotalSeconds:F1}s.");
var indexedSections = await ReadIndexedSectionsAsync(dataSource);
var factKeys = await ReadFactKeysAsync(dataSource);

// 5. Retrieval: lexical-only, vector-only, combined (production RetrieveAsync).
var activities = new HotelKnowledgeActivities(
    dataSource,
    new EmbeddingModelIdentityStore(dataSource),
    embeddingGenerator,
    embeddingOptions);
var cases = package["cases"]!.AsArray().Select(node => node!).ToArray();
var caseResults = new List<CaseResult>();
foreach (var evalCase in cases)
{
    var id = evalCase["id"]!.GetValue<string>();
    var question = evalCase["question"]!.GetValue<string>();
    var expectedKeys = evalCase["expectedEvidence"]!.AsArray()
        .Select(e => $"{e!["sourceId"]!.GetValue<string>()}#{e["sectionId"]!.GetValue<string>()}")
        .ToArray();

    var lexical = (await activities.SearchLexicallyAsync(question, CancellationToken.None))
        .Take(FinalK)
        .Select(chunk => ToPassage(chunk.Evidence, chunk.Rank))
        .ToArray();
    var queryVector = (await embeddingGenerator.GenerateAsync([question])).Single().Vector.ToArray();
    var vector = (await activities.SearchByVectorAsync(queryVector, CancellationToken.None))
        .Take(FinalK)
        .Select(chunk => ToPassage(chunk.Evidence, chunk.Rank))
        .ToArray();
    // A fresh context per case, started now with production's allowance: ActivityCallBudget derives
    // each provider call's bound from StartedTime + StartToCloseTimeout, so one environment shared
    // across the run left every later case with an expired 1 ms bound.
    var activityEnvironment = new ActivityEnvironment
    {
        Info = ActivityEnvironment.DefaultInfo with
        {
            StartedTime = DateTime.UtcNow,
            StartToCloseTimeout = ConciergeStayWorkflow.RetrievalActivityTimeout,
        },
    };
    var combinedResult = await activityEnvironment.RunAsync(
        () => activities.RetrieveAsync(new HotelRetrievalRequest(question)));
    var combined = combinedResult.Evidence
        .Select(evidence => ToPassage(evidence, evidence.FinalRank))
        .ToArray();

    caseResults.Add(new CaseResult(
        id,
        evalCase["category"]!.GetValue<string>(),
        question,
        evalCase["answerSupportLabel"]!.GetValue<string>(),
        expectedKeys,
        evalCase["expectedClaims"]!.AsArray().Select(c => c!.GetValue<string>()).ToArray(),
        new Dictionary<string, ArmResult>
        {
            ["lexical"] = Score(lexical, expectedKeys, null),
            ["vector"] = Score(vector, expectedKeys, null),
            ["combined"] = Score(combined, expectedKeys, combinedResult.Path.ToString()),
        },
        combinedResult.Path == HotelRetrievalPath.ExactFact,
        null));
}

// 6. Answers from the COMBINED evidence only (one call per case).
// Match the production answer cap and omitted temperature (shared provider adapters
// apply reasoning effort). Cases are independent turns without stay history.
var generationOptions = new ChatOptions
{
    MaxOutputTokens = ConciergeStayWorkflow.AnswerMaxOutputTokens,
};
for (var i = 0; i < caseResults.Count; i++)
{
    var result = caseResults[i];
    var evidencePrompt = BuildEvidencePrompt(result.Arms["combined"].Passages);
    var timer = Stopwatch.StartNew();
    var response = await chatClient.GetResponseAsync(
        [
            new ChatMessage(ChatRole.System, ConciergeStayWorkflow.ConciergeInstructions),
            new ChatMessage(ChatRole.System, evidencePrompt),
            new ChatMessage(ChatRole.User, result.Question),
        ],
        generationOptions);
    timer.Stop();
    var reasoning = string.Concat(response.Messages
        .SelectMany(message => message.Contents)
        .OfType<TextReasoningContent>()
        .Select(content => content.Text));
    caseResults[i] = result with
    {
        Answer = new AnswerResult(
            response.Text,
            string.IsNullOrEmpty(reasoning) ? null : reasoning,
            $"{evidencePrompt}\n\nGuest question: {result.Question}",
            response.ModelId,
            response.FinishReason?.ToString(),
            response.Usage?.InputTokenCount,
            response.Usage?.OutputTokenCount,
            timer.Elapsed.TotalSeconds),
    };
    Console.WriteLine($"{result.Id}: {response.Text.ReplaceLineEndings(" ")}");
}

await host.StopAsync();

// 7. Recall@5 (micro over expected evidence keys) per arm and category.
var answerable = caseResults.Where(c => c.ExpectedEvidenceKeys.Length > 0).ToArray();
var recall = new[] { "lexical", "vector", "combined" }.ToDictionary(
    arm => arm,
    arm => new
    {
        hits = answerable.Sum(c => c.Arms[arm].Hits.Length),
        denominator = answerable.Sum(c => c.ExpectedEvidenceKeys.Length),
        byCategory = answerable.GroupBy(c => c.Category).ToDictionary(
            g => g.Key,
            g => new
            {
                hits = g.Sum(c => c.Arms[arm].Hits.Length),
                denominator = g.Sum(c => c.ExpectedEvidenceKeys.Length),
            }),
    });

var run = new
{
    runKind = "evaluation-only comparison run (not a product surface)",
    package = new
    {
        path = PackageRelativePath,
        sha256 = Sha256(packageBytes),
    },
    startedUtc,
    completedUtc = DateTimeOffset.UtcNow,
    corpus = new
    {
        root = snapshot["root"]!.GetValue<string>(),
        verifiedAgainstSnapshot = true,
        files = actualFiles.OrderBy(f => f.Key).Select(f => new { path = f.Key, sha256 = f.Value }),
    },
    infrastructure = new
    {
        postgresImage = PostgreSqlContainerImage.FullName,
        temporal = "Temporalio.Testing.WorkflowEnvironment.StartLocalAsync (ephemeral dev server)",
        combinedInvocation = "HotelKnowledgeActivities.RetrieveAsync via Temporalio.Testing.ActivityEnvironment",
    },
    models = new
    {
        embedding = new
        {
            embeddingIdentity.Provider,
            embeddingIdentity.Model,
            embeddingIdentity.Dimensions,
            embeddingOptions.Value.Endpoint,
        },
        chat = new { chatOptions.Provider, chatOptions.Model, chatOptions.Endpoint },
        ollama = modelIdentities,
    },
    ingestion = new
    {
        setupOutcome,
        seconds = ingestionTimer.Elapsed.TotalSeconds,
        indexedSections,
        factKeys,
    },
    retrievalPolicy = new
    {
        finalK = FinalK,
        singleArmUnit = "first 5 ranked chunks of the arm's 50-candidate list, deduplicated to evidence keys",
        combinedUnit = "production RetrieveAsync result (exact-fact path or RRF k=60 top 5 chunks), deduplicated to evidence keys",
        recallAggregation = package["retrievalPolicy"]!["recallAggregation"]!.GetValue<string>(),
    },
    generation = new
    {
        systemPrompt = ConciergeStayWorkflow.ConciergeInstructions,
        promptStatus = "production ConciergeStayWorkflow.ConciergeInstructions revised after evidence on 2026-10-14; not the original evaluation fixture or its rule-3 revision",
        messageLayout = "production layout: system instructions, system current evidence, user question; independent cases with no continuity summary or prior turns",
        options = new
        {
            temperature = generationOptions.Temperature,
            maxOutputTokens = generationOptions.MaxOutputTokens,
            reasoningEffort = chatOptions.ReasoningEffort,
        },
        evidenceSource = "combined",
    },
    recall,
    cases = caseResults,
};

Directory.CreateDirectory(outputDirectory);
await File.WriteAllTextAsync(
    outputPath,
    JsonSerializer.Serialize(run, new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    }));

foreach (var (arm, value) in recall)
{
    Console.WriteLine($"Recall@5 {arm}: {value.hits}/{value.denominator}");
}

Console.WriteLine($"Wrote {Path.GetRelativePath(repoRoot, outputPath)}");
return 0;

static int Fail(string message)
{
    Console.Error.WriteLine("ABORT: " + message);
    return 2;
}

static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

static string FindRepositoryRoot()
{
    for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        directory is not null;
        directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "Concierge.slnx")))
        {
            return directory.FullName;
        }
    }

    throw new InvalidOperationException("Run from inside the repository (Concierge.slnx not found).");
}

static Passage ToPassage(HotelRetrievalEvidence evidence, int rank) => new(
    rank,
    evidence.EvidenceKey,
    evidence.ChunkId,
    evidence.SourceTitle,
    evidence.ChunkTitle,
    evidence.LexicalRank,
    evidence.VectorRank,
    evidence.FusedScore,
    evidence.Passage);

static ArmResult Score(Passage[] passages, string[] expectedKeys, string? path)
{
    var keys = passages.Select(p => p.EvidenceKey).Distinct(StringComparer.Ordinal).ToArray();
    return new ArmResult(
        path,
        passages,
        keys,
        expectedKeys.Where(keys.Contains).ToArray(),
        expectedKeys.Where(key => !keys.Contains(key)).ToArray());
}

static string BuildEvidencePrompt(IReadOnlyList<Passage> passages)
{
    var evidence = passages.Count > 0
        ? string.Join("\n\n", passages.Select(passage => passage.Text))
        : "No current hotel evidence was found for this question.";
    return $"Current Hotel ZGY evidence for this question:\n{evidence}";
}

static async Task<JsonNode> ReadOllamaIdentitiesAsync(string endpoint, string[] models)
{
    using var http = new HttpClient { BaseAddress = new Uri(endpoint) };
    var version = await http.GetFromJsonAsync<JsonNode>("api/version");
    var tags = await http.GetFromJsonAsync<JsonNode>("api/tags");
    var shown = new JsonObject();
    foreach (var model in models.Distinct())
    {
        var response = await http.PostAsJsonAsync("api/show", new { model });
        var show = (await response.Content.ReadFromJsonAsync<JsonNode>())!;
        shown[model] = new JsonObject
        {
            ["details"] = show["details"]?.DeepClone(),
            ["capabilities"] = show["capabilities"]?.DeepClone(),
            ["modified_at"] = show["modified_at"]?.DeepClone(),
        };
    }

    return new JsonObject
    {
        ["version"] = version?["version"]?.DeepClone(),
        ["show"] = shown,
        ["tags"] = new JsonArray(tags!["models"]!.AsArray()
            .Where(m => models.Contains(m!["name"]!.GetValue<string>()))
            .Select(m => (JsonNode)new JsonObject
            {
                ["name"] = m!["name"]!.DeepClone(),
                ["digest"] = m["digest"]!.DeepClone(),
                ["runner"] = m["details"]?["runner"]?.DeepClone(),
                ["family"] = m["details"]?["family"]?.DeepClone(),
            })
            .ToArray()),
    };
}

static async Task<object[]> ReadIndexedSectionsAsync(NpgsqlDataSource dataSource)
{
    await using var command = dataSource.CreateCommand(
        """
        SELECT c.section_anchor, count(*) AS chunks, bool_and(c.embedding IS NOT NULL) AS embedded
        FROM knowledge_chunks c
        JOIN ingestion_versions v
          ON v.ingestion_version_id = c.ingestion_version_id AND v.status = 'completed'
        GROUP BY c.section_anchor
        ORDER BY c.section_anchor;
        """);
    var rows = new List<object>();
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        rows.Add(new
        {
            sectionAnchor = reader.IsDBNull(0) ? null : reader.GetString(0),
            chunks = reader.GetInt64(1),
            embedded = reader.GetBoolean(2),
        });
    }

    return [.. rows];
}

static async Task<string[]> ReadFactKeysAsync(NpgsqlDataSource dataSource)
{
    await using var command = dataSource.CreateCommand(
        "SELECT fact_key FROM hotel_facts ORDER BY fact_key;");
    var keys = new List<string>();
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        keys.Add(reader.GetString(0));
    }

    return [.. keys];
}

internal sealed record Passage(
    int Rank,
    string EvidenceKey,
    string ChunkId,
    string SourceTitle,
    string ChunkTitle,
    int? LexicalRank,
    int? VectorRank,
    double? FusedScore,
    string Text);

internal sealed record ArmResult(
    string? Path,
    Passage[] Passages,
    string[] EvidenceKeys,
    string[] Hits,
    string[] Misses);

internal sealed record AnswerResult(
    string Text,
    string? Reasoning,
    string UserPrompt,
    string? ResponseModelId,
    string? FinishReason,
    long? InputTokens,
    long? OutputTokens,
    double Seconds);

internal sealed record CaseResult(
    string Id,
    string Category,
    string Question,
    string AnswerSupportLabel,
    string[] ExpectedEvidenceKeys,
    string[] ExpectedClaims,
    Dictionary<string, ArmResult> Arms,
    bool ExactFactPathFired,
    AnswerResult? Answer);
