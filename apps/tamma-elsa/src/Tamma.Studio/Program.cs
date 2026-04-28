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
using Tamma.Studio.Auth;
using Tamma.Studio.Branding;
using Tamma.Studio.Navigation;
using Tamma.Studio.Services;
using Tamma.Studio.Theming;
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

// Auto-login: bypass ELSA's login page by auto-authenticating with admin credentials.
// nginx already gates access (only admin/owner with valid tamma_session JWT can reach Studio).
var backendUrl = configuration["Backend:Url"] ?? "http://localhost:13000/elsa/api";
builder.Services.AddHttpClient("ElsaAutoLogin", client =>
{
    client.BaseAddress = new Uri(backendUrl.TrimEnd('/') + "/");
});
builder.Services.Replace(new(
    typeof(Elsa.Studio.Login.Contracts.IAuthorizationService),
    typeof(AutoLoginAuthorizationService),
    ServiceLifetime.Scoped));

// Tamma branding — replaces DefaultBrandingProvider.
builder.Services.AddScoped<IBrandingProvider, TammaBrandingProvider>();
builder.Services.AddCore()
    .Replace(new(typeof(IBrandingProvider), typeof(TammaBrandingProvider), ServiceLifetime.Scoped));

// Tamma custom navigation menu items.
builder.Services.AddScoped<IMenuProvider, TammaMenuProvider>();

// Tamma custom UI hint handlers.
builder.Services.AddUIHintHandler<JsonEditorUIHintHandler>();
builder.Services.AddUIHintHandler<ProviderSelectorUIHintHandler>();

// Tamma localStorage-based user preferences.
builder.Services.AddScoped<LocalStorageService>();
builder.Services.AddScoped<UserPreferencesService>();

// Wave C.3 — Tamma admin-alerts HTTP client. Targets the Tamma.Api
// mount (not the Elsa backend) since the alert endpoints live under
// /api/v1/admin/alerts/*, not the Elsa workflow surface. The
// auto-login cookie (nginx + tamma_session JWT) authenticates these
// calls the same way it authenticates the rest of Studio.
var tammaApiUrl = configuration["Tamma:ApiBaseUrl"]
    ?? builder.HostEnvironment.BaseAddress.TrimEnd('/');
builder.Services.AddHttpClient("TammaAdminApi", client =>
{
    client.BaseAddress = new Uri(tammaApiUrl);
});
builder.Services.AddScoped(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    var http = factory.CreateClient("TammaAdminApi");
    return new AlertAdminApiService(http);
});

// Tamma theme service — persists dark mode to localStorage.
// Replaces the default IThemeService registered by AddCore().
builder.Services.AddScoped<TammaThemeService>();
builder.Services.Replace(new(typeof(IThemeService), typeof(TammaThemeService), ServiceLifetime.Scoped));

// Build the application.
var app = builder.Build();

// Run startup tasks.
var startupTaskRunner = app.Services.GetRequiredService<IStartupTaskRunner>();
await startupTaskRunner.RunStartupTasksAsync();

// Run the application.
await app.RunAsync();
