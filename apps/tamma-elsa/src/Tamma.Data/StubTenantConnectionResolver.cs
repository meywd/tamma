using Npgsql;
using Tamma.Data.Abstractions;

namespace Tamma.Data;

/// <summary>
/// Story 28-3 stub <see cref="ITenantConnectionResolver"/>. Routes
/// every tenant id to the same single <see cref="NpgsqlDataSource"/>
/// supplied at construction. Used until Story 28-4 lands the per-tenant
/// LRU pool cache backed by the encrypted-connection-string envelope
/// in <c>tenants.EncryptedConnectionString</c>.
///
/// <para><b>Caveat for tests</b>: integration tests that depend on
/// per-tenant isolation MUST wait for Story 28-4 — the stub gives every
/// tenant the same DB. The new <see cref="TenantDbContextFactory"/>
/// can still be exercised end-to-end against this stub, but expect
/// cross-tenant data visibility until the real resolver lands.</para>
/// </summary>
public sealed class StubTenantConnectionResolver : ITenantConnectionResolver, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private long _opened;

    public StubTenantConnectionResolver(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
        Interlocked.Increment(ref _opened);
    }

    public ValueTask<NpgsqlDataSource> GetDataSourceAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_dataSource);

    public ValueTask<NpgsqlDataSource> GetElsaDataSourceAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default) =>
        // Stub: per-tenant Elsa DB does not exist yet (Story 28-5). Return
        // the same dev DataSource so callers that touch this in dev get a
        // working connection rather than a NullReferenceException.
        ValueTask.FromResult(_dataSource);

    public ValueTask EvictAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default) =>
        // Stub: nothing to evict — the resolver holds a single shared
        // data source. Real eviction lands in Story 28-4.
        ValueTask.CompletedTask;

    public TenantConnectionPoolStats GetStats() =>
        new(WarmPoolCount: 1,
            TotalPoolsOpenedSinceStartup: Interlocked.Read(ref _opened),
            TotalPoolsEvictedSinceStartup: 0);

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
