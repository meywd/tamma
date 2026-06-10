using Microsoft.EntityFrameworkCore;
using Tamma.Data;
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

        // Idempotency: an already-placed tenant returns its existing
        // placement unchanged (SchemaName + DatabaseId shadow columns).
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
        // empty (one tenant per dedicated DB). NOTE: TierEligibility is a
        // text[] column — Npgsql translates Contains to array containment.
        var candidates = db.TenantDatabases.Where(d =>
            d.Status == "active"
            && d.PlacementClass == policy
            && d.TierEligibility.Contains(slug)
            && (d.TenantCapacity == null || d.TenantCount < d.TenantCapacity));
        if (policy == "dedicated")
        {
            candidates = candidates.Where(d => d.TenantCount == 0);
        }

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
}
