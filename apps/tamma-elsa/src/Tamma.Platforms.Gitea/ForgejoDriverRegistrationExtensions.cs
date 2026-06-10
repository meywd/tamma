using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tamma.Platforms.Abstractions;

namespace Tamma.Platforms.Gitea;

/// <summary>
/// Story 31-5 — host-side wiring. Calling
/// <see cref="AddForgejoPlatformDriver"/> in <c>Program.cs</c> makes
/// Forgejo an available driver for the platform resolver. The resolver
/// (Story 31-2) picks the factory up via keyed-DI when an installation
/// row's <c>platform_kind = "forgejo"</c>.
///
/// <para>This extension is a peer of
/// <see cref="GiteaDriverRegistrationExtensions.AddGiteaPlatformDriver"/>
/// — both can be called in the same host without conflict; they share
/// the OAuth2 token cache (one cache, keyed internally by installation
/// id) and the webhook signature verifier infrastructure but each
/// registers a distinct keyed <see cref="IGitPlatformDriverFactory"/>.</para>
///
/// <para>Registration shape:</para>
/// <code>
/// services.AddGiteaPlatformDriver();
/// services.AddForgejoPlatformDriver();
/// </code>
///
/// <para>The extension is idempotent — calling it twice doesn't
/// double-register or stomp existing keyed singletons. Existing
/// <c>tamma-forgejo</c> named HttpClient registrations are preserved
/// so integration tests can inject custom <c>HttpMessageHandler</c>
/// instances before this call.</para>
/// </summary>
public static class ForgejoDriverRegistrationExtensions
{
    /// <summary>
    /// Register the Forgejo platform driver factory + supporting
    /// services. Safe to call multiple times. Implicitly depends on
    /// the same <see cref="GiteaOAuth2TokenCache"/> singleton the
    /// Gitea extension registers; either extension may register the
    /// cache first.
    /// </summary>
    public static IServiceCollection AddForgejoPlatformDriver(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Idempotent: only add the named HttpClient if not already
        // registered (tests may have plugged a custom handler).
        services.AddHttpClient(ForgejoPlatformDriverFactory.ForgejoHttpClientName);

        // OAuth2 token cache is a process-wide singleton — keyed by
        // installation id internally so it's safe to share with the
        // Gitea factory when both are registered.
        services.TryAddSingleton<GiteaOAuth2TokenCache>();

        // Forgejo-flavoured webhook signature verifier — keyed by
        // PlatformKind.Forgejo so 31-7's receiver can fetch the right
        // header list (Forgejo native first, Gitea legacy fallback).
        services.TryAddKeyedSingleton<GiteaWebhookSignatureVerifier>(
            PlatformKind.Forgejo,
            (_, _) => new GiteaWebhookSignatureVerifier(
                GiteaWebhookSignatureVerifier.ForgejoAndGiteaHeaderNames));

        // The factory itself. Keyed-DI under PlatformKind.Forgejo so
        // PlatformResolver picks it up via
        // GetKeyedService<IGitPlatformDriverFactory>(PlatformKind.Forgejo).
        services.AddKeyedSingleton<IGitPlatformDriverFactory, ForgejoPlatformDriverFactory>(
            PlatformKind.Forgejo);

        return services;
    }
}
