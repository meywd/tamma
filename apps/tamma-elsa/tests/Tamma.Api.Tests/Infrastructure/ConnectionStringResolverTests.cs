using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Tamma.Api.Infrastructure;

namespace Tamma.Api.Tests.Infrastructure;

[TestFixture]
public class ConnectionStringResolverTests
{
    private static IConfiguration Build(params (string key, string? value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.key, p.value)))
            .Build();

    [Test]
    public void ResolveAdmin_PrefersTammaDb_WhenSet()
    {
        var cfg = Build(
            ("ConnectionStrings:TammaDb", "Host=postgres;Database=tamma"),
            ("ConnectionStrings:DefaultConnection", "Host=fallback;Database=ignored"));

        ConnectionStringResolver.ResolveAdmin(cfg).Should().Be("Host=postgres;Database=tamma");
    }

    [Test]
    public void ResolveAdmin_FallsBackToDefaultConnection_WhenTammaDbMissing()
    {
        var cfg = Build(("ConnectionStrings:DefaultConnection", "Host=postgres;Database=tamma"));
        ConnectionStringResolver.ResolveAdmin(cfg).Should().Be("Host=postgres;Database=tamma");
    }

    // Reproduces the prod regression that broke the "Deploy to VPS" check on
    // commit 3e97563: appsettings.json shipped a non-null but locally-invalid
    // TammaDb default ("Server=localhost;..."), while the deployed compose at
    // /docker/docker-compose.yml only sets ConnectionStrings__DefaultConnection.
    // Under the original `??` chain, TammaDb won non-null → container connected
    // to localhost:5432 → "Connection refused". The resolver must treat empty
    // and whitespace TammaDb as "not configured" and fall through.
    [Test]
    public void ResolveAdmin_FallsBackToDefaultConnection_WhenTammaDbIsEmpty()
    {
        var cfg = Build(
            ("ConnectionStrings:TammaDb", ""),
            ("ConnectionStrings:DefaultConnection", "Host=postgres;Database=tamma"));

        ConnectionStringResolver.ResolveAdmin(cfg).Should().Be("Host=postgres;Database=tamma");
    }

    [Test]
    public void ResolveAdmin_FallsBackToDefaultConnection_WhenTammaDbIsWhitespace()
    {
        var cfg = Build(
            ("ConnectionStrings:TammaDb", "   "),
            ("ConnectionStrings:DefaultConnection", "Host=postgres;Database=tamma"));

        ConnectionStringResolver.ResolveAdmin(cfg).Should().Be("Host=postgres;Database=tamma");
    }

    [Test]
    public void ResolveAdmin_Throws_WhenNeitherConfigured()
    {
        var cfg = Build();
        var act = () => ConnectionStringResolver.ResolveAdmin(cfg);
        act.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void ResolveAdmin_Throws_WhenBothAreEmpty()
    {
        var cfg = Build(
            ("ConnectionStrings:TammaDb", ""),
            ("ConnectionStrings:DefaultConnection", ""));

        var act = () => ConnectionStringResolver.ResolveAdmin(cfg);
        act.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void ResolveApp_ReturnsNull_WhenEmpty()
    {
        var cfg = Build(("ConnectionStrings:TammaAppDb", ""));
        ConnectionStringResolver.ResolveApp(cfg).Should().BeNull();
    }

    [Test]
    public void ResolveApp_ReturnsValue_WhenSet()
    {
        var cfg = Build(("ConnectionStrings:TammaAppDb", "Host=postgres;User Id=tamma_app"));
        ConnectionStringResolver.ResolveApp(cfg).Should().Be("Host=postgres;User Id=tamma_app");
    }

    // ── ResolveSecretStore (Epic 29 review fix) ──────────────────────────

    [Test]
    public void ResolveSecretStore_PrefersDedicatedSecretStore_WhenSet()
    {
        var cfg = Build(
            ("ConnectionStrings:SecretStore", "Host=secrets;Database=tamma_secrets"),
            ("ConnectionStrings:ControlPlane", "Host=cp;Database=tamma_control"),
            ("ConnectionStrings:TammaDb", "Host=admin;Database=tamma"));

        ConnectionStringResolver.ResolveSecretStore(cfg)
            .Should().Be("Host=secrets;Database=tamma_secrets");
    }

    [Test]
    public void ResolveSecretStore_FallsBackToControlPlane_WhenSecretStoreMissing()
    {
        var cfg = Build(
            ("ConnectionStrings:ControlPlane", "Host=cp;Database=tamma_control"),
            ("ConnectionStrings:TammaDb", "Host=admin;Database=tamma"));

        ConnectionStringResolver.ResolveSecretStore(cfg)
            .Should().Be("Host=cp;Database=tamma_control");
    }

    // The VPS shape: both SecretStore and ControlPlane ship as EMPTY STRING;
    // the CP DbContext only works via the admin-connection fallback. The raw
    // GetConnectionString guard this replaces resolved to "" (not null) →
    // IsNullOrWhiteSpace("") == true → the whole backend guard was skipped and
    // Production silently used volatile in-memory. ResolveSecretStore must
    // coerce the empty strings to null and fall through to the admin
    // connection so the secret store sees a REAL connection.
    [Test]
    public void ResolveSecretStore_EmptySecretStoreAndControlPlane_FallsBackToAdmin()
    {
        var cfg = Build(
            ("ConnectionStrings:SecretStore", ""),
            ("ConnectionStrings:ControlPlane", ""),
            ("ConnectionStrings:TammaDb", "Host=admin;Database=tamma"));

        ConnectionStringResolver.ResolveSecretStore(cfg)
            .Should().Be("Host=admin;Database=tamma",
                "the secret store must ride the SAME admin connection the CP DbContext falls back to");
    }

    [Test]
    public void ResolveSecretStore_FallsBackToLegacyDefaultConnection()
    {
        var cfg = Build(
            ("ConnectionStrings:DefaultConnection", "Host=legacy;Database=tamma"));

        ConnectionStringResolver.ResolveSecretStore(cfg)
            .Should().Be("Host=legacy;Database=tamma");
    }

    [Test]
    public void ResolveSecretStore_ReturnsNull_WhenNothingResolves()
    {
        // Non-throwing (unlike ResolveAdmin) so the caller can fail closed.
        ConnectionStringResolver.ResolveSecretStore(Build()).Should().BeNull();
    }
}
