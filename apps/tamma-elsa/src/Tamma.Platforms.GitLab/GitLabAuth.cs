namespace Tamma.Platforms.GitLab;

/// <summary>
/// Story 31-6 — credential variants accepted by the driver.
///
/// <para>GitLab supports four token shapes for the REST API. The driver
/// keeps them as discriminated records so the HTTP client can pick the
/// right authorization header per request. PAT / project / group tokens
/// all use the same <c>PRIVATE-TOKEN</c> header — they're separated as
/// types only so logs / events can attribute scope correctly. OAuth2
/// uses the standard <c>Authorization: Bearer</c> header and is gated
/// behind a stretch goal in 31-6 (refresh flow deferred to 31-6-b).</para>
///
/// <para>Plaintext is held in a string at construction time only; the
/// caller (factory) is responsible for not retaining the value
/// elsewhere. The driver scrubs it from logs.</para>
/// </summary>
public abstract record GitLabAuth
{
    private GitLabAuth() { }

    /// <summary>Personal access token (per-user). Header: PRIVATE-TOKEN.</summary>
    public sealed record PersonalAccessToken(string Token) : GitLabAuth;

    /// <summary>Project access token (per-project). Header: PRIVATE-TOKEN.</summary>
    public sealed record ProjectAccessToken(string Token) : GitLabAuth;

    /// <summary>Group access token (per-group). Header: PRIVATE-TOKEN.</summary>
    public sealed record GroupAccessToken(string Token) : GitLabAuth;

    /// <summary>OAuth2 access token. Header: Authorization: Bearer.</summary>
    public sealed record OAuth2(string AccessToken) : GitLabAuth;

    /// <summary>
    /// Default plaintext-string parser used by the factory when the
    /// credential is loaded as a flat secret. The current contract is:
    /// any value that starts with <c>glpat-</c> / <c>glptt-</c> /
    /// <c>glgtt-</c> is treated as a PAT / project / group token; an
    /// "oauth:" prefix denotes OAuth2; otherwise default to PAT.
    /// </summary>
    public static GitLabAuth FromPlaintext(string plaintext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintext);
        if (plaintext.StartsWith("oauth:", StringComparison.Ordinal))
        {
            return new OAuth2(plaintext["oauth:".Length..]);
        }
        if (plaintext.StartsWith("glptt-", StringComparison.Ordinal))
        {
            return new ProjectAccessToken(plaintext);
        }
        if (plaintext.StartsWith("glgtt-", StringComparison.Ordinal))
        {
            return new GroupAccessToken(plaintext);
        }
        // glpat- and any unprefixed value default to PAT — most
        // self-hosted setups don't use the new prefixes.
        return new PersonalAccessToken(plaintext);
    }
}
