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
/// </summary>
public readonly record struct TenantClaim(Guid TenantId, string Role);

public interface IJwtService
{
    /// <summary>
    /// Generates a 15-minute access JWT with the seven non-time claims defined
    /// by Story 18-2 AC 8 / TS <c>UnifiedJwtPayload</c>:
    /// <c>{ sub, tenantId, role, platformRole, email, name, authMethod }</c>.
    /// Story 28-9 also emits an explicit <c>active_tenant_id</c> claim
    /// (mirror of <c>tenantId</c>; the explicit name removes the ambiguity
    /// that <c>tenantId</c> could be a request-scoped tenant) and a
    /// <c>tenants</c> JSON array of <c>{tenantId, role}</c> tuples for every
    /// membership the user holds. The <paramref name="tenants"/> argument is
    /// optional — handlers that haven't been updated yet still mint valid
    /// (single-tenant) tokens with an empty <c>tenants</c> claim.
    /// </summary>
    string GenerateAccessToken(
        User user,
        Guid? tenantId,
        string role,
        IEnumerable<TenantClaim>? tenants = null);

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
        IEnumerable<TenantClaim>? tenants = null)
    {
        // Story 28-R2 / Finding C1 — the platform role is now sourced from the
        // dedicated users.platform_role column (added by AddUsersPlatformRole
        // migration), NOT from the per-tenant role. Before C1, this was
        // `role == "owner" ? "platform_admin" : "user"` — but every signed-up
        // user is auto-owner of their personal tenant, so that mapping let
        // every user pass OwnerAccess on every /api/admin/* route.
        //
        // Defensive default: if the column ever ends up NULL/empty (legacy row
        // pre-migration, hand-edited DB), treat it as the safest non-elevated
        // value ("user"). Promoting a user to platform_admin requires an
        // explicit DB write — never auto-derived from runtime state.
        var platformRole = string.IsNullOrWhiteSpace(user.PlatformRole)
            ? "user"
            : user.PlatformRole;
        var displayName = user.DisplayName
            ?? user.GitHubLogin
            ?? user.Email.Split('@')[0];
        var tenantClaimValue = tenantId is null || tenantId.Value == Guid.Empty
            ? string.Empty
            : tenantId.Value.ToString();

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
            .Select(t => new { tenantId = t.TenantId.ToString(), role = t.Role })
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

        var credentials = new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256);
        var handler = new JwtSecurityTokenHandler();
        // Disable inbound claim mapping so consumers see raw `role` not the URI.
        handler.InboundClaimTypeMap.Clear();
        handler.OutboundClaimTypeMap.Clear();

        var token = new JwtSecurityToken(
            issuer: _config["Jwt:Issuer"] ?? "tamma",
            audience: _config["Jwt:Audience"] ?? "tamma-api",
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(15),
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
