using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NUnit.Framework;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Pooling;

namespace Tamma.Api.Tests.Tracker;

/// <summary>
/// Story 44-1 sweep hygiene (2026-07-30) — <see cref="TenantMigrationSweepRunner"/>:
/// the single-flight gate and the background/pollable run lifecycle.
///
/// <para>The defect being pinned: two concurrent
/// <c>POST /api/admin/tenants/migrate?apply=true</c> both swept, double-migrating
/// every tenant (EF's per-migration transaction makes the loser mostly record
/// failures — noise and wasted load on every tenant in the fleet at best). The
/// guard has to be CLUSTER-wide, because on a multi-pod deploy the two racing
/// POSTs are exactly as likely to land on two different pods, where a
/// process-local lock is decoration. So the interesting test here is
/// <see cref="Second_apply_sweep_from_another_instance_is_refused_by_the_cluster_lock"/>:
/// two runner instances = two pods, one Postgres.</para>
///
/// <para>REQUIRES DOCKER — the advisory lock is the thing under test, so these
/// run against the shared <see cref="ApiTestFixture.Postgres"/> container and
/// not against a stub. The control plane's SCHEMA is irrelevant here (the
/// sweeper itself is a double); only the session is.</para>
/// </summary>
[TestFixture]
[Category("Docker")]
public class TenantMigrationSweepRunnerTests
{
    private string _cs = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp() => _cs = ApiTestFixture.Postgres.GetConnectionString();

    private TenantMigrationSweepRunner NewRunner(ITenantMigrationSweeper sweeper) =>
        new(new PlainCpFactory(_cs), sweeper);

    /// <summary>
    /// A runner whose lock heartbeat fires fast enough to observe in a test.
    /// Production is 15s (see <see cref="TenantMigrationSweepRunner.LockHeartbeatInterval"/>);
    /// the INTERVAL is a tuning constant, the abort-on-loss behaviour is not.
    /// </summary>
    private TenantMigrationSweepRunner NewFastHeartbeatRunner(ITenantMigrationSweeper sweeper) =>
        new(new PlainCpFactory(_cs), sweeper)
        {
            LockHeartbeatInterval = TimeSpan.FromMilliseconds(150),
        };

    // ───────────────────────── single-flight ─────────────────────────

    [Test]
    public async Task Second_apply_sweep_on_the_same_instance_is_refused_with_the_running_runs_identity()
    {
        var sweeper = new GatedSweeper();
        using var runner = NewRunner(sweeper);

        var first = await runner.StartAsync(dryRun: false);
        first.Accepted.Should().BeTrue();
        await sweeper.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var second = await runner.StartAsync(dryRun: false);

        second.Accepted.Should().BeFalse("only one sweep may migrate the fleet at a time");
        second.Run.Should().BeNull();
        second.Conflict!.Scope.Should().Be(TenantMigrationSweepConflict.ScopeThisInstance);
        second.Conflict.RunId.Should().Be(first.Run!.RunId,
            "the refusal must name the sweep that holds the gate, not just say 'busy'");
        second.Conflict.StartedAt.Should().Be(first.Run.StartedAt);

        sweeper.Release.TrySetResult();
        await WaitForCompletionAsync(runner, first.Run.RunId);
    }

    [Test]
    public async Task Second_apply_sweep_from_another_instance_is_refused_by_the_cluster_lock()
    {
        // Two runner instances over one Postgres = two pods behind one load
        // balancer. The process-local slot of instance B is FREE, so the only
        // thing that can refuse this start is the advisory lock — which is
        // precisely the multi-pod case a per-process guard would miss.
        var sweeperA = new GatedSweeper();
        var sweeperB = new GatedSweeper();
        using var podA = NewRunner(sweeperA);
        using var podB = NewRunner(sweeperB);

        var first = await podA.StartAsync(dryRun: false);
        first.Accepted.Should().BeTrue();
        await sweeperA.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var second = await podB.StartAsync(dryRun: false);

        second.Accepted.Should().BeFalse();
        second.Conflict!.Scope.Should().Be(TenantMigrationSweepConflict.ScopeAnotherInstance);
        second.Conflict.RunId.Should().BeNull(
            "pod B genuinely cannot know pod A's run id — reporting one would be fiction");
        sweeperB.Started.Task.IsCompleted.Should().BeFalse(
            "the refused start must never have run the sweep");

        sweeperA.Release.TrySetResult();
        await WaitForCompletionAsync(podA, first.Run!.RunId);
    }

    [Test]
    public async Task The_gate_reopens_after_the_run_completes()
    {
        var sweeper = new GatedSweeper();
        using var runner = NewRunner(sweeper);

        var first = await runner.StartAsync(dryRun: false);
        await sweeper.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        sweeper.Release.TrySetResult();
        await WaitForCompletionAsync(runner, first.Run!.RunId);

        var second = await runner.StartAsync(dryRun: false);

        second.Accepted.Should().BeTrue(
            "a completed sweep must release both halves of the gate — a fleet-DDL escape "
            + "hatch that can wedge itself shut is worse than none");
        sweeper.Release.TrySetResult();
        await WaitForCompletionAsync(runner, second.Run!.RunId);
    }

    [Test]
    public async Task A_sweep_that_throws_fails_the_run_and_still_releases_the_gate()
    {
        var sweeper = new GatedSweeper { Throw = new InvalidOperationException("control plane down") };
        using var runner = NewRunner(sweeper);

        var first = await runner.StartAsync(dryRun: false);
        await sweeper.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        sweeper.Release.TrySetResult();

        var run = await WaitForTerminalAsync(runner, first.Run!.RunId);
        run.State.Should().Be(TenantMigrationSweepRunState.Failed);
        run.Error.Should().Contain("control plane down");
        run.ResultIsPartial.Should().BeTrue();
        run.Result!.Total.Should().Be(0,
            "this sweep died without completing a single tenant — the empty partial result "
            + "is the honest answer to 'which tenants got the DDL?', where null was not");

        var second = await runner.StartAsync(dryRun: false);
        second.Accepted.Should().BeTrue("a failed sweep must not leave the gate held");
        sweeper.Release.TrySetResult();
        await WaitForTerminalAsync(runner, second.Run!.RunId);
    }

    [Test]
    public async Task Dry_runs_are_not_gated_by_the_apply_single_flight()
    {
        // A dry run writes nothing, so it cannot double-migrate anything —
        // and "what is still pending?" is the question an operator most wants
        // answered WHILE a long apply is running.
        var applySweeper = new GatedSweeper();
        var drySweeper = new GatedSweeper();
        using var applyRunner = NewRunner(applySweeper);
        using var dryRunner = NewRunner(drySweeper);

        var apply = await applyRunner.StartAsync(dryRun: false);
        await applySweeper.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var dry = await dryRunner.StartAsync(dryRun: true);

        dry.Accepted.Should().BeTrue();
        dry.Run!.DryRun.Should().BeTrue();
        await drySweeper.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        drySweeper.Release.TrySetResult();
        applySweeper.Release.TrySetResult();
        await WaitForCompletionAsync(applyRunner, apply.Run!.RunId);
        await WaitForCompletionAsync(dryRunner, dry.Run.RunId);
    }

    // ───────────────────── background run + polling ─────────────────────

    [Test]
    public async Task Start_returns_before_the_sweep_finishes_and_the_result_arrives_by_polling()
    {
        // The item-3 defect: the sweep used to run inside the HTTP request, so
        // a fleet that outlived the proxy timeout gave the caller a 504 and no
        // result at all while the DDL kept going.
        var sweeper = new GatedSweeper();
        using var runner = NewRunner(sweeper);

        var start = await runner.StartAsync(dryRun: false);
        start.Accepted.Should().BeTrue();

        var running = runner.TryGetRun(start.Run!.RunId)!;
        running.State.Should().Be(TenantMigrationSweepRunState.Running,
            "StartAsync returns a handle while the sweep is still going");
        running.Result.Should().BeNull();

        sweeper.Release.TrySetResult();
        var done = await WaitForCompletionAsync(runner, start.Run.RunId);

        done.Result.Should().NotBeNull();
        done.Result!.Migrated.Should().Be(3);
        done.CompletedAt.Should().NotBeNull();
    }

    [Test]
    public async Task An_unknown_run_id_is_simply_absent()
    {
        using var runner = NewRunner(new GatedSweeper());
        runner.TryGetRun(Guid.NewGuid()).Should().BeNull();
    }

    [Test]
    public async Task IsSweepRunning_sees_a_sweep_held_by_another_instance_and_clears_afterwards()
    {
        var sweeper = new GatedSweeper();
        using var podA = NewRunner(sweeper);
        using var podB = NewRunner(new GatedSweeper());

        (await podB.IsSweepRunningAsync()).Should().BeFalse("nothing is running yet");

        var start = await podA.StartAsync(dryRun: false);
        await sweeper.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        (await podB.IsSweepRunningAsync()).Should().BeTrue(
            "the pg_locks probe is how a pod that did not accept the POST can still tell "
            + "the operator their run is alive somewhere");

        sweeper.Release.TrySetResult();
        await WaitForCompletionAsync(podA, start.Run!.RunId);

        (await podB.IsSweepRunningAsync()).Should().BeFalse(
            "the session-scoped lock is released when the run ends");
    }

    // ───────────── lock liveness (2026-07-30 review, Finding 1.1) ─────────────

    [Test]
    public async Task A_run_whose_lock_holding_backend_is_killed_aborts_instead_of_sweeping_on_unguarded()
    {
        // The reviewer's probe, verbatim in shape: terminate the single backend
        // that holds the advisory lock — a pooler recycle, an idle timeout, a
        // DBA's pg_terminate_backend — WITHOUT touching the process. Before the
        // fix, nothing monitored that session, the sweep ran over entirely
        // different connections, and a second runner was then accepted for a
        // concurrent fleet-wide apply while the first was still running. The
        // guard was gone; the danger was not.
        var sweeper = new GatedSweeper();
        using var runner = NewFastHeartbeatRunner(sweeper);

        var start = await runner.StartAsync(dryRun: false);
        start.Accepted.Should().BeTrue();
        await sweeper.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var killed = await TerminateAdvisoryLockHolderAsync();
        killed.Should().BeTrue("the test must actually kill the lock-holding backend");

        // NOTE: Release is never signalled. If the runner did not abort, the run
        // would sit in `running` until the 30s in-sweeper timeout — so reaching
        // a terminal state promptly IS the abort.
        var run = await WaitForTerminalAsync(runner, start.Run!.RunId);

        run.State.Should().Be(TenantMigrationSweepRunState.Failed,
            "a fleet-wide apply that has lost its exclusivity guarantee must not continue");
        run.Error.Should().Contain("lock was lost",
            "the operator has to be told WHY it stopped — 'canceled' would send them "
            + "looking for a shutdown that did not happen");
        run.Result.Should().NotBeNull();
        run.ResultIsPartial.Should().BeTrue(
            "an aborted apply reports the tenants it already migrated, never silence");
    }

    [Test]
    public async Task A_completed_run_is_never_falsely_aborted_by_the_heartbeat()
    {
        // The other side of Finding 1.1: a re-verification that mistakes normal
        // completion (or the release itself) for lock loss would turn every
        // successful sweep into a scary failure.
        var sweeper = new GatedSweeper();
        using var runner = NewFastHeartbeatRunner(sweeper);

        var start = await runner.StartAsync(dryRun: false);
        await sweeper.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(600); // several heartbeats while genuinely holding the lock
        sweeper.Release.TrySetResult();

        var run = await WaitForCompletionAsync(runner, start.Run!.RunId);
        run.Error.Should().BeNull();
        run.ResultIsPartial.Should().BeFalse();
    }

    // ─────────── partial results (Finding 1.3) ───────────

    [Test]
    public async Task A_failed_run_reports_the_tenants_it_already_migrated()
    {
        // "We don't know which tenants got the DDL" is the worst possible
        // post-failure state for a fleet-DDL primitive. Before the fix the
        // failure path recorded Result: null and the wire said applied=false.
        var already = new[]
        {
            new TenantMigrationSweepEntry(Guid.NewGuid(), TenantMigrationSweep.OutcomeMigrated, 2, null),
            new TenantMigrationSweepEntry(Guid.NewGuid(), TenantMigrationSweep.OutcomeMigrated, 1, null),
            new TenantMigrationSweepEntry(Guid.NewGuid(), TenantMigrationSweep.OutcomeFailed, 0, "boom"),
        };
        var sweeper = new GatedSweeper
        {
            EmitBeforeRelease = already,
            Throw = new InvalidOperationException("control plane vanished mid-sweep"),
        };
        using var runner = NewRunner(sweeper);

        var start = await runner.StartAsync(dryRun: false);
        await sweeper.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        sweeper.Release.TrySetResult();

        var run = await WaitForTerminalAsync(runner, start.Run!.RunId);
        run.State.Should().Be(TenantMigrationSweepRunState.Failed);
        run.ResultIsPartial.Should().BeTrue();
        run.Result!.Total.Should().Be(3);
        run.Result.Migrated.Should().Be(2, "two tenants really do carry the new schema now");
        run.Result.Failed.Should().Be(1);
        run.Result.Tenants.Select(t => t.TenantId).Should().BeEquivalentTo(
            already.Select(t => t.TenantId),
            "the operator must be able to name the tenants, not just count them");
    }

    // ─────────── dry-run admission cap (Finding 1.4) ───────────

    [Test]
    public async Task Background_dry_runs_are_capped_and_the_cap_frees_up_again()
    {
        // A background dry run takes neither the process slot nor the cluster
        // lock, so before the cap NOTHING bounded it: the reviewer got 200/200
        // accepted on one instance, each opening a pooled connection per tenant
        // N-way parallel off one repeated curl.
        var sweeper = new CountingSweeper();
        using var instance = NewRunner(sweeper);
        var accepted = new List<TenantMigrationSweepRun>();

        for (var i = 0; i < TenantMigrationSweepRunner.MaxConcurrentDryRuns; i++)
        {
            var start = await instance.StartAsync(dryRun: true);
            start.Accepted.Should().BeTrue($"dry run {i} is within the cap");
            accepted.Add(start.Run!);
        }

        await sweeper.WaitForStartsAsync(TenantMigrationSweepRunner.MaxConcurrentDryRuns);

        var overflow = await instance.StartAsync(dryRun: true);
        overflow.Accepted.Should().BeFalse(
            "ungated is not the same as unbounded — the cap is the only thing between "
            + "one repeated request and unbounded concurrent fleet-wide connection walks");
        overflow.Conflict!.Scope.Should().Be(
            TenantMigrationSweepConflict.ScopeDryRunCapacity,
            "a capacity refusal must not masquerade as the apply single-flight — it is "
            + "retryable the moment a slot frees, which is a different thing to tell an operator");

        sweeper.Release.TrySetResult();
        foreach (var run in accepted) await WaitForCompletionAsync(instance, run.RunId);

        var afterDrain = await instance.StartAsync(dryRun: true);
        afterDrain.Accepted.Should().BeTrue("the cap must free up, not latch");
        await WaitForCompletionAsync(instance, afterDrain.Run!.RunId);
    }

    [Test]
    public async Task The_run_registry_stays_bounded_under_repeated_dry_runs()
    {
        // Eviction skips `running` runs, so the ring is only bounded because the
        // number of simultaneously-running runs is itself bounded (one apply +
        // the dry-run cap). Drive well past MaxRetainedRuns and prove it holds.
        var sweeper = new CountingSweeper();
        sweeper.Release.TrySetResult(); // never block
        using var runner = NewRunner(sweeper);

        var ids = new List<Guid>();
        for (var i = 0; i < 60; i++)
        {
            var start = await runner.StartAsync(dryRun: true);
            start.Accepted.Should().BeTrue("each run completes immediately, so a slot is always free");
            ids.Add(start.Run!.RunId);
            await WaitForCompletionAsync(runner, start.Run.RunId);
        }

        var retained = ids.Count(id => runner.TryGetRun(id) is not null);
        retained.Should().BeLessThanOrEqualTo(20,
            "the ring is bounded at MaxRetainedRuns; an unbounded registry is a slow leak "
            + "in a long-lived singleton");
        runner.TryGetRun(ids[^1]).Should().NotBeNull("the newest run must survive eviction");
        runner.TryGetRun(ids[0]).Should().BeNull("the oldest must not");
    }

    // ─────────── shutdown (Finding 1.5) ───────────

    [Test]
    public async Task Disposing_mid_run_cancels_the_run_instead_of_reporting_ObjectDisposed()
    {
        // Dispose cancelled AND disposed the shutdown source while ExecuteAsync
        // was inside SweepAsync(_shutdown.Token), so a shutdown surfaced as an
        // ObjectDisposedException in the run's Error — a wrong story about why a
        // fleet migration stopped. (Also the double-dispose race on the lease
        // context between Dispose and ReleaseAsync: if it were still there, this
        // test's teardown would surface it.)
        var sweeper = new GatedSweeper();
        var runner = NewRunner(sweeper);

        var start = await runner.StartAsync(dryRun: false);
        await sweeper.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        runner.Dispose();

        var run = await WaitForTerminalAsync(runner, start.Run!.RunId);
        run.State.Should().Be(TenantMigrationSweepRunState.Failed);
        run.Error.Should().NotContain("ObjectDisposed",
            "process shutdown is a cancellation, not a bug in the runner");
        run.Error.Should().NotContain("disposed");

        runner.Dispose(); // idempotent
    }

    // ─────────── lock probe qualification (Finding 1.6) ───────────

    [Test]
    public async Task IsSweepRunning_ignores_a_two_argument_advisory_lock_with_the_same_halves()
    {
        // pg_advisory_lock(int, int) and pg_advisory_lock(bigint) are DIFFERENT
        // locks that reassemble to the same 64-bit number. The unqualified
        // pg_locks probe conflated them, so an unrelated seam using the
        // two-argument form told the operator "a sweep is running" — and this
        // probe is the ONLY cross-pod signal they get.
        using var runner = NewRunner(new GatedSweeper());
        var high = (int)(TenantMigrationSweepRunner.AdvisoryLockKey >> 32);
        var low = (int)(TenantMigrationSweepRunner.AdvisoryLockKey & 0xFFFFFFFF);

        await using var holder = new NpgsqlConnection(_cs);
        await holder.OpenAsync();
        await using (var take = holder.CreateCommand())
        {
            take.CommandText = "SELECT pg_advisory_lock(@h, @l);";
            take.Parameters.AddWithValue("h", high);
            take.Parameters.AddWithValue("l", low);
            await take.ExecuteScalarAsync();
        }

        try
        {
            (await runner.IsSweepRunningAsync()).Should().BeFalse(
                "objsubid distinguishes the one-argument form (1) from the two-argument "
                + "form (2) — a same-key lock of the other flavour is not this sweep");
        }
        finally
        {
            await using var release = holder.CreateCommand();
            release.CommandText = "SELECT pg_advisory_unlock(@h, @l);";
            release.Parameters.AddWithValue("h", high);
            release.Parameters.AddWithValue("l", low);
            await release.ExecuteScalarAsync();
        }
    }

    [Test]
    public async Task IsSweepRunning_ignores_the_same_key_held_in_another_database_on_the_cluster()
    {
        // pg_locks is CLUSTER-wide while advisory locks are per-database. An
        // unqualified match reported "a sweep is running" because some other
        // database on the same Postgres held a lock with this key — and the
        // operator's only cross-pod signal then points at a run that does not
        // exist here.
        const string otherDb = "sweep_lock_probe_other";
        var builder = new NpgsqlConnectionStringBuilder(_cs);
        var maintenanceCs = builder.ConnectionString;
        builder.Database = otherDb;
        var otherCs = builder.ConnectionString;

        await ExecuteMaintenanceAsync(maintenanceCs, $"DROP DATABASE IF EXISTS \"{otherDb}\";");
        await ExecuteMaintenanceAsync(maintenanceCs, $"CREATE DATABASE \"{otherDb}\";");
        try
        {
            using var runner = NewRunner(new GatedSweeper());
            await using var holder = new NpgsqlConnection(otherCs);
            await holder.OpenAsync();
            await using (var take = holder.CreateCommand())
            {
                take.CommandText = "SELECT pg_try_advisory_lock(@k);";
                take.Parameters.AddWithValue("k", TenantMigrationSweepRunner.AdvisoryLockKey);
                ((bool?)await take.ExecuteScalarAsync()).Should().BeTrue();
            }

            (await runner.IsSweepRunningAsync()).Should().BeFalse(
                "the probe is qualified by database oid — another database's lock is "
                + "another database's business");
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteMaintenanceAsync(maintenanceCs, $"DROP DATABASE IF EXISTS \"{otherDb}\" WITH (FORCE);");
        }
    }

    // ─────────────────────────── helpers ───────────────────────────

    private static async Task ExecuteMaintenanceAsync(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Kill the backend that holds the sweep's advisory lock, from a completely
    /// separate session — the process under test is untouched, exactly as a
    /// pooler drop / idle timeout / DBA action would be.
    /// </summary>
    private async Task<bool> TerminateAdvisoryLockHolderAsync()
    {
        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT pg_terminate_backend(pid)
            FROM pg_locks
            WHERE locktype = 'advisory'
              AND granted
              AND objsubid = 1
              AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
              AND ((classid::bigint << 32) + objid::bigint) = @k;
            """;
        cmd.Parameters.AddWithValue("k", TenantMigrationSweepRunner.AdvisoryLockKey);
        return (bool?)await cmd.ExecuteScalarAsync() == true;
    }


    private static Task<TenantMigrationSweepRun> WaitForCompletionAsync(
        ITenantMigrationSweepRunner runner, Guid runId) =>
        WaitForTerminalAsync(runner, runId, TenantMigrationSweepRunState.Completed);

    private static async Task<TenantMigrationSweepRun> WaitForTerminalAsync(
        ITenantMigrationSweepRunner runner, Guid runId, string? expected = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var run = runner.TryGetRun(runId);
            if (run is not null && run.State != TenantMigrationSweepRunState.Running)
            {
                if (expected is not null)
                    run.State.Should().Be(expected, "run error was: {0}", run.Error ?? "<none>");
                return run;
            }
            await Task.Delay(25);
        }

        throw new TimeoutException($"run {runId} never left the running state");
    }

    /// <summary>
    /// A sweeper whose run is held open until the test releases it — the only
    /// way to observe "a sweep is in flight" deterministically.
    /// </summary>
    private sealed class GatedSweeper : ITenantMigrationSweeper
    {
        public readonly TaskCompletionSource Started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public readonly TaskCompletionSource Release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Exception? Throw { get; init; }

        /// <summary>
        /// Per-tenant rows published through the observer BEFORE the sweep is
        /// released — i.e. the tenants that "already got the DDL" when the
        /// sweep is later made to die.
        /// </summary>
        public IReadOnlyList<TenantMigrationSweepEntry> EmitBeforeRelease { get; init; } =
            Array.Empty<TenantMigrationSweepEntry>();

        public async Task<TenantMigrationSweepResult> SweepAsync(
            bool dryRun,
            int maxConcurrency = TenantMigrationSweep.DefaultMaxConcurrency,
            Action<TenantMigrationSweepEntry>? onTenantCompleted = null,
            CancellationToken ct = default)
        {
            Started.TrySetResult();
            foreach (var entry in EmitBeforeRelease) onTenantCompleted?.Invoke(entry);
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            if (Throw is not null) throw Throw;

            return new TenantMigrationSweepResult(
                DryRun: dryRun,
                Total: 3,
                Migrated: dryRun ? 0 : 3,
                AlreadyCurrent: 0,
                Pending: dryRun ? 3 : 0,
                Failed: 0,
                Tenants: Array.Empty<TenantMigrationSweepEntry>());
        }
    }

    /// <summary>
    /// A sweeper shared by MANY simultaneous runs — it counts concurrent starts
    /// and holds them all on one release gate, which is what the dry-run
    /// admission cap has to be measured against.
    /// </summary>
    private sealed class CountingSweeper : ITenantMigrationSweeper
    {
        private int _started;

        public readonly TaskCompletionSource Release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task WaitForStartsAsync(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Volatile.Read(ref _started) < count)
            {
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException(
                        $"only {Volatile.Read(ref _started)} of {count} sweeps started");
                await Task.Delay(10);
            }
        }

        public async Task<TenantMigrationSweepResult> SweepAsync(
            bool dryRun,
            int maxConcurrency = TenantMigrationSweep.DefaultMaxConcurrency,
            Action<TenantMigrationSweepEntry>? onTenantCompleted = null,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _started);
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            return new TenantMigrationSweepResult(
                dryRun, 0, 0, 0, 0, 0, Array.Empty<TenantMigrationSweepEntry>());
        }
    }

    /// <summary>Minimal CP factory — the runner only needs the SESSION.</summary>
    private sealed class PlainCpFactory(string cs) : IDbContextFactory<ControlPlaneDbContext>
    {
        public ControlPlaneDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<ControlPlaneDbContext>().UseNpgsql(cs).Options);
    }
}
