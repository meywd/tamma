using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Tamma.Api.Tests.Infrastructure;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Email;

/// <summary>
/// Unit tests for the email-outbox persistence port. Uses the EF Core InMemory
/// provider for isolation — the <c>FOR UPDATE SKIP LOCKED</c> Postgres path is
/// separately covered by integration tests in
/// <see cref="AuthRegisterTxnIdIntegrationTests"/>.
/// </summary>
[TestFixture]
public class EmailOutboxRepositoryTests
{
    private ControlPlaneDbContext _db = null!;
    private EmailOutboxRepository _repo = null!;

    [SetUp]
    public void SetUp()
    {
        var dbName = Guid.NewGuid().ToString();
        var cpOptions = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var tenantOptions = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _db = new TestControlPlaneDbContext(cpOptions);
        var factory = new TestTenantDbContextFactory(tenantOptions);
        _repo = new EmailOutboxRepository(factory, _db);
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    private static EmailOutboxMessage NewMessage(string template = "verification")
        => new()
        {
            Template = template,
            ToAddress = "user@example.com",
            Subject = "Verify your email",
            HtmlBody = "<p>hi</p>",
            TextBody = "hi",
            FromAddress = "noreply@tamma.dev",
            MaxAttempts = 5,
        };

    // ── Enqueue ──

    [Test]
    public async Task EnqueueAsync_AssignsId_PendingStatus_AndTimestamps()
    {
        var msg = await _repo.EnqueueAsync(NewMessage());

        msg.Id.Should().NotBe(Guid.Empty);
        msg.Status.Should().Be("pending");
        msg.Attempts.Should().Be(0);
        msg.LastError.Should().BeNull();
        msg.SentAt.Should().BeNull();
        msg.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        msg.UpdatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        msg.NextAttemptAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));

        var stored = await _db.EmailOutbox.FindAsync(msg.Id);
        stored.Should().NotBeNull();
        stored!.Template.Should().Be("verification");
    }

    [Test]
    public async Task EnqueueAsync_DefaultsMaxAttemptsWhenZero()
    {
        var seed = NewMessage();
        seed.MaxAttempts = 0;

        var msg = await _repo.EnqueueAsync(seed);

        msg.MaxAttempts.Should().Be(5);
    }

    // ── ClaimNextPendingAsync ──

    [Test]
    public async Task ClaimNextPendingAsync_ReturnsNull_WhenNothingPending()
    {
        var claim = await _repo.ClaimNextPendingAsync(DateTime.UtcNow);
        claim.Should().BeNull();
    }

    [Test]
    public async Task ClaimNextPendingAsync_FlipsRowToSending()
    {
        var msg = await _repo.EnqueueAsync(NewMessage());

        var claimed = await _repo.ClaimNextPendingAsync(DateTime.UtcNow);

        claimed.Should().NotBeNull();
        claimed!.Id.Should().Be(msg.Id);

        var stored = await _repo.GetByIdAsync(msg.Id);
        stored!.Status.Should().Be("sending");
    }

    [Test]
    public async Task ClaimNextPendingAsync_SkipsRowsNotYetDue()
    {
        var msg = await _repo.EnqueueAsync(NewMessage());
        // Schedule far in the future so current-time claims can't grab it.
        msg.NextAttemptAt = DateTime.UtcNow.AddHours(1);
        await _db.SaveChangesAsync();

        var claim = await _repo.ClaimNextPendingAsync(DateTime.UtcNow);

        claim.Should().BeNull("row is not due yet — NextAttemptAt > now");
    }

    [Test]
    public async Task ClaimNextPendingAsync_PicksOldestDueFirst()
    {
        var first = await _repo.EnqueueAsync(NewMessage("first"));
        await Task.Delay(10);
        var second = await _repo.EnqueueAsync(NewMessage("second"));

        var claim = await _repo.ClaimNextPendingAsync(DateTime.UtcNow);

        claim!.Id.Should().Be(first.Id, "FIFO — the earlier NextAttemptAt wins");
    }

    [Test]
    public async Task ClaimNextPendingAsync_SkipsAlreadyClaimedRows()
    {
        var first = await _repo.EnqueueAsync(NewMessage("first"));
        var second = await _repo.EnqueueAsync(NewMessage("second"));

        var a = await _repo.ClaimNextPendingAsync(DateTime.UtcNow);
        var b = await _repo.ClaimNextPendingAsync(DateTime.UtcNow);

        a!.Id.Should().Be(first.Id);
        b!.Id.Should().Be(second.Id, "second claim returns a different row");
        var c = await _repo.ClaimNextPendingAsync(DateTime.UtcNow);
        c.Should().BeNull("no more pending rows");
    }

    // ── MarkSent ──

    [Test]
    public async Task MarkSentAsync_SetsStatusAndSentAt()
    {
        var msg = await _repo.EnqueueAsync(NewMessage());
        await _repo.ClaimNextPendingAsync(DateTime.UtcNow);

        await _repo.MarkSentAsync(msg.Id);

        var stored = await _repo.GetByIdAsync(msg.Id);
        stored!.Status.Should().Be("sent");
        stored.SentAt.Should().NotBeNull();
        stored.SentAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        stored.LastError.Should().BeNull();
    }

    [Test]
    public async Task MarkSentAsync_Noop_WhenRowMissing()
    {
        var act = async () => await _repo.MarkSentAsync(Guid.NewGuid());
        await act.Should().NotThrowAsync("missing rows are silently ignored");
    }

    // ── MarkFailed ──

    [Test]
    public async Task MarkFailedAsync_UnderCeiling_IncrementsAttempts_RequeuesWithBackoff()
    {
        var msg = await _repo.EnqueueAsync(NewMessage());
        await _repo.ClaimNextPendingAsync(DateTime.UtcNow);

        var before = DateTime.UtcNow;
        var updated = await _repo.MarkFailedAsync(
            msg.Id, "smtp connect refused", TimeSpan.FromMinutes(5));

        updated.Should().NotBeNull();
        updated!.Status.Should().Be("pending", "requeue when attempts < max");
        updated.Attempts.Should().Be(1);
        updated.LastError.Should().Be("smtp connect refused");
        updated.NextAttemptAt.Should().BeAfter(before.AddMinutes(4));
    }

    [Test]
    public async Task MarkFailedAsync_AtCeiling_FlipsToFailed()
    {
        var msg = NewMessage();
        msg.MaxAttempts = 2;
        var enq = await _repo.EnqueueAsync(msg);

        await _repo.ClaimNextPendingAsync(DateTime.UtcNow);
        await _repo.MarkFailedAsync(enq.Id, "err1", TimeSpan.FromMinutes(1));

        await _repo.ClaimNextPendingAsync(DateTime.UtcNow.AddHours(1));
        var final = await _repo.MarkFailedAsync(enq.Id, "err2", TimeSpan.FromMinutes(5));

        final!.Status.Should().Be("failed");
        final.Attempts.Should().Be(2);
        final.LastError.Should().Be("err2");
    }

    [Test]
    public async Task MarkFailedAsync_DefaultsBackoffWhenNull()
    {
        var msg = await _repo.EnqueueAsync(NewMessage());
        await _repo.ClaimNextPendingAsync(DateTime.UtcNow);

        var before = DateTime.UtcNow;
        var updated = await _repo.MarkFailedAsync(msg.Id, "err", backoff: null);

        updated!.NextAttemptAt.Should().BeAfter(before,
            "default backoff still schedules NextAttemptAt in the future");
    }

    // ── GetByIdAsync ──

    [Test]
    public async Task GetByIdAsync_ReturnsNull_ForUnknownId()
        => (await _repo.GetByIdAsync(Guid.NewGuid())).Should().BeNull();

    // ── DeleteAsync ──

    [Test]
    public async Task DeleteAsync_RemovesRow()
    {
        var msg = await _repo.EnqueueAsync(NewMessage());

        await _repo.DeleteAsync(msg.Id);

        (await _repo.GetByIdAsync(msg.Id)).Should().BeNull();
    }

    [Test]
    public async Task DeleteAsync_Noop_WhenRowMissing()
    {
        // No exception on deleting a non-existent id.
        await _repo.DeleteAsync(Guid.NewGuid());
    }
}
