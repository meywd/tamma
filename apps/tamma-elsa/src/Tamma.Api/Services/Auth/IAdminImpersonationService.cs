using System.Security.Claims;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Auth;

/// <summary>
/// Story 28-R2 follow-up B — service surface for first-class platform-admin
/// impersonation sessions. The service owns:
/// <list type="bullet">
///   <item><description>Persisting the SOC2 audit row in
///     <c>admin_impersonations</c> (one INSERT at session start, one
///     UPDATE at session end).</description></item>
///   <item><description>Charset-validating the operator-supplied
///     <c>reason</c> against the M17 whitelist.</description></item>
///   <item><description>Minting the impersonation-scoped JWT (15-minute
///     cap; carries an <c>imp_id</c> claim pointing back at the audit
///     row).</description></item>
///   <item><description>Listing currently-active sessions for the
///     incident-response surface.</description></item>
/// </list>
///
/// <para>The service does NOT emit platform events directly — endpoints
/// emit <c>IMPERSONATION.STARTED</c> / <c>IMPERSONATION.ENDED</c> via the
/// existing <c>IPlatformEventPublisher</c> seam after the service returns,
/// so an event-store outage doesn't block a forensic INSERT against the
/// audit table.</para>
/// </summary>
public interface IAdminImpersonationService
{
    /// <summary>
    /// Begin a new impersonation session. Validates the reason charset,
    /// inserts an <c>admin_impersonations</c> row, mints a tenant-scoped
    /// JWT with an <c>imp_id</c> claim pointing at the new row, and
    /// returns the result. Throws <see cref="ArgumentException"/> on a
    /// rejected reason (charset / length).
    /// </summary>
    /// <param name="impersonator">The platform-admin's principal — must
    /// carry <c>sub</c> + <c>email</c>. Source of the captured operator
    /// identity columns on the audit row.</param>
    /// <param name="targetTenantId">Tenant to impersonate.</param>
    /// <param name="targetUserId">Optional specific member to impersonate
    /// inside <paramref name="targetTenantId"/>; <c>null</c> means
    /// "full-tenant impersonation" (act as a generic admin for the
    /// tenant).</param>
    /// <param name="reason">Required, charset-whitelisted operator note.</param>
    /// <param name="ipAddress">Best-effort caller IP for the audit trail.</param>
    /// <param name="userAgent">Best-effort caller User-Agent for the audit trail.</param>
    /// <param name="ct">Cancellation.</param>
    Task<BeginImpersonationResult> BeginImpersonationAsync(
        ClaimsPrincipal impersonator,
        Guid targetTenantId,
        Guid? targetUserId,
        string reason,
        string? ipAddress,
        string? userAgent,
        CancellationToken ct = default);

    /// <summary>
    /// End an active impersonation session. Stamps <c>EndedAt</c> +
    /// <c>EndedReason</c> on the row. Returns the updated row, or
    /// <c>null</c> if the row does not exist OR was already ended (caller
    /// treats either as "session not active" and returns 404 / 410).
    /// </summary>
    /// <param name="impersonationId">PK of the row to end.</param>
    /// <param name="endedReason">Why the session ended — one of
    /// <c>"explicit_exit"</c>, <c>"session_expired"</c>, <c>"revoked"</c>.</param>
    Task<AdminImpersonation?> EndImpersonationAsync(
        Guid impersonationId,
        string endedReason,
        CancellationToken ct = default);

    /// <summary>
    /// Returns a single active impersonation row by id, or <c>null</c>
    /// if the id does not match an active session. Used by
    /// <c>ImpersonationContextMiddleware</c> to gate every request that
    /// carries an <c>imp_id</c> JWT claim.
    /// </summary>
    Task<AdminImpersonation?> GetActiveByIdAsync(
        Guid impersonationId,
        CancellationToken ct = default);

    /// <summary>
    /// Returns every active impersonation session for a single
    /// impersonator. Most callers want the platform-wide
    /// <see cref="ListAllActiveAsync"/> view; this overload supports the
    /// "show me MY active sessions" personal-dashboard surface.
    /// </summary>
    Task<IReadOnlyList<AdminImpersonation>> GetActiveAsync(
        Guid impersonatorUserId,
        CancellationToken ct = default);

    /// <summary>
    /// Returns every currently-active impersonation session across the
    /// platform — keys the incident-response surface ("who's impersonating
    /// right now?"). Hits the <c>idx_admin_impersonations_active</c>
    /// partial index.
    /// </summary>
    Task<IReadOnlyList<AdminImpersonation>> ListAllActiveAsync(
        CancellationToken ct = default);
}

/// <summary>
/// Result of <see cref="IAdminImpersonationService.BeginImpersonationAsync"/>.
/// Carries the new audit row id, the minted JWT, and the JWT's expiry
/// (UTC) so the caller can return all three to the operator without a
/// second decode.
/// </summary>
/// <param name="ImpersonationId">PK of the new <c>admin_impersonations</c> row.</param>
/// <param name="AccessToken">Tenant-scoped JWT carrying <c>imp_id</c>.</param>
/// <param name="ExpiresAt">UTC instant the JWT expires (15-minute cap).</param>
/// <param name="MaxSessionExpiresAt">UTC instant the audit row will be
/// auto-marked <c>session_expired</c> by the cleanup pass — the upper
/// bound on session lifetime. Sourced from
/// <c>Tamma:Impersonation:MaxSessionMinutes</c>.</param>
public sealed record BeginImpersonationResult(
    Guid ImpersonationId,
    string AccessToken,
    DateTime ExpiresAt,
    DateTime MaxSessionExpiresAt);
