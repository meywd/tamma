namespace Tamma.Api.Services.Providers;

/// <summary>
/// Configurable thresholds for the <see cref="CircuitBreakerService"/>.
/// Defaults: 5 failures inside a 60-second sliding window open the circuit,
/// which then stays open for 300 seconds before transitioning to half-open.
/// </summary>
public sealed class CircuitBreakerOptions
{
    /// <summary>Failures required within <see cref="FailureWindow"/> to open the circuit.</summary>
    public int FailureThreshold { get; init; } = 5;

    /// <summary>Sliding window over which failures are counted.</summary>
    public TimeSpan FailureWindow { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long the circuit remains fully open before becoming half-open.</summary>
    public TimeSpan CooldownDuration { get; init; } = TimeSpan.FromSeconds(300);
}
