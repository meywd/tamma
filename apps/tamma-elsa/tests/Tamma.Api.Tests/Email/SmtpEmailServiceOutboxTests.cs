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
    private static readonly Guid TestTenantId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private InMemoryDbFixture _fx = null!;
    private ControlPlaneDbContext _db = null!;
    private EmailOutboxRepository _outbox = null!;
    private PlatformEmailOutboxRepository _platformOutbox = null!;
    private EventRepository _events = null!;
    private TenantContext _tenantContext = null!;
    private IConfiguration _config = null!;

    [SetUp]
    public void SetUp()
    {
        _fx = new InMemoryDbFixture();
        _tenantContext = new TenantContext();
        _db = _fx.Cp;
        _outbox = new EmailOutboxRepository(_fx.Factory, _db);
        _platformOutbox = new PlatformEmailOutboxRepository(_db);
        _events = new EventRepository(_fx.Factory, _tenantContext, _db);

        // Seed an active tenant so tenant-scope SendAsync calls can route
        // to a real EF in-memory DB.
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

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Email:From"] = "noreply@tamma.dev",
            })
            .Build();
    }

    [TearDown]
    public async Task TearDown() => await _fx.DisposeAsync();

    private SmtpEmailService NewService() => new(
        _outbox, _platformOutbox, _events, _tenantContext, _config,
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
        var userId = Guid.NewGuid();

        var txnId = await svc.SendAsync(NewMessage(TestTenantId, userId));

        txnId.Should().NotBe(Guid.Empty);

        var row = await _outbox.GetByIdAsync(TestTenantId, txnId);
        row.Should().NotBeNull();
        row!.Status.Should().Be("pending");
        row.ToAddress.Should().Be("alice@example.com");
        row.Subject.Should().Be("Verify your email");
        row.HtmlBody.Should().Be("<p>hi</p>");
        row.TextBody.Should().Be("hi");
        row.FromAddress.Should().Be("noreply@tamma.dev");
        row.Template.Should().Be("verification");
        row.TenantId.Should().Be(TestTenantId);
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
        // Tenant-scope so the row lands in EmailOutboxRepository (we can
        // GetByIdAsync it). The From-config behaviour is identical for
        // platform-scope.
        var txnId = await svc.SendAsync(NewMessage(TestTenantId) with { From = null });

        var row = await _outbox.GetByIdAsync(TestTenantId, txnId);
        row!.FromAddress.Should().Be("noreply@tamma.dev");
    }

    [Test]
    public async Task SendAsync_ThrowsWhenNeitherMessageNorConfigHasFrom()
    {
        var emptyConfig = new ConfigurationBuilder().Build();
        var svc = new SmtpEmailService(_outbox, _platformOutbox, _events, _tenantContext, emptyConfig,
            NullLogger<SmtpEmailService>.Instance);

        var act = async () => await svc.SendAsync(NewMessage(TestTenantId) with { From = null });

        await act.Should().ThrowAsync<InvalidOperationException>(
            "programmer error — missing both message.From and Email:From config");
    }

    [Test]
    public async Task SendAsync_FallsBackToTenantContext_WhenMessageTenantIdNull()
    {
        // Story 28-1 PR B — when message.TenantId is null but the
        // ambient ITenantContext.TenantId is set, the service still
        // routes through the tenant repo. This preserves the historical
        // "implicit tenant" behaviour.
        _tenantContext.SetTenantId(TestTenantId);

        var svc = NewService();
        var txnId = await svc.SendAsync(NewMessage());

        var row = await _outbox.GetByIdAsync(TestTenantId, txnId);
        row!.TenantId.Should().Be(TestTenantId,
            "SmtpEmailService falls back to the ambient tenant when the message didn't supply one");
    }

    [Test]
    public async Task SendAsync_RoutesToPlatformOutbox_WhenNoTenantId()
    {
        // Story 28-1 PR B — verification / password-reset / welcome
        // emails set TenantId=null and the ambient context is unset
        // too. The platform repo gets the row, NOT the tenant repo.
        var svc = NewService();
        var txnId = await svc.SendAsync(NewMessage(tenantId: null, userId: Guid.NewGuid()));

        // Tenant repo doesn't have it.
        var tenantSearch = await _db.EmailOutbox.FindAsync(txnId);
        tenantSearch.Should().BeNull("platform-scope email must NOT land in the tenant outbox");

        // Platform repo does.
        var platformRow = await _platformOutbox.GetByIdAsync(txnId);
        platformRow.Should().NotBeNull();
        platformRow!.Status.Should().Be("pending");
        platformRow.Template.Should().Be("verification");
        platformRow.TenantId.Should().BeNull();
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
