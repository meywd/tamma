using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Pooling;

namespace Tamma.Api.Services.Provisioning;

/// <summary>
/// Unified-tenancy Phase 2 — tier-driven placement over the
/// <c>tenant_databases</c> pool. Resolves the tenant's plan
/// (tenants.Plan slug → plans.PlacementPolicy), picks the least-loaded
/// eligible pool row, and stamps the <c>SchemaName</c>/<c>DatabaseId</c>
/// shadow columns plus the row's <c>TenantCount</c> in one SaveChanges.
/// </summary>
public sealed class TenantPlacementService : ITenantPlacementService
{
    private readonly IDbContextFactory<ControlPlaneDbContext> _contextFactory;
    private readonly ILogger<TenantPlacementService> _logger;

    public TenantPlacementService(
        IDbContextFactory<ControlPlaneDbContext> contextFactory,
        ILogger<TenantPlacementService> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    public async Task<TenantPlacement> AssignAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);

        // IgnoreQueryFilters: placement may run for tenants in any
        // lifecycle state (the soft-delete filter must not hide a row
        // mid-provisioning retry).
        var tenant = await db.Tenants.IgnoreQueryFilters()
                .FirstOrDefaultAsync(t => t.Id == tenantId, ct)
            ?? throw new InvalidOperationException(
                $"Tenant '{tenantId}' not found — cannot assign placement.");

        if (tenant.DeletedAt is not null)
            throw new InvalidOperationException(
                $"Tenant '{tenantId}' is soft-deleted — placement is not allowed.");

        // Idempotency: an already-placed tenant returns its existing
        // placement unchanged (SchemaName + DatabaseId shadow columns).
        // Both props or neither: a half-stamped row (corrupt state) is treated as unplaced and re-stamped below.
        var entry = db.Entry(tenant);
        var existingSchema = entry.Property<string?>("SchemaName").CurrentValue;
        var existingDatabaseId = entry.Property<Guid?>("DatabaseId").CurrentValue;
        if (existingSchema is not null && existingDatabaseId is not null)
        {
            return new TenantPlacement(existingDatabaseId.Value, existingSchema);
        }

        // Plan lookup is tenant→system→error: a tenant that resolves to no plans
        // row is a configuration fault — never default silently. Story 34-4:
        // resolve the version-pinned Tenant.PlanId FK (so a CUSTOM-plan tenant,
        // whose legacy Tenant.Plan slug is stale/non-canonical, gets the right
        // PlacementPolicy); only a legacy tenant with a NULL PlanId falls back to
        // the active version of its slug. (Story 34-1 made a slug a multi-version
        // chain, so the slug fallback pins Status == "active" — the partial unique
        // index UX_plans_OneActivePerSlug guarantees exactly one.)
        var plan = await ResolveTenantPlanAsync(db, tenant, asNoTracking: false, ct)
            ?? throw new InvalidOperationException(
                $"No plans row resolved for tenant '{tenantId}' (PlanId shadow FK or legacy slug "
                + $"'{tenant.Plan}') — placement requires plans.PlacementPolicy; seed or repair "
                + "the plans table.");

        var slug = plan.Slug;
        var policy = plan.PlacementPolicy;

        // Candidates: active rows of the plan's placement class, eligible
        // for the tier, with capacity headroom; dedicated rows must be
        // empty (one tenant per dedicated DB). The predicate is shared
        // with TenantMoveService's target validation (Phase 4) via
        // EligibleFor. NOTE: TierEligibility is a text[] column — Npgsql
        // translates Enumerable.Contains to array containment. The STATIC
        // Enumerable.Contains call (not extension-method syntax) is
        // deliberate: newer C# compilers bind `array.Contains(x)` to the
        // MemoryExtensions span overload (ReadOnlySpan op_Implicit), which
        // EF cannot translate — local SDK builds passed while CI's newer
        // SDK failed with 120 query-translation errors.
        var candidates = db.TenantDatabases.Where(EligibleFor(slug, policy));

        var row = await candidates
                .OrderBy(d => d.TenantCount)
                .ThenBy(d => d.CreatedAt)
                .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException(
                $"No eligible tenant_databases pool row for tier '{slug}' (placement policy "
                + $"'{policy}'): need an active row with PlacementClass '{policy}', tier "
                + "eligibility, and capacity headroom"
                + (policy == "dedicated"
                    ? " — dedicated rows host exactly one tenant; the operator adds them via "
                      + "the Phase 4 admin CRUD"
                    : string.Empty)
                + ".");

        var schemaName = TenantNaming.SchemaName(tenantId);
        var now = DateTime.UtcNow;

        entry.Property<string?>("SchemaName").CurrentValue = schemaName;
        entry.Property<Guid?>("DatabaseId").CurrentValue = row.Id;
        tenant.UpdatedAt = now;

        // Concurrency is advisory: two concurrent placements may both read
        // the same headroom and both succeed — TenantCount/TenantCapacity
        // are best-effort load signals, not hard limits. Exact enforcement
        // (locking/atomic claim) is Phase 4's problem alongside admin CRUD.
        row.TenantCount += 1;
        row.UpdatedAt = now;

        // One SaveChanges: the tenant's stamp and the pool row's count move
        // together or not at all.
        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "tenant placement assigned tenantId={TenantId} databaseId={DatabaseId} "
            + "label={Label} schema={Schema} tier={Tier} policy={Policy} tenantCount={TenantCount}",
            tenantId, row.Id, row.Label, schemaName, slug, policy, row.TenantCount);

        return new TenantPlacement(row.Id, schemaName);
    }

    /// <summary>
    /// The ONE eligibility rule for landing a tenant of plan tier
    /// <paramref name="tier"/> (placement policy <paramref name="policy"/>)
    /// on a <c>tenant_databases</c> row: active, matching placement class,
    /// tier-eligible, capacity headroom — and empty when dedicated.
    /// Shared by <see cref="AssignAsync"/> (as an EF query predicate) and
    /// by <c>TenantMoveService</c>'s target validation (compiled,
    /// in-memory) so placement and move can never disagree (Phase 4
    /// reuse/extract).
    /// </summary>
    public static Expression<Func<TenantDatabase, bool>> EligibleFor(string tier, string policy)
    {
        if (policy == "dedicated")
        {
            return d => d.Status == "active"
                && d.PlacementClass == policy
                && Enumerable.Contains(d.TierEligibility, tier)
                && (d.TenantCapacity == null || d.TenantCount < d.TenantCapacity)
                && d.TenantCount == 0;
        }
        return d => d.Status == "active"
            && d.PlacementClass == policy
            && Enumerable.Contains(d.TierEligibility, tier)
            && (d.TenantCapacity == null || d.TenantCount < d.TenantCapacity);
    }

    /// <summary>
    /// Resolve the tenant's effective <see cref="Plan"/> row for placement/move
    /// decisions (Story 34-4). When the version-pinned <c>Tenant.PlanId</c> shadow
    /// FK is set, resolve THAT exact plan version by id — with NO
    /// <c>Status == "active"</c> filter, because the pin may legitimately point at
    /// a <b>custom</b> plan (never <c>active</c> under a canonical slug) or an
    /// intentionally-retained <c>deprecated</c> version. Only when <c>PlanId</c> is
    /// NULL (legacy, pre-34-4 tenants) does it fall back to the active version of
    /// the legacy <c>Tenant.Plan</c> slug. This is what lets placement, move, and
    /// Cranl resolve the correct <c>PlacementPolicy</c>/tier for a custom-plan
    /// tenant whose stale legacy slug is not canonical.
    ///
    /// <para><paramref name="asNoTracking"/> mirrors the caller's prior query
    /// behaviour: placement/move track the plan; the Cranl read path does not.
    /// Reading the shadow <c>PlanId</c> requires <paramref name="tenant"/> to be
    /// tracked by <paramref name="db"/> (all three callers already track it).</para>
    /// </summary>
    public static async Task<Plan?> ResolveTenantPlanAsync(
        ControlPlaneDbContext db, Tenant tenant, bool asNoTracking, CancellationToken ct)
    {
        var pinnedPlanId = db.Entry(tenant).Property<Guid?>("PlanId").CurrentValue;
        IQueryable<Plan> plans = asNoTracking ? db.Plans.AsNoTracking() : db.Plans;

        if (pinnedPlanId is Guid planId)
        {
            return await plans.FirstOrDefaultAsync(p => p.Id == planId, ct);
        }

        return await plans.FirstOrDefaultAsync(
            p => p.Slug == tenant.Plan && p.Status == "active", ct);
    }
}
