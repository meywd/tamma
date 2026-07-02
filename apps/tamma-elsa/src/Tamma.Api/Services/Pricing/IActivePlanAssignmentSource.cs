using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tamma.Data;

namespace Tamma.Api.Services.Pricing;

/// <summary>
/// Story 34-6 — the pinned <c>(PlanId, PlanVersion)</c> a tenant is currently
/// assigned. This is the seam the resolver reads BEFORE snapshotting the
/// catalog (<see cref="IPlanCatalogService.GetByIdAsync"/>), so a
/// <c>null</c> return means "no active assignment" (a hard
/// <c>NO_ASSIGNMENT</c> fail-loud) while a non-null id that fails to snapshot
/// means "catalog unavailable" — two distinct failure reasons.
///
/// <para><b>34-4 dependency (interim wiring):</b> Story 34-4
/// (<c>IPlanAssignmentService.GetActiveAsync</c> + the <c>TenantPlanAssignment</c>
/// table) is NOT yet implemented. Until it lands, the default implementation
/// (<see cref="TenantShadowColumnPlanAssignmentSource"/>) reads the tenant's
/// EXISTING Epic-28 <c>PlanId</c> shadow column — the same pinned column
/// <see cref="PlanCatalogService.GetForTenantAsync"/> already resolves. When
/// 34-4 lands, register an adapter over its <c>GetActiveAsync</c> under this
/// same interface — a one-line DI change in
/// <c>PricingServiceCollectionExtensions</c> — with no resolver code change.
/// This story invents NO new assignment table; it reads what exists.</para>
/// </summary>
public interface IActivePlanAssignmentSource
{
    /// <summary>
    /// The tenant's active pinned plan reference, or <c>null</c> when the
    /// tenant has no assignment (or does not exist). Read-only.
    /// </summary>
    Task<ActivePlanAssignment?> GetActiveAsync(Guid tenantId, CancellationToken ct = default);
}

/// <summary>
/// Story 34-6 — the pinned plan coordinates a tenant is assigned. Immutable
/// value. <see cref="PlanVersion"/> is <c>null</c> in the interim shadow-column
/// path (the version is read from the snapshot); 34-4 will supply it directly.
/// </summary>
public sealed record ActivePlanAssignment(Guid PlanId, int? PlanVersion);

/// <summary>
/// Story 34-6 — interim default: resolves the pinned plan from the Epic-28
/// <c>PlanId</c> shadow column on the tenant row. Scoped (depends on the scoped
/// <see cref="ControlPlaneDbContext"/>). Read-only projection of a single
/// column; never mutates. Replaced by a 34-4 <c>IPlanAssignmentService</c>
/// adapter behind the same seam once that story lands.
/// </summary>
public sealed class TenantShadowColumnPlanAssignmentSource : IActivePlanAssignmentSource
{
    private readonly ControlPlaneDbContext _db;
    private readonly ILogger<TenantShadowColumnPlanAssignmentSource> _logger;

    public TenantShadowColumnPlanAssignmentSource(
        ControlPlaneDbContext db,
        ILogger<TenantShadowColumnPlanAssignmentSource> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ActivePlanAssignment?> GetActiveAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        // Read the pinned PlanId shadow column (Guid? pointing at a specific
        // plan version row). IgnoreQueryFilters + explicit DeletedAt guard so
        // the admin cross-tenant read path is not blocked by the ambient
        // tenant filter — same projection PlanCatalogService.GetForTenantAsync
        // uses.
        var planId = await _db.Tenants
            .IgnoreQueryFilters()
            .Where(t => t.Id == tenantId && t.DeletedAt == null)
            .Select(t => EF.Property<Guid?>(t, "PlanId"))
            .FirstOrDefaultAsync(ct);

        if (planId is null || planId == Guid.Empty)
        {
            _logger.LogDebug(
                "No active plan assignment for tenant {TenantId} (PlanId shadow column empty)",
                tenantId);
            return null;
        }

        // Version comes from the snapshot in the interim path (the shadow
        // column pins the version-row id, not the version number).
        return new ActivePlanAssignment(planId.Value, PlanVersion: null);
    }
}
