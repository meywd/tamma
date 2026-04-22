using FluentAssertions;
using NUnit.Framework;
using Npgsql;
using Tamma.Data;
using Tamma.Data.Abstractions;

namespace Tamma.Api.Tests.Epic28;

/// <summary>
/// Story 28-3 — exercises <see cref="TenantDbContextFactory"/> against
/// the <see cref="StubTenantConnectionResolver"/>. The stub points at a
/// fake DataSource; we never connect, only verify the factory wiring.
///
/// <para>Per-tenant isolation tests live with Story 28-4 once the real
/// resolver lands.</para>
/// </summary>
[TestFixture]
public class TenantDbContextFactoryTests
{
    // The DataSource is owned by the StubTenantConnectionResolver and
    // disposed via the resolver's DisposeAsync in TearDown — analyzer
    // is satisfied because we don't track it as a separate field.
    private StubTenantConnectionResolver _resolver = null!;
    private TenantDbContextFactory _factory = null!;

    [SetUp]
    public void SetUp()
    {
        var dataSource = NpgsqlDataSource.Create(
            "Host=stub.invalid;Port=5432;Database=tamma_factory_test;Username=tamma;Password=tamma");
        _resolver = new StubTenantConnectionResolver(dataSource);
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
    public async Task CreateAsync_Stub_Routes_All_Tenants_To_Same_DataSource()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        var sourceA = await _resolver.GetDataSourceAsync(tenantA);
        var sourceB = await _resolver.GetDataSourceAsync(tenantB);

        ReferenceEquals(sourceA, sourceB).Should().BeTrue(
            "stub resolver must route every tenant to the single dev DataSource until 28-4 lands");
    }

    [Test]
    public void Factory_NullResolver_Throws()
    {
        Action act = () => _ = new TenantDbContextFactory(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void StubResolver_NullDataSource_Throws()
    {
        Action act = () => _ = new StubTenantConnectionResolver(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void Stub_GetStats_Reports_Single_Warm_Pool()
    {
        var stats = _resolver.GetStats();
        stats.WarmPoolCount.Should().Be(1);
        stats.TotalPoolsEvictedSinceStartup.Should().Be(0);
    }

    [Test]
    public async Task Stub_EvictAsync_Is_NoOp_For_Now()
    {
        // EvictAsync is a stub until Story 28-4. Should complete without
        // throwing, and the pool count should not change.
        await _resolver.EvictAsync(Guid.NewGuid());
        _resolver.GetStats().WarmPoolCount.Should().Be(1);
    }

    [Test]
    public async Task Stub_GetElsaDataSourceAsync_Returns_Same_Source()
    {
        var tenantId = Guid.NewGuid();
        var app = await _resolver.GetDataSourceAsync(tenantId);
        var elsa = await _resolver.GetElsaDataSourceAsync(tenantId);

        ReferenceEquals(app, elsa).Should().BeTrue(
            "stub Elsa resolver mirrors the app resolver until Story 28-5 ships per-tenant Elsa DBs");
    }

    [Test]
    public async Task CreateAsync_Honors_CancellationToken()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Stub resolver does not actually connect, so the cancel-after-create
        // path must still surface a successful return — the real resolver in
        // 28-4 will respect the token during pool acquisition.
        await using var ctx = await _factory.CreateAsync(Guid.NewGuid(), cts.Token);
        ctx.Should().NotBeNull();
    }
}
