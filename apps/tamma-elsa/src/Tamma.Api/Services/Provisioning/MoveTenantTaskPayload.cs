namespace Tamma.Api.Services.Provisioning;

/// <summary>
/// Unified-tenancy Phase 4 — payload that travels on the platform queue
/// (<see cref="Tamma.Data.Repositories.IPlatformQueuedTaskRepository"/>)
/// for an admin-requested tenant move
/// (<c>POST /api/admin/tenants/{tenantId}/move</c>).
///
/// <para><b>Why platform queue, not per-tenant queue</b>: the move
/// re-points the tenant's database placement — routing work through the
/// tenant's own DB while that DB is being dumped/dropped would be
/// self-defeating. This mirrors the v2 provisioning constraint
/// (<see cref="V2.ProvisionTenantV2TaskPayload"/>): placement-changing
/// tasks ride the platform queue.</para>
/// </summary>
public sealed class MoveTenantTaskPayload
{
    /// <summary>Stable task-type identifier the
    /// <see cref="MoveTenantTaskHandler"/> matches on. Convention is
    /// dot-separated lower-snake-case; matches the
    /// <c>tenant.move.&lt;step&gt;</c> log prefix the move engine uses.</summary>
    public const string TaskType = "tenant.move";

    /// <summary>Tenant whose schema is being moved.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Destination <c>tenant_databases</c> pool row.</summary>
    public Guid TargetDatabaseId { get; set; }
}
