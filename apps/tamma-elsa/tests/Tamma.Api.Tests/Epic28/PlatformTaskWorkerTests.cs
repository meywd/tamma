using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Tamma.Api.Services.PlatformTasks;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Epic28;

/// <summary>
/// Story 28-6 — unit tests for <see cref="PlatformTaskWorker"/>'s
/// drive-once entry point. Exercises the four success/failure paths:
/// <list type="bullet">
///   <item><description>Empty queue → returns false</description></item>
///   <item><description>Handler succeeds → row marked completed</description></item>
///   <item><description>Handler throws normal Exception → row failed (retry counted)</description></item>
///   <item><description>Handler throws PlatformTaskTerminalException → row dead-lettered</description></item>
///   <item><description>No registered handler → row dead-lettered with explanation</description></item>
/// </list>
/// </summary>
[TestFixture]
public class PlatformTaskWorkerTests
{
    private string _dbName = null!;
    private ServiceProvider _sp = null!;

    private sealed class CountingHandler : IPlatformTaskHandler
    {
        public CountingHandler(string type) { TaskType = type; }
        public string TaskType { get; }
        public int Calls { get; private set; }
        public Func<PlatformQueuedTask, CancellationToken, Task> Behavior { get; set; } =
            (_, _) => Task.CompletedTask;

        public async Task HandleAsync(PlatformQueuedTask task, CancellationToken ct)
        {
            Calls++;
            await Behavior(task, ct);
        }
    }

    [SetUp]
    public void SetUp()
    {
        _dbName = $"task-worker-test-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ControlPlaneDbContext>(opts =>
            opts.UseInMemoryDatabase(_dbName));
        services.AddScoped<IPlatformQueuedTaskRepository, PlatformQueuedTaskRepository>();
        _sp = services.BuildServiceProvider();
    }

    [TearDown]
    public void TearDown() => _sp.Dispose();

    private PlatformTaskWorker NewWorker(IPlatformTaskHandlerRegistry registry,
        PlatformTaskWorkerOptions? opts = null)
    {
        // Worker resolves repo + registry from a scope each tick.
        // Stitch the registry into the SP via a shim.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ControlPlaneDbContext>(o =>
            o.UseInMemoryDatabase(_dbName));
        services.AddScoped<IPlatformQueuedTaskRepository, PlatformQueuedTaskRepository>();
        services.AddSingleton(registry);
        var sp = services.BuildServiceProvider();
        return new PlatformTaskWorker(
            sp,
            Options.Create(opts ?? new PlatformTaskWorkerOptions { RunOnStartup = false }),
            TimeProvider.System,
            NullLogger<PlatformTaskWorker>.Instance);
    }

    private async Task<Guid> EnqueueAsync(string type)
    {
        await using var scope = _sp.CreateAsyncScope();
        var repo = scope.ServiceProvider
            .GetRequiredService<IPlatformQueuedTaskRepository>();
        var task = await repo.EnqueueAsync(new PlatformQueuedTask
        {
            Type = type,
            Payload = "{}",
        });
        return task.Id;
    }

    private async Task<PlatformQueuedTask?> GetAsync(Guid id)
    {
        await using var scope = _sp.CreateAsyncScope();
        var repo = scope.ServiceProvider
            .GetRequiredService<IPlatformQueuedTaskRepository>();
        return await repo.GetAsync(id);
    }

    [Test]
    public async Task ProcessOnce_EmptyQueue_ReturnsFalse()
    {
        var registry = new PlatformTaskHandlerRegistry(Array.Empty<IPlatformTaskHandler>());
        var worker = NewWorker(registry);

        var processed = await worker.ProcessOnceAsync(default);

        processed.Should().BeFalse();
    }

    [Test]
    public async Task ProcessOnce_HandlerSucceeds_RowMarkedCompleted()
    {
        var handler = new CountingHandler("ok.task");
        var registry = new PlatformTaskHandlerRegistry(new[] { handler });
        var id = await EnqueueAsync("ok.task");
        var worker = NewWorker(registry);

        var processed = await worker.ProcessOnceAsync(default);

        processed.Should().BeTrue();
        handler.Calls.Should().Be(1);
        var row = await GetAsync(id);
        row!.Status.Should().Be("completed");
    }

    [Test]
    public async Task ProcessOnce_HandlerThrows_RowMarkedFailedWithRetry()
    {
        var handler = new CountingHandler("retry.task")
        {
            Behavior = (_, _) => throw new InvalidOperationException("boom"),
        };
        var registry = new PlatformTaskHandlerRegistry(new[] { handler });
        var id = await EnqueueAsync("retry.task");
        var worker = NewWorker(registry, new PlatformTaskWorkerOptions
        {
            RunOnStartup = false,
            MaxRetries = 5,
        });

        await worker.ProcessOnceAsync(default);

        var row = await GetAsync(id);
        row!.Error.Should().Contain("boom");
        // Retried (status returned to pending) — not dead-lettered yet.
        row.Status.Should().Be("pending");
        row.RetryCount.Should().Be(1);
    }

    [Test]
    public async Task ProcessOnce_HandlerThrowsTerminal_RowDeadLettered()
    {
        var handler = new CountingHandler("terminal.task")
        {
            Behavior = (_, _) =>
                throw new PlatformTaskTerminalException("malformed payload"),
        };
        var registry = new PlatformTaskHandlerRegistry(new[] { handler });
        var id = await EnqueueAsync("terminal.task");
        var worker = NewWorker(registry);

        await worker.ProcessOnceAsync(default);

        var row = await GetAsync(id);
        row!.Status.Should().Be("dead_letter");
        row.Error.Should().Contain("malformed payload");
        row.RetryCount.Should().Be(0,
            "terminal failure does not consume retry budget");
    }

    [Test]
    public async Task ProcessOnce_NoHandler_RowDeadLetteredWithExplanation()
    {
        var registry = new PlatformTaskHandlerRegistry(Array.Empty<IPlatformTaskHandler>());
        var id = await EnqueueAsync("orphan.task");
        var worker = NewWorker(registry);

        await worker.ProcessOnceAsync(default);

        var row = await GetAsync(id);
        row!.Status.Should().Be("dead_letter");
        row.Error.Should().Contain("orphan.task");
        row.Error.Should().Contain("No IPlatformTaskHandler");
    }
}
