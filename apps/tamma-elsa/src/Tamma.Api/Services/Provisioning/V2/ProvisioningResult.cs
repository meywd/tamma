namespace Tamma.Api.Services.Provisioning.V2;

/// <summary>
/// What
/// <see cref="ITenantInfrastructureProvider.ProvisionAsync"/> returns. Per
/// the brief, <c>ProvisionAsync</c> usually returns <c>Pending</c> immediately
/// — the long-running cloud-API walk happens on a background queue
/// (today's <c>TaskQueueProcessor</c> / Story 30-2's Elsa workflow). The
/// provider then updates its persistent state and subsequent
/// <see cref="ITenantInfrastructureProvider.GetStatusAsync"/> calls reflect
/// the latest transition.
/// </summary>
/// <param name="Status">Snapshot of the state machine after the call.</param>
/// <param name="ProviderResourceIds">Provider-specific identifiers minted
/// during provisioning (e.g. <c>{"cranl_project_id": "...", "cranl_app_id":
/// "..."}</c> for Cranl, <c>{"hetzner_server_id": "..."}</c> for Hetzner).
/// Persisted on <c>tenants.provider_resource_ids</c> (JSONB) by the
/// dispatch workflow. Empty dictionary when nothing has been minted yet
/// (e.g. the first <c>Pending</c> snapshot).</param>
/// <param name="Endpoints">Connection details for the freshly-provisioned
/// tenant. <c>null</c> until the underlying database is ready (i.e. while
/// <see cref="ProvisioningStatus.State"/> is one of the early states).</param>
/// <param name="ProvisioningDurationSeconds">Wall-clock seconds the
/// provider has spent on this provisioning run so far. Useful for the
/// cost dashboard (Story 30-10) and for SLO alerts. <c>null</c> when the
/// provider has no elapsed-time tracking (e.g. the null provider).</param>
public sealed record ProvisioningResult(
    ProvisioningStatusSnapshot Status,
    IReadOnlyDictionary<string, string> ProviderResourceIds,
    TenantEndpoints? Endpoints = null,
    double? ProvisioningDurationSeconds = null);
