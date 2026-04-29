using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tamma.Api.Services.PlatformTasks;
using Tamma.Api.Services.Provisioning;
using Tamma.Api.Services.Provisioning.Cranl;
using Tamma.Api.Services.Provisioning.V2;
using Tamma.Api.Services.Provisioning.V2.Cranl;
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
                // R2-H11: flow IHostEnvironment so production hard-fails
                // when Cranl:EncryptionKey is unset. The HKDF fallback is
                // strictly behind env.IsDevelopment().
                var env = sp.GetService<Microsoft.Extensions.Hosting.IHostEnvironment>();
                return TenantSecretProtector.FromConfiguration(cfg, env, logger);
            });
            services.TryAddScoped<CranlProvisioningWorkflow>();
#pragma warning disable CS0618 // v1 surface is [Obsolete] (Story 30-3) but still wired
                               // alongside v2 until wave-C migrates callers.
            services.TryAddScoped<ITenantProvisioner, CranlTenantProvisioner>();
#pragma warning restore CS0618
            // Register handler under both ITaskHandler (so the registry sees
            // it) and itself (so direct resolution works in tests).
            services.AddScoped<ITaskHandler, TenantProvisioningTaskHandler>();

            // ── Story 30-3: v2 Cranl provider ──────────────────────────────
            // Wired alongside v1 so both surfaces are available during the
            // wave-B → wave-C transition. v2 takes the same dependencies
            // (DbContext, platform queue repo, secret protector, options)
            // and preserves the platform-queue dispatch pattern v1 relies on.
            services.AddTenantProviderCranl();
        }
        else
        {
#pragma warning disable CS0618 // v1 surface is [Obsolete] (Story 30-3) but still wired
                               // alongside v2 until wave-C migrates callers.
            services.TryAddScoped<ITenantProvisioner, NullTenantProvisioner>();
#pragma warning restore CS0618
        }

        // ── Story 30-1: v2 ITenantInfrastructureProvider registry ───────────
        //
        // The v2 surface coexists with the v1 ITenantProvisioner above. Story
        // 30-3 will retire the v1 path and refactor CranlTenantProvisioner
        // into a v2 provider; until then the v2 registry has only the null
        // seam wired so the dispatch workflow + onboarding UI can take a
        // hard dependency on TenantProviderRegistry without breaking
        // existing single-user deployments. Mode behaviour:
        //   single-user: NullTenantProvider only — provisioning is unused.
        //   SaaS:        NullTenantProvider + (eventually) per-backend
        //                providers from 30-3..30-6, plugged in by their
        //                own AddTenantProvider* extension methods.
        //
        // Note: providers are registered as IEnumerable<ITenantInfrastructureProvider>
        // — the TenantProviderRegistry consumes the collection at startup.
        // We use TryAddEnumerable so a follow-up provider registration in a
        // 30-3..30-6 extension method idempotently adds rather than replaces.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ITenantInfrastructureProvider, NullTenantProvider>());
        services.TryAddSingleton<TenantProviderRegistry>();

        // ── Story 30-2: v2 dispatch workflow + platform-queue handler ──────
        //
        // The dispatcher is the entry-point operators / admin endpoints
        // call. It enqueues onto the platform queue (preserving the v1
        // constraint that provisioning tasks ride the platform queue,
        // not the per-tenant queue, because the tenant DB doesn't exist
        // at provision time). The handler is the IPlatformTaskHandler
        // PlatformTaskWorker dispatches into when it reserves the row.
        // Both need ControlPlaneDbContext + the registry, so Scoped.
        // The workflow itself is also Scoped because it persists
        // tenant-row state via the same DbContext.
        services.TryAddScoped<ProvisionTenantV2Workflow>();
        services.TryAddScoped<ProvisionTenantV2Dispatcher>();
        // Register the handler under both IPlatformTaskHandler (so
        // PlatformTaskHandlerRegistry sees it) and as a concrete type
        // (so direct resolution works in tests). Mirrors the v1
        // TenantProvisioningTaskHandler dual-registration pattern.
        services.AddScoped<IPlatformTaskHandler, ProvisionTenantV2TaskHandler>();

        return services;
    }

    /// <summary>
    /// Story 30-3 — register the v2 Cranl <see cref="ITenantInfrastructureProvider"/>.
    /// Called from <see cref="AddTenantProvisioning"/> when the Cranl
    /// configuration is populated; exposed as a public extension so a
    /// test fixture / future wave-C composition root can wire it
    /// independently of the v1 path.
    ///
    /// <para><b>Lifetime</b>: scoped, mirroring v1's
    /// <see cref="CranlTenantProvisioner"/>. The provider's only
    /// not-thread-safe dependency is the scoped
    /// <see cref="ControlPlaneDbContext"/>; everything else
    /// (<see cref="IPlatformQueuedTaskRepository"/>,
    /// <see cref="TenantSecretProtector"/>, <see cref="CranlOptions"/>) is
    /// already singleton-friendly. This deviates from the 30-1 ADR §4
    /// "platform-scoped singletons" recommendation because that guidance
    /// targets providers that own a real API client + rate limiter; our
    /// wrapper neither makes outbound calls nor holds any per-tenant
    /// state, so it inherits v1's scope cleanly.</para>
    ///
    /// <para><b>Registry plumbing</b>: the v2 registry's constructor
    /// expects <c>IEnumerable&lt;ITenantInfrastructureProvider&gt;</c> with
    /// singleton lifetime (it caches the dictionary at construction time).
    /// We adapt the scoped Cranl provider through
    /// <see cref="ScopedTenantInfrastructureProviderAdapter"/>, which
    /// resolves a fresh scope per method invocation. The adapter is
    /// singleton + thread-safe by construction.</para>
    ///
    /// <para>Idempotent via <c>TryAddEnumerable</c>: a second call adds
    /// nothing because the registration is keyed by implementation type.</para>
    /// </summary>
    public static IServiceCollection AddTenantProviderCranl(this IServiceCollection services)
    {
        services.TryAddScoped<CranlTenantProviderV2>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                ITenantInfrastructureProvider,
                ScopedTenantInfrastructureProviderAdapter<CranlTenantProviderV2>>());
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
