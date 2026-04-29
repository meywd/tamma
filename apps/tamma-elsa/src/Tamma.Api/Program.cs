using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using Serilog.Sinks.OpenSearch;
using Tamma.Api.Auth;
using Tamma.Api.Endpoints;
using Tamma.Api.Extensions;
using Tamma.Api.Infrastructure;
using Tamma.Api.Middleware;
using Tamma.Api.Services;
using Tamma.Api.Services.Provisioning.V2;
using Tamma.Core.Interfaces;
using Tamma.Api.Services.PlatformTasks;
using Tamma.Data;
using Tamma.Data.Pooling;
using Tamma.Data.Repositories;
using Tamma.Platforms.Gitea;

var builder = WebApplication.CreateBuilder(args);

// ────────────────────────────────────────────────────────────────────────────
// Serilog
// ────────────────────────────────────────────────────────────────────────────
var opensearchUrl = builder.Configuration["OpenSearch:Url"] ?? "http://opensearch:9200";
var opensearchEnabled = builder.Configuration.GetValue<bool>("OpenSearch:Enabled", true);
var logIndexPrefix = builder.Configuration["OpenSearch:IndexPrefix"] ?? "tamma-api-dotnet";

var logConfig = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("service", "tamma-api-dotnet")
    .Enrich.WithProperty("environment", builder.Environment.EnvironmentName)
    .Enrich.WithMachineName()
    .WriteTo.Console()
    .WriteTo.File("logs/tamma-api-.log", rollingInterval: RollingInterval.Day);

if (opensearchEnabled)
{
    logConfig.WriteTo.OpenSearch(new OpenSearchSinkOptions(new Uri(opensearchUrl))
    {
        AutoRegisterTemplate = false,
        IndexFormat = $"{logIndexPrefix}-{{0:yyyy.MM.dd}}",
        BatchAction = OpenOpType.Create,
        ModifyConnectionSettings = conn =>
            conn.ServerCertificateValidationCallback((_, _, _, _) => true),
        EmitEventFailure = EmitEventFailureHandling.WriteToSelfLog,
        FailureCallback = e => Console.Error.WriteLine(
            $"[Serilog-OpenSearch] Failed to submit: {e.MessageTemplate}"),
        BufferBaseFilename = "./logs/opensearch-buffer",
        BufferFileSizeLimitBytes = 50_000_000,
        Period = TimeSpan.FromSeconds(2),
        BatchPostingLimit = 500,
    });
    Serilog.Debugging.SelfLog.Enable(Console.Error);
}

Log.Logger = logConfig.CreateLogger();
builder.Host.UseSerilog();

// ────────────────────────────────────────────────────────────────────────────
// Services
// ────────────────────────────────────────────────────────────────────────────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "Tamma API",
        Version = "v2",
        Description = "REST API for Tamma Autonomous Development Platform"
    });
});

// ────────────────────────────────────────────────────────────────────────────
// JSON wire format — explicit lock on camelCase to prevent silent regressions
// (port-gap audit prompts/013). ASP.NET Core's JsonSerializerDefaults.Web
// already enables CamelCase by default, but configuring it explicitly here
// guarantees the contract survives any future re-binding of JsonOptions and
// is documented at the composition root.
// ────────────────────────────────────────────────────────────────────────────
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
    options.SerializerOptions.DictionaryKeyPolicy = null; // preserve dict keys verbatim (role names, action names)
});

// HTTP clients
builder.Services.AddHttpClient();
builder.Services.AddHttpClient("elsa", client =>
{
    var elsaUrl = builder.Configuration["Elsa:ServerUrl"] ?? "http://localhost:5000";
    client.BaseAddress = new Uri(elsaUrl);
    var elsaApiKey = builder.Configuration["Elsa:ApiKey"];
    if (!string.IsNullOrEmpty(elsaApiKey))
        client.DefaultRequestHeaders.Add("Authorization", $"ApiKey {elsaApiKey}");
});
builder.Services.AddHttpClient("anthropic", client =>
{
    client.BaseAddress = new Uri("https://api.anthropic.com");
    client.DefaultRequestHeaders.Add("anthropic-version", "2024-01-01");
    var apiKey = builder.Configuration["Anthropic:ApiKey"];
    if (!string.IsNullOrEmpty(apiKey))
        client.DefaultRequestHeaders.Add("x-api-key", apiKey);
});
// HTTP-based providers used by HttpProviderClient (finding 003). Each named
// client carries its own base URL + auth header so the dispatch layer doesn't
// have to know the provider details. CLI-agent providers (claude-code,
// opencode) and the Zen MCP provider are NOT registered here — they require
// subprocess / MCP transports that are tracked separately.
builder.Services.AddHttpClient("openai", client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["OpenAI:BaseUrl"] ?? "https://api.openai.com");
    var apiKey = builder.Configuration["OpenAI:ApiKey"];
    if (!string.IsNullOrEmpty(apiKey))
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
});
builder.Services.AddHttpClient("github-copilot", client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Copilot:BaseUrl"] ?? "https://api.githubcopilot.com");
    var apiKey = builder.Configuration["Copilot:ApiKey"];
    if (!string.IsNullOrEmpty(apiKey))
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
});
builder.Services.AddHttpClient("gemini", client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Gemini:BaseUrl"] ?? "https://generativelanguage.googleapis.com");
    // Gemini accepts the API key via X-Goog-Api-Key header on v1beta.
    var apiKey = builder.Configuration["Gemini:ApiKey"];
    if (!string.IsNullOrEmpty(apiKey))
        client.DefaultRequestHeaders.Add("X-Goog-Api-Key", apiKey);
});
builder.Services.AddHttpClient("openrouter", client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["OpenRouter:BaseUrl"] ?? "https://openrouter.ai");
    var apiKey = builder.Configuration["OpenRouter:ApiKey"];
    if (!string.IsNullOrEmpty(apiKey))
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
});
builder.Services.AddHttpClient("z.ai", client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["ZAi:BaseUrl"] ?? "https://api.z.ai");
    var apiKey = builder.Configuration["ZAi:ApiKey"];
    if (!string.IsNullOrEmpty(apiKey))
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
});
builder.Services.AddHttpClient("local", client =>
{
    // Local model server (Ollama / LM Studio default). Configurable per-deploy.
    var baseUrl = builder.Configuration["LocalLLM:BaseUrl"] ?? "http://localhost:11434";
    client.BaseAddress = new Uri(baseUrl);
});
builder.Services.AddHttpClient("github", client =>
{
    var baseUrl = builder.Configuration["GitHub:ApiBaseUrl"] ?? "https://api.github.com";
    client.BaseAddress = new Uri(baseUrl);
    client.DefaultRequestHeaders.Add("User-Agent", "Tamma-ELSA");
    client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
    var token = builder.Configuration["GitHub:Token"];
    if (!string.IsNullOrEmpty(token))
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
});

// ────────────────────────────────────────────────────────────────────────────
// Database + repositories (via extension method)
//
// Phase-3 dual-connection-string architecture:
//   - TammaDb        → admin / migrations / background services (superuser)
//   - TammaAppDb     → per-request runtime, role=tamma_app, RLS-enforced
//
// For backward compat with pre-Phase-3 configs, `DefaultConnection` still
// works: it's treated as the admin string when TammaDb isn't set. If
// TammaAppDb is absent, it falls through to the admin connection with a
// warning — dev-mode single-role Postgres continues to function, but
// production must set TammaAppDb explicitly (see the Phase-3 runbook).
// ────────────────────────────────────────────────────────────────────────────
// IsNullOrWhiteSpace fallback (not just `??`): appsettings.json may ship an
// empty TammaDb default to opt operators into env-only configuration, and the
// container's appsettings.json default of "Server=localhost;..." would
// otherwise mask a missing env override and connect to the wrong host.
// Resolver lives in Tamma.Api.Infrastructure with unit tests exercising
// each fallback branch — see ConnectionStringResolverTests.
var connectionString = ConnectionStringResolver.ResolveAdmin(builder.Configuration);
var appConnectionString = ConnectionStringResolver.ResolveApp(builder.Configuration);
var controlPlaneConnectionString = ConnectionStringResolver.ResolveControlPlane(builder.Configuration);
if (appConnectionString is null)
{
    Log.Warning(
        "ConnectionStrings:TammaAppDb is not configured — falling back to the "
        + "admin connection for per-request DbContexts. RLS will be inactive "
        + "until the app-role connection is wired (see Phase-3 runbook). "
        + "This is expected for local development; production deployments "
        + "must set this explicitly.");
}
if (controlPlaneConnectionString is null)
{
    // Story 28-2: ControlPlane connection falls back to the admin connection
    // for local dev. Production must set ConnectionStrings:ControlPlane to
    // point at the new tamma_control database (created by the Story 28-5
    // bootstrap script).
    Log.Information(
        "ConnectionStrings:ControlPlane not configured — ControlPlaneDbContext "
        + "will share the admin connection. Acceptable for local dev; production "
        + "must point at the dedicated tamma_control database (Story 28-1).");
}

// ── Story 28-12: AES-GCM connection-string decryptor + KEK cabinet ──
//
// The KekProvider is the single source of truth for the platform's
// primary + (optional) secondary KEK. The AesGcmConnectionStringDecryptor
// adapter is the production override for the Story 28-4 resolver's
// IConnectionStringDecryptor seam — registered BEFORE AddTammaData so
// the resolver's TryAddSingleton<IConnectionStringDecryptor, Passthrough>
// fallback does not win.
builder.Services.AddSingleton<Tamma.Api.Services.Secrets.KekProvider>();
builder.Services.AddSingleton<
    Tamma.Data.Abstractions.IConnectionStringDecryptor,
    Tamma.Api.Services.Secrets.AesGcmConnectionStringDecryptor>();

builder.Services.AddTammaData(connectionString, appConnectionString, controlPlaneConnectionString);

// ── Story 28-4 — production tenant connection pool (LRU + handles) ──
//
// Replaces the StubTenantConnectionResolver registered by AddTammaData
// with the LRU-cached LruPooledTenantConnectionResolver. Callers (every
// per-tenant DbContext build, every TenantDbContextFactory.CreateAsync)
// gain warm-pool reuse + ref-counted leases for SSE/streaming consumers.
//
// Wired AFTER AddTammaData so this Replace+AddSingleton wins over the
// stub's TryAddSingleton fallback. The CP connection string drives the
// resolver's pooled IDbContextFactory<ControlPlaneDbContext> for cold-
// miss tenant-row lookups.
//
// Test fixtures and dev environments without a real CP connection
// string keep the StubTenantConnectionResolver wiring (good enough for
// the EF query-filter fallback that the existing tests rely on).
if (!string.IsNullOrWhiteSpace(controlPlaneConnectionString))
{
    builder.Services.AddTenantConnectionPool(
        builder.Configuration,
        controlPlaneConnectionString);

    // Optional pre-warm of top-N most-active tenants on startup. Off
    // by default (TenantConnectionPool:Warmup:Enabled=false). Reads
    // the top-tenants list from Story 28-10's IPlatformAnalyticsService
    // — fresh installs see an empty list and skip cleanly.
    builder.Services.Configure<Tamma.Api.Services.PoolWarmupOptions>(opts =>
        builder.Configuration
            .GetSection(Tamma.Api.Services.PoolWarmupOptions.SectionName)
            .Bind(opts));
    builder.Services.AddHostedService<Tamma.Api.Services.PoolWarmupService>();

    // Story 30-8 — V2 routing seam: the LRU pool consults
    // ITenantEndpointDirectory before falling back to the legacy
    // EncryptedConnectionString path. Wires the registry, the null
    // provider seam, the provider-key lookup (gracefully handles
    // Story 30-3's not-yet-landed migration via information_schema
    // probe) and the V2 directory adapter. Real providers plug in
    // via additional AddSingleton<ITenantInfrastructureProvider, …>
    // calls (Stories 30-4..30-6).
    builder.Services.AddTenantProvisioningV2();
}
else
{
    Log.Information(
        "Story 28-4 — no ControlPlane connection string configured; " +
        "tenant connection pool stays on the StubTenantConnectionResolver. " +
        "Set ConnectionStrings:ControlPlane to enable the LRU pool + " +
        "/api/admin/pools/* diagnostics.");
}

// R2-M1: register IErrorRedactor so KekRotationCoordinator can scrub
// ex.Message before it lands in platform_events.data or
// kek_rotations.FailureReason. Idempotent — TryAdd lets ElsaServer
// or Tamma.Activities own the singleton when those projects also
// register it.
builder.Services.TryAddSingleton<
    Tamma.Activities.Security.IErrorRedactor,
    Tamma.Activities.Security.ErrorRedactor>();

// Coordinator drives the platform-wide KEK rotation flow. Singleton —
// only one rotation can be in flight at a time.
builder.Services.AddSingleton<Tamma.Api.Services.Secrets.KekRotationCoordinator>();

// R2-H13 — readiness probe refuses to flip green when there are
// tenant rows further behind than the cabinet history can decrypt.
builder.Services.AddSingleton<Tamma.Api.Services.Secrets.KekCabinetHealthCheck>();

// Keep existing mentorship repos/services for backward compat
builder.Services.AddScoped<IMentorshipSessionRepository, MentorshipSessionRepository>();
builder.Services.AddScoped<IMentorshipService, MentorshipService>();
builder.Services.AddScoped<ISlackIntegrationService, SlackIntegrationService>();
builder.Services.AddScoped<IGitHubIntegrationService, GitHubIntegrationService>();
builder.Services.AddScoped<IJiraIntegrationService, JiraIntegrationService>();
builder.Services.AddScoped<ICIIntegrationService, CIIntegrationService>();
builder.Services.AddScoped<IEmailIntegrationService, EmailIntegrationService>();
builder.Services.AddScoped<IIntegrationService, IntegrationService>();
builder.Services.AddScoped<IAnalyticsService, AnalyticsService>();
builder.Services.AddScoped<IElsaWorkflowService, ElsaWorkflowService>();
builder.Services.AddHostedService<WorkflowSyncService>();

// Auth services
builder.Services.AddSingleton<IPasswordService, PasswordService>();
builder.Services.AddSingleton<ILoginLockoutService, LoginLockoutService>();
// Two-phase delete confirmation (finding 021) + session cookie writer (finding 018).
builder.Services.AddSingleton<IDeleteConfirmationService, DeleteConfirmationService>();
builder.Services.AddScoped<ISessionCookieWriter, SessionCookieWriter>();
// Story 28-R2 / PF-S6 — trusted-proxy resolver. Singleton because the
// CIDR list is read from configuration once at startup; runtime
// changes require a restart. Default empty list = trust nothing
// (matches a directly-exposed Kestrel). Operators behind nginx /
// traefik populate Tamma:TrustedProxies:Cidrs with the proxy subnet so
// X-Forwarded-For flows through for audit-event ip resolution.
builder.Services.AddSingleton<Tamma.Api.Services.Auth.TrustedProxyResolver>();
// Story 28-R2 follow-up B — admin impersonation. Scoped because it
// depends on the per-request ControlPlaneDbContext for the audit
// table inserts/updates and on the singleton IJwtService for token
// minting. The ImpersonationContextMiddleware reads via this same
// service so a "revoke" by another platform-admin lands on the very
// next request.
builder.Services.AddScoped<
    Tamma.Api.Services.Auth.IAdminImpersonationService,
    Tamma.Api.Services.Auth.AdminImpersonationService>();
// Path-tenant gate: every /api/v1/orgs/{tenantId}/* endpoint runs this
// filter to verify caller membership (findings 001, 024).
builder.Services.AddScoped<Tamma.Api.Authorization.RequireTenantMembershipFilter>();
// Audit finding auth/014 follow-up — distributed rate-limit backend.
// ConnectionStrings:Redis present → Redis-backed (multi-pod safe);
// absent → in-process (single-pod default, matches pre-Redis behaviour).
var redisConnectionString = builder.Configuration.GetConnectionString("Redis");
if (!string.IsNullOrWhiteSpace(redisConnectionString))
{
    builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(_ =>
        StackExchange.Redis.ConnectionMultiplexer.Connect(redisConnectionString));
    builder.Services.AddSingleton<Tamma.Api.Services.RateLimit.IDistributedRateLimitBackend,
        Tamma.Api.Services.RateLimit.RedisDistributedRateLimitBackend>();
}
else
{
    builder.Services.AddSingleton<Tamma.Api.Services.RateLimit.IDistributedRateLimitBackend,
        Tamma.Api.Services.RateLimit.InMemoryDistributedRateLimitBackend>();
}
builder.Services.AddSingleton<Tamma.Api.Services.RateLimit.IRateLimitService,
    Tamma.Api.Services.RateLimit.RateLimitService>();
// Story 28-7 deferred-item: per-API-key RPM limiter. Sits next to the
// per-action IRateLimitService but keyed per ApiKey.Id with 60s windows.
builder.Services.AddSingleton<Tamma.Api.Services.RateLimit.IApiKeyRateLimiter,
    Tamma.Api.Services.RateLimit.ApiKeyRateLimiter>();
builder.Services.AddHttpContextAccessor();
// IMemoryCache for the installation router cache (audit finding 029).
builder.Services.AddMemoryCache();
// GitHub OAuth http client (token exchange + profile fetch). User-Agent
// header is required by the GitHub API.
builder.Services.AddHttpClient("github-oauth", client =>
{
    client.DefaultRequestHeaders.Add("User-Agent", "Tamma-API");
    client.DefaultRequestHeaders.Add("Accept", "application/json");
});
builder.Services.AddScoped<Tamma.Api.Services.OAuth.IGitHubOAuthService,
    Tamma.Api.Services.OAuth.GitHubOAuthService>();

// Hardening workstreams — ported from the deleted TS API services.
// Each extension method owns its own service registrations.
builder.Services.AddPromptStoreServices();
builder.Services.AddProviderHealthServices();
builder.Services.AddDiagnosticsServices();
builder.Services.AddSanitizationServices();
builder.Services.AddAgentResolverServices();
builder.Services.AddGitHubInstallationServices(builder.Configuration);
// Wave 2
builder.Services.AddConventionServices();
builder.Services.AddEmailServices();
builder.Services.AddTaskQueue();
builder.Services.AddProviderSessionServices();
builder.Services.AddSaaSServices();
// Per-tenant provisioning (Cranl). When Cranl:ApiKey + Cranl:OrganizationId
// are both configured, the Cranl-backed provisioner + workflow + queue
// handler are wired; otherwise the Null seam keeps every tenant on the
// shared central Postgres via RLS. See docs/vendors/cranl/README.md for
// the per-tenant provisioning flow.
builder.Services.AddTenantProvisioning(builder.Configuration);

// Platform secret cabinet (Epic 29). Story 29-1 wires only the
// abstraction + a null auditor + an in-memory backend placeholder;
// Story 29-2 swaps in the Postgres envelope-encrypted backend and the
// real auditor.
builder.Services.AddTammaSecrets();

// Story 29-9 / 29-10: runtime resolver + stopgap migrator. The
// resolver is the read-path abstraction over the cabinet + the
// (deprecated) env-var fallback — when the TAMMA_STOPGAP_FAIL_FAST
// env var is set (Story 29-10 mode) the resolver throws
// MissingSecretException on a missing cabinet row instead of
// silently falling back to IConfiguration. Default during the
// coexistence window is fallback=on.
//
// Only wired when the secrets DbContext factory is itself wired —
// the resolver and migrator both depend on it for cabinet reads /
// writes. Tests that do not exercise the secrets pipeline therefore
// do not need to provide a Postgres connection string.
if (!string.IsNullOrWhiteSpace(
        builder.Configuration.GetConnectionString("SecretStore"))
    || !string.IsNullOrWhiteSpace(
        builder.Configuration.GetConnectionString("ControlPlane")))
{
    var stopgapFailFast = string.Equals(
        Environment.GetEnvironmentVariable("TAMMA_STOPGAP_FAIL_FAST"),
        "true", StringComparison.OrdinalIgnoreCase);
    builder.Services.AddTammaSecretStopgapMigrator(
        allowEnvFallback: !stopgapFailFast);
}

// Story 29-3 reveal-once pipeline. Registers:
//   • IDbContextFactory<SecretRevealDbContext> on the secret-store
//     connection string (falls back to ControlPlane).
//   • ISecretRevealService — issues + consumes reveal tokens.
//   • RevealTokenSweeper — 30s background sweep for expired rows.
// Only wired when the secret-store schema is actually reachable
// (i.e. the same ConnectionStrings:SecretStore / ControlPlane is
// available); the extension throws on missing config so a mis-
// configured host fails fast instead of returning 500s at runtime.
if (!string.IsNullOrWhiteSpace(
        builder.Configuration.GetConnectionString("SecretStore"))
    || !string.IsNullOrWhiteSpace(
        builder.Configuration.GetConnectionString("ControlPlane")))
{
    builder.Services.AddTammaSecretReveal(builder.Configuration);
}

// Story 31-2: platform routing resolver. Exposes IPlatformResolver as a
// scoped service over a singleton driver cache and the Epic 29 secret
// store seam. Drivers themselves (GitHub 31-3, Gitea 31-4, ...) ship in
// sibling projects and self-register their per-kind
// IGitPlatformDriverFactory via keyed DI; until 31-3 lands no kind has
// a real factory and the resolver returns null for every tenant.
//
// Credential reader registration follows the same conditional pattern as
// IAlertChannelSecretReader (Story 1.5-37): when the Story 29-2
// SecretsDbContextFactory is registered we wire the real reader; in tests
// or dev environments without it we fall back to NullPlatformCredentialReader
// so a misconfigured host doesn't fail at DI-validation time.
builder.Services.AddSingleton<Tamma.Platforms.PlatformDriverCache>();
if (builder.Services.Any(d => d.ServiceType ==
    typeof(Microsoft.EntityFrameworkCore.IDbContextFactory<
        Tamma.Api.Services.Secrets.Postgres.SecretsDbContext>)))
{
    builder.Services.AddScoped<
        Tamma.Platforms.Abstractions.IPlatformCredentialReader,
        Tamma.Api.Services.Platforms.SecretStorePlatformCredentialReader>();
}
else
{
    builder.Services.AddSingleton<
        Tamma.Platforms.Abstractions.IPlatformCredentialReader,
        Tamma.Api.Services.Platforms.NullPlatformCredentialReader>();
}
builder.Services.AddScoped<
    Tamma.Platforms.Abstractions.IPlatformResolver,
    Tamma.Platforms.PlatformResolver>();
builder.Services.AddScoped<
    Tamma.Platforms.IPlatformInstallationEventEmitter,
    Tamma.Platforms.PlatformInstallationEventEmitter>();

// Story 31-4: register the Gitea driver factory under keyed DI for
// PlatformKind.Gitea. PlatformResolver picks the factory up via
// GetKeyedService<IGitPlatformDriverFactory>(PlatformKind.Gitea) when
// a tenant's installation row carries platform_kind = 'gitea'. The
// extension is idempotent and registers the named tamma-gitea HTTP
// client + OAuth2 token cache + webhook signature verifier.
builder.Services.AddGiteaPlatformDriver();

// Engine callback services (audit findings 001, 004, 005-011). Context store
// is in-memory (single-instance only) until the real RAG pipeline ports.
//
// IGitHubEngineCallbackService uses the Octokit-backed impl when the GitHub
// App is configured (GitHub:AppId + GitHub:PrivateKey), otherwise falls
// through to the Null impl which returns 503 with
// `github_client_not_configured`. Matches the switching logic in
// AddGitHubInstallationServices so the two GitHub surfaces stay in sync.
builder.Services.AddSingleton<Tamma.Api.Services.Engine.IContextStore,
    Tamma.Api.Services.Engine.InMemoryContextStore>();
builder.Services.AddScoped<Tamma.Api.Services.Engine.IExecuteTaskService,
    Tamma.Api.Services.Engine.ExecuteTaskService>();
if (builder.Configuration.GetValue<long?>("GitHub:AppId") is long appId && appId > 0
    && !string.IsNullOrWhiteSpace(builder.Configuration["GitHub:PrivateKey"]))
{
    // The resolver is scoped (takes a scoped repository); wrap the service
    // itself as scoped so the resolver flow works.
    builder.Services.AddScoped<Tamma.Api.Services.Engine.IRepoInstallationResolver,
        Tamma.Api.Services.Engine.InstallationRepoResolver>();
    builder.Services.AddScoped<Tamma.Api.Services.Engine.IGitHubEngineCallbackService,
        Tamma.Api.Services.Engine.OctokitGitHubEngineCallbackService>();
}
else
{
    builder.Services.AddSingleton<Tamma.Api.Services.Engine.IGitHubEngineCallbackService,
        Tamma.Api.Services.Engine.NullGitHubEngineCallbackService>();
}
// Engine registry (audit finding 013). Until TammaEngine ports, the
// in-memory impl materialises synthetic per-tenant entries from the
// workflow store so the dashboard /engines tile is not blank.
builder.Services.AddSingleton<Tamma.Api.Services.Engine.IEngineRegistry,
    Tamma.Api.Services.Engine.InMemoryEngineRegistry>();

// ─── Epic 19: Agent dispatch (stories 19-2 / 3 / 4 / 5) ────────────────
//
// IGitHubActionsClient — Octokit-backed when the GitHub App is wired,
// otherwise the Null impl that reports NotConfigured so the activities
// surface a clean operator error instead of silently succeeding.
//
// Services (Dispatch/Monitor/Collect) wrap the client and encapsulate
// the logic shared by the Elsa activities AND the GitHubActionsExecutor.
// The AgentExecutorFactory picks between LocalExecutor and
// GitHubActionsExecutor at runtime (TAMMA_AGENT_MODE env var > config
// `Agent:ExecutorMode` > auto-detect via GitHub App presence).
if (builder.Configuration.GetValue<long?>("GitHub:AppId") is long actionsAppId && actionsAppId > 0
    && !string.IsNullOrWhiteSpace(builder.Configuration["GitHub:PrivateKey"]))
{
    // Scoped because IRepoInstallationResolver depends on a scoped
    // installation repository. Matches the engine-callback pattern
    // above.
    builder.Services.AddScoped<Tamma.Activities.AgentDispatch.IGitHubActionsClient,
        Tamma.Api.Services.GitHub.OctokitGitHubActionsClient>();
}
else
{
    builder.Services.AddSingleton<Tamma.Activities.AgentDispatch.IGitHubActionsClient,
        Tamma.Activities.AgentDispatch.NullGitHubActionsClient>();
}

// Services — scoped to match the client lifetime.
builder.Services.AddScoped<Tamma.Activities.AgentDispatch.IAgentDispatchService,
    Tamma.Activities.AgentDispatch.AgentDispatchService>();
builder.Services.AddScoped<Tamma.Activities.AgentDispatch.IAgentMonitorService,
    Tamma.Activities.AgentDispatch.AgentMonitorService>();
builder.Services.AddScoped<Tamma.Activities.AgentDispatch.IAgentResultCollectorService,
    Tamma.Activities.AgentDispatch.AgentResultCollectorService>();

// Webhook-signal registry (story 19-3 AC-7). Singleton — the registry
// IS the in-process pub/sub plane. Lets a workflow_run.completed webhook
// wake a suspended IAgentMonitorService call, eliminating the 60 GitHub
// API poll calls per agent run. Mode=Auto on MonitorAgentWorkflowActivity
// falls back to poll when this registry is missing, so wiring it is
// purely additive.
builder.Services.AddSingleton<Tamma.Activities.AgentDispatch.IWebhookSignalRegistry,
    Tamma.Activities.AgentDispatch.WebhookSignalRegistry>();

// Executors — LocalExecutor only needs the process runner (singleton-safe);
// GitHubActionsExecutor composes the three scoped services so itself must
// be scoped.
builder.Services.AddSingleton<Tamma.Activities.AgentDispatch.IProcessRunner,
    Tamma.Activities.AgentDispatch.DefaultProcessRunner>();
builder.Services.AddSingleton(_ =>
    Tamma.Activities.AgentDispatch.LocalExecutorOptions.FromConfiguration(builder.Configuration));
builder.Services.AddScoped<Tamma.Activities.AgentDispatch.LocalExecutor>();
builder.Services.AddScoped<Tamma.Activities.AgentDispatch.GitHubActionsExecutor>();

// Factory + activity wrapper.
builder.Services.AddScoped<Tamma.Activities.AgentDispatch.AgentExecutorFactory>();

// Engine lifecycle SSE bus (audit finding 012). In-process pub/sub that
// fans workflow / task-queue / engine-registry events to dashboard
// EventSource clients subscribed on /api/engine/events/state and
// /api/engine/events/logs. Singleton — the bus IS the fanout plane.
builder.Services.AddSingleton<Tamma.Api.Services.Engine.Lifecycle.IEngineLifecycleBus,
    Tamma.Api.Services.Engine.Lifecycle.InMemoryEngineLifecycleBus>();
builder.Services.Configure<Tamma.Api.Services.Engine.Lifecycle.EngineLifecycleOptions>(
    builder.Configuration.GetSection("EngineLifecycle"));
builder.Services.AddHostedService<Tamma.Api.Services.Engine.Lifecycle.EngineRegistryHeartbeatService>();

// Story 28-6: in-process platform-event bus. Subscribers attach in this
// process only; multi-pod fanout pending a Postgres LISTEN/NOTIFY bridge
// against platform_events. Repository registration lives in AddTammaData.
builder.Services.AddPlatformEventBus();

builder.Services.AddKnowledgeBaseServices(builder.Configuration);

// Controllers (for existing mentorship controller)
builder.Services.AddControllers();

// Rate limiting (finding 020). Per-IP token-bucket with named policies for
// settings/provider/agent endpoints. TS used @fastify/rate-limit at 100/min
// read, 30/min write; we mirror those defaults.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;

    options.AddFixedWindowLimiter("ConfigRead", o =>
    {
        o.PermitLimit = 100;
        o.Window = TimeSpan.FromMinutes(1);
        o.QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst;
        o.QueueLimit = 0;
    });
    options.AddFixedWindowLimiter("ConfigWrite", o =>
    {
        o.PermitLimit = 30;
        o.Window = TimeSpan.FromMinutes(1);
        o.QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst;
        o.QueueLimit = 0;
    });
    options.AddFixedWindowLimiter("ProviderIngest", o =>
    {
        o.PermitLimit = 500;
        o.Window = TimeSpan.FromMinutes(1);
        o.QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst;
        o.QueueLimit = 0;
    });
    options.AddFixedWindowLimiter("ProviderExecute", o =>
    {
        o.PermitLimit = 50;
        o.Window = TimeSpan.FromMinutes(1);
        o.QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst;
        o.QueueLimit = 0;
    });
    // Audit findings 014 + 017 — match TS @fastify/rate-limit on the GitHub
    // public surface. Webhook = 300/min (high enough for an active GitHub
    // App, low enough that an attacker spamming HMAC-failed deliveries hits
    // 429 before exhausting CPU). OAuth start = 60/min to throttle CSRF
    // cookie spray and code-exchange amplification.
    options.AddFixedWindowLimiter("GitHubWebhook", o =>
    {
        o.PermitLimit = 300;
        o.Window = TimeSpan.FromMinutes(1);
        o.QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst;
        o.QueueLimit = 0;
    });
    options.AddFixedWindowLimiter("OAuthStart", o =>
    {
        o.PermitLimit = 60;
        o.Window = TimeSpan.FromMinutes(1);
        o.QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst;
        o.QueueLimit = 0;
    });
    // Story 29-3 — reveal-once token exchange. 10/min matches the plan
    // AC7: a brute-force attacker on the 256-bit token search space
    // trips 429 well before exhausting a meaningful slice of the key
    // space, and the low limit keeps the audit log noisier for the
    // attempted guesses.
    options.AddFixedWindowLimiter("SecretReveal", o =>
    {
        o.PermitLimit = 10;
        o.Window = TimeSpan.FromMinutes(1);
        o.QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst;
        o.QueueLimit = 0;
    });
});

// CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowDashboard", policy =>
    {
        policy.WithOrigins(
                builder.Configuration["Dashboard:Url"] ?? "http://localhost:3001")
            .WithHeaders("Content-Type", "Authorization")
            .WithMethods("GET", "POST", "PUT", "DELETE", "PATCH");
    });
});

// Health checks. Tag the Postgres check as "ready" so the readiness probe
// fails when the DB is unreachable; the liveness probe (no DB dependency)
// only verifies the process is up.
//
// R2-H13: KekCabinetHealthCheck refuses to flip to "ready" when there
// are tenant rows whose KekVersion is more than
// KekProvider.RetainedHistorySize behind the active primary — those
// rows would be undecryptable on the next rotation.
builder.Services.AddHealthChecks()
    .AddNpgSql(connectionString, tags: new[] { "ready" })
    .AddCheck<Tamma.Api.Services.Secrets.KekCabinetHealthCheck>(
        name: "kek-cabinet",
        failureStatus: Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy,
        tags: new[] { "ready" });

// Admin health aggregator (per-service ping fan-out for the dashboard).
// Mirrors the TS /api/admin/health behavior.
builder.Services.AddScoped<IAdminHealthService, AdminHealthService>();

// Story 5.6 / 1.5-37 (Wave C.1) — alert core: sink, dispatcher,
// four channels (email / slack / pagerduty / webhook), rate
// limiter, secret reader. Registered before any caller so
// IAlertSink can be injected by future wave-C.4 activity edits.
builder.Services.AddTammaAlerts();

// Story 5.6 (Wave C.2) — alert rule engine: evaluator, registry,
// window store, and the built-in rule seeder. Subscribes to the
// DCB event stream and emits AlertPayloads through IAlertSink.
builder.Services.AddTammaAlertRuleEngine();

// Wave C.4 §4 — per-process health monitor for TammaApiClient.
// Singleton so the rolling 5-min failure window is shared across every
// call site. Fires PLATFORM.API.UNHEALTHY via IAlertEventEmitter when
// sustained failures cross the threshold. The ScopedAlertEventEmitter
// adapter resolves the scoped IAlertEventEmitter per emission (bridge
// between singleton monitor + scoped emitter lifetime).
builder.Services.AddSingleton<Tamma.Activities.LlmCall.TammaApiHealthMonitor>(sp =>
    new Tamma.Activities.LlmCall.TammaApiHealthMonitor(
        new Tamma.Activities.LlmCall.ScopedAlertEventEmitter(sp),
        sp.GetService<TimeProvider>()));

// Story 28-10 — platform-wide analytics rollup behind the
// /api/admin/analytics/* endpoints. Reads the CP context
// (tenants + platform_events) and the app context (workflow_instances +
// domain_events) so it is scoped alongside both. Clock via TimeProvider.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<
    Tamma.Api.Services.Analytics.IPlatformAnalyticsService,
    Tamma.Api.Services.Analytics.PlatformAnalyticsService>();

// Story 28-8 AC3 — short-TTL tenant status cache (per-pod, in-memory).
// Cuts CP round-trips in TenantContextMiddleware on hot tenant requests.
// Cluster-wide invalidation (RabbitMQ pub/sub) is a future enhancement —
// per-pod cache + 10s TTL provides eventual consistency in the meantime.
builder.Services.AddOptions<Tamma.Api.Services.TenantStatus.TenantStatusCacheOptions>()
    .Configure(opts => builder.Configuration
        .GetSection(Tamma.Api.Services.TenantStatus.TenantStatusCacheOptions.SectionName)
        .Bind(opts));
builder.Services.AddSingleton<Tamma.Api.Services.TenantStatus.MemoryTenantStatusCache>();
builder.Services.AddSingleton<Tamma.Api.Services.TenantStatus.ITenantStatusCache>(
    sp => sp.GetRequiredService<Tamma.Api.Services.TenantStatus.MemoryTenantStatusCache>());
// Story 28-8 H12 — surface the same cache to the resolver hot path so
// status flips force a cold CP refresh instead of returning a stale
// pool. Lives under Tamma.Data.Abstractions.ITenantStatusProbe so the
// resolver's project doesn't take a reference on Tamma.Api.
builder.Services.AddSingleton<Tamma.Data.Abstractions.ITenantStatusProbe>(
    sp => sp.GetRequiredService<Tamma.Api.Services.TenantStatus.MemoryTenantStatusCache>());

// Round-2 follow-up — cluster-wide tenant-status invalidation. Pairs
// with the per-pod cache above so a status flip on pod A propagates
// to sibling pods within milliseconds via Postgres LISTEN/NOTIFY,
// instead of converging only after the 10s TTL.
//
// AddTenantConnectionPool already registered the publish-side bus +
// the singleton CP NpgsqlDataSource. Here we register the subscribe
// side: a BackgroundService that LISTENs on the channel and
// dispatches into ITenantStatusCache + ITenantConnectionResolver.
//
// In environments without a CP connection string (test fixtures,
// single-pod dev), AddTenantStatusInvalidation registered the
// NullTenantStatusInvalidationBus and skipped the data source — so
// the listener registration is gated on the same condition.
if (!string.IsNullOrWhiteSpace(controlPlaneConnectionString))
{
    builder.Services.AddSingleton<
        Tamma.Api.Services.TenantStatus.TenantStatusInvalidationListener>();
    builder.Services.AddHostedService(sp =>
        sp.GetRequiredService<
            Tamma.Api.Services.TenantStatus.TenantStatusInvalidationListener>());
}
else
{
    // Make sure the bus is registered as Null even when
    // AddTenantConnectionPool was skipped (it's the one that wires
    // AddTenantStatusInvalidation today). Keeps the admin endpoints'
    // `bus.PublishAsync` calls compile-time + runtime safe.
    builder.Services.AddTenantStatusInvalidation(controlPlaneConnectionString: null);
}

// M1 — IErrorRedactor scrubs sensitive material from exception messages
// before they cross the long-lived storage boundary (event store +
// ProvisioningDetail column). Used by CleanUpFailedTenantActivity and
// any other activity that publishes exception text to platform_events.
builder.Services.AddSingleton<
    Tamma.Activities.Security.IErrorRedactor,
    Tamma.Activities.Security.ErrorRedactor>();

// Story 28-6 — platform-task worker (drains platform_queued_tasks via
// IPlatformTaskHandler routing). Concrete handlers are registered by
// each capability owner (e.g. webhook routing in Story 28-7); the
// worker itself is hosted-service singleton + registry singleton.
builder.Services.AddPlatformTaskWorker(builder.Configuration);

// ────────────────────────────────────────────────────────────────────────────
// Authentication + Authorization
// ────────────────────────────────────────────────────────────────────────────
var jwtSecret = builder.Configuration["Jwt:Secret"];
if (!string.IsNullOrEmpty(jwtSecret))
{
    builder.Services.AddSingleton<IJwtService, JwtService>();

    builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "tamma",
            ValidateAudience = true,
            ValidAudience = builder.Configuration["Jwt:Audience"] ?? "tamma-api",
            ClockSkew = TimeSpan.Zero,
            // sub claim is the user id; role claim is the bare string "role".
            // Without these, ClaimsPrincipal.Identity.Name and IsInRole
            // would look at the long URI claim names — see audit finding 002.
            NameClaimType = System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub,
            RoleClaimType = "role",
        };
        // Cookie fallback: if no Authorization header, read the JWT from
        // the tamma_session cookie. Mirrors TS where the cookie is the
        // primary auth source for cross-subdomain dashboard requests
        // (audit finding 011 / 010).
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                if (string.IsNullOrEmpty(ctx.Token) &&
                    ctx.Request.Cookies.TryGetValue("tamma_session", out var cookieJwt) &&
                    !string.IsNullOrEmpty(cookieJwt))
                {
                    ctx.Token = cookieJwt;
                }
                return Task.CompletedTask;
            }
        };
    })
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthHandler>("ApiKey", null);

    builder.Services.AddScoped<IAuthorizationHandler, PermissionHandler>();
    builder.Services.AddScoped<IAuthorizationHandler, SelfOrPermissionHandler>();
    // Story 28-R2 / C1 — handler for the new PlatformOwnerAccess policy.
    builder.Services.AddScoped<IAuthorizationHandler, PlatformPermissionHandler>();

    builder.Services.AddAuthorization(options =>
    {
        options.DefaultPolicy = new AuthorizationPolicyBuilder()
            .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, "ApiKey")
            .RequireAuthenticatedUser()
            .Build();

        options.AddPolicy("AdminAccess", p =>
        {
            p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, "ApiKey");
            p.AddRequirements(new PermissionRequirement("admin:access"));
        });
        options.AddPolicy("OwnerAccess", p =>
        {
            p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, "ApiKey");
            p.AddRequirements(new PermissionRequirement("users:manage"));
        });
        // Story 28-R2 / Finding C1 — platform-scoped admin gate. Distinct
        // from OwnerAccess (which keys off the per-tenant role and lets
        // every personal-tenant owner through). PlatformOwnerAccess
        // requires the JWT `platformRole` claim to be `platform_admin`,
        // which is sourced from the dedicated users.platform_role column.
        // Every /api/admin/* route that performs platform-scoped work
        // (tenant lifecycle, KEK rotation, alert config, pool diagnostics,
        // platform secrets, analytics) MUST use this policy — not
        // OwnerAccess. Keep OwnerAccess for tenant-scoped owner gates
        // (e.g. tenant-level user management, settings:manage).
        options.AddPolicy("PlatformOwnerAccess", p =>
        {
            p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, "ApiKey");
            p.AddRequirements(new PlatformPermissionRequirement("platform_admin"));
        });
        options.AddPolicy("MemberAccess", p =>
        {
            p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, "ApiKey");
            p.RequireAuthenticatedUser();
        });
        options.AddPolicy("SettingsView", p =>
        {
            p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, "ApiKey");
            p.AddRequirements(new PermissionRequirement("settings:view"));
        });
        options.AddPolicy("SettingsManage", p =>
        {
            p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, "ApiKey");
            p.AddRequirements(new PermissionRequirement("settings:manage"));
        });
        // Story 27-3 — Prompt Store tenant-admin policy. CLAUDE.md
        // "Prompt Store Architecture / RBAC" allows PUT/DELETE override to
        // tenant_owner OR tenant_admin (admin+owner in this codebase's role
        // matrix). The existing SettingsManage policy is owner-only and would
        // 403 every tenant_admin, so prompt PUT/DELETE/POST-reset routes use
        // the dedicated PromptManage gate instead.
        options.AddPolicy("PromptManage", p =>
        {
            p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, "ApiKey");
            p.AddRequirements(new PermissionRequirement("prompts:manage"));
        });
        options.AddPolicy("WorkflowsView", p =>
        {
            p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, "ApiKey");
            p.AddRequirements(new PermissionRequirement("workflows:view"));
        });
        options.AddPolicy("WorkflowsManage", p =>
        {
            p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, "ApiKey");
            p.AddRequirements(new PermissionRequirement("workflows:manage"));
        });
        // Story 16-5 AC 7: DELETE /api/workflows/* must be owner-only.
        // workflows:delete maps to ["owner"] in the permission matrix.
        options.AddPolicy("WorkflowsDelete", p =>
        {
            p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, "ApiKey");
            p.AddRequirements(new PermissionRequirement("workflows:delete"));
        });
        options.AddPolicy("DashboardView", p =>
        {
            p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, "ApiKey");
            p.AddRequirements(new PermissionRequirement("dashboard:view"));
        });
        options.AddPolicy("ApiKeysManage", p =>
        {
            p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, "ApiKey");
            p.AddRequirements(new PermissionRequirement("apikeys:manage"));
        });
        // Self-or-permission policies — mirror TS requireSelfOrRole. Allow a
        // member-role user to manage their OWN API keys / read their OWN
        // profile, while still gating cross-user access by the underlying
        // permission. Audit finding 016.
        options.AddPolicy("SelfOrApiKeysManage", p =>
        {
            p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, "ApiKey");
            p.AddRequirements(new SelfOrPermissionRequirement("apikeys:manage"));
        });
        options.AddPolicy("SelfOrUsersView", p =>
        {
            p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, "ApiKey");
            p.AddRequirements(new SelfOrPermissionRequirement("users:view"));
        });
        // RoleCheck policy: cookie or bearer JWT, must be authenticated. Used
        // by nginx auth_request to gate cross-subdomain access (finding 010).
        options.AddPolicy("AuthenticatedAny", p =>
        {
            p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, "ApiKey");
            p.RequireAuthenticatedUser();
        });
    });
}
else if (builder.Environment.IsDevelopment())
{
    Log.Warning("JWT secret not configured. Using permissive authorization in Development mode.");
    builder.Services.AddSingleton<IJwtService, JwtService>(sp =>
    {
        // Provide a temporary dev-only secret
        var config = sp.GetRequiredService<IConfiguration>();
        var tempConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Secret"] = "dev-secret-at-least-32-characters-long-for-hmac",
                ["Jwt:Issuer"] = config["Jwt:Issuer"] ?? "tamma",
                ["Jwt:Audience"] = config["Jwt:Audience"] ?? "tamma-api"
            })
            .Build();
        return new JwtService(tempConfig);
    });
    builder.Services.AddAuthentication()
        .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthHandler>("ApiKey", null);
    builder.Services.AddAuthorization(options =>
    {
        options.DefaultPolicy = new AuthorizationPolicyBuilder()
            .AddRequirements(new Tamma.Api.Infrastructure.AllowAnonymousRequirement())
            .Build();
        // Register all named policies with permissive default
        foreach (var name in new[] { "AdminAccess", "OwnerAccess", "PlatformOwnerAccess", "MemberAccess", "SettingsView",
            "SettingsManage", "PromptManage", "WorkflowsView", "WorkflowsManage", "WorkflowsDelete", "DashboardView", "ApiKeysManage",
            "SelfOrApiKeysManage", "SelfOrUsersView", "AuthenticatedAny" })
        {
            options.AddPolicy(name, p => p.AddRequirements(new Tamma.Api.Infrastructure.AllowAnonymousRequirement()));
        }
    });
    builder.Services.AddSingleton<IAuthorizationHandler, Tamma.Api.Infrastructure.AllowAnonymousHandler>();
}
else
{
    throw new InvalidOperationException(
        "JWT secret (Jwt:Secret) must be configured in non-Development environments.");
}

var app = builder.Build();

// ────────────────────────────────────────────────────────────────────────────
// CLI dispatch — Story 29-9 one-shot commands run BEFORE the HTTP pipeline
// binds. `dotnet run --project Tamma.Api -- migrate-secrets` imports every
// stopgap secret into the cabinet, prints a report, and exits.
// ────────────────────────────────────────────────────────────────────────────
if (Tamma.Api.Services.Secrets.Stopgap.MigrateSecretsCommand.ShouldRun(args))
{
    var exitCode = await Tamma.Api.Services.Secrets.Stopgap
        .MigrateSecretsCommand.RunAsync(app.Services);
    return exitCode;
}

// ────────────────────────────────────────────────────────────────────────────
// Middleware pipeline
// ────────────────────────────────────────────────────────────────────────────
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Tamma API v2");
        c.RoutePrefix = "swagger";
    });
}

app.UseSerilogRequestLogging();
app.UseHttpsRedirection();
app.UseCors("AllowDashboard");

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
// Story 28-R2 follow-up B — verify the impersonation row backing any
// `imp_id` JWT claim is still active. Runs AFTER auth (so HttpContext.User
// is bound) and BEFORE TenantContextMiddleware (so a stale impersonation
// token blows up here, not after the request has bound a tenant).
app.UseMiddleware<ImpersonationContextMiddleware>();
app.UseMiddleware<TenantContextMiddleware>();
app.UseMiddleware<EnsurePersonalTenantMiddleware>();

// Existing MVC controllers
app.MapControllers();

// ASP.NET health checks. Three routes:
//   /health      — full check (all checks, including DB-dependent ones)
//   /health/live — liveness probe (no checks; passes whenever the process is up)
//   /health/ready — readiness probe (only checks tagged "ready", e.g. DB)
// Kubernetes / Docker compose can target the split routes for distinct
// liveness / readiness semantics.
app.MapHealthChecks("/health");
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false
});
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});

// ────────────────────────────────────────────────────────────────────────────
// Minimal API Endpoints
// ────────────────────────────────────────────────────────────────────────────

// ── Health (no auth) ──
app.MapGet("/api/health", HealthEndpoints.GetHealth);

// ── Auth (mostly no auth) ──
var auth = app.MapGroup("/api/v1/auth");
auth.MapPost("/register", AuthEndpoints.Register);
auth.MapPost("/verify-email", AuthEndpoints.VerifyEmail);
auth.MapPost("/resend-verification", AuthEndpoints.ResendVerification);
auth.MapPost("/login", AuthEndpoints.Login);
auth.MapPost("/refresh", AuthEndpoints.Refresh);
auth.MapPost("/logout", AuthEndpoints.Logout).RequireAuthorization("MemberAccess");
auth.MapPost("/password-reset/request", AuthEndpoints.PasswordResetRequest);
auth.MapPost("/password-reset/confirm", AuthEndpoints.PasswordResetConfirm);
// Story 28-9 — switch-org owns refresh-token rotation alongside the new JWT
// mint, so it lives in AuthEndpoints. The Story 18-3 OrgEndpoints version
// has been deleted because its direct `UpdateActiveTenantAsync` call would
// fail at runtime against the Phase-2 `prevent_tenant_id_change` trigger;
// `POST /api/v1/orgs/switch-org` now 404s.
auth.MapPost("/switch-org", AuthEndpoints.SwitchOrg).RequireAuthorization("MemberAccess");

app.MapGet("/api/auth/me", AuthEndpoints.GetMe).RequireAuthorization("AuthenticatedAny");
// /api/auth/role-check is the nginx auth_request gate — must accept either
// the JWT cookie or the Authorization header and return 200/401/403 by
// status alone (audit finding 010). AuthenticatedAny enforces auth; the
// endpoint itself returns 403 for insufficient role.
app.MapGet("/api/auth/role-check", AuthEndpoints.RoleCheck).RequireAuthorization("AuthenticatedAny");
// Audit finding 014 — rate limit OAuth start + callback (60/min). Both share
// the policy because the callback's HTTP-side cost is comparable to the start
// (token exchange + GitHub API call) and an attacker spraying either consumes
// the same downstream budget.
app.MapGet("/api/auth/github", AuthEndpoints.GitHubAuth)
    .RequireRateLimiting("OAuthStart");
app.MapGet("/api/auth/github/callback", AuthEndpoints.GitHubCallback)
    .RequireRateLimiting("OAuthStart");

// ── Admin ──
var admin = app.MapGroup("/api/admin").RequireAuthorization("AdminAccess");
admin.MapGet("/health", AdminEndpoints.GetHealth);
// Service keys are platform credentials (Elsa → API, BFF → API). Per Story
// 16-7 they are owner-only — admins must NOT be able to mint or rotate them.
// SettingsManage maps to settings:manage (owner-only) in the RBAC matrix.
admin.MapPost("/service-keys", AdminEndpoints.CreateServiceKey).RequireAuthorization("SettingsManage");
admin.MapGet("/service-keys", AdminEndpoints.ListServiceKeys).RequireAuthorization("SettingsManage");
admin.MapPost("/service-keys/{id}/rotate", AdminEndpoints.RotateServiceKey).RequireAuthorization("SettingsManage");
admin.MapDelete("/service-keys/{id}", AdminEndpoints.DeleteServiceKey).RequireAuthorization("SettingsManage");
admin.MapGet("/users", AdminEndpoints.ListUsers);
// SelfOrUsersView allows a regular member to GET their own profile via the
// admin-prefixed route (audit finding 016 — TS requireSelfOrRole behavior).
admin.MapGet("/users/{id}", AdminEndpoints.GetUser).RequireAuthorization("SelfOrUsersView");
// Story 28-R2 / PF-S1 — these mutate the GLOBAL `users` table (cross-
// tenant identity), so they must require platform-admin scope. The
// previous OwnerAccess gate keyed off the per-tenant role; every
// signed-up user is auto-`owner` of their personal tenant, which let
// any user call PUT /api/admin/users/{id}/role and DELETE
// /api/admin/users/{id} against any other platform user. The handler
// bodies also defend-in-depth against demoting platform admins —
// only the same caller can demote themselves; one platform admin
// cannot strip another's platform role through this surface.
admin.MapPut("/users/{id}/role", AdminEndpoints.UpdateUserRole).RequireAuthorization("PlatformOwnerAccess");
admin.MapDelete("/users/{id}", AdminEndpoints.DeleteUser).RequireAuthorization("PlatformOwnerAccess");
admin.MapPost("/users/invite", AdminEndpoints.InviteUser);
admin.MapGet("/users/invites", AdminEndpoints.ListInvites);
admin.MapDelete("/users/invites/{id}", AdminEndpoints.DeleteInvite);
// SelfOrApiKeysManage: a regular member can manage their own keys; admins
// can manage anyone's. Audit finding 016.
admin.MapPost("/users/{id}/keys", AdminEndpoints.CreateUserApiKey).RequireAuthorization("SelfOrApiKeysManage");
admin.MapGet("/users/{id}/keys", AdminEndpoints.ListUserApiKeys).RequireAuthorization("SelfOrApiKeysManage");
admin.MapDelete("/users/{id}/keys/{keyId}", AdminEndpoints.DeleteUserApiKey).RequireAuthorization("SelfOrApiKeysManage");

// Tenant provisioning (audit cranl/003). Platform-owner-only — these flip
// per-tenant Cranl resources into existence (POST), report status (GET), or
// tear them down (POST /deprovision). When Cranl:ApiKey is unset the Null
// provisioner short-circuits to "shared infra" and these endpoints still
// work — they just mark the tenant Ready without external API calls.
//
// Story 28-R2 / C1: switched from OwnerAccess → PlatformOwnerAccess. The
// legacy OwnerAccess policy keys off the per-tenant role and admits every
// signed-up user (auto-owner of their personal tenant); PlatformOwnerAccess
// keys off the JWT `platformRole` claim sourced from users.platform_role.
admin.MapPost("/tenants/{tenantId:guid}/provision", AdminEndpoints.ProvisionTenant)
    .RequireAuthorization("PlatformOwnerAccess");
admin.MapGet("/tenants/{tenantId:guid}/provisioning", AdminEndpoints.GetTenantProvisioning)
    .RequireAuthorization("PlatformOwnerAccess");
admin.MapPost("/tenants/{tenantId:guid}/deprovision", AdminEndpoints.DeprovisionTenant)
    .RequireAuthorization("PlatformOwnerAccess");

// Story 28-4 AC5 — per-tenant connection pool diagnostics.
// Story 28-R2 / C1: PlatformOwnerAccess (cross-tenant infrastructure state +
// the evict endpoint can disrupt any tenant's request path).
admin.MapGet("/pools/stats", Tamma.Api.Endpoints.Admin.PoolsAdminEndpoints.GetStats)
    .RequireAuthorization("PlatformOwnerAccess");
admin.MapGet("/pools/tenants", Tamma.Api.Endpoints.Admin.PoolsAdminEndpoints.ListTenants)
    .RequireAuthorization("PlatformOwnerAccess");
admin.MapPost("/pools/{tenantId:guid}/evict", Tamma.Api.Endpoints.Admin.PoolsAdminEndpoints.Evict)
    .RequireAuthorization("PlatformOwnerAccess");

// Story 28-11 AC3 — SSE stream of platform events for one tenant.
// Story 28-R2 / C1: PlatformOwnerAccess (cross-tenant infra events).
admin.MapGet("/tenants/{tenantId:guid}/events/stream",
        Tamma.Api.Endpoints.Admin.AdminTenantEventsSseEndpoint.StreamEvents)
    .RequireAuthorization("PlatformOwnerAccess");

// Story 28-6 — admin diagnostics for platform-side queues
// (platform_queued_tasks, platform_email_outbox, platform_events).
// Story 28-R2 / C1: PlatformOwnerAccess (cross-tenant infra state).
admin.MapGet("/diagnostics/platform-queues",
        Tamma.Api.Endpoints.Admin.PlatformQueuesAdminEndpoints.GetDiagnostics)
    .RequireAuthorization("PlatformOwnerAccess");

// Story 28-12 — KEK rotation. Platform-owner only because rotating
// the master key is a once-per-quarter operator action with global
// blast radius.
// Story 28-R2 / C1: PlatformOwnerAccess.
admin.MapPost("/kek/rotate/start", KekRotationEndpoints.Start)
    .RequireAuthorization("PlatformOwnerAccess");
admin.MapGet("/kek/rotate/status", KekRotationEndpoints.GetStatus)
    .RequireAuthorization("PlatformOwnerAccess");
// R2-H3: retry a failed rotation (re-uses the persisted staged
// secondary; idempotent re-run that does NOT mint a fresh KEK).
admin.MapPost("/kek/rotate/retry", KekRotationEndpoints.Retry)
    .RequireAuthorization("PlatformOwnerAccess");

// Story 28-7 deferred-item — platform API-keys CRUD. Platform keys carry
// global auth; only platform admins may mint them.
// Story 28-R2 / C1: PlatformOwnerAccess.
admin.MapPost("/api-keys", AdminApiKeysEndpoints.CreateApiKey).RequireAuthorization("PlatformOwnerAccess");
admin.MapGet("/api-keys", AdminApiKeysEndpoints.ListApiKeys).RequireAuthorization("PlatformOwnerAccess");
admin.MapGet("/api-keys/{id:guid}", AdminApiKeysEndpoints.GetApiKey).RequireAuthorization("PlatformOwnerAccess");
admin.MapDelete("/api-keys/{id:guid}", AdminApiKeysEndpoints.DeleteApiKey).RequireAuthorization("PlatformOwnerAccess");

// Story 28-10 — platform-wide analytics rollup. Each handler reads across
// every tenant regardless of the caller's TenantId.
// Story 28-R2 / C1: PlatformOwnerAccess.
admin.MapGet("/analytics/summary", AdminAnalyticsEndpoints.GetSummary)
    .RequireAuthorization("PlatformOwnerAccess");
admin.MapGet("/analytics/tenants", AdminAnalyticsEndpoints.GetTopTenants)
    .RequireAuthorization("PlatformOwnerAccess");
admin.MapGet("/analytics/events", AdminAnalyticsEndpoints.GetEventHistogram)
    .RequireAuthorization("PlatformOwnerAccess");

// Story 28-11 — platform-admin tenant-status UX. List + detail surface the
// Epic-28 shadow columns on tenants (Status, PlanId, KekVersion,
// FailureReason, DeleteRequestedAt); action endpoints re-drive the Story
// 28-5 workflows (retry / delete / force-delete) under a state-gate that
// returns 409 for illegal transitions.
// Story 28-R2 / C1: PlatformOwnerAccess.
admin.MapGet("/tenants", Tamma.Api.Endpoints.Admin.AdminTenantsEndpoints.ListTenants)
    .RequireAuthorization("PlatformOwnerAccess");
admin.MapGet("/tenants/{tenantId:guid}/detail",
        Tamma.Api.Endpoints.Admin.AdminTenantsEndpoints.GetTenantDetail)
    .RequireAuthorization("PlatformOwnerAccess");
admin.MapPost("/tenants/{tenantId:guid}/actions/retry",
        Tamma.Api.Endpoints.Admin.AdminTenantsEndpoints.RetryTenant)
    .RequireAuthorization("PlatformOwnerAccess");
admin.MapPost("/tenants/{tenantId:guid}/actions/delete",
        Tamma.Api.Endpoints.Admin.AdminTenantsEndpoints.DeleteTenant)
    .RequireAuthorization("PlatformOwnerAccess");
admin.MapPost("/tenants/{tenantId:guid}/actions/force-delete",
        Tamma.Api.Endpoints.Admin.AdminTenantsEndpoints.ForceDeleteTenant)
    .RequireAuthorization("PlatformOwnerAccess");
// Story 28-5 AC7 — operator-triggered cleanup of damaged tenants.
// Story 28-R2 / C1: PlatformOwnerAccess (destructive DDL across DBs).
admin.MapPost("/tenants/{tenantId:guid}/cleanup",
        Tamma.Api.Endpoints.Admin.AdminTenantsEndpoints.CleanupTenant)
    .RequireAuthorization("PlatformOwnerAccess");
admin.MapPatch("/tenants/{tenantId:guid}/plan",
        Tamma.Api.Endpoints.Admin.AdminTenantsEndpoints.UpdateTenantPlan)
    .RequireAuthorization("PlatformOwnerAccess");

// Story 28-R2 follow-up B — platform-admin impersonation surface (SOC2
// audit table + middleware). Begin requires PlatformOwnerAccess (only a
// real platform-admin can mint an impersonation session); end is gated by
// AuthenticatedAny because the impersonation JWT itself carries
// platformRole=user from the target's POV — proof-of-possession of the
// `imp_id` claim is the authorisation. Active-list is platform-owner-only:
// it's the incident-response surface.
admin.MapPost("/tenants/{tenantId:guid}/impersonate",
        Tamma.Api.Endpoints.Admin.AdminImpersonationsEndpoints.BeginImpersonation)
    .RequireAuthorization("PlatformOwnerAccess");
admin.MapGet("/impersonations/active",
        Tamma.Api.Endpoints.Admin.AdminImpersonationsEndpoints.ListActive)
    .RequireAuthorization("PlatformOwnerAccess");
app.MapPost("/api/auth/impersonate/end",
        Tamma.Api.Endpoints.Admin.AdminImpersonationsEndpoints.EndImpersonation)
    .RequireAuthorization("AuthenticatedAny");

// Story 5.6 / 1.5-37 (Wave C.1) — alert-system admin surface.
// Platform-owner only because alert acknowledgment + channel
// configuration carries cross-tenant blast radius. Mounted under
// /api/v1/admin/* per the Wave C.1 brief (new prefix — the
// existing /api/admin routes keep their legacy paths so CI tests
// don't churn).
//
// Story 28-R2 / C1: switched OwnerAccess → PlatformOwnerAccess for every
// alert / channel / rule route.
var v1Admin = app.MapGroup("/api/v1/admin").RequireAuthorization("AdminAccess");
v1Admin.MapGet("/alerts", AlertEndpoints.ListAlerts)
    .RequireAuthorization("PlatformOwnerAccess");
v1Admin.MapGet("/alerts/{id:guid}", AlertEndpoints.GetAlert)
    .RequireAuthorization("PlatformOwnerAccess");
v1Admin.MapPost("/alerts/{id:guid}/acknowledge", AlertEndpoints.AcknowledgeAlert)
    .RequireAuthorization("PlatformOwnerAccess");
v1Admin.MapPost("/alerts/{id:guid}/resolve", AlertEndpoints.ResolveAlert)
    .RequireAuthorization("PlatformOwnerAccess");
v1Admin.MapPost("/alerts/_test", AlertEndpoints.TestRaiseAlert)
    .RequireAuthorization("PlatformOwnerAccess");
v1Admin.MapGet("/alert-channels", AlertEndpoints.ListChannels)
    .RequireAuthorization("PlatformOwnerAccess");
v1Admin.MapPost("/alert-channels", AlertEndpoints.CreateChannel)
    .RequireAuthorization("PlatformOwnerAccess");
v1Admin.MapPatch("/alert-channels/{id:guid}", AlertEndpoints.UpdateChannel)
    .RequireAuthorization("PlatformOwnerAccess");
v1Admin.MapDelete("/alert-channels/{id:guid}", AlertEndpoints.DeleteChannel)
    .RequireAuthorization("PlatformOwnerAccess");

// Story 5.6 (Wave C.2) — alert rule CRUD + synthetic-fire. Same
// PlatformOwnerAccess policy as alerts/channels — configuration here
// carries cross-tenant blast radius.
v1Admin.MapGet("/alert-rules", AlertRuleEndpoints.ListRules)
    .RequireAuthorization("PlatformOwnerAccess");
v1Admin.MapGet("/alert-rules/{id:guid}", AlertRuleEndpoints.GetRule)
    .RequireAuthorization("PlatformOwnerAccess");
v1Admin.MapPost("/alert-rules", AlertRuleEndpoints.CreateRule)
    .RequireAuthorization("PlatformOwnerAccess");
v1Admin.MapPatch("/alert-rules/{id:guid}", AlertRuleEndpoints.UpdateRule)
    .RequireAuthorization("PlatformOwnerAccess");
v1Admin.MapDelete("/alert-rules/{id:guid}", AlertRuleEndpoints.DeleteRule)
    .RequireAuthorization("PlatformOwnerAccess");
v1Admin.MapPost("/alert-rules/{id:guid}/_test", AlertRuleEndpoints.TestFireRule)
    .RequireAuthorization("PlatformOwnerAccess");

// Story 29-3 — platform-scope secret-cabinet create + rotate. Both
// return the newly-minted plaintext via a one-shot reveal token in
// the response (no plaintext bytes in the body); the caller must
// exchange the token through GET /api/v1/secrets/reveal/{token}
// within 60 seconds.
// Story 28-R2 / C1: PlatformOwnerAccess (matches the KEK-rotation precedent
// — a platform-admin operator action with global blast radius).
admin.MapPost("/secrets", SecretEndpoints.CreatePlatformSecret)
    .RequireAuthorization("PlatformOwnerAccess");
admin.MapPost("/secrets/{id:guid}/rotate", SecretEndpoints.RotateSecret)
    .RequireAuthorization("PlatformOwnerAccess");

// Story 29-4 — platform-admin query + lifecycle surface consumed by
// the /admin/secrets UI. Metadata-only; no plaintext ever leaves
// these endpoints (reveal-once is the /reveal/{token} path).
// Story 28-R2 / C1: PlatformOwnerAccess.
admin.MapGet("/secrets", SecretEndpoints.ListPlatformSecrets)
    .RequireAuthorization("PlatformOwnerAccess");
admin.MapGet("/secrets/{id:guid}", SecretEndpoints.GetPlatformSecret)
    .RequireAuthorization("PlatformOwnerAccess");
admin.MapGet("/secrets/{id:guid}/versions", SecretEndpoints.ListPlatformVersions)
    .RequireAuthorization("PlatformOwnerAccess");
admin.MapPost("/secrets/{id:guid}/retire-version/{versionNumber:int}",
        SecretEndpoints.RetirePlatformVersion)
    .RequireAuthorization("PlatformOwnerAccess");

// ── Orgs / Tenants ──
// Path-tenant routes (i.e. /api/v1/orgs/{tenantId}/*) attach the
// RequireTenantMembershipFilter so the handler body can trust the route
// tenant. Tenant-role gating (admin+, owner) is enforced inside each
// handler against HttpContext.Items["TenantRole"] (filter stash) — the
// previous `AdminAccess` / `OwnerAccess` policies checked JWT *platform*
// permissions, not the caller's role within the path tenant, and were the
// root cause of audit findings 001, 012, 013, 020, 021.
var orgs = app.MapGroup("/api/v1/orgs").RequireAuthorization("MemberAccess");
orgs.MapPost("/", OrgEndpoints.CreateOrg);
orgs.MapPost("/invites/accept", OrgEndpoints.AcceptInvite);
// Every tenant-scoped route constrains {tenantId} to :guid so accidental
// path confusion (e.g. `/api/v1/orgs/switch-org` — a legitimate 404) does
// not route through the tenant-membership filter and return 405 for a
// non-matching verb. The Story-18-3 OrgEndpoints.SwitchOrg handler is
// gone (replaced by POST /api/v1/auth/switch-org in Story 28-9); the
// regression test `OrgSwitchOrgRoute404Tests` pins this contract by
// accepting either 404 (preferred) or 405 (acceptable). We deliberately
// do NOT add an explicit `MapMethods("/switch-org", ...)` 404 shim — it
// was tried, but the POST registration forced GET-on-that-URL to 405,
// which broke the GET assertion.
orgs.MapGet("/{tenantId:guid}", OrgEndpoints.GetOrg)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();
orgs.MapPut("/{tenantId:guid}/settings", OrgEndpoints.UpdateOrgSettings)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();
orgs.MapGet("/{tenantId:guid}/members", OrgEndpoints.ListMembers)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();
orgs.MapPut("/{tenantId:guid}/members/{userId:guid}/role", OrgEndpoints.UpdateMemberRole)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();
orgs.MapDelete("/{tenantId:guid}/members/{userId:guid}", OrgEndpoints.RemoveMember)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();
orgs.MapPost("/{tenantId:guid}/invites", OrgEndpoints.CreateInvite)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();
orgs.MapGet("/{tenantId:guid}/invites", OrgEndpoints.ListInvites)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();
orgs.MapDelete("/{tenantId:guid}/invites/{inviteId:guid}", OrgEndpoints.DeleteInvite)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();
// Story 18-7: resend a pending invite (extends expiry, re-dispatches email).
orgs.MapPost("/{tenantId:guid}/invites/{inviteId:guid}/resend", OrgEndpoints.ResendInvite)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();
// Story 18-7: tenant-scoped audit log read for tenant admins.
orgs.MapGet("/{tenantId:guid}/audit", OrgEndpoints.ListTenantAudit)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();
orgs.MapPost("/{tenantId:guid}/transfer-ownership", OrgEndpoints.TransferOwnership)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();
orgs.MapDelete("/{tenantId:guid}", OrgEndpoints.DeleteOrg)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();

// Story 29-3 — tenant-scope secret create. Caller must be a member of
// {tenantId} (RequireTenantMembershipFilter); the endpoint handler
// derives the tenant-role gating from HttpContext.Items["TenantRole"]
// if the admin+ requirement needs enforcing (deferred to 29-4 UI).
orgs.MapPost("/{tenantId:guid}/secrets", SecretEndpoints.CreateTenantSecret)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();

// Story 29-5 — tenant-admin query + lifecycle surface consumed by the
// dash.tamma.dev /secrets UI. Read (list / detail / versions) is
// available to any member; write (rotate / retire) is gated to admin+
// inside each handler. RequireTenantMembershipFilter provides the
// membership proof; the handler body does the admin check.
orgs.MapGet("/{tenantId:guid}/secrets", SecretEndpoints.ListTenantSecrets)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();
orgs.MapGet("/{tenantId:guid}/secrets/{id:guid}", SecretEndpoints.GetTenantSecret)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();
orgs.MapGet("/{tenantId:guid}/secrets/{id:guid}/versions",
        SecretEndpoints.ListTenantVersions)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();
orgs.MapPost("/{tenantId:guid}/secrets/{id:guid}/rotate",
        SecretEndpoints.RotateTenantSecret)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();
orgs.MapPost("/{tenantId:guid}/secrets/{id:guid}/retire-version/{versionNumber:int}",
        SecretEndpoints.RetireTenantVersion)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();

// Story 28-7 deferred-item — tenant-scoped API keys. Membership filter
// guards path-tenant access; the handler body enforces admin+ role before
// mutations (minting credentials is destructive).
orgs.MapPost("/{tenantId:guid}/api-keys", OrgApiKeysEndpoints.CreateApiKey)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();
orgs.MapGet("/{tenantId:guid}/api-keys", OrgApiKeysEndpoints.ListApiKeys)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();
orgs.MapGet("/{tenantId:guid}/api-keys/{id:guid}", OrgApiKeysEndpoints.GetApiKey)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();
orgs.MapDelete("/{tenantId:guid}/api-keys/{id:guid}", OrgApiKeysEndpoints.DeleteApiKey)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();

// Story 18-5 — user-facing dashboard endpoints. Same path-tenant gate as
// the rest of /api/v1/orgs/{tenantId}/* (findings 001, 024). These mirror
// /api/dashboard/* in purpose but are strictly scoped to the route tenant,
// so a member of org A cannot peek at org B's events / runs / stats.
orgs.MapGet("/{tenantId:guid}/dashboard/summary", UserDashboardEndpoints.GetOrgSummary)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();
orgs.MapGet("/{tenantId:guid}/dashboard/runs", UserDashboardEndpoints.GetRecentRuns)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();
orgs.MapGet("/{tenantId:guid}/dashboard/stats", UserDashboardEndpoints.GetStats)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();

// Story 5.6 / 1.5-37 (Wave C.3) — tenant-scope alert surface.
// The path-tenant membership filter proves the caller is a member;
// admin+ gating for mutations lives inline in the handlers
// (AlertEndpoints.RequireTenantAdmin). Cross-tenant leaks are
// prevented by hard-coded `TenantId == tenantId` filters on every
// query — a plain `FindAsync(id)` would have been a bug.
orgs.MapGet("/{tenantId:guid}/alerts", AlertEndpoints.ListTenantAlerts)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();
orgs.MapGet("/{tenantId:guid}/alerts/{id:guid}", AlertEndpoints.GetTenantAlert)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();
orgs.MapPost("/{tenantId:guid}/alerts/{id:guid}/acknowledge",
        AlertEndpoints.AcknowledgeTenantAlert)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();
orgs.MapPost("/{tenantId:guid}/alerts/{id:guid}/resolve",
        AlertEndpoints.ResolveTenantAlert)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();
orgs.MapGet("/{tenantId:guid}/alert-channels", AlertEndpoints.ListTenantChannels)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();
orgs.MapPost("/{tenantId:guid}/alert-channels", AlertEndpoints.CreateTenantChannel)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();
orgs.MapPatch("/{tenantId:guid}/alert-channels/{id:guid}",
        AlertEndpoints.UpdateTenantChannel)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();
orgs.MapDelete("/{tenantId:guid}/alert-channels/{id:guid}",
        AlertEndpoints.DeleteTenantChannel)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();

app.MapGet("/api/v1/tenants", OrgEndpoints.ListTenants).RequireAuthorization("MemberAccess");
// Story 28-5 AC6 — public polling endpoint for the onboarding flow.
// Allow-listed for users with a membership for {id} (or platform
// owner). Accessible during provisioning so the onboarding poller can
// see step progress before the tenant flips to active.
app.MapGet("/api/v1/tenants/{tenantId:guid}/status",
        TenantStatusEndpoint.GetStatus)
    .RequireAuthorization("AuthenticatedAny");

// Story 29-3 — reveal-once token exchange. The token IS the auth (a
// 256-bit bearer secret) so the route is not behind MemberAccess — a
// caller with the token can exchange it exactly once, and the rate
// limit on SecretReveal (10/min/user or anon) frustrates brute-force
// guessing attempts without needing a login.
app.MapGet("/api/v1/secrets/reveal/{token}", SecretEndpoints.RevealSecret)
    .RequireRateLimiting("SecretReveal");

// ── Onboarding wizard (Story 18-4) ──
// Status is the polling endpoint the dashboard wizard hits every few
// seconds while the user is on the GitHub install page; install-github is
// a 302 → github.com/apps/<slug>/installations/new with a signed `state`
// param so the existing GitHubEndpoints.Callback can re-bind the new
// install to the user's active tenant.
app.MapGet("/api/v1/onboarding/status", OnboardingEndpoints.GetStatus)
    .RequireAuthorization("MemberAccess");
app.MapGet("/api/v1/onboarding/install-github", OnboardingEndpoints.InstallGitHub)
    .RequireAuthorization("MemberAccess");

// ── Agents Config ──
// Rate limit (finding 020): ConfigRead default for the group; ConfigWrite
// override on the PUT.
var agents = app.MapGroup("/api/v1/agents")
    .RequireAuthorization("SettingsView")
    .RequireRateLimiting("ConfigRead");
agents.MapGet("/config", AgentEndpoints.GetConfig);
agents.MapPut("/config", AgentEndpoints.UpdateConfig)
    .RequireAuthorization("SettingsManage")
    .RequireRateLimiting("ConfigWrite");
agents.MapPost("/config/validate", AgentEndpoints.ValidateConfig);
agents.MapGet("/{role}/resolve", AgentEndpoints.ResolveAgent);
agents.MapPost("/resolve-for-phase", AgentEndpoints.ResolveForPhase);

// ── Prompts ──
// CLAUDE.md "Prompt Store Architecture > API" defines /defaults as the canonical
// read-only system-default URL. Both /system (legacy TS naming) and /defaults
// (CLAUDE.md naming) are wired so existing dashboard/CLI clients keep working
// while new integrators can follow the spec verbatim. POST /reset is a
// documented alias for DELETE (see CLAUDE.md).
var prompts = app.MapGroup("/api/prompts").RequireAuthorization("SettingsView");
prompts.MapGet("/", PromptEndpoints.ListAll);
// System-default reads — both naming conventions point to the same handler
prompts.MapGet("/system", PromptEndpoints.ListSystemDefaults);
prompts.MapGet("/defaults", PromptEndpoints.ListSystemDefaults);
prompts.MapGet("/system/{role}/{action}", PromptEndpoints.GetSystemDefault);
prompts.MapGet("/defaults/{role}/{action}", PromptEndpoints.GetSystemDefault);
prompts.MapGet("/defaults/{action}", PromptEndpoints.GetActionDefault);
// Resolved (per-user) reads + mutations.
// Story 27-3 — PUT/DELETE/POST-reset gated by `PromptManage` (admin+owner)
// instead of `SettingsManage` (owner-only). CLAUDE.md "Prompt Store
// Architecture / RBAC" requires tenant_admin to be able to manage tenant
// overrides in SaaS mode; the legacy SettingsManage gate would 403 every
// admin-role caller. Single-user mode is unaffected — every signed-up user
// is auto-`owner` of their personal tenant.
prompts.MapGet("/{role}/{action}", PromptEndpoints.GetPrompt);
prompts.MapPut("/{role}/{action}", PromptEndpoints.UpsertPrompt).RequireAuthorization("PromptManage");
prompts.MapDelete("/{role}/{action}", PromptEndpoints.DeletePrompt).RequireAuthorization("PromptManage");
prompts.MapPost("/{role}/{action}/reset", PromptEndpoints.DeletePrompt).RequireAuthorization("PromptManage");
// Role-system overrides (preamble) — CLAUDE.md role-system scope is keyed by
// (userId, role) only; no action axis. Dropped the {action} URL segment to
// match (audit prompts/005).
prompts.MapPut("/system/{role}", PromptEndpoints.UpsertSystemPrompt).RequireAuthorization("PromptManage");
prompts.MapDelete("/system/{role}", PromptEndpoints.DeleteSystemPrompt).RequireAuthorization("PromptManage");
prompts.MapPost("/{role}/{action}/render", PromptEndpoints.RenderPrompt);

// ── Convention Templates (no auth) ──
app.MapGet("/api/convention-templates", ConventionEndpoints.ListAll);
app.MapGet("/api/convention-templates/{key}", ConventionEndpoints.GetByKey);

// ── Settings / Config ──
// Rate limit (finding 020): ConfigRead default for the group; ConfigWrite
// override on each write surface. Sanitize is a runtime POST and shares the
// write quota since it can be expensive to run hot.
var config = app.MapGroup("/api/config")
    .RequireAuthorization("SettingsView")
    .RequireRateLimiting("ConfigRead");
config.MapGet("/agents", SettingsEndpoints.GetAgentsConfig);
config.MapPut("/agents", SettingsEndpoints.UpdateAgentsConfig)
    .RequireAuthorization("SettingsManage").RequireRateLimiting("ConfigWrite");
config.MapGet("/security", SettingsEndpoints.GetSecurityConfig);
config.MapPut("/security", SettingsEndpoints.UpdateSecurityConfig)
    .RequireAuthorization("SettingsManage").RequireRateLimiting("ConfigWrite");
config.MapPost("/sanitize", SettingsEndpoints.Sanitize)
    .RequireAuthorization("SettingsManage").RequireRateLimiting("ConfigWrite");
config.MapGet("/sanitize/rules", SettingsEndpoints.GetSanitizationRules);
config.MapPut("/sanitize/rules", SettingsEndpoints.UpdateSanitizationRules)
    .RequireAuthorization("SettingsManage").RequireRateLimiting("ConfigWrite");
config.MapGet("/prompts", SettingsEndpoints.GetPromptsConfig);
config.MapPut("/prompts/{role}", SettingsEndpoints.UpdatePromptsConfig)
    .RequireAuthorization("SettingsManage").RequireRateLimiting("ConfigWrite");
config.MapGet("/providers", SettingsEndpoints.GetProvidersConfig);
config.MapPut("/providers", SettingsEndpoints.UpdateProvidersConfig)
    .RequireAuthorization("SettingsManage").RequireRateLimiting("ConfigWrite");

// ── Providers ──
// Rate limit (finding 020): ConfigRead for the group; per-route policies on
// ingest (high-volume from Elsa workers) and execute (expensive).
var providers = app.MapGroup("/api/providers")
    .RequireAuthorization("SettingsView")
    .RequireRateLimiting("ConfigRead");
providers.MapGet("/health", ProviderEndpoints.GetHealthSummary);
providers.MapGet("/health/providers", ProviderEndpoints.ListProviderHealth);
providers.MapGet("/health/providers/{key}", ProviderEndpoints.GetProviderHealth);
providers.MapPost("/health/providers/{key}/failure", ProviderEndpoints.RecordFailure)
    .RequireAuthorization("SettingsManage").RequireRateLimiting("ConfigWrite");
providers.MapPost("/health/providers/{key}/success", ProviderEndpoints.RecordSuccess)
    .RequireAuthorization("SettingsManage").RequireRateLimiting("ConfigWrite");
providers.MapPost("/health/providers/{key}/reset", ProviderEndpoints.ResetProvider)
    .RequireAuthorization("SettingsManage").RequireRateLimiting("ConfigWrite");
providers.MapPost("/chain/resolve", ProviderEndpoints.ResolveChain);
providers.MapGet("/diagnostics", ProviderEndpoints.GetDiagnostics);
providers.MapGet("/diagnostics/query", ProviderEndpoints.QueryDiagnostics);
providers.MapGet("/diagnostics/report", ProviderEndpoints.GetReport);
providers.MapGet("/diagnostics/budget/{accountId}", ProviderEndpoints.GetBudget);
providers.MapPut("/diagnostics/budget/{accountId}", ProviderEndpoints.UpdateBudget)
    .RequireAuthorization("SettingsManage").RequireRateLimiting("ConfigWrite");
providers.MapPost("/diagnostics", ProviderEndpoints.IngestDiagnostic)
    .RequireAuthorization("SettingsManage").RequireRateLimiting("ProviderIngest");
// Batch ingest restored from TS (finding 010) — accepts up to 100 records.
providers.MapPost("/diagnostics/batch", ProviderEndpoints.IngestDiagnosticBatch)
    .RequireAuthorization("SettingsManage").RequireRateLimiting("ProviderIngest");
providers.MapPost("/providers/create", ProviderEndpoints.CreateProvider)
    .RequireAuthorization("SettingsManage").RequireRateLimiting("ProviderExecute");
providers.MapPost("/providers/{handle}/execute", ProviderEndpoints.ExecuteProvider)
    .RequireAuthorization("SettingsManage").RequireRateLimiting("ProviderExecute");
providers.MapDelete("/providers/{handle}", ProviderEndpoints.DeleteProvider)
    .RequireAuthorization("SettingsManage").RequireRateLimiting("ConfigWrite");
providers.MapGet("/providers/sessions", ProviderEndpoints.ListSessions);

// ── Engine ──
var engine = app.MapGroup("/api/engine").RequireAuthorization("WorkflowsView");
engine.MapPost("/command", EngineEndpoints.SendCommand).RequireAuthorization("WorkflowsManage");
engine.MapGet("/state", EngineEndpoints.GetState);
engine.MapGet("/stats", EngineEndpoints.GetStats);
engine.MapGet("/plan", EngineEndpoints.GetPlan);
engine.MapGet("/history", EngineEndpoints.GetHistory);
engine.MapGet("/events/state", EngineEndpoints.GetEventsState);
engine.MapGet("/events/logs", EngineEndpoints.GetEventsLogs);
engine.MapPost("/store-context", EngineEndpoints.StoreContext).RequireAuthorization("WorkflowsManage");
engine.MapGet("/context/{issueNumber}", EngineEndpoints.GetContext);
engine.MapPost("/query-context", EngineEndpoints.QueryContext);
engine.MapGet("/repo-config", EngineEndpoints.GetRepoConfig);
engine.MapGet("/issues", EngineEndpoints.GetIssues);
engine.MapGet("/security-alerts", EngineEndpoints.GetSecurityAlerts);
engine.MapPost("/issue-comment", EngineEndpoints.PostIssueComment).RequireAuthorization("WorkflowsManage");
engine.MapPost("/issue-labels", EngineEndpoints.PostIssueLabels).RequireAuthorization("WorkflowsManage");
engine.MapDelete("/issue-labels/{repo}/{issueNumber}/{label}", EngineEndpoints.DeleteIssueLabel).RequireAuthorization("WorkflowsManage");
engine.MapPost("/create-issue", EngineEndpoints.CreateIssue).RequireAuthorization("WorkflowsManage");
engine.MapPost("/trigger-ci", EngineEndpoints.TriggerCi).RequireAuthorization("WorkflowsManage");
engine.MapPost("/execute-task", EngineEndpoints.ExecuteTask).RequireAuthorization("WorkflowsManage");
engine.MapPost("/cycle-result", EngineEndpoints.PostCycleResult).RequireAuthorization("WorkflowsManage");
engine.MapGet("/cycle-results", EngineEndpoints.GetCycleResults);
// Audit finding 002 — `agent-available` is a GET liveness probe (no body),
// not a POST registration call. The previous wiring as POST silently drifted
// from the TS contract.
engine.MapGet("/agent-available", EngineEndpoints.AgentAvailable);

// ── Workflows ──
var workflows = app.MapGroup("/api/workflows").RequireAuthorization("WorkflowsView");
workflows.MapPost("/definitions", WorkflowEndpoints.CreateDefinition).RequireAuthorization("WorkflowsManage");
workflows.MapGet("/definitions", WorkflowEndpoints.ListDefinitions);
workflows.MapPost("/instances", WorkflowEndpoints.CreateInstance).RequireAuthorization("WorkflowsManage");
workflows.MapPut("/instances/{id}", WorkflowEndpoints.UpdateInstance).RequireAuthorization("WorkflowsManage");
workflows.MapGet("/instances", WorkflowEndpoints.ListInstances);
workflows.MapPost("/instances/{id}/cancel", WorkflowEndpoints.CancelInstance).RequireAuthorization("WorkflowsManage");
// Story 16-5 AC 7: workflow instance deletion is owner-only via WorkflowsDelete
// (workflows:delete -> ["owner"]). Cancel stays admin/owner via WorkflowsManage.
workflows.MapDelete("/instances/{id}", WorkflowEndpoints.DeleteInstance).RequireAuthorization("WorkflowsDelete");
workflows.MapGet("/instances/{id}/events", WorkflowEndpoints.GetInstanceEvents);

// ── GitHub App (no auth, webhook signature verification) ──
// Audit finding 017 — webhook gets the GitHubWebhook policy (300/min). The
// install callback shares OAuthStart's 60/min cap; legitimate installs are
// rare events but the route is publicly reachable.
var github = app.MapGroup("/api/github");
github.MapGet("/callback", GitHubEndpoints.Callback)
    .RequireRateLimiting("OAuthStart");
github.MapPost("/webhooks", GitHubEndpoints.Webhooks)
    .RequireRateLimiting("GitHubWebhook");

// ── SaaS (API key auth) ──
app.MapPost("/api/v1/llm/chat", SaaSEndpoints.LlmChat).RequireAuthorization();
app.MapPost("/api/v1/workflows/{id}/status", SaaSEndpoints.UpdateWorkflowStatus).RequireAuthorization();
app.MapPost("/api/v1/workflows/{id}/result", SaaSEndpoints.PostWorkflowResult).RequireAuthorization();
app.MapPost("/api/v1/installations/{id}/rotate-key", SaaSEndpoints.RotateInstallationKey).RequireAuthorization();

// ── Dashboard ──
var dashboard = app.MapGroup("/api/dashboard").RequireAuthorization("DashboardView");
dashboard.MapGet("/summary", DashboardEndpoints.GetSummary);
dashboard.MapGet("/engines", DashboardEndpoints.GetEngines);
dashboard.MapGet("/workflows", DashboardEndpoints.GetWorkflows);

// ── Knowledge Base (30 stub routes) ──
var kb = app.MapGroup("/api/kb").RequireAuthorization("SettingsView");
// Index (6)
kb.MapGet("/index/status", KbEndpoints.GetIndexStatus);
kb.MapPost("/index/trigger", KbEndpoints.TriggerIndex).RequireAuthorization("SettingsManage");
kb.MapGet("/index/config", KbEndpoints.GetIndexConfig);
kb.MapPut("/index/config", KbEndpoints.UpdateIndexConfig).RequireAuthorization("SettingsManage");
kb.MapGet("/index/stats", KbEndpoints.GetIndexStats);
kb.MapDelete("/index", KbEndpoints.ClearIndex).RequireAuthorization("SettingsManage");
// Vector DB (6)
kb.MapGet("/vector-db/status", KbEndpoints.GetVectorDbStatus);
kb.MapPost("/vector-db/search", KbEndpoints.SearchVectors);
kb.MapPost("/vector-db/upsert", KbEndpoints.UpsertVectors).RequireAuthorization("SettingsManage");
kb.MapDelete("/vector-db/delete", KbEndpoints.DeleteVectors).RequireAuthorization("SettingsManage");
kb.MapGet("/vector-db/collections", KbEndpoints.GetVectorCollections);
kb.MapGet("/vector-db/stats", KbEndpoints.GetVectorStats);
// RAG (4)
kb.MapGet("/rag/config", KbEndpoints.GetRagConfig);
kb.MapPut("/rag/config", KbEndpoints.UpdateRagConfig).RequireAuthorization("SettingsManage");
kb.MapPost("/rag/query", KbEndpoints.QueryRag);
kb.MapGet("/rag/metrics", KbEndpoints.GetRagMetrics);
// MCP (8)
kb.MapGet("/mcp/servers", KbEndpoints.ListMcpServers);
kb.MapGet("/mcp/servers/{id}", KbEndpoints.GetMcpServer);
kb.MapPost("/mcp/servers/{id}/start", KbEndpoints.StartMcpServer).RequireAuthorization("SettingsManage");
kb.MapPost("/mcp/servers/{id}/stop", KbEndpoints.StopMcpServer).RequireAuthorization("SettingsManage");
kb.MapGet("/mcp/config", KbEndpoints.GetMcpConfig);
kb.MapPut("/mcp/config", KbEndpoints.UpdateMcpConfig).RequireAuthorization("SettingsManage");
kb.MapGet("/mcp/tools", KbEndpoints.ListMcpTools);
kb.MapPost("/mcp/tools/invoke", KbEndpoints.InvokeMcpTool).RequireAuthorization("SettingsManage");
// Context (3)
kb.MapGet("/context/history", KbEndpoints.GetContextHistory);
kb.MapPost("/context/feedback", KbEndpoints.PostContextFeedback).RequireAuthorization("SettingsManage");
kb.MapGet("/context/config", KbEndpoints.GetContextConfig);
// Analytics (3)
kb.MapGet("/analytics", KbEndpoints.GetKbAnalytics);
kb.MapGet("/analytics/usage", KbEndpoints.GetKbUsage);
kb.MapGet("/analytics/costs", KbEndpoints.GetKbCosts);

// ────────────────────────────────────────────────────────────────────────────
// Database Migration
//
// On first-ever deploy (no EF migration history), any legacy tables from the
// previous TypeScript API / raw-SQL mentorship schema would collide with the
// InitialSchema migration. Drop them before applying migrations — per the
// Epic 19 wipe-and-recreate directive. Subsequent deploys apply migrations
// incrementally without any cleanup.
// ────────────────────────────────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
    try
    {
        // Per the Epic 19 wipe-and-recreate directive ("nothing exists
        // important, wipe and recreate"): drop all Tamma-managed tables
        // including the EF migration history on every deploy, then let
        // Migrate() rebuild from InitialSchema + all subsequent migrations.
        //
        // This is destructive. Do NOT adopt this pattern in an environment
        // with user data you care about. It exists to unstick the EF Core
        // + Npgsql history-table race that crashed two consecutive deploys
        // with SqlState=42P07 on __TammaMigrationsHistory.
        //
        // TAMMA_PRESERVE_DB=1 opts out — Migrate() runs incrementally,
        // preserving data but risking the 42P07 collision until EF/Npgsql
        // resolves it.
        var preserveDb = string.Equals(
            Environment.GetEnvironmentVariable("TAMMA_PRESERVE_DB"),
            "1", StringComparison.Ordinal);

        if (!preserveDb)
        {
            Log.Information("Wiping Tamma-managed public-schema tables (TAMMA_PRESERVE_DB not set)");
            dbContext.Database.ExecuteSqlRaw(@"
                DROP TABLE IF EXISTS
                    admin_impersonations,
                    alert_delivery_attempts, alert_channels, alerts,
                    alert_evaluator_cursor, alert_rules,
                    api_keys, agent_configs, budget_configs, domain_events,
                    email_outbox,
                    github_installation_repos, github_installations,
                    github_webhook_deliveries,
                    junior_developers, kek_rotations,
                    mentorship_events, mentorship_sessions,
                    password_reset_tokens, plans,
                    platform_analytics_hourly,
                    platform_api_key_index,
                    platform_bootstrap,
                    platform_email_outbox, platform_events, platform_queued_tasks,
                    prompt_overrides,
                    provider_diagnostics, provider_health, queued_tasks, refresh_tokens,
                    sanitization_rules, stories, tenant_memberships, tenant_invites, tenants,
                    tenant_platform_installations,
                    user_api_keys, user_installations, user_invites, users,
                    workflow_definitions, workflow_instances,
                    knex_migrations, knex_migrations_lock,
                    ""__TammaMigrationsHistory""
                CASCADE;");
        }

        dbContext.Database.Migrate();
        Log.Information("Database migrations applied successfully ({Count} total)",
            dbContext.Database.GetAppliedMigrations().Count());
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Fatal: database migration failed");
        throw;
    }
}

Log.Information("Tamma API starting up...");

app.Run();
return 0;

// Make Program class accessible for testing
public partial class Program { }
