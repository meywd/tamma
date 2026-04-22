using Microsoft.Extensions.DependencyInjection.Extensions;
using Tamma.Api.Services.Provisioning;
using Tamma.Api.Services.Provisioning.Cranl;
using Tamma.Api.Services.TaskQueue;

namespace Tamma.Api.Extensions;

/// <summary>
/// DI wiring for per-tenant provisioning. Mirrors the
/// <see cref="GitHubInstallationServiceCollectionExtensions"/> pattern:
/// the real Cranl-backed implementation is wired only when the
/// <c>Cranl:*</c> options are populated; otherwise the Null seam wins
/// and tenants ride on the central / shared Postgres via RLS.
/// </summary>
public static class ProvisioningServiceCollectionExtensions
{
    /// <summary>
    /// Register <see cref="ITenantProvisioner"/> + collaborators.
    /// <list type="bullet">
    ///   <item><description>When <c>Cranl:ApiKey</c> + <c>Cranl:OrganizationId</c>
    ///     are both set: registers <see cref="CranlTenantProvisioner"/>,
    ///     <see cref="CranlProvisioningWorkflow"/>, the typed
    ///     <see cref="ICranlApiClient"/> (with the auth header bound on
    ///     the pooled HttpClient), the
    ///     <see cref="TenantProvisioningTaskHandler"/>, and the
    ///     <see cref="TenantSecretProtector"/> for at-rest encryption of
    ///     the tenant DATABASE_URL.</description></item>
    ///   <item><description>Otherwise: registers
    ///     <see cref="NullTenantProvisioner"/> only — every tenant gets
    ///     "shared infrastructure" semantics and no Cranl wiring is
    ///     created.</description></item>
    /// </list>
    /// Idempotent via TryAdd*; safe to call from tests + production.
    /// </summary>
    public static IServiceCollection AddTenantProvisioning(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        var options = BuildOptions(configuration);
        services.TryAddSingleton(options);

        // Connection resolver. Stub impl always returns the central
        // connection; the cascade to wire per-tenant routing through
        // every repository is deferred (see
        // ITenantConnectionResolver doc-comment for scope).
        services.TryAddSingleton<ITenantConnectionResolver, CentralOnlyTenantConnectionResolver>();

        if (options.IsConfigured)
        {
            // Real Cranl path — needs the typed HttpClient, the workflow,
            // the handler that the queue dispatches into, and the
            // secret protector for DATABASE_URL encryption.
            services.AddHttpClient<ICranlApiClient, CranlApiClient>((sp, client) =>
            {
                var opts = sp.GetRequiredService<CranlOptions>();
                CranlApiClient.ConfigureClient(client, opts);
            });
            services.TryAddSingleton(sp =>
            {
                var cfg = sp.GetRequiredService<IConfiguration>();
                var logger = sp.GetService<ILogger<TenantSecretProtector>>();
                return TenantSecretProtector.FromConfiguration(cfg, logger);
            });
            services.TryAddScoped<CranlProvisioningWorkflow>();
            services.TryAddScoped<ITenantProvisioner, CranlTenantProvisioner>();
            // Register handler under both ITaskHandler (so the registry sees
            // it) and itself (so direct resolution works in tests).
            services.AddScoped<ITaskHandler, TenantProvisioningTaskHandler>();
        }
        else
        {
            services.TryAddScoped<ITenantProvisioner, NullTenantProvisioner>();
        }

        return services;
    }

    private static CranlOptions BuildOptions(IConfiguration? configuration)
    {
        var options = new CranlOptions();
        if (configuration is null) return options;

        var section = configuration.GetSection("Cranl");
        if (!section.Exists()) return options;

        options.BaseUrl = section["BaseUrl"] ?? options.BaseUrl;
        options.ApiKey = section["ApiKey"] ?? options.ApiKey;
        options.OrganizationId = section["OrganizationId"] ?? options.OrganizationId;
        options.RepositoryId = section["RepositoryId"] ?? options.RepositoryId;
        options.DefaultRegion = section["DefaultRegion"] ?? options.DefaultRegion;
        options.DefaultBuildType = section["DefaultBuildType"] ?? options.DefaultBuildType;
        options.AppBuildPath = section["AppBuildPath"] ?? options.AppBuildPath;
        options.DefaultBranch = section["DefaultBranch"] ?? options.DefaultBranch;
        options.UserAgent = section["UserAgent"] ?? options.UserAgent;

        if (TimeSpan.TryParse(section["RequestTimeout"], out var ts))
            options.RequestTimeout = ts;

        return options;
    }
}
