using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Epic28;

/// <summary>
/// Story 28-6 — unit tests for <see cref="PlatformQueuedTaskRepository"/>.
/// Uses the EF InMemory provider; the Postgres SKIP LOCKED reservation
/// path is exercised by integration tests (those use a real PG container)
/// because the in-memory fallback is single-writer by design.
/// </summary>
[TestFixture]
public class PlatformQueuedTaskRepositoryTests
{
    private DbContextOptions<ControlPlaneDbContext> _options = null!;
    private ControlPlaneDbContext _db = null!;
    private PlatformQueuedTaskRepository _repo = null!;

    [SetUp]
    public void SetUp()
    {
        _options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _db = new ControlPlaneDbContext(_options);
        _repo = new PlatformQueuedTaskRepository(_db);
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    private static PlatformQueuedTask NewTask(string type = "TENANT.PROVISION") => new()
    {
        Type = type,
        Payload = "{}",
    };

    // ── EnqueueAsync ──────────────────────────────────────────────────────────

    [Test]
    public async Task EnqueueAsync_AssignsId_DefaultsToPending()
    {
        var t = await _repo.EnqueueAsync(NewTask());

        t.Id.Should().NotBe(Guid.Empty);
        t.Status.Should().Be("pending");
        t.RetryCount.Should().Be(0);
        t.Error.Should().BeNull();
        t.ClaimedAt.Should().BeNull();
        t.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Test]
    public void EnqueueAsync_RejectsEmptyType()
    {
        var act = async () => await _repo.EnqueueAsync(new PlatformQueuedTask { Type = "" });
        act.Should().ThrowAsync<ArgumentException>();
    }

    // ── ReserveNextAsync ──────────────────────────────────────────────────────

    [Test]
    public async Task ReserveNextAsync_PicksOldestPending_FlipsToProcessing()
    {
        var first = await _repo.EnqueueAsync(NewTask("a"));
        await Task.Delay(5);
        var second = await _repo.EnqueueAsync(NewTask("b"));

        var claimed = await _repo.ReserveNextAsync("worker-1");

        claimed.Should().NotBeNull();
        claimed!.Id.Should().Be(first.Id);
        claimed.Status.Should().Be("processing");
        claimed.ClaimedAt.Should().NotBeNull();
    }

    [Test]
    public async Task ReserveNextAsync_ReturnsNull_WhenQueueEmpty()
    {
        var claimed = await _repo.ReserveNextAsync("worker-1");
        claimed.Should().BeNull();
    }

    [Test]
    public async Task ReserveNextAsync_SecondReserveReturnsDifferentRow_OnInMemoryProvider()
    {
        var t1 = await _repo.EnqueueAsync(NewTask("a"));
        var t2 = await _repo.EnqueueAsync(NewTask("b"));

        var first = await _repo.ReserveNextAsync("worker-1");
        var second = await _repo.ReserveNextAsync("worker-1");

        first!.Id.Should().Be(t1.Id);
        second!.Id.Should().Be(t2.Id, "the second reserve must skip the row already in 'processing'");
    }

    [Test]
    public void ReserveNextAsync_RejectsBlankWorkerId()
    {
        var act = async () => await _repo.ReserveNextAsync("");
        act.Should().ThrowAsync<ArgumentException>();
    }

    // ── CompleteAsync ─────────────────────────────────────────────────────────

    [Test]
    public async Task CompleteAsync_FlipsStatus_ClearsError()
    {
        var t = await _repo.EnqueueAsync(NewTask());
        await _repo.ReserveNextAsync("w");

        await _repo.CompleteAsync(t.Id);

        var stored = await _repo.GetAsync(t.Id);
        stored!.Status.Should().Be("completed");
        stored.Error.Should().BeNull();
    }

    [Test]
    public async Task CompleteAsync_NoOp_ForUnknownId()
    {
        var act = async () => await _repo.CompleteAsync(Guid.NewGuid());
        await act.Should().NotThrowAsync();
    }

    // ── FailAsync ─────────────────────────────────────────────────────────────

    [Test]
    public async Task FailAsync_UnderCeiling_RequeuesPending_IncrementsRetry()
    {
        var t = await _repo.EnqueueAsync(NewTask());
        await _repo.ReserveNextAsync("w");

        var updated = await _repo.FailAsync(t.Id, "transient", maxRetries: 5);

        updated!.Status.Should().Be("pending");
        updated.RetryCount.Should().Be(1);
        updated.Error.Should().Be("transient");
        updated.ClaimedAt.Should().BeNull("requeued tasks must not carry the prior claim timestamp");
    }

    [Test]
    public async Task FailAsync_AtCeiling_FlipsToDeadLetter()
    {
        var t = await _repo.EnqueueAsync(NewTask());
        await _repo.ReserveNextAsync("w");

        await _repo.FailAsync(t.Id, "err1", maxRetries: 3);
        await _repo.FailAsync(t.Id, "err2", maxRetries: 3);
        var terminal = await _repo.FailAsync(t.Id, "err3", maxRetries: 3);

        terminal!.Status.Should().Be("dead_letter");
        terminal.RetryCount.Should().Be(3);
        terminal.Error.Should().Be("err3");
    }

    [Test]
    public async Task FailAsync_ReturnsNull_ForUnknownId()
    {
        var updated = await _repo.FailAsync(Guid.NewGuid(), "x", 3);
        updated.Should().BeNull();
    }

    // ── DeadLetterAsync ───────────────────────────────────────────────────────

    [Test]
    public async Task DeadLetterAsync_FlipsStatus_RecordsError()
    {
        var t = await _repo.EnqueueAsync(NewTask());

        await _repo.DeadLetterAsync(t.Id, "no handler");

        var stored = await _repo.GetAsync(t.Id);
        stored!.Status.Should().Be("dead_letter");
        stored.Error.Should().Be("no handler");
    }

    // ── ReapStaleProcessingAsync ──────────────────────────────────────────────

    [Test]
    public async Task ReapStaleProcessingAsync_RequeuesStaleRows_BelowCeiling()
    {
        var t = await _repo.EnqueueAsync(NewTask());
        await _repo.ReserveNextAsync("w");

        // Push the claim timestamp into the past so the reaper sees it stale.
        using (var db = new ControlPlaneDbContext(_options))
        {
            var row = await db.PlatformQueuedTasks.FindAsync(t.Id);
            row!.ClaimedAt = DateTime.UtcNow.AddMinutes(-30);
            await db.SaveChangesAsync();
        }

        var reaped = await _repo.ReapStaleProcessingAsync(
            visibilityTimeout: TimeSpan.FromMinutes(10), maxRetries: 5);

        reaped.Should().Be(1);
        var stored = await _repo.GetAsync(t.Id);
        stored!.Status.Should().Be("pending");
        stored.RetryCount.Should().Be(1);
        stored.ClaimedAt.Should().BeNull();
    }

    [Test]
    public async Task ReapStaleProcessingAsync_ReturnsZero_WhenNothingStale()
    {
        await _repo.EnqueueAsync(NewTask());
        var reaped = await _repo.ReapStaleProcessingAsync(TimeSpan.FromMinutes(10), 5);
        reaped.Should().Be(0);
    }
}
