namespace Tamma.Platforms.Abstractions;

/// <summary>
/// Story 31-1 AC7 — discriminated-union error type that every driver
/// must map platform-specific failures into. Retry policies key off
/// the variant rather than string-matching error messages.
///
/// <para>Pattern-match against the concrete record types:</para>
/// <code>
/// var hint = error switch
/// {
///     PlatformError.AuthExpired       => "reauthorize",
///     PlatformError.PermissionDenied  => "ask owner for access",
///     PlatformError.NotFound          => "404 - check ref/repo",
///     PlatformError.RateLimited rl    => $"retry after {rl.RetryAfter}",
///     PlatformError.ServiceUnavailable=> "platform is down - back off",
///     PlatformError.InvalidRequest ir => $"bad request: {ir.Code}",
///     PlatformError.Unknown u         => $"unmapped: {u.Reason}",
///     _ => throw new InvalidOperationException(
///         $"unhandled PlatformError variant: {error.GetType().Name}"),
/// };
/// </code>
/// </summary>
public abstract record PlatformError
{
    private PlatformError() { }

    /// <summary>
    /// Auth token / installation token is expired or revoked. Driver
    /// should invalidate its cached token and retry; if the retry hits
    /// the same error the operator must reauthorize.
    /// </summary>
    public sealed record AuthExpired() : PlatformError;

    /// <summary>
    /// The credential is valid but lacks permission for the requested
    /// scope (e.g. read-only token attempting a write). NOT retryable.
    /// </summary>
    public sealed record PermissionDenied() : PlatformError;

    /// <summary>
    /// Resource (repo, PR, branch, run, artifact) does not exist or is
    /// hidden from the credential. NOT retryable.
    /// </summary>
    public sealed record NotFound() : PlatformError;

    /// <summary>
    /// Platform rate limit hit. <see cref="RetryAfter"/> is the
    /// platform-suggested wait if it provided one (GitHub returns
    /// <c>X-RateLimit-Reset</c>; GitLab uses <c>RateLimit-Reset</c>;
    /// Gitea returns <c>Retry-After</c>). Caller should respect it.
    /// </summary>
    public sealed record RateLimited(TimeSpan? RetryAfter) : PlatformError;

    /// <summary>
    /// Driver couldn't reach the platform — network error, 5xx, or
    /// the credential isn't configured at all (mirrors today's
    /// <c>GitHubAppResult&lt;T&gt;.NotConfigured()</c> path). The
    /// distinct <see cref="PlatformResult{T}.ServiceUnavailable"/>
    /// result variant covers the no-creds case directly so callers
    /// don't unwrap a Failed; this error variant is reserved for
    /// upstream 5xx after the driver tried.
    /// </summary>
    public sealed record ServiceUnavailable() : PlatformError;

    /// <summary>
    /// 4xx that wasn't auth/permission/notfound/rate-limit — the
    /// platform rejected the request shape (validation, conflict,
    /// merge-not-mergeable, etc.). <see cref="Code"/> is a stable
    /// driver-chosen identifier (e.g. <c>"merge_conflict"</c>);
    /// <see cref="Hint"/> is human-readable detail safe to log /
    /// surface to the user.
    /// </summary>
    public sealed record InvalidRequest(string Code, string? Hint) : PlatformError;

    /// <summary>
    /// The driver couldn't classify the failure into a concrete variant.
    /// Should be rare; retry policy treats this as non-retryable so a
    /// genuine bug doesn't get masked by exponential backoff.
    /// </summary>
    public sealed record Unknown(string Reason) : PlatformError;
}
