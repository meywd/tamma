using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

public interface IInviteRepository
{
    Task<UserInvite> CreateAsync(UserInvite invite);
    Task<UserInvite?> GetByIdAsync(Guid id);
    Task<UserInvite?> GetByTokenHashAsync(string tokenHash);
    Task AcceptAsync(Guid id);
    Task<List<UserInvite>> ListPendingByTenantAsync(Guid tenantId);

    /// <summary>
    /// Delete the invite iff <paramref name="id"/> exists and belongs to
    /// <paramref name="tenantId"/>. Returns <c>true</c> on deletion,
    /// <c>false</c> when no matching row existed. Drives the 404 path in
    /// finding 016 and the tenant-scope check for cross-tenant revoke.
    /// </summary>
    Task<bool> DeleteScopedAsync(Guid tenantId, Guid id);

    /// <summary>
    /// Look up the invite by id only when it lives under
    /// <paramref name="tenantId"/>. Used by the resend handler in story
    /// 18-7 — guards against cross-tenant id spoofing the same way
    /// <see cref="DeleteScopedAsync"/> does for delete.
    /// </summary>
    Task<UserInvite?> GetByIdScopedAsync(Guid tenantId, Guid id);

    /// <summary>
    /// Extend an invite's <c>ExpiresAt</c> to <paramref name="newExpiresAt"/>.
    /// Used by the resend handler — caller must pre-validate that the
    /// invite belongs to the tenant (via <see cref="GetByIdScopedAsync"/>),
    /// is not yet accepted, and has not already expired.
    /// </summary>
    Task ExtendExpiryAsync(Guid id, DateTime newExpiresAt);

    /// <summary>Delete all invites for a tenant (cascade on tenant purge).</summary>
    Task<int> DeleteAllByTenantAsync(Guid tenantId);

    [Obsolete("Use DeleteScopedAsync for per-tenant invariant. Kept for transitional callers.")]
    Task DeleteAsync(Guid id);
}
