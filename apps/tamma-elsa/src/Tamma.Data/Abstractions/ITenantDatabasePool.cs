namespace Tamma.Data.Abstractions;

/// <summary>
/// Unified-tenancy Phase 2 — accessor over the <c>tenant_databases</c>
/// registry (the operator's DB pool). Decrypts a pool row's admin
/// connection and derives tenant-facing connection strings against the
/// TARGET database. Roles are cluster-scoped, so every DDL the tenant
/// lifecycle runs (CREATE ROLE / SCHEMA / GRANT / DROP) must go through
/// the assigned row's admin connection — never the central
/// <see cref="ITenantAdminConnection"/>.
/// </summary>
public interface ITenantDatabasePool
{
    /// <summary>Decrypted admin connection string of the pool row.</summary>
    Task<string> GetAdminConnectionStringAsync(Guid databaseId, CancellationToken ct = default);

    /// <summary>
    /// Execute one statement on the pool row's admin connection
    /// (autocommit, fresh connection — mirrors ITenantAdminConnection).
    /// </summary>
    Task<int> ExecuteOnAsync(Guid databaseId, string commandText, CancellationToken ct = default);

    /// <summary>True when pg_roles on the row's cluster has the role.</summary>
    Task<bool> RoleExistsOnAsync(Guid databaseId, string roleName, CancellationToken ct = default);

    /// <summary>
    /// Tenant-facing connection string: the row's Host/Port/SSL + the
    /// row's database + the tenant role/password +
    /// <c>Search Path=&lt;schemaName&gt;</c>.
    /// </summary>
    Task<string> BuildTenantConnectionStringAsync(
        Guid databaseId, string roleName, string password, string schemaName,
        CancellationToken ct = default);
}
