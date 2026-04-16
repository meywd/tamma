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
    /// Register the <see cref="IInstallationRouterService"/> implementation.
    /// Safe to call multiple times (idempotent via TryAddScoped).
    /// </summary>
    public static IServiceCollection AddGitHubInstallationServices(this IServiceCollection services)
    {
        services.AddScoped<IInstallationRouterService, InstallationRouterService>();
        return services;
    }
}
