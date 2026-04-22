namespace Tamma.Data.Entities;

public class ProviderHealth
{
    public Guid Id { get; set; }
    public string ProviderKey { get; set; } = null!;

    /// <summary>
    /// Legacy string label derived from circuit state:
    /// <c>healthy</c> (Closed), <c>degraded</c> (HalfOpen), <c>down</c> (Open),
    /// <c>unknown</c> (no recorded activity). Maintained for API compatibility.
    /// </summary>
    public string Status { get; set; } = "unknown";

    public DateTime? LastSuccess { get; set; }
    public DateTime? LastFailure { get; set; }
    public int FailureCount { get; set; }

    /// <summary>
    /// When the current sliding failure window started. Resets on success
    /// and whenever a failure arrives outside the prior window.
    /// </summary>
    public DateTime? FailureWindowStart { get; set; }

    /// <summary>
    /// If the circuit is Open, the UTC instant at which it transitions to HalfOpen.
    /// Null when the circuit is Closed.
    /// </summary>
    public DateTime? CircuitOpenUntil { get; set; }

    /// <summary>True when a HalfOpen probe has been claimed and is in flight.</summary>
    public bool HalfOpenInProgress { get; set; }

    public Guid? TenantId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
