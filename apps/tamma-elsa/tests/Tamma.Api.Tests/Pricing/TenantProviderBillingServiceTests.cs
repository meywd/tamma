using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Activities.LlmCall.Credentials;
using Tamma.Activities.Security;
using Tamma.Api.Services.Pricing;
using Tamma.Api.Services.Providers;
using Tamma.Api.Services.PromptStore;
using Tamma.Api.Services.Secrets;
using Tamma.Api.Tests.Security;
using Tamma.Core;
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

    // ── Fix 1 (a) — gemini keys on the RAW identity, NOT the rate-card family. The
    //    cabinet slug + owner row are "gemini" / "provider/gemini/api-key" — byte-
    //    identical to what the REAL 32-3 credential resolver reads for a "gemini" call,
    //    so the call resolves BOTH byok mode AND the tenant's key. Fail-before: the old
    //    family-canonicalized write stored provider/google/api-key, which the "gemini"
    //    read never finds → the tenant's key is silently unused / billed BYOK on the
    //    platform key. ──
    [Test]
    public async Task Enable_Gemini_KeysRawIdentity_ResolverReadsByokAndTenantKey()
    {
        var tenant = Guid.NewGuid();
        await using var db = NewContext();
        var (service, cabinet, _, _) = NewService(db);

        var result = await service.EnableByokAsync(tenant, "gemini", Key, actorUserId: null);

        // Owner row + cabinet slug key on the RAW handle "gemini" (NOT the family "google").
        result.Provider.Should().Be("gemini");
        var row = await db.TenantProviderBillings.SingleAsync();
        row.ProviderKey.Should().Be("gemini");
        row.SecretName.Should().Be("provider/gemini/api-key");
        cabinet.Writes.Should().ContainSingle().Which.Provider.Should().Be("gemini");

        // The billing resolver reads byok for the raw handle; "google" is a DIFFERENT
        // identity that does not pick up the gemini row.
        (await NewResolver(db).ResolveModeAsync(tenant, "gemini")).Should().Be(MetricBillingMode.Byok);
        (await NewResolver(db).ResolveModeAsync(tenant, "google")).Should().Be(MetricBillingMode.PlatformProvided);

        // The REAL 32-3 credential resolver reads the tenant key at the EXACT slug the
        // service wrote (seed the cabinet reader at the owner row's SecretName). Only a
        // matching slug resolves byok; a mismatch would fail-closed (fallback denied).
        var byokReader = new SeededByokReader();
        byokReader.Seed(tenant, row.SecretName!, Key, version: 1);
        var cred = await NewCredentialResolver(byokReader).ResolveAsync(tenant, "gemini");
        cred.Source.Should().Be(CredentialSource.Byok);
        cred.ApiKey.Should().Be(Key);
    }

    // ── Fix 1 (b) — github-copilot keys on its OWN identity and never clobbers openai.
    //    Fail-before: github-copilot canonicalized to "openai", so the write stored the
    //    tenant's Copilot key under provider/openai/api-key and flipped the openai owner
    //    row — overwriting a separately-configured OpenAI key. ──
    [Test]
    public async Task Enable_GithubCopilot_DoesNotClobberOpenAi_TwoDistinctRowsAndKeys()
    {
        var tenant = Guid.NewGuid();
        await using var db = NewContext();
        var (service, cabinet, _, resolver) = NewService(db);

        await service.EnableByokAsync(tenant, "github-copilot", "sk-copilot-key-value", actorUserId: null);

        // The github-copilot enable touched ONLY the github-copilot slug/row — never openai.
        cabinet.Writes.Should().ContainSingle().Which.Provider.Should().Be("github-copilot");
        resolver.Invalidated.Should().ContainSingle().Which.Should().Be((tenant, "github-copilot"));

        await service.EnableByokAsync(tenant, "openai", "sk-openai-key-value", actorUserId: null);

        // TWO distinct owner rows + TWO distinct cabinet keys — no clobber.
        var rows = await db.TenantProviderBillings.Where(r => r.Status == "active").ToListAsync();
        rows.Select(r => r.ProviderKey).Should().BeEquivalentTo(new[] { "github-copilot", "openai" });
        cabinet.Writes.Select(w => w.Provider).Should().BeEquivalentTo(new[] { "github-copilot", "openai" });
        cabinet.Writes.Should().Contain(w => w.Provider == "github-copilot" && w.ApiKey == "sk-copilot-key-value");
        cabinet.Writes.Should().Contain(w => w.Provider == "openai" && w.ApiKey == "sk-openai-key-value");
    }

    // ── Fix 1 (c) — the vendor handle "anthropic-claude" round-trips on the RAW handle:
    //    write "anthropic-claude" → owner row "anthropic-claude" → the billing resolver
    //    reads "anthropic-claude" and matches. It is NOT collapsed to the family
    //    "anthropic" (fail-before: the old write stored "anthropic"). ──
    [Test]
    public async Task Enable_AnthropicClaudeVendorHandle_RoundTripsOnRawHandle()
    {
        var tenant = Guid.NewGuid();
        await using var db = NewContext();
        var (service, cabinet, _, resolver) = NewService(db);

        var result = await service.EnableByokAsync(tenant, "anthropic-claude", Key, actorUserId: null);

        result.Provider.Should().Be("anthropic-claude");
        (await db.TenantProviderBillings.SingleAsync()).ProviderKey.Should().Be("anthropic-claude");
        cabinet.Writes[0].Provider.Should().Be("anthropic-claude");
        resolver.Invalidated[0].Should().Be((tenant, "anthropic-claude"));

        // The billing resolver reads byok for the SAME raw handle; the family "anthropic"
        // is a distinct identity here (no cross-family collapse).
        (await NewResolver(db).ResolveModeAsync(tenant, "anthropic-claude")).Should().Be(MetricBillingMode.Byok);
        (await NewResolver(db).ResolveModeAsync(tenant, "anthropic")).Should().Be(MetricBillingMode.PlatformProvided);
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

    // ── Fix 3 — a disable whose cabinet retire THROWS must still end with row=platform,
    //    still emit PRICING.BYOK.DISABLED (not skipped), and surface a RETRIABLE error —
    //    never a mid-way 500 that leaves the (still-live) key AND skips the event. ──
    [Test]
    public async Task Disable_CabinetRemoveThrows_RowIsPlatform_EventEmitted_RetriableError()
    {
        var tenant = Guid.NewGuid();
        await using var db = NewContext();
        var (service, cabinet, events, resolver) = NewService(db);

        await service.EnableByokAsync(tenant, "anthropic", Key, actorUserId: null);
        events.Appended.Clear();
        resolver.Invalidated.Clear();

        cabinet.ThrowOnRemove = true;

        var act = async () => await service.DisableByokAsync(tenant, "anthropic", actorUserId: null);

        // A RETRIABLE, typed error surfaces (not a raw 500), so the caller re-runs the
        // idempotent disable to actually retire the key.
        var ex = (await act.Should().ThrowAsync<TammaError>()).Which;
        ex.Code.Should().Be("BYOK.DISABLE.CABINET_RETIRE_FAILED");
        ex.Retryable.Should().BeTrue();

        // The owner row is ALREADY platform (billing mode flipped) …
        var row = await db.TenantProviderBillings.SingleAsync();
        row.Mode.Should().Be("platform");
        row.SecretName.Should().BeNull();

        // … the DISABLED event still ran (the mode change is authoritative), and the
        // credential cache was still invalidated.
        events.Appended.Should().ContainSingle().Which.Type.Should().Be(PricingEventTypes.ByokDisabled);
        resolver.Invalidated.Should().ContainSingle().Which.Should().Be((tenant, "anthropic"));
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

        var result = await service.GetModeAsync(tenant, "ANTHROPIC"); // same identity, case-insensitive
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

        /// <summary>Fix 3 — simulate a cabinet-retire failure on disable.</summary>
        public bool ThrowOnRemove { get; set; }

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
            if (ThrowOnRemove)
            {
                throw new InvalidOperationException("cabinet backend down");
            }
            Removes.Add(providerCanonical);
            return Task.FromResult(true);
        }
    }

    // A cabinet-backed BYOK reader for the REAL 32-3 resolver, seeded by (tenant,
    // cabinetName) — the round-trip closes on the SLUG the service wrote.
    private sealed class SeededByokReader : ITenantProviderKeyReader
    {
        private readonly Dictionary<(Guid, string), TenantProviderKey> _store = new();

        public void Seed(Guid tenantId, string cabinetName, string plaintext, int version) =>
            _store[(tenantId, cabinetName)] = new TenantProviderKey(plaintext, version);

        public Task<TenantProviderKey?> TryReadAsync(
            Guid tenantId, string cabinetName, CancellationToken ct = default) =>
            Task.FromResult(_store.TryGetValue((tenantId, cabinetName), out var v) ? v : null);
    }

    // Platform fallback denied — so an unmatched BYOK slug fails closed (proving the
    // resolver read only succeeds when the slug is byte-identical to the write).
    private sealed class DenyFallback : IPlatformFallbackPolicy
    {
        public bool IsPlatformFallbackAllowed(Guid? tenantId, string providerName) => false;
    }

    private static DefaultProviderCredentialResolver NewCredentialResolver(ITenantProviderKeyReader byok) =>
        new(
            byok, platformKeys: null, new DenyFallback(), new RecordingGateEventRepository(),
            new StubMode(TammaMode.SaaS), new ProviderAllowlist(),
            NullLogger<DefaultProviderCredentialResolver>.Instance);

    private sealed class SpyCredentialResolver : IProviderCredentialResolver
    {
        public List<(Guid?, string)> Invalidated { get; } = new();
        public Task<ProviderCredential> ResolveAsync(Guid? tenantId, string providerName, CancellationToken ct = default) =>
            throw new NotSupportedException("34-3 never resolves keys.");
        public void Invalidate(Guid? tenantId, string providerName) => Invalidated.Add((tenantId, providerName));
    }
}
