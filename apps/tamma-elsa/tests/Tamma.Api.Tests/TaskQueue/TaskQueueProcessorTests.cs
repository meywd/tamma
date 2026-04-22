using Tamma.Data.Abstractions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.TaskQueue;
using Tamma.Api.Tests.Infrastructure;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.TaskQueue;

/// <summary>
/// Processor behaviour tests. Exercises a single poll cycle at a time via
/// <see cref="TaskQueueProcessor.ProcessOnceAsync"/> so the hosted-service
/// loop stays simple and timer-free here. The polling cadence itself is
/// covered by the integration test in
/// <see cref="GitHubWebhookTaskQueueIntegrationTests"/>.
/// </summary>
[TestFixture]
public class TaskQueueProcessorTests
{
    private ServiceProvider _services = null!;
    private DbContextOptions<ControlPlaneDbContext> _cpOptions = null!;
    private DbContextOptions<TenantDbContext> _tenantOptions = null!;
    private Mock<ITaskHandler> _handler = null!;
    private TaskQueueProcessor _processor = null!;

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
        var services = new ServiceCollection();
        services.AddScoped<ControlPlaneDbContext>(_ => new TestControlPlaneDbContext(capturedCp));
        services.AddSingleton<ITenantDbContextFactory>(_ => new TestTenantDbContextFactory(capturedTenant));
        services.AddScoped<IQueuedTaskRepository, QueuedTaskRepository>();

        _handler = new Mock<ITaskHandler>();
        _handler.SetupGet(h => h.TypePrefix).Returns("github.");

        var registry = new Mock<ITaskHandlerRegistry>();
        registry.Setup(r => r.ResolveFor(It.Is<string>(s => s.StartsWith("github."))))
            .Returns(_handler.Object);
        registry.Setup(r => r.ResolveFor(It.Is<string>(s => !s.StartsWith("github."))))
            .Returns((ITaskHandler?)null);
        services.AddSingleton(registry.Object);

        _services = services.BuildServiceProvider();

        _processor = new TaskQueueProcessor(
            _services,
            new TaskQueueProcessorOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(50),
                MaxRetries = 3
            },
            NullLogger<TaskQueueProcessor>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _processor.Dispose();
        _services.Dispose();
    }

    /// <summary>Fresh repo/context per call — mirrors the scope-per-poll pattern
    /// the processor uses, and stops EF Core change-tracking from returning
    /// stale copies of rows that another scope already updated.</summary>
    private QueuedTaskRepository FreshRepo()
        => new QueuedTaskRepository(
            new TestTenantDbContextFactory(_tenantOptions),
            new TestControlPlaneDbContext(_cpOptions));

    // ─── Happy path ───────────────────────────────────────────────────────────

    [Test]
    public async Task ProcessOnceAsync_PicksPendingTask_CallsHandler_MarksComplete()
    {
        var enqueued = await FreshRepo().EnqueueAsync(new QueuedTask
        {
            Type = "github.push.main",
            Payload = "{\"ref\":\"refs/heads/main\"}"
        });

        _handler.Setup(h => h.HandleAsync(It.IsAny<QueuedTask>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var processed = await _processor.ProcessOnceAsync(CancellationToken.None);

        processed.Should().Be(1);
        _handler.Verify(h => h.HandleAsync(
            It.Is<QueuedTask>(t => t.Id == enqueued.Id),
            It.IsAny<CancellationToken>()), Times.Once);

        var stored = await FreshRepo().GetAsync(enqueued.Id);
        stored!.Status.Should().Be("completed");
        stored.Error.Should().BeNull();
    }

    [Test]
    public async Task ProcessOnceAsync_NoPendingTasks_ReturnsZero()
    {
        var processed = await _processor.ProcessOnceAsync(CancellationToken.None);
        processed.Should().Be(0);
        _handler.Verify(
            h => h.HandleAsync(It.IsAny<QueuedTask>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ─── Handler throws ───────────────────────────────────────────────────────

    [Test]
    public async Task ProcessOnceAsync_HandlerThrows_RequeuesWithIncrementedRetry()
    {
        var enqueued = await FreshRepo().EnqueueAsync(new QueuedTask { Type = "github.x" });

        _handler.Setup(h => h.HandleAsync(It.IsAny<QueuedTask>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("transient boom"));

        await _processor.ProcessOnceAsync(CancellationToken.None);

        var stored = await FreshRepo().GetAsync(enqueued.Id);
        stored!.Status.Should().Be("pending");  // requeued
        stored.RetryCount.Should().Be(1);
        stored.Error.Should().Contain("transient boom");
    }

    [Test]
    public async Task ProcessOnceAsync_HandlerThrowsThreeTimes_MarksFailed()
    {
        var enqueued = await FreshRepo().EnqueueAsync(new QueuedTask { Type = "github.x" });

        _handler.Setup(h => h.HandleAsync(It.IsAny<QueuedTask>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("broken"));

        // Attempt 1 → RetryCount 1, pending
        await _processor.ProcessOnceAsync(CancellationToken.None);
        (await FreshRepo().GetAsync(enqueued.Id))!.Status.Should().Be("pending");

        // Attempt 2 → RetryCount 2, pending
        await _processor.ProcessOnceAsync(CancellationToken.None);
        (await FreshRepo().GetAsync(enqueued.Id))!.Status.Should().Be("pending");

        // Attempt 3 → RetryCount 3, now failed (exhausted)
        await _processor.ProcessOnceAsync(CancellationToken.None);

        var stored = await FreshRepo().GetAsync(enqueued.Id);
        stored!.Status.Should().Be("failed");
        stored.RetryCount.Should().Be(3);
        stored.Error.Should().Contain("broken");
    }

    // ─── No handler registered ────────────────────────────────────────────────

    [Test]
    public async Task ProcessOnceAsync_NoHandlerRegistered_MarksFailedWithClearError()
    {
        // Type does NOT start with "github." — registry returns null.
        var enqueued = await FreshRepo().EnqueueAsync(new QueuedTask { Type = "unknown.type" });

        await _processor.ProcessOnceAsync(CancellationToken.None);

        var stored = await FreshRepo().GetAsync(enqueued.Id);
        stored!.Status.Should().Be("failed");
        stored.Error.Should().Contain("no handler");
    }
}
