using System.Security.Cryptography;

namespace Tamma.Api.Auth;

/// <summary>
/// Story 28-7: produces fresh API keys in the three on-wire formats parsed
/// by <see cref="ApiKeyPrefixParser"/>. Centralises the literal scope
/// markers as compile-time constants so the parser, generator, and any
/// future import tools stay locked to the same alphabet.
///
/// <para>Reserved markers (per Story 28-7 brief §"Risks / Open Questions"):
/// <c>tk_x_</c> for future-extension imports and <c>tk_s_</c> for
/// system/service tokens. Both are listed in <see cref="ReservedMarkers"/>
/// so a future contributor adding a new scope letter can spot the
/// reservations at code-review.</para>
/// </summary>
public static class ApiKeyPrefixGenerator
{
    /// <summary>
    /// Suffix random body length, in bytes. 32 bytes of CSPRNG output
    /// gives 256 bits of entropy — well above the OWASP recommendation
    /// for API tokens.
    /// </summary>
    public const int SuffixBytes = 32;

    /// <summary>Tenant-scoped marker (<c>t_</c>).</summary>
    public const string TenantMarker = "t_";

    /// <summary>Platform-admin marker (<c>pl_</c>).</summary>
    public const string PlatformMarker = "pl_";

    /// <summary>User-scoped marker (<c>u_</c>).</summary>
    public const string UserMarker = "u_";

    /// <summary>
    /// Markers reserved for future use. Keeping them named here so a
    /// future code-reviewer adding a new scope letter immediately sees
    /// the prior reservations and avoids collisions with imports.
    /// </summary>
    public static readonly IReadOnlyList<string> ReservedMarkers = new[] { "x_", "s_" };

    /// <summary>
    /// Generates a tenant-scoped key:
    /// <c>tamma_sk_t_&lt;base32-tenant&gt;_&lt;base64url-random&gt;</c>.
    /// </summary>
    /// <param name="tenantId">Owning tenant. Encoded into the prefix so
    /// the auth handler can route to the per-tenant data source without
    /// a control-plane lookup.</param>
    public static string GenerateTenantKey(Guid tenantId)
    {
        var tenantSegment = Base32.Encode(tenantId.ToByteArray());
        var random = RandomSuffix();
        return ApiKeyHasher.KeyPrefix + TenantMarker + tenantSegment + "_" + random;
    }

    /// <summary>
    /// Generates a platform-admin key:
    /// <c>tamma_sk_pl_&lt;base64url-random&gt;</c>.
    /// </summary>
    public static string GeneratePlatformKey()
        => ApiKeyHasher.KeyPrefix + PlatformMarker + RandomSuffix();

    /// <summary>
    /// Generates a user-scoped key:
    /// <c>tamma_sk_u_&lt;base64url-random&gt;</c>.
    /// </summary>
    public static string GenerateUserKey()
        => ApiKeyHasher.KeyPrefix + UserMarker + RandomSuffix();

    private static string RandomSuffix()
    {
        var bytes = RandomNumberGenerator.GetBytes(SuffixBytes);
        return Base64Url.Encode(bytes);
    }
}
