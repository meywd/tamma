using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Pricing;

/// <summary>
/// Story 34-1 — read-only implementation of <see cref="IPlanCatalogService"/>
/// over <see cref="ControlPlaneDbContext"/>. Eager-loads the typed children and
/// projects them into an immutable <see cref="PlanSnapshot"/>. Never mutates;
/// never throws on a missing plan (returns <c>null</c>). Scoped lifetime (the
/// <see cref="ControlPlaneDbContext"/> dependency is scoped).
/// </summary>
public sealed class PlanCatalogService : IPlanCatalogService
{
    private readonly ControlPlaneDbContext _db;
    private readonly ILogger<PlanCatalogService> _logger;

    public PlanCatalogService(ControlPlaneDbContext db, ILogger<PlanCatalogService> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<PlanSnapshot?> GetActiveBySlugAsync(string slug, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        var plan = await BaseQuery()
            .FirstOrDefaultAsync(p => p.Slug == slug && p.Status == "active", ct);

        return plan is null ? null : ToSnapshot(plan);
    }

    public async Task<PlanSnapshot?> GetByIdAsync(Guid planId, CancellationToken ct = default)
    {
        var plan = await BaseQuery().FirstOrDefaultAsync(p => p.Id == planId, ct);
        return plan is null ? null : ToSnapshot(plan);
    }

    public async Task<PlanSnapshot?> GetForTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        // The tenant's plan assignment lives in the Epic 28 PlanId shadow
        // column (a Guid? pointing at a specific plan version row). Read it via
        // a projection so we don't have to materialize the tenant entity.
        var planId = await _db.Tenants
            .IgnoreQueryFilters()
            .Where(t => t.Id == tenantId && t.DeletedAt == null)
            .Select(t => EF.Property<Guid?>(t, "PlanId"))
            .FirstOrDefaultAsync(ct);

        if (planId is null || planId == Guid.Empty)
        {
            _logger.LogWarning(
                "GetForTenantAsync: tenant {TenantId} has no assigned PlanId", tenantId);
            return null;
        }

        var snapshot = await GetByIdAsync(planId.Value, ct);
        _logger.LogDebug(
            "GetForTenantAsync resolved tenant {TenantId} → plan {PlanId} ({Slug} v{Version})",
            tenantId, planId, snapshot?.Slug, snapshot?.Version);
        return snapshot;
    }

    public async Task<IReadOnlyList<PlanSnapshot>> ListActiveAsync(CancellationToken ct = default)
    {
        var plans = await BaseQuery()
            .Where(p => p.Status == "active")
            .OrderBy(p => p.Slug)
            .ToListAsync(ct);

        return plans.Select(ToSnapshot).ToList();
    }

    public async Task<IReadOnlyList<PlanSnapshot>> ListActivePublicAsync(CancellationToken ct = default)
    {
        var plans = await BaseQuery()
            .Where(p => p.Status == "active" && !p.IsCustom)
            .OrderBy(p => p.Slug)
            .ToListAsync(ct);

        _logger.LogDebug("ListActivePublicAsync returned {Count} public plan(s)", plans.Count);
        return plans.Select(ToSnapshot).ToList();
    }

    public async Task<PlanSnapshot?> GetActivePublicBySlugAsync(string slug, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        var plan = await BaseQuery()
            .FirstOrDefaultAsync(p => p.Slug == slug && p.Status == "active" && !p.IsCustom, ct);

        return plan is null ? null : ToSnapshot(plan);
    }

    public async Task<IReadOnlyList<PlanSnapshot>> ListAllForAdminAsync(
        PlanListFilter filter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var query = BaseQuery();

        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            query = query.Where(p => p.Status == filter.Status);
        }

        if (filter.IsCustom is bool isCustom)
        {
            query = query.Where(p => p.IsCustom == isCustom);
        }

        if (filter.TenantId is Guid tenantId)
        {
            // Custom plans encode their bound tenant in the slug
            // (custom-{tenantId:N}-*), so filter on that prefix. Only custom
            // plans carry the binding.
            var prefix = CustomPlanSlug.PrefixFor(tenantId);
            query = query.Where(p => p.IsCustom && p.Slug.StartsWith(prefix));
        }

        var plans = await query
            .OrderBy(p => p.Slug)
            .ThenByDescending(p => p.Version)
            .ToListAsync(ct);

        _logger.LogDebug(
            "ListAllForAdminAsync(status={Status}, isCustom={IsCustom}, tenantId={TenantId}) returned {Count}",
            filter.Status, filter.IsCustom, filter.TenantId, plans.Count);
        return plans.Select(ToSnapshot).ToList();
    }

    public async Task<IReadOnlyList<PlanSnapshot>> GetVersionsBySlugAsync(string slug, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        var plans = await BaseQuery()
            .Where(p => p.Slug == slug)
            .OrderByDescending(p => p.Version)
            .ToListAsync(ct);

        return plans.Select(ToSnapshot).ToList();
    }

    private IQueryable<Plan> BaseQuery() =>
        _db.Plans
            .AsNoTracking()
            .Include(p => p.Features)
            .Include(p => p.Entitlements)
            .Include(p => p.Prices);

    private PlanSnapshot ToSnapshot(Plan plan)
    {
        _logger.LogDebug(
            "Assembling snapshot for {Slug} v{Version}: {Features}f/{Entitlements}e/{Prices}p",
            plan.Slug, plan.Version, plan.Features.Count, plan.Entitlements.Count, plan.Prices.Count);

        return new PlanSnapshot(
            plan.Id,
            plan.Slug,
            plan.DisplayName,
            plan.Version,
            plan.Status,
            plan.IsCustom,
            plan.BillingInterval,
            plan.SupersedesPlanId,
            plan.Features
                .OrderBy(f => f.FeatureKey)
                .Select(f => new PlanFeatureView(f.FeatureKey, f.BoolValue, f.StringValue))
                .ToList(),
            plan.Entitlements
                .OrderBy(e => e.MetricKey)
                .Select(e => new PlanEntitlementView(e.MetricKey, e.LimitValue, e.Period, e.OverageMode))
                .ToList(),
            plan.Prices
                .OrderBy(pr => pr.PricingMode)
                .Select(pr => new PlanPriceView(pr.PricingMode, pr.RecurringUsd, pr.SeatUsd, pr.MeteredComponent))
                .ToList());
    }
}
