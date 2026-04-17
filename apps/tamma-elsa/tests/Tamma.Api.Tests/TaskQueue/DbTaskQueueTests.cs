using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.TaskQueue;
using Tamma.Api.Tests.Infrastructure;
using Tamma.Data;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.TaskQueue;

/// <summary>
/// Tenant-isolation + FIFO contract for <see cref="DbTaskQueue"/>. Uses the
/// EF Core InMemory provider behind a real repository so the service's tenant
/// scoping is exercised against actual persistence, not a repo mock.
/// </summary>
[TestFixture]
public class DbTaskQueueTests
{
    private TammaDbContext _db = null!;
    private QueuedTaskRepository _repo = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<TammaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _db = new TestDbContext(options);
        _repo = new QueuedTaskRepository(_db);
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    private static DbTaskQueue NewQueue(QueuedTaskRepository repo, Guid? tenantId)
    {
        var context = new Mock<ITenantContext>();
        context.SetupGet(c => c.TenantId).Returns(tenantId);
        return new DbTaskQueue(repo, context.Object);
    }

    // ─── Enqueue + ambient tenant ─────────────────────────────────────────────

    [Test]
    public async Task EnqueueAsync_StampsAmbientTenant_WhenNoOverride()
    {
        var tenantA = Guid.NewGuid();
        var queue = NewQueue(_repo, tenantA);

        var task = await queue.EnqueueAsync("x", "{}");

        task.TenantId.Should().Be(tenantA);
    }

    [Test]
    public async Task EnqueueAsync_UsesOverride_WhenSupplied()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var queue = NewQueue(_repo, tenantA);

        var task = await queue.EnqueueAsync(
            "x", "{}", installationId: 99, tenantIdOverride: tenantB);

        task.TenantId.Should().Be(tenantB);
        task.InstallationId.Should().Be(99);
    }

    [Test]
    public async Task EnqueueAsync_AllowsNullAmbientTenant()
    {
        var queue = NewQueue(_repo, null);

        var task = await queue.EnqueueAsync("x", "{}");

        task.TenantId.Should().BeNull();
    }

    // ─── Tenant isolation ────────────────────────────────────────────────────

    [Test]
    public async Task ListPendingAsync_TenantACannotSeeTenantBTasks()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        await NewQueue(_repo, tenantA).EnqueueAsync("a1", "{}");
        await NewQueue(_repo, tenantA).EnqueueAsync("a2", "{}");
        await NewQueue(_repo, tenantB).EnqueueAsync("b1", "{}");

        var aPending = await NewQueue(_repo, tenantA).ListPendingAsync();
        var bPending = await NewQueue(_repo, tenantB).ListPendingAsync();

        aPending.Select(t => t.Type).Should().BeEquivalentTo(new[] { "a1", "a2" });
        bPending.Select(t => t.Type).Should().BeEquivalentTo(new[] { "b1" });
    }

    [Test]
    public async Task ListPendingAsync_NullTenant_ReturnsAllTenants()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        await NewQueue(_repo, tenantA).EnqueueAsync("a1", "{}");
        await NewQueue(_repo, tenantB).EnqueueAsync("b1", "{}");

        var all = await NewQueue(_repo, null).ListPendingAsync();

        all.Select(t => t.Type).Should().BeEquivalentTo(new[] { "a1", "b1" });
    }

    // ─── FIFO within a tenant ─────────────────────────────────────────────────

    [Test]
    public async Task ListPendingAsync_ReturnsTasksInCreationOrder()
    {
        var tenant = Guid.NewGuid();
        var queue = NewQueue(_repo, tenant);

        var first = await queue.EnqueueAsync("first", "{}");
        await Task.Delay(5);
        var second = await queue.EnqueueAsync("second", "{}");
        await Task.Delay(5);
        var third = await queue.EnqueueAsync("third", "{}");

        var pending = await queue.ListPendingAsync();

        pending.Select(t => t.Id).Should().ContainInOrder(first.Id, second.Id, third.Id);
    }

    // ─── Get/Mark transitions ─────────────────────────────────────────────────

    [Test]
    public async Task GetAsync_ReturnsTaskAcrossTenants()
    {
        // Processor (null tenant) must be able to read any tenant's task.
        var tenantA = Guid.NewGuid();
        var enqueued = await NewQueue(_repo, tenantA).EnqueueAsync("a1", "{}");

        var fetched = await NewQueue(_repo, null).GetAsync(enqueued.Id);

        fetched.Should().NotBeNull();
        fetched!.TenantId.Should().Be(tenantA);
    }

    [Test]
    public async Task MarkProcessing_ThenCompleted_ClearsFromPendingList()
    {
        var tenant = Guid.NewGuid();
        var queue = NewQueue(_repo, tenant);
        var t = await queue.EnqueueAsync("x", "{}");

        await queue.MarkProcessingAsync(t.Id);
        await queue.MarkCompletedAsync(t.Id);

        var pending = await queue.ListPendingAsync();
        pending.Should().BeEmpty();
    }

    [Test]
    public async Task MarkFailed_StoresErrorString()
    {
        var queue = NewQueue(_repo, Guid.NewGuid());
        var t = await queue.EnqueueAsync("x", "{}");
        await queue.MarkProcessingAsync(t.Id);

        await queue.MarkFailedAsync(t.Id, "nope");

        var stored = await queue.GetAsync(t.Id);
        stored!.Status.Should().Be("failed");
        stored.Error.Should().Be("nope");
    }
}
