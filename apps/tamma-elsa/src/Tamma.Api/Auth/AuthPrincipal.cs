using Microsoft.AspNetCore.Http;

namespace Tamma.Api.Auth;

/// <summary>
/// Tagged-union representation of an authenticated API-key principal. Mirrors
/// the deleted TS <c>packages/api/src/auth/principal.ts</c> three-variant
/// discriminated union.
///
/// <para>Populated by <see cref="ApiKeyAuthHandler"/> and exposed via
/// <see cref="HttpContextAuthExtensions.GetAuthPrincipal"/>. Endpoint code can
/// pattern-match on the concrete type to read scope-specific fields without
/// re-parsing flat <c>ClaimTypes</c> claims.</para>
/// </summary>
public abstract record AuthPrincipal(Guid KeyId);

/// <summary>User-scope key: tied to a specific user with a tenant-role.</summary>
public sealed record UserAuthPrincipal(
    Guid KeyId,
    Guid UserId,
    string Role,
    Guid TenantId) : AuthPrincipal(KeyId);

/// <summary>Installation-scope key: ties to a GitHub App installation.</summary>
public sealed record InstallationAuthPrincipal(
    Guid KeyId,
    long InstallationId,
    Guid? TenantId) : AuthPrincipal(KeyId);

/// <summary>
/// Service-scope key: cross-tenant credential for service-to-service calls.
/// <see cref="TenantId"/> is populated from the <c>X-Tenant-Id</c> header
/// when present; otherwise null (the request is platform-level).
/// </summary>
public sealed record ServiceAuthPrincipal(
    Guid KeyId,
    string ServiceName,
    IReadOnlyList<string> Permissions,
    Guid? TenantId) : AuthPrincipal(KeyId);

public static class HttpContextAuthExtensions
{
    private const string AuthPrincipalKey = "Tamma.AuthPrincipal";

    public static void SetAuthPrincipal(this HttpContext ctx, AuthPrincipal principal)
        => ctx.Items[AuthPrincipalKey] = principal;

    public static AuthPrincipal? GetAuthPrincipal(this HttpContext ctx)
        => ctx.Items.TryGetValue(AuthPrincipalKey, out var v) ? v as AuthPrincipal : null;
}
