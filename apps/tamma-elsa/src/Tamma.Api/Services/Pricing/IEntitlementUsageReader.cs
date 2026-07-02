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
    Task<long?> GetCurrentAsync(
        Guid tenantId, EntitlementMetricKey metric, CancellationToken ct = default);
}
