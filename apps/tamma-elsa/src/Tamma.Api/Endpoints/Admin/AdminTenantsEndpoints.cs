using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tamma.Api.Dtos.Admin;
using Tamma.Api.Services.Pricing;
using Tamma.Api.Services.TenantStatus;
using Tamma.Core;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;
// `using Tamma.Api.Services.Provisioning` would make ITenantConnectionResolver
// ambiguous against Tamma.Data.Abstractions — alias the one type we need.
using MoveTenantTaskPayload = Tamma.Api.Services.Provisioning.MoveTenantTaskPayload;

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

    // ── GET /api/admin/tenants/{tenantId}/entitlements ──

    /// <summary>
    /// Story 34-6 (AC5) — the platform-owner read seam for any tenant's
    /// resolved entitlement set + live headroom. <c>PlatformOwnerAccess</c>
    /// only; the tenant is taken from the route. An unknown tenant OR a tenant
    /// with no active assignment → 404 (the <c>NO_ASSIGNMENT</c> error mapped,
    /// never a 500). Same body shape as the member self-read
    /// (<c>GET /api/pricing/entitlements</c>).
    /// </summary>
    public static async Task<IResult> GetTenantEntitlements(
        Guid tenantId,
        IEntitlementService entitlements,
        IEntitlementUsageReader usageReader,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(
            "Tamma.Api.Endpoints.Admin.AdminTenantsEndpoints");

        try
        {
            var dto = await EntitlementResponseBuilder.BuildAsync(
                entitlements, usageReader,
                EntitlementPrincipal.ForTenant(tenantId), logger, ct);

            logger.LogInformation(
                "Admin entitlement read: tenant {TenantId}", tenantId);
            return Results.Ok(dto);
        }
        catch (TammaError ex) when (ex.Code == "ENTITLEMENT.RESOLVE.NO_ASSIGNMENT")
        {
            // Unknown tenant OR no active assignment — both 404 (AC5).
            return Results.NotFound(new { error = "no_active_assignment" });
        }
        catch (TammaError ex) when (ex.Code == "ENTITLEMENT.RESOLVE.CATALOG_UNAVAILABLE")
        {
            // A pinned plan whose catalog snapshot vanished — a transient/config
            // fault (the snapshot SHOULD exist), not "no plan". Fail loud as 503
            // (typed ProblemDetails), never a bare 500 and never a permissive 200.
            logger.LogError(
                "Admin entitlement read failed — tenant {TenantId} pinned plan has no catalog snapshot",
                tenantId);
            return Results.Problem(
                title: ex.Code, detail: ex.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (TammaError ex) when (ex.Code == "ENTITLEMENT.RESOLVE.NO_PRINCIPAL")
        {
            // Defensive — the admin route always supplies a tenant id, so a
            // malformed principal reaching here is a bad request, not a 500.
            return Results.Problem(
                title: ex.Code, detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

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
            // Story 34-1: a slug is now a multi-version chain (active +
            // deprecated). Pin Status == "active" so the slug→Id resolution
            // is deterministic (UX_plans_OneActivePerSlug guarantees one
            // active row) and tenant filtering keys off the live version's Id.
            var planId = await db.Plans
                .Where(p => p.Slug == plan && p.Status == "active")
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
                KekVersion = (int?)EF.Property<short>(t, "KekVersion"),
                FailureReason = EF.Property<string?>(t, "FailureReason"),
                DeleteRequestedAt = EF.Property<DateTime?>(t, "DeleteRequestedAt"),
                // Phase 4 — tenant→DB view: which pool row hosts the
                // tenant's schema, and the schema's name.
                DatabaseId = EF.Property<Guid?>(t, "DatabaseId"),
                SchemaName = EF.Property<string?>(t, "SchemaName"),
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
                r.EncryptedConn is not null && r.EncryptedConn.Length > 0,
                r.DatabaseId,
                r.SchemaName);
        }).ToList();

        return Results.Ok(new AdminTenantListResponse(items, total, pageNum, size));
    }

    // ── GET /api/admin/tenants/{id} ──

    public static async Task<IResult> GetTenantDetail(
        Guid tenantId,
        ControlPlaneDbContext db,
        IPlatformEventPublisher eventPublisher,
        Tamma.Api.Services.Analytics.IPlatformAnalyticsService analytics,
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

        // Story 28-11 AC2 — the 24h resource rollup from
        // platform_analytics_hourly. Fact-table-only read; a fresh tenant
        // with no rows gets TenantResourceSummary.Empty (zeros, never null).
        var resourceSummary = await analytics.GetTenantResourceSummaryAsync(tenantId, ct);

        return Results.Ok(new AdminTenantDetailResponse(
            item,
            events,
            ComputeActions(item.Status),
            resourceSummary));
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
        ITenantConnectionResolver connectionResolver,
        ITenantStatusInvalidationBus invalidationBus,
        [FromServices] TimeProvider timeProvider,
        ClaimsPrincipal principal,
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

        var now = timeProvider.GetUtcNow().UtcDateTime;
        db.Entry(tenant).Property("Status").CurrentValue = StatusPendingVerification;
        db.Entry(tenant).Property("FailureReason").CurrentValue = null;
        tenant.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        // Story 28-8 — drop the cached status so the next request sees
        // the new state immediately (per-pod; sibling pods converge
        // via TTL).
        statusCache.Invalidate(tenantId);
        // H12 #2 — also evict the resolver's data-source pool. The
        // status cache alone doesn't unwind a warm NpgsqlDataSource —
        // without this, in-flight handlers continue holding a pool
        // built against the now-stale connection envelope until the
        // pool's natural eviction kicks in.
        await connectionResolver.EvictAsync(tenantId, ct);
        // Round-2 follow-up — cluster-wide fan-out via Postgres
        // LISTEN/NOTIFY so sibling pods drop their copy + evict their
        // resolver pool within milliseconds, not the 10s TTL window.
        // Best-effort: a Postgres failure is logged + swallowed inside
        // the bus; this admin action does not fail on a transient
        // notify hiccup.
        await invalidationBus.PublishAsync(tenantId, ct);

        await publisher.AppendAndPublishAsync(
            BuildAdminEvent(
                "TENANT.PROVISIONING_REQUESTED",
                tenantId,
                principal,
                new Dictionary<string, object?>
                {
                    ["requestedAt"] = now,
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
        ITenantConnectionResolver connectionResolver,
        ITenantStatusInvalidationBus invalidationBus,
        [FromServices] TimeProvider timeProvider,
        ClaimsPrincipal principal,
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

        var now = timeProvider.GetUtcNow().UtcDateTime;
        db.Entry(tenant).Property("Status").CurrentValue = StatusDeleting;
        db.Entry(tenant).Property("DeleteRequestedAt").CurrentValue = now;
        tenant.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        statusCache.Invalidate(tenantId);  // Story 28-8
        await connectionResolver.EvictAsync(tenantId, ct);  // H12 #2
        await invalidationBus.PublishAsync(tenantId, ct);  // R2 follow-up — cluster fan-out

        await publisher.AppendAndPublishAsync(
            BuildAdminEvent(
                "TENANT.DELETE.REQUESTED",
                tenantId,
                principal,
                new Dictionary<string, object?>
                {
                    ["requestedAt"] = now,
                    ["source"] = "admin-delete",
                }),
            ct);

        return Results.Ok(new AdminTenantActionResponse(
            tenantId,
            StatusDeleting,
            "Delete queued — cooling-off period begins now; destructive drop runs after the configured delay."));
    }

    // ── POST /api/admin/tenants/{id}/actions/cancel-delete ──

    /// <summary>
    /// Story 28-5 AC4 — cancels a pending tenant deletion during the
    /// cooling-off window. Flips <c>Status</c> → <c>active</c>, clears
    /// <c>DeleteRequestedAt</c>, invalidates the status cache (+ resolver pool
    /// + cluster NOTIFY), and emits <c>TENANT.DELETE_CANCELLED</c>. 409 if the
    /// tenant is not currently <c>deleting</c> (the destructive drop may
    /// already have run, or never started).
    ///
    /// <para><b>Cancellation is honoured even AFTER the trigger dispatches.</b>
    /// The flip back to <c>active</c> is caught at THREE checkpoints: (1) the
    /// trigger's pre-dispatch re-read (before the workflow even starts); (2)
    /// the workflow's mark step (top of run); (3) the workflow's cancellation
    /// guard immediately before <c>DROP SCHEMA</c>. Any of the three aborts the
    /// teardown (terminal emits <c>TENANT.DELETE.ABORTED</c>), so a cancel that
    /// races a just-dispatched workflow does NOT result in a dropped schema.
    /// The only un-cancellable point is after the irreversible drop has
    /// physically run — at which point the status is no longer <c>deleting</c>
    /// and this endpoint returns 409.</para>
    /// </summary>
    public static async Task<IResult> CancelDeleteTenant(
        Guid tenantId,
        ControlPlaneDbContext db,
        IPlatformEventPublisher publisher,
        ITenantStatusCache statusCache,
        ITenantConnectionResolver connectionResolver,
        ITenantStatusInvalidationBus invalidationBus,
        [FromServices] TimeProvider timeProvider,
        ClaimsPrincipal principal,
        CancellationToken ct = default)
    {
        var tenant = await db.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == tenantId && t.DeletedAt == null, ct);
        if (tenant is null)
            return Results.NotFound(new { error = "tenant_not_found" });

        var current = (string?)db.Entry(tenant).Property("Status").CurrentValue;
        if (!IsCancelDeletable(current))
            return IllegalTransition(current, "cancel-delete");

        var now = timeProvider.GetUtcNow().UtcDateTime;
        db.Entry(tenant).Property("Status").CurrentValue = StatusActive;
        db.Entry(tenant).Property("DeleteRequestedAt").CurrentValue = (DateTime?)null;
        tenant.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        statusCache.Invalidate(tenantId);  // Story 28-8
        await connectionResolver.EvictAsync(tenantId, ct);  // H12 #2
        await invalidationBus.PublishAsync(tenantId, ct);  // R2 follow-up — cluster fan-out

        await publisher.AppendAndPublishAsync(
            BuildAdminEvent(
                "TENANT.DELETE_CANCELLED",
                tenantId,
                principal,
                new Dictionary<string, object?>
                {
                    ["cancelledAt"] = now,
                    ["source"] = "admin-cancel-delete",
                }),
            ct);

        return Results.Ok(new AdminTenantActionResponse(
            tenantId,
            StatusActive,
            "Delete cancelled — tenant restored to active before the destructive drop ran."));
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
        ITenantConnectionResolver connectionResolver,
        ITenantStatusInvalidationBus invalidationBus,
        [FromServices] TimeProvider timeProvider,
        ClaimsPrincipal principal,
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

        var now = timeProvider.GetUtcNow().UtcDateTime;
        db.Entry(tenant).Property("Status").CurrentValue = StatusDeleting;
        db.Entry(tenant).Property("DeleteRequestedAt").CurrentValue = now;
        tenant.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        statusCache.Invalidate(tenantId);  // Story 28-8
        await connectionResolver.EvictAsync(tenantId, ct);  // H12 #2
        await invalidationBus.PublishAsync(tenantId, ct);  // R2 follow-up — cluster fan-out

        await publisher.AppendAndPublishAsync(
            BuildAdminEvent(
                "TENANT.DELETE.REQUESTED",
                tenantId,
                principal,
                new Dictionary<string, object?>
                {
                    ["requestedAt"] = now,
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
        [FromServices] TimeProvider timeProvider,
        ClaimsPrincipal principal,
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

        // Optional operator note for the audit trail. Story 28-R2 / Finding
        // M17 — the raw header value flows into platform_events.data["note"]
        // and tenants.ProvisioningDetail; sanitise charset + clamp length
        // before persisting so a malicious operator can't inject control
        // characters / SSE-poisoning payloads / log-forging newlines.
        var note = http.Request.Headers["X-Admin-Note"].ToString();
        if (!string.IsNullOrEmpty(note))
        {
            var (sanitized, ok) = SanitizeAdminNote(note);
            if (!ok)
                return Results.Json(
                    new
                    {
                        error = "invalid_admin_note",
                        message = "X-Admin-Note must match [A-Za-z0-9 .,;:_!@#$%&()-]{0,500}.",
                    },
                    statusCode: StatusCodes.Status400BadRequest);
            note = sanitized;
        }

        await publisher.AppendAndPublishAsync(
            BuildAdminEvent(
                "TENANT.CLEANUP.REQUESTED",
                tenantId,
                principal,
                new Dictionary<string, object?>
                {
                    ["requestedAt"] = timeProvider.GetUtcNow().UtcDateTime,
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

    // ── Story 28-R2 / Finding M17 — X-Admin-Note charset gate ──

    /// <summary>
    /// Whitelist regex for the <c>X-Admin-Note</c> operator-note header.
    /// Allowed: ASCII letters/digits, space, and a small punctuation set
    /// safe to round-trip through JSON/event store/UI without injection.
    /// Rejected: control characters (incl. CR/LF — log forgery), HTML
    /// metacharacters (&lt; &gt; &quot;), and anything outside ASCII.
    /// 500-char cap matches the original soft clamp.
    /// </summary>
    private static readonly Regex AdminNoteRegex = new(
        @"^[A-Za-z0-9 .,;:_!@#$%&()\-]{0,500}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Returns <c>(sanitized, true)</c> when <paramref name="raw"/> matches
    /// the whitelist; <c>(_, false)</c> otherwise. Trims surrounding
    /// whitespace before validation so a leading/trailing space alone does
    /// not 400 (callers who send ` reason ` naturally still pass).
    /// </summary>
    internal static (string Sanitized, bool Ok) SanitizeAdminNote(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length > 500)
            return (trimmed[..500], false);
        return (trimmed, AdminNoteRegex.IsMatch(trimmed));
    }

    // ── PATCH /api/admin/tenants/{id}/plan ──

    public static async Task<IResult> UpdateTenantPlan(
        Guid tenantId,
        UpdateTenantPlanRequest req,
        ControlPlaneDbContext db,
        IPlatformEventPublisher publisher,
        [FromServices] TimeProvider timeProvider,
        ClaimsPrincipal principal,
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
        tenant.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);

        await publisher.AppendAndPublishAsync(
            BuildAdminEvent(
                "PLAN.UPDATED",
                tenantId,
                principal,
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

    // ── POST /api/admin/tenants/{id}/move ──

    /// <summary>
    /// Unified-tenancy Phase 4 — queue a tenant move to another
    /// <c>tenant_databases</c> pool row. Validation here is deliberately
    /// cheap (tenant exists, target row exists, target differs from the
    /// current placement); the deep checks (tenant 'active', target
    /// 'active' + tier-eligible + capacity, aliasing guard) run inside
    /// <see cref="ITenantMoveService.MoveAsync"/> when the platform-queue
    /// worker claims the task. Returns 202 + the
    /// <c>GET /api/admin/tenants/{id}/move</c> polling URL — the same
    /// 202-plus-status-poll shape the Cranl provisioning endpoints use.
    /// </summary>
    public static async Task<IResult> MoveTenant(
        Guid tenantId,
        MoveTenantRequest? req,
        ControlPlaneDbContext db,
        IPlatformQueuedTaskRepository platformTasks,
        IPlatformEventPublisher publisher,
        ClaimsPrincipal principal,
        CancellationToken ct = default)
    {
        if (req is null || req.TargetDatabaseId == Guid.Empty)
            return Results.BadRequest(new
            {
                error = "target_database_id_required",
                message = "Body must carry a non-empty targetDatabaseId.",
            });

        var tenant = await db.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == tenantId && t.DeletedAt == null, ct);
        if (tenant is null)
            return Results.NotFound(new { error = "tenant_not_found" });

        var targetExists = await db.TenantDatabases
            .AsNoTracking()
            .AnyAsync(d => d.Id == req.TargetDatabaseId, ct);
        if (!targetExists)
            return Results.NotFound(new { error = "target_database_not_found" });

        var currentDatabaseId =
            (Guid?)db.Entry(tenant).Property("DatabaseId").CurrentValue;
        if (currentDatabaseId == req.TargetDatabaseId)
            return Results.Json(
                new
                {
                    error = "already_on_target_database",
                    message = $"Tenant '{tenantId}' is already placed on database "
                        + $"'{req.TargetDatabaseId}' — moving a tenant onto its current "
                        + "pool row is a no-op.",
                },
                statusCode: StatusCodes.Status409Conflict);

        await platformTasks.EnqueueAsync(new PlatformQueuedTask
        {
            Type = MoveTenantTaskPayload.TaskType,
            TenantId = tenantId,
            Payload = JsonSerializer.Serialize(new MoveTenantTaskPayload
            {
                TenantId = tenantId,
                TargetDatabaseId = req.TargetDatabaseId,
            }),
        }, ct);

        await publisher.AppendAndPublishAsync(
            BuildAdminEvent(
                "TENANT.MOVE.REQUESTED",
                tenantId,
                principal,
                new Dictionary<string, object?>
                {
                    ["targetDatabaseId"] = req.TargetDatabaseId.ToString("D"),
                    ["sourceDatabaseId"] = currentDatabaseId?.ToString("D"),
                    ["source"] = "admin-move",
                }),
            ct);

        var statusUrl = $"/api/admin/tenants/{tenantId}/move";
        return Results.Accepted(
            statusUrl,
            new AdminTenantMoveAcceptedResponse(
                tenantId,
                req.TargetDatabaseId,
                (string?)db.Entry(tenant).Property("Status").CurrentValue,
                statusUrl,
                "Move queued — the tenant enters a brief read-only window "
                + "(status 'draining') while the schema is moved; poll the "
                + "status URL for progress."));
    }

    // ── GET /api/admin/tenants/{id}/move ──

    /// <summary>
    /// Unified-tenancy Phase 4 — move polling surface (the move analogue
    /// of <c>GET /api/admin/tenants/{id}/provisioning</c>). Reports the
    /// tenant's <c>Status</c> (<c>draining</c> while a move runs; back to
    /// <c>active</c> on completion), the last move error the queue
    /// handler stamped into <c>FailureReason</c>, and the current
    /// placement (<c>DatabaseId</c> flips to the target once the move's
    /// re-point commits).
    /// </summary>
    public static async Task<IResult> GetTenantMove(
        Guid tenantId,
        ControlPlaneDbContext db,
        CancellationToken ct = default)
    {
        var row = await db.Tenants
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(t => t.Id == tenantId && t.DeletedAt == null)
            .Select(t => new
            {
                Status = EF.Property<string?>(t, "Status"),
                FailureReason = EF.Property<string?>(t, "FailureReason"),
                DatabaseId = EF.Property<Guid?>(t, "DatabaseId"),
                SchemaName = EF.Property<string?>(t, "SchemaName"),
            })
            .FirstOrDefaultAsync(ct);
        if (row is null)
            return Results.NotFound(new { error = "tenant_not_found" });

        return Results.Ok(new AdminTenantMoveStatusResponse(
            tenantId,
            row.Status,
            row.FailureReason,
            row.DatabaseId,
            row.SchemaName));
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
                KekVersion = (int?)EF.Property<short>(t, "KekVersion"),
                FailureReason = EF.Property<string?>(t, "FailureReason"),
                DeleteRequestedAt = EF.Property<DateTime?>(t, "DeleteRequestedAt"),
                // Phase 4 — tenant→DB view (see ListTenants).
                DatabaseId = EF.Property<Guid?>(t, "DatabaseId"),
                SchemaName = EF.Property<string?>(t, "SchemaName"),
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
            row.EncryptedConn is not null && row.EncryptedConn.Length > 0,
            row.DatabaseId,
            row.SchemaName);
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

    /// <summary>
    /// Story 28-5 AC4 — cancel-delete is legal only while the tenant is
    /// still <c>deleting</c> (i.e. inside the cooling-off window before the
    /// trigger dispatches the destructive workflow). Once the workflow runs
    /// the status leaves <c>deleting</c> and the cancel is rejected with 409.
    /// </summary>
    private static bool IsCancelDeletable(string? status) =>
        string.Equals(status, StatusDeleting, StringComparison.OrdinalIgnoreCase);

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

    /// <summary>
    /// Story 28-R2 / Finding M2 — actor-bearing platform event constructor.
    /// Captures the JWT <c>sub</c> + <c>email</c> claims of the operator
    /// driving the action, both into <c>tags</c> (for SQL queries) and into
    /// <c>data</c> (defence-in-depth: tags can be projected/dropped, data
    /// is the immutable record). Without this, the existing audit trail
    /// only records the affected <c>tenantId</c> — there's no way to
    /// answer "which platform admin retried tenant X?" after the fact.
    ///
    /// <para>The principal is the request <see cref="ClaimsPrincipal"/>
    /// the minimal-API binding hands to the handler; tests inject a stub
    /// principal directly.</para>
    /// </summary>
    private static PlatformEvent BuildAdminEvent(
        string type,
        Guid tenantId,
        ClaimsPrincipal principal,
        IReadOnlyDictionary<string, object?>? data = null)
    {
        var actor = ExtractActor(principal);

        var tags = new Dictionary<string, string?>
        {
            ["tenantId"] = tenantId.ToString("D"),
            ["source"] = "admin",
        };
        if (!string.IsNullOrEmpty(actor.UserId))
            tags["actorUserId"] = actor.UserId;
        if (!string.IsNullOrEmpty(actor.Email))
            tags["actorEmail"] = actor.Email;
        if (!string.IsNullOrEmpty(actor.PlatformRole))
            tags["actorPlatformRole"] = actor.PlatformRole;

        // Defence-in-depth: also write into data. Tags get projected onto
        // the dashboard timeline + are easy to mass-update; data is the
        // immutable canonical record, so the actor identity must live
        // there too in case a future refactor drops or rewrites tags.
        var enriched = data is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(data);
        enriched["actorUserId"] = actor.UserId;
        enriched["actorEmail"] = actor.Email;
        enriched["actorPlatformRole"] = actor.PlatformRole;

        return new PlatformEvent
        {
            Type = type,
            TenantId = tenantId,
            Tags = JsonSerializer.Serialize(tags),
            Metadata = """{"workflowVersion":"1.0.0","eventSource":"system"}""",
            Data = JsonSerializer.Serialize(enriched),
        };
    }

    /// <summary>
    /// Lightweight projection of the operator identity captured from the
    /// JWT. <see cref="ExtractActor"/> tolerates a missing principal (e.g.
    /// permissive-dev test runs) by returning all-null fields — the event
    /// still gets persisted, it just lacks the actor breadcrumb. In
    /// production with the real <c>PlatformOwnerAccess</c> gate, the
    /// principal always carries <c>sub</c> + <c>email</c>.
    /// </summary>
    internal readonly record struct ActorIdentity(
        string? UserId,
        string? Email,
        string? PlatformRole);

    internal static ActorIdentity ExtractActor(ClaimsPrincipal? principal)
    {
        if (principal is null) return new ActorIdentity(null, null, null);

        // sub (mapped to NameClaimType when MapInboundClaims=false in
        // JwtService) carries the user GUID. Fallback to NameIdentifier
        // covers cookie + tests that mint identities under either name.
        var userId = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var email = principal.FindFirst(JwtRegisteredClaimNames.Email)?.Value
            ?? principal.FindFirst(ClaimTypes.Email)?.Value
            ?? principal.FindFirst("email")?.Value;
        var platformRole = principal.FindFirst("platformRole")?.Value;

        return new ActorIdentity(userId, email, platformRole);
    }
}
