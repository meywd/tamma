using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.Provisioning.V2;
using Tamma.Api.Services.Secrets;

namespace Tamma.Api.Tests.Provisioning.V2;

/// <summary>
/// Story 30-3 — unit tests for <see cref="ProvisioningSecretRegistrar"/>
/// (the RegisterSecrets saga-step helper). Drives the registrar directly
/// against a <see cref="FakeSecretStore"/> facade seam:
///
/// <list type="bullet">
///   <item><description>DedicatedCompute → registers the per-tenant HMAC
///     shadow (tenant:cranl/app-env-hmac).</description></item>
///   <item><description>Non-dedicated topologies → guarded no-op (nothing
///     registered).</description></item>
///   <item><description>DedicatedCompute with no cabinet → fails loud.</description></item>
///   <item><description>Register is idempotent (reuses an existing row).</description></item>
///   <item><description>Retire is idempotent + non-throwing.</description></item>
/// </list>
/// </summary>
[TestFixture]
public sealed class ProvisioningSecretRegistrarTests
{
    private static ProvisioningSecretRegistrar Build(ISecretStore? store) =>
        new(store, NullLogger<ProvisioningSecretRegistrar>.Instance);

    private static SecretRef HmacRefFor(Guid tenantId) =>
        SecretRef.ForTenant(tenantId, ProvisioningSecretRegistrar.HmacSecretName);

    [Test]
    public async Task RegisterInitialSecretsAsync_DedicatedCompute_RegistersHmacShadow()
    {
        var store = new FakeSecretStore();
        var registrar = Build(store);
        var tenantId = Guid.NewGuid();

        var registered = await registrar.RegisterInitialSecretsAsync(
            tenantId, ProvisioningTopology.DedicatedCompute, CancellationToken.None);

        registered.Should().ContainSingle().Which.Should().Be(HmacRefFor(tenantId));

        store.CreateCalls.Should().ContainSingle();
        var req = store.CreateCalls[0];
        req.Name.Should().Be("cranl/app-env-hmac");
        req.Scope.Should().Be(SecretScope.Tenant);
        req.TenantId.Should().Be(tenantId);
        req.Purpose.Should().Be(SecretPurpose.HmacSharedSecret);
        req.InitialPlaintext.Should().NotBeNullOrWhiteSpace(
            "the per-tenant HMAC value is minted, not left null");
        req.OwnerUserId.Should().NotBe(Guid.Empty,
            "the cabinet requires a non-empty owner GUID");
    }

    [Test]
    public async Task RegisterInitialSecretsAsync_MintsFreshRandomValuePerTenant()
    {
        var store = new FakeSecretStore();
        var registrar = Build(store);

        await registrar.RegisterInitialSecretsAsync(
            Guid.NewGuid(), ProvisioningTopology.DedicatedCompute, CancellationToken.None);
        await registrar.RegisterInitialSecretsAsync(
            Guid.NewGuid(), ProvisioningTopology.DedicatedCompute, CancellationToken.None);

        store.CreateCalls.Should().HaveCount(2);
        store.CreateCalls[0].InitialPlaintext.Should()
            .NotBe(store.CreateCalls[1].InitialPlaintext,
                "each tenant gets its own random HMAC — not a shared value");
    }

    [Test]
    public async Task RegisterInitialSecretsAsync_DatabaseOnly_RegistersNothing_GuardedNoOp()
    {
        var store = new FakeSecretStore();
        var registrar = Build(store);

        var registered = await registrar.RegisterInitialSecretsAsync(
            Guid.NewGuid(), ProvisioningTopology.DatabaseOnly, CancellationToken.None);

        registered.Should().BeEmpty();
        store.CreateCalls.Should().BeEmpty("non-dedicated topologies have no per-tenant engine");
    }

    [Test]
    public async Task RegisterInitialSecretsAsync_Managed_RegistersNothing_GuardedNoOp()
    {
        var store = new FakeSecretStore();
        var registrar = Build(store);

        var registered = await registrar.RegisterInitialSecretsAsync(
            Guid.NewGuid(), ProvisioningTopology.Managed, CancellationToken.None);

        registered.Should().BeEmpty();
        store.CreateCalls.Should().BeEmpty();
    }

    [Test]
    public async Task RegisterInitialSecretsAsync_NoCabinet_NonDedicated_IsCleanNoOp()
    {
        // No ISecretStore wired (dev/in-memory host). A non-dedicated tenant
        // needs no secret, so the guard short-circuits BEFORE the null check —
        // clean no-op, no throw.
        var registrar = Build(store: null);

        var registered = await registrar.RegisterInitialSecretsAsync(
            Guid.NewGuid(), ProvisioningTopology.DatabaseOnly, CancellationToken.None);

        registered.Should().BeEmpty();
    }

    [Test]
    public async Task RegisterInitialSecretsAsync_NoCabinet_DedicatedCompute_FailsLoud()
    {
        var registrar = Build(store: null);

        var act = async () => await registrar.RegisterInitialSecretsAsync(
            Guid.NewGuid(), ProvisioningTopology.DedicatedCompute, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*secret cabinet*",
                "dedicated compute cannot proceed with no place to store the HMAC");
    }

    [Test]
    public async Task RegisterInitialSecretsAsync_AlreadyRegistered_IsIdempotent()
    {
        var store = new FakeSecretStore();
        var tenantId = Guid.NewGuid();
        store.SeedActiveSecret(HmacRefFor(tenantId)); // prior (rolled-back) attempt minted it
        var registrar = Build(store);

        var registered = await registrar.RegisterInitialSecretsAsync(
            tenantId, ProvisioningTopology.DedicatedCompute, CancellationToken.None);

        registered.Should().ContainSingle().Which.Should().Be(HmacRefFor(tenantId));
        store.CreateCalls.Should().BeEmpty(
            "an existing row is reused — a resumed provision must not double-create");
    }

    [Test]
    public async Task RegisterInitialSecretsAsync_CabinetCreateThrows_Propagates()
    {
        var store = new FakeSecretStore
        {
            OnCreate = _ => throw new InvalidOperationException("fail-closed backend"),
        };
        var registrar = Build(store);

        var act = async () => await registrar.RegisterInitialSecretsAsync(
            Guid.NewGuid(), ProvisioningTopology.DedicatedCompute, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*fail-closed*",
                "a genuine create failure surfaces to the caller (the saga fails loud)");
    }

    [Test]
    public async Task RetireInitialSecretsAsync_AttemptsToRetireRegisteredSecret()
    {
        var store = new FakeSecretStore();
        var tenantId = Guid.NewGuid();
        var registrar = Build(store);

        var registered = await registrar.RegisterInitialSecretsAsync(
            tenantId, ProvisioningTopology.DedicatedCompute, CancellationToken.None);

        // Compensation must not throw even though the facade refuses to retire
        // the sole active version (documented limitation).
        var act = async () => await registrar.RetireInitialSecretsAsync(
            registered, CancellationToken.None);
        await act.Should().NotThrowAsync();

        store.RetireVersionCalls.Should().ContainSingle()
            .Which.Should().Be((HmacRefFor(tenantId), 1),
                "compensation retires the active version of each registered secret");
    }

    [Test]
    public async Task RetireInitialSecretsAsync_SecretNeverCreated_IsIdempotentNoOp()
    {
        var store = new FakeSecretStore();
        var registrar = Build(store);
        var refs = new[] { HmacRefFor(Guid.NewGuid()) };

        var act = async () => await registrar.RetireInitialSecretsAsync(
            refs, CancellationToken.None);
        await act.Should().NotThrowAsync();

        store.RetireVersionCalls.Should().BeEmpty(
            "GetAsync returns null for a never-created secret → nothing to retire");
    }

    [Test]
    public async Task RetireInitialSecretsAsync_EmptyList_NoOp()
    {
        var store = new FakeSecretStore();
        var registrar = Build(store);

        var act = async () => await registrar.RetireInitialSecretsAsync(
            Array.Empty<SecretRef>(), CancellationToken.None);
        await act.Should().NotThrowAsync();
        store.RetireVersionCalls.Should().BeEmpty();
    }

    [Test]
    public async Task RetireInitialSecretsAsync_NoCabinet_NoThrow()
    {
        var registrar = Build(store: null);

        var act = async () => await registrar.RetireInitialSecretsAsync(
            new[] { HmacRefFor(Guid.NewGuid()) }, CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Test]
    public async Task RetireInitialSecretsAsync_RunTwice_IsIdempotent()
    {
        var store = new FakeSecretStore();
        var tenantId = Guid.NewGuid();
        var registrar = Build(store);

        var registered = await registrar.RegisterInitialSecretsAsync(
            tenantId, ProvisioningTopology.DedicatedCompute, CancellationToken.None);

        await registrar.RetireInitialSecretsAsync(registered, CancellationToken.None);
        var act = async () => await registrar.RetireInitialSecretsAsync(
            registered, CancellationToken.None);

        await act.Should().NotThrowAsync("re-running compensation is safe");
    }
}
