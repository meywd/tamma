using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Tamma.Data.Pooling;

/// <summary>
/// The one correct way to hold a SESSION-scoped Postgres advisory lock:
/// on a connection that is <b>not pooled</b>, opened from a connection
/// string that still carries its credentials, and — for a critical
/// section long enough to outlive a connection — watched.
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
/// <para><b>The reverse hazard, and the watchdog</b> (2026-07-30
/// follow-up). "The lock dies with the session" cuts both ways: a session
/// can die without the process dying — a pooler recycle, an idle timeout,
/// a network blip, an administrative <c>pg_terminate_backend</c> — which
/// silently OPENS the gate while the work it guards carries on believing
/// it is alone. A second pod is then admitted into a critical section that
/// was supposed to be exclusive. For a critical section that is short and
/// bounded that is a tolerable risk; for one that spans minutes with the
/// lock session sitting idle (a dump/restore, a fleet-wide re-encrypt) it
/// is not. <see cref="PostgresAdvisoryLockLease.WatchLiveness"/> starts a
/// heartbeat that re-reads <c>pg_locks</c> <b>from the lease session
/// itself</b>, pinned to that backend's pid, and cancels a token linked to
/// the caller's on loss — so the guarded work aborts instead of running
/// unguarded. See its own docs for why a FAILED probe counts as loss.</para>
///
/// <para><b>Where the connection string comes from matters too</b> — see
/// <see cref="TryResolveSessionConnectionString"/>. Re-opening a dedicated
/// session means re-parsing a connection string, and the two obvious
/// sources silently drop the password.</para>
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

    // ───────────────────── connection-string sourcing ─────────────────────

    /// <summary>
    /// <c>ConnectionStrings:</c> keys, in the order the host itself resolves
    /// the control plane. Mirrors <c>Tamma.Api.Infrastructure.ConnectionStringResolver</c>
    /// (<c>ResolveControlPlane</c> then <c>ResolveAdmin</c>); the order is
    /// pinned by <c>PostgresAdvisoryLockConnectionStringTests</c> so the two
    /// cannot drift apart silently.
    /// </summary>
    public const string ControlPlaneConnectionStringKey = "ControlPlane";

    /// <inheritdoc cref="ControlPlaneConnectionStringKey"/>
    public const string AdminConnectionStringKey = "TammaDb";

    /// <inheritdoc cref="ControlPlaneConnectionStringKey"/>
    public const string LegacyAdminConnectionStringKey = "DefaultConnection";

    /// <summary>
    /// Is this connection string usable for opening a NEW session — i.e.
    /// does it still carry a password?
    ///
    /// <para>A password-less string is the specific trap the 2026-07-30
    /// audit fell into. Npgsql defaults <c>PersistSecurityInfo</c> to false,
    /// so <b><see cref="NpgsqlDataSource.ConnectionString"/> NEVER carries
    /// the password</b>, and EF's <c>Database.GetConnectionString()</c>
    /// carries it in most shapes but NOT when an <see cref="NpgsqlDataSource"/>
    /// is registered in DI (the Npgsql EF provider then mints the context's
    /// <c>DbConnection</c> from that data source, and inherits its laundered
    /// string). Re-parsing one of those yields a connection that cannot
    /// authenticate — and since every caller here correctly treats a failed
    /// lock attempt as "did not acquire", that turns a silent credential
    /// loss into a permanently-closed gate, reported to the operator as
    /// "someone else is already running".</para>
    /// </summary>
    public static bool HasCredentials(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return false;
        try
        {
            return !string.IsNullOrEmpty(
                new NpgsqlConnectionStringBuilder(connectionString).Password);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// The control-plane connection string as CONFIGURATION sees it:
    /// <c>ControlPlane</c> → <c>TammaDb</c> → <c>DefaultConnection</c>.
    /// Raw configuration is the only source Npgsql never launders, which is
    /// why it is preferred over EF's view.
    /// </summary>
    public static string? FromConfiguration(IConfiguration? configuration)
    {
        if (configuration is null) return null;

        foreach (var key in new[]
                 {
                     ControlPlaneConnectionStringKey,
                     AdminConnectionStringKey,
                     LegacyAdminConnectionStringKey,
                 })
        {
            var value = configuration.GetConnectionString(key);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        return null;
    }

    /// <summary>
    /// Where a dedicated lock session's connection string must come from.
    /// <b>Every</b> caller of <see cref="TryAcquireAsync"/> that re-opens a
    /// control-plane session should source it here rather than reaching for
    /// a data source or an EF context directly — four sites independently
    /// re-derived the pooled-connection bug, and the same argument applies
    /// to this second trap.
    ///
    /// <para>Order, and why:</para>
    /// <list type="number">
    ///   <item><description><b>Configuration, if it still carries a
    ///     password.</b> This is the string the host itself resolved and the
    ///     one the singleton <see cref="NpgsqlDataSource"/> was built from,
    ///     so it names the same database the lock has always lived on, and
    ///     it is raw — Npgsql never touches it.</description></item>
    ///   <item><description><b>EF's <c>GetConnectionString()</c>, if it
    ///     still carries a password.</b> Correct in most shapes, and it is
    ///     what a container without configuration (unit fixtures that bind
    ///     a context straight to a container) has to use.</description></item>
    ///   <item><description><b>Configuration verbatim, even without a
    ///     password.</b> A trust-auth / integrated-security deployment
    ///     legitimately has none, and configuration is never laundered — so
    ///     a missing password THERE means the deployment genuinely has no
    ///     password, not that one was stripped. Refusing this tier would
    ///     cost those deployments the lock entirely.</description></item>
    /// </list>
    ///
    /// <para>What is deliberately NOT a tier: a password-less string that
    /// only EF produced. That is indistinguishable from a laundered one, and
    /// it is precisely the shape observed in production. Returning null there
    /// makes the caller fail CLOSED — a sweep/move/checkpoint/rotation that
    /// refuses to start and says why — rather than opening an unauthenticable
    /// connection and reporting the resulting error as "someone else holds
    /// the gate".</para>
    ///
    /// <para>Pass <paramref name="efContext"/> as <c>null</c> (or a
    /// non-relational context) when there is no EF view to consult; the
    /// method checks <c>IsRelational()</c> itself so callers do not have
    /// to.</para>
    /// </summary>
    public static string? TryResolveSessionConnectionString(
        IConfiguration? configuration,
        DbContext? efContext,
        ILogger? logger = null,
        string? site = null)
    {
        var fromConfiguration = FromConfiguration(configuration);
        if (HasCredentials(fromConfiguration)) return fromConfiguration;

        string? fromEf = null;
        if (efContext is not null && efContext.Database.IsRelational())
        {
            fromEf = efContext.Database.GetConnectionString();
            if (HasCredentials(fromEf)) return fromEf;
        }

        if (!string.IsNullOrWhiteSpace(fromConfiguration))
        {
            logger?.LogDebug(
                "pg.advisory_lock.connection_string site={Site} source=configuration_without_password "
                + "(trust-auth / integrated-security deployment: configuration is raw, so a missing "
                + "password there is the deployment's own, not a stripped one)", site ?? "unknown");
            return fromConfiguration;
        }

        logger?.LogWarning(
            "pg.advisory_lock.connection_string_unusable site={Site} configured={HasConfigured} "
            + "ef={HasEf} — refusing to open a lock session from a password-less connection string "
            + "that only EF produced: NpgsqlDataSource.ConnectionString never carries the password, "
            + "and EF inherits that laundered string whenever an NpgsqlDataSource is registered in "
            + "DI. Set ConnectionStrings:{ControlPlaneKey} (or {AdminKey} / {LegacyKey}) so the lock "
            + "can open its own session.",
            site ?? "unknown",
            fromConfiguration is not null,
            fromEf is not null,
            ControlPlaneConnectionStringKey,
            AdminConnectionStringKey,
            LegacyAdminConnectionStringKey);
        return null;
    }

    /// <summary>
    /// <see cref="TryResolveSessionConnectionString"/> for the callers whose
    /// fail-closed path is an exception rather than a status flip. The
    /// message names the mechanism, because "connection string missing" sends
    /// an operator looking in the wrong place.
    /// </summary>
    public static string ResolveSessionConnectionString(
        IConfiguration? configuration,
        DbContext? efContext,
        string site,
        ILogger? logger = null)
        => TryResolveSessionConnectionString(configuration, efContext, logger, site)
           ?? throw new InvalidOperationException(
               $"No usable control-plane connection string for {site}'s dedicated advisory-lock "
               + "session. The lock must be taken on its own NON-POOLED session, which means "
               + "re-opening from a connection string — and the only candidate available here "
               + "carries no password. NpgsqlDataSource.ConnectionString never carries one, and "
               + "EF's Database.GetConnectionString() inherits that laundered string whenever an "
               + $"NpgsqlDataSource is registered in DI. Set ConnectionStrings:"
               + $"{ControlPlaneConnectionStringKey} (or {AdminConnectionStringKey} / "
               + $"{LegacyAdminConnectionStringKey}) — refusing to continue unguarded.");
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

    /// <summary>
    /// "Is this exact advisory lock still granted to the session running
    /// this query?"
    ///
    /// <para>Fully qualified on purpose. <c>pg_locks</c> is CLUSTER-wide
    /// while advisory locks are per-database, so an unqualified match finds
    /// a same-keyed lock held in a completely different database on the same
    /// cluster. <c>objsubid</c> distinguishes the one-argument
    /// <c>pg_advisory_lock(bigint)</c> form (1) from the two-argument
    /// <c>(int, int)</c> form (2), whose halves reassemble to the same 64-bit
    /// value but are a different lock entirely. And <c>pid =
    /// pg_backend_pid()</c> is the point of the whole exercise: the question
    /// is not "does anyone hold this key" — someone else holding it is
    /// exactly the disaster being watched for — but "do <b>I</b> still".</para>
    ///
    /// <para>The 64-bit key is reassembled with <c>|</c> rather than
    /// <c>+</c> because <c>hashtextextended</c> keys are frequently negative
    /// and the OR form round-trips those without relying on wrap-around.</para>
    /// </summary>
    internal string HeldByThisBackendSql() =>
        $"""
        SELECT EXISTS (
            SELECT 1 FROM pg_locks
            WHERE locktype = 'advisory'
              AND granted
              AND objsubid = 1
              AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
              AND pid = pg_backend_pid()
              AND ((classid::bigint << 32) | objid::bigint) = {KeyExpressionSql}
        );
        """;
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
    /// The non-pooled session carrying the lock. Null once disposed.
    /// Prefer <see cref="WatchLiveness"/> over poking at this directly —
    /// it exists mostly so tests can simulate a dead session.
    /// </summary>
    public NpgsqlConnection? Session => Volatile.Read(ref _session);

    /// <summary>
    /// Ask the lease session itself whether it STILL holds this key.
    ///
    /// <para>Any failure counts as "no". That is deliberate, not sloppy:
    /// the dominant reason a command on the lease connection throws is that
    /// the backend behind it is gone — which IS loss — and the opposite bias
    /// ("a blip probably means we still hold it") keeps destructive work
    /// running on a guarantee nobody can verify. Aborting is recoverable and
    /// reported; two concurrent holders of an exclusive critical section are
    /// not.</para>
    ///
    /// <para>An <see cref="OperationCanceledException"/> raised by
    /// <paramref name="ct"/> propagates instead — a cancelled probe is a
    /// probe that never ran, not evidence about the lock.</para>
    /// </summary>
    public async Task<bool> StillHeldAsync(CancellationToken ct = default)
    {
        var session = Volatile.Read(ref _session);
        if (session is null) return false;

        try
        {
            if (session.State != ConnectionState.Open) return false;

            await using var cmd = session.CreateCommand();
            cmd.CommandText = Key.HeldByThisBackendSql();
            Key.Bind(cmd);
            return (bool?)await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) == true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "pg.advisory_lock.recheck_failed key={Key} — treating as LOST", Key.Description);
            return false;
        }
    }

    /// <summary>
    /// Start a liveness heartbeat for a critical section long enough that
    /// the lock session can plausibly die inside it, and get back a
    /// cancellation token to run that work under.
    ///
    /// <para><b>The hazard this closes.</b> Everything else about this type
    /// guards the direction "the lock outlives the work". This guards the
    /// reverse: the work outlives the LOCK. A pooler recycle, an idle
    /// timeout, a network blip or an administrative
    /// <c>pg_terminate_backend</c> ends the lease session without touching
    /// the process — the gate silently opens, a second pod is admitted, and
    /// two supposedly-exclusive critical sections run at once. Nothing in
    /// the happy path notices, because the guarded work runs over completely
    /// different connections.</para>
    ///
    /// <para><b>Who needs it.</b> Sites whose critical section is short and
    /// bounded (elect a leader, dispatch a job, write a checkpoint) do not:
    /// the window is too small to be worth a background task, and the
    /// consequence of a rare double-run there is duplicated work, not
    /// corruption. Sites that hold the gate across minutes of external work
    /// — a <c>pg_dump</c>/<c>pg_restore</c>, a fleet-wide re-encrypt, a
    /// fleet-wide DDL sweep — do, because the window is the whole operation
    /// and the consequence of two of them is data loss.</para>
    ///
    /// <para><b>Ordering contract.</b> Dispose the watchdog BEFORE the lease.
    /// Both use the same single session, and a probe racing the release
    /// would either fault on a concurrently-executing command or report a
    /// spurious loss. <c>await using</c> in declaration order (lease first,
    /// watchdog second) gets this right; the reverse does not.</para>
    /// </summary>
    /// <param name="interval">How often to re-verify. Judge it against the
    /// shape of the guarded work: the probe is one indexed <c>pg_locks</c>
    /// read on an otherwise idle dedicated session, so the cost is
    /// negligible, and what the interval really buys is a bound on how long
    /// the work can keep running after losing exclusivity.</param>
    /// <param name="callerToken">The token the guarded work would otherwise
    /// have used. <see cref="PostgresAdvisoryLockWatchdog.Token"/> is linked
    /// to it, so cancelling the caller's token still cancels the work.</param>
    /// <param name="logger">Overrides the lease's logger for the loss report.</param>
    /// <param name="site">Log label identifying the critical section.</param>
    public PostgresAdvisoryLockWatchdog WatchLiveness(
        TimeSpan interval,
        CancellationToken callerToken = default,
        ILogger? logger = null,
        string? site = null)
    {
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "must be positive");
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _session) is null, this);

        return new PostgresAdvisoryLockWatchdog(
            this, interval, callerToken, logger ?? _logger, site);
    }

    /// <summary>
    /// Synchronous teardown, for <see cref="IDisposable"/> hosts (a
    /// stopping pod's <c>Dispose</c>) that cannot await.
    ///
    /// <para>It issues no <c>pg_advisory_unlock</c> — it just ends the
    /// session, which on a NON-POOLED connection is what actually releases
    /// the lock. Prefer <see cref="DisposeAsync"/> anywhere you can await:
    /// the explicit unlock makes the release prompt and greppable. This type
    /// deliberately does not implement <see cref="IDisposable"/>, so that a
    /// <c>using</c> written where <c>await using</c> was meant cannot
    /// silently pick the weaker path.</para>
    /// </summary>
    public void DisposeSession()
    {
        var session = Interlocked.Exchange(ref _session, null);
        session?.Dispose();
    }

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

/// <summary>
/// A running liveness heartbeat over a <see cref="PostgresAdvisoryLockLease"/>:
/// re-verifies from the lease session that the lock is still granted to
/// that backend, and cancels <see cref="Token"/> the moment it is not.
/// Created by <see cref="PostgresAdvisoryLockLease.WatchLiveness"/> — see
/// that method for why the hazard exists and which sites need it.
/// </summary>
public sealed class PostgresAdvisoryLockWatchdog : IAsyncDisposable
{
    private readonly PostgresAdvisoryLockLease _lease;
    private readonly CancellationTokenSource _work;
    private readonly CancellationTokenSource _loop;
    private readonly ILogger? _logger;
    private readonly string _site;
    private readonly Task _heartbeat;
    private int _lost;
    private int _disposed;

    internal PostgresAdvisoryLockWatchdog(
        PostgresAdvisoryLockLease lease,
        TimeSpan interval,
        CancellationToken callerToken,
        ILogger? logger,
        string? site)
    {
        _lease = lease;
        _logger = logger;
        _site = site ?? "unknown";
        _work = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        // Nested so DisposeAsync can stop the heartbeat WITHOUT cancelling
        // the work token — a caller that disposes the watchdog after its
        // work completed must not see a phantom cancellation.
        _loop = CancellationTokenSource.CreateLinkedTokenSource(_work.Token);
        _heartbeat = Task.Run(() => RunAsync(interval), CancellationToken.None);
    }

    /// <summary>
    /// Run the guarded work under this token instead of the caller's. It is
    /// cancelled when the caller's token is, and additionally the instant
    /// the lock is observed lost.
    ///
    /// <para>Valid only until <see cref="DisposeAsync"/> — which is the
    /// point at which the guarded work is over anyway. Use
    /// <see cref="LockLost"/> (safe forever) for post-mortem branching.</para>
    /// </summary>
    public CancellationToken Token => _work.Token;

    /// <summary>
    /// True once the heartbeat concluded the lock is gone. Callers use it to
    /// tell "the operator/host cancelled us" from "we lost exclusivity" —
    /// two very different stories to put in front of an operator.
    /// </summary>
    public bool LockLost => Volatile.Read(ref _lost) == 1;

    private async Task RunAsync(TimeSpan interval)
    {
        var token = _loop.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(interval, token).ConfigureAwait(false);
                if (token.IsCancellationRequested) return;
                if (await _lease.StillHeldAsync(token).ConfigureAwait(false)) continue;

                Interlocked.Exchange(ref _lost, 1);
                _logger?.LogError(
                    "pg.advisory_lock.lost site={Site} key={Key} — ABORTING the guarded work: the "
                    + "session holding pg_try_advisory_lock died (a pooler/proxy drop, an idle "
                    + "timeout, or a terminated backend) and this critical section can no longer "
                    + "guarantee it is the only one running", _site, _lease.Key.Description);
                await _work.CancelAsync().ConfigureAwait(false);
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // Normal: the work finished and DisposeAsync stopped us, or the
            // caller's token was cancelled.
        }
        catch (Exception ex)
        {
            // A watchdog that throws must not take the run down with it; a
            // dead heartbeat degrades to the pre-watchdog behaviour, loudly.
            _logger?.LogWarning(ex,
                "pg.advisory_lock.watchdog_failed site={Site} key={Key} — liveness is no longer "
                + "being verified for this critical section", _site, _lease.Key.Description);
        }
    }

    /// <summary>
    /// Stop the heartbeat and wait for it to exit. Must run BEFORE the lease
    /// is disposed — see <see cref="PostgresAdvisoryLockLease.WatchLiveness"/>.
    /// Does not cancel <see cref="Token"/>.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        try { await _loop.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { /* best effort */ }

        try { await _heartbeat.ConfigureAwait(false); }
        catch { /* RunAsync already swallows; belt and braces */ }

        _loop.Dispose();
        _work.Dispose();
    }
}
