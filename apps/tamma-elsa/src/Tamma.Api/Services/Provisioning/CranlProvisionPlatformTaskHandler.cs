using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tamma.Api.Services.PlatformTasks;
using Tamma.Api.Services.Provisioning.Cranl;
using Tamma.Api.Services.Provisioning.V2.Cranl;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Provisioning;

/// <summary>
/// Epic 30 Phase A — platform-queue handler that consumes the
/// <c>provisioning.tenant</c> rows <see cref="CranlTenantProviderV2"/>
/// enqueues and drives the <see cref="CranlProvisioningWorkflow"/> Cranl
/// REST walk (project → db → poll → application → env → deploy → poll →
/// domains).
///
/// <para>This closes the orphan-enqueue gap: the v2 Cranl provider flips
/// the tenant row to <see cref="ProvisioningState.Pending"/> and enqueues
/// a <c>provisioning.tenant</c> task, but until this handler existed no
/// <see cref="IPlatformTaskHandler"/> matched that exact
/// <see cref="TaskType"/>, so the row parked and the dispatch probe timed
/// out to <see cref="ProvisioningState.Failed"/>.</para>
///
/// <para>Runs on the platform queue (NOT the per-tenant queue) because at
/// provisioning time the tenant DB does not exist yet — its whole job is
/// to create it. Mirrors the failure semantics of
/// <see cref="V2.ProvisionTenantV2TaskHandler"/>:</para>
/// <list type="bullet">
///   <item><description>Malformed / missing payload →
///     <see cref="PlatformTaskTerminalException"/> (dead-letter without
///     burning the retry budget — a re-run will never parse).</description></item>
///   <item><description>The workflow flips the row to
///     <see cref="ProvisioningState.Failed"/> on a Cranl error and
///     re-throws; the bubbled exception lets the worker count a retry and
///     re-fire (the workflow resumes from the last good step on the next
///     reservation, so retries are safe).</description></item>
/// </list>
/// </summary>
public sealed class CranlProvisionPlatformTaskHandler : IPlatformTaskHandler
{
    private readonly CranlProvisioningWorkflow _workflow;
    private readonly CranlOptions _options;
    private readonly ILogger<CranlProvisionPlatformTaskHandler> _logger;

    public CranlProvisionPlatformTaskHandler(
        CranlProvisioningWorkflow workflow,
        CranlOptions options,
        ILogger<CranlProvisionPlatformTaskHandler> logger)
    {
        _workflow = workflow;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public string TaskType => CranlTenantProviderV2.ProvisioningTaskType;

    /// <inheritdoc />
    public async Task HandleAsync(PlatformQueuedTask task, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);

        var payload = ProvisioningTaskPayloadParser.ParseOrThrow(task);

        // Prefer the payload region; fall back to the configured default so a
        // task minted before a region default was set still picks a server.
        var region = string.IsNullOrWhiteSpace(payload.Region)
            ? _options.DefaultRegion
            : payload.Region;

        _logger.LogInformation(
            "cranl_provisioning.task_started taskId={TaskId} tenantId={TenantId} region={Region}",
            task.Id, payload.TenantId, region);

        await _workflow.ProvisionAsync(
            payload.TenantId,
            new ProvisioningOptions(region, payload.CustomName),
            ct).ConfigureAwait(false);

        _logger.LogInformation(
            "cranl_provisioning.task_finished taskId={TaskId} tenantId={TenantId}",
            task.Id, payload.TenantId);
    }
}
