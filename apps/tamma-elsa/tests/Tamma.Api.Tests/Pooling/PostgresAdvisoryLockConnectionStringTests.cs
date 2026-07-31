using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NUnit.Framework;
using Tamma.Api.Infrastructure;
using Tamma.Api.Tests.TestDoubles;
using Tamma.Data;
using Tamma.Data.Pooling;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Pooling;

/// <summary>
/// 2026-07-30 advisory-lock audit, second trap — where a dedicated lock
/// session's connection string comes from.
///
/// <para>Moving a session-scoped advisory lock off a pooled connection means
/// RE-OPENING a connection from a connection string, and the two obvious
/// sources silently drop the password (Npgsql defaults
/// <c>PersistSecurityInfo</c> to false):</para>
/// <list type="bullet">
///   <item><description><see cref="NpgsqlDataSource.ConnectionString"/>
///     <b>never</b> carries it.</description></item>
///   <item><description>EF's <c>Database.GetConnectionString()</c> carries it
///     in most shapes, but NOT when an <see cref="NpgsqlDataSource"/> is
///     registered in DI — the Npgsql EF provider then mints the context's
///     <c>DbConnection</c> from that data source and inherits its laundered
///     string. <b>That is the production shape</b>: <c>Program.cs</c>
///     registers a singleton CP data source for the tenant-status invalidation
///     bus alongside the EF factory. Worse, which of the two behaviours you
///     get is a PROCESS-wide, first-one-wins property of EF Core's internal
///     service-provider cache — see
///     <see cref="EfContextWhoseStringHasNoPassword"/>.</description></item>
/// </list>
///
/// <para>The consequence is severe and silent. A stripped string throws on
/// open; every advisory-lock caller correctly treats a failed acquisition as
/// "did not acquire"; so the gate reports "someone else is already running"
/// forever. That is how the first cut of the KEK fix disabled key rotation
/// entirely, and only the FULL unfiltered suite caught it. Three further sites
/// (audit checkpoints, tenant moves, the fleet-migration sweep) were left on
/// the EF route in that pass; these tests pin the single resolution all four
/// now share.</para>
/// </summary>
[TestFixture]
public class PostgresAdvisoryLockConnectionStringTests
{
    private PostgreSqlContainer _postgres = null!;
    private string _cs = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("advisory_lock_cs")
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

    private static IConfiguration Config(params (string Key, string? Value)[] entries)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(entries.ToDictionary(e => e.Key, e => e.Value))
            .Build();

    // ───────────── the mechanism, pinned ─────────────

    [Test]
    public void A_data_source_connection_string_never_carries_the_password()
    {
        using var dataSource = NpgsqlDataSource.Create(_cs);

        new NpgsqlConnectionStringBuilder(_cs).Password.Should().NotBeNullOrEmpty(
            "the fixture's own string has one — otherwise this test proves nothing");
        PostgresAdvisoryLock.HasCredentials(dataSource.ConnectionString).Should().BeFalse(
            "NpgsqlDataSource.ConnectionString is laundered. Anything that re-opens a "
            + "session from it cannot authenticate — and the caller reads that failure as "
            + "'the gate is held by someone else'");
    }

    /// <summary>
    /// <b>Why no test here asserts that EF launders the string.</b> Whether
    /// Npgsql's EF provider picks up a DI-registered <see cref="NpgsqlDataSource"/>
    /// — and therefore whether EF's connection string comes back stripped — is
    /// decided by EF Core's <b>process-wide</b> internal service-provider
    /// cache, which is populated by whichever context of that options shape is
    /// built FIRST in the process. Two probes proved it: build a
    /// data-source-less container first and every later context in that
    /// process keeps its password; build a data-source-bearing one first and
    /// every later context loses it. It is not a per-container property, it is
    /// a per-PROCESS one — which is exactly why the 2026-07-30 audit saw it in
    /// one container and could not reproduce it in another, and why an
    /// assertion on it would be a coin flip that depends on test ordering.
    ///
    /// <para>So these tests simulate the hazard deterministically instead: a
    /// context bound to an explicitly password-less connection string is
    /// indistinguishable, at the seam, from one Npgsql laundered.</para>
    /// </summary>
    private ControlPlaneDbContext EfContextWhoseStringHasNoPassword()
        => new(new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseNpgsql(StripPassword(_cs)).Options);

    // ───────────── the resolution ─────────────

    [Test]
    public async Task Resolution_yields_a_usable_connection_when_EFs_string_has_lost_its_password()
    {
        // The property that actually matters: when EF's view is stripped, the
        // resolved string still OPENS and still takes a lock IN THE RIGHT
        // DATABASE. Asserting "returns something" would not catch a string
        // that is merely un-authenticable, which is the whole defect.
        await using var sp = BuildDataSourceInDiContainer();
        await using var cp = EfContextWhoseStringHasNoPassword();

        var resolved = PostgresAdvisoryLock.ResolveSessionConnectionString(
            sp.GetRequiredService<IConfiguration>(), cp, site: "test");

        const long key = 0x7A11_C500L;
        await using var lease = await PostgresAdvisoryLock.TryAcquireAsync(
            resolved, PostgresAdvisoryLockKey.FromInt64(key));
        lease.Should().NotBeNull(
            "the resolved string must open a real, authenticated session — a laundered one "
            + "throws NpgsqlException here, which every caller reads as 'gate held elsewhere'");
        (await AdvisoryLockProbe.IsHeldAsync(_cs, key)).Should().BeTrue(
            "…and it must be the SAME database the control plane lives on, not merely some "
            + "database that accepted the credentials");
    }

    [Test]
    public async Task Configuration_wins_over_a_laundered_EF_string()
    {
        await using var sp = BuildDataSourceInDiContainer();
        await using var cp = EfContextWhoseStringHasNoPassword();

        var resolved = PostgresAdvisoryLock.TryResolveSessionConnectionString(
            sp.GetRequiredService<IConfiguration>(), cp);

        PostgresAdvisoryLock.HasCredentials(resolved).Should().BeTrue();
        resolved.Should().Be(_cs, "configuration is raw — Npgsql never touches it");
    }

    [Test]
    public async Task EF_is_used_when_there_is_no_configuration_at_all()
    {
        // Unit fixtures bind a context straight to a container and register no
        // IConfiguration. Falling closed there would break every such suite
        // for a hazard that cannot arise (no data source, nothing to launder).
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseNpgsql(_cs).Options;
        await using var cp = new ControlPlaneDbContext(options);

        PostgresAdvisoryLock.TryResolveSessionConnectionString(configuration: null, cp)
            .Should().Be(_cs);
    }

    [Test]
    public void A_password_less_string_that_only_EF_produced_is_refused_and_the_refusal_names_the_trap()
    {
        // The genuinely ambiguous case: no configuration, and EF's string has
        // no password. Indistinguishable from a laundered one, so it is
        // refused — the caller fails CLOSED, loudly, rather than opening an
        // unauthenticable connection whose error reads as "gate held".
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseNpgsql(StripPassword(_cs)).Options;
        using var cp = new ControlPlaneDbContext(options);

        PostgresAdvisoryLock.TryResolveSessionConnectionString(configuration: null, cp)
            .Should().BeNull();

        var act = () => PostgresAdvisoryLock.ResolveSessionConnectionString(
            configuration: null, cp, site: "the widget gate");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*the widget gate*")
            .WithMessage("*NpgsqlDataSource*")
            .WithMessage("*ConnectionStrings:ControlPlane*",
                "an operator handed 'connection string missing' looks in the wrong place — "
                + "the message has to name the laundering mechanism and the fix");
    }

    [Test]
    public void A_trust_auth_deployment_keeps_its_lock()
    {
        // Third tier. Configuration is raw, so a missing password THERE means
        // the deployment genuinely has none (trust auth / integrated
        // security), not that one was stripped. Refusing it would cost those
        // deployments the cluster-wide gate outright — a regression the strict
        // credentials-only rule would have shipped.
        var trustAuth = StripPassword(_cs);

        PostgresAdvisoryLock.TryResolveSessionConnectionString(
                Config(("ConnectionStrings:ControlPlane", trustAuth)), efContext: null)
            .Should().Be(trustAuth);
    }

    [Test]
    public void Nothing_configured_and_no_context_resolves_to_nothing()
    {
        PostgresAdvisoryLock.TryResolveSessionConnectionString(
            configuration: null, efContext: null).Should().BeNull();
        PostgresAdvisoryLock.TryResolveSessionConnectionString(
            Config(("ConnectionStrings:ControlPlane", "")), efContext: null).Should().BeNull(
            "an appsettings default of \"\" must not mask a missing override");
    }

    // ───────────── key order, pinned against the host's own resolver ─────────────

    [Test]
    public void The_configuration_key_order_matches_the_hosts_own_resolution()
    {
        // The seam lives in Tamma.Data (the sweep runner is there) while the
        // host's resolver lives in Tamma.Api, so the key order is stated
        // twice. If they drift, the lock opens a session against a DIFFERENT
        // database than the control plane — a gate that excludes nobody. This
        // test is the join.
        const string cp = "Host=cp;Database=cp;Username=u;Password=p";
        const string admin = "Host=admin;Database=admin;Username=u;Password=p";
        const string legacy = "Host=legacy;Database=legacy;Username=u;Password=p";

        var all = Config(
            ("ConnectionStrings:ControlPlane", cp),
            ("ConnectionStrings:TammaDb", admin),
            ("ConnectionStrings:DefaultConnection", legacy));
        PostgresAdvisoryLock.FromConfiguration(all).Should().Be(cp);
        ConnectionStringResolver.ResolveControlPlane(all).Should().Be(cp);

        var noCp = Config(
            ("ConnectionStrings:TammaDb", admin),
            ("ConnectionStrings:DefaultConnection", legacy));
        PostgresAdvisoryLock.FromConfiguration(noCp).Should().Be(admin);
        ConnectionStringResolver.ResolveAdmin(noCp).Should().Be(admin);

        var legacyOnly = Config(("ConnectionStrings:DefaultConnection", legacy));
        PostgresAdvisoryLock.FromConfiguration(legacyOnly).Should().Be(legacy);
        ConnectionStringResolver.ResolveAdmin(legacyOnly).Should().Be(legacy);

        PostgresAdvisoryLock.ControlPlaneConnectionStringKey.Should().Be("ControlPlane");
        PostgresAdvisoryLock.AdminConnectionStringKey.Should().Be("TammaDb");
        PostgresAdvisoryLock.LegacyAdminConnectionStringKey.Should().Be("DefaultConnection");
    }

    // ───────────── helpers ─────────────

    /// <summary>
    /// The production container shape: a singleton <see cref="NpgsqlDataSource"/>
    /// registered next to an EF factory built from the same connection string,
    /// plus that string in configuration where the host puts it.
    /// </summary>
    private ServiceProvider BuildDataSourceInDiContainer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(
            Config(("ConnectionStrings:ControlPlane", _cs)));
        services.AddSingleton(NpgsqlDataSource.Create(_cs));
        services.AddDbContextFactory<ControlPlaneDbContext>(o => o.UseNpgsql(_cs));
        return services.BuildServiceProvider();
    }

    private static string StripPassword(string connectionString)
        => new NpgsqlConnectionStringBuilder(connectionString) { Password = null }
            .ConnectionString;
}
