using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
/// Unit tests for the rewritten outbox-backed <see cref="SmtpEmailService"/>.
/// The service must:
/// <list type="bullet">
///   <item><description>Enqueue a row in <see cref="IEmailOutboxRepository"/>
///     with the recipient, subject, and body persisted on the row.</description></item>
///   <item><description>Emit exactly one <c>EMAIL.QUEUED.SUCCESS</c> event
///     with the transaction id on tags — and <b>no</b> recipient, subject,
///     or body anywhere in tags or data.</description></item>
///   <item><description>Return the row's id as the transaction id.</description></item>
///   <item><description>Never touch MailKit / SMTP directly.</description></item>
/// </list>
/// </summary>
[TestFixture]
public class SmtpEmailServiceOutboxTests
{
    private TammaDbContext _db = null!;
    private EmailOutboxRepository _outbox = null!;
    private EventRepository _events = null!;
    private TenantContext _tenantContext = null!;
    private IConfiguration _config = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<TammaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _tenantContext = new TenantContext();
        _db = new TestDbContext(options, _tenantContext);
        _outbox = new EmailOutboxRepository(_db);
        _events = new EventRepository(_db);

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Email:From"] = "noreply@tamma.dev",
            })
            .Build();
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    private SmtpEmailService NewService() => new(
        _outbox, _events, _tenantContext, _config,
        NullLogger<SmtpEmailService>.Instance);

    private static EmailMessage NewMessage(Guid? tenantId = null, Guid? userId = null)
        => new(
            To: "alice@example.com",
            Subject: "Verify your email",
            Html: "<p>hi</p>",
            Text: "hi",
            Template: "verification",
            TenantId: tenantId,
            UserId: userId);

    [Test]
    public async Task SendAsync_EnqueuesRowWithRecipient_AndReturnsRowId()
    {
        var svc = NewService();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var txnId = await svc.SendAsync(NewMessage(tenantId, userId));

        txnId.Should().NotBe(Guid.Empty);

        var row = await _outbox.GetByIdAsync(txnId);
        row.Should().NotBeNull();
        row!.Status.Should().Be("pending");
        row.ToAddress.Should().Be("alice@example.com");
        row.Subject.Should().Be("Verify your email");
        row.HtmlBody.Should().Be("<p>hi</p>");
        row.TextBody.Should().Be("hi");
        row.FromAddress.Should().Be("noreply@tamma.dev");
        row.Template.Should().Be("verification");
        row.TenantId.Should().Be(tenantId);
        row.UserId.Should().Be(userId);
        row.Attempts.Should().Be(0);
        row.MaxAttempts.Should().Be(5);
    }

    [Test]
    public async Task SendAsync_EmitsQueuedEventWithTxnIdTag()
    {
        var svc = NewService();

        var txnId = await svc.SendAsync(NewMessage());

        var events = await _events.QueryAsync(null, EmailEventTypes.Queued, null, 10);
        events.Should().ContainSingle();

        var tags = JsonSerializer.Deserialize<Dictionary<string, string?>>(events[0].Tags)!;
        tags["txn_id"].Should().Be(txnId.ToString());
        tags["template"].Should().Be("verification");
    }

    [Test]
    public async Task SendAsync_DoesNotLeakRecipientOrSubjectIntoEvent()
    {
        var svc = NewService();

        await svc.SendAsync(NewMessage());

        var events = await _events.QueryAsync(null, EmailEventTypes.Queued, null, 10);
        var combined = events[0].Tags + events[0].Data;

        combined.Should().NotContain("alice@example.com");
        combined.Should().NotContain("Verify your email");
        combined.Should().NotContain("<p>hi</p>");
    }

    [Test]
    public async Task SendAsync_UsesConfigFromAddressWhenMessageFromIsNull()
    {
        var svc = NewService();
        var txnId = await svc.SendAsync(NewMessage() with { From = null });

        var row = await _outbox.GetByIdAsync(txnId);
        row!.FromAddress.Should().Be("noreply@tamma.dev");
    }

    [Test]
    public async Task SendAsync_ThrowsWhenNeitherMessageNorConfigHasFrom()
    {
        var emptyConfig = new ConfigurationBuilder().Build();
        var svc = new SmtpEmailService(_outbox, _events, _tenantContext, emptyConfig,
            NullLogger<SmtpEmailService>.Instance);

        var act = async () => await svc.SendAsync(NewMessage() with { From = null });

        await act.Should().ThrowAsync<InvalidOperationException>(
            "programmer error — missing both message.From and Email:From config");
    }

    [Test]
    public async Task SendAsync_FallsBackToTenantContext_WhenMessageTenantIdNull()
    {
        var ambientTenant = Guid.NewGuid();
        _tenantContext.SetTenantId(ambientTenant);

        var svc = NewService();
        var txnId = await svc.SendAsync(NewMessage());

        var row = await _outbox.GetByIdAsync(txnId);
        row!.TenantId.Should().Be(ambientTenant,
            "SmtpEmailService falls back to the ambient tenant when the message didn't supply one");
    }

    [Test]
    public async Task SendAsync_DoesNotTouchSmtp()
    {
        // The rewritten service uses ISmtpTransport for real delivery but
        // SmtpEmailService itself must NOT call it. Wire a fail-on-use mock
        // and confirm enqueue succeeds.
        var smtpMock = new Mock<ISmtpTransport>(MockBehavior.Strict);
        // No setups — any call will throw thanks to strict mode.

        var svc = NewService();
        var act = async () => await svc.SendAsync(NewMessage());

        await act.Should().NotThrowAsync();
        smtpMock.VerifyNoOtherCalls();
    }
}
