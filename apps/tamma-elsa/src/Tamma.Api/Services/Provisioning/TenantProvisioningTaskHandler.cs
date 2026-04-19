using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Tamma.Api.Services.TaskQueue;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Provisioning;

/// <summary>
/// Queue handler for <c>provisioning.tenant</c> + <c>provisioning.tenant.deprovision</c>.
/// Runs on the <see cref="TaskQueueProcessor"/> thread (separate from
/// request handling) so the long-running Cranl polling does not pin a
/// request thread.
///
/// <para>Inherits from <see cref="TaskHandlerBase"/> so the ambient tenant
/// context is bound to <see cref="QueuedTask.TenantId"/> before
/// <see cref="HandleCoreAsync"/> runs (audit finding 027 — handlers must
/// scope their own DB work to the task's tenant).</para>
/// </summary>
public sealed class TenantProvisioningTaskHandler : TaskHandlerBase
{
    public TenantProvisioningTaskHandler(IServiceProvider services) : base(services)
    {
    }

    /// <summary>
    /// Single registration matches both <c>provisioning.tenant</c> and
    /// <c>provisioning.tenant.deprovision</c> via the registry's
    /// "longest prefix wins" semantics.
    /// </summary>
    public override string TypePrefix => "provisioning.tenant";

    protected override async Task HandleCoreAsync(QueuedTask task, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<ProvisioningTaskPayload>(task.Payload)
            ?? throw new InvalidOperationException(
                $"Provisioning task {task.Id} has invalid payload");

        var workflow = (CranlProvisioningWorkflow?)Services.GetService(typeof(CranlProvisioningWorkflow))
            ?? throw new InvalidOperationException(
                "CranlProvisioningWorkflow is not registered. "
                + "Provisioning tasks should not be enqueued when Cranl is not configured.");

        if (string.Equals(task.Type, CranlTenantProvisioner.DeprovisioningTaskType, StringComparison.Ordinal))
        {
            await workflow.DeprovisionAsync(payload.TenantId, ct);
            return;
        }

        // Default: provision (or resume).
        var options = new ProvisioningOptions(
            Region: string.IsNullOrEmpty(payload.Region) ? "germany-1" : payload.Region,
            CustomName: payload.CustomName);
        await workflow.ProvisionAsync(payload.TenantId, options, ct);
    }
}
