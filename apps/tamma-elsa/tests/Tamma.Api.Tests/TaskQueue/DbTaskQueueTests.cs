using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.TaskQueue;
using Tamma.Api.Tests.Infrastructure;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.TaskQueue;

/// <summary>
/// Tenant-isolation + FIFO contract for <see cref="DbTaskQueue"/>. Uses the
/// EF Core InMemory provider behind a real repository so the service's tenant
/// scoping is exercised against actual persistence, not a repo mock.
///
/// <para>Story 28-1 PR B — DbTaskQueue is now strictly tenant-scoped.
/// Tests assert that calling EnqueueAsync without an ambient tenant
/// throws (callers MUST use IPlatformQueuedTaskRepository for
/// platform-scope work).</para>
/// </summary>
[TestFixture]
public class DbTaskQueueTests
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

    private static DbTaskQueue NewQueue(QueuedTaskRepository repo, Guid? tenantId)
    {
        var context = new Mock<ITenantContext>();
        context.SetupGet(c => c.TenantId).Returns(tenantId);
        return new DbTaskQueue(repo, context.Object);
    }

    private void SeedTenant(Guid tenantId)
    {
        if (_db.Tenants.Find(tenantId) is null)
        {
            _db.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Name = tenantId.ToString()[..8],
                Slug = "t-" + tenantId.ToString()[..8],
                Type = "personal",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            _db.SaveChanges();
        }
    }

    // ─── Enqueue + ambient tenant ─────────────────────────────────────────────

    [Test]
    public async Task EnqueueAsync_StampsAmbientTenant_WhenNoOverride()
    {
        var tenantA = Guid.NewGuid();
        SeedTenant(tenantA);
        var queue = NewQueue(_repo, tenantA);

        var task = await queue.EnqueueAsync("x", "{}");

        task.TenantId.Should().Be(tenantA);
    }

    [Test]
    public async Task EnqueueAsync_UsesOverride_WhenSupplied()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        SeedTenant(tenantA);
        SeedTenant(tenantB);
        var queue = NewQueue(_repo, tenantA);

        var task = await queue.EnqueueAsync(
            "x", "{}", installationId: 99, tenantIdOverride: tenantB);

        task.TenantId.Should().Be(tenantB);
        task.InstallationId.Should().Be(99);
    }

    [Test]
    public async Task EnqueueAsync_ThrowsWhenAmbientTenantNull()
    {
        // Story 28-1 PR B — DbTaskQueue is strictly tenant-scoped now.
        // Platform-scope callers must use IPlatformQueuedTaskRepository.
        var queue = NewQueue(_repo, null);

        var act = async () => await queue.EnqueueAsync("x", "{}");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*tenant*");
    }

    // ─── Tenant isolation ────────────────────────────────────────────────────

    [Test]
    public async Task ListPendingAsync_TenantACannotSeeTenantBTasks()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        SeedTenant(tenantA);
        SeedTenant(tenantB);

        await NewQueue(_repo, tenantA).EnqueueAsync("a1", "{}");
        await NewQueue(_repo, tenantA).EnqueueAsync("a2", "{}");
        await NewQueue(_repo, tenantB).EnqueueAsync("b1", "{}");

        var aPending = await NewQueue(_repo, tenantA).ListPendingAsync(tenantA);
        var bPending = await NewQueue(_repo, tenantB).ListPendingAsync(tenantB);

        aPending.Select(t => t.Type).Should().BeEquivalentTo(new[] { "a1", "a2" });
        bPending.Select(t => t.Type).Should().BeEquivalentTo(new[] { "b1" });
    }

    // ─── FIFO within a tenant ─────────────────────────────────────────────────

    [Test]
    public async Task ListPendingAsync_ReturnsTasksInCreationOrder()
    {
        var tenant = Guid.NewGuid();
        SeedTenant(tenant);
        var queue = NewQueue(_repo, tenant);

        var first = await queue.EnqueueAsync("first", "{}");
        await Task.Delay(5);
        var second = await queue.EnqueueAsync("second", "{}");
        await Task.Delay(5);
        var third = await queue.EnqueueAsync("third", "{}");

        var pending = await queue.ListPendingAsync(tenant);

        pending.Select(t => t.Id).Should().ContainInOrder(first.Id, second.Id, third.Id);
    }

    // ─── Get/Mark transitions ─────────────────────────────────────────────────

    [Test]
    public async Task GetAsync_ReturnsTaskByTenantAndId()
    {
        var tenantA = Guid.NewGuid();
        SeedTenant(tenantA);
        var enqueued = await NewQueue(_repo, tenantA).EnqueueAsync("a1", "{}");

        var fetched = await NewQueue(_repo, tenantA).GetAsync(tenantA, enqueued.Id);

        fetched.Should().NotBeNull();
        fetched!.TenantId.Should().Be(tenantA);
    }

    [Test]
    public async Task MarkProcessing_ThenCompleted_ClearsFromPendingList()
    {
        var tenant = Guid.NewGuid();
        SeedTenant(tenant);
        var queue = NewQueue(_repo, tenant);
        var t = await queue.EnqueueAsync("x", "{}");

        await queue.MarkProcessingAsync(tenant, t.Id);
        await queue.MarkCompletedAsync(tenant, t.Id);

        var pending = await queue.ListPendingAsync(tenant);
        pending.Should().BeEmpty();
    }

    [Test]
    public async Task MarkFailed_StoresErrorString()
    {
        var tenant = Guid.NewGuid();
        SeedTenant(tenant);
        var queue = NewQueue(_repo, tenant);
        var t = await queue.EnqueueAsync("x", "{}");
        await queue.MarkProcessingAsync(tenant, t.Id);

        await queue.MarkFailedAsync(tenant, t.Id, "nope");

        var stored = await queue.GetAsync(tenant, t.Id);
        stored!.Status.Should().Be("failed");
        stored.Error.Should().Be("nope");
    }
}
