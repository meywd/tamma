using Microsoft.EntityFrameworkCore;
using Tamma.Data;
using Tamma.Data.Repositories;

namespace Tamma.Api.Endpoints;

/// <summary>
/// Story 18-5 — user-facing dashboard endpoints. Unlike <see cref="DashboardEndpoints"/>
/// (operator/admin surface, returns cross-tenant rollups when
/// <c>TenantContext.TenantId</c> is unset), these endpoints live under
/// <c>/api/v1/orgs/{tenantId:guid}/dashboard/*</c> and are scoped to the
/// path tenant — <strong>always</strong>. The caller is validated by
/// <see cref="Tamma.Api.Authorization.RequireTenantMembershipFilter"/>
/// before any handler here runs, so by the time a handler executes the
/// caller is known to be a member of <c>tenantId</c>.
///
/// <para>
/// Response shapes deliberately match the contract the user dashboard SPA
/// consumes (see <c>packages/dashboard-user/src/api/</c>).
/// </para>
/// </summary>
public static class UserDashboardEndpoints
{
    // Max number of rows the caller can request via the optional limit param.
    // Picked to match the TS admin dashboard defaults (finding 022) so the
    // user-facing widgets and ops widgets pull at most the same page size.
    private const int MaxRunLimit = 100;
    private const int DefaultRunLimit = 10;
    private const int RecentEventsLimit = 10;

    /// <summary>
    /// Dashboard home summary: total-events, total-workflows, workflow-defs
    /// count (across the whole platform — definitions aren't tenant-scoped
    /// yet), and the 10 most recent tenant-scoped events.
    /// </summary>
    public static async Task<IResult> GetOrgSummary(
        Guid tenantId,
        TammaDbContext db,
        IEventRepository eventRepo,
        IWorkflowRepository workflowRepo)
    {
        var totalEvents = await db.DomainEvents
            .Where(e => e.TenantId == tenantId)
            .CountAsync();

        var totalWorkflows = await db.WorkflowInstances
            .Where(i => i.TenantId == tenantId)
            .CountAsync();

        var defs = await workflowRepo.ListDefinitionsAsync();
        var recent = await eventRepo.QueryAsync(tenantId, null, null, RecentEventsLimit);

        return Results.Ok(new
        {
            tenantId,
            totalEvents,
            totalWorkflows,
            workflowDefinitions = defs.Count,
            recentEvents = recent.Select(e => new
            {
                id = e.Id,
                type = e.Type,
                createdAt = e.CreatedAt,
                issueNumber = e.IssueNumber,
            }),
            timestamp = DateTime.UtcNow,
        });
    }

    /// <summary>
    /// Latest workflow runs for the tenant, newest-first. Used by the
    /// dashboard home "Recent runs" widget + <c>/runs</c> list page.
    /// </summary>
    public static async Task<IResult> GetRecentRuns(
        Guid tenantId,
        IWorkflowRepository workflowRepo,
        int? limit)
    {
        var pageSize = Math.Clamp(limit ?? DefaultRunLimit, 1, MaxRunLimit);
        var (instances, total) = await workflowRepo.ListInstancesAsync(
            definitionId: null,
            tenantId: tenantId,
            page: 1,
            pageSize: pageSize);

        return Results.Ok(new
        {
            tenantId,
            total,
            runs = instances.Select(i => new
            {
                id = i.Id,
                definitionId = i.DefinitionId,
                status = i.Status,
                currentActivity = i.CurrentActivity,
                createdAt = i.CreatedAt,
                startedAt = i.StartedAt,
                completedAt = i.CompletedAt,
                durationMs = i.StartedAt is null || i.CompletedAt is null
                    ? (double?)null
                    : (i.CompletedAt.Value - i.StartedAt.Value).TotalMilliseconds,
            }),
        });
    }

    /// <summary>
    /// Aggregate stats for the dashboard's "Quick stats" widget: total
    /// runs, terminal-state counts, success rate, and average duration.
    /// </summary>
    public static async Task<IResult> GetStats(
        Guid tenantId,
        TammaDbContext db)
    {
        var rows = await db.WorkflowInstances
            .Where(i => i.TenantId == tenantId)
            .Select(i => new
            {
                i.Status,
                i.StartedAt,
                i.CompletedAt,
            })
            .ToListAsync();

        var total = rows.Count;
        var completed = rows.Count(r => r.Status == "completed");
        var failed = rows.Count(r => r.Status == "failed");
        var running = rows.Count(r => r.Status is "pending" or "running");

        // Success rate is computed over terminal runs only (completed +
        // failed). Runs still in-flight don't yet count toward the rate.
        var terminal = completed + failed;
        var successRate = terminal == 0 ? 0.0 : (double)completed / terminal;

        var durations = rows
            .Where(r => r.StartedAt.HasValue && r.CompletedAt.HasValue)
            .Select(r => (r.CompletedAt!.Value - r.StartedAt!.Value).TotalSeconds)
            .ToList();
        var avgDurationSeconds = durations.Count == 0 ? 0.0 : durations.Average();

        return Results.Ok(new
        {
            tenantId,
            totalRuns = total,
            completedRuns = completed,
            failedRuns = failed,
            runningRuns = running,
            successRate,
            avgDurationSeconds,
        });
    }
}
