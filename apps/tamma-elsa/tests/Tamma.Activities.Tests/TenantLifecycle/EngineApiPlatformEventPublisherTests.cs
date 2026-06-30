using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Activities.LlmCall;
using Tamma.Activities.TenantLifecycle;
using Tamma.Data.Entities;

namespace Tamma.Activities.Tests.TenantLifecycle;

/// <summary>
/// Task 3 — unit tests for <see cref="EngineApiPlatformEventPublisher"/>.
///
/// <para>The publisher is a singleton that resolves <see cref="TammaApiClient"/>
/// per-call via <see cref="IServiceScopeFactory"/> to avoid the captive-dependency
/// trap. Tests use a real <see cref="ServiceCollection"/> registered with the
/// pre-wired client so <c>GetRequiredService&lt;TammaApiClient&gt;()</c> works
/// exactly as in production — no Moq seam needed for the scope factory.</para>
/// </summary>
[TestFixture]
public class EngineApiPlatformEventPublisherTests
{
    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static TammaApiClient BuildClient(StubHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = null };
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tamma:ApiUrl"] = "http://tamma.test",
            })
            .Build();
        return new TammaApiClient(http, NullLogger<TammaApiClient>.Instance, cfg);
    }

    /// <summary>
    /// Wire a real DI container so <c>GetRequiredService&lt;TammaApiClient&gt;()</c>
    /// inside the publisher returns the pre-built client (backed by <paramref name="api"/>).
    /// The publisher is NOT registered here — the test constructs it directly.
    /// </summary>
    private static EngineApiPlatformEventPublisher NewPublisher(TammaApiClient api)
    {
        var services = new ServiceCollection();
        services.AddSingleton(api);
        var rootProvider = services.BuildServiceProvider();
        var scopeFactory = rootProvider.GetRequiredService<IServiceScopeFactory>();
        return new EngineApiPlatformEventPublisher(
            scopeFactory,
            NullLogger<EngineApiPlatformEventPublisher>.Instance);
    }

    // -----------------------------------------------------------------------
    // Tests
    // -----------------------------------------------------------------------

    [Test]
    public async Task AppendAndPublishAsync_Posts_Event_And_Returns_It_On_Success()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHandler(req =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.Created);
        });
        var api = BuildClient(handler);
        var pub = NewPublisher(api);
        var evt = new PlatformEvent
        {
            Id = Guid.NewGuid(),
            Type = "TENANT.DELETED.SUCCESS",
            TenantId = Guid.NewGuid(),
            Tags = "{}",
            Metadata = "{}",
            Data = "{}",
        };

        var result = await pub.AppendAndPublishAsync(evt, CancellationToken.None);

        Assert.That(result, Is.SameAs(evt));
        Assert.That(captured, Is.Not.Null, "publisher must POST to the API");
        Assert.That(captured!.RequestUri!.AbsolutePath, Is.EqualTo("/api/engine/platform-events"));
    }

    [Test]
    public async Task AppendAndPublishAsync_Returns_Null_And_Does_Not_Throw_On_Post_Failure()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var api = BuildClient(handler);
        var pub = NewPublisher(api);
        var evt = new PlatformEvent
        {
            Id = Guid.NewGuid(),
            Type = "X",
            Tags = "{}",
            Metadata = "{}",
            Data = "{}",
        };

        // Must NOT throw — degraded path returns null so lifecycle workflows
        // complete even when the control-plane callback is temporarily unavailable.
        var result = await pub.AppendAndPublishAsync(evt, CancellationToken.None);

        Assert.That(result, Is.Null, "degraded: logged at WARN, not thrown (mirrors Null seam philosophy)");
    }

    [Test]
    public async Task AppendAndPublishAsync_Null_Event_Returns_Null_Without_Posting()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        var api = BuildClient(handler);
        var pub = NewPublisher(api);

        // Act — null guard must short-circuit before network I/O.
        var result = await pub.AppendAndPublishAsync(null!, CancellationToken.None);

        Assert.That(result, Is.Null);
        Assert.That(handler.LastRequest, Is.Null, "null event must not trigger a POST");
    }

    [Test]
    public async Task AppendAndPublishAsync_Malformed_Tags_Returns_Null_And_Does_Not_Throw()
    {
        // "[1,2,3]" is valid JSON but cannot be deserialized as Dictionary<string,string?>.
        // Before FIX 1 this would have thrown JsonException, aborting the lifecycle workflow.
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        var api = BuildClient(handler);
        var pub = NewPublisher(api);
        var evt = new PlatformEvent
        {
            Id = Guid.NewGuid(),
            Type = "TENANT.DROPPED.SUCCESS",
            TenantId = Guid.NewGuid(),
            Tags = "[1,2,3]",   // not a JSON object — ParseTags will throw without FIX 1
            Metadata = "{}",
            Data = "{}",
        };

        // Must NOT throw — the catch block returns null and logs WARN instead.
        var result = await pub.AppendAndPublishAsync(evt, CancellationToken.None);

        Assert.That(result, Is.Null, "malformed jsonb must degrade to null, not throw");
    }

    [Test]
    public async Task AppendAndPublishAsync_NonEmpty_Tags_Round_Trips_Through_Mapping()
    {
        // Verifies that ParseTags / ToJsonElement are actually exercised (not short-circuited
        // by the null/"{}" early-return) and that the publisher still returns the event on success.
        string? capturedBody = null;
        var handler = new StubHandler(req =>
        {
            // Read synchronously inside the callback (the handler is invoked on the test thread).
            capturedBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.Created);
        });
        var api = BuildClient(handler);
        var pub = NewPublisher(api);
        var evt = new PlatformEvent
        {
            Id = Guid.NewGuid(),
            Type = "TENANT.SCHEMA.DROPPED",
            TenantId = Guid.NewGuid(),
            Tags = "{\"step\":\"drop_schema\"}",
            Metadata = "{\"workflowVersion\":\"1.0.0\"}",
            Data = "{\"foo\":1}",
        };

        var result = await pub.AppendAndPublishAsync(evt, CancellationToken.None);

        Assert.That(result, Is.SameAs(evt), "non-empty tags must succeed and return the event");
        Assert.That(capturedBody, Is.Not.Null, "publisher must POST a request body");
        // Confirm the mapping ran — the serialized body must contain the tag key/value.
        Assert.That(capturedBody, Does.Contain("drop_schema"),
            "captured request body must include the mapped tag value");
    }

    // -----------------------------------------------------------------------
    // Test double
    // -----------------------------------------------------------------------

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public HttpRequestMessage? LastRequest { get; private set; }

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
            => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(_respond(request));
        }
    }
}
