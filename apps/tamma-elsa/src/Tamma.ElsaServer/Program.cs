using Elsa.Agents;
using Elsa.EntityFrameworkCore.Extensions;
using Elsa.EntityFrameworkCore.Modules.Management;
using Elsa.EntityFrameworkCore.Modules.Runtime;
using Elsa.Extensions;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Sinks.OpenSearch;
using Tamma.Activities.AI;
using Tamma.Activities.Core;
using Tamma.Activities.LlmCall.Credentials;
using Tamma.Activities.Security;
using Tamma.ElsaServer.Workflows;

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog with Console + File + OpenSearch sinks
var opensearchUrl = builder.Configuration["OpenSearch:Url"] ?? "http://opensearch:9200";
var opensearchEnabled = builder.Configuration.GetValue<bool>("OpenSearch:Enabled", true);
var logIndexPrefix = builder.Configuration["OpenSearch:IndexPrefix"] ?? "tamma-elsa";

var logConfig = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("service", "tamma-elsa")
    .Enrich.WithProperty("environment", builder.Environment.EnvironmentName)
    .Enrich.WithMachineName()
    .WriteTo.Console()
    .WriteTo.File("logs/tamma-elsa-.log", rollingInterval: RollingInterval.Day);

if (opensearchEnabled)
{
    logConfig.WriteTo.OpenSearch(new OpenSearchSinkOptions(new Uri(opensearchUrl))
    {
        AutoRegisterTemplate = false, // We manage templates externally via setup.sh
        IndexFormat = $"{logIndexPrefix}-{{0:yyyy.MM.dd}}",
        BatchAction = OpenOpType.Create,
        ModifyConnectionSettings = conn =>
            conn.ServerCertificateValidationCallback((_, _, _, _) => true),
        EmitEventFailure = EmitEventFailureHandling.WriteToSelfLog,
        FailureCallback = e => Console.Error.WriteLine(
            $"[Serilog-OpenSearch] Failed to submit: {e.MessageTemplate}"),
        BufferBaseFilename = "./logs/opensearch-buffer",
        BufferFileSizeLimitBytes = 50_000_000, // 50 MB buffer
        Period = TimeSpan.FromSeconds(2),
        BatchPostingLimit = 500,
    });
    Serilog.Debugging.SelfLog.Enable(Console.Error);
}

Log.Logger = logConfig.CreateLogger();

builder.Host.UseSerilog();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException(
        "Connection string 'DefaultConnection' is not configured.");

var signingKey = builder.Configuration["Elsa:Identity:SigningKey"]
    ?? throw new InvalidOperationException(
        "Elsa identity signing key is not configured. Set Elsa__Identity__SigningKey.");

// Add ELSA services
builder.Services.AddElsa(elsa =>
{
    // Identity & authentication
    elsa.UseIdentity(identity =>
    {
        identity.TokenOptions = options => options.SigningKey = signingKey;
        identity.UseAdminUserProvider();
    });
    elsa.UseDefaultAuthentication(auth => auth.UseAdminApiKey());

    // Workflow management — persists workflow definitions
    elsa.UseWorkflowManagement(management =>
        management.UseEntityFrameworkCore(ef =>
        {
            ef.UsePostgreSql(connectionString);
            ef.RunMigrations = true;
        }));

    // Workflow runtime — persists bookmarks, execution logs
    elsa.UseWorkflowRuntime(runtime =>
        runtime.UseEntityFrameworkCore(ef =>
        {
            ef.UsePostgreSql(connectionString);
            ef.RunMigrations = true;
        }));

    // Agents module — DB-backed agent config store with Studio UI and REST API.
    // Auto-creates AgentDefinitions, ApiKeysDefinitions, ServicesDefinitions tables.
    // We intentionally omit UseAgentActivities() to avoid registering Semantic Kernel's
    // AgentActivity — our llm-call workflow is the execution engine.
    elsa.UseAgentPersistence(p =>
        p.UseEntityFrameworkCore(ef => ef.UsePostgreSql(connectionString)));
    elsa.UseAgents();
    elsa.UseAgentsApi();

    // Scheduling (timer/cron activities)
    elsa.UseScheduling();

    // REST API for workflow CRUD
    elsa.UseWorkflowsApi();

    // HTTP trigger/response activities
    elsa.UseHttp(options =>
        options.ConfigureHttpOptions = httpOptions =>
            httpOptions.BaseUrl = new Uri(
                builder.Configuration["Elsa:Server:BaseUrl"] ?? "http://localhost:5000"));

    // Register all custom Tamma activities from the Activities assembly.
    // AddActivitiesFrom<T>() registers every [Activity]-marked type in T's
    // assembly, so ClaudeAnalysisActivity brings along the Analytics
    // (Story 28-10) activities too without an extra call.
    elsa.AddActivitiesFrom<ClaudeAnalysisActivity>();

    // Durable DCB-event persistence — drain the in-process tamma:events
    // transient list to POST /api/engine/events (-> tenant domain_events).
    // CRITICAL: this APPENDS the drain to the FULL Elsa default activity- and
    // workflow-execution pipelines (re-installing the activity invoker that
    // actually runs activities) instead of REPLACING the pipeline. The old
    // app.Services.ConfigureDefaultActivityExecutionPipeline(p => p.Use(...))
    // wiring called IActivityExecutionPipeline.Setup, which discarded the
    // invoker and turned every workflow into a silent no-op (it also mutated
    // only the root-scope pipeline, never the per-run scoped one). This call
    // runs from the AppFeature configurator, which Elsa invokes LAST — after
    // ElsaFeature's own WithDefaultActivityExecutionPipeline() — so it is the
    // authoritative final pipeline. See EventPersistencePipelineExtensions.
    elsa.UseWorkflows(workflows => workflows.UseTammaEventPersistence());

    // Register all code-first WorkflowBase subclasses from the ElsaServer
    // assembly. HourlyAnalyticsRollupWorkflow (Story 28-10) is picked up
    // by the same assembly sweep as LlmCallWorkflow.
    elsa.AddWorkflowsFrom<LlmCallWorkflow>();
});

// Story 28-10 — scheduler that fires HourlyAnalyticsRollupWorkflow on
// the configured cron offset (default: minute 5 of every hour, UTC).
// Lightweight in-process scheduler — preferred over an external cron
// dependency since the Elsa host is the only consumer.
builder.Services.AddOptions<Tamma.ElsaServer.Workflows.HourlyAnalyticsRollupSchedulerOptions>()
    .Configure(opts =>
        builder.Configuration
            .GetSection(Tamma.ElsaServer.Workflows.HourlyAnalyticsRollupSchedulerOptions.SectionName)
            .Bind(opts));
Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions
    .TryAddSingleton<TimeProvider>(builder.Services, _ => TimeProvider.System);
builder.Services.AddHostedService<Tamma.ElsaServer.Workflows.HourlyAnalyticsRollupScheduler>();

// Round-2 review M3 — bridge that polls platform_events for new
// TENANT.CLEANUP.REQUESTED rows and re-publishes the matching Elsa
// event so CleanUpFailedTenantWorkflow's starter trigger fires. The
// bridge needs a ControlPlaneDbContext to read the durable event log;
// if ConnectionStrings:ControlPlane is unset (dev / single-process
// composition) the registration short-circuits and the bridge logs a
// disabled message at startup. ConnectionStrings:DefaultConnection is
// the fallback so a single-DB dev composition still wires the bridge.
var cpConnection = builder.Configuration.GetConnectionString("ControlPlane")
    ?? builder.Configuration.GetConnectionString("DefaultConnection");
if (!string.IsNullOrWhiteSpace(cpConnection))
{
    builder.Services.AddDbContextFactory<Tamma.Data.ControlPlaneDbContext>(opts =>
        opts.UseNpgsql(cpConnection, npgsql =>
            // Must match ControlPlaneDesignTimeDbContextFactory and DependencyInjection.cs
            // (unified-tenancy Phase 0 reconciliation).
            npgsql.MigrationsHistoryTable("__ControlPlaneMigrationsHistory")));
    builder.Services.AddScoped(sp =>
        sp.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<
            Tamma.Data.ControlPlaneDbContext>>().CreateDbContext());

    builder.Services.AddOptions<Tamma.ElsaServer.Workflows.TenantCleanupRequestedTriggerOptions>()
        .Configure(opts =>
            builder.Configuration
                .GetSection(Tamma.ElsaServer.Workflows.TenantCleanupRequestedTriggerOptions.SectionName)
                .Bind(opts));
    builder.Services.AddHostedService<Tamma.ElsaServer.Workflows.TenantCleanupRequestedTrigger>();
}

// CORS for Tamma API and Dashboard
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins(
                builder.Configuration["Cors:ApiUrl"] ?? "http://localhost:3000",
                builder.Configuration["Cors:DashboardUrl"] ?? "http://localhost:3001",
                builder.Configuration["Cors:StudioUrl"] ?? "http://localhost:5000")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .WithExposedHeaders("x-elsa-workflow-instance-id"));
});

// HttpClientFactory — used by activities that call external APIs (e.g. UpdateCodeIndexActivity, CallLlmInlineActivity)
builder.Services.AddHttpClient();

// Story 9-11: Tamma API client — used by simplified activities to delegate
// agent config, health, diagnostics, and provider execution to the central
// Fastify/ASP.NET API plane.
builder.Services.AddHttpClient<Tamma.Activities.LlmCall.TammaApiClient>();

// Production blocker fix (post-Story 27-18) — Engine → API authentication for
// the resolve activities. ResolveConventionsActivity (Story 27-13) and
// ResolvePromptFromRegistryActivity (Story 27-18) POST to API endpoints gated
// by AuthenticatedAny / SettingsView; previously they used a plain
// CreateClient() with NO Authorization header, which 401'd in production
// (Dev mode silently passed via AllowAnonymousHandler, masking the issue).
//
// The DelegatingHandler reads Tamma:ApiToken (env: TAMMA_API_TOKEN) — the
// same key TammaApiClient already uses — and stamps Authorization: Bearer
// <token> on every outgoing request. Token absent → no-op (dev-friendly).
//
// Activities resolve this client via IHttpClientFactory.CreateClient("tamma-engine").
builder.Services.AddTransient<Tamma.Activities.LlmCall.TammaEngineAuthHandler>();
builder.Services
    .AddHttpClient("tamma-engine")
    .AddHttpMessageHandler<Tamma.Activities.LlmCall.TammaEngineAuthHandler>();

// Wave C.4 §4 — per-process health monitor for TammaApiClient. Singleton
// so the rolling 5-min window is shared across every call site. Fires
// PLATFORM.API.UNHEALTHY via IAlertEventEmitter when sustained failures
// cross threshold. Only wired if alerts are registered (Scoped
// IAlertEventEmitter resolved per-call via IServiceProvider).
builder.Services.AddSingleton<Tamma.Activities.LlmCall.TammaApiHealthMonitor>(sp =>
    new Tamma.Activities.LlmCall.TammaApiHealthMonitor(
        new Tamma.Activities.LlmCall.ScopedAlertEventEmitter(sp),
        sp.GetService<TimeProvider>()));

// Tool execution services — used by the agentic tool loop in CallLlmInlineActivity (Story 12.1)
// All tools are stateless singletons. The registry (also Singleton) captures them via
// IEnumerable<IToolExecutor>, so they must share the same lifetime to avoid a captive dependency.
builder.Services.AddSingleton<Tamma.Activities.LlmCall.Tools.IToolExecutor, Tamma.Activities.LlmCall.Tools.FileReadTool>();
builder.Services.AddSingleton<Tamma.Activities.LlmCall.Tools.IToolExecutor, Tamma.Activities.LlmCall.Tools.FileWriteTool>();
builder.Services.AddSingleton<Tamma.Activities.LlmCall.Tools.IToolExecutor, Tamma.Activities.LlmCall.Tools.SearchCodeTool>();
builder.Services.AddSingleton<Tamma.Activities.LlmCall.Tools.IToolExecutor, Tamma.Activities.LlmCall.Tools.ShellExecuteTool>();
builder.Services.AddSingleton<Tamma.Activities.LlmCall.Tools.IToolExecutor, Tamma.Activities.LlmCall.Tools.GitOperationsTool>();
builder.Services.AddSingleton<Tamma.Activities.LlmCall.Tools.IToolExecutor, Tamma.Activities.LlmCall.Tools.RunTestsTool>();
builder.Services.AddSingleton<Tamma.Activities.LlmCall.Tools.IToolExecutorRegistry, Tamma.Activities.LlmCall.Tools.ToolExecutorRegistry>();

// ─── Epic 19: Agent dispatch services (stories 19-2 / 3 / 4 / 5) ───────
//
// The ElsaServer process runs the Tamma workflows. It does NOT reference
// Tamma.Api, so the Octokit-backed IGitHubActionsClient isn't available
// here — the Null impl surfaces a clean operator error if workflows try
// to dispatch. In production, agent-dispatching workflows are hosted by
// the Tamma.Api process (which wires Octokit). This registration keeps
// the Elsa runtime self-consistent for the non-SaaS (CLI / local-only)
// deployments.
builder.Services.AddSingleton<Tamma.Activities.AgentDispatch.IGitHubActionsClient,
    Tamma.Activities.AgentDispatch.NullGitHubActionsClient>();

// Engine has no control-plane platform_events sink. The tenant-lifecycle
// activities (TenantLifecycleActivity / CleanupStepActivity /
// EmitCleanupTerminalEventActivity) resolve IPlatformEventPublisher via
// GetRequiredService — without a registration that THROWS in the engine and
// aborts CreateTenant/DeleteTenant/CleanUpFailedTenant workflows. The Null
// seam (mirrors NullGitHubActionsClient above) lets those workflows complete;
// the per-step platform telemetry is a best-effort no-op (logged at WARN)
// until a sibling POST /api/engine/platform-events callback lands (FOLLOW-UP).
// This is DISTINCT from the tenant domain_events drain, which now flows
// through POST /api/engine/events + the event-persistence middleware below.
Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions
    .TryAddSingleton<Tamma.Data.Abstractions.IPlatformEventPublisher,
        Tamma.Activities.TenantLifecycle.NullPlatformEventPublisher>(builder.Services);
builder.Services.AddScoped<Tamma.Activities.AgentDispatch.IAgentDispatchService,
    Tamma.Activities.AgentDispatch.AgentDispatchService>();
builder.Services.AddScoped<Tamma.Activities.AgentDispatch.IAgentMonitorService,
    Tamma.Activities.AgentDispatch.AgentMonitorService>();
builder.Services.AddScoped<Tamma.Activities.AgentDispatch.IAgentResultCollectorService,
    Tamma.Activities.AgentDispatch.AgentResultCollectorService>();
builder.Services.AddSingleton<Tamma.Activities.AgentDispatch.IProcessRunner,
    Tamma.Activities.AgentDispatch.DefaultProcessRunner>();
builder.Services.AddSingleton(_ =>
    Tamma.Activities.AgentDispatch.LocalExecutorOptions.FromConfiguration(builder.Configuration));
builder.Services.AddScoped<Tamma.Activities.AgentDispatch.LocalExecutor>();
builder.Services.AddScoped<Tamma.Activities.AgentDispatch.GitHubActionsExecutor>();
builder.Services.AddScoped<Tamma.Activities.AgentDispatch.AgentExecutorFactory>();

// Story 28-5 AC4 — optional pre-drop tenant backup (pg_dump). Disabled
// by default; the DeleteTenantWorkflow's BackupTenantDatabaseActivity
// reads this to decide whether to snapshot before DROP DATABASE.
builder.Services.AddOptions<Tamma.Activities.TenantLifecycle.TenantBackupOptions>()
    .Configure(opts =>
        builder.Configuration
            .GetSection(Tamma.Activities.TenantLifecycle.TenantBackupOptions.SectionName)
            .Bind(opts));

// Security services (Epic 11 — LLM injection hardening)
builder.Services.AddSingleton<IContentSanitizer, ContentSanitizer>();
builder.Services.AddSingleton<IErrorRedactor, ErrorRedactor>();

// Provider allowlist (Story 11.5 — fail-closed guards)
builder.Services.Configure<ProviderAllowlistOptions>(
    builder.Configuration.GetSection("Security:ProviderAllowlist"));
builder.Services.AddSingleton<ProviderAllowlist>();

// Story 32-3 — provider-credential resolution for CallLlmInlineActivity.
// CRITICAL: the activity executes in THIS (Elsa engine) process, which does
// NOT reference Tamma.Api, so the cabinet-backed DefaultProviderCredential
// Resolver (BYOK) is unreachable here. Without a resolver registered, the
// activity bound a null resolver and sent an EMPTY ApiKey — a hard regression
// to no-auth. AddEngineProviderCredentialResolution wires the config-backed
// platform-key resolver (ConfigPlatformProviderCredentialResolver) so the
// platform key from LlmProviders:<provider>:ApiKey (or the legacy
// <Provider>:ApiKey slot) flows through to the outbound call (AC12), and the
// resolver fails closed (never an empty key) when no key is configured.
// SaaS BYOK resolution stays owned by Tamma.Api's cabinet-backed resolver.
builder.Services.AddEngineProviderCredentialResolution();

// Tool call validation (Story 11.3 — allowlist enforcement, ActionGate)
builder.Services.Configure<ActionGateOptions>(
    builder.Configuration.GetSection("Security:ActionGate"));
builder.Services.AddSingleton<ActionGate>();
builder.Services.AddSingleton<IToolCallValidator, ToolCallValidator>();

// Context compaction for long-running tool loops (Story 12.3)
builder.Services.AddSingleton<Tamma.Activities.LlmCall.Tools.ContextCompactor>();

// Health checks
builder.Services.AddHealthChecks();

// Seed workflow definitions from JSON files at startup
builder.Services.AddHostedService<Tamma.ElsaServer.WorkflowSeeder>();

// Seed default agent definitions (prompts, settings) into ELSA Agents store
builder.Services.AddHostedService<Tamma.ElsaServer.AgentSeeder>();

var app = builder.Build();

// NB: the DCB-event drain is wired at AddElsa build time via
// elsa.UseWorkflows(w => w.UseTammaEventPersistence()) above — it must APPEND
// to the Elsa default activity/workflow pipelines, not replace them, or the
// activity invoker is dropped and every workflow becomes a no-op.

app.UseCors();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseWorkflowsApi();
app.UseWorkflows();
app.UseStaticFiles();
app.MapHealthChecks("/health");

app.UseSerilogRequestLogging();

Log.Information("Tamma ELSA Server starting up...");

app.Run();
