using Npgsql;

namespace Tamma.Data.Abstractions;

/// <summary>
/// Resolves the per-tenant Npgsql data sources used by
/// <see cref="ITenantDbContextFactory"/>. Story 28-3 ships a stub
/// implementation that returns a single dev DataSource for every
/// tenant; Story 28-4 replaces it with the LRU pool cache backed by
/// <c>tenants.EncryptedConnectionString</c>.
///
/// <para>Note: this is the new abstraction owned by
/// <c>Tamma.Data.Abstractions</c>. The legacy single-DB resolver under
/// <c>Tamma.Api/Services/Provisioning</c> stays in place for the
/// Cranl Phase-3 plumbing — both will continue to coexist until the
/// per-tenant pool cache is fully wired (Story 28-4).</para>
/// </summary>
public interface ITenantConnectionResolver
{
    /// <summary>
    /// Returns the <see cref="NpgsqlDataSource"/> backing the tenant's
    /// application schema. The resolver may build a new pool on cache
    /// miss; callers must NOT dispose the returned data source — its
    /// lifetime belongs to the resolver.
    /// </summary>
    /// <param name="tenantId">Target tenant. Must be a registered tenant
    /// — passing an unknown id throws.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<NpgsqlDataSource> GetDataSourceAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the data source backing the tenant's per-tenant Elsa
    /// schema. Story 28-5 wires this on first provisioning.
    /// </summary>
    ValueTask<NpgsqlDataSource> GetElsaDataSourceAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Story 28-4 AC4 — acquire a ref-counted lease over the tenant's
    /// per-tenant data source. Use for long-running consumers (SSE
    /// streams, hosted services, Elsa long-running activities) that
    /// hold the data-source reference across multiple awaits.
    /// Short-lived request/response handlers should keep using
    /// <see cref="GetDataSourceAsync"/> — it's cheaper and Npgsql's
    /// own connection draining covers the eviction race for that
    /// pattern.
    ///
    /// <para>Disposal rules: always wrap the returned lease in
    /// <c>await using</c>. While at least one lease is open, the
    /// resolver defers the actual <c>NpgsqlDataSource.DisposeAsync()</c>
    /// until the final lease releases; eviction still removes the
    /// entry from the LRU cache so subsequent calls build a fresh
    /// pool.</para>
    /// </summary>
    ValueTask<ITenantConnectionLease> LeaseAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops a tenant from the pool cache (called after delete or after
    /// connection-string rotation). No-op if the tenant has no warm
    /// pool. Implementation must <see cref="IAsyncDisposable.DisposeAsync"/>
    /// the evicted data source — or defer the dispose if any lease
    /// (see <see cref="LeaseAsync"/>) is still outstanding.
    /// </summary>
    ValueTask EvictAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Diagnostic snapshot of the pool cache — exposed for
    /// <c>tamma.tenant_pools.warm</c> metric (Doc 01 §9.3) and for
    /// admin UX in Story 28-11.
    /// </summary>
    TenantConnectionPoolStats GetStats();
}

/// <summary>
/// Snapshot of the pool cache state, returned from
/// <see cref="ITenantConnectionResolver.GetStats"/>. Doc 01 §9.3.
/// </summary>
public sealed record TenantConnectionPoolStats(
    int WarmPoolCount,
    long TotalPoolsOpenedSinceStartup,
    long TotalPoolsEvictedSinceStartup);
