using Npgsql;

namespace Tamma.Data.Abstractions;

/// <summary>
/// Story 28-4 AC4 — ref-counted lease over a per-tenant
/// <see cref="NpgsqlDataSource"/>. Returned by
/// <see cref="ITenantConnectionResolver.LeaseAsync"/> for long-running
/// consumers (SSE streams, hosted services, Elsa long-running
/// activities) that hold the data-source reference across multiple
/// awaits and could otherwise be yanked by a mid-stream
/// <see cref="ITenantConnectionResolver.EvictAsync"/>.
///
/// <para>While at least one lease is open, the resolver defers the
/// actual <c>NpgsqlDataSource.DisposeAsync()</c> until the final lease
/// releases. Eviction still removes the entry from the LRU cache (so
/// subsequent <see cref="ITenantConnectionResolver.LeaseAsync"/> calls
/// build a fresh pool); only the underlying data-source teardown is
/// deferred.</para>
///
/// <para>Disposal: always wrap in <c>await using</c>. After disposal,
/// reading <see cref="DataSource"/> throws
/// <see cref="ObjectDisposedException"/>.</para>
/// </summary>
public interface ITenantConnectionLease : IAsyncDisposable
{
    /// <summary>Owning tenant id. Useful for diagnostic logging.</summary>
    Guid TenantId { get; }

    /// <summary>
    /// The per-tenant data source. Valid for the lifetime of the
    /// lease. Reading after disposal throws
    /// <see cref="ObjectDisposedException"/>.
    /// </summary>
    NpgsqlDataSource DataSource { get; }
}
