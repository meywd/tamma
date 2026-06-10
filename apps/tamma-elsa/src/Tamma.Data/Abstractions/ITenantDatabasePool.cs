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
    /// The <paramref name="commandText"/> is sent verbatim — callers are
    /// responsible for quoting identifiers (use
    /// <c>TenantNaming.Quote</c>) and must never inline untrusted input.
    /// </summary>
    Task<int> ExecuteOnAsync(Guid databaseId, string commandText, CancellationToken ct = default);

    /// <summary>True when pg_roles on the row's cluster has the role.</summary>
    Task<bool> RoleExistsOnAsync(Guid databaseId, string roleName, CancellationToken ct = default);

    /// <summary>
    /// True when the row's database contains the schema
    /// (<c>information_schema.schemata</c> probe). Idempotency probe for
    /// the delete path (Phase 2 Task 5) — a workflow retry after the
    /// schema was already dropped must skip cleanly, mirroring the legacy
    /// <see cref="ITenantAdminConnection.DatabaseExistsAsync"/> probe.
    /// </summary>
    Task<bool> SchemaExistsOnAsync(Guid databaseId, string schemaName, CancellationToken ct = default);

    /// <summary>
    /// Discrete connection parameters of the pool row's admin connection,
    /// targeting the row's OWN database — for external CLI tooling
    /// (notably <c>pg_dump</c> in the pre-drop backup step) that needs the
    /// values separately rather than as one Npgsql connection string.
    /// Mirrors <see cref="ITenantAdminConnection.GetConnectionInfo"/>
    /// (Phase 2 Task 5 interface growth, pre-authorized by the plan). The
    /// password is returned so the caller can pass it via the
    /// <c>PGPASSWORD</c> environment variable — callers MUST NOT place it
    /// on a process command line (it would leak via
    /// <c>/proc/&lt;pid&gt;/cmdline</c>).
    /// </summary>
    Task<TenantAdminConnectionInfo> GetConnectionInfoAsync(Guid databaseId, CancellationToken ct = default);

    /// <summary>
    /// Database name of the pool row's target database, parsed from the
    /// decrypted admin connection string. Needed by the schema step for
    /// <c>GRANT CONNECT ON DATABASE</c> / <c>ALTER ROLE ... IN DATABASE</c>
    /// (Phase 2 Task 3 interface growth, pre-authorized by the plan).
    /// </summary>
    Task<string> GetDatabaseNameAsync(Guid databaseId, CancellationToken ct = default);

    /// <summary>
    /// Tenant-facing connection string: the row's Host/Port/SSL + the
    /// row's database + the tenant role/password +
    /// <c>Search Path=&lt;schemaName&gt;</c>.
    /// </summary>
    Task<string> BuildTenantConnectionStringAsync(
        Guid databaseId, string roleName, string password, string schemaName,
        CancellationToken ct = default);
}
