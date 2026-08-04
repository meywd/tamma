using Npgsql;

namespace Tamma.Api.Tests.TestDoubles;

/// <summary>
/// Reads the Postgres advisory-lock state of a cluster from a connection
/// that is deliberately <b>NOT POOLED</b>.
///
/// <para><b>The "not pooled" part is the whole point</b> (2026-07-30 audit;
/// the same subtlety is spelled out in
/// <c>TenantMigrationSweepRunnerTests.SweepLockIsHeldOnTheClusterAsync</c>).
/// A pooled observer can be handed the very connector that leaked a lock,
/// and Npgsql prepends its deferred <c>DISCARD ALL</c> reset — which runs
/// <c>pg_advisory_unlock_all()</c> — to the observer's own query. The probe
/// then silently repairs the state it was sent to measure and reports
/// "released". That is exactly how the pooled-advisory-lock defect hid for
/// so long; do not "simplify" these connections back onto a pool.</para>
/// </summary>
internal static class AdvisoryLockProbe
{
    private static async Task<NpgsqlConnection> OpenUnpooledAsync(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };
        var conn = new NpgsqlConnection(builder.ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    /// <summary>
    /// The backend pid holding advisory lock <paramref name="key"/> on this
    /// database, or null if nobody holds it.
    ///
    /// <para>The <c>|</c> reconstruction of the 64-bit key from
    /// <c>classid</c>/<c>objid</c> is a STYLE choice, matching
    /// <c>PostgresAdvisoryLockKey.HeldByThisBackendSql</c> and the sweep
    /// runner's cross-pod probe so all three read the same. <c>+</c> is
    /// exactly equivalent — the high term is always a multiple of 2^32 and the
    /// low term always in <c>[0, 2^32)</c>, so the bits never overlap and int8
    /// never overflows. Verified on postgres:17 over 500k random
    /// <c>(classid, objid)</c> pairs plus every boundary case: 0 differences,
    /// negatives included. An earlier comment here claimed negative
    /// <c>hashtextextended</c> keys FORCED the OR form; that was false and is
    /// corrected here so nobody "fixes" a working site (2026-07-31 review,
    /// F3).</para>
    /// </summary>
    public static async Task<int?> HolderPidAsync(string connectionString, long key)
    {
        await using var conn = await OpenUnpooledAsync(connectionString);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT pid FROM pg_locks
            WHERE locktype = 'advisory'
              AND granted
              AND mode = 'ExclusiveLock'
              AND objsubid = 1
              AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
              AND ((classid::bigint << 32) | objid::bigint) = @k
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("k", key);
        var result = await cmd.ExecuteScalarAsync();
        return result is int pid ? pid : null;
    }

    /// <summary>Is advisory lock <paramref name="key"/> held by anyone?</summary>
    public static async Task<bool> IsHeldAsync(string connectionString, long key)
        => await HolderPidAsync(connectionString, key) is not null;

    /// <summary>
    /// Does backend <paramref name="pid"/> still exist? A pooled connector
    /// that was "closed" is still a live backend sitting idle in the pool;
    /// a non-pooled one is gone. That difference is the whole fix.
    /// </summary>
    public static async Task<bool> BackendIsAliveAsync(string connectionString, int pid)
    {
        await using var conn = await OpenUnpooledAsync(connectionString);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE pid = @p);";
        cmd.Parameters.AddWithValue("p", pid);
        return (bool?)await cmd.ExecuteScalarAsync() == true;
    }

    /// <summary>
    /// Wait (briefly) for backend <paramref name="pid"/> to disappear.
    /// Closing a socket and the backend actually exiting are not the same
    /// instant, so poll rather than sleep-and-hope.
    /// </summary>
    public static async Task<bool> WaitForBackendGoneAsync(
        string connectionString, int pid, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!await BackendIsAliveAsync(connectionString, pid)) return true;
            await Task.Delay(50);
        }
        return !await BackendIsAliveAsync(connectionString, pid);
    }

    /// <summary>
    /// Kill the backend holding advisory lock <paramref name="key"/>, from a
    /// completely separate session — the process under test is untouched,
    /// exactly as a pooler recycle, an idle timeout or a DBA's
    /// <c>pg_terminate_backend</c> would be. Returns true when a backend was
    /// actually terminated (so a test can assert it really did kill
    /// something, rather than passing because there was nothing to kill).
    ///
    /// <para>This is the REVERSE hazard's probe: it releases the lock while
    /// the work the lock guards keeps running. Everything else in this class
    /// tests the direction "the lock outlived the work".</para>
    /// </summary>
    public static async Task<bool> TerminateHolderAsync(string connectionString, long key)
    {
        await using var conn = await OpenUnpooledAsync(connectionString);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT pg_terminate_backend(pid) FROM pg_locks
            WHERE locktype = 'advisory'
              AND granted
              AND mode = 'ExclusiveLock'
              AND objsubid = 1
              AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
              AND ((classid::bigint << 32) | objid::bigint) = @k;
            """;
        cmd.Parameters.AddWithValue("k", key);
        return (bool?)await cmd.ExecuteScalarAsync() == true;
    }

    /// <summary>What does the database compute for <c>hashtextextended(text, 0)</c>?</summary>
    public static async Task<long> HashTextExtendedAsync(string connectionString, string text)
    {
        await using var conn = await OpenUnpooledAsync(connectionString);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT hashtextextended(@t, 0);";
        cmd.Parameters.AddWithValue("t", text);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
