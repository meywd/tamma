using Tamma.Data.Abstractions;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Email;
using Tamma.Api.Tests.Infrastructure;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Epic28;

/// <summary>
/// Story 28-6 — exercises the new platform-outbox draining path on
/// <see cref="OutboxSmtpSender"/>. The companion tenant path (and event
/// payload safety + retry behaviour) is covered by
/// <c>OutboxSmtpSenderTests</c>; this suite focuses on:
/// <list type="bullet">
///   <item><description>Platform queue drained when the tenant queue is empty.</description></item>
///   <item><description>Tenant path remains preferred when both queues have rows.</description></item>
///   <item><description>Successful platform delivery emits a
///     <c>EMAIL.SENT.SUCCESS</c> event on <c>platform_events</c> and
///     deletes the platform row.</description></item>
///   <item><description>Permanent platform failure emits
///     <c>EMAIL.SENT.FAILED</c> on <c>platform_events</c>.</description></item>
///   <item><description>Sender is back-compat: when
///     <see cref="IPlatformEmailOutboxRepository"/> is not registered the
///     platform path is a no-op, not an error.</description></item>
/// </list>
/// </summary>
[TestFixture]
public class OutboxSmtpSenderPlatformPathTests
{
    private DbContextOptions<TenantDbContext> _tenantOptions = null!;
    private DbContextOptions<ControlPlaneDbContext> _cpOptions = null!;
    private Mock<ISmtpTransport> _transport = null!;
    private IConfiguration _config = null!;

    [SetUp]
    public void SetUp()
    {
        // Wave A.5 post-merge: tenant writes route through
        // ITenantDbContextFactory → TenantDbContext. CP reads/writes
        // stay on ControlPlaneDbContext. Both share the same EF InMemory
        // database name so the email sender sees the same rows the test
        // seeds through either surface (the transitional single-DB
        // topology — once Story 28-1's db-per-tenant ships the factory
        // will hand back a different data source and this shared-name
        // pattern will be replaced by a round-robin poll).
        var dbName = "outbox-" + Guid.NewGuid();
        _tenantOptions = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _cpOptions = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _transport = new Mock<ISmtpTransport>();

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Email:Provider"] = "smtp",
            })
            .Build();
    }

    private (ServiceProvider services, OutboxSmtpSender sender) BuildSender(bool registerPlatform)
    {
        var capturedCp = _cpOptions;
        var capturedTenant = _tenantOptions;
        var services = new ServiceCollection();
        services.AddScoped<ControlPlaneDbContext>(_ => new TestControlPlaneDbContext(capturedCp));
        services.AddSingleton<ITenantDbContextFactory>(_ => new TestTenantDbContextFactory(capturedTenant));
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<IEmailOutboxRepository, EmailOutboxRepository>();
        services.AddScoped<IEventRepository, EventRepository>();

        if (registerPlatform)
        {
            services.AddScoped<IPlatformEmailOutboxRepository, PlatformEmailOutboxRepository>();
            services.AddScoped<IPlatformEventRepository, PlatformEventRepository>();
        }

        services.AddSingleton(_transport.Object);

        var sp = services.BuildServiceProvider();

        var sender = new OutboxSmtpSender(
            sp,
            new OutboxSmtpSenderOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(10),
                BackoffSchedule = new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) },
            },
            _config,
            NullLogger<OutboxSmtpSender>.Instance);

        return (sp, sender);
    }

    private static PlatformEmailOutboxMessage NewPlatformRow(int maxAttempts = 5) => new()
    {
        Template = "verification",
        ToAddress = "platform@example.com",
        Subject = "Verify",
        HtmlBody = "<p>p</p>",
        TextBody = "p",
        FromAddress = "noreply@tamma.dev",
        MaxAttempts = maxAttempts,
    };

    private static readonly Guid TenantPathTestId = Guid.Parse("ffffffff-1111-2222-3333-444444444444");

    private static EmailOutboxMessage NewTenantRow() => new()
    {
        TenantId = TenantPathTestId,
        Template = "verification",
        ToAddress = "tenant@example.com",
        Subject = "Verify",
        HtmlBody = "<p>t</p>",
        TextBody = "t",
        FromAddress = "noreply@tamma.dev",
        MaxAttempts = 5,
    };

    /// <summary>
    /// Seed an active tenant row in CP. Story 28-1 PR B: the tenant
    /// outbox drain path enumerates active tenants from CP — without
    /// one, the tenant path returns null and only the platform path
    /// drains.
    /// </summary>
    private void SeedActiveTenant(Guid tenantId)
    {
        using var cp = new TestControlPlaneDbContext(_cpOptions);
        if (cp.Tenants.Find(tenantId) is null)
        {
            cp.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Name = "test-tenant",
                Slug = "test-tenant-" + tenantId.ToString()[..8],
                Type = "personal",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            cp.SaveChanges();
        }
    }

    /// <summary>
    /// Seed a platform row already in <c>sending</c> with an explicit
    /// <c>UpdatedAt</c> — simulates a row claimed by a sender that crashed
    /// before MarkSent/MarkFailed. Bypasses <c>EnqueueAsync</c> (which always
    /// writes <c>pending</c>).
    /// </summary>
    private async Task<PlatformEmailOutboxMessage> SeedSendingPlatformRowAsync(DateTime updatedAt)
    {
        using var cp = new ControlPlaneDbContext(_cpOptions);
        var row = new PlatformEmailOutboxMessage
        {
            Id = Guid.NewGuid(),
            Template = "verification",
            ToAddress = "platform@example.com",
            Subject = "Verify",
            HtmlBody = "<p>p</p>",
            TextBody = "p",
            FromAddress = "noreply@tamma.dev",
            Status = "sending",
            Attempts = 0,
            MaxAttempts = 5,
            NextAttemptAt = updatedAt,
            CreatedAt = updatedAt,
            UpdatedAt = updatedAt,
        };
        cp.PlatformEmailOutbox.Add(row);
        await cp.SaveChangesAsync();
        return row;
    }

    // ── Durability reaper: orphaned 'sending' platform rows ──────────────────

    [Test]
    public async Task ProcessOnceAsync_StuckSendingPlatformRowPastLease_ReclaimedAndDelivered()
    {
        var (sp, sender) = BuildSender(registerPlatform: true);
        try
        {
            // A row a crashed sender left in 'sending' 10 minutes ago (past the
            // default 5-minute lease). Without the reaper it is orphaned forever
            // — ClaimNextPendingAsync only selects 'pending'.
            var enq = await SeedSendingPlatformRowAsync(DateTime.UtcNow.AddMinutes(-10));
            _transport.Setup(t => t.SendAsync(It.IsAny<EmailOutboxMessage>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Single cycle: reclaim → pending → claim → deliver → purge.
            var processed = await sender.ProcessOnceAsync(CancellationToken.None);

            processed.Should().BeTrue("the reclaimed row is claimed and delivered in the same cycle");
            _transport.Verify(t => t.SendAsync(
                It.Is<EmailOutboxMessage>(m => m.Id == enq.Id),
                It.IsAny<CancellationToken>()), Times.Once);

            using var cpVerify = new ControlPlaneDbContext(_cpOptions);
            (await cpVerify.PlatformEmailOutbox.FindAsync(enq.Id))
                .Should().BeNull("the re-delivered row is purged, exactly like a normal send");
        }
        finally
        {
            sender.Dispose();
            sp.Dispose();
        }
    }

    [Test]
    public async Task ProcessOnceAsync_FreshSendingPlatformRowWithinLease_NotReclaimed()
    {
        var (sp, sender) = BuildSender(registerPlatform: true);
        try
        {
            // A row another sender just claimed (UpdatedAt = now) and is
            // delivering right now. The reaper must NOT steal it back to pending.
            var enq = await SeedSendingPlatformRowAsync(DateTime.UtcNow);

            var processed = await sender.ProcessOnceAsync(CancellationToken.None);

            processed.Should().BeFalse("a fresh 'sending' row is neither reclaimed nor claimable");
            _transport.Verify(t => t.SendAsync(
                It.IsAny<EmailOutboxMessage>(), It.IsAny<CancellationToken>()), Times.Never);

            using var cpVerify = new ControlPlaneDbContext(_cpOptions);
            var row = await cpVerify.PlatformEmailOutbox.FindAsync(enq.Id);
            row!.Status.Should().Be("sending", "a within-lease claim is left untouched");
            row.Attempts.Should().Be(0, "reclaim never bumps the attempt counter");
        }
        finally
        {
            sender.Dispose();
            sp.Dispose();
        }
    }

    // ── Platform path drains when tenant queue empty ─────────────────────────

    [Test]
    public async Task ProcessOnceAsync_TenantEmpty_DrainsPlatformRow_DeletesAfterSent()
    {
        var (sp, sender) = BuildSender(registerPlatform: true);
        try
        {
            using var cp = new ControlPlaneDbContext(_cpOptions);
            var platformRepo = new PlatformEmailOutboxRepository(cp);
            var enq = await platformRepo.EnqueueAsync(NewPlatformRow());

            _transport.Setup(t => t.SendAsync(It.IsAny<EmailOutboxMessage>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var processed = await sender.ProcessOnceAsync(CancellationToken.None);

            processed.Should().BeTrue();
            _transport.Verify(t => t.SendAsync(
                It.Is<EmailOutboxMessage>(m => m.Id == enq.Id),
                It.IsAny<CancellationToken>()), Times.Once);

            using var cpVerify = new ControlPlaneDbContext(_cpOptions);
            var stillThere = await cpVerify.PlatformEmailOutbox.FindAsync(enq.Id);
            stillThere.Should().BeNull("platform row is deleted after successful delivery");
        }
        finally
        {
            sender.Dispose();
            sp.Dispose();
        }
    }

    [Test]
    public async Task ProcessOnceAsync_PlatformSendSuccess_EmitsPlatformSentEvent()
    {
        var (sp, sender) = BuildSender(registerPlatform: true);
        try
        {
            using var cp = new ControlPlaneDbContext(_cpOptions);
            var platformRepo = new PlatformEmailOutboxRepository(cp);
            var enq = await platformRepo.EnqueueAsync(NewPlatformRow());

            _transport.Setup(t => t.SendAsync(It.IsAny<EmailOutboxMessage>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            await sender.ProcessOnceAsync(CancellationToken.None);

            using var cpQuery = new ControlPlaneDbContext(_cpOptions);
            var events = await cpQuery.PlatformEvents
                .Where(e => e.Type == EmailEventTypes.Sent)
                .ToListAsync();

            events.Should().HaveCount(1);
            var tags = JsonSerializer.Deserialize<Dictionary<string, string?>>(events[0].Tags)!;
            tags["txn_id"].Should().Be(enq.Id.ToString());
            tags["scope"].Should().Be("platform");
            tags["template"].Should().Be("verification");
        }
        finally
        {
            sender.Dispose();
            sp.Dispose();
        }
    }

    // ── Tenant path is preferred when both queues have work ──────────────────

    [Test]
    public async Task ProcessOnceAsync_BothQueuesHaveWork_TenantPathRunsFirst()
    {
        var (sp, sender) = BuildSender(registerPlatform: true);
        try
        {
            // Enqueue a tenant-scoped row through the factory-driven repo —
            // the cross-cutting EmailOutboxRepository routes to a per-tenant
            // DbContext when msg.TenantId is set, else to the CP legacy-shared
            // email_outbox. We want the tenant path to win here, so stamp a
            // tenant id. The sender's ClaimNext scans cp.EmailOutbox which
            // under EF-InMemory shares the same database name as the tenant
            // options (see SetUp), so the tenant row is visible to the CP
            // scan too — the test's invariant is "tenant row delivers first",
            // which the sender enforces by polling CP first.
            SeedActiveTenant(TenantPathTestId);
            var tenantRepo = new EmailOutboxRepository(
                new TestTenantDbContextFactory(_tenantOptions),
                new TestControlPlaneDbContext(_cpOptions));
            var tenantRow = NewTenantRow();
            var tenantEnq = await tenantRepo.EnqueueAsync(tenantRow);

            using var cp = new ControlPlaneDbContext(_cpOptions);
            var platformRepo = new PlatformEmailOutboxRepository(cp);
            await platformRepo.EnqueueAsync(NewPlatformRow());

            _transport.Setup(t => t.SendAsync(It.IsAny<EmailOutboxMessage>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            await sender.ProcessOnceAsync(CancellationToken.None);

            // Only the tenant row should have been delivered this cycle.
            _transport.Verify(t => t.SendAsync(
                It.Is<EmailOutboxMessage>(m => m.Id == tenantEnq.Id),
                It.IsAny<CancellationToken>()), Times.Once);
            _transport.Verify(t => t.SendAsync(
                It.IsAny<EmailOutboxMessage>(),
                It.IsAny<CancellationToken>()), Times.Once);

            // Platform row remains pending awaiting the next poll cycle.
            using var cpVerify = new ControlPlaneDbContext(_cpOptions);
            var pending = await cpVerify.PlatformEmailOutbox
                .CountAsync(m => m.Status == "pending");
            pending.Should().Be(1);
        }
        finally
        {
            sender.Dispose();
            sp.Dispose();
        }
    }

    // ── Permanent platform failure emits failed event ────────────────────────

    [Test]
    public async Task ProcessOnceAsync_PlatformPermanentFailure_EmitsPlatformFailedEvent()
    {
        var (sp, sender) = BuildSender(registerPlatform: true);
        try
        {
            using var cp = new ControlPlaneDbContext(_cpOptions);
            var platformRepo = new PlatformEmailOutboxRepository(cp);
            var enq = await platformRepo.EnqueueAsync(NewPlatformRow(maxAttempts: 2));

            _transport.Setup(t => t.SendAsync(It.IsAny<EmailOutboxMessage>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("relay down"));

            // Attempt 1 — transient failure, requeued.
            await sender.ProcessOnceAsync(CancellationToken.None);

            // Fast-forward NextAttemptAt so the next ProcessOnceAsync picks it up.
            using (var cp2 = new ControlPlaneDbContext(_cpOptions))
            {
                var row = await cp2.PlatformEmailOutbox.FindAsync(enq.Id);
                row!.NextAttemptAt = DateTime.UtcNow.AddMinutes(-1);
                await cp2.SaveChangesAsync();
            }

            // Attempt 2 — terminal failure.
            await sender.ProcessOnceAsync(CancellationToken.None);

            using var cpVerify = new ControlPlaneDbContext(_cpOptions);
            var rowAfter = await cpVerify.PlatformEmailOutbox.FindAsync(enq.Id);
            rowAfter.Should().BeNull("terminal failure deletes the platform row");

            var failedEvents = await cpVerify.PlatformEvents
                .Where(e => e.Type == EmailEventTypes.Failed)
                .ToListAsync();
            failedEvents.Should().ContainSingle();

            var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(failedEvents[0].Data)!;
            data["provider"].GetString().Should().Be("smtp");
            data["error_class"].GetString().Should().Be(typeof(InvalidOperationException).FullName);
        }
        finally
        {
            sender.Dispose();
            sp.Dispose();
        }
    }

    // ── Back-compat: missing platform repo is silent no-op ───────────────────

    [Test]
    public async Task ProcessOnceAsync_NoPlatformRepoRegistered_TenantEmpty_ReturnsFalse_NoThrow()
    {
        var (sp, sender) = BuildSender(registerPlatform: false);
        try
        {
            // No tenant rows + no platform repo → false, no exception.
            var processed = await sender.ProcessOnceAsync(CancellationToken.None);

            processed.Should().BeFalse(
                "with no platform repo registered the sender silently skips the platform queue");
            _transport.Verify(t => t.SendAsync(
                It.IsAny<EmailOutboxMessage>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            sender.Dispose();
            sp.Dispose();
        }
    }
}
