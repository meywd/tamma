namespace Tamma.Data.Pooling;

/// <summary>
/// Story 28-4 / Round-2 M7 — typed failure thrown by
/// <see cref="LruPooledTenantConnectionResolver.LeaseAsync"/> when the
/// configured number of retry attempts have all observed a race against
/// eviction. Carries a hint <see cref="RetryAfterMs"/> for callers that
/// want to back off and retry at the application layer.
///
/// <para>This replaces the previous generic
/// <see cref="System.InvalidOperationException"/> so callers (and ops
/// tooling) can pattern-match on the cause without inspecting message
/// text.</para>
/// </summary>
public sealed class TenantConnectionLeaseRaceException : Exception
{
    /// <summary>Tenant whose lease acquisition repeatedly raced eviction.</summary>
    public Guid TenantId { get; }

    /// <summary>How many attempts were made before giving up.</summary>
    public int Attempts { get; }

    /// <summary>
    /// Suggested delay (milliseconds) before retrying. Conservative —
    /// derived from <see cref="TenantConnectionPoolOptions.LeaseRetryAttempts"/>
    /// and the per-attempt delay schedule used internally.
    /// </summary>
    public int RetryAfterMs { get; }

    public TenantConnectionLeaseRaceException(
        Guid tenantId,
        int attempts,
        int retryAfterMs)
        : base(
            $"LeaseAsync({tenantId:N}) failed after {attempts} retry attempts " +
            "— repeated race against eviction. This indicates an eviction " +
            "storm; check tamma.tenant_pools.evicted_total. " +
            $"Suggested retry-after: {retryAfterMs}ms.")
    {
        TenantId = tenantId;
        Attempts = attempts;
        RetryAfterMs = retryAfterMs;
    }
}
