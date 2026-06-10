namespace Tamma.Data.Entities;

/// <summary>
/// Persistent refresh-token row. One physical row per token in the user's
/// refresh-token rotation chain; the chain is bound together by
/// <see cref="JtiChainHead"/> so reuse-detection can revoke every descendant
/// of a compromised token in a single update.
///
/// <para><b>Story 28-9 AC3</b> — refresh tokens are tenant-scoped. Each row
/// carries a <see cref="TenantId"/> (the tenant the access-token pair is
/// minted for) so a refresh token issued in tenant A can never mint an
/// access token for tenant B. The DB binding is defence in depth: the
/// access-token's <c>tenantId</c> claim is the inbound check, but the
/// refresh row's <c>TenantId</c> column is the durable truth that survives
/// access-token expiry. <see cref="TenantId"/> is nullable for rootless
/// refresh tokens minted before a user picks an active tenant (login with
/// 0 or 2+ memberships per AC4).</para>
///
/// <para><see cref="JtiChainHead"/> is the original JTI of the first
/// access-token issued from this rotation lineage. Rotation copies the
/// parent's chain head onto the child; reuse-detection at the
/// <c>/auth/refresh</c> endpoint revokes every active token sharing the
/// presented (revoked) token's chain head so an attacker holding any
/// snapshot of the lineage is locked out in one shot. Nullable to keep the
/// schema migration backwards-compatible with rows minted before this
/// story landed — those rows behave as if each were its own chain head
/// (no cross-row revocation).</para>
///
/// <para><see cref="RevokedReason"/> records WHY the row flipped from
/// active to revoked, so SOC2 / SIEM tooling can distinguish a normal
/// logout from a security event without consulting <c>platform_events</c>.
/// Closed enum of strings — see the constants below.</para>
///
/// <para><b>Logout-all + JtiChainHead semantics</b>: when
/// <c>/auth/logout?all=true</c> revokes tokens across tenants, the
/// <c>JtiChainHead</c> values stay valid pointers into the now-revoked
/// lineage. A subsequent login starts a new chain — no stitching is
/// required across login boundaries.</para>
/// </summary>
public class RefreshToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = null!;
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Story 28-9 AC3 — the tenant the access/refresh pair is scoped to,
    /// or <c>null</c> for a rootless token (user has 0 or 2+ memberships
    /// at login per AC4). A non-null value is the binding that prevents
    /// cross-tenant refresh replay: a request asking to refresh against a
    /// different tenant returns 400 <c>tenant_mismatch_on_refresh</c>.
    /// </summary>
    public Guid? TenantId { get; set; }

    /// <summary>
    /// Story 28-9 AC3 — the JTI of the first access token in this rotation
    /// lineage. Rotation copies the parent's chain head onto the child so
    /// every row in the lineage shares one chain head. Reuse-detection
    /// revokes the entire lineage in a single UPDATE when an already-
    /// revoked token is presented to <c>/auth/refresh</c>.
    /// Nullable for backwards compatibility with rows minted before this
    /// story landed.
    /// </summary>
    public Guid? JtiChainHead { get; set; }

    /// <summary>
    /// Story 28-9 AC3 — closed enum recording WHY the row was revoked.
    /// <c>null</c> when <see cref="RevokedAt"/> is <c>null</c>; set
    /// alongside the revocation timestamp on every revoke path. See the
    /// <see cref="RefreshTokenRevokedReasons"/> constants.
    /// </summary>
    public string? RevokedReason { get; set; }

    public User User { get; set; } = null!;
}

/// <summary>
/// Story 28-9 AC3 — closed enum of values that <see cref="RefreshToken.RevokedReason"/>
/// can take. Stored as <c>character varying(32)</c> in Postgres with a
/// CHECK constraint that mirrors this list. Adding a value requires a
/// migration to widen the constraint.
/// </summary>
public static class RefreshTokenRevokedReasons
{
    /// <summary>User logged out of the current session.</summary>
    public const string ManualLogout = "manual_logout";

    /// <summary>
    /// Logout-all path: every active refresh token for the user was
    /// burned across all tenants (Doc 01 §2.4 / Story 28-9 AC6).
    /// </summary>
    public const string LogoutAll = "logout_all";

    /// <summary>
    /// Normal rotation: the row was rotated by <c>/auth/refresh</c> and
    /// a successor row was issued. Presenting a row carrying this reason
    /// a second time triggers <see cref="ReuseDetected"/>.
    /// </summary>
    public const string RotationConsumed = "rotation_consumed";

    /// <summary>
    /// <c>/auth/switch-org</c> rotated the row when the user changed
    /// tenant context.
    /// </summary>
    public const string SwitchOrg = "switch_org";

    /// <summary>
    /// Reuse-detection: an already-revoked token was presented to
    /// <c>/auth/refresh</c> — every row sharing the chain head was
    /// burned in defence.
    /// </summary>
    public const string ReuseDetected = "reuse_detected";

    /// <summary>
    /// Password reset confirmed: every active session was burned so the
    /// new credential is the only path back in.
    /// </summary>
    public const string PasswordReset = "password_reset";

    /// <summary>
    /// Admin force-logout: a platform admin invoked
    /// <c>DELETE /api/admin/users/{userId}/sessions</c>.
    /// </summary>
    public const string AdminForceLogout = "admin_force_logout";
}
