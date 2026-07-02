using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tamma.Api.Services.PromptStore;
using Tamma.Core;
using Tamma.Core.Enums;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Pricing;

/// <summary>
/// Story 34-6 — core <see cref="IEntitlementService"/>: resolve a principal to
/// the complete, closed <see cref="ResolvedEntitlements"/> map (cache-first) and
/// compute non-enforcing headroom. Read-only; fails loud on a missing
/// assignment (never an empty/plain fallback — mirrors the prompt/convention
/// contract, <c>feedback_resolution_no_empty_fallback</c>).
///
/// <para>Resolution: principal → tenantId (per-mode) → cache → active pinned
/// assignment (<see cref="IActivePlanAssignmentSource"/>) → catalog snapshot
/// (<see cref="IPlanCatalogService.GetByIdAsync"/>) → closed 7-key map (missing
/// rows backfill the documented default) → cache + emit
/// <c>ENTITLEMENT.RESOLVED.SUCCESS</c> on the miss.</para>
/// </summary>
public sealed class EntitlementService : IEntitlementService
{
    private readonly IActivePlanAssignmentSource _assignments;
    private readonly IPlanCatalogService _catalog;
    private readonly IEntitlementSnapshotCache _cache;
    private readonly ITammaModeProvider _mode;
    private readonly IUserRepository _users;
    private readonly IPlatformEventPublisher _events;
    private readonly ILogger<EntitlementService> _logger;

    public EntitlementService(
        IActivePlanAssignmentSource assignments,
        IPlanCatalogService catalog,
        IEntitlementSnapshotCache cache,
        ITammaModeProvider mode,
        IUserRepository users,
        IPlatformEventPublisher events,
        ILogger<EntitlementService> logger)
    {
        _assignments = assignments ?? throw new ArgumentNullException(nameof(assignments));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _mode = mode ?? throw new ArgumentNullException(nameof(mode));
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ResolvedEntitlements> ResolveAsync(
        EntitlementPrincipal principal, CancellationToken ct = default)
    {
        var tenantId = await ResolvePrincipalTenantAsync(principal, ct);

        // 2. Cache-first — a hit skips the assignment + catalog reads and emits
        //    no event.
        var cached = _cache.TryGet(tenantId);
        if (cached is not null)
        {
            _logger.LogDebug("Entitlements cache hit for tenant {TenantId}", tenantId);
            return cached;
        }

        // 3. Active pinned assignment. Null ⇒ NO_ASSIGNMENT fail-loud.
        var assignment = await _assignments.GetActiveAsync(tenantId, ct);
        if (assignment is null)
        {
            await EmitFailedAsync(tenantId, reason: "no_assignment", ct);
            _logger.LogError(
                "ENTITLEMENT.RESOLVE.NO_ASSIGNMENT — tenant {TenantId} has no active plan assignment",
                tenantId);
            throw new TammaError(
                "ENTITLEMENT.RESOLVE.NO_ASSIGNMENT",
                $"Tenant '{tenantId}' has no active plan assignment.",
                new Dictionary<string, object?>
                {
                    ["tenantId"] = tenantId.ToString(),
                    ["mode"] = _mode.Mode.ToString(),
                },
                retryable: false,
                severity: TammaErrorSeverity.High);
        }

        // 4. Pinned catalog snapshot (immutable for deprecated versions, so a
        //    later deprecation cannot retro-mutate). Null ⇒ CATALOG_UNAVAILABLE.
        var snapshot = await _catalog.GetByIdAsync(assignment.PlanId, ct);
        if (snapshot is null)
        {
            await EmitFailedAsync(tenantId, reason: "catalog_unavailable", ct);
            _logger.LogError(
                "ENTITLEMENT.RESOLVE.CATALOG_UNAVAILABLE — tenant {TenantId} pinned plan {PlanId} has no catalog snapshot",
                tenantId, assignment.PlanId);
            throw new TammaError(
                "ENTITLEMENT.RESOLVE.CATALOG_UNAVAILABLE",
                $"Pinned plan '{assignment.PlanId}' for tenant '{tenantId}' has no catalog snapshot.",
                new Dictionary<string, object?>
                {
                    ["tenantId"] = tenantId.ToString(),
                    ["planId"] = assignment.PlanId.ToString(),
                    ["mode"] = _mode.Mode.ToString(),
                },
                retryable: false,
                severity: TammaErrorSeverity.High);
        }

        // 5. Build the complete, closed map (missing rows backfill the default).
        var limits = EntitlementDefaults.BuildClosedMap(
            snapshot.Entitlements,
            onBackfill: metric => _logger.LogWarning(
                "Entitlement backfill: tenant {TenantId} plan {PlanId} v{Version} missing metric {Metric} → default (limit {Limit}, {Period}, {Overage})",
                tenantId, snapshot.PlanId, snapshot.Version, metric.ToMetricString(),
                EntitlementDefaults.DefaultLimit, EntitlementDefaults.DefaultPeriod,
                EntitlementDefaults.DefaultOverageMode));

        var resolved = new ResolvedEntitlements(
            tenantId, snapshot.PlanId, snapshot.Version, snapshot.IsCustom, limits);

        // 6. Cache + emit success (cache-miss only).
        _cache.Set(tenantId, resolved);
        await EmitSuccessAsync(resolved, source: "cache-miss", ct);

        _logger.LogInformation(
            "Entitlements resolved (cache miss): tenant {TenantId} plan {PlanId} v{Version} (custom={IsCustom}) mode {Mode}",
            tenantId, snapshot.PlanId, snapshot.Version, snapshot.IsCustom, _mode.Mode);

        return resolved;
    }

    public EntitlementHeadroom CheckHeadroom(
        ResolvedEntitlements resolved, EntitlementMetricKey metric, long currentUsage)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        var line = resolved.Get(metric);
        return EntitlementDefaults.ComputeHeadroom(metric, line.LimitValue, currentUsage);
    }

    /// <summary>
    /// Per-mode principal → tenantId. SaaS keys by tenant id; single-user keys
    /// by the sole user → their personal/active tenant (<c>User.TenantId</c>).
    /// A user with no active tenant is a <c>NO_ASSIGNMENT</c> case (the
    /// personal-tenant invariant should prevent it).
    /// </summary>
    private async Task<Guid> ResolvePrincipalTenantAsync(
        EntitlementPrincipal principal, CancellationToken ct)
    {
        if (principal.TenantId is Guid tenantId)
        {
            return tenantId;
        }

        if (principal.UserId is Guid userId)
        {
            var user = await _users.GetByIdAsync(userId);
            if (user?.TenantId is Guid personalTenant)
            {
                return personalTenant;
            }

            await EmitFailedAsync(tenantId: null, reason: "no_assignment", ct);
            _logger.LogError(
                "ENTITLEMENT.RESOLVE.NO_ASSIGNMENT — user {UserId} has no active/personal tenant",
                userId);
            throw new TammaError(
                "ENTITLEMENT.RESOLVE.NO_ASSIGNMENT",
                $"User '{userId}' has no active tenant to resolve entitlements for.",
                new Dictionary<string, object?>
                {
                    ["userId"] = userId.ToString(),
                    ["mode"] = _mode.Mode.ToString(),
                },
                retryable: false,
                severity: TammaErrorSeverity.High);
        }

        // Neither id set — a malformed principal (defensive; the factory
        // methods always set exactly one).
        throw new TammaError(
            "ENTITLEMENT.RESOLVE.NO_PRINCIPAL",
            "EntitlementPrincipal has neither a tenant id nor a user id.",
            new Dictionary<string, object?> { ["mode"] = _mode.Mode.ToString() },
            retryable: false,
            severity: TammaErrorSeverity.High);
    }

    private Task EmitSuccessAsync(ResolvedEntitlements resolved, string source, CancellationToken ct)
    {
        var tags = new Dictionary<string, string?>
        {
            ["tenantId"] = resolved.TenantId.ToString(),
            ["planId"] = resolved.PlanId.ToString(),
            ["planVersion"] = resolved.PlanVersion.ToString(),
            ["mode"] = _mode.Mode.ToString(),
            ["source"] = source,
        };
        return EmitAsync(EntitlementEventTypes.ResolvedSuccess, resolved.TenantId, tags, ct);
    }

    private Task EmitFailedAsync(Guid? tenantId, string reason, CancellationToken ct)
    {
        var tags = new Dictionary<string, string?>
        {
            ["tenantId"] = tenantId?.ToString(),
            ["mode"] = _mode.Mode.ToString(),
            ["reason"] = reason,
        };
        return EmitAsync(EntitlementEventTypes.ResolvedFailed, tenantId, tags, ct);
    }

    /// <summary>
    /// Emit an entitlement DCB event to the CP <c>platform_events</c> store +
    /// the in-process bus (same home as the catalog events; matches
    /// <c>PlanVersionEditor</c>). Best-effort: an emission failure is
    /// WARN-logged, never thrown back into the resolve path.
    /// </summary>
    private async Task EmitAsync(
        string type, Guid? tenantId, Dictionary<string, string?> tags, CancellationToken ct)
    {
        var data = new Dictionary<string, object?>(
            tags.ToDictionary(kv => kv.Key, kv => (object?)kv.Value));

        var evt = new PlatformEvent
        {
            Type = type,
            TenantId = tenantId,
            Tags = JsonSerializer.Serialize(tags),
            Metadata = """{"workflowVersion":"1.0.0","eventSource":"system"}""",
            Data = JsonSerializer.Serialize(data),
        };

        try
        {
            await _events.AppendAndPublishAsync(evt, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Failed to emit entitlement event {Type} for tenant {TenantId}",
                type, tenantId);
        }
    }
}
