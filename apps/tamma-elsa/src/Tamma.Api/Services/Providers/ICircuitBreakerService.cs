namespace Tamma.Api.Services.Providers;

/// <summary>
/// Real circuit-breaker state machine for provider health tracking.
/// Persists state to the <c>provider_health</c> table and is per-tenant.
/// </summary>
public interface ICircuitBreakerService
{
    /// <summary>Record a successful provider invocation. Closes the circuit.</summary>
    Task<CircuitBreakerStatus> RecordSuccessAsync(string providerKey, Guid? tenantId, CancellationToken ct = default);

    /// <summary>Record a failed provider invocation. May transition Closed→Open or HalfOpen→Open.</summary>
    Task<CircuitBreakerStatus> RecordFailureAsync(string providerKey, Guid? tenantId, CancellationToken ct = default);

    /// <summary>
    /// Get the current state, promoting Open→HalfOpen automatically if cooldown has elapsed
    /// since the circuit opened.
    /// </summary>
    Task<CircuitBreakerStatus> GetStateAsync(string providerKey, Guid? tenantId, CancellationToken ct = default);

    /// <summary>
    /// Attempt to claim the HalfOpen probe slot. Returns true if the caller
    /// may attempt a single probe request, false if another caller is already
    /// probing or the circuit is not in a state that permits probing.
    /// </summary>
    Task<bool> TryProbeAsync(string providerKey, Guid? tenantId, CancellationToken ct = default);

    /// <summary>Reset state to Closed with zero failures.</summary>
    Task<CircuitBreakerStatus> ResetAsync(string providerKey, Guid? tenantId, CancellationToken ct = default);

    /// <summary>List current state for every tracked provider under a tenant.</summary>
    Task<IReadOnlyList<CircuitBreakerStatus>> ListAsync(Guid? tenantId, CancellationToken ct = default);
}
