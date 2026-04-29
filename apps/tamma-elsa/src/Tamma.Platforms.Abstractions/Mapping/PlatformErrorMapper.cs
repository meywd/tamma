namespace Tamma.Platforms.Abstractions.Mapping;

/// <summary>
/// Story 31-1 — extension point for driver-specific error mappers.
/// Each driver implements this interface to translate its native
/// platform exception type into a <see cref="PlatformError"/> variant.
/// 31-3 (GitHub), 31-4 (Gitea), 31-5 (Forgejo), 31-6 (GitLab) ship
/// concrete implementations; 31-1 ships the contract + a default
/// that callers can use when they don't have a platform-specific
/// mapper handy.
///
/// <para>Pattern (illustrative — drivers do this in their own
/// projects, not here):</para>
/// <code>
/// internal sealed class OctokitErrorMapper : IPlatformErrorMapper&lt;ApiException&gt;
/// {
///     public PlatformError Map(ApiException ex) =&gt; ex.StatusCode switch
///     {
///         HttpStatusCode.Unauthorized => new PlatformError.AuthExpired(),
///         HttpStatusCode.Forbidden    => new PlatformError.PermissionDenied(),
///         HttpStatusCode.NotFound     => new PlatformError.NotFound(),
///         _ when (int)ex.StatusCode == 429 => new PlatformError.RateLimited(...),
///         _ when (int)ex.StatusCode &gt;= 500 => new PlatformError.ServiceUnavailable(),
///         _ => new PlatformError.InvalidRequest(
///             ex.ApiError?.Message ?? "unknown",
///             ex.Message),
///     };
/// }
/// </code>
/// </summary>
public interface IPlatformErrorMapper<in TException>
    where TException : Exception
{
    /// <summary>Map a driver-specific exception to a neutral error.</summary>
    PlatformError Map(TException exception);
}

/// <summary>
/// Default safe mapper — anything is <see cref="PlatformError.Unknown"/>.
/// Drivers should ALWAYS override with a platform-specific mapper; this
/// exists so a partially-implemented driver still compiles + runs.
/// </summary>
public sealed class PassthroughExceptionMapper : IPlatformErrorMapper<Exception>
{
    public static PassthroughExceptionMapper Instance { get; } = new();

    public PlatformError Map(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new PlatformError.Unknown(exception.GetType().Name + ": " + exception.Message);
    }
}
