using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Requests;
using Elsa.Workflows.Runtime.Responses;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Analytics;

/// <summary>
/// Round-2 H9 — coverage for the new
/// <see cref="HourlyAnalyticsRollupScheduler"/> leader-election
/// behaviour. The scheduler now wraps its dispatch in a
/// <see cref="IRollupSchedulerLeaderLock"/> so two pods racing for the
/// same hour don't both fire the workflow.
///
/// <para>The tests use a fixed <see cref="TimeProvider"/> + a fake
/// leader-lock so the assertions are deterministic without standing up
/// a Postgres container.</para>
/// </summary>
[TestFixture]
public class HourlyAnalyticsRollupSchedulerTests
{
    private sealed class FakeLeaderLock : IRollupSchedulerLeaderLock
    {
        private readonly Func<long, IAsyncDisposable?> _factory;
        public List<long> Attempts { get; } = new();
        public int ReleaseCount { get; private set; }

        public FakeLeaderLock(Func<long, IAsyncDisposable?> factory)
        {
            _factory = factory;
        }

        public Task<IAsyncDisposable?> TryAcquireAsync(long lockKey, CancellationToken ct)
        {
            Attempts.Add(lockKey);
            var lease = _factory(lockKey);
            if (lease is null) return Task.FromResult<IAsyncDisposable?>(null);
            return Task.FromResult<IAsyncDisposable?>(new TrackingLease(lease, () => ReleaseCount++));
        }

        private sealed class TrackingLease : IAsyncDisposable
        {
            private readonly IAsyncDisposable _inner;
            private readonly Action _onRelease;
            public TrackingLease(IAsyncDisposable inner, Action onRelease)
            {
                _inner = inner;
                _onRelease = onRelease;
            }
            public async ValueTask DisposeAsync()
            {
                _onRelease();
                await _inner.DisposeAsync();
            }
        }
    }

    private sealed class GrantingLease : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static (Mock<IWorkflowDispatcher> dispatcher, FakeLeaderLock leader,
        HourlyAnalyticsRollupScheduler scheduler) Build(
        DateTimeOffset now, Func<long, IAsyncDisposable?> leaseFactory)
    {
        var time = new FakeTimeProvider(now);
        var leader = new FakeLeaderLock(leaseFactory);
        var dispatcher = new Mock<IWorkflowDispatcher>();
        dispatcher.Setup(d => d.DispatchAsync(
                It.IsAny<DispatchWorkflowDefinitionRequest>(),
                It.IsAny<DispatchWorkflowOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchWorkflowResponse(Fault: null));

        var opts = Options.Create(new HourlyAnalyticsRollupSchedulerOptions
        {
            FireAtMinute = 5,
            PollInterval = TimeSpan.FromSeconds(30),
        });
        var scheduler = new HourlyAnalyticsRollupScheduler(
            dispatcher.Object,
            opts,
            time,
            NullLogger<HourlyAnalyticsRollupScheduler>.Instance,
            configuration: null,
            leaderLock: leader);
        return (dispatcher, leader, scheduler);
    }

    [Test]
    public void ComputeAdvisoryLockKey_IsDeterministic()
    {
        var a = HourlyAnalyticsRollupScheduler.ComputeAdvisoryLockKey(2026, 117, 12);
        var b = HourlyAnalyticsRollupScheduler.ComputeAdvisoryLockKey(2026, 117, 12);
        a.Should().Be(b, "two pods computing the lock id for the same hour must agree");
    }

    [Test]
    public void ComputeAdvisoryLockKey_DiffersAcrossHours()
    {
        var h12 = HourlyAnalyticsRollupScheduler.ComputeAdvisoryLockKey(2026, 117, 12);
        var h13 = HourlyAnalyticsRollupScheduler.ComputeAdvisoryLockKey(2026, 117, 13);
        h12.Should().NotBe(h13);
    }

    [Test]
    public async Task TickAsync_AcquiresLeader_AndDispatches_WhenLockGranted()
    {
        var fireAt = new DateTimeOffset(2026, 04, 26, 12, 06, 00, TimeSpan.Zero);
        var (dispatcher, leader, scheduler) = Build(fireAt, _ => new GrantingLease());

        await scheduler.InvokeTickForTestsAsync(default);

        dispatcher.Verify(d => d.DispatchAsync(
            It.IsAny<DispatchWorkflowDefinitionRequest>(),
            It.IsAny<DispatchWorkflowOptions>(),
            It.IsAny<CancellationToken>()), Times.Once);
        leader.Attempts.Should().HaveCount(1);
        leader.ReleaseCount.Should().Be(1, "the lease must release at end of dispatch");
    }

    [Test]
    public async Task TickAsync_DoesNotDispatch_WhenAnotherPodIsLeader()
    {
        // Round-2 H9 — when pg_try_advisory_lock returns false the
        // scheduler MUST skip the dispatch (another pod is the
        // leader for this hour).
        var fireAt = new DateTimeOffset(2026, 04, 26, 12, 06, 00, TimeSpan.Zero);
        var (dispatcher, leader, scheduler) = Build(fireAt, _ => null);

        await scheduler.InvokeTickForTestsAsync(default);

        dispatcher.Verify(d => d.DispatchAsync(
            It.IsAny<DispatchWorkflowDefinitionRequest>(),
            It.IsAny<DispatchWorkflowOptions>(),
            It.IsAny<CancellationToken>()), Times.Never);
        leader.Attempts.Should().HaveCount(1);
        leader.ReleaseCount.Should().Be(0,
            "the lease was never acquired so nothing to release");
    }

    [Test]
    public async Task TickAsync_TwoPodsRacing_OnlyOneDispatches()
    {
        // Simulate two pods polling the same hour in parallel: the
        // first call returns a lease; the second sees the lock held
        // and gets null.
        var fireAt = new DateTimeOffset(2026, 04, 26, 12, 06, 00, TimeSpan.Zero);
        var leaderState = 0;
        Func<long, IAsyncDisposable?> factory = _ =>
        {
            return Interlocked.Increment(ref leaderState) == 1
                ? new GrantingLease()
                : null;
        };

        var (dispatcherA, _, schedulerA) = Build(fireAt, factory);
        var (dispatcherB, _, schedulerB) = Build(fireAt, factory);

        await schedulerA.InvokeTickForTestsAsync(default);
        await schedulerB.InvokeTickForTestsAsync(default);

        dispatcherA.Verify(d => d.DispatchAsync(
            It.IsAny<DispatchWorkflowDefinitionRequest>(),
            It.IsAny<DispatchWorkflowOptions>(),
            It.IsAny<CancellationToken>()), Times.Once);
        dispatcherB.Verify(d => d.DispatchAsync(
            It.IsAny<DispatchWorkflowDefinitionRequest>(),
            It.IsAny<DispatchWorkflowOptions>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task TickAsync_RecordsHourLocally_WhenAnotherPodIsLeader()
    {
        // After the leader-skip branch fires, a subsequent tick within
        // the same hour must NOT race the lock again.
        var fireAt = new DateTimeOffset(2026, 04, 26, 12, 06, 00, TimeSpan.Zero);
        var (_, leader, scheduler) = Build(fireAt, _ => null);

        await scheduler.InvokeTickForTestsAsync(default);
        await scheduler.InvokeTickForTestsAsync(default);

        leader.Attempts.Should().HaveCount(1,
            "after observing 'another pod is leader', the scheduler should not re-race the lock for the same hour");
    }
}

/// <summary>
/// Local <see cref="TimeProvider"/> stub. The shared
/// Microsoft.Extensions.TimeProvider.Testing package isn't on the
/// activities-tests project; this minimal fake suffices.
/// </summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now;
    public FakeTimeProvider(DateTimeOffset now) { _now = now; }
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan delta) => _now = _now.Add(delta);
}
