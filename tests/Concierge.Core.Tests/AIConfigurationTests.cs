using System.Diagnostics;
using System.Text.Json.Nodes;
using Concierge.Evaluation;
using Concierge.WorkflowServer.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Concierge.Core.Tests;

public sealed class AIConfigurationTests
{
    private static readonly string RepoRoot = FindRepositoryRoot();

    [Fact]
    public void EvaluationReusesProductionGroundingInstructionsAndAnswerCap()
    {
        var runner = File.ReadAllText(
            Path.Combine(RepoRoot, "tests/Concierge.Evaluation/runner/Program.cs"));
        Assert.Contains("ConciergeStayWorkflow.ConciergeInstructions", runner, StringComparison.Ordinal);
        Assert.Contains("MaxOutputTokens = ConciergeStayWorkflow.AnswerMaxOutputTokens",
            runner, StringComparison.Ordinal);
        Assert.DoesNotContain("const string SystemPrompt", runner, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkerSettingsDoNotOwnAIDefaults()
    {
        foreach (var name in new[] { "appsettings.json", "appsettings.Development.json" })
        {
            var settings = JsonNode.Parse(File.ReadAllText(
                Path.Combine(RepoRoot, "src/Concierge.WorkflowServer", name)))!;
            Assert.Null(settings["AI"]);
        }
    }

    [Fact]
    public void EvaluationLoadsCentralDevelopmentDefaultsAndAllowsOverrides()
    {
        var configuration = new ConfigurationBuilder().AddEvaluationDefaults(RepoRoot);
        Assert.IsType<EnvironmentVariablesConfigurationSource>(configuration.Sources.Last());
        // Keep this defaults assertion independent of the machine's AI overrides.
        configuration.Sources.RemoveAt(configuration.Sources.Count - 1);
        var defaults = configuration.Build();
        Assert.Equal("Ollama", defaults["AI:Chat:Provider"]);
        Assert.Equal("concierge-chat", defaults["AI:Chat:Model"]);
        Assert.Equal("embeddinggemma-2:740m", defaults["AI:Embeddings:Model"]);
        Assert.Equal("768", defaults["AI:Embeddings:Dimensions"]);
        Assert.Equal("32768", defaults["AI:Chat:ContextWindowTokens"]);
        Assert.Equal("low", defaults["AI:Chat:ReasoningEffort"]);
        var modelfile = File.ReadAllText(
            Path.Combine(RepoRoot, "models/ollama/Modelfile.concierge-chat"));
        Assert.Contains("FROM qwen3.8:27b-mlx", modelfile, StringComparison.Ordinal);
        Assert.Contains("PARAMETER num_ctx 32768", modelfile, StringComparison.Ordinal);
        // Each request timeout sits inside its Temporal activity attempt; cold model loads are
        // handled by keep_alive and warm-up, not by a long interactive timeout.
        Assert.Equal("18", defaults["AI:Chat:RequestTimeoutSeconds"]);
        Assert.Equal("8", defaults["AI:Embeddings:RequestTimeoutSeconds"]);
        foreach (var capability in new[] { "Chat", "Embeddings" })
        {
            Assert.Equal("2", defaults[$"AI:{capability}:MaxConcurrentRequests"]);
        }
        Assert.Equal("knowledge/hotel-zgy", defaults["Setup:KnowledgeDirectory"]);

        var overridden = configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AI:Chat:Model"] = "evaluation-model",
        }).Build();
        Assert.Equal("evaluation-model", overridden["AI:Chat:Model"]);
        Assert.Equal("768", overridden["AI:Embeddings:Dimensions"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("12")]
    public async Task ManifestForwardsOneRidePolicyToBothServices(string? minimumNotice)
    {
        var values = new Dictionary<string, string>
        {
            ["AI:Chat:Provider"] = "Ollama",
            ["AI:Chat:Endpoint"] = "http://localhost:11434",
            ["AI:Chat:Model"] = "test-chat",
            ["AI:Embeddings:Provider"] = "Ollama",
            ["AI:Embeddings:Endpoint"] = "http://localhost:11434",
            ["AI:Embeddings:Model"] = "test-embeddings",
        };
        if (minimumNotice is not null)
        {
            values[Concierge.Contracts.RideSchedulingOptions.MinimumAdvanceNoticeKey] = minimumNotice;
        }
        var (exitCode, manifest, output) = await PublishManifestAsync(values);
        Assert.True(exitCode == 0, output);
        foreach (var resource in new[] { "ride-service", "workflow-server" })
        {
            Assert.Equal(minimumNotice,
                manifest!["resources"]![resource]!["env"]!["Rides__MinimumAdvanceNoticeMinutes"]?.GetValue<string>());
        }
    }

    [Theory]
    [InlineData("Ollama", "Ollama")]
    [InlineData("Foundry", "Foundry")]
    [InlineData("Foundry", "Ollama")]
    [InlineData("Ollama", "Foundry")]
    [InlineData("oLlAmA", "fOuNdRy")]
    public async Task ManifestPassesAllCentralSettingsAndOnlyCreatesRequiredSecrets(
        string chatProvider, string embeddingProvider)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(RepoRoot, "src/Concierge.AppHost/appsettings.json"))
            .AddJsonFile(Path.Combine(RepoRoot, "src/Concierge.AppHost/appsettings.Development.json"))
            .Build();
        var values = configuration.AsEnumerable()
            .Where(pair => pair.Key.StartsWith("AI:", StringComparison.Ordinal) && pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value!);
        values["AI:Chat:Provider"] = chatProvider;
        values["AI:Embeddings:Provider"] = embeddingProvider;
        foreach (var capability in new[] { "Chat", "Embeddings" })
        {
            if (values[$"AI:{capability}:Provider"].Equals("Foundry", StringComparison.OrdinalIgnoreCase))
            {
                values[$"AI:{capability}:Endpoint"] = "https://example.openai.azure.com/";
                values[$"AI:{capability}:Model"] = $"{capability.ToLowerInvariant()}-deployment";
            }
        }

        var (exitCode, manifest, _) = await PublishManifestAsync(values);
        Assert.Equal(0, exitCode);
        var resources = manifest!["resources"]!;
        var environment = resources["workflow-server"]!["env"]!;
        // Coordinates are geocoded from Hotel__Address at workflow-server startup.
        Assert.Null(environment["Hotel__Latitude"]);
        Assert.Null(environment["Hotel__Longitude"]);
        Assert.Equal("America/New_York", environment["Hotel__TimeZoneId"]!.GetValue<string>());
        var rideEnvironment = resources["ride-service"]!["env"]!;
        Assert.Equal("America/New_York", rideEnvironment["Hotel__TimeZoneId"]!.GetValue<string>());
        Assert.Null(rideEnvironment["Hotel__Latitude"]);
        Assert.Null(rideEnvironment["Hotel__Address"]);
        Assert.Equal("2217 NW Miami Court, Miami, FL 33127", environment["Hotel__Address"]!.GetValue<string>());
        Assert.Equal("{geoapify-api-key.value}", environment["Geoapify__ApiKey"]!.GetValue<string>());
        Assert.NotNull(resources["geoapify-api-key"]);
        Assert.Null(resources["web"]!["env"]!["Geoapify__ApiKey"]);
        foreach (var (key, value) in values)
        {
            Assert.Equal(value, environment[key.Replace(":", "__")]!.GetValue<string>());
        }
        foreach (var capability in new[] { "Chat", "Embeddings" })
        {
            var parameter = $"foundry-{capability.ToLowerInvariant()}-api-key";
            if (values[$"AI:{capability}:Provider"].Equals("Foundry", StringComparison.OrdinalIgnoreCase))
            {
                Assert.NotNull(resources[parameter]);
                Assert.Equal($"{{{parameter}.value}}",
                    environment[$"AI__{capability}__ApiKey"]!.GetValue<string>());
            }
            else
            {
                Assert.Null(resources[parameter]);
                Assert.Null(environment[$"AI__{capability}__ApiKey"]);
            }
        }

        // Bind the actual manifest environment through the product's existing validators.
        using var services = new ServiceCollection().AddConciergeAIProviders(
            new ConfigurationBuilder().AddInMemoryCollection(
                environment.AsObject().Where(pair => pair.Key.StartsWith("AI__", StringComparison.Ordinal)
                    && !pair.Key.EndsWith("__ApiKey", StringComparison.Ordinal))
                    .Select(pair => new KeyValuePair<string, string?>(
                        pair.Key.Replace("__", ":"), pair.Value!.GetValue<string>()))).Build())
            .BuildServiceProvider();
        Assert.Equal(18, services.GetRequiredService<IOptions<ChatProviderOptions>>().Value.RequestTimeoutSeconds);
        Assert.Equal(768, services.GetRequiredService<IOptions<EmbeddingProviderOptions>>().Value.Dimensions);
    }

    [Theory]
    [InlineData("AI:Chat:Provider", "Unknown")]
    [InlineData("AI:Embeddings:Provider", "Unknown")]
    [InlineData("AI:Chat:Endpoint", "")]
    [InlineData("AI:Chat:Model", " ")]
    [InlineData("AI:Embeddings:Endpoint", "")]
    [InlineData("AI:Embeddings:Model", "")]
    public async Task MissingOrUnknownSettingsNameCentralConfigurationKey(string key, string value)
    {
        var values = new Dictionary<string, string>
        {
            ["AI:Chat:Endpoint"] = "https://example.openai.azure.com/",
            ["AI:Chat:Model"] = "chat-deployment",
            ["AI:Embeddings:Endpoint"] = "https://example.openai.azure.com/",
            ["AI:Embeddings:Model"] = "embedding-deployment",
            [key] = value,
        };
        var (exitCode, _, output) = await PublishManifestAsync(values);
        Assert.NotEqual(0, exitCode);
        Assert.Contains(key, output, StringComparison.Ordinal);
        Assert.Contains("src/Concierge.AppHost/appsettings.json", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonDevelopmentDefaultsRequireExplicitFoundryEndpoint()
    {
        var (exitCode, _, output) = await PublishManifestAsync([]);
        Assert.NotEqual(0, exitCode);
        Assert.Contains("AI:Chat:Endpoint", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManifestPassesConfiguredLimitsWithoutReplacingThemWithDefaults()
    {
        var values = new Dictionary<string, string>
        {
            ["AI:Chat:Endpoint"] = "https://example.openai.azure.com/",
            ["AI:Chat:Model"] = "chat-deployment",
            ["AI:Chat:RequestTimeoutSeconds"] = "91",
            ["AI:Chat:MaxConcurrentRequests"] = "7",
            ["AI:Chat:ContextWindowTokens"] = "49152",
            ["AI:Embeddings:Endpoint"] = "https://example.openai.azure.com/",
            ["AI:Embeddings:Model"] = "embedding-deployment",
            ["AI:Embeddings:Dimensions"] = "1024",
            ["AI:Embeddings:RequestTimeoutSeconds"] = "92",
            ["AI:Embeddings:MaxConcurrentRequests"] = "8",
        };
        var (exitCode, manifest, _) = await PublishManifestAsync(values);
        Assert.Equal(0, exitCode);
        var environment = manifest!["resources"]!["workflow-server"]!["env"]!;
        foreach (var (key, value) in values)
        {
            Assert.Equal(value, environment[key.Replace(":", "__")]!.GetValue<string>());
        }
        // Invalid dimensions remain visible to the worker's schema validator.
        using var services = new ServiceCollection().AddConciergeAIProviders(
            new ConfigurationBuilder().AddInMemoryCollection(
                values.Select(pair => new KeyValuePair<string, string?>(pair.Key, pair.Value))).Build())
            .BuildServiceProvider();
        var error = Assert.Throws<OptionsValidationException>(
            () => services.GetRequiredService<IOptions<EmbeddingProviderOptions>>().Value);
        Assert.Contains("AI:Embeddings:Dimensions", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StandaloneWorkerOnlyRequiresDeploymentSettingsAndDimensions()
    {
        using var services = new ServiceCollection().AddConciergeAIProviders(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AI:Chat:Endpoint"] = "https://example.openai.azure.com/",
                ["AI:Chat:Model"] = "chat-deployment",
                ["AI:Embeddings:Endpoint"] = "https://example.openai.azure.com/",
                ["AI:Embeddings:Model"] = "embedding-deployment",
                ["AI:Embeddings:Dimensions"] = "768",
            }).Build()).BuildServiceProvider();
        var chat = services.GetRequiredService<IOptions<ChatProviderOptions>>().Value;
        var embeddings = services.GetRequiredService<IOptions<EmbeddingProviderOptions>>().Value;
        Assert.Equal("Foundry", chat.Provider);
        Assert.Equal("Foundry", embeddings.Provider);
        Assert.Equal(18, chat.RequestTimeoutSeconds);
        Assert.Equal(8, embeddings.RequestTimeoutSeconds);
        Assert.Equal(4, chat.MaxConcurrentRequests);
        Assert.Equal(32768, chat.ContextWindowTokens);
        Assert.Null(chat.ApiKey);
        Assert.Null(embeddings.ApiKey);
    }

    private static async Task<(int ExitCode, JsonNode? Manifest, string Output)> PublishManifestAsync(
        Dictionary<string, string> values)
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"concierge-manifest-{Guid.NewGuid():N}.json");
        var appHostDirectory = Path.Combine(RepoRoot, "src/Concierge.AppHost");
        var testOutput = new DirectoryInfo(AppContext.BaseDirectory);
        var appHostAssembly = testOutput.Parent?.Name == "Concierge.Core.Tests"
            ? Path.Combine(testOutput.Parent.Parent!.FullName, "Concierge.AppHost",
                testOutput.Name, "Concierge.AppHost.dll")
            : Path.Combine(appHostDirectory, "bin", testOutput.Parent!.Name,
                testOutput.Name, "Concierge.AppHost.dll");
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = appHostDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var key in start.Environment.Keys.Where(key =>
            key.StartsWith("AI__", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("AI:", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("Rides__", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("Rides:", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("Parameters", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            start.Environment.Remove(key);
        }
        // No user-secrets, Docker, Dev Tunnel login, or running resources are needed.
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.ArgumentList.Add(appHostAssembly);
        foreach (var argument in new[] { "--publisher", "manifest", "--output-path", outputPath,
            "--Parameters:deepgram-api-key=", "--Parameters:foundry-chat-api-key=",
            "--Parameters:foundry-embeddings-api-key=" })
        {
            start.ArgumentList.Add(argument);
        }
        foreach (var (key, value) in values)
        {
            start.ArgumentList.Add($"--{key}={value}");
        }
        try
        {
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }
            return (process.ExitCode,
                File.Exists(outputPath) ? JsonNode.Parse(await File.ReadAllTextAsync(outputPath)) : null,
                await stdout + await stderr);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
            directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Concierge.slnx")))
            {
                return directory.FullName;
            }
        }
        throw new InvalidOperationException("Concierge.slnx not found above the test output.");
    }
}
