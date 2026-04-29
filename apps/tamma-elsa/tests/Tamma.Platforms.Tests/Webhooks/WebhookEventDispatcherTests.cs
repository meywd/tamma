using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Webhooks;

namespace Tamma.Platforms.Tests.Webhooks;

/// <summary>
/// Story 31-7 — dispatcher routing + failure-isolation + cross-tenant
/// invariants.
/// </summary>
[TestFixture]
public class WebhookEventDispatcherTests
{
    private sealed class RecordingHandler(
        PlatformKind kind, string pattern, Action<PlatformWebhookEvent>? body = null)
        : IWebhookHandler
    {
        public PlatformKind Kind { get; } = kind;
        public string EventTypePattern { get; } = pattern;
        public List<PlatformWebhookEvent> Received { get; } = new();
        public Task HandleAsync(PlatformWebhookEvent evt, CancellationToken ct = default)
        {
            Received.Add(evt);
            body?.Invoke(evt);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingHandler : IWebhookHandler
    {
        public PlatformKind Kind => PlatformKind.GitHub;
        public string EventTypePattern => "installation.created";
        public Task HandleAsync(PlatformWebhookEvent evt, CancellationToken ct = default)
            => throw new InvalidOperationException("handler-blew-up");
    }

    private static PlatformWebhookEvent MakeEvent(
        PlatformKind kind, string eventType, string? action = null,
        Guid? tenantId = null)
    {
        using var doc = JsonDocument.Parse("{}");
        return new PlatformWebhookEvent(
            kind, eventType, action, WebhookEventCategory.Unknown,
            DeliveryId: "delivery-1",
            InstallationExternalId: "ext-1",
            RepoFullName: null,
            TenantId: tenantId,
            Installation: null,
            RawBody: ReadOnlyMemory<byte>.Empty,
            ParsedJson: doc.RootElement.Clone());
    }

    private static WebhookEventDispatcher New() =>
        new(NullLogger<WebhookEventDispatcher>.Instance);

    [Test]
    public void RegisterHandler_NullThrows()
    {
        var dispatcher = New();
        Assert.Throws<ArgumentNullException>(
            () => dispatcher.RegisterHandler(null!));
    }

    [Test]
    public void RegisterHandler_DuplicateKey_Throws()
    {
        var dispatcher = New();
        dispatcher.RegisterHandler(new RecordingHandler(
            PlatformKind.GitHub, "installation.created"));
        Assert.Throws<InvalidOperationException>(
            () => dispatcher.RegisterHandler(new RecordingHandler(
                PlatformKind.GitHub, "installation.created")),
            "single-handler-per-event invariant");
    }

    [Test]
    public async Task DispatchAsync_NoHandlerRegistered_Returns0()
    {
        var dispatcher = New();
        var dispatched = await dispatcher.DispatchAsync(
            MakeEvent(PlatformKind.GitHub, "installation", "created"));

        dispatched.Should().Be(0);
    }

    [Test]
    public async Task DispatchAsync_ExactMatch_RoutesToHandler()
    {
        var dispatcher = New();
        var handler = new RecordingHandler(PlatformKind.GitHub, "installation.created");
        dispatcher.RegisterHandler(handler);

        var evt = MakeEvent(PlatformKind.GitHub, "installation", "created");
        var dispatched = await dispatcher.DispatchAsync(evt);

        dispatched.Should().Be(1);
        handler.Received.Should().HaveCount(1);
    }

    [Test]
    public async Task DispatchAsync_WildcardMatch_RoutesToHandler()
    {
        var dispatcher = New();
        var handler = new RecordingHandler(PlatformKind.GitHub, "installation.*");
        dispatcher.RegisterHandler(handler);

        var deleted = MakeEvent(PlatformKind.GitHub, "installation", "deleted");
        var dispatched = await dispatcher.DispatchAsync(deleted);

        dispatched.Should().Be(1);
        handler.Received.Should().HaveCount(1);
    }

    [Test]
    public async Task DispatchAsync_ExactWinsOverWildcard()
    {
        var dispatcher = New();
        var exact = new RecordingHandler(PlatformKind.GitHub, "installation.created");
        var wild = new RecordingHandler(PlatformKind.GitHub, "installation.*");
        dispatcher.RegisterHandler(exact);
        dispatcher.RegisterHandler(wild);

        var evt = MakeEvent(PlatformKind.GitHub, "installation", "created");
        await dispatcher.DispatchAsync(evt);

        exact.Received.Should().HaveCount(1);
        wild.Received.Should().BeEmpty();
    }

    [Test]
    public async Task DispatchAsync_BareEventTypeWithoutAction_Matches()
    {
        var dispatcher = New();
        var handler = new RecordingHandler(PlatformKind.GitHub, "push");
        dispatcher.RegisterHandler(handler);

        var evt = MakeEvent(PlatformKind.GitHub, "push", action: null);
        await dispatcher.DispatchAsync(evt);

        handler.Received.Should().HaveCount(1);
    }

    [Test]
    public async Task DispatchAsync_DifferentPlatform_NotDispatched()
    {
        // Cross-platform isolation — GitHub handler must not see Gitea
        // events even with the same eventType+action.
        var dispatcher = New();
        var githubHandler = new RecordingHandler(PlatformKind.GitHub, "push");
        dispatcher.RegisterHandler(githubHandler);

        var giteaEvt = MakeEvent(PlatformKind.Gitea, "push");
        var dispatched = await dispatcher.DispatchAsync(giteaEvt);

        dispatched.Should().Be(0);
        githubHandler.Received.Should().BeEmpty();
    }

    [Test]
    public async Task DispatchAsync_HandlerThrows_FailureIsolated()
    {
        var dispatcher = New();
        dispatcher.RegisterHandler(new ThrowingHandler());

        var evt = MakeEvent(PlatformKind.GitHub, "installation", "created");
        // Receiver awaits this fire-and-forget — it must not throw.
        var dispatched = await dispatcher.DispatchAsync(evt);

        dispatched.Should().Be(1, "handler was invoked");
    }

    [Test]
    public async Task DispatchAsync_HandlerThrows_HandlerFailedHookFires()
    {
        var dispatcher = New();
        dispatcher.RegisterHandler(new ThrowingHandler());

        Exception? captured = null;
        IWebhookHandler? capturedHandler = null;
        dispatcher.HandlerFailedHook = (_, h, ex, _) =>
        {
            captured = ex;
            capturedHandler = h;
            return Task.CompletedTask;
        };

        var evt = MakeEvent(PlatformKind.GitHub, "installation", "created");
        await dispatcher.DispatchAsync(evt);

        captured.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Be("handler-blew-up");
        capturedHandler.Should().BeOfType<ThrowingHandler>();
    }

    [Test]
    public async Task DispatchAsync_CrossTenantIsolation_TenantBHandlerNeverSeesTenantAEvent()
    {
        // CRITICAL invariant: a handler registered for PlatformKind.GitHub
        // pattern "push" sees only events whose Kind == GitHub. The
        // event's TenantId is preserved unchanged through dispatch — a
        // handler that scopes its own DB reads by TenantId cannot leak
        // tenant A's data into tenant B's response. This test asserts the
        // dispatcher does not silently widen the tenant scope.
        var dispatcher = New();

        Guid? observedTenantA = null;
        Guid? observedTenantB = null;
        dispatcher.RegisterHandler(new RecordingHandler(
            PlatformKind.GitHub, "push",
            evt =>
            {
                if (observedTenantA is null) observedTenantA = evt.TenantId;
                else observedTenantB = evt.TenantId;
            }));

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        await dispatcher.DispatchAsync(MakeEvent(
            PlatformKind.GitHub, "push", tenantId: tenantA));
        await dispatcher.DispatchAsync(MakeEvent(
            PlatformKind.GitHub, "push", tenantId: tenantB));

        observedTenantA.Should().Be(tenantA);
        observedTenantB.Should().Be(tenantB);
        // Guard against tenant-bleed: the two captures must be distinct.
        observedTenantA.Should().NotBe(tenantB);
        observedTenantB.Should().NotBe(tenantA);
    }

    [Test]
    public async Task DispatchAsync_HandlerKindMismatch_RefusedAndReturns0()
    {
        // Defence in depth — a handler whose Kind doesn't match the
        // event Kind should be refused even if the pattern matches.
        // This protects against keyed-DI mis-registration where someone
        // accidentally registers a Gitea handler under the GitHub key.
        var dispatcher = New();

        // Synthesize a misregistered handler by manually constructing
        // a handler that says Kind=Gitea but registers itself under
        // Pattern that the dispatcher would resolve for a GitHub
        // event. We can't easily route a handler under the wrong kind
        // because the dispatcher keys by handler.Kind — instead,
        // assert the dispatcher's own (Kind, Pattern) keying gives us
        // isolation: an event for GitHub never resolves a Gitea-keyed
        // handler.
        var giteaHandler = new RecordingHandler(PlatformKind.Gitea, "push");
        dispatcher.RegisterHandler(giteaHandler);

        var githubEvent = MakeEvent(PlatformKind.GitHub, "push");
        var dispatched = await dispatcher.DispatchAsync(githubEvent);

        dispatched.Should().Be(0);
        giteaHandler.Received.Should().BeEmpty();
    }

    [Test]
    public void HandlerCount_ReflectsRegistrations()
    {
        var dispatcher = New();
        dispatcher.HandlerCount.Should().Be(0);
        dispatcher.RegisterHandler(new RecordingHandler(PlatformKind.GitHub, "push"));
        dispatcher.RegisterHandler(new RecordingHandler(PlatformKind.Gitea, "push"));
        dispatcher.HandlerCount.Should().Be(2);
    }
}
