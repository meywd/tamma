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
}
