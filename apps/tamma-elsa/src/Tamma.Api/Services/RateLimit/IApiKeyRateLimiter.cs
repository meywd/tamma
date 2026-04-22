namespace Tamma.Api.Services.RateLimit;

/// <summary>
/// Story 28-7 deferred-item: per-API-key RPM limiter. Separate from
/// <see cref="IRateLimitService"/> (which is locked to 3/hour for
/// resend-verification / password-reset surfaces) because API-key limits
/// are per-key and operator-configurable via <c>api_keys.RateLimitRpm</c>.
///
/// <para>Shares the <see cref="IDistributedRateLimitBackend"/> backend so
/// multi-pod deployments get a single shared counter when Redis is
/// configured.</para>
/// </summary>
public interface IApiKeyRateLimiter
{
    /// <summary>
    /// Returns <c>true</c> when the caller has exceeded
    /// <paramref name="limitRpm"/> in the last 60 seconds. When
    /// <paramref name="limitRpm"/> is <c>null</c>, no limit is enforced
    /// (null is the "legacy unlimited key" state — pre-28-7 rows).
    /// </summary>
    bool IsLimited(Guid apiKeyId, int? limitRpm);

    /// <summary>
    /// Records a single request against the per-key bucket. Called once per
    /// authenticated request after the IsLimited check.
    /// </summary>
    void Record(Guid apiKeyId);
}

/// <summary>
/// Thin wrapper over <see cref="IDistributedRateLimitBackend"/>. Composite
/// key: <c>api-key:&lt;guid&gt;</c>. Window: 60s (per-minute RPM semantics).
/// </summary>
public sealed class ApiKeyRateLimiter : IApiKeyRateLimiter
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly IDistributedRateLimitBackend _backend;

    public ApiKeyRateLimiter(IDistributedRateLimitBackend backend)
    {
        _backend = backend;
    }

    public bool IsLimited(Guid apiKeyId, int? limitRpm)
    {
        if (limitRpm is null || limitRpm.Value <= 0)
            return false; // No limit configured on this key.
        return _backend.Count(Compose(apiKeyId), Window) >= limitRpm.Value;
    }

    public void Record(Guid apiKeyId)
        => _backend.Increment(Compose(apiKeyId), Window);

    private static string Compose(Guid apiKeyId)
        => $"api-key:{apiKeyId:N}";
}
