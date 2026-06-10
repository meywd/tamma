using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace Tamma.Api.Auth;

/// <summary>
/// Reads the calling user id from a <see cref="ClaimsPrincipal"/> in a way
/// that works for BOTH authentication schemes used by the API:
///   • <b>JWT bearer</b> (configured with <c>MapInboundClaims = false</c> in
///     Program.cs so token claim names like <c>sub</c> stay verbatim) —
///     here the user id lives in <see cref="JwtRegisteredClaimNames.Sub"/>
///     and <see cref="ClaimTypes.NameIdentifier"/> is NOT populated.
///   • <b>API key</b> (<see cref="ApiKeyAuthHandler"/>) — here the handler
///     sets <see cref="ClaimTypes.NameIdentifier"/> directly, no
///     <c>sub</c> claim is added.
///
/// Reading only one of those names is the bug-shaped well-trodden path:
///   • Reading only <see cref="ClaimTypes.NameIdentifier"/> ⇒ JWT requests
///     return 401/500 because the value is null. This shipped in
///     <see cref="Tamma.Api.Authorization.RequireTenantMembershipFilter"/>
///     and the prompt-store handlers, causing /api/v1/orgs/.../members
///     401s and /api/prompts 500s the moment a JWT-authenticated user
///     hit them.
///   • Reading only <see cref="JwtRegisteredClaimNames.Sub"/> ⇒ API-key
///     requests fail the same way.
///
/// All endpoint code that needs the caller's user id MUST go through
/// <see cref="GetUserId"/> or <see cref="GetUserIdString"/>. A
/// `dotnet test` round-trip test (UserIdClaimRoundTripTests) covers
/// the JWT side; the API-key path is exercised by the existing
/// <c>ApiKeyAuthHandlerTests</c>.
/// </summary>
public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// Returns the caller's user id as a <see cref="Guid"/>, or <c>null</c>
    /// when the principal is anonymous, the claim is missing, or the value
    /// isn't a parseable GUID.
    /// </summary>
    public static Guid? GetUserId(this ClaimsPrincipal principal)
    {
        var raw = principal.GetUserIdString();
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    /// <summary>
    /// Returns the caller's user id as a string. Reads
    /// <see cref="JwtRegisteredClaimNames.Sub"/> first (JWT path), then
    /// falls back to <see cref="ClaimTypes.NameIdentifier"/> (API-key
    /// path). Returns <c>null</c> when neither claim is present.
    /// </summary>
    public static string? GetUserIdString(this ClaimsPrincipal principal)
    {
        return principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    }
}
