using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tamma.Api.Services.PlatformTasks;
using Tamma.Data;

namespace Tamma.Api.Endpoints.Admin;

/// <summary>
/// Story 28-6 AC8 — admin diagnostics for the platform-side queues
/// (<c>platform_queued_tasks</c>, <c>platform_email_outbox</c>,
/// <c>platform_events</c>). One endpoint:
/// <c>GET /api/admin/diagnostics/platform-queues</c>. Owner-only at
/// the wiring site because it exposes cross-tenant infrastructure
/// state.
///
/// <para>Response groups counters by status and lists the registered
/// task-handler types so an operator can spot a queue full of <c>X</c>
/// with no handler registered (the worker dead-letters those, but the
/// diagnostic flags it earlier).</para>
/// </summary>
public static class PlatformQueuesAdminEndpoints
{
    public static async Task<IResult> GetDiagnostics(
        [FromServices] ControlPlaneDbContext db,
        [FromServices] IPlatformTaskHandlerRegistry registry,
        CancellationToken ct)
    {
        // Tasks: status counts + 10 oldest pending.
        var taskStatusCounts = await db.PlatformQueuedTasks
            .AsNoTracking()
            .GroupBy(t => t.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Status, x => x.Count, ct);

        var oldestPending = await db.PlatformQueuedTasks
            .AsNoTracking()
            .Where(t => t.Status == "pending")
            .OrderBy(t => t.CreatedAt)
            .Take(10)
            .Select(t => new
            {
                t.Id,
                t.Type,
                t.RetryCount,
                t.CreatedAt,
                t.TenantId,
            })
            .ToListAsync(ct);

        // Outbox: status counts + 10 oldest pending.
        var outboxStatusCounts = await db.PlatformEmailOutbox
            .AsNoTracking()
            .GroupBy(m => m.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Status, x => x.Count, ct);

        var oldestEmails = await db.PlatformEmailOutbox
            .AsNoTracking()
            .Where(m => m.Status == "pending")
            .OrderBy(m => m.CreatedAt)
            .Take(10)
            .Select(m => new
            {
                m.Id,
                m.Template,
                m.Attempts,
                m.NextAttemptAt,
                m.CreatedAt,
                m.TenantId,
            })
            .ToListAsync(ct);

        // Platform events: most-recent 10.
        var recentEvents = await db.PlatformEvents
            .AsNoTracking()
            .OrderByDescending(e => e.CreatedAt)
            .Take(10)
            .Select(e => new
            {
                e.Id,
                e.Type,
                e.TenantId,
                e.CreatedAt,
            })
            .ToListAsync(ct);

        // Registered handlers — operator-actionable mismatch detection.
        var registeredHandlers = registry.RegisteredTypes
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        // Mismatch heuristic: pending task types with no handler.
        var pendingTaskTypes = await db.PlatformQueuedTasks
            .AsNoTracking()
            .Where(t => t.Status == "pending")
            .Select(t => t.Type)
            .Distinct()
            .ToListAsync(ct);
        var unhandledPendingTypes = pendingTaskTypes
            .Where(t => !registeredHandlers.Contains(t))
            .ToArray();

        return Results.Ok(new
        {
            tasks = new
            {
                statusCounts = taskStatusCounts,
                oldestPending,
                registeredHandlers,
                unhandledPendingTypes,
            },
            emails = new
            {
                statusCounts = outboxStatusCounts,
                oldestPending = oldestEmails,
            },
            events = new
            {
                recent = recentEvents,
            },
        });
    }
}
