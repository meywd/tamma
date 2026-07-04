using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Activities.LlmCall.Credentials;
using Tamma.Api.Services.Pricing;
using Tamma.Api.Services.Secrets;
using Tamma.Api.Tests.Security;
using Tamma.Core.Enums;
using Tamma.Data;

namespace Tamma.Api.Tests.Pricing;

/// <summary>
/// Story 34-3 — WRITE side. <see cref="TenantProviderBillingService"/> enable/disable
/// populates the authoritative <c>TenantProviderBilling</c> owner rows the read-side
/// <see cref="TenantProviderBillingResolver"/> consumes. Pins: enable writes the
/// cabinet key + one active byok row (canonical key) + PRICING.BYOK.ENABLED + 32-3
/// cache invalidation; disable retires the secret + flips to platform +
/// PRICING.BYOK.DISABLED + invalidation; one-active-row on re-enable; and the real
/// resolver reads byok after enable / platform after disable. InMemory CP — the SQL
/// invariants (CHECK + partial unique index) are pinned by the Postgres model tests.
/// </summary>
[TestFixture]
public class TenantProviderBillingServiceTests
{
    private const string Key = "sk-fake-byok-key-value";

    private static ControlPlaneDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static TenantProviderBillingResolver NewResolver(ControlPlaneDbContext db) =>
        new(db, NullLogger<TenantProviderBillingResolver>.Instance);

    private static (TenantProviderBillingService Service, FakeByokCabinet Cabinet,
        RecordingGateEventRepository Events, SpyCredentialResolver Resolver)
        NewService(ControlPlaneDbContext db)
    {
        var cabinet = new FakeByokCabinet();
        var events = new RecordingGateEventRepository();
        var resolver = new SpyCredentialResolver();
        var service = new TenantProviderBillingService(
            db, cabinet, events, resolver, TimeProvider.System,
            NullLogger<TenantProviderBillingService>.Instance);
        return (service, cabinet, events, resolver);
    }

    [Test]
    public async Task Enable_WritesCabinetKey_FlipsRowToByok_EmitsEnabled_Invalidates()
    {
        var tenant = Guid.NewGuid();
        await using var db = NewContext();
        var (service, cabinet, events, resolver) = NewService(db);

        var result = await service.EnableByokAsync(tenant, "anthropic", Key, actorUserId: Guid.NewGuid());

        result.Mode.Should().Be("byok");
        result.KeySet.Should().BeTrue();
        result.Provider.Should().Be("anthropic");

        // Cabinet key written under the canonical slug 32-3 reads.
        cabinet.Writes.Should().ContainSingle();
        cabinet.Writes[0].Provider.Should().Be("anthropic");
        cabinet.Writes[0].ApiKey.Should().Be(Key);

        // One active byok owner row under the canonical key + the exact cabinet name.
        var row = await db.TenantProviderBillings.SingleAsync();
        row.TenantId.Should().Be(tenant);
        row.ProviderKey.Should().Be("anthropic");
        row.Mode.Should().Be("byok");
        row.Status.Should().Be("active");
        row.SecretName.Should().Be("provider/anthropic/api-key");

        // PRICING.BYOK.ENABLED emitted (tags carry tenantId/provider/mode).
        events.Appended.Should().ContainSingle();
        events.Appended[0].Type.Should().Be(PricingEventTypes.ByokEnabled);
        events.Appended[0].TenantId.Should().Be(tenant);
        events.Appended[0].Tags.Should().Contain("anthropic").And.Contain("byok");
        // The key is NEVER serialized into the event.
        events.Appended[0].Data.Should().NotContain(Key);
        events.Appended[0].Tags.Should().NotContain(Key);

        // 32-3 credential cache invalidated for (tenant, canonical).
        resolver.Invalidated.Should().ContainSingle().Which.Should().Be((tenant, "anthropic"));
    }

    [Test]
    public async Task Enable_CanonicalizesVendorHandle_StoresUnderFamilyKey()
    {
        var tenant = Guid.NewGuid();
        await using var db = NewContext();
        var (service, cabinet, _, resolver) = NewService(db);

        // The caller passes the vendor handle; storage MUST be the canonical family key.
        var result = await service.EnableByokAsync(tenant, "anthropic-claude", Key, actorUserId: null);

        result.Provider.Should().Be("anthropic");
        (await db.TenantProviderBillings.SingleAsync()).ProviderKey.Should().Be("anthropic");
        cabinet.Writes[0].Provider.Should().Be("anthropic");
        resolver.Invalidated[0].Should().Be((tenant, "anthropic"));

        // The real resolver (reads canonical) now returns byok for the vendor handle.
        (await NewResolver(db).ResolveModeAsync(tenant, "anthropic-claude"))
            .Should().Be(MetricBillingMode.Byok);
    }

    [Test]
    public async Task Enable_Idempotent_ReEnable_NoDuplicateActiveRow()
    {
        var tenant = Guid.NewGuid();
        await using var db = NewContext();
        var (service, cabinet, _, _) = NewService(db);

        await service.EnableByokAsync(tenant, "anthropic", Key, actorUserId: null);
        await service.EnableByokAsync(tenant, "anthropic", "sk-fake-rotated-value", actorUserId: null);

        // Exactly ONE active row (the second enable updated, not duplicated).
        (await db.TenantProviderBillings.CountAsync(r => r.Status == "active")).Should().Be(1);
        // The cabinet was written both times (the key rotates on re-enable).
        cabinet.Writes.Should().HaveCount(2);
    }

    [Test]
    public async Task Disable_RetiresSecret_FlipsToPlatform_EmitsDisabled_Invalidates()
    {
        var tenant = Guid.NewGuid();
        await using var db = NewContext();
        var (service, cabinet, events, resolver) = NewService(db);

        await service.EnableByokAsync(tenant, "anthropic", Key, actorUserId: null);
        resolver.Invalidated.Clear();
        events.Appended.Clear();

        var result = await service.DisableByokAsync(tenant, "anthropic", actorUserId: null);

        result.Mode.Should().Be("platform");
        result.KeySet.Should().BeFalse();

        // Row kept for audit but flipped to platform + secret ref tombstoned (XOR).
        var row = await db.TenantProviderBillings.SingleAsync();
        row.Mode.Should().Be("platform");
        row.SecretName.Should().BeNull();
        row.Status.Should().Be("active");

        // Cabinet secret retired.
        cabinet.Removes.Should().Contain("anthropic");
        // PRICING.BYOK.DISABLED emitted + cache invalidated.
        events.Appended.Should().ContainSingle().Which.Type.Should().Be(PricingEventTypes.ByokDisabled);
        resolver.Invalidated.Should().ContainSingle().Which.Should().Be((tenant, "anthropic"));

        // The real resolver now reads platform.
        (await NewResolver(db).ResolveModeAsync(tenant, "anthropic"))
            .Should().Be(MetricBillingMode.PlatformProvided);
    }

    [Test]
    public async Task Disable_NoPriorRow_Idempotent_ReturnsPlatform()
    {
        var tenant = Guid.NewGuid();
        await using var db = NewContext();
        var (service, cabinet, events, _) = NewService(db);

        var result = await service.DisableByokAsync(tenant, "anthropic", actorUserId: null);

        result.Mode.Should().Be("platform");
        (await db.TenantProviderBillings.CountAsync()).Should().Be(0, "a disable with no row creates nothing");
        cabinet.Removes.Should().Contain("anthropic", "the cabinet retire is best-effort / idempotent");
        events.Appended.Should().ContainSingle().Which.Type.Should().Be(PricingEventTypes.ByokDisabled);
    }

    [Test]
    public async Task GetMode_NoRow_Platform_KeySetFalse()
    {
        var tenant = Guid.NewGuid();
        await using var db = NewContext();
        var (service, _, _, _) = NewService(db);

        var result = await service.GetModeAsync(tenant, "anthropic");
        result.Mode.Should().Be("platform");
        result.KeySet.Should().BeFalse();
    }

    [Test]
    public async Task GetMode_ActiveByok_KeySetTrue()
    {
        var tenant = Guid.NewGuid();
        await using var db = NewContext();
        var (service, _, _, _) = NewService(db);
        await service.EnableByokAsync(tenant, "anthropic", Key, actorUserId: null);

        var result = await service.GetModeAsync(tenant, "anthropic-claude"); // canonicalizes
        result.Mode.Should().Be("byok");
        result.KeySet.Should().BeTrue();
    }

    [Test]
    public void Enable_BlankProvider_Throws_NoPartialWrite()
    {
        var tenant = Guid.NewGuid();
        using var db = NewContext();
        var (service, cabinet, _, _) = NewService(db);

        var act = async () => await service.EnableByokAsync(tenant, "   ", Key, actorUserId: null);
        act.Should().ThrowAsync<ArgumentException>();
        cabinet.Writes.Should().BeEmpty("a blank provider must not reach the cabinet");
    }

    // ── fakes ─────────────────────────────────────────────────────────────

    private sealed class FakeByokCabinet : IProviderByokSecretCabinet
    {
        public List<(Guid Tenant, string Provider, string ApiKey)> Writes { get; } = new();
        public List<string> Removes { get; } = new();

        public Task<SecretMetadata> WriteAsync(
            Guid tenantId, string providerCanonical, string apiKey, Guid ownerUserId, CancellationToken ct = default)
        {
            Writes.Add((tenantId, providerCanonical, apiKey));
            return Task.FromResult(new SecretMetadata(
                Guid.NewGuid(), $"provider/{providerCanonical}/api-key", SecretScope.Tenant, tenantId,
                SecretPurpose.ApiKey, Array.Empty<ConsumerRef>(), ownerUserId, RotationSchedule.None,
                LastRotatedAt: null, NextRotationDueAt: null, ActiveVersionNumber: 1,
                CreatedAt: DateTimeOffset.UtcNow, UpdatedAt: DateTimeOffset.UtcNow));
        }

        public Task<bool> RemoveAsync(Guid tenantId, string providerCanonical, CancellationToken ct = default)
        {
            Removes.Add(providerCanonical);
            return Task.FromResult(true);
        }
    }

    private sealed class SpyCredentialResolver : IProviderCredentialResolver
    {
        public List<(Guid?, string)> Invalidated { get; } = new();
        public Task<ProviderCredential> ResolveAsync(Guid? tenantId, string providerName, CancellationToken ct = default) =>
            throw new NotSupportedException("34-3 never resolves keys.");
        public void Invalidate(Guid? tenantId, string providerName) => Invalidated.Add((tenantId, providerName));
    }
}
