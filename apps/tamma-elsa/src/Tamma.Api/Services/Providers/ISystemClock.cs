namespace Tamma.Api.Services.Providers;

/// <summary>
/// Abstraction over the system clock so circuit-breaker cooldown scenarios
/// can be tested without actual elapsed wall time.
/// </summary>
public interface ISystemClock
{
    /// <summary>Current UTC time.</summary>
    DateTimeOffset UtcNow { get; }
}

/// <summary>
/// Default clock implementation that delegates to <see cref="DateTimeOffset.UtcNow"/>.
/// </summary>
public sealed class SystemClock : ISystemClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>
/// Test double whose current time can be advanced explicitly.
/// Not registered in production DI — exposed for unit/integration tests.
/// </summary>
public sealed class TestSystemClock : ISystemClock
{
    private DateTimeOffset _now;

    public TestSystemClock(DateTimeOffset start)
    {
        _now = start;
    }

    public DateTimeOffset UtcNow => _now;

    public void Advance(TimeSpan delta) => _now = _now.Add(delta);

    public void SetUtcNow(DateTimeOffset now) => _now = now;
}
