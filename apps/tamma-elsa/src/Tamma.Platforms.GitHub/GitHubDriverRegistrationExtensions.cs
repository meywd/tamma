using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Platforms.Abstractions;

namespace Tamma.Platforms.GitHub;

/// <summary>
/// Epic 31 P1 stage 2 — host-side wiring for the GitHub driver.
/// Calling <see cref="AddGitHubPlatformDriver"/> in <c>Program.cs</c>
/// makes GitHub an available driver for 31-2's
/// <c>IPlatformResolver</c>, which picks the factory up via keyed-DI
/// when an installation row's <c>platform_kind = "github"</c>.
///
/// <para>The driver is now self-contained: it makes its own REST /
/// GraphQL calls over the <c>tamma-github</c> named
/// <see cref="HttpClient"/> and mints App-installation tokens
/// internally when the credential is App-shaped. It no longer consumes
/// Tamma.Api / Tamma.Activities GitHub clients — the plan §2
/// "absorb, don't wrap" decision.</para>
///
/// <para>Usage:</para>
/// <code>
/// services.AddGitHubPlatformDriver();
/// </code>
/// </summary>
public static class GitHubDriverRegistrationExtensions
{
    /// <summary>
    /// Register the GitHub platform driver factory under
    /// <see cref="PlatformKind.GitHub"/>. Safe to call once per
    /// <see cref="IServiceCollection"/> (matches the Gitea / GitLab
    /// registration pattern).
    /// </summary>
    public static IServiceCollection AddGitHubPlatformDriver(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Named HttpClient — idempotent; tests may pre-register the
        // same name with a custom primary HttpMessageHandler.
        services.AddHttpClient(GitHubPlatformDriverFactory.GitHubHttpClientName);

        services.AddKeyedSingleton<IGitPlatformDriverFactory>(
            PlatformKind.GitHub,
            (sp, _) => new GitHubPlatformDriverFactory(
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetService<ILoggerFactory>()));

        return services;
    }
}
