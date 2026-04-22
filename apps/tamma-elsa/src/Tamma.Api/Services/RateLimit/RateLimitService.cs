namespace Tamma.Api.Services.RateLimit;

/// <summary>
/// Sliding-window per-key rate limiter. Mirrors the TS
/// <c>resendRateLimit</c> / <c>resetRateLimit</c> Maps in
/// <c>routes/auth/register.ts</c> and <c>routes/auth/password-reset.ts</c>.
///
/// <para>Limit is fixed per scope: 3 events per <see cref="WindowDuration"/>
/// (1 hour).</para>
///
/// <para>
/// Audit finding auth/014 follow-up: the implementation now delegates to an
/// <see cref="IDistributedRateLimitBackend"/> so multi-pod deployments can
/// share a window counter via Redis/Valkey. The in-process backend remains
/// the default for single-pod deployments and tests.
/// </para>
/// </summary>
public interface IRateLimitService
{
    /// <summary>Returns true if the (scope, key) pair is currently over-limit.</summary>
    bool IsLimited(string scope, string key);

    /// <summary>Records a single event against (scope, key). Call after the
    /// successful action so failed branches do not consume quota.</summary>
    void Record(string scope, string key);
}

/// <summary>
/// Thin wrapper that normalises the composed key and forwards to an
/// <see cref="IDistributedRateLimitBackend"/>. Keeps the public
/// <see cref="IRateLimitService"/> contract unchanged so all existing
/// callers (ResendVerification, PasswordResetRequest) work without edit.
/// </summary>
public sealed class RateLimitService : IRateLimitService
{
    public const int MaxEventsPerWindow = 3;
    public static readonly TimeSpan WindowDuration = TimeSpan.FromHours(1);

    private readonly IDistributedRateLimitBackend _backend;

    public RateLimitService(IDistributedRateLimitBackend backend)
    {
        _backend = backend;
    }

    public bool IsLimited(string scope, string key)
        => _backend.Count(Compose(scope, key), WindowDuration) >= MaxEventsPerWindow;

    public void Record(string scope, string key)
        => _backend.Increment(Compose(scope, key), WindowDuration);

    private static string Compose(string scope, string key)
        => $"rate-limit:{scope}|{key.ToLowerInvariant()}";
}

/// <summary>
/// Legacy alias for the in-process backend. Kept so existing test code that
/// instantiates <c>new InMemoryRateLimitService()</c> keeps working — the
/// class now wraps <see cref="InMemoryDistributedRateLimitBackend"/> with
/// the <see cref="RateLimitService"/> adapter for public contract parity.
/// </summary>
public sealed class InMemoryRateLimitService : IRateLimitService
{
    private readonly RateLimitService _inner;

    public InMemoryRateLimitService()
        : this(() => DateTime.UtcNow) { }

    /// <summary>Test seam for deterministic window expiry tests.</summary>
    public InMemoryRateLimitService(Func<DateTime> utcNow)
    {
        _inner = new RateLimitService(new InMemoryDistributedRateLimitBackend(utcNow));
    }

    public const int MaxEventsPerWindow = RateLimitService.MaxEventsPerWindow;
    public static readonly TimeSpan WindowDuration = RateLimitService.WindowDuration;

    public bool IsLimited(string scope, string key) => _inner.IsLimited(scope, key);
    public void Record(string scope, string key) => _inner.Record(scope, key);
}
