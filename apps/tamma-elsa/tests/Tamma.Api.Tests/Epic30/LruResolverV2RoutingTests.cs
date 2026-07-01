using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using System.Collections.Concurrent;
using System.Text;
using Tamma.Api.Services.Provisioning.V2;
using Tamma.Api.Tests.Provisioning.V2;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Pooling;

namespace Tamma.Api.Tests.Epic30;

/// <summary>
/// Story 30-8 — V2 routing tests for
/// <see cref="LruPooledTenantConnectionResolver"/>. Covers the new
/// <see cref="ITenantEndpointDirectory"/> seam: dispatch through V2 when
/// the directory returns Applicable, fall back to the legacy decrypt
/// path when it returns NotApplicable, negative-cache the
/// <see cref="TenantNotProvisionedException"/> outcome, and preserve
/// the existing thundering-herd coalescing.
///
/// <para>The fixtures here keep the same EF-InMemory + recording-decryptor
/// shape as the Epic 28 suite — the V2 path is layered on top via a
/// <see cref="FakeEndpointDirectory"/> that exposes deterministic
/// hooks the tests drive directly.</para>
/// </summary>
[TestFixture]
public class LruResolverV2RoutingTests
{
    private string _dbName = null!;
    private ServiceProvider _sp = null!;
    private IDbContextFactory<ControlPlaneDbContext> _factory = null!;
    private RecordingDecryptor _decryptor = null!;
    private TenantConnectionPoolMetrics _metrics = null!;

    [SetUp]
    public void SetUp()
    {
        _dbName = $"v2-routing-{Guid.NewGuid():N}";
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
        ITenantEndpointDirectory? directory = null,
        TenantConnectionPoolOptions? options = null)
        => new(
            _factory,
            _decryptor,
            _metrics,
            Options.Create(options ?? new TenantConnectionPoolOptions()),
            NullLogger<LruPooledTenantConnectionResolver>.Instance,
            statusProbe: null,
            endpointDirectory: directory);

    private async Task<Guid> SeedTenantAsync(
        string legacyConnectionString = "Host=stub.invalid;Port=5432;Database=legacy;Username=u;Password=p",
        string status = "active")
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
        entry.Property("Status").CurrentValue = status;
        entry.Property("EncryptedConnectionString").CurrentValue =
            Encoding.UTF8.GetBytes(legacyConnectionString);
        entry.Property("KekVersion").CurrentValue = (short)1;
        await ctx.SaveChangesAsync();
        return tenantId;
    }

    // ─── 1. Dispatch via V2 when provider_key is set ──────────────────

    [Test]
    public async Task Resolver_DispatchesToV2Provider_WhenProviderKeyIsSet()
    {
        var tenantId = await SeedTenantAsync();
        var directory = new FakeEndpointDirectory();
        directory.SetResolved(
            tenantId,
            "Host=stub.invalid;Port=5432;Database=v2_routed;Username=v2;Password=p",
            engineUrl: "https://engine-abc.cranl.net",
            providerKey: "cranl");

        await using var resolver = CreateResolver(directory);

        var ds = await resolver.GetDataSourceAsync(tenantId);

        // V2 path produced the data source — verify by the database
        // name embedded in the resolved connection string.
        ds.ConnectionString.Should().Contain("Database=v2_routed");
        // Legacy decrypt MUST NOT have been invoked.
        _decryptor.Calls.Should().Be(0,
            "V2 directory provided the connection string; the encrypted-column decryptor must be skipped");
        directory.ResolveCalls.Should().Be(1);
    }

    // ─── 1b. Epic 30 Phase B (B1) — the REAL directory never bypasses ─
    //
    // The tests above exercise the resolver's generic seam via a
    // test-double directory (FakeEndpointDirectory) that CAN return
    // Applicable — that seam is intentionally retained. This test proves
    // that the PRODUCTION V2TenantEndpointDirectory never uses it for DB
    // routing: a provider-keyed (provider_key set) tenant resolves its DB
    // connection through the unified EncryptedConnectionString envelope,
    // NOT the provider's DatabaseUrl.

    [Test]
    public async Task Resolver_WithRealV2Directory_RoutesProviderKeyedTenant_ViaUnifiedEnvelope()
    {
        var tenantId = await SeedTenantAsync(
            legacyConnectionString:
                "Host=stub.invalid;Port=5432;Database=unified;Username=u;Password=p");

        var provider = new FakeTenantInfrastructureProvider("cranl")
        {
            // The provider WOULD hand back a routable DatabaseUrl, but B1
            // means DB routing never consults it. If the directory calls
            // this, the test fails loudly.
            OnResolveEndpoints = (_, _) => throw new InvalidOperationException(
                "provider ResolveEndpointsAsync must not be consulted for DB routing (B1)"),
        };
        var registry = new TenantProviderRegistry(
            new ITenantInfrastructureProvider[] { provider });
        var directory = new V2TenantEndpointDirectory(
            registry,
            new StubProviderKeyLookup("cranl"),
            NullLogger<V2TenantEndpointDirectory>.Instance);

        await using var resolver = CreateResolver(directory);

        var ds = await resolver.GetDataSourceAsync(tenantId);

        ds.ConnectionString.Should().Contain("Database=unified",
            "the provider-keyed tenant must be routed via the unified envelope");
        _decryptor.Calls.Should().Be(1,
            "the unified EncryptedConnectionString decrypt path must be taken — " +
            "the provider DatabaseUrl bypass is removed in B1");
    }

    // ─── 2. Fall back to legacy path when provider_key is null ────────

    [Test]
    public async Task Resolver_FallsBackToLegacy_WhenProviderKeyIsNull()
    {
        var tenantId = await SeedTenantAsync();
        var directory = new FakeEndpointDirectory();
        // Default behaviour: TryResolveAsync returns NotApplicable for
        // unconfigured tenants (mimics provider_key=NULL).

        await using var resolver = CreateResolver(directory);

        var ds = await resolver.GetDataSourceAsync(tenantId);

        ds.ConnectionString.Should().Contain("Database=legacy");
        _decryptor.Calls.Should().Be(1,
            "directory returned NotApplicable; resolver must fall through to the legacy decrypt path");
        directory.ResolveCalls.Should().Be(1);
    }

    // ─── 3. Fall back to legacy when provider key isn't registered ────

    [Test]
    public async Task Resolver_FallsBackToLegacy_WhenProviderUnregistered()
    {
        var tenantId = await SeedTenantAsync();
        // The directory itself returns NotApplicable when its registry
        // doesn't know the tenant's provider key — same observable
        // behaviour from the resolver's POV.
        var directory = new FakeEndpointDirectory(); // default = NotApplicable

        await using var resolver = CreateResolver(directory);

        var ds = await resolver.GetDataSourceAsync(tenantId);

        ds.ConnectionString.Should().Contain("Database=legacy");
        _decryptor.Calls.Should().Be(1);
    }

    // ─── 4. V2 result is cached with LRU semantics ────────────────────

    [Test]
    public async Task Resolver_CachesV2Result_WithLruSemantics()
    {
        var tenantId = await SeedTenantAsync();
        var directory = new FakeEndpointDirectory();
        directory.SetResolved(
            tenantId,
            "Host=stub.invalid;Port=5432;Database=v2_cached;Username=u;Password=p",
            engineUrl: null,
            providerKey: "hetzner");

        await using var resolver = CreateResolver(directory);

        var first = await resolver.GetDataSourceAsync(tenantId);
        var second = await resolver.GetDataSourceAsync(tenantId);
        var third = await resolver.GetDataSourceAsync(tenantId);

        // Same warm pool returned — LRU cache hit semantics preserved.
        ReferenceEquals(first, second).Should().BeTrue();
        ReferenceEquals(second, third).Should().BeTrue();

        // V2 directory consulted exactly once — for the cold miss.
        directory.ResolveCalls.Should().Be(1,
            "subsequent hits must be served from the LRU cache, not re-dispatch through the directory");
        _metrics.HitsTotal.Should().Be(2);
        _metrics.MissesTotal.Should().Be(1);
        _metrics.OpenedTotal.Should().Be(1);
    }

    // ─── 5. Negative-cache the not-provisioned outcome ────────────────

    [Test]
    public async Task Resolver_NegativeCachesNotProvisioned_BrieflyToAvoidStorm()
    {
        var tenantId = await SeedTenantAsync();
        var directory = new FakeEndpointDirectory();
        directory.SetNotProvisioned(tenantId, status: "cranl");

        var options = new TenantConnectionPoolOptions
        {
            // Long enough for the burst test below to all land inside
            // the negative-cache window. The burst itself runs in
            // milliseconds.
            NotProvisionedNegativeCacheSeconds = 30,
        };
        await using var resolver = CreateResolver(directory, options);

        // First call MUST throw and populate the negative cache.
        var first = async () => await resolver.GetDataSourceAsync(tenantId);
        await first.Should().ThrowAsync<TenantNotProvisionedException>();
        directory.ResolveCalls.Should().Be(1);

        // Burst of subsequent calls — all must short-circuit through
        // the negative cache without re-dispatching to the directory.
        const int burst = 20;
        for (var i = 0; i < burst; i++)
        {
            var act = async () => await resolver.GetDataSourceAsync(tenantId);
            await act.Should().ThrowAsync<TenantNotProvisionedException>();
        }
        directory.ResolveCalls.Should().Be(1,
            "negative cache must absorb the storm; directory called exactly once");
    }

    [Test]
    public async Task Resolver_EvictAsync_ClearsNegativeCache_SoStatusFlipPropagates()
    {
        var tenantId = await SeedTenantAsync();
        var directory = new FakeEndpointDirectory();
        directory.SetNotProvisioned(tenantId, status: "cranl");

        var options = new TenantConnectionPoolOptions
        {
            NotProvisionedNegativeCacheSeconds = 60,
        };
        await using var resolver = CreateResolver(directory, options);

        // Populate the negative cache.
        var act = async () => await resolver.GetDataSourceAsync(tenantId);
        await act.Should().ThrowAsync<TenantNotProvisionedException>();
        directory.ResolveCalls.Should().Be(1);

        // Status flips (provisioning → ready). Eviction is the standard
        // signal — matches the real status-invalidation listener path.
        directory.SetResolved(
            tenantId,
            "Host=stub.invalid;Port=5432;Database=now_ready;Username=u;Password=p",
            engineUrl: null,
            providerKey: "cranl");
        await resolver.EvictAsync(tenantId);

        // The next call must re-dispatch to the directory and succeed.
        var ds = await resolver.GetDataSourceAsync(tenantId);
        ds.ConnectionString.Should().Contain("Database=now_ready");
        directory.ResolveCalls.Should().Be(2);
    }

    // ─── 6. Concurrent cold-misses collapse to one V2 call ────────────

    [Test]
    public async Task Resolver_ConcurrentResolves_OnlyHitsProviderOnce()
    {
        var tenantId = await SeedTenantAsync();
        var directory = new FakeEndpointDirectory(perCallDelay: TimeSpan.FromMilliseconds(50));
        directory.SetResolved(
            tenantId,
            "Host=stub.invalid;Port=5432;Database=coalesced;Username=u;Password=p",
            engineUrl: null,
            providerKey: "cranl");

        await using var resolver = CreateResolver(directory);

        const int parallel = 25;
        var tasks = Enumerable.Range(0, parallel)
            .Select(_ => resolver.GetDataSourceAsync(tenantId).AsTask())
            .ToArray();

        var results = await Task.WhenAll(tasks);

        var distinct = results.Distinct(ReferenceEqualityComparer.Instance).Count();
        distinct.Should().Be(1,
            "per-tenant build lock must collapse the burst to a single V2 dispatch");
        directory.ResolveCalls.Should().Be(1,
            "thundering-herd guard must apply to the V2 directory call too");
        _metrics.OpenedTotal.Should().Be(1);
    }

    // ─── Test doubles ─────────────────────────────────────────────────

    /// <summary>
    /// Deterministic <see cref="ITenantEndpointDirectory"/> for unit
    /// tests. Three configurable behaviours per tenant:
    /// <list type="bullet">
    ///   <item><description>Default — return NotApplicable.</description></item>
    ///   <item><description><c>SetResolved</c> — return Applicable
    ///     with a fixed connection string.</description></item>
    ///   <item><description><c>SetNotProvisioned</c> — throw
    ///     TenantNotProvisionedException.</description></item>
    /// </list>
    /// Tracks call count via the thread-safe <see cref="ResolveCalls"/>
    /// counter so concurrent-test assertions are reliable.
    /// </summary>
    private sealed class FakeEndpointDirectory : ITenantEndpointDirectory
    {
        private readonly ConcurrentDictionary<Guid, Behaviour> _behaviour = new();
        private readonly TimeSpan _perCallDelay;
        private int _resolveCalls;

        public FakeEndpointDirectory(TimeSpan? perCallDelay = null)
        {
            _perCallDelay = perCallDelay ?? TimeSpan.Zero;
        }

        public int ResolveCalls => Volatile.Read(ref _resolveCalls);

        public void SetResolved(Guid tenantId, string databaseUrl, string? engineUrl, string providerKey)
        {
            _behaviour[tenantId] = new Behaviour
            {
                Kind = BehaviourKind.Resolved,
                DatabaseUrl = databaseUrl,
                EngineUrl = engineUrl,
                ProviderKey = providerKey,
            };
        }

        public void SetNotProvisioned(Guid tenantId, string status)
        {
            _behaviour[tenantId] = new Behaviour
            {
                Kind = BehaviourKind.NotProvisioned,
                Status = status,
            };
        }

        public async Task<TenantEndpointResolution> TryResolveAsync(
            Guid tenantId,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _resolveCalls);
            if (_perCallDelay > TimeSpan.Zero)
                await Task.Delay(_perCallDelay, cancellationToken).ConfigureAwait(false);

            if (!_behaviour.TryGetValue(tenantId, out var b))
                return TenantEndpointResolution.NotApplicable;

            return b.Kind switch
            {
                BehaviourKind.Resolved => TenantEndpointResolution.Resolved(
                    b.DatabaseUrl!, b.EngineUrl, b.ProviderKey!),
                BehaviourKind.NotProvisioned => throw new TenantNotProvisionedException(
                    tenantId, b.Status),
                _ => TenantEndpointResolution.NotApplicable,
            };
        }

        private enum BehaviourKind { NotApplicable, Resolved, NotProvisioned }

        private sealed class Behaviour
        {
            public BehaviourKind Kind { get; init; }
            public string? DatabaseUrl { get; init; }
            public string? EngineUrl { get; init; }
            public string? ProviderKey { get; init; }
            public string? Status { get; init; }
        }
    }

    /// <summary>
    /// Minimal <see cref="ITenantProviderKeyLookup"/> that returns a fixed
    /// provider key for every tenant — used to drive the REAL
    /// <see cref="V2TenantEndpointDirectory"/> in the B1 end-to-end test.
    /// </summary>
    private sealed class StubProviderKeyLookup : ITenantProviderKeyLookup
    {
        private readonly string? _providerKey;

        public StubProviderKeyLookup(string? providerKey) => _providerKey = providerKey;

        public Task<string?> GetProviderKeyAsync(Guid tenantId, CancellationToken ct)
            => Task.FromResult(_providerKey);
    }

    private sealed class RecordingDecryptor : IConnectionStringDecryptor
    {
        public int Calls;

        public string Decrypt(byte[] envelope, int? kekVersion)
        {
            Interlocked.Increment(ref Calls);
            return Encoding.UTF8.GetString(envelope);
        }
    }
}
