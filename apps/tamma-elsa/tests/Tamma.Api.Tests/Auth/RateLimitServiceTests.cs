using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.RateLimit;

namespace Tamma.Api.Tests.Auth;

[TestFixture]
public class RateLimitServiceTests
{
    [Test]
    public void ThreeRequests_NotLimited()
    {
        var svc = new InMemoryRateLimitService();
        svc.IsLimited("scope", "alice@x").Should().BeFalse();
        svc.Record("scope", "alice@x");
        svc.IsLimited("scope", "alice@x").Should().BeFalse();
        svc.Record("scope", "alice@x");
        svc.IsLimited("scope", "alice@x").Should().BeFalse();
        svc.Record("scope", "alice@x");
        svc.IsLimited("scope", "alice@x").Should().BeTrue();
    }

    [Test]
    public void DifferentScope_HasIndependentBudget()
    {
        var svc = new InMemoryRateLimitService();
        for (int i = 0; i < 3; i++) svc.Record("a", "k");
        svc.IsLimited("a", "k").Should().BeTrue();
        svc.IsLimited("b", "k").Should().BeFalse();
    }

    [Test]
    public void KeyIsCaseInsensitive()
    {
        var svc = new InMemoryRateLimitService();
        for (int i = 0; i < 3; i++) svc.Record("scope", "Alice@X");
        svc.IsLimited("scope", "alice@x").Should().BeTrue();
    }

    [Test]
    public void OldEventsExpire()
    {
        var clock = DateTime.UtcNow;
        var svc = new InMemoryRateLimitService(() => clock);
        for (int i = 0; i < 3; i++) svc.Record("scope", "k");
        svc.IsLimited("scope", "k").Should().BeTrue();
        clock = clock.AddHours(2);
        svc.IsLimited("scope", "k").Should().BeFalse();
    }
}
