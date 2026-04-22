using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Tamma.Api.Tests.Infrastructure;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.TaskQueue;

/// <summary>
/// Exercises the pure persistence port for the task queue. Uses the EF Core
/// InMemory provider — transitions and FIFO ordering are provider-independent
/// and are separately validated against Postgres in
/// <see cref="GitHubWebhookTaskQueueIntegrationTests"/>.
/// </summary>
[TestFixture]
public class QueuedTaskRepositoryTests
{
    private InMemoryDbFixture _fx = null!;
    private ControlPlaneDbContext _db = null!;
    private QueuedTaskRepository _repo = null!;

    [SetUp]
    public void SetUp()
    {
        _fx = new InMemoryDbFixture();
        _db = _fx.Cp;
        _repo = new QueuedTaskRepository(_fx.Factory, _db);
    }

    [TearDown]
    public async Task TearDown() => await _fx.DisposeAsync();

    // ── Enqueue ───────────────────────────────────────────────────────────────

    [Test]
    public async Task EnqueueAsync_AssignsIdAndTimestamps_AndDefaultsToPending()
    {
        var task = await _repo.EnqueueAsync(new QueuedTask
        {
            Type = "github.push.main",
            TenantId = Guid.NewGuid(),
            InstallationId = 4242,
            Payload = "{\"a\":1}"
        });

        task.Id.Should().NotBe(Guid.Empty);
        task.Status.Should().Be("pending");
        task.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        task.UpdatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        task.RetryCount.Should().Be(0);
        task.Error.Should().BeNull();

        var stored = await _db.QueuedTasks.FindAsync(task.Id);
        stored.Should().NotBeNull();
        stored!.Type.Should().Be("github.push.main");
        stored.Payload.Should().Be("{\"a\":1}");
    }

    [Test]
    public async Task EnqueueAsync_AllowsNullTenantAndInstallation()
    {
        var task = await _repo.EnqueueAsync(new QueuedTask
        {
            Type = "system.cleanup",
            TenantId = null,
            InstallationId = null,
            Payload = "{}"
        });

        task.TenantId.Should().BeNull();
        task.InstallationId.Should().BeNull();
    }

    // ── ListPending ───────────────────────────────────────────────────────────

    [Test]
    public async Task ListPendingAsync_ReturnsOnlyPending_InCreationOrder()
    {
        var t1 = await _repo.EnqueueAsync(new QueuedTask { Type = "a" });
        await Task.Delay(5);
        var t2 = await _repo.EnqueueAsync(new QueuedTask { Type = "b" });
        await Task.Delay(5);
        var t3 = await _repo.EnqueueAsync(new QueuedTask { Type = "c" });

        await _repo.MarkProcessingAsync(t2.Id);
        await _repo.MarkCompletedAsync(t2.Id);

        var pending = await _repo.ListPendingAsync(null, limit: 10);

        pending.Select(t => t.Id).Should().ContainInOrder(t1.Id, t3.Id);
        pending.Should().NotContain(t => t.Id == t2.Id);
    }

    [Test]
    public async Task ListPendingAsync_FiltersByTenant_WhenSupplied()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        var a1 = await _repo.EnqueueAsync(new QueuedTask { Type = "a1", TenantId = tenantA });
        var b1 = await _repo.EnqueueAsync(new QueuedTask { Type = "b1", TenantId = tenantB });
        var a2 = await _repo.EnqueueAsync(new QueuedTask { Type = "a2", TenantId = tenantA });

        var onlyA = await _repo.ListPendingAsync(tenantA, limit: 10);

        onlyA.Select(t => t.Id).Should().BeEquivalentTo(new[] { a1.Id, a2.Id });
        onlyA.Should().NotContain(t => t.Id == b1.Id);
    }

    [Test]
    public async Task ListPendingAsync_RespectsLimit()
    {
        await _repo.EnqueueAsync(new QueuedTask { Type = "a" });
        await _repo.EnqueueAsync(new QueuedTask { Type = "b" });
        await _repo.EnqueueAsync(new QueuedTask { Type = "c" });

        var pending = await _repo.ListPendingAsync(null, limit: 2);
        pending.Should().HaveCount(2);
    }

    // ── MarkProcessing ────────────────────────────────────────────────────────

    [Test]
    public async Task MarkProcessingAsync_FlipsPendingToProcessing()
    {
        var t = await _repo.EnqueueAsync(new QueuedTask { Type = "x" });

        var claimed = await _repo.MarkProcessingAsync(t.Id);

        claimed.Should().NotBeNull();
        claimed!.Status.Should().Be("processing");
        claimed.UpdatedAt.Should().BeAfter(t.CreatedAt.AddTicks(-1));
    }

    [Test]
    public async Task MarkProcessingAsync_ReturnsNull_WhenAlreadyClaimed()
    {
        var t = await _repo.EnqueueAsync(new QueuedTask { Type = "x" });
        await _repo.MarkProcessingAsync(t.Id);

        var second = await _repo.MarkProcessingAsync(t.Id);

        second.Should().BeNull();
    }

    [Test]
    public async Task MarkProcessingAsync_ReturnsNull_ForUnknownId()
        => (await _repo.MarkProcessingAsync(Guid.NewGuid())).Should().BeNull();

    // ── MarkCompleted ─────────────────────────────────────────────────────────

    [Test]
    public async Task MarkCompletedAsync_FlipsStatus_AndLeavesErrorNull()
    {
        var t = await _repo.EnqueueAsync(new QueuedTask { Type = "x" });
        await _repo.MarkProcessingAsync(t.Id);
        await _repo.MarkCompletedAsync(t.Id);

        var stored = await _repo.GetAsync(t.Id);
        stored!.Status.Should().Be("completed");
        stored.Error.Should().BeNull();
    }

    // ── MarkFailed ────────────────────────────────────────────────────────────

    [Test]
    public async Task MarkFailedAsync_StoresErrorAndFlipsStatus()
    {
        var t = await _repo.EnqueueAsync(new QueuedTask { Type = "x" });
        await _repo.MarkProcessingAsync(t.Id);

        await _repo.MarkFailedAsync(t.Id, "boom");

        var stored = await _repo.GetAsync(t.Id);
        stored!.Status.Should().Be("failed");
        stored.Error.Should().Be("boom");
    }

    // ── IncrementRetryAndRequeue ─────────────────────────────────────────────

    [Test]
    public async Task IncrementRetryAndRequeueAsync_BumpsCount_AndResetsToPending()
    {
        var t = await _repo.EnqueueAsync(new QueuedTask { Type = "x" });
        await _repo.MarkProcessingAsync(t.Id);

        var requeued = await _repo.IncrementRetryAndRequeueAsync(t.Id, "transient");

        requeued.Should().NotBeNull();
        requeued!.Status.Should().Be("pending");
        requeued.RetryCount.Should().Be(1);
        requeued.Error.Should().Be("transient");
    }
}
