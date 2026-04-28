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
///
/// <para>Story 28-1 PR B — every test now operates against a single
/// fixed tenant id; the repo is strictly tenant-scoped post-PR-B and
/// every operation requires the tenant id explicitly. The
/// <c>FromAnyTenant</c> drain path is covered by a small additional
/// suite at the bottom that seeds an active tenant row in CP and
/// asserts the drain returns the seeded outbox row.</para>
/// </summary>
[TestFixture]
public class EmailOutboxRepositoryTests
{
    private static readonly Guid TestTenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private ControlPlaneDbContext _db = null!;
    private DbContextOptions<TenantDbContext> _tenantOptions = null!;
    private TestTenantDbContextFactory _tenantFactory = null!;
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
        _tenantOptions = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _db = new TestControlPlaneDbContext(cpOptions);
        _tenantFactory = new TestTenantDbContextFactory(_tenantOptions);
        _repo = new EmailOutboxRepository(_tenantFactory, _db);

        // Seed an active-tenant row so ClaimNextPendingFromAnyTenantAsync
        // has a tenant to walk.
        _db.Tenants.Add(new Tenant
        {
            Id = TestTenantId,
            Name = "test",
            Slug = "test",
            Type = "personal",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    private static EmailOutboxMessage NewMessage(string template = "verification")
        => new()
        {
            TenantId = TestTenantId,
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

        // Story 28-1 PR D — email_outbox lives on the tenant DB.
        await using var tdb = await _tenantFactory.CreateAsync(TestTenantId);
        var stored = await tdb.EmailOutbox.FindAsync(msg.Id);
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

    [Test]
    public async Task EnqueueAsync_ThrowsWhenTenantIdNull()
    {
        var seed = NewMessage();
        seed.TenantId = null;

        var act = async () => await _repo.EnqueueAsync(seed);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*TenantId*");
    }

    // ── ClaimNextPendingAsync ──

    [Test]
    public async Task ClaimNextPendingAsync_ReturnsNull_WhenNothingPending()
    {
        var claim = await _repo.ClaimNextPendingAsync(TestTenantId, DateTime.UtcNow);
        claim.Should().BeNull();
    }

    [Test]
    public async Task ClaimNextPendingAsync_FlipsRowToSending()
    {
        var msg = await _repo.EnqueueAsync(NewMessage());

        var claimed = await _repo.ClaimNextPendingAsync(TestTenantId, DateTime.UtcNow);

        claimed.Should().NotBeNull();
        claimed!.Id.Should().Be(msg.Id);

        var stored = await _repo.GetByIdAsync(TestTenantId, msg.Id);
        stored!.Status.Should().Be("sending");
    }

    [Test]
    public async Task ClaimNextPendingAsync_SkipsRowsNotYetDue()
    {
        var msg = await _repo.EnqueueAsync(NewMessage());

        // Schedule far in the future so current-time claims can't grab
        // it. Story 28-1 PR D — email_outbox lives on the tenant DB.
        await using (var tdb = await _tenantFactory.CreateAsync(TestTenantId))
        {
            var stored = await tdb.EmailOutbox.FindAsync(msg.Id);
            stored!.NextAttemptAt = DateTime.UtcNow.AddHours(1);
            await tdb.SaveChangesAsync();
        }

        var claim = await _repo.ClaimNextPendingAsync(TestTenantId, DateTime.UtcNow);

        claim.Should().BeNull("row is not due yet — NextAttemptAt > now");
    }

    [Test]
    public async Task ClaimNextPendingAsync_PicksOldestDueFirst()
    {
        var first = await _repo.EnqueueAsync(NewMessage("first"));
        await Task.Delay(10);
        var second = await _repo.EnqueueAsync(NewMessage("second"));

        var claim = await _repo.ClaimNextPendingAsync(TestTenantId, DateTime.UtcNow);

        claim!.Id.Should().Be(first.Id, "FIFO — the earlier NextAttemptAt wins");
    }

    [Test]
    public async Task ClaimNextPendingAsync_SkipsAlreadyClaimedRows()
    {
        var first = await _repo.EnqueueAsync(NewMessage("first"));
        var second = await _repo.EnqueueAsync(NewMessage("second"));

        var a = await _repo.ClaimNextPendingAsync(TestTenantId, DateTime.UtcNow);
        var b = await _repo.ClaimNextPendingAsync(TestTenantId, DateTime.UtcNow);

        a!.Id.Should().Be(first.Id);
        b!.Id.Should().Be(second.Id, "second claim returns a different row");
        var c = await _repo.ClaimNextPendingAsync(TestTenantId, DateTime.UtcNow);
        c.Should().BeNull("no more pending rows");
    }

    // ── ClaimNextPendingFromAnyTenantAsync (Story 28-1 PR B) ──

    [Test]
    public async Task ClaimNextPendingFromAnyTenantAsync_ReturnsRowFromActiveTenant()
    {
        var msg = await _repo.EnqueueAsync(NewMessage("verification"));

        var claimed = await _repo.ClaimNextPendingFromAnyTenantAsync(DateTime.UtcNow);

        claimed.Should().NotBeNull();
        claimed!.Id.Should().Be(msg.Id);
        claimed.TenantId.Should().Be(TestTenantId);
    }

    [Test]
    public async Task ClaimNextPendingFromAnyTenantAsync_ReturnsNull_WhenNoTenantsActive()
    {
        // Soft-delete the tenant.
        var t = await _db.Tenants.FindAsync(TestTenantId);
        t!.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var claimed = await _repo.ClaimNextPendingFromAnyTenantAsync(DateTime.UtcNow);

        claimed.Should().BeNull();
    }

    [Test]
    public async Task ClaimNextPendingFromAnyTenantAsync_ReturnsNull_WhenNothingPending()
    {
        var claimed = await _repo.ClaimNextPendingFromAnyTenantAsync(DateTime.UtcNow);
        claimed.Should().BeNull();
    }

    // ── MarkSent ──

    [Test]
    public async Task MarkSentAsync_SetsStatusAndSentAt()
    {
        var msg = await _repo.EnqueueAsync(NewMessage());
        await _repo.ClaimNextPendingAsync(TestTenantId, DateTime.UtcNow);

        await _repo.MarkSentAsync(TestTenantId, msg.Id);

        var stored = await _repo.GetByIdAsync(TestTenantId, msg.Id);
        stored!.Status.Should().Be("sent");
        stored.SentAt.Should().NotBeNull();
        stored.SentAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        stored.LastError.Should().BeNull();
    }

    [Test]
    public async Task MarkSentAsync_Noop_WhenRowMissing()
    {
        var act = async () => await _repo.MarkSentAsync(TestTenantId, Guid.NewGuid());
        await act.Should().NotThrowAsync("missing rows are silently ignored");
    }

    // ── MarkFailed ──

    [Test]
    public async Task MarkFailedAsync_UnderCeiling_IncrementsAttempts_RequeuesWithBackoff()
    {
        var msg = await _repo.EnqueueAsync(NewMessage());
        await _repo.ClaimNextPendingAsync(TestTenantId, DateTime.UtcNow);

        var before = DateTime.UtcNow;
        var updated = await _repo.MarkFailedAsync(
            TestTenantId, msg.Id, "smtp connect refused", TimeSpan.FromMinutes(5));

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

        await _repo.ClaimNextPendingAsync(TestTenantId, DateTime.UtcNow);
        await _repo.MarkFailedAsync(TestTenantId, enq.Id, "err1", TimeSpan.FromMinutes(1));

        await _repo.ClaimNextPendingAsync(TestTenantId, DateTime.UtcNow.AddHours(1));
        var final = await _repo.MarkFailedAsync(TestTenantId, enq.Id, "err2", TimeSpan.FromMinutes(5));

        final!.Status.Should().Be("failed");
        final.Attempts.Should().Be(2);
        final.LastError.Should().Be("err2");
    }

    [Test]
    public async Task MarkFailedAsync_DefaultsBackoffWhenNull()
    {
        var msg = await _repo.EnqueueAsync(NewMessage());
        await _repo.ClaimNextPendingAsync(TestTenantId, DateTime.UtcNow);

        var before = DateTime.UtcNow;
        var updated = await _repo.MarkFailedAsync(TestTenantId, msg.Id, "err", backoff: null);

        updated!.NextAttemptAt.Should().BeAfter(before,
            "default backoff still schedules NextAttemptAt in the future");
    }

    // ── GetByIdAsync ──

    [Test]
    public async Task GetByIdAsync_ReturnsNull_ForUnknownId()
        => (await _repo.GetByIdAsync(TestTenantId, Guid.NewGuid())).Should().BeNull();

    // ── DeleteAsync ──

    [Test]
    public async Task DeleteAsync_RemovesRow()
    {
        var msg = await _repo.EnqueueAsync(NewMessage());

        await _repo.DeleteAsync(TestTenantId, msg.Id);

        (await _repo.GetByIdAsync(TestTenantId, msg.Id)).Should().BeNull();
    }

    [Test]
    public async Task DeleteAsync_Noop_WhenRowMissing()
    {
        // No exception on deleting a non-existent id.
        await _repo.DeleteAsync(TestTenantId, Guid.NewGuid());
    }
}
