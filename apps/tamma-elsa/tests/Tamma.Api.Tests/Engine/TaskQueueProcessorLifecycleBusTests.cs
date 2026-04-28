using Tamma.Data.Abstractions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Engine.Lifecycle;
using Tamma.Api.Services.TaskQueue;
using Tamma.Api.Tests.Infrastructure;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Engine;

/// <summary>
/// Ensures <see cref="TaskQueueProcessor"/> publishes <c>task.claimed</c>
/// / <c>task.completed</c> / <c>task.failed</c> frames on the
/// <see cref="IEngineLifecycleBus"/> so the dashboard SSE task-lifecycle
/// tile updates live (finding 012).
/// </summary>
[TestFixture]
public class TaskQueueProcessorLifecycleBusTests
{
    private ServiceProvider _services = null!;
    private DbContextOptions<ControlPlaneDbContext> _cpOptions = null!;
    private DbContextOptions<TenantDbContext> _tenantOptions = null!;
    private Mock<ITaskHandler> _handler = null!;
    private TaskQueueProcessor _processor = null!;
    private InMemoryEngineLifecycleBus _bus = null!;

    [SetUp]
    public void SetUp()
    {
        var dbName = Guid.NewGuid().ToString();
        _cpOptions = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _tenantOptions = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        var capturedCp = _cpOptions;
        var capturedTenant = _tenantOptions;
        _bus = new InMemoryEngineLifecycleBus();

        var services = new ServiceCollection();
        services.AddScoped<ControlPlaneDbContext>(_ => new TestControlPlaneDbContext(capturedCp));
        services.AddSingleton<ITenantDbContextFactory>(_ => new TestTenantDbContextFactory(capturedTenant));
        services.AddScoped<IQueuedTaskRepository, QueuedTaskRepository>();
        services.AddSingleton<IEngineLifecycleBus>(_bus);

        _handler = new Mock<ITaskHandler>();
        _handler.SetupGet(h => h.TypePrefix).Returns("github.");

        var registry = new Mock<ITaskHandlerRegistry>();
        registry.Setup(r => r.ResolveFor(It.Is<string>(s => s.StartsWith("github."))))
            .Returns(_handler.Object);
        services.AddSingleton(registry.Object);

        _services = services.BuildServiceProvider();
        _processor = new TaskQueueProcessor(
            _services,
            new TaskQueueProcessorOptions { PollInterval = TimeSpan.FromMilliseconds(50), MaxRetries = 3 },
            NullLogger<TaskQueueProcessor>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _processor.Dispose();
        _services.Dispose();
        _bus.Dispose();
    }

    private QueuedTaskRepository FreshRepo()
        => new(new TestTenantDbContextFactory(_tenantOptions),
               new TestControlPlaneDbContext(_cpOptions));

    /// <summary>
    /// Seed a tenant so <see cref="QueuedTaskRepository.ListPendingFromAnyTenantAsync"/>
    /// has a tenant to walk. Story 28-1 PR B — the processor's drain
    /// path enumerates active tenants from CP.
    /// </summary>
    private void SeedTenant(Guid tenantId)
    {
        using var cp = new TestControlPlaneDbContext(_cpOptions);
        if (cp.Tenants.Find(tenantId) is null)
        {
            cp.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Name = "test",
                Slug = "test-" + tenantId.ToString()[..8],
                Type = "personal",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            cp.SaveChanges();
        }
    }

    [Test]
    public async Task ProcessOnceAsync_SuccessfulTask_PublishesClaimedThenCompleted()
    {
        var tenantId = Guid.NewGuid();
        SeedTenant(tenantId);
        await FreshRepo().EnqueueAsync(new QueuedTask
        {
            Type = "github.push.main",
            Payload = "{}",
            TenantId = tenantId,
        });

        _handler.Setup(h => h.HandleAsync(It.IsAny<QueuedTask>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new List<EngineLifecycleEvent>();
        var consumer = Task.Run(async () =>
        {
            await foreach (var evt in _bus.SubscribeAsync(tenantId, cts.Token))
            {
                received.Add(evt);
                if (received.Count >= 2) break;
            }
        });

        await WaitForSubscribersAsync(_bus, 1, cts.Token);
        await _processor.ProcessOnceAsync(CancellationToken.None);

        await consumer.WaitAsync(cts.Token);

        received.Should().HaveCount(2);
        received[0].Type.Should().Be("task.claimed");
        received[0].TenantId.Should().Be(tenantId);
        received[1].Type.Should().Be("task.completed");
    }

    [Test]
    public async Task ProcessOnceAsync_HandlerThrows_PublishesClaimedThenFailed()
    {
        var tenantId = Guid.NewGuid();
        SeedTenant(tenantId);
        await FreshRepo().EnqueueAsync(new QueuedTask
        {
            Type = "github.x",
            TenantId = tenantId,
        });

        _handler.Setup(h => h.HandleAsync(It.IsAny<QueuedTask>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new List<EngineLifecycleEvent>();
        var consumer = Task.Run(async () =>
        {
            await foreach (var evt in _bus.SubscribeAsync(tenantId, cts.Token))
            {
                received.Add(evt);
                if (received.Count >= 2) break;
            }
        });

        await WaitForSubscribersAsync(_bus, 1, cts.Token);
        await _processor.ProcessOnceAsync(CancellationToken.None);
        await consumer.WaitAsync(cts.Token);

        received.Should().HaveCount(2);
        received[0].Type.Should().Be("task.claimed");
        received[1].Type.Should().Be("task.failed");
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
}
