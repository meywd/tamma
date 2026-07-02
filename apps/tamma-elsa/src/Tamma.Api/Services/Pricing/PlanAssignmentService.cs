using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tamma.Api.Services.Provisioning;
using Tamma.Api.Services.PromptStore;
using Tamma.Core;
using Tamma.Core.Enums;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Pricing;

/// <summary>
/// Story 34-4 — the version-pinned, audited plan-assignment service. See
/// <see cref="IPlanAssignmentService"/>. Writes are transactional (one active
/// row per tenant), pin the plan <c>Version</c>, keep the lockstep
/// <c>Tenant.PlanId</c>/<c>Tenant.Plan</c> cache columns aligned, and emit a
/// <c>TENANT.PLAN.CHANGED</c>/<c>.CANCELLED</c> platform event (which is what
/// evicts Story 34-6's cached entitlement snapshot for the tenant).
/// </summary>
public sealed class PlanAssignmentService : IPlanAssignmentService
{
    private const string FreePlanSlug = "free";
    private const string PlatformProvidedPricingMode = "platform_provided";

    // The legacy Tenant.Plan string column is constrained by the pre-existing
    // ck_tenants_plan CHECK (Plan IN ('free','team','enterprise')). It is only a
    // best-effort cache for old dashboards — the source of truth is the
    // assignment row + the Tenant.PlanId shadow FK (which accepts ANY plan,
    // incl. custom/deprecated). So the legacy slug is written ONLY for a
    // canonical slug; a custom/non-canonical plan updates PlanId + the assignment
    // row and leaves the (stale-but-valid) legacy string untouched.
    private static readonly IReadOnlySet<string> CanonicalPlanSlugs =
        new HashSet<string>(StringComparer.Ordinal) { "free", "team", "enterprise" };

    private static bool IsCanonicalPlanSlug(string slug) => CanonicalPlanSlugs.Contains(slug);

    private readonly ControlPlaneDbContext _db;
    private readonly IPlanCatalogService _catalog;
    private readonly ITenantUsageReader _usageReader;
    private readonly IPlatformEventPublisher _publisher;
    private readonly IPlatformQueuedTaskRepository _platformTasks;
    private readonly ITammaModeProvider _mode;
    private readonly TimeProvider _time;
    private readonly ILogger<PlanAssignmentService> _logger;

    public PlanAssignmentService(
        ControlPlaneDbContext db,
        IPlanCatalogService catalog,
        ITenantUsageReader usageReader,
        IPlatformEventPublisher publisher,
        IPlatformQueuedTaskRepository platformTasks,
        ITammaModeProvider mode,
        TimeProvider time,
        ILogger<PlanAssignmentService> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _usageReader = usageReader ?? throw new ArgumentNullException(nameof(usageReader));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _platformTasks = platformTasks ?? throw new ArgumentNullException(nameof(platformTasks));
        _mode = mode ?? throw new ArgumentNullException(nameof(mode));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<TenantPlanAssignment?> GetActiveAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        // Scalar comparison against the Active constant — no collection is
        // referenced in the predicate (C#13 EF span-overload trap avoided).
        return await _db.TenantPlanAssignments
            .Where(a => a.TenantId == tenantId && a.Status == PlanAssignmentStatus.Active)
            .OrderByDescending(a => a.EffectiveFrom)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<PlanAssignmentResult> AssignAsync(
        Guid tenantId, Guid planId, AssignPlanOptions opts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(opts);

        var snapshot = await _catalog.GetByIdAsync(planId, ct).ConfigureAwait(false);
        if (snapshot is null)
        {
            throw new TammaError(
                "PLAN.ASSIGN.PLAN_NOT_FOUND",
                $"Plan '{planId:D}' does not exist — cannot assign.",
                new Dictionary<string, object?> { ["planId"] = planId.ToString("D") },
                retryable: false, severity: TammaErrorSeverity.Medium);
        }

        GuardAssignable(tenantId, snapshot, opts.Force);

        var tenant = await LoadTenantAsync(tenantId, ct).ConfigureAwait(false);

        var current = await GetActiveAsync(tenantId, ct).ConfigureAwait(false);

        // Idempotent PUT — re-assigning the exact same (PlanId, PlanVersion) is a
        // lateral no-op: no swap, no event. It must STILL reconcile away any
        // pending `scheduled` downgrade (a prior period-end cancel): re-affirming
        // the current plan means the tenant no longer intends to be downgraded at
        // the boundary. Without this a "keep my plan" PUT would silently drop the
        // tenant to free when the scheduled row later activates.
        if (current is not null
            && current.PlanId == planId
            && current.PlanVersion == snapshot.Version)
        {
            var voided = await VoidPendingScheduledAsync(
                    tenantId, _time.GetUtcNow().UtcDateTime, saveInOwnTransaction: true, ct)
                .ConfigureAwait(false);
            if (voided > 0)
            {
                _logger.LogInformation(
                    "Plan re-affirm voided {Count} pending scheduled downgrade(s) for tenant {TenantId} (plan {PlanId} v{Version})",
                    voided, tenantId, planId, snapshot.Version);
            }
            _logger.LogDebug(
                "Plan assign no-op: tenant {TenantId} already on plan {PlanId} v{Version}",
                tenantId, planId, snapshot.Version);
            return new PlanAssignmentResult(
                current, PlanChangeDirection.Lateral,
                Array.Empty<EntitlementWarning>(), null);
        }

        var direction = await ClassifyDirectionAsync(current, snapshot, ct).ConfigureAwait(false);
        var warnings = await ComputeDowngradeWarningsAsync(tenantId, direction, snapshot, ct)
            .ConfigureAwait(false);

        var now = _time.GetUtcNow().UtcDateTime;
        var newRow = await SwapActiveAsync(
            tenant, tenantId, current, snapshot, now, opts.ActorUserId, opts.Reason, ct)
            .ConfigureAwait(false);

        await EmitPlanChangedAsync(
            tenantId, current, snapshot, direction, warnings,
            opts.ActorUserId, opts.ActorEmail, opts.ActorPlatformRole, opts.Source,
            prorationBoundaryAt: now, billingIntervalAnchor: newRow.EffectiveFrom, ct)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Plan assigned: tenant {TenantId} {OldPlan}→{NewPlan} v{Version} direction={Direction} source={Source} warnings={Warnings}",
            tenantId, current?.PlanId, planId, snapshot.Version, direction, opts.Source, warnings.Count);

        return new PlanAssignmentResult(newRow, direction, warnings, null);
    }

    public async Task<PlanAssignmentResult> CancelAsync(
        Guid tenantId, CancelPlanOptions opts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(opts);

        var free = await _catalog.GetActiveBySlugAsync(FreePlanSlug, ct).ConfigureAwait(false);
        if (free is null)
        {
            throw new TammaError(
                "PLAN.CANCEL.NO_FREE_PLAN",
                "No active 'free' plan version exists to cancel down to — the catalog is misconfigured.",
                retryable: false, severity: TammaErrorSeverity.High);
        }

        var tenant = await LoadTenantAsync(tenantId, ct).ConfigureAwait(false);
        var current = await GetActiveAsync(tenantId, ct).ConfigureAwait(false);
        if (current is null)
        {
            throw new TammaError(
                "PLAN.CANCEL.NO_ACTIVE_ASSIGNMENT",
                $"Tenant '{tenantId:D}' has no active plan assignment to cancel.",
                new Dictionary<string, object?> { ["tenantId"] = tenantId.ToString("D") },
                retryable: false, severity: TammaErrorSeverity.Medium);
        }

        // Already on free → nothing to cancel (idempotent no-op).
        if (current.PlanId == free.PlanId)
        {
            _logger.LogDebug("Cancel no-op: tenant {TenantId} already on free plan", tenantId);
            return new PlanAssignmentResult(
                current, PlanChangeDirection.Lateral, Array.Empty<EntitlementWarning>(), null);
        }

        var now = _time.GetUtcNow().UtcDateTime;

        if (opts.Immediate)
        {
            // Drop to free NOW — a transactional swap identical to an assign.
            var newRow = await SwapActiveAsync(
                tenant, tenantId, current, free, now, opts.ActorUserId, opts.Reason, ct)
                .ConfigureAwait(false);

            await EmitCancelledAsync(
                tenantId, current.PlanId, effectiveAt: now, immediate: true,
                opts.ActorUserId, opts.ActorEmail, opts.ActorPlatformRole, ct)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Plan cancel (immediate): tenant {TenantId} {OldPlan}→free at {At}",
                tenantId, current.PlanId, now);

            return new PlanAssignmentResult(
                newRow, PlanChangeDirection.Downgrade, Array.Empty<EntitlementWarning>(), now);
        }

        // Period-end cancel — stamp the boundary on the current active row (it
        // stays active until activation) and queue a scheduled free row.
        var boundary = await ComputePeriodEndAsync(current, now, ct).ConfigureAwait(false);

        TenantPlanAssignment scheduled;
        await using (var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false))
        {
            current.EffectiveTo = boundary;
            current.UpdatedAt = now;

            scheduled = new TenantPlanAssignment
            {
                TenantId = tenantId,
                PlanId = free.PlanId,
                PlanVersion = free.Version,
                Status = PlanAssignmentStatus.Scheduled,
                EffectiveFrom = boundary,
                AssignedByUserId = opts.ActorUserId,
                Reason = opts.Reason ?? "cancel: scheduled downgrade to free at period end",
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.TenantPlanAssignments.Add(scheduled);

            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        // Enqueue the boundary-activation task AFTER commit (the platform-queue
        // repository runs on its own context/connection). VisibleAt defers the
        // reservation until the boundary; the handler is idempotent by
        // AssignmentId, so a duplicate enqueue is harmless.
        await EnqueueActivationAsync(tenantId, scheduled.Id, boundary, ct).ConfigureAwait(false);

        await EmitCancelledAsync(
            tenantId, current.PlanId, effectiveAt: boundary, immediate: false,
            opts.ActorUserId, opts.ActorEmail, opts.ActorPlatformRole, ct)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Plan cancel scheduled: tenant {TenantId} {OldPlan}→free effectiveAt={At}",
            tenantId, current.PlanId, boundary);

        return new PlanAssignmentResult(
            scheduled, PlanChangeDirection.Downgrade, Array.Empty<EntitlementWarning>(), boundary);
    }

    public async Task<PlanAssignmentResult?> ActivateScheduledAsync(
        Guid tenantId, Guid assignmentId, CancellationToken ct = default)
    {
        var scheduled = await _db.TenantPlanAssignments
            .FirstOrDefaultAsync(a => a.Id == assignmentId && a.TenantId == tenantId, ct)
            .ConfigureAwait(false);
        if (scheduled is null)
        {
            _logger.LogDebug(
                "Activate no-op: scheduled assignment {AssignmentId} for tenant {TenantId} not found",
                assignmentId, tenantId);
            return null;
        }

        // Idempotent — already promoted (or superseded). A re-run is a no-op.
        if (scheduled.Status == PlanAssignmentStatus.Active)
        {
            return new PlanAssignmentResult(
                scheduled, PlanChangeDirection.Lateral, Array.Empty<EntitlementWarning>(), null);
        }
        if (scheduled.Status != PlanAssignmentStatus.Scheduled)
        {
            _logger.LogDebug(
                "Activate no-op: assignment {AssignmentId} is '{Status}', not scheduled",
                assignmentId, scheduled.Status);
            return null;
        }

        var snapshot = await _catalog.GetByIdAsync(scheduled.PlanId, ct).ConfigureAwait(false);
        var tenant = await LoadTenantAsync(tenantId, ct).ConfigureAwait(false);
        var current = await GetActiveAsync(tenantId, ct).ConfigureAwait(false);

        var now = _time.GetUtcNow().UtcDateTime;

        // Reconciliation guard (defence-in-depth alongside the void-on-reassign in
        // SwapActiveAsync): only promote this scheduled downgrade if the CURRENT
        // active row is still the one that scheduled it — i.e. its EffectiveTo
        // boundary equals this scheduled row's EffectiveFrom. A later re-assign
        // replaces that active row with one whose EffectiveTo is null (or a
        // different boundary), so the downgrade is no longer intended: void the
        // stale scheduled row and no-op rather than reverting the tenant. When
        // there is NO active row we still promote (never leave the tenant with no
        // active plan).
        if (current is not null
            && current.Id != scheduled.Id
            && current.EffectiveTo != scheduled.EffectiveFrom)
        {
            scheduled.Status = PlanAssignmentStatus.Cancelled;
            // Leave EffectiveTo null (see VoidPendingScheduledAsync): a scheduled
            // row voided here never activated, and `now` may precede its future
            // EffectiveFrom (ck_tpa_effective_window).
            scheduled.UpdatedAt = now;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Scheduled activation superseded: tenant {TenantId} assignment {AssignmentId} voided "
                + "— a newer active plan {CurrentPlanId} replaced the scheduled downgrade to {ScheduledPlanId}",
                tenantId, assignmentId, current.PlanId, scheduled.PlanId);
            return null;
        }

        var direction = await ClassifyDirectionAsync(current, snapshot, ct).ConfigureAwait(false);

        await using (var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false))
        {
            if (current is not null && current.Id != scheduled.Id)
            {
                current.Status = PlanAssignmentStatus.Cancelled;
                current.EffectiveTo ??= now;
                current.UpdatedAt = now;
                // Flip the expiring active row FIRST so the partial unique index
                // never sees two active rows for the tenant.
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            scheduled.Status = PlanAssignmentStatus.Active;
            scheduled.EffectiveTo = null;
            scheduled.UpdatedAt = now;

            _db.Entry(tenant).Property("PlanId").CurrentValue = scheduled.PlanId;
            if (snapshot is not null && IsCanonicalPlanSlug(snapshot.Slug))
                tenant.Plan = snapshot.Slug;
            tenant.UpdatedAt = now;

            try
            {
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                throw ConcurrentAssignError(tenantId, ex);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        await EmitPlanChangedAsync(
            tenantId, current, snapshot, direction, Array.Empty<EntitlementWarning>(),
            actorUserId: scheduled.AssignedByUserId, actorEmail: null, actorPlatformRole: null,
            source: "scheduled-activation",
            prorationBoundaryAt: now, billingIntervalAnchor: scheduled.EffectiveFrom, ct)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Scheduled plan activated: tenant {TenantId} assignment {AssignmentId} plan {PlanId} v{Version}",
            tenantId, assignmentId, scheduled.PlanId, scheduled.PlanVersion);

        return new PlanAssignmentResult(
            scheduled, direction, Array.Empty<EntitlementWarning>(), null);
    }

    // ── Internals ────────────────────────────────────────────────────────

    /// <summary>Guards: draft (always), deprecated (unless force), custom binding.</summary>
    private static void GuardAssignable(Guid tenantId, PlanSnapshot snapshot, bool force)
    {
        if (string.Equals(snapshot.Status, "draft", StringComparison.Ordinal))
        {
            throw new TammaError(
                "PLAN.ASSIGN.PLAN_DRAFT",
                $"Plan '{snapshot.Slug}' v{snapshot.Version} is a draft and cannot be assigned.",
                DiagContext(tenantId, snapshot),
                retryable: false, severity: TammaErrorSeverity.Medium);
        }

        if (string.Equals(snapshot.Status, "deprecated", StringComparison.Ordinal) && !force)
        {
            throw new TammaError(
                "PLAN.ASSIGN.PLAN_DEPRECATED",
                $"Plan '{snapshot.Slug}' v{snapshot.Version} is deprecated — assign requires force=true.",
                DiagContext(tenantId, snapshot),
                retryable: false, severity: TammaErrorSeverity.Medium);
        }

        if (snapshot.IsCustom && !CustomPlanSlug.IsBoundTo(snapshot.Slug, tenantId))
        {
            throw new TammaError(
                "PLAN.ASSIGN.CUSTOM_PLAN_MISBOUND",
                $"Custom plan '{snapshot.Slug}' is bound to another tenant and cannot be assigned to '{tenantId:D}'.",
                DiagContext(tenantId, snapshot),
                retryable: false, severity: TammaErrorSeverity.High);
        }
    }

    private static Dictionary<string, object?> DiagContext(Guid tenantId, PlanSnapshot snapshot) =>
        new()
        {
            ["tenantId"] = tenantId.ToString("D"),
            ["planId"] = snapshot.PlanId.ToString("D"),
            ["slug"] = snapshot.Slug,
            ["version"] = snapshot.Version,
            ["status"] = snapshot.Status,
        };

    private async Task<Tenant> LoadTenantAsync(Guid tenantId, CancellationToken ct)
    {
        var tenant = await _db.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == tenantId && t.DeletedAt == null, ct)
            .ConfigureAwait(false);
        if (tenant is null)
        {
            throw new TammaError(
                "PLAN.ASSIGN.TENANT_NOT_FOUND",
                $"Tenant '{tenantId:D}' does not exist.",
                new Dictionary<string, object?> { ["tenantId"] = tenantId.ToString("D") },
                retryable: false, severity: TammaErrorSeverity.Medium);
        }
        return tenant;
    }

    /// <summary>
    /// Transactional swap: flip the prior active row → cancelled (stamp
    /// EffectiveTo), insert the new active row, and update the lockstep tenant
    /// columns. The flip is committed in its own SaveChanges FIRST so the partial
    /// unique index never observes two active rows.
    /// </summary>
    private async Task<TenantPlanAssignment> SwapActiveAsync(
        Tenant tenant, Guid tenantId, TenantPlanAssignment? current, PlanSnapshot snapshot,
        DateTime now, Guid? actorUserId, string? reason, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        // Reconcile away any pending `scheduled` downgrade for the tenant BEFORE
        // inserting the new active row — in the SAME transaction. A new active
        // assignment (or an immediate cancel) supersedes a prior period-end
        // cancel's scheduled row; leaving it live would let ActivateScheduledAsync
        // later cancel this new active row and silently revert the tenant. These
        // rows are `scheduled` (not `active`), so voiding them never trips the
        // one-active-per-tenant partial unique index. Persisted by the SaveChanges
        // below (or the current-flip SaveChanges when there is a prior active row).
        await VoidPendingScheduledAsync(tenantId, now, saveInOwnTransaction: false, ct)
            .ConfigureAwait(false);

        if (current is not null)
        {
            current.Status = PlanAssignmentStatus.Cancelled;
            current.EffectiveTo = now;
            current.UpdatedAt = now;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        var newRow = new TenantPlanAssignment
        {
            TenantId = tenantId,
            PlanId = snapshot.PlanId,
            PlanVersion = snapshot.Version,
            Status = PlanAssignmentStatus.Active,
            EffectiveFrom = now,
            AssignedByUserId = actorUserId,
            Reason = reason,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.TenantPlanAssignments.Add(newRow);

        _db.Entry(tenant).Property("PlanId").CurrentValue = snapshot.PlanId;
        if (IsCanonicalPlanSlug(snapshot.Slug)) tenant.Plan = snapshot.Slug;
        tenant.UpdatedAt = now;

        try
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw ConcurrentAssignError(tenantId, ex);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return newRow;
    }

    /// <summary>
    /// Void (→ <c>cancelled</c>) every pending <c>scheduled</c> assignment for the
    /// tenant. A new/re-affirmed active plan supersedes a prior period-end cancel's
    /// scheduled downgrade; leaving it live lets <see cref="ActivateScheduledAsync"/>
    /// later revert the tenant to <c>free</c>. Returns the count voided. By default
    /// it does NOT open a transaction/SaveChanges — the caller
    /// (<see cref="SwapActiveAsync"/>) persists it inside its own transaction so the
    /// void and the swap are atomic. When <paramref name="saveInOwnTransaction"/> is
    /// set (the idempotent re-affirm path, which has no surrounding transaction) it
    /// commits its own transaction, and only when there is something to void.
    /// </summary>
    private async Task<int> VoidPendingScheduledAsync(
        Guid tenantId, DateTime now, bool saveInOwnTransaction, CancellationToken ct)
    {
        var pending = await _db.TenantPlanAssignments
            .Where(a => a.TenantId == tenantId && a.Status == PlanAssignmentStatus.Scheduled)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (pending.Count == 0) return 0;

        foreach (var row in pending)
        {
            row.Status = PlanAssignmentStatus.Cancelled;
            // Leave EffectiveTo null: a scheduled row voided before its boundary
            // never had an active window. Stamping `now` here would violate
            // ck_tpa_effective_window (EffectiveTo >= EffectiveFrom) because the
            // row's EffectiveFrom is the future boundary.
            row.UpdatedAt = now;
        }

        if (saveInOwnTransaction)
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        return pending.Count;
    }

    private async Task<PlanChangeDirection> ClassifyDirectionAsync(
        TenantPlanAssignment? current, PlanSnapshot? newSnapshot, CancellationToken ct)
    {
        if (newSnapshot is null) return PlanChangeDirection.Lateral;

        var newPrice = RecurringUsdOf(newSnapshot);

        if (current is null)
        {
            // First-ever assignment: a paid plan is an upgrade, free is lateral.
            return newPrice > 0m ? PlanChangeDirection.Upgrade : PlanChangeDirection.Lateral;
        }

        var oldSnapshot = await _catalog.GetByIdAsync(current.PlanId, ct).ConfigureAwait(false);
        var oldPrice = oldSnapshot is null ? 0m : RecurringUsdOf(oldSnapshot);

        if (newPrice > oldPrice) return PlanChangeDirection.Upgrade;
        if (newPrice < oldPrice) return PlanChangeDirection.Downgrade;
        return PlanChangeDirection.Lateral;
    }

    private static decimal RecurringUsdOf(PlanSnapshot snapshot)
    {
        var price = snapshot.Prices.FirstOrDefault(
            p => p.PricingMode == PlatformProvidedPricingMode)
            ?? snapshot.Prices.FirstOrDefault();
        return price?.RecurringUsd ?? 0m;
    }

    /// <summary>Flag (never block) over-limit downgrades via the usage seam.</summary>
    private async Task<IReadOnlyList<EntitlementWarning>> ComputeDowngradeWarningsAsync(
        Guid tenantId, PlanChangeDirection direction, PlanSnapshot snapshot, CancellationToken ct)
    {
        if (direction != PlanChangeDirection.Downgrade)
        {
            return Array.Empty<EntitlementWarning>();
        }

        var warnings = new List<EntitlementWarning>();
        foreach (var ent in snapshot.Entitlements)
        {
            if (ent.LimitValue is not long limit) continue; // null ⇒ unlimited

            long? currentUsage;
            try
            {
                currentUsage = await _usageReader
                    .GetCurrentUsageAsync(tenantId, ent.MetricKey, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Usage unavailable ⇒ degrade to no warning for this metric.
                _logger.LogWarning(ex,
                    "Downgrade usage read failed for tenant {TenantId} metric {Metric}; no warning",
                    tenantId, ent.MetricKey);
                continue;
            }

            if (currentUsage is long usage && usage > limit)
            {
                _logger.LogWarning(
                    "Downgrade over-limit: tenant {TenantId} metric {Metric} usage={Usage} newLimit={Limit}",
                    tenantId, ent.MetricKey, usage, limit);
                warnings.Add(new EntitlementWarning(ent.MetricKey, usage, limit));
            }
        }

        return warnings;
    }

    /// <summary>
    /// The period-end boundary for a scheduled cancel. Default = the current
    /// plan's billing interval from now (monthly ⇒ +1 month, annual ⇒ +1 year).
    /// A real subscription anchor arrives with Epic 35 Billing behind this seam.
    /// </summary>
    private async Task<DateTime> ComputePeriodEndAsync(
        TenantPlanAssignment current, DateTime now, CancellationToken ct)
    {
        var snapshot = await _catalog.GetByIdAsync(current.PlanId, ct).ConfigureAwait(false);
        var interval = snapshot?.BillingInterval ?? "monthly";
        return string.Equals(interval, "annual", StringComparison.OrdinalIgnoreCase)
            ? now.AddYears(1)
            : now.AddMonths(1);
    }

    private async Task EnqueueActivationAsync(
        Guid tenantId, Guid assignmentId, DateTime visibleAt, CancellationToken ct)
    {
        try
        {
            await _platformTasks.EnqueueAsync(new PlatformQueuedTask
            {
                Type = ActivateScheduledPlanTaskPayload.TaskType,
                TenantId = tenantId,
                Payload = JsonSerializer.Serialize(new ActivateScheduledPlanTaskPayload
                {
                    TenantId = tenantId,
                    AssignmentId = assignmentId,
                }),
                VisibleAt = visibleAt,
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The scheduled row is already durable; a failed enqueue is logged
            // loud (an operator/reconciliation can re-drive it) but must not
            // roll back the committed cancel.
            _logger.LogError(ex,
                "Failed to enqueue activate-scheduled-plan task for tenant {TenantId} assignment {AssignmentId}",
                tenantId, assignmentId);
        }
    }

    private async Task EmitPlanChangedAsync(
        Guid tenantId, TenantPlanAssignment? old, PlanSnapshot? newSnapshot,
        PlanChangeDirection direction, IReadOnlyList<EntitlementWarning> warnings,
        Guid? actorUserId, string? actorEmail, string? actorPlatformRole, string source,
        DateTime prorationBoundaryAt, DateTime billingIntervalAnchor, CancellationToken ct)
    {
        var tags = new Dictionary<string, string?>
        {
            ["tenantId"] = tenantId.ToString("D"),
            ["oldPlanId"] = old?.PlanId.ToString("D"),
            ["oldPlanVersion"] = old?.PlanVersion.ToString(),
            ["newPlanId"] = newSnapshot?.PlanId.ToString("D"),
            ["newPlanVersion"] = newSnapshot?.Version.ToString(),
            ["direction"] = direction.ToString().ToLowerInvariant(),
            ["mode"] = ModeTag(),
            ["actorUserId"] = actorUserId?.ToString("D"),
            ["actorEmail"] = actorEmail,
            ["actorPlatformRole"] = actorPlatformRole,
            ["source"] = source,
            ["entitlementWarnings"] = warnings.Count > 0 ? "true" : "false",
            // One-release back-compat: the dashboard timeline still keys the
            // superseded PLAN.UPDATED event.
            ["supersedesLegacy"] = PlanAssignmentEventTypes.LegacyPlanUpdated,
        };

        var data = new Dictionary<string, object?>
        {
            ["prorationBoundaryAt"] = prorationBoundaryAt.ToString("O"),
            ["billingIntervalAnchor"] = billingIntervalAnchor.ToString("O"),
            ["direction"] = direction.ToString().ToLowerInvariant(),
            ["source"] = source,
            // Defence-in-depth: actor breadcrumb also in the immutable data record.
            ["actorUserId"] = actorUserId?.ToString("D"),
            ["actorEmail"] = actorEmail,
            ["actorPlatformRole"] = actorPlatformRole,
            ["warnings"] = warnings
                .Select(w => new
                {
                    metricKey = w.MetricKey.ToString(),
                    currentUsage = w.CurrentUsage,
                    newLimit = w.NewLimit,
                })
                .ToArray(),
        };

        await _publisher.AppendAndPublishAsync(new PlatformEvent
        {
            Type = PlanAssignmentEventTypes.TenantPlanChanged,
            TenantId = tenantId,
            Tags = JsonSerializer.Serialize(tags),
            Metadata = """{"workflowVersion":"1.0.0","eventSource":"system"}""",
            Data = JsonSerializer.Serialize(data),
        }, ct).ConfigureAwait(false);
    }

    private async Task EmitCancelledAsync(
        Guid tenantId, Guid currentPlanId, DateTime effectiveAt, bool immediate,
        Guid? actorUserId, string? actorEmail, string? actorPlatformRole, CancellationToken ct)
    {
        var tags = new Dictionary<string, string?>
        {
            ["tenantId"] = tenantId.ToString("D"),
            ["currentPlanId"] = currentPlanId.ToString("D"),
            ["effectiveAt"] = effectiveAt.ToString("O"),
            ["mode"] = ModeTag(),
            ["actorUserId"] = actorUserId?.ToString("D"),
            ["actorEmail"] = actorEmail,
            ["actorPlatformRole"] = actorPlatformRole,
            ["immediate"] = immediate ? "true" : "false",
        };

        var data = new Dictionary<string, object?>
        {
            ["currentPlanId"] = currentPlanId.ToString("D"),
            ["effectiveAt"] = effectiveAt.ToString("O"),
            ["immediate"] = immediate,
            ["actorUserId"] = actorUserId?.ToString("D"),
            ["actorEmail"] = actorEmail,
            ["actorPlatformRole"] = actorPlatformRole,
        };

        await _publisher.AppendAndPublishAsync(new PlatformEvent
        {
            Type = PlanAssignmentEventTypes.TenantPlanCancelled,
            TenantId = tenantId,
            Tags = JsonSerializer.Serialize(tags),
            Metadata = """{"workflowVersion":"1.0.0","eventSource":"system"}""",
            Data = JsonSerializer.Serialize(data),
        }, ct).ConfigureAwait(false);
    }

    private string ModeTag() =>
        _mode.Mode == TammaMode.SingleUser ? "single-user" : "saas";

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException pg
        && string.Equals(pg.SqlState, "23505", StringComparison.Ordinal);

    private static TammaError ConcurrentAssignError(Guid tenantId, Exception inner) =>
        new(
            "PLAN.ASSIGN.CONCURRENT",
            $"A concurrent assignment won the race for tenant '{tenantId:D}' — retry.",
            new Dictionary<string, object?> { ["tenantId"] = tenantId.ToString("D") },
            retryable: true, severity: TammaErrorSeverity.Low);
}
