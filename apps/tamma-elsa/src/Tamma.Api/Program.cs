using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using Serilog.Sinks.OpenSearch;
using Tamma.Api.Auth;
using Tamma.Api.Endpoints;
using Tamma.Api.Extensions;
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

// Database + repositories (via extension method)
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");

builder.Services.AddTammaData(connectionString);

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

// Hardening workstreams — ported from the deleted TS API services.
// Each extension method owns its own service registrations.
builder.Services.AddPromptStoreServices();
builder.Services.AddProviderHealthServices();
builder.Services.AddDiagnosticsServices();
builder.Services.AddSanitizationServices();
builder.Services.AddAgentResolverServices();
builder.Services.AddGitHubInstallationServices();
// Wave 2
builder.Services.AddConventionServices();
builder.Services.AddEmailServices();
builder.Services.AddTaskQueue();
builder.Services.AddProviderSessionServices();
builder.Services.AddSaaSServices();
builder.Services.AddKnowledgeBaseServices(builder.Configuration);

// Controllers (for existing mentorship controller)
builder.Services.AddControllers();

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

// Health checks
builder.Services.AddHealthChecks()
    .AddNpgSql(connectionString);

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
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "tamma",
            ValidateAudience = true,
            ValidAudience = builder.Configuration["Jwt:Audience"] ?? "tamma-api",
            ClockSkew = TimeSpan.Zero
        };
    })
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthHandler>("ApiKey", null);

    builder.Services.AddScoped<IAuthorizationHandler, PermissionHandler>();

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
            "SettingsManage", "WorkflowsView", "WorkflowsManage", "DashboardView", "ApiKeysManage" })
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
app.UseMiddleware<TenantContextMiddleware>();
app.UseMiddleware<EnsurePersonalTenantMiddleware>();

// Existing MVC controllers
app.MapControllers();

// ASP.NET health checks (detailed)
app.MapHealthChecks("/health");

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
auth.MapPost("/switch-org", OrgEndpoints.SwitchOrg).RequireAuthorization("MemberAccess");

app.MapGet("/api/auth/me", AuthEndpoints.GetMe).RequireAuthorization("MemberAccess");
app.MapGet("/api/auth/role-check", AuthEndpoints.RoleCheck).RequireAuthorization("MemberAccess");
app.MapGet("/api/auth/github", AuthEndpoints.GitHubAuth);
app.MapGet("/api/auth/github/callback", AuthEndpoints.GitHubCallback);

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
admin.MapGet("/users/{id}", AdminEndpoints.GetUser);
admin.MapPut("/users/{id}/role", AdminEndpoints.UpdateUserRole).RequireAuthorization("OwnerAccess");
admin.MapDelete("/users/{id}", AdminEndpoints.DeleteUser).RequireAuthorization("OwnerAccess");
admin.MapPost("/users/invite", AdminEndpoints.InviteUser);
admin.MapGet("/users/invites", AdminEndpoints.ListInvites);
admin.MapDelete("/users/invites/{id}", AdminEndpoints.DeleteInvite);
admin.MapPost("/users/{id}/keys", AdminEndpoints.CreateUserApiKey).RequireAuthorization("ApiKeysManage");
admin.MapGet("/users/{id}/keys", AdminEndpoints.ListUserApiKeys).RequireAuthorization("ApiKeysManage");
admin.MapDelete("/users/{id}/keys/{keyId}", AdminEndpoints.DeleteUserApiKey).RequireAuthorization("ApiKeysManage");

// ── Orgs / Tenants ──
var orgs = app.MapGroup("/api/v1/orgs").RequireAuthorization("MemberAccess");
orgs.MapPost("/", OrgEndpoints.CreateOrg);
orgs.MapGet("/{tenantId}", OrgEndpoints.GetOrg);
orgs.MapPut("/{tenantId}/settings", OrgEndpoints.UpdateOrgSettings).RequireAuthorization("SettingsManage");
orgs.MapGet("/{tenantId}/members", OrgEndpoints.ListMembers);
orgs.MapPut("/{tenantId}/members/{userId}/role", OrgEndpoints.UpdateMemberRole).RequireAuthorization("AdminAccess");
orgs.MapDelete("/{tenantId}/members/{userId}", OrgEndpoints.RemoveMember).RequireAuthorization("AdminAccess");
orgs.MapPost("/{tenantId}/invites", OrgEndpoints.CreateInvite).RequireAuthorization("AdminAccess");
orgs.MapGet("/{tenantId}/invites", OrgEndpoints.ListInvites).RequireAuthorization("AdminAccess");
orgs.MapDelete("/{tenantId}/invites/{inviteId}", OrgEndpoints.DeleteInvite).RequireAuthorization("AdminAccess");
orgs.MapPost("/invites/accept", OrgEndpoints.AcceptInvite);
orgs.MapPost("/{tenantId}/transfer-ownership", OrgEndpoints.TransferOwnership).RequireAuthorization("OwnerAccess");
orgs.MapDelete("/{tenantId}", OrgEndpoints.DeleteOrg).RequireAuthorization("OwnerAccess");

app.MapGet("/api/v1/tenants", OrgEndpoints.ListTenants).RequireAuthorization("MemberAccess");

// ── Agents Config ──
var agents = app.MapGroup("/api/v1/agents").RequireAuthorization("SettingsView");
agents.MapGet("/config", AgentEndpoints.GetConfig);
agents.MapPut("/config", AgentEndpoints.UpdateConfig).RequireAuthorization("SettingsManage");
agents.MapPost("/config/validate", AgentEndpoints.ValidateConfig);
agents.MapGet("/{role}/resolve", AgentEndpoints.ResolveAgent);
agents.MapPost("/resolve-for-phase", AgentEndpoints.ResolveForPhase);

// ── Prompts ──
var prompts = app.MapGroup("/api/prompts").RequireAuthorization("SettingsView");
prompts.MapGet("/", PromptEndpoints.ListAll);
prompts.MapGet("/system", PromptEndpoints.ListSystemDefaults);
prompts.MapGet("/system/{role}/{action}", PromptEndpoints.GetSystemDefault);
prompts.MapGet("/{role}/{action}", PromptEndpoints.GetPrompt);
prompts.MapPut("/{role}/{action}", PromptEndpoints.UpsertPrompt).RequireAuthorization("SettingsManage");
prompts.MapDelete("/{role}/{action}", PromptEndpoints.DeletePrompt).RequireAuthorization("SettingsManage");
prompts.MapPut("/system/{role}/{action}", PromptEndpoints.UpsertSystemPrompt).RequireAuthorization("SettingsManage");
prompts.MapDelete("/system/{role}/{action}", PromptEndpoints.DeleteSystemPrompt).RequireAuthorization("SettingsManage");
prompts.MapPost("/{role}/{action}/render", PromptEndpoints.RenderPrompt);

// ── Convention Templates (no auth) ──
app.MapGet("/api/convention-templates", ConventionEndpoints.ListAll);
app.MapGet("/api/convention-templates/{key}", ConventionEndpoints.GetByKey);

// ── Settings / Config ──
var config = app.MapGroup("/api/config").RequireAuthorization("SettingsView");
config.MapGet("/agents", SettingsEndpoints.GetAgentsConfig);
config.MapPut("/agents", SettingsEndpoints.UpdateAgentsConfig).RequireAuthorization("SettingsManage");
config.MapGet("/security", SettingsEndpoints.GetSecurityConfig);
config.MapPut("/security", SettingsEndpoints.UpdateSecurityConfig).RequireAuthorization("SettingsManage");
config.MapPost("/sanitize", SettingsEndpoints.Sanitize).RequireAuthorization("SettingsManage");
config.MapGet("/sanitize/rules", SettingsEndpoints.GetSanitizationRules);
config.MapPut("/sanitize/rules", SettingsEndpoints.UpdateSanitizationRules).RequireAuthorization("SettingsManage");
config.MapGet("/prompts", SettingsEndpoints.GetPromptsConfig);
config.MapPut("/prompts/{role}", SettingsEndpoints.UpdatePromptsConfig).RequireAuthorization("SettingsManage");
config.MapGet("/providers", SettingsEndpoints.GetProvidersConfig);
config.MapPut("/providers", SettingsEndpoints.UpdateProvidersConfig).RequireAuthorization("SettingsManage");

// ── Providers ──
var providers = app.MapGroup("/api/providers").RequireAuthorization("SettingsView");
providers.MapGet("/health", ProviderEndpoints.GetHealthSummary);
providers.MapGet("/health/providers", ProviderEndpoints.ListProviderHealth);
providers.MapGet("/health/providers/{key}", ProviderEndpoints.GetProviderHealth);
providers.MapPost("/health/providers/{key}/failure", ProviderEndpoints.RecordFailure).RequireAuthorization("SettingsManage");
providers.MapPost("/health/providers/{key}/success", ProviderEndpoints.RecordSuccess).RequireAuthorization("SettingsManage");
providers.MapPost("/health/providers/{key}/reset", ProviderEndpoints.ResetProvider).RequireAuthorization("SettingsManage");
providers.MapPost("/chain/resolve", ProviderEndpoints.ResolveChain);
providers.MapGet("/diagnostics", ProviderEndpoints.GetDiagnostics);
providers.MapGet("/diagnostics/query", ProviderEndpoints.QueryDiagnostics);
providers.MapGet("/diagnostics/report", ProviderEndpoints.GetReport);
providers.MapGet("/diagnostics/budget/{accountId}", ProviderEndpoints.GetBudget);
providers.MapPost("/diagnostics", ProviderEndpoints.IngestDiagnostic).RequireAuthorization("SettingsManage");
providers.MapPost("/providers/create", ProviderEndpoints.CreateProvider).RequireAuthorization("SettingsManage");
providers.MapPost("/providers/{handle}/execute", ProviderEndpoints.ExecuteProvider).RequireAuthorization("SettingsManage");
providers.MapDelete("/providers/{handle}", ProviderEndpoints.DeleteProvider).RequireAuthorization("SettingsManage");
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
engine.MapPost("/agent-available", EngineEndpoints.AgentAvailable).RequireAuthorization("WorkflowsManage");

// ── Workflows ──
var workflows = app.MapGroup("/api/workflows").RequireAuthorization("WorkflowsView");
workflows.MapPost("/definitions", WorkflowEndpoints.CreateDefinition).RequireAuthorization("WorkflowsManage");
workflows.MapGet("/definitions", WorkflowEndpoints.ListDefinitions);
workflows.MapPost("/instances", WorkflowEndpoints.CreateInstance).RequireAuthorization("WorkflowsManage");
workflows.MapPut("/instances/{id}", WorkflowEndpoints.UpdateInstance).RequireAuthorization("WorkflowsManage");
workflows.MapGet("/instances", WorkflowEndpoints.ListInstances);
workflows.MapPost("/instances/{id}/cancel", WorkflowEndpoints.CancelInstance).RequireAuthorization("WorkflowsManage");
workflows.MapDelete("/instances/{id}", WorkflowEndpoints.DeleteInstance).RequireAuthorization("WorkflowsManage");
workflows.MapGet("/instances/{id}/events", WorkflowEndpoints.GetInstanceEvents);

// ── GitHub App (no auth, webhook signature verification) ──
var github = app.MapGroup("/api/github");
github.MapGet("/callback", GitHubEndpoints.Callback);
github.MapPost("/webhooks", GitHubEndpoints.Webhooks);

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
                    api_keys, agent_configs, domain_events,
                    github_installation_repos, github_installations,
                    junior_developers, mentorship_events, mentorship_sessions,
                    password_reset_tokens, prompt_overrides,
                    provider_diagnostics, provider_health, refresh_tokens,
                    sanitization_rules, stories, tenant_memberships, tenants,
                    user_invites, users, workflow_definitions, workflow_instances,
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
