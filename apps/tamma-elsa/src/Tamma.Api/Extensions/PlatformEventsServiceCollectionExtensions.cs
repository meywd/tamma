using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Tamma.Api.Services.PlatformEvents;
using Tamma.Api.Services.Provisioning;
using Tamma.Data.Abstractions;
using Tamma.Data.Pooling;

namespace Tamma.Api.Extensions;

/// <summary>
/// Wires the in-process <see cref="IPlatformEventBus"/> into DI. Registered
/// as a singleton so subscribers added at composition root persist for the
/// process lifetime; per-request publishers resolve the same instance.
///
/// <para>Story 28-6 §AC4 — companion to the platform repositories
/// registered by <c>AddTammaData</c>. Idempotent (uses TryAdd) so adjacent
/// stories or test fixtures may call it multiple times without conflict
/// or re-register a test-double bus by registering it before invoking
/// this method.</para>
///
/// <para>Story 28-5 — also registers <see cref="IPlatformEventPublisher"/>
/// (the lower-layer port the tenant-lifecycle activities depend on) and
/// <see cref="ITenantAdminConnection"/> (the admin Postgres seam used by
/// the Create/Delete tenant workflows).</para>
/// </summary>
public static class PlatformEventsServiceCollectionExtensions
{
    public static IServiceCollection AddPlatformEventBus(this IServiceCollection services)
    {
        services.TryAddSingleton<IPlatformEventBus, InMemoryPlatformEventBus>();

        // Story 28-5 — lifecycle activities resolve this lower-layer port
        // (lives in Tamma.Data.Abstractions) instead of taking a hard
        // dependency on Tamma.Api. The adapter forwards to the bus above.
        services.TryAddSingleton<IPlatformEventPublisher, PlatformEventPublisher>();

        // Story 28-5 — admin Postgres seam for create/drop role + database.
        // Singleton + opens a fresh connection per call so each statement
        // runs outside any user transaction (required for DROP DATABASE
        // WITH FORCE per Postgres 17 docs).
        services.TryAddSingleton<ITenantAdminConnection, NpgsqlTenantAdminConnection>();

        // Unified-tenancy Phase 2 — accessor over the tenant_databases
        // registry. The tenant lifecycle runs its cluster-scoped DDL
        // (CREATE ROLE / SCHEMA / GRANT) through the ASSIGNED pool row's
        // admin connection, which this seam decrypts and serves. Singleton
        // (caches decrypted admin strings per pool row) + fresh connection
        // per statement, mirroring NpgsqlTenantAdminConnection.
        services.TryAddSingleton<ITenantDatabasePool, TenantDatabasePool>();

        // Unified-tenancy Phase 2 — tier-driven placement: picks the
        // tenant_databases row for a tenant by plans.PlacementPolicy and
        // stamps tenants.SchemaName/DatabaseId. Stateless (opens a CP
        // context per call via the factory), so singleton is safe.
        services.TryAddSingleton<ITenantPlacementService, TenantPlacementService>();

        // Unified-tenancy Phase 2 — the ONE provisioning step engine
        // (placement → role → schema → conn-string → migrate → encrypt →
        // active) shared by the SaaS CreateTenantWorkflow activities and
        // the single-user EnsurePersonalTenantMiddleware. Stateless over
        // singleton seams, so singleton is safe.
        services.TryAddSingleton<ITenantProvisioningService, TenantProvisioningService>();

        // Unified-tenancy Phase 4 — the schema-move engine (draining →
        // pg_dump → restore → re-point → drop source → active). Stateless
        // over singleton seams (pool, provisioning, resolver, process
        // runner), so singleton is safe. TenantMoveOptions is bound in
        // Program.cs beside TenantBackupOptions.
        services.TryAddSingleton<ITenantMoveService, TenantMoveService>();

        // Story 28-5 — per-tenant migrator runs the InitialTenant migration
        // set against a freshly-created tenant DB. Singleton; opens an
        // ad-hoc TenantDbContext per call.
        services.TryAddSingleton<ITenantDbMigrator, EfTenantDbMigrator>();

        // Story 28-5 — narrow protector port consumed by
        // EncryptAndPersistConnectionStringActivity. Wraps the existing
        // TenantSecretProtector. We resolve it lazily so the adapter
        // works whether or not the Cranl extension already registered
        // the underlying TenantSecretProtector — when missing, we build
        // one from configuration here.
        //
        // Story 28-R2 / PF-S4 — flow IHostEnvironment so the production
        // hard-fail in TenantSecretProtector.FromConfiguration runs
        // when this fallback path is taken. The previous single-arg
        // overload (now deleted) silently HKDF'd from Cranl:ApiKey in
        // production, which was a dispatcher-bypass for the H11 fix
        // when DI ordering registered this extension before the
        // provisioning extension.
        services.TryAddSingleton<ITenantConnectionStringProtector>(sp =>
        {
            var existing = sp.GetService<TenantSecretProtector>();
            if (existing is not null)
                return new TenantSecretProtectorAdapter(existing);

            var cfg = sp.GetRequiredService<IConfiguration>();
            var logger = sp.GetService<ILogger<TenantSecretProtector>>();
            var env = sp.GetService<Microsoft.Extensions.Hosting.IHostEnvironment>();
            var fresh = TenantSecretProtector.FromConfiguration(cfg, env, logger);
            return new TenantSecretProtectorAdapter(fresh);
        });

        return services;
    }
}
