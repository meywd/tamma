using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tamma.Api.Services.PlatformTasks;
using Tamma.Api.Services.Pricing;
using Tamma.Core;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Provisioning;

/// <summary>
/// Story 34-4 — platform-queue handler that promotes a due <c>scheduled</c>
/// plan assignment to <c>active</c> at its boundary, by calling
/// <see cref="IPlanAssignmentService.ActivateScheduledAsync"/>. Enqueued by the
/// period-end cancel path with <c>VisibleAt</c> = the boundary, so the queue
/// only reserves it once the window opens. Idempotent by <c>AssignmentId</c>: a
/// re-run whose target is already active is a no-op.
///
/// <para>Failure semantics mirror <see cref="MoveTenantTaskHandler"/>: a
/// malformed payload is terminal (dead-letter, no retry budget burn); a
/// concurrent-assignment race (<c>PLAN.ASSIGN.CONCURRENT</c>, retryable) and any
/// other exception bubble as a retryable failure so the worker re-enqueues;
/// worker-shutdown cancellation rethrows without marking failure.</para>
/// </summary>
public sealed class ActivateScheduledPlanTaskHandler : IPlatformTaskHandler
{
    private readonly IPlanAssignmentService _assignments;
    private readonly ILogger<ActivateScheduledPlanTaskHandler> _logger;

    public ActivateScheduledPlanTaskHandler(
        IPlanAssignmentService assignments,
        ILogger<ActivateScheduledPlanTaskHandler> logger)
    {
        _assignments = assignments ?? throw new ArgumentNullException(nameof(assignments));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string TaskType => ActivateScheduledPlanTaskPayload.TaskType;

    public async Task HandleAsync(PlatformQueuedTask task, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);

        ActivateScheduledPlanTaskPayload? payload;
        try
        {
            payload = string.IsNullOrEmpty(task.Payload)
                ? null
                : JsonSerializer.Deserialize<ActivateScheduledPlanTaskPayload>(task.Payload);
        }
        catch (JsonException ex)
        {
            throw new PlatformTaskTerminalException(
                $"activate-scheduled-plan task {task.Id} has malformed JSON payload: {ex.Message}", ex);
        }

        if (payload is null || payload.TenantId == Guid.Empty || payload.AssignmentId == Guid.Empty)
        {
            throw new PlatformTaskTerminalException(
                $"activate-scheduled-plan task {task.Id} payload missing TenantId/AssignmentId.");
        }

        _logger.LogInformation(
            "plan.activate_scheduled.task_started taskId={TaskId} tenantId={TenantId} assignmentId={AssignmentId}",
            task.Id, payload.TenantId, payload.AssignmentId);

        try
        {
            var result = await _assignments
                .ActivateScheduledAsync(payload.TenantId, payload.AssignmentId, ct)
                .ConfigureAwait(false);

            if (result is null)
            {
                _logger.LogInformation(
                    "plan.activate_scheduled.noop taskId={TaskId} assignmentId={AssignmentId} (not found / not scheduled)",
                    task.Id, payload.AssignmentId);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Worker shutdown — leave the row for the reaper; activation is
            // idempotent so the re-run is safe.
            throw;
        }
        catch (TammaError ex) when (ex.Code == "PLAN.ASSIGN.CONCURRENT")
        {
            // A concurrent assignment won the race — retryable; let the worker
            // re-enqueue (the scheduled row is either already promoted or will be
            // re-evaluated).
            _logger.LogWarning(ex,
                "plan.activate_scheduled.concurrent taskId={TaskId} assignmentId={AssignmentId}",
                task.Id, payload.AssignmentId);
            throw;
        }

        _logger.LogInformation(
            "plan.activate_scheduled.task_finished taskId={TaskId} assignmentId={AssignmentId}",
            task.Id, payload.AssignmentId);
    }
}
