using System.Diagnostics.CodeAnalysis;

namespace Tamma.Api.Auth;

/// <summary>
/// Story 28-7: parses an inbound <c>Bearer</c> token into its scope marker
/// and (optionally) the embedded tenant id.
///
/// <para>Three on-wire prefixes are recognised:
/// <list type="bullet">
///   <item><description><c>tamma_sk_t_&lt;base32-tenant-id&gt;_&lt;random&gt;</c>
///         — tenant-scoped. Encodes the owning tenant directly so the auth
///         handler can route to the per-tenant DataSource without a control-
///         plane lookup on every request.</description></item>
///   <item><description><c>tamma_sk_pl_&lt;random&gt;</c> — platform-admin.
///         No tenant id; resolves against the control-plane <c>api_keys</c>
///         table only.</description></item>
///   <item><description><c>tamma_sk_u_&lt;random&gt;</c> — user-scoped. No
///         tenant id; tenant is supplied via <c>X-Tenant-Id</c> at request
///         time when the route is tenant-scoped.</description></item>
/// </list></para>
///
/// <para>Tokens that don't match any of the three prefixed shapes parse as
/// <see cref="ApiKeyScope.Legacy"/> — the auth handler routes those to the
/// pre-Epic-28 hash-lookup fallback (<see cref="ParsedApiKey.IsLegacy"/>).</para>
///
/// <para>The parser is tolerant on the on-wire shape — anything that does
/// not start with the canonical <c>tamma_sk_</c> banner returns
/// <see langword="null"/> from <see cref="TryParse"/> so the auth handler
/// can short-circuit with a clean 401. We never echo the raw token in any
/// error message — exposes nothing about the token shape to a probing
/// caller.</para>
/// </summary>
public static class ApiKeyPrefixParser
{
    /// <summary>
    /// Attempts to parse <paramref name="rawKey"/> into its scope and (when
    /// applicable) embedded tenant id. Returns <see langword="false"/> when
    /// the token does not start with <see cref="ApiKeyHasher.KeyPrefix"/> at
    /// all — those are not API keys and the auth handler should short-circuit.
    /// </summary>
    /// <param name="rawKey">The raw <c>Bearer</c> value as received on the wire.</param>
    /// <param name="parsed">The parsed key on success; <see langword="default"/> on failure.</param>
    /// <returns><see langword="true"/> when the token starts with the
    /// <c>tamma_sk_</c> banner, regardless of whether the scope marker is
    /// recognised — callers downstream of the parser still need to check
    /// <see cref="ParsedApiKey.Scope"/> (a value of
    /// <see cref="ApiKeyScope.Unknown"/> means the banner is right but the
    /// scope letter sequence is not one we ship; treat as 401).</returns>
    public static bool TryParse(string? rawKey, [NotNullWhen(true)] out ParsedApiKey? parsed)
    {
        parsed = null;
        if (string.IsNullOrEmpty(rawKey))
            return false;

        if (!rawKey.StartsWith(ApiKeyHasher.KeyPrefix, StringComparison.Ordinal))
            return false;

        // Strip the "tamma_sk_" banner; what remains is one of:
        //   t_<b32-tenant>_<random>   → tenant-scoped
        //   pl_<random>               → platform-admin
        //   u_<random>                → user-scoped
        //   <random>                  → legacy (no scope marker)
        var afterBanner = rawKey[ApiKeyHasher.KeyPrefix.Length..];

        // Identify the scope marker. We check longest-first so the
        // single-letter <c>t_</c> can't shadow a future multi-letter marker.
        if (TryStripScope(afterBanner, "pl_", out var afterPlatform))
        {
            // Platform-admin keys carry no tenant id; the rest is the random
            // suffix and is opaque here.
            if (string.IsNullOrEmpty(afterPlatform))
                return false;
            parsed = new ParsedApiKey(rawKey, ApiKeyScope.Platform, TenantId: null);
            return true;
        }

        if (TryStripScope(afterBanner, "u_", out var afterUser))
        {
            if (string.IsNullOrEmpty(afterUser))
                return false;
            parsed = new ParsedApiKey(rawKey, ApiKeyScope.User, TenantId: null);
            return true;
        }

        if (TryStripScope(afterBanner, "t_", out var afterTenant))
        {
            // Tenant-scoped: next segment is base32-encoded tenant id, then
            // an underscore, then the random body.
            var nextUnderscore = afterTenant.IndexOf('_');
            if (nextUnderscore <= 0 || nextUnderscore == afterTenant.Length - 1)
                return false; // no body after the tenant id

            var tenantSegment = afterTenant[..nextUnderscore];
            var random = afterTenant[(nextUnderscore + 1)..];

            if (string.IsNullOrEmpty(random))
                return false;

            if (!TryDecodeTenantId(tenantSegment, out var tenantId))
            {
                // Banner+scope-letter were right, but the tenant segment is
                // malformed. Return scope=Unknown so the handler can 401
                // without leaking that the prefix shape "almost" matched.
                parsed = new ParsedApiKey(rawKey, ApiKeyScope.Unknown, TenantId: null);
                return true;
            }

            parsed = new ParsedApiKey(rawKey, ApiKeyScope.Tenant, TenantId: tenantId);
            return true;
        }

        // Banner present, no recognised scope marker → legacy un-prefixed key.
        parsed = new ParsedApiKey(rawKey, ApiKeyScope.Legacy, TenantId: null);
        return true;
    }

    /// <summary>
    /// Lookup-prefix variant of <see cref="ApiKeyHasher.Prefix"/> that masks
    /// the tenant segment in <c>tamma_sk_t_</c> tokens. Used for log
    /// messages so a single error log can't leak the tenant id of a
    /// misdelivered key.
    /// </summary>
    public static string SafeDisplayPrefix(string rawKey)
    {
        if (string.IsNullOrEmpty(rawKey))
            return string.Empty;
        // Force the existing 12-char display rule on every shape.
        return ApiKeyHasher.Prefix(rawKey);
    }

    /// <summary>
    /// True when <paramref name="rawKey"/> carries the banner AND one of the
    /// three shipped scope markers — i.e. this parser will NOT treat it as a
    /// legacy un-prefixed key. <see cref="ApiKeyHasher.NewKey"/> uses it to
    /// reject a random body that accidentally opens with a marker.
    /// </summary>
    internal static bool StartsWithScopeMarker(string rawKey)
    {
        if (string.IsNullOrEmpty(rawKey)
            || !rawKey.StartsWith(ApiKeyHasher.KeyPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var afterBanner = rawKey[ApiKeyHasher.KeyPrefix.Length..];
        return TryStripScope(afterBanner, "pl_", out _)
            || TryStripScope(afterBanner, "u_", out _)
            || TryStripScope(afterBanner, "t_", out _);
    }

    private static bool TryStripScope(string s, string marker, out string remainder)
    {
        if (s.StartsWith(marker, StringComparison.Ordinal))
        {
            remainder = s[marker.Length..];
            return true;
        }
        remainder = string.Empty;
        return false;
    }

    /// <summary>
    /// Decodes the 26-character RFC-4648 base32 (no padding) representation
    /// of a 16-byte UUID. We use base32 (not base64url) because base32 is
    /// case-insensitive on the wire — operators copy-pasting keys from
    /// terminals don't have to worry about case.
    /// </summary>
    internal static bool TryDecodeTenantId(string segment, out Guid tenantId)
    {
        tenantId = Guid.Empty;
        if (string.IsNullOrEmpty(segment))
            return false;

        var bytes = Base32.TryDecode(segment);
        if (bytes is null || bytes.Length != 16)
            return false;
        tenantId = new Guid(bytes);
        return true;
    }
}

/// <summary>
/// Marker for the three on-wire scope categories plus two terminal states
/// for unrecognised inputs.
/// </summary>
public enum ApiKeyScope
{
    /// <summary>
    /// Banner matched but the scope marker is unrecognised — return 401.
    /// Distinguished from <see cref="Legacy"/> so the handler can refuse
    /// future-but-unsupported scope letters (e.g. <c>tamma_sk_x_</c>).
    /// </summary>
    Unknown = 0,

    /// <summary>Tenant-scoped (<c>tamma_sk_t_&lt;tid&gt;_&lt;rand&gt;</c>).</summary>
    Tenant,

    /// <summary>Platform-admin (<c>tamma_sk_pl_&lt;rand&gt;</c>).</summary>
    Platform,

    /// <summary>User-scoped (<c>tamma_sk_u_&lt;rand&gt;</c>).</summary>
    User,

    /// <summary>
    /// Pre-Epic-28 key shape (<c>tamma_sk_&lt;rand&gt;</c>, no scope letter).
    /// Falls through to the deprecated CP-hash-lookup path with a WARN log.
    /// </summary>
    Legacy,
}

/// <summary>
/// Outcome of <see cref="ApiKeyPrefixParser.TryParse"/>. Carries the raw
/// key for downstream hashing along with the structural metadata.
/// </summary>
public sealed record ParsedApiKey(
    string RawKey,
    ApiKeyScope Scope,
    Guid? TenantId)
{
    /// <summary>
    /// True when the key is on the deprecated un-prefixed path. The auth
    /// handler emits a deprecation WARN and gates the path behind the
    /// <c>Tamma:Auth:AllowLegacyUnprefixedKeys</c> flag.
    /// </summary>
    public bool IsLegacy => Scope == ApiKeyScope.Legacy;
}
