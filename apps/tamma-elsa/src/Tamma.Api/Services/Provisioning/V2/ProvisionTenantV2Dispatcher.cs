using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Provisioning.V2;

/// <summary>
/// Story 30-2 — entry-point service for the v2 provisioning workflow. Mode-aware
/// (CLAUDE.md §"Operating Modes"):
///
/// <list type="bullet">
///   <item><description><b>single-user mode</b> — the
///     <see cref="TenantProviderRegistry"/> only has the null seam wired. The
///     dispatcher short-circuits without enqueueing: it stamps the tenant
///     row with <see cref="ProvisioningState.Failed"/> +
///     <see cref="ProvisioningFailureReasons.NoProvisioningInThisMode"/>
///     and returns the snapshot. Per ADR §6, we surface a structured
///     failure rather than letting the null seam throw mid-workflow.</description></item>
///   <item><description><b>SaaS mode</b> — the registry has at least one real
///     provider keyed by <see cref="ITenantInfrastructureProvider.ProviderKey"/>.
///     The dispatcher refuses unknown keys (<c>provider_not_registered</c>)
///     before enqueueing. On accept, it enqueues a
///     <see cref="ProvisionTenantV2TaskPayload"/> onto the platform queue
///     (<see cref="IPlatformQueuedTaskRepository"/>) — NOT the per-tenant
///     queue, because at provision time the tenant DB doesn't exist yet
///     (30-1 audit constraint).</description></item>
/// </list>
///
/// <para>The dispatcher itself does NOT walk the provisioning steps — that's
/// <see cref="ProvisionTenantV2Workflow"/>, which the
/// <see cref="ProvisionTenantV2TaskHandler"/> invokes when the platform-task
/// worker reserves the row. Splitting submit-vs-execute lets the dispatcher
/// return synchronously to the operator (e.g. POST /api/admin/tenants/{id}/provision
/// returns 202 with a snapshot) while the long-running cloud-API walk runs
/// out-of-band.</para>
/// </summary>
public sealed class ProvisionTenantV2Dispatcher
{
    private readonly ControlPlaneDbContext _db;
    private readonly TenantProviderRegistry _registry;
    private readonly IPlatformQueuedTaskRepository _platformTasks;
    private readonly TimeProvider _clock;
    private readonly ILogger<ProvisionTenantV2Dispatcher> _logger;

    public ProvisionTenantV2Dispatcher(
        ControlPlaneDbContext db,
        TenantProviderRegistry registry,
        IPlatformQueuedTaskRepository platformTasks,
        TimeProvider clock,
        ILogger<ProvisionTenantV2Dispatcher> logger)
    {
        _db = db;
        _registry = registry;
        _platformTasks = platformTasks;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Submit a provisioning request. Returns immediately with a snapshot
    /// describing the dispatched (or refused) work. Does NOT call
    /// provider APIs.
    /// </summary>
    public async Task<ProvisioningResult> DispatchAsync(
        Guid tenantId,
        string providerKey,
        ProvisioningRequest request,
        Guid? invokingOrgId = null,
        CancellationToken ct = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(providerKey))
        {
            return await PersistFailureAsync(
                tenantId,
                ProvisioningFailureReasons.ProviderNotRegistered,
                detail: "provider_key_blank",
                ct).ConfigureAwait(false);
        }

        var tenant = await _db.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == tenantId && t.DeletedAt == null, ct)
            .ConfigureAwait(false);
        if (tenant is null)
        {
            // No tenant row to stamp — return a synthetic snapshot. The
            // caller is responsible for surfacing the failure to the
            // operator (typically a 404 in the admin endpoint).
            return BuildSyntheticFailure(
                ProvisioningFailureReasons.TenantNotFound,
                $"tenant_{tenantId}_not_found");
        }

        // Mode-aware short-circuit. The null seam advertises
        // ProvisioningTopology.None — the registry contains it in every
        // configuration. If the caller picked the null key explicitly,
        // OR the registry has no real backends wired (single-user mode
        // signature), surface a structured failure rather than letting
        // NullTenantProvider.ProvisionAsync throw downstream.
        if (string.Equals(providerKey, NullTenantProvider.Key, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "v2_provisioning.short_circuit_null_provider tenantId={TenantId}", tenantId);
            return await StampFailureAsync(
                tenant,
                ProvisioningFailureReasons.NoProvisioningInThisMode,
                detail: "single_user_or_dev_mode",
                ct).ConfigureAwait(false);
        }

        if (!_registry.TryGetProvider(providerKey, out var provider) || provider is null)
        {
            _logger.LogWarning(
                "v2_provisioning.provider_not_registered tenantId={TenantId} providerKey={ProviderKey} registered={Registered}",
                tenantId,
                providerKey,
                string.Join(",", _registry.RegisteredKeys));
            return await StampFailureAsync(
                tenant,
                ProvisioningFailureReasons.ProviderNotRegistered,
                detail: $"provider_key_{providerKey}_unknown",
                ct).ConfigureAwait(false);
        }

        // Detect "registry has only the null seam" — i.e. single-user
        // mode wiring even though the caller named a real provider.
        // TryGetProvider above returned the named provider, so we only
        // hit this branch when the caller asked for a key that DOES
        // resolve. The relevant single-user check is the explicit-null
        // branch above; nothing else to do here.

        var caps = provider.GetCapabilities();
        if (!caps.SupportsTopology(request.Topology))
        {
            _logger.LogWarning(
                "v2_provisioning.unsupported_topology tenantId={TenantId} providerKey={ProviderKey} topology={Topology}",
                tenantId,
                providerKey,
                request.Topology);
            return await StampFailureAsync(
                tenant,
                ProvisioningFailureReasons.UnsupportedTopology,
                detail: $"provider_{providerKey}_does_not_support_{request.Topology}",
                ct).ConfigureAwait(false);
        }

        if (request.Region is not null
            && caps.Regions.Count > 0
            && !caps.Regions.Contains(request.Region))
        {
            return await StampFailureAsync(
                tenant,
                ProvisioningFailureReasons.UnsupportedRegion,
                detail: $"provider_{providerKey}_region_{request.Region}_not_supported",
                ct).ConfigureAwait(false);
        }

        // Accept — flip to Pending + enqueue.
        var nowUtc = _clock.GetUtcNow();
        tenant.ProvisioningState = ProvisioningState.Pending.ToStorageString();
        tenant.ProvisioningDetail = "queued_for_v2_provisioning";
        tenant.ProvisioningUpdatedAt = nowUtc.UtcDateTime;
        tenant.UpdatedAt = nowUtc.UtcDateTime;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var payload = new ProvisionTenantV2TaskPayload
        {
            TenantId = tenantId,
            ProviderKey = providerKey,
            Topology = request.Topology,
            Region = request.Region,
            Tier = request.Tier,
            CustomName = request.CustomName,
            ExistingDatabaseUrl = request.ExistingDatabaseUrl,
            ExistingEngineUrl = request.ExistingEngineUrl,
            InvokingOrgId = invokingOrgId,
        };

        await _platformTasks.EnqueueAsync(new PlatformQueuedTask
        {
            Type = ProvisionTenantV2TaskPayload.TaskType,
            TenantId = tenantId,
            Payload = JsonSerializer.Serialize(payload),
        }, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "v2_provisioning.dispatched tenantId={TenantId} providerKey={ProviderKey} topology={Topology}",
            tenantId,
            providerKey,
            request.Topology);

        return new ProvisioningResult(
            new ProvisioningStatusSnapshot(
                ProvisioningState.Pending,
                Detail: "queued_for_v2_provisioning",
                FailureReason: null,
                UpdatedAt: nowUtc),
            ProviderResourceIds: new Dictionary<string, string>(),
            Endpoints: null,
            ProvisioningDurationSeconds: null);
    }

    private async Task<ProvisioningResult> StampFailureAsync(
        Tenant tenant,
        string failureReason,
        string detail,
        CancellationToken ct)
    {
        var nowUtc = _clock.GetUtcNow();
        tenant.ProvisioningState = ProvisioningState.Failed.ToStorageString();
        tenant.ProvisioningDetail = detail;
        tenant.ProvisioningUpdatedAt = nowUtc.UtcDateTime;
        tenant.UpdatedAt = nowUtc.UtcDateTime;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return new ProvisioningResult(
            new ProvisioningStatusSnapshot(
                ProvisioningState.Failed,
                Detail: detail,
                FailureReason: failureReason,
                UpdatedAt: nowUtc),
            ProviderResourceIds: new Dictionary<string, string>(),
            Endpoints: null,
            ProvisioningDurationSeconds: null);
    }

    private async Task<ProvisioningResult> PersistFailureAsync(
        Guid tenantId,
        string failureReason,
        string detail,
        CancellationToken ct)
    {
        var tenant = await _db.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == tenantId && t.DeletedAt == null, ct)
            .ConfigureAwait(false);
        if (tenant is null) return BuildSyntheticFailure(failureReason, detail);
        return await StampFailureAsync(tenant, failureReason, detail, ct).ConfigureAwait(false);
    }

    private ProvisioningResult BuildSyntheticFailure(string failureReason, string detail) =>
        new(
            new ProvisioningStatusSnapshot(
                ProvisioningState.Failed,
                Detail: detail,
                FailureReason: failureReason,
                UpdatedAt: _clock.GetUtcNow()),
            ProviderResourceIds: new Dictionary<string, string>(),
            Endpoints: null,
            ProvisioningDurationSeconds: null);
}
