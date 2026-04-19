namespace Tamma.Api.Auth;

/// <summary>
/// Validates a post-login <c>?rd=</c> redirect URL is safe to follow. Mirrors
/// TS <c>packages/api/src/routes/auth/github-oauth.ts:239-272</c>.
///
/// <para>Accepts:</para>
/// <list type="bullet">
///   <item>Relative paths: <c>/dashboard</c>, <c>/elsa/studio</c></item>
///   <item>Absolute HTTPS URLs whose host matches the configured domain
///         allowlist (e.g. <c>app.tamma.dev</c>, <c>elsa.tamma.dev</c>)</item>
/// </list>
///
/// <para>Rejects:</para>
/// <list type="bullet">
///   <item>Off-domain hosts (open-redirect prevention)</item>
///   <item>Non-HTTPS schemes</item>
///   <item>Protocol-relative URLs (<c>//evil.com/...</c>)</item>
///   <item>Anything that fails to parse as a URI</item>
/// </list>
/// </summary>
public static class RedirectUrlSanitizer
{
    /// <summary>
    /// Returns the sanitized URL when valid; null otherwise. The caller falls
    /// back to a default (e.g. <c>Dashboard:Url</c>) when null is returned.
    /// </summary>
    public static string? Sanitize(string? rawUrl, string allowedDomain)
    {
        if (string.IsNullOrWhiteSpace(rawUrl)) return null;
        if (string.IsNullOrWhiteSpace(allowedDomain)) return null;

        // Reject protocol-relative URLs outright. These start with "//" and
        // would otherwise be parsed by Uri as the host.
        if (rawUrl.StartsWith("//", StringComparison.Ordinal))
            return null;

        // Relative path → safe by construction (browser resolves against the
        // current origin, which is always Tamma).
        if (rawUrl.StartsWith('/'))
            return rawUrl;

        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
            return null;

        if (uri.Scheme != Uri.UriSchemeHttps)
            return null;

        if (!IsAllowedHost(uri.Host, allowedDomain))
            return null;

        // Rebuild the URL from parsed components so any tainted-input flow
        // analyzer sees a clean output (defense-in-depth; TS used the same
        // technique to satisfy CodeQL).
        return new UriBuilder
        {
            Scheme = uri.Scheme,
            Host = uri.Host,
            Port = uri.IsDefaultPort ? -1 : uri.Port,
            Path = uri.AbsolutePath,
            Query = uri.Query.TrimStart('?'),
            Fragment = uri.Fragment.TrimStart('#'),
        }.Uri.ToString();
    }

    private static bool IsAllowedHost(string host, string allowedDomain)
    {
        // Allow the bare domain and any subdomain. Comparison is
        // case-insensitive per RFC 3986.
        if (host.Equals(allowedDomain, StringComparison.OrdinalIgnoreCase))
            return true;
        return host.EndsWith("." + allowedDomain, StringComparison.OrdinalIgnoreCase);
    }
}
