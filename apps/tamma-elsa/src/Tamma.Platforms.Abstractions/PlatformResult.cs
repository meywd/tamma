namespace Tamma.Platforms.Abstractions;

/// <summary>
/// Story 31-1 AC1 — result envelope for every
/// <see cref="IGitPlatformClient"/> /
/// <see cref="IGitPlatformActionsClient"/> call. Mirrors the existing
/// <c>GitHubAppResult&lt;T&gt;</c> three-way shape so 31-3 can adapt
/// the GitHub driver without callers learning a new pattern.
///
/// <para>Three variants:</para>
/// <list type="bullet">
///   <item><see cref="Ok"/> — call succeeded, value is non-null.</item>
///   <item><see cref="Failed"/> — call hit a known error
///         (<see cref="PlatformError"/>); caller decides whether to
///         retry based on the variant.</item>
///   <item><see cref="ServiceUnavailable"/> — driver isn't wired (no
///         creds, dev-mode null seam). Distinct from Failed so the
///         caller can no-op cheaply rather than treat it as an error.</item>
/// </list>
///
/// <para>Pattern-match shape:</para>
/// <code>
/// switch (await client.GetRepoAsync(...))
/// {
///     case PlatformResult&lt;Repo&gt;.Ok(var repo):                 ...
///     case PlatformResult&lt;Repo&gt;.Failed(var err):               ...
///     case PlatformResult&lt;Repo&gt;.ServiceUnavailable:            ...
/// }
/// </code>
/// </summary>
public abstract record PlatformResult<T>
{
    private PlatformResult() { }

    /// <summary>
    /// Successful call. <see cref="Value"/> is non-null.
    /// </summary>
    public sealed record Ok(T Value) : PlatformResult<T>;

    /// <summary>
    /// Call reached the platform but the platform returned a known
    /// failure — see <see cref="PlatformError"/> for shape.
    /// </summary>
    public sealed record Failed(PlatformError Error) : PlatformResult<T>;

    /// <summary>
    /// Driver isn't configured (no token, no install id resolved).
    /// Use this rather than wrapping a synthetic error so callers can
    /// treat "no platform wired" as a separate concept from "platform
    /// rejected the call".
    /// </summary>
    public sealed record ServiceUnavailable() : PlatformResult<T>;

    /// <summary>True when the result is the <see cref="Ok"/> variant.</summary>
    public bool IsOk => this is Ok;

    /// <summary>
    /// Safe accessor — returns the value if <see cref="Ok"/>, else default.
    /// Prefer pattern-matching for production code; this exists for
    /// inline assertions in tests.
    /// </summary>
    public T? GetValueOrDefault() => this is Ok ok ? ok.Value : default;

    /// <summary>
    /// Convenience constructors so call sites don't have to spell out
    /// the closed type. Mirrors <c>GitHubAppResult&lt;T&gt;.Ok(...)</c> /
    /// <c>NotConfigured()</c> ergonomics.
    /// </summary>
    public static PlatformResult<T> FromOk(T value) => new Ok(value);

    public static PlatformResult<T> FromError(PlatformError error) => new Failed(error);

    public static PlatformResult<T> FromServiceUnavailable() => new ServiceUnavailable();

    /// <summary>
    /// Map the success value, leaving error variants intact. Common
    /// shape for driver code that needs to project a platform DTO into
    /// a neutral <see cref="Models"/> record.
    /// </summary>
    public PlatformResult<TOther> Map<TOther>(Func<T, TOther> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        return this switch
        {
            Ok ok => new PlatformResult<TOther>.Ok(selector(ok.Value)),
            Failed f => new PlatformResult<TOther>.Failed(f.Error),
            ServiceUnavailable => new PlatformResult<TOther>.ServiceUnavailable(),
            _ => throw new InvalidOperationException(
                $"unhandled PlatformResult variant: {GetType().Name}"),
        };
    }
}
