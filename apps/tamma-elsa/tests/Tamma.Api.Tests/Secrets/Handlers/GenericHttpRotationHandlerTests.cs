using System.Net;
using System.Net.Http;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Activities.SecretsRotation.Contracts;
using Tamma.Api.Services.Secrets.Handlers;

namespace Tamma.Api.Tests.Secrets.Handlers;

/// <summary>
/// Story 29-6 AC4 — unit tests for the fallback
/// <see cref="GenericHttpRotationHandler"/>. Uses a fake
/// <see cref="HttpMessageHandler"/> to record outbound calls.
/// </summary>
[TestFixture]
public class GenericHttpRotationHandlerTests
{
    [Test]
    public async Task PushAsync_PostsJsonBodyWithCorrelationHeader()
    {
        var fake = new FakeHandler(HttpStatusCode.OK);
        var client = new HttpClient(fake);
        var handler = new GenericHttpRotationHandler(client, NullLogger<GenericHttpRotationHandler>.Instance);

        var target = new RotationTarget(Guid.NewGuid(), "x", null, "custom", "id=1", 2, 1);
        var ctx = new RotationContext("rot_1", Guid.Empty, DryRun: false,
            new Dictionary<string, string> { ["WebhookUrl"] = "https://example.com/hook" });

        await handler.PushAsync(target, "hunter2", ctx, default);

        fake.LastRequest!.Method.Should().Be(HttpMethod.Post);
        fake.LastRequest!.RequestUri!.ToString().Should().Be("https://example.com/hook");
        fake.LastRequest!.Headers.GetValues("X-Tamma-Rotation-Id").Should().Contain("rot_1");
        (fake.LastRequestBody ?? string.Empty).Should().Contain("rotationCorrelationId");
        (fake.LastRequestBody ?? string.Empty).Should().Contain("hunter2");
    }

    [Test]
    public async Task PushAsync_DryRun_DoesNotCallHttp()
    {
        var fake = new FakeHandler(HttpStatusCode.OK);
        var client = new HttpClient(fake);
        var handler = new GenericHttpRotationHandler(client, NullLogger<GenericHttpRotationHandler>.Instance);

        var target = new RotationTarget(Guid.NewGuid(), "x", null, "custom", "id=1", 2, 1);
        var ctx = new RotationContext("rot_1", Guid.Empty, DryRun: true,
            new Dictionary<string, string> { ["WebhookUrl"] = "https://example.com/hook" });
        await handler.PushAsync(target, "hunter2", ctx, default);
        fake.LastRequest.Should().BeNull();
    }

    [Test]
    public async Task PushAsync_NoWebhookUrl_Throws()
    {
        var fake = new FakeHandler(HttpStatusCode.OK);
        var client = new HttpClient(fake);
        var handler = new GenericHttpRotationHandler(client, NullLogger<GenericHttpRotationHandler>.Instance);
        var target = new RotationTarget(Guid.NewGuid(), "x", null, "custom", "id=1", 1, 0);
        var ctx = RotationContext.ForCorrelation("rot_1");
        Func<Task> act = () => handler.PushAsync(target, "x", ctx, default);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Test]
    public async Task PushAsync_Non2xx_Throws()
    {
        var fake = new FakeHandler(HttpStatusCode.InternalServerError);
        var client = new HttpClient(fake);
        var handler = new GenericHttpRotationHandler(client, NullLogger<GenericHttpRotationHandler>.Instance);
        var target = new RotationTarget(Guid.NewGuid(), "x", null, "custom", "id=1", 2, 1);
        var ctx = new RotationContext("rot_1", Guid.Empty, false,
            new Dictionary<string, string> { ["WebhookUrl"] = "https://example.com/hook" });
        Func<Task> act = () => handler.PushAsync(target, "x", ctx, default);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Test]
    public async Task ProbeAsync_NoProbeUrl_ReturnsHealthy()
    {
        var fake = new FakeHandler(HttpStatusCode.OK);
        var client = new HttpClient(fake);
        var handler = new GenericHttpRotationHandler(client, NullLogger<GenericHttpRotationHandler>.Instance);
        var target = new RotationTarget(Guid.NewGuid(), "x", null, "custom", "id=1", 1, 0);
        var result = await handler.ProbeAsync(target, RotationContext.ForCorrelation("rot"), default);
        result.Status.Should().Be(ProbeStatus.Healthy);
        fake.LastRequest.Should().BeNull();
    }

    [Test]
    public async Task ProbeAsync_200_Healthy()
    {
        var fake = new FakeHandler(HttpStatusCode.OK);
        var client = new HttpClient(fake);
        var handler = new GenericHttpRotationHandler(client, NullLogger<GenericHttpRotationHandler>.Instance);
        var target = new RotationTarget(Guid.NewGuid(), "x", null, "custom", "id=1", 1, 0);
        var ctx = new RotationContext("r", Guid.Empty, false,
            new Dictionary<string, string> { ["ProbeUrl"] = "https://example.com/health" });
        var result = await handler.ProbeAsync(target, ctx, default);
        result.Status.Should().Be(ProbeStatus.Healthy);
    }

    [Test]
    public async Task ProbeAsync_503_Unhealthy_WithReason()
    {
        var fake = new FakeHandler(HttpStatusCode.ServiceUnavailable);
        var client = new HttpClient(fake);
        var handler = new GenericHttpRotationHandler(client, NullLogger<GenericHttpRotationHandler>.Instance);
        var target = new RotationTarget(Guid.NewGuid(), "x", null, "custom", "id=1", 1, 0);
        var ctx = new RotationContext("r", Guid.Empty, false,
            new Dictionary<string, string> { ["ProbeUrl"] = "https://example.com/health" });
        var result = await handler.ProbeAsync(target, ctx, default);
        result.Status.Should().Be(ProbeStatus.Unhealthy);
        result.Reason.Should().Be("http_503");
    }

    [Test]
    public async Task PushAsync_WithSigningKey_SetsHmacHeader()
    {
        var fake = new FakeHandler(HttpStatusCode.OK);
        var client = new HttpClient(fake);
        var handler = new GenericHttpRotationHandler(client, NullLogger<GenericHttpRotationHandler>.Instance);
        var target = new RotationTarget(Guid.NewGuid(), "x", null, "custom", "id=1", 2, 1);
        var ctx = new RotationContext("rot", Guid.Empty, false,
            new Dictionary<string, string>
            {
                ["WebhookUrl"] = "https://example.com/hook",
                ["SigningKey"] = "abc",
            });
        await handler.PushAsync(target, "hunter2", ctx, default);
        fake.LastRequest!.Headers.Contains("X-Tamma-Signature").Should().BeTrue();
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        public FakeHandler(HttpStatusCode status) => _status = status;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_status);
        }
    }
}
