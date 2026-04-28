namespace Tamma.Api.Tests.Alerts;

/// <summary>
/// Minimal controllable <see cref="TimeProvider"/> for the alert-system
/// tests. Matches the pattern used by Epic 28 analytics tests
/// (<c>PlatformAnalyticsServiceTests.FakeTimeProvider</c>). The
/// project does not reference <c>Microsoft.Extensions.Time.Testing</c>,
/// so we hand-roll the minimum surface we need.
/// </summary>
internal sealed class TestTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public TestTimeProvider(DateTimeOffset start)
    {
        _now = start;
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now = _now.Add(delta);

    public void Set(DateTimeOffset now) => _now = now;
}
