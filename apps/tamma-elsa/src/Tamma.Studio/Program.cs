using Elsa.Studio.Branding;
using Elsa.Studio.Contracts;
using Elsa.Studio.Core.BlazorWasm.Extensions;
using Elsa.Studio.Dashboard.Extensions;
using Elsa.Studio.Extensions;
using Elsa.Studio.Login.BlazorWasm.Extensions;
using Elsa.Studio.Login.Extensions;
using Elsa.Studio.Login.HttpMessageHandlers;
using Elsa.Studio.Models;
using Elsa.Studio.Shell;
using Elsa.Studio.Shell.Extensions;
using Elsa.Studio.Workflows.Designer.Extensions;
using Elsa.Studio.Workflows.Extensions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tamma.Studio.Branding;
using Tamma.Studio.Navigation;
using Tamma.Studio.UIHints;

// Build the host.
var builder = WebAssemblyHostBuilder.CreateDefault(args);
var configuration = builder.Configuration;

// Register root components.
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");
builder.RootComponents.RegisterCustomElsaStudioElements();

// Configure backend connection.
var backendApiConfig = new BackendApiConfig
{
    ConfigureBackendOptions = options => configuration.GetSection("Backend").Bind(options),
    ConfigureHttpClientBuilder = options =>
        options.AuthenticationHandler = typeof(AuthenticatingApiHttpMessageHandler),
};

// Register shell services and modules.
builder.Services.AddCore();
builder.Services.AddShell();
builder.Services.AddRemoteBackend(backendApiConfig);
builder.Services.AddLoginModule().UseElsaIdentity();
builder.Services.AddDashboardModule();
builder.Services.AddWorkflowsModule();

// Tamma branding — replaces DefaultBrandingProvider.
builder.Services.AddScoped<IBrandingProvider, TammaBrandingProvider>();
builder.Services.AddCore()
    .Replace(new(typeof(IBrandingProvider), typeof(TammaBrandingProvider), ServiceLifetime.Scoped));

// Tamma custom navigation menu items.
builder.Services.AddScoped<IMenuProvider, TammaMenuProvider>();

// Tamma custom UI hint handlers.
builder.Services.AddUIHintHandler<JsonEditorUIHintHandler>();
builder.Services.AddUIHintHandler<ProviderSelectorUIHintHandler>();

// Build the application.
var app = builder.Build();

// Run startup tasks.
var startupTaskRunner = app.Services.GetRequiredService<IStartupTaskRunner>();
await startupTaskRunner.RunStartupTasksAsync();

// Run the application.
await app.RunAsync();
