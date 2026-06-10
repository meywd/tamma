using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Activities.TenantLifecycle;

/// <summary>
/// Unified-tenancy Phase 2 (Task 5) — helpers over the <c>tenants</c>
/// row's placement shadow columns (<c>DatabaseId</c> / <c>SchemaName</c>)
/// used by the delete/cleanup paths:
///
/// <list type="bullet">
///   <item><description><see cref="LoadAsync"/> — read-only lookup so the
///     drop-schema / drop-role / backup activities know WHERE the
///     tenant's objects live (the assigned pool row's cluster) or that
///     the tenant predates placement (both props null — nothing
///     schema-scoped to act on).</description></item>
///   <item><description><see cref="ReleaseAsync"/> — releases the pool
///     slot: decrements the pool row's <c>TenantCount</c> (floor 0) and
///     nulls the shadow props on the TRACKED tenant entity. It does NOT
///     call SaveChanges — the caller persists the release atomically
///     with its own row mutation (soft-delete + envelope null), per the
///     plan's "same SaveChanges" requirement.</description></item>
/// </list>
/// </summary>
public static class TenantPlacementShadow
{
    /// <summary>The tenant's placement shadow props. Both null for a
    /// pre-placement tenant (or a missing CP row).</summary>
    public readonly record struct PlacementProps(Guid? DatabaseId, string? SchemaName);

    /// <summary>
    /// Loads the placement shadow props for <paramref name="tenantId"/>.
    /// IgnoreQueryFilters: the delete path operates on rows in any
    /// lifecycle state (including already-soft-deleted retries). A
    /// missing CP row yields <c>(null, null)</c> — the delete steps treat
    /// it like an unplaced tenant and skip.
    /// </summary>
    public static async Task<PlacementProps> LoadAsync(
        IDbContextFactory<ControlPlaneDbContext> contextFactory,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);

        await using var db = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var tenant = await db.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (tenant is null)
            return new PlacementProps(null, null);

        var entry = db.Entry(tenant);
        return new PlacementProps(
            entry.Property<Guid?>("DatabaseId").CurrentValue,
            entry.Property<string?>("SchemaName").CurrentValue);
    }

    /// <summary>
    /// Releases the tenant's placement on the TRACKED
    /// <paramref name="tenant"/> entity: decrements the assigned pool
    /// row's <c>TenantCount</c> (floor 0, matching the increment in
    /// <c>TenantPlacementService.AssignAsync</c>) and nulls the
    /// <c>DatabaseId</c>/<c>SchemaName</c> shadow props. Idempotent: a
    /// tenant with no placement returns false untouched, so a replayed
    /// terminal step never double-decrements. The caller's SaveChanges
    /// persists everything in one transaction.
    /// </summary>
    /// <returns>True when a placement was released.</returns>
    public static async Task<bool> ReleaseAsync(
        ControlPlaneDbContext db,
        Tenant tenant,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(tenant);

        var entry = db.Entry(tenant);
        var databaseIdProp = entry.Property<Guid?>("DatabaseId");
        var schemaNameProp = entry.Property<string?>("SchemaName");
        var databaseId = databaseIdProp.CurrentValue;

        if (databaseId is null && schemaNameProp.CurrentValue is null)
            return false; // never placed, or already released

        if (databaseId is not null)
        {
            var poolRow = await db.TenantDatabases
                .FirstOrDefaultAsync(d => d.Id == databaseId.Value, cancellationToken)
                .ConfigureAwait(false);
            if (poolRow is not null)
            {
                poolRow.TenantCount = Math.Max(0, poolRow.TenantCount - 1);
                poolRow.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                // The FK (Restrict) makes this near-impossible, but a
                // missing registry row must not block the tenant's
                // deletion — release the shadow props regardless.
                logger?.LogWarning(
                    "tenant.lifecycle.placement_release pool_row_missing tenantId={TenantId} databaseId={DatabaseId}",
                    tenant.Id, databaseId);
            }
        }

        databaseIdProp.CurrentValue = null;
        schemaNameProp.CurrentValue = null;

        logger?.LogInformation(
            "tenant.lifecycle.placement_released tenantId={TenantId} databaseId={DatabaseId}",
            tenant.Id, databaseId);
        return true;
    }
}
