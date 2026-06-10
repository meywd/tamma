using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tamma.Api.Services.PlatformTasks;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Provisioning;

/// <summary>
/// Unified-tenancy Phase 4 — platform-queue handler that drives
/// <see cref="ITenantMoveService.MoveAsync"/>. Consumes
/// <see cref="MoveTenantTaskPayload.TaskType"/> (<c>tenant.move</c>) tasks
/// reserved by <see cref="PlatformTaskWorker"/>; the admin endpoint
/// (<c>POST /api/admin/tenants/{tenantId}/move</c>) enqueues them after
/// cheap validation, mirroring how the Cranl/v2 provisioning endpoints
/// hand off long-running work
/// (<see cref="V2.ProvisionTenantV2TaskHandler"/>).
///
/// <para><b>Failure semantics</b> (mirrors the v2 provisioning handler):</para>
/// <list type="bullet">
///   <item><description>Malformed payload (bad JSON, empty ids) →
///     <see cref="PlatformTaskTerminalException"/> so the row goes
///     straight to dead-letter without burning the retry budget.</description></item>
///   <item><description>Worker shutdown
///     (<see cref="OperationCanceledException"/> while the token is
///     cancelled) → rethrow WITHOUT stamping <c>FailureReason</c>; the
///     visibility-timeout reaper re-claims the row and the move's own
///     idempotency (draining-resume) makes the re-run safe.</description></item>
///   <item><description>Any other exception — including the move
///     engine's advisory-lock "already in progress" rejection
///     (<see cref="InvalidOperationException"/>) — stamps the tenant's
///     <c>FailureReason</c> shadow column (+<c>UpdatedAt</c>), logs, and
///     rethrows so the worker's <c>FailAsync</c> re-enqueues the row
///     (retry budget; dead-letter at the ceiling). The lock rejection is
///     deliberately retryable: the competing move finishes (or fails) and
///     a later retry proceeds — same treatment the v2 handler gives every
///     unexpected exception.</description></item>
/// </list>
///
/// <para>On success any stale <c>FailureReason</c> from a previous failed
/// attempt is cleared (a retried task that eventually succeeds must not
/// leave the admin UX reporting the old error). The move engine itself
/// owns the <c>Status</c> column (draining → active / left draining on
/// failure per its documented failure windows) — this handler never
/// touches <c>Status</c>.</para>
/// </summary>
public sealed class MoveTenantTaskHandler : IPlatformTaskHandler
{
    private readonly ITenantMoveService _moveService;
    private readonly ControlPlaneDbContext _db;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MoveTenantTaskHandler> _logger;

    public MoveTenantTaskHandler(
        ITenantMoveService moveService,
        ControlPlaneDbContext db,
        TimeProvider timeProvider,
        ILogger<MoveTenantTaskHandler> logger)
    {
        _moveService = moveService;
        _db = db;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public string TaskType => MoveTenantTaskPayload.TaskType;

    public async Task HandleAsync(PlatformQueuedTask task, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);

        MoveTenantTaskPayload? payload;
        try
        {
            payload = string.IsNullOrEmpty(task.Payload)
                ? null
                : JsonSerializer.Deserialize<MoveTenantTaskPayload>(task.Payload);
        }
        catch (JsonException ex)
        {
            throw new PlatformTaskTerminalException(
                $"tenant-move task {task.Id} has malformed JSON payload: {ex.Message}",
                ex);
        }

        if (payload is null)
        {
            throw new PlatformTaskTerminalException(
                $"tenant-move task {task.Id} has no payload (expected MoveTenantTaskPayload).");
        }
        if (payload.TenantId == Guid.Empty)
        {
            throw new PlatformTaskTerminalException(
                $"tenant-move task {task.Id} payload has empty TenantId.");
        }
        if (payload.TargetDatabaseId == Guid.Empty)
        {
            throw new PlatformTaskTerminalException(
                $"tenant-move task {task.Id} payload has empty TargetDatabaseId.");
        }

        _logger.LogInformation(
            "tenant.move.task_started taskId={TaskId} tenantId={TenantId} targetDatabaseId={TargetDatabaseId}",
            task.Id, payload.TenantId, payload.TargetDatabaseId);

        try
        {
            await _moveService
                .MoveAsync(payload.TenantId, payload.TargetDatabaseId, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Worker shutdown — leave the row in 'processing' for the
            // reaper; the move engine's draining-resume makes the
            // re-run safe. No FailureReason stamp (nothing failed).
            throw;
        }
        catch (Exception ex)
        {
            // Includes the advisory-lock "already in progress" rejection:
            // bubble as a retryable failure (worker FailAsync → re-enqueue,
            // dead-letter at MaxRetries), mirroring how the v2 provisioning
            // handler treats every unexpected exception.
            await StampFailureReasonAsync(
                payload.TenantId,
                $"{ex.GetType().Name}: {ex.Message}",
                ct).ConfigureAwait(false);
            _logger.LogWarning(ex,
                "tenant.move.task_failed taskId={TaskId} tenantId={TenantId} targetDatabaseId={TargetDatabaseId}",
                task.Id, payload.TenantId, payload.TargetDatabaseId);
            throw;
        }

        await ClearFailureReasonAsync(payload.TenantId, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "tenant.move.task_finished taskId={TaskId} tenantId={TenantId} targetDatabaseId={TargetDatabaseId}",
            task.Id, payload.TenantId, payload.TargetDatabaseId);
    }

    /// <summary>
    /// Best-effort write of the <c>FailureReason</c> shadow column
    /// (+<c>UpdatedAt</c> — same SaveChanges pattern
    /// <c>AdminTenantsEndpoints.RetryTenant</c> uses). Swallows its own
    /// errors so a bookkeeping failure never masks the original move
    /// exception that is about to propagate to the worker.
    /// </summary>
    private async Task StampFailureReasonAsync(
        Guid tenantId, string reason, CancellationToken ct)
    {
        try
        {
            var tenant = await _db.Tenants
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(t => t.Id == tenantId && t.DeletedAt == null, ct)
                .ConfigureAwait(false);
            if (tenant is null) return;

            // Clamp like PlatformTaskWorker's persisted-error discipline.
            if (reason.Length > 2000) reason = reason[..2000];

            _db.Entry(tenant).Property("FailureReason").CurrentValue = reason;
            tenant.UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "tenant.move.failure_reason_stamp_failed tenantId={TenantId}", tenantId);
        }
    }

    /// <summary>
    /// Clears a stale <c>FailureReason</c> left by a previous failed
    /// attempt once a retried move succeeds. No-op when nothing is set.
    /// </summary>
    private async Task ClearFailureReasonAsync(Guid tenantId, CancellationToken ct)
    {
        var tenant = await _db.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == tenantId && t.DeletedAt == null, ct)
            .ConfigureAwait(false);
        if (tenant is null) return;

        var current = (string?)_db.Entry(tenant).Property("FailureReason").CurrentValue;
        if (current is null) return;

        _db.Entry(tenant).Property("FailureReason").CurrentValue = null;
        tenant.UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
