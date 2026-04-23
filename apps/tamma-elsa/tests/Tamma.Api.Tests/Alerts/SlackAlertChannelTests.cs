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
/// <see cref="SlackAlertChannel"/>. Exercises the webhook POST
/// contract, severity-colour mapping, and secret-store plumbing.
/// Reuses the stub HTTP handler from <see cref="WebhookAlertChannelTests"/>.
/// </summary>
[TestFixture]
public class SlackAlertChannelTests
{
    private WebhookAlertChannelTests.StubHttpHandler _handler = null!;
    private SlackAlertChannel _channel = null!;
    private WebhookAlertChannelTests.StubSecretReader _secrets = null!;

    [SetUp]
    public void SetUp()
    {
        _handler = new WebhookAlertChannelTests.StubHttpHandler();
        _secrets = new WebhookAlertChannelTests.StubSecretReader();
        _channel = new SlackAlertChannel(
            new WebhookAlertChannelTests.StubHttpFactory(_handler),
            _secrets,
            NullLogger<SlackAlertChannel>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _handler.Dispose();
    }

    [Test]
    public async Task SendAsync_Success_PostsJsonPayloadToWebhookUrl()
    {
        var secretId = Guid.NewGuid();
        _secrets.Plaintext[secretId] = "https://hooks.slack.com/services/XXX";
        _handler.Response = new HttpResponseMessage(HttpStatusCode.OK);

        var alert = new Alert
        {
            Id = Guid.NewGuid(),
            Severity = AlertSeverity.Critical,
            Title = "Production DB unreachable",
            Description = "3 consecutive probe failures",
            CreatedAt = DateTime.UtcNow,
        };
        var channel = new AlertChannel
        {
            Id = Guid.NewGuid(),
            Name = "Ops Slack",
            ChannelType = AlertChannelType.Slack,
            Config = "{}",
            IsEnabled = true,
            CredentialsSecretId = secretId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var result = await _channel.SendAsync(alert, channel, default);

        result.Success.Should().BeTrue();
        _handler.LastRequest!.RequestUri!.ToString()
            .Should().Be("https://hooks.slack.com/services/XXX");
        _handler.LastRequest.Method.Should().Be(HttpMethod.Post);
        _handler.LastRequestBody.Should().NotBeNullOrEmpty();

        using var doc = JsonDocument.Parse(_handler.LastRequestBody!);
        doc.RootElement.GetProperty("text").GetString()
            .Should().Contain("CRITICAL");
        var color = doc.RootElement
            .GetProperty("attachments")[0]
            .GetProperty("color").GetString();
        color.Should().Be("#c0392b", "critical severity should map to red");
    }

    [Test]
    public async Task SendAsync_MissingCredentials_Fails()
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
                Name = "slack",
                ChannelType = AlertChannelType.Slack,
                Config = "{}",
                IsEnabled = true,
                CredentialsSecretId = null,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            },
            default);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("CredentialsSecretId");
    }

    [Test]
    public async Task SendAsync_Non2xxResponse_ReturnsFailureWithStatusCode()
    {
        var secretId = Guid.NewGuid();
        _secrets.Plaintext[secretId] = "https://hooks.slack.com/services/YYY";
        _handler.Response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);

        var result = await _channel.SendAsync(
            new Alert
            {
                Id = Guid.NewGuid(),
                Severity = AlertSeverity.Info,
                Title = "t",
                Description = "d",
                CreatedAt = DateTime.UtcNow,
            },
            new AlertChannel
            {
                Id = Guid.NewGuid(),
                Name = "slack",
                ChannelType = AlertChannelType.Slack,
                Config = "{}",
                IsEnabled = true,
                CredentialsSecretId = secretId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            },
            default);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("429");
    }

    [Test]
    public void ChannelType_IsSlack() =>
        _channel.ChannelType.Should().Be("slack");
}
