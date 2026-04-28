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
    public async Task ReserveNextAsync_PersistsWorkerId_OnTheRow()
    {
        // Round-2 M8 — workerId must land in the row's ClaimedBy
        // column so ops can identify the original claimant on a stuck
        // row. Previously the parameter was accepted by the API but
        // silently discarded.
        var task = await _repo.EnqueueAsync(NewTask("identity"));

        var claimed = await _repo.ReserveNextAsync("agent-d-test");

        claimed!.ClaimedBy.Should().Be("agent-d-test");
        var stored = await _repo.GetAsync(task.Id);
        stored!.ClaimedBy.Should().Be("agent-d-test");
    }

    [Test]
    public async Task FailAsync_BelowCeiling_ClearsClaimedBy_AlongWithClaimedAt()
    {
        var task = await _repo.EnqueueAsync(NewTask("retry"));
        await _repo.ReserveNextAsync("worker-1");

        var updated = await _repo.FailAsync(task.Id, "transient", maxRetries: 5);

        updated!.ClaimedBy.Should().BeNull(
            "Round-2 M8 — when the row returns to pending, ClaimedBy is cleared so the next claim is unambiguous");
        updated.ClaimedAt.Should().BeNull();
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

    // ── ParkUnprocessableAsync (Round-2 H8) ───────────────────────────────────

    [Test]
    public async Task ParkUnprocessableAsync_KeepsRowPending_StampsTimestamp_BumpsRetryCount()
    {
        var task = await _repo.EnqueueAsync(NewTask("orphan"));
        await _repo.ReserveNextAsync("w");

        var parked = await _repo.ParkUnprocessableAsync(
            task.Id, "no handler", maxRetries: 5);

        parked!.Status.Should().Be("pending",
            "a missing handler is a deploy gap, not a permanent failure");
        parked.UnprocessableAt.Should().NotBeNull();
        parked.RetryCount.Should().Be(1);
        parked.Error.Should().Contain("no handler");
        parked.ClaimedBy.Should().BeNull();
        parked.ClaimedAt.Should().BeNull();
    }

    [Test]
    public async Task ParkUnprocessableAsync_AtCeiling_FallsThroughToDeadLetter()
    {
        var task = await _repo.EnqueueAsync(NewTask("orphan-permanent"));
        await _repo.ReserveNextAsync("w");

        await _repo.ParkUnprocessableAsync(task.Id, "no handler", maxRetries: 2);
        await _repo.ReserveNextAsync("w");
        var terminal = await _repo.ParkUnprocessableAsync(
            task.Id, "still no handler", maxRetries: 2);

        terminal!.Status.Should().Be("dead_letter",
            "after MaxRetries no-handler observations the row finally gives up");
        terminal.RetryCount.Should().Be(2);
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

    [Test]
    public async Task ReapStaleProcessingAsync_ConcurrentInvocations_DoNotDoubleDecrement()
    {
        // Round-2 M9 — two reapers across pods racing the same row
        // must not both bump RetryCount. The InMemory provider is
        // single-threaded so this test asserts the EF path of the
        // naive reaper still increments by exactly one per stale row.
        // The Postgres-native path uses FOR UPDATE SKIP LOCKED in a
        // single UPDATE so two pods each grab disjoint sets — covered
        // by integration tests with a real PG container.
        var t1 = await _repo.EnqueueAsync(NewTask("a"));
        var t2 = await _repo.EnqueueAsync(NewTask("b"));
        await _repo.ReserveNextAsync("w");
        await _repo.ReserveNextAsync("w");

        // Push both claims into the past.
        using (var db = new ControlPlaneDbContext(_options))
        {
            foreach (var t in db.PlatformQueuedTasks)
            {
                t.ClaimedAt = DateTime.UtcNow.AddMinutes(-30);
            }
            await db.SaveChangesAsync();
        }

        var firstRun = await _repo.ReapStaleProcessingAsync(
            TimeSpan.FromMinutes(10), maxRetries: 5);
        firstRun.Should().Be(2);

        var t1After = await _repo.GetAsync(t1.Id);
        var t2After = await _repo.GetAsync(t2.Id);
        t1After!.RetryCount.Should().Be(1);
        t2After!.RetryCount.Should().Be(1);
        t1After.ClaimedBy.Should().BeNull("reaper clears ClaimedBy when row returns to pending");

        // Second run must reap zero — both rows are now pending +
        // their ClaimedAt is null so they don't pass the threshold.
        var secondRun = await _repo.ReapStaleProcessingAsync(
            TimeSpan.FromMinutes(10), maxRetries: 5);
        secondRun.Should().Be(0);
    }
}
