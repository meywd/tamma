using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Persistence contract for <see cref="ProviderDiagnostic"/> records.
/// </summary>
/// <remarks>
/// All read operations honour the ambient EF global query filter so that
/// tenant isolation is preserved even when <c>tenantId</c> is not passed
/// explicitly. Methods that accept an explicit <c>tenantId</c> narrow the
/// scope further.
/// </remarks>
public interface IDiagnosticsRepository
{
    /// <summary>Persist a diagnostic row. Returns the generated id.</summary>
    Task<Guid> InsertAsync(ProviderDiagnostic diagnostic);

    /// <summary>
    /// Legacy signature retained for backward compatibility (Phase 1 shape).
    /// Filters are applied with AND semantics.
    /// </summary>
    Task<(List<ProviderDiagnostic> Items, int Total)> QueryAsync(
        string? providerKey, DateTime? from, DateTime? to, int limit, int offset);

    /// <summary>
    /// Extended query supporting tenant, success, and model filters in addition
    /// to the legacy parameters. Ordering is <c>CreatedAt DESC</c>.
    /// </summary>
    Task<(List<ProviderDiagnostic> Items, int Total)> QueryAsync(
        string? providerKey,
        DateTime? from,
        DateTime? to,
        int limit,
        int offset,
        Guid? tenantId,
        bool? success,
        string? model);

    /// <summary>
    /// Sum of <c>Cost</c> over the (optional) tenant and time range using a
    /// single DB-side aggregate query.
    /// </summary>
    Task<decimal> GetCostSumAsync(Guid? tenantId, DateTime from, DateTime to);

    /// <summary>
    /// Aggregates diagnostics into time buckets of width <paramref name="bucket"/>
    /// across the half-open range <c>[from, to)</c>. Only buckets that contain
    /// at least one row are returned — callers should reconcile against a
    /// materialised bucket grid if they need zero-fill.
    /// </summary>
    Task<List<DiagnosticsBucketRow>> AggregateAsync(
        DateTime from,
        DateTime to,
        TimeSpan bucket,
        Guid? tenantId);
}

/// <summary>
/// Low-level repository projection for a single aggregated bucket. The
/// <see cref="Tamma.Api.Services.Diagnostics.DiagnosticsService"/> wraps this
/// into the richer <c>DiagnosticsBucket</c> DTO.
/// </summary>
/// <param name="BucketStart">Bucket start timestamp (UTC).</param>
/// <param name="TotalCalls">Rows in the bucket.</param>
/// <param name="SuccessCount">Rows with <c>Success == true</c>.</param>
/// <param name="TotalCost">Sum of <c>Cost</c> for the bucket.</param>
/// <param name="AvgLatencyMs">Average <c>RequestDurationMs</c>.</param>
public sealed record DiagnosticsBucketRow(
    DateTime BucketStart,
    long TotalCalls,
    long SuccessCount,
    decimal TotalCost,
    double AvgLatencyMs);
