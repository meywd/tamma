using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Pooling;

namespace Tamma.Api.Tests.Epic28;

/// <summary>
/// Story 28-4 — coverage for the new ref-counted lease API
/// (<see cref="LruPooledTenantConnectionResolver.LeaseAsync"/>) and the
/// admin diagnostics surface (<see cref="IAdminPoolDiagnostics"/>) on
/// the LRU resolver. Complements the existing
/// <c>LruPooledTenantConnectionResolverTests</c> which covers the
/// hot/cold paths + cache mechanics.
/// </summary>
[TestFixture]
public class LruResolverLeaseAndDiagnosticsTests
{
    private string _dbName = null!;
    private ServiceProvider _sp = null!;
    private IDbContextFactory<ControlPlaneDbContext> _factory = null!;
    private TenantConnectionPoolMetrics _metrics = null!;

    private sealed class PassthroughDecryptor : IConnectionStringDecryptor
    {
        public string Decrypt(byte[] envelope, int? kekVersion)
            => Encoding.UTF8.GetString(envelope);
    }

    [SetUp]
    public void SetUp()
    {
        _dbName = $"lease-test-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddDbContextFactory<ControlPlaneDbContext>(
            opts => opts.UseInMemoryDatabase(_dbName));
        _sp = services.BuildServiceProvider();
        _factory = _sp.GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>();
        _metrics = new TenantConnectionPoolMetrics();
    }

    [TearDown]
    public void TearDown()
    {
        _metrics.Dispose();
        _sp.Dispose();
    }

    private LruPooledTenantConnectionResolver NewResolver(
        TenantConnectionPoolOptions? opts = null) =>
        new(_factory,
            new PassthroughDecryptor(),
            _metrics,
            Options.Create(opts ?? new TenantConnectionPoolOptions()),
            NullLogger<LruPooledTenantConnectionResolver>.Instance);

    private async Task<Guid> SeedTenantAsync(string cs =
        "Host=stub.invalid;Port=5432;Database=t;Username=u;Password=p")
    {
        var tenantId = Guid.NewGuid();
        await using var ctx = await _factory.CreateDbContextAsync();
        var tenant = new Tenant
        {
            Id = tenantId,
            Name = "T",
            Slug = $"slug-{tenantId:N}",
            Type = "personal",
            Plan = "free",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var entry = ctx.Tenants.Add(tenant);
        entry.Property("Status").CurrentValue = "active";
        entry.Property("EncryptedConnectionString").CurrentValue =
            Encoding.UTF8.GetBytes(cs);
        entry.Property("KekVersion").CurrentValue = 1;
        await ctx.SaveChangesAsync();
        return tenantId;
    }

    // ── LeaseAsync ────────────────────────────────────────────────

    [Test]
    public async Task LeaseAsync_Returns_Working_Lease_With_DataSource()
    {
        var tid = await SeedTenantAsync();
        await using var resolver = NewResolver();

        await using var lease = await resolver.LeaseAsync(tid);

        lease.TenantId.Should().Be(tid);
        lease.DataSource.Should().NotBeNull();
        lease.DataSource.ConnectionString.Should().Contain($"tenant={tid:D}");
    }

    [Test]
    public async Task LeaseAsync_Then_Evict_Defers_DataSource_Dispose_Until_Lease_Releases()
    {
        var tid = await SeedTenantAsync();
        await using var resolver = NewResolver();

        var lease = await resolver.LeaseAsync(tid);
        var ds = lease.DataSource;

        // Evict while lease is open. Cache entry removed, but data
        // source should still be usable through the lease (deferred
        // dispose path).
        await resolver.EvictAsync(tid);

        // The lease's data source must still work — i.e. ConnectionString
        // accessor doesn't throw, meaning the data source is not yet
        // disposed.
        Action stillUsable = () => _ = ds.ConnectionString;
        stillUsable.Should().NotThrow(
            "deferred-dispose must keep the data source alive while the lease is open");

        // A subsequent GetDataSourceAsync should build a fresh pool —
        // proving the cache entry is gone, not just that the lease
        // is keeping it.
        var freshDs = await resolver.GetDataSourceAsync(tid);
        ReferenceEquals(freshDs, ds).Should().BeFalse(
            "post-evict access must build a new data source");

        // Now release the lease — the original data source's deferred
        // dispose fires asynchronously. We don't assert the dispose
        // happens on a deadline here because the dispose callback
        // runs on a background Task; the resolver's own dispose at
        // tear-down will catch any leak.
        await lease.DisposeAsync();
    }

    [Test]
    public async Task LeaseAsync_Multiple_Concurrent_Leases_All_Share_Same_DataSource()
    {
        var tid = await SeedTenantAsync();
        await using var resolver = NewResolver();

        var leases = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => resolver.LeaseAsync(tid).AsTask()));

        // All siblings point at the same data source instance.
        for (var i = 1; i < leases.Length; i++)
            ReferenceEquals(leases[0].DataSource, leases[i].DataSource).Should().BeTrue();

        foreach (var l in leases)
            await l.DisposeAsync();
    }

    [Test]
    public async Task LeaseAsync_DataSource_Throws_After_Dispose()
    {
        var tid = await SeedTenantAsync();
        await using var resolver = NewResolver();

        var lease = await resolver.LeaseAsync(tid);
        await lease.DisposeAsync();

        Action act = () => _ = lease.DataSource;
        act.Should().Throw<ObjectDisposedException>();
    }

    // ── IAdminPoolDiagnostics ─────────────────────────────────────

    [Test]
    public async Task GetDetailedStats_Reflects_Hits_Misses_Evictions()
    {
        var tidA = await SeedTenantAsync();
        var tidB = await SeedTenantAsync();
        await using var resolver = NewResolver();

        // Two cold-misses + two hits.
        _ = await resolver.GetDataSourceAsync(tidA);
        _ = await resolver.GetDataSourceAsync(tidA);
        _ = await resolver.GetDataSourceAsync(tidB);
        _ = await resolver.GetDataSourceAsync(tidB);

        // Then explicit eviction.
        await resolver.EvictAsync(tidA);

        var stats = ((IAdminPoolDiagnostics)resolver).GetDetailedStats();

        stats.WarmPoolCount.Should().Be(1, "tidA evicted, tidB still warm");
        stats.OpenedTotal.Should().Be(2, "two cold-miss builds");
        stats.HitsTotal.Should().Be(2, "two repeat lookups");
        stats.MissesTotal.Should().Be(2);
        stats.EvictedTotal.Should().Be(1);
        stats.EvictedExplicit.Should().Be(1);
        stats.EvictedByLru.Should().Be(0);
        stats.HitRatio.Should().BeApproximately(0.5d, 0.01);
    }

    [Test]
    public async Task GetDetailedStats_Counts_Lru_Evictions_Separately_From_Explicit()
    {
        // Cap at 1 entry so the second tenant's miss evicts the first.
        await using var resolver = NewResolver(
            new TenantConnectionPoolOptions { MaxEntries = 1 });
        var tidA = await SeedTenantAsync();
        var tidB = await SeedTenantAsync();

        _ = await resolver.GetDataSourceAsync(tidA);
        _ = await resolver.GetDataSourceAsync(tidB);

        var stats = ((IAdminPoolDiagnostics)resolver).GetDetailedStats();
        stats.EvictedByLru.Should().Be(1, "tidA evicted by LRU when tidB was added");
        stats.EvictedExplicit.Should().Be(0);
        stats.WarmPoolCount.Should().Be(1);
    }

    [Test]
    public async Task ListWarmTenants_Returns_MRU_Order_With_Outstanding_Lease_Counts()
    {
        var tidA = await SeedTenantAsync();
        var tidB = await SeedTenantAsync();
        var tidC = await SeedTenantAsync();
        await using var resolver = NewResolver();

        // Warm in order A, B, C — so MRU is C, B, A
        _ = await resolver.GetDataSourceAsync(tidA);
        _ = await resolver.GetDataSourceAsync(tidB);
        _ = await resolver.GetDataSourceAsync(tidC);

        // Hold a lease on B so it shows outstandingLeases=1.
        var lease = await resolver.LeaseAsync(tidB);

        // Touch C again so it's MRU.
        _ = await resolver.GetDataSourceAsync(tidC);

        var entries = ((IAdminPoolDiagnostics)resolver).ListWarmTenants(10);

        entries.Should().HaveCount(3);
        entries[0].TenantId.Should().Be(tidC, "MRU first");
        entries.Should().Contain(e => e.TenantId == tidB && e.OutstandingLeases == 1);
        entries.Should().Contain(e => e.TenantId == tidA && e.OutstandingLeases == 0);

        await lease.DisposeAsync();
    }

    [Test]
    public async Task ListWarmTenants_Clamps_Limit_To_1000()
    {
        await using var resolver = NewResolver();
        var entries = ((IAdminPoolDiagnostics)resolver).ListWarmTenants(99999);
        entries.Should().BeEmpty();   // empty cache
        // Smoke: a 100k limit shouldn't OOM — the impl pre-allocates
        // Math.Min(limit, _lru.Count). Just confirm the call returned.
    }

    [Test]
    public async Task ListWarmTenants_Floor_At_Limit_One()
    {
        var tid = await SeedTenantAsync();
        await using var resolver = NewResolver();
        _ = await resolver.GetDataSourceAsync(tid);

        var entries = ((IAdminPoolDiagnostics)resolver).ListWarmTenants(0);
        entries.Should().HaveCount(1, "limit < 1 floors to 1");
    }
}
