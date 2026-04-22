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
    private DbContextOptions<TammaDbContext> _tenantOptions = null!;
    private DbContextOptions<ControlPlaneDbContext> _cpOptions = null!;
    private Mock<ISmtpTransport> _transport = null!;
    private IConfiguration _config = null!;

    [SetUp]
    public void SetUp()
    {
        var dbName = Guid.NewGuid().ToString();
        _tenantOptions = new DbContextOptionsBuilder<TammaDbContext>()
            .UseInMemoryDatabase("tenant-" + dbName)
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _cpOptions = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase("cp-" + dbName)
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
        var services = new ServiceCollection();
        services.AddScoped<TammaDbContext>(_ => new TestDbContext(_tenantOptions));
        services.AddScoped<IEmailOutboxRepository, EmailOutboxRepository>();
        services.AddScoped<IEventRepository, EventRepository>();

        if (registerPlatform)
        {
            services.AddScoped(_ => new ControlPlaneDbContext(_cpOptions));
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

    private static EmailOutboxMessage NewTenantRow() => new()
    {
        Template = "verification",
        ToAddress = "tenant@example.com",
        Subject = "Verify",
        HtmlBody = "<p>t</p>",
        TextBody = "t",
        FromAddress = "noreply@tamma.dev",
        MaxAttempts = 5,
    };

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
            using var tdb = new TestDbContext(_tenantOptions);
            var tenantRepo = new EmailOutboxRepository(tdb);
            var tenantEnq = await tenantRepo.EnqueueAsync(NewTenantRow());

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
