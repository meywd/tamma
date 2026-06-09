using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using Tamma.Data.Entities;

namespace Tamma.Api.Auth;

/// <summary>
/// One row of the JWT <c>tenants</c> claim — the list of tenants the user is
/// a member of, with the per-tenant role. Story 28-9 introduced this claim so
/// the dashboard can render a tenant switcher without a separate
/// <c>GET /api/v1/orgs</c> round-trip on every page load, and so the
/// switch-org flow can validate membership against the request token rather
/// than re-querying the DB on every gate.
///
/// <para>Story 28-9 AC1 residual — each row also carries the tenant's
/// <see cref="Slug"/> so the dashboard's switcher can render a human-readable
/// URL/label per tenant, and so <see cref="JwtService.GenerateAccessToken"/>
/// can source the <c>active_tenant_slug</c> claim from the active tenant's
/// entry without an extra DB round-trip. The parameter defaults to the empty
/// string so transitional callers (and the older positional
/// <c>new TenantClaim(id, role)</c> call sites) keep compiling and degrade
/// gracefully to a blank slug rather than throwing.</para>
/// </summary>
public readonly record struct TenantClaim(Guid TenantId, string Role, string Slug = "");

public interface IJwtService
{
    /// <summary>
    /// Generates a 15-minute access JWT with the seven non-time claims defined
    /// by Story 18-2 AC 8 / TS <c>UnifiedJwtPayload</c>:
    /// <c>{ sub, tenantId, role, platformRole, email, name, authMethod }</c>.
    /// Story 28-9 also emits an explicit <c>active_tenant_id</c> claim
    /// (mirror of <c>tenantId</c>; the explicit name removes the ambiguity
    /// that <c>tenantId</c> could be a request-scoped tenant), an
    /// <c>active_tenant_slug</c> claim (the human-readable slug of the active
    /// tenant, sourced from the matching <paramref name="tenants"/> entry;
    /// <c>""</c> when there is no active tenant or its slug is unknown), and a
    /// <c>tenants</c> JSON array of <c>{tenantId, role, slug}</c> tuples for
    /// every membership the user holds. The <paramref name="tenants"/> argument
    /// is optional — handlers that haven't been updated yet still mint valid
    /// (single-tenant) tokens with an empty <c>tenants</c> claim and a blank
    /// <c>active_tenant_slug</c>.
    ///
    /// <para>Story 28-R2 follow-up B — when <paramref name="impId"/> is set
    /// the token carries an <c>imp_id</c> claim pointing at the
    /// <c>admin_impersonations.id</c> row that authorises the session.
    /// Token lifetime is hard-capped at 15 minutes — the configured
    /// <c>Tamma:Impersonation:MaxSessionMinutes</c> value bounds the
    /// outer-session window enforced by
    /// <see cref="Middleware.ImpersonationContextMiddleware"/>, not the
    /// per-token lifetime.</para>
    ///
    /// <para><b>SCOPE-REDUCTION GUARANTEE (Story 28-R2 / PF-S3)</b>: when
    /// <paramref name="impId"/> is set, the minted JWT carries
    /// <c>platformRole="user"</c> regardless of the
    /// <see cref="User.PlatformRole"/> value on the impersonator's row.
    /// The original operator's identity is preserved on the new
    /// <c>actor_user_id</c> + <c>actor_email</c> claims so audit-log
    /// enrichers can attribute downstream calls to the operator without
    /// granting them platform-admin reach inside the impersonation
    /// session. Concretely: a stolen impersonation token CANNOT call
    /// <c>PlatformOwnerAccess</c>-gated routes (KEK rotation, alerts,
    /// other-tenant admin endpoints) even when the originating operator
    /// is a platform admin. The role inside the impersonated tenant
    /// (passed via <paramref name="role"/>) determines per-tenant
    /// reach — typically <c>"owner"</c> for a tenant-scoped session,
    /// the target user's actual role for a user-scoped session.</para>
    /// </summary>
    string GenerateAccessToken(
        User user,
        Guid? tenantId,
        string role,
        IEnumerable<TenantClaim>? tenants = null,
        Guid? impId = null);

    string GenerateRefreshToken();
    ClaimsPrincipal? ValidateToken(string token);
}

public class JwtService : IJwtService
{
    private readonly IConfiguration _config;
    private readonly SymmetricSecurityKey _signingKey;

    public JwtService(IConfiguration config)
    {
        _config = config;
        var secret = config["Jwt:Secret"] ?? throw new InvalidOperationException("Jwt:Secret not configured");
        _signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
    }

    public string GenerateAccessToken(
        User user,
        Guid? tenantId,
        string role,
        IEnumerable<TenantClaim>? tenants = null,
        Guid? impId = null)
    {
        // Story 28-R2 / Finding C1 — the platform role is now sourced from the
        // dedicated users.platform_role column, NOT from the per-tenant
        // role. Before C1, this was
        // `role == "owner" ? "platform_admin" : "user"` — but every signed-up
        // user is auto-owner of their personal tenant, so that mapping let
        // every user pass OwnerAccess on every /api/admin/* route.
        //
        // Defensive default: if the column ever ends up NULL/empty (legacy row
        // pre-migration, hand-edited DB), treat it as the safest non-elevated
        // value ("user"). Promoting a user to platform_admin requires an
        // explicit DB write — never auto-derived from runtime state.
        //
        // Story 28-R2 / PF-S3 — IMPERSONATION SCOPE REDUCTION. When the
        // caller passes a non-empty `impId`, this token is being minted
        // INSIDE an impersonation session: the operator (an actual
        // platform admin) is "becoming" a target user/tenant. Per the
        // PF-S3 design, the impersonation token must NOT carry the
        // operator's platform_admin role — otherwise it doubles as a
        // cross-tenant platform-admin ticket (KEK rotation, alerts,
        // every PlatformOwnerAccess route). We therefore force
        // platformRole="user" for impersonation tokens. The original
        // operator's identity is preserved on the dedicated
        // `actor_user_id` + `actor_email` claims (added below) so audit
        // enrichers can still attribute the action to the operator.
        var isImpersonation = impId.HasValue && impId.Value != Guid.Empty;
        var platformRole = isImpersonation
            ? "user"
            : (string.IsNullOrWhiteSpace(user.PlatformRole) ? "user" : user.PlatformRole);
        var displayName = user.DisplayName
            ?? user.GitHubLogin
            ?? user.Email.Split('@')[0];
        var tenantClaimValue = tenantId is null || tenantId.Value == Guid.Empty
            ? string.Empty
            : tenantId.Value.ToString();

        // Story 28-9 AC1 residual — `active_tenant_slug`. The slug of the
        // currently-active tenant, sourced from the matching entry in the
        // `tenants` membership list so we avoid a separate DB round-trip.
        // Degrades to "" when there is no active tenant, when the active
        // tenant isn't present in the (possibly transitional / empty)
        // membership list, or when that entry has no slug — never throws.
        var activeTenantSlug = tenantId is null || tenantId.Value == Guid.Empty
            ? string.Empty
            : (tenants ?? Array.Empty<TenantClaim>())
                .Where(t => t.TenantId == tenantId.Value)
                .Select(t => t.Slug ?? string.Empty)
                .FirstOrDefault() ?? string.Empty;

        // Use short claim names (no ASP.NET URI prefix) so the JWT JSON has
        // bare `role`, `tenantId`, `platformRole`, etc. — matching what the
        // dashboard / nginx role-check / unified-nav clients expect.
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new("tenantId", tenantClaimValue),
            // Story 28-9 — explicit "active tenant" claim. `tenantId` is kept
            // as an alias so existing readers (TenantContextMiddleware
            // fallback, nginx auth_request, dashboard) keep working through
            // the transition.
            new("active_tenant_id", tenantClaimValue),
            // Story 28-9 AC1 residual — the active tenant's human-readable
            // slug. Always emitted (possibly "") so the dashboard can
            // distinguish "no/blank slug" from "claim missing → stale token".
            new("active_tenant_slug", activeTenantSlug),
            new("role", role),
            new("platformRole", platformRole),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new("name", displayName),
            new("authMethod", user.AuthMethod),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(JwtRegisteredClaimNames.Iat,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
                ClaimValueTypes.Integer64),
        };

        // Story 28-9 — `tenants` claim: JSON array of {tenantId, role} so
        // multi-tenant users can list/switch without a DB hit. Always emitted
        // (possibly empty) so the dashboard can distinguish "no memberships"
        // from "claim missing → stale token". Empty Guids are filtered out
        // defensively.
        var tenantList = (tenants ?? Array.Empty<TenantClaim>())
            .Where(t => t.TenantId != Guid.Empty)
            // Story 28-9 AC1 residual — carry the per-tenant slug alongside
            // {tenantId, role} so the dashboard switcher can label/route each
            // tenant. Additive: existing readers that only pick tenantId/role
            // are unaffected. Null slug coalesces to "" so the JSON never
            // emits a null literal.
            .Select(t => new { tenantId = t.TenantId.ToString(), role = t.Role, slug = t.Slug ?? string.Empty })
            .ToArray();
        var tenantsJson = JsonSerializer.Serialize(tenantList);
        // Stored as a string-typed claim that holds the JSON array literal.
        // The dashboard / TenantContextMiddleware / tests JSON.parse the
        // value to recover the array. We tried the JWT JsonArray / Json
        // value-type expansion paths but JwtSecurityTokenHandler's roundtrip
        // re-shapes the claim into nested objects rather than a single
        // string claim, which broke every consumer. Plain string is the
        // shape that survives both serialization and read-back unchanged.
        claims.Add(new Claim("tenants", tenantsJson));

        // Story 28-R2 follow-up B — impersonation linkage. The `imp_id`
        // claim is the FK back to `admin_impersonations.id`, which the
        // ImpersonationContextMiddleware reads to (a) verify the session
        // is still active and (b) tag downstream audit events with both
        // the impersonator + the impersonated identity. Absent for normal
        // (non-impersonation) sessions.
        //
        // Story 28-R2 / PF-S3 — alongside `imp_id` we also emit
        // `actor_user_id` + `actor_email` claims that capture the
        // impersonator's identity. The token's `sub`/`email` claims
        // already carry the operator's identity (the JWT is minted FROM
        // the operator's User row), so today these "actor" claims are a
        // duplicate breadcrumb. The breadcrumb exists so audit-event
        // enrichers and the ImpersonationContextMiddleware can attribute
        // the action to the operator without parsing `imp_id` and
        // re-querying the impersonation table on every request — and so
        // a future change that flips the JWT's `sub` to the TARGET user
        // (e.g. for tenant-scoped impersonation that needs target-user
        // identity in `sub`) does not orphan the audit trail.
        if (isImpersonation)
        {
            claims.Add(new Claim("imp_id", impId!.Value.ToString("D")));
            claims.Add(new Claim("actor_user_id", user.Id.ToString()));
            claims.Add(new Claim("actor_email", user.Email));
        }

        var credentials = new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256);
        var handler = new JwtSecurityTokenHandler();
        // Disable inbound claim mapping so consumers see raw `role` not the URI.
        handler.InboundClaimTypeMap.Clear();
        handler.OutboundClaimTypeMap.Clear();

        // Token lifetime — both regular and impersonation sessions are
        // hard-capped at 15 minutes. The configured
        // `Tamma:Impersonation:MaxSessionMinutes` (default 60) bounds
        // the OUTER-session window enforced by
        // `ImpersonationContextMiddleware` (StartedAt + MaxSessionMinutes
        // wall); the per-token cap is always 15 to limit blast radius
        // of a stolen impersonation token. Story 28-R2 / PF-C2 collapsed
        // a dead ternary that branched on `impId` but returned 15 min
        // on both sides; the comment now matches the literal.
        var lifetime = TimeSpan.FromMinutes(15);

        var token = new JwtSecurityToken(
            issuer: _config["Jwt:Issuer"] ?? "tamma",
            audience: _config["Jwt:Audience"] ?? "tamma-api",
            claims: claims,
            expires: DateTime.UtcNow.Add(lifetime),
            signingCredentials: credentials
        );

        return handler.WriteToken(token);
    }

    public string GenerateRefreshToken()
    {
        // 32 random bytes, hex-encoded → 64-char lowercase hex. Matches the
        // TS refresh-token shape so the SHA-256 hash stored in the DB is the
        // same length and shape regardless of which API minted it.
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    }

    public ClaimsPrincipal? ValidateToken(string token)
    {
        var handler = new JwtSecurityTokenHandler();
        handler.InboundClaimTypeMap.Clear();
        try
        {
            return handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = _signingKey,
                ValidateIssuer = true,
                ValidIssuer = _config["Jwt:Issuer"] ?? "tamma",
                ValidateAudience = true,
                ValidAudience = _config["Jwt:Audience"] ?? "tamma-api",
                ClockSkew = TimeSpan.Zero,
                NameClaimType = JwtRegisteredClaimNames.Sub,
                RoleClaimType = "role",
            }, out _);
        }
        catch
        {
            return null;
        }
    }
}
