using Microsoft.Extensions.DependencyInjection.Extensions;
using Tamma.Api.Services.GitHub;

namespace Tamma.Api.Extensions;

/// <summary>
/// DI wiring for the GitHub App installation router. Called by
/// <c>Program.cs</c> or test fixtures to register
/// <see cref="IInstallationRouterService"/> and its collaborators.
///
/// Repository implementations come from <c>Tamma.Data.DependencyInjection</c>.
/// </summary>
public static class GitHubInstallationServiceCollectionExtensions
{
    /// <summary>
    /// Register the <see cref="IInstallationRouterService"/> implementation
    /// plus the Null fallbacks for <see cref="IGitHubAppClient"/> and
    /// <see cref="IGitHubSecretsProvisioner"/> (audit findings 007, 008, 013,
    /// 015). The fallbacks return <c>github_client_not_configured</c> so the
    /// install / rotation flows degrade gracefully until the real Octokit-
    /// backed implementations land. <c>TryAdd*</c> means a wired
    /// implementation registered earlier (e.g., a future
    /// <c>AddGitHubAppHttpClient</c> extension) takes precedence.
    /// </summary>
    public static IServiceCollection AddGitHubInstallationServices(this IServiceCollection services)
    {
        services.AddScoped<IInstallationRouterService, InstallationRouterService>();
        services.TryAddSingleton<IGitHubAppClient, NullGitHubAppClient>();
        services.TryAddSingleton<IGitHubSecretsProvisioner, NullGitHubSecretsProvisioner>();
        return services;
    }
}
