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
    /// Record a pending authorization request. At most one OPEN
    /// (pending/granted) row per (principal, correlation, target) — a second
    /// request while one is open returns the existing row instead of
    /// inserting (the partial unique index arbitrates the race).
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
    /// a <c>group</c>-scoped grant (key = <paramref name="groupWire"/>) covers
    /// every member of that group. An expired grant does not cover; a consumed
    /// grant does not cover a second call. On success the grant's
    /// <c>ConsumedAtUtc</c> is stamped and the row returned; null when no
    /// covering grant exists.
    /// </summary>
    Task<ActionAuthorization?> TryConsumeAsync(
        Guid? tenantId,
        Guid? userId,
        string correlationId,
        string actionKeyWire,
        string groupWire,
        CancellationToken ct = default);

    /// <summary>Transition a row to granted/denied (the 43-9 decision path;
    /// shipped here so the state machine has one owner). Returns the updated
    /// row, or null when the row is missing, already decided, or expired.</summary>
    Task<ActionAuthorization?> DecideAsync(
        Guid id, bool granted, Guid decidedByUserId, string? reason,
        CancellationToken ct = default);
}
