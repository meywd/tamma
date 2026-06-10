using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tamma.Platforms.Abstractions;

namespace Tamma.Platforms.Gitea;

/// <summary>
/// Story 31-4 / 31-2 — host-side wiring. Calling
/// <see cref="AddGiteaPlatformDriver"/> in <c>Program.cs</c> makes
/// Gitea an available driver for the platform resolver. The resolver
/// (Story 31-2) picks the factory up via keyed-DI when an installation
/// row's <c>platform_kind = "gitea"</c>.
///
/// <para>Registration shape:</para>
/// <code>
/// services.AddGiteaPlatformDriver();
/// </code>
///
/// <para>The extension is idempotent — calling it twice doesn't double
/// register or stomp on existing keyed singletons. Existing
/// <c>tamma-gitea</c> named HttpClient registrations are preserved so
/// integration tests can inject custom <c>HttpMessageHandler</c>
/// instances before this call.</para>
/// </summary>
public static class GiteaDriverRegistrationExtensions
{
    /// <summary>
    /// Register the Gitea platform driver factory + supporting
    /// services. Safe to call multiple times.
    /// </summary>
    public static IServiceCollection AddGiteaPlatformDriver(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Idempotent: only add the named HttpClient if not already
        // registered (tests may have plugged a custom handler).
        services.AddHttpClient(GiteaPlatformDriverFactory.GiteaHttpClientName);

        // OAuth2 token cache is a process-wide singleton — keyed by
        // installation id internally so it's safe to share.
        services.TryAddSingleton<GiteaOAuth2TokenCache>();

        // Webhook signature verifier — singleton, used by 31-7.
        services.TryAddSingleton<GiteaWebhookSignatureVerifier>(_ =>
            new GiteaWebhookSignatureVerifier());

        // The factory itself. Keyed-DI under PlatformKind.Gitea so
        // PlatformResolver picks it up via
        // GetKeyedService<IGitPlatformDriverFactory>(PlatformKind.Gitea).
        services.AddKeyedSingleton<IGitPlatformDriverFactory, GiteaPlatformDriverFactory>(
            PlatformKind.Gitea);

        return services;
    }
}
