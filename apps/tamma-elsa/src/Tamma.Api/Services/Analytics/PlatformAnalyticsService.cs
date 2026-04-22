using Microsoft.EntityFrameworkCore;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Analytics;

/// <summary>
/// Story 28-10 — on-demand implementation of
/// <see cref="IPlatformAnalyticsService"/>. Aggregates from the existing
/// <see cref="ControlPlaneDbContext"/> (tenants + platform_events) and the
/// legacy <see cref="TammaAppDbContext"/> (workflow_instances +
/// domain_events) until the full <c>platform_analytics_hourly</c> fact
/// table + hourly rollup workflow from Story 28-10 §AC1–AC4 lands.
///
/// <para>Every query is bounded by a UTC window derived from
/// <see cref="DateTime.UtcNow"/> at the call site so results are
/// deterministic within a single invocation. Nothing here is cached —
/// each call re-queries. The endpoint layer will add a short HTTP cache
/// header (Story 28-11) once rollup rates show caching actually helps.</para>
///
/// <para>Hard tenant-isolation guard: every query runs with
/// <c>IgnoreQueryFilters()</c> or touches the <c>platform_events</c> /
/// <c>tenants</c> tables that are not tenant-filtered by default.
/// Callers MUST gate the endpoints behind <c>OwnerAccess</c> so the
/// rollup never leaks cross-tenant volume to a regular member.</para>
/// </summary>
public sealed class PlatformAnalyticsService : IPlatformAnalyticsService
{
    private readonly ControlPlaneDbContext _cp;
    private readonly TammaAppDbContext _app;
    private readonly TimeProvider _clock;

    // Event-type prefixes used by the rollup. Kept as constants so they
    // track exactly one source-of-truth copy; each is the same prefix
    // Story 28-10 §AC2/§AC3 lists.
    internal const string AgentDispatchPrefix = "AGENT.DISPATCH.";
    internal const string AgentDispatchSuccess = "AGENT.DISPATCH.SUCCESS";
    internal const string AgentDispatchFailed = "AGENT.DISPATCH.FAILED";
    internal const string LlmCallSuccess = "LLM.CALL.SUCCESS";

    // Workflow instance storage states — match Tamma.Data.Entities.WorkflowInstance.Status.
    internal const string WfStatusCompleted = "completed";
    internal const string WfStatusFailed = "failed";
    internal const string WfStatusRunning = "running";
    internal const string WfStatusPending = "pending";

    // Tenant status shadow values — match Story 28-5 lifecycle activities.
    internal const string TenantStatusActive = "active";
    internal const string TenantStatusProvisioning = "provisioning";
    internal const string TenantStatusDeleting = "deleting";
    internal const string TenantStatusDeleted = "deleted";

    public PlatformAnalyticsService(
        ControlPlaneDbContext cp,
        TammaAppDbContext app,
        TimeProvider? clock = null)
    {
        _cp = cp ?? throw new ArgumentNullException(nameof(cp));
        _app = app ?? throw new ArgumentNullException(nameof(app));
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<PlatformAnalyticsSummary> GetSummaryAsync(CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var t24h = now.AddHours(-24);
        var t7d = now.AddDays(-7);
        var t30d = now.AddDays(-30);

        var tenantCounts = await GetTenantCountsAsync(ct).ConfigureAwait(false);
        var workflows = await GetWorkflowCountsAsync(t24h, t7d, t30d, ct).ConfigureAwait(false);
        var agents = await GetAgentDispatchCountsAsync(t24h, t7d, t30d, ct).ConfigureAwait(false);
        var costs = await GetCostAggregatesAsync(t24h, t7d, t30d, ct).ConfigureAwait(false);

        return new PlatformAnalyticsSummary(tenantCounts, workflows, agents, costs, now);
    }

    public async Task<IReadOnlyList<TenantAnalyticsRow>> GetTopTenantsAsync(
        int limit = 25,
        CancellationToken ct = default)
    {
        if (limit <= 0) limit = 1;
        if (limit > 200) limit = 200;

        var now = _clock.GetUtcNow().UtcDateTime;
        var since = now.AddDays(-30);

        // Aggregate workflow counts per tenant over the 30-day window.
        var wfStats = await _app.WorkflowInstances
            .IgnoreQueryFilters()
            .Where(w => w.CreatedAt >= since && w.TenantId != null)
            .GroupBy(w => w.TenantId!.Value)
            .Select(g => new
            {
                TenantId = g.Key,
                Total = g.Count(),
                Failed = g.Count(w => w.Status == WfStatusFailed),
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (wfStats.Count == 0)
            return Array.Empty<TenantAnalyticsRow>();

        var tenantIds = wfStats.Select(s => s.TenantId).ToHashSet();

        // Pull tenant directory rows for the active set.
        var tenantRows = await _cp.Tenants
            .IgnoreQueryFilters()
            .Where(t => tenantIds.Contains(t.Id))
            .Select(t => new
            {
                t.Id,
                t.Slug,
                t.Name,
                t.Plan,
                Status = EF.Property<string?>(t, "Status"),
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var tenantById = tenantRows.ToDictionary(t => t.Id);

        // Cost per tenant from domain_events LLM.CALL.SUCCESS in the window.
        var costByTenant = await SumLlmCostsPerTenantAsync(since, ct).ConfigureAwait(false);

        var rows = new List<TenantAnalyticsRow>(wfStats.Count);
        foreach (var s in wfStats)
        {
            if (!tenantById.TryGetValue(s.TenantId, out var t))
            {
                // Tenant row missing (e.g. hard-deleted). Fall back to id+slug
                // placeholders so the row still renders and ops can investigate.
                rows.Add(new TenantAnalyticsRow(
                    s.TenantId,
                    Slug: s.TenantId.ToString("N"),
                    Name: "(unknown)",
                    Plan: "unknown",
                    Status: null,
                    WorkflowsLast30d: s.Total,
                    WorkflowsFailedLast30d: s.Failed,
                    CostUsdLast30d: costByTenant.GetValueOrDefault(s.TenantId)));
                continue;
            }

            rows.Add(new TenantAnalyticsRow(
                s.TenantId,
                Slug: t.Slug,
                Name: t.Name,
                Plan: t.Plan,
                Status: t.Status,
                WorkflowsLast30d: s.Total,
                WorkflowsFailedLast30d: s.Failed,
                CostUsdLast30d: costByTenant.GetValueOrDefault(s.TenantId)));
        }

        return rows
            .OrderByDescending(r => r.WorkflowsLast30d)
            .ThenBy(r => r.Slug, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
    }

    public async Task<IReadOnlyList<EventTypeBucket>> GetEventHistogramAsync(
        DateTime? since = null,
        int limit = 20,
        CancellationToken ct = default)
    {
        if (limit <= 0) limit = 1;
        if (limit > 100) limit = 100;

        var lowerBound = since ?? _clock.GetUtcNow().UtcDateTime.AddHours(-24);

        var buckets = await _cp.PlatformEvents
            .AsNoTracking()
            .Where(e => e.CreatedAt >= lowerBound)
            .GroupBy(e => e.Type)
            .Select(g => new EventTypeBucket(g.Key, g.Count()))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return buckets
            .OrderByDescending(b => b.Count)
            .ThenBy(b => b.Type, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
    }

    // ── Internal helpers — exposed internal (not private) for unit tests via
    //    InternalsVisibleTo("Tamma.Api.Tests") declared in Tamma.Api.csproj. ──

    internal async Task<TenantCounts> GetTenantCountsAsync(CancellationToken ct)
    {
        // Project Status via EF.Property so the InMemory provider and the
        // real Npgsql provider both see the shadow column identically.
        // IgnoreQueryFilters is required because the CP context applies a
        // DeletedAt == null filter by default — we want the deleted
        // bucket too.
        var rows = await _cp.Tenants
            .IgnoreQueryFilters()
            .Select(t => new
            {
                t.Id,
                t.DeletedAt,
                Status = EF.Property<string?>(t, "Status"),
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var total = 0;
        var active = 0;
        var provisioning = 0;
        var deleted = 0;

        foreach (var r in rows)
        {
            // Soft-deleted: count in "deleted" bucket and omit from total
            // so the summary stays aligned with the CP filter the rest of
            // the app sees.
            if (r.DeletedAt is not null)
            {
                deleted++;
                continue;
            }

            total++;

            if (string.Equals(r.Status, TenantStatusActive, StringComparison.OrdinalIgnoreCase))
            {
                active++;
            }
            else if (string.Equals(r.Status, TenantStatusProvisioning, StringComparison.OrdinalIgnoreCase))
            {
                provisioning++;
            }
            else if (string.Equals(r.Status, TenantStatusDeleted, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(r.Status, TenantStatusDeleting, StringComparison.OrdinalIgnoreCase))
            {
                // Status='deleted' without DeletedAt set is a transient window
                // between Story 28-5's EmitDeletedSuccessActivity flipping the
                // status and SoftDeleteTenantActivity setting DeletedAt. Count
                // as deleted but not in total.
                deleted++;
                total--;
            }
        }

        return new TenantCounts(total, active, provisioning, deleted);
    }

    internal async Task<WorkflowCounts> GetWorkflowCountsAsync(
        DateTime t24h,
        DateTime t7d,
        DateTime t30d,
        CancellationToken ct)
    {
        // One query over the 30-day window returns every candidate row;
        // bucket in memory so we avoid three round trips. 30 days of
        // workflow activity fits comfortably in a platform-admin response
        // for the fleet size we target (< 10k tenants per README §3).
        var rows = await _app.WorkflowInstances
            .IgnoreQueryFilters()
            .Where(w => w.CreatedAt >= t30d)
            .Select(w => new { w.Status, w.CreatedAt })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var b24 = BucketWorkflow(rows, t24h);
        var b7 = BucketWorkflow(rows, t7d);
        var b30 = BucketWorkflow(rows, t30d);

        return new WorkflowCounts(b24, b7, b30);
    }

    private static WorkflowWindowCounts BucketWorkflow(
        IEnumerable<dynamic> rows,
        DateTime lowerBound)
    {
        var completed = 0;
        var failed = 0;
        var running = 0;

        foreach (var r in rows)
        {
            if (r.CreatedAt < lowerBound) continue;

            string status = (string)r.Status;

            if (string.Equals(status, WfStatusCompleted, StringComparison.OrdinalIgnoreCase))
                completed++;
            else if (string.Equals(status, WfStatusFailed, StringComparison.OrdinalIgnoreCase))
                failed++;
            else if (string.Equals(status, WfStatusRunning, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(status, WfStatusPending, StringComparison.OrdinalIgnoreCase))
                running++;
        }

        return new WorkflowWindowCounts(completed, failed, running);
    }

    internal async Task<AgentDispatchCounts> GetAgentDispatchCountsAsync(
        DateTime t24h,
        DateTime t7d,
        DateTime t30d,
        CancellationToken ct)
    {
        // Pull every AGENT.DISPATCH.* event in the 30-day window; bucket
        // in memory. Epic 19 §7 caps this at ~200k events per month for
        // the target 10k-tenant fleet so this is well under the memory
        // budget of the admin request.
        var rows = await _cp.PlatformEvents
            .AsNoTracking()
            .Where(e => e.CreatedAt >= t30d && EF.Functions.Like(e.Type, AgentDispatchPrefix + "%"))
            .Select(e => new { e.Type, e.CreatedAt })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var b24 = BucketAgent(rows, t24h);
        var b7 = BucketAgent(rows, t7d);
        var b30 = BucketAgent(rows, t30d);

        return new AgentDispatchCounts(b24, b7, b30);
    }

    private static AgentDispatchWindowCounts BucketAgent(
        IEnumerable<dynamic> rows,
        DateTime lowerBound)
    {
        var attempted = 0;
        var success = 0;
        var failed = 0;

        foreach (var r in rows)
        {
            if (r.CreatedAt < lowerBound) continue;

            attempted++;

            string type = (string)r.Type;

            if (string.Equals(type, AgentDispatchSuccess, StringComparison.Ordinal))
                success++;
            else if (string.Equals(type, AgentDispatchFailed, StringComparison.Ordinal))
                failed++;
        }

        return new AgentDispatchWindowCounts(attempted, success, failed);
    }

    internal async Task<CostAggregates> GetCostAggregatesAsync(
        DateTime t24h,
        DateTime t7d,
        DateTime t30d,
        CancellationToken ct)
    {
        // Sum LLM.CALL.SUCCESS.data.costUsd from the legacy domain_events
        // stream. The column is a JSONB string on Postgres and a CLR
        // string under the InMemory provider — we parse in memory so
        // both paths share code. Precision capped at 4 decimals to
        // match the future platform_analytics_hourly.Value column.
        var rows = await _app.DomainEvents
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(e => e.Type == LlmCallSuccess && e.CreatedAt >= t30d)
            .Select(e => new { e.CreatedAt, e.Data })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        decimal sum24 = 0m, sum7 = 0m, sum30 = 0m;

        foreach (var r in rows)
        {
            if (!TryExtractCostUsd(r.Data, out var cost)) continue;

            if (r.CreatedAt >= t30d) sum30 += cost;
            if (r.CreatedAt >= t7d) sum7 += cost;
            if (r.CreatedAt >= t24h) sum24 += cost;
        }

        return new CostAggregates(
            Round4(sum24),
            Round4(sum7),
            Round4(sum30));
    }

    private async Task<Dictionary<Guid, decimal>> SumLlmCostsPerTenantAsync(
        DateTime since,
        CancellationToken ct)
    {
        var rows = await _app.DomainEvents
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(e => e.Type == LlmCallSuccess
                        && e.CreatedAt >= since
                        && e.TenantId != null)
            .Select(e => new { TenantId = e.TenantId!.Value, e.Data })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var byTenant = new Dictionary<Guid, decimal>();

        foreach (var r in rows)
        {
            if (!TryExtractCostUsd(r.Data, out var cost)) continue;

            byTenant[r.TenantId] = byTenant.GetValueOrDefault(r.TenantId) + cost;
        }

        // Normalise to 4 decimals so the round-trip to the future rollup table is lossless.
        foreach (var k in byTenant.Keys.ToList())
            byTenant[k] = Round4(byTenant[k]);

        return byTenant;
    }

    /// <summary>
    /// Extracts <c>data.costUsd</c> from the JSON blob. Returns
    /// <c>false</c> when the blob is not valid JSON or the field is
    /// missing / not a number — cost aggregation tolerates malformed
    /// historical events (shape predates Story 9-2) rather than
    /// failing the whole summary.
    /// </summary>
    internal static bool TryExtractCostUsd(string? json, out decimal cost)
    {
        cost = 0m;
        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("costUsd", out var el))
                return false;

            return el.ValueKind switch
            {
                System.Text.Json.JsonValueKind.Number => el.TryGetDecimal(out cost),
                System.Text.Json.JsonValueKind.String =>
                    decimal.TryParse(el.GetString(),
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out cost),
                _ => false,
            };
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static decimal Round4(decimal v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);
}
