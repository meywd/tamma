using Microsoft.Extensions.DependencyInjection;
using Tamma.Platforms.Abstractions;

namespace Tamma.Platforms.GitHub;

/// <summary>
/// Story 31-3 — host-side wiring for the GitHub driver. Calling
/// <see cref="AddGitHubPlatformDriver"/> in <c>Program.cs</c> makes
/// GitHub an available driver for 31-2's
/// <c>IPlatformResolver</c>. The resolver picks the factory up via
/// keyed-DI when an installation row's
/// <c>platform_kind = "github"</c>.
///
/// <para>Usage:</para>
/// <code>
/// // GitHub Octokit clients are still registered by
/// // GitHubInstallationServiceCollectionExtensions.AddGitHubInstallationServices().
/// services.AddGitHubInstallationServices(configuration);
/// services.AddGitHubPlatformDriver();
/// </code>
///
/// <para>Idempotency: keyed-DI registrations are appended; calling
/// this method twice will produce duplicate factory bindings. Hosts
/// MUST call it exactly once per <see cref="IServiceCollection"/>.
/// This matches the Gitea / GitLab driver registration pattern.</para>
/// </summary>
public static class GitHubDriverRegistrationExtensions
{
    /// <summary>
    /// Register the GitHub platform driver factory under
    /// <see cref="PlatformKind.GitHub"/>. The factory consumes the
    /// existing <c>IGitHubActionsClient</c> (already registered by
    /// <c>Tamma.Activities</c> / <c>Tamma.Api</c>) and composes it
    /// into an <see cref="IGitPlatformDriver"/>.
    /// </summary>
    public static IServiceCollection AddGitHubPlatformDriver(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddKeyedSingleton<IGitPlatformDriverFactory, GitHubPlatformDriverFactory>(
            PlatformKind.GitHub);

        return services;
    }
}
