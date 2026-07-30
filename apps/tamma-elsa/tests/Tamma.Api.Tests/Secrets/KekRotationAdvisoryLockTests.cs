using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;
using Tamma.Api.Services.PlatformEvents;
using Tamma.Api.Services.Secrets;
using Tamma.Api.Tests.TestDoubles;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Repositories;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Secrets;

/// <summary>
/// R2-H14 — verifies <see cref="KekRotationCoordinator.RunRotationAsync"/>
/// declines a second concurrent invocation when another holder is on
/// the cluster-wide advisory lock. Uses a real Postgres container
/// because EF InMemory does not support
/// <c>pg_try_advisory_lock</c>.
/// </summary>
[TestFixture]
public class KekRotationAdvisoryLockTests
{
    private PostgreSqlContainer _postgres = null!;
    private string _connectionString = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("kek_advisory_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();
        _connectionString = _postgres.GetConnectionString();

        // We don't need to run any migrations — the advisory lock test
        // only exercises pg_try_advisory_lock against the bare cluster.
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    [Test]
    public async Task Two_Concurrent_Holders_Cannot_Both_Acquire_Lock()
    {
        // Open two independent Npgsql connections and try to acquire
        // the rotation advisory lock from both. Only one wins.
        await using var connA = new NpgsqlConnection(_connectionString);
        await using var connB = new NpgsqlConnection(_connectionString);
        await connA.OpenAsync();
        await connB.OpenAsync();

        await using var cmdA = connA.CreateCommand();
        cmdA.CommandText = $"SELECT pg_try_advisory_lock({KekRotationCoordinator.AdvisoryLockKey})";
        var aGotIt = (bool)(await cmdA.ExecuteScalarAsync())!;

        await using var cmdB = connB.CreateCommand();
        cmdB.CommandText = $"SELECT pg_try_advisory_lock({KekRotationCoordinator.AdvisoryLockKey})";
        var bGotIt = (bool)(await cmdB.ExecuteScalarAsync())!;

        aGotIt.Should().BeTrue("first caller wins the lock");
        bGotIt.Should().BeFalse(
            "second caller must NOT acquire the lock while connection A holds it");

        // Release from A; B should now be able to acquire.
        await using var unlockA = connA.CreateCommand();
        unlockA.CommandText = $"SELECT pg_advisory_unlock({KekRotationCoordinator.AdvisoryLockKey})";
        await unlockA.ExecuteScalarAsync();

        await using var cmdB2 = connB.CreateCommand();
        cmdB2.CommandText = $"SELECT pg_try_advisory_lock({KekRotationCoordinator.AdvisoryLockKey})";
        var bGotItNow = (bool)(await cmdB2.ExecuteScalarAsync())!;
        bGotItNow.Should().BeTrue("after A releases, B can acquire");
    }

    [Test]
    public async Task RunRotationAsync_With_External_Lock_Held_Marks_Rotation_Failed()
    {
        // Hold the advisory lock from outside the coordinator, then
        // run the coordinator. RunRotationAsync's
        // pg_try_advisory_lock returns false → coordinator updates
        // status to Failed with the documented reason and exits
        // without staging anything.
        await using var holderConn = new NpgsqlConnection(_connectionString);
        await holderConn.OpenAsync();
        await using var holdCmd = holderConn.CreateCommand();
        holdCmd.CommandText = $"SELECT pg_try_advisory_lock({KekRotationCoordinator.AdvisoryLockKey})";
        var held = (bool)(await holdCmd.ExecuteScalarAsync())!;
        held.Should().BeTrue("test setup must successfully hold the lock");

        // Build a coordinator with an Npgsql-backed CP factory so it
        // hits the real pg_try_advisory_lock path.
        //
        // PF-C3 (R2 post-fix): the coordinator now resolves an
        // NpgsqlDataSource for the dedicated lock connection rather
        // than borrowing EF's pooled context. Register a
        // singleton NpgsqlDataSource alongside the DbContext factory
        // so the new path is exercised.
        var services = new ServiceCollection();
        services.AddDbContextFactory<ControlPlaneDbContext>(opts =>
            opts.UseNpgsql(_connectionString));
        services.AddLogging();
        services.AddSingleton<IPlatformEventRepository, NoopPlatformEventRepository>();
        services.AddSingleton(NpgsqlDataSource.Create(_connectionString));
        // 2026-07-30 advisory-lock audit: the coordinator now opens its
        // cluster-wide lock on a dedicated NON-POOLED session, which means it
        // needs a connection string that still carries the password. Neither
        // NpgsqlDataSource.ConnectionString nor (once an NpgsqlDataSource is in
        // DI) EF's GetConnectionString() does — Npgsql strips it. The host
        // always has it in configuration under ConnectionStrings:ControlPlane
        // (that is the very string the data source is built from), so register
        // it here too; a data source with no matching configuration is a
        // container shape production never produces.
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ControlPlane"] = _connectionString,
            }).Build());
        var sp = services.BuildServiceProvider();

        var primaryKey = new byte[32];
        for (var i = 0; i < 32; i++) primaryKey[i] = (byte)(i + 1);
        var dict = new Dictionary<string, string?>
        {
            [KekProvider.PrimaryConfigKey] = Convert.ToBase64String(primaryKey),
            [KekProvider.ActiveVersionConfigKey] = "1",
        };
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        var provider = new KekProvider(cfg, NullLogger<KekProvider>.Instance);

        var resolver = new NoopTenantConnectionResolver();
        var coordinator = new KekRotationCoordinator(
            sp.GetRequiredService<IServiceScopeFactory>(),
            provider,
            resolver,
            NullLogger<KekRotationCoordinator>.Instance);

        var newKek = new byte[32];
        for (var i = 0; i < 32; i++) newKek[i] = (byte)(i + 50);

        coordinator.Start(newKek);
        await coordinator.WaitForCompletionAsync();

        var status = coordinator.GetStatus();
        status.Phase.Should().Be(KekRotationPhase.Failed);
        status.FailureReason.Should().Contain("another rotation is already in progress");

        // Release the test-side lock.
        await using var unlockCmd = holderConn.CreateCommand();
        unlockCmd.CommandText = $"SELECT pg_advisory_unlock({KekRotationCoordinator.AdvisoryLockKey})";
        await unlockCmd.ExecuteScalarAsync();

        await sp.DisposeAsync();
    }

}
