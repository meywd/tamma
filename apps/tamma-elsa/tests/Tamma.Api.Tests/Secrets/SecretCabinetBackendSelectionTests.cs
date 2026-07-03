using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Extensions;
using Tamma.Api.Services.Secrets;
using Tamma.Api.Services.Secrets.Postgres;

namespace Tamma.Api.Tests.Secrets;

/// <summary>
/// Story 29-1 (review fix) — config-matrix for
/// <see cref="SecretsServiceCollectionExtensions.AddTammaSecretCabinet"/>.
/// Pins that Production NEVER silently uses the volatile in-memory backend
/// for real secrets: it either persists (KEK + connection) or fails closed.
///
/// <para>Asserts the RESOLVED <see cref="ISecretStoreBackend"/> descriptor
/// per case (by <c>ImplementationType</c>, so the assertion needs no KEK env
/// var and never instantiates the Postgres backend).</para>
/// </summary>
[TestFixture]
public class SecretCabinetBackendSelectionTests
{
    private const string Conn = "Host=localhost;Database=t;Username=u;Password=p";

    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>()).Build();

    private static Type? ResolvedBackendType(IServiceCollection services) =>
        services.Single(d => d.ServiceType == typeof(ISecretStoreBackend))
            .ImplementationType;

    [Test]
    public void KekPresentWithConnection_WiresPersistentPostgresBackend()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var selected = services.AddTammaSecretCabinet(
            EmptyConfig(), isProduction: true, kekConfigured: true,
            resolvedConnectionString: Conn);

        selected.Should().Be(SecretCabinetBackend.PersistentPostgres);
        ResolvedBackendType(services).Should().Be(typeof(PostgresSecretStoreBackend));
    }

    [Test]
    public void Production_NoKek_ReachesFailClosed_NotInMemory()
    {
        // The VPS shape: KEK unset, but a connection resolves (CP admin
        // fallback). Production MUST fail closed, never volatile in-memory.
        var services = new ServiceCollection();
        services.AddLogging();

        var selected = services.AddTammaSecretCabinet(
            EmptyConfig(), isProduction: true, kekConfigured: false,
            resolvedConnectionString: Conn);

        selected.Should().Be(SecretCabinetBackend.FailClosed);
        ResolvedBackendType(services).Should().Be(typeof(FailClosedSecretStoreBackend));
        ResolvedBackendType(services).Should().NotBe(typeof(InMemorySecretStoreBackend),
            "Production must never silently store real secrets in volatile memory");
    }

    [Test]
    public void Production_NoKek_NoConnection_StillFailsClosed()
    {
        // Even with no resolvable connection, Production never falls to
        // volatile in-memory for real secrets.
        var services = new ServiceCollection();
        services.AddLogging();

        var selected = services.AddTammaSecretCabinet(
            EmptyConfig(), isProduction: true, kekConfigured: false,
            resolvedConnectionString: null);

        selected.Should().Be(SecretCabinetBackend.FailClosed);
        ResolvedBackendType(services).Should().Be(typeof(FailClosedSecretStoreBackend));
    }

    [Test]
    public void Production_KekButNoConnection_FailsClosed()
    {
        // KEK present but nothing resolves → no persistent backend possible →
        // fail closed rather than in-memory in Production.
        var services = new ServiceCollection();
        services.AddLogging();

        var selected = services.AddTammaSecretCabinet(
            EmptyConfig(), isProduction: true, kekConfigured: true,
            resolvedConnectionString: null);

        selected.Should().Be(SecretCabinetBackend.FailClosed);
        ResolvedBackendType(services).Should().Be(typeof(FailClosedSecretStoreBackend));
    }

    [Test]
    public void Development_NoKek_UsesVolatileInMemory()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var selected = services.AddTammaSecretCabinet(
            EmptyConfig(), isProduction: false, kekConfigured: false,
            resolvedConnectionString: Conn);

        selected.Should().Be(SecretCabinetBackend.VolatileInMemory);
        ResolvedBackendType(services).Should().Be(typeof(InMemorySecretStoreBackend));
    }
}
