using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Tamma.Api.Auth;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Auth;

/// <summary>
/// Default implementation of <see cref="IAdminImpersonationService"/>.
///
/// <para>Lifetime: scoped (depends on <see cref="ControlPlaneDbContext"/>
/// and <see cref="IJwtService"/>; both scoped/singleton). Wired via
/// <c>AddScoped&lt;IAdminImpersonationService, AdminImpersonationService&gt;</c>
/// in <c>Program.cs</c>.</para>
///
/// <para><b>Reason charset gate:</b> the regex below mirrors the M17 pattern
/// used by <c>AdminTenantsEndpoints.SanitizeAdminNote</c>. Length window
/// 1..500 — note <see cref="AdminImpersonation.Reason"/> is REQUIRED, so
/// the lower bound is 1 (not 0 like X-Admin-Note). The DB-level CHECK
/// constraint is the redundant defence-in-depth gate; this regex is the
/// fast-path service-layer gate.</para>
/// </summary>
public sealed class AdminImpersonationService : IAdminImpersonationService
{
    /// <summary>
    /// Same charset whitelist as M17 / <c>AdminTenantsEndpoints</c>, but
    /// with <c>{1,500}</c> instead of <c>{0,500}</c> — a session reason
    /// is REQUIRED for SOC2 evidence (an empty audit reason is
    /// indistinguishable from a missing one).
    /// </summary>
    private static readonly Regex ReasonRegex = new(
        @"^[A-Za-z0-9 .,;:_!@#$%&()\-]{1,500}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ControlPlaneDbContext _db;
    private readonly IJwtService _jwt;
    private readonly IConfiguration _config;
    private readonly TimeProvider _timeProvider;

    public AdminImpersonationService(
        ControlPlaneDbContext db,
        IJwtService jwt,
        IConfiguration config,
        TimeProvider timeProvider)
    {
        _db = db;
        _jwt = jwt;
        _config = config;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task<BeginImpersonationResult> BeginImpersonationAsync(
        ClaimsPrincipal impersonator,
        Guid targetTenantId,
        Guid? targetUserId,
        string reason,
        string? ipAddress,
        string? userAgent,
        CancellationToken ct = default)
    {
        // 1. Reason charset gate. Trim leading/trailing whitespace so
        // ` reason ` doesn't 400 on a stray space alone (matches the M17
        // SanitizeAdminNote behaviour).
        var trimmed = reason?.Trim() ?? string.Empty;
        if (!ReasonRegex.IsMatch(trimmed))
        {
            throw new ArgumentException(
                "reason must match [A-Za-z0-9 .,;:_!@#$%&()-]{1,500}",
                nameof(reason));
        }

        // 2. Operator identity from the JWT. The PlatformOwnerAccess gate
        // guarantees `sub` + `email` are present; we still defend with a
        // fail-closed throw if they aren't (e.g. permissive-dev tests
        // that mint a stripped principal).
        var (userId, email) = ExtractOperator(impersonator);
        if (userId is null || string.IsNullOrEmpty(email))
        {
            throw new InvalidOperationException(
                "Impersonator principal missing sub/email — refuse to mint an audit row without identity.");
        }

        // 3. Target tenant existence — surface "tenant_not_found" as an
        // ArgumentException so the endpoint maps it to a 404 cleanly.
        // We deliberately tolerate `Status = pending_verification`/`failed`/
        // `deleting` here — impersonation is a stuck-state recovery tool,
        // and refusing the session because the tenant isn't fully active
        // would defeat the point.
        var tenantExists = await _db.Tenants
            .AsNoTracking()
            .IgnoreQueryFilters()
            .AnyAsync(t => t.Id == targetTenantId && t.DeletedAt == null, ct)
            .ConfigureAwait(false);
        if (!tenantExists)
        {
            throw new ArgumentException("target_tenant_not_found", nameof(targetTenantId));
        }

        // 4. Target user — when set, must belong to the target tenant via
        // tenant_memberships. Fall closed: a mismatched (targetUserId,
        // targetTenantId) pair is a misuse signal, not a soft warning.
        if (targetUserId is not null)
        {
            var targetIsMember = await _db.TenantMemberships
                .AsNoTracking()
                .AnyAsync(
                    m => m.TenantId == targetTenantId && m.UserId == targetUserId.Value,
                    ct)
                .ConfigureAwait(false);
            if (!targetIsMember)
            {
                throw new ArgumentException(
                    "target_user_not_member_of_tenant",
                    nameof(targetUserId));
            }
        }

        // 5. Insert the audit row FIRST. The JWT mint depends on the row
        // id (it goes into the `imp_id` claim), and a row without a
        // matching token is harmless (the cleanup sweep will mark it
        // session_expired). A token without a matching row would be a
        // forgery the middleware can detect.
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var row = new AdminImpersonation
        {
            Id = Guid.NewGuid(),
            ImpersonatorUserId = userId.Value,
            ImpersonatorEmail = email,
            TargetTenantId = targetTenantId,
            TargetUserId = targetUserId,
            Reason = trimmed,
            StartedAt = now,
            IpAddress = string.IsNullOrEmpty(ipAddress) ? null : ipAddress,
            UserAgent = string.IsNullOrEmpty(userAgent) ? null : userAgent,
        };
        _db.AdminImpersonations.Add(row);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // 6. Mint the impersonation JWT. We need a User entity to hand to
        // JwtService.GenerateAccessToken — ideally the impersonator's row,
        // because we want the JWT's `sub` / `email` to remain the
        // operator's identity (so audit / log enrichers continue to
        // attribute downstream calls to the operator). The `imp_id`
        // claim is the breadcrumb that the request is acting INSIDE an
        // impersonation session.
        var impersonatorUser = await _db.Users
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == userId.Value, ct)
            .ConfigureAwait(false);
        if (impersonatorUser is null)
        {
            // Defence-in-depth — should be unreachable given step 2.
            throw new InvalidOperationException(
                "impersonator user vanished between principal extraction and DB lookup");
        }

        // The session's per-tenant role mirrors the target's role inside
        // the tenant when targetUserId is set; otherwise we use "owner"
        // (full-tenant impersonation grants admin-level access inside the
        // target tenant).
        var sessionRole = "owner";
        if (targetUserId is not null)
        {
            var membershipRole = await _db.TenantMemberships
                .AsNoTracking()
                .Where(m => m.TenantId == targetTenantId && m.UserId == targetUserId.Value)
                .Select(m => m.Role)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            if (!string.IsNullOrEmpty(membershipRole))
                sessionRole = membershipRole;
        }

        var token = _jwt.GenerateAccessToken(
            impersonatorUser,
            targetTenantId,
            sessionRole,
            tenants: null,
            impId: row.Id);

        // JWT expiry — 15-minute hard cap (JwtService enforces; we mirror
        // here for the response envelope). The MaxSessionMinutes config
        // bounds the OUTER session window: even if the operator
        // re-authenticates / re-issues, the original audit row's
        // StartedAt + MaxSessionMinutes is the hard outer wall the
        // cleanup pass enforces.
        var maxMinutes = _config.GetValue<int?>("Tamma:Impersonation:MaxSessionMinutes") ?? 60;
        if (maxMinutes < 15) maxMinutes = 15;          // floor
        if (maxMinutes > 24 * 60) maxMinutes = 24 * 60; // ceiling: 24h hard wall

        return new BeginImpersonationResult(
            ImpersonationId: row.Id,
            AccessToken: token,
            ExpiresAt: now.AddMinutes(15),
            MaxSessionExpiresAt: now.AddMinutes(maxMinutes));
    }

    /// <inheritdoc />
    public async Task<AdminImpersonation?> EndImpersonationAsync(
        Guid impersonationId,
        string endedReason,
        CancellationToken ct = default)
    {
        // Whitelist the ended-reason vocabulary so a buggy caller can't
        // stash an arbitrary string in the audit column. Anything else
        // collapses to "explicit_exit" with a logged warning at the
        // endpoint layer.
        var normalisedReason = endedReason switch
        {
            "explicit_exit" => "explicit_exit",
            "session_expired" => "session_expired",
            "revoked" => "revoked",
            _ => "explicit_exit",
        };

        // Tracked entity (NOT AsNoTracking) so the SaveChanges below
        // emits the targeted UPDATE.
        var row = await _db.AdminImpersonations
            .FirstOrDefaultAsync(
                r => r.Id == impersonationId && r.EndedAt == null, ct)
            .ConfigureAwait(false);
        if (row is null) return null;

        row.EndedAt = _timeProvider.GetUtcNow().UtcDateTime;
        row.EndedReason = normalisedReason;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return row;
    }

    /// <inheritdoc />
    public async Task<AdminImpersonation?> GetActiveByIdAsync(
        Guid impersonationId,
        CancellationToken ct = default)
    {
        return await _db.AdminImpersonations
            .AsNoTracking()
            .FirstOrDefaultAsync(
                r => r.Id == impersonationId && r.EndedAt == null, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AdminImpersonation>> GetActiveAsync(
        Guid impersonatorUserId,
        CancellationToken ct = default)
    {
        return await _db.AdminImpersonations
            .AsNoTracking()
            .Where(r => r.ImpersonatorUserId == impersonatorUserId && r.EndedAt == null)
            .OrderByDescending(r => r.StartedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AdminImpersonation>> ListAllActiveAsync(
        CancellationToken ct = default)
    {
        return await _db.AdminImpersonations
            .AsNoTracking()
            .Where(r => r.EndedAt == null)
            .OrderByDescending(r => r.StartedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Pull (userId, email) out of the operator principal. Mirrors the
    /// extraction in <c>AdminTenantsEndpoints.ExtractActor</c> but
    /// returns a stricter shape — userId is required.
    /// </summary>
    private static (Guid? UserId, string? Email) ExtractOperator(ClaimsPrincipal? principal)
    {
        if (principal is null) return (null, null);

        var sub = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var email = principal.FindFirst(JwtRegisteredClaimNames.Email)?.Value
            ?? principal.FindFirst(ClaimTypes.Email)?.Value
            ?? principal.FindFirst("email")?.Value;

        if (Guid.TryParse(sub, out var userId))
            return (userId, email);
        return (null, email);
    }
}
