namespace Tamma.Api.Services.Providers;

/// <summary>States of the circuit breaker state machine.</summary>
public enum CircuitBreakerState
{
    /// <summary>Provider is healthy; requests flow through normally.</summary>
    Closed,

    /// <summary>Provider has tripped; requests should be short-circuited.</summary>
    Open,

    /// <summary>Cooldown elapsed; a single probe request may be attempted.</summary>
    HalfOpen,
}

/// <summary>Snapshot returned by <see cref="ICircuitBreakerService.GetStateAsync"/>.</summary>
public sealed record CircuitBreakerStatus(
    string ProviderKey,
    CircuitBreakerState State,
    int FailureCount,
    DateTimeOffset? LastSuccess,
    DateTimeOffset? LastFailure,
    DateTimeOffset? CircuitOpenUntil,
    bool HalfOpenInProgress);
