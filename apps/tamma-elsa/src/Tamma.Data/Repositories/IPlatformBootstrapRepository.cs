namespace Tamma.Data.Repositories;

/// <summary>
/// Story 28-R2 / PF-S9 — single-row sentinel that pins which user
/// owns the bootstrap superadmin promotion. Closes the previous
/// TOCTOU race where two concurrent first-user registrations both
/// observed <c>existingUserCount == 0</c> and both received
/// <c>platform_admin</c>.
///
/// <para>Concurrency model: every registration calls
/// <see cref="TryClaimAsync"/>. The first caller inserts the
/// single sentinel row and returns <c>true</c> (caller's user is
/// promoted to <c>platform_admin</c>). Every subsequent caller (and
/// every concurrent loser) catches the unique-key violation and
/// returns <c>false</c> (caller becomes a regular <c>"user"</c>).</para>
/// </summary>
public interface IPlatformBootstrapRepository
{
    /// <summary>
    /// Try to claim the bootstrap superadmin sentinel for
    /// <paramref name="userId"/>. Returns <c>true</c> exactly once
    /// per deployment; every subsequent call returns <c>false</c>.
    /// </summary>
    /// <remarks>
    /// Implementation must rely on the schema's unique-PK + CHECK
    /// (Id = 1) constraint to keep the operation atomic — counting
    /// rows + inserting is NOT good enough because two concurrent
    /// transactions can both see "no rows" and both insert under
    /// READ COMMITTED.
    /// </remarks>
    Task<bool> TryClaimAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Read-only check. <c>true</c> when the bootstrap sentinel has
    /// already been claimed (by anyone). Used by callers that want to
    /// know whether to bother attempting the insert.
    /// </summary>
    Task<bool> HasBeenClaimedAsync(CancellationToken ct = default);
}
