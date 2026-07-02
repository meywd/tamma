using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tamma.Api.Services.Billing;
using Tamma.Data;

namespace Tamma.Api.Endpoints.Billing;

/// <summary>
/// Story 35-5 (AC12) — platform-operator replay/inspect surface for stored
/// Stripe webhook deliveries. Both routes are <c>PlatformOwnerAccess</c>-gated (a
/// Stripe webhook is a platform-operator concern, never a tenant-scoped route —
/// there is no path for one tenant to read another tenant's webhook rows).
///
/// <list type="bullet">
///   <item><c>GET /api/v1/admin/billing/webhook-events</c> — recent rows,
///     filterable by <c>status</c>/<c>eventType</c>/<c>tenantId</c>, paged
///     (default 50, max 200).</item>
///   <item><c>POST /api/v1/admin/billing/webhook-events/{id}/replay</c> —
///     re-dispatch a stored payload (idempotent; a <c>projected</c> row is a
///     no-op).</item>
/// </list>
/// </summary>
public static class BillingWebhookAdminEndpoints
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    public static async Task<IResult> List(
        [FromServices] ControlPlaneDbContext db,
        [FromServices] ILoggerFactory loggerFactory,
        string? status = null,
        string? eventType = null,
        Guid? tenantId = null,
        int limit = DefaultLimit,
        int offset = 0)
    {
        var logger = loggerFactory.CreateLogger("BillingWebhookAdminEndpoints");
        limit = Math.Clamp(limit, 1, MaxLimit);
        offset = Math.Max(offset, 0);

        var query = db.BillingWebhookEvents.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(e => e.Status == status);
        if (!string.IsNullOrWhiteSpace(eventType))
            query = query.Where(e => e.EventType == eventType);
        if (tenantId is not null)
            query = query.Where(e => e.TenantId == tenantId);

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(e => e.ReceivedAt)
            .Skip(offset)
            .Take(limit)
            .Select(e => new
            {
                e.Id,
                e.StripeEventId,
                e.EventType,
                e.TenantId,
                e.StripeObjectId,
                e.Status,
                e.Attempts,
                e.LastError,
                e.ReceivedAt,
                e.ProcessedAt,
            })
            .ToListAsync();

        logger.LogInformation(
            "Admin listed {Count} billing webhook events (status={Status}, eventType={EventType}, "
            + "tenantId={TenantId}, total={Total}).",
            items.Count, status, eventType, tenantId, total);

        return Results.Ok(new { items, total, limit, offset });
    }

    public static async Task<IResult> Replay(
        Guid id,
        [FromServices] IStripeWebhookProcessor processor,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("BillingWebhookAdminEndpoints");
        logger.LogInformation("Admin replay requested for billing webhook event {Id}.", id);

        var result = await processor.ReplayAsync(id);
        if (result is null)
        {
            return Results.NotFound(new { error = "webhook event not found", id });
        }

        return Results.Ok(new { replayed = true, id, status = result.Status });
    }
}
