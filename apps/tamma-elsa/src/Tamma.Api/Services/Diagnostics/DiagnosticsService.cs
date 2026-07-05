using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Tamma.Api.Services.Diagnostics.Models;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Diagnostics;

/// <summary>
/// Orchestrates diagnostics persistence, recent-events caching (settings UI),
/// time-bucketed aggregation, and per-account budget status computation.
/// </summary>
/// <remarks>
/// <para>
/// The in-memory cache mirrors the behaviour of the deleted TypeScript
/// <c>settings/DiagnosticsService</c>: it keeps the most recent
/// <see cref="MaxCachedEventsPerTenant"/> events per tenant (LRU by insertion
/// order). The cache is populated only when events flow through
/// <see cref="RecordEventAsync"/>; rows inserted directly via the repository
/// do <em>not</em> populate it.
/// </para>
/// <para>
/// Registered as a singleton so the cache survives request scopes. The
/// repository is resolved per-call via the <see cref="IServiceScopeFactory"/>
/// to avoid captive dependencies on scoped EF services.
/// </para>
/// </remarks>
public sealed class DiagnosticsService : IDiagnosticsService
{
    /// <summary>Maximum number of recent events kept per tenant in the LRU cache.</summary>
    public const int MaxCachedEventsPerTenant = 500;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IBudgetConfigProvider _budgetProvider;

    // Per-tenant ring buffer. Guid.Empty key denotes "no tenant" rows.
    private readonly ConcurrentDictionary<Guid, LinkedList<ProviderDiagnostic>> _cache = new();
    private readonly object _cacheLock = new();

    /// <summary>
    /// Construct a new service. All heavy dependencies are resolved per-call
    /// via <paramref name="scopeFactory"/> so the service can safely live as
    /// a singleton.
    /// </summary>
    public DiagnosticsService(IServiceScopeFactory scopeFactory, IBudgetConfigProvider budgetProvider)
    {
        _scopeFactory = scopeFactory;
        _budgetProvider = budgetProvider;
    }

    /// <inheritdoc />
    public async Task<Guid> RecordEventAsync(ProviderDiagnostic diag, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(diag);

        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDiagnosticsRepository>();
        var id = await repo.InsertAsync(diag);

        AddToCache(diag);
        return id;
    }

    /// <inheritdoc />
    public async Task<(List<ProviderDiagnostic> Items, int Total)> QueryAsync(
        DiagnosticsFilter filter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDiagnosticsRepository>();

        return await repo.QueryAsync(
            providerKey: filter.ProviderKey,
            from: filter.From,
            to: filter.To,
            limit: filter.Limit,
            offset: filter.Offset,
            tenantId: filter.TenantId,
            success: filter.Success,
            model: filter.Model);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Story 28-1 PR C audit (#340 MEDIUM finding): unlike
    /// <see cref="GetDimensionReportAsync"/>, this overload does <em>not</em>
    /// reject a <c>null</c> tenantId. The two methods reach
    /// ProviderDiagnostics via different code paths:
    /// <list type="bullet">
    ///   <item><see cref="GetDimensionReportAsync"/> uses an EF
    ///     <see cref="ITenantDbContextFactory"/> route — that path has no
    ///     null-tenant code branch (the factory requires a tenant id), so
    ///     the only honest answer is <see cref="NotSupportedException"/>.</item>
    ///   <item><see cref="GetReportAsync"/> calls
    ///     <see cref="IDiagnosticsRepository.AggregateAsync"/>, a CP-resident
    ///     raw SQL aggregation whose <c>(@p_tenant IS NULL OR ...)</c>
    ///     predicate already supports cross-tenant rollups for billing /
    ///     ops dashboards. Cross-tenant time-series rollup IS a current
    ///     user story (the <c>/api/providers/diagnostics/report</c> endpoint
    ///     consumed by the platform-admin dashboard), so we keep the
    ///     null-tenant code path here.</item>
    /// </list>
    /// PR D moves ProviderDiagnostics off CP entirely; at that point this
    /// method must either grow a per-tenant fan-out via
    /// <see cref="ITenantDbContextFactory"/>, or the cross-tenant rollup
    /// query moves to a denormalized cross-tenant rollup table (the same
    /// design decision Decision #2 defers — when a user story forces it).
    /// Until then we accept the asymmetry rather than silently turn off a
    /// shipped admin feature.
    /// </remarks>
    public async Task<DiagnosticsReport> GetReportAsync(
        Guid? tenantId,
        DateTime from,
        DateTime to,
        BucketSize bucketSize,
        CancellationToken ct = default)
    {
        if (to <= from)
            return new DiagnosticsReport(from, to, bucketSize, Array.Empty<DiagnosticsBucket>(), 0, 0m, 0.0);

        var bucketWidth = BucketWidthFor(bucketSize);

        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDiagnosticsRepository>();
        var rows = await repo.AggregateAsync(from, to, bucketWidth, tenantId);

        var buckets = new List<DiagnosticsBucket>(rows.Count);
        long totalCalls = 0;
        long totalSuccess = 0;
        decimal totalCost = 0m;

        foreach (var row in rows)
        {
            var rate = row.TotalCalls > 0
                ? (double)row.SuccessCount / row.TotalCalls
                : 0.0;

            buckets.Add(new DiagnosticsBucket(
                BucketStart: row.BucketStart,
                TotalCalls: row.TotalCalls,
                SuccessCount: row.SuccessCount,
                SuccessRate: rate,
                TotalCost: row.TotalCost,
                AvgLatencyMs: row.AvgLatencyMs));

            totalCalls += row.TotalCalls;
            totalSuccess += row.SuccessCount;
            totalCost += row.TotalCost;
        }

        var overallRate = totalCalls > 0 ? (double)totalSuccess / totalCalls : 0.0;

        return new DiagnosticsReport(
            From: from,
            To: to,
            BucketSize: bucketSize,
            Buckets: buckets,
            TotalCalls: totalCalls,
            TotalCost: totalCost,
            OverallSuccessRate: overallRate);
    }

    /// <inheritdoc />
    public async Task<DimensionReport> GetDimensionReportAsync(
        Guid? tenantId,
        DateTime from,
        DateTime to,
        DimensionGroup groupBy,
        CancellationToken ct = default)
    {
        if (to <= from)
        {
            return new DimensionReport(from, to, groupBy, Array.Empty<DimensionBucket>());
        }

        // Story 28-1 PR C — Decision #2 (cross-tenant admin queries get a
        // per-call answer). ProviderDiagnostics moves to the per-tenant DB
        // in PR D, so:
        //   • A non-null tenantId routes via ITenantDbContextFactory and
        //     reads the per-tenant slice (works under both transitional
        //     shared-DB and post-PR-D db-per-tenant topologies).
        //   • A null tenantId is "show me every tenant's provider
        //     diagnostics" — a cross-tenant tenant-scoped scan with no
        //     current user story behind it. Defer per Decision #2 with a
        //     loud NotSupportedException so callers (admin dashboards
        //     that aggregate provider stats across the platform) surface
        //     the gap and route a real fan-out implementation when one
        //     ships. Until then, callers MUST scope to a tenant.
        if (!tenantId.HasValue)
        {
            throw new NotSupportedException(
                "Cross-tenant ProviderDiagnostics dimension reports are not " +
                "implemented. ProviderDiagnostics is a tenant-scoped table " +
                "(moves to per-tenant DB in Story 28-1 PR D); pass a tenantId " +
                "to scope the report. See .dev/decisions/story-28-1-design-calls.md " +
                "Decision #2.");
        }

        using var scope = _scopeFactory.CreateScope();
        var tenantDbFactory = scope.ServiceProvider
            .GetRequiredService<ITenantDbContextFactory>();
        await using var db = await tenantDbFactory.CreateAsync(tenantId.Value);

        var fromUtc = DateTime.SpecifyKind(from, DateTimeKind.Utc);
        var toUtc = DateTime.SpecifyKind(to, DateTimeKind.Utc);

        // Defence-in-depth tenant predicate: the factory binds the tenant
        // via the per-tenant Npgsql connection, but the transitional
        // shared-DB phase still mixes rows from every tenant in one
        // physical table — the explicit Where keeps the slice tight.
        var tid = tenantId.Value;
        var query = db.ProviderDiagnostics
            .Where(d => d.TenantId == tid
                        && d.CreatedAt >= fromUtc
                        && d.CreatedAt < toUtc);

        // Group server-side and project to the bucket DTO. EF Core 8 supports
        // GroupBy → Select aggregation translation against Postgres.
        var grouped = groupBy switch
        {
            DimensionGroup.Provider => await query
                .GroupBy(d => d.ProviderKey)
                .Select(g => new
                {
                    Key = g.Key ?? "unknown",
                    TotalCalls = (long)g.Count(),
                    SuccessCount = (long)g.Count(d => d.Success),
                    TotalCost = g.Sum(d => (decimal?)d.Cost) ?? 0m,
                    TotalTokens = (long)(g.Sum(d => (int?)d.TokensUsed) ?? 0),
                    AvgLatency = g.Average(d => (double?)d.RequestDurationMs) ?? 0.0,
                })
                .ToListAsync(ct),
            DimensionGroup.Model => await query
                .GroupBy(d => d.Model)
                .Select(g => new
                {
                    Key = g.Key ?? "unknown",
                    TotalCalls = (long)g.Count(),
                    SuccessCount = (long)g.Count(d => d.Success),
                    TotalCost = g.Sum(d => (decimal?)d.Cost) ?? 0m,
                    TotalTokens = (long)(g.Sum(d => (int?)d.TokensUsed) ?? 0),
                    AvgLatency = g.Average(d => (double?)d.RequestDurationMs) ?? 0.0,
                })
                .ToListAsync(ct),
            DimensionGroup.AgentType => await query
                .GroupBy(d => d.AgentType)
                .Select(g => new
                {
                    Key = g.Key ?? "unknown",
                    TotalCalls = (long)g.Count(),
                    SuccessCount = (long)g.Count(d => d.Success),
                    TotalCost = g.Sum(d => (decimal?)d.Cost) ?? 0m,
                    TotalTokens = (long)(g.Sum(d => (int?)d.TokensUsed) ?? 0),
                    AvgLatency = g.Average(d => (double?)d.RequestDurationMs) ?? 0.0,
                })
                .ToListAsync(ct),
            _ => throw new ArgumentOutOfRangeException(nameof(groupBy), groupBy, null),
        };

        var buckets = grouped
            .OrderByDescending(g => g.TotalCalls)
            .Select(g => new DimensionBucket(
                Key: g.Key,
                TotalCalls: g.TotalCalls,
                SuccessCount: g.SuccessCount,
                ErrorRate: g.TotalCalls > 0
                    ? 1.0 - ((double)g.SuccessCount / g.TotalCalls)
                    : 0.0,
                TotalCost: g.TotalCost,
                TotalTokens: g.TotalTokens,
                AvgLatencyMs: g.AvgLatency))
            .ToList();

        return new DimensionReport(from, to, groupBy, buckets);
    }

    /// <inheritdoc />
    public async Task<ProviderDiagnosticsDeepReport> GetDeepReportAsync(
        Guid? tenantId,
        DateTime from,
        DateTime to,
        string? providerKey,
        CancellationToken ct = default)
    {
        if (to <= from)
        {
            return new ProviderDiagnosticsDeepReport(
                from, to, Array.Empty<ProviderDiagnosticSummary>(), 0, 0, 0, 0m);
        }

        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDiagnosticsRepository>();
        var rows = await repo.FetchDetailAsync(from, to, tenantId, providerKey);

        var summaries = rows
            .GroupBy(r => r.ProviderKey)
            .Select(BuildProviderSummary)
            .OrderByDescending(s => s.TotalCalls)
            .ThenBy(s => s.ProviderKey, StringComparer.Ordinal)
            .ToList();

        long totalCalls = summaries.Sum(s => s.TotalCalls);
        long totalErrors = summaries.Sum(s => s.FailureCount);
        long totalTokens = summaries.Sum(s => s.TotalTokens);
        decimal totalCost = summaries.Sum(s => s.TotalCost);

        return new ProviderDiagnosticsDeepReport(
            From: from,
            To: to,
            Providers: summaries,
            TotalCalls: totalCalls,
            TotalErrors: totalErrors,
            TotalTokens: totalTokens,
            TotalCost: totalCost);
    }

    private static ProviderDiagnosticSummary BuildProviderSummary(
        IGrouping<string, DiagnosticsDetailRow> group)
    {
        var rows = group.ToList();
        var totalCalls = (long)rows.Count;
        var successCount = (long)rows.Count(r => r.Success);
        var failureCount = totalCalls - successCount;
        var successRate = totalCalls > 0 ? (double)successCount / totalCalls : 0.0;
        var errorRate = totalCalls > 0 ? (double)failureCount / totalCalls : 0.0;

        var durations = rows.Select(r => r.RequestDurationMs).OrderBy(v => v).ToList();
        var latency = new LatencyPercentiles(
            P50: Percentile(durations, 50),
            P95: Percentile(durations, 95),
            P99: Percentile(durations, 99),
            Max: durations.Count > 0 ? durations[^1] : 0.0,
            Avg: durations.Count > 0 ? durations.Average() : 0.0);

        var totalTokens = rows.Sum(r => (long)r.TokensUsed);
        var inputTokens = rows.Sum(r => (long)r.InputTokens);
        var outputTokens = rows.Sum(r => (long)r.OutputTokens);
        var totalCost = rows.Sum(r => r.Cost);

        // Error-class breakdown — only failed calls, grouped by ErrorCode.
        var errors = rows
            .Where(r => !r.Success)
            .GroupBy(r => string.IsNullOrWhiteSpace(r.ErrorCode) ? "unknown" : r.ErrorCode!)
            .Select(g => new { Key = g.Key, Count = (long)g.Count() })
            .OrderByDescending(e => e.Count)
            .ThenBy(e => e.Key, StringComparer.Ordinal)
            .Select(e => new ProviderErrorClass(
                ErrorClass: e.Key,
                Count: e.Count,
                Share: failureCount > 0 ? (double)e.Count / failureCount : 0.0))
            .ToList();

        // Per-model usage — availability + cost comparison.
        var models = rows
            .GroupBy(r => string.IsNullOrWhiteSpace(r.Model) ? "unknown" : r.Model!)
            .Select(g =>
            {
                var mRows = g.ToList();
                var mCalls = (long)mRows.Count;
                var mSuccess = (long)mRows.Count(r => r.Success);
                return new ProviderModelUsage(
                    Model: g.Key,
                    TotalCalls: mCalls,
                    SuccessCount: mSuccess,
                    SuccessRate: mCalls > 0 ? (double)mSuccess / mCalls : 0.0,
                    TotalCost: mRows.Sum(r => r.Cost),
                    TotalTokens: mRows.Sum(r => (long)r.TokensUsed),
                    AvgLatencyMs: mRows.Count > 0 ? mRows.Average(r => r.RequestDurationMs) : 0.0);
            })
            .OrderByDescending(m => m.TotalCalls)
            .ThenBy(m => m.Model, StringComparer.Ordinal)
            .ToList();

        return new ProviderDiagnosticSummary(
            ProviderKey: group.Key,
            TotalCalls: totalCalls,
            SuccessCount: successCount,
            FailureCount: failureCount,
            SuccessRate: successRate,
            ErrorRate: errorRate,
            Latency: latency,
            TotalTokens: totalTokens,
            InputTokens: inputTokens,
            OutputTokens: outputTokens,
            TotalCost: totalCost,
            Errors: errors,
            Models: models);
    }

    /// <summary>
    /// Nearest-rank percentile over an ascending-sorted list of values.
    /// Returns 0 for an empty list. <paramref name="p"/> is in [0, 100].
    /// </summary>
    private static double Percentile(IReadOnlyList<double> sortedAscending, double p)
    {
        if (sortedAscending.Count == 0) return 0.0;
        if (sortedAscending.Count == 1) return sortedAscending[0];
        var rank = (int)Math.Ceiling(p / 100.0 * sortedAscending.Count);
        var index = Math.Clamp(rank - 1, 0, sortedAscending.Count - 1);
        return sortedAscending[index];
    }

    /// <inheritdoc />
    public async Task<BudgetStatus> GetBudgetAsync(Guid accountId, CancellationToken ct = default)
    {
        var cfg = _budgetProvider.GetConfig(accountId);

        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDiagnosticsRepository>();
        var spent = await repo.GetCostSumAsync(accountId, cfg.PeriodStart, cfg.PeriodEnd);

        var remaining = Math.Max(0m, cfg.LimitUsd - spent);
        var percentUsed = cfg.LimitUsd > 0
            ? (double)(spent / cfg.LimitUsd) * 100.0
            : 0.0;
        var fraction = cfg.LimitUsd > 0 ? (double)(spent / cfg.LimitUsd) : 0.0;
        var isOver = spent > cfg.LimitUsd && cfg.LimitUsd > 0;
        var shouldAlert = isOver || (cfg.LimitUsd > 0 && fraction >= cfg.AlertThreshold);

        return new BudgetStatus(
            AccountId: accountId,
            PeriodStart: cfg.PeriodStart,
            PeriodEnd: cfg.PeriodEnd,
            Spent: spent,
            Limit: cfg.LimitUsd,
            Remaining: remaining,
            PercentUsed: percentUsed,
            AlertThreshold: cfg.AlertThreshold,
            ShouldAlert: shouldAlert,
            IsOverBudget: isOver);
    }

    /// <inheritdoc />
    public IReadOnlyList<ProviderDiagnostic> GetRecentEvents(Guid? tenantId, int limit = 50)
    {
        if (limit <= 0) return Array.Empty<ProviderDiagnostic>();

        lock (_cacheLock)
        {
            IEnumerable<ProviderDiagnostic> source;
            if (tenantId is null)
            {
                // Merge every bucket. Most recent first across all tenants.
                source = _cache.Values.SelectMany(q => q).OrderByDescending(e => e.CreatedAt);
            }
            else
            {
                var key = tenantId.Value;
                if (!_cache.TryGetValue(key, out var list))
                    return Array.Empty<ProviderDiagnostic>();
                source = list.OrderByDescending(e => e.CreatedAt);
            }

            return source.Take(limit).ToList();
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // Internals
    // ──────────────────────────────────────────────────────────────────────

    private static TimeSpan BucketWidthFor(BucketSize size) => size switch
    {
        BucketSize.FiveMinutes => TimeSpan.FromMinutes(5),
        BucketSize.Hour => TimeSpan.FromHours(1),
        BucketSize.Day => TimeSpan.FromDays(1),
        _ => throw new ArgumentOutOfRangeException(nameof(size), size, "Unsupported bucket size")
    };

    private void AddToCache(ProviderDiagnostic diag)
    {
        var key = diag.TenantId ?? Guid.Empty;
        lock (_cacheLock)
        {
            var list = _cache.GetOrAdd(key, _ => new LinkedList<ProviderDiagnostic>());
            list.AddLast(diag);
            while (list.Count > MaxCachedEventsPerTenant)
            {
                list.RemoveFirst();
            }
        }
    }
}
