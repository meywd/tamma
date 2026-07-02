using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Audit;
using Tamma.Api.Tests.Alerts;

namespace Tamma.Api.Tests.Audit;

/// <summary>
/// Story 37-10 (AC8) — the <c>AUTH.APIKEY.USED</c> throttle: one heartbeat per
/// key per time bucket so the hot per-request auth path does not flood the trail.
/// </summary>
[TestFixture]
public class ApiKeyAuditHeartbeatTests
{
    [Test]
    public void First_Call_Emits_Subsequent_In_Same_Bucket_Suppressed()
    {
        var time = new TestTimeProvider(DateTimeOffset.UtcNow);
        var heartbeat = new ApiKeyAuditHeartbeat(time);
        var keyId = Guid.NewGuid();

        heartbeat.ShouldEmit(keyId).Should().BeTrue("the first request in a bucket emits");
        heartbeat.ShouldEmit(keyId).Should().BeFalse("subsequent requests in the same bucket are suppressed");
        heartbeat.ShouldEmit(keyId).Should().BeFalse();
    }

    [Test]
    public void Hundred_Requests_In_One_Bucket_Emit_Exactly_Once()
    {
        var time = new TestTimeProvider(DateTimeOffset.UtcNow);
        var heartbeat = new ApiKeyAuditHeartbeat(time);
        var keyId = Guid.NewGuid();

        var emitted = 0;
        for (var i = 0; i < 100; i++)
            if (heartbeat.ShouldEmit(keyId)) emitted++;

        emitted.Should().Be(1, "100 auths in one bucket produce one heartbeat, not 100");
    }

    [Test]
    public void After_Window_Elapses_Emits_Again()
    {
        var time = new TestTimeProvider(DateTimeOffset.UtcNow);
        var heartbeat = new ApiKeyAuditHeartbeat(time);
        var keyId = Guid.NewGuid();

        heartbeat.ShouldEmit(keyId).Should().BeTrue();
        heartbeat.ShouldEmit(keyId).Should().BeFalse();

        // Advance past the window into the next bucket.
        time.Advance(ApiKeyAuditHeartbeat.Window + TimeSpan.FromSeconds(1));

        heartbeat.ShouldEmit(keyId).Should().BeTrue("a new bucket emits a fresh heartbeat");
    }

    [Test]
    public void Distinct_Keys_Are_Throttled_Independently()
    {
        var time = new TestTimeProvider(DateTimeOffset.UtcNow);
        var heartbeat = new ApiKeyAuditHeartbeat(time);

        heartbeat.ShouldEmit(Guid.NewGuid()).Should().BeTrue();
        heartbeat.ShouldEmit(Guid.NewGuid()).Should().BeTrue("a different key has its own bucket");
    }
}
