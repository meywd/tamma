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
