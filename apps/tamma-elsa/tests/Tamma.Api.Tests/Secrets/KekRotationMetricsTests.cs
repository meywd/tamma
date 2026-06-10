using System.Diagnostics.Metrics;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.PlatformEvents;
using Tamma.Api.Services.Secrets;
using Tamma.Api.Tests.TestDoubles;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Secrets;

/// <summary>
/// Story 28-12 (AC5 residual) — verifies
/// <see cref="KekRotationMetrics"/> publishes the
/// <c>tamma.kek_rotation.remaining</c> ObservableGauge under the
/// <see cref="KekRotationMetrics.MeterName"/> meter and that the gauge
/// reads "tenants still needing rekey" live from the coordinator's
/// status snapshot.
///
/// <para>Backs the control plane with EF InMemory + recording doubles —
/// the same single-process fixture the
/// <see cref="KekRotationCoordinatorTests"/> use (no NpgsqlDataSource
/// registered ⇒ the advisory lock is a no-op and the in-process lock
/// is the only guard).</para>
/// </summary>
[TestFixture]
public class KekRotationMetricsTests
{
    private const string GaugeName = "tamma.kek_rotation.remaining";

    private string _dbName = null!;
    private ServiceProvider _sp = null!;
    private IDbContextFactory<ControlPlaneDbContext> _factory = null!;
    private RecordingTenantConnectionResolver _resolver = null!;
    private RecordingPlatformEventRepository _eventRepo = null!;
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
        _dbName = $"kek-rotation-metrics-test-{Guid.NewGuid():N}";
        _initialPrimary = BuildKek(seed: 1);
        _resolver = new RecordingTenantConnectionResolver();
        _eventRepo = new RecordingPlatformEventRepository();

        var services = new ServiceCollection();
        services.AddDbContextFactory<ControlPlaneDbContext>(
            options => options.UseInMemoryDatabase(_dbName));
        services.AddSingleton<IPlatformEventRepository>(_eventRepo);
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
        entry.Property("KekVersion").CurrentValue = (short)kekVersion;
        await ctx.SaveChangesAsync();
        return tenantId;
    }

    /// <summary>
    /// Flush the observable gauge once and return the single observed
    /// value (null when the instrument never fired).
    /// </summary>
    private static long? ObserveRemainingGauge()
    {
        long? observed = null;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == KekRotationMetrics.MeterName
                    && instrument.Name == GaugeName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => observed = value);
        listener.Start();
        listener.RecordObservableInstruments();
        return observed;
    }

    [Test]
    public void Otel_Surface_Exposes_Remaining_Gauge()
    {
        var provider = BuildProvider();
        var coordinator = BuildCoordinator(provider);
        using var metrics = new KekRotationMetrics(coordinator);

        var names = new HashSet<string>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == KekRotationMetrics.MeterName)
                {
                    names.Add(instrument.Name);
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.Start();
        listener.RecordObservableInstruments();

        names.Should().Contain(GaugeName);
    }

    [Test]
    public void Remaining_Is_Zero_When_No_Rotation_In_Flight()
    {
        var provider = BuildProvider();
        var coordinator = BuildCoordinator(provider);
        using var metrics = new KekRotationMetrics(coordinator);

        // Idle coordinator ⇒ Total/Reencrypted/Failed all 0 ⇒ remaining 0.
        metrics.RemainingTenants.Should().Be(0);
        ObserveRemainingGauge().Should().Be(0L);
    }

    [Test]
    public async Task Gauge_Reads_Coordinator_Remaining_Count()
    {
        const string cs1 = "Host=h1;Database=t1;Username=u;Password=p";
        const string cs2 = "Host=h2;Database=t2;Username=u;Password=p";
        await SeedTenantAsync(cs1, _initialPrimary);
        await SeedTenantAsync(cs2, _initialPrimary);

        var provider = BuildProvider();
        var coordinator = BuildCoordinator(provider);
        using var metrics = new KekRotationMetrics(coordinator);

        coordinator.Start(BuildKek(seed: 99));
        await coordinator.WaitForCompletionAsync();

        var status = coordinator.GetStatus();
        status.Phase.Should().Be(KekRotationPhase.Completed);

        // The gauge must equal the live coordinator arithmetic:
        // remaining = Total - Reencrypted - Failed. A successful rotation
        // re-encrypts every tenant, so remaining settles at 0.
        var expectedRemaining =
            (long)status.TotalTenants - status.ReencryptedTenants - status.FailedTenants;
        expectedRemaining.Should().Be(0);

        metrics.RemainingTenants.Should().Be(expectedRemaining);
        ObserveRemainingGauge().Should().Be(expectedRemaining);
    }

    [Test]
    public async Task Gauge_Accounts_Failed_Tenants_As_Done()
    {
        // Seed one healthy tenant + one whose envelope is encrypted under a
        // DIFFERENT key than the configured primary, so the re-encrypt loop
        // fails to decrypt it. Status ends Failed with Total=2, one
        // re-encrypted, one failed → remaining = 2 - 1 - 1 = 0 (both rows
        // are accounted for, none is still "pending rekey").
        await SeedTenantAsync("Host=ok;Database=t;Username=u;Password=p", _initialPrimary);
        await SeedTenantAsync(
            "Host=bad;Database=t;Username=u;Password=p", BuildKek(seed: 200));

        var provider = BuildProvider();
        var coordinator = BuildCoordinator(provider);
        using var metrics = new KekRotationMetrics(coordinator);

        coordinator.Start(BuildKek(seed: 99));
        await coordinator.WaitForCompletionAsync();

        var status = coordinator.GetStatus();
        status.Phase.Should().Be(KekRotationPhase.Failed);
        status.TotalTenants.Should().Be(2);
        status.FailedTenants.Should().Be(1);

        var expectedRemaining =
            (long)status.TotalTenants - status.ReencryptedTenants - status.FailedTenants;
        expectedRemaining.Should().Be(0);

        metrics.RemainingTenants.Should().Be(expectedRemaining);
        ObserveRemainingGauge().Should().Be(expectedRemaining);
    }
}
