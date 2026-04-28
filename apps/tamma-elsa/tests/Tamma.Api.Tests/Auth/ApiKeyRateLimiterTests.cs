using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.RateLimit;

namespace Tamma.Api.Tests.Auth;

/// <summary>
/// Story 28-7 deferred-item — unit tests for <see cref="ApiKeyRateLimiter"/>.
/// </summary>
[TestFixture]
public class ApiKeyRateLimiterTests
{
    private InMemoryDistributedRateLimitBackend _backend = null!;
    private ApiKeyRateLimiter _limiter = null!;

    [SetUp]
    public void SetUp()
    {
        _backend = new InMemoryDistributedRateLimitBackend();
        _limiter = new ApiKeyRateLimiter(_backend);
    }

    [Test]
    public void IsLimited_NullRpm_NeverLimited()
    {
        var id = Guid.NewGuid();
        for (var i = 0; i < 1000; i++)
            _limiter.Record(id);
        _limiter.IsLimited(id, null).Should().BeFalse();
    }

    [Test]
    public void IsLimited_ZeroOrNegativeRpm_NeverLimited()
    {
        var id = Guid.NewGuid();
        _limiter.Record(id);
        _limiter.IsLimited(id, 0).Should().BeFalse();
        _limiter.IsLimited(id, -1).Should().BeFalse();
    }

    [Test]
    public void IsLimited_UnderLimit_ReturnsFalse()
    {
        var id = Guid.NewGuid();
        _limiter.Record(id);
        _limiter.Record(id);
        _limiter.IsLimited(id, limitRpm: 10).Should().BeFalse();
    }

    [Test]
    public void IsLimited_AtLimit_ReturnsTrue()
    {
        var id = Guid.NewGuid();
        for (var i = 0; i < 5; i++)
            _limiter.Record(id);
        _limiter.IsLimited(id, limitRpm: 5).Should().BeTrue();
    }

    [Test]
    public void IsLimited_DifferentKeys_IndependentBuckets()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        for (var i = 0; i < 5; i++)
            _limiter.Record(id1);
        _limiter.IsLimited(id1, limitRpm: 5).Should().BeTrue();
        _limiter.IsLimited(id2, limitRpm: 5).Should().BeFalse();
    }
}
