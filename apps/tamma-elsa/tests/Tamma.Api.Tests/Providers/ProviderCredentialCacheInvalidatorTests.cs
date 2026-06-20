using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Activities.LlmCall.Credentials;
using Tamma.Api.Services.Providers;
using Tamma.Api.Services.Secrets;

namespace Tamma.Api.Tests.Providers;

/// <summary>
/// Story 32-3 (AC9) — the SECRET.ROTATE.ACTIVATED → resolver-cache eviction.
/// </summary>
[TestFixture]
public class ProviderCredentialCacheInvalidatorTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    [Test]
    public void RotateActivated_ForByokTenantRef_InvalidatesMatchingEntry()
    {
        var resolver = new RecordingResolver();
        var invalidator = new ProviderCredentialCacheInvalidator(
            resolver, NullLogger<ProviderCredentialCacheInvalidator>.Instance);

        invalidator.HandleRotateActivated(
            SecretRef.ForTenant(Tenant, "provider/anthropic/api-key"));

        resolver.Invalidated.Should().ContainSingle().Which.Should().Be((Tenant, "anthropic"));
    }

    [Test]
    public void RotateActivated_ForNonProviderTenantRef_NoOp()
    {
        var resolver = new RecordingResolver();
        var invalidator = new ProviderCredentialCacheInvalidator(
            resolver, NullLogger<ProviderCredentialCacheInvalidator>.Instance);

        invalidator.HandleRotateActivated(SecretRef.ForTenant(Tenant, "db/app-role"));

        resolver.Invalidated.Should().BeEmpty();
    }

    [Test]
    public void RotateActivated_ForPlatformRef_NoOp()
    {
        var resolver = new RecordingResolver();
        var invalidator = new ProviderCredentialCacheInvalidator(
            resolver, NullLogger<ProviderCredentialCacheInvalidator>.Instance);

        // Platform rotations are RuntimeSecretResolver's concern, not ours.
        invalidator.HandleRotateActivated(SecretRef.ForPlatform("anthropic/api-key"));

        resolver.Invalidated.Should().BeEmpty();
    }

    [TestCase("provider/anthropic/api-key", "anthropic")]
    [TestCase("provider/openrouter/api-key", "openrouter")]
    [TestCase("db/app-role", null)]
    [TestCase("provider//api-key", null)]
    [TestCase("anthropic/api-key", null)]
    public void TryParse_ExtractsProviderFromByokSlug(string name, string? expected)
    {
        ProviderCabinetNames.TryParse(name).Should().Be(expected);
    }

    private sealed class RecordingResolver : IProviderCredentialResolver
    {
        public List<(Guid?, string)> Invalidated { get; } = new();
        public Task<ProviderCredential> ResolveAsync(Guid? tenantId, string providerName, CancellationToken ct = default) =>
            Task.FromResult(new ProviderCredential("x", CredentialSource.Platform, null, null));
        public void Invalidate(Guid? tenantId, string providerName) => Invalidated.Add((tenantId, providerName));
    }
}
