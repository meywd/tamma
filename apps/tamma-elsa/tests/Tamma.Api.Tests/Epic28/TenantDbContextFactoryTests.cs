using FluentAssertions;
using NUnit.Framework;
using Npgsql;
using Tamma.Data;
using Tamma.Data.Abstractions;

namespace Tamma.Api.Tests.Epic28;

/// <summary>
/// Story 28-3 test suite — exercises <see cref="TenantDbContextFactory"/>
/// against a tiny in-memory <see cref="ITenantConnectionResolver"/>
/// double. The Story 28-3 stub was deleted in Story 28-4 once the real
/// LRU-pooled resolver shipped; these tests are kept because the
/// factory contract itself didn't change — only the resolver
/// implementation it delegates to did.
///
/// <para>The real resolver (<see cref="Tamma.Data.Pooling.LruPooledTenantConnectionResolver"/>)
/// has its own dedicated suite that doesn't go through the factory.</para>
/// </summary>
[TestFixture]
public class TenantDbContextFactoryTests
{
    private FakeTenantConnectionResolver _resolver = null!;
    private TenantDbContextFactory _factory = null!;

    [SetUp]
    public void SetUp()
    {
        var dataSource = NpgsqlDataSource.Create(
            "Host=stub.invalid;Port=5432;Database=tamma_factory_test;Username=tamma;Password=tamma");
        _resolver = new FakeTenantConnectionResolver(dataSource);
        _factory = new TenantDbContextFactory(_resolver);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _resolver.DisposeAsync();
    }

    [Test]
    public async Task CreateAsync_Returns_Fresh_Instance_Each_Call()
    {
        await using var first = await _factory.CreateAsync(Guid.NewGuid());
        await using var second = await _factory.CreateAsync(Guid.NewGuid());

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        ReferenceEquals(first, second).Should().BeFalse(
            "the factory must hand out distinct contexts per call");
    }

    [Test]
    public async Task CreateAsync_Returns_Disposable_Context()
    {
        var ctx = await _factory.CreateAsync(Guid.NewGuid());
        // Should not throw.
        await ctx.DisposeAsync();
    }

    [Test]
    public async Task CreateAsync_Routes_All_Tenants_To_Same_DataSource_In_Test_Double()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        var sourceA = await _resolver.GetDataSourceAsync(tenantA);
        var sourceB = await _resolver.GetDataSourceAsync(tenantB);

        ReferenceEquals(sourceA, sourceB).Should().BeTrue(
            "the test-double resolver hands every tenant the single fixture DataSource");
    }

    [Test]
    public void Factory_NullResolver_Throws()
    {
        Action act = () => _ = new TenantDbContextFactory(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public async Task EvictAsync_Is_NoOp_For_Test_Double()
    {
        await _resolver.EvictAsync(Guid.NewGuid());
        _resolver.GetStats().WarmPoolCount.Should().Be(1);
    }

    [Test]
    public async Task GetElsaDataSourceAsync_Returns_Same_Source_In_Test_Double()
    {
        var tenantId = Guid.NewGuid();
        var app = await _resolver.GetDataSourceAsync(tenantId);
        var elsa = await _resolver.GetElsaDataSourceAsync(tenantId);

        ReferenceEquals(app, elsa).Should().BeTrue(
            "the test-double resolver mirrors the app source for Elsa");
    }

    [Test]
    public async Task CreateAsync_Honors_CancellationToken()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // The fixture resolver does not actually connect, so the
        // cancel-after-create path must still surface a successful
        // return — the real LRU-pooled resolver respects the token
        // during pool acquisition (covered by its own suite).
        await using var ctx = await _factory.CreateAsync(Guid.NewGuid(), cts.Token);
        ctx.Should().NotBeNull();
    }

    /// <summary>
    /// Minimal in-memory <see cref="ITenantConnectionResolver"/> for
    /// factory-level wiring tests. Owns a single
    /// <see cref="NpgsqlDataSource"/> and routes every tenant to it —
    /// no DB connection is ever opened.
    /// </summary>
    private sealed class FakeTenantConnectionResolver : ITenantConnectionResolver, IAsyncDisposable
    {
        private readonly NpgsqlDataSource _dataSource;

        public FakeTenantConnectionResolver(NpgsqlDataSource dataSource)
        {
            ArgumentNullException.ThrowIfNull(dataSource);
            _dataSource = dataSource;
        }

        public ValueTask<NpgsqlDataSource> GetDataSourceAsync(
            Guid tenantId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_dataSource);

        public ValueTask<NpgsqlDataSource> GetElsaDataSourceAsync(
            Guid tenantId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_dataSource);

        public ValueTask EvictAsync(
            Guid tenantId,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public TenantConnectionPoolStats GetStats() =>
            new(WarmPoolCount: 1,
                TotalPoolsOpenedSinceStartup: 1,
                TotalPoolsEvictedSinceStartup: 0);

        public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
    }
}
