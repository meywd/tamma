using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Provisioning;
using Tamma.Api.Services.Provisioning.V2;
using Tamma.Api.Services.Secrets;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Provisioning.V2;

/// <summary>
/// Story 30-2 — end-to-end tests for
/// <see cref="ProvisionTenantV2Workflow"/>. The workflow drives all 8
/// steps end-to-end against a scriptable
/// <see cref="FakeTenantInfrastructureProvider"/>; tests assert:
///
/// <list type="bullet">
///   <item><description>Happy path: tenant flips through Pending → Ready
///     and the provider's <c>ProvisionAsync</c> + <c>GetStatusAsync</c>
///     are called the expected number of times.</description></item>
///   <item><description>Provider returns Failed snapshot →
///     <c>FailureReason</c> is surfaced verbatim and compensation
///     <c>DeprovisionAsync</c> runs.</description></item>
///   <item><description>Provider throws exception → workflow stamps
///     <c>provider_unexpected_exception</c> + runs compensation.</description></item>
///   <item><description>Probe times out → workflow stamps
///     <c>probe_timeout</c> + runs compensation.</description></item>
///   <item><description>Single-user mode (null seam picked) →
///     <c>no_provisioning_in_this_mode</c> short-code, no provider
///     calls.</description></item>
///   <item><description>Resumability: re-running the workflow against a
///     tenant in <c>Pending</c> state re-issues the provider call (idempotency
///     is the provider's contract per ADR §4) and reaches Ready.</description></item>
/// </list>
/// </summary>
[TestFixture]
public sealed class ProvisionTenantV2WorkflowTests
{
    private IServiceScope _scope = null!;
#pragma warning disable NUnit1032
    private ControlPlaneDbContext _db = null!;
#pragma warning restore NUnit1032

    [SetUp]
    public async Task SetUp()
    {
        await ApiTestFixture.ResetDatabaseAsync();
        _scope = ApiTestFixture.Factory.Services.CreateScope();
        _db = _scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
    }

    [TearDown]
    public void TearDown() => _scope?.Dispose();

    private async Task<Tenant> SeedAsync(string state = "none")
    {
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "Acme",
            Slug = "acme-" + Guid.NewGuid().ToString("N").Substring(0, 6),
            ProvisioningState = state,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Tenants.Add(tenant);
        await _db.SaveChangesAsync();
        return tenant;
    }

    /// <summary>The fake cabinet the last <see cref="Build"/> wired into the
    /// workflow's Story 30-3 secret registrar. Tests inspect / script it
    /// (e.g. <c>_secretStore.CreateCalls</c>, <c>_secretStore.OnCreate</c>).</summary>
    private FakeSecretStore _secretStore = null!;

    private ProvisionTenantV2Workflow Build(
        TenantProviderRegistry registry,
        IPlatformEventPublisher? events = null,
        IProvisioningSecretRegistrar? registrar = null)
    {
        var publisher = events ?? Mock.Of<IPlatformEventPublisher>();
        _secretStore = new FakeSecretStore();
        var secretRegistrar = registrar ?? new ProvisioningSecretRegistrar(
            _secretStore, NullLogger<ProvisioningSecretRegistrar>.Instance);
        return new ProvisionTenantV2Workflow(
            _db,
            registry,
            secretRegistrar,
            publisher,
            TimeProvider.System,
            NullLogger<ProvisionTenantV2Workflow>.Instance)
        {
            ProbeInterval = TimeSpan.FromMilliseconds(1),
            ProbeTimeout = TimeSpan.FromSeconds(5),
        };
    }

    private static TenantProviderRegistry RegistryWith(
        params ITenantInfrastructureProvider[] providers)
    {
        var all = new List<ITenantInfrastructureProvider> { new NullTenantProvider() };
        all.AddRange(providers);
        return new TenantProviderRegistry(all);
    }

    private static ProvisionTenantV2TaskPayload PayloadFor(
        Guid tenantId,
        string providerKey,
        ProvisioningTopology topology = ProvisioningTopology.DedicatedCompute,
        string region = "germany-1") =>
        new()
        {
            TenantId = tenantId,
            ProviderKey = providerKey,
            Topology = topology,
            Region = region,
        };

    [Test]
    public async Task ExecuteAsync_HappyPath_ReachesReadyAndCallsProviderOnce()
    {
        var tenant = await SeedAsync();
        var fake = new FakeTenantInfrastructureProvider("cranl");
        // Phase-B I1: the probe is SINGLE-SHOT — one GetStatusAsync per
        // invocation. A tenant that is Ready on the first probe reaches Ready
        // in one call; the "not ready yet" case is a DeferRequested outcome,
        // covered by ExecuteAsync_NonTerminalWithinBudget_ReturnsDeferRequested.
        fake.EnqueueReady();

        var workflow = Build(RegistryWith(fake));

        var result = await workflow.ExecuteAsync(
            PayloadFor(tenant.Id, "cranl"), CancellationToken.None);

        result.Kind.Should().Be(ProvisionTenantV2OutcomeKind.Completed);
        result.Status.State.Should().Be(ProvisioningState.Ready);
        result.Status.FailureReason.Should().BeNull();
        fake.ProvisionCalls.Should().HaveCount(1);
        fake.StatusCalls.Should().HaveCount(1, "single-shot probe reads status exactly once");
        fake.DeprovisionCalls.Should().BeEmpty("happy path runs no compensation");

        var refreshed = await _db.Tenants.IgnoreQueryFilters()
            .FirstAsync(t => t.Id == tenant.Id);
        refreshed.ProvisioningState.Should().Be("ready");
    }

    [Test]
    public async Task ExecuteAsync_NonTerminalWithinBudget_ReturnsDeferRequested()
    {
        // Phase-B I1: a still-provisioning tenant within the probe budget must
        // NOT block-poll — it returns DeferRequested so the caller releases the
        // worker slot. No compensation runs; the tenant stays Pending.
        var tenant = await SeedAsync();
        var fake = new FakeTenantInfrastructureProvider("cranl");
        fake.EnqueueDeploying(times: 1);

        var workflow = Build(RegistryWith(fake));
        // 2-arg overload → deadline = now + ProbeTimeout(5s) ⇒ within budget.
        var result = await workflow.ExecuteAsync(
            PayloadFor(tenant.Id, "cranl"), CancellationToken.None);

        result.Kind.Should().Be(ProvisionTenantV2OutcomeKind.DeferRequested);
        result.IsDeferRequested.Should().BeTrue();
        result.DeferDelay.Should().Be(workflow.ProbeInterval);
        result.Status.State.Should().Be(ProvisioningState.AppDeploying,
            "the defer outcome carries the last non-terminal snapshot");
        fake.StatusCalls.Should().HaveCount(1, "exactly one probe before yielding");
        fake.DeprovisionCalls.Should().BeEmpty("a defer runs no compensation");

        var refreshed = await _db.Tenants.IgnoreQueryFilters()
            .FirstAsync(t => t.Id == tenant.Id);
        refreshed.ProvisioningState.Should().Be("pending",
            "the tenant stays Pending across the yield so the next resume re-enters");
    }

    [Test]
    public async Task ExecuteAsync_ProviderReturnsFailed_SurfacesReasonAndDeprovisions()
    {
        var tenant = await SeedAsync();
        var fake = new FakeTenantInfrastructureProvider("cranl")
        {
            OnProvision = (_, _, _) => Task.FromResult(new ProvisioningResult(
                new ProvisioningStatusSnapshot(
                    ProvisioningState.Failed,
                    "cranl_db_create_failed",
                    "cranl_db_create_failed",
                    DateTimeOffset.UtcNow),
                new Dictionary<string, string>())),
        };

        var workflow = Build(RegistryWith(fake));

        var result = await workflow.ExecuteAsync(
            PayloadFor(tenant.Id, "cranl"), CancellationToken.None);

        result.Status.State.Should().Be(ProvisioningState.Failed);
        result.Status.FailureReason.Should().Be("cranl_db_create_failed",
            "the workflow surfaces the provider's structured short code verbatim");
        fake.DeprovisionCalls.Should().HaveCount(1,
            "compensation runs DeprovisionAsync once");
        fake.DeprovisionCalls[0].Request.CleanupMode
            .Should().Be(DeprovisioningCleanupMode.BestEffort);
    }

    [Test]
    public async Task ExecuteAsync_ProviderThrowsException_StampsUnexpectedExceptionAndCompensates()
    {
        var tenant = await SeedAsync();
        var fake = new FakeTenantInfrastructureProvider("cranl")
        {
            OnProvision = (_, _, _) => throw new InvalidOperationException("boom"),
        };

        var workflow = Build(RegistryWith(fake));

        var result = await workflow.ExecuteAsync(
            PayloadFor(tenant.Id, "cranl"), CancellationToken.None);

        result.Status.FailureReason.Should().Be(
            ProvisioningFailureReasons.ProviderUnexpectedException);
        result.Status.Detail.Should().Contain("InvalidOperationException");
        // Provider threw before returning a result — compensation
        // catalog was empty for ExecuteProvision (we add it AFTER a
        // successful return). Reserve-resources compensation still
        // runs to flip state back.
        fake.DeprovisionCalls.Should().BeEmpty(
            "compensation for ExecuteProvision is only registered after the provider's call returns");
    }

    [Test]
    public async Task ExecuteAsync_ProbeDeadlineExceeded_StampsTimeoutAndDeprovisions()
    {
        // Phase-B I1: the ~30-min budget is now enforced by an ABSOLUTE
        // deadline passed in by the caller (derived from the task's
        // first-enqueue time so it survives defers). A still-provisioning
        // probe past that deadline drives the same timeout + compensation the
        // old in-loop budget-exceeded branch produced.
        var tenant = await SeedAsync();
        var fake = new FakeTenantInfrastructureProvider("cranl");
        fake.EnqueueDeploying(times: 1);

        var workflow = Build(RegistryWith(fake));
        var alreadyExpired = DateTimeOffset.UtcNow.AddSeconds(-1);

        var result = await workflow.ExecuteAsync(
            PayloadFor(tenant.Id, "cranl"), alreadyExpired, CancellationToken.None);

        result.Kind.Should().Be(ProvisionTenantV2OutcomeKind.Failed);
        result.Status.FailureReason.Should().Be(ProvisioningFailureReasons.ProbeTimeout);
        fake.DeprovisionCalls.Should().HaveCount(1);
    }

    [Test]
    public async Task ExecuteAsync_NullProviderKey_ShortCircuitsAsNoProvisioningInThisMode()
    {
        var tenant = await SeedAsync();
        var workflow = Build(RegistryWith()); // null seam only

        var result = await workflow.ExecuteAsync(
            PayloadFor(tenant.Id, NullTenantProvider.Key,
                ProvisioningTopology.DatabaseOnly),
            CancellationToken.None);

        result.Status.FailureReason.Should().Be(
            ProvisioningFailureReasons.NoProvisioningInThisMode);
    }

    [Test]
    public async Task ExecuteAsync_UnknownProviderKey_FailsAtResolveStep()
    {
        var tenant = await SeedAsync();
        var workflow = Build(RegistryWith()); // only null seam

        var result = await workflow.ExecuteAsync(
            PayloadFor(tenant.Id, "unknown-backend"),
            CancellationToken.None);

        result.Status.FailureReason.Should().Be(
            ProvisioningFailureReasons.ProviderNotRegistered);
    }

    [Test]
    public async Task ExecuteAsync_TopologyNotSupported_FailsAtPreflight()
    {
        var tenant = await SeedAsync();
        var fake = new FakeTenantInfrastructureProvider(
            "cloudflare",
            new ProviderCapabilities(
                "cloudflare",
                "Cloudflare Workers",
                ProvisioningTopology.Managed,
                new[] { "auto" }));

        var workflow = Build(RegistryWith(fake));

        var result = await workflow.ExecuteAsync(
            PayloadFor(tenant.Id, "cloudflare", ProvisioningTopology.DedicatedCompute),
            CancellationToken.None);

        result.Status.FailureReason.Should().Be(
            ProvisioningFailureReasons.UnsupportedTopology);
        fake.ProvisionCalls.Should().BeEmpty();
    }

    [Test]
    public async Task ExecuteAsync_TenantNotFound_ReturnsSyntheticFailure()
    {
        var fake = new FakeTenantInfrastructureProvider("cranl");
        var workflow = Build(RegistryWith(fake));

        var result = await workflow.ExecuteAsync(
            PayloadFor(Guid.NewGuid(), "cranl"), CancellationToken.None);

        result.Status.FailureReason.Should().Be(
            ProvisioningFailureReasons.TenantNotFound);
        fake.ProvisionCalls.Should().BeEmpty();
    }

    [Test]
    public async Task ExecuteAsync_TenantAlreadyReady_RefusesAsIllegalState()
    {
        var tenant = await SeedAsync(state: "ready");
        var fake = new FakeTenantInfrastructureProvider("cranl");
        var workflow = Build(RegistryWith(fake));

        var result = await workflow.ExecuteAsync(
            PayloadFor(tenant.Id, "cranl"), CancellationToken.None);

        result.Status.FailureReason.Should().Be(
            ProvisioningFailureReasons.IllegalTenantState);
        fake.ProvisionCalls.Should().BeEmpty();
    }

    [Test]
    public async Task ExecuteAsync_ResumesAfterRestart_StillReachesReady()
    {
        // Simulate "process died after the dispatcher flipped to
        // Pending but before the workflow ran". The next worker
        // reservation re-fires ExecuteAsync against the Pending state.
        var tenant = await SeedAsync(state: "pending");
        var fake = new FakeTenantInfrastructureProvider("cranl");
        fake.EnqueueReady();

        var workflow = Build(RegistryWith(fake));

        var result = await workflow.ExecuteAsync(
            PayloadFor(tenant.Id, "cranl"), CancellationToken.None);

        result.Status.State.Should().Be(ProvisioningState.Ready);
        // Provider is called exactly once on resume — provider
        // idempotency (ADR §4) handles the "already in flight" case
        // on its side. Workflow doesn't try to skip the call.
        fake.ProvisionCalls.Should().HaveCount(1);
    }

    [Test]
    public async Task ExecuteAsync_PayloadNull_Throws()
    {
        var workflow = Build(RegistryWith(new FakeTenantInfrastructureProvider("cranl")));

        Func<Task> act = async () => await workflow.ExecuteAsync(null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── Deprovision tests ───────────────────────────────────────────

    [Test]
    public async Task DeprovisionAsync_RealProvider_TearsDownAndStampsDeprovisioned()
    {
        var tenant = await SeedAsync("ready");
        var provider = new FakeTenantInfrastructureProvider("cranl");
        var workflow = Build(RegistryWith(provider));
        var payload = new ProvisionTenantV2TaskPayload
        {
            TenantId = tenant.Id,
            ProviderKey = "cranl",
            Operation = ProvisioningOperation.Deprovision,
            Topology = ProvisioningTopology.DedicatedCompute,
        };

        var result = await workflow.DeprovisionAsync(payload, CancellationToken.None);

        result.Status.State.Should().Be(ProvisioningState.Deprovisioned);
        result.Status.FailureReason.Should().BeNull();
        provider.DeprovisionCalls.Should().HaveCount(1, "provider.DeprovisionAsync called once");
        var row = await _db.Tenants.IgnoreQueryFilters().FirstAsync(t => t.Id == tenant.Id);
        row.ProvisioningState.Should().Be("deprovisioned");
    }

    [Test]
    public async Task DeprovisionAsync_ProviderThrows_SwallowsAndStillStampsDeprovisioned()
    {
        var tenant = await SeedAsync("ready");
        var provider = new FakeTenantInfrastructureProvider("cranl")
        {
            OnDeprovision = (_, _, _) => throw new InvalidOperationException("infra_error"),
        };
        var workflow = Build(RegistryWith(provider));
        var payload = new ProvisionTenantV2TaskPayload
        {
            TenantId = tenant.Id,
            ProviderKey = "cranl",
            Operation = ProvisioningOperation.Deprovision,
            Topology = ProvisioningTopology.DedicatedCompute,
        };

        // BestEffort: the workflow must NOT rethrow — a rethrow would re-enqueue teardown.
        Func<Task> act = async () => await workflow.DeprovisionAsync(payload, CancellationToken.None);
        await act.Should().NotThrowAsync();

        var row = await _db.Tenants.IgnoreQueryFilters().FirstAsync(t => t.Id == tenant.Id);
        row.ProvisioningState.Should().Be("deprovisioned",
            "BestEffort swallows the provider exception and still stamps Deprovisioned");
    }

    [Test]
    public async Task DeprovisionAsync_UnknownProviderKey_StampsProviderNotRegistered()
    {
        var tenant = await SeedAsync("ready");
        var workflow = Build(RegistryWith()); // null seam only

        var payload = new ProvisionTenantV2TaskPayload
        {
            TenantId = tenant.Id,
            ProviderKey = "unknown-backend",
            Operation = ProvisioningOperation.Deprovision,
            Topology = ProvisioningTopology.DedicatedCompute,
        };

        var result = await workflow.DeprovisionAsync(payload, CancellationToken.None);

        result.Status.State.Should().Be(ProvisioningState.Failed);
        result.Status.FailureReason.Should().Be(ProvisioningFailureReasons.ProviderNotRegistered);
    }

    [Test]
    public async Task DeprovisionAsync_TenantNotFound_ReturnsSyntheticFailure()
    {
        var provider = new FakeTenantInfrastructureProvider("cranl");
        var workflow = Build(RegistryWith(provider));

        var payload = new ProvisionTenantV2TaskPayload
        {
            TenantId = Guid.NewGuid(), // no such tenant
            ProviderKey = "cranl",
            Operation = ProvisioningOperation.Deprovision,
            Topology = ProvisioningTopology.DedicatedCompute,
        };

        var result = await workflow.DeprovisionAsync(payload, CancellationToken.None);

        result.Status.State.Should().Be(ProvisioningState.Failed);
        result.Status.FailureReason.Should().Be(ProvisioningFailureReasons.TenantNotFound);
        provider.DeprovisionCalls.Should().BeEmpty();
    }

    [Test]
    public async Task DeprovisionAsync_PayloadNull_Throws()
    {
        var workflow = Build(RegistryWith(new FakeTenantInfrastructureProvider("cranl")));

        Func<Task> act = async () => await workflow.DeprovisionAsync(null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Test]
    public async Task ExecuteAsync_RegionNotSupported_FailsAtPreflight()
    {
        var tenant = await SeedAsync();
        var fake = new FakeTenantInfrastructureProvider(
            "hetzner",
            new ProviderCapabilities(
                "hetzner", "Hetzner Cloud",
                ProvisioningTopology.DedicatedCompute,
                new[] { "nbg1", "fsn1" }));

        var workflow = Build(RegistryWith(fake));

        var result = await workflow.ExecuteAsync(
            PayloadFor(tenant.Id, "hetzner",
                ProvisioningTopology.DedicatedCompute,
                region: "us-west-2"),
            CancellationToken.None);

        result.Status.FailureReason.Should().Be(
            ProvisioningFailureReasons.UnsupportedRegion);
        fake.ProvisionCalls.Should().BeEmpty();
    }

    [Test]
    public async Task ExecuteAsync_ProviderProbeReportsFailedDuringPolling_TreatsAsProvisionFailure()
    {
        var tenant = await SeedAsync();
        var fake = new FakeTenantInfrastructureProvider("cranl");
        // Single-shot probe observes the provider's Failed transition.
        fake.EnqueueProviderFailure("cranl_app_deploy_failed", "deploy returned 500");

        var workflow = Build(RegistryWith(fake));

        var result = await workflow.ExecuteAsync(
            PayloadFor(tenant.Id, "cranl"), CancellationToken.None);

        result.Kind.Should().Be(ProvisionTenantV2OutcomeKind.Failed);
        result.Status.State.Should().Be(ProvisioningState.Failed);
        result.Status.FailureReason.Should().Be("cranl_app_deploy_failed",
            "probe surfaces the provider's failure short-code");
        fake.DeprovisionCalls.Should().HaveCount(1, "compensation runs once");
    }

    // ── Step 6: RegisterSecrets (Story 30-3) ────────────────────────

    [Test]
    public async Task ExecuteAsync_DedicatedCompute_RegistersHmacSecretAtStep6()
    {
        var tenant = await SeedAsync();
        var fake = new FakeTenantInfrastructureProvider("cranl");
        fake.EnqueueReady();

        var workflow = Build(RegistryWith(fake));

        var result = await workflow.ExecuteAsync(
            PayloadFor(tenant.Id, "cranl", ProvisioningTopology.DedicatedCompute),
            CancellationToken.None);

        result.Kind.Should().Be(ProvisionTenantV2OutcomeKind.Completed);
        _secretStore.CreateCalls.Should().ContainSingle(
            "dedicated compute registers the per-tenant TAMMA_SHARED_SECRET shadow");
        _secretStore.CreateCalls[0].Name.Should().Be("cranl/app-env-hmac");
        _secretStore.CreateCalls[0].Scope.Should().Be(SecretScope.Tenant);
        _secretStore.CreateCalls[0].TenantId.Should().Be(tenant.Id);
    }

    [Test]
    public async Task ExecuteAsync_NonDedicatedTopology_RegistersNoSecrets_GuardedNoOp()
    {
        var tenant = await SeedAsync();
        // Default fake caps support DatabaseOnly | DedicatedCompute.
        var fake = new FakeTenantInfrastructureProvider("cranl");
        fake.EnqueueReady();

        var workflow = Build(RegistryWith(fake));

        var result = await workflow.ExecuteAsync(
            PayloadFor(tenant.Id, "cranl", ProvisioningTopology.DatabaseOnly),
            CancellationToken.None);

        result.Kind.Should().Be(ProvisionTenantV2OutcomeKind.Completed,
            "the guarded no-op completes the step cleanly and reaches Ready");
        _secretStore.CreateCalls.Should().BeEmpty(
            "a database-only tenant has no per-tenant engine, so nothing is registered");
    }

    [Test]
    public async Task ExecuteAsync_SecretRegistrationThrows_FailsStepLoudAndCompensates()
    {
        var tenant = await SeedAsync();
        var fake = new FakeTenantInfrastructureProvider("cranl");
        fake.EnqueueReady();

        var workflow = Build(RegistryWith(fake));
        // Script a cabinet failure at Step 6 (e.g. fail-closed backend).
        _secretStore.OnCreate = _ => throw new InvalidOperationException("fail_closed_backend");

        var result = await workflow.ExecuteAsync(
            PayloadFor(tenant.Id, "cranl", ProvisioningTopology.DedicatedCompute),
            CancellationToken.None);

        result.Kind.Should().Be(ProvisionTenantV2OutcomeKind.Failed);
        result.Status.FailureReason.Should().Be(
            ProvisioningFailureReasons.SecretRegistrationFailed,
            "a genuine registration failure fails the saga step loud, not silently");
        fake.ProvisionCalls.Should().HaveCount(1, "Step 4 ran before Step 6 failed");
        fake.DeprovisionCalls.Should().HaveCount(1,
            "compensation tears down the ExecuteProvision resources");
    }

    [Test]
    public async Task ExecuteAsync_DedicatedCompute_LaterStepFails_CompensationRetiresHmacSecret()
    {
        var tenant = await SeedAsync();
        var fake = new FakeTenantInfrastructureProvider("cranl");
        // Registers the HMAC at Step 6, then the probe (Step 7) observes Failed.
        fake.EnqueueProviderFailure("cranl_app_deploy_failed", "deploy returned 500");

        var workflow = Build(RegistryWith(fake));

        var result = await workflow.ExecuteAsync(
            PayloadFor(tenant.Id, "cranl", ProvisioningTopology.DedicatedCompute),
            CancellationToken.None);

        result.Kind.Should().Be(ProvisionTenantV2OutcomeKind.Failed);
        _secretStore.CreateCalls.Should().ContainSingle("the HMAC was registered at Step 6");

        var hmacRef = SecretRef.ForTenant(
            tenant.Id, ProvisioningSecretRegistrar.HmacSecretName);
        _secretStore.RetireVersionCalls.Should().ContainSingle()
            .Which.Should().Be((hmacRef, 1),
                "register_secrets compensation retires the secret it registered");
        fake.DeprovisionCalls.Should().HaveCount(1, "execute_provision compensation also runs");
    }
}
