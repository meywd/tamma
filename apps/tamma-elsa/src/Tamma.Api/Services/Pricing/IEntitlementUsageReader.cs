using Tamma.Core.Enums;

namespace Tamma.Api.Services.Pricing;

/// <summary>
/// Story 34-6 — current value for gauge-style metrics, so headroom on the read
/// API reflects live counts without the caller wiring three repositories.
/// Returns <c>null</c> for metrics this reader can't answer (metering-only
/// <see cref="EntitlementMetricKey.LlmTokens"/> /
/// <see cref="EntitlementMetricKey.WorkflowRuns"/> until Epic 35 supplies its
/// metering reader; also <see cref="EntitlementMetricKey.RagStorageMb"/> /
/// <see cref="EntitlementMetricKey.BenchmarkRetentionDays"/>).
/// </summary>
public interface IEntitlementUsageReader
{
    /// <summary>
    /// The live current value for a gauge metric, or <c>null</c> when this
    /// reader cannot answer the metric (headroom degrades to
    /// <c>CurrentUsage = null</c> for those).
    /// </summary>
    /// <param name="tenantId">
    /// The resolved tenant id (SaaS: the ambient tenant; single-user: the sole
    /// user's personal tenant). Tenant-scoped counts (<c>Seats</c>, <c>Repos</c>)
    /// key off this in both modes.
    /// </param>
    /// <param name="userId">
    /// The single-user principal's user id (<c>null</c> in SaaS). Needed for
    /// USER-owned counts: an <c>Agent</c> owned in single-user mode carries
    /// <c>OwnerUserId</c> (not <c>OwnerTenantId</c>), so without this the
    /// <c>Agents</c> count is silently 0 in single-user (CLAUDE.md
    /// "design two scoping models, not one").
    /// </param>
    Task<long?> GetCurrentAsync(
        Guid tenantId, Guid? userId, EntitlementMetricKey metric, CancellationToken ct = default);
}
