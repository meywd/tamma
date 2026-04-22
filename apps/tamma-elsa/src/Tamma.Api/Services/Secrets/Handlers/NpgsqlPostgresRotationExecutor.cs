using System.Diagnostics;
using Npgsql;

namespace Tamma.Api.Services.Secrets.Handlers;

/// <summary>
/// Story 29-7 — production
/// <see cref="IPostgresRotationExecutor"/> backed by Npgsql. Opens
/// one-shot connections per operation (no pooling across rotations)
/// so the admin credentials are only resident in memory for the
/// duration of the ALTER statement.
/// </summary>
public sealed class NpgsqlPostgresRotationExecutor : IPostgresRotationExecutor
{
    public async Task AlterRolePasswordAsync(
        string adminConnectionString,
        string roleName,
        string newPassword,
        CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(adminConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        var escapedRole = SqlLiteralEscaper.EscapeIdentifier(roleName);
        var escapedPw = SqlLiteralEscaper.Escape(newPassword);
        var sql = $"ALTER ROLE \"{escapedRole}\" WITH PASSWORD '{escapedPw}'";

        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task SetRolePasswordNullAsync(
        string adminConnectionString,
        string roleName,
        CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(adminConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        var escapedRole = SqlLiteralEscaper.EscapeIdentifier(roleName);
        var sql = $"ALTER ROLE \"{escapedRole}\" WITH PASSWORD NULL";

        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<long> ProbeRoleAsync(string probeConnectionString, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        // Build a single-use datasource rather than reusing the default
        // pool. ClearAllPools on the datasource is cheap and avoids
        // polluting the long-lived pools in the application.
        await using var dataSource = NpgsqlDataSource.Create(probeConnectionString);
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1";
        await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        sw.Stop();
        return sw.ElapsedMilliseconds;
    }

    public void DrainPool(string connectionString)
    {
        // NpgsqlConnection.ClearPool targets the default pool bucket
        // keyed by connection-string equality. The caller passes the
        // exact old-password connection string so the pool it built
        // from that earlier password is the one that drains.
        using var conn = new NpgsqlConnection(connectionString);
        NpgsqlConnection.ClearPool(conn);
    }
}
