namespace Tamma.Data.Entities;

/// <summary>
/// Story 28-R2 follow-up B — first-class audit row for a platform-admin
/// impersonating a tenant member. Each row records the start of a session
/// (one immutable insert), then is stamped with <see cref="EndedAt"/> +
/// <see cref="EndedReason"/> when the session terminates (via explicit
/// <c>POST /api/auth/impersonate/end</c>, JWT expiry, or operator revoke).
///
/// <para><b>SOC2 / ISO 27001:</b> the row is the non-repudiable record of
/// which operator (<see cref="ImpersonatorUserId"/> + <see cref="ImpersonatorEmail"/>)
/// impersonated which target (<see cref="TargetTenantId"/> + optional
/// <see cref="TargetUserId"/>) for which reason (<see cref="Reason"/>),
/// over which window (<see cref="StartedAt"/>..<see cref="EndedAt"/>),
/// from which client (<see cref="IpAddress"/> + <see cref="UserAgent"/>).
/// Pair this with the matching <c>IMPERSONATION.STARTED</c> /
/// <c>IMPERSONATION.ENDED</c> platform events for a full
/// search-and-replay audit trail.</para>
///
/// <para><b>Charset gate:</b> <see cref="Reason"/> is constrained at the
/// DB level by a <c>CHECK</c> regex matching
/// <c>^[A-Za-z0-9 .,;:_!@#$%&amp;()\-]{1,500}$</c> — the same whitelist used
/// for <c>X-Admin-Note</c> (Story 28-R2 / M17). Rejects newline / NUL /
/// HTML metacharacters so a malicious operator can't smuggle a log-forging
/// or SSE-poisoning payload into the audit trail.</para>
///
/// <para><b>Active-session lookup:</b> a partial index on
/// <c>EndedAt IS NULL</c> keeps the "currently active impersonations"
/// query (incident-response surface) cheap regardless of historical
/// volume.</para>
/// </summary>
public class AdminImpersonation
{
    /// <summary>UUID PK; Postgres mints via <c>gen_random_uuid()</c>.</summary>
    public Guid Id { get; set; }

    /// <summary>FK to <c>users.id</c>. The platform-admin who initiated the session.</summary>
    public Guid ImpersonatorUserId { get; set; }

    /// <summary>
    /// Snapshot of the impersonator's email at session-start. Stored
    /// alongside the FK so the audit trail survives a future user rename
    /// or hard-delete. Required.
    /// </summary>
    public string ImpersonatorEmail { get; set; } = null!;

    /// <summary>FK to <c>tenants.id</c>. The tenant being impersonated.</summary>
    public Guid TargetTenantId { get; set; }

    /// <summary>
    /// FK to <c>users.id</c>. Nullable: when set, the session targets a
    /// specific tenant member; when null, the session is "full-tenant
    /// impersonation" (impersonator acts as a generic admin for the tenant
    /// without binding to a particular member).
    /// </summary>
    public Guid? TargetUserId { get; set; }

    /// <summary>
    /// Operator-supplied free-text reason. Required, charset-whitelisted.
    /// Used for SOC2 review evidence ("why did Alice impersonate Bob's
    /// org on 2026-04-26?").
    /// </summary>
    public string Reason { get; set; } = null!;

    /// <summary>UTC timestamp the session began. Set by the service.</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>
    /// UTC timestamp the session ended. <c>null</c> while the session is
    /// active — see also the partial index <c>idx_admin_impersonations_active</c>.
    /// </summary>
    public DateTime? EndedAt { get; set; }

    /// <summary>
    /// Why the session ended. One of:
    /// <list type="bullet">
    ///   <item><description><c>"explicit_exit"</c> — operator hit
    ///     <c>POST /api/auth/impersonate/end</c>.</description></item>
    ///   <item><description><c>"session_expired"</c> — JWT expiry passed
    ///     and a downstream request rejected the stale token.</description></item>
    ///   <item><description><c>"revoked"</c> — another platform-admin
    ///     forcibly ended the session via the active-list management
    ///     surface (future work).</description></item>
    /// </list>
    /// </summary>
    public string? EndedReason { get; set; }

    /// <summary>
    /// Best-effort client IP at session-start. Pulled from
    /// <c>HttpContext.Connection.RemoteIpAddress</c> with no proxy
    /// awareness — SOC2 evidence trail, not a security boundary.
    /// </summary>
    public string? IpAddress { get; set; }

    /// <summary>Best-effort User-Agent header at session-start.</summary>
    public string? UserAgent { get; set; }
}
