using Microsoft.Extensions.Logging;
using Tamma.Api.Services.PlatformTasks;
using Tamma.Api.Services.Provisioning.V2.Cranl;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Provisioning;

/// <summary>
/// Epic 30 Phase A — platform-queue handler that consumes the
/// <c>provisioning.tenant.deprovision</c> rows
/// <see cref="CranlTenantProviderV2"/> enqueues and drives the
/// <see cref="CranlProvisioningWorkflow"/> teardown (delete app → db →
/// project, 404-as-absent, clears the tenant's <c>cranl_*</c> columns).
///
/// <para>Separate from <see cref="CranlProvisionPlatformTaskHandler"/>
/// because the platform-task registry routes by <b>exact</b>
/// <see cref="TaskType"/> match — one task type per handler class.</para>
///
/// <para>Failure semantics match the provision handler: a malformed /
/// missing payload is a <see cref="PlatformTaskTerminalException"/>; a
/// Cranl teardown error flips the row to
/// <see cref="ProvisioningState.Failed"/> in the workflow and re-throws so
/// the worker retries.</para>
/// </summary>
public sealed class CranlDeprovisionPlatformTaskHandler : IPlatformTaskHandler
{
    private readonly CranlProvisioningWorkflow _workflow;
    private readonly ILogger<CranlDeprovisionPlatformTaskHandler> _logger;

    public CranlDeprovisionPlatformTaskHandler(
        CranlProvisioningWorkflow workflow,
        ILogger<CranlDeprovisionPlatformTaskHandler> logger)
    {
        _workflow = workflow;
        _logger = logger;
    }

    /// <inheritdoc />
    public string TaskType => CranlTenantProviderV2.DeprovisioningTaskType;

    /// <inheritdoc />
    public async Task HandleAsync(PlatformQueuedTask task, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);

        var payload = ProvisioningTaskPayloadParser.ParseOrThrow(task);

        _logger.LogInformation(
            "cranl_deprovisioning.task_started taskId={TaskId} tenantId={TenantId}",
            task.Id, payload.TenantId);

        await _workflow.DeprovisionAsync(payload.TenantId, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "cranl_deprovisioning.task_finished taskId={TaskId} tenantId={TenantId}",
            task.Id, payload.TenantId);
    }
}
