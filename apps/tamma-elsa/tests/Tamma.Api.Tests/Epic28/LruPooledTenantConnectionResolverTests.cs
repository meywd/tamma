using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using System.Text;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Pooling;

namespace Tamma.Api.Tests.Epic28;

/// <summary>
/// Story 28-4 — unit suite for
/// <see cref="LruPooledTenantConnectionResolver"/>.
///
/// <para>The tests build NpgsqlDataSources but never open a connection,
/// so the cheap <c>Host=stub.invalid</c> string is safe — the resolver
/// only resolves data sources, doesn't query through them. The
/// control-plane factory is backed by EF InMemory; tenants are seeded
/// row-by-row per test.</para>
/// </summary>
[TestFixture]
public class LruPooledTenantConnectionResolverTests
{
    private string _dbName = null!;
    private ServiceProvider _sp = null!;
    private IDbContextFactory<ControlPlaneDbContext> _factory = null!;
    private RecordingDecryptor _decryptor = null!;
    private TenantConnectionPoolMetrics _metrics = null!;

    [SetUp]
    public void SetUp()
    {
        _dbName = $"resolver-test-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddDbContextFactory<ControlPlaneDbContext>(
            options => options.UseInMemoryDatabase(_dbName));
        _sp = services.BuildServiceProvider();
        _factory = _sp.GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>();
        _decryptor = new RecordingDecryptor();
        _metrics = new TenantConnectionPoolMetrics();
    }

    [TearDown]
    public void TearDown()
    {
        _metrics.Dispose();
        _sp.Dispose();
    }

    private LruPooledTenantConnectionResolver CreateResolver(
        TenantConnectionPoolOptions? options = null,
        IConnectionStringDecryptor? decryptor = null)
    {
        return new LruPooledTenantConnectionResolver(
            _factory,
            decryptor ?? _decryptor,
            _metrics,
            Options.Create(options ?? new TenantConnectionPoolOptions()),
            NullLogger<LruPooledTenantConnectionResolver>.Instance);
    }

    private async Task<Guid> SeedActiveTenantAsync(
        string connectionString = "Host=stub.invalid;Port=5432;Database=t;Username=u;Password=p",
        string status = "active")
    {
        var tenantId = Guid.NewGuid();
        await using var ctx = await _factory.CreateDbContextAsync();
        var tenant = new Tenant
        {
            Id = tenantId,
            Name = "Test",
            Slug = $"slug-{tenantId:N}",
            Type = "personal",
            Plan = "free",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var entry = ctx.Tenants.Add(tenant);
        entry.Property("Status").CurrentValue = status;
        entry.Property("EncryptedConnectionString").CurrentValue =
            Encoding.UTF8.GetBytes(connectionString);
        entry.Property("KekVersion").CurrentValue = (short)1;
        await ctx.SaveChangesAsync();
        return tenantId;
    }

    [Test]
    public async Task Cache_Hit_Returns_Same_Instance_And_Records_Hit()
    {
        var tenantId = await SeedActiveTenantAsync();
        await using var resolver = CreateResolver();

        var first = await resolver.GetDataSourceAsync(tenantId);
        var second = await resolver.GetDataSourceAsync(tenantId);

        ReferenceEquals(first, second).Should().BeTrue(
            "two consecutive lookups must return the same warm pool");
        _metrics.HitsTotal.Should().Be(1, "the second lookup is a hit");
        _metrics.MissesTotal.Should().Be(1, "the first lookup is a miss");
        _metrics.OpenedTotal.Should().Be(1, "exactly one pool was built");
        _metrics.WarmPoolCount.Should().Be(1);
    }

    [Test]
    public async Task Cache_Miss_Builds_Pool_And_Calls_Decryptor()
    {
        var connectionString = "Host=stub.invalid;Port=5432;Database=mypayload;Username=u;Password=p";
        var tenantId = await SeedActiveTenantAsync(connectionString);
        await using var resolver = CreateResolver();

        var ds = await resolver.GetDataSourceAsync(tenantId);

        ds.Should().NotBeNull();
        _decryptor.Calls.Should().Be(1);
        _decryptor.LastKekVersion.Should().Be(1);
        // ApplicationName is overridden by the resolver — confirm the
        // resulting data source string carries the tenant tag.
        ds.ConnectionString.Should().Contain($"tenant={tenantId:D}");
        ds.ConnectionString.Should().Contain("Database=mypayload");
    }

    [Test]
    public async Task Unknown_Tenant_Throws_TenantNotFoundException()
    {
        await using var resolver = CreateResolver();
        var act = async () => await resolver.GetDataSourceAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<TenantNotFoundException>();
    }

    [Test]
    public async Task Provisioning_Tenant_Throws_TenantNotProvisionedException()
    {
        var tenantId = await SeedActiveTenantAsync(status: "provisioning");
        await using var resolver = CreateResolver();

        var act = async () => await resolver.GetDataSourceAsync(tenantId);

        var exception = await act.Should().ThrowAsync<TenantNotProvisionedException>();
        exception.Which.TenantId.Should().Be(tenantId);
        exception.Which.Status.Should().Be("provisioning");
    }

    [Test]
    public async Task Empty_Envelope_Throws_ConnectionStringMissingException()
    {
        var tenantId = Guid.NewGuid();
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            var tenant = new Tenant
            {
                Id = tenantId,
                Name = "T",
                Slug = $"s-{tenantId:N}",
                Type = "personal",
                Plan = "free",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            var entry = ctx.Tenants.Add(tenant);
            entry.Property("Status").CurrentValue = "active";
            entry.Property("EncryptedConnectionString").CurrentValue = Array.Empty<byte>();
            entry.Property("KekVersion").CurrentValue = (short)1;
            await ctx.SaveChangesAsync();
        }

        await using var resolver = CreateResolver();
        var act = async () => await resolver.GetDataSourceAsync(tenantId);

        await act.Should().ThrowAsync<TenantConnectionStringMissingException>();
    }

    [Test]
    public async Task Decryptor_Failure_Throws_DecryptionException_Without_Envelope()
    {
        var tenantId = await SeedActiveTenantAsync();
        var failing = new ThrowingDecryptor();
        await using var resolver = CreateResolver(decryptor: failing);

        var act = async () => await resolver.GetDataSourceAsync(tenantId);

        var exception = await act.Should().ThrowAsync<TenantConnectionDecryptionException>();
        exception.Which.TenantId.Should().Be(tenantId);
        // Message must not include any envelope contents — assert it stays
        // terse and references only the tenant id.
        exception.Which.Message.Should().NotContain("Host=");
        exception.Which.Message.Should().NotContain("Password=");
    }

    [Test]
    public async Task LRU_Eviction_Disposes_Least_Recently_Used()
    {
        var t1 = await SeedActiveTenantAsync();
        var t2 = await SeedActiveTenantAsync();
        var t3 = await SeedActiveTenantAsync();

        var options = new TenantConnectionPoolOptions { MaxEntries = 2, MaxPoolSize = 5 };
        await using var resolver = CreateResolver(options);

        // Access in order t1 → t2 → t3.  After the third miss, t1 should be
        // evicted (oldest), t2 and t3 should remain.
        var ds1 = await resolver.GetDataSourceAsync(t1);
        await resolver.GetDataSourceAsync(t2);
        await resolver.GetDataSourceAsync(t3);

        _metrics.WarmPoolCount.Should().Be(2);
        _metrics.OpenedTotal.Should().Be(3);
        _metrics.EvictedTotal.Should().Be(1);

        // Re-fetching t1 must build a NEW data source — the old one was
        // disposed and removed from the cache. Decryptor count goes up.
        var calls = _decryptor.Calls;
        var ds1Again = await resolver.GetDataSourceAsync(t1);
        ReferenceEquals(ds1, ds1Again).Should().BeFalse(
            "evicted tenant must rebuild on next access");
        _decryptor.Calls.Should().Be(calls + 1);
    }

    [Test]
    public async Task LRU_Reposition_On_Hit_Saves_Recently_Used()
    {
        var t1 = await SeedActiveTenantAsync();
        var t2 = await SeedActiveTenantAsync();
        var t3 = await SeedActiveTenantAsync();

        var options = new TenantConnectionPoolOptions { MaxEntries = 2 };
        await using var resolver = CreateResolver(options);

        await resolver.GetDataSourceAsync(t1);
        await resolver.GetDataSourceAsync(t2);
        // Touch t1 — it's now most recently used; t2 becomes the LRU victim.
        await resolver.GetDataSourceAsync(t1);

        await resolver.GetDataSourceAsync(t3);

        // t2 should have been evicted (it was the LRU at the moment of t3
        // arrival), not t1.
        var calls = _decryptor.Calls;
        await resolver.GetDataSourceAsync(t1);
        _decryptor.Calls.Should().Be(calls,
            "t1 was reposition'd to MRU and must still be cached");
        await resolver.GetDataSourceAsync(t2);
        _decryptor.Calls.Should().Be(calls + 1,
            "t2 was evicted by t3's arrival and must rebuild");
    }

    [Test]
    public async Task EvictAsync_Removes_Tenant_And_Records_Eviction()
    {
        var tenantId = await SeedActiveTenantAsync();
        await using var resolver = CreateResolver();

        await resolver.GetDataSourceAsync(tenantId);
        _metrics.WarmPoolCount.Should().Be(1);

        await resolver.EvictAsync(tenantId);

        _metrics.WarmPoolCount.Should().Be(0);
        _metrics.EvictedTotal.Should().Be(1);
    }

    [Test]
    public async Task EvictAsync_NonExistent_Is_NoOp()
    {
        await using var resolver = CreateResolver();
        // Should not throw and should not move any counters.
        await resolver.EvictAsync(Guid.NewGuid());

        _metrics.WarmPoolCount.Should().Be(0);
        _metrics.EvictedTotal.Should().Be(0);
    }

    [Test]
    public async Task EvictAsync_Drops_Tenant_Row_Cache_So_Rotation_Picks_Up_New_String()
    {
        var tenantId = await SeedActiveTenantAsync(
            "Host=stub.invalid;Port=5432;Database=before;Username=u;Password=p");
        await using var resolver = CreateResolver();

        var first = await resolver.GetDataSourceAsync(tenantId);
        first.ConnectionString.Should().Contain("Database=before");

        // Rotate the encrypted column; mimic Story 28-12 KEK rotation.
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            var tenant = await ctx.Tenants.FirstAsync(t => t.Id == tenantId);
            ctx.Entry(tenant).Property("EncryptedConnectionString").CurrentValue =
                Encoding.UTF8.GetBytes(
                    "Host=stub.invalid;Port=5432;Database=after;Username=u;Password=p");
            await ctx.SaveChangesAsync();
        }

        // Without eviction the resolver still serves the warm pool.
        var stale = await resolver.GetDataSourceAsync(tenantId);
        stale.ConnectionString.Should().Contain("Database=before");

        // After eviction, the next lookup must read the new envelope.
        await resolver.EvictAsync(tenantId);
        var fresh = await resolver.GetDataSourceAsync(tenantId);
        fresh.ConnectionString.Should().Contain("Database=after");
    }

    [Test]
    public async Task GetStats_Reports_Live_Counters()
    {
        var t1 = await SeedActiveTenantAsync();
        await using var resolver = CreateResolver();

        await resolver.GetDataSourceAsync(t1);
        await resolver.GetDataSourceAsync(t1);

        var stats = resolver.GetStats();
        stats.WarmPoolCount.Should().Be(1);
        stats.TotalPoolsOpenedSinceStartup.Should().Be(1);
        stats.TotalPoolsEvictedSinceStartup.Should().Be(0);
    }

    [Test]
    public async Task ElsaDataSource_Mirrors_App_DataSource_Until_28_5()
    {
        var tenantId = await SeedActiveTenantAsync();
        await using var resolver = CreateResolver();

        var app = await resolver.GetDataSourceAsync(tenantId);
        var elsa = await resolver.GetElsaDataSourceAsync(tenantId);

        ReferenceEquals(app, elsa).Should().BeTrue();
    }

    [Test]
    public async Task Concurrent_First_Miss_Builds_Pool_Exactly_Once()
    {
        var tenantId = await SeedActiveTenantAsync();
        var slowDecryptor = new SlowDecryptor(TimeSpan.FromMilliseconds(50));
        await using var resolver = CreateResolver(decryptor: slowDecryptor);

        const int parallel = 25;
        var tasks = Enumerable.Range(0, parallel)
            .Select(_ => resolver.GetDataSourceAsync(tenantId).AsTask())
            .ToArray();

        var results = await Task.WhenAll(tasks);

        // Every caller saw the same data source — semaphore + double-check
        // collapsed the herd to one build.
        var distinct = results.Distinct(ReferenceEqualityComparer.Instance).Count();
        distinct.Should().Be(1, "thundering-herd guard must dedupe to a single build");
        slowDecryptor.Calls.Should().Be(1, "decryptor was invoked exactly once");
        _metrics.OpenedTotal.Should().Be(1);
    }

    [Test]
    public void Options_Validation_Rejects_Zero_MaxEntries()
    {
        Action act = () => _ = new LruPooledTenantConnectionResolver(
            _factory,
            _decryptor,
            _metrics,
            Options.Create(new TenantConnectionPoolOptions { MaxEntries = 0 }),
            NullLogger<LruPooledTenantConnectionResolver>.Instance);

        act.Should().Throw<ArgumentException>().WithMessage("*MaxEntries*");
    }

    [Test]
    public void Options_Validation_Rejects_Zero_MaxPoolSize()
    {
        Action act = () => _ = new LruPooledTenantConnectionResolver(
            _factory,
            _decryptor,
            _metrics,
            Options.Create(new TenantConnectionPoolOptions { MaxPoolSize = 0 }),
            NullLogger<LruPooledTenantConnectionResolver>.Instance);

        act.Should().Throw<ArgumentException>().WithMessage("*MaxPoolSize*");
    }

    [Test]
    public async Task Disposed_Resolver_Throws_On_Subsequent_Calls()
    {
        var resolver = CreateResolver();
        await resolver.DisposeAsync();

        var act = async () => await resolver.GetDataSourceAsync(Guid.NewGuid());
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Test]
    public async Task BuildDataSource_Applies_Configured_Pool_Size()
    {
        var tenantId = await SeedActiveTenantAsync();
        var options = new TenantConnectionPoolOptions
        {
            MaxPoolSize = 3,
            MinPoolSize = 1,
            ConnectionIdleLifetimeSeconds = 120,
            ConnectTimeoutSeconds = 7,
            CommandTimeoutSeconds = 17,
            KeepAliveSeconds = 41,
        };
        await using var resolver = CreateResolver(options);

        var ds = await resolver.GetDataSourceAsync(tenantId);
        var cs = ds.ConnectionString;

        cs.Should().Contain("Maximum Pool Size=3");
        cs.Should().Contain("Minimum Pool Size=1");
        cs.Should().Contain("Connection Idle Lifetime=120");
        cs.Should().Contain("Timeout=7");
        cs.Should().Contain("Command Timeout=17");
        cs.Should().Contain("Keepalive=41");
    }

    // ── H12: status-probe-driven cold-path forcing ────────────────────

    private LruPooledTenantConnectionResolver CreateResolverWithProbe(
        ITenantStatusProbe probe,
        TenantConnectionPoolOptions? options = null)
    {
        return new LruPooledTenantConnectionResolver(
            _factory,
            _decryptor,
            _metrics,
            Options.Create(options ?? new TenantConnectionPoolOptions()),
            NullLogger<LruPooledTenantConnectionResolver>.Instance,
            probe);
    }

    [Test]
    public async Task HotPath_Status_Probe_Active_Returns_Cached_Pool()
    {
        // Probe says "active" → fast path serves the warm pool, no
        // extra decryptor call.
        var tenantId = await SeedActiveTenantAsync();
        var probe = new RecordingProbe();
        await using var resolver = CreateResolverWithProbe(probe);

        await resolver.GetDataSourceAsync(tenantId);
        var callsAfterFirst = _decryptor.Calls;

        // Set the probe to "active" — hot path should bypass cold path.
        probe.Set(tenantId, "active");
        await resolver.GetDataSourceAsync(tenantId);

        _decryptor.Calls.Should().Be(callsAfterFirst,
            "active probe value must let the resolver serve the cache");
        _metrics.HitsTotal.Should().BeGreaterThan(0);
    }

    [Test]
    public async Task HotPath_Status_Probe_NonActive_Forces_Cold_Refresh_With_Exception()
    {
        // Tenant currently 'active'. Build the warm pool. Then flip CP
        // to 'provisioning' AND set the probe to 'provisioning' so the
        // hot path detects the flip + forces a cold CP read which
        // throws TenantNotProvisionedException.
        var tenantId = await SeedActiveTenantAsync();
        var probe = new RecordingProbe();
        await using var resolver = CreateResolverWithProbe(probe);

        // Warm the pool while the row is active.
        await resolver.GetDataSourceAsync(tenantId);

        // Mutate CP — Status flipped to provisioning.
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            var tenant = await ctx.Tenants.FirstAsync(t => t.Id == tenantId);
            ctx.Entry(tenant).Property("Status").CurrentValue = "provisioning";
            await ctx.SaveChangesAsync();
        }
        // Probe (admin endpoint just invalidated/set the cache).
        probe.Set(tenantId, "provisioning");

        // Next call must force the cold path → CP read raises the
        // structured non-provisioned exception.
        var act = async () => await resolver.GetDataSourceAsync(tenantId);
        await act.Should().ThrowAsync<TenantNotProvisionedException>()
            .Where(ex => ex.Status == "provisioning");
    }

    [Test]
    public async Task HotPath_Status_Probe_NoEntry_KeepsFastPathSemantics()
    {
        // Probe TryGet returns false → resolver must NOT change the
        // cache-hit fast path. This test pins the contract: probe miss
        // is a "no opinion" signal, not a "force refresh" signal.
        var tenantId = await SeedActiveTenantAsync();
        var probe = new RecordingProbe();  // empty
        await using var resolver = CreateResolverWithProbe(probe);

        await resolver.GetDataSourceAsync(tenantId);
        var callsAfterFirst = _decryptor.Calls;
        await resolver.GetDataSourceAsync(tenantId);

        _decryptor.Calls.Should().Be(callsAfterFirst,
            "probe miss must not force a cold rebuild");
    }

    // ── test doubles ──────────────────────────────────────────────────

    /// <summary>Read-only probe stub for the H12 hot-path checks.
    /// PF-C6: probe surface is strictly <c>TryGet</c>; the hidden
    /// <c>Remove</c> below is test-side state management for arranging
    /// arrange/act sequences and is intentionally NOT on the probe
    /// contract.</summary>
    private sealed class RecordingProbe : ITenantStatusProbe
    {
        private readonly Dictionary<Guid, string?> _entries = new();
        public int Reads;

        public void Set(Guid tenantId, string? status) => _entries[tenantId] = status;

        public bool TryGet(Guid tenantId, out string? status)
        {
            Reads++;
            return _entries.TryGetValue(tenantId, out status);
        }

        // Test-only helper — NOT part of ITenantStatusProbe (PF-C6).
        public void Remove(Guid tenantId) => _entries.Remove(tenantId);
    }

    private sealed class RecordingDecryptor : IConnectionStringDecryptor
    {
        public int Calls;
        public int? LastKekVersion;

        public string Decrypt(byte[] envelope, int? kekVersion)
        {
            Interlocked.Increment(ref Calls);
            LastKekVersion = kekVersion;
            return Encoding.UTF8.GetString(envelope);
        }
    }

    private sealed class ThrowingDecryptor : IConnectionStringDecryptor
    {
        public string Decrypt(byte[] envelope, int? kekVersion) =>
            throw new InvalidOperationException("auth tag mismatch");
    }

    private sealed class SlowDecryptor : IConnectionStringDecryptor
    {
        private readonly TimeSpan _delay;
        public int Calls;

        public SlowDecryptor(TimeSpan delay) { _delay = delay; }

        public string Decrypt(byte[] envelope, int? kekVersion)
        {
            Interlocked.Increment(ref Calls);
            Thread.Sleep(_delay);
            return Encoding.UTF8.GetString(envelope);
        }
    }
}
