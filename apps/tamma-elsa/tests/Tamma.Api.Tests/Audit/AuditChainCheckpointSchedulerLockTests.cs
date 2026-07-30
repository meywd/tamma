using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.Audit;
using Tamma.Api.Tests.TestDoubles;
using Tamma.Core.Audit;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Pooling;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Audit;

/// <summary>
/// 2026-07-30 advisory-lock audit — <see cref="AuditChainCheckpointScheduler"/>
/// takes a per-hour <c>pg_try_advisory_lock</c> to elect one pod to write
/// that hour's audit-chain checkpoints. It used to take it on the tick
/// scope's <b>pooled</b> control-plane connection, and it had the worst
/// miss path of the four audited sites: the release in the <c>finally</c>
/// was passed the tick's OWN <see cref="CancellationToken"/>, so on host
/// shutdown — the moment that token is cancelled — the unlock threw
/// instantly, was swallowed by a bare <c>catch</c>, and the connector went
/// back to the pool with the hour's lock still held.
///
/// <para>These tests observe <c>pg_locks</c> through
/// <see cref="AdvisoryLockProbe"/>, which is deliberately NOT pooled: a
/// pooled probe can draw the leaking connector and clear the lock (via the
/// deferred <c>DISCARD ALL</c>) before reading, i.e. repair the very state
/// it was sent to measure.</para>
/// </summary>
[TestFixture]
public class AuditChainCheckpointSchedulerLockTests
{
    // Must match AuditChainCheckpointScheduler's private key derivation
    // exactly. Duplicated on purpose: a change to either half is a change
    // to WHO the lock excludes, and should break this test loudly.
    private const long AdvisoryLockBase = (0x5441_5544L << 32) | 0x434B_5054L;

    private static long LockKeyFor(DateTimeOffset now)
        => AdvisoryLockBase ^ ((long)now.Year << 20) ^ (now.DayOfYear * 64L + now.Hour);

    private PostgreSqlContainer _postgres = null!;
    private string _cs = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("audit_ckpt_lock")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();
        _cs = _postgres.GetConnectionString();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    private ServiceProvider BuildServices(IAuditChainCheckpointService checkpoints)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // Scoped, exactly as the API host registers it — the scheduler
        // resolves ControlPlaneDbContext out of its per-tick scope. No
        // migrations needed: the scheduler only reads the provider and the
        // connection string off this context.
        services.AddDbContext<ControlPlaneDbContext>(o => o.UseNpgsql(_cs));
        services.AddSingleton(checkpoints);
        return services.BuildServiceProvider();
    }

    private AuditChainCheckpointScheduler BuildScheduler(
        IServiceProvider sp, DateTimeOffset now)
        => new(
            sp,
            new AuditChainCheckpointOptions
            {
                RunOnStartup = true,
                FireAtMinute = 0,
                PollInterval = TimeSpan.FromMilliseconds(50),
            },
            new FixedClock(now),
            NullLogger<AuditChainCheckpointScheduler>.Instance);

    [Test]
    public async Task Host_shutdown_mid_tick_does_not_park_the_hours_leader_lock_shut()
    {
        // THE DEFECT. StopAsync cancels the scheduler's stoppingToken while
        // WriteAllActiveScopesAsync is in flight. Before the fix:
        //   1. the guarded work throws OperationCanceledException,
        //   2. the finally issues pg_advisory_unlock **with that same,
        //      already-cancelled token**, so it throws before reaching the
        //      server,
        //   3. `catch { }` swallows it — "closing the connection releases
        //      it anyway",
        //   4. the CP context's POOLED connector returns to the pool with
        //      the hour's advisory lock still held, and Npgsql defers the
        //      DISCARD ALL that would release it until that connector is
        //      next used.
        // Result: every pod (including this process's next tick) reads
        // "another pod is the leader for this hour" and skips, so that
        // hour gets NO audit-chain checkpoints from anyone.
        var now = new DateTimeOffset(2026, 07, 30, 14, 30, 00, TimeSpan.Zero);
        var key = LockKeyFor(now);

        var checkpoints = new BlockingCheckpointService();
        await using var sp = BuildServices(checkpoints);
        var scheduler = BuildScheduler(sp, now);

        await ((IHostedService)scheduler).StartAsync(CancellationToken.None);
        await checkpoints.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        (await AdvisoryLockProbe.IsHeldAsync(_cs, key)).Should().BeTrue(
            "the leader really did take this hour's lock — otherwise this test proves nothing");

        // Host shutdown: cancels stoppingToken with the work in flight.
        await ((IHostedService)scheduler).StopAsync(CancellationToken.None);

        (await AdvisoryLockProbe.IsHeldAsync(_cs, key)).Should().BeFalse(
            "a pod that shuts down mid-checkpoint must not leave this hour's leader "
            + "lock parked on an idle pooled connector — every other pod would then "
            + "skip the hour and no audit-chain checkpoints would be written for it");
    }

    [Test]
    public async Task The_leader_lock_rides_a_session_that_dies_with_the_tick()
    {
        // The property that makes every other exit path safe: the lock is
        // NOT on a connector that survives in a pool. Capture the holding
        // backend while the lock is held, then assert it is gone once the
        // tick ends. On the pooled implementation the backend survives
        // (idle in the pool) and can still be holding the lock on any path
        // where the unlock did not run.
        var now = new DateTimeOffset(2026, 07, 30, 15, 30, 00, TimeSpan.Zero);
        var key = LockKeyFor(now);

        var checkpoints = new BlockingCheckpointService();
        await using var sp = BuildServices(checkpoints);
        var scheduler = BuildScheduler(sp, now);

        await ((IHostedService)scheduler).StartAsync(CancellationToken.None);
        await checkpoints.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var pid = await AdvisoryLockProbe.HolderPidAsync(_cs, key);
        pid.Should().NotBeNull();

        // Let the tick finish normally, then stop the loop.
        checkpoints.Release.TrySetResult();
        await ((IHostedService)scheduler).StopAsync(CancellationToken.None);

        (await AdvisoryLockProbe.WaitForBackendGoneAsync(_cs, pid!.Value, TimeSpan.FromSeconds(10)))
            .Should().BeTrue(
                "the lock-holding backend must END with the tick, not be handed back to a "
                + "connection pool still alive — a live backend is a session that can still "
                + "own the lock");
        (await AdvisoryLockProbe.IsHeldAsync(_cs, key)).Should().BeFalse();
    }

    [Test]
    public async Task A_pod_that_loses_the_election_skips_and_leaves_the_lock_with_the_winner()
    {
        // The lock's meaning must be unchanged by the audit: an outside
        // holder of this hour's key still makes this pod stand down, and
        // the loser must not write checkpoints.
        var now = new DateTimeOffset(2026, 07, 30, 16, 30, 00, TimeSpan.Zero);
        var key = LockKeyFor(now);

        await using var holder = await PostgresAdvisoryLock.TryAcquireAsync(
            _cs, PostgresAdvisoryLockKey.FromInt64(key));
        holder.Should().NotBeNull("test setup takes this hour's leader lock");

        var checkpoints = new BlockingCheckpointService();
        checkpoints.Release.TrySetResult();
        await using var sp = BuildServices(checkpoints);
        var scheduler = BuildScheduler(sp, now);

        await ((IHostedService)scheduler).StartAsync(CancellationToken.None);
        await Task.Delay(500);
        await ((IHostedService)scheduler).StopAsync(CancellationToken.None);

        checkpoints.Calls.Should().Be(0,
            "the pod that loses pg_try_advisory_lock for the hour must not write checkpoints");
        (await AdvisoryLockProbe.IsHeldAsync(_cs, key)).Should().BeTrue(
            "and it must not have released the winner's lock either");
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class BlockingCheckpointService : IAuditChainCheckpointService
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public Task<AuditChainCheckpoint?> WriteCheckpointAsync(
            AuditChainScope scope, CancellationToken ct = default)
            => Task.FromResult<AuditChainCheckpoint?>(null);

        public async Task<int> WriteAllActiveScopesAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            Entered.TrySetResult();
            // Throws OperationCanceledException when the host stops — the
            // exact shape of the shutdown path under audit.
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(60), ct);
            return 0;
        }
    }
}
