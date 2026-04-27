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

namespace Tamma.Api.Tests.Email;

/// <summary>
/// Unit tests for the <see cref="OutboxSmtpSender"/> hosted service. Uses
/// <see cref="OutboxSmtpSender.ProcessOnceAsync"/> to drive single poll cycles
/// deterministically — the real <c>ExecuteAsync</c> loop's timer cadence is
/// exercised only in integration tests.
/// </summary>
[TestFixture]
public class OutboxSmtpSenderTests
{
    private ServiceProvider _services = null!;
    private DbContextOptions<ControlPlaneDbContext> _cpOptions = null!;
    private DbContextOptions<TenantDbContext> _tenantOptions = null!;
    private Mock<ISmtpTransport> _transport = null!;
    private OutboxSmtpSender _sender = null!;
    private IConfiguration _config = null!;

    [SetUp]
    public void SetUp()
    {
        var dbName = Guid.NewGuid().ToString();
        _cpOptions = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _tenantOptions = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        var capturedCp = _cpOptions;
        var capturedTenant = _tenantOptions;
        var services = new ServiceCollection();
        services.AddScoped<ControlPlaneDbContext>(_ => new TestControlPlaneDbContext(capturedCp));
        services.AddSingleton<ITenantDbContextFactory>(_ => new TestTenantDbContextFactory(capturedTenant));
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<IEmailOutboxRepository, EmailOutboxRepository>();
        services.AddScoped<IPlatformEventRepository, PlatformEventRepository>();
        services.AddScoped<IEventRepository, EventRepository>();

        _transport = new Mock<ISmtpTransport>();
        services.AddSingleton(_transport.Object);

        _services = services.BuildServiceProvider();

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Email:Provider"] = "smtp",
                ["Email:Smtp:Host"] = "mail.example.com",
            })
            .Build();

        _sender = new OutboxSmtpSender(
            _services,
            new OutboxSmtpSenderOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(10),
                BackoffSchedule = new[]
                {
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(4),
                },
            },
            _config,
            NullLogger<OutboxSmtpSender>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _sender.Dispose();
        _services.Dispose();
    }

    private EmailOutboxRepository FreshOutbox()
        => new(new TestTenantDbContextFactory(_tenantOptions),
               new TestControlPlaneDbContext(_cpOptions));
    private EventRepository FreshEvents()
    {
        var cp = new TestControlPlaneDbContext(_cpOptions);
        return new EventRepository(
            new TestTenantDbContextFactory(_tenantOptions),
            new TenantContext(),
            cp,
            new PlatformEventRepository(cp));
    }

    private static EmailOutboxMessage NewRow(int maxAttempts = 5) => new()
    {
        Template = "verification",
        ToAddress = "u@example.com",
        Subject = "Verify",
        HtmlBody = "<p>hi</p>",
        TextBody = "hi",
        FromAddress = "noreply@tamma.dev",
        MaxAttempts = maxAttempts,
    };

    // ── Happy path ──────────────────────────────────────────────────────────

    [Test]
    public async Task ProcessOnceAsync_DeliversPendingRow_EmitsSentEvent()
    {
        var enq = await FreshOutbox().EnqueueAsync(NewRow());
        _transport.Setup(t => t.SendAsync(It.IsAny<EmailOutboxMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var processed = await _sender.ProcessOnceAsync(CancellationToken.None);

        processed.Should().BeTrue();
        _transport.Verify(
            t => t.SendAsync(It.Is<EmailOutboxMessage>(m => m.Id == enq.Id), It.IsAny<CancellationToken>()),
            Times.Once);

        // Row is deleted after successful delivery — audit trail lives in the
        // event store (EMAIL.SENT.SUCCESS below), so recipient/subject/body
        // don't persist in the outbox past the successful-send moment.
        var stored = await FreshOutbox().GetByIdAsync(enq.Id);
        stored.Should().BeNull();

        var sent = await FreshEvents().QueryAsync(null, EmailEventTypes.Sent, null, 10);
        sent.Should().ContainSingle();
        JsonSerializer.Deserialize<Dictionary<string, string?>>(sent[0].Tags)!["txn_id"]
            .Should().Be(enq.Id.ToString());
    }

    [Test]
    public async Task ProcessOnceAsync_SuccessfulDelivery_PurgesRowFromOutbox()
    {
        var enq = await FreshOutbox().EnqueueAsync(NewRow());
        _transport.Setup(t => t.SendAsync(It.IsAny<EmailOutboxMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _sender.ProcessOnceAsync(CancellationToken.None);

        var stored = await FreshOutbox().GetByIdAsync(enq.Id);
        stored.Should().BeNull("the sent row must be purged so the recipient " +
                              "address and body don't linger beyond delivery");
    }

    [Test]
    public async Task ProcessOnceAsync_NoPendingRows_ReturnsFalse()
    {
        (await _sender.ProcessOnceAsync(CancellationToken.None)).Should().BeFalse();
    }

    // ── Transient failure ──────────────────────────────────────────────────

    [Test]
    public async Task ProcessOnceAsync_TransportThrows_RequeuesWithBackoff_NoFailedEvent()
    {
        var enq = await FreshOutbox().EnqueueAsync(NewRow());
        _transport.Setup(t => t.SendAsync(It.IsAny<EmailOutboxMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("connect refused"));

        var before = DateTime.UtcNow;
        await _sender.ProcessOnceAsync(CancellationToken.None);

        var stored = await FreshOutbox().GetByIdAsync(enq.Id);
        stored!.Status.Should().Be("pending", "requeued — not yet at max attempts");
        stored.Attempts.Should().Be(1);
        stored.LastError.Should().Contain("connect refused");
        stored.NextAttemptAt.Should().BeAfter(before,
            "backoff pushes the next attempt into the future");

        // No terminal Failed event yet — only a transient retry.
        var failed = await FreshEvents().QueryAsync(null, EmailEventTypes.Failed, null, 10);
        failed.Should().BeEmpty();
    }

    // ── Permanent failure ──────────────────────────────────────────────────

    [Test]
    public async Task ProcessOnceAsync_HitsMaxAttempts_MarksFailed_EmitsFailedEvent()
    {
        var enq = await FreshOutbox().EnqueueAsync(NewRow(maxAttempts: 2));
        _transport.Setup(t => t.SendAsync(It.IsAny<EmailOutboxMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("broken"));

        // Attempt 1 — transient, requeued with backoff into the future.
        await _sender.ProcessOnceAsync(CancellationToken.None);
        var afterFirst = await FreshOutbox().GetByIdAsync(enq.Id);
        afterFirst!.Status.Should().Be("pending");

        // Fast-forward NextAttemptAt so the next ProcessOnceAsync picks it up.
        using (var db = new TestControlPlaneDbContext(_cpOptions))
        {
            var row = await db.EmailOutbox.FindAsync(enq.Id);
            row!.NextAttemptAt = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        // Attempt 2 — hits ceiling, flips to failed, then row is purged.
        // Inbox is a retry buffer only: once retries exhaust, the audit lives
        // in the event store (EMAIL.SENT.FAILED below). Keeping failed rows
        // would mean recipient/subject/body lingering in the DB indefinitely.
        await _sender.ProcessOnceAsync(CancellationToken.None);

        var stored = await FreshOutbox().GetByIdAsync(enq.Id);
        stored.Should().BeNull("terminal failure purges the row — event store holds the audit");

        var failed = await FreshEvents().QueryAsync(null, EmailEventTypes.Failed, null, 10);
        failed.Should().ContainSingle();
        var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(failed[0].Data)!;
        data["provider"].GetString().Should().Be("smtp");
        data["error_class"].GetString().Should().Be(typeof(InvalidOperationException).FullName);
    }

    // ── Skip when Email:Provider != smtp ───────────────────────────────────

    [Test]
    public async Task ExecuteAsync_NoOpWhenProviderIsNotSmtp()
    {
        var altConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Email:Provider"] = "resend",
            })
            .Build();
        using var altSender = new OutboxSmtpSender(
            _services, new OutboxSmtpSenderOptions(), altConfig,
            NullLogger<OutboxSmtpSender>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        // StartAsync + delay + StopAsync path
        await altSender.StartAsync(cts.Token);
        await Task.Delay(50, cts.Token);
        await altSender.StopAsync(CancellationToken.None);

        _transport.Verify(
            t => t.SendAsync(It.IsAny<EmailOutboxMessage>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Event payload safety ───────────────────────────────────────────────

    [Test]
    public async Task ProcessOnceAsync_EventPayloadNeverLeaksRecipientOrSubjectOrBody()
    {
        var enq = await FreshOutbox().EnqueueAsync(NewRow());
        _transport.Setup(t => t.SendAsync(It.IsAny<EmailOutboxMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _sender.ProcessOnceAsync(CancellationToken.None);

        var all = await FreshEvents().QueryAsync(null, null, null, 20);
        foreach (var evt in all)
        {
            var combined = evt.Tags + evt.Data;
            combined.Should().NotContain("u@example.com");
            combined.Should().NotContain("Verify");
            combined.Should().NotContain("<p>hi</p>");
        }
    }
}
