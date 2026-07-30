using System.Collections.Concurrent;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Tamma.Data.Abstractions;

namespace Tamma.Data.Pooling;

/// <summary>
/// Production <see cref="ITenantMigrationSweepRunner"/> (Story 44-1 sweep
/// hygiene, 2026-07-30). See the interface for the three properties this type
/// adds over the raw <see cref="ITenantMigrationSweeper"/>; the implementation
/// notes below cover only the mechanics.
///
/// <para><b>⚠ DEPLOYMENT REQUIREMENT — the control-plane connection must not sit
/// behind a transaction-mode connection pooler.</b> The cluster-wide gate is a
/// Postgres SESSION-scoped <c>pg_try_advisory_lock</c>, whose entire meaning is
/// "this lock lives exactly as long as this backend session". PgBouncer in
/// <c>pool_mode = transaction</c> (and every proxy modelled on it) hands the
/// next transaction a DIFFERENT backend, so the lock is taken on one backend
/// and every later statement — including the release — runs on another: the
/// gate would be silently ineffective while appearing to work, and two
/// concurrent fleet-wide applies would both be admitted. <c>pool_mode =
/// session</c>, or a direct connection, is REQUIRED for
/// <c>ConnectionStrings:ControlPlane</c>. This is a hard requirement of the
/// primitive, not a tuning preference; if the control plane must move behind a
/// transaction pooler, this gate has to be replaced (a control-plane lease row
/// with a heartbeat) before it does.</para>
///
/// <para><b>The gate is two-layered on purpose.</b> A process-local slot
/// (<see cref="_localRun"/>) is taken first: it is what makes the 409 body
/// exact ("this instance, run X, started at T") and it is the only guard that
/// works on a non-Postgres provider (tests). The Postgres session-scoped
/// <c>pg_try_advisory_lock</c> is taken second and is the guard that actually
/// matters in production, where the two racing POSTs land on two different
/// pods. Session scope means a crashed pod's lock dies with its connection —
/// no stuck gate needing manual clearing, which is the property a fleet-DDL
/// escape hatch must have.</para>
///
/// <para><b>…and that is why the lease session is NON-POOLED</b> (2026-07-31,
/// the CI flake in <c>Dry_runs_are_not_gated_by_the_apply_single_flight</c>).
/// "The lock dies with the connection" is only true of a connection that is
/// actually CLOSED. The lease used to be an <see cref="ControlPlaneDbContext"/>
/// straight off <see cref="IDbContextFactory{TContext}"/>, i.e. an Npgsql
/// POOLED connection: disposing it returns the connector to the pool with the
/// backend session — and therefore the advisory lock — still alive, and
/// Npgsql defers its <c>DISCARD ALL</c> reset (which is what runs
/// <c>pg_advisory_unlock_all()</c>) until that connector is next USED. So any
/// path that dropped the context without an explicit <c>pg_advisory_unlock</c>
/// — <see cref="Dispose"/> on host shutdown, or <see cref="Dispose"/> winning
/// the <see cref="RunLease.TakeSession"/> race against
/// <see cref="ReleaseAsync"/> — parked the cluster-wide fleet-DDL gate SHUT on
/// an idle pooled connection, for as long as the pool kept that connector
/// (Npgsql's <c>Connection Idle Lifetime</c> is 300s by default and
/// <c>MinPoolSize</c> connectors are never pruned at all). That is exactly the
/// "stuck gate needing manual clearing" this design says it cannot have. The
/// lease now opens its own connection with <c>Pooling=false</c>, so closing it
/// really does end the backend session and really does drop the lock — on the
/// orderly path, on the Dispose path, on an unlock that throws, and on a
/// process crash alike. It costs one extra connect per apply run (there is at
/// most one at a time) and it stops the lease pinning a pooled EF context for
/// the entire duration of a fleet-wide sweep.</para>
///
/// <para><b>…and the lock is re-verified, because a connection can die without
/// the pod dying</b> (2026-07-30 review, Finding 1.1). "The lock dies with the
/// connection" is true but was only half the story: the sweep runs over
/// entirely DIFFERENT connections (each tenant's own pooled data source), so
/// lock liveness and sweep liveness were decoupled in the dangerous direction.
/// An idle-timeout drop, a proxy recycle, or a
/// <c>pg_terminate_backend</c> on the lease session removed the guard while the
/// fleet-wide DDL kept running — and a second runner was then admitted for a
/// concurrent apply. So <see cref="LockHeartbeatInterval"/> re-checks, from the
/// lease session itself, that the lease session still holds the lock, and the
/// run is ABORTED on loss. A fleet-wide apply that has lost its exclusivity
/// guarantee must not continue silently; the tenants already migrated are
/// reported as a partial result (see <see cref="TenantMigrationSweepRun.ResultIsPartial"/>).</para>
///
/// <para><b>Why not the platform task queue</b> (the shape
/// <c>POST /api/admin/tenants/{id}/move</c> uses)? <c>PlatformTaskWorker</c>
/// ships with <c>RunOnStartup=false</c>, so a queued sweep would sit
/// un-drained in the default deployment — the endpoint would silently do
/// nothing, which is strictly worse than the synchronous version it replaces.
/// The sweep therefore runs in-process on a background task and borrows only
/// the queue path's WIRE shape (202 + a status URL).</para>
/// </summary>
public sealed class TenantMigrationSweepRunner : ITenantMigrationSweepRunner, IDisposable
{
    /// <summary>
    /// The fleet-wide sweep's advisory-lock id. High word is ASCII
    /// <c>"MGSW"</c> — the <c>pg_locks</c>-greppable namespace prefix, the same
    /// convention as <c>HourlyAnalyticsRollupScheduler</c>'s <c>"RLUP"</c> and
    /// <c>ScheduleLockKey</c>'s <c>"SCHD"</c>. Low word is 1: unlike those two
    /// seams the sweep has no partition key (no tenant, no window) — there is
    /// exactly ONE sweep gate for the whole cluster, which is the point.
    /// </summary>
    public const long AdvisoryLockKey = 0x4D47535700000001L;

    /// <summary>
    /// How many BACKGROUND dry runs may be in flight on this instance at once
    /// (Finding 1.4). Dry runs are deliberately ungated by the apply
    /// single-flight — "what is still pending?" is the question an operator
    /// most wants answered mid-apply — but ungated is not the same as
    /// unbounded: each dry run opens a pooled connection per tenant,
    /// <c>maxConcurrency</c>-way parallel, so a repeated
    /// <c>POST .../migrate?async=true</c> amplified one curl into arbitrarily
    /// many concurrent fleet-wide connection walks (the reviewer got 200
    /// accepted on one instance). 4 is chosen to match
    /// <see cref="TenantMigrationSweep.DefaultMaxConcurrency"/>: at the default
    /// it bounds in-flight tenant connections at 4×4, and even at the
    /// <c>maxConcurrency</c> ceiling of 16 it stays a two-digit number. More
    /// than four simultaneous "what is pending?" questions is a stuck script,
    /// not an operator. Capping this is also what makes
    /// <see cref="MaxRetainedRuns"/> a real bound: at most one apply plus this
    /// many dry runs can be in the un-evictable <c>running</c> state.
    /// </summary>
    public const int MaxConcurrentDryRuns = 4;

    private readonly IDbContextFactory<ControlPlaneDbContext> _cpFactory;
    private readonly ITenantMigrationSweeper _sweeper;
    private readonly ILogger<TenantMigrationSweepRunner> _logger;

    private readonly object _gate = new();
    private readonly object _ringGate = new();
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>Bounded ring of recent runs, keyed by run id.</summary>
    private readonly ConcurrentDictionary<Guid, TenantMigrationSweepRun> _runs = new();
    private const int MaxRetainedRuns = 20;

    /// <summary>The apply run this process currently owns, or null.</summary>
    private RunLease? _localRun;

    /// <summary>Background dry runs in flight on this instance.</summary>
    private int _dryRunsInFlight;

    private int _disposed;

    /// <summary>
    /// How often a running APPLY sweep re-verifies that its lease session still
    /// holds the cluster lock. 15s is chosen against the shape of the work, not
    /// arbitrarily: the check is one indexed <c>pg_locks</c> read on an
    /// otherwise idle dedicated session (free), while the thing it bounds is
    /// how long a fleet-wide apply can keep issuing DDL after losing
    /// exclusivity. A single tenant's migration is typically seconds to
    /// minutes, so 15s keeps the unguarded window under roughly one tenant's
    /// worth of work — small enough that a concurrent second sweep cannot get
    /// far before this one aborts, and long enough that a whole-fleet sweep
    /// lasting an hour costs 240 trivial reads. Overridable for tests only.
    /// </summary>
    internal TimeSpan LockHeartbeatInterval { get; init; } = TimeSpan.FromSeconds(15);

    public TenantMigrationSweepRunner(
        IDbContextFactory<ControlPlaneDbContext> cpFactory,
        ITenantMigrationSweeper sweeper,
        ILogger<TenantMigrationSweepRunner>? logger = null)
    {
        _cpFactory = cpFactory;
        _sweeper = sweeper;
        _logger = logger ?? NullLogger<TenantMigrationSweepRunner>.Instance;
    }

    public async Task<TenantMigrationSweepStart> StartAsync(
        bool dryRun,
        int maxConcurrency = TenantMigrationSweep.DefaultMaxConcurrency,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

        var runId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        RunLease? lease = null;
        var tookDryRunSlot = false;

        if (dryRun)
        {
            // ── admission cap (NOT single-flight — see MaxConcurrentDryRuns) ──
            if (Interlocked.Increment(ref _dryRunsInFlight) > MaxConcurrentDryRuns)
            {
                Interlocked.Decrement(ref _dryRunsInFlight);
                _logger.LogWarning(
                    "tenant.migration_sweep.rejected reason=dry_run_capacity inFlight={InFlight} cap={Cap}",
                    Volatile.Read(ref _dryRunsInFlight), MaxConcurrentDryRuns);
                return new TenantMigrationSweepStart(
                    Accepted: false,
                    Run: null,
                    Conflict: new TenantMigrationSweepConflict(
                        TenantMigrationSweepConflict.ScopeDryRunCapacity,
                        RunId: null,
                        StartedAt: null));
            }

            tookDryRunSlot = true;
        }
        else
        {
            // ── layer 1: process-local slot ──
            lock (_gate)
            {
                if (_localRun is not null)
                {
                    var running = _localRun;
                    return new TenantMigrationSweepStart(
                        Accepted: false,
                        Run: null,
                        Conflict: new TenantMigrationSweepConflict(
                            TenantMigrationSweepConflict.ScopeThisInstance,
                            running.RunId,
                            running.StartedAt));
                }

                lease = new RunLease(runId, startedAt);
                _localRun = lease;
            }

            // ── layer 2: cluster-wide advisory lock ──
            try
            {
                var acquired = await TryAcquireClusterLockAsync(lease, ct).ConfigureAwait(false);
                if (!acquired)
                {
                    await ReleaseAsync(lease).ConfigureAwait(false);
                    _logger.LogWarning(
                        "tenant.migration_sweep.rejected reason=already_running scope=another-instance");
                    return new TenantMigrationSweepStart(
                        Accepted: false,
                        Run: null,
                        Conflict: new TenantMigrationSweepConflict(
                            TenantMigrationSweepConflict.ScopeAnotherInstance,
                            RunId: null,
                            StartedAt: null));
                }
            }
            catch
            {
                await ReleaseAsync(lease).ConfigureAwait(false);
                throw;
            }
        }

        var run = new TenantMigrationSweepRun(
            runId,
            TenantMigrationSweepRunState.Running,
            dryRun,
            maxConcurrency,
            startedAt,
            CompletedAt: null,
            Error: null,
            Result: null);
        Record(run);

        _logger.LogInformation(
            "tenant.migration_sweep.accepted runId={RunId} dryRun={DryRun} maxConcurrency={Max}",
            runId, dryRun, maxConcurrency);

        // Deliberately NOT the request's cancellation token: the HTTP request
        // completes the instant this method returns, and a run tied to it would
        // be canceled before it started. The run is tied to process shutdown.
        _ = Task.Run(() => ExecuteAsync(run, lease, tookDryRunSlot), CancellationToken.None);

        return new TenantMigrationSweepStart(Accepted: true, Run: run, Conflict: null);
    }

    public TenantMigrationSweepRun? TryGetRun(Guid runId) =>
        _runs.TryGetValue(runId, out var run) ? run : null;

    public async Task<bool> IsSweepRunningAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_localRun is not null) return true;
        }

        try
        {
            await using var cp = await _cpFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            if (!cp.Database.IsNpgsql()) return false;
            var conn = (NpgsqlConnection)cp.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open)
                await conn.OpenAsync(ct).ConfigureAwait(false);

            // READ-ONLY probe. Acquiring-then-releasing would work too, but it
            // would briefly hold the gate and could make a genuine concurrent
            // start spuriously 409 — a status poll must never be able to
            // perturb the thing it is reporting on.
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = AdvisoryLockHeldSql(byThisBackend: false);
            cmd.Parameters.AddWithValue("k", AdvisoryLockKey);
            return (bool?)await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) == true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best effort by contract — a failed probe must never turn a status
            // poll into a 500.
            _logger.LogDebug(ex, "tenant.migration_sweep.lock_probe_failed");
            return false;
        }
    }

    /// <summary>
    /// The <c>pg_locks</c> predicate for "the sweep's advisory lock is held".
    ///
    /// <para>Fully qualified (Finding 1.6). <c>pg_locks</c> is CLUSTER-wide and
    /// advisory locks are per-database, so an unqualified match reported a
    /// sweep running when a lock with the same key was held in a completely
    /// different database on the same cluster. And <c>objsubid</c> distinguishes
    /// the one-argument <c>pg_advisory_lock(bigint)</c> form (1) from the
    /// two-argument <c>(int, int)</c> form (2), whose halves reassemble to the
    /// same 64-bit value — a different lock entirely. This is the only
    /// cross-pod signal an operator gets; a false "a sweep is running" sends
    /// them to look for a run that does not exist.</para>
    ///
    /// <para><paramref name="byThisBackend"/> additionally pins the holder to
    /// the current session — that is the liveness re-verification (Finding 1.1),
    /// which must not be satisfied by SOMEONE ELSE holding the lock.</para>
    /// </summary>
    private static string AdvisoryLockHeldSql(bool byThisBackend) =>
        $"""
        SELECT EXISTS (
            SELECT 1 FROM pg_locks
            WHERE locktype = 'advisory'
              AND granted
              AND objsubid = 1
              AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
              {(byThisBackend ? "AND pid = pg_backend_pid()" : string.Empty)}
              AND ((classid::bigint << 32) + objid::bigint) = @k
        );
        """;

    private async Task ExecuteAsync(
        TenantMigrationSweepRun run, RunLease? lease, bool tookDryRunSlot)
    {
        // Per-tenant rows as they complete, so a sweep that dies partway can
        // still answer "which tenants got the DDL?" (Finding 1.3).
        var observed = new List<TenantMigrationSweepEntry>();
        void OnTenant(TenantMigrationSweepEntry e)
        {
            lock (observed) observed.Add(e);
        }

        TenantMigrationSweepEntry[] Snapshot()
        {
            lock (observed) return observed.ToArray();
        }

        // Linked so the watchdog can abort THIS run without touching the
        // process-wide shutdown source.
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        var lockLost = 0;
        var watchdog = lease is { HoldsClusterLock: true }
            ? WatchClusterLockAsync(run, lease, runCts, () => Interlocked.Exchange(ref lockLost, 1))
            : Task.CompletedTask;

        try
        {
            var result = await _sweeper
                .SweepAsync(run.DryRun, run.MaxConcurrency, OnTenant, runCts.Token)
                .ConfigureAwait(false);
            Record(run with
            {
                State = TenantMigrationSweepRunState.Completed,
                CompletedAt = DateTimeOffset.UtcNow,
                Result = result,
                ResultIsPartial = false,
            });
        }
        catch (Exception ex)
        {
            // A per-tenant failure is a result ROW (the sweeper isolates it);
            // reaching here means the sweep itself died (control-plane
            // unreachable, shutdown, or a LOST advisory lock). The run keeps
            // whatever per-tenant rows completed first — reporting nothing is
            // the worst possible post-failure state for a fleet-DDL primitive.
            var partial = Snapshot();
            var error = Volatile.Read(ref lockLost) == 1
                ? "The cluster-wide sweep lock was lost mid-run (the control-plane session "
                  + "holding pg_try_advisory_lock died — a pooler/proxy drop, an idle timeout, "
                  + "or a terminated backend). The sweep was ABORTED because it could no longer "
                  + "guarantee it was the only fleet-wide apply running. "
                  + $"{partial.Count(e => e.Outcome == TenantMigrationSweep.OutcomeMigrated)} "
                  + "tenant(s) were migrated before the abort; see the partial result. "
                  + "Original error: " + ex.Message
                : ex.Message;

            _logger.LogError(ex,
                "tenant.migration_sweep.run_failed runId={RunId} lockLost={LockLost} partialTenants={Partial}",
                run.RunId, Volatile.Read(ref lockLost) == 1, partial.Length);
            Record(run with
            {
                State = TenantMigrationSweepRunState.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                Error = error,
                Result = TenantMigrationSweep.Summarize(run.DryRun, partial),
                ResultIsPartial = true,
            });
        }
        finally
        {
            // Stop the watchdog before the lease connection goes away, so its
            // probe can never race the release into a spurious "lock lost".
            if (!runCts.IsCancellationRequested) runCts.Cancel();
            try { await watchdog.ConfigureAwait(false); } catch { /* best effort */ }

            if (lease is not null) await ReleaseAsync(lease).ConfigureAwait(false);
            if (tookDryRunSlot) Interlocked.Decrement(ref _dryRunsInFlight);
        }
    }

    /// <summary>
    /// Re-verify, on the LEASE session itself, that this run still holds the
    /// cluster lock; cancel the run on loss. See the class doc (Finding 1.1)
    /// for why "the lock dies with the connection" was not sufficient.
    ///
    /// <para>Any failure of the probe counts as loss. That is deliberate: the
    /// dominant reason a command on the lease connection throws is that the
    /// backend is gone — which IS loss — and the alternative bias (treat a
    /// blip as "probably still held") continues fleet-wide DDL on an
    /// unverifiable guarantee. Aborting a sweep is recoverable and reported;
    /// two concurrent applies are not.</para>
    /// </summary>
    private async Task WatchClusterLockAsync(
        TenantMigrationSweepRun run, RunLease lease, CancellationTokenSource runCts, Action onLost)
    {
        var token = runCts.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(LockHeartbeatInterval, token).ConfigureAwait(false);
                if (token.IsCancellationRequested) return;
                if (await StillHoldsClusterLockAsync(lease, token).ConfigureAwait(false)) continue;

                _logger.LogError(
                    "tenant.migration_sweep.lock_lost runId={RunId} — aborting: the sweep can no "
                    + "longer guarantee it is the only fleet-wide apply running", run.RunId);
                onLost();
                lease.HoldsClusterLock = false;
                await runCts.CancelAsync().ConfigureAwait(false);
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // Normal: the run finished and the finally block cancelled us.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "tenant.migration_sweep.lock_watchdog_failed runId={RunId}", run.RunId);
        }
    }

    private async Task<bool> StillHoldsClusterLockAsync(RunLease lease, CancellationToken ct)
    {
        // Only ever non-null on the Postgres path — the non-Postgres branch of
        // TryAcquireClusterLockAsync leaves HoldsClusterLock false, and the
        // watchdog only starts for a lease that holds the cluster lock.
        var conn = lease.Session;
        if (conn is null) return false;

        try
        {
            if (conn.State != ConnectionState.Open) return false;

            await using var cmd = conn.CreateCommand();
            // pid = pg_backend_pid() — the lock must still be held by THIS
            // session. A reconnect (new pid) or another pod having taken the
            // key both read as loss, which is the honest answer either way.
            cmd.CommandText = AdvisoryLockHeldSql(byThisBackend: true);
            cmd.Parameters.AddWithValue("k", AdvisoryLockKey);
            return (bool?)await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) == true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "tenant.migration_sweep.lock_recheck_failed");
            return false;
        }
    }

    /// <summary>
    /// Open a dedicated, NON-POOLED control-plane session and take the sweep's
    /// advisory lock on it. The session (and therefore the lock) lives for the
    /// run's duration on <see cref="RunLease.Session"/>.
    ///
    /// <para>The control-plane <see cref="DbContext"/> is used only to learn
    /// the provider and the connection string, and is disposed immediately.
    /// The lock must NOT ride a pooled connection — see the class doc: a
    /// pooled connector handed back to the pool keeps the session, and
    /// therefore the lock, alive.</para>
    /// </summary>
    private async Task<bool> TryAcquireClusterLockAsync(RunLease lease, CancellationToken ct)
    {
        string connectionString;
        await using (var cp = await _cpFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            if (!cp.Database.IsNpgsql())
            {
                // Non-Postgres (in-memory/sqlite test hosts): the process-local
                // slot is the whole guard. Single-pod by construction there.
                lease.HoldsClusterLock = false;
                return true;
            }

            connectionString = cp.Database.GetConnectionString()
                ?? throw new InvalidOperationException(
                    "The control-plane context exposes no connection string, so the sweep's "
                    + "cluster-wide advisory lock cannot be taken on a dedicated session.");
        }

        // Pooling=false is load-bearing, not a tuning choice: it is what makes
        // "closing the connection releases the lock" true. See the class doc.
        var dedicated = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };
        var session = new NpgsqlConnection(dedicated.ConnectionString);
        // Published BEFORE the open, so a failed open still hands the
        // connection to ReleaseAsync/Dispose to be torn down.
        lease.Session = session;
        await session.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = session.CreateCommand();
        cmd.CommandText = "SELECT pg_try_advisory_lock(@k);";
        cmd.Parameters.AddWithValue("k", AdvisoryLockKey);
        var acquired = (bool?)await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) == true;
        lease.HoldsClusterLock = acquired;
        return acquired;
    }

    private async Task ReleaseAsync(RunLease lease)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_localRun, lease)) _localRun = null;
        }

        // Interlocked, not read-then-null: Dispose races this method for the
        // same session and exactly one of them may dispose it.
        var session = lease.TakeSession();
        if (session is null) return;

        try
        {
            if (lease.HoldsClusterLock && session.State == ConnectionState.Open)
            {
                await using var unlock = session.CreateCommand();
                unlock.CommandText = "SELECT pg_advisory_unlock(@k);";
                unlock.Parameters.AddWithValue("k", AdvisoryLockKey);
                await unlock.ExecuteScalarAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // Session-scoped on a NON-POOLED connection: the dispose below
            // really does end the backend session, which releases the lock
            // anyway. That is the reason the lock is session- and not
            // transaction-scoped — there is no failure mode that leaves the
            // gate stuck shut. (It is only true because the connection is not
            // pooled; see the class doc.)
            _logger.LogDebug(ex, "tenant.migration_sweep.unlock_failed");
        }
        finally
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Record a run and keep the ring bounded.
    ///
    /// <para>Eviction skips <c>running</c> runs — a running sweep must stay
    /// pollable no matter how many finished runs pile up behind it — which is
    /// only a real bound because the number of simultaneously-running runs is
    /// itself capped: at most one apply (single-flight) plus
    /// <see cref="MaxConcurrentDryRuns"/> dry runs. Before that cap existed
    /// (Finding 1.4), unbounded concurrent dry runs grew the ring without
    /// limit. Eviction loops under a lock rather than computing a single
    /// batch size, so concurrent recorders cannot leave the ring over its
    /// bound.</para>
    /// </summary>
    private void Record(TenantMigrationSweepRun run)
    {
        _runs[run.RunId] = run;
        if (_runs.Count <= MaxRetainedRuns) return;

        lock (_ringGate)
        {
            while (_runs.Count > MaxRetainedRuns)
            {
                var stale = _runs.Values
                    .Where(r => r.State != TenantMigrationSweepRunState.Running)
                    .OrderBy(r => r.StartedAt)
                    .FirstOrDefault();
                if (stale is null) break;              // all remaining are running (≤ 1 + cap)
                if (!_runs.TryRemove(stale.RunId, out _)) break;
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        // Cancel but do NOT dispose _shutdown (Finding 1.5): ExecuteAsync may
        // be inside SweepAsync on a token linked to this source, and disposing
        // it underneath turns a clean cancellation into an
        // ObjectDisposedException that surfaces as the run's Error — a
        // confusing, wrong story about why a fleet migration stopped. A CTS
        // with no timer holds nothing that needs deterministic release; the
        // linked per-run sources are disposed by their own runs.
        try { _shutdown.Cancel(); } catch (ObjectDisposedException) { /* best effort */ }

        RunLease? lease;
        lock (_gate) { lease = _localRun; _localRun = null; }
        // TakeSession is interlocked: if ReleaseAsync is concurrently ending
        // the run, exactly one of us gets the session and disposes it once.
        if (lease?.TakeSession() is { } session)
        {
            // The session is NON-POOLED, so disposing it genuinely closes the
            // backend — which is what releases the advisory lock. This is the
            // crashed/stopped-pod path, exercised here on the orderly-shutdown
            // path too. On a pooled connection this line would hand the
            // connector back to the pool with the lock still held and wedge
            // the cluster-wide gate shut; see the class doc.
            session.Dispose();
        }
    }

    /// <summary>The mutable half of a run: what has to be released when it ends.</summary>
    private sealed class RunLease(Guid runId, DateTimeOffset startedAt)
    {
        private NpgsqlConnection? _session;

        public Guid RunId { get; } = runId;
        public DateTimeOffset StartedAt { get; } = startedAt;

        /// <summary>
        /// The dedicated, NON-POOLED Postgres session that holds the advisory
        /// lock open. Null on a non-Postgres provider (nothing to hold).
        /// </summary>
        public NpgsqlConnection? Session
        {
            get => Volatile.Read(ref _session);
            set => Volatile.Write(ref _session, value);
        }

        /// <summary>
        /// Atomically claim the session for disposal. Dispose and ReleaseAsync
        /// both end a run and both used to read-then-null, so both could
        /// dispose the same connection (Finding 1.5, second half).
        /// </summary>
        public NpgsqlConnection? TakeSession() => Interlocked.Exchange(ref _session, null);

        public bool HoldsClusterLock { get; set; }
    }
}
