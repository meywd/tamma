using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Story 43-5 (AC4) — the authorization ledger seam: one human decision covers
/// one run of a governed action. 43-9's seams call
/// <see cref="TryConsumeAsync"/> at gate time; the human decision endpoint
/// (transitioning <c>pending → granted/denied</c>) lands with Story 43-9.
/// Same CP residency + parallel-plane rules as
/// <see cref="IActionAssignmentRepository"/>.
/// </summary>
public interface IActionAuthorizationLedger
{
    /// <summary>
    /// Record a pending authorization request. At most one LIVE open
    /// (pending/granted, not past expiry) row per (principal, correlation,
    /// target) — a second request while one is live returns the existing row
    /// instead of inserting (the partial unique index arbitrates the race).
    /// A time-expired open row is transitioned to <c>expired</c> (CAS) and a
    /// fresh pending row minted, so an unattended request can never deadlock
    /// its (principal, correlation, target) key forever (adversarial review
    /// F3, 2026-07-29).
    /// </summary>
    Task<ActionAuthorization> RequestAsync(
        Guid? tenantId,
        Guid? userId,
        string correlationId,
        string targetKind,
        string targetKey,
        string? reason,
        int? autonomyLevelAtRequest,
        TimeSpan? ttl = null,
        CancellationToken ct = default);

    /// <summary>
    /// Consume a live grant covering <paramref name="actionKeyWire"/> for this
    /// (principal, correlation): an <c>action</c>-scoped grant covers itself;
    /// a <c>group</c>-scoped grant covers every member of that group, where
    /// membership is resolved from <c>ActionCatalog</c> INSIDE the ledger —
    /// never from caller input (adversarial review F2: a caller-supplied group
    /// wire let a grant for one group be consumed for an action outside it).
    /// An action key with no catalog entry can only be covered by an exact
    /// action-scoped grant. An expired grant does not cover; a consumed grant
    /// does not cover a second call. Consumption is a conditional
    /// single-statement UPDATE (CAS) — under concurrency exactly one caller
    /// consumes a given grant (F1). On success the grant's
    /// <c>ConsumedAtUtc</c> is stamped and the row returned; null when no
    /// covering grant exists (or every candidate was consumed concurrently).
    /// </summary>
    Task<ActionAuthorization?> TryConsumeAsync(
        Guid? tenantId,
        Guid? userId,
        string correlationId,
        string actionKeyWire,
        CancellationToken ct = default);

    /// <summary>Transition a row to granted/denied (the 43-9 decision path;
    /// shipped here so the state machine has one owner). A conditional
    /// single-statement UPDATE (<c>WHERE state = 'pending'</c> and not past
    /// expiry): under a concurrent grant-vs-deny race exactly one caller wins
    /// (F1). Returns the updated row, or null when the row is missing,
    /// already decided, expired, or lost the race.</summary>
    Task<ActionAuthorization?> DecideAsync(
        Guid id, bool granted, Guid decidedByUserId, string? reason,
        CancellationToken ct = default);
}
