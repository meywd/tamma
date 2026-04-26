namespace Tamma.Data.Pooling;

/// <summary>
/// Story 28-4 / Round-2 M7 — typed failure thrown by
/// <see cref="LruPooledTenantConnectionResolver.LeaseAsync"/> when a
/// single tenant has more outstanding leases than
/// <see cref="TenantConnectionPoolOptions.MaxOutstandingLeases"/>. Acts
/// as a per-tenant fairness guard so a runaway SSE/long-running
/// consumer can't pin the cache entry forever and starve other tenants.
///
/// <para>Carries the configured cap and the live count at the moment
/// of refusal so callers can surface a human-readable error and ops
/// can set alerts on this as a leading indicator of a leaking
/// consumer.</para>
/// </summary>
public sealed class TenantLeaseLimitExceededException : Exception
{
    /// <summary>Tenant id that hit the cap.</summary>
    public Guid TenantId { get; }

    /// <summary>
    /// Configured ceiling
    /// (<see cref="TenantConnectionPoolOptions.MaxOutstandingLeases"/>).
    /// </summary>
    public int MaxOutstandingLeases { get; }

    /// <summary>Live outstanding lease count at the moment of refusal.</summary>
    public int CurrentOutstandingLeases { get; }

    public TenantLeaseLimitExceededException(
        Guid tenantId,
        int maxOutstandingLeases,
        int currentOutstandingLeases)
        : base(
            $"LeaseAsync({tenantId:N}) refused — tenant has " +
            $"{currentOutstandingLeases} outstanding leases, which is at " +
            $"or above the configured per-tenant cap " +
            $"({maxOutstandingLeases}). Long-running consumer may be " +
            "leaking handles; check the admin pool diagnostics " +
            "(GET /api/admin/pools/tenants).")
    {
        TenantId = tenantId;
        MaxOutstandingLeases = maxOutstandingLeases;
        CurrentOutstandingLeases = currentOutstandingLeases;
    }
}
