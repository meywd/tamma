using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tamma.Api.Services.PlatformTasks;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Provisioning.V2;

/// <summary>
/// Story 30-2 — platform-queue handler that drives
/// <see cref="ProvisionTenantV2Workflow"/>. Consumes
/// <see cref="ProvisionTenantV2TaskPayload.TaskType"/>
/// (<c>provisioning.tenant.v2</c>) tasks reserved by
/// <see cref="PlatformTaskWorker"/>.
///
/// <para>Implements <see cref="IPlatformTaskHandler"/>, NOT the per-tenant
/// <c>ITaskHandler</c>, because the V2 work runs on the platform queue
/// (the constraint preserved from 30-1's audit of the v1 Cranl
/// provisioner: at provisioning time the tenant DB
/// doesn't exist yet). This is the first
/// <see cref="IPlatformTaskHandler"/> implementation in the codebase.</para>
///
/// <para>Failure semantics:</para>
/// <list type="bullet">
///   <item><description>Malformed payload (deserialise failure, missing
///     fields) → <see cref="PlatformTaskTerminalException"/> so the
///     row goes straight to dead-letter without burning the retry
///     budget.</description></item>
///   <item><description>Workflow returned a Failed snapshot → handler
///     completes normally. The Failed state is persisted on the tenant
///     row by the workflow itself; throwing here would re-enqueue the
///     row and trigger compensation again, which would be wrong.</description></item>
///   <item><description>Workflow returned <c>DeferRequested</c> (tenant
///     still provisioning, within budget) → the handler returns the row to
///     <c>pending</c> with <c>VisibleAt = now + ProbeInterval</c> via
///     <see cref="IPlatformQueuedTaskRepository.DeferAsync"/> and throws
///     <see cref="PlatformTaskDeferredException"/> (modelled on
///     <c>RetireSecretVersionTaskHandler</c>). The worker treats this as a
///     no-op acknowledgement: no <c>CompleteAsync</c>, no <c>FailAsync</c>,
///     retry budget untouched. This is the Phase-B I1 fix — the saga
///     RELEASES the one-task-at-a-time worker slot between probes so a
///     SINGLE worker can interleave the inner <c>provisioning.tenant</c>
///     task before the next resume.</description></item>
///   <item><description>Workflow threw an unexpected exception → bubble
///     up so the worker counts a retry and re-fires the task on the
///     next reservation. Provider idempotency (ADR §4) makes this
///     safe.</description></item>
/// </list>
///
/// <para><b>Cross-resume probe budget</b>: the ~30-min probe budget must
/// span defers, not restart on each resume. The handler derives the
/// absolute deadline from the task's first-enqueue timestamp
/// (<see cref="PlatformQueuedTask.CreatedAt"/> + the workflow's
/// <c>ProbeTimeout</c>) and passes it into <c>ExecuteAsync</c>. This needs
/// NO new persisted state / migration: <c>CreatedAt</c> is set once at
/// enqueue and <c>DeferAsync</c> leaves it untouched (it only moves
/// <c>VisibleAt</c>), so the deadline is stable across every resume.</para>
/// </summary>
public sealed class ProvisionTenantV2TaskHandler : IPlatformTaskHandler
{
    private readonly ProvisionTenantV2Workflow _workflow;
    private readonly IPlatformQueuedTaskRepository _platformTasks;
    private readonly TimeProvider _clock;
    private readonly ILogger<ProvisionTenantV2TaskHandler> _logger;

    public ProvisionTenantV2TaskHandler(
        ProvisionTenantV2Workflow workflow,
        IPlatformQueuedTaskRepository platformTasks,
        TimeProvider clock,
        ILogger<ProvisionTenantV2TaskHandler> logger)
    {
        _workflow = workflow;
        _platformTasks = platformTasks;
        _clock = clock;
        _logger = logger;
    }

    public string TaskType => ProvisionTenantV2TaskPayload.TaskType;

    public async Task HandleAsync(PlatformQueuedTask task, CancellationToken ct)
    {
        if (task is null) throw new ArgumentNullException(nameof(task));

        ProvisionTenantV2TaskPayload? payload;
        try
        {
            payload = string.IsNullOrEmpty(task.Payload)
                ? null
                : JsonSerializer.Deserialize<ProvisionTenantV2TaskPayload>(task.Payload);
        }
        catch (JsonException ex)
        {
            throw new PlatformTaskTerminalException(
                $"v2 provisioning task {task.Id} has malformed JSON payload: {ex.Message}",
                ex);
        }

        if (payload is null)
        {
            throw new PlatformTaskTerminalException(
                $"v2 provisioning task {task.Id} has no payload (expected ProvisionTenantV2TaskPayload).");
        }
        if (payload.TenantId == Guid.Empty)
        {
            throw new PlatformTaskTerminalException(
                $"v2 provisioning task {task.Id} payload has empty TenantId.");
        }
        if (string.IsNullOrWhiteSpace(payload.ProviderKey))
        {
            throw new PlatformTaskTerminalException(
                $"v2 provisioning task {task.Id} payload has blank ProviderKey.");
        }

        _logger.LogInformation(
            "v2_provisioning.task_started taskId={TaskId} tenantId={TenantId} providerKey={ProviderKey} op={Operation}",
            task.Id, payload.TenantId, payload.ProviderKey, payload.Operation);

        if (payload.Operation == ProvisioningOperation.Deprovision)
        {
            var deprovisionResult = await _workflow
                .DeprovisionAsync(payload, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "v2_provisioning.task_finished taskId={TaskId} tenantId={TenantId} state={State} failureReason={FailureReason}",
                task.Id, payload.TenantId,
                deprovisionResult.Status.State.ToStorageString(),
                deprovisionResult.Status.FailureReason);
            return;
        }

        // Provision. Derive the STABLE cross-resume probe deadline from the
        // task's first-enqueue timestamp so a defer never resets the budget.
        // (CreatedAt is UTC in the DB; DeferAsync leaves it untouched.)
        var createdAtUtc = DateTime.SpecifyKind(task.CreatedAt, DateTimeKind.Utc);
        var probeDeadline = new DateTimeOffset(createdAtUtc) + _workflow.ProbeTimeout;

        var outcome = await _workflow
            .ExecuteAsync(payload, probeDeadline, ct).ConfigureAwait(false);

        if (outcome.IsDeferRequested)
        {
            // Tenant still provisioning, within budget. Return the row to the
            // queue with a future VisibleAt so this worker's slot is freed for
            // the inner provisioning.tenant task; throw the deferred-exception
            // sentinel so the worker does NOT Complete (clobber) or Fail (burn
            // retry budget). Mirrors RetireSecretVersionTaskHandler exactly.
            var visibleAt = _clock.GetUtcNow().UtcDateTime + outcome.DeferDelay;
            await _platformTasks.DeferAsync(task.Id, visibleAt, ct).ConfigureAwait(false);
            _logger.LogDebug(
                "v2_provisioning.deferred taskId={TaskId} tenantId={TenantId} state={State} visibleAt={VisibleAt:o}",
                task.Id, payload.TenantId,
                outcome.Status.State.ToStorageString(), visibleAt);
            throw new PlatformTaskDeferredException(
                $"v2 provisioning task {task.Id} deferred until {visibleAt:o} (tenant {payload.TenantId} still provisioning).");
        }

        _logger.LogInformation(
            "v2_provisioning.task_finished taskId={TaskId} tenantId={TenantId} state={State} failureReason={FailureReason}",
            task.Id, payload.TenantId,
            outcome.Status.State.ToStorageString(),
            outcome.Status.FailureReason);
    }
}
