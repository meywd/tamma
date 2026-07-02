namespace Tamma.Api.Tests.Pricing;

/// <summary>
/// Story 34-6 — minimal controllable <see cref="TimeProvider"/> for the
/// entitlement cache TTL tests. The <c>Tamma.Api.Tests</c> project does not
/// reference <c>Microsoft.Extensions.Time.Testing</c>, so (matching the Alerts /
/// Epic-28 analytics tests) we hand-roll the tiny surface we need.
/// </summary>
internal sealed class PricingTestClock : TimeProvider
{
    private DateTimeOffset _now;

    public PricingTestClock(DateTimeOffset? start = null)
    {
        _now = start ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now = _now.Add(delta);
}
