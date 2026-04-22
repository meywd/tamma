using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Engine.Lifecycle;

namespace Tamma.Api.Tests.Engine;

/// <summary>
/// Unit tests for the in-memory lifecycle bus. These don't touch HTTP —
/// they exercise the subscribe / fanout / tenant-filter / cancellation
/// contract directly.
/// </summary>
[TestFixture]
public class InMemoryEngineLifecycleBusTests
{
    [Test]
    public async Task PublishAsync_DeliversEventsToSubscriberInOrder()
    {
        await using var bus = AsAsyncDisposable(new InMemoryEngineLifecycleBus());
        var tenant = Guid.NewGuid();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var received = new List<EngineLifecycleEvent>();
        var consumer = Task.Run(async () =>
        {
            await foreach (var evt in bus.Value.SubscribeAsync(tenant, cts.Token))
            {
                received.Add(evt);
                if (received.Count >= 3) break;
            }
        });

        // Give the subscriber a beat to actually register its channel before
        // we publish. The SingleReader channel is created inside the async
        // iterator's first MoveNextAsync.
        await WaitForSubscriberAsync(bus.Value, expected: 1, cts.Token);

        await bus.Value.PublishAsync(MakeEvent("workflow.started", tenant, 1));
        await bus.Value.PublishAsync(MakeEvent("workflow.completed", tenant, 2));
        await bus.Value.PublishAsync(MakeEvent("task.claimed", tenant, 3));

        await consumer.WaitAsync(cts.Token);

        received.Should().HaveCount(3);
        received.Select(e => e.Type).Should().ContainInOrder(
            "workflow.started", "workflow.completed", "task.claimed");
    }

    [Test]
    public async Task PublishAsync_DoesNotLeakAcrossTenants()
    {
        await using var bus = AsAsyncDisposable(new InMemoryEngineLifecycleBus());
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var receivedA = new List<EngineLifecycleEvent>();
        var consumerA = Task.Run(async () =>
        {
            await foreach (var evt in bus.Value.SubscribeAsync(tenantA, cts.Token))
            {
                receivedA.Add(evt);
                if (receivedA.Count >= 1) break;
            }
        });

        var receivedB = new List<EngineLifecycleEvent>();
        var consumerB = Task.Run(async () =>
        {
            await foreach (var evt in bus.Value.SubscribeAsync(tenantB, cts.Token))
            {
                receivedB.Add(evt);
                if (receivedB.Count >= 1) break;
            }
        });

        await WaitForSubscriberAsync(bus.Value, expected: 2, cts.Token);

        // Publish one event each. A must never see B's and vice versa.
        await bus.Value.PublishAsync(MakeEvent("workflow.a", tenantA, 1));
        await bus.Value.PublishAsync(MakeEvent("workflow.b", tenantB, 2));

        await Task.WhenAll(consumerA, consumerB).WaitAsync(cts.Token);

        receivedA.Should().HaveCount(1);
        receivedA[0].TenantId.Should().Be(tenantA);
        receivedA[0].Type.Should().Be("workflow.a");

        receivedB.Should().HaveCount(1);
        receivedB[0].TenantId.Should().Be(tenantB);
        receivedB[0].Type.Should().Be("workflow.b");
    }

    [Test]
    public async Task PublishAsync_NullTenantEvent_ReachesEverySubscriber()
    {
        await using var bus = AsAsyncDisposable(new InMemoryEngineLifecycleBus());
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var receivedA = new List<EngineLifecycleEvent>();
        var consumerA = Task.Run(async () =>
        {
            await foreach (var evt in bus.Value.SubscribeAsync(tenantA, cts.Token))
            {
                receivedA.Add(evt);
                if (receivedA.Count >= 1) break;
            }
        });
        var receivedB = new List<EngineLifecycleEvent>();
        var consumerB = Task.Run(async () =>
        {
            await foreach (var evt in bus.Value.SubscribeAsync(tenantB, cts.Token))
            {
                receivedB.Add(evt);
                if (receivedB.Count >= 1) break;
            }
        });

        await WaitForSubscriberAsync(bus.Value, expected: 2, cts.Token);

        // Platform-wide event — finding 013's heartbeat uses this for
        // the "registry is empty but alive" signal.
        await bus.Value.PublishAsync(new EngineLifecycleEvent(
            "engine.heartbeat", TenantId: null, DateTimeOffset.UtcNow,
            new { engineCount = 0 }));

        await Task.WhenAll(consumerA, consumerB).WaitAsync(cts.Token);

        receivedA.Should().HaveCount(1);
        receivedB.Should().HaveCount(1);
        receivedA[0].TenantId.Should().BeNull();
        receivedB[0].TenantId.Should().BeNull();
    }

    [Test]
    public async Task SubscribeAsync_CancellationCleansUpSubscription()
    {
        await using var bus = AsAsyncDisposable(new InMemoryEngineLifecycleBus());
        var tenant = Guid.NewGuid();
        using var cts = new CancellationTokenSource();

        var consumer = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in bus.Value.SubscribeAsync(tenant, cts.Token))
                {
                    // never
                }
            }
            catch (OperationCanceledException)
            {
                // expected
            }
        });

        await WaitForSubscriberAsync(bus.Value, expected: 1, CancellationToken.None);
        bus.Value.SubscriberCount.Should().Be(1);

        cts.Cancel();
        await consumer.WaitAsync(TimeSpan.FromSeconds(5));

        bus.Value.SubscriberCount.Should().Be(0,
            "cancellation must remove the subscription from the fanout map");
    }

    [Test]
    public void PublishAsync_WithoutSubscribers_DoesNotThrow()
    {
        using var bus = new InMemoryEngineLifecycleBus();
        var act = async () => await bus.PublishAsync(MakeEvent("engine.heartbeat", null, 1));
        act.Should().NotThrowAsync();
    }

    // ---------- Helpers ---------------------------------------------------

    private static EngineLifecycleEvent MakeEvent(string type, Guid? tenantId, int seq) =>
        new(type, tenantId, DateTimeOffset.UtcNow, new { seq });

    private static async Task WaitForSubscriberAsync(
        IEngineLifecycleBus bus, int expected, CancellationToken ct)
    {
        // The async iterator registers its channel on first MoveNextAsync,
        // which happens in the background task. We busy-spin with a tiny
        // delay to avoid publishing before the channel exists.
        for (var i = 0; i < 100; i++)
        {
            if (bus.SubscriberCount >= expected) return;
            await Task.Delay(20, ct);
        }
        throw new TimeoutException(
            $"Expected {expected} subscriber(s) but saw {bus.SubscriberCount}");
    }

    // Simple async-disposable wrapper so NUnit's `await using` works with the
    // non-async IDisposable bus.
    private sealed class AsyncWrap<T> : IAsyncDisposable where T : IDisposable
    {
        public T Value { get; }
        public AsyncWrap(T value) { Value = value; }
        public ValueTask DisposeAsync() { Value.Dispose(); return ValueTask.CompletedTask; }
    }
    private static AsyncWrap<T> AsAsyncDisposable<T>(T value) where T : IDisposable =>
        new(value);
}
