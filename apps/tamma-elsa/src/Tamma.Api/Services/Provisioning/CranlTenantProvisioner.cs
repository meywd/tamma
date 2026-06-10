using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tamma.Api.Services.Provisioning.Cranl;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Provisioning;

/// <summary>
/// Cranl-backed <see cref="ITenantProvisioner"/>. Walks the per-tenant flow
/// from <c>docs/vendors/cranl/README.md</c>:
///
/// <list type="number">
///   <item><description><c>POST /api/projects</c> →
///     <c>cranl_project_id</c></description></item>
///   <item><description><c>POST /api/databases</c> →
///     <c>cranl_database_id</c></description></item>
///   <item><description>Poll <c>GET /api/databases/:id</c> until
///     <c>status == "running"</c> (5-minute timeout). Capture the
///     connection string, encrypt with
///     <see cref="TenantSecretProtector"/>, persist on
///     <c>cranl_database_url_encrypted</c>.</description></item>
///   <item><description><c>POST /api/applications</c> →
///     <c>cranl_app_id</c></description></item>
///   <item><description><c>PUT /api/applications/:id/environment</c> with
///     DATABASE_URL + control-plane vars + shared HMAC.</description></item>
///   <item><description><c>POST /api/applications/:id/deploy</c>.</description></item>
///   <item><description>Poll the app until <c>status == "running"</c>.</description></item>
///   <item><description><c>GET /api/applications/:id/domains</c> →
///     <c>cranl_app_url</c>.</description></item>
///   <item><description>Mark <c>provisioning_state = ready</c>.</description></item>
/// </list>
///
/// <para>The flow is long-running (several minutes). <see cref="ProvisionAsync"/>
/// returns immediately with <see cref="ProvisioningState.Pending"/> after
/// enqueueing a <c>provisioning.tenant</c> task; the actual walk happens
/// in <see cref="TenantProvisioningTaskHandler"/> on the
/// <see cref="TaskQueueProcessor"/> thread. Subsequent
/// <see cref="GetStatusAsync"/> calls report the current row state.</para>
///
/// <para><b>Story 30-3 — DEPRECATED.</b> Replaced by
/// <see cref="V2.Cranl.CranlTenantProviderV2"/>. The v2 implementation
/// preserves this same enqueue-onto-platform-queue dispatch pattern (see
/// <see cref="TenantProvisioningTaskHandler"/>'s reliance on the static
/// task-type constants below). Wave-C will remove this class once admin
/// endpoints + tests migrate.</para>
/// </summary>
[Obsolete("Use ITenantInfrastructureProvider (V2) instead. Removed in Wave C.")]
public sealed class CranlTenantProvisioner : ITenantProvisioner
{
    public const string ProvisioningTaskType = "provisioning.tenant";
    public const string DeprovisioningTaskType = "provisioning.tenant.deprovision";

    private readonly ControlPlaneDbContext _db;
    private readonly IPlatformQueuedTaskRepository _platformTasks;
    private readonly ILogger<CranlTenantProvisioner> _logger;

    /// <summary>
    /// Story 28-1 PR B — provisioning + deprovisioning tasks live on
    /// the <em>platform</em> queue, not the per-tenant queue. Reason:
    /// at provisioning time the tenant DB doesn't exist yet (the task's
    /// whole job is to create it!); at deprovisioning time the tenant
    /// DB is about to be torn down. Either way the per-tenant queue is
    /// not a viable home for the work item, so the task lives in
    /// <c>platform_queued_tasks</c> and the
    /// <see cref="Tamma.Api.Services.PlatformTasks.PlatformTaskWorker"/>
    /// drains it.
    /// </summary>
    public CranlTenantProvisioner(
        ControlPlaneDbContext db,
        IPlatformQueuedTaskRepository platformTasks,
        ILogger<CranlTenantProvisioner> logger)
    {
        _db = db;
        _platformTasks = platformTasks;
        _logger = logger;
    }

    public async Task<ProvisioningStatus> ProvisionAsync(
        Guid tenantId, ProvisioningOptions options, CancellationToken ct = default)
    {
        var tenant = await GetTenantAsync(tenantId, ct);

        // Idempotency: if the tenant already has a Cranl project the work
        // is in flight or done — return the current snapshot rather than
        // restarting the flow (which would leak resources).
        var current = ProvisioningStateExtensions.ParseState(tenant.ProvisioningState);
        if (!string.IsNullOrEmpty(tenant.CranlProjectId)
            || current is ProvisioningState.Pending
                       or ProvisioningState.DatabaseProvisioning
                       or ProvisioningState.DatabaseReady
                       or ProvisioningState.AppProvisioning
                       or ProvisioningState.AppDeploying
                       or ProvisioningState.Ready)
        {
            return ToStatus(tenant);
        }

        var now = DateTime.UtcNow;
        tenant.ProvisioningState = ProvisioningState.Pending.ToStorageString();
        tenant.ProvisioningDetail = "queued_for_cranl_provisioning";
        tenant.ProvisioningUpdatedAt = now;
        tenant.CranlRegion = options.Region;
        tenant.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        var payload = JsonSerializer.Serialize(new ProvisioningTaskPayload
        {
            TenantId = tenantId,
            Region = options.Region,
            CustomName = options.CustomName
        });
        await _platformTasks.EnqueueAsync(new PlatformQueuedTask
        {
            Type = ProvisioningTaskType,
            TenantId = tenantId,
            Payload = payload,
        }, ct);

        _logger.LogInformation(
            "Enqueued Cranl provisioning task for tenant {TenantId} (region={Region})",
            tenantId, options.Region);

        return ToStatus(tenant);
    }

    public async Task<ProvisioningStatus> GetStatusAsync(Guid tenantId, CancellationToken ct = default)
    {
        var tenant = await GetTenantAsync(tenantId, ct);
        return ToStatus(tenant);
    }

    public async Task DeprovisionAsync(Guid tenantId, CancellationToken ct = default)
    {
        var tenant = await GetTenantAsync(tenantId, ct);
        var state = ProvisioningStateExtensions.ParseState(tenant.ProvisioningState);
        if (state is ProvisioningState.Deprovisioning or ProvisioningState.Deprovisioned)
        {
            return; // already torn down (or in flight)
        }

        var now = DateTime.UtcNow;
        tenant.ProvisioningState = ProvisioningState.Deprovisioning.ToStorageString();
        tenant.ProvisioningDetail = "queued_for_cranl_teardown";
        tenant.ProvisioningUpdatedAt = now;
        tenant.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        var payload = JsonSerializer.Serialize(new ProvisioningTaskPayload
        {
            TenantId = tenantId,
            Region = tenant.CranlRegion ?? string.Empty,
            CustomName = null
        });
        await _platformTasks.EnqueueAsync(new PlatformQueuedTask
        {
            Type = DeprovisioningTaskType,
            TenantId = tenantId,
            Payload = payload,
        }, ct);

        _logger.LogInformation("Enqueued Cranl deprovisioning task for tenant {TenantId}", tenantId);
    }

    private async Task<Tenant> GetTenantAsync(Guid tenantId, CancellationToken ct)
    {
        var tenant = await _db.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == tenantId, ct)
            ?? throw new InvalidOperationException($"Tenant {tenantId} not found");
        return tenant;
    }

    private static ProvisioningStatus ToStatus(Tenant tenant) =>
        new(
            ProvisioningStateExtensions.ParseState(tenant.ProvisioningState),
            tenant.ProvisioningDetail,
            tenant.CranlAppUrl,
            tenant.ProvisioningUpdatedAt is { } u
                ? new DateTimeOffset(DateTime.SpecifyKind(u, DateTimeKind.Utc))
                : DateTimeOffset.UtcNow);
}

/// <summary>Payload shape for queued provisioning / deprovisioning tasks.</summary>
public sealed class ProvisioningTaskPayload
{
    public Guid TenantId { get; set; }
    public string Region { get; set; } = string.Empty;
    public string? CustomName { get; set; }
}
