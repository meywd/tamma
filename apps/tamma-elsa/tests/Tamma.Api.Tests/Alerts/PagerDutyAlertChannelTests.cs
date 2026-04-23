using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.Alerts;
using Tamma.Api.Services.Alerts.Channels;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Alerts;

/// <summary>
/// Story 1.5-37 (Wave C.1) — unit tests for
/// <see cref="PagerDutyAlertChannel"/>. Exercises Events v2 payload
/// shape (routing_key resolved from secret store, dedup_key =
/// alert.Id) and severity passthrough.
/// </summary>
[TestFixture]
public class PagerDutyAlertChannelTests
{
    private WebhookAlertChannelTests.StubHttpHandler _handler = null!;
    private WebhookAlertChannelTests.StubSecretReader _secrets = null!;
    private PagerDutyAlertChannel _channel = null!;

    [SetUp]
    public void SetUp()
    {
        _handler = new WebhookAlertChannelTests.StubHttpHandler();
        _secrets = new WebhookAlertChannelTests.StubSecretReader();
        _channel = new PagerDutyAlertChannel(
            new WebhookAlertChannelTests.StubHttpFactory(_handler),
            _secrets,
            NullLogger<PagerDutyAlertChannel>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _handler.Dispose();
    }

    [Test]
    public async Task SendAsync_Success_PostsEventsV2PayloadToPagerDuty()
    {
        var secretId = Guid.NewGuid();
        _secrets.Plaintext[secretId] = "pd-routing-key-123";
        _handler.Response = new HttpResponseMessage(HttpStatusCode.Accepted);

        var alertId = Guid.NewGuid();
        var alert = new Alert
        {
            Id = alertId,
            Severity = AlertSeverity.Critical,
            Title = "Secret rotation failed",
            Description = "handler threw mid-rotate",
            CreatedAt = DateTime.UtcNow,
        };
        var channel = new AlertChannel
        {
            Id = Guid.NewGuid(),
            Name = "Platform Oncall",
            ChannelType = AlertChannelType.PagerDuty,
            Config = "{}",
            IsEnabled = true,
            CredentialsSecretId = secretId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var result = await _channel.SendAsync(alert, channel, default);
        result.Success.Should().BeTrue();

        _handler.LastRequest!.RequestUri!.ToString()
            .Should().Be(PagerDutyAlertChannel.EventsApiUrl);

        using var doc = JsonDocument.Parse(_handler.LastRequestBody!);
        doc.RootElement.GetProperty("routing_key").GetString()
            .Should().Be("pd-routing-key-123");
        doc.RootElement.GetProperty("event_action").GetString()
            .Should().Be("trigger");
        doc.RootElement.GetProperty("dedup_key").GetString()
            .Should().Be(alertId.ToString("D"),
                "dedup_key must be the alert id so re-deliveries don't re-page");
        doc.RootElement.GetProperty("payload")
            .GetProperty("severity").GetString()
            .Should().Be("critical");
    }

    [Test]
    public async Task SendAsync_RejectsMissingCredentials()
    {
        var result = await _channel.SendAsync(
            new Alert
            {
                Id = Guid.NewGuid(),
                Severity = AlertSeverity.Warning,
                Title = "x",
                Description = "y",
                CreatedAt = DateTime.UtcNow,
            },
            new AlertChannel
            {
                Id = Guid.NewGuid(),
                Name = "pd",
                ChannelType = AlertChannelType.PagerDuty,
                Config = "{}",
                IsEnabled = true,
                CredentialsSecretId = null,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            },
            default);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("routing_key");
    }

    [Test]
    public void ChannelType_IsPagerDuty() =>
        _channel.ChannelType.Should().Be("pagerduty");
}
