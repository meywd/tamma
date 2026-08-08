using Microsoft.Extensions.DependencyInjection.Extensions;
using Tamma.Api.Services.GitHub;
using Tamma.Platforms.GitHub;

namespace Tamma.Api.Extensions;

/// <summary>
/// DI wiring for the GitHub App installation router. Called by
/// <c>Program.cs</c> or test fixtures to register
/// <see cref="IInstallationRouterService"/> and its collaborators.
///
/// <para>Epic 31 P4 M4 — the Octokit-backed <c>IGitHubAppClient</c> and the
/// <c>[Obsolete]</c> libsodium provisioner were DELETED. The App
/// installation-metadata plane now lives in the GitHub DRIVER project
/// (<see cref="RestGitHubAppInstallationReader"/>, plain REST + the
/// hand-rolled App JWT), and install-time <c>TAMMA_API_KEY</c> provisioning
/// rides the driver plane (<see cref="DriverInstallationSecretsPusher"/> →
/// resolved <c>driver.CiSecrets</c>). Degraded mode is unchanged: without
/// <c>GitHub:AppId</c>+<c>GitHub:PrivateKey</c> the Null reader answers
/// <c>github_client_not_configured</c>, and without a resolvable driver the
/// pusher records the same code per repo.</para>
///
/// Repository implementations come from <c>Tamma.Data.DependencyInjection</c>.
/// </summary>
public static class GitHubInstallationServiceCollectionExtensions
{
    public static IServiceCollection AddGitHubInstallationServices(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        services.AddScoped<IInstallationRouterService, InstallationRouterService>();
        services.AddScoped<IInstallationSecretsPusher, DriverInstallationSecretsPusher>();

        // Epic 31 P2 (seam 14) — registry unification: the App callback also
        // upserts a tenant_platform_installations row (App-installation
        // credential REFERENCE, never a PAT), and the startup backfill sweeps
        // installations linked before the bridge existed. The bridge resolves
        // ISecretRevealService optionally, so hosts without the secret cabinet
        // still boot (bridging degrades to a logged no-op).
        services.AddScoped<Tamma.Api.Services.Platforms.IGitHubInstallationBridge>(sp =>
            new Tamma.Api.Services.Platforms.GitHubInstallationBridge(
                sp.GetRequiredService<Tamma.Data.Repositories.ITenantPlatformInstallationRepository>(),
                sp.GetRequiredService<Tamma.Platforms.IPlatformInstallationEventEmitter>(),
                sp.GetRequiredService<IConfiguration>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<
                    Tamma.Api.Services.Platforms.GitHubInstallationBridge>>(),
                sp.GetService<Tamma.Api.Services.Secrets.Reveal.ISecretRevealService>()));
        services.AddHostedService<Tamma.Api.Services.Platforms.GitHubInstallationBridgeBackfillService>();

        if (IsGitHubAppConfigured(configuration))
        {
            // Singleton: the reader caches installation tokens (~55 min) and
            // parses the RSA key once per construction.
            services.TryAddSingleton<IGitHubAppInstallationReader>(sp =>
            {
                var cfg = sp.GetRequiredService<IConfiguration>();
                var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
                return new RestGitHubAppInstallationReader(
                    httpFactory.CreateClient(GitHubPlatformDriverFactory.GitHubHttpClientName),
                    appId: cfg.GetValue<long>("GitHub:AppId"),
                    privateKeyPem: cfg["GitHub:PrivateKey"] ?? string.Empty,
                    baseUrl: cfg["GitHub:ApiBaseUrl"],
                    logger: sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<
                        RestGitHubAppInstallationReader>>());
            });
        }
        else
        {
            services.TryAddSingleton<IGitHubAppInstallationReader, NullGitHubAppInstallationReader>();
        }

        return services;
    }

    private static bool IsGitHubAppConfigured(IConfiguration? configuration)
    {
        if (configuration is null) return false;
        var appId = configuration.GetValue<long?>("GitHub:AppId") ?? 0;
        var privateKey = configuration["GitHub:PrivateKey"];
        return appId > 0 && !string.IsNullOrWhiteSpace(privateKey);
    }
}
