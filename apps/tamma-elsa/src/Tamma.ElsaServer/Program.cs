using Elsa.Agents;
using Elsa.EntityFrameworkCore.Extensions;
using Elsa.EntityFrameworkCore.Modules.Management;
using Elsa.EntityFrameworkCore.Modules.Runtime;
using Elsa.Extensions;
using Serilog;
using Serilog.Sinks.OpenSearch;
using Tamma.Activities.AI;
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

    // Register all code-first WorkflowBase subclasses from the ElsaServer
    // assembly. HourlyAnalyticsRollupWorkflow (Story 28-10) is picked up
    // by the same assembly sweep as LlmCallWorkflow.
    elsa.AddWorkflowsFrom<LlmCallWorkflow>();
});

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

// Security services (Epic 11 — LLM injection hardening)
builder.Services.AddSingleton<IContentSanitizer, ContentSanitizer>();
builder.Services.AddSingleton<IErrorRedactor, ErrorRedactor>();

// Provider allowlist (Story 11.5 — fail-closed guards)
builder.Services.Configure<ProviderAllowlistOptions>(
    builder.Configuration.GetSection("Security:ProviderAllowlist"));
builder.Services.AddSingleton<ProviderAllowlist>();

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
