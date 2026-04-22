using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.Engine;
using Tamma.Api.Services.Engine.Lifecycle;

namespace Tamma.Api.Tests.Engine;

/// <summary>
/// Unit test for <see cref="EngineRegistryHeartbeatService.PublishOnceAsync"/>.
/// Exercises the per-cycle publish path without the hosted-service timer so
/// the test stays deterministic.
/// </summary>
[TestFixture]
public class EngineRegistryHeartbeatServiceTests
{
    [Test]
    public async Task PublishOnceAsync_EmptyRegistry_EmitsPlatformWideHeartbeat()
    {
        using var bus = new InMemoryEngineLifecycleBus();

        var services = new ServiceCollection();
        services.AddSingleton<IEngineRegistry>(new StubRegistry(Array.Empty<EngineInfo>()));
        using var provider = services.BuildServiceProvider();

        var svc = new EngineRegistryHeartbeatService(
            provider, bus, NullLogger<EngineRegistryHeartbeatService>.Instance);

        var received = new List<EngineLifecycleEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var consumer = Task.Run(async () =>
        {
            await foreach (var evt in bus.SubscribeAsync(Guid.NewGuid(), cts.Token))
            {
                received.Add(evt);
                if (received.Count >= 1) break;
            }
        });

        await WaitForSubscribersAsync(bus, 1, cts.Token);
        await svc.PublishOnceAsync(cts.Token);

        await consumer.WaitAsync(cts.Token);

        received.Should().HaveCount(1);
        received[0].Type.Should().Be("engine.heartbeat");
        received[0].TenantId.Should().BeNull("empty registry publishes platform-wide");
    }

    [Test]
    public async Task PublishOnceAsync_TenantScopedEngine_EmitsPerTenantHeartbeat()
    {
        using var bus = new InMemoryEngineLifecycleBus();
        var tenantId = Guid.NewGuid();

        var services = new ServiceCollection();
        services.AddSingleton<IEngineRegistry>(new StubRegistry(new[]
        {
            new EngineInfo(
                Id: $"engine-{tenantId:N}",
                State: "running",
                Stats: new EngineStats(TotalEvents: 42, LastEventAt: DateTime.UtcNow),
                TenantId: tenantId)
        }));
        using var provider = services.BuildServiceProvider();

        var svc = new EngineRegistryHeartbeatService(
            provider, bus, NullLogger<EngineRegistryHeartbeatService>.Instance);

        var received = new List<EngineLifecycleEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var consumer = Task.Run(async () =>
        {
            await foreach (var evt in bus.SubscribeAsync(tenantId, cts.Token))
            {
                received.Add(evt);
                if (received.Count >= 1) break;
            }
        });

        await WaitForSubscribersAsync(bus, 1, cts.Token);
        await svc.PublishOnceAsync(cts.Token);

        await consumer.WaitAsync(cts.Token);

        received.Should().HaveCount(1);
        received[0].Type.Should().Be("engine.heartbeat");
        received[0].TenantId.Should().Be(tenantId);
    }

    private static async Task WaitForSubscribersAsync(IEngineLifecycleBus bus, int expected, CancellationToken ct)
    {
        for (var i = 0; i < 100; i++)
        {
            if (bus.SubscriberCount >= expected) return;
            await Task.Delay(20, ct);
        }
        throw new TimeoutException();
    }

    private sealed class StubRegistry : IEngineRegistry
    {
        private readonly IReadOnlyList<EngineInfo> _engines;
        public StubRegistry(IEnumerable<EngineInfo> engines) { _engines = engines.ToList(); }
        public int Count => _engines.Count;
        public Task<IReadOnlyList<EngineInfo>> ListAsync(Guid? tenantId, CancellationToken ct = default)
        {
            var filtered = tenantId.HasValue
                ? _engines.Where(e => e.TenantId == tenantId).ToList()
                : _engines.ToList();
            return Task.FromResult<IReadOnlyList<EngineInfo>>(filtered);
        }
    }
}
