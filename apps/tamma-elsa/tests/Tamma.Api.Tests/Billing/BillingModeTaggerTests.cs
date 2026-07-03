using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Billing;
using Tamma.Api.Services.Pricing;
using Tamma.Core;
using Tamma.Core.Enums;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Billing;

/// <summary>
/// Story 35-2 — <see cref="BillingModeTagger"/> computes the canonical
/// <c>billing_mode</c> token by reading Story 34-3's owner and reconciling Story
/// 32-3's runtime credential source. On disagreement 32-3 WINS (it is the wire
/// credential) + WARN + one <c>BILLING.MODE.MISMATCH</c> event; an out-of-domain
/// token fails loud (AC11). The single-user <see cref="NullBillingModeTagger"/>
/// yields platform with no billable implication.
/// </summary>
[TestFixture]
public class BillingModeTaggerTests
{
    private sealed class StubOwner : ITenantProviderBillingResolver
    {
        private readonly MetricBillingMode _mode;
        public StubOwner(MetricBillingMode mode) => _mode = mode;
        public Task<MetricBillingMode> ResolveModeAsync(
            Guid? tenantId, string provider, CancellationToken ct = default)
            => Task.FromResult(_mode);
    }

    private static (BillingModeTagger tagger, List<DomainEvent> events) NewTagger(MetricBillingMode ownerMode)
    {
        var captured = new List<DomainEvent>();
        var repo = new Mock<IEventRepository>();
        repo.Setup(r => r.AppendAsync(It.IsAny<DomainEvent>()))
            .ReturnsAsync((DomainEvent d) => d)
            .Callback((DomainEvent d) => captured.Add(d));
        var tagger = new BillingModeTagger(
            new StubOwner(ownerMode), repo.Object, NullLogger<BillingModeTagger>.Instance);
        return (tagger, captured);
    }

    [Test]
    public async Task Resolve_OwnerByok_NoSource_TagsByok()
    {
        var (tagger, events) = NewTagger(MetricBillingMode.Byok);
        var tag = await tagger.ResolveTagAsync(Guid.NewGuid(), "anthropic");
        tag.Should().Be(BillingModeTokens.Byok);
        events.Should().BeEmpty("no 32-3 source ⇒ nothing to reconcile ⇒ no mismatch event");
    }

    [Test]
    public async Task Resolve_OwnerPlatform_NoSource_TagsPlatform()
    {
        var (tagger, _) = NewTagger(MetricBillingMode.PlatformProvided);
        (await tagger.ResolveTagAsync(Guid.NewGuid(), "anthropic")).Should().Be(BillingModeTokens.Platform);
    }

    [Test]
    public async Task Resolve_SourceAgrees_TagMatches_NoMismatchEvent()
    {
        var (tagger, events) = NewTagger(MetricBillingMode.Byok);
        var tag = await tagger.ResolveTagAsync(Guid.NewGuid(), "anthropic", credentialSource: "byok");
        tag.Should().Be(BillingModeTokens.Byok);
        events.Should().BeEmpty("agreement ⇒ no BILLING.MODE.MISMATCH");
    }

    [Test]
    public async Task Resolve_SourceDisagrees_32_3Wins_WarnAndMismatchEvent()
    {
        // Owner DECLARES byok, but 32-3 reports the wire credential was platform.
        var (tagger, events) = NewTagger(MetricBillingMode.Byok);
        var tenantId = Guid.NewGuid();

        var tag = await tagger.ResolveTagAsync(tenantId, "anthropic", credentialSource: "platform");

        tag.Should().Be(BillingModeTokens.Platform, "32-3 (the credential actually used) wins for the tag");
        events.Should().HaveCount(1, "exactly one mismatch audit event");
        var evt = events.Single();
        evt.Type.Should().Be(BillingModeEvents.BillingModeMismatch);
        evt.TenantId.Should().Be(tenantId);
        evt.Tags.Should().Contain("\"mode34\":\"byok\"");
        evt.Tags.Should().Contain("\"source32\":\"platform\"");
    }

    [Test]
    public async Task Resolve_InvalidSourceToken_FailsLoud()
    {
        var (tagger, _) = NewTagger(MetricBillingMode.Byok);
        var act = async () => await tagger.ResolveTagAsync(Guid.NewGuid(), "anthropic", credentialSource: "garbage");
        (await act.Should().ThrowAsync<TammaError>()).Which.Code.Should().Be("BILLING_MODE_INVALID_SOURCE");
    }

    [Test]
    public async Task NullTagger_AlwaysPlatform_NoEvents()
    {
        var tagger = new NullBillingModeTagger();
        (await tagger.ResolveTagAsync(null, "anthropic", credentialSource: "byok"))
            .Should().Be(BillingModeTokens.Platform, "single-user has no billable-mode implication (AC8)");
    }
}
