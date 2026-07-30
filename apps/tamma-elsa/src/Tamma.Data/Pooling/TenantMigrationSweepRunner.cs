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

    private readonly IDbContextFactory<ControlPlaneDbContext> _cpFactory;
    private readonly ITenantMigrationSweeper _sweeper;
    private readonly ILogger<TenantMigrationSweepRunner> _logger;

    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>Bounded ring of recent runs, keyed by run id.</summary>
    private readonly ConcurrentDictionary<Guid, TenantMigrationSweepRun> _runs = new();
    private const int MaxRetainedRuns = 20;

    /// <summary>The apply run this process currently owns, or null.</summary>
    private RunLease? _localRun;

    private int _disposed;

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

        if (!dryRun)
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
        _ = Task.Run(() => ExecuteAsync(run, lease), CancellationToken.None);

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
            cmd.CommandText =
                """
                SELECT EXISTS (
                    SELECT 1 FROM pg_locks
                    WHERE locktype = 'advisory'
                      AND granted
                      AND ((classid::bigint << 32) + objid::bigint) = @k
                );
                """;
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

    private async Task ExecuteAsync(TenantMigrationSweepRun run, RunLease? lease)
    {
        try
        {
            var result = await _sweeper
                .SweepAsync(run.DryRun, run.MaxConcurrency, _shutdown.Token)
                .ConfigureAwait(false);
            Record(run with
            {
                State = TenantMigrationSweepRunState.Completed,
                CompletedAt = DateTimeOffset.UtcNow,
                Result = result,
            });
        }
        catch (Exception ex)
        {
            // A per-tenant failure is a result ROW (the sweeper isolates it);
            // reaching here means the sweep itself died (control-plane
            // unreachable, shutdown) and the run has no result at all.
            _logger.LogError(ex, "tenant.migration_sweep.run_failed runId={RunId}", run.RunId);
            Record(run with
            {
                State = TenantMigrationSweepRunState.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                Error = ex.Message,
            });
        }
        finally
        {
            if (lease is not null) await ReleaseAsync(lease).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Open a dedicated control-plane session and take the sweep's advisory
    /// lock on it. The session (and therefore the lock) lives for the run's
    /// duration on <see cref="RunLease.Context"/>.
    /// </summary>
    private async Task<bool> TryAcquireClusterLockAsync(RunLease lease, CancellationToken ct)
    {
        var cp = await _cpFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        lease.Context = cp;

        if (!cp.Database.IsNpgsql())
        {
            // Non-Postgres (in-memory/sqlite test hosts): the process-local
            // slot is the whole guard. Single-pod by construction there.
            lease.HoldsClusterLock = false;
            return true;
        }

        var conn = (NpgsqlConnection)cp.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = conn.CreateCommand();
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

        var cp = lease.Context;
        lease.Context = null;
        if (cp is null) return;

        try
        {
            if (lease.HoldsClusterLock && cp.Database.IsNpgsql())
            {
                var conn = (NpgsqlConnection)cp.Database.GetDbConnection();
                if (conn.State == ConnectionState.Open)
                {
                    await using var unlock = conn.CreateCommand();
                    unlock.CommandText = "SELECT pg_advisory_unlock(@k);";
                    unlock.Parameters.AddWithValue("k", AdvisoryLockKey);
                    await unlock.ExecuteScalarAsync().ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            // Session-scoped: closing the connection releases it anyway. This
            // is the reason the lock is session- and not transaction-scoped —
            // there is no failure mode that leaves the gate stuck shut.
            _logger.LogDebug(ex, "tenant.migration_sweep.unlock_failed");
        }
        finally
        {
            await cp.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void Record(TenantMigrationSweepRun run)
    {
        _runs[run.RunId] = run;
        if (_runs.Count <= MaxRetainedRuns) return;

        // Evict oldest COMPLETED runs only — a running sweep must stay pollable
        // no matter how many finished runs pile up behind it.
        foreach (var stale in _runs.Values
            .Where(r => r.State != TenantMigrationSweepRunState.Running)
            .OrderBy(r => r.StartedAt)
            .Take(Math.Max(0, _runs.Count - MaxRetainedRuns)))
        {
            _runs.TryRemove(stale.RunId, out _);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        try { _shutdown.Cancel(); } catch { /* best effort */ }
        _shutdown.Dispose();

        RunLease? lease;
        lock (_gate) { lease = _localRun; _localRun = null; }
        if (lease?.Context is { } cp)
        {
            lease.Context = null;
            // Disposing the context closes the session, which releases the
            // advisory lock — the crashed/stopped-pod path, exercised here on
            // the orderly-shutdown path too.
            cp.Dispose();
        }
    }

    /// <summary>The mutable half of a run: what has to be released when it ends.</summary>
    private sealed class RunLease(Guid runId, DateTimeOffset startedAt)
    {
        public Guid RunId { get; } = runId;
        public DateTimeOffset StartedAt { get; } = startedAt;

        /// <summary>Holds the advisory lock's Postgres session open.</summary>
        public ControlPlaneDbContext? Context { get; set; }

        public bool HoldsClusterLock { get; set; }
    }
}
