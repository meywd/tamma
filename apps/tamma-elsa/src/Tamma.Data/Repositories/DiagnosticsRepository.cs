using Tamma.Data.Abstractions;
using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// EF Core / Npgsql backed implementation of <see cref="IDiagnosticsRepository"/>.
///
/// <para>Story 28-1 PR D: provider_diagnostics has moved off
/// <see cref="ControlPlaneDbContext"/>; every read/write routes through
/// <see cref="ITenantDbContextFactory"/>. Cross-tenant aggregation
/// (<c>tenantId == null</c>) is implemented as a per-tenant fan-out over
/// the registry of active tenants (Decision #2). The fan-out is in-memory
/// — for the diagnostics report's typical &lt; 7-day windows the row
/// count per tenant is small, and the API enforces a non-trivial cap on
/// the bucket count returned (one bucket per <c>BucketSize</c>).</para>
/// </summary>
public class DiagnosticsRepository(
    ITenantDbContextFactory tenantDbFactory,
    ControlPlaneDbContext cp) : IDiagnosticsRepository
{
    /// <inheritdoc />
    public async Task<Guid> InsertAsync(ProviderDiagnostic diagnostic)
    {
        if (diagnostic.CreatedAt == default)
            diagnostic.CreatedAt = DateTime.UtcNow;

        if (diagnostic.TenantId is not Guid tid || tid == Guid.Empty)
        {
            // Story 28-1 PR D: provider_diagnostics is tenant-resident only.
            // Platform-scope telemetry that has no tenant should be emitted
            // as a platform_event instead of a provider_diagnostic row.
            throw new InvalidOperationException(
                "DiagnosticsRepository.InsertAsync requires a non-empty " +
                "TenantId. Story 28-1 PR D moved provider_diagnostics off " +
                "the control plane; platform-scope telemetry must use " +
                "platform_events instead.");
        }

        await using var db = await tenantDbFactory.CreateAsync(tid);
        db.ProviderDiagnostics.Add(diagnostic);
        await db.SaveChangesAsync();
        return diagnostic.Id;
    }

    /// <inheritdoc />
    public Task<(List<ProviderDiagnostic> Items, int Total)> QueryAsync(
        string? providerKey, DateTime? from, DateTime? to, int limit, int offset)
        => QueryAsync(providerKey, from, to, limit, offset,
            tenantId: null, success: null, model: null);

    /// <inheritdoc />
    public async Task<(List<ProviderDiagnostic> Items, int Total)> QueryAsync(
        string? providerKey,
        DateTime? from,
        DateTime? to,
        int limit,
        int offset,
        Guid? tenantId,
        bool? success,
        string? model)
    {
        if (tenantId is Guid tid)
        {
            await using var db = await tenantDbFactory.CreateAsync(tid);
            // Wave A.5 transitional shared-DB phase — explicit tenant predicate.
            return await PageAsync(
                db.ProviderDiagnostics.Where(d => d.TenantId == tid),
                providerKey, from, to, limit, offset, success, model);
        }

        // Cross-tenant fan-out per Decision #2. Aggregate page across
        // tenants in memory and apply Skip/Take after merging.
        var allRows = new List<ProviderDiagnostic>();
        await foreach (var row in StreamAcrossTenantsAsync(providerKey, from, to, success, model))
        {
            allRows.Add(row);
        }
        var total = allRows.Count;
        var items = allRows
            .OrderByDescending(d => d.CreatedAt)
            .Skip(offset)
            .Take(limit)
            .ToList();
        return (items, total);
    }

    /// <inheritdoc />
    public async Task<decimal> GetCostSumAsync(Guid? tenantId, DateTime from, DateTime to)
    {
        var fromUtc = DateTime.SpecifyKind(from, DateTimeKind.Utc);
        var toUtc = DateTime.SpecifyKind(to, DateTimeKind.Utc);

        if (tenantId is Guid tid)
        {
            await using var db = await tenantDbFactory.CreateAsync(tid);
            // Wave A.5 transitional shared-DB phase requires explicit TenantId
            // predicate (see TammaModelConfiguration.ApplyTenantFilter). The
            // per-tenant Npgsql connection becomes the isolation plane once
            // each tenant has its own DB, at which point this predicate is
            // redundant but harmless.
            return await SumCostAsync(
                db.ProviderDiagnostics.Where(d => d.TenantId == tid),
                fromUtc, toUtc);
        }

        decimal total = 0m;
        var tenantIds = await ActiveTenantIdsAsync(default);
        foreach (var t in tenantIds)
        {
            await using var db = await tenantDbFactory.CreateAsync(t);
            total += await SumCostAsync(
                db.ProviderDiagnostics.Where(d => d.TenantId == t),
                fromUtc, toUtc);
        }
        return total;
    }

    /// <inheritdoc />
    public async Task<List<DiagnosticsBucketRow>> AggregateAsync(
        DateTime from,
        DateTime to,
        TimeSpan bucket,
        Guid? tenantId)
    {
        if (bucket <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(bucket), "Bucket must be positive.");

        var fromUtc = DateTime.SpecifyKind(from, DateTimeKind.Utc);
        var toUtc = DateTime.SpecifyKind(to, DateTimeKind.Utc);

        // Per-tenant fan-out collects rows then aggregates client-side.
        // The bucket window is bounded by the API's BucketSize choice so
        // the working set stays small for typical reporting windows.
        var rows = new List<ProviderDiagnostic>();
        if (tenantId is Guid tid)
        {
            await using var db = await tenantDbFactory.CreateAsync(tid);
            rows.AddRange(await db.ProviderDiagnostics
                .Where(d => d.TenantId == tid &&
                            d.CreatedAt >= fromUtc && d.CreatedAt < toUtc)
                .ToListAsync());
        }
        else
        {
            var tenantIds = await ActiveTenantIdsAsync(default);
            foreach (var t in tenantIds)
            {
                await using var db = await tenantDbFactory.CreateAsync(t);
                rows.AddRange(await db.ProviderDiagnostics
                    .Where(d => d.TenantId == t &&
                                d.CreatedAt >= fromUtc && d.CreatedAt < toUtc)
                    .ToListAsync());
            }
        }

        // Bucket the rows by floor((CreatedAt - from) / bucket).
        var bucketTicks = bucket.Ticks;
        var fromTicks = fromUtc.Ticks;
        var grouped = rows
            .GroupBy(d =>
            {
                var idx = (d.CreatedAt.Ticks - fromTicks) / bucketTicks;
                return new DateTime(fromTicks + idx * bucketTicks, DateTimeKind.Utc);
            })
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var groupRows = g.ToList();
                var totalCalls = (long)groupRows.Count;
                var successCount = (long)groupRows.Count(r => r.Success);
                var totalCost = groupRows.Sum(r => r.Cost);
                var avgLatency = groupRows.Count == 0
                    ? 0.0
                    : groupRows.Average(r => (double)r.RequestDurationMs);
                return new DiagnosticsBucketRow(
                    BucketStart: g.Key,
                    TotalCalls: totalCalls,
                    SuccessCount: successCount,
                    TotalCost: totalCost,
                    AvgLatencyMs: avgLatency);
            })
            .ToList();
        return grouped;
    }

    private static async Task<(List<ProviderDiagnostic> Items, int Total)> PageAsync(
        IQueryable<ProviderDiagnostic> baseQuery,
        string? providerKey,
        DateTime? from,
        DateTime? to,
        int limit,
        int offset,
        bool? success,
        string? model)
    {
        var query = baseQuery;
        if (!string.IsNullOrEmpty(providerKey))
            query = query.Where(d => d.ProviderKey == providerKey);
        if (from.HasValue)
        {
            var fromUtc = DateTime.SpecifyKind(from.Value, DateTimeKind.Utc);
            query = query.Where(d => d.CreatedAt >= fromUtc);
        }
        if (to.HasValue)
        {
            var toUtc = DateTime.SpecifyKind(to.Value, DateTimeKind.Utc);
            query = query.Where(d => d.CreatedAt <= toUtc);
        }
        if (success.HasValue)
            query = query.Where(d => d.Success == success.Value);
        if (!string.IsNullOrEmpty(model))
            query = query.Where(d => d.Model == model);

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(d => d.CreatedAt)
            .Skip(offset)
            .Take(limit)
            .ToListAsync();
        return (items, total);
    }

    private async IAsyncEnumerable<ProviderDiagnostic> StreamAcrossTenantsAsync(
        string? providerKey,
        DateTime? from,
        DateTime? to,
        bool? success,
        string? model)
    {
        var tenantIds = await ActiveTenantIdsAsync(default);
        foreach (var tid in tenantIds)
        {
            await using var db = await tenantDbFactory.CreateAsync(tid);
            // Wave A.5 transitional shared-DB phase — explicit tenant predicate.
            var query = db.ProviderDiagnostics.Where(d => d.TenantId == tid);
            if (!string.IsNullOrEmpty(providerKey))
                query = query.Where(d => d.ProviderKey == providerKey);
            if (from.HasValue)
            {
                var fromUtc = DateTime.SpecifyKind(from.Value, DateTimeKind.Utc);
                query = query.Where(d => d.CreatedAt >= fromUtc);
            }
            if (to.HasValue)
            {
                var toUtc = DateTime.SpecifyKind(to.Value, DateTimeKind.Utc);
                query = query.Where(d => d.CreatedAt <= toUtc);
            }
            if (success.HasValue)
                query = query.Where(d => d.Success == success.Value);
            if (!string.IsNullOrEmpty(model))
                query = query.Where(d => d.Model == model);

            foreach (var row in await query.ToListAsync())
            {
                yield return row;
            }
        }
    }

    private static async Task<decimal> SumCostAsync(
        IQueryable<ProviderDiagnostic> baseQuery, DateTime fromUtc, DateTime toUtc)
    {
        var sum = await baseQuery
            .Where(d => d.CreatedAt >= fromUtc && d.CreatedAt < toUtc)
            .Select(d => (decimal?)d.Cost)
            .SumAsync();
        return sum ?? 0m;
    }

    private async Task<List<Guid>> ActiveTenantIdsAsync(CancellationToken ct)
        => await cp.Tenants
            .AsNoTracking()
            .Where(t => t.DeletedAt == null)
            .OrderBy(t => t.Id)
            .Select(t => t.Id)
            .ToListAsync(ct);
}
