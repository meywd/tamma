using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Tamma.Data.Entities;

namespace Tamma.Api.Auth;

public interface IJwtService
{
    /// <summary>
    /// Generates a 15-minute access JWT with the seven non-time claims defined
    /// by Story 18-2 AC 8 / TS <c>UnifiedJwtPayload</c>:
    /// <c>{ sub, tenantId, role, platformRole, email, name, authMethod }</c>.
    /// </summary>
    string GenerateAccessToken(User user, Guid? tenantId, string role);

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

    public string GenerateAccessToken(User user, Guid? tenantId, string role)
    {
        // Derive the platform role: tenant-owners are also platform admins
        // by convention (matches TS github-oauth.ts mapping). Anything else
        // is a regular user. Update this when a dedicated platform-role
        // column lands.
        var platformRole = role == "owner" ? "platform_admin" : "user";
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
