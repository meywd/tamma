using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tamma.Api.Services.PlatformTasks;
using Tamma.Data.Entities;

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
/// (the constraint preserved from 30-1's audit of v1
/// <c>CranlTenantProvisioner</c>: at provisioning time the tenant DB
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
///   <item><description>Workflow threw an unexpected exception → bubble
///     up so the worker counts a retry and re-fires the task on the
///     next reservation. Provider idempotency (ADR §4) makes this
///     safe.</description></item>
/// </list>
/// </summary>
public sealed class ProvisionTenantV2TaskHandler : IPlatformTaskHandler
{
    private readonly ProvisionTenantV2Workflow _workflow;
    private readonly ILogger<ProvisionTenantV2TaskHandler> _logger;

    public ProvisionTenantV2TaskHandler(
        ProvisionTenantV2Workflow workflow,
        ILogger<ProvisionTenantV2TaskHandler> logger)
    {
        _workflow = workflow;
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
            "v2_provisioning.task_started taskId={TaskId} tenantId={TenantId} providerKey={ProviderKey}",
            task.Id, payload.TenantId, payload.ProviderKey);

        var result = await _workflow.ExecuteAsync(payload, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "v2_provisioning.task_finished taskId={TaskId} tenantId={TenantId} state={State} failureReason={FailureReason}",
            task.Id, payload.TenantId,
            result.Status.State.ToStorageString(),
            result.Status.FailureReason);
    }
}
