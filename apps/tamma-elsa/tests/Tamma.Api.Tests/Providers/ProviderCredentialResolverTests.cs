using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Activities.LlmCall.Credentials;
using Tamma.Activities.Security;
using Tamma.Api.Services.PromptStore;
using Tamma.Api.Services.Providers;
using Tamma.Api.Services.Secrets.Stopgap;
using Tamma.Core;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Providers;

/// <summary>
/// Story 32-3 Phase 2 — the resolver algorithm (AC1/2/5/6/9/10/11/13). Uses
/// plain in-memory fakes for the cabinet read, platform key, events, mode, and
/// fallback policy so the precedence / cache / fail-closed / redaction logic is
/// exercised without a Postgres container.
/// </summary>
[TestFixture]
public class ProviderCredentialResolverTests
{
    private const string Sentinel = "SENTINEL-BYOK-XYZ";
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private FakeByokReader _byok = null!;
    private FakePlatformKeys _platform = null!;
    private FakeFallbackPolicy _policy = null!;
    private RecordingEventRepository _events = null!;

    [SetUp]
    public void SetUp()
    {
        _byok = new FakeByokReader();
        _platform = new FakePlatformKeys();
        _policy = new FakeFallbackPolicy(true);
        _events = new RecordingEventRepository();
    }

    private DefaultProviderCredentialResolver Resolver(
        TammaMode mode = TammaMode.SaaS, TimeProvider? clock = null, TimeSpan? ttl = null) =>
        new(
            _byok, _platform, _policy, _events, new StubMode(mode),
            new ProviderAllowlist(), NullLogger<DefaultProviderCredentialResolver>.Instance,
            clock, ttl);

    // ── AC1/AC13: BYOK present → tenant key, source=byok ──────────────────

    [Test]
    public async Task ByokPresent_ReturnsTenantKey_SourceByok()
    {
        _byok.Seed(TenantA, "provider/anthropic/api-key", Sentinel, version: 1);

        var cred = await Resolver().ResolveAsync(TenantA, "anthropic");

        cred.ApiKey.Should().Be(Sentinel);
        cred.Source.Should().Be(CredentialSource.Byok);
        cred.SecretRefStorageKey.Should().Be($"tenant:{TenantA}:provider/anthropic/api-key");
        cred.VersionNumber.Should().Be(1);
        _events.Types.Should().ContainSingle().Which.Should().Be("AGENT.CREDENTIAL_RESOLVED.SUCCESS");
    }

    // ── AC2/AC13: BYOK absent + platform present → source=platform ────────

    [Test]
    public async Task ByokAbsent_PlatformPresent_ReturnsPlatformKey_SourcePlatform()
    {
        _platform.Set("anthropic/api-key", "PLATFORM-KEY");

        var cred = await Resolver().ResolveAsync(TenantA, "anthropic");

        cred.ApiKey.Should().Be("PLATFORM-KEY");
        cred.Source.Should().Be(CredentialSource.Platform);
        cred.SecretRefStorageKey.Should().Be("platform:anthropic/api-key");
        cred.VersionNumber.Should().BeNull();
    }

    // ── AC6: SaaS + both absent + fallback disabled → fail-closed ─────────

    [Test]
    public async Task Saas_NoByok_FallbackDisabled_ThrowsAndEmitsDenied()
    {
        _policy = new FakeFallbackPolicy(false);

        var act = async () => await Resolver(TammaMode.SaaS).ResolveAsync(TenantA, "anthropic");

        var ex = (await act.Should().ThrowAsync<TammaError>()).Which;
        ex.Code.Should().Be("PROVIDER_CREDENTIAL_UNAVAILABLE");
        ex.Retryable.Should().BeFalse();
        ex.Severity.Should().Be(TammaErrorSeverity.High);
        _events.Types.Should().ContainSingle().Which.Should().Be("AGENT.CREDENTIAL.DENIED");
        _events.Types.Should().NotContain("AGENT.CREDENTIAL_RESOLVED.SUCCESS");
    }

    [Test]
    public async Task Saas_NoByok_FallbackAllowedButPlatformUnset_ThrowsAndEmitsDenied()
    {
        // policy allows fallback, but no platform key is set → still loud.
        var act = async () => await Resolver(TammaMode.SaaS).ResolveAsync(TenantA, "anthropic");

        await act.Should().ThrowAsync<TammaError>()
            .Where(e => e.Code == "PROVIDER_CREDENTIAL_UNAVAILABLE");
        _events.Types.Should().ContainSingle().Which.Should().Be("AGENT.CREDENTIAL.DENIED");
    }

    // ── AC6/AC10: single-user falls back to platform; loud only if unset ──

    [Test]
    public async Task SingleUser_PlatformPresent_ReturnsPlatform()
    {
        _platform.Set("anthropic/api-key", "PLATFORM-KEY");

        var cred = await Resolver(TammaMode.SingleUser).ResolveAsync(tenantId: null, "anthropic");

        cred.Source.Should().Be(CredentialSource.Platform);
        cred.ApiKey.Should().Be("PLATFORM-KEY");
    }

    [Test]
    public async Task SingleUser_PlatformUnset_StillThrowsLoud()
    {
        var act = async () =>
            await Resolver(TammaMode.SingleUser).ResolveAsync(tenantId: null, "anthropic");

        await act.Should().ThrowAsync<TammaError>()
            .Where(e => e.Code == "PROVIDER_CREDENTIAL_UNAVAILABLE");
        _events.Types.Should().Contain("AGENT.CREDENTIAL.DENIED");
    }

    // ── AC11: tenant isolation ────────────────────────────────────────────

    [Test]
    public async Task TenantIsolation_TenantBNeverGetsTenantAByokKey()
    {
        _byok.Seed(TenantA, "provider/anthropic/api-key", Sentinel, version: 1);
        _platform.Set("anthropic/api-key", "PLATFORM-KEY");

        var a = await Resolver().ResolveAsync(TenantA, "anthropic");
        var b = await Resolver().ResolveAsync(TenantB, "anthropic");

        a.ApiKey.Should().Be(Sentinel);
        a.Source.Should().Be(CredentialSource.Byok);

        // Tenant B has no BYOK → platform key, NEVER tenant A's sentinel.
        b.ApiKey.Should().Be("PLATFORM-KEY");
        b.ApiKey.Should().NotBe(Sentinel);
        b.Source.Should().Be(CredentialSource.Platform);
    }

    // ── AC5: redaction — sentinel never reaches any emitted event ─────────

    [Test]
    public async Task Redaction_SentinelByokKeyNeverAppearsInAnyEvent()
    {
        _byok.Seed(TenantA, "provider/anthropic/api-key", Sentinel, version: 3);

        await Resolver().ResolveAsync(TenantA, "anthropic");

        _events.Appended.Should().NotBeEmpty();
        foreach (var e in _events.Appended)
        {
            e.Tags.Should().NotContain(Sentinel);
            e.Data.Should().NotContain(Sentinel);
            e.Metadata.Should().NotContain(Sentinel);
            e.Type.Should().NotContain(Sentinel);
        }
        // The tag-safe projection carries source + ref + version, never the key.
        _events.Appended[0].Tags.Should().Contain("byok");
        _events.Appended[0].Data.Should().Contain("3");
    }

    // ── AC9: cache + rotation invalidation ────────────────────────────────

    [Test]
    public async Task Cache_SecondResolveDoesNotReReadCabinet_UntilInvalidate()
    {
        _byok.Seed(TenantA, "provider/anthropic/api-key", "KEY-V1", version: 1);
        var resolver = Resolver();

        var first = await resolver.ResolveAsync(TenantA, "anthropic");
        first.ApiKey.Should().Be("KEY-V1");
        _byok.ReadCount.Should().Be(1);

        // Rotate the underlying cabinet to v2 — but the cache still serves v1.
        _byok.Seed(TenantA, "provider/anthropic/api-key", "KEY-V2", version: 2);
        var cachedAgain = await resolver.ResolveAsync(TenantA, "anthropic");
        cachedAgain.ApiKey.Should().Be("KEY-V1"); // stale-but-cached
        _byok.ReadCount.Should().Be(1);

        // Invalidate (AC7 mutate path) → next resolve re-reads → v2.
        resolver.Invalidate(TenantA, "anthropic");
        var afterInvalidate = await resolver.ResolveAsync(TenantA, "anthropic");
        afterInvalidate.ApiKey.Should().Be("KEY-V2");
        afterInvalidate.VersionNumber.Should().Be(2);
        _byok.ReadCount.Should().Be(2);
    }

    [Test]
    public async Task Cache_TtlExpiry_ReReadsNewKey()
    {
        _byok.Seed(TenantA, "provider/anthropic/api-key", "KEY-V1", version: 1);
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var resolver = Resolver(clock: clock, ttl: TimeSpan.FromSeconds(60));

        (await resolver.ResolveAsync(TenantA, "anthropic")).ApiKey.Should().Be("KEY-V1");
        _byok.Seed(TenantA, "provider/anthropic/api-key", "KEY-V2", version: 2);

        clock.Advance(TimeSpan.FromSeconds(61)); // past TTL
        var afterTtl = await resolver.ResolveAsync(TenantA, "anthropic");
        afterTtl.ApiKey.Should().Be("KEY-V2");
    }

    // ── Edge: unknown provider rejected ──────────────────────────────────

    [Test]
    public async Task UnknownProvider_Throws()
    {
        var act = async () => await Resolver().ResolveAsync(TenantA, "not-a-provider");
        await act.Should().ThrowAsync<TammaError>()
            .Where(e => e.Code == "PROVIDER_CREDENTIAL_UNAVAILABLE");
    }

    // ── Edge: cabinet probe throws → degrade to platform fallback ────────

    [Test]
    public async Task CabinetProbeThrows_DegradesToPlatformFallback()
    {
        _byok.ThrowOnRead = true; // reader contract: it returns null on failure,
        _byok.SwallowToNull = true; // so model the degrade-to-absent behaviour
        _platform.Set("anthropic/api-key", "PLATFORM-KEY");

        var cred = await Resolver().ResolveAsync(TenantA, "anthropic");
        cred.Source.Should().Be(CredentialSource.Platform);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Fakes
    // ─────────────────────────────────────────────────────────────────────

    private sealed class FakeByokReader : ITenantProviderKeyReader
    {
        private readonly ConcurrentDictionary<(Guid, string), TenantProviderKey> _store = new();
        public int ReadCount { get; private set; }
        public bool ThrowOnRead { get; set; }
        public bool SwallowToNull { get; set; }

        public void Seed(Guid tenantId, string cabinetName, string plaintext, int version) =>
            _store[(tenantId, cabinetName)] = new TenantProviderKey(plaintext, version);

        public Task<TenantProviderKey?> TryReadAsync(
            Guid tenantId, string cabinetName, CancellationToken ct = default)
        {
            ReadCount++;
            if (ThrowOnRead && !SwallowToNull)
                throw new InvalidOperationException("cabinet down");
            if (ThrowOnRead && SwallowToNull)
                return Task.FromResult<TenantProviderKey?>(null);
            _store.TryGetValue((tenantId, cabinetName), out var v);
            return Task.FromResult(v);
        }
    }

    private sealed class FakePlatformKeys : IRuntimeSecretResolver
    {
        private readonly Dictionary<string, string> _keys = new(StringComparer.Ordinal);
        public void Set(string cabinetName, string value) => _keys[cabinetName] = value;

        public Task<string?> GetAsync(string cabinetName, CancellationToken ct = default) =>
            Task.FromResult(_keys.TryGetValue(cabinetName, out var v) ? v : null);
    }

    private sealed class FakeFallbackPolicy(bool allowed) : IPlatformFallbackPolicy
    {
        public bool IsPlatformFallbackAllowed(Guid? tenantId, string providerName) => allowed;
    }

    private sealed class RecordingEventRepository : IEventRepository
    {
        public List<DomainEvent> Appended { get; } = new();
        public IEnumerable<string> Types => Appended.Select(e => e.Type);

        public Task<DomainEvent> AppendAsync(DomainEvent evt)
        {
            Appended.Add(evt);
            return Task.FromResult(evt);
        }

        public Task<DomainEvent?> GetByIdAsync(Guid id) => Task.FromResult<DomainEvent?>(null);
        public Task<List<DomainEvent>> QueryAsync(Guid? tenantId, string? type, int? issueNumber, int limit) =>
            Task.FromResult(new List<DomainEvent>());
        public Task<DomainEvent?> GetLastByTypeAsync(Guid tenantId, string type) =>
            Task.FromResult<DomainEvent?>(null);
        public Task ClearAsync(Guid tenantId) => Task.CompletedTask;
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> QueryWithPaginationAsync(
            Guid? tenantId, string? type, int? issueNumber, int limit, int offset) =>
            Task.FromResult(((IReadOnlyList<DomainEvent>)new List<DomainEvent>(), 0));
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> ListByTenantAsync(
            Guid tenantId, string? typePrefix, int limit, int offset) =>
            Task.FromResult(((IReadOnlyList<DomainEvent>)new List<DomainEvent>(), 0));
    }

    private sealed class StubMode(TammaMode mode) : ITammaModeProvider
    {
        public TammaMode Mode { get; } = mode;
    }

    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
