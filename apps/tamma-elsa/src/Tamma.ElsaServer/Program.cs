using Elsa.Agents;
using Elsa.EntityFrameworkCore.Extensions;
using Elsa.EntityFrameworkCore.Modules.Management;
using Elsa.EntityFrameworkCore.Modules.Runtime;
using Elsa.Extensions;
using Serilog;
using Tamma.Activities.AI;
using Tamma.ElsaServer.Workflows;

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File("logs/tamma-elsa-.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();

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

    // Register all custom Tamma activities from the Activities assembly
    elsa.AddActivitiesFrom<ClaudeAnalysisActivity>();

    // Register all code-first WorkflowBase subclasses from the ElsaServer assembly
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
