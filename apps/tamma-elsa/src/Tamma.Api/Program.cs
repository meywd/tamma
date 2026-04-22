using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using Serilog.Sinks.OpenSearch;
using Tamma.Api.Auth;
using Tamma.Api.Endpoints;
using Tamma.Api.Extensions;
using Tamma.Api.Infrastructure;
using Tamma.Api.Middleware;
using Tamma.Api.Services;
using Tamma.Core.Interfaces;
using Tamma.Data;
using Tamma.Data.Repositories;

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

builder.Services.AddTammaData(connectionString, appConnectionString, controlPlaneConnectionString);

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
builder.Services.AddHealthChecks()
    .AddNpgSql(connectionString, tags: new[] { "ready" });

// Admin health aggregator (per-service ping fan-out for the dashboard).
// Mirrors the TS /api/admin/health behavior.
builder.Services.AddScoped<IAdminHealthService, AdminHealthService>();

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
        foreach (var name in new[] { "AdminAccess", "OwnerAccess", "MemberAccess", "SettingsView",
            "SettingsManage", "WorkflowsView", "WorkflowsManage", "WorkflowsDelete", "DashboardView", "ApiKeysManage",
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
// stays in place for any future `/api/v1/orgs/switch` wiring but is no
// longer mounted under /auth.
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
admin.MapPut("/users/{id}/role", AdminEndpoints.UpdateUserRole).RequireAuthorization("OwnerAccess");
admin.MapDelete("/users/{id}", AdminEndpoints.DeleteUser).RequireAuthorization("OwnerAccess");
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
admin.MapPost("/tenants/{tenantId:guid}/provision", AdminEndpoints.ProvisionTenant)
    .RequireAuthorization("OwnerAccess");
admin.MapGet("/tenants/{tenantId:guid}/provisioning", AdminEndpoints.GetTenantProvisioning)
    .RequireAuthorization("OwnerAccess");
admin.MapPost("/tenants/{tenantId:guid}/deprovision", AdminEndpoints.DeprovisionTenant)
    .RequireAuthorization("OwnerAccess");

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

orgs.MapGet("/{tenantId}", OrgEndpoints.GetOrg)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();
orgs.MapPut("/{tenantId}/settings", OrgEndpoints.UpdateOrgSettings)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();
orgs.MapGet("/{tenantId}/members", OrgEndpoints.ListMembers)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();
orgs.MapPut("/{tenantId}/members/{userId}/role", OrgEndpoints.UpdateMemberRole)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();
orgs.MapDelete("/{tenantId}/members/{userId}", OrgEndpoints.RemoveMember)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();
orgs.MapPost("/{tenantId}/invites", OrgEndpoints.CreateInvite)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();
orgs.MapGet("/{tenantId}/invites", OrgEndpoints.ListInvites)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();
orgs.MapDelete("/{tenantId}/invites/{inviteId}", OrgEndpoints.DeleteInvite)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();
// Story 18-7: resend a pending invite (extends expiry, re-dispatches email).
orgs.MapPost("/{tenantId}/invites/{inviteId}/resend", OrgEndpoints.ResendInvite)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();
// Story 18-7: tenant-scoped audit log read for tenant admins.
orgs.MapGet("/{tenantId}/audit", OrgEndpoints.ListTenantAudit)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();
orgs.MapPost("/{tenantId}/transfer-ownership", OrgEndpoints.TransferOwnership)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();
orgs.MapDelete("/{tenantId}", OrgEndpoints.DeleteOrg)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();

app.MapGet("/api/v1/tenants", OrgEndpoints.ListTenants).RequireAuthorization("MemberAccess");

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
// Resolved (per-user) reads + mutations
prompts.MapGet("/{role}/{action}", PromptEndpoints.GetPrompt);
prompts.MapPut("/{role}/{action}", PromptEndpoints.UpsertPrompt).RequireAuthorization("SettingsManage");
prompts.MapDelete("/{role}/{action}", PromptEndpoints.DeletePrompt).RequireAuthorization("SettingsManage");
prompts.MapPost("/{role}/{action}/reset", PromptEndpoints.DeletePrompt).RequireAuthorization("SettingsManage");
// Role-system overrides (preamble) — CLAUDE.md role-system scope is keyed by
// (userId, role) only; no action axis. Dropped the {action} URL segment to
// match (audit prompts/005).
prompts.MapPut("/system/{role}", PromptEndpoints.UpsertSystemPrompt).RequireAuthorization("SettingsManage");
prompts.MapDelete("/system/{role}", PromptEndpoints.DeleteSystemPrompt).RequireAuthorization("SettingsManage");
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
    var dbContext = scope.ServiceProvider.GetRequiredService<TammaDbContext>();
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
                    api_keys, agent_configs, budget_configs, domain_events,
                    email_outbox,
                    github_installation_repos, github_installations,
                    github_webhook_deliveries,
                    junior_developers, mentorship_events, mentorship_sessions,
                    password_reset_tokens, prompt_overrides,
                    provider_diagnostics, provider_health, queued_tasks, refresh_tokens,
                    sanitization_rules, stories, tenant_memberships, tenant_invites, tenants,
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

// Make Program class accessible for testing
public partial class Program { }
