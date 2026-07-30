using System.Data;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Tamma.Data.Pooling;

/// <summary>
/// The one correct way to hold a SESSION-scoped Postgres advisory lock:
/// on a connection that is <b>not pooled</b>.
///
/// <para><b>Why this type exists</b> (2026-07-30 audit; the defect was
/// proven in <c>TenantMigrationSweepRunner</c>, commit <c>b958adc</c>).
/// Every advisory-lock holder in this codebase documented the same
/// invariant — "the lock is session-scoped, so closing the connection
/// releases it, so there is no way to wedge the gate shut". <b>That
/// invariant is false on a pooled connection.</b> Disposing a pooled
/// <see cref="NpgsqlConnection"/> — whether it came from EF's
/// <c>DbContext</c>, from an <see cref="NpgsqlDataSource"/>, or from
/// <c>new NpgsqlConnection(cs)</c> against an ordinary connection string —
/// hands the connector back to the pool with the backend session, and
/// therefore the advisory lock, <b>still alive</b>. Npgsql defers the
/// <c>DISCARD ALL</c> reset (which is what runs
/// <c>pg_advisory_unlock_all()</c>) until that connector is next USED. So
/// any exit path that drops the connection without a successful explicit
/// <c>pg_advisory_unlock</c> parks the lock on an idle connector — for up
/// to <c>Connection Idle Lifetime</c> (300s by default), or <b>forever</b>
/// for a <c>MinPoolSize</c> connector that is never pruned. Whether the
/// gate is open then depends on which connector the pool happens to hand
/// out next, which is not a property any gate should have.</para>
///
/// <para>Because that failure mode is invisible in tests (a pooled probe
/// can draw the leaking connector and repair the very state it was sent
/// to measure), the answer is <b>not</b> "add another cleanup path".
/// It is to make the documented invariant literally true: open a
/// <c>Pooling=false</c> connection, so closing it really does end the
/// backend session and really does drop the lock — on the orderly release
/// path, on an unlock that throws, on cancellation, on a dropped
/// <c>finally</c>, and on a process crash alike. The explicit unlock in
/// <see cref="PostgresAdvisoryLockLease.DisposeAsync"/> is kept only to
/// make the release prompt and greppable; it is no longer the guarantee.</para>
///
/// <para><b>Contract for callers: do not "optimise" the
/// <c>Pooling=false</c> away.</b> It is load-bearing, not a tuning
/// choice. The cost is one extra TCP connect per critical section, which
/// every caller here takes at most once per run / per hour / per move.</para>
///
/// <para><see cref="Tamma.Data.Pooling.TenantMigrationSweepRunner"/> keeps
/// its own inlined copy of this pattern rather than using this helper: its
/// lease additionally carries an <c>Interlocked</c> ownership handoff
/// between <c>ReleaseAsync</c> and <c>Dispose</c>, and a watchdog that
/// re-verifies the lock is still held mid-sweep. Those are sweep-specific;
/// the session discipline is identical.</para>
/// </summary>
public static class PostgresAdvisoryLock
{
    /// <summary>
    /// Rewrite <paramref name="connectionString"/> so the connection it
    /// opens is NOT pooled. This single flag is what makes "closing the
    /// connection releases the lock" true — see the type doc.
    /// </summary>
    public static string ToUnpooledConnectionString(string connectionString)
        => new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }
            .ConnectionString;

    /// <summary>
    /// Open a dedicated, non-pooled session and try to take
    /// <c>pg_try_advisory_lock</c> on it. <c>pg_try_advisory_lock</c> never
    /// blocks: it returns false immediately when someone else holds the key.
    ///
    /// <para>Returns a lease that owns the session (dispose it to release),
    /// or <c>null</c> when the lock is held elsewhere — in which case the
    /// session is closed before returning, so a refused attempt leaks
    /// nothing. A failure to connect or to execute propagates, with the
    /// session closed; callers decide whether that is fatal (it should
    /// generally be treated as "did not acquire", never as "acquired").</para>
    /// </summary>
    public static async Task<PostgresAdvisoryLockLease?> TryAcquireAsync(
        string connectionString,
        PostgresAdvisoryLockKey key,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var session = new NpgsqlConnection(ToUnpooledConnectionString(connectionString));
        try
        {
            await session.OpenAsync(ct).ConfigureAwait(false);

            await using var cmd = session.CreateCommand();
            cmd.CommandText = $"SELECT pg_try_advisory_lock({key.KeyExpressionSql});";
            key.Bind(cmd);
            var acquired = (bool?)await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) == true;

            if (!acquired)
            {
                await session.DisposeAsync().ConfigureAwait(false);
                return null;
            }
        }
        catch
        {
            // Includes the narrow window where the backend granted the lock
            // but the client never saw the reply (a cancellation, a dropped
            // socket). Because the session is NOT pooled, closing it here
            // ends that backend and drops any lock it managed to take.
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new PostgresAdvisoryLockLease(session, key, logger);
    }
}

/// <summary>
/// The key half of an advisory-lock call, kept as SQL + one bound
/// parameter so each call site's <b>exact</b> key expression is preserved.
/// Two call sites that compute their key differently
/// (<c>pg_try_advisory_lock(@k)</c> on a precomputed bigint vs
/// <c>pg_try_advisory_lock(hashtextextended(@t, 0))</c>) must keep
/// computing it exactly as before — changing where a key is derived
/// silently changes who the lock excludes.
/// </summary>
public readonly record struct PostgresAdvisoryLockKey
{
    private PostgresAdvisoryLockKey(string keyExpressionSql, string parameterName, object value, string description)
    {
        KeyExpressionSql = keyExpressionSql;
        ParameterName = parameterName;
        Value = value;
        Description = description;
    }

    /// <summary>SQL fragment that evaluates to the bigint lock key.</summary>
    public string KeyExpressionSql { get; }

    /// <summary>Name of the single parameter <see cref="KeyExpressionSql"/> reads.</summary>
    public string ParameterName { get; }

    /// <summary>Value bound to <see cref="ParameterName"/>.</summary>
    public object Value { get; }

    /// <summary>Human-readable key, for logs only.</summary>
    public string Description { get; }

    /// <summary>A key that is already a 64-bit integer.</summary>
    public static PostgresAdvisoryLockKey FromInt64(long key)
        => new("@k", "k", key, key.ToString());

    /// <summary>
    /// A key derived in the database by <c>hashtextextended(text, 0)</c>.
    /// The hash is deliberately computed by Postgres, not in C#: that is
    /// how the existing per-tenant move lock is keyed, and re-deriving it
    /// client-side would produce a different number and therefore a
    /// different lock.
    /// </summary>
    public static PostgresAdvisoryLockKey FromHashTextExtended(string text)
        => new("hashtextextended(@t, 0)", "t", text, $"hashtextextended('{text}', 0)");

    internal void Bind(NpgsqlCommand cmd)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = ParameterName;
        p.Value = Value;
        cmd.Parameters.Add(p);
    }
}

/// <summary>
/// A held advisory lock plus the non-pooled session that holds it.
/// Disposing releases the lock — first by an explicit
/// <c>pg_advisory_unlock</c> (prompt and greppable in logs), then, and
/// this is the actual guarantee, by ending the session.
/// </summary>
public sealed class PostgresAdvisoryLockLease : IAsyncDisposable
{
    private readonly ILogger? _logger;
    private NpgsqlConnection? _session;

    internal PostgresAdvisoryLockLease(NpgsqlConnection session, PostgresAdvisoryLockKey key, ILogger? logger)
    {
        _session = session;
        Key = key;
        _logger = logger;
    }

    /// <summary>The key this lease holds.</summary>
    public PostgresAdvisoryLockKey Key { get; }

    /// <summary>
    /// The non-pooled session carrying the lock. Exposed so a long-running
    /// critical section can re-verify liveness before doing something
    /// destructive: a connection can die without the process dying, which
    /// releases the lock while the guarded work carries on believing it is
    /// alone (the reverse hazard —
    /// <see cref="Tamma.Data.Pooling.TenantMigrationSweepRunner"/> is the
    /// only caller that currently guards against it). Null once disposed.
    /// </summary>
    public NpgsqlConnection? Session => Volatile.Read(ref _session);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // Interlocked, not read-then-null: two racing disposals must not
        // both dispose the same connection.
        var session = Interlocked.Exchange(ref _session, null);
        if (session is null) return;

        try
        {
            if (session.State == ConnectionState.Open)
            {
                await using var cmd = session.CreateCommand();
                cmd.CommandText = $"SELECT pg_advisory_unlock({Key.KeyExpressionSql});";
                Key.Bind(cmd);
                // Deliberately NOT the caller's token: this runs on the
                // cancellation and shutdown paths too, and an unlock that
                // is itself cancelled is an unlock that never happened.
                await cmd.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // Safe to swallow ONLY because the session below is not pooled:
            // disposing it ends the backend, which releases the lock. On a
            // pooled connection this catch is the bug — see the type doc.
            _logger?.LogDebug(ex, "pg.advisory_lock.unlock_failed key={Key}", Key.Description);
        }
        finally
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }
}
