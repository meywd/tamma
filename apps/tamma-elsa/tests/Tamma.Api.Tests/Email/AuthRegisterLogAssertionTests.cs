using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Tamma.Api.Auth;
using Tamma.Api.Dtos.Auth;
using Tamma.Api.Endpoints;
using Tamma.Api.Services.Email;
using Tamma.Api.Tests.Infrastructure;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Email;

/// <summary>
/// Unit-level counterpart to <see cref="AuthRegisterTxnIdIntegrationTests"/>.
/// Exercises <see cref="AuthEndpoints.Register"/> directly with a mocked
/// <see cref="IEmailService"/> and a <see cref="CapturingLoggerProvider"/> so
/// we can assert the log-line contract (txn id yes, recipient/subject no)
/// without fighting Serilog's hosted logger-factory replacement.
/// </summary>
[TestFixture]
public class AuthRegisterLogAssertionTests
{
    private CapturingLoggerProvider _logProvider = null!;
    private ILoggerFactory _loggerFactory = null!;

    [SetUp]
    public void SetUp()
    {
        _logProvider = new CapturingLoggerProvider();
        _loggerFactory = LoggerFactory.Create(b => b.AddProvider(_logProvider));
    }

    [TearDown]
    public void TearDown()
    {
        _loggerFactory.Dispose();
        _logProvider.Dispose();
    }

    [Test]
    public async Task Register_LogsTxnIdAndNoRecipient()
    {
        // ── Fixture ──
        var userRepo = new Mock<IUserRepository>();
        userRepo.Setup(r => r.GetByEmailAsync(It.IsAny<string>()))
            .ReturnsAsync((User?)null);

        var createdUserId = Guid.NewGuid();
        userRepo.Setup(r => r.CreateAsync(It.IsAny<User>()))
            .ReturnsAsync((User u) =>
            {
                u.Id = createdUserId;
                return u;
            });
        userRepo.Setup(r => r.UpdateActiveTenantAsync(It.IsAny<Guid>(), It.IsAny<Guid>()))
            .Returns(Task.CompletedTask);

        var passwordSvc = new Mock<IPasswordService>();
        passwordSvc.Setup(p => p.HashPassword(It.IsAny<string>())).Returns("hash");

        var tenantRepo = new Mock<ITenantRepository>();
        var createdTenantId = Guid.NewGuid();
        tenantRepo.Setup(r => r.CreateAsync(It.IsAny<Tenant>()))
            .ReturnsAsync((Tenant t) =>
            {
                t.Id = createdTenantId;
                return t;
            });

        var membershipRepo = new Mock<ITenantMembershipRepository>();
        membershipRepo.Setup(r => r.AddAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>()))
            .ReturnsAsync(new TenantMembership());

        // PF-S9 — bootstrap claim returns false for this test (any
        // already-existing user → no platform_admin promotion).
        var bootstrapRepo = new Mock<IPlatformBootstrapRepository>();
        bootstrapRepo.Setup(r => r.TryClaimAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var txnId = Guid.NewGuid();
        var emailSvc = new Mock<IEmailService>();
        emailSvc.Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(txnId);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Dashboard:Url"] = "https://dash.test.tamma.dev",
            })
            .Build();

        var req = new RegisterRequest(
            Email: "user-under-test@example.com",
            Password: "Sup3rSecure!",
            DisplayName: "Test User");

        // ── Act ──
        await AuthEndpoints.Register(
            req, userRepo.Object, passwordSvc.Object,
            tenantRepo.Object, membershipRepo.Object,
            bootstrapRepo.Object,
            emailSvc.Object, config, _loggerFactory);

        // ── Assert ──
        _logProvider.Messages.Should().Contain(
            m => m.Contains(txnId.ToString()),
            "Register must log the transaction id returned by SendAsync");

        _logProvider.Messages.Should().NotContain(
            m => m.Contains("user-under-test@example.com"),
            "recipient address must never appear in any log line");

        // SendAsync was called with a message tagged with the template
        // name and the user id. Story 28-1 PR B — TenantId is null on
        // verification emails (platform-scope: no tenant DB exists yet
        // at registration). The user id is preserved for correlation.
        emailSvc.Verify(s => s.SendAsync(
            It.Is<EmailMessage>(m =>
                m.Template == "verification" &&
                m.UserId == createdUserId &&
                m.TenantId == null),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
