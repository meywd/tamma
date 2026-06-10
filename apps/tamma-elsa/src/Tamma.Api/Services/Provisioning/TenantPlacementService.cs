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

        // Plan lookup is tenant→system→error: a tenant whose plan slug has
        // no plans row is a configuration fault — never default silently.
        var plan = await db.Plans.FirstOrDefaultAsync(p => p.Slug == tenant.Plan, ct)
            ?? throw new InvalidOperationException(
                $"No plans row for slug '{tenant.Plan}' (tenant '{tenantId}') — placement "
                + "requires plans.PlacementPolicy; seed or repair the plans table.");

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
}
