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
using Tamma.Api.Services.Secrets.Rotation;
using Tamma.Api.Middleware;
using Tamma.Api.Services;
using Tamma.Api.Services.Provisioning.V2;
using Tamma.Core.Interfaces;
using Tamma.Api.Services.PlatformTasks;
using Tamma.Api.Services.Webhooks;
using Tamma.Data;
using Tamma.Data.Pooling;
using Tamma.Data.Repositories;
using Tamma.Platforms.Gitea;
using Tamma.Platforms.GitHub;
using Tamma.Platforms.GitLab;

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
// Integration BYOK — the credential-bound JIRA client (JiraApiClient) rides this
// named client. It carries NO base address (each call targets the per-tenant
// baseUrl) and is hardened against SSRF: redirects are NOT auto-followed (a 3xx to
// an internal host is refused, not chased), and the connect callback re-checks the
// resolved address at connect time so a host that passed URL validation but rebinds
// its DNS to a private/metadata address cannot be reached.
builder.Services.AddHttpClient(Tamma.Api.Services.Integrations.JiraApiClient.HttpClientName)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectCallback = Tamma.Api.Services.Integrations.JiraBaseUrlGuard.SafeConnectAsync,
    });

// ────────────────────────────────────────────────────────────────────────────
// Database + repositories (via extension method)
//
// Dual-connection-string architecture:
//   - TammaDb        → admin / migrations / background services (superuser)
//   - TammaAppDb     → per-request runtime, role=tamma_app (least-privilege
//                      runtime role; tenant isolation itself is schema +
//                      per-tenant role under the unified tenancy model)
//
// For backward compat with older configs, `DefaultConnection` still
// works: it's treated as the admin string when TammaDb isn't set. If
// TammaAppDb is absent, it falls through to the admin connection with a
// warning — dev-mode single-role Postgres continues to function, but
// production must set TammaAppDb explicitly.
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
        + "admin connection for per-request DbContexts. The least-privilege "
        + "tamma_app role is bypassed until the app-role connection is wired. "
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

// ── Story 28-4 / unified-tenancy Phase 3 — tenant connection pool ──
//
// The LRU-cached LruPooledTenantConnectionResolver is the ONLY tenant
// connection path: every per-tenant DbContext build (every
// TenantDbContextFactory.CreateAsync) resolves the tenant's stored
// encrypted connection string through it. Registered UNCONDITIONALLY —
// the transitional StubTenantConnectionResolver and the
// Tamma:RequireTenantIsolation startup guard were removed in Phase 3.
//
// Wired AFTER AddTammaData. The CP connection string is optional: when
// set, the resolver's cold-miss tenant-row lookups go through a pooled
// IDbContextFactory<ControlPlaneDbContext> on the dedicated CP database;
// when unset (dev / self-host — the CP IS the central DB), the factory
// AddTammaData registered on the central connection is used as-is.
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

// Story 28-12 AC5 residual — OTel ObservableGauge
// `tamma.kek_rotation.remaining` reading "tenants still needing rekey"
// from the coordinator's in-memory status snapshot. Constructing this
// singleton instantiates the Meter ("Tamma.KekRotation") whose gauge is
// then discoverable by any wired MeterProvider; resolve it eagerly so
// the meter exists from process start rather than first /status poll.
builder.Services.AddSingleton<Tamma.Api.Services.Secrets.KekRotationMetrics>();

// R2-H13 — readiness probe refuses to flip green when there are
// tenant rows further behind than the cabinet history can decrypt.
builder.Services.AddSingleton<Tamma.Api.Services.Secrets.KekCabinetHealthCheck>();

// ── Story 28-12 AC1+AC2 (2026-05-30 residual #3) ─────────────────────
// Runtime least-privilege assertion: probe `SELECT current_user` on the
// app connection and refuse readiness in Production if the API is running
// as tamma_provisioner / tamma_admin (privileged) instead of tamma_app.
// Outside Production it's a warning only (dev/test run as a single default
// role with no split — keeps the suite green). Captures the resolved app
// connection string + environment so the check is self-contained.
builder.Services.AddSingleton(sp =>
    new Tamma.Api.Services.Secrets.DbRoleLeastPrivilegeCheck(
        appConnectionString,
        builder.Environment.IsProduction(),
        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<
            Tamma.Api.Services.Secrets.DbRoleLeastPrivilegeCheck>>()));

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
// Bridge from oauth2-proxy (browser auth gateway) to Tamma's tamma_session
// JWT. The middleware is wired in the request pipeline below; the
// HttpClient feeds it /oauth2/userinfo lookups.
builder.Services.AddHttpClient("oauth2-proxy", client =>
{
    client.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddScoped<Tamma.Api.Middleware.ProxyHeaderAuthMiddleware>();
// Hardening workstreams — ported from the deleted TS API services.
// Each extension method owns its own service registrations.
builder.Services.AddPromptStoreServices();
// Story 39-5 — acceptance-rules resolver + events + tool factory. The service
// implements IAcceptanceRulesResolver (consumed by the 39-17 tool factory and,
// in later stories, the 39-6 workflow). The GetAcceptanceRulesTool itself is NOT
// registered as an IToolExecutor (Design Decision D6) — the factory mints
// principal-bound instances per tenant-agent session.
builder.Services.AddScoped<Tamma.Api.Services.AcceptanceRules.AcceptanceRulesEventsService>();
builder.Services.AddScoped<Tamma.Api.Services.AcceptanceRules.AcceptanceRulesService>();
builder.Services.AddScoped<Tamma.Core.Documents.Policy.IAcceptanceRulesResolver>(
    sp => sp.GetRequiredService<Tamma.Api.Services.AcceptanceRules.AcceptanceRulesService>());
builder.Services.AddScoped<Tamma.Api.Services.AcceptanceRules.GetAcceptanceRulesToolFactory>();
// Story 39-8 — the escalation disposition surface (appends ESCALATION.RESOLVED, FAIL-LOUD).
builder.Services.AddScoped<Tamma.Api.Services.Documents.EscalationDispositionService>();
builder.Services.AddProviderHealthServices();
builder.Services.AddDiagnosticsServices();
builder.Services.AddSanitizationServices();
builder.Services.AddAgentResolverServices();
builder.Services.AddGitHubInstallationServices(builder.Configuration);
// Wave 2
builder.Services.AddConventionServices();
builder.Services.AddEmailServices();
// Story 38-3 (Epic 38, Class D) — Slack notification outbox sender (sole webhook
// credential holder). Gated off automatically when Slack:WebhookUrl is unset.
builder.Services.AddSlackNotificationServices();
builder.Services.AddTaskQueue();
builder.Services.AddProviderSessionServices();
builder.Services.AddSaaSServices();
// Per-tenant provisioning (Cranl). When Cranl:ApiKey + Cranl:OrganizationId
// are both configured, the Cranl-backed provisioner + workflow + queue
// handler are wired; otherwise the Null seam mints no external resources —
// tenant placement stays on the unified tenant_databases pool (central DB
// by default). See docs/vendors/cranl/README.md for the provisioning flow.
builder.Services.AddTenantProvisioning(builder.Configuration);

// Platform secret cabinet (Epic 29). Backend selection (Story 29-2):
//   • KEK configured (TAMMA_SECRET_STORE_KEK_PRIMARY) + a secret-store
//     connection string → the PERSISTENT Postgres envelope-encrypted
//     backend (AddTammaPostgresSecrets). This is the REQUIRED production
//     wiring: BYOK ciphertext survives restart. It ALSO registers
//     IKekProvider (which the reveal + rotation writers require) and the
//     Story 29-1 ISecretStore facade.
//   • KEK absent → the VOLATILE in-memory placeholder, which is safe
//     ONLY for dev/test. In Production we FAIL LOUD rather than silently
//     backing real (tenant BYOK) secrets with volatile memory — a
//     persistent secret whose ciphertext evaporates on restart is the
//     exact silent failure this project forbids. The fail-closed backend
//     lets startup succeed (health answers) but throws on any secret
//     WRITE with a clear remediation message. Mirrors
//     TenantSecretProtector's env-gated production hard-fail.
// Resolve the secret-store connection the SAME way the ControlPlane
// DbContext binds at runtime (SecretStore → ControlPlane → admin fallback,
// empty strings coerced to null). The raw GetConnectionString guard this
// replaces was a no-op on the VPS: both keys ship as "" there and the CP
// DbContext only works via the admin-connection fallback, so
// IsNullOrWhiteSpace("") == true skipped the whole guard and Production
// silently used the volatile in-memory backend for real secrets.
var secretStoreConnString =
    Tamma.Api.Infrastructure.ConnectionStringResolver.ResolveSecretStore(builder.Configuration);
var secretStoreKekConfigured = !string.IsNullOrWhiteSpace(
    Environment.GetEnvironmentVariable(
        Tamma.Api.Services.Secrets.Postgres.EnvKekProvider.PrimaryEnvVar));

var secretCabinetBackend = builder.Services.AddTammaSecretCabinet(
    builder.Configuration,
    builder.Environment.IsProduction(),
    secretStoreKekConfigured,
    secretStoreConnString);

var kekEnvVar = Tamma.Api.Services.Secrets.Postgres.EnvKekProvider.PrimaryEnvVar;
switch (secretCabinetBackend)
{
    case SecretCabinetBackend.PersistentPostgres:
        Log.Information(
            "Secret cabinet: persistent Postgres envelope-encrypted backend wired ({KekEnv} present).",
            kekEnvVar);
        break;
    case SecretCabinetBackend.FailClosed:
        // Production with no persistent backend (KEK absent OR no resolvable
        // connection). Secret WRITES fail closed; startup stays up so the
        // operator sees the cause at the first write, not a crash loop.
        Log.Error(
            "Secret cabinet MISCONFIGURED: Production has no persistent secret backend " +
            "({KekEnv} unset or no resolvable connection). Secret WRITES will FAIL CLOSED " +
            "(no plaintext is persisted to volatile memory). Set {KekEnv} (and, if a " +
            "dedicated secrets DB is wanted, ConnectionStrings:SecretStore) to enable the " +
            "persistent Postgres envelope-encrypted backend.",
            kekEnvVar, kekEnvVar);
        break;
    case SecretCabinetBackend.VolatileInMemory:
        Log.Warning(
            "Secret cabinet: {KekEnv} not set — using the VOLATILE in-memory backend. " +
            "Secret ciphertext will NOT survive a restart. Acceptable for dev/test only; " +
            "set {KekEnv} for any persistent deployment.",
            kekEnvVar, kekEnvVar);
        break;
}

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

    // Story 29-6 — rotation saga ports (gateway / handler registry /
    // audit emitter / retire executor + scheduler / trigger service) +
    // the postgres / cranl / generic-http handlers. Wired here (inside
    // the cabinet-present guard) because the gateway depends on the
    // IDbContextFactory<SecretsDbContext> AddTammaSecretReveal just
    // registered. Without this call the rotate-secret workflow's ports
    // resolve to nothing and a dispatch 500s.
    builder.Services
        .AddTammaSecretRotation(); // Tamma.Api.Services.Secrets.Rotation

    // Story 29-6 (review fix) — bind the rotation gateway options
    // (stale-pending TTL: a pending marker older than this is treated as an
    // abandoned/crashed saga and reclaimed so a crash can't wedge the secret).
    builder.Services
        .AddOptions<Tamma.Api.Services.Secrets.Rotation.SecretRotationGatewayOptions>()
        .Configure(opts => builder.Configuration
            .GetSection(Tamma.Api.Services.Secrets.Rotation.SecretRotationGatewayOptions.SectionName)
            .Bind(opts));

    // Story 29-6 AC8 — the RETIRE_SECRET_VERSION platform-task handler.
    // This is the AC8-specified PlatformTaskWorker drain route for the
    // retire tail. Registering it makes the type HANDLED, which is the
    // fix for the type-blind dead-letter hazard (we deliberately do NOT
    // flip PlatformTaskWorker:RunOnStartup).
    builder.Services
        .AddPlatformTaskHandler<
            Tamma.Api.Services.Secrets.Rotation.RetireSecretVersionTaskHandler>();

    // Story 29-6 (audit gap #2b) — scheduled auto-rotation. Gated off by
    // default (SecretAutoRotation:Enabled=false); an operator opts in
    // once the Elsa engine + rotation handlers are deployed.
    builder.Services
        .AddOptions<Tamma.Api.Services.Secrets.Rotation.SecretAutoRotationSchedulerOptions>()
        .Configure(opts => builder.Configuration
            .GetSection(Tamma.Api.Services.Secrets.Rotation.SecretAutoRotationSchedulerOptions.SectionName)
            .Bind(opts));
    builder.Services
        .AddHostedService<Tamma.Api.Services.Secrets.Rotation.SecretAutoRotationScheduler>();

    // Story 29-6 (review fix) — the ACTIVE retire-tail drainer. Because
    // PlatformTaskWorker:RunOnStartup stays false (the generic worker is not
    // yet safe for every platform-task type), the AC8 per-task handler would
    // never run — nothing would drain RETIRE_SECRET_VERSION rows. This
    // dedicated sweeper periodically reserves ONLY due retire rows (the
    // VisibleAt guard leaves not-due rows untouched) and routes them through
    // the same IRetireTaskExecutor body, so the old credential reliably
    // reaches Revoked. Always on once the cabinet is wired — draining a
    // scheduled retirement is a correctness requirement, not an opt-in.
    builder.Services
        .AddOptions<Tamma.Api.Services.Secrets.Rotation.RetireSweepOptions>()
        .Configure(opts => builder.Configuration
            .GetSection(Tamma.Api.Services.Secrets.Rotation.RetireSweepOptions.SectionName)
            .Bind(opts));
    builder.Services
        .AddHostedService<Tamma.Api.Services.Secrets.Rotation.RetireSweepHostedService>();
}

// Story 32-3 — BYOK→platform provider-credential resolver + cache invalidator.
// Registered AFTER the secrets wiring so the cabinet-backed BYOK reader is
// chosen when the SecretsDbContext factory is present (else the Null reader
// degrades cleanly to the platform path). The resolver is the canonical owner
// of provider-key resolution into the LLM call path (CallLlmInlineActivity).
builder.Services.AddProviderCredentialResolution();

// Story 32-5 (T3): the managed execution layer behind POST /api/v1/llm/call.
// ManagedAgent composes the rule-2 sequence (resolve+enablement+prompt → gate →
// budget → credential → STARTED → runner → meter → terminal) and the mapper
// projects AgentRunResult → LlmCallResponse + the §2.4 HTTP-status decision.
// The endpoint mapping itself is T4; here we register the services so the host
// resolves the whole chain at startup.
//
// The 34-5 markup engine and 32-9 usage emitter are not yet landed: the
// interim seams (PassthroughProviderMarkupEngine = byok⇒0 / platform⇒basis;
// NullUsageEmitter = no-op, the AGENT.RUN.* DCB events remain the durable
// signal) ship the SAFE default until those stories replace them behind the
// same interfaces. IBudgetGuard is the per-call fail-closed gate (32-9 backs
// running-spend later). InlineToolLoopRunner is the extracted T2 runner, now
// hosted in Tamma.Api.Services.Agents (the workflow-engine assembly no longer
// hosts the agentic tool loop); its provider-side collaborators (sanitizer/
// registry/validator/compactor) are all optional and are fully wired in the API
// in T4.
builder.Services.TryAddSingleton<Tamma.Api.Services.Agents.IProviderMarkupEngine,
    Tamma.Api.Services.Agents.PassthroughProviderMarkupEngine>();
builder.Services.TryAddSingleton<Tamma.Api.Services.Agents.IUsageEmitter,
    Tamma.Api.Services.Agents.NullUsageEmitter>();
builder.Services.TryAddSingleton<Tamma.Api.Services.Agents.IBudgetGuard,
    Tamma.Api.Services.Agents.PerCallBudgetGuard>();
builder.Services.TryAddSingleton<Tamma.Api.Services.Agents.ILlmCallResponseMapper,
    Tamma.Api.Services.Agents.LlmCallResponseMapper>();

// Story 38-1 (Epic 38) — git-platform step mediation (Class A). The engine's
// thin ADL git activities (CreateBranch / CreatePullRequest / MergePullRequest /
// UpdateIssueStatus / AnalyzeReview) POST to /api/v1/git/{owner}/{repo}/... here
// instead of resolving the co-hosted IGitHubIntegrationService. The API holds the
// per-tenant token: it authorizes tenant↔repo FIRST (the cross-tenant guard),
// resolves the token BYOK→platform, performs the platform call with THAT token,
// and emits one terminal GIT.* DCB event. IGitHubIntegrationService stays
// API-only; the GitHubClientFactory mints a token-bound instance per request.
builder.Services.AddScoped<Tamma.Api.Services.Git.IGitRepoAuthorizer,
    Tamma.Api.Services.Git.GitRepoAuthorizer>();
builder.Services.AddScoped<Tamma.Api.Services.Git.IGitTokenResolver,
    Tamma.Api.Services.Git.GitTokenResolver>();
builder.Services.AddSingleton<Tamma.Api.Services.Git.IGitHubClientFactory,
    Tamma.Api.Services.Git.GitHubClientFactory>();
builder.Services.AddScoped<Tamma.Api.Services.Git.IGitMediationService,
    Tamma.Api.Services.Git.GitMediationService>();

// ── Story 38 (Phase 1) — CI / JIRA / email step mediation ──
// CI (GitHub Actions) reuses the git guard + token resolver (CI runs on the same
// per-tenant git token); the CiClientFactory mints a token-bound CIIntegrationService
// per request. JIRA + email are NOT repo-scoped (like Slack): they run the existing
// config-credentialed IJiraIntegrationService / outbox-backed IEmailService under the
// caller's tenant context. In every case the credential stays in Tamma.Api; the
// engine holds nothing.
builder.Services.AddSingleton<Tamma.Api.Services.Ci.ICiClientFactory,
    Tamma.Api.Services.Ci.CiClientFactory>();
builder.Services.AddScoped<Tamma.Api.Services.Ci.ICiMediationService,
    Tamma.Api.Services.Ci.CiMediationService>();
builder.Services.AddScoped<Tamma.Api.Services.Jira.IJiraMediationService,
    Tamma.Api.Services.Jira.JiraMediationService>();
builder.Services.AddScoped<Tamma.Api.Services.EmailMediation.IEmailMediationService,
    Tamma.Api.Services.EmailMediation.EmailMediationService>();

// ── Integration BYOK — per-tenant JIRA + email credentials ──
// The JIRA/email mediation now resolves the acting tenant's OWN credential
// per-request (BYOK→system→fail-loud, like git/LLM) instead of a shared platform
// credential + SaaS-deny guard. This wires the resolvers (mediation reads them),
// the credential-bound JIRA HTTP client, and the cabinet write helper (the
// /api/v1/integrations/* management endpoints use it). Registered AFTER
// AddProviderCredentialResolution so the singleton cabinet reader + SecretsDbContext
// factory signal are already present.
builder.Services.AddIntegrationCredentialResolution();

// Story 34-3 — BYOK toggle WRITE side. TenantProviderBillingService enables /
// disables BYOK: it writes the tenant's key into the Epic 29 cabinet (via the
// governed ProviderByokSecretCabinet, under the canonical provider/<name>/api-key
// slug Story 32-3 reads), upserts the one active TenantProviderBilling owner row
// the read-side resolver consumes, invalidates 32-3's credential cache, and emits
// PRICING.BYOK.*. Scoped (composes the scoped ControlPlaneDbContext + ISecretStore
// facade). The cabinet is only meaningful with the secret store wired — guarded on
// the SecretsDbContext factory exactly like AddIntegrationCredentialResolution.
// NON-migration: reuses the existing tenant_provider_billing owner table + the
// secrets / secret_versions cabinet tables.
if (builder.Services.Any(d =>
    d.ServiceType == typeof(IDbContextFactory<Tamma.Api.Services.Secrets.Postgres.SecretsDbContext>)))
{
    builder.Services.TryAddScoped<
        Tamma.Api.Services.Pricing.IProviderByokSecretCabinet,
        Tamma.Api.Services.Pricing.ProviderByokSecretCabinet>();
    // The service composes the cabinet, so it is only wired when the secret store
    // is present (the byok endpoints are inert — and 500 rather than silently
    // mis-store — without it). Mirrors AddIntegrationCredentialResolution's guard.
    builder.Services.TryAddScoped<
        Tamma.Api.Services.Pricing.ITenantProviderBillingService,
        Tamma.Api.Services.Pricing.TenantProviderBillingService>();
}

// Story 32-5 (T4) — provider-side DI for the server-side tool loop, FORMALIZED
// in the API process (replacing T3's best-effort GetService factory). The loop
// (extracted verbatim into InlineToolLoopRunner) now executes HERE, where the
// request-scoped key is resolved, so its collaborators must resolve fully at
// host startup. These registrations mirror the engine's (ElsaServer/Program.cs);
// the engine-side copies are deleted in T6 (the engine holds no key after
// cutover). Lifetimes match the engine: built-in tool executors + registry +
// sanitizer + action-gate + validator + compactor are singletons (stateless /
// config-only); the runner is scoped so each call gets a fresh request-scoped
// provider config.
builder.Services.Configure<Tamma.Activities.Security.ActionGateOptions>(
    builder.Configuration.GetSection("Security:ActionGate"));
builder.Services.TryAddSingleton<Tamma.Activities.Security.IContentSanitizer,
    Tamma.Activities.Security.ContentSanitizer>();
builder.Services.TryAddSingleton<Tamma.Activities.Security.ActionGate>();
builder.Services.TryAddSingleton<Tamma.Activities.Security.IToolCallValidator,
    Tamma.Activities.Security.ToolCallValidator>();
builder.Services.AddSingleton<Tamma.Activities.LlmCall.Tools.IToolExecutor,
    Tamma.Activities.LlmCall.Tools.FileReadTool>();
builder.Services.AddSingleton<Tamma.Activities.LlmCall.Tools.IToolExecutor,
    Tamma.Activities.LlmCall.Tools.FileWriteTool>();
builder.Services.AddSingleton<Tamma.Activities.LlmCall.Tools.IToolExecutor,
    Tamma.Activities.LlmCall.Tools.SearchCodeTool>();
builder.Services.AddSingleton<Tamma.Activities.LlmCall.Tools.IToolExecutor,
    Tamma.Activities.LlmCall.Tools.ShellExecuteTool>();
builder.Services.AddSingleton<Tamma.Activities.LlmCall.Tools.IToolExecutor,
    Tamma.Activities.LlmCall.Tools.GitOperationsTool>();
builder.Services.AddSingleton<Tamma.Activities.LlmCall.Tools.IToolExecutor,
    Tamma.Activities.LlmCall.Tools.RunTestsTool>();
builder.Services.TryAddSingleton<Tamma.Activities.LlmCall.Tools.IToolExecutorRegistry,
    Tamma.Activities.LlmCall.Tools.ToolExecutorRegistry>();
builder.Services.TryAddSingleton<Tamma.Activities.LlmCall.Tools.ContextCompactor>();
// ToolLoopEventEmitter + ParallelToolExecutor: the runner accepts them as
// optional (nullable) deps and the engine never registered them either (the
// buffered-only path doesn't emit a live tool-loop event stream — that's the
// deferred streaming-run-tap follow-on). Register them anyway so the server-side
// loop is fully wired (their only hard dep is ILogger; the emitter's sink
// defaults to the no-op NullToolLoopEventSink when none is registered).
builder.Services.TryAddSingleton<Tamma.Activities.ToolExecution.ToolLoopEventEmitter>();
builder.Services.TryAddSingleton<Tamma.Activities.ToolExecution.ParallelToolExecutor>();

// ── Story 32-23 — the streaming run tap (SSE for dashboard / CLI) ──
// The in-process run-stream bus is ALWAYS registered (a cheap in-memory
// singleton). ManagedAgent publishes the terminal `final` frame to it as a
// decoupled SIDE-EFFECT; the human tap (GET /api/v1/llm/runs/{cid}/stream)
// subscribes. The bus is fire-and-forget: a no-op with zero subscribers, and it
// never blocks or throws into the buffered run, so the engine's /llm/call
// contract stays byte-for-byte unchanged (AC5/AC6).
builder.Services.TryAddSingleton<Tamma.Api.Services.Streaming.ILlmRunStreamBus,
    Tamma.Api.Services.Streaming.LlmRunStreamBus>();
// The LIVE tool-loop sink is gated behind the app-level streaming flag. When
// enabled, TOOL_LOOP.* events map to tool_call/tool_result frames on the bus;
// when disabled the registration stays NullToolLoopEventSink (a graceful no-op —
// the tap still shows the terminal `final`, never an error). This makes live the
// IToolLoopEventSink seam 32-5 shipped inert.
var runTapStreamingEnabled = string.Equals(
    builder.Configuration["Tamma:Streaming:Enabled"], "true", StringComparison.OrdinalIgnoreCase);
if (runTapStreamingEnabled)
{
    builder.Services.TryAddSingleton<Tamma.Activities.ToolExecution.IToolLoopEventSink,
        Tamma.Api.Services.Streaming.BusToolLoopEventSink>();
}
else
{
    builder.Services.TryAddSingleton<Tamma.Activities.ToolExecution.IToolLoopEventSink>(
        Tamma.Activities.ToolExecution.NullToolLoopEventSink.Instance);
}
// The extracted runner — the SINGLE home of the loop (no fork). Scoped so each
// call binds a request-scoped provider config. Its IProviderCredentialResolver
// dep is the cabinet-backed DefaultProviderCredentialResolver (registered above
// via AddProviderCredentialResolution) — but the loop never re-resolves: the key
// is set on the provider config ManagedAgent hands it.
builder.Services.AddScoped<Tamma.Api.Services.Agents.IInlineToolLoopRunner,
    Tamma.Api.Services.Agents.InlineToolLoopRunner>();
builder.Services.AddScoped<Tamma.Api.Services.Agents.IManagedAgent,
    Tamma.Api.Services.Agents.ManagedAgent>();

// Story 39-9 — the GLOBAL deterministic repair-ring config (bounds + per-type gate).
// Default OFF for every document type (EnabledDocumentTypes empty), default 1 turn,
// hard cap 2 by clamp. Bound from the "RepairRing" section (TenantBackupOptions block
// pattern). ManagedAgent reads it to build the per-call RepairRingPlan.
builder.Services.AddOptions<Tamma.Api.Services.Agents.RepairRingOptions>()
    .Configure(opts =>
        builder.Configuration
            .GetSection(Tamma.Api.Services.Agents.RepairRingOptions.SectionName)
            .Bind(opts));

// Story 32-6 — the per-agent ACTION TRAIL emitter. The single seam ManagedAgent
// (and later 32-7 panels / 32-8 review gate) calls to record AGENT.TASK.* /
// AGENT.TOOL_CALL.* / AGENT.ITERATION.* / AGENT.PANEL.* / REVIEW.BUG.* events into
// the resolving tenant's domain_events stream. Scoped (its IEventRepository dep is
// scoped); the IContentSanitizer redaction seam is optional and resolves from the
// singleton registered above. It never throws into a run (AC7).
builder.Services.AddScoped<Tamma.Api.Services.Agents.IAgentTrailEmitter,
    Tamma.Api.Services.Agents.AgentTrailEmitter>();

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

// Story 31-9 — onboarding platform picker connect service. Composes
// the secret-cabinet (29-1/29-3 reveal-on-create), the 31-2 platform
// installation registry, and the 31-1 driver factory to validate +
// persist a new platform binding. Scoped because it touches the
// per-request DbContext via the repository.
//
// Resolved via a factory lambda so the DI container does not eagerly
// validate ISecretRevealService at startup. ISecretRevealService is
// only registered when ConnectionStrings:SecretStore / ControlPlane
// is configured (Program.cs lines 433-439). Test environments without
// Postgres connection strings would fail container build-time
// ValidateOnBuild if PlatformConnectService were registered with
// constructor-injection sugar; the factory shape defers resolution to
// the first request, at which point the endpoint is gated on
// PlatformsManage so it would 401/403 long before the service is
// invoked. When the reveal service is wired (production +
// AddTammaPostgresSecrets-enabled tests) the factory hands back a
// real PlatformConnectService.
builder.Services.AddScoped<
    Tamma.Api.Services.Onboarding.IPlatformConnectService>(sp =>
{
    var reveal = sp.GetService<Tamma.Api.Services.Secrets.Reveal.ISecretRevealService>();
    if (reveal is null)
    {
        // No secret cabinet wired — the endpoint will surface a
        // 503-style error when invoked. Tests exercise the service
        // directly via constructor injection.
        return new Tamma.Api.Services.Onboarding.NullPlatformConnectService();
    }
    return new Tamma.Api.Services.Onboarding.PlatformConnectService(
        sp.GetRequiredService<Tamma.Data.Repositories.ITenantPlatformInstallationRepository>(),
        reveal,
        sp,
        sp.GetRequiredService<Tamma.Platforms.IPlatformInstallationEventEmitter>(),
        sp.GetRequiredService<TimeProvider>(),
        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<
            Tamma.Api.Services.Onboarding.PlatformConnectService>>());
});

// Story 31-4: register the Gitea driver factory under keyed DI for
// PlatformKind.Gitea. PlatformResolver picks the factory up via
// GetKeyedService<IGitPlatformDriverFactory>(PlatformKind.Gitea) when
// a tenant's installation row carries platform_kind = 'gitea'. The
// extension is idempotent and registers the named tamma-gitea HTTP
// client + OAuth2 token cache + webhook signature verifier.
builder.Services.AddGiteaPlatformDriver();

// Story 31-5: Forgejo compat shim. Composes the Gitea driver — wire-
// compatible REST surface — under PlatformKind.Forgejo so the resolver
// can pick a Forgejo-branded driver for tenants with platform_kind =
// 'forgejo'. Webhook verifier registered keyed-DI with X-Forgejo-Sig
// preferred, X-Gitea-Sig fallback for older forks. Idempotent.
builder.Services.AddForgejoPlatformDriver();

// Story 31-7: webhook receiver. Registers per-platform signature
// verifiers (HMAC for GitHub/Gitea/Forgejo, static-token for GitLab),
// the in-process IWebhookEventDispatcher (single-handler-per-event for
// now; multi-handler dispatch is a follow-up), the cross-platform
// idempotency repo, and the secret-resolver that bridges the
// installation row to its webhook secret via the Story 29 seam.
builder.Services.AddTammaWebhookReceiver();

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
// Story 4-8 — black-box replay: tenant-scoped point-in-time state reconstruction
// (a pure read-fold over the DCB domain_events store via the 4-7 event query API).
// Scoped: reads through the request-scoped tenant EventRepository.
builder.Services.AddScoped<Tamma.Api.Services.Engine.Replay.IReplayService,
    Tamma.Api.Services.Engine.Replay.ReplayService>();
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

// ─── Epic 19 / Story 38-2: Agent dispatch (Class-C mediation) ──────────
//
// IGitHubActionsClient — Octokit-backed when the GitHub App is wired,
// otherwise the Null impl that reports NotConfigured. After the Story 38-2
// cutover this client is API-ONLY: it is consumed by the new
// AgentDispatchMediationService + ActionsResultAggregator behind the
// /api/v1/agent-dispatch endpoints (which mint the per-repo installation
// token internally), NOT by the engine phase services (those are now thin
// TammaApiClient clients). The engine's NullGitHubActionsClient registration
// was removed from ElsaServer/Program.cs.
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

// Story 38-2 — the managed agent-dispatch execution layer behind
// /api/v1/agent-dispatch/{owner}/{repo}/... . The mediation service composes the
// Story 38-1 cross-tenant guard (IGitRepoAuthorizer) → IGitHubActionsClient →
// one DCB event; the aggregator does the collect multi-read server-side.
builder.Services.AddScoped<Tamma.Api.Services.AgentDispatch.IActionsResultAggregator,
    Tamma.Api.Services.AgentDispatch.ActionsResultAggregator>();
builder.Services.AddScoped<Tamma.Api.Services.AgentDispatch.IAgentDispatchMediationService,
    Tamma.Api.Services.AgentDispatch.AgentDispatchMediationService>();

// Story 38-2 — the engine's TammaApiClient. Tamma.Api does NOT host the Elsa
// engine, so the phase services below are dead registrations here; but wiring the
// client keeps them resolvable (belt-and-suspenders for DI validation / any
// co-host path) and holds no cost.
builder.Services.AddHttpClient<Tamma.Activities.LlmCall.TammaApiClient>();

// Services — scoped to match the client lifetime. After 38-2 these are thin
// TammaApiClient clients (no IGitHubActionsClient injection).
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

// Story 28-5 AC4 — optional pre-drop tenant backup (pg_dump), disabled by
// default. Bound here too so the activity resolves config when the delete
// workflow runs in-process under the API host.
builder.Services.AddOptions<Tamma.Activities.TenantLifecycle.TenantBackupOptions>()
    .Configure(opts =>
        builder.Configuration
            .GetSection(Tamma.Activities.TenantLifecycle.TenantBackupOptions.SectionName)
            .Bind(opts));

// Unified-tenancy Phase 4 — pg_dump/pg_restore knobs for the tenant move
// engine (TenantMoveService, registered by AddPlatformEventBus).
builder.Services.AddOptions<Tamma.Api.Services.Provisioning.TenantMoveOptions>()
    .Configure(opts =>
        builder.Configuration
            .GetSection(Tamma.Api.Services.Provisioning.TenantMoveOptions.SectionName)
            .Bind(opts));

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
// Task #10 (post-review) — register the heartbeat options as a singleton
// so the shared API test fixture can flip RunOnStartup=false without
// removing the hosted-service registration. Default options (RunOnStartup=true)
// preserve production behaviour.
builder.Services.TryAddSingleton<Tamma.Api.Services.Engine.Lifecycle.EngineRegistryHeartbeatOptions>();
builder.Services.AddHostedService<Tamma.Api.Services.Engine.Lifecycle.EngineRegistryHeartbeatService>();

// Story 28-6: in-process platform-event bus. Subscribers attach in this
// process only; multi-pod fanout pending a Postgres LISTEN/NOTIFY bridge
// against platform_events. Repository registration lives in AddTammaData.
builder.Services.AddPlatformEventBus();

// Story 31-6: GitLab driver — registers the keyed
// IGitPlatformDriverFactory the 31-2 PlatformResolver picks up. Same
// driver serves SaaS gitlab.com and self-hosted; BaseUrl comes from
// the per-tenant PlatformInstallation row.
builder.Services.AddGitLabPlatform();

// Story 31-3: GitHub driver — wraps the existing
// IGitHubActionsClient (Tamma.Activities) behind IGitPlatformDriver
// so GitHub becomes a peer of Gitea/GitLab/Forgejo for the 31-2
// PlatformResolver. The factory pulls the inner client from the
// request scope on each CreateAsync call so it picks up the existing
// Octokit / Null registration above without changing those code
// paths.
builder.Services.AddGitHubPlatformDriver();

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
        tags: new[] { "ready" })
    // Story 28-12 AC1+AC2 — least-privilege DB role assertion (see the
    // DbRoleLeastPrivilegeCheck registration above for the gating rules).
    .AddCheck<Tamma.Api.Services.Secrets.DbRoleLeastPrivilegeCheck>(
        name: "db-role-least-privilege",
        failureStatus: Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy,
        tags: new[] { "ready" });

// Admin health aggregator (per-service ping fan-out for the dashboard).
// Mirrors the TS /api/admin/health behavior.
builder.Services.AddScoped<IAdminHealthService, AdminHealthService>();

// Story 23-8 — Infrastructure Monitor metrics. A live, read-only snapshot of the
// API process + host (runtime / CPU / memory / disk / uptime) composed with the
// admin health probes. No new infra: reads GC / Process / DriveInfo / cgroup only.
builder.Services.AddScoped<IInfrastructureMetricsService, InfrastructureMetricsService>();

// Story 5.6 / 1.5-37 (Wave C.1) — alert core: sink, dispatcher,
// four channels (email / slack / pagerduty / webhook), rate
// limiter, secret reader. Registered before any caller so
// IAlertSink can be injected by future wave-C.4 activity edits.
builder.Services.AddTammaAlerts();

// Story 5.6 (Wave C.2) — alert rule engine: evaluator, registry,
// window store, and the built-in rule seeder. Subscribes to the
// DCB event stream and emits AlertPayloads through IAlertSink.
builder.Services.AddTammaAlertRuleEngine();

// Story 37-1 — curated audit-record projection: catalog-driven projector,
// insert-if-absent repository, lag metric, and the background host. READS the
// DCB stream and materializes audit_records (never writes raw events). The
// background loop is opt-in (AuditProjectorOptions.RunOnStartup defaults false).
builder.Services.AddTammaAuditProjection();

// Story 37-2 — tamper-evident audit hash-chain: canonical hasher/verifier,
// cabinet-backed checkpoint signer, verification + checkpoint-writer services,
// and the AUDIT.CHAIN.* event emitter (raises a critical tamper alert). Scoped;
// the checkpoint scheduler (Tamma.ElsaServer) creates its own per-tick scope.
builder.Services.AddTammaAuditChain();

// Story 37-10 — curated sensitive-action EMISSION seam (write side): the single
// ISensitiveActionEmitter every sensitive-action call site (auth login/refresh,
// API-key auth, BYOK provider-key changes, ...) appends through, plus the
// per-key AUTH.APIKEY.USED heartbeat throttle. Routes tenant actions to
// domain_events and platform-edge actions to platform_events so the 37-1
// projector materialises them into audit_records in the correct scope. Never
// throws to the caller (a failed audit emit must not break the action).
builder.Services.AddTammaSensitiveActionEmitter();

// Story 37-3 — audit query/search/filter read seam over the curated
// audit_records read-model (Story 37-1). Scoped (depends on the scoped
// ControlPlaneDbContext); reads the tenant schema via ITenantDbContextFactory
// (SaaS) or the CP by user/tenant-null (single-user / platform). Read-only —
// it never re-projects raw events; the only write is the best-effort
// AUDIT.QUERIED meta-audit event.
builder.Services.AddScoped<
    Tamma.Api.Services.Audit.IAuditQueryService,
    Tamma.Api.Services.Audit.AuditQueryService>();

// Story 34-1 — plan price-book catalog: read-only IPlanCatalogService +
// the PlanVersionEditor (immutable, versioned plan management). CP-resident;
// platform-owned in both modes.
builder.Services.AddPlanCatalog();

// Story 34-5 — the canonical cost->price markup engine (pure IUsagePricingEngine)
// + the DB-backed IMarginPolicyResolver + the pricing-mode resolver. Story 34-3
// repointed the pricing-mode resolver onto the authoritative per-(tenant, provider)
// TenantProviderBilling owner. CP-resident; platform-owned margin policies in both modes.
builder.Services.AddUsagePricingEngine();

// Story 35-2 — the billing-mode tagger: reads the 34-3 owner + reconciles 32-3's
// runtime credential source, producing the canonical billing_mode tag stamped on
// LLM.CALL.* usage events + ProviderDiagnostic.BillingMode. Null seam in single-user.
builder.Services.AddBillingModeTagging(builder.Configuration);

// Story 34-6 — entitlement & quota resolution: the single read seam turning a
// tenant's pinned plan assignment into a closed ResolvedEntitlements map, plus
// the per-tenant snapshot cache, gauge-metric usage reader, and event-driven
// cache-invalidation listener. Read-only; fails loud on no assignment.
builder.Services.AddEntitlementResolution();

// Story 34-4 — the version-pinned plan-assignment service (source of truth for
// "what plan version is this tenant on"), its ITenantUsageReader seam (null
// default; Epic 35 supplies the real reader), and the platform-queue
// boundary-activation handler. AddEntitlementResolution's
// IActivePlanAssignmentSource now reads the assignment table via this service.
builder.Services.AddPlanAssignment();

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

// Story 36-3 — tenant-facing usage analytics read seam. Reads the per-tenant
// Story 36-1 fact tables (analytics_usage_hourly / analytics_usage_daily,
// populated by Story 36-2) through ITenantDbContextFactory — NOT the
// control-plane platform_analytics_hourly surface above.
builder.Services.AddScoped<
    Tamma.Api.Services.Analytics.ITenantAnalyticsService,
    Tamma.Api.Services.Analytics.TenantAnalyticsService>();

// Story 36-4 — tenant-facing cost & spend analytics read seam. Reads the same
// per-tenant Story 36-1 analytics_usage_daily fact table (populated by 36-2)
// through ITenantDbContextFactory, splitting BYOK CostUsd (informational) from
// the materialised PlatformBilledUsd (billable). Reads PlatformBilledUsd as the
// single source of truth — NO markup/pricing dependency (AC4/AC10). Joins
// BudgetConfig read-only and emits the deduped budget-exceeded DCB event.
builder.Services.AddScoped<
    Tamma.Api.Services.Analytics.ICostAnalyticsService,
    Tamma.Api.Services.Analytics.CostAnalyticsService>();

// Story 34-11 — swap the frozen ProviderPricingService for the DB-backed
// DbProviderPricingService behind the unchanged IProviderPricingService seam
// (a one-line DI change; zero downstream consumer edits). Must run AFTER
// AddProviderSessionServices (which TryAdds the frozen impl) so this explicit
// registration wins. The frozen class stays resolvable as the seed source +
// boot fallback.
builder.Services.AddDbProviderPricing();

// Story 32-4 — SaaS provider gate (composition step 1 of the 32-5 call-LLM
// endpoint). In SaaS it denies cli-token (harness) + unknown providers
// fail-closed (typed → HTTP 400) and un-entitled tenants (typed → 403); in
// single-user it is a hard no-op. The gate is a pure service over existing
// seams (mode + provider-auth lookup + entitlement + events + metrics) — NO EF
// migration, NO new table, NO credential/secret dependency.
//
//   * IProviderAuthLookup → EntityProviderAuthLookup reads the 34-11
//     Provider.AuthModel column (canonical source, now landed). To revert to
//     the interim static allowlist, swap this ONE line for:
//         builder.Services.AddSingleton<IProviderAuthLookup, StaticProviderAuthLookup>();
//     The ProviderGateDecision / ISaaSProviderGate contract is identical for
//     both (contract-neutral; pinned by the 34-11 swap matrix test).
//   * ITenantProviderEntitlement → permissive default (every tenant entitled to
//     every api-key provider). Replace with Epic 34's entitlement engine (DI
//     swap) to activate the 403 TenantNotEntitled path; this story owns only
//     the typed surfacing of the result, not the entitlement rules.
builder.Services.AddScoped<
    Tamma.Api.Services.Security.IProviderAuthLookup,
    Tamma.Api.Services.Security.EntityProviderAuthLookup>();
builder.Services.AddSingleton<
    Tamma.Api.Services.Security.ITenantProviderEntitlement,
    Tamma.Api.Services.Security.PermissiveTenantProviderEntitlement>();
builder.Services.AddSingleton<Tamma.Api.Services.Security.ProviderGatingMetrics>();
builder.Services.AddScoped<
    Tamma.Api.Services.Security.ISaaSProviderGate,
    Tamma.Api.Services.Security.SaaSProviderGate>();

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

// Story 35-1 — Epic 35 billing foundation. Mode-aware: StripeBillingProvider
// in SaaS, NullBillingProvider in single-user. Registers the catalog reader,
// the cabinet-resolving Stripe client factory, BillingOptions, and the
// billing.customer.create retry handler (IPlatformTaskHandler). The Stripe key
// resolves through the Epic 29 cabinet — never raw env in production (AC5).
builder.Services.AddTammaBilling(builder.Configuration);

// Story 35-5 — Stripe webhook ingestion pipeline (SaaS only). Registers the
// processor, the pluggable IBillingEventHandler registry + 35-5's default
// DCB-emitting handlers, the cabinet-resolving signing-secret source, the
// Stripe event verifier, and the fast-ack billing.webhook.followup task handler.
// Single-user is a no-op (NullBillingProvider — zero Stripe surface); the routes
// below are mapped only in SaaS mode.
builder.Services.AddBillingWebhookIngestion(builder.Configuration);

// Unified-tenancy Phase 4 — `tenant.move` platform-task handler. Drives
// ITenantMoveService.MoveAsync for tasks enqueued by
// POST /api/admin/tenants/{tenantId}/move; on failure it stamps the
// tenant's FailureReason shadow column and rethrows so the worker
// retries (dead-letter at the ceiling).
builder.Services
    .AddPlatformTaskHandler<Tamma.Api.Services.Provisioning.MoveTenantTaskHandler>();

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
    // I4 / Story 32-5 Finding C2 — handler for the EngineServiceOnly policy
    // (service-principal-only: engine→API callbacks + POST /api/v1/llm/call).
    builder.Services.AddScoped<IAuthorizationHandler, ServicePrincipalHandler>();

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
        // Story 27-10 — Convention Store tenant-admin policy. Mirrors
        // PromptManage exactly: tenant PUT/DELETE of a convention override must
        // be reachable by tenant_owner OR tenant_admin (admin+owner here);
        // member-role callers hit 403. SettingsManage is owner-only and would
        // 403 every tenant_admin, so the dedicated ConventionManage gate is used
        // for the tenant override mutations. The /api/admin/conventions/* system
        // -default routes use PlatformOwnerAccess instead (platform-admin only).
        options.AddPolicy("ConventionManage", p =>
        {
            p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, "ApiKey");
            p.AddRequirements(new PermissionRequirement("conventions:manage"));
        });
        // Story 31-9 — onboarding platform picker / connect. CLAUDE.md
        // "Operating Modes" requires the same admin+owner reach as
        // PromptManage so tenant admins (not just owners) can wire
        // platform installations. SettingsManage is owner-only and
        // would 403 every admin-role tenant member.
        options.AddPolicy("PlatformsManage", p =>
        {
            p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, "ApiKey");
            p.AddRequirements(new PermissionRequirement("platforms:manage"));
        });
        // Story 32-1 — first-class agent entity writes (create/publish/archive).
        // Mirrors PromptManage/ConventionManage: tenant_owner OR tenant_admin
        // reach so member-role SaaS callers hit 403 at the policy. Public-agent
        // writes are additionally gated by the platform-admin claim inside the
        // handler (CreateAgent / CanWriteAgent).
        options.AddPolicy("AgentManage", p =>
        {
            p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, "ApiKey");
            p.AddRequirements(new PermissionRequirement("agents:manage"));
        });
        // Story 34-3 — BYOK pricing-mode management (enable/disable). Mirrors
        // PromptManage / ConventionManage / AgentManage: CLAUDE.md "Operating
        // Modes" makes per-(tenant, provider) BYOK a tenant-scoped setting
        // reachable by tenant_owner OR tenant_admin (member → 403). The spec
        // names SettingsManage, but that policy is owner-only (settings:manage)
        // and would 403 every tenant_admin, so the dedicated PricingManage gate
        // (pricing:manage, admin+owner) is used for the BYOK mutations.
        options.AddPolicy("PricingManage", p =>
        {
            p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, "ApiKey");
            p.AddRequirements(new PermissionRequirement("pricing:manage"));
        });
        // Story 39-5 — Acceptance Rules tenant-admin policy. Mirrors PromptManage
        // exactly: PUT/DELETE of a tenant acceptance-rules override must be
        // reachable by tenant_owner OR tenant_admin (admin+owner here); member-role
        // callers hit 403. The owner-only SettingsManage would 403 every
        // tenant_admin, so the dedicated AcceptanceRulesManage gate is used.
        options.AddPolicy("AcceptanceRulesManage", p =>
        {
            p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, "ApiKey");
            p.AddRequirements(new PermissionRequirement("acceptance-rules:manage"));
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
        // I4 / Story 32-5 Finding C2 — engine/service-principal-only gate. Used
        // by the engine→API callback (POST /api/engine/events) AND the internal
        // LLM-mediation endpoint (POST /api/v1/llm/call). Distinct from
        // WorkflowsManage (which maps to ["admin","owner"] and so is reachable by
        // any tenant owner/admin → audit-event forgery / unauthorized LLM spend).
        // EngineServiceOnly succeeds only for the typed ServiceAuthPrincipal that
        // ApiKeyAuthHandler mints for a service-scope key (the engine's drain
        // token / Tamma:ApiToken). A user JWT authenticates but never produces a
        // ServiceAuthPrincipal, so it is rejected with 403.
        options.AddPolicy("EngineServiceOnly", p =>
        {
            p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, "ApiKey");
            p.RequireAuthenticatedUser();
            p.AddRequirements(new ServicePrincipalRequirement());
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
            "SettingsManage", "PromptManage", "ConventionManage", "PlatformsManage", "AgentManage", "PricingManage", "AcceptanceRulesManage", "WorkflowsView", "WorkflowsManage", "WorkflowsDelete", "DashboardView", "ApiKeysManage",
            "SelfOrApiKeysManage", "SelfOrUsersView", "AuthenticatedAny", "EngineServiceOnly" })
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

// Story 28-12 AC5 residual — eagerly resolve the KEK-rotation metrics so
// the `tamma.kek_rotation.remaining` ObservableGauge's Meter is alive
// from process start (otherwise no consumer would force-construct the
// lazy singleton until the first /status poll). Resolving here also keeps
// the instance rooted on the app's service provider for the process
// lifetime so the Meter is never GC-disposed mid-run.
_ = app.Services.GetRequiredService<Tamma.Api.Services.Secrets.KekRotationMetrics>();

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

// Story 35-1 — `dotnet run --project Tamma.Api -- seed-billing` idempotently
// syncs the Stripe Product/Price/Meter catalog into billing_plan_prices and
// exits. Single-user prints "billing is SaaS-only" and exits 0.
if (Tamma.Api.Services.Billing.SeedBillingCommand.ShouldRun(args))
{
    var exitCode = await Tamma.Api.Services.Billing
        .SeedBillingCommand.RunAsync(app.Services);
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
// After UseAuthentication and BEFORE UseAuthorization: if the request
// arrived without a valid JWT but with a _oauth2_proxy cookie, mint a
// tamma_session JWT from the proxy's /oauth2/userinfo response. CLI /
// API-key callers (no proxy cookie) pass through untouched. See
// ProxyHeaderAuthMiddleware for the full rationale.
app.UseMiddleware<Tamma.Api.Middleware.ProxyHeaderAuthMiddleware>();
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
// Logout sits at bare /api/auth/logout (not /api/v1/auth/logout) to match
// the bare-path session endpoints /api/auth/me and /api/auth/role-check
// that the dashboard polls. The /api/v1/auth/* group reserves login,
// register, refresh — the OAuth2-flow endpoints — while the bare /api/auth
// group is for "what is my current session" semantics.
app.MapPost("/api/auth/logout", AuthEndpoints.Logout).RequireAuthorization("MemberAccess");
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
// Browser user-login flow lives in oauth2-proxy (see docker/oauth2-proxy.cfg
// + nginx auth_request). Tamma.Api does NOT own a /api/auth/github route;
// the dashboard's "Sign in with GitHub" button links to /oauth2/start.

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
// can manage anyone's. Audit finding 016. The path is "/keys" (not
// "/api-keys") and the response shape is { apiKeys: [...] } per the
// Story 16.2 contract — see the apiKeysApi client at
// packages/dashboard/src/services/admin/admin-api-client.ts which is the
// canonical consumer of these endpoints.
admin.MapPost("/users/{id}/keys", AdminEndpoints.CreateUserApiKey).RequireAuthorization("SelfOrApiKeysManage");
admin.MapGet("/users/{id}/keys", AdminEndpoints.ListUserApiKeys).RequireAuthorization("SelfOrApiKeysManage");
admin.MapDelete("/users/{id}/keys/{keyId}", AdminEndpoints.DeleteUserApiKey).RequireAuthorization("SelfOrApiKeysManage");

// Tenant provisioning (audit cranl/003). Platform-owner-only — these flip
// per-tenant Cranl resources into existence (POST), report status (GET), or
// tear them down (POST /deprovision). When Cranl:ApiKey is unset the Null
// provisioner mints nothing and these endpoints still work — they just mark
// the tenant Ready without external API calls (placement stays on the
// unified tenant_databases pool).
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

// Story 23-8 — Infrastructure Monitor. Live system/host metrics (runtime, CPU,
// memory, disk, uptime) + coarse dependency up/down status. These are
// SYSTEM/PLATFORM-level (not tenant-scoped), so PlatformOwnerAccess: a regular
// member / tenant-owner who is not a platform admin gets 403 and never sees
// process internals. Read-only; exposes no connection string / secret / tenant
// data (dependency detail is allowlist-sanitized).
admin.MapGet("/monitoring/infrastructure",
        Tamma.Api.Endpoints.Admin.AdminInfrastructureMonitoringEndpoints.GetInfrastructure)
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

// Story 34-1 — read-only plan price-book endpoints. PlatformOwnerAccess: the
// price book is platform-GLOBAL (incl. BYOK-vs-platform pricing) in both modes
// with no per-tenant override layer, so it is platform-scoped admin work — like
// the adjacent /analytics routes above. OwnerAccess would let every
// personal-tenant owner read the whole platform price book (Finding C1). The
// write (create/deprecate version) endpoints are Story 34-2 — this story ships
// only the three reads + the tested PlanVersionEditor.
admin.MapGet("/plans", Tamma.Api.Endpoints.Admin.PlanCatalogEndpoints.ListActive)
    .RequireAuthorization("PlatformOwnerAccess");
admin.MapGet("/plans/{slug}", Tamma.Api.Endpoints.Admin.PlanCatalogEndpoints.GetActiveBySlug)
    .RequireAuthorization("PlatformOwnerAccess");
admin.MapGet("/plans/{slug}/versions", Tamma.Api.Endpoints.Admin.PlanCatalogEndpoints.GetVersions)
    .RequireAuthorization("PlatformOwnerAccess");

// Story 34-11 — provider COST price-book admin CRUD. PlatformOwnerAccess: the
// cost book is platform-GLOBAL in both modes (no per-tenant override layer), so
// it is platform-scoped admin work — NOT OwnerAccess (which admits every
// personal-tenant owner, Finding C1). Mutations emit PROVIDER.* DCB events.
admin.MapGet("/providers",
        Tamma.Api.Endpoints.Admin.AdminProviderPricingEndpoints.ListProviders)
    .RequireAuthorization("PlatformOwnerAccess");
admin.MapPost("/providers",
        Tamma.Api.Endpoints.Admin.AdminProviderPricingEndpoints.RegisterProvider)
    .RequireAuthorization("PlatformOwnerAccess");
admin.MapPatch("/providers/{key}",
        Tamma.Api.Endpoints.Admin.AdminProviderPricingEndpoints.UpdateProvider)
    .RequireAuthorization("PlatformOwnerAccess");
admin.MapGet("/providers/{key}/prices",
        Tamma.Api.Endpoints.Admin.AdminProviderPricingEndpoints.ListPrices)
    .RequireAuthorization("PlatformOwnerAccess");
admin.MapPut("/providers/{key}/prices",
        Tamma.Api.Endpoints.Admin.AdminProviderPricingEndpoints.VersionPrice)
    .RequireAuthorization("PlatformOwnerAccess");

// Story 34-5 — platform MARGIN policy admin (view + version). PlatformOwnerAccess:
// margin policies are platform-GLOBAL in both modes (no per-tenant margin rows),
// so it is platform-scoped admin work — NOT OwnerAccess. The PUT supersedes the
// prior active row + inserts a new one and emits PRICING.MARGIN.UPDATED.
admin.MapGet("/pricing/margins",
        Tamma.Api.Endpoints.Admin.AdminPricingEndpoints.ListMargins)
    .RequireAuthorization("PlatformOwnerAccess");
admin.MapPut("/pricing/margins",
        Tamma.Api.Endpoints.Admin.AdminPricingEndpoints.VersionMargin)
    .RequireAuthorization("PlatformOwnerAccess");

// Story 34-2 — plan-catalog admin write surface under /api/admin/pricing/plans*.
// PlatformOwnerAccess (Finding C1): the price book is platform-GLOBAL in both
// modes (no per-tenant override layer), so it is platform-scoped admin work — a
// tenant_owner/admin/member gets 403. All mutation flows through the immutable,
// versioned PlanVersionEditor (Story 34-1) and emits PLAN.CATALOG.UPDATED /
// PLAN.CUSTOM.CREATED DCB events. (The 34-1 read routes stay at /api/admin/plans.)
admin.MapGet("/pricing/plans",
        Tamma.Api.Endpoints.Admin.AdminPlanCatalogEndpoints.ListForAdmin)
    .RequireAuthorization("PlatformOwnerAccess");
admin.MapPost("/pricing/plans",
        Tamma.Api.Endpoints.Admin.AdminPlanCatalogEndpoints.CreatePlan)
    .RequireAuthorization("PlatformOwnerAccess");
admin.MapPut("/pricing/plans/{slug}",
        Tamma.Api.Endpoints.Admin.AdminPlanCatalogEndpoints.VersionPlan)
    .RequireAuthorization("PlatformOwnerAccess");
admin.MapPost("/pricing/plans/custom",
        Tamma.Api.Endpoints.Admin.AdminPlanCatalogEndpoints.CreateCustomPlan)
    .RequireAuthorization("PlatformOwnerAccess");
admin.MapDelete("/pricing/plans/{slug}/versions/{version:int}",
        Tamma.Api.Endpoints.Admin.AdminPlanCatalogEndpoints.DeprecateVersion)
    .RequireAuthorization("PlatformOwnerAccess");

// Story 34-9 — the platform-owner PRICING DASHBOARD read surface. One read-only
// aggregation over the existing 34-x data (plan catalog + live per-plan active
// tenant-assignment counts + a margin-config rollup) that powers the admin
// Pricing dashboard. PlatformOwnerAccess (Finding C1 + the 34-5 estimate-leak
// rule): this surface reveals platform-internal economics (list prices + margin
// knobs) that a tenant caller must NEVER see, so it stays platform-owner-only —
// the tenant-facing /api/pricing/* surface only ever exposes the sell price. No
// new pricing logic and no schema (additive read; no EF migration).
admin.MapGet("/pricing/overview",
        Tamma.Api.Endpoints.Admin.AdminPricingDashboardEndpoints.GetOverview)
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
// Story 34-6 (AC5) — platform-owner read of any tenant's resolved entitlements.
admin.MapGet("/tenants/{tenantId:guid}/entitlements",
        Tamma.Api.Endpoints.Admin.AdminTenantsEndpoints.GetTenantEntitlements)
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
// Story 28-5 AC4 — cancel a pending delete during the cooling-off window.
// PlatformOwnerAccess (cross-tenant infra state, like delete/force-delete).
admin.MapPost("/tenants/{tenantId:guid}/actions/cancel-delete",
        Tamma.Api.Endpoints.Admin.AdminTenantsEndpoints.CancelDeleteTenant)
    .RequireAuthorization("PlatformOwnerAccess");
// Story 28-5 AC7 — operator-triggered cleanup of damaged tenants.
// Story 28-R2 / C1: PlatformOwnerAccess (destructive DDL across DBs).
admin.MapPost("/tenants/{tenantId:guid}/cleanup",
        Tamma.Api.Endpoints.Admin.AdminTenantsEndpoints.CleanupTenant)
    .RequireAuthorization("PlatformOwnerAccess");
admin.MapPatch("/tenants/{tenantId:guid}/plan",
        Tamma.Api.Endpoints.Admin.AdminTenantsEndpoints.UpdateTenantPlan)
    .RequireAuthorization("PlatformOwnerAccess");
// Story 34-4 — idempotent version-pinned assign (body { planId, reason?, force? })
// + period-end / immediate cancel → free. Both delegate to
// IPlanAssignmentService, emit TENANT.PLAN.CHANGED / .CANCELLED, and keep the
// lockstep tenant plan columns aligned. PlatformOwnerAccess.
admin.MapPut("/tenants/{tenantId:guid}/plan",
        Tamma.Api.Endpoints.Admin.AdminTenantsEndpoints.PutTenantPlan)
    .RequireAuthorization("PlatformOwnerAccess");
admin.MapPost("/tenants/{tenantId:guid}/plan/cancel",
        Tamma.Api.Endpoints.Admin.AdminTenantsEndpoints.CancelTenantPlan)
    .RequireAuthorization("PlatformOwnerAccess");

// Unified-tenancy Phase 4 — move a tenant's schema to another pool row.
// POST validates cheaply + enqueues a `tenant.move` platform task and
// returns 202 with the GET polling URL (the same 202-plus-status-poll
// shape the Cranl provisioning endpoints use); the MoveTenantTaskHandler
// drives ITenantMoveService.MoveAsync when PlatformTaskWorker claims the
// row. GET reports Status / FailureReason / current placement.
admin.MapPost("/tenants/{tenantId:guid}/move",
        Tamma.Api.Endpoints.Admin.AdminTenantsEndpoints.MoveTenant)
    .RequireAuthorization("PlatformOwnerAccess");
admin.MapGet("/tenants/{tenantId:guid}/move",
        Tamma.Api.Endpoints.Admin.AdminTenantsEndpoints.GetTenantMove)
    .RequireAuthorization("PlatformOwnerAccess");

// Unified-tenancy Phase 4 — platform-admin CRUD over the tenant_databases
// registry (the operator's DB pool). The admin connection string travels
// inbound only (encrypted at rest; never serialised into a response);
// rotation evicts the TenantDatabasePool decrypt cache. PlatformOwnerAccess
// because pool rows carry cross-tenant blast radius.
admin.MapGet("/tenant-databases",
        Tamma.Api.Endpoints.Admin.AdminTenantDatabasesEndpoints.ListDatabases)
    .RequireAuthorization("PlatformOwnerAccess");
admin.MapGet("/tenant-databases/{databaseId:guid}",
        Tamma.Api.Endpoints.Admin.AdminTenantDatabasesEndpoints.GetDatabaseDetail)
    .RequireAuthorization("PlatformOwnerAccess");
admin.MapPost("/tenant-databases",
        Tamma.Api.Endpoints.Admin.AdminTenantDatabasesEndpoints.CreateDatabase)
    .RequireAuthorization("PlatformOwnerAccess");
admin.MapPatch("/tenant-databases/{databaseId:guid}",
        Tamma.Api.Endpoints.Admin.AdminTenantDatabasesEndpoints.UpdateDatabase)
    .RequireAuthorization("PlatformOwnerAccess");
admin.MapDelete("/tenant-databases/{databaseId:guid}",
        Tamma.Api.Endpoints.Admin.AdminTenantDatabasesEndpoints.DeleteDatabase)
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

// Story 37-3 — platform-scope audit query. Same PlatformOwnerAccess gate as
// the alerts/alert-rules platform-admin surface (cross-tenant + platform-
// internal blast radius). Reads ONLY the control-plane audit_records rows;
// a tenant's audit lives in a different schema and is never returned here.
v1Admin.MapGet("/audit", AdminEndpoints.ListPlatformAudit)
    .RequireAuthorization("PlatformOwnerAccess");

// Story 37-2 (AC8) — platform (or ?tenantId) audit hash-chain verification +
// on-demand checkpoint. PlatformOwnerAccess (same gate as the platform audit
// read). The tenant-scope verify lives on the org route below (tenant_admin+).
v1Admin.MapGet("/audit/verify", AdminEndpoints.VerifyPlatformAudit)
    .RequireAuthorization("PlatformOwnerAccess");
v1Admin.MapPost("/audit/checkpoint", AdminEndpoints.CheckpointAudit)
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
// Story 34-5 — tenant-facing pricing estimate (MemberAccess). Prices a
// hypothetical usage line under the caller's OWN plan + (tenant, provider)
// pricing mode; the margin-policy rows themselves stay platform-owner-only
// (/api/admin/pricing/*). Powers the upgrade/cost UI in packages/dashboard-user.
var pricing = app.MapGroup("/api/pricing").RequireAuthorization("MemberAccess");
pricing.MapGet("/estimate", Tamma.Api.Endpoints.PricingEndpoints.GetEstimate);
// Story 34-6 — the caller's OWN resolved entitlements + live headroom. Read is
// unprivileged (any authenticated member); tenant is taken from ITenantContext
// (SaaS) / the sole user (single-user), never from a request param.
pricing.MapGet("/entitlements", Tamma.Api.Endpoints.PricingEndpoints.GetEntitlements);
// Story 34-2 (AC1/AC2) — the PUBLIC plan catalog powering the pricing/upgrade UI.
// Active, non-custom plans only (deprecated/draft/custom excluded by the
// IPlanCatalogService filter). MemberAccess: any authenticated tenant member can
// read; the admin write surface stays platform-owner-only (/api/admin/pricing/plans*).
pricing.MapGet("/plans", Tamma.Api.Endpoints.PricingEndpoints.ListPublicPlans);
pricing.MapGet("/plans/{slug}", Tamma.Api.Endpoints.PricingEndpoints.GetPublicPlanBySlug);
// Story 34-4 (AC11) — tenant self-service subscribe to a PUBLIC plan. Tenant is
// resolved strictly from ITenantContext (never the body) so a caller can only
// affect their OWN tenant. Gated by SettingsManage (tenant_owner) on top of the
// group's MemberAccess — a member-role caller gets 403; custom/draft/deprecated
// or unknown plans return 422.
pricing.MapPost("/subscribe", Tamma.Api.Endpoints.PricingEndpoints.Subscribe)
    .RequireAuthorization("SettingsManage");

// Story 34-3 — per-(tenant, provider) BYOK toggle. Reads inherit the group's
// MemberAccess (any authenticated tenant member sees their OWN modes); the byok
// mutations use PricingManage (tenant_owner OR tenant_admin — a member-role
// caller gets 403). The tenant is resolved STRICTLY from ITenantContext (never a
// route/body tenantId), so there is no cross-tenant IDOR. The 422 SaaS-eligibility
// gate (cli-token / unknown provider) is applied inside EnableByok via Story 32-4's
// IProviderAuthLookup. Reveal-safe: responses carry { provider, mode, keySet }.
pricing.MapGet("/providers", Tamma.Api.Endpoints.PricingEndpoints.ListProviderModes);
pricing.MapGet("/providers/{provider}", Tamma.Api.Endpoints.PricingEndpoints.GetProviderMode);
pricing.MapPost("/providers/{provider}/byok", Tamma.Api.Endpoints.PricingEndpoints.EnableByok)
    .RequireAuthorization("PricingManage");
pricing.MapDelete("/providers/{provider}/byok", Tamma.Api.Endpoints.PricingEndpoints.DisableByok)
    .RequireAuthorization("PricingManage");

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
// Tenancy residual (post-#343): self-service re-provision for a tenant's
// own owner/admin. Membership filter kills cross-tenant access; the
// handler enforces admin+ role and the failed/degraded-only state machine.
orgs.MapPost("/{tenantId:guid}/reprovision", OrgEndpoints.ReprovisionOrg)
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
// Story 37-2 (AC8): tenant-scoped audit hash-chain verification (tenant_admin+).
orgs.MapGet("/{tenantId:guid}/audit/verify", OrgEndpoints.VerifyTenantAudit)
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
// Story 29-6 (audit gap #2) — tenant-scoped trigger of the AUDITED
// rotate-secret SAGA workflow (distinct from the legacy reveal-based
// rotate above). Admin+ enforced inside the handler; membership proof
// via the filter. Returns 202 + correlation id.
orgs.MapPost("/{tenantId:guid}/secrets/{id:guid}/rotate-workflow",
        SecretEndpoints.TriggerRotateTenantWorkflow)
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

// Story 36-3 — tenant usage analytics API. Reads Story 36-1's per-tenant
// analytics_usage_hourly / analytics_usage_daily fact tables (populated by
// Story 36-2) through ITenantDbContextFactory. Member-read (the group's
// MemberAccess policy + the path-tenant membership filter); NO owner/admin
// gate — usage analytics is read-only and tenant-wide. A member of org A can
// never reach org B's route (403) nor its schema (physical isolation).
orgs.MapGet("/{tenantId:guid}/analytics/usage", TenantAnalyticsEndpoints.GetUsage)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();
orgs.MapGet("/{tenantId:guid}/analytics/usage/breakdown", TenantAnalyticsEndpoints.GetBreakdown)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();

// Story 36-4 — tenant cost & spend analytics API (BYOK vs platform split +
// budget projection). Same MemberAccess + membership-filter gate as the usage
// routes above; reads the per-tenant analytics_usage_daily fact table only and
// exposes the tenant's own BYOK cost + billed amount (never a platform-internal
// margin). Read-only aggregation + one deduped budget-exceeded DCB event.
orgs.MapGet("/{tenantId:guid}/analytics/cost", TenantAnalyticsEndpoints.GetCost)
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

// Story 32-6 — per-agent ACTION TRAIL read surface. Member-readable, no
// mutation. The path-tenant membership filter proves the caller is a member of
// {tenantId}; the IEventRepository.QueryAgentTrailAsync read is PHYSICALLY scoped
// to that tenant's schema (schema-per-tenant), so a member of org A can never
// read org B's trail and a platform owner has no route to any tenant's trail
// (AC4). Both page on SequenceNumber (nextCursor/hasMore).
orgs.MapGet("/{tenantId:guid}/agents/{agentId:guid}/runs", AgentTrailEndpoints.ListRuns)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();
orgs.MapGet("/{tenantId:guid}/agents/{agentId:guid}/trail", AgentTrailEndpoints.ListTrail)
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

// Story 29-6 (audit gap #2) — platform-scope trigger of the AUDITED
// rotate-secret SAGA workflow. Mints a fresh correlation id, takes the
// per-secret concurrency guard, dispatches mint → push → probe →
// activate → schedule-retire, returns 202 + correlation id. This is the
// trigger that finally STARTS the workflow (it was previously dead
// unless invoked manually via Elsa Studio). Gated by PlatformOwnerAccess
// (cross-tenant infrastructure — NOT OwnerAccess, which admits every
// personal-tenant owner).
app.MapPost("/api/v1/secrets/{secretId:guid}/rotate", SecretEndpoints.TriggerRotateWorkflow)
    .RequireAuthorization("PlatformOwnerAccess");

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

// Story 18-4 AC4/AC7 non-migration write slices (WRITE counterparts to the
// Story 21-4 GET /api/v1/repos read; tenant resolved strictly from
// ITenantContext, null-tenant fails closed → 404, no IDOR):
//   PATCH .../onboarding/repos/{installationId}/{repoId} — flip the EXISTING
//     IsActive flag on a repo of the caller's OWN installation. Gated by
//     PlatformsManage (tenant_owner/tenant_admin → member 403): managing which
//     repos Tamma monitors is a platform-admin action, same policy as the
//     platform-install write above. Idempotent; emits REPO.(DE)ACTIVATED.SUCCESS.
//   POST .../onboarding/complete — record onboarding completion + emit
//     ONBOARDING.COMPLETED.SUCCESS (the DCB event IS the record; no new column).
//     MemberAccess, matching the sibling wizard endpoints. Idempotent.
app.MapPatch("/api/v1/onboarding/repos/{installationId:long}/{repoId:long}", OnboardingEndpoints.SetRepoActive)
    .RequireAuthorization("PlatformsManage");
app.MapPost("/api/v1/onboarding/complete", OnboardingEndpoints.CompleteOnboarding)
    .RequireAuthorization("MemberAccess");

// ── Story 31-9 — onboarding platform picker + installation API ──
// GET /platforms returns the list of platforms the picker renders +
// per-kind capability flags, marking deferred kinds (Bitbucket /
// AzureDevOps) as coming-soon. POST /install is gated by the new
// PlatformsManage policy (admin+owner): wires a credential into the
// Epic 29 cabinet, runs an auth dry-run via the driver factory, and
// inserts a tenant_platform_installations row. GET /installations
// powers the connected-platforms list on the settings panel.
app.MapGet("/api/onboarding/platforms", PlatformInstallEndpoints.ListPlatforms)
    .RequireAuthorization("MemberAccess");
app.MapPost("/api/onboarding/install", PlatformInstallEndpoints.Install)
    .RequireAuthorization("PlatformsManage");
app.MapGet("/api/onboarding/installations", PlatformInstallEndpoints.ListInstallations)
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

// ── Story 32-1 — first-class agent entities (/api/v1/agents) ──
// Reads inherit the group's SettingsView gate; writes use AgentManage
// (tenant_owner/tenant_admin → member 403). Public-agent writes are
// additionally gated by the platform-admin claim inside the handlers. The
// legacy GET/PUT /config endpoints above are untouched (cutover is later in
// the epic). :guid constraints keep the {id} routes from colliding with the
// {role}/resolve route.
agents.MapGet("/", AgentEndpoints.ListAgents);
agents.MapPost("/", AgentEndpoints.CreateAgent)
    .RequireAuthorization("AgentManage").RequireRateLimiting("ConfigWrite");
agents.MapGet("/{id:guid}", AgentEndpoints.GetAgent);
agents.MapGet("/{id:guid}/versions", AgentEndpoints.ListVersions);
agents.MapGet("/{id:guid}/versions/{version:int}", AgentEndpoints.GetVersion);
agents.MapPost("/{id:guid}/versions", AgentEndpoints.PublishVersion)
    .RequireAuthorization("AgentManage").RequireRateLimiting("ConfigWrite");
agents.MapPost("/{id:guid}/archive", AgentEndpoints.ArchiveAgent)
    .RequireAuthorization("AgentManage").RequireRateLimiting("ConfigWrite");

// ── Story 32-3 — tenant-admin BYOK provider-credential management ──
// GET list (metadata only) inherits the group's SettingsView gate; the
// register/rotate/delete mutations are AgentManage (tenant_owner/tenant_admin →
// member 403). Response bodies never carry the raw key (reveal-once token only).
agents.MapGet("/providers", ProviderCredentialEndpoints.ListProviders);
agents.MapPost("/providers/{provider}/credential", ProviderCredentialEndpoints.RegisterCredential)
    .RequireAuthorization("AgentManage").RequireRateLimiting("ConfigWrite");
agents.MapPost("/providers/{provider}/credential/rotate", ProviderCredentialEndpoints.RotateCredential)
    .RequireAuthorization("AgentManage").RequireRateLimiting("ConfigWrite");
agents.MapDelete("/providers/{provider}/credential", ProviderCredentialEndpoints.DeleteCredential)
    .RequireAuthorization("AgentManage").RequireRateLimiting("ConfigWrite");

// ── Integration BYOK — tenant-admin JIRA + email credential management ──
// Sibling of the Story 32-3 provider BYOK endpoints. Set/remove the tenant's own
// JIRA/email credential in the secret cabinet so the mediation resolves it
// per-request (BYOK→system→fail-loud). PlatformsManage (tenant_owner/tenant_admin →
// member 403), like the platform-installation picker. Set is reveal-safe — the
// response carries the version only, NEVER the secret.
app.MapPost("/api/v1/integrations/jira/credential", IntegrationCredentialEndpoints.SetJiraCredential)
    .RequireAuthorization("PlatformsManage").RequireRateLimiting("ConfigWrite")
    .WithName("SetJiraCredential");
app.MapDelete("/api/v1/integrations/jira/credential", IntegrationCredentialEndpoints.DeleteJiraCredential)
    .RequireAuthorization("PlatformsManage").RequireRateLimiting("ConfigWrite")
    .WithName("DeleteJiraCredential");
app.MapPost("/api/v1/integrations/email/credential", IntegrationCredentialEndpoints.SetEmailCredential)
    .RequireAuthorization("PlatformsManage").RequireRateLimiting("ConfigWrite")
    .WithName("SetEmailCredential");
app.MapDelete("/api/v1/integrations/email/credential", IntegrationCredentialEndpoints.DeleteEmailCredential)
    .RequireAuthorization("PlatformsManage").RequireRateLimiting("ConfigWrite")
    .WithName("DeleteEmailCredential");

// ── Story 32-2 — entity-aware registry / resolution surface (/api/agents) ──
// Distinct from the legacy /api/v1/agents group above (which stays byte-for-byte
// working). Reads (list / get-one / resolve / role-selection reads) under
// MemberAccess (any member); writes (create / version / archive / rollback /
// role-selection upsert) under AgentManage (admin+owner → member 403). Public-
// agent mutation is additionally gated in-handler by the platform-admin claim.
var agentsV2 = app.MapGroup("/api/agents")
    .RequireAuthorization("MemberAccess")
    .RequireRateLimiting("ConfigRead");
// Reads
// AC5 — optional ?role=&visibility=&status= query filters bind automatically
// onto ListAgents' trailing string? params; they NARROW the visibility-scoped
// set (never widen it). Unknown role/visibility/status → 400.
agentsV2.MapGet("/", AgentEndpoints.ListAgents);
agentsV2.MapGet("/resolve", AgentEndpoints.Resolve);
agentsV2.MapGet("/role-selections", AgentEndpoints.GetRoleSelections);
agentsV2.MapGet("/{id:guid}", AgentEndpoints.GetAgent);
agentsV2.MapGet("/{id:guid}/versions", AgentEndpoints.ListVersions);
agentsV2.MapGet("/{id:guid}/versions/{version:int}", AgentEndpoints.GetVersion);
// Writes
agentsV2.MapPost("/", AgentEndpoints.CreateAgent)
    .RequireAuthorization("AgentManage").RequireRateLimiting("ConfigWrite");
agentsV2.MapPost("/{id:guid}/versions", AgentEndpoints.PublishVersion)
    .RequireAuthorization("AgentManage").RequireRateLimiting("ConfigWrite");
agentsV2.MapPost("/{id:guid}/archive", AgentEndpoints.ArchiveAgent)
    .RequireAuthorization("AgentManage").RequireRateLimiting("ConfigWrite");
agentsV2.MapPost("/{id:guid}/rollback", AgentEndpoints.RollbackVersion)
    .RequireAuthorization("AgentManage").RequireRateLimiting("ConfigWrite");
agentsV2.MapPut("/role-selections/{role}", AgentEndpoints.SelectForRole)
    .RequireAuthorization("AgentManage").RequireRateLimiting("ConfigWrite");

// ── Story 32-16 — per-tenant agent/persona enablement (catalog membership) ──
// GET (catalog view) inherits the group's MemberAccess gate — any member may
// read. PUT/DELETE (enable/disable a public persona for THIS tenant's catalog)
// are AgentManage (tenant_owner/tenant_admin → member 403). Public-catalog
// management (creating/retiring personas) stays PlatformOwnerAccess and is NOT
// in this group.
agentsV2.MapGet("/enablement", AgentEndpoints.ListEnablement);
agentsV2.MapPut("/{agentId:guid}/enablement", AgentEndpoints.SetEnablement)
    .RequireAuthorization("AgentManage").RequireRateLimiting("ConfigWrite");
agentsV2.MapDelete("/{agentId:guid}/enablement", AgentEndpoints.DisableEnablement)
    .RequireAuthorization("AgentManage").RequireRateLimiting("ConfigWrite");

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
// Story 27-18 — the generic action-default tier and its
// `GET /api/prompts/defaults/{action}` route were removed (clean cut, no
// compat shim). Resolution is override → system default → error.
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
// Legacy starter-template catalogue (Story 27 prep). LEFT UNCHANGED for
// backward compat — distinct surface from the DB-backed convention store below.
app.MapGet("/api/convention-templates", ConventionEndpoints.ListAll);
app.MapGet("/api/convention-templates/{key}", ConventionEndpoints.GetByKey);

// ── Convention Store (DB-backed, Story 27-10) ──
// Tenant CRUD + tenant-scoped resolution + registry pickers. All endpoints
// require auth (any authed tenant member can READ; ConventionManage gates the
// tenant override mutations). Mirrors the prompt-store registration in style,
// with one DELIBERATE difference: convention reads use AuthenticatedAny (any
// authed tenant member) rather than SettingsView (admin/owner) because
// conventions are per-workflow context injected into prompts — all agents
// running a workflow need read access, not just admins (Story 27-10 spec §5).
//
// Route ordering (mirrors prompt store Story 27-3): the specific routes
// (/defaults*, /resolve, /registry/*) MUST be registered BEFORE the
// parameterized /{role}/{action} so a literal segment like "resolve" is not
// swallowed by the {role} route.
//
// Rate limiting: the prompt-store endpoints do NOT apply per-endpoint rate
// limiting (the /api/prompts group carries no RequireRateLimiting), so for
// consistency the convention store does the same here rather than inventing a
// bespoke per-tenant limiter. The story's GET 100/min / write 30/min / resolve
// 300/min targets are tracked as a deferred item (would reuse the existing
// AddFixedWindowLimiter policies in Program.cs once the prompt store adopts
// them too).
var conventions = app.MapGroup("/api/conventions").RequireAuthorization("AuthenticatedAny");
conventions.MapGet("/", ConventionStoreEndpoints.ListAll);
// Specific routes first — defaults, resolve, registry.
conventions.MapGet("/defaults", ConventionStoreEndpoints.ListSystemDefaults);
conventions.MapGet("/defaults/{role}/{action}", ConventionStoreEndpoints.GetSystemDefault);
conventions.MapPost("/resolve", ConventionStoreEndpoints.Resolve);
conventions.MapGet("/registry/roles", ConventionStoreEndpoints.RegistryRoles);
conventions.MapGet("/registry/actions", ConventionStoreEndpoints.RegistryActions);
conventions.MapGet("/registry/role-actions", ConventionStoreEndpoints.RegistryRoleActions);
// Parameterized routes last. Tenant override mutations gated by ConventionManage
// (tenant_owner/tenant_admin; member → 403). Reads are any authed member.
conventions.MapGet("/{role}/{action}", ConventionStoreEndpoints.GetResolved);
conventions.MapPut("/{role}/{action}", ConventionStoreEndpoints.UpsertTenantOverride).RequireAuthorization("ConventionManage");
conventions.MapDelete("/{role}/{action}", ConventionStoreEndpoints.DeleteTenantOverride).RequireAuthorization("ConventionManage");

// ── Convention Store — system-default admin (platform-admin only) ──
// PlatformOwnerAccess matches the prompt/other admin routes' platform-admin
// gate. Non-platform-admin → 403.
var adminConventions = app.MapGroup("/api/admin/conventions").RequireAuthorization("PlatformOwnerAccess");
adminConventions.MapPut("/{role}/{action}", ConventionStoreEndpoints.UpsertSystemDefault);
adminConventions.MapDelete("/{role}/{action}", ConventionStoreEndpoints.DeleteSystemDefault);
adminConventions.MapPost("/{role}/{action}/reset", ConventionStoreEndpoints.ResetSystemDefault);

// ── Acceptance Rules (Story 39-5) ──
// Configurable per-document-type acceptance policy (autonomy dial + bounds +
// escalation + reviewer selection + guidance). RBAC parity with the prompt/
// convention stores, with the SAME DELIBERATE read-gate deviation as the
// convention store: reads use AuthenticatedAny (any authed tenant member) rather
// than SettingsView (admin/owner) because AC7 requires reads for any tenant
// member — the orchestrator + role-holders all need to see the effective rules,
// not just admins. Writes are gated by AcceptanceRulesManage (tenant_owner/
// tenant_admin; member → 403).
//
// Route ordering (mirrors prompt/convention stores): the specific literal route
// (/defaults) MUST be registered BEFORE the parameterized /{documentTypeKey} so
// "defaults" is not swallowed by the {documentTypeKey} route. The literal `base`
// segment addresses the principal base row (the dial) and is handled inside
// GetResolved / Upsert / Delete.
var acceptanceRules = app.MapGroup("/api/acceptance-rules").RequireAuthorization("AuthenticatedAny");
acceptanceRules.MapGet("/", AcceptanceRulesEndpoints.ListEffective);
acceptanceRules.MapGet("/defaults", AcceptanceRulesEndpoints.GetDefaults);
acceptanceRules.MapGet("/{documentTypeKey}", AcceptanceRulesEndpoints.GetResolved);
acceptanceRules.MapPut("/{documentTypeKey}", AcceptanceRulesEndpoints.Upsert).RequireAuthorization("AcceptanceRulesManage");
acceptanceRules.MapDelete("/{documentTypeKey}", AcceptanceRulesEndpoints.Delete).RequireAuthorization("AcceptanceRulesManage");

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
// Story 23-6 — deep provider diagnostics (latency percentiles / error classes /
// token+cost analytics / per-model usage). Tenant-scoped read, inherits the
// group's SettingsView gate. No platform margin exposed (Story 34-5 rule).
providers.MapGet("/diagnostics/deep", ProviderEndpoints.GetDeepDiagnostics);
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
// Story 4-7 — time-travel event query: tenant-scoped, keyset-paginated read over
// domain_events with time-range / correlationId / actor / type (exact|prefix)
// filters. Inherits WorkflowsView (read-only, tenant-scoped) from the group.
engine.MapGet("/events/query", EngineEndpoints.QueryEvents);
// Story 4-8 — black-box replay: reconstruct a run's point-in-time state by folding
// its ordered DCB event slice (a pure, read-only fold over domain_events; no Elsa
// re-run). Inherits WorkflowsView (read-only, tenant-scoped) from the group;
// null-tenant fails closed (404). ?upTo={seq|timestamp} = as-of point; ?from={seq}
// adds a diff (AC6).
engine.MapGet("/runs/{correlationId}/replay", EngineEndpoints.ReplayRun);
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
// Generic engine→domain_events DCB-event append. The Elsa engine drains its
// in-process tamma:events list here so the audit trail persists (previously
// the events were written to a write-only transient list nothing drained).
// Gated to EngineServiceOnly (service principal) — NOT WorkflowsManage, which
// every tenant owner/admin holds and would let them forge audit events (I4).
engine.MapPost("/events", EngineEndpoints.AppendEvents).RequireAuthorization("EngineServiceOnly");
// Engine→platform_events callback: cross-tenant lifecycle / analytics events that the
// engine drains from its in-process list and POSTs here for durable control-plane
// persistence + in-process fan-out. Gated EngineServiceOnly (same rationale as /events).
engine.MapPost("/platform-events", EngineEndpoints.AppendPlatformEvents)
    .RequireAuthorization("EngineServiceOnly");
// Audit finding 002 — `agent-available` is a GET liveness probe (no body),
// not a POST registration call. The previous wiring as POST silently drifted
// from the TS contract.
engine.MapGet("/agent-available", EngineEndpoints.AgentAvailable);

// ── User dashboard: Repos & Workflow Runs (Story 21-4) ──
// Tenant-facing read surface behind the SPA's /repos + /runs destinations.
// Tenant is resolved strictly from ITenantContext inside each handler (no
// path/body tenant → no IDOR); a null/empty tenant fails closed with
// 404 no_active_tenant (mirrors the Story 23-6 / #283 diagnostics fix). All
// three are member-level reads over data that already exists (connected
// installations + the DCB run event stream); per-run cost is the tenant's OWN
// recorded spend (no platform margin). Kept out of the health region so the
// concurrent System Health work (#277) does not conflict.
app.MapGet("/api/v1/repos", ReposRunsEndpoints.ListRepos).RequireAuthorization("MemberAccess");
app.MapGet("/api/v1/runs", ReposRunsEndpoints.ListRuns).RequireAuthorization("MemberAccess");
// Story 23-5 Workflow Monitor: windowed per-status/per-definition instance counts
// (literal segment — never collides with the {runId:guid} route below). Counts
// only; same fail-closed tenant scoping, no economics.
app.MapGet("/api/v1/runs/summary", ReposRunsEndpoints.GetRunsSummary).RequireAuthorization("MemberAccess");
app.MapGet("/api/v1/runs/{runId:guid}", ReposRunsEndpoints.GetRunDetail).RequireAuthorization("MemberAccess");

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

// ── ADL human gates (IMPORTANT-2) ──
// Lets a human DRIVE the merge-approval gate of the autonomous loop. Resumes the
// tenant+repo-scoped adl-merge-approval-{tenant}-{repo}-{issue}-{pr} bookmark via
// the engine, injecting the {decision,feedback,approver} payload (approver is
// derived server-side from the caller — I2). WorkflowsManage = tenant owner/admin
// (members 403). SECURITY C1 — the handler threads the caller's ambient tenant id
// so a caller can only resume a gate in its OWN tenant (cross-tenant → 404).
var adl = app.MapGroup("/api/adl").RequireAuthorization("WorkflowsManage");
adl.MapPost("/merge-approval/resume", AdlEndpoints.ResumeMergeApproval);
// Production-deploy approval gate (completeness audit P0 item 3). Resumes the
// tenant+repo+SHA-scoped adl-deploy-prod-approval-{tenant}-{repo}-{issue}-{sha}
// bookmark; approver derived server-side (I2); tenant-scoped (cross-tenant → 404).
adl.MapPost("/deploy-approval/resume", AdlEndpoints.ResumeDeploymentApproval);
// Blocker-diagnosis progressive-resolution ladder (follow-up #15). Resumes the
// session-scoped blocker-progress-{session}-{level} / blocker-escalation-{session}
// bookmark so a run can reach the Resolved terminal. WorkflowsManage = tenant
// owner/admin (members 403); resolver derived server-side (I2). IDOR guard: the
// handler verifies the caller's ambient tenant OWNS the session (tenant-scoped
// session lookup) before forwarding — a cross-tenant/unknown session → 404.
adl.MapPost("/blocker/resume", AdlEndpoints.ResumeBlocker);
// Clarifying-questions answer gate (Story 3.5). Resumes the tenant+session-scoped
// clarify-answers-{tenant}-{session} bookmark with the stakeholder's answers so the
// workflow can incorporate them. WorkflowsManage = tenant owner/admin (members 403);
// resolver derived server-side (I2). IDOR guard: the engine folds the caller's ambient
// tenant id into the bookmark name, so a cross-tenant/unknown session → 404.
adl.MapPost("/clarify/resume", AdlEndpoints.ResumeClarify);
// Design-proposal review gate (Story 3.7). Resumes the tenant+session-scoped
// design-approval-{tenant}-{session} bookmark with the reviewer's approve/reject
// decision so the workflow can finalise (approved → implementation, rejected →
// feedback captured). WorkflowsManage = tenant owner/admin (members 403); reviewer
// derived server-side (I2). IDOR guard: the engine folds the caller's ambient tenant
// id into the bookmark name, so a cross-tenant/unknown session → 404.
adl.MapPost("/design/resume", AdlEndpoints.ResumeDesign);

// ── Story 39-8: generic document-decision gate + escalation disposition ──
// The ONE generic decision gate's public surface (resume) + the escalation disposition
// surface. D10 — AuthenticatedAny, DEVIATING from the adl group's WorkflowsManage: AC5 says
// SaaS deciders are tenant MEMBERS per RBAC, and the whole point of orchestrator-assigned
// decisions (Task View, 39-20) is that member users decide — WorkflowsManage (owner/admin)
// would 403 exactly the assigned decider. Security still holds: tenant folding + a 128-bit
// session id (cross-tenant → 404, unguessable within tenant). Per-assignee authorization
// ("only the user the orchestrator assigned") is 39-20's ITaskAudienceResolver — recorded
// here as explicit MIGRATION DEBT (the convention-store-deviation style), not an oversight.
// Single-user mode folds the sole user's scope (ambient tenant null → "none" segment).
var documents = app.MapGroup("/api/documents").RequireAuthorization("AuthenticatedAny");
documents.MapPost("/decisions/{sessionId}/resume", DocumentDecisionEndpoints.ResumeDecision);
documents.MapPost("/escalations/{escalationId}/resolve", DocumentDecisionEndpoints.ResolveEscalation);

// ── GitHub App (no auth, webhook signature verification) ──
// Audit finding 017 — webhook gets the GitHubWebhook policy (300/min). The
// install callback shares OAuthStart's 60/min cap; legitimate installs are
// rare events but the route is publicly reachable.
var github = app.MapGroup("/api/github");
github.MapGet("/callback", GitHubEndpoints.Callback)
    .RequireRateLimiting("OAuthStart");
// Legacy GitHub-specific webhook path. Story 31-7 generalises the
// receiver to /api/webhooks/{platform}; the old path stays active
// (with the GitHub-specific install-linking logic) for the deprecation
// window so in-flight GitHub deliveries during a deploy don't change
// shape. New deployments wire downstream platforms (Gitea, Forgejo,
// GitLab) through the new path; the next epic story will port the
// install-linking handler into a neutral IWebhookHandler and retire
// the legacy path.
github.MapPost("/webhooks", GitHubEndpoints.Webhooks)
    .RequireRateLimiting("GitHubWebhook");

// Story 31-7 — generalised webhook receiver. Path-based routing for
// GitHub/Gitea/Forgejo/GitLab; per-platform signature verification,
// idempotency, and dispatch via IWebhookEventDispatcher.
app.MapPost("/api/webhooks/{platform}", WebhookEndpoints.Receive)
    .RequireRateLimiting("GitHubWebhook"); // reuse the 300/min budget

// ── Story 35-5 — Stripe billing webhook (SaaS only; signature-gated, no JWT) ──
// Mapped only in SaaS mode: single-user has no Stripe surface (NullBillingProvider,
// 35-1 AC7 / 35-5 AC13), so the webhook + admin routes stay unmapped (→ 404). The
// webhook route is anonymous at the app-auth layer (Stripe calls it) and gated by
// the Stripe signature instead; the admin routes are PlatformOwnerAccess (a Stripe
// webhook is a platform-operator concern, never a tenant-scoped route).
if (app.Services.GetRequiredService<Tamma.Api.Services.PromptStore.ITammaModeProvider>()
        .Mode == Tamma.Api.Services.PromptStore.TammaMode.SaaS)
{
    app.MapPost("/api/v1/billing/stripe/webhook",
            Tamma.Api.Endpoints.Billing.StripeWebhookEndpoint.Receive)
        .RequireRateLimiting("GitHubWebhook"); // reuse the 300/min webhook budget

    app.MapGet("/api/v1/admin/billing/webhook-events",
            Tamma.Api.Endpoints.Billing.BillingWebhookAdminEndpoints.List)
        .RequireAuthorization("PlatformOwnerAccess");
    app.MapPost("/api/v1/admin/billing/webhook-events/{id:guid}/replay",
            Tamma.Api.Endpoints.Billing.BillingWebhookAdminEndpoints.Replay)
        .RequireAuthorization("PlatformOwnerAccess");

    // ── Story 35-4 — tenant-scoped subscription lifecycle (SaaS only) ──
    // /api/v1/orgs/{tenantId}/billing/subscription/{checkout,change,cancel,seats}
    // + GET. Membership-gated (RequireTenantMembershipFilter); mutations require
    // tenant owner/admin (checked in-handler). Single-user leaves these unmapped
    // (NullBillingProvider, zero Stripe — AC11).
    Tamma.Api.Endpoints.Billing.SubscriptionEndpoints.MapSubscriptionEndpoints(app);
}

// ── SaaS (API key auth) ──
app.MapPost("/api/v1/llm/chat", SaaSEndpoints.LlmChat).RequireAuthorization();

// ── Story 32-5 (T4) — the call-LLM mediation endpoint (sequence step F) ──
// Internal/engine-only (Finding C2): the engine's CallLlmInlineActivity thin
// client posts an LlmCallRequest here as the service-scope Tamma:ApiToken
// (Bearer, authenticated by the platform ApiKey chain → ServiceAuthPrincipal).
// The EngineServiceOnly policy requires that typed service principal, so a user
// JWT — which authenticates but never produces a ServiceAuthPrincipal — is
// rejected with 403 (the endpoint drives arbitrary LLM spend + tool execution
// and must be engine-only, unlike the broad default the rest of the callbacks
// ride). A missing/invalid bearer ⇒ 401 from the auth pipeline before the
// handler. The handler delegates to IManagedAgent.RunAsync and maps via
// ToHttpResult under the §2.4 status discipline (200 / 200 success:false
// +httpStatusCode / 400 / 403; NEVER a raw 5xx).
app.MapPost("/api/v1/llm/call", LlmCallEndpoints.CallLlm)
    .RequireAuthorization("EngineServiceOnly")
    .WithName("CallLlm");

// ── Story 32-23 — the streaming run tap (human SSE plane) ──
// GET /api/v1/llm/runs/{correlationId}/stream — a live, READ-ONLY view of a
// managed run for the dashboard (JWT) / tamma CLI (ApiKey). Rides AuthenticatedAny
// (the HUMAN plane), NOT the engine bearer: unlike POST /api/v1/llm/call this is
// consumed by people. In SaaS the caller may only tap runs its tenant owns (a
// foreign correlationId ⇒ 404, never a cross-tenant existence oracle); in
// single-user the sole user taps any local run. Decoupled from the buffered call:
// it can never break RetryCheck/SkipIfSucceeded/the circuit breaker.
app.MapGet("/api/v1/llm/runs/{correlationId}/stream", LlmRunStreamEndpoints.StreamRun)
    .RequireAuthorization("AuthenticatedAny")
    .WithName("StreamRunTap");

// ── Story 38-1 (Epic 38) — git-platform step mediation (Class A) ──
// Same engine-only plane as /api/v1/llm/call: the engine's thin ADL git
// activities post here as the service-scope Tamma:ApiToken; the API holds the
// per-tenant token, authorizes tenant↔repo (cross-tenant guard), performs the
// platform call with the resolved token, and audits it. {owner}/{repo} is bound
// as two segments (an owner/name full name carries a slash).
app.MapPost("/api/v1/git/{owner}/{repo}/branches", GitEndpoints.CreateBranch)
    .RequireAuthorization("EngineServiceOnly")
    .WithName("GitCreateBranch");
app.MapPost("/api/v1/git/{owner}/{repo}/pull-requests", GitEndpoints.CreatePullRequest)
    .RequireAuthorization("EngineServiceOnly")
    .WithName("GitCreatePullRequest");
app.MapPut("/api/v1/git/{owner}/{repo}/pull-requests/{n:int}/merge", GitEndpoints.MergePullRequest)
    .RequireAuthorization("EngineServiceOnly")
    .WithName("GitMergePullRequest");
app.MapGet("/api/v1/git/{owner}/{repo}/pull-requests/{n:int}/comments", GitEndpoints.GetPullRequestComments)
    .RequireAuthorization("EngineServiceOnly")
    .WithName("GitGetPullRequestComments");
app.MapPatch("/api/v1/git/{owner}/{repo}/issues/{n:int}", GitEndpoints.UpdateIssue)
    .RequireAuthorization("EngineServiceOnly")
    .WithName("GitUpdateIssue");

// ── Story 38 (Phase 1) — GitHub "extra ops" (commits + file-changes reads,
// standalone branch delete) the engine's context/debug/integration activities call
// on the composite today. Same engine-only plane + guard→token→platform→one-event
// mediation as the git-platform ops above. The branch name (may carry a slash)
// travels as a query param so the route owns only {owner}/{repo}.
app.MapGet("/api/v1/git/{owner}/{repo}/commits", GitEndpoints.GetCommits)
    .RequireAuthorization("EngineServiceOnly")
    .WithName("GitGetCommits");
app.MapGet("/api/v1/git/{owner}/{repo}/file-changes", GitEndpoints.GetFileChanges)
    .RequireAuthorization("EngineServiceOnly")
    .WithName("GitGetFileChanges");
app.MapDelete("/api/v1/git/{owner}/{repo}/branches", GitEndpoints.DeleteBranch)
    .RequireAuthorization("EngineServiceOnly")
    .WithName("GitDeleteBranch");

// ── Epic 38 follow-up #21 — deployment-pipeline release step. Create a GitHub
// release/tag for the shipped version. Same engine-only plane + guard→token→
// platform→one-event mediation as the git-platform ops above.
app.MapPost("/api/v1/git/{owner}/{repo}/releases", GitEndpoints.CreateRelease)
    .RequireAuthorization("EngineServiceOnly")
    .WithName("GitCreateRelease");

// ── Story 38 (Phase 1) — CI (GitHub Actions) step mediation ──
// Same engine-only plane + guard→token→platform→one-event mediation as git.
app.MapPost("/api/v1/ci/{owner}/{repo}/test-runs", CiEndpoints.TriggerTests)
    .RequireAuthorization("EngineServiceOnly")
    .WithName("CiTriggerTests");
app.MapGet("/api/v1/ci/{owner}/{repo}/build-status", CiEndpoints.GetBuildStatus)
    .RequireAuthorization("EngineServiceOnly")
    .WithName("CiGetBuildStatus");

// ── Story 38 (Phase 1) — JIRA step mediation ──
// Not repo-scoped (like Slack): no tenant↔repo guard; the JIRA credential lives in
// Tamma.Api config, resolved inside IJiraIntegrationService.
app.MapGet("/api/v1/jira/tickets/{ticketId}", JiraEndpoints.GetTicket)
    .RequireAuthorization("EngineServiceOnly")
    .WithName("JiraGetTicket");
app.MapPatch("/api/v1/jira/tickets/{ticketId}", JiraEndpoints.UpdateTicket)
    .RequireAuthorization("EngineServiceOnly")
    .WithName("JiraUpdateTicket");

// ── Story 38-2 (Epic 38) — agent-dispatch step mediation (Class C) ──
// Same engine-only plane as /api/v1/git and /api/v1/llm/call: the engine's thin
// phase services post here as the service-scope Tamma:ApiToken; the API holds the
// per-repo GitHub App installation token, authorizes tenant↔repo (reusing 38-1's
// guard), triggers/polls/collects the workflow_dispatch run, and audits it.
// The monitor's poll LOOP stays engine-side — GET .../runs (discover) and
// GET .../runs/{id} (poll) are single-shot status reads it loops over.
// {owner}/{repo} is bound as two segments.
app.MapPost("/api/v1/agent-dispatch/{owner}/{repo}/runs", AgentDispatchEndpoints.TriggerRun)
    .RequireAuthorization("EngineServiceOnly")
    .WithName("DispatchAgentRun");
app.MapGet("/api/v1/agent-dispatch/{owner}/{repo}/runs", AgentDispatchEndpoints.DiscoverRun)
    .RequireAuthorization("EngineServiceOnly")
    .WithName("DiscoverAgentRun");
app.MapGet("/api/v1/agent-dispatch/{owner}/{repo}/runs/{id:long}", AgentDispatchEndpoints.GetRun)
    .RequireAuthorization("EngineServiceOnly")
    .WithName("GetAgentRun");
app.MapGet("/api/v1/agent-dispatch/{owner}/{repo}/runs/{id:long}/results", AgentDispatchEndpoints.CollectResults)
    .RequireAuthorization("EngineServiceOnly")
    .WithName("CollectAgentResults");
app.MapGet("/api/v1/agent-dispatch/{owner}/{repo}/installation", AgentDispatchEndpoints.ResolveInstallation)
    .RequireAuthorization("EngineServiceOnly")
    .WithName("ResolveAgentInstallation");

// ── Story 38-3 (Epic 38) — Slack / notifications step mediation (Class D) ──
// Same engine-only plane as /api/v1/llm/call: the engine's thin SlackActivity
// posts an (already-formatted) notification intent here as the service-scope
// Tamma:ApiToken; the API writes a slack_outbox row (tenant from ITenantContext,
// never the body — no tenant↔repo guard, Slack is not repo-scoped) and returns
// 202. The out-of-band OutboxSlackSender holds the webhook credential, performs
// the transport, and audits to platform_events. NO Slack token ever reaches here.
app.MapPost("/api/v1/notifications/slack", NotificationEndpoints.QueueSlack)
    .RequireAuthorization("EngineServiceOnly")
    .WithName("QueueSlackNotification");

// ── Story 38 (Phase 1) — email step mediation ──
// Same engine-only plane as the Slack notification: the engine posts an
// already-rendered message; the API accepts it into the credentialed, outbox-backed
// IEmailService (which owns transport + EMAIL.* audit). Not repo-scoped; the acting
// tenant scopes the message. NO SMTP/Resend credential ever reaches the engine.
app.MapPost("/api/v1/notifications/email", EmailEndpoints.SendEmail)
    .RequireAuthorization("EngineServiceOnly")
    .WithName("SendEmailNotification");
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
// InitialControlPlane baseline. Drop them before applying migrations — per the
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
        // Migrate() rebuild from the InitialControlPlane baseline + any
        // subsequent migrations.
        //
        // This is destructive. Do NOT adopt this pattern in an environment
        // with user data you care about. It exists to unstick the EF Core
        // + Npgsql history-table race that crashed two consecutive deploys
        // with SqlState=42P07 on the CP migrations-history table.
        //
        // History-table note (unified-tenancy Phase 0): the CP history table
        // was renamed from __TammaMigrationsHistory to __ControlPlaneMigrationsHistory
        // to match the design-time factory. BOTH names are dropped below so
        // servers deployed before this rename are also cleaned up correctly.
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
                    agents, agent_versions, agent_role_selections,
                    tenant_agent_enablements,
                    audit_records, audit_projector_cursor, audit_chain_checkpoints,
                    billing_customers, billing_plan_prices,
                    billing_webhook_events, billing_subscriptions,
                    alert_delivery_attempts, alert_channels, alerts,
                    alert_evaluator_cursor, alert_rules,
                    api_keys, agent_configs, budget_configs, domain_events,
                    email_outbox,
                    github_installation_repos, github_installations,
                    github_webhook_deliveries,
                    platform_webhook_deliveries,
                    junior_developers, kek_rotations,
                    mentorship_events, mentorship_sessions,
                    password_reset_tokens,
                    tenant_plan_assignments,
                    tenant_provider_billing,
                    plan_features, plan_entitlements, plan_prices, plans,
                    margin_policies,
                    provider_model_prices, providers,
                    platform_analytics_hourly,
                    platform_api_key_index,
                    platform_bootstrap,
                    platform_email_outbox, platform_events, platform_queued_tasks,
                    slack_outbox,
                    prompt_overrides,
                    provider_diagnostics, provider_health, queued_tasks, refresh_tokens,
                    sanitization_rules, stories,
                    tenant_databases, tenant_invites, tenant_memberships, tenants,
                    tenant_platform_installations,
                    user_api_keys, user_installations, user_invites, users,
                    workflow_definitions, workflow_instances,
                    knex_migrations, knex_migrations_lock,
                    ""__TammaMigrationsHistory"",
                    ""__ControlPlaneMigrationsHistory""
                CASCADE;");
        }

        dbContext.Database.Migrate();
        Log.Information("Database migrations applied successfully ({Count} total)",
            dbContext.Database.GetAppliedMigrations().Count());

        // ── Startup seeds (insert-missing-only, no-op on re-run) ────────────
        //
        // Plans: the three default tiers referenced by tenants.Plan and by
        // the Phase 2 placement lookup (Plan.PlacementPolicy). PlansSeeder's
        // doc contract says "invoked once at API startup after migrations
        // apply" — this is that call site.
        await Tamma.Data.Seeders.PlansSeeder.SeedAsync(dbContext);

        // Story 34-11 — provider COST price-book. Ports the frozen
        // ProviderPricingService rate sheet into providers + provider_model_prices
        // as v1 seed rows (Source='seed', Status='active'). Insert-missing-only;
        // no-op on re-run and never reverts a Source='admin' (re-priced) row.
        //
        // MUST run before the persona seeder below: Story 32-15's persona
        // seeder validates each persona's (provider, model) against the active
        // price rows this seeder writes (the in-data IsKnown guard).
        await Tamma.Data.Seeders.ProviderPricingSeeder.SeedAsync(dbContext);

        // Story 34-5 — the default GLOBAL margin policy (1.3x = +30%). Gives the
        // cost->price engine a global safety-net policy to resolve to.
        // Insert-missing-only; no-op on re-run and never reverts an admin edit.
        await Tamma.Data.Seeders.MarginPolicySeeder.SeedAsync(dbContext);

        // Story 32-15 — public/system PERSONAS (named cross-role agents:
        // claude/gemini/codegpt, Role=NULL, explicit provider+model, no prompts).
        // Insert-missing-only (keyed by Name); no-op on re-run and never reverts
        // an admin edit. Archives any legacy tamma-<role> public rows (AC11).
        // Emits AGENT.CREATED.SUCCESS / AGENT.ARCHIVED.SUCCESS on real writes.
        await Tamma.Data.Seeders.AgentEntitySeeder.SeedAsync(
            dbContext,
            scope.ServiceProvider.GetRequiredService<Tamma.Data.Repositories.IPlatformEventRepository>(),
            scope.ServiceProvider.GetRequiredService<
                Microsoft.Extensions.Logging.ILoggerFactory>()
                .CreateLogger("Tamma.Data.Seeders.AgentEntitySeeder"));

        // tenant_databases: register the central DB as pool member #1
        // (unified-tenancy Phase 2) so single-user/dev and SaaS share one
        // placement code path. The admin connection string uses the SAME
        // lookup chain as NpgsqlTenantAdminConnection (TenantAdmin →
        // DefaultConnection → ControlPlane) so the seeded row's envelope
        // matches what the lifecycle activities connect with.
        var tenantAdminCs = app.Configuration.GetConnectionString("TenantAdmin");
        if (string.IsNullOrWhiteSpace(tenantAdminCs))
        {
            tenantAdminCs = app.Configuration.GetConnectionString("DefaultConnection")
                ?? app.Configuration.GetConnectionString("ControlPlane");
        }
        if (string.IsNullOrWhiteSpace(tenantAdminCs))
        {
            Log.Warning(
                "TenantDatabasesSeeder skipped — no admin connection string found via "
                + "ConnectionStrings:TenantAdmin / :DefaultConnection / :ControlPlane. "
                + "Tenant placement will have no pool member until one is configured.");
        }
        else
        {
            // Boot-safe: resolving the protector can throw in Production when
            // Cranl:EncryptionKey is unset (TenantSecretProtector's hard-fail
            // guard). That guard is correct for deployments that PROVISION
            // tenants, but it must not crash-loop a deployment that never
            // does — pre-Phase-2 the protector was only
            // resolved lazily. Warn + skip mirrors the missing-admin-CS path:
            // placement simply has no pool member until the key is configured.
            try
            {
                var protector = scope.ServiceProvider
                    .GetRequiredService<Tamma.Data.Abstractions.ITenantConnectionStringProtector>();
                await Tamma.Data.Seeders.TenantDatabasesSeeder.SeedAsync(
                    dbContext, tenantAdminCs, protector);
            }
            catch (Exception ex)
            {
                Log.Warning(ex,
                    "TenantDatabasesSeeder skipped — connection-string protector "
                    + "unavailable (set Cranl:EncryptionKey to enable the tenant "
                    + "placement pool). Tenant provisioning is unavailable until then.");
            }
        }

        Log.Information("Startup seeds applied (plans + tenant_databases bootstrap)");
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Fatal: database migration/startup-seed failed");
        throw;
    }
}

Log.Information("Tamma API starting up...");

app.Run();
return 0;

// Make Program class accessible for testing
public partial class Program { }
