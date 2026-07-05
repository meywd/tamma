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

// Story 36-2 — dimensional analytics projection seams. The margin/pricing
// config is the Story 36-7 seam; until 36-7 lands the Null impl yields a zero
// margin (billed == cost) + WARN so the rollup stays green. The metrics
// singleton is a self-registering meter exposing
// tamma.analytics.projection_lag_seconds (KekRotationMetrics precedent).
Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions
    .TryAddSingleton<Tamma.Activities.Analytics.IAnalyticsPricingConfig,
        Tamma.Activities.Analytics.NullAnalyticsPricingConfig>(builder.Services);
builder.Services.AddSingleton<Tamma.Activities.Analytics.DimensionalProjectionMetrics>();

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

    // Story 28-5 item #1 — bridge that dispatches DeleteTenantWorkflow from
    // TENANT.DELETE.REQUESTED rows (with the cooling-off + operator-cancel
    // checks). Mirrors the cleanup trigger above.
    builder.Services.AddOptions<Tamma.ElsaServer.Workflows.TenantDeleteRequestedTriggerOptions>()
        .Configure(opts =>
            builder.Configuration
                .GetSection(Tamma.ElsaServer.Workflows.TenantDeleteRequestedTriggerOptions.SectionName)
                .Bind(opts));
    builder.Services.AddHostedService<Tamma.ElsaServer.Workflows.TenantDeleteRequestedTrigger>();
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

// Story 38-1 (Epic 38) — the ADL git activities (CreateBranch /
// CreatePullRequest / MergePullRequest / UpdateIssueStatus / AnalyzeReview) NO
// LONGER resolve IGitHubIntegrationService or hold a GitHub token in the engine.
// A workflow step never calls GitHub over the wire: each thin activity POSTs to
// the internal /api/v1/git/{owner}/{repo}/... endpoints in Tamma.Api (via
// TammaApiClient), where the per-tenant token lives, the tenant↔repo guard runs,
// and the platform call + audit happen. So there is deliberately NO
// IGitHubIntegrationService registration and NO "github" HttpClient / GitHub:Token
// in the engine process (the highest-blast-radius rule-1 violation is closed).

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

// Story 32-5 (AC9) — the agentic tool-loop tool catalog (IToolExecutor* +
// IToolExecutorRegistry) was REMOVED from the engine. The loop now runs in
// Tamma.Api (where the request-scoped provider key is resolved), so the tool
// executors are registered there, not here.

// ─── Epic 19: Agent dispatch services (stories 19-2 / 3 / 4 / 5) ───────
//
// Story 38-2 (Class-C cutover): the engine NO LONGER resolves the co-hosted,
// credential-holding IGitHubActionsClient — the former NullGitHubActionsClient
// registration is REMOVED. The three phase services below are now thin
// TammaApiClient clients (registered at AddHttpClient<TammaApiClient> above);
// every workflow_dispatch / poll / collect goes over the wire to Tamma.Api's
// /api/v1/agent-dispatch endpoints, where the per-repo GitHub App installation
// token lives, the tenant↔repo guard runs, and the audit event is emitted.

// Real engine→API platform-events publisher. The tenant-lifecycle activities
// (TenantLifecycleActivity / CleanupStepActivity /
// EmitCleanupTerminalEventActivity) resolve IPlatformEventPublisher via
// GetRequiredService. EngineApiPlatformEventPublisher POSTs to
// POST /api/engine/platform-events (Task 3 of the engine→platform_events
// callback plan) so the 13 emitters that previously dropped events now
// land durably. On POST failure it degrades to WARN+null (never throws)
// so lifecycle workflows complete even when the callback is briefly
// unavailable. This is DISTINCT from the tenant domain_events drain, which
// flows through POST /api/engine/events + the event-persistence middleware.
// NullPlatformEventPublisher is kept below (documents the seam; may be used
// in tests).
Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions
    .TryAddSingleton<Tamma.Data.Abstractions.IPlatformEventPublisher,
        Tamma.Activities.TenantLifecycle.EngineApiPlatformEventPublisher>(builder.Services);

// Story 29-6 — engine-side rotation audit emitter. RotateSecretWorkflow runs
// HERE (the engine), and RotateSecretSagaActivity resolves
// IRotationAuditEmitter via GetRequiredService. The concrete RotationAuditEmitter
// lives in Tamma.Api (it forwards to IPlatformEventPublisher) and can't be
// referenced from the engine — so without this registration the resolve threw
// "No service for type IRotationAuditEmitter" and crashed the saga, losing the
// audit trail. DrainRotationAuditEmitter maps each rotation event to a
// TammaEvent on the workflow's tamma:events list (via the ambient
// RotationAuditDrainScope the saga opens) so the events ride the durable DCB
// drain (EventPersistenceMiddleware) → POST /api/engine/events → domain_events,
// matching the EventType + tag keys the Api-side emitter produces.
builder.Services.AddSingleton<Tamma.Activities.SecretsRotation.Contracts.IRotationAuditEmitter,
    Tamma.Activities.SecretsRotation.Activities.DrainRotationAuditEmitter>();

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

// Security services (Epic 11 — LLM injection hardening).
// Story 32-5 (AC9): IContentSanitizer was REMOVED from the engine — input-prompt
// sanitization runs SERVER-SIDE in ManagedAgent (Tamma.Api), where the tool loop
// now executes. IErrorRedactor is KEPT — RecordDiagnostics* and the tenant
// cleanup classifier (engine-resident) still redact error strings with it.
builder.Services.AddSingleton<IErrorRedactor, ErrorRedactor>();

// Provider allowlist (Story 11.5 — fail-closed guards)
builder.Services.Configure<ProviderAllowlistOptions>(
    builder.Configuration.GetSection("Security:ProviderAllowlist"));
builder.Services.AddSingleton<ProviderAllowlist>();

// Story 32-5 (AC9) — the engine holds NO LLM provider key.
//
// The 32-3 engine credential-resolver wiring was DELETED here: after the Epic-32
// pivot a workflow STEP never calls an external provider. Every LLM call routes
// through POST /api/v1/llm/call in Tamma.Api (via TammaApiClient.CallLlmAsync),
// which holds the request-scoped credential, gates, runs the agentic tool loop
// server-side, and meters. The CallLlm activities and the TDD/ADL/Debug/AI
// callers are thin clients over that endpoint.
//
// Consequently the provider-side collaborators that only ever fed the in-engine
// loop are NO LONGER registered in the engine — they live in the API process
// where the runner now executes: the content sanitizer, the tool-call validator
// + action gate, the tool-executor catalog + registry, the context compactor,
// and the tool-loop runner. (IErrorRedactor is KEPT — it is still used by
// RecordDiagnostics* and the tenant-lifecycle cleanup path. ProviderAllowlist is
// KEPT — LlmCallWorkflow still filters the provider chain through it; no key.)

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

// IMPORTANT-2 — in-process resume seam for the merge-approval human gate.
// Tamma.Api's tenant-scoped, RBAC-gated POST /api/adl/merge-approval/resume
// forwards here; this endpoint looks up the gate's tenant+repo-scoped named
// bookmark and runs the owning instance with the {decision,feedback,approver}
// payload injected as input.
//
// SECURITY C3 — this is an engine control surface (it can drive a real merge).
// It MUST NOT be drivable unauthenticated from the public internet. We require
// an authenticated caller: the only legitimate caller is the Tamma.Api→engine
// hop, which presents the Elsa admin API key (Authorization: ApiKey ...) that
// UseAdminApiKey() validates into an authenticated principal. Anonymous public
// callers fail .RequireAuthorization() with 401. The route is ALSO excluded
// from the public nginx /elsa/api/ block (internal hop only) — defense in depth.
// The C1/C2 tenant/repo constraints are enforced inside the handler via the
// bookmark name.
app.MapPost("/elsa/api/adl/merge-approval/resume",
    Tamma.ElsaServer.Endpoints.MergeApprovalResumeEndpoint.Resume)
    .RequireAuthorization();

// Completeness audit P0 item 3 — in-process resume seam for the deployment
// pipeline's production-approval human gate. Tamma.Api's tenant-scoped,
// RBAC-gated POST /api/adl/deploy-approval/resume forwards here; this endpoint
// looks up the gate's tenant+repo+SHA-scoped named bookmark and runs the owning
// instance with the {decision,feedback,approver} payload injected. Same
// engine-control-surface / RequireAuthorization rationale as the merge gate.
app.MapPost("/elsa/api/adl/deploy-approval/resume",
    Tamma.ElsaServer.Endpoints.DeploymentApprovalResumeEndpoint.Resume)
    .RequireAuthorization();

// Follow-up #15 — in-process resume seam for the blocker-diagnosis progressive
// resolution ladder. Tamma.Api's RBAC-gated, tenant-scoped POST /api/adl/blocker/resume
// forwards here (after verifying the caller's tenant OWNS the mentorship session);
// this endpoint looks up the session-scoped progress / escalation bookmark and runs the
// owning instance with the {ProgressDetected,...} / {Resolved,SeniorResponse} payload
// injected. It closes the "Resolved terminal is unreachable in production" gap called out
// in BlockerDiagnosisWorkflow. Same engine-control-surface / RequireAuthorization rationale
// as the merge/deploy gates.
app.MapPost("/elsa/api/adl/blocker/resume",
    Tamma.ElsaServer.Endpoints.BlockerResumeEndpoint.Resume)
    .RequireAuthorization();

// Story 3.5 — in-process resume seam for the clarifying-questions workflow's
// human-answer gate. Tamma.Api's RBAC-gated, tenant-scoped POST /api/adl/clarify/resume
// forwards here; this endpoint looks up the tenant+session-scoped
// clarify-answers-{tenant}-{session} bookmark and runs the owning instance with the
// {Answered, Answers} payload injected. Same engine-control-surface /
// RequireAuthorization rationale as the merge/deploy/blocker gates.
app.MapPost("/elsa/api/adl/clarify/resume",
    Tamma.ElsaServer.Endpoints.ClarifyResumeEndpoint.Resume)
    .RequireAuthorization();

// Story 3.7 — in-process resume seam for the design-proposal workflow's human review
// gate. Tamma.Api's RBAC-gated, tenant-scoped POST /api/adl/design/resume forwards here;
// this endpoint looks up the tenant+session-scoped design-approval-{tenant}-{session}
// bookmark and runs the owning instance with the {Approved, Feedback} payload injected
// (the gate branches Approved/Rejected off the flag). Same engine-control-surface /
// RequireAuthorization rationale as the merge/deploy/blocker/clarify gates.
app.MapPost("/elsa/api/adl/design/resume",
    Tamma.ElsaServer.Endpoints.DesignResumeEndpoint.Resume)
    .RequireAuthorization();

app.UseSerilogRequestLogging();

Log.Information("Tamma ELSA Server starting up...");

app.Run();
