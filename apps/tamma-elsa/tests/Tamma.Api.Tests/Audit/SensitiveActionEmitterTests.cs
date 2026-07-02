using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Audit;
using Tamma.Core.Audit;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Audit;

/// <summary>
/// Story 37-10 (S1) — unit tests for <see cref="SensitiveActionEmitter"/>: the
/// single seam that validates the catalog, routes tenant vs platform, redacts
/// defensively, and never throws to the caller.
/// </summary>
[TestFixture]
public class SensitiveActionEmitterTests
{
    private Mock<IEventRepository> _events = null!;
    private Mock<IPlatformEventPublisher> _platform = null!;
    private DomainEvent? _appendedDomain;
    private PlatformEvent? _appendedPlatform;

    [SetUp]
    public void SetUp()
    {
        _appendedDomain = null;
        _appendedPlatform = null;

        _events = new Mock<IEventRepository>();
        _events.Setup(e => e.AppendAsync(It.IsAny<DomainEvent>()))
            .Callback<DomainEvent>(d => _appendedDomain = d)
            .ReturnsAsync((DomainEvent d) => d);

        _platform = new Mock<IPlatformEventPublisher>();
        _platform.Setup(p => p.AppendAndPublishAsync(It.IsAny<PlatformEvent>(), It.IsAny<CancellationToken>()))
            .Callback<PlatformEvent, CancellationToken>((p, _) => _appendedPlatform = p)
            .ReturnsAsync((PlatformEvent p, CancellationToken _) => p);
    }

    private SensitiveActionEmitter NewEmitter() =>
        new(_events.Object, _platform.Object, TimeProvider.System,
            NullLogger<SensitiveActionEmitter>.Instance);

    // ── Scope routing ──────────────────────────────────────────────────────

    [Test]
    public async Task Tenant_Scope_Appends_To_EventRepository_Not_Platform()
    {
        var tenantId = Guid.NewGuid();
        var actor = Guid.NewGuid();

        await NewEmitter().EmitAsync(SensitiveAction.ForTenant(
            SensitiveActionCatalog.ProviderKeyChanged, tenantId, actor,
            new Dictionary<string, string?> { ["provider"] = "anthropic" },
            new Dictionary<string, object?> { ["operation"] = "set" }));

        _events.Verify(e => e.AppendAsync(It.IsAny<DomainEvent>()), Times.Once);
        _platform.Verify(p => p.AppendAndPublishAsync(It.IsAny<PlatformEvent>(), It.IsAny<CancellationToken>()), Times.Never);

        _appendedDomain.Should().NotBeNull();
        _appendedDomain!.Type.Should().Be(SensitiveActionCatalog.ProviderKeyChanged);
        _appendedDomain.TenantId.Should().Be(tenantId);
        _appendedDomain.Metadata.Should().Contain("\"workflowVersion\":\"1.0.0\"");
        _appendedDomain.Metadata.Should().Contain("\"eventSource\":\"system\"");

        var tags = JsonSerializer.Deserialize<Dictionary<string, string?>>(_appendedDomain.Tags)!;
        tags["actorUserId"].Should().Be(actor.ToString("D"));
        tags["tenantId"].Should().Be(tenantId.ToString("D"));
        tags["provider"].Should().Be("anthropic");
    }

    [Test]
    public async Task Platform_Scope_Publishes_To_Platform_Not_EventRepository()
    {
        var tenantId = Guid.NewGuid();

        await NewEmitter().EmitAsync(SensitiveAction.ForPlatform(
            SensitiveActionCatalog.LoginSuccess, tenantId, Guid.NewGuid(),
            new Dictionary<string, string?> { ["ip"] = "203.0.113.5" }));

        _platform.Verify(p => p.AppendAndPublishAsync(It.IsAny<PlatformEvent>(), It.IsAny<CancellationToken>()), Times.Once);
        _events.Verify(e => e.AppendAsync(It.IsAny<DomainEvent>()), Times.Never);

        _appendedPlatform.Should().NotBeNull();
        _appendedPlatform!.Type.Should().Be(SensitiveActionCatalog.LoginSuccess);
        _appendedPlatform.TenantId.Should().Be(tenantId, "a platform event may still carry a tenant id");
    }

    [Test]
    public async Task Platform_Scope_With_Null_Tenant_Publishes_Platform_Only()
    {
        await NewEmitter().EmitAsync(SensitiveAction.ForPlatform(
            SensitiveActionCatalog.LoginFailure, tenantId: null, actorUserId: null,
            new Dictionary<string, string?> { ["reason"] = "bad_credentials" }));

        _appendedPlatform.Should().NotBeNull();
        _appendedPlatform!.TenantId.Should().BeNull();
    }

    // ── Catalog typo-guard ─────────────────────────────────────────────────

    [Test]
    public async Task Uncatalogued_Type_Is_Dropped_Neither_Sink_Called()
    {
        await NewEmitter().EmitAsync(SensitiveAction.ForTenant(
            "NOT.A.CATALOG.CODE", Guid.NewGuid(), Guid.NewGuid()));

        _events.Verify(e => e.AppendAsync(It.IsAny<DomainEvent>()), Times.Never);
        _platform.Verify(p => p.AppendAndPublishAsync(It.IsAny<PlatformEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Tenant_Scope_Without_TenantId_Is_Dropped()
    {
        await NewEmitter().EmitAsync(new SensitiveAction(
            SensitiveActionCatalog.ProviderKeyChanged, SensitiveActionScope.Tenant,
            TenantId: null, ActorUserId: Guid.NewGuid(),
            new Dictionary<string, string?>(), new Dictionary<string, object?>()));

        _events.Verify(e => e.AppendAsync(It.IsAny<DomainEvent>()), Times.Never);
    }

    // ── Never-throws contract ──────────────────────────────────────────────

    [Test]
    public async Task Sink_Failure_Is_Swallowed_Not_Rethrown()
    {
        _events.Setup(e => e.AppendAsync(It.IsAny<DomainEvent>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        var act = async () => await NewEmitter().EmitAsync(SensitiveAction.ForTenant(
            SensitiveActionCatalog.ProviderKeyChanged, Guid.NewGuid(), Guid.NewGuid()));

        await act.Should().NotThrowAsync("a failed audit emit must never break the action");
    }

    [Test]
    public async Task Platform_Sink_Failure_Is_Swallowed()
    {
        _platform.Setup(p => p.AppendAndPublishAsync(It.IsAny<PlatformEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("bus down"));

        var act = async () => await NewEmitter().EmitAsync(SensitiveAction.ForPlatform(
            SensitiveActionCatalog.LoginSuccess, null, Guid.NewGuid()));

        await act.Should().NotThrowAsync();
    }

    // ── Redaction (belt-and-suspenders) ────────────────────────────────────

    [Test]
    public async Task Secret_Shaped_Value_Is_Scrubbed_From_Data()
    {
        const string plaintext = "tamma_sk_LIVEDEADBEEF0123456789";

        await NewEmitter().EmitAsync(SensitiveAction.ForTenant(
            SensitiveActionCatalog.ProviderKeyChanged, Guid.NewGuid(), Guid.NewGuid(),
            data: new Dictionary<string, object?>
            {
                ["provider"] = "anthropic",
                ["note"] = $"raw={plaintext}",
            }));

        _appendedDomain!.Data.Should().NotContain(plaintext);
        _appendedDomain.Data.Should().Contain("[REDACTED]");
    }

    [Test]
    public async Task Denylisted_Key_Is_Replaced_With_Placeholder()
    {
        await NewEmitter().EmitAsync(SensitiveAction.ForTenant(
            SensitiveActionCatalog.ProviderKeyChanged, Guid.NewGuid(), Guid.NewGuid(),
            data: new Dictionary<string, object?>
            {
                ["provider"] = "anthropic",
                ["apiKey"] = "should-never-appear",
            }));

        _appendedDomain!.Data.Should().NotContain("should-never-appear");
        _appendedDomain.Data.Should().Contain("[REDACTED]");
    }
}
