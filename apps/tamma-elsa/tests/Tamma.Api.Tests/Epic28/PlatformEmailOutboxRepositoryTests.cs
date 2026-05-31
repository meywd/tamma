using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Epic28;

/// <summary>
/// Story 28-6 — unit tests for <see cref="PlatformEmailOutboxRepository"/>.
/// Mirrors <see cref="Tamma.Api.Tests.Email.EmailOutboxRepositoryTests"/>
/// against the platform-scoped repo and the
/// <see cref="ControlPlaneDbContext"/>.
/// </summary>
[TestFixture]
public class PlatformEmailOutboxRepositoryTests
{
    private ControlPlaneDbContext _db = null!;
    private PlatformEmailOutboxRepository _repo = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _db = new ControlPlaneDbContext(options);
        _repo = new PlatformEmailOutboxRepository(_db);
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    private static PlatformEmailOutboxMessage NewMessage(string template = "verification") => new()
    {
        Template = template,
        ToAddress = "u@example.com",
        Subject = "Verify your email",
        HtmlBody = "<p>hi</p>",
        TextBody = "hi",
        FromAddress = "noreply@tamma.dev",
        MaxAttempts = 5,
    };

    // ── EnqueueAsync ──────────────────────────────────────────────────────────

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
    }

    [Test]
    public async Task EnqueueAsync_DefaultsMaxAttemptsWhenZero()
    {
        var seed = NewMessage();
        seed.MaxAttempts = 0;

        var msg = await _repo.EnqueueAsync(seed);
        msg.MaxAttempts.Should().Be(5);
    }

    // ── ClaimNextPendingAsync ─────────────────────────────────────────────────

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
        msg.NextAttemptAt = DateTime.UtcNow.AddHours(1);
        await _db.SaveChangesAsync();

        var claim = await _repo.ClaimNextPendingAsync(DateTime.UtcNow);
        claim.Should().BeNull();
    }

    [Test]
    public async Task ClaimNextPendingAsync_PicksOldestDueFirst()
    {
        var first = await _repo.EnqueueAsync(NewMessage("first"));
        await Task.Delay(10);
        var second = await _repo.EnqueueAsync(NewMessage("second"));

        var claim = await _repo.ClaimNextPendingAsync(DateTime.UtcNow);

        claim!.Id.Should().Be(first.Id);
    }

    [Test]
    public async Task ClaimNextPendingAsync_SkipsAlreadyClaimedRows()
    {
        var first = await _repo.EnqueueAsync(NewMessage("first"));
        var second = await _repo.EnqueueAsync(NewMessage("second"));

        var a = await _repo.ClaimNextPendingAsync(DateTime.UtcNow);
        var b = await _repo.ClaimNextPendingAsync(DateTime.UtcNow);

        a!.Id.Should().Be(first.Id);
        b!.Id.Should().Be(second.Id);
        var c = await _repo.ClaimNextPendingAsync(DateTime.UtcNow);
        c.Should().BeNull();
    }

    // ── MarkSentAsync ─────────────────────────────────────────────────────────

    [Test]
    public async Task MarkSentAsync_SetsStatusAndSentAt()
    {
        var msg = await _repo.EnqueueAsync(NewMessage());
        await _repo.ClaimNextPendingAsync(DateTime.UtcNow);

        await _repo.MarkSentAsync(msg.Id);

        var stored = await _repo.GetByIdAsync(msg.Id);
        stored!.Status.Should().Be("sent");
        stored.SentAt.Should().NotBeNull();
        stored.LastError.Should().BeNull();
    }

    [Test]
    public async Task MarkSentAsync_NoOp_WhenRowMissing()
    {
        var act = async () => await _repo.MarkSentAsync(Guid.NewGuid());
        await act.Should().NotThrowAsync();
    }

    // ── MarkFailedAsync ───────────────────────────────────────────────────────

    [Test]
    public async Task MarkFailedAsync_UnderCeiling_RequeuesWithBackoff()
    {
        var msg = await _repo.EnqueueAsync(NewMessage());
        await _repo.ClaimNextPendingAsync(DateTime.UtcNow);

        var before = DateTime.UtcNow;
        var updated = await _repo.MarkFailedAsync(
            msg.Id, "smtp connect refused", TimeSpan.FromMinutes(5));

        updated!.Status.Should().Be("pending");
        updated.Attempts.Should().Be(1);
        updated.LastError.Should().Be("smtp connect refused");
        updated.NextAttemptAt.Should().BeAfter(before.AddMinutes(4));
    }

    [Test]
    public async Task MarkFailedAsync_AtCeiling_FlipsToFailed()
    {
        var seed = NewMessage();
        seed.MaxAttempts = 2;
        var enq = await _repo.EnqueueAsync(seed);

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

        updated!.NextAttemptAt.Should().BeAfter(before);
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    [Test]
    public async Task DeleteAsync_RemovesRow()
    {
        var msg = await _repo.EnqueueAsync(NewMessage());

        await _repo.DeleteAsync(msg.Id);

        (await _repo.GetByIdAsync(msg.Id)).Should().BeNull();
    }

    [Test]
    public async Task DeleteAsync_NoOp_WhenRowMissing()
    {
        await _repo.DeleteAsync(Guid.NewGuid());
    }

    // ── EnqueueWelcomeOnceAsync (Story 28-5 AC2 step-10 + AC5) ────────────────

    [Test]
    public async Task EnqueueWelcomeOnceAsync_InsertsWelcomeRow_WithCorrectRecipientAndTemplate()
    {
        var tenantId = Guid.NewGuid();

        var row = await _repo.EnqueueWelcomeOnceAsync(
            tenantId, "owner@example.com", "Acme Inc", "noreply@tamma.dev");

        row.Id.Should().NotBe(Guid.Empty);
        row.TenantId.Should().Be(tenantId);
        row.Template.Should().Be("welcome");
        row.ToAddress.Should().Be("owner@example.com");
        row.FromAddress.Should().Be("noreply@tamma.dev");
        row.Status.Should().Be("pending");
        row.Subject.Should().Contain("Acme Inc");
        row.HtmlBody.Should().NotBeNullOrWhiteSpace();
        row.TextBody.Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public async Task EnqueueWelcomeOnceAsync_IsIdempotent_SecondCallProducesNoSecondRow()
    {
        var tenantId = Guid.NewGuid();

        var first = await _repo.EnqueueWelcomeOnceAsync(
            tenantId, "owner@example.com", "Acme Inc", "noreply@tamma.dev");
        var second = await _repo.EnqueueWelcomeOnceAsync(
            tenantId, "owner@example.com", "Acme Inc", "noreply@tamma.dev");

        // Same row returned — exactly-once-per-tenant.
        second.Id.Should().Be(first.Id);

        var count = await _db.PlatformEmailOutbox
            .CountAsync(m => m.TenantId == tenantId && m.Template == "welcome");
        count.Should().Be(1);
    }

    [Test]
    public async Task EnqueueWelcomeOnceAsync_ReQueues_WhenPriorWelcomeFailed()
    {
        var tenantId = Guid.NewGuid();

        var first = await _repo.EnqueueWelcomeOnceAsync(
            tenantId, "owner@example.com", "Acme Inc", "noreply@tamma.dev");

        // Simulate the prior welcome exhausting its retries.
        first.Status = "failed";
        await _db.SaveChangesAsync();

        // A failed prior row does NOT block a fresh welcome (the partial
        // unique index excludes status='failed').
        var second = await _repo.EnqueueWelcomeOnceAsync(
            tenantId, "owner@example.com", "Acme Inc", "noreply@tamma.dev");

        second.Id.Should().NotBe(first.Id);
        second.Status.Should().Be("pending");
    }
}
