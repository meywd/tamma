namespace Tamma.Data.Abstractions;

/// <summary>
/// Story 28-5 — port for executing privileged DDL on the cluster's
/// <c>postgres</c> (or <c>tamma_provisioner</c>) admin DB during tenant
/// create/delete workflows. The interface is a thin abstraction over an
/// admin <see cref="Npgsql.NpgsqlConnection"/> so workflow activities can
/// be unit-tested without standing up a real Postgres — tests inject a
/// stub that records each command, the production adapter actually opens
/// the admin connection.
///
/// <para>The contract is intentionally small. The four primitives below
/// cover everything <see cref="CreateTenantWorkflow"/> +
/// <see cref="DeleteTenantWorkflow"/> need today:</para>
///
/// <list type="bullet">
///   <item><description><see cref="RoleExistsAsync"/> + <see cref="DatabaseExistsAsync"/>
///     for idempotency probes against <c>pg_roles</c> /
///     <c>pg_database</c>.</description></item>
///   <item><description><see cref="ExecuteAsync"/> for
///     <c>CREATE/DROP ROLE</c>, <c>CREATE DATABASE</c>, etc.
///     <c>DROP DATABASE … WITH (FORCE)</c> requires us to issue the
///     statement on the admin connection (you cannot drop the database
///     you are connected to), so the runner targets a fixed admin DB —
///     <c>postgres</c> by default.</description></item>
///   <item><description><see cref="BuildTenantConnectionString"/> derives
///     the per-tenant connection string from the admin connection's host
///     / port / SSL settings + the freshly-generated tenant role +
///     password. Centralised here so the workflow doesn't have to know
///     about Npgsql connection-string syntax.</description></item>
/// </list>
///
/// <para>Security: the production adapter expects a connection string
/// configured under <c>ConnectionStrings:TenantAdmin</c> (falls back to
/// <c>ConnectionStrings:DefaultConnection</c>). The role supplying that
/// connection MUST have <c>CREATEDB</c> + <c>CREATEROLE</c> (per Doc 01
/// §7.1 — the <c>tamma_provisioner</c> role) but should NOT be
/// <c>SUPERUSER</c>.</para>
/// </summary>
public interface ITenantAdminConnection
{
    /// <summary>
    /// True when a row exists in <c>pg_roles</c> with
    /// <c>rolname = roleName</c>. Idempotency probe used by the
    /// <c>CreateRole</c> step so a workflow retry doesn't fail with
    /// <c>42710 (duplicate_object)</c>.
    /// </summary>
    Task<bool> RoleExistsAsync(string roleName, CancellationToken ct = default);

    /// <summary>
    /// True when a row exists in <c>pg_database</c> with
    /// <c>datname = databaseName</c>.
    /// </summary>
    Task<bool> DatabaseExistsAsync(string databaseName, CancellationToken ct = default);

    /// <summary>
    /// Execute one or more SQL statements on the admin connection. The
    /// <paramref name="commandText"/> is sent verbatim — callers are
    /// responsible for quoting identifiers (use double-quotes around
    /// names that contain hyphens, dots, or mixed case). The runner
    /// returns the number of rows affected reported by Npgsql.
    ///
    /// <para>For <c>DROP DATABASE … WITH (FORCE)</c> the command MUST run
    /// outside any user transaction. The default adapter opens a fresh
    /// admin connection and uses <c>NpgsqlCommand.ExecuteNonQuery</c>
    /// (no <c>BEGIN</c>/<c>COMMIT</c>) for each invocation.</para>
    /// </summary>
    Task<int> ExecuteAsync(string commandText, CancellationToken ct = default);

    /// <summary>
    /// Build the per-tenant connection string the LRU pool will consume.
    /// Combines the admin connection's host / port / SSL with the
    /// supplied <paramref name="databaseName"/>, <paramref name="roleName"/>
    /// and <paramref name="password"/>. Username, Password, Database,
    /// ApplicationName are overwritten on the admin builder; everything
    /// else (Host, Port, SSL Mode, Trust Server Certificate, Search Path)
    /// is preserved.
    /// </summary>
    string BuildTenantConnectionString(
        string databaseName,
        string roleName,
        string password);
}
