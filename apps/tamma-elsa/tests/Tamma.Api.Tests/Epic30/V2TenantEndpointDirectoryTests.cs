using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.Provisioning.V2;
using Tamma.Api.Tests.Provisioning.V2;
using Tamma.Data.Abstractions;

namespace Tamma.Api.Tests.Epic30;

/// <summary>
/// Epic 30 Phase B (B1) — <see cref="V2TenantEndpointDirectory"/> is
/// narrowed so it NEVER supplies a routed DB connection string. The
/// unified per-tenant <c>EncryptedConnectionString</c> envelope (via the
/// <c>tenant_databases</c> pool) is the ONLY DB route. The old bypass —
/// turning a provider-supplied <c>DatabaseUrl</c> into a
/// <see cref="TenantEndpointResolution.Resolved"/> result the LRU resolver
/// routes on — is removed.
///
/// <para>These tests pin the new contract directly at the directory
/// level: every path returns
/// <see cref="TenantEndpointResolution.NotApplicable"/> for DB-routing
/// purposes, and the provider's
/// <see cref="ITenantInfrastructureProvider.ResolveEndpointsAsync"/> is
/// NOT consulted on the DB-routing hot path.</para>
/// </summary>
[TestFixture]
public class V2TenantEndpointDirectoryTests
{
    private static V2TenantEndpointDirectory CreateDirectory(
        ITenantProviderKeyLookup keyLookup,
        params ITenantInfrastructureProvider[] providers)
    {
        var registry = new TenantProviderRegistry(providers);
        return new V2TenantEndpointDirectory(
            registry, keyLookup, NullLogger<V2TenantEndpointDirectory>.Instance);
    }

    // ─── Core B1 assertion — the DatabaseUrl bypass is gone ───────────

    [Test]
    public async Task TryResolveAsync_NeverRoutesOnProviderDatabaseUrl_ForProviderKeyedTenant()
    {
        var tenantId = Guid.NewGuid();
        var resolveCalls = 0;
        // A fully-provisioned provider that WOULD hand back a routable
        // DatabaseUrl. Pre-B1 this made the directory return Resolved(...)
        // and the resolver route on the provider URL. Post-B1 the directory
        // must ignore it entirely (and not even ask the provider).
        var provider = new FakeTenantInfrastructureProvider("cranl")
        {
            OnResolveEndpoints = (id, _) =>
            {
                Interlocked.Increment(ref resolveCalls);
                return Task.FromResult(new TenantEndpoints(
                    DatabaseUrl: $"Host=provider;Port=5432;Database=provider_bypass_{id:N};Username=u;Password=p",
                    EngineHost: null,
                    EngineUrl: "https://engine.example"));
            },
        };
        var directory = CreateDirectory(FakeProviderKeyLookup.Returning("cranl"), provider);

        var result = await directory.TryResolveAsync(tenantId);

        result.IsApplicable.Should().BeFalse(
            "B1: the unified EncryptedConnectionString envelope is the only DB route; " +
            "the directory must never supply a routed provider DatabaseUrl");
        result.DatabaseUrl.Should().BeNull(
            "no DatabaseUrl may be carried back to the resolver for DB routing");
        resolveCalls.Should().Be(0,
            "DB routing must not consult the provider's ResolveEndpointsAsync — " +
            "engine-URL resolution stays available for a future dedicated-compute dispatch consumer, " +
            "but it is not on the DB-routing hot path");
    }

    // ─── Provider-key not set → NotApplicable (unchanged) ─────────────

    [Test]
    public async Task TryResolveAsync_ReturnsNotApplicable_WhenProviderKeyNull()
    {
        var tenantId = Guid.NewGuid();
        var directory = CreateDirectory(FakeProviderKeyLookup.Returning(null));

        var result = await directory.TryResolveAsync(tenantId);

        result.IsApplicable.Should().BeFalse();
        result.DatabaseUrl.Should().BeNull();
    }

    // ─── Provider key set but not registered in this deployment ───────

    [Test]
    public async Task TryResolveAsync_ReturnsNotApplicable_WhenProviderKeyUnregistered()
    {
        var tenantId = Guid.NewGuid();
        // Registry only knows "cranl"; the tenant claims "hetzner".
        var directory = CreateDirectory(
            FakeProviderKeyLookup.Returning("hetzner"),
            new FakeTenantInfrastructureProvider("cranl"));

        var result = await directory.TryResolveAsync(tenantId);

        result.IsApplicable.Should().BeFalse();
        result.DatabaseUrl.Should().BeNull();
    }

    // ─── Unknown tenant bubbles as a definitive control-plane 404 ─────

    [Test]
    public async Task TryResolveAsync_BubblesTenantNotFound()
    {
        var tenantId = Guid.NewGuid();
        var directory = CreateDirectory(FakeProviderKeyLookup.NotFound());

        var act = async () => await directory.TryResolveAsync(tenantId);

        await act.Should().ThrowAsync<TenantNotFoundException>();
    }

    // ─── Test double ──────────────────────────────────────────────────

    private sealed class FakeProviderKeyLookup : ITenantProviderKeyLookup
    {
        private readonly string? _providerKey;
        private readonly bool _throwNotFound;

        private FakeProviderKeyLookup(string? providerKey, bool throwNotFound)
        {
            _providerKey = providerKey;
            _throwNotFound = throwNotFound;
        }

        public static FakeProviderKeyLookup Returning(string? providerKey) => new(providerKey, false);

        public static FakeProviderKeyLookup NotFound() => new(null, true);

        public Task<string?> GetProviderKeyAsync(Guid tenantId, CancellationToken ct)
        {
            if (_throwNotFound)
                throw new TenantNotFoundException(tenantId);
            return Task.FromResult(_providerKey);
        }
    }
}
