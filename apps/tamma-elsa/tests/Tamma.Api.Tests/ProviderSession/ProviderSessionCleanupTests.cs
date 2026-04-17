using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.Providers;

namespace Tamma.Api.Tests.ProviderSession;

/// <summary>
/// Tests the idle-session eviction path: both the underlying
/// <see cref="IProviderSessionService.EvictInactiveAsync"/> call and the
/// <see cref="ProviderSessionCleanupService"/> hosted-service wiring.
/// </summary>
[TestFixture]
public class ProviderSessionCleanupTests
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Test]
    public async Task EvictInactive_SessionOlderThanTtl_IsRemoved()
    {
        var clock = new TestSystemClock(new DateTimeOffset(2026, 4, 16, 12, 0, 0, TimeSpan.Zero));
        var diagnostics = new RecordingDiagnosticsService();
        var sut = new ProviderSessionService(
            new StubProviderClient(), diagnostics, clock,
            NullLogger<ProviderSessionService>.Instance);

        var session = await sut.CreateAsync("anthropic", "claude-sonnet-4", TenantA);

        clock.Advance(TimeSpan.FromMinutes(31));
        var evicted = await sut.EvictInactiveAsync(TimeSpan.FromMinutes(30));

        evicted.Should().Be(1);
        (await sut.GetAsync(session.Handle)).Should().BeNull();
    }

    [Test]
    public async Task EvictInactive_FreshSession_IsKept()
    {
        var clock = new TestSystemClock(new DateTimeOffset(2026, 4, 16, 12, 0, 0, TimeSpan.Zero));
        var sut = new ProviderSessionService(
            new StubProviderClient(), new RecordingDiagnosticsService(), clock,
            NullLogger<ProviderSessionService>.Instance);

        var session = await sut.CreateAsync("anthropic", "claude-sonnet-4", TenantA);

        clock.Advance(TimeSpan.FromMinutes(5));
        var evicted = await sut.EvictInactiveAsync(TimeSpan.FromMinutes(30));

        evicted.Should().Be(0);
        (await sut.GetAsync(session.Handle)).Should().NotBeNull();
    }

    [Test]
    public async Task EvictInactive_UsingLastUsed_NotCreatedAt()
    {
        var clock = new TestSystemClock(new DateTimeOffset(2026, 4, 16, 12, 0, 0, TimeSpan.Zero));
        var sut = new ProviderSessionService(
            new StubProviderClient(), new RecordingDiagnosticsService(), clock,
            NullLogger<ProviderSessionService>.Instance);

        var session = await sut.CreateAsync("anthropic", "claude-sonnet-4", TenantA);

        // 20 min later — touch the session so LastUsed refreshes
        clock.Advance(TimeSpan.FromMinutes(20));
        await sut.GetAsync(session.Handle);

        // Another 20 min later (40m total, but only 20m since touch)
        clock.Advance(TimeSpan.FromMinutes(20));
        var evicted = await sut.EvictInactiveAsync(TimeSpan.FromMinutes(30));

        evicted.Should().Be(0);
        (await sut.GetAsync(session.Handle)).Should().NotBeNull();
    }

    [Test]
    public async Task CleanupHostedService_RunsEvictionOnInterval()
    {
        var clock = new TestSystemClock(new DateTimeOffset(2026, 4, 16, 12, 0, 0, TimeSpan.Zero));
        var sut = new ProviderSessionService(
            new StubProviderClient(), new RecordingDiagnosticsService(), clock,
            NullLogger<ProviderSessionService>.Instance);

        var options = new ProviderSessionOptions
        {
            InactivityTtl = TimeSpan.FromMinutes(30),
            CleanupInterval = TimeSpan.FromMilliseconds(50),
        };
        var hosted = new ProviderSessionCleanupService(
            sut, options, NullLogger<ProviderSessionCleanupService>.Instance);

        var session = await sut.CreateAsync("anthropic", "claude-sonnet-4", TenantA);
        clock.Advance(TimeSpan.FromMinutes(31));

        using var cts = new CancellationTokenSource();
        var runTask = hosted.StartAsync(cts.Token);
        await runTask;

        // Wait until the session is evicted or timeout.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while ((await sut.GetAsync(session.Handle)) is not null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        await hosted.StopAsync(CancellationToken.None);

        (await sut.GetAsync(session.Handle)).Should().BeNull();
    }
}
