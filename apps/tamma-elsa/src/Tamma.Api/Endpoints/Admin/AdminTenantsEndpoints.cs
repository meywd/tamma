using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tamma.Api.Dtos.Admin;
using Tamma.Api.Services.TenantStatus;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;

namespace Tamma.Api.Endpoints.Admin;

/// <summary>
/// Story 28-11 — platform-admin UX for the tenant lifecycle. These endpoints
/// sit behind <c>OwnerAccess</c> (platform-owner only) and expose the
/// Epic-28 shadow columns on <c>tenants</c> (<c>Status</c>,
/// <c>EncryptedConnectionString</c>, <c>KekVersion</c>,
/// <c>FailureReason</c>, <c>DeleteRequestedAt</c>, <c>PlanId</c>) as a
/// read-only list + detail view, plus state-gated actions that re-drive
/// the Story 28-5 workflows.
///
/// <para>Shadow-column access uses <c>EF.Property&lt;T&gt;</c> against the
/// <see cref="ControlPlaneDbContext"/>. The encrypted connection string
/// itself is NEVER serialised — only its presence leaks into the DTO
/// (<c>HasEncryptedConnectionString</c>). This keeps the admin UX
/// compliant with Doc 01 §8.1 (encrypted-at-rest envelope, never
/// round-trips through the API).</para>
///
/// <para>Action-endpoint state machine (legal transitions only; 409 if
/// illegal):</para>
/// <list type="table">
///   <listheader>
///     <term>Current Status</term>
///     <description>Permitted actions</description>
///   </listheader>
///   <item>
///     <term><c>active</c> / null (legacy)</term>
///     <description>delete, change-plan</description>
///   </item>
///   <item>
///     <term><c>failed</c></term>
///     <description>retry, force-delete</description>
///   </item>
///   <item>
///     <term><c>provisioning</c></term>
///     <description>(none — workflow owns the row)</description>
///   </item>
///   <item>
///     <term><c>deleting</c></term>
///     <description>force-delete (stuck-state recovery only)</description>
///   </item>
///   <item>
///     <term><c>deleted</c></term>
///     <description>(terminal; record is soft-deleted + hidden)</description>
///   </item>
/// </list>
///
/// <para>Retry and delete emit <c>TENANT.PROVISIONING_REQUESTED</c> /
/// <c>TENANT.DELETE.REQUESTED</c> events so the Story 28-5 workflows
/// (wired via the Elsa trigger in a follow-up) pick them up; until the
/// trigger lands, the endpoints still flip the tenant row + emit the
/// event for the audit trail, which is what the admin UX needs to
/// demonstrate.</para>
/// </summary>
public static class AdminTenantsEndpoints
{
    // ── Status vocabulary (matches Story 28-5 activities + Doc 01 §7.2) ──
    private const string StatusPendingVerification = "pending_verification";
    private const string StatusProvisioning = "provisioning";
    private const string StatusActive = "active";
    private const string StatusFailed = "failed";
    private const string StatusDeleting = "deleting";
    private const string StatusDeleted = "deleted";

    private static readonly HashSet<string> AllowedFilterStatuses = new(
        StringComparer.OrdinalIgnoreCase)
    {
        StatusPendingVerification,
        StatusProvisioning,
        StatusActive,
        StatusFailed,
        StatusDeleting,
        StatusDeleted,
    };

    // ── GET /api/admin/tenants ──

    /// <summary>
    /// Paginated + filterable listing. Query params: <c>status</c>,
    /// <c>plan</c> (slug), <c>search</c> (name/slug/owner email prefix),
    /// <c>page</c> (1-indexed, default 1), <c>pageSize</c> (default 25, max
    /// 200).
    /// </summary>
    public static async Task<IResult> ListTenants(
        ControlPlaneDbContext db,
        [FromQuery] string? status = null,
        [FromQuery] string? plan = null,
        [FromQuery] string? search = null,
        [FromQuery] int? page = null,
        [FromQuery] int? pageSize = null,
        CancellationToken ct = default)
    {
        var pageNum = page is null || page < 1 ? 1 : page.Value;
        var size = pageSize is null ? 25 : Math.Clamp(pageSize.Value, 1, 200);
        var skip = (pageNum - 1) * size;

        // Base query — IgnoreQueryFilters so soft-deleted rows (Status=deleted)
        // are visible for audit; we hide them unless status=deleted is
        // explicitly requested.
        var query = db.Tenants
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(t => t.DeletedAt == null);

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!AllowedFilterStatuses.Contains(status))
                return Results.BadRequest(new
                {
                    error = "invalid_status",
                    message = $"status must be one of: {string.Join(", ", AllowedFilterStatuses)}",
                });
            query = query.Where(t => EF.Property<string?>(t, "Status") == status);
        }

        if (!string.IsNullOrWhiteSpace(plan))
        {
            var planId = await db.Plans
                .Where(p => p.Slug == plan)
                .Select(p => (Guid?)p.Id)
                .FirstOrDefaultAsync(ct);
            if (planId is null)
                return Results.BadRequest(new
                {
                    error = "invalid_plan",
                    message = $"plan slug '{plan}' not found",
                });
            query = query.Where(t => EF.Property<Guid?>(t, "PlanId") == planId);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            // Lowercase both sides for case-insensitive match. Works across
            // EF providers (Postgres + InMemory) — unlike EF.Functions.ILike
            // which is Postgres-only.
            var s = search.Trim().ToLowerInvariant();
            query = query.Where(t =>
                t.Name.ToLower().Contains(s) || t.Slug.ToLower().Contains(s));
        }

        var total = await query.CountAsync(ct);

        // Project into an intermediate shape that surfaces shadow columns via
        // EF.Property. We then hydrate owner email in a second pass to keep
        // the server-side translation simple (EF in-memory provider used by
        // tests does not translate a multi-join + FK-via-shadow in one go).
        var raw = await query
            .OrderByDescending(t => t.UpdatedAt)
            .Skip(skip)
            .Take(size)
            .Select(t => new
            {
                Tenant = t,
                Status = EF.Property<string?>(t, "Status"),
                PlanId = EF.Property<Guid?>(t, "PlanId"),
                EncryptedConn = EF.Property<byte[]?>(t, "EncryptedConnectionString"),
                KekVersion = EF.Property<int?>(t, "KekVersion"),
                FailureReason = EF.Property<string?>(t, "FailureReason"),
                DeleteRequestedAt = EF.Property<DateTime?>(t, "DeleteRequestedAt"),
            })
            .ToListAsync(ct);

        var planIds = raw
            .Where(r => r.PlanId.HasValue)
            .Select(r => r.PlanId!.Value)
            .Distinct()
            .ToList();
        var planLookup = await db.Plans
            .AsNoTracking()
            .Where(p => planIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);

        var ownerIds = raw
            .Where(r => r.Tenant.OwnerId.HasValue)
            .Select(r => r.Tenant.OwnerId!.Value)
            .Distinct()
            .ToList();
        var ownerLookup = await db.Users
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(u => ownerIds.Contains(u.Id))
            .Select(u => new { u.Id, u.Email })
            .ToDictionaryAsync(u => u.Id, u => u.Email, ct);

        var items = raw.Select(r =>
        {
            var t = r.Tenant;
            var plan = r.PlanId.HasValue && planLookup.TryGetValue(r.PlanId.Value, out var p)
                ? p : null;
            var ownerEmail = t.OwnerId.HasValue
                && ownerLookup.TryGetValue(t.OwnerId.Value, out var em)
                ? em : null;
            return new AdminTenantListItem(
                t.Id,
                t.Name,
                t.Slug,
                t.Type,
                r.Status,
                t.Plan,
                plan?.DisplayName,
                plan?.Slug,
                r.PlanId,
                t.OwnerId,
                ownerEmail,
                t.CreatedAt,
                t.UpdatedAt,
                r.FailureReason,
                r.DeleteRequestedAt,
                r.KekVersion,
                r.EncryptedConn is not null && r.EncryptedConn.Length > 0);
        }).ToList();

        return Results.Ok(new AdminTenantListResponse(items, total, pageNum, size));
    }

    // ── GET /api/admin/tenants/{id} ──

    public static async Task<IResult> GetTenantDetail(
        Guid tenantId,
        ControlPlaneDbContext db,
        IPlatformEventPublisher eventPublisher,
        CancellationToken ct = default)
    {
        var item = await LoadItemAsync(tenantId, db, ct);
        if (item is null)
            return Results.NotFound(new { error = "tenant_not_found" });

        // Recent 100 platform events for this tenant. Limit keeps the
        // response small; the dashboard SSE follow-up handles tailing.
        var events = await db.PlatformEvents
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId)
            .OrderByDescending(e => e.CreatedAt)
            .Take(100)
            .Select(e => new AdminTenantEventItem(
                e.Id, e.Type, e.CreatedAt, e.Tags, e.Data))
            .ToListAsync(ct);

        return Results.Ok(new AdminTenantDetailResponse(
            item,
            events,
            ComputeActions(item.Status)));
    }

    // ── POST /api/admin/tenants/{id}/actions/retry ──

    /// <summary>
    /// Re-dispatches the create workflow for a tenant stuck in
    /// <c>failed</c>. Flips Status → <c>pending_verification</c> (so the
    /// verify-email trigger picks it back up) and emits
    /// <c>TENANT.PROVISIONING_REQUESTED</c> for the audit log. 409 if the
    /// tenant is not in a retryable state.
    /// </summary>
    public static async Task<IResult> RetryTenant(
        Guid tenantId,
        ControlPlaneDbContext db,
        IPlatformEventPublisher publisher,
        ITenantStatusCache statusCache,
        CancellationToken ct = default)
    {
        var tenant = await db.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == tenantId && t.DeletedAt == null, ct);
        if (tenant is null)
            return Results.NotFound(new { error = "tenant_not_found" });

        var current = (string?)db.Entry(tenant).Property("Status").CurrentValue;
        if (!IsRetryable(current))
            return IllegalTransition(current, "retry");

        db.Entry(tenant).Property("Status").CurrentValue = StatusPendingVerification;
        db.Entry(tenant).Property("FailureReason").CurrentValue = null;
        tenant.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        // Story 28-8 — drop the cached status so the next request sees
        // the new state immediately (per-pod; sibling pods converge
        // via TTL).
        statusCache.Invalidate(tenantId);

        await publisher.AppendAndPublishAsync(
            BuildAdminEvent(
                "TENANT.PROVISIONING_REQUESTED",
                tenantId,
                new Dictionary<string, object?>
                {
                    ["requestedAt"] = DateTime.UtcNow,
                    ["source"] = "admin-retry",
                }),
            ct);

        return Results.Ok(new AdminTenantActionResponse(
            tenantId,
            StatusPendingVerification,
            "Retry queued — provisioning workflow will restart on the next trigger poll."));
    }

    // ── POST /api/admin/tenants/{id}/actions/delete ──

    /// <summary>
    /// Initiates tenant deletion against an <c>active</c> tenant. Flips
    /// Status → <c>deleting</c>, stamps <c>DeleteRequestedAt</c>, emits
    /// <c>TENANT.DELETE.REQUESTED</c>. 409 if the tenant is already
    /// deleting/deleted or in a non-stable state.
    /// </summary>
    public static async Task<IResult> DeleteTenant(
        Guid tenantId,
        ControlPlaneDbContext db,
        IPlatformEventPublisher publisher,
        ITenantStatusCache statusCache,
        CancellationToken ct = default)
    {
        var tenant = await db.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == tenantId && t.DeletedAt == null, ct);
        if (tenant is null)
            return Results.NotFound(new { error = "tenant_not_found" });

        var current = (string?)db.Entry(tenant).Property("Status").CurrentValue;
        if (!IsDeletable(current))
            return IllegalTransition(current, "delete");

        db.Entry(tenant).Property("Status").CurrentValue = StatusDeleting;
        db.Entry(tenant).Property("DeleteRequestedAt").CurrentValue = DateTime.UtcNow;
        tenant.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        statusCache.Invalidate(tenantId);  // Story 28-8

        await publisher.AppendAndPublishAsync(
            BuildAdminEvent(
                "TENANT.DELETE.REQUESTED",
                tenantId,
                new Dictionary<string, object?>
                {
                    ["requestedAt"] = DateTime.UtcNow,
                    ["source"] = "admin-delete",
                }),
            ct);

        return Results.Ok(new AdminTenantActionResponse(
            tenantId,
            StatusDeleting,
            "Delete queued — cooling-off period begins now; destructive drop runs after the configured delay."));
    }

    // ── POST /api/admin/tenants/{id}/actions/force-delete ──

    /// <summary>
    /// Stuck-state recovery. Accepts a tenant in <c>failed</c> or
    /// <c>deleting</c> and forces another delete pass. Requires the
    /// caller to confirm via the <c>X-Admin-Confirm</c> header matching
    /// the tenant id — the dashboard collects this via a typed-slug
    /// friction modal.
    /// </summary>
    public static async Task<IResult> ForceDeleteTenant(
        Guid tenantId,
        HttpContext http,
        ControlPlaneDbContext db,
        IPlatformEventPublisher publisher,
        ITenantStatusCache statusCache,
        CancellationToken ct = default)
    {
        // 2FA-lite: the caller must echo the tenant id in a header so a
        // fat-fingered POST never nukes a prod tenant. Dashboard builds
        // this from the typed-slug modal.
        var confirm = http.Request.Headers["X-Admin-Confirm"].ToString();
        if (!string.Equals(confirm, tenantId.ToString(), StringComparison.OrdinalIgnoreCase))
            return Results.Json(
                new
                {
                    error = "confirmation_required",
                    message = "X-Admin-Confirm header must echo the tenant id to authorise force-delete.",
                },
                statusCode: StatusCodes.Status400BadRequest);

        var tenant = await db.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == tenantId && t.DeletedAt == null, ct);
        if (tenant is null)
            return Results.NotFound(new { error = "tenant_not_found" });

        var current = (string?)db.Entry(tenant).Property("Status").CurrentValue;
        if (!IsForceDeletable(current))
            return IllegalTransition(current, "force-delete");

        db.Entry(tenant).Property("Status").CurrentValue = StatusDeleting;
        db.Entry(tenant).Property("DeleteRequestedAt").CurrentValue = DateTime.UtcNow;
        tenant.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        statusCache.Invalidate(tenantId);  // Story 28-8

        await publisher.AppendAndPublishAsync(
            BuildAdminEvent(
                "TENANT.DELETE.REQUESTED",
                tenantId,
                new Dictionary<string, object?>
                {
                    ["requestedAt"] = DateTime.UtcNow,
                    ["source"] = "admin-force-delete",
                    ["previousStatus"] = current,
                }),
            ct);

        return Results.Ok(new AdminTenantActionResponse(
            tenantId,
            StatusDeleting,
            "Force-delete queued — destructive drop runs immediately (cooling-off waived)."));
    }

    // ── POST /api/admin/tenants/{id}/cleanup ──

    /// <summary>
    /// Story 28-5 AC7 — operator-triggered "best effort" cleanup for a
    /// tenant left in a damaged state (half-provisioned, half-deleted,
    /// or failed compensation). Emits <c>TENANT.CLEANUP.REQUESTED</c>;
    /// the global-Elsa <c>CleanUpFailedTenantWorkflow</c> picks it up
    /// and runs the idempotent teardown sequence with continue-on-error
    /// semantics. The workflow emits a single terminal event
    /// (<c>TENANT.DELETED.SUCCESS</c> or <c>TENANT.DELETE.FAILED</c>)
    /// and, on partial failure, sets
    /// <c>tenants.ProvisioningState='requires_manual_cleanup'</c> +
    /// <c>ProvisioningDetail=&lt;summary&gt;</c>.
    ///
    /// <para>Unlike <c>delete</c> / <c>force-delete</c>, this does NOT
    /// require a particular <c>Status</c> — cleanup is for tenants
    /// already known to be damaged. The endpoint just verifies the
    /// tenant row exists + isn't already soft-deleted.</para>
    /// </summary>
    public static async Task<IResult> CleanupTenant(
        Guid tenantId,
        HttpContext http,
        ControlPlaneDbContext db,
        IPlatformEventPublisher publisher,
        CancellationToken ct = default)
    {
        // 2FA-lite per the force-delete pattern — cleanup runs
        // destructive DDL (DROP DATABASE, DROP ROLE) and a fat-finger
        // POST should not nuke a tenant by accident.
        var confirm = http.Request.Headers["X-Admin-Confirm"].ToString();
        if (!string.Equals(confirm, tenantId.ToString(), StringComparison.OrdinalIgnoreCase))
            return Results.Json(
                new
                {
                    error = "confirmation_required",
                    message = "X-Admin-Confirm header must echo the tenant id to authorise cleanup.",
                },
                statusCode: StatusCodes.Status400BadRequest);

        var tenant = await db.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == tenantId && t.DeletedAt == null, ct);
        if (tenant is null)
            return Results.NotFound(new { error = "tenant_not_found" });

        // Optional operator note for the audit trail.
        var note = http.Request.Headers["X-Admin-Note"].ToString();
        if (note.Length > 500) note = note[..500];

        await publisher.AppendAndPublishAsync(
            BuildAdminEvent(
                "TENANT.CLEANUP.REQUESTED",
                tenantId,
                new Dictionary<string, object?>
                {
                    ["requestedAt"] = DateTime.UtcNow,
                    ["source"] = "admin-cleanup",
                    ["note"] = string.IsNullOrWhiteSpace(note) ? null : note,
                    ["currentStatus"] = (string?)db.Entry(tenant).Property("Status").CurrentValue,
                }),
            ct);

        return Results.Ok(new AdminTenantActionResponse(
            tenantId,
            (string?)db.Entry(tenant).Property("Status").CurrentValue ?? "unknown",
            "Cleanup queued — global-Elsa workflow will run best-effort teardown."));
    }

    // ── PATCH /api/admin/tenants/{id}/plan ──

    public static async Task<IResult> UpdateTenantPlan(
        Guid tenantId,
        UpdateTenantPlanRequest req,
        ControlPlaneDbContext db,
        IPlatformEventPublisher publisher,
        CancellationToken ct = default)
    {
        if (req.PlanId == Guid.Empty)
            return Results.BadRequest(new { error = "plan_id_required" });

        var tenant = await db.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == tenantId && t.DeletedAt == null, ct);
        if (tenant is null)
            return Results.NotFound(new { error = "tenant_not_found" });

        var current = (string?)db.Entry(tenant).Property("Status").CurrentValue;
        if (!IsPlanChangeAllowed(current))
            return IllegalTransition(current, "change-plan");

        var plan = await db.Plans.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == req.PlanId, ct);
        if (plan is null)
            return Results.BadRequest(new { error = "plan_not_found" });
        if (!plan.IsActive)
            return Results.BadRequest(new { error = "plan_inactive" });

        var oldPlanId = (Guid?)db.Entry(tenant).Property("PlanId").CurrentValue;
        db.Entry(tenant).Property("PlanId").CurrentValue = plan.Id;
        // Keep the legacy string column in lockstep so dashboards that still
        // read it render the same plan.
        tenant.Plan = plan.Slug;
        tenant.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        await publisher.AppendAndPublishAsync(
            BuildAdminEvent(
                "PLAN.UPDATED",
                tenantId,
                new Dictionary<string, object?>
                {
                    ["oldPlanId"] = oldPlanId?.ToString("D"),
                    ["newPlanId"] = plan.Id.ToString("D"),
                    ["newPlanSlug"] = plan.Slug,
                }),
            ct);

        return Results.Ok(new AdminTenantActionResponse(
            tenantId,
            current ?? StatusActive,
            $"Plan changed to {plan.DisplayName}."));
    }

    // ── helpers ──

    private static async Task<AdminTenantListItem?> LoadItemAsync(
        Guid tenantId,
        ControlPlaneDbContext db,
        CancellationToken ct)
    {
        var row = await db.Tenants
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(t => t.Id == tenantId && t.DeletedAt == null)
            .Select(t => new
            {
                Tenant = t,
                Status = EF.Property<string?>(t, "Status"),
                PlanId = EF.Property<Guid?>(t, "PlanId"),
                EncryptedConn = EF.Property<byte[]?>(t, "EncryptedConnectionString"),
                KekVersion = EF.Property<int?>(t, "KekVersion"),
                FailureReason = EF.Property<string?>(t, "FailureReason"),
                DeleteRequestedAt = EF.Property<DateTime?>(t, "DeleteRequestedAt"),
            })
            .FirstOrDefaultAsync(ct);
        if (row is null) return null;

        Plan? plan = null;
        if (row.PlanId.HasValue)
        {
            plan = await db.Plans.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == row.PlanId.Value, ct);
        }

        string? ownerEmail = null;
        if (row.Tenant.OwnerId.HasValue)
        {
            ownerEmail = await db.Users.AsNoTracking()
                .IgnoreQueryFilters()
                .Where(u => u.Id == row.Tenant.OwnerId.Value)
                .Select(u => u.Email)
                .FirstOrDefaultAsync(ct);
        }

        return new AdminTenantListItem(
            row.Tenant.Id,
            row.Tenant.Name,
            row.Tenant.Slug,
            row.Tenant.Type,
            row.Status,
            row.Tenant.Plan,
            plan?.DisplayName,
            plan?.Slug,
            row.PlanId,
            row.Tenant.OwnerId,
            ownerEmail,
            row.Tenant.CreatedAt,
            row.Tenant.UpdatedAt,
            row.FailureReason,
            row.DeleteRequestedAt,
            row.KekVersion,
            row.EncryptedConn is not null && row.EncryptedConn.Length > 0);
    }

    internal static AdminTenantActionGate ComputeActions(string? status)
    {
        // Normalise null (legacy rows) to "active" — legacy tenants predate
        // the shadow column and are treated as live for gating purposes.
        var effective = string.IsNullOrWhiteSpace(status) ? StatusActive : status;
        return new AdminTenantActionGate(
            CanRetry: IsRetryable(effective),
            CanDelete: IsDeletable(effective),
            CanForceDelete: IsForceDeletable(effective),
            CanChangePlan: IsPlanChangeAllowed(effective));
    }

    private static bool IsRetryable(string? status)
        => string.Equals(status, StatusFailed, StringComparison.OrdinalIgnoreCase);

    private static bool IsDeletable(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return true; // legacy row
        return string.Equals(status, StatusActive, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsForceDeletable(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return false;
        return string.Equals(status, StatusFailed, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, StatusDeleting, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPlanChangeAllowed(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return true;
        return string.Equals(status, StatusActive, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, StatusFailed, StringComparison.OrdinalIgnoreCase);
    }

    private static IResult IllegalTransition(string? current, string action) =>
        Results.Json(
            new
            {
                error = "illegal_transition",
                message = $"Action '{action}' is not allowed against tenants in status '{current ?? "null"}'.",
                currentStatus = current,
            },
            statusCode: StatusCodes.Status409Conflict);

    private static PlatformEvent BuildAdminEvent(
        string type,
        Guid tenantId,
        IReadOnlyDictionary<string, object?>? data = null)
    {
        return new PlatformEvent
        {
            Type = type,
            TenantId = tenantId,
            Tags = JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["tenantId"] = tenantId.ToString("D"),
                ["source"] = "admin",
            }),
            Metadata = """{"workflowVersion":"1.0.0","eventSource":"system"}""",
            Data = data is null ? "{}" : JsonSerializer.Serialize(data),
        };
    }
}
