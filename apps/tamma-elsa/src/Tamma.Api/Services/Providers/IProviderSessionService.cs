namespace Tamma.Api.Services.Providers;

/// <summary>
/// In-memory provider-session registry. Ported from the Story 9-4
/// TypeScript <c>ProviderSessionService</c> (deleted in Epic 19 phase 3).
///
/// <para>
/// A session represents a long-lived logical handle on a specific
/// <c>(provider, model)</c> pair so that the TS engine, Elsa workflows,
/// and CLI clients can chain multiple executions without re-authenticating
/// or re-resolving credentials. The service does not manage any per-session
/// state beyond identity + timestamps — credential resolution happens at
/// <see cref="ExecuteAsync"/> time via the named <see cref="HttpClient"/>
/// registered for the provider.
/// </para>
/// <para>
/// Idle sessions are evicted by <see cref="ProviderSessionCleanupService"/>
/// after <see cref="ProviderSessionOptions.InactivityTtl"/> (default: 30 min).
/// </para>
/// </summary>
public interface IProviderSessionService
{
    /// <summary>
    /// Create a new session and return its metadata. The returned
    /// <see cref="ProviderSession.Handle"/> is an opaque UUID string.
    /// </summary>
    Task<ProviderSession> CreateAsync(string provider, string model, Guid? tenantId);

    /// <summary>
    /// Resolve a session by handle. Updates <see cref="ProviderSession.LastUsed"/>
    /// on hit so the TTL cleanup does not evict an in-flight session.
    /// </summary>
    Task<ProviderSession?> GetAsync(string handle);

    /// <summary>
    /// Tenant-scoped form of <see cref="GetAsync"/>. Returns <c>null</c>
    /// when the session exists but is owned by a different tenant — this
    /// lets endpoints treat cross-tenant access as a 404.
    /// </summary>
    Task<ProviderSession?> GetTenantScopedAsync(Guid? callerTenantId, string handle);

    /// <summary>
    /// Dispatch a provider invocation against the session. On success a
    /// diagnostic row is persisted via <see cref="IDiagnosticsService"/>.
    /// On failure a diagnostic row with <c>Success=false</c> is persisted
    /// before the exception propagates.
    /// </summary>
    Task<ExecuteResult> ExecuteAsync(string handle, ExecuteRequest req, CancellationToken ct = default);

    /// <summary>
    /// Tenant-scoped form of <see cref="ExecuteAsync"/>. Throws
    /// <see cref="ProviderSessionNotFoundException"/> when the caller's
    /// tenant does not own the session.
    /// </summary>
    Task<ExecuteResult> ExecuteTenantScopedAsync(
        Guid? callerTenantId, string handle, ExecuteRequest req, CancellationToken ct = default);

    /// <summary>
    /// Remove a session by handle. Returns <c>true</c> if a session was
    /// removed, <c>false</c> if no session matched.
    /// </summary>
    Task<bool> DeleteAsync(string handle);

    /// <summary>Tenant-scoped form of <see cref="DeleteAsync"/>.</summary>
    Task<bool> DeleteTenantScopedAsync(Guid? callerTenantId, string handle);

    /// <summary>
    /// Enumerate active sessions, filtered by tenant when
    /// <paramref name="tenantId"/> is non-null.
    /// </summary>
    Task<IReadOnlyList<ProviderSession>> ListAsync(Guid? tenantId);

    /// <summary>
    /// Evict every session whose <see cref="ProviderSession.LastUsed"/> is
    /// older than <paramref name="olderThan"/>. Invoked by
    /// <see cref="ProviderSessionCleanupService"/>; returns the count
    /// evicted so the hosted service can log it.
    /// </summary>
    Task<int> EvictInactiveAsync(TimeSpan olderThan);
}
