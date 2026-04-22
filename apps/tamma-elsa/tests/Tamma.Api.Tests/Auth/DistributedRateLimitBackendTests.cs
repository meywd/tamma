using FluentAssertions;
using NUnit.Framework;
using StackExchange.Redis;
using Tamma.Api.Services.RateLimit;
using Testcontainers.Redis;

namespace Tamma.Api.Tests.Auth;

/// <summary>
/// Behavioral contract tests for <see cref="IDistributedRateLimitBackend"/>.
/// Both the in-process and Redis-backed implementations must satisfy the
/// same semantics: Increment returns the post-bump count, Count matches the
/// last Increment value, and after TTL the counter resets.
///
/// <para>
/// The Redis impl runs against a Testcontainers Redis instance (same
/// pattern as the Phase-3 Postgres fixtures). Audit finding auth/014
/// follow-up.
/// </para>
/// </summary>
[TestFixture]
public class InMemoryDistributedRateLimitBackendTests
{
    private InMemoryDistributedRateLimitBackend _backend = null!;
    private DateTime _clock;

    [SetUp]
    public void SetUp()
    {
        _clock = DateTime.UtcNow;
        _backend = new InMemoryDistributedRateLimitBackend(() => _clock);
    }

    [Test]
    public void Increment_ReturnsPostBumpCount()
    {
        _backend.Increment("k", TimeSpan.FromMinutes(1)).Should().Be(1);
        _backend.Increment("k", TimeSpan.FromMinutes(1)).Should().Be(2);
        _backend.Increment("k", TimeSpan.FromMinutes(1)).Should().Be(3);
    }

    [Test]
    public void Count_ReturnsZero_WhenKeyAbsent()
    {
        _backend.Count("never-touched", TimeSpan.FromMinutes(1)).Should().Be(0);
    }

    [Test]
    public void Count_TracksIncrements()
    {
        _backend.Increment("k", TimeSpan.FromMinutes(1));
        _backend.Increment("k", TimeSpan.FromMinutes(1));
        _backend.Count("k", TimeSpan.FromMinutes(1)).Should().Be(2);
    }

    [Test]
    public void OldEvents_Expire_AfterTtl()
    {
        _backend.Increment("k", TimeSpan.FromHours(1));
        _backend.Increment("k", TimeSpan.FromHours(1));
        _clock = _clock.AddHours(2);
        _backend.Count("k", TimeSpan.FromHours(1)).Should().Be(0);
    }

    [Test]
    public void Keys_AreIsolated()
    {
        _backend.Increment("a", TimeSpan.FromMinutes(1));
        _backend.Increment("a", TimeSpan.FromMinutes(1));
        _backend.Count("b", TimeSpan.FromMinutes(1)).Should().Be(0);
    }
}

/// <summary>
/// Redis-backed contract tests. Uses a Testcontainers Redis instance — the
/// upstream fixed-window semantics differ slightly from the in-memory
/// sliding window (the whole counter resets when the TTL pops, rather than
/// individual events aging out), but the high-level contract Count ==
/// last-Increment is preserved. Audit finding auth/014 follow-up.
/// </summary>
[TestFixture]
[Category("Redis")]
public class RedisDistributedRateLimitBackendTests
{
    private RedisContainer _redis = null!;
    private IConnectionMultiplexer _mux = null!;
    private RedisDistributedRateLimitBackend _backend = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _redis = new RedisBuilder().WithImage("redis:7-alpine").Build();
        await _redis.StartAsync();

        // AllowAdmin=true enables FlushDatabase between tests; tests are the
        // only caller that would ever run admin commands, so this is not a
        // concern for the production DI.
        var options = ConfigurationOptions.Parse(_redis.GetConnectionString());
        options.AllowAdmin = true;
        _mux = await ConnectionMultiplexer.ConnectAsync(options);
        _backend = new RedisDistributedRateLimitBackend(_mux);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        _mux?.Dispose();
        if (_redis is not null) await _redis.DisposeAsync();
    }

    [SetUp]
    public void Flush()
    {
        // Wipe the DB between tests so keys don't cross-contaminate.
        _mux.GetServer(_mux.GetEndPoints()[0]).FlushDatabase();
    }

    [Test]
    public void Increment_ReturnsPostBumpCount()
    {
        _backend.Increment("k", TimeSpan.FromMinutes(1)).Should().Be(1);
        _backend.Increment("k", TimeSpan.FromMinutes(1)).Should().Be(2);
        _backend.Increment("k", TimeSpan.FromMinutes(1)).Should().Be(3);
    }

    [Test]
    public void Count_ReturnsZero_WhenKeyAbsent()
    {
        _backend.Count("never-touched-redis", TimeSpan.FromMinutes(1)).Should().Be(0);
    }

    [Test]
    public void Count_TracksIncrements()
    {
        _backend.Increment("k", TimeSpan.FromMinutes(1));
        _backend.Increment("k", TimeSpan.FromMinutes(1));
        _backend.Count("k", TimeSpan.FromMinutes(1)).Should().Be(2);
    }

    [Test]
    public void Keys_AreIsolated()
    {
        _backend.Increment("a", TimeSpan.FromMinutes(1));
        _backend.Increment("a", TimeSpan.FromMinutes(1));
        _backend.Count("b", TimeSpan.FromMinutes(1)).Should().Be(0);
    }

    [Test]
    public async Task Ttl_Expires_Counter()
    {
        // Use a 1-second TTL so the test finishes quickly. The Redis script
        // clamps TTLs to at least 1s.
        _backend.Increment("short-ttl", TimeSpan.FromSeconds(1)).Should().Be(1);
        _backend.Count("short-ttl", TimeSpan.FromSeconds(1)).Should().Be(1);
        await Task.Delay(TimeSpan.FromMilliseconds(1500));
        _backend.Count("short-ttl", TimeSpan.FromSeconds(1)).Should().Be(0);
    }

    [Test]
    public void EndToEnd_ThroughRateLimitService()
    {
        // Service-level smoke: 3 events inside the window is the threshold,
        // the 4th trips the limit. Matches the InMemoryRateLimitService
        // tests byte-for-byte — the backend choice is transparent.
        var svc = new RateLimitService(_backend);
        svc.IsLimited("scope", "alice@x").Should().BeFalse();
        svc.Record("scope", "alice@x");
        svc.IsLimited("scope", "alice@x").Should().BeFalse();
        svc.Record("scope", "alice@x");
        svc.IsLimited("scope", "alice@x").Should().BeFalse();
        svc.Record("scope", "alice@x");
        svc.IsLimited("scope", "alice@x").Should().BeTrue();
    }
}
