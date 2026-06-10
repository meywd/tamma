namespace Tamma.Api.Services.Provisioning.V2;

/// <summary>
/// State machine for v2 provisioning. Same set of transitions as the v1
/// <see cref="Tamma.Api.Services.Provisioning.ProvisioningState"/> (so
/// the existing <c>tenants.provisioning_state</c> column carries forward
/// without churn) but flagged "snapshot" because we return it embedded
/// in <see cref="ProvisioningResult"/> rather than as the state column
/// itself.
/// </summary>
/// <remarks>
/// Story 30-1 (this story) deliberately does not mint a new state-machine
/// vocabulary — Story 28-1's vocabulary already covers every transition
/// every Epic 30 backend needs. The v2 layer just wraps the same enum in
/// a snapshot record that pairs the state with a human-readable detail
/// string + an updated-at timestamp.
/// </remarks>
/// <param name="State">Current state. See
/// <see cref="Tamma.Api.Services.Provisioning.ProvisioningState"/>.</param>
/// <param name="Detail">Free-text status detail for the most recent
/// transition (e.g. <c>"shared_infrastructure_no_cranl_configured"</c>,
/// <c>"cranl_db_ready_polling_app"</c>). May be <c>null</c>.</param>
/// <param name="FailureReason">When <see cref="State"/> is
/// <see cref="Tamma.Api.Services.Provisioning.ProvisioningState.Failed"/>,
/// a structured short code the dispatch workflow uses to decide retry vs.
/// surface-to-operator (e.g. <c>"unsupported_topology"</c>,
/// <c>"cranl_db_create_failed"</c>). <c>null</c> for non-failed states.</param>
/// <param name="UpdatedAt">When the snapshot was taken.</param>
public sealed record ProvisioningStatusSnapshot(
    ProvisioningState State,
    string? Detail,
    string? FailureReason,
    DateTimeOffset UpdatedAt);
