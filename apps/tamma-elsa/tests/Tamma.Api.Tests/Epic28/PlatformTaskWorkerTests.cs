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
    public async Task ProcessOnce_NoHandler_RowParkedAsPendingWithUnprocessableAtStamp()
    {
        // Round-2 H8 — first observation of a no-handler row parks it
        // in 'pending' with UnprocessableAt set + RetryCount bumped.
        // It does NOT dead-letter immediately; that happens only after
        // MaxRetries observations.
        var registry = new PlatformTaskHandlerRegistry(Array.Empty<IPlatformTaskHandler>());
        var id = await EnqueueAsync("orphan.task");
        var worker = NewWorker(registry, new PlatformTaskWorkerOptions
        {
            RunOnStartup = false,
            MaxRetries = 5,
        });

        await worker.ProcessOnceAsync(default);

        var row = await GetAsync(id);
        row!.Status.Should().Be(
            "pending",
            "missing handlers are a deploy gap, not a permanent failure — keep the row pending until a deploy ships the handler");
        row.UnprocessableAt.Should().NotBeNull(
            "a no-handler observation must stamp the row so ops can see parked work");
        row.RetryCount.Should().Be(1,
            "the no-handler observation consumes one retry so a permanently-orphan task eventually dead-letters");
        row.Error.Should().Contain("orphan.task");
        row.Error.Should().Contain("No IPlatformTaskHandler");
        row.ClaimedBy.Should().BeNull(
            "ClaimedBy is cleared when the row returns to pending");
    }

    [Test]
    public async Task ProcessOnce_NoHandler_AfterMaxRetries_RowFallsThroughToDeadLetter()
    {
        // After MaxRetries no-handler observations the row finally
        // gives up and dead-letters so the queue doesn't accumulate
        // permanent zombies.
        var registry = new PlatformTaskHandlerRegistry(Array.Empty<IPlatformTaskHandler>());
        var id = await EnqueueAsync("permanently.orphan");
        var worker = NewWorker(registry, new PlatformTaskWorkerOptions
        {
            RunOnStartup = false,
            MaxRetries = 2,
        });

        await worker.ProcessOnceAsync(default);
        await worker.ProcessOnceAsync(default);

        var row = await GetAsync(id);
        row!.Status.Should().Be("dead_letter");
        row.RetryCount.Should().Be(2);
    }

    [Test]
    public async Task ProcessOnce_PersistsWorkerId_OnReservedRow()
    {
        // Round-2 M8 — workerId argument must land in the row's
        // ClaimedBy column so ops can identify the original claimant.
        var handler = new CountingHandler("identity.task");
        var registry = new PlatformTaskHandlerRegistry(new[] { handler });
        var id = await EnqueueAsync("identity.task");
        var worker = NewWorker(registry, new PlatformTaskWorkerOptions
        {
            RunOnStartup = false,
            WorkerId = "agent-d-test-pod",
        });

        // Hold a barrier inside the handler so we can inspect the
        // claimed row mid-processing (when ClaimedBy is set).
        var midProcessTcs = new TaskCompletionSource();
        var releaseTcs = new TaskCompletionSource();
        handler.Behavior = async (_, _) =>
        {
            midProcessTcs.SetResult();
            await releaseTcs.Task.ConfigureAwait(false);
        };

        var processTask = worker.ProcessOnceAsync(default);
        await midProcessTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var midRow = await GetAsync(id);
        midRow!.Status.Should().Be("processing");
        midRow.ClaimedBy.Should().Be("agent-d-test-pod");

        releaseTcs.SetResult();
        await processTask;

        var finalRow = await GetAsync(id);
        finalRow!.Status.Should().Be("completed");
    }

    [Test]
    public async Task ProcessOnce_HandlerReceivesFreshScopedDbContext_PerTick()
    {
        // Round-2 M10 — verify handlers can take scoped dependencies
        // (canonical case: ControlPlaneDbContext). The worker opens an
        // AsyncScope per tick, the registry resolves the handler from
        // that scope, and the handler's scoped DbContext is alive for
        // the duration of HandleAsync.
        var seenDbContexts = new List<ControlPlaneDbContext>();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ControlPlaneDbContext>(o =>
            o.UseInMemoryDatabase(_dbName));
        services.AddScoped<IPlatformQueuedTaskRepository, PlatformQueuedTaskRepository>();
        services.AddScoped<IPlatformTaskHandlerRegistry, PlatformTaskHandlerRegistry>();
        services.AddScoped<IPlatformTaskHandler>(sp =>
        {
            var ctx = sp.GetRequiredService<ControlPlaneDbContext>();
            seenDbContexts.Add(ctx);
            return new CountingHandler("scoped.task");
        });
        var sp = services.BuildServiceProvider();

        await EnqueueAsync("scoped.task");
        await EnqueueAsync("scoped.task");

        var worker = new PlatformTaskWorker(
            sp,
            Options.Create(new PlatformTaskWorkerOptions { RunOnStartup = false }),
            TimeProvider.System,
            NullLogger<PlatformTaskWorker>.Instance);

        await worker.ProcessOnceAsync(default);
        await worker.ProcessOnceAsync(default);

        seenDbContexts.Should().HaveCount(2,
            "each tick resolves a fresh scoped handler");
        ReferenceEquals(seenDbContexts[0], seenDbContexts[1]).Should().BeFalse(
            "scoped DbContext must not be shared across ticks");
    }

    [Test]
    public async Task ExecuteAsync_DoesNothing_WhenRunOnStartupFalse()
    {
        // Round-2 H8 — default RunOnStartup=false means the polling
        // loop never starts, so a queue with no handlers doesn't
        // immediately dead-letter every row.
        var registry = new PlatformTaskHandlerRegistry(Array.Empty<IPlatformTaskHandler>());
        var id = await EnqueueAsync("untouched.task");
        var worker = NewWorker(registry, new PlatformTaskWorkerOptions
        {
            RunOnStartup = false,
        });

        await ((Microsoft.Extensions.Hosting.IHostedService)worker)
            .StartAsync(default);
        // Give the (gated) ExecuteAsync a moment to early-return.
        await Task.Delay(50);
        await ((Microsoft.Extensions.Hosting.IHostedService)worker)
            .StopAsync(default);

        var row = await GetAsync(id);
        row!.Status.Should().Be(
            "pending",
            "RunOnStartup=false must not run the polling loop");
    }
}
