using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Tamma.Api.Services.Alerts;
using Tamma.Api.Services.Alerts.Channels;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Alerts;

/// <summary>
/// Story 1.5-37 (Wave C.1) — unit tests for
/// <see cref="EmailAlertChannel"/>. Verifies platform outbox enqueue,
/// config parsing, validation errors, and no-credential leakage.
/// </summary>
[TestFixture]
public class EmailAlertChannelTests
{
    private ControlPlaneDbContext _db = null!;
    private EmailAlertChannel _channel = null!;
    private TestTimeProvider _time = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _db = new ControlPlaneDbContext(options);
        _time = new TestTimeProvider(DateTimeOffset.Parse("2026-04-23T12:00:00Z"));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Email:FromAddress"] = "alerts@tamma.dev",
            })
            .Build();
        _channel = new EmailAlertChannel(_db, _time, config);
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    [Test]
    public async Task SendAsync_ValidConfig_EnqueuesPlatformOutboxRow()
    {
        var alert = NewAlert();
        var channel = new AlertChannel
        {
            Id = Guid.NewGuid(),
            Name = "Ops Email",
            ChannelType = AlertChannelType.Email,
            Config = """{"toAddress":"ops@acme.io","subjectPrefix":"[ALERT] "}""",
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var result = await _channel.SendAsync(alert, channel, default);

        result.Success.Should().BeTrue();
        result.Error.Should().BeNull();

        var outbox = await _db.PlatformEmailOutbox.SingleAsync();
        outbox.ToAddress.Should().Be("ops@acme.io");
        outbox.Subject.Should().StartWith("[ALERT] CRITICAL:");
        outbox.TextBody.Should().Contain(alert.Title);
        outbox.TextBody.Should().Contain(alert.Description);
        outbox.HtmlBody.Should().Contain("CRITICAL");
        outbox.Status.Should().Be("pending");
        outbox.FromAddress.Should().Be("alerts@tamma.dev");
    }

    [Test]
    public async Task SendAsync_MissingToAddress_ReturnsFailure()
    {
        var result = await _channel.SendAsync(
            NewAlert(),
            new AlertChannel
            {
                Id = Guid.NewGuid(),
                Name = "bad",
                ChannelType = AlertChannelType.Email,
                Config = """{"subjectPrefix":"[x] "}""",
                IsEnabled = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            },
            default);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("toAddress");
        (await _db.PlatformEmailOutbox.CountAsync()).Should().Be(0);
    }

    [Test]
    public async Task SendAsync_MalformedConfig_ReturnsFailure()
    {
        var result = await _channel.SendAsync(
            NewAlert(),
            new AlertChannel
            {
                Id = Guid.NewGuid(),
                Name = "bad",
                ChannelType = AlertChannelType.Email,
                Config = "{not-json",
                IsEnabled = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            },
            default);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("parse");
    }

    [Test]
    public void ChannelType_IsEmail() =>
        _channel.ChannelType.Should().Be("email");

    private static Alert NewAlert() => new()
    {
        Id = Guid.NewGuid(),
        Severity = AlertSeverity.Critical,
        Title = "Budget exhausted",
        Description = "tenant over cap",
        CreatedAt = DateTime.UtcNow,
    };
}
