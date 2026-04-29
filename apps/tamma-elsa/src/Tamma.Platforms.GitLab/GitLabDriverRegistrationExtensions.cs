using Microsoft.Extensions.DependencyInjection;
using Tamma.Platforms.Abstractions;

namespace Tamma.Platforms.GitLab;

/// <summary>
/// Story 31-6 §Step 11 — DI extension registering the GitLab factory
/// under the keyed-DI key consumed by 31-2's <c>PlatformResolver</c>.
///
/// <para>Usage:</para>
/// <code>
/// services.AddGitLabPlatform();
/// </code>
///
/// <para>Idempotent — safe to call multiple times. Adds the named
/// <see cref="HttpClient"/> the factory uses internally.</para>
/// </summary>
public static class GitLabDriverRegistrationExtensions
{
    /// <summary>
    /// Register the GitLab driver factory + named HTTP client.
    /// </summary>
    public static IServiceCollection AddGitLabPlatform(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient(GitLabPlatformDriverFactory.HttpClientName, http =>
        {
            // Tamma-specific UA so platform admins can identify our calls
            // in audit logs. Version is intentionally generic — bumped
            // by release tooling rather than per-build.
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Tamma-GitLab-Driver/1.0");
            http.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddKeyedSingleton<IGitPlatformDriverFactory, GitLabPlatformDriverFactory>(
            PlatformKind.GitLab);

        return services;
    }
}
