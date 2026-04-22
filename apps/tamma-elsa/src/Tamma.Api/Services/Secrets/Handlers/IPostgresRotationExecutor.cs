namespace Tamma.Api.Services.Secrets.Handlers;

/// <summary>
/// Story 29-7 — narrow port so
/// <see cref="PostgresRoleRotationHandler"/> can be unit-tested
/// without standing up a Postgres container for every test. Covers:
///
/// <list type="bullet">
///   <item><description>Executing <c>ALTER ROLE</c> on the admin
///     connection.</description></item>
///   <item><description>Probing a role's credentials by opening a
///     fresh pool with the new password and running a lightweight
///     query.</description></item>
///   <item><description>Draining the connection pool for the old
///     password after the grace window.</description></item>
/// </list>
///
/// <para>Integration tests use the real Npgsql-backed implementation
/// against a Testcontainers Postgres; unit tests use a fake.</para>
/// </summary>
public interface IPostgresRotationExecutor
{
    /// <summary>
    /// Execute an <c>ALTER ROLE &lt;role&gt; WITH PASSWORD '...'</c>
    /// statement on the admin connection string. The caller passes the
    /// already-escaped SQL — no further escaping is applied.
    /// </summary>
    Task AlterRolePasswordAsync(
        string adminConnectionString,
        string roleName,
        string newPassword,
        CancellationToken ct);

    /// <summary>
    /// Alter a role to have a NULL password — effectively disabling
    /// login for that role. Used by the rollback path when there was
    /// no previous active version to restore.
    /// </summary>
    Task SetRolePasswordNullAsync(
        string adminConnectionString,
        string roleName,
        CancellationToken ct);

    /// <summary>
    /// Open a fresh, single-use connection using the supplied
    /// connection string (which should contain the new password) and
    /// run <c>SELECT 1</c>. Returns the elapsed milliseconds, or
    /// throws on any error (caller converts the exception to a
    /// <c>ProbeResult.Unhealthy</c>).
    /// </summary>
    Task<long> ProbeRoleAsync(string probeConnectionString, CancellationToken ct);

    /// <summary>
    /// Force the Npgsql connection pool backing
    /// <paramref name="connectionString"/> to close idle connections.
    /// </summary>
    void DrainPool(string connectionString);
}
