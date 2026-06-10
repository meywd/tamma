using Microsoft.Extensions.DependencyInjection;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Pooling;
using Tamma.Data.Seeders;

namespace Tamma.Api.Tests.Infrastructure;

/// <summary>
/// Unified-tenancy Phase 3 test helper. The stub resolver is gone — every
/// test tenant whose TENANT data is touched (provider_diagnostics,
/// domain_events, agent_configs, provider_health, ...) must be provisioned
/// through the real pipeline (<see cref="ITenantProvisioningService"/>:
/// placement → role → schema → mint → migrate → encrypt → activate) so the
/// LRU resolver can decrypt a connection string for it.
///
/// <para>Two shared mechanics live here so the per-namespace
/// [SetUpFixture]s don't each reinvent them:</para>
///
/// <list type="number">
///   <item><description><see cref="ReseedPoolAsync"/> — Respawner wipes
///   <c>plans</c> and <c>tenant_databases</c> between tests, but placement
///   needs both (plan slug → placement policy; pool row → target cluster).
///   Re-running the insert-missing-only startup seeders restores the
///   canonical rows.</description></item>
///   <item><description><see cref="ProvisionAsync"/> — provisions a tenant
///   and recovers from the fixed-tenant-id case: a previous test in the
///   same assembly run provisioned the same id, Respawner wiped the
///   tenants row (and its envelope), but the physical role + schema
///   survive on the container. ProvisionAsync then throws its
///   "password unrecoverable" guard; the helper drops the leftover role
///   and schema and provisions fresh — which also restores per-test data
///   isolation for fixed ids.</description></item>
/// </list>
/// </summary>
public static class TestTenantProvisioning
{
    /// <summary>
    /// Provision <paramref name="tenantId"/> through the real Phase 2/3
    /// pipeline. The tenants row must already exist (tests create it via
    /// repositories or DbContext). Safe to call for an already-provisioned
    /// tenant (idempotent). Evicts the resolver cache afterwards so a
    /// stale pool / negative-cache entry from an earlier test cannot mask
    /// the fresh envelope.
    /// </summary>
    public static async Task ProvisionAsync(IServiceProvider rootServices, Guid tenantId)
    {
        using var scope = rootServices.CreateScope();
        var sp = scope.ServiceProvider;
        var provisioner = sp.GetRequiredService<ITenantProvisioningService>();
        var resolver = sp.GetRequiredService<ITenantConnectionResolver>();

        try
        {
            await provisioner.ProvisionAsync(tenantId);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("unrecoverable"))
        {
            // Fixed-id reuse across tests (see class docs): close any warm
            // pool logged in as the tenant role, drop the leftover
            // role/schema, then provision fresh.
            await resolver.EvictAsync(tenantId);
            await DropTenantArtifactsAsync(sp, tenantId);
            await provisioner.ProvisionAsync(tenantId);
        }

        await resolver.EvictAsync(tenantId);
    }

    /// <summary>
    /// Restore the startup-seeded rows that Respawner just wiped: the three
    /// default plans and the central <c>tenant_databases</c> pool row.
    /// Both seeders are insert-missing-only, so this is cheap and
    /// deterministic (stable ids).
    /// </summary>
    public static async Task ReseedPoolAsync(
        IServiceProvider rootServices, string adminConnectionString)
    {
        using var scope = rootServices.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<ControlPlaneDbContext>();
        await PlansSeeder.SeedAsync(db);
        var protector = sp.GetRequiredService<ITenantConnectionStringProtector>();
        await TenantDatabasesSeeder.SeedAsync(db, adminConnectionString, protector);
    }

    /// <summary>
    /// Drop the physical artifacts of a previously provisioned tenant:
    /// terminate sessions logged in as the tenant role, revoke/drop
    /// everything it owns (which includes its <c>t_&lt;hex&gt;</c> schema),
    /// then drop the role itself. Runs on the fixture's admin connection —
    /// in tests the pool row points at the same database the admin
    /// connection targets.
    /// </summary>
    private static async Task DropTenantArtifactsAsync(IServiceProvider sp, Guid tenantId)
    {
        var admin = sp.GetRequiredService<ITenantAdminConnection>();
        var roleName = TenantNaming.RoleName(tenantId);
        if (!await admin.RoleExistsAsync(roleName))
            return;

        var quotedRole = TenantNaming.Quote(roleName);
        // Single-quote-safe: role names are t_<32 hex chars>.
        await admin.ExecuteAsync(
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity "
            + $"WHERE usename = '{roleName}';");
        await admin.ExecuteAsync($"DROP OWNED BY {quotedRole};");
        await admin.ExecuteAsync($"DROP ROLE {quotedRole};");
    }
}
