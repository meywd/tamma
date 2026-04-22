namespace Tamma.Api.Services.Secrets;

/// <summary>
/// Story 28-12 — lifecycle phase of an in-progress (or completed) KEK
/// rotation. Surfaced via
/// <c>GET /api/admin/kek/rotate/status</c> so an operator can watch the
/// 90-minute runbook complete.
/// </summary>
public enum KekRotationPhase
{
    /// <summary>
    /// No rotation has ever been attempted or the previous rotation
    /// completed and the coordinator is back at rest.
    /// </summary>
    Idle,

    /// <summary>
    /// Coordinator has staged a new secondary KEK and is iterating
    /// over the tenant rows that still hold envelopes encrypted under
    /// the previous primary.
    /// </summary>
    Running,

    /// <summary>
    /// Every tenant row has been re-encrypted under the new KEK and
    /// the new key has been promoted to primary.
    /// </summary>
    Completed,

    /// <summary>
    /// Rotation aborted before completion. The previous primary KEK
    /// is still in place; partially re-encrypted rows have been bumped
    /// to the new <c>KekVersion</c>, but the old primary stays so the
    /// resolver fallback path keeps live traffic alive. Operator
    /// inspects the failure detail and re-runs.
    /// </summary>
    Failed,
}

/// <summary>
/// Snapshot of the rotation pipeline. <see cref="KekRotationPhase.Idle"/>
/// returns the empty record (zero counts, nullable timestamps).
/// </summary>
public sealed record KekRotationStatus(
    KekRotationPhase Phase,
    int FromVersion,
    int ToVersion,
    int TotalTenants,
    int ReencryptedTenants,
    int FailedTenants,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? FailureReason);
