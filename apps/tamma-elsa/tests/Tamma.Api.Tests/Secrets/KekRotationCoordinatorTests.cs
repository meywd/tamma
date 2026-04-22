using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Npgsql;
using Tamma.Api.Services.PlatformEvents;
using Tamma.Api.Services.Secrets;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Secrets;

/// <summary>
/// Story 28-12 — unit suite for
/// <see cref="KekRotationCoordinator"/>. Backs the control plane with
/// EF InMemory and a recording resolver/event-bus so the rotation
/// pipeline can be exercised end-to-end without Postgres.
///
/// <para>The test fixtures purposefully use a known-good <see cref="byte[]"/>
/// KEK rather than the runtime
/// <see cref="System.Security.Cryptography.RandomNumberGenerator"/>
/// path so the assertions can verify the new envelope decrypts under
/// the supplied key.</para>
/// </summary>
[TestFixture]
public class KekRotationCoordinatorTests
{
    private string _dbName = null!;
    private ServiceProvider _sp = null!;
    private IDbContextFactory<ControlPlaneDbContext> _factory = null!;
    private RecordingResolver _resolver = null!;
    private RecordingEventRepository _eventRepo = null!;
    private byte[] _initialPrimary = null!;

    private static byte[] BuildKek(byte seed)
    {
        var key = new byte[32];
        for (var i = 0; i < key.Length; i++) key[i] = (byte)(seed + i);
        return key;
    }

    [SetUp]
    public void SetUp()
    {
        _dbName = $"kek-rotation-test-{Guid.NewGuid():N}";
        _initialPrimary = BuildKek(seed: 1);
        _resolver = new RecordingResolver();
        _eventRepo = new RecordingEventRepository();

        var services = new ServiceCollection();
        services.AddDbContextFactory<ControlPlaneDbContext>(
            options => options.UseInMemoryDatabase(_dbName));
        services.AddSingleton<IPlatformEventRepository>(_eventRepo);
        // Bus is optional in the coordinator — register one so the
        // publish path is exercised end-to-end.
        services.AddLogging();
        services.AddSingleton<IPlatformEventBus, InMemoryPlatformEventBus>();
        _sp = services.BuildServiceProvider();
        _factory = _sp.GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>();
    }

    [TearDown]
    public void TearDown()
    {
        _sp.Dispose();
    }

    private KekProvider BuildProvider(byte[]? primary = null)
    {
        var dict = new Dictionary<string, string?>
        {
            [KekProvider.PrimaryConfigKey] = Convert.ToBase64String(primary ?? _initialPrimary),
            [KekProvider.ActiveVersionConfigKey] = "1",
        };
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        return new KekProvider(cfg, NullLogger<KekProvider>.Instance);
    }

    private KekRotationCoordinator BuildCoordinator(KekProvider provider)
    {
        return new KekRotationCoordinator(
            _sp.GetRequiredService<IServiceScopeFactory>(),
            provider,
            _resolver,
            NullLogger<KekRotationCoordinator>.Instance);
    }

    private async Task<Guid> SeedTenantAsync(string connectionString, byte[] kek, int kekVersion = 1)
    {
        var tenantId = Guid.NewGuid();
        var envelope = AesGcmConnectionStringDecryptor.EncryptWithKey(connectionString, kek);
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
        entry.Property("EncryptedConnectionString").CurrentValue = envelope;
        entry.Property("KekVersion").CurrentValue = kekVersion;
        await ctx.SaveChangesAsync();
        return tenantId;
    }

    [Test]
    public async Task Rotation_Reencrypts_Every_Tenant_And_Promotes_Key()
    {
        const string cs1 = "Host=h1;Database=t1;Username=u;Password=p";
        const string cs2 = "Host=h2;Database=t2;Username=u;Password=p";

        var t1 = await SeedTenantAsync(cs1, _initialPrimary);
        var t2 = await SeedTenantAsync(cs2, _initialPrimary);

        var provider = BuildProvider();
        var coordinator = BuildCoordinator(provider);
        var newKek = BuildKek(seed: 99);

        var initialStatus = coordinator.StartAsync(newKek);
        initialStatus.Phase.Should().Be(KekRotationPhase.Running);

        await coordinator.WaitForCompletionAsync();
        var finalStatus = coordinator.GetStatus();

        finalStatus.Phase.Should().Be(KekRotationPhase.Completed);
        finalStatus.TotalTenants.Should().Be(2);
        finalStatus.ReencryptedTenants.Should().Be(2);
        finalStatus.FailedTenants.Should().Be(0);
        finalStatus.FromVersion.Should().Be(1);
        finalStatus.ToVersion.Should().Be(2);

        // Verify both tenant rows were written with the NEW envelope and
        // bumped KekVersion.
        await using var ctx = await _factory.CreateDbContextAsync();
        foreach (var (tenantId, expectedCs) in new[] { (t1, cs1), (t2, cs2) })
        {
            var tenant = await ctx.Tenants.IgnoreQueryFilters()
                .FirstAsync(t => t.Id == tenantId);
            var entry = ctx.Entry(tenant);
            var envelope = (byte[])entry.Property("EncryptedConnectionString").CurrentValue!;
            var version = (int?)entry.Property("KekVersion").CurrentValue;

            version.Should().Be(2);
            var decrypted = AesGcmConnectionStringDecryptor.DecryptWithKey(envelope, newKek);
            decrypted.Should().Be(expectedCs);
        }

        // Resolver must have been evicted for both tenants so the next
        // request reads the freshly rotated row.
        _resolver.Evictions.Should().Contain(t1).And.Contain(t2);
        _resolver.Evictions.Should().HaveCount(2);

        // Provider promoted: secondary cleared, version bumped, primary
        // is the new key.
        provider.GetActiveVersion().Should().Be(2);
        provider.GetPrimary().Should().BeEquivalentTo(newKek);
        provider.GetSecondary().Should().BeNull();
    }

    [Test]
    public async Task Rotation_Emits_Started_Step_And_Completed_Events()
    {
        const string cs = "Host=h;Database=t;Username=u;Password=p";
        var tenantId = await SeedTenantAsync(cs, _initialPrimary);

        var provider = BuildProvider();
        var coordinator = BuildCoordinator(provider);

        coordinator.StartAsync(BuildKek(seed: 50));
        await coordinator.WaitForCompletionAsync();

        var types = _eventRepo.AppendedEvents.Select(e => e.Type).ToList();
        types.Should().Contain("SECRETS.KEK.ROTATION.STARTED");
        types.Should().Contain("TENANT.CONNECTION_STRING_ROTATED.SUCCESS");
        types.Should().Contain("SECRETS.KEK.ROTATION.COMPLETED");

        // Per-tenant step event carries the tenant id.
        var stepEvent = _eventRepo.AppendedEvents
            .First(e => e.Type == "TENANT.CONNECTION_STRING_ROTATED.SUCCESS");
        stepEvent.TenantId.Should().Be(tenantId);
    }

    [Test]
    public async Task Rotation_With_No_Tenants_Completes_And_Promotes()
    {
        var provider = BuildProvider();
        var coordinator = BuildCoordinator(provider);

        coordinator.StartAsync(BuildKek(seed: 77));
        await coordinator.WaitForCompletionAsync();

        var status = coordinator.GetStatus();
        status.Phase.Should().Be(KekRotationPhase.Completed);
        status.TotalTenants.Should().Be(0);
        status.ReencryptedTenants.Should().Be(0);
        provider.GetActiveVersion().Should().Be(2);
    }

    [Test]
    public async Task Rotation_Skips_Rows_Already_At_Target_Version()
    {
        // A row already at KekVersion=2 is left alone. The coordinator
        // bumps to version 2 (since the active is 1), so this row's
        // KekVersion(2) >= toVersion(2) — skipped.
        const string cs = "Host=h;Database=t;Username=u;Password=p";
        await SeedTenantAsync(cs, BuildKek(seed: 99), kekVersion: 2);

        var provider = BuildProvider();
        var coordinator = BuildCoordinator(provider);

        coordinator.StartAsync(BuildKek(seed: 99));
        await coordinator.WaitForCompletionAsync();

        var status = coordinator.GetStatus();
        status.TotalTenants.Should().Be(0,
            "rows already at target version are filtered out at query time");
        status.Phase.Should().Be(KekRotationPhase.Completed);
    }

    [Test]
    public async Task Concurrent_Start_Returns_Running_Snapshot_Without_Restaging()
    {
        const string cs = "Host=h;Database=t;Username=u;Password=p";
        await SeedTenantAsync(cs, _initialPrimary);

        var provider = BuildProvider();
        var coordinator = BuildCoordinator(provider);
        var firstKek = BuildKek(seed: 50);
        var secondKek = BuildKek(seed: 60);

        // Kick off and immediately try to start a second one. The second
        // call must see Phase=Running and not stage a new key.
        coordinator.StartAsync(firstKek);
        var secondStatus = coordinator.StartAsync(secondKek);

        secondStatus.Phase.Should().Be(KekRotationPhase.Running);

        await coordinator.WaitForCompletionAsync();
        // The promoted primary must be the FIRST kek, not the second.
        provider.GetPrimary().Should().BeEquivalentTo(firstKek);
    }

    [Test]
    public async Task Decrypt_Failure_Marks_Tenant_Failed_And_Does_Not_Promote()
    {
        // Seed a row whose envelope was encrypted under a key the
        // coordinator does NOT know about. The decrypt loop fails for
        // this row; promotion is skipped because the failure count > 0.
        const string cs = "Host=h;Database=t;Username=u;Password=p";
        var corruptKek = BuildKek(seed: 200);
        await SeedTenantAsync(cs, corruptKek); // ← encrypted under unknown key

        var provider = BuildProvider();
        var coordinator = BuildCoordinator(provider);

        coordinator.StartAsync(BuildKek(seed: 50));
        await coordinator.WaitForCompletionAsync();

        var status = coordinator.GetStatus();
        status.Phase.Should().Be(KekRotationPhase.Failed);
        status.FailedTenants.Should().Be(1);
        status.ReencryptedTenants.Should().Be(0);
        status.FailureReason.Should().NotBeNull();

        // Old primary is still the active key — operator's safety net.
        provider.GetActiveVersion().Should().Be(1);
        provider.GetPrimary().Should().BeEquivalentTo(_initialPrimary);
    }

    [Test]
    public void GetStatus_Idle_When_Never_Started()
    {
        var provider = BuildProvider();
        var coordinator = BuildCoordinator(provider);

        var status = coordinator.GetStatus();

        status.Phase.Should().Be(KekRotationPhase.Idle);
        status.TotalTenants.Should().Be(0);
        status.StartedAt.Should().BeNull();
        status.CompletedAt.Should().BeNull();
    }

    [Test]
    public async Task Start_Without_Primary_Marks_Rotation_Failed()
    {
        // Build a provider with NO primary — StartAsync stages the
        // secondary successfully but the background task fails because
        // it cannot read the old primary to decrypt with.
        var dict = new Dictionary<string, string?>
        {
            [KekProvider.ActiveVersionConfigKey] = "1",
        };
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        var provider = new KekProvider(cfg, NullLogger<KekProvider>.Instance);
        var coordinator = BuildCoordinator(provider);

        coordinator.StartAsync(BuildKek(seed: 50));
        await coordinator.WaitForCompletionAsync();

        var status = coordinator.GetStatus();
        status.Phase.Should().Be(KekRotationPhase.Failed);
        status.FailureReason.Should().NotBeNull();
        status.FailureReason!.ToLowerInvariant().Should().Contain("primary");
    }

    [Test]
    public void Start_With_Wrong_Length_Kek_Throws()
    {
        var provider = BuildProvider();
        var coordinator = BuildCoordinator(provider);

        Action act = () => coordinator.StartAsync(new byte[16]);

        act.Should().Throw<ArgumentException>();
    }

    // ── helpers ───────────────────────────────────────────────────────

    private sealed class RecordingResolver : ITenantConnectionResolver
    {
        public List<Guid> Evictions { get; } = new();

        public ValueTask<NpgsqlDataSource> GetDataSourceAsync(
            Guid tenantId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("not exercised by these tests");

        public ValueTask<NpgsqlDataSource> GetElsaDataSourceAsync(
            Guid tenantId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("not exercised by these tests");

        public ValueTask EvictAsync(Guid tenantId, CancellationToken cancellationToken = default)
        {
            Evictions.Add(tenantId);
            return ValueTask.CompletedTask;
        }

        public TenantConnectionPoolStats GetStats() => new(0, 0, 0);
    }

    private sealed class RecordingEventRepository : IPlatformEventRepository
    {
        public List<PlatformEvent> AppendedEvents { get; } = new();

        public Task<PlatformEvent?> AppendAsync(PlatformEvent evt, CancellationToken ct = default)
        {
            evt.Id = Guid.NewGuid();
            evt.CreatedAt = DateTime.UtcNow;
            AppendedEvents.Add(evt);
            return Task.FromResult<PlatformEvent?>(evt);
        }

        public Task<PlatformEvent?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(AppendedEvents.FirstOrDefault(e => e.Id == id));

        public Task<IReadOnlyList<PlatformEvent>> QueryAsync(
            Guid? tenantId = null,
            Guid? userId = null,
            string? typePrefix = null,
            DateTime? since = null,
            bool includePlatformWide = false,
            int limit = 100,
            CancellationToken ct = default)
        {
            IEnumerable<PlatformEvent> query = AppendedEvents;
            if (tenantId is not null)
            {
                query = includePlatformWide
                    ? query.Where(e => e.TenantId == tenantId || e.TenantId == null)
                    : query.Where(e => e.TenantId == tenantId);
            }
            if (userId is not null) query = query.Where(e => e.UserId == userId);
            if (typePrefix is not null) query = query.Where(e => e.Type.StartsWith(typePrefix));
            if (since is not null) query = query.Where(e => e.CreatedAt >= since);
            return Task.FromResult<IReadOnlyList<PlatformEvent>>(
                query.OrderByDescending(e => e.CreatedAt).Take(limit).ToList());
        }
    }
}
