using Concierge.AppHost;
using TemporalCommunity.Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithImage(PostgreSqlContainerImage.Repository, PostgreSqlContainerImage.Tag)
    // Aspire cannot infer PostgreSQL 18's data root from pgvector's image tag.
    .WithVolume("concierge-postgres-data", "/var/lib/postgresql");

var concierge = postgres.AddDatabase("concierge");
var rides = postgres.AddDatabase("rides");

var temporal = builder.AddTemporalDevContainer("temporal", options =>
{
    options.Namespace = "default";
    // Each ride-feed subscriber holds one Workflow Streams poll Update, and an abandoned poll (closed
    // tab, restarted Web) stays in flight until the stay continues-as-new. Temporal's default of 10
    // in-flight Updates per workflow refuses every turn once 10 accumulate. Workflow Streams targets
    // "tens of subscribers", so allow that; the stay's periodic continue-as-new bounds the rest.
    options.DynamicConfigValues.Add("history.maxInFlightUpdates=50");
})
.WithDataVolume("concierge-temporal-data");

// The string-value overload bypasses configuration, so preserve user-secrets explicitly.
var deepgramApiKey = builder.AddParameter(
    "deepgram-api-key", secret: true, value: builder.Configuration["Parameters:deepgram-api-key"] ?? "");
var geoapifyApiKey = builder.AddParameter(
    "geoapify-api-key", secret: true, value: builder.Configuration["Parameters:geoapify-api-key"] ?? "");

var workflowServer = builder.AddProject<Projects.Concierge_WorkflowServer>("workflow-server")
    .WithReference(temporal)
    .WithReference(concierge)
    .WithEnvironment("Geoapify__ApiKey", geoapifyApiKey)
    .WaitFor(temporal)
    .WaitFor(concierge)
    .WithHttpHealthCheck("/health/ready");

var rideService = builder.AddProject<Projects.Concierge_RideService>("ride-service")
    .WithReference(temporal)
    .WithReference(rides)
    .WaitFor(temporal)
    .WaitFor(rides)
    .WithHttpEndpoint(name: "http")
    .WithHttpHealthCheck("/health/ready");

workflowServer
    .WithReference(rideService)
    .WaitFor(rideService);

var hotelTimeZone = builder.Configuration["Hotel:TimeZoneId"];
if (string.IsNullOrWhiteSpace(hotelTimeZone))
{
    throw new InvalidOperationException(
        "AppHost setting Hotel:TimeZoneId is required in src/Concierge.AppHost/appsettings.json.");
}

workflowServer.WithEnvironment("Hotel__TimeZoneId", hotelTimeZone);
rideService.WithEnvironment("Hotel__TimeZoneId", hotelTimeZone);

// Forward one policy to both validators; absent configuration uses their shared default.
if (builder.Configuration["Rides:MinimumAdvanceNoticeMinutes"] is { } minimumNotice)
{
    workflowServer.WithEnvironment("Rides__MinimumAdvanceNoticeMinutes", minimumNotice);
    rideService.WithEnvironment("Rides__MinimumAdvanceNoticeMinutes", minimumNotice);
}

// The fictional hotel's real demo anchor; weather, routes, and ride pickups use it for "Hotel ZGY".
var hotelAddress = builder.Configuration["Hotel:Address"];
if (string.IsNullOrWhiteSpace(hotelAddress))
{
    throw new InvalidOperationException(
        "AppHost setting Hotel:Address is required in src/Concierge.AppHost/appsettings.json.");
}

workflowServer.WithEnvironment("Hotel__Address", hotelAddress);

ConfigureAIProvider(builder, workflowServer, "Chat", "foundry-chat-api-key");
ConfigureAIProvider(builder, workflowServer, "Embeddings", "foundry-embeddings-api-key");

var web = builder.AddProject<Projects.Concierge_Web>("web")
    .WithReference(temporal)
    // LISTEN only: partial answer text from the model activity (ADR 0015).
    .WithReference(concierge)
    .WithEnvironment("Deepgram__ApiKey", deepgramApiKey)
    .WaitFor(temporal)
    .WaitFor(workflowServer)
    .WithHttpsEndpoint(name: "think", env: "THINK_PORT")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health");

var thinkTunnel = builder.AddDevTunnel("think-tunnel")
    .WithReference(web.GetEndpoint("think"), allowAnonymous: true);

web.WithEnvironment(
    "THINK_PUBLIC_URL",
    thinkTunnel.GetEndpoint(web.GetEndpoint("think")));

// Opt-in: publishes the guest UI and /api/concierge/* for clients that cannot reach localhost,
// such as phones or hardware on another network. Guest verification (last name + room) is the
// only gate, so it stays off unless a demo needs it.
// The port goes on the think tunnel rather than a second tunnel. Aspire.Hosting.DevTunnels 13.6.4
// runs some devtunnel port commands without a tunnel ID, so the CLI applies them to its last-used
// tunnel; with two tunnels starting together, ports landed on the wrong one and Web never started.
if (bool.TryParse(builder.Configuration["RemoteAccess:PublicTunnel"], out var publicTunnel) && publicTunnel)
{
    thinkTunnel.WithReference(web.GetEndpoint("https"), allowAnonymous: true);
}

builder.Build().Run();

// The provider decides which resources exist, so it is read from configuration while the
// model is built instead of being a parameter, whose value is resolved only at run time.
static void ConfigureAIProvider(
    IDistributedApplicationBuilder builder,
    IResourceBuilder<ProjectResource> workflowServer,
    string capability,
    string apiKeyParameterName)
{
    var section = builder.Configuration.GetSection($"AI:{capability}");
    var provider = section["Provider"];
    var isFoundry = string.Equals(provider, "Foundry", StringComparison.OrdinalIgnoreCase);
    if (!isFoundry && !string.Equals(provider, "Ollama", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            $"AppHost setting AI:{capability}:Provider is '{provider}', but it must be 'Foundry' or 'Ollama'. "
            + "Set it in src/Concierge.AppHost/appsettings.json (or appsettings.Development.json) or with "
            + $"'aspire secret set \"AI:{capability}:Provider\" \"Foundry\" --apphost src/Concierge.AppHost'.");
    }

    foreach (var key in new[] { "Endpoint", "Model" })
    {
        if (string.IsNullOrWhiteSpace(section[key]))
        {
            var meaning = key == "Endpoint"
                ? "the AI service URL"
                : "the model name (Azure deployment name for Foundry)";
            throw new InvalidOperationException(
                $"AppHost setting AI:{capability}:{key} is required: set {meaning} in "
                + "src/Concierge.AppHost/appsettings.json (or appsettings.Development.json), "
                + $"AppHost user-secrets, or the AI__{capability}__{key} environment variable.");
        }
    }

    // Forward configured values, leaving absent optional settings to the worker's
    // existing options defaults. API keys stay on the secret-parameter path below.
    var forwardedKeys = new List<string>
    {
        "Provider", "Endpoint", "Model", "RequestTimeoutSeconds", "MaxConcurrentRequests",
        capability == "Chat" ? "ContextWindowTokens" : "Dimensions",
    };
    if (capability == "Embeddings")
    {
        forwardedKeys.Add("IngestionRequestTimeoutSeconds");
    }
    if (capability == "Chat")
    {
        forwardedKeys.Add("ReasoningEffort");
    }

    foreach (var key in forwardedKeys)
    {
        if (section[key] is { } value)
        {
            workflowServer.WithEnvironment($"AI__{capability}__{key}", value);
        }
    }

    if (isFoundry)
    {
        // An empty key makes the workflow server use DefaultAzureCredential, and the
        // explicit value keeps Aspire from prompting for it.
        var apiKey = builder.AddParameter(
            apiKeyParameterName,
            secret: true,
            value: builder.Configuration[$"Parameters:{apiKeyParameterName}"] ?? "");
        workflowServer.WithEnvironment($"AI__{capability}__ApiKey", apiKey);
    }
}
