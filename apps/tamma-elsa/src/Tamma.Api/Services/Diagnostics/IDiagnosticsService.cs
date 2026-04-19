using Tamma.Api.Services.Diagnostics.Models;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Diagnostics;

/// <summary>
/// High-level diagnostics service: bridges <see cref="ProviderDiagnostic"/>
/// persistence, in-memory recent-events cache (for the settings UI), time-
/// bucketed reporting, and per-account budget enforcement.
/// </summary>
public interface IDiagnosticsService
{
    /// <summary>
    /// Persist a diagnostic row and refresh the in-memory recent-events
    /// cache for the owning tenant. The id of the persisted row is returned.
    /// </summary>
    Task<Guid> RecordEventAsync(ProviderDiagnostic diag, CancellationToken ct = default);

    /// <summary>Query raw diagnostic rows using a rich filter.</summary>
    Task<(List<ProviderDiagnostic> Items, int Total)> QueryAsync(
        DiagnosticsFilter filter, CancellationToken ct = default);

    /// <summary>
    /// Build a time-bucketed report across the half-open range
    /// <c>[from, to)</c> using the requested <paramref name="bucketSize"/>.
    /// </summary>
    Task<DiagnosticsReport> GetReportAsync(
        Guid? tenantId,
        DateTime from,
        DateTime to,
        BucketSize bucketSize,
        CancellationToken ct = default);

    /// <summary>
    /// Build a per-dimension diagnostics report ("provider", "model",
    /// "agentType") across the half-open range <c>[from, to)</c>. Restored
    /// from TS <c>?groupBy=...</c> support — finding 009.
    /// </summary>
    Task<DimensionReport> GetDimensionReportAsync(
        Guid? tenantId,
        DateTime from,
        DateTime to,
        DimensionGroup groupBy,
        CancellationToken ct = default);

    /// <summary>Compute current-period budget status for the given account (tenant).</summary>
    Task<BudgetStatus> GetBudgetAsync(Guid accountId, CancellationToken ct = default);

    /// <summary>
    /// Return the most-recent cached events (LRU) for the given tenant.
    /// When <paramref name="tenantId"/> is <c>null</c> all cached events are
    /// merged (used for global/admin views).
    /// </summary>
    IReadOnlyList<ProviderDiagnostic> GetRecentEvents(Guid? tenantId, int limit = 50);
}
