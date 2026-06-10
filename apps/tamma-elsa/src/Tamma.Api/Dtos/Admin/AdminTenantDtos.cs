using Tamma.Api.Services.Analytics;

namespace Tamma.Api.Dtos.Admin;

/// <summary>
/// Story 28-11 — request + response contracts for the platform-admin
/// tenant-status UX. The UX surfaces every tenant's lifecycle row
/// (<c>tenants.Status</c> shadow column, plan, owner) and offers
/// state-gated actions: retry provisioning, initiate delete, force-delete,
/// change plan.
///
/// <para>These DTOs are consumed by <c>AdminTenantsEndpoints</c> and the
/// React dashboard at <c>/admin/tenants</c>. All fields are surface-safe:
/// the encrypted connection string is never serialised — only its
/// presence is reported via <see cref="AdminTenantDetailResponse.HasEncryptedConnectionString"/>.</para>
/// </summary>
public record AdminTenantListItem(
    Guid Id,
    string Name,
    string Slug,
    string Type,
    /// <summary>
    /// Tenant lifecycle status from the shadow column
    /// (<c>pending_verification</c> | <c>provisioning</c> | <c>active</c>
    /// | <c>failed</c> | <c>deleting</c> | <c>deleted</c>). Null when the
    /// row predates Epic 28 shadow columns — legacy rows fall back to
    /// <c>"active"</c> in the UI layer.
    /// </summary>
    string? Status,
    /// <summary>Legacy plan string (<c>free</c>/<c>team</c>/<c>enterprise</c>).</summary>
    string LegacyPlan,
    /// <summary>Plan display name resolved from PlanId FK; null when PlanId unset.</summary>
    string? PlanName,
    string? PlanSlug,
    Guid? PlanId,
    Guid? OwnerId,
    string? OwnerEmail,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    /// <summary>Populated when <c>Status='failed'</c>; else null.</summary>
    string? FailureReason,
    /// <summary>Populated when <c>Status='deleting'</c>; else null.</summary>
    DateTime? DeleteRequestedAt,
    int? KekVersion,
    bool HasEncryptedConnectionString,
    /// <summary>
    /// Unified-tenancy Phase 4 — which <c>tenant_databases</c> pool row
    /// hosts this tenant's schema (shadow column; null until placement).
    /// </summary>
    Guid? DatabaseId = null,
    /// <summary>
    /// Unified-tenancy Phase 4 — the tenant's <c>t_&lt;hex&gt;</c> schema
    /// inside its assigned database (shadow column; null until placement).
    /// </summary>
    string? SchemaName = null);

/// <summary>
/// Paged list envelope. <see cref="Total"/> reflects the full filter-matched
/// count (before pagination) so the UI can render page controls.
/// </summary>
public record AdminTenantListResponse(
    IReadOnlyList<AdminTenantListItem> Tenants,
    int Total,
    int Page,
    int PageSize);

/// <summary>
/// Detail response — everything the dashboard needs to build the
/// workflow-ladder, events timeline, and destructive-action UI for a
/// single tenant.
/// </summary>
public record AdminTenantDetailResponse(
    AdminTenantListItem Tenant,
    IReadOnlyList<AdminTenantEventItem> RecentEvents,
    /// <summary>
    /// Action gating — mirrors the state-machine transitions. Each flag
    /// reflects whether the action is legal against the current
    /// <see cref="AdminTenantListItem.Status"/>.
    /// </summary>
    AdminTenantActionGate Actions,
    /// <summary>
    /// Story 28-11 AC2 — the tenant's last-24h resource rollup, aggregated
    /// from <c>platform_analytics_hourly</c>. Always present (never null): a
    /// freshly provisioned tenant with no rows yet carries
    /// <see cref="TenantResourceSummary.Empty"/> (all zeros).
    /// </summary>
    TenantResourceSummary ResourceSummary);

public record AdminTenantEventItem(
    Guid Id,
    string Type,
    DateTime CreatedAt,
    string Tags,
    string Data);

/// <summary>
/// Pre-computed server-side state gate. The dashboard can (and should)
/// also render its own disabled state, but the server authoritative
/// truth lives here so a 409 on action POST is never a surprise.
/// </summary>
public record AdminTenantActionGate(
    bool CanRetry,
    bool CanDelete,
    bool CanForceDelete,
    bool CanChangePlan);

/// <summary>
/// Request body for <c>PATCH /api/admin/tenants/{id}/plan</c>. PlanId must
/// resolve to an <c>IsActive</c> row in <c>plans</c>.
/// </summary>
public record UpdateTenantPlanRequest(Guid PlanId);

/// <summary>
/// Minimal response the action POST handlers return (retry / delete /
/// force-delete / change-plan). <see cref="Status"/> is the new Status
/// value after the action applies; <see cref="Message"/> is a
/// human-friendly note for the UI toast.
/// </summary>
public record AdminTenantActionResponse(
    Guid TenantId,
    string Status,
    string Message);

/// <summary>
/// Request body for <c>POST /api/admin/tenants/{tenantId}/move</c>
/// (unified-tenancy Phase 4). <see cref="TargetDatabaseId"/> must be an
/// existing <c>tenant_databases</c> pool row that differs from the
/// tenant's current placement.
/// </summary>
public record MoveTenantRequest(Guid TargetDatabaseId);

/// <summary>
/// 202 body for <c>POST /api/admin/tenants/{tenantId}/move</c>. The move
/// runs out-of-band on the platform task queue (mirroring the Cranl
/// provisioning shape); <see cref="StatusUrl"/> is the polling endpoint.
/// </summary>
public record AdminTenantMoveAcceptedResponse(
    Guid TenantId,
    Guid TargetDatabaseId,
    /// <summary>Tenant Status at enqueue time (the move flips it to
    /// <c>draining</c> once the queued task starts).</summary>
    string? Status,
    string StatusUrl,
    string Message);

/// <summary>
/// Response for <c>GET /api/admin/tenants/{tenantId}/move</c> — the move
/// polling surface. <see cref="Status"/> is <c>tenants.Status</c>
/// (<c>draining</c> while the move runs; back to <c>active</c> on
/// completion); <see cref="FailureReason"/> carries the last move error
/// the queue handler recorded (null when none / after a successful
/// retry); <see cref="DatabaseId"/> + <see cref="SchemaName"/> show the
/// current placement (the DatabaseId flips to the target once the move's
/// re-point commits).
/// </summary>
public record AdminTenantMoveStatusResponse(
    Guid TenantId,
    string? Status,
    string? FailureReason,
    Guid? DatabaseId,
    string? SchemaName);
