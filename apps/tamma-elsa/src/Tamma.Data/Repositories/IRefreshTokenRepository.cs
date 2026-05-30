using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

public interface IRefreshTokenRepository
{
    /// <summary>
    /// Legacy overload retained for transitional callers. Mints a refresh
    /// token row with NULL <see cref="RefreshToken.TenantId"/> and NULL
    /// <see cref="RefreshToken.JtiChainHead"/>. New call sites should
    /// prefer <see cref="CreateAsync(Guid, Guid?, string, DateTime, Guid?)"/>
    /// so AC3's tenant binding is established at issue time.
    /// </summary>
    Task<RefreshToken> CreateAsync(Guid userId, string tokenHash, DateTime expiresAt);

    /// <summary>
    /// Story 28-9 AC3 — mint a refresh-token row carrying the tenant
    /// binding + JTI chain head.
    /// <para><paramref name="tenantId"/> is <c>null</c> for rootless
    /// tokens issued before the user picks an active tenant; non-null
    /// for tokens minted by login (with exactly one membership),
    /// switch-org, or refresh.</para>
    /// <para><paramref name="jtiChainHead"/> is the original JTI of the
    /// session lineage. The login + switch-org paths pass a freshly-
    /// generated UUID (this row is the head of its own chain); the
    /// refresh-rotation path copies the parent row's chain head so the
    /// lineage stays intact across rotations.</para>
    /// </summary>
    Task<RefreshToken> CreateAsync(
        Guid userId,
        Guid? tenantId,
        string tokenHash,
        DateTime expiresAt,
        Guid? jtiChainHead);

    Task<RefreshToken?> GetByTokenHashAsync(string tokenHash);

    /// <summary>
    /// Story 28-9 AC2 — acquires a Postgres <c>SELECT ... FOR UPDATE</c>
    /// row-lock on the user's most-recent active (non-revoked) refresh
    /// token and returns it (or <c>null</c> when the user has none —
    /// e.g. a rootless session that only ever held an access token).
    ///
    /// <para>This is the serialisation point for concurrent
    /// <c>/auth/switch-org</c> calls from the same user: the second
    /// caller blocks on the held row-lock until the first caller's
    /// switch-org transaction commits, then proceeds against the
    /// first caller's freshly-rotated state. MUST be called inside an
    /// open transaction on the same <see cref="ControlPlaneDbContext"/>
    /// so the lock is held for the duration of the revoke-old +
    /// insert-new sequence; the caller is responsible for opening that
    /// transaction.</para>
    ///
    /// <para>On the EF InMemory provider (unit tests) there is no real
    /// row-lock — the method degrades to an ordinary
    /// most-recent-active lookup, which is safe because in-process
    /// unit tests never race two switch-org calls against the same
    /// context. The production Npgsql path takes the real lock.</para>
    /// </summary>
    Task<RefreshToken?> FindActiveTokenForUpdateAsync(Guid userId);

    /// <summary>
    /// Legacy single-arg revoke; sets <see cref="RefreshToken.RevokedReason"/>
    /// to <see cref="RefreshTokenRevokedReasons.ManualLogout"/>. New call
    /// sites should pass an explicit reason via
    /// <see cref="RevokeAsync(Guid, string)"/> so SOC2 / SIEM tooling
    /// can distinguish a normal logout from a security event without
    /// re-querying <c>platform_events</c>.
    /// </summary>
    Task RevokeAsync(Guid id);

    /// <summary>
    /// Story 28-9 AC3 — revoke a single row with an explicit reason.
    /// <paramref name="reason"/> must be one of the values in
    /// <see cref="RefreshTokenRevokedReasons"/>; the DB-level CHECK
    /// constraint rejects anything else.
    /// </summary>
    Task RevokeAsync(Guid id, string reason);

    /// <summary>
    /// Revokes every active refresh token for the user and returns the number
    /// of rows that flipped from active → revoked. Story 28-R2 / H2 — the
    /// count is surfaced in the <c>USER.LOGOUT_ALL.SUCCESS</c> /
    /// <c>USER.ORG_SWITCHED.SUCCESS</c> audit events so SIEM can flag mass
    /// revocations (e.g. attacker burning every device after credential
    /// theft). A return value of 0 means the call was a no-op (already
    /// revoked / never had any).
    ///
    /// <para>Story 28-9 AC3 — all revoked rows are stamped with
    /// <see cref="RefreshTokenRevokedReasons.LogoutAll"/> (default) or a
    /// caller-supplied value via the <see cref="RevokeAllForUserAsync(Guid, string)"/>
    /// overload.</para>
    /// </summary>
    Task<int> RevokeAllForUserAsync(Guid userId);

    /// <summary>
    /// Story 28-9 AC3 — overload taking an explicit revoke reason so the
    /// switch-org / password-reset / admin-force-logout paths can
    /// distinguish themselves from a normal user-initiated logout-all in
    /// the audit log.
    /// </summary>
    Task<int> RevokeAllForUserAsync(Guid userId, string reason);

    /// <summary>
    /// Story 28-9 AC3 — reuse-detection lookup. Returns every active
    /// (non-revoked) refresh token sharing the given chain head. Used by
    /// <see cref="RevokeChainAsync"/> when a revoked-then-presented token
    /// burns its entire lineage.
    /// </summary>
    Task<IReadOnlyList<RefreshToken>> FindByJtiChainHeadAsync(Guid chainHead);

    /// <summary>
    /// Story 28-9 AC3 — revokes every active refresh token sharing the
    /// given <paramref name="chainHead"/>. Called by the refresh endpoint
    /// when reuse-detection fires: an already-revoked token presented to
    /// <c>/auth/refresh</c> burns the whole lineage so the attacker
    /// (holding any snapshot) is locked out atomically.
    /// Returns the number of rows that flipped from active → revoked.
    /// </summary>
    Task<int> RevokeChainAsync(Guid chainHead, string reason);

    Task<int> CleanExpiredAsync();
}
