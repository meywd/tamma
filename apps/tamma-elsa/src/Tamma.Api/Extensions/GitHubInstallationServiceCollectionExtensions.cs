using Microsoft.Extensions.DependencyInjection.Extensions;
using Tamma.Api.Services.GitHub;

namespace Tamma.Api.Extensions;

#pragma warning disable CS0618 // Story 31-8: transitional consumer of obsolete IGitHubSecretsProvisioner.

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
    /// Register the <see cref="IInstallationRouterService"/> plus the GitHub
    /// App client and secrets provisioner.
    ///
    /// <para>When <c>GitHub:AppId</c> and <c>GitHub:PrivateKey</c> are both
    /// present in config, registers the Octokit-backed
    /// <see cref="OctokitGitHubAppClient"/> +
    /// <see cref="LibsodiumGitHubSecretsProvisioner"/>. Otherwise falls back
    /// to the Null impls so the install / rotation flows degrade gracefully
    /// with <c>github_client_not_configured</c> per-repo entries.</para>
    ///
    /// <para>Audit findings: github 007/013/015; engine 005-011, 021.</para>
    /// </summary>
    public static IServiceCollection AddGitHubInstallationServices(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        services.AddScoped<IInstallationRouterService, InstallationRouterService>();

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
            // Bind options from the GitHub:* config section and register the
            // real impls. Factory is captured as a singleton so the RSA key
            // is parsed exactly once per process.
            services.TryAddSingleton<IOctokitClientFactory, DefaultOctokitClientFactory>();
            services.TryAddSingleton(sp =>
            {
                var cfg = sp.GetRequiredService<IConfiguration>();
                return new GitHubAppOptions
                {
                    AppId = cfg.GetValue<long>("GitHub:AppId"),
                    PrivateKeyPem = cfg["GitHub:PrivateKey"] ?? string.Empty,
                    UserAgent = cfg["GitHub:UserAgent"] ?? "Tamma-API"
                };
            });
            services.TryAddSingleton<OctokitGitHubAppClient>();
            services.TryAddSingleton<IGitHubAppClient>(sp => sp.GetRequiredService<OctokitGitHubAppClient>());
            services.TryAddSingleton<IGitHubSecretsProvisioner, LibsodiumGitHubSecretsProvisioner>();
        }
        else
        {
            services.TryAddSingleton<IGitHubAppClient, NullGitHubAppClient>();
            services.TryAddSingleton<IGitHubSecretsProvisioner, NullGitHubSecretsProvisioner>();
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
