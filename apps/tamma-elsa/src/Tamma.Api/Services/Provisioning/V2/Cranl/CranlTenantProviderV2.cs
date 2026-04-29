using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tamma.Api.Services.Provisioning;
using Tamma.Api.Services.Provisioning.Cranl;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Provisioning.V2.Cranl;

/// <summary>
/// Cranl-backed implementation of v2's
/// <see cref="ITenantInfrastructureProvider"/>. Wraps the same per-tenant
/// flow that v1's <see cref="CranlTenantProvisioner"/> enqueues — Cranl
/// project → db → poll → application → env → deploy → poll → domains
/// (see <c>docs/vendors/cranl/README.md</c>) — but presents the v2
/// capability/topology/endpoint contract that the dispatch workflow
/// (Story 30-2) and onboarding UI (Story 30-7) consume.
///
/// <para><b>Dispatch pattern (preserved from v1)</b>: the long-running
/// Cranl walk runs out-of-band on the
/// <see cref="IPlatformQueuedTaskRepository"/> — NOT on the per-tenant
/// queue, because at provisioning time the tenant DB doesn't exist yet
/// (the task's whole job is to create it!) and at deprovisioning time
/// the tenant DB is about to be torn down. <see cref="ProvisionAsync"/>
/// flips the row state to <see cref="ProvisioningState.Pending"/>,
/// enqueues a <c>provisioning.tenant</c> task, and returns immediately
/// with the <see cref="ProvisioningStatusSnapshot"/>. Subsequent
/// <see cref="GetStatusAsync"/> calls report whichever transition the
/// background task has reached. This matches Story 28-1 PR B and the
/// 30-1 ADR §"v1 design audit" — preserving it is a hard contract.</para>
///
/// <para><b>Idempotency</b>: every method is idempotent.
/// <see cref="ProvisionAsync"/> on a tenant that already has a Cranl
/// project (or whose <c>ProvisioningState</c> is anywhere in the active
/// flow) returns the current snapshot without enqueueing a second task —
/// re-enqueueing would leak resources by spawning a parallel project.
/// <see cref="DeprovisionAsync"/> on an already-torn-down tenant is a
/// no-op.</para>
///
/// <para><b>Topology gating (AC9)</b>: when the request's topology is
/// not in <see cref="CranlCapabilities.SupportedTopologies"/>, the method
/// returns <see cref="ProvisioningState.Failed"/> with
/// <c>FailureReason = "unsupported_topology"</c> instead of throwing.
/// The dispatch workflow expects a structured failure here, not an
/// exception.</para>
///
/// <para><b>Persistence of v2 fields</b>: the new
/// <c>tenants.provider_key</c> and <c>tenants.provider_resource_ids</c>
/// columns (added by this story's migration) are populated lazily —
/// when <see cref="ProvisionAsync"/> first writes to the row it sets
/// <c>provider_key = "cranl"</c>. Existing Cranl-backed tenants that
/// pre-date this story are backfilled by the migration's <c>UPDATE</c>
/// statement. The structured <c>failure_reason</c> short-code lives on
/// the existing <c>tenants.failure_reason</c> shadow column (Epic 28
/// already shipped it).</para>
/// </summary>
public sealed class CranlTenantProviderV2 : ITenantInfrastructureProvider
{
    private static readonly ProviderCapabilities CapabilitiesValue =
        new(
            CranlCapabilities.ProviderKey,
            CranlCapabilities.DisplayName,
            CranlCapabilities.SupportedTopologies,
            CranlCapabilities.Regions,
            CranlCapabilities.Features,
            MaxTenantsPerOrg: null,
            CostHint: null);

    private readonly ControlPlaneDbContext _db;
    private readonly IPlatformQueuedTaskRepository _platformTasks;
    private readonly TenantSecretProtector _protector;
    private readonly CranlOptions _options;
    private readonly ILogger<CranlTenantProviderV2> _logger;

    public CranlTenantProviderV2(
        ControlPlaneDbContext db,
        IPlatformQueuedTaskRepository platformTasks,
        TenantSecretProtector protector,
        CranlOptions options,
        ILogger<CranlTenantProviderV2> logger)
    {
        _db = db;
        _platformTasks = platformTasks;
        _protector = protector;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public string ProviderKey => CranlCapabilities.ProviderKey;

    /// <inheritdoc />
    public ProviderCapabilities GetCapabilities() => CapabilitiesValue;

    /// <inheritdoc />
    public async Task<ProvisioningResult> ProvisionAsync(
        Guid tenantId,
        ProvisioningRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // AC9 — topology gating. Return a structured failure rather than throwing
        // so the dispatch workflow can decide retry-vs-surface from FailureReason.
        if (!CapabilitiesValue.SupportsTopology(request.Topology))
        {
            var snap = new ProvisioningStatusSnapshot(
                ProvisioningState.Failed,
                Detail: $"cranl_does_not_support_topology_{request.Topology.ToString().ToLowerInvariant()}",
                FailureReason: "unsupported_topology",
                UpdatedAt: DateTimeOffset.UtcNow);
            return new ProvisioningResult(
                snap,
                ProviderResourceIds: EmptyResourceIds,
                Endpoints: null,
                ProvisioningDurationSeconds: null);
        }

        var tenant = await GetTenantAsync(tenantId, ct);

        // Idempotency: if Cranl resources already exist (or the row is mid-flow)
        // return the current snapshot rather than enqueueing a second task.
        var current = ProvisioningStateExtensions.ParseState(tenant.ProvisioningState);
        if (!string.IsNullOrEmpty(tenant.CranlProjectId)
            || current is ProvisioningState.Pending
                       or ProvisioningState.DatabaseProvisioning
                       or ProvisioningState.DatabaseReady
                       or ProvisioningState.AppProvisioning
                       or ProvisioningState.AppDeploying
                       or ProvisioningState.Ready)
        {
            return BuildResult(tenant, decryptEndpoint: current is ProvisioningState.Ready or ProvisioningState.DatabaseReady or ProvisioningState.AppProvisioning or ProvisioningState.AppDeploying);
        }

        var now = DateTime.UtcNow;
        var region = !string.IsNullOrWhiteSpace(request.Region)
            ? request.Region!
            : _options.DefaultRegion;

        tenant.ProvisioningState = ProvisioningState.Pending.ToStorageString();
        tenant.ProvisioningDetail = "queued_for_cranl_provisioning";
        tenant.ProvisioningUpdatedAt = now;
        tenant.CranlRegion = region;
        tenant.UpdatedAt = now;

        // Story 30-3 — write the new v2 fields. Shadow columns are accessed via
        // the change tracker entry so we don't have to add them to the POCO yet
        // (they'll move onto the Tenant entity when 30-8/30-9 read them via
        // navigation; for now they live on the row only).
        var entry = _db.Entry(tenant);
        entry.Property<string?>("ProviderKey").CurrentValue = CranlCapabilities.ProviderKey;
        // Clear any stale failure short-code from a prior failed run.
        entry.Property<string?>("FailureReason").CurrentValue = null;

        await _db.SaveChangesAsync(ct);

        var payload = JsonSerializer.Serialize(new ProvisioningTaskPayload
        {
            TenantId = tenantId,
            Region = region,
            CustomName = request.CustomName
        });
        await _platformTasks.EnqueueAsync(new PlatformQueuedTask
        {
            // V2 deliberately reuses the v1 task-type constants so the existing
            // TenantProvisioningTaskHandler dispatches both v1- and v2-enqueued
            // tasks identically. Wave-C will retire the v1 const onto a v2-
            // owned constant when AdminEndpoints + the handler migrate.
#pragma warning disable CS0618
            Type = CranlTenantProvisioner.ProvisioningTaskType,
#pragma warning restore CS0618
            TenantId = tenantId,
            Payload = payload,
        }, ct);

        _logger.LogInformation(
            "Cranl V2 provider: enqueued provisioning task for tenant {TenantId} (region={Region}, topology={Topology})",
            tenantId, region, request.Topology);

        return BuildResult(tenant, decryptEndpoint: false);
    }

    /// <inheritdoc />
    public async Task<ProvisioningStatusSnapshot> GetStatusAsync(
        Guid tenantId,
        CancellationToken ct)
    {
        var tenant = await GetTenantAsync(tenantId, ct);
        return BuildSnapshot(tenant);
    }

    /// <inheritdoc />
    public async Task DeprovisionAsync(
        Guid tenantId,
        DeprovisioningRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var tenant = await GetTenantAsync(tenantId, ct);
        var state = ProvisioningStateExtensions.ParseState(tenant.ProvisioningState);
        if (state is ProvisioningState.Deprovisioning or ProvisioningState.Deprovisioned)
        {
            return; // already torn down (or in flight) — idempotent no-op
        }

        var now = DateTime.UtcNow;
        tenant.ProvisioningState = ProvisioningState.Deprovisioning.ToStorageString();
        tenant.ProvisioningDetail = string.IsNullOrWhiteSpace(request.Reason)
            ? "queued_for_cranl_teardown"
            : $"queued_for_cranl_teardown:{request.Reason}";
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
            // V2 deliberately reuses the v1 task-type constants — see the
            // matching note on ProvisionAsync above.
#pragma warning disable CS0618
            Type = CranlTenantProvisioner.DeprovisioningTaskType,
#pragma warning restore CS0618
            TenantId = tenantId,
            Payload = payload,
        }, ct);

        _logger.LogInformation(
            "Cranl V2 provider: enqueued deprovisioning task for tenant {TenantId} (reason={Reason}, mode={Mode})",
            tenantId, request.Reason ?? "<none>", request.CleanupMode);
    }

    /// <inheritdoc />
    public async Task<TenantEndpoints> ResolveEndpointsAsync(
        Guid tenantId,
        CancellationToken ct)
    {
        var tenant = await GetTenantAsync(tenantId, ct);
        var state = ProvisioningStateExtensions.ParseState(tenant.ProvisioningState);
        var endpoints = TryBuildEndpoints(tenant);
        if (endpoints is null)
        {
            throw new InvalidOperationException(
                $"Tenant {tenantId} is in state '{state}' (no endpoint available). " +
                "ResolveEndpointsAsync requires DatabaseReady / AppProvisioning / " +
                "AppDeploying / Ready — call GetStatusAsync first to check progress.");
        }
        return endpoints;
    }

    private async Task<Tenant> GetTenantAsync(Guid tenantId, CancellationToken ct)
    {
        return await _db.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == tenantId, ct)
            ?? throw new InvalidOperationException($"Tenant {tenantId} not found");
    }

    private ProvisioningResult BuildResult(Tenant tenant, bool decryptEndpoint)
    {
        var snapshot = BuildSnapshot(tenant);
        var endpoints = decryptEndpoint ? TryBuildEndpoints(tenant) : null;
        return new ProvisioningResult(
            snapshot,
            ProviderResourceIds: BuildResourceIds(tenant),
            Endpoints: endpoints,
            ProvisioningDurationSeconds: null);
    }

    private ProvisioningStatusSnapshot BuildSnapshot(Tenant tenant)
    {
        var state = ProvisioningStateExtensions.ParseState(tenant.ProvisioningState);

        // Shadow column read: failure_reason short-code populated by the
        // background workflow when a step fails; null when the row isn't
        // in Failed (or when the failure pre-dated structured short-codes).
        string? failureReason = null;
        try
        {
            failureReason = _db.Entry(tenant)
                .Property<string?>("FailureReason").CurrentValue;
        }
        catch (InvalidOperationException)
        {
            // Shadow column not modelled (e.g. older test fixture without
            // 28-1 shadow columns). Leave failureReason null.
        }

        var updated = tenant.ProvisioningUpdatedAt is { } u
            ? new DateTimeOffset(DateTime.SpecifyKind(u, DateTimeKind.Utc))
            : DateTimeOffset.UtcNow;

        return new ProvisioningStatusSnapshot(
            state,
            tenant.ProvisioningDetail,
            // FailureReason is only meaningful when the row is in Failed; for
            // any other state, surface null even if a stale value lingers from
            // a prior failed run that was retried.
            FailureReason: state == ProvisioningState.Failed ? failureReason : null,
            UpdatedAt: updated);
    }

    private static IReadOnlyDictionary<string, string> BuildResourceIds(Tenant tenant)
    {
        // Build the resource-id map from whatever columns are populated. The
        // dispatch workflow persists this onto tenants.provider_resource_ids
        // when the v2 result lands — the in-memory shape is the contract.
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(tenant.CranlProjectId))
            map["cranl_project_id"] = tenant.CranlProjectId!;
        if (!string.IsNullOrEmpty(tenant.CranlDatabaseId))
            map["cranl_database_id"] = tenant.CranlDatabaseId!;
        if (!string.IsNullOrEmpty(tenant.CranlAppId))
            map["cranl_app_id"] = tenant.CranlAppId!;
        if (!string.IsNullOrEmpty(tenant.CranlRegion))
            map["cranl_region"] = tenant.CranlRegion!;
        return map.Count == 0 ? EmptyResourceIds : map;
    }

    private TenantEndpoints? TryBuildEndpoints(Tenant tenant)
    {
        if (tenant.CranlDatabaseUrlEncrypted is null
            || tenant.CranlDatabaseUrlEncrypted.Length == 0)
        {
            return null;
        }

        string databaseUrl;
        try
        {
            databaseUrl = _protector.Decrypt(tenant.CranlDatabaseUrlEncrypted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Cranl V2 provider: failed to decrypt DATABASE_URL for tenant {TenantId} — the encryption key may be misconfigured",
                tenant.Id);
            throw;
        }

        var engineUrl = !string.IsNullOrWhiteSpace(tenant.CranlAppUrl)
            ? $"https://{tenant.CranlAppUrl}"
            : null;

        return new TenantEndpoints(
            DatabaseUrl: databaseUrl,
            EngineHost: tenant.CranlAppUrl,
            EngineUrl: engineUrl,
            CustomDomain: null);
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyResourceIds =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
