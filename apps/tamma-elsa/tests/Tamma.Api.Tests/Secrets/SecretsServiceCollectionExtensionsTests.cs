using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Extensions;
using Tamma.Api.Services.Secrets;

namespace Tamma.Api.Tests.Secrets;

/// <summary>
/// Tests for <see cref="SecretsServiceCollectionExtensions"/>. Pins
/// the Story 29-1 wiring contract: AddTammaSecrets() yields a
/// resolvable <see cref="ISecretAccessAuditor"/> +
/// <see cref="ISecretStoreBackend"/>, both backed by the placeholder
/// implementations until Story 29-2 swaps them out.
/// </summary>
[TestFixture]
public class SecretsServiceCollectionExtensionsTests
{
    [Test]
    public void AddTammaSecrets_RegistersNullAuditor()
    {
        var services = new ServiceCollection();
        services.AddTammaSecrets();
        var sp = services.BuildServiceProvider();

        var auditor = sp.GetRequiredService<ISecretAccessAuditor>();
        auditor.Should().BeOfType<NullSecretAccessAuditor>();
    }

    [Test]
    public void AddTammaSecrets_RegistersInMemoryBackend()
    {
        var services = new ServiceCollection();
        services.AddTammaSecrets();
        var sp = services.BuildServiceProvider();

        var backend = sp.GetRequiredService<ISecretStoreBackend>();
        backend.Should().BeOfType<InMemorySecretStoreBackend>();
    }

    [Test]
    public void AddTammaSecrets_IsIdempotent()
    {
        // TryAdd* contract: calling twice does not duplicate
        // registrations or override the first wiring.
        var services = new ServiceCollection();
        services.AddTammaSecrets();
        services.AddTammaSecrets();
        var sp = services.BuildServiceProvider();

        sp.GetServices<ISecretAccessAuditor>().Should().HaveCount(1);
        sp.GetServices<ISecretStoreBackend>().Should().HaveCount(1);
    }

    [Test]
    public void AddTammaSecrets_AllowsExternalOverride()
    {
        // Story 29-2 will register the real Postgres backend BEFORE
        // calling AddTammaSecrets so the TryAdd* sees an existing
        // registration and bows out — pin that behaviour here.
        var services = new ServiceCollection();
        services.AddSingleton<ISecretStoreBackend, ExternalBackend>();
        services.AddTammaSecrets();
        var sp = services.BuildServiceProvider();

        sp.GetRequiredService<ISecretStoreBackend>()
            .Should().BeOfType<ExternalBackend>();
    }

    [Test]
    public void AddTammaSecrets_ThrowsOnNullServices()
    {
        Action act = () => SecretsServiceCollectionExtensions.AddTammaSecrets(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    private sealed class ExternalBackend : ISecretStoreBackend
    {
        public Task PutVersionAsync(Guid secretId, int versionNumber,
            string plaintext, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<string?> GetVersionPlaintextAsync(Guid secretId,
            int versionNumber, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
        public Task DeleteVersionAsync(Guid secretId, int versionNumber,
            CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
