using Concierge.WorkflowServer;
using Concierge.WorkflowServer.Activities;
using Concierge.WorkflowServer.AI;
using Concierge.WorkflowServer.Data;
using Concierge.WorkflowServer.Health;
using Concierge.WorkflowServer.Location;
using Concierge.WorkflowServer.Setup;
using Concierge.WorkflowServer.Workflows;
using Concierge.Contracts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using TemporalCommunity.Extensions.AI;
using Temporalio.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddOptions<RideSchedulingOptions>()
    .Bind(builder.Configuration.GetSection(RideSchedulingOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddConciergeAIProviders(builder.Configuration);
builder.Services.AddSingleton<LocationOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<HotelLocationOptions>>(
    services => services.GetRequiredService<LocationOptionsValidator>());
builder.Services.AddSingleton<IValidateOptions<GeoapifyOptions>>(
    services => services.GetRequiredService<LocationOptionsValidator>());
builder.Services.AddOptions<HotelLocationOptions>()
    .Bind(builder.Configuration.GetSection(HotelLocationOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddOptions<GeoapifyOptions>()
    .Bind(builder.Configuration.GetSection(GeoapifyOptions.SectionName))
    .ValidateOnStart();
// Also registers the named location-provider client (no HTTP retries; Temporal owns them).
builder.Services.AddHotelLocation();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<GeoapifyPlaceResolver>();
builder.Services.AddSingleton<OpenMeteoWeatherService>();
builder.Services.AddSingleton<GeoapifyDrivingRouteService>();
builder.Services.AddSingleton<GeoapifyNearbyPlacesService>();
builder.Services.AddSingleton<GeoapifyPlaceOpeningHoursService>();
ConciergeActionToolService.AddRideServiceClient(builder.Services);
builder.Services.AddScoped<IConciergeActionToolBackend, ConciergeActionToolService>();

var conciergeConnectionString = builder.Configuration.GetConnectionString("concierge")
    ?? throw new InvalidOperationException(
        "Connection string 'concierge' is required. Configure it through Aspire or secure deployment configuration.");
builder.Services.AddSingleton(_ =>
{
    var dataSourceBuilder = new NpgsqlDataSourceBuilder(conciergeConnectionString);
    return dataSourceBuilder.Build();
});
builder.Services.AddSingleton<DatabaseMigrator>();
// Partial answer text for the web chat over Postgres LISTEN/NOTIFY (ADR 0015).
builder.Services.AddSingleton<AnswerStreamPublisher>();
builder.Services.AddHostedService(services => services.GetRequiredService<AnswerStreamPublisher>());
builder.Services.AddSingleton<EmbeddingModelIdentityStore>();
builder.Services.AddOptions<SetupOptions>()
    .Bind(builder.Configuration.GetSection(SetupOptions.SectionName));
builder.Services.AddSingleton<ConciergeSessionService>();
// Malformed request bodies return a bare 400, never the developer exception page.
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);
builder.Services.AddSingleton<KnowledgeMarkdownCatalog>();
builder.Services.AddSingleton<SetupReadinessState>();

var connectOptions = TemporalWorkerConnection.Resolve();
builder.Services.AddTemporalClient(options =>
{
    TemporalWorkerConnection.ApplyTo(connectOptions, options);
    // The worker below uses this client, so it inherits the tracing interceptor.
    TemporalTelemetry.ApplyTo(options);
});
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource([.. TemporalTelemetry.ActivitySourceNames])
        .AddSource(DurableChatTelemetry.ActivitySourceName)
        .AddSource(ConciergeAITelemetry.MeaiSourceName)
        .AddSource("Npgsql"))
    .WithMetrics(metrics => metrics
        .AddMeter(TemporalTelemetry.MeterName)
        .AddMeter(DurableChatTelemetry.MeterName)
        .AddMeter(ConciergeAITelemetry.MeaiSourceName)
        .AddMeter("Npgsql"));

builder.Services.AddHostedTemporalWorker(TemporalWorkerConnection.TaskQueue)
    .ConfigureOptions(options => options.Interceptors =
        [.. options.Interceptors ?? [], new ConciergeTurnDeadlineInterceptor()])
    .AddDurableAI(options =>
    {
        options.RegisterDefaultWorkflow = false;
        // Fallback only: the stay workflow sets a per-call timeout no longer than this.
        options.ActivityTimeout = ConciergeStayWorkflow.ModelActivityTimeout;
        // Model calls retry transient provider failures; Temporal owns these retries (the chat
        // SDK client is configured with zero retries). Tool invokers set their own policy:
        // read-only weather retries, while side-effecting ride actions stay single-attempt.
        options.RetryPolicy = new Temporalio.Common.RetryPolicy
        {
            MaximumAttempts = 3,
            InitialInterval = TimeSpan.FromSeconds(1),
            MaximumInterval = TimeSpan.FromSeconds(2),
        };
    })
    .AddDurableTools(ConciergeToolDefinitions.CreateActivityTools())
    .AddWorkflow<ConciergeStayWorkflow>()
    .AddWorkflow<HotelSetupWorkflow>()
    .AddScopedActivities<ConciergeTranscriptActivities>()
    .AddScopedActivities<ConciergeStayResetActivities>()
    .AddScopedActivities<HotelKnowledgeActivities>()
    // Session activities run as Standalone Activities started directly by Web.
    .AddScopedActivities<ConciergeSessionActivities>()
    .AddScopedActivities<SetupActivities>()
    .AddScopedActivities<WeatherActivities>()
    .AddScopedActivities<DrivingRouteActivities>();
builder.Services.AddHostedService<SetupWorkflowStarter>();
// Ollama only (no-op for Foundry): load the embedding model before the first guest question.
builder.Services.AddHostedService<OllamaEmbeddingWarmup>();

builder.Services.AddSingleton<IPostgreSqlReadinessProbe, PostgreSqlReadinessProbe>();
builder.Services.AddSingleton<ITemporalReadinessProbe, TemporalReadinessProbe>();
builder.Services.AddHealthChecks()
    .AddCheck<PostgreSqlHealthCheck>(
        "postgresql",
        tags: ["ready"],
        timeout: TimeSpan.FromSeconds(5))
    .AddCheck<TemporalHealthCheck>(
        "temporal",
        tags: ["ready"],
        timeout: TimeSpan.FromSeconds(5))
    .AddCheck<SetupHealthCheck>(
        "setup",
        tags: ["ready"]);

var app = builder.Build();

// Validate provider identity and security-sensitive configuration before changing schema
// or starting Temporal workers. Client construction remains lazy and makes no model request.
_ = app.Services.GetRequiredService<IOptions<ChatProviderOptions>>().Value;
_ = app.Services
    .GetRequiredService<IOptions<EmbeddingProviderOptions>>()
    .Value;
_ = app.Services.GetRequiredService<IOptions<HotelLocationOptions>>().Value;
_ = app.Services.GetRequiredService<IOptions<GeoapifyOptions>>().Value;

// Schema ownership stays in this process and completes before Temporal workers start.
await app.Services.GetRequiredService<DatabaseMigrator>()
    .MigrateAsync(app.Lifetime.ApplicationStopping);

await app.ResolveHotelLocationAsync();

app.MapDefaultEndpoints();
await app.RunAsync();
