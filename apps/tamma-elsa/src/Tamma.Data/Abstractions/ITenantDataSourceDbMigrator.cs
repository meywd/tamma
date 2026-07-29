using Npgsql;

namespace Tamma.Data.Abstractions;

/// <summary>
/// Story 44-1 — the data-source flavour of <see cref="ITenantDbMigrator"/>,
/// for callers that reach a tenant through
/// <see cref="ITenantConnectionResolver.GetDataSourceAsync"/> (the sweep).
///
/// <para><b>Why a second seam exists:</b> <see cref="NpgsqlDataSource.ConnectionString"/>
/// strips the password (Npgsql's Persist-Security-Info posture), so a caller
/// holding only a resolved data source can never reconstruct a connection
/// string the string-based <see cref="ITenantDbMigrator.MigrateTenantAppAsync"/>
/// could authenticate with — it fails with "No password has been provided"
/// (SASL/SCRAM). Migrating OVER the data source keeps the credentials where
/// they already live. The tenant schema is still derived from the data
/// source's connection string (<c>Search Path</c> survives the stripping;
/// only the password is removed).</para>
///
/// <para>Kept separate from <see cref="ITenantDbMigrator"/> so the existing
/// interface — and every test stub implementing it — is unchanged.
/// <see cref="Pooling.EfTenantDbMigrator"/> implements both over one core.</para>
/// </summary>
public interface ITenantDataSourceDbMigrator
{
    /// <summary>
    /// Run the tenant-app EF migration set over <paramref name="dataSource"/>
    /// (idempotent — the per-schema <c>__TenantMigrationsHistory</c> makes a
    /// replay a fast no-op). The caller does NOT own the data source's
    /// lifetime (the resolver does); no connection is left open on return.
    /// </summary>
    Task MigrateTenantAppAsync(NpgsqlDataSource dataSource, CancellationToken ct = default);

    /// <summary>
    /// The number of migrations not yet recorded in the tenant's history
    /// table. A schema with no history table (a tenant provisioned before the
    /// first sweep) reports the full set. Applies nothing.
    /// </summary>
    Task<int> CountPendingMigrationsAsync(NpgsqlDataSource dataSource, CancellationToken ct = default);
}
