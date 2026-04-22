using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;
using Tamma.Data.Pooling;

namespace Tamma.Api.Tests.Epic28;

/// <summary>
/// Story 28-5 — offline tests for the admin-connection adapter. Asserts
/// the connection-string assembly preserves admin host/port/SSL while
/// overwriting identity-bearing fields with the new role + DB; the
/// RoleExists / DatabaseExists / Execute methods need a real Postgres so
/// they are deferred to integration tests.
/// </summary>
[TestFixture]
public class NpgsqlTenantAdminConnectionTests
{
    private static IConfiguration BuildConfig(string admin)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:TenantAdmin"] = admin,
            }!)
            .Build();
    }

    [Test]
    public void Ctor_RequiresAtLeastOneConnectionString()
    {
        var cfg = new ConfigurationBuilder().Build();
        var act = () => new NpgsqlTenantAdminConnection(cfg, NullLogger<NpgsqlTenantAdminConnection>.Instance);
        act.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void Ctor_FallsBackToDefaultConnection()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] =
                    "Host=h;Port=5432;Database=tamma;Username=u;Password=p",
            }!)
            .Build();
        var act = () => new NpgsqlTenantAdminConnection(cfg, NullLogger<NpgsqlTenantAdminConnection>.Instance);
        act.Should().NotThrow();
    }

    [Test]
    public void BuildTenantConnectionString_PreservesAdminHostAndPort()
    {
        var cfg = BuildConfig(
            "Host=db.internal;Port=6543;Database=tamma_control;Username=tamma_provisioner;Password=secret;SSL Mode=Require");
        var sut = new NpgsqlTenantAdminConnection(cfg, NullLogger<NpgsqlTenantAdminConnection>.Instance);

        var cs = sut.BuildTenantConnectionString(
            databaseName: "tamma_tenant_aaaa",
            roleName: "tamma_tenant_aaaa",
            password: "tenant-pwd");

        var parsed = new NpgsqlConnectionStringBuilder(cs);
        parsed.Host.Should().Be("db.internal");
        parsed.Port.Should().Be(6543);
        parsed.SslMode.Should().Be(SslMode.Require);
    }

    [Test]
    public void BuildTenantConnectionString_OverwritesUsernamePasswordDatabase()
    {
        var cfg = BuildConfig(
            "Host=h;Port=5432;Database=tamma_control;Username=tamma_provisioner;Password=secret");
        var sut = new NpgsqlTenantAdminConnection(cfg, NullLogger<NpgsqlTenantAdminConnection>.Instance);

        var cs = sut.BuildTenantConnectionString(
            databaseName: "tamma_tenant_bbbb",
            roleName: "tamma_tenant_bbbb",
            password: "fresh-pwd");

        var parsed = new NpgsqlConnectionStringBuilder(cs);
        parsed.Database.Should().Be("tamma_tenant_bbbb");
        parsed.Username.Should().Be("tamma_tenant_bbbb");
        parsed.Password.Should().Be("fresh-pwd");
        parsed.ApplicationName.Should().Be("tamma-tenant;db=tamma_tenant_bbbb");
    }

    [Test]
    public void BuildTenantConnectionString_RejectsBlankInputs()
    {
        var cfg = BuildConfig("Host=h;Port=5432;Database=tamma;Username=u;Password=p");
        var sut = new NpgsqlTenantAdminConnection(cfg, NullLogger<NpgsqlTenantAdminConnection>.Instance);

        var noDb = () => sut.BuildTenantConnectionString("", "r", "p");
        var noRole = () => sut.BuildTenantConnectionString("d", "", "p");
        var noPwd = () => sut.BuildTenantConnectionString("d", "r", "");

        noDb.Should().Throw<ArgumentException>();
        noRole.Should().Throw<ArgumentException>();
        noPwd.Should().Throw<ArgumentException>();
    }
}
