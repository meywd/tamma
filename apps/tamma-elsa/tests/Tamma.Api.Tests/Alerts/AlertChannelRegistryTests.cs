using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Alerts;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Alerts;

/// <summary>
/// Story 5.6 / 1.5-37 (Wave C.1) — unit tests for
/// <see cref="AlertChannelRegistry"/>. The registry is a thin
/// dictionary; we verify: every registered type resolves, unknown
/// types return null, and resolution is case-insensitive.
/// </summary>
[TestFixture]
public class AlertChannelRegistryTests
{
    [Test]
    public void Resolve_KnownType_ReturnsMatchingImpl()
    {
        var email = new StubChannel("email");
        var slack = new StubChannel("slack");
        var registry = new AlertChannelRegistry(new[] { email, slack });

        registry.Resolve("email").Should().BeSameAs(email);
        registry.Resolve("slack").Should().BeSameAs(slack);
    }

    [Test]
    public void Resolve_UnknownType_ReturnsNull()
    {
        var registry = new AlertChannelRegistry(
            new[] { new StubChannel("email") });

        registry.Resolve("pagerduty").Should().BeNull();
    }

    [Test]
    public void Resolve_CaseInsensitive()
    {
        var registry = new AlertChannelRegistry(
            new[] { new StubChannel("email") });

        registry.Resolve("EMAIL").Should().NotBeNull();
        registry.Resolve("Email").Should().NotBeNull();
    }

    [Test]
    public void Resolve_NullType_Throws()
    {
        var registry = new AlertChannelRegistry(
            new[] { new StubChannel("email") });

        var act = () => registry.Resolve(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    private sealed class StubChannel : IAlertChannel
    {
        public StubChannel(string type) { ChannelType = type; }
        public string ChannelType { get; }

        public Task<DeliveryResult> SendAsync(
            Alert alert, AlertChannel channel, CancellationToken ct) =>
            Task.FromResult(new DeliveryResult(true, null));
    }
}
