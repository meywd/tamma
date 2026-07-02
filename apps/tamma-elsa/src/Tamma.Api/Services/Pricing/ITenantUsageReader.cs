using Microsoft.Extensions.Logging;
using Tamma.Core.Enums;

namespace Tamma.Api.Services.Pricing;

/// <summary>
/// Story 34-4 — usage seam consumed ONLY to FLAG (never block) over-limit
/// downgrades. On a plan change, <see cref="PlanAssignmentService"/> asks this
/// reader for the tenant's current usage of each metric the new plan limits; a
/// current value above the new limit becomes an <see cref="EntitlementWarning"/>
/// on the result and sets the <c>entitlementWarnings=true</c> tag on
/// <c>TENANT.PLAN.CHANGED</c>.
///
/// <para><b>Scope boundary (34-4 non-goal):</b> this story ships the interface +
/// a null default (<see cref="NullTenantUsageReader"/>) that returns "unknown"
/// for every metric, so no metering/Billing dependency leaks into the
/// assignment transaction. Epic 35 / Epic 20 supply the real reader behind this
/// same seam. A <c>null</c> return degrades to "no warning" — the assignment
/// still succeeds.</para>
/// </summary>
public interface ITenantUsageReader
{
    /// <summary>
    /// The tenant's current usage of <paramref name="metric"/>, or <c>null</c>
    /// when this reader cannot answer it (degrades to no warning). Read-only.
    /// </summary>
    Task<long?> GetCurrentUsageAsync(
        Guid tenantId, EntitlementMetricKey metric, CancellationToken ct = default);
}

/// <summary>
/// Story 34-4 — the shipped default: answers "unknown" (<c>null</c>) for every
/// metric so a downgrade never produces a warning until Epic 35 wires the real
/// metering reader behind the same seam. Deliberately dependency-free and
/// deterministic (no DB, no metering) — keeping usage/Billing out of the
/// assignment path (the whole point of the seam).
/// </summary>
public sealed class NullTenantUsageReader : ITenantUsageReader
{
    private readonly ILogger<NullTenantUsageReader> _logger;

    public NullTenantUsageReader(ILogger<NullTenantUsageReader> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<long?> GetCurrentUsageAsync(
        Guid tenantId, EntitlementMetricKey metric, CancellationToken ct = default)
    {
        _logger.LogDebug(
            "NullTenantUsageReader: no usage signal for tenant {TenantId} metric {Metric} "
            + "(downgrade warnings degrade to none until Epic 35 supplies the reader)",
            tenantId, metric);
        return Task.FromResult<long?>(null);
    }
}
