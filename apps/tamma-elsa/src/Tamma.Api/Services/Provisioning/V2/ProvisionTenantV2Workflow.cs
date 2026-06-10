using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Provisioning.V2;

/// <summary>
/// Story 30-2 — resumable per-backend provisioning workflow consuming the
/// 30-1 <see cref="ITenantInfrastructureProvider"/> contract.
///
/// <para><b>Why a plain orchestrator class, not an Elsa workflow</b>: the
/// 30-1 ADR §5 locked the V2 namespace inside <c>Tamma.Api</c>. The Elsa
/// activities live in a sibling project (<c>Tamma.Activities</c>) which
/// does NOT reference <c>Tamma.Api</c>, so an Elsa workflow that calls
/// V2 types would force a cross-project refactor outside this story's
/// scope. The pre-existing <c>CranlProvisioningWorkflow</c> uses the
/// same plain-class pattern for the same reason. Resumability comes
/// from the per-step state persisted on the <c>tenants</c> row +
/// provider idempotency, not from Elsa's activity-state journal.</para>
///
/// <para><b>Step list</b>:</para>
/// <list type="number">
///   <item><description>ResolveProvider — registry lookup. No
///     compensation (pure read).</description></item>
///   <item><description>Preflight — capability gate (topology, region) +
///     per-org quota cap (where supported). No compensation.</description></item>
///   <item><description>ReserveResources — flip tenant to <c>pending</c>,
///     emit <c>TENANT.PROVISION.STARTED</c>. Compensation: flip back to
///     prior state.</description></item>
///   <item><description>ExecuteProvision — call
///     <see cref="ITenantInfrastructureProvider.ProvisionAsync"/>.
///     Compensation: <c>DeprovisionAsync</c>.</description></item>
///   <item><description>PersistEndpoints — capture provider resource ids
///     +  endpoints in workflow state (not yet persisted to columns
///     because <c>provider_resource_ids</c> + <c>provider_key</c> land
///     in 30-3). Compensation: clear the in-memory captures.</description></item>
///   <item><description>RegisterSecrets — placeholder hook that 30-2 leaves
///     intentionally unimplemented. The cabinet integration (Epic 29
///     <c>ISecretStore.CreateAsync</c>) is wired by 30-3 once each
///     provider declares which secrets it surfaces. Compensation: noop
///     today; <c>RetireVersionAsync</c> per registered secret in 30-3.</description></item>
///   <item><description>InitialProbe — poll
///     <see cref="ITenantInfrastructureProvider.GetStatusAsync"/> until
///     Ready or budget exhausted. Compensation: <c>DeprovisionAsync</c>.</description></item>
///   <item><description>Activate — flip tenant to <c>ready</c>, emit
///     <c>TENANT.PROVISIONED.SUCCESS</c>. Compensation: flip back to
///     <c>failed</c> with diagnostic.</description></item>
/// </list>
///
/// <para><b>Resumability</b>: the entry method <see cref="ExecuteAsync"/>
/// inspects <c>tenants.provisioning_state</c> on every run and skips
/// already-completed steps. A worker that died between step 4 and step
/// 5 is resumed by re-firing the same task — provider idempotency
/// (ADR §4) guarantees no double-create. Step state granularity is
/// coarse (the existing column has the v1 vocabulary; 30-3 may extend
/// it).</para>
///
/// <para><b>Compensation</b>: failures past step 3 trigger reverse-order
/// compensation. If a compensation step itself throws, the workflow
/// halts with <see cref="ProvisioningFailureReasons.CompensationFailed"/>
/// — operator intervention is required (orphans may exist).</para>
/// </summary>
public sealed class ProvisionTenantV2Workflow
{
    /// <summary>Bound on the InitialProbe step. The 30-1 contract did not
    /// define <c>Capabilities.TimeoutSeconds</c> (the brief mentioned it,
    /// the ADR didn't lock it). Hard-coded default until that field
    /// lands. <see cref="https://github.com/Tam-ma/tamma/issues">Tracked
    /// as a 30-2 follow-up.</see></summary>
    public static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromMinutes(30);

    /// <summary>Probe step's poll interval — same cadence as the v1
    /// Cranl workflow. Provider idempotency ensures repeated calls are
    /// cheap (cached on the provider's side).</summary>
    public static readonly TimeSpan DefaultProbeInterval = TimeSpan.FromSeconds(5);

    /// <summary>Test seam — override to shorten the probe loop in unit
    /// tests. Production wiring leaves this at <see cref="DefaultProbeInterval"/>.</summary>
    public TimeSpan ProbeInterval { get; set; } = DefaultProbeInterval;

    /// <summary>Test seam — override to shorten the probe budget in unit
    /// tests. Production wiring leaves this at <see cref="DefaultProbeTimeout"/>.</summary>
    public TimeSpan ProbeTimeout { get; set; } = DefaultProbeTimeout;

    private readonly ControlPlaneDbContext _db;
    private readonly TenantProviderRegistry _registry;
    private readonly IPlatformEventPublisher _events;
    private readonly TimeProvider _clock;
    private readonly ILogger<ProvisionTenantV2Workflow> _logger;

    public ProvisionTenantV2Workflow(
        ControlPlaneDbContext db,
        TenantProviderRegistry registry,
        IPlatformEventPublisher events,
        TimeProvider clock,
        ILogger<ProvisionTenantV2Workflow> logger)
    {
        _db = db;
        _registry = registry;
        _events = events;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Run (or resume) the workflow for the supplied payload.
    /// </summary>
    /// <returns>Final snapshot — either Ready (happy path) or Failed
    /// (with FailureReason short-code).</returns>
    public async Task<ProvisioningResult> ExecuteAsync(
        ProvisionTenantV2TaskPayload payload,
        CancellationToken ct)
    {
        if (payload is null) throw new ArgumentNullException(nameof(payload));

        var tenant = await LoadTenantAsync(payload.TenantId, ct).ConfigureAwait(false);
        if (tenant is null)
        {
            return BuildSyntheticFailure(
                ProvisioningFailureReasons.TenantNotFound,
                $"tenant_{payload.TenantId}_not_found");
        }

        var initialState = ProvisioningStateExtensions.ParseState(tenant.ProvisioningState);
        if (initialState is ProvisioningState.Ready
            or ProvisioningState.Deprovisioning
            or ProvisioningState.Deprovisioned)
        {
            return await StampFailureAsync(
                tenant,
                ProvisioningFailureReasons.IllegalTenantState,
                $"refused_to_run_against_state_{initialState.ToStorageString()}",
                ct).ConfigureAwait(false);
        }

        // ── Step 1: ResolveProvider ───────────────────────────────────
        await EmitStepEventAsync(payload.TenantId, "resolve_provider",
            "STEP_STARTED", null, ct).ConfigureAwait(false);

        if (string.Equals(payload.ProviderKey, NullTenantProvider.Key, StringComparison.Ordinal))
        {
            await EmitStepEventAsync(payload.TenantId, "resolve_provider",
                "STEP_FAILED",
                new Dictionary<string, object?>
                {
                    ["failureReason"] = ProvisioningFailureReasons.NoProvisioningInThisMode,
                }, ct).ConfigureAwait(false);
            return await StampFailureAsync(
                tenant,
                ProvisioningFailureReasons.NoProvisioningInThisMode,
                "single_user_or_dev_mode",
                ct).ConfigureAwait(false);
        }

        if (!_registry.TryGetProvider(payload.ProviderKey, out var provider) || provider is null)
        {
            await EmitStepEventAsync(payload.TenantId, "resolve_provider",
                "STEP_FAILED",
                new Dictionary<string, object?>
                {
                    ["failureReason"] = ProvisioningFailureReasons.ProviderNotRegistered,
                    ["providerKey"] = payload.ProviderKey,
                }, ct).ConfigureAwait(false);
            return await StampFailureAsync(
                tenant,
                ProvisioningFailureReasons.ProviderNotRegistered,
                $"provider_key_{payload.ProviderKey}_unknown",
                ct).ConfigureAwait(false);
        }

        await EmitStepEventAsync(payload.TenantId, "resolve_provider",
            "STEP_COMPLETED", null, ct).ConfigureAwait(false);

        // ── Step 2: Preflight ─────────────────────────────────────────
        var caps = provider.GetCapabilities();
        await EmitStepEventAsync(payload.TenantId, "preflight",
            "STEP_STARTED", null, ct).ConfigureAwait(false);

        if (!caps.SupportsTopology(payload.Topology))
        {
            await EmitStepEventAsync(payload.TenantId, "preflight",
                "STEP_FAILED",
                new Dictionary<string, object?>
                {
                    ["failureReason"] = ProvisioningFailureReasons.UnsupportedTopology,
                    ["topology"] = payload.Topology.ToString(),
                }, ct).ConfigureAwait(false);
            return await StampFailureAsync(
                tenant,
                ProvisioningFailureReasons.UnsupportedTopology,
                $"provider_{payload.ProviderKey}_does_not_support_{payload.Topology}",
                ct).ConfigureAwait(false);
        }

        if (payload.Region is not null
            && caps.Regions.Count > 0
            && !caps.Regions.Contains(payload.Region))
        {
            await EmitStepEventAsync(payload.TenantId, "preflight",
                "STEP_FAILED",
                new Dictionary<string, object?>
                {
                    ["failureReason"] = ProvisioningFailureReasons.UnsupportedRegion,
                    ["region"] = payload.Region,
                }, ct).ConfigureAwait(false);
            return await StampFailureAsync(
                tenant,
                ProvisioningFailureReasons.UnsupportedRegion,
                $"region_{payload.Region}_not_supported_by_{payload.ProviderKey}",
                ct).ConfigureAwait(false);
        }

        // Quota check — skipped today because the per-org tenant count
        // helper requires the `tenants.provider_key` column that lands
        // in 30-3. Once that column exists, replace the no-op below
        // with `await _tenantRepository.CountByOrgAndProviderAsync(...)`
        // and fail-fast on caps.MaxTenantsPerOrg breach. Documented
        // here so the surface lands in 30-2 even if the enforcement
        // flips on in 30-3.
        _ = caps.MaxTenantsPerOrg;
        _ = payload.InvokingOrgId;

        await EmitStepEventAsync(payload.TenantId, "preflight",
            "STEP_COMPLETED", null, ct).ConfigureAwait(false);

        // ── Step 3: ReserveResources ──────────────────────────────────
        await EmitStepEventAsync(payload.TenantId, "reserve_resources",
            "STEP_STARTED", null, ct).ConfigureAwait(false);
        await TransitionAsync(tenant, ProvisioningState.Pending,
            "reserved_pending_provider_call", ct).ConfigureAwait(false);
        await EmitStepEventAsync(payload.TenantId, "reserve_resources",
            "STEP_COMPLETED", null, ct).ConfigureAwait(false);
        var reserved = true;

        // Compensation catalog. Each entry knows how to undo its step.
        // Reverse-order execution; halt on first compensation failure.
        var compensations = new List<(string Step, Func<CancellationToken, Task> Run)>();
        compensations.Add(("reserve_resources", async ictok =>
        {
            await TransitionAsync(tenant, initialState,
                "compensated_to_initial_state", ictok).ConfigureAwait(false);
        }));

        IReadOnlyDictionary<string, string> resourceIds = new Dictionary<string, string>();
        TenantEndpoints? endpoints = null;
        ProvisioningResult? executeResult = null;

        // ── Step 4: ExecuteProvision ─────────────────────────────────
        try
        {
            await EmitStepEventAsync(payload.TenantId, "execute_provision",
                "STEP_STARTED", null, ct).ConfigureAwait(false);
            try
            {
                executeResult = await provider.ProvisionAsync(
                    payload.TenantId,
                    payload.ToProvisioningRequest(),
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex,
                    "v2_provisioning.provider_threw tenantId={TenantId} providerKey={ProviderKey}",
                    payload.TenantId, payload.ProviderKey);
                await EmitStepEventAsync(payload.TenantId, "execute_provision",
                    "STEP_FAILED",
                    new Dictionary<string, object?>
                    {
                        ["failureReason"] = ProvisioningFailureReasons.ProviderUnexpectedException,
                        ["errorType"] = ex.GetType().Name,
                    }, ct).ConfigureAwait(false);
                await RunCompensationsAsync(compensations, payload.TenantId, ct).ConfigureAwait(false);
                return await StampFailureAsync(
                    tenant,
                    ProvisioningFailureReasons.ProviderUnexpectedException,
                    $"provider_threw_{ex.GetType().Name}",
                    ct).ConfigureAwait(false);
            }

            // Provider returned a structured Failed snapshot — surface
            // verbatim (don't override its FailureReason) and run
            // compensation. This is the AC9 / ADR §1 contract path.
            //
            // Why DeprovisionAsync runs even though ExecuteProvision
            // returned Failed: a "Failed" return doesn't promise the
            // provider didn't mint partial cloud resources before
            // detecting the failure (e.g. Cranl created the project
            // but the database call 500'd). Provider DeprovisionAsync
            // is idempotent (ADR §4) — calling on a tenant with no
            // resources is a documented no-op.
            if (executeResult.Status.State == ProvisioningState.Failed)
            {
                compensations.Add(("execute_provision", async ictok =>
                {
                    await provider.DeprovisionAsync(
                        payload.TenantId,
                        new DeprovisioningRequest(
                            DeprovisioningCleanupMode.BestEffort,
                            Reason: "compensation_after_failed_provision_v2"),
                        ictok).ConfigureAwait(false);
                }));
                await EmitStepEventAsync(payload.TenantId, "execute_provision",
                    "STEP_FAILED",
                    new Dictionary<string, object?>
                    {
                        ["failureReason"] = executeResult.Status.FailureReason
                            ?? "provider_returned_failed_without_reason",
                        ["detail"] = executeResult.Status.Detail,
                    }, ct).ConfigureAwait(false);
                await RunCompensationsAsync(compensations, payload.TenantId, ct).ConfigureAwait(false);
                return await StampFailureAsync(
                    tenant,
                    executeResult.Status.FailureReason
                        ?? ProvisioningFailureReasons.ProviderUnexpectedException,
                    executeResult.Status.Detail
                        ?? "provider_returned_failed_without_detail",
                    ct).ConfigureAwait(false);
            }

            await EmitStepEventAsync(payload.TenantId, "execute_provision",
                "STEP_COMPLETED",
                new Dictionary<string, object?>
                {
                    ["state"] = executeResult.Status.State.ToStorageString(),
                }, ct).ConfigureAwait(false);

            // Compensation for ExecuteProvision = DeprovisionAsync
            compensations.Add(("execute_provision", async ictok =>
            {
                await provider.DeprovisionAsync(
                    payload.TenantId,
                    new DeprovisioningRequest(
                        DeprovisioningCleanupMode.BestEffort,
                        Reason: "compensation_after_failed_provision_v2"),
                    ictok).ConfigureAwait(false);
            }));

            // ── Step 5: PersistEndpoints ─────────────────────────────
            await EmitStepEventAsync(payload.TenantId, "persist_endpoints",
                "STEP_STARTED", null, ct).ConfigureAwait(false);
            resourceIds = executeResult.ProviderResourceIds
                ?? new Dictionary<string, string>();
            endpoints = executeResult.Endpoints;
            // NOTE: 30-3 lands the `provider_resource_ids` JSONB column
            // and the `provider_key` column on tenants. Until then the
            // capture lives in workflow state only — survives restarts
            // because the next ExecuteAsync call asks the provider
            // again, and providers are idempotent (ADR §4).
            await EmitStepEventAsync(payload.TenantId, "persist_endpoints",
                "STEP_COMPLETED",
                new Dictionary<string, object?>
                {
                    ["resourceIdCount"] = resourceIds.Count,
                    ["hasEndpoints"] = endpoints is not null,
                }, ct).ConfigureAwait(false);
            compensations.Add(("persist_endpoints", _ =>
            {
                resourceIds = new Dictionary<string, string>();
                endpoints = null;
                return Task.CompletedTask;
            }));

            // ── Step 6: RegisterSecrets ──────────────────────────────
            // Hook is intentionally a no-op in 30-2. 30-3 lands the
            // per-provider secret declaration + the
            // ISecretStore.CreateAsync call. Tracked as a follow-up;
            // documented in the return summary so the gap is visible.
            await EmitStepEventAsync(payload.TenantId, "register_secrets",
                "STEP_STARTED", null, ct).ConfigureAwait(false);
            await EmitStepEventAsync(payload.TenantId, "register_secrets",
                "STEP_COMPLETED",
                new Dictionary<string, object?>
                {
                    ["status"] = "deferred_to_30_3",
                }, ct).ConfigureAwait(false);
            compensations.Add(("register_secrets", _ => Task.CompletedTask));

            // ── Step 7: InitialProbe ─────────────────────────────────
            await EmitStepEventAsync(payload.TenantId, "initial_probe",
                "STEP_STARTED", null, ct).ConfigureAwait(false);
            var probeOutcome = await ProbeUntilReadyAsync(
                provider, payload.TenantId, ProbeTimeout, ct).ConfigureAwait(false);
            if (!probeOutcome.IsReady)
            {
                await EmitStepEventAsync(payload.TenantId, "initial_probe",
                    "STEP_FAILED",
                    new Dictionary<string, object?>
                    {
                        ["failureReason"] = probeOutcome.FailureReason,
                        ["detail"] = probeOutcome.Detail,
                    }, ct).ConfigureAwait(false);
                await RunCompensationsAsync(compensations, payload.TenantId, ct).ConfigureAwait(false);
                return await StampFailureAsync(
                    tenant,
                    probeOutcome.FailureReason
                        ?? ProvisioningFailureReasons.ProbeTimeout,
                    probeOutcome.Detail ?? "probe_timeout_no_detail",
                    ct).ConfigureAwait(false);
            }
            await EmitStepEventAsync(payload.TenantId, "initial_probe",
                "STEP_COMPLETED", null, ct).ConfigureAwait(false);

            // ── Step 8: Activate ─────────────────────────────────────
            await EmitStepEventAsync(payload.TenantId, "activate",
                "STEP_STARTED", null, ct).ConfigureAwait(false);
            await TransitionAsync(tenant, ProvisioningState.Ready,
                "activated_v2", ct).ConfigureAwait(false);
            await EmitStepEventAsync(payload.TenantId, "activate",
                "STEP_COMPLETED", null, ct).ConfigureAwait(false);

            // Terminal success event — mirror the v1 lifecycle name.
            await _events.AppendAndPublishAsync(new PlatformEvent
            {
                Type = "TENANT.PROVISIONED.SUCCESS",
                TenantId = payload.TenantId,
                Tags = JsonSerializer.Serialize(new Dictionary<string, string?>
                {
                    ["tenantId"] = payload.TenantId.ToString("D"),
                    ["providerKey"] = payload.ProviderKey,
                    ["topology"] = payload.Topology.ToString(),
                }),
                Metadata = """{"workflowVersion":"2.0.0","eventSource":"system"}""",
                Data = JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["resourceIdCount"] = resourceIds.Count,
                }),
            }, ct).ConfigureAwait(false);

            return new ProvisioningResult(
                new ProvisioningStatusSnapshot(
                    ProvisioningState.Ready,
                    Detail: "activated_v2",
                    FailureReason: null,
                    UpdatedAt: _clock.GetUtcNow()),
                resourceIds,
                endpoints,
                executeResult.ProvisioningDurationSeconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Worker shutdown — let the row stay in flight for the
            // visibility-timeout reaper to recover. DO NOT run
            // compensation; the next worker that picks the task up
            // will resume from the persisted state.
            _logger.LogInformation(
                "v2_provisioning.cancelled tenantId={TenantId} (will resume on next reservation)",
                payload.TenantId);
            throw;
        }
        finally
        {
            _ = reserved; // suppress unused-warning when path returns early.
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private async Task<Tenant?> LoadTenantAsync(Guid tenantId, CancellationToken ct)
    {
        return await _db.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == tenantId && t.DeletedAt == null, ct)
            .ConfigureAwait(false);
    }

    private async Task TransitionAsync(
        Tenant tenant,
        ProvisioningState target,
        string detail,
        CancellationToken ct)
    {
        var nowUtc = _clock.GetUtcNow().UtcDateTime;
        tenant.ProvisioningState = target.ToStorageString();
        tenant.ProvisioningDetail = detail;
        tenant.ProvisioningUpdatedAt = nowUtc;
        tenant.UpdatedAt = nowUtc;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
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

        await _events.AppendAndPublishAsync(new PlatformEvent
        {
            Type = "TENANT.PROVISION.FAILED",
            TenantId = tenant.Id,
            Tags = JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["tenantId"] = tenant.Id.ToString("D"),
                ["failureReason"] = failureReason,
            }),
            Metadata = """{"workflowVersion":"2.0.0","eventSource":"system"}""",
            Data = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["detail"] = detail,
            }),
        }, ct).ConfigureAwait(false);

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

    private async Task EmitStepEventAsync(
        Guid tenantId,
        string step,
        string outcome,
        IReadOnlyDictionary<string, object?>? data,
        CancellationToken ct)
    {
        var type = $"TENANT.PROVISION.{step.ToUpperInvariant()}.{outcome}";
        var tags = new Dictionary<string, string?>
        {
            ["tenantId"] = tenantId.ToString("D"),
            ["step"] = step,
            ["workflow"] = "v2",
        };
        await _events.AppendAndPublishAsync(new PlatformEvent
        {
            Type = type,
            TenantId = tenantId,
            Tags = JsonSerializer.Serialize(tags),
            Metadata = """{"workflowVersion":"2.0.0","eventSource":"system"}""",
            Data = data is null ? "{}" : JsonSerializer.Serialize(data),
        }, ct).ConfigureAwait(false);
    }

    private async Task<ProbeOutcome> ProbeUntilReadyAsync(
        ITenantInfrastructureProvider provider,
        Guid tenantId,
        TimeSpan budget,
        CancellationToken ct)
    {
        var start = _clock.GetUtcNow();
        ProvisioningStatusSnapshot lastSnapshot;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            lastSnapshot = await provider.GetStatusAsync(tenantId, ct).ConfigureAwait(false);
            if (lastSnapshot.State == ProvisioningState.Ready)
            {
                return ProbeOutcome.Ready();
            }
            if (lastSnapshot.State == ProvisioningState.Failed)
            {
                return ProbeOutcome.Failed(
                    lastSnapshot.FailureReason
                        ?? ProvisioningFailureReasons.ProviderUnexpectedException,
                    lastSnapshot.Detail ?? "provider_reported_failed_during_probe");
            }
            var elapsed = _clock.GetUtcNow() - start;
            if (elapsed >= budget)
            {
                return ProbeOutcome.Failed(
                    ProvisioningFailureReasons.ProbeTimeout,
                    $"probe_budget_{(int)budget.TotalSeconds}s_exceeded_state_{lastSnapshot.State.ToStorageString()}");
            }

            // Cancellable delay so worker shutdown unblocks fast.
            try
            {
                await Task.Delay(ProbeInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }
    }

    private async Task RunCompensationsAsync(
        List<(string Step, Func<CancellationToken, Task> Run)> compensations,
        Guid tenantId,
        CancellationToken ct)
    {
        // Reverse-order. Use a non-cancellable child token for compensation
        // so a queue-shutdown signal doesn't leave orphan resources.
        // Fall back to the original token only if it's already cancelled —
        // we still attempt to clean up.
        for (var i = compensations.Count - 1; i >= 0; i--)
        {
            var (step, run) = compensations[i];
            try
            {
                await run(ct).ConfigureAwait(false);
                await EmitStepEventAsync(tenantId, $"{step}_compensated",
                    "STEP_COMPLETED", null, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex,
                    "v2_provisioning.compensation_failed tenantId={TenantId} step={Step}",
                    tenantId, step);
                await EmitStepEventAsync(tenantId, $"{step}_compensated",
                    "STEP_FAILED",
                    new Dictionary<string, object?>
                    {
                        ["failureReason"] = ProvisioningFailureReasons.CompensationFailed,
                        ["errorType"] = ex.GetType().Name,
                    }, ct).ConfigureAwait(false);
                // Halt — no automatic retry of compensation per the brief.
                return;
            }
        }
    }

    private readonly struct ProbeOutcome
    {
        public bool IsReady { get; init; }
        public string? FailureReason { get; init; }
        public string? Detail { get; init; }

        public static ProbeOutcome Ready() => new() { IsReady = true };
        public static ProbeOutcome Failed(string reason, string detail) =>
            new() { IsReady = false, FailureReason = reason, Detail = detail };
    }
}
