using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Extensions;
using Tamma.Api.Services.Secrets;
using Tamma.Api.Services.Secrets.Postgres;

namespace Tamma.Api.Tests.Secrets.Postgres;

/// <summary>
/// Tests for the Story 29-2 DI extension
/// <see cref="SecretsServiceCollectionExtensions.AddTammaPostgresSecrets"/>.
/// Pins the swap contract: calling the Postgres extension replaces the
/// Story 29-1 in-memory placeholder with
/// <see cref="PostgresSecretStoreBackend"/>, regardless of whether
/// <see cref="SecretsServiceCollectionExtensions.AddTammaSecrets"/>
/// was called first.
/// </summary>
[TestFixture]
public class PostgresSecretsServiceCollectionExtensionsTests
{
    private const string ValidKekSpec =
        "1:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="; // 32 zero bytes
    private static readonly string SavedPrimaryKek =
        Environment.GetEnvironmentVariable(EnvKekProvider.PrimaryEnvVar) ?? "";
    private static readonly string SavedSecondaryKek =
        Environment.GetEnvironmentVariable(EnvKekProvider.SecondaryEnvVar) ?? "";

    [SetUp]
    public void SetUp()
    {
        Environment.SetEnvironmentVariable(EnvKekProvider.PrimaryEnvVar, ValidKekSpec);
        Environment.SetEnvironmentVariable(EnvKekProvider.SecondaryEnvVar, null);
    }

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable(
            EnvKekProvider.PrimaryEnvVar,
            string.IsNullOrEmpty(SavedPrimaryKek) ? null : SavedPrimaryKek);
        Environment.SetEnvironmentVariable(
            EnvKekProvider.SecondaryEnvVar,
            string.IsNullOrEmpty(SavedSecondaryKek) ? null : SavedSecondaryKek);
    }

    private static IConfiguration Config(string? csKey = null, string? csValue = null)
    {
        var dict = new Dictionary<string, string?>();
        if (csKey is not null) dict[$"ConnectionStrings:{csKey}"] = csValue;
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    [Test]
    public void AddTammaPostgresSecrets_RegistersPostgresBackend()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTammaPostgresSecrets(
            Config(),
            connectionString: "Host=localhost;Database=test;Username=u;Password=p");

        var sp = services.BuildServiceProvider();
        sp.GetRequiredService<ISecretStoreBackend>()
            .Should().BeOfType<PostgresSecretStoreBackend>();
    }

    [Test]
    public void AddTammaPostgresSecrets_RegistersEnvKekProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTammaPostgresSecrets(
            Config(),
            connectionString: "Host=localhost;Database=test;Username=u;Password=p");

        var sp = services.BuildServiceProvider();
        var provider = sp.GetRequiredService<IKekProvider>();
        provider.Should().BeOfType<EnvKekProvider>();
        provider.PrimaryKekId.Should().Be(1);
    }

    [Test]
    public void AddTammaPostgresSecrets_RegistersDbContextFactory()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTammaPostgresSecrets(
            Config(),
            connectionString: "Host=localhost;Database=test;Username=u;Password=p");

        var sp = services.BuildServiceProvider();
        var factory = sp.GetService<IDbContextFactory<SecretsDbContext>>();
        factory.Should().NotBeNull(
            "PostgresSecretStoreBackend depends on the SecretsDbContext factory");
    }

    [Test]
    public void AddTammaPostgresSecrets_OverridesInMemoryBackendRegisteredFirst()
    {
        // Calling AddTammaSecrets() first registers the in-memory
        // backend; the subsequent AddTammaPostgresSecrets() must
        // replace it. Pins the order-doesn't-matter contract.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTammaSecrets();
        services.AddTammaPostgresSecrets(
            Config(),
            connectionString: "Host=localhost;Database=test;Username=u;Password=p");

        var sp = services.BuildServiceProvider();
        sp.GetRequiredService<ISecretStoreBackend>()
            .Should().BeOfType<PostgresSecretStoreBackend>();
    }

    [Test]
    public void AddTammaPostgresSecrets_BackendOverrideIsSingleton()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTammaPostgresSecrets(
            Config(),
            connectionString: "Host=localhost;Database=test;Username=u;Password=p");

        var sp = services.BuildServiceProvider();
        var instances = sp.GetServices<ISecretStoreBackend>().ToList();
        instances.Should().HaveCount(1,
            "the override removes the placeholder so only one descriptor remains");
        instances[0].Should().BeOfType<PostgresSecretStoreBackend>();
    }

    [Test]
    public void AddTammaPostgresSecrets_FallsBackToControlPlaneConnectionString()
    {
        // No explicit connection string + no SecretStore key →
        // falls back to ControlPlane key.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTammaPostgresSecrets(
            Config("ControlPlane",
                "Host=localhost;Database=tamma_control;Username=u;Password=p"));

        var sp = services.BuildServiceProvider();
        sp.GetService<IDbContextFactory<SecretsDbContext>>()
            .Should().NotBeNull();
    }

    [Test]
    public void AddTammaPostgresSecrets_PrefersDedicatedSecretStoreConnectionString()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTammaPostgresSecrets(
            Config("SecretStore",
                "Host=secrets-host;Database=tamma_secrets;Username=u;Password=p"));

        var sp = services.BuildServiceProvider();
        sp.GetService<IDbContextFactory<SecretsDbContext>>()
            .Should().NotBeNull();
    }

    [Test]
    public void AddTammaPostgresSecrets_ThrowsWhenNoConnectionStringAvailable()
    {
        // Empty config + no explicit arg → InvalidOperationException
        // at registration time so a misconfigured host fails fast.
        var services = new ServiceCollection();
        services.AddLogging();

        Action act = () => services.AddTammaPostgresSecrets(Config());
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ConnectionStrings:SecretStore*");
    }

    [Test]
    public void AddTammaPostgresSecrets_ThrowsOnNullServices()
    {
        Action act = () =>
            SecretsServiceCollectionExtensions.AddTammaPostgresSecrets(
                null!, Config(), "x");
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void AddTammaPostgresSecrets_ThrowsOnNullConfiguration()
    {
        Action act = () =>
            new ServiceCollection().AddTammaPostgresSecrets(null!, "x");
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void AddTammaPostgresSecrets_RegistersSecretStoreFacade()
    {
        // Backend-selection contract: with the KEK present (SetUp) the
        // Postgres path also wires the concrete ISecretStore facade so
        // consumers can depend on it. Resolved inside a scope because the
        // facade is scoped (it opens short-lived DbContexts).
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTammaPostgresSecrets(
            Config(),
            connectionString: "Host=localhost;Database=test;Username=u;Password=p");

        var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<ISecretStore>()
            .Should().BeOfType<SecretStore>();
    }

    [Test]
    public void AddTammaPostgresSecrets_StillRegistersAuditor()
    {
        // The Postgres extension must call AddTammaSecrets() under
        // the hood so the Story 29-1 auditor wiring is preserved.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTammaPostgresSecrets(
            Config(),
            connectionString: "Host=localhost;Database=test;Username=u;Password=p");

        var sp = services.BuildServiceProvider();
        sp.GetRequiredService<ISecretAccessAuditor>()
            .Should().BeOfType<NullSecretAccessAuditor>();
    }
}
