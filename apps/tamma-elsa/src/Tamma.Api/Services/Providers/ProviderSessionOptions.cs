namespace Tamma.Api.Services.Providers;

/// <summary>
/// Tunables for <see cref="ProviderSessionService"/> and
/// <see cref="ProviderSessionCleanupService"/>. Registered as a
/// singleton via <c>AddProviderSessionServices</c>; tests may replace
/// the instance to tighten intervals.
/// </summary>
public sealed class ProviderSessionOptions
{
    /// <summary>
    /// Maximum time a session may be idle (no <c>Get</c>/<c>Execute</c>
    /// hit) before it is evicted by the cleanup loop. Default: 30 minutes.
    /// </summary>
    public TimeSpan InactivityTtl { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Interval at which the cleanup hosted service runs. Default: 60 seconds.
    /// </summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Task #10 (post-review): when <c>true</c> (default) the cleanup
    /// service runs its loop in <see cref="ProviderSessionCleanupService.ExecuteAsync"/>.
    /// Shared API test fixtures gate this off to keep background eviction
    /// activity from racing test assertions. Mirrors the
    /// <c>BuiltInAlertRuleSeederOptions.RunOnStartup</c> pattern.
    /// </summary>
    public bool RunOnStartup { get; set; } = true;
}
