using FluentAssertions;
using Moq;
using NUnit.Framework;
using Tamma.Platforms;
using Tamma.Platforms.Abstractions;

namespace Tamma.Platforms.Tests;

/// <summary>
/// Story 31-2 Step 4 — <see cref="PlatformDriverCache"/> unit tests.
/// Validates LRU semantics, TTL, explicit invalidation, and tenant
/// scoping (one tenant's invalidate must not touch another tenant's
/// entries).
/// </summary>
[TestFixture]
public class PlatformDriverCacheTests
{
    private static IGitPlatformDriver MakeDriver(PlatformKind kind = PlatformKind.GitHub)
    {
        var driver = new Mock<IGitPlatformDriver>(MockBehavior.Loose);
        driver.SetupGet(d => d.Kind).Returns(kind);
        return driver.Object;
    }

    [Test]
    public void TryGet_OnEmpty_ReturnsFalse()
    {
        using var cache = new PlatformDriverCache();
        var hit = cache.TryGet(Guid.NewGuid(), PlatformKind.GitHub, out var driver);
        hit.Should().BeFalse();
        driver.Should().BeNull();
    }

    [Test]
    public void Set_ThenTryGet_Hits()
    {
        using var cache = new PlatformDriverCache();
        var tenantId = Guid.NewGuid();
        var driver = MakeDriver();

        cache.Set(tenantId, PlatformKind.GitHub, driver);
        var hit = cache.TryGet(tenantId, PlatformKind.GitHub, out var cached);

        hit.Should().BeTrue();
        cached.Should().BeSameAs(driver);
    }

    [Test]
    public void Set_ReplacesExistingEntry()
    {
        using var cache = new PlatformDriverCache();
        var tenantId = Guid.NewGuid();
        var first = MakeDriver();
        var second = MakeDriver();

        cache.Set(tenantId, PlatformKind.GitHub, first);
        cache.Set(tenantId, PlatformKind.GitHub, second);

        cache.TryGet(tenantId, PlatformKind.GitHub, out var cached);
        cached.Should().BeSameAs(second);
    }

    // ── Epic 31 review (F-medium) — the ABSOLUTE bound. Sliding
    //    expiration renews on every hit, so a hot tenant (the CI poller
    //    resolves every 30s) kept a stale driver — and its compose-time
    //    credential — alive forever. The absolute TTL is the hard cap
    //    regardless of hit rate. ──

    [Test]
    public async Task HotEntry_ContinuouslyTouched_StillExpiresAtTheAbsoluteBound()
    {
        using var cache = new PlatformDriverCache(new PlatformDriverCacheOptions
        {
            // Sliding shorter than absolute so each touch renews it —
            // exactly the pattern that used to keep the entry alive forever.
            SlidingTtl = TimeSpan.FromMilliseconds(150),
            AbsoluteTtl = TimeSpan.FromMilliseconds(300),
        });
        var tenantId = Guid.NewGuid();
        cache.Set(tenantId, PlatformKind.GitHub, MakeDriver());

        // Touch the entry well inside the sliding window, past the
        // absolute bound. Generous deadline so a slow CI runner can't
        // flake this: the entry MUST be gone within 5s.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        var expired = false;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!cache.TryGet(tenantId, PlatformKind.GitHub, out _))
            {
                expired = true;
                break;
            }
            await Task.Delay(50);
        }

        expired.Should().BeTrue(
            "sliding expiration alone renews on every hit — the absolute TTL is what makes "
            + "'a missed invalidation event self-heals within the window' actually true for "
            + "a tenant with sustained traffic");
    }

    [Test]
    public void DifferentKinds_DontCollide_OnSameTenant()
    {
        using var cache = new PlatformDriverCache();
        var tenantId = Guid.NewGuid();
        var github = MakeDriver(PlatformKind.GitHub);
        var gitea = MakeDriver(PlatformKind.Gitea);

        cache.Set(tenantId, PlatformKind.GitHub, github);
        cache.Set(tenantId, PlatformKind.Gitea, gitea);

        cache.TryGet(tenantId, PlatformKind.GitHub, out var cachedGh);
        cache.TryGet(tenantId, PlatformKind.Gitea, out var cachedGitea);

        cachedGh.Should().BeSameAs(github);
        cachedGitea.Should().BeSameAs(gitea);
    }

    [Test]
    public void DifferentTenants_DontCollide_OnSameKind()
    {
        using var cache = new PlatformDriverCache();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var driverA = MakeDriver();
        var driverB = MakeDriver();

        cache.Set(tenantA, PlatformKind.GitHub, driverA);
        cache.Set(tenantB, PlatformKind.GitHub, driverB);

        cache.TryGet(tenantA, PlatformKind.GitHub, out var cachedA);
        cache.TryGet(tenantB, PlatformKind.GitHub, out var cachedB);

        cachedA.Should().BeSameAs(driverA);
        cachedB.Should().BeSameAs(driverB);
    }

    [Test]
    public async Task InvalidateTenantAsync_DropsAllKindsForTenant()
    {
        using var cache = new PlatformDriverCache();
        var tenantId = Guid.NewGuid();

        cache.Set(tenantId, PlatformKind.GitHub, MakeDriver(PlatformKind.GitHub));
        cache.Set(tenantId, PlatformKind.Gitea, MakeDriver(PlatformKind.Gitea));
        cache.Set(tenantId, PlatformKind.GitLab, MakeDriver(PlatformKind.GitLab));

        await cache.InvalidateTenantAsync(tenantId);

        cache.TryGet(tenantId, PlatformKind.GitHub, out _).Should().BeFalse();
        cache.TryGet(tenantId, PlatformKind.Gitea, out _).Should().BeFalse();
        cache.TryGet(tenantId, PlatformKind.GitLab, out _).Should().BeFalse();
    }

    [Test]
    public async Task InvalidateTenantAsync_DoesNotImpactOtherTenants()
    {
        using var cache = new PlatformDriverCache();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        cache.Set(tenantA, PlatformKind.GitHub, MakeDriver());
        cache.Set(tenantB, PlatformKind.GitHub, MakeDriver());

        await cache.InvalidateTenantAsync(tenantA);

        cache.TryGet(tenantA, PlatformKind.GitHub, out _).Should().BeFalse();
        cache.TryGet(tenantB, PlatformKind.GitHub, out _).Should().BeTrue();
    }

    [Test]
    public async Task InvalidateTenantAsync_OnUnknownTenant_IsNoOp()
    {
        using var cache = new PlatformDriverCache();
        var act = async () => await cache.InvalidateTenantAsync(Guid.NewGuid());
        await act.Should().NotThrowAsync();
    }

    [Test]
    public void Invalidate_DropsSingleKind_OnlyForThatKind()
    {
        using var cache = new PlatformDriverCache();
        var tenantId = Guid.NewGuid();
        cache.Set(tenantId, PlatformKind.GitHub, MakeDriver(PlatformKind.GitHub));
        cache.Set(tenantId, PlatformKind.Gitea, MakeDriver(PlatformKind.Gitea));

        cache.Invalidate(tenantId, PlatformKind.GitHub);

        cache.TryGet(tenantId, PlatformKind.GitHub, out _).Should().BeFalse();
        cache.TryGet(tenantId, PlatformKind.Gitea, out _).Should().BeTrue();
    }

    [Test]
    public void MaxEntries_EnforcesEviction()
    {
        // Tight cap so the test is deterministic.
        using var cache = new PlatformDriverCache(
            new PlatformDriverCacheOptions { MaxEntries = 2 });

        var tenant1 = Guid.NewGuid();
        var tenant2 = Guid.NewGuid();
        var tenant3 = Guid.NewGuid();

        cache.Set(tenant1, PlatformKind.GitHub, MakeDriver());
        cache.Set(tenant2, PlatformKind.GitHub, MakeDriver());
        cache.Set(tenant3, PlatformKind.GitHub, MakeDriver());

        // MemoryCache compaction is asynchronous; total in-cache count
        // must not exceed the configured limit by much, and at least
        // one of the older entries should be gone.
        var count =
            (cache.TryGet(tenant1, PlatformKind.GitHub, out _) ? 1 : 0) +
            (cache.TryGet(tenant2, PlatformKind.GitHub, out _) ? 1 : 0) +
            (cache.TryGet(tenant3, PlatformKind.GitHub, out _) ? 1 : 0);
        count.Should().BeLessThanOrEqualTo(2);
    }
}
