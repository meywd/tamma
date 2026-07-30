using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;
using Tamma.Api.Services.Secrets;
using Tamma.Api.Tests.TestDoubles;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Secrets;

/// <summary>
/// 2026-07-30 advisory-lock audit — the KEK rotation gate is the most
/// serious of the four audited sites.
///
/// <para>PF-C3 already moved the cluster-wide rotation lock OFF the EF
/// pooled <c>DbContext</c> and onto "a dedicated
/// <see cref="NpgsqlConnection"/>". But dedicated is not the same as
/// non-pooled: it was opened via <c>NpgsqlDataSource.OpenConnectionAsync</c>,
/// which hands out a POOLED connector. Disposing it in the rotation's
/// <c>finally</c> returned it to the pool with the backend session — and
/// the rotation lock — still alive, because Npgsql defers the
/// <c>DISCARD ALL</c> that runs <c>pg_advisory_unlock_all()</c> until that
/// connector is next USED. The explicit unlock was best-effort and its
/// failure swallowed, on the (false, for a pooled connection) grounds
/// that "the connection drop is the actual guarantee".</para>
///
/// <para><b>Blast radius, and why this ranks first.</b>
/// <see cref="KekRotationCoordinator.AdvisoryLockKey"/> is a CONSTANT — it
/// never rotates the way the per-hour scheduler keys do. So a single
/// swallowed unlock wedges KEK rotation shut for the whole cluster
/// indefinitely, and every subsequent <c>/api/admin/kek/rotate</c> fails
/// with the operator-visible untruth "another rotation is already in
/// progress on this cluster". That is a security operation an operator may
/// need urgently (suspected key compromise), blocked by a lock nobody
/// holds.</para>
///
/// <para>The observer is <see cref="AdvisoryLockProbe"/>, deliberately NOT
/// pooled: a pooled probe can draw the leaking connector and clear the
/// lock via the deferred reset before reading, i.e. repair the very state
/// it was sent to measure.</para>
/// </summary>
[TestFixture]
public class KekRotationLockSessionTests
{
    private PostgreSqlContainer _postgres = null!;
    private string _cs = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("kek_lock_session")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();
        _cs = _postgres.GetConnectionString();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    /// <summary>
    /// Configuration shaped like the host's: the control-plane connection
    /// string under <c>ConnectionStrings:ControlPlane</c>.
    /// </summary>
    private IConfiguration ControlPlaneConfig()
        => new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:ControlPlane"] = _cs,
            }).Build();

    private static byte[] BuildKek(byte seed)
    {
        var key = new byte[32];
        for (var i = 0; i < key.Length; i++) key[i] = (byte)(seed + i);
        return key;
    }

    private static KekProvider BuildProvider(byte[] primary)
    {
        var dict = new Dictionary<string, string?>
        {
            [KekProvider.PrimaryConfigKey] = Convert.ToBase64String(primary),
            [KekProvider.ActiveVersionConfigKey] = "1",
        };
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        return new KekProvider(cfg, NullLogger<KekProvider>.Instance);
    }

    [Test]
    public async Task The_rotation_lock_rides_a_session_that_dies_with_the_rotation()
    {
        // The property that makes every exit path safe: when the rotation
        // ends, the backend that held the cluster-wide gate is GONE — not
        // parked idle in a connection pool, where it would still own the
        // lock on any path that skipped or failed the explicit unlock.
        //
        // The pid is captured from INSIDE the rotation, via the platform
        // event repository: SECRETS.KEK.ROTATION.STARTED is emitted after
        // the advisory lock has been taken, so the probe runs while the
        // gate is genuinely held.
        var services = new ServiceCollection();
        services.AddLogging();
        // Production resolves the control-plane connection string from
        // configuration (ConnectionStringResolver.ResolveControlPlane), and so
        // does the rotation lock — it is the only source that reliably still
        // carries the password. Register it exactly as the host does.
        services.AddSingleton<IConfiguration>(ControlPlaneConfig());
        services.AddDbContextFactory<ControlPlaneDbContext>(o => o.UseNpgsql(_cs));
        var probe = new LockProbingEventRepository(_cs);
        services.AddSingleton<IPlatformEventRepository>(probe);
        services.AddSingleton(NpgsqlDataSource.Create(_cs));
        await using var sp = services.BuildServiceProvider();

        await using (var boot = await sp
            .GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>()
            .CreateDbContextAsync())
        {
            await boot.Database.EnsureCreatedAsync();
        }

        var coordinator = new KekRotationCoordinator(
            sp.GetRequiredService<IServiceScopeFactory>(),
            BuildProvider(BuildKek(seed: 1)),
            new NoopTenantConnectionResolver(),
            NullLogger<KekRotationCoordinator>.Instance);

        coordinator.Start(BuildKek(seed: 50));
        await coordinator.WaitForCompletionAsync();

        probe.HolderPid.Should().NotBeNull(
            "the rotation really did hold the cluster-wide gate while it ran — "
            + "otherwise this test proves nothing");

        (await AdvisoryLockProbe.WaitForBackendGoneAsync(
            _cs, probe.HolderPid!.Value, TimeSpan.FromSeconds(10)))
            .Should().BeTrue(
                "the rotation lock's Postgres session must END with the rotation. A "
                + "surviving backend means the lock rode a POOLED connector, and since "
                + "this key is a constant that never rotates, any exit that skipped the "
                + "unlock would wedge cluster-wide KEK rotation shut indefinitely — every "
                + "later /rotate failing with the untrue 'another rotation is already in "
                + "progress on this cluster'");

        (await AdvisoryLockProbe.IsHeldAsync(_cs, KekRotationCoordinator.AdvisoryLockKey))
            .Should().BeFalse("and the gate must be open for the next operator");
    }

    [Test]
    public async Task A_finished_rotation_leaves_the_gate_open_for_the_next_one()
    {
        // The end-to-end consequence: back-to-back rotations must both be
        // admitted. On the pooled implementation this passes only when the
        // pool happens to hand back the leaking connector (whose deferred
        // DISCARD ALL then releases the lock) — i.e. whether the gate is
        // open is a connection-pool draw.
        var services = new ServiceCollection();
        services.AddLogging();
        // Production resolves the control-plane connection string from
        // configuration (ConnectionStringResolver.ResolveControlPlane), and so
        // does the rotation lock — it is the only source that reliably still
        // carries the password. Register it exactly as the host does.
        services.AddSingleton<IConfiguration>(ControlPlaneConfig());
        services.AddDbContextFactory<ControlPlaneDbContext>(o => o.UseNpgsql(_cs));
        services.AddSingleton<IPlatformEventRepository, NoopPlatformEventRepository>();
        services.AddSingleton(NpgsqlDataSource.Create(_cs));
        await using var sp = services.BuildServiceProvider();

        await using (var boot = await sp
            .GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>()
            .CreateDbContextAsync())
        {
            await boot.Database.EnsureCreatedAsync();
        }

        var provider = BuildProvider(BuildKek(seed: 1));
        var coordinator = new KekRotationCoordinator(
            sp.GetRequiredService<IServiceScopeFactory>(),
            provider,
            new NoopTenantConnectionResolver(),
            NullLogger<KekRotationCoordinator>.Instance);

        coordinator.Start(BuildKek(seed: 60));
        await coordinator.WaitForCompletionAsync();

        // Guard against passing VACUOUSLY. If the coordinator could not take
        // the gate at all — e.g. it was handed a connection string Npgsql had
        // stripped the password from — it aborts with exactly the phase and
        // reason a lock-loser gets, and every "the gate is free afterwards"
        // assertion below would hold for the wrong reason.
        var status = coordinator.GetStatus();
        status.Phase.Should().NotBe(KekRotationPhase.Failed,
            "this test is only meaningful if the rotation actually RAN and actually "
            + "held the cluster-wide gate; a rotation that never acquired it leaves the "
            + "gate free trivially. FailureReason: " + status.FailureReason);

        // Immediately after, a fresh session must be able to take the gate.
        await using var next = await Tamma.Data.Pooling.PostgresAdvisoryLock.TryAcquireAsync(
            _cs,
            Tamma.Data.Pooling.PostgresAdvisoryLockKey.FromInt64(
                KekRotationCoordinator.AdvisoryLockKey));
        next.Should().NotBeNull(
            "a finished rotation must leave the cluster-wide gate open, whichever "
            + "connection the next attempt happens to land on");
    }

    [Test]
    public async Task An_externally_held_gate_still_makes_the_rotation_stand_down()
    {
        // The lock's SEMANTICS must be unchanged by the audit: same key,
        // same exclusion, same canonical failure. Only the session it rides
        // on changed.
        var services = new ServiceCollection();
        services.AddLogging();
        // Production resolves the control-plane connection string from
        // configuration (ConnectionStringResolver.ResolveControlPlane), and so
        // does the rotation lock — it is the only source that reliably still
        // carries the password. Register it exactly as the host does.
        services.AddSingleton<IConfiguration>(ControlPlaneConfig());
        services.AddDbContextFactory<ControlPlaneDbContext>(o => o.UseNpgsql(_cs));
        services.AddSingleton<IPlatformEventRepository, NoopPlatformEventRepository>();
        services.AddSingleton(NpgsqlDataSource.Create(_cs));
        await using var sp = services.BuildServiceProvider();

        await using (var boot = await sp
            .GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>()
            .CreateDbContextAsync())
        {
            await boot.Database.EnsureCreatedAsync();
        }

        await using var holder = await Tamma.Data.Pooling.PostgresAdvisoryLock.TryAcquireAsync(
            _cs,
            Tamma.Data.Pooling.PostgresAdvisoryLockKey.FromInt64(
                KekRotationCoordinator.AdvisoryLockKey));
        holder.Should().NotBeNull("test setup holds the cluster-wide rotation gate");

        var coordinator = new KekRotationCoordinator(
            sp.GetRequiredService<IServiceScopeFactory>(),
            BuildProvider(BuildKek(seed: 1)),
            new NoopTenantConnectionResolver(),
            NullLogger<KekRotationCoordinator>.Instance);

        coordinator.Start(BuildKek(seed: 70));
        await coordinator.WaitForCompletionAsync();

        var status = coordinator.GetStatus();
        status.Phase.Should().Be(KekRotationPhase.Failed);
        status.FailureReason.Should().Contain("another rotation is already in progress");

        (await AdvisoryLockProbe.IsHeldAsync(_cs, KekRotationCoordinator.AdvisoryLockKey))
            .Should().BeTrue("the lock-loser must not have released the winner's gate");

        // Guard against passing VACUOUSLY: a coordinator that can NEVER take
        // the gate (a broken connection string, say) produces the identical
        // Failed/"already in progress" shape. Release the external holder and
        // show that the very same wiring now DOES rotate — which means the
        // stand-down above was caused by the lock, not by breakage.
        await holder!.DisposeAsync();

        var second = new KekRotationCoordinator(
            sp.GetRequiredService<IServiceScopeFactory>(),
            BuildProvider(BuildKek(seed: 1)),
            new NoopTenantConnectionResolver(),
            NullLogger<KekRotationCoordinator>.Instance);
        second.Start(BuildKek(seed: 80));
        await second.WaitForCompletionAsync();

        second.GetStatus().Phase.Should().NotBe(KekRotationPhase.Failed,
            "with the gate released the same wiring must rotate — otherwise the "
            + "stand-down above proved nothing about the lock");
    }

    /// <summary>
    /// Records the backend pid holding the rotation gate at the moment the
    /// rotation emits its STARTED event — i.e. from inside the critical
    /// section, through a NON-POOLED observer.
    /// </summary>
    private sealed class LockProbingEventRepository(string connectionString) : IPlatformEventRepository
    {
        public int? HolderPid { get; private set; }

        public async Task<PlatformEvent?> AppendAsync(PlatformEvent evt, CancellationToken ct = default)
        {
            if (evt.Id == Guid.Empty) evt.Id = Guid.NewGuid();
            if (evt.CreatedAt == default) evt.CreatedAt = DateTime.UtcNow;

            HolderPid ??= await AdvisoryLockProbe.HolderPidAsync(
                connectionString, KekRotationCoordinator.AdvisoryLockKey);

            return evt;
        }

        public Task<PlatformEvent?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult<PlatformEvent?>(null);

        public Task<IReadOnlyList<PlatformEvent>> QueryAsync(
            Guid? tenantId = null,
            Guid? userId = null,
            string? typePrefix = null,
            DateTime? since = null,
            bool includePlatformWide = false,
            int limit = 100,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PlatformEvent>>(Array.Empty<PlatformEvent>());
    }
}
