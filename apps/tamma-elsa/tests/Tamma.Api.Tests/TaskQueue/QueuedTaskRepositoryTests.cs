using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Tamma.Api.Tests.Infrastructure;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.TaskQueue;

/// <summary>
/// Exercises the pure persistence port for the per-tenant task queue.
/// Uses the EF Core InMemory provider — transitions and FIFO ordering
/// are provider-independent and are separately validated against
/// Postgres in <see cref="GitHubWebhookTaskQueueIntegrationTests"/>.
///
/// <para>Story 28-1 PR B — every test now operates against a fixed
/// tenant id; the repo is strictly tenant-scoped post-PR-B and every
/// operation requires the tenant id explicitly. The
/// <c>FromAnyTenant</c> drain path is covered separately at the
/// bottom.</para>
/// </summary>
[TestFixture]
public class QueuedTaskRepositoryTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-1111-1111-1111-aaaaaaaaaaaa");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-2222-2222-2222-bbbbbbbbbbbb");

    private InMemoryDbFixture _fx = null!;
    private ControlPlaneDbContext _db = null!;
    private QueuedTaskRepository _repo = null!;

    [SetUp]
    public void SetUp()
    {
        _fx = new InMemoryDbFixture();
        _db = _fx.Cp;
        _repo = new QueuedTaskRepository(_fx.Factory, _db);

        // Seed two active tenants so cross-tenant drain has work to walk.
        _db.Tenants.AddRange(
            new Tenant { Id = TenantA, Name = "A", Slug = "a", Type = "personal", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new Tenant { Id = TenantB, Name = "B", Slug = "b", Type = "personal", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        _db.SaveChanges();
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
            TenantId = TenantA,
            InstallationId = 4242,
            Payload = "{\"a\":1}"
        });

        task.Id.Should().NotBe(Guid.Empty);
        task.Status.Should().Be("pending");
        task.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        task.UpdatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        task.RetryCount.Should().Be(0);
        task.Error.Should().BeNull();

        // Story 28-1 PR D — queued_tasks live on the tenant DB.
        await using var tdb = await _fx.Factory.CreateAsync(task.TenantId!.Value);
        var stored = await tdb.QueuedTasks.FindAsync(task.Id);
        stored.Should().NotBeNull();
        stored!.Type.Should().Be("github.push.main");
        stored.Payload.Should().Be("{\"a\":1}");
    }

    [Test]
    public async Task EnqueueAsync_ThrowsWhenTenantIdNull()
    {
        var act = async () => await _repo.EnqueueAsync(new QueuedTask
        {
            Type = "system.cleanup",
            TenantId = null,
            InstallationId = null,
            Payload = "{}"
        });

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*TenantId*");
    }

    // ── ListPending ───────────────────────────────────────────────────────────

    [Test]
    public async Task ListPendingAsync_ReturnsOnlyPending_InCreationOrder()
    {
        var t1 = await _repo.EnqueueAsync(new QueuedTask { Type = "a", TenantId = TenantA });
        await Task.Delay(5);
        var t2 = await _repo.EnqueueAsync(new QueuedTask { Type = "b", TenantId = TenantA });
        await Task.Delay(5);
        var t3 = await _repo.EnqueueAsync(new QueuedTask { Type = "c", TenantId = TenantA });

        await _repo.MarkProcessingAsync(TenantA, t2.Id);
        await _repo.MarkCompletedAsync(TenantA, t2.Id);

        var pending = await _repo.ListPendingAsync(TenantA, limit: 10);

        pending.Select(t => t.Id).Should().ContainInOrder(t1.Id, t3.Id);
        pending.Should().NotContain(t => t.Id == t2.Id);
    }

    [Test]
    public async Task ListPendingAsync_FiltersByTenant()
    {
        var a1 = await _repo.EnqueueAsync(new QueuedTask { Type = "a1", TenantId = TenantA });
        var b1 = await _repo.EnqueueAsync(new QueuedTask { Type = "b1", TenantId = TenantB });
        var a2 = await _repo.EnqueueAsync(new QueuedTask { Type = "a2", TenantId = TenantA });

        var onlyA = await _repo.ListPendingAsync(TenantA, limit: 10);

        onlyA.Select(t => t.Id).Should().BeEquivalentTo(new[] { a1.Id, a2.Id });
        onlyA.Should().NotContain(t => t.Id == b1.Id);
    }

    [Test]
    public async Task ListPendingAsync_RespectsLimit()
    {
        await _repo.EnqueueAsync(new QueuedTask { Type = "a", TenantId = TenantA });
        await _repo.EnqueueAsync(new QueuedTask { Type = "b", TenantId = TenantA });
        await _repo.EnqueueAsync(new QueuedTask { Type = "c", TenantId = TenantA });

        var pending = await _repo.ListPendingAsync(TenantA, limit: 2);
        pending.Should().HaveCount(2);
    }

    // ── ListPendingFromAnyTenantAsync (Story 28-1 PR B) ──────────────────────

    [Test]
    public async Task ListPendingFromAnyTenantAsync_AggregatesAcrossActiveTenants()
    {
        var a1 = await _repo.EnqueueAsync(new QueuedTask { Type = "a1", TenantId = TenantA });
        var b1 = await _repo.EnqueueAsync(new QueuedTask { Type = "b1", TenantId = TenantB });

        var aggregate = await _repo.ListPendingFromAnyTenantAsync(batchSizePerTenant: 10);

        aggregate.Select(t => t.Id).Should().Contain(new[] { a1.Id, b1.Id });
    }

    [Test]
    public async Task ListPendingFromAnyTenantAsync_RespectsBatchSizePerTenant()
    {
        for (int i = 0; i < 5; i++)
        {
            await _repo.EnqueueAsync(new QueuedTask { Type = $"a{i}", TenantId = TenantA });
            await _repo.EnqueueAsync(new QueuedTask { Type = $"b{i}", TenantId = TenantB });
        }

        var aggregate = await _repo.ListPendingFromAnyTenantAsync(batchSizePerTenant: 2);

        // 2 per tenant × 2 tenants = 4 rows total.
        aggregate.Should().HaveCount(4);
    }

    [Test]
    public async Task ListPendingFromAnyTenantAsync_IgnoresSoftDeletedTenants()
    {
        await _repo.EnqueueAsync(new QueuedTask { Type = "a1", TenantId = TenantA });
        var b1 = await _repo.EnqueueAsync(new QueuedTask { Type = "b1", TenantId = TenantB });

        var b = await _db.Tenants.FindAsync(TenantB);
        b!.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var aggregate = await _repo.ListPendingFromAnyTenantAsync(batchSizePerTenant: 10);

        aggregate.Select(t => t.Id).Should().NotContain(b1.Id);
    }

    // ── MarkProcessing ────────────────────────────────────────────────────────

    [Test]
    public async Task MarkProcessingAsync_FlipsPendingToProcessing()
    {
        var t = await _repo.EnqueueAsync(new QueuedTask { Type = "x", TenantId = TenantA });

        var claimed = await _repo.MarkProcessingAsync(TenantA, t.Id);

        claimed.Should().NotBeNull();
        claimed!.Status.Should().Be("processing");
        claimed.UpdatedAt.Should().BeAfter(t.CreatedAt.AddTicks(-1));
    }

    [Test]
    public async Task MarkProcessingAsync_ReturnsNull_WhenAlreadyClaimed()
    {
        var t = await _repo.EnqueueAsync(new QueuedTask { Type = "x", TenantId = TenantA });
        await _repo.MarkProcessingAsync(TenantA, t.Id);

        var second = await _repo.MarkProcessingAsync(TenantA, t.Id);

        second.Should().BeNull();
    }

    [Test]
    public async Task MarkProcessingAsync_ReturnsNull_ForUnknownId()
        => (await _repo.MarkProcessingAsync(TenantA, Guid.NewGuid())).Should().BeNull();

    // ── MarkCompleted ─────────────────────────────────────────────────────────

    [Test]
    public async Task MarkCompletedAsync_FlipsStatus_AndLeavesErrorNull()
    {
        var t = await _repo.EnqueueAsync(new QueuedTask { Type = "x", TenantId = TenantA });
        await _repo.MarkProcessingAsync(TenantA, t.Id);
        await _repo.MarkCompletedAsync(TenantA, t.Id);

        var stored = await _repo.GetAsync(TenantA, t.Id);
        stored!.Status.Should().Be("completed");
        stored.Error.Should().BeNull();
    }

    // ── MarkFailed ────────────────────────────────────────────────────────────

    [Test]
    public async Task MarkFailedAsync_StoresErrorAndFlipsStatus()
    {
        var t = await _repo.EnqueueAsync(new QueuedTask { Type = "x", TenantId = TenantA });
        await _repo.MarkProcessingAsync(TenantA, t.Id);

        await _repo.MarkFailedAsync(TenantA, t.Id, "boom");

        var stored = await _repo.GetAsync(TenantA, t.Id);
        stored!.Status.Should().Be("failed");
        stored.Error.Should().Be("boom");
    }

    // ── IncrementRetryAndRequeue ─────────────────────────────────────────────

    [Test]
    public async Task IncrementRetryAndRequeueAsync_BumpsCount_AndResetsToPending()
    {
        var t = await _repo.EnqueueAsync(new QueuedTask { Type = "x", TenantId = TenantA });
        await _repo.MarkProcessingAsync(TenantA, t.Id);

        var requeued = await _repo.IncrementRetryAndRequeueAsync(TenantA, t.Id, "transient");

        requeued.Should().NotBeNull();
        requeued!.Status.Should().Be("pending");
        requeued.RetryCount.Should().Be(1);
        requeued.Error.Should().Be("transient");
    }
}
