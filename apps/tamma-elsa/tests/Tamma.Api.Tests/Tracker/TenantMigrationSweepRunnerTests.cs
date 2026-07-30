using FluentAssertions;
using Microsoft.EntityFrameworkCore;
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
        run.Result.Should().BeNull("the sweep itself died — there is no per-tenant result");

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

    // ─────────────────────────── helpers ───────────────────────────

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

        public async Task<TenantMigrationSweepResult> SweepAsync(
            bool dryRun,
            int maxConcurrency = TenantMigrationSweep.DefaultMaxConcurrency,
            CancellationToken ct = default)
        {
            Started.TrySetResult();
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

    /// <summary>Minimal CP factory — the runner only needs the SESSION.</summary>
    private sealed class PlainCpFactory(string cs) : IDbContextFactory<ControlPlaneDbContext>
    {
        public ControlPlaneDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<ControlPlaneDbContext>().UseNpgsql(cs).Options);
    }
}
