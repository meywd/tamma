using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Sinks.OpenSearch;
using Tamma.Api.Services;
using Tamma.Core.Interfaces;
using Tamma.Data;
using Tamma.Data.Repositories;

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog with Console + File + OpenSearch sinks
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

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "Tamma Mentorship API",
        Version = "v1",
        Description = "REST API for Tamma Autonomous Mentorship Engine"
    });
});

// Configure HTTP client factory with named clients
builder.Services.AddHttpClient();
builder.Services.AddHttpClient("elsa", client =>
{
    var elsaUrl = builder.Configuration["Elsa:ServerUrl"] ?? "http://localhost:5000";
    client.BaseAddress = new Uri(elsaUrl);

    var elsaApiKey = builder.Configuration["Elsa:ApiKey"];
    if (!string.IsNullOrEmpty(elsaApiKey))
    {
        client.DefaultRequestHeaders.Add("Authorization", $"ApiKey {elsaApiKey}");
    }
});
builder.Services.AddHttpClient("anthropic", client =>
{
    client.BaseAddress = new Uri("https://api.anthropic.com");
    client.DefaultRequestHeaders.Add("anthropic-version", "2024-01-01");

    var apiKey = builder.Configuration["Anthropic:ApiKey"];
    if (!string.IsNullOrEmpty(apiKey))
    {
        client.DefaultRequestHeaders.Add("x-api-key", apiKey);
    }
});
builder.Services.AddHttpClient("github", client =>
{
    var baseUrl = builder.Configuration["GitHub:ApiBaseUrl"] ?? "https://api.github.com";
    client.BaseAddress = new Uri(baseUrl);
    client.DefaultRequestHeaders.Add("User-Agent", "Tamma-ELSA");
    client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");

    var token = builder.Configuration["GitHub:Token"];
    if (!string.IsNullOrEmpty(token))
    {
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
    }
});

// Configure database
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured. Set ConnectionStrings__DefaultConnection environment variable or configure it in appsettings.json.");

builder.Services.AddDbContext<TammaDbContext>(options =>
    options.UseNpgsql(connectionString));

// Register repositories
builder.Services.AddScoped<IMentorshipSessionRepository, MentorshipSessionRepository>();

// Register services
builder.Services.AddScoped<IMentorshipService, MentorshipService>();

// Register focused integration services
builder.Services.AddScoped<ISlackIntegrationService, SlackIntegrationService>();
builder.Services.AddScoped<IGitHubIntegrationService, GitHubIntegrationService>();
builder.Services.AddScoped<IJiraIntegrationService, JiraIntegrationService>();
builder.Services.AddScoped<ICIIntegrationService, CIIntegrationService>();
builder.Services.AddScoped<IEmailIntegrationService, EmailIntegrationService>();
// Composite facade for backward compatibility
builder.Services.AddScoped<IIntegrationService, IntegrationService>();

builder.Services.AddScoped<IAnalyticsService, AnalyticsService>();
builder.Services.AddScoped<IElsaWorkflowService, ElsaWorkflowService>();
builder.Services.AddHostedService<WorkflowSyncService>();

// Configure CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowDashboard", policy =>
    {
        policy.WithOrigins(
                builder.Configuration["Dashboard:Url"] ?? "http://localhost:3001")
            .WithHeaders("Content-Type", "Authorization")
            .WithMethods("GET", "POST", "PUT", "DELETE");
    });
});

// Configure health checks
builder.Services.AddHealthChecks()
    .AddNpgSql(connectionString);

// Configure authentication
var jwtSecret = builder.Configuration["Jwt:Secret"];
if (!string.IsNullOrEmpty(jwtSecret))
{
    builder.Services.AddAuthentication("Bearer")
        .AddJwtBearer("Bearer", options =>
        {
            options.Authority = builder.Configuration["Jwt:Authority"];
            options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
            {
                ValidateAudience = true,
                ValidAudience = builder.Configuration["Jwt:Audience"] ?? "tamma-api",
                ValidateIssuer = true,
                ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "tamma"
            };
        });
    builder.Services.AddAuthorization();
}
else if (builder.Environment.IsDevelopment())
{
    Log.Warning("JWT secret not configured. Using permissive authorization in Development mode. " +
        "Set Jwt:Secret in configuration for production deployments.");
    // In development without JWT, register auth services with a permissive default policy
    // so [Authorize] attributes do not block requests during local development
    builder.Services.AddAuthentication();
    builder.Services.AddAuthorization(options =>
    {
        options.DefaultPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
            .AddRequirements(new Tamma.Api.Infrastructure.AllowAnonymousRequirement())
            .Build();
    });
    builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler,
        Tamma.Api.Infrastructure.AllowAnonymousHandler>();
}
else
{
    throw new InvalidOperationException(
        "JWT secret (Jwt:Secret) must be configured in non-Development environments. " +
        "Authentication cannot be disabled in production.");
}

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Tamma API v1");
        c.RoutePrefix = "swagger";
    });
}

app.UseSerilogRequestLogging();

app.UseHttpsRedirection();
app.UseCors("AllowDashboard");

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health");

// Ensure database is created and migrations applied
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<TammaDbContext>();
    try
    {
        dbContext.Database.Migrate();
        Log.Information("Database migrations applied successfully");
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Error applying migrations, database may already be up to date");
    }
}

Log.Information("Tamma API starting up...");

app.Run();

// Make Program class accessible for testing
public partial class Program { }
