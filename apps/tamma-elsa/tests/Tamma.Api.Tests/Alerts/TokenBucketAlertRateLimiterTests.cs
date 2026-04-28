using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Alerts;

namespace Tamma.Api.Tests.Alerts;

/// <summary>
/// Story 5.6 (Wave C.1) — unit tests for
/// <see cref="TokenBucketAlertRateLimiter"/>. Verifies the 5/min
/// ceiling, per-rule independence, linear refill, and the null-rule
/// bypass contract.
/// </summary>
[TestFixture]
public class TokenBucketAlertRateLimiterTests
{
    private TestTimeProvider _time = null!;
    private TokenBucketAlertRateLimiter _limiter = null!;

    [SetUp]
    public void SetUp()
    {
        _time = new TestTimeProvider(DateTimeOffset.Parse("2026-04-23T00:00:00Z"));
        _limiter = new TokenBucketAlertRateLimiter(
            new AlertRateLimiterOptions { CeilingPerMinute = 5 },
            _time);
    }

    [Test]
    public void TryConsume_NullRuleId_AlwaysReturnsTrue()
    {
        for (var i = 0; i < 20; i++)
            _limiter.TryConsume(null).Should().BeTrue();
    }

    [Test]
    public void TryConsume_FirstFiveForRule_ReturnTrue_SixthReturnsFalse()
    {
        var rule = Guid.NewGuid();

        for (var i = 0; i < 5; i++)
            _limiter.TryConsume(rule).Should().BeTrue(
                $"the {i + 1}th consume within the ceiling should succeed");

        _limiter.TryConsume(rule).Should().BeFalse(
            "the 6th consume within the same minute must be rejected");
    }

    [Test]
    public void TryConsume_AfterFullMinute_BucketRefillsToCeiling()
    {
        var rule = Guid.NewGuid();
        for (var i = 0; i < 5; i++)
            _limiter.TryConsume(rule).Should().BeTrue();
        _limiter.TryConsume(rule).Should().BeFalse();

        _time.Advance(TimeSpan.FromMinutes(1));

        for (var i = 0; i < 5; i++)
            _limiter.TryConsume(rule).Should().BeTrue(
                $"refill after 1 minute should restore 5 tokens (attempt {i + 1})");
        _limiter.TryConsume(rule).Should().BeFalse();
    }

    [Test]
    public void TryConsume_DifferentRules_HaveIndependentBuckets()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        for (var i = 0; i < 5; i++)
            _limiter.TryConsume(a).Should().BeTrue();
        _limiter.TryConsume(a).Should().BeFalse();

        // Rule b's bucket is untouched.
        for (var i = 0; i < 5; i++)
            _limiter.TryConsume(b).Should().BeTrue();
    }

    [Test]
    public void TryConsume_HalfMinuteRefill_RestoresHalfCapacity()
    {
        var rule = Guid.NewGuid();
        for (var i = 0; i < 5; i++)
            _limiter.TryConsume(rule).Should().BeTrue();

        // After 30 seconds, linear refill = 2.5 tokens — floor to 2.
        _time.Advance(TimeSpan.FromSeconds(30));

        _limiter.TryConsume(rule).Should().BeTrue("first refilled token");
        _limiter.TryConsume(rule).Should().BeTrue("second refilled token");
        _limiter.TryConsume(rule).Should().BeFalse(
            "only 2 whole tokens refilled at the 30s mark");
    }

    [Test]
    public void Ctor_ZeroCeiling_Throws()
    {
        var act = () => new TokenBucketAlertRateLimiter(
            new AlertRateLimiterOptions { CeilingPerMinute = 0 },
            _time);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
