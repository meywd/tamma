using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Provisioning;
using Tamma.Api.Services.Provisioning.Cranl;
using Tamma.Api.Services.Provisioning.V2;
using Tamma.Api.Services.Provisioning.V2.Cranl;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

#pragma warning disable CS0618 // ITenantProvisioner / CranlTenantProvisioner / NullTenantProvisioner
                               // are [Obsolete] (Story 30-3) but the test fixture wires them
                               // alongside v2 to verify both surfaces coexist.

namespace Tamma.Api.Tests.Provisioning.V2;

/// <summary>
/// Story 30-3 — behavioural contract for
/// <see cref="CranlTenantProviderV2"/>. Mirrors the v1
/// <see cref="CranlTenantProvisioner"/> tests where the dispatch
/// semantics overlap (idempotency, platform-queue task type) and adds
/// v2-specific coverage:
///
/// <list type="bullet">
///   <item><description><see cref="ITenantInfrastructureProvider.GetCapabilities"/>
///     reports the documented topology + feature + region matrix.</description></item>
///   <item><description><see cref="ITenantInfrastructureProvider.ProvisionAsync"/>
///     returns a structured failure (not an exception) for unsupported
///     topologies (AC9).</description></item>
///   <item><description><see cref="ITenantInfrastructureProvider.GetStatusAsync"/>
///     reads the structured failure short-code from the
///     <c>FailureReason</c> shadow column when state is <c>Failed</c>.</description></item>
///   <item><description><see cref="ITenantInfrastructureProvider.ResolveEndpointsAsync"/>
///     decrypts the stored <c>CranlDatabaseUrlEncrypted</c> via
///     <see cref="TenantSecretProtector"/> and assembles the
///     <see cref="TenantEndpoints"/>.</description></item>
/// </list>
/// </summary>
[TestFixture]
public sealed class CranlTenantProviderV2Tests
{
    private static readonly byte[] TestKey =
    {
        0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
        0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10,
        0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18,
        0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F, 0x20,
    };

    private IServiceScope _scope = null!;
#pragma warning disable NUnit1032
    private ControlPlaneDbContext _db = null!;
#pragma warning restore NUnit1032
    private Mock<IPlatformQueuedTaskRepository> _platformTasks = null!;
    private TenantSecretProtector _protector = null!;
    private CranlOptions _options = null!;
    private CranlTenantProviderV2 _provider = null!;

    [SetUp]
    public async Task SetUp()
    {
        await ApiTestFixture.ResetDatabaseAsync();
        _scope = ApiTestFixture.Factory.Services.CreateScope();
        _db = _scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();

        _platformTasks = new Mock<IPlatformQueuedTaskRepository>(MockBehavior.Strict);
        _platformTasks.Setup(q => q.EnqueueAsync(
                It.IsAny<PlatformQueuedTask>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((PlatformQueuedTask t, CancellationToken _) =>
            {
                t.Id = Guid.NewGuid();
                return t;
            });

        _protector = new TenantSecretProtector(TestKey);
        _options = new CranlOptions
        {
            ApiKey = "cranl_sk_test_dummy",
            OrganizationId = "org_test",
            DefaultRegion = "germany-1",
        };

        _provider = new CranlTenantProviderV2(
            _db, _platformTasks.Object, _protector, _options,
            NullLogger<CranlTenantProviderV2>.Instance);
    }

    [TearDown]
    public void TearDown() => _scope?.Dispose();

    private async Task<Tenant> SeedAsync(
        string state = "none",
        string? projectId = null,
        string? databaseId = null,
        string? appId = null,
        string? region = null,
        string? appUrl = null,
        byte[]? dbUrlEncrypted = null,
        string? failureReason = null)
    {
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "Acme",
            Slug = "acme-" + Guid.NewGuid().ToString("N").Substring(0, 6),
            ProvisioningState = state,
            CranlProjectId = projectId,
            CranlDatabaseId = databaseId,
            CranlAppId = appId,
            CranlRegion = region,
            CranlAppUrl = appUrl,
            CranlDatabaseUrlEncrypted = dbUrlEncrypted,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Tenants.Add(tenant);
        await _db.SaveChangesAsync();

        if (failureReason is not null)
        {
            _db.Entry(tenant).Property<string?>("FailureReason").CurrentValue = failureReason;
            await _db.SaveChangesAsync();
        }
        return tenant;
    }

    // ─── Static contract ─────────────────────────────────────────────────

    [Test]
    public void ProviderKey_IsCranlSentinel()
    {
        _provider.ProviderKey.Should().Be("cranl");
        CranlCapabilities.ProviderKey.Should().Be("cranl");
    }

    [Test]
    public void GetCapabilities_AdvertisesDocumentedMatrix()
    {
        var caps = _provider.GetCapabilities();

        caps.ProviderKey.Should().Be("cranl");
        caps.DisplayName.Should().NotBeNullOrEmpty();
        caps.SupportsTopology(ProvisioningTopology.DatabaseOnly).Should().BeTrue();
        caps.SupportsTopology(ProvisioningTopology.DedicatedCompute).Should().BeTrue();
        caps.SupportsTopology(ProvisioningTopology.Managed).Should().BeFalse();
        caps.Features.Should().HaveFlag(ProviderFeatures.DedicatedDb);
        caps.Regions.Should().Contain("germany-1");
        caps.Regions.Should().Contain("us-east-1");
    }

    // ─── ProvisionAsync ──────────────────────────────────────────────────

    [Test]
    public async Task ProvisionAsync_FreshTenant_FlipsToPendingAndEnqueuesTask()
    {
        var tenant = await SeedAsync();

        var result = await _provider.ProvisionAsync(
            tenant.Id,
            new ProvisioningRequest(ProvisioningTopology.DedicatedCompute, Region: "germany-1"),
            CancellationToken.None);

        result.Status.State.Should().Be(ProvisioningState.Pending);
        result.Status.FailureReason.Should().BeNull();
        result.Endpoints.Should().BeNull(); // db not yet provisioned

        _platformTasks.Verify(q => q.EnqueueAsync(
            It.Is<PlatformQueuedTask>(t =>
                t.Type == CranlTenantProvisioner.ProvisioningTaskType
                && t.TenantId == tenant.Id
                && t.Payload != null
                && t.Payload.Contains(tenant.Id.ToString())
                && t.Payload.Contains("germany-1")),
            It.IsAny<CancellationToken>()),
            Times.Once);

        // V2 column population: provider_key gets stamped on first provision.
        var refreshed = await _db.Tenants.IgnoreQueryFilters()
            .FirstAsync(t => t.Id == tenant.Id);
        var providerKey = _db.Entry(refreshed).Property<string?>("ProviderKey").CurrentValue;
        providerKey.Should().Be("cranl");
    }

    [Test]
    public async Task ProvisionAsync_NoRegionGiven_FallsBackToConfiguredDefault()
    {
        var tenant = await SeedAsync();

        await _provider.ProvisionAsync(
            tenant.Id,
            new ProvisioningRequest(ProvisioningTopology.DedicatedCompute),
            CancellationToken.None);

        _platformTasks.Verify(q => q.EnqueueAsync(
            It.Is<PlatformQueuedTask>(t => t.Payload != null && t.Payload.Contains("germany-1")),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task ProvisionAsync_UnsupportedTopology_ReturnsStructuredFailure()
    {
        // AC9: managed / BYO topology is NOT in Cranl's support set →
        // structured failure rather than throw.
        var tenant = await SeedAsync();

        var result = await _provider.ProvisionAsync(
            tenant.Id,
            new ProvisioningRequest(ProvisioningTopology.Managed),
            CancellationToken.None);

        result.Status.State.Should().Be(ProvisioningState.Failed);
        result.Status.FailureReason.Should().Be("unsupported_topology");
        result.ProviderResourceIds.Should().BeEmpty();
        _platformTasks.Verify(q => q.EnqueueAsync(
            It.IsAny<PlatformQueuedTask>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task ProvisionAsync_AlreadyHasCranlProject_DoesNotEnqueueAgain()
    {
        var tenant = await SeedAsync(state: "database_provisioning", projectId: "proj-existing");

        var result = await _provider.ProvisionAsync(
            tenant.Id,
            new ProvisioningRequest(ProvisioningTopology.DedicatedCompute, Region: "germany-1"),
            CancellationToken.None);

        result.Status.State.Should().Be(ProvisioningState.DatabaseProvisioning);
        result.ProviderResourceIds.Should().ContainKey("cranl_project_id");
        result.ProviderResourceIds["cranl_project_id"].Should().Be("proj-existing");

        _platformTasks.Verify(q => q.EnqueueAsync(
            It.IsAny<PlatformQueuedTask>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task ProvisionAsync_AlreadyReady_DoesNotReProvisionAndExposesEndpoint()
    {
        var encrypted = _protector.Encrypt("postgresql://u:p@db.cranl.net/x");
        var tenant = await SeedAsync(
            state: "ready",
            projectId: "proj-1",
            databaseId: "db-1",
            appId: "app-1",
            region: "germany-1",
            appUrl: "tamma-engine-x.cranl.net",
            dbUrlEncrypted: encrypted);

        var result = await _provider.ProvisionAsync(
            tenant.Id,
            new ProvisioningRequest(ProvisioningTopology.DedicatedCompute),
            CancellationToken.None);

        result.Status.State.Should().Be(ProvisioningState.Ready);
        result.Endpoints.Should().NotBeNull();
        result.Endpoints!.DatabaseUrl.Should().Be("postgresql://u:p@db.cranl.net/x");
        result.Endpoints.EngineHost.Should().Be("tamma-engine-x.cranl.net");
        result.Endpoints.EngineUrl.Should().Be("https://tamma-engine-x.cranl.net");
        result.ProviderResourceIds.Should().ContainKeys(
            "cranl_project_id", "cranl_database_id", "cranl_app_id", "cranl_region");

        _platformTasks.VerifyNoOtherCalls();
    }

    // ─── DeprovisionAsync ────────────────────────────────────────────────

    [Test]
    public async Task DeprovisionAsync_EnqueuesDeprovisioningTask()
    {
        var tenant = await SeedAsync(state: "ready", projectId: "proj-1", region: "germany-1");

        await _provider.DeprovisionAsync(
            tenant.Id, new DeprovisioningRequest(), CancellationToken.None);

        _platformTasks.Verify(q => q.EnqueueAsync(
            It.Is<PlatformQueuedTask>(t =>
                t.Type == CranlTenantProvisioner.DeprovisioningTaskType
                && t.TenantId == tenant.Id),
            It.IsAny<CancellationToken>()),
            Times.Once);

        var refreshed = await _db.Tenants.IgnoreQueryFilters()
            .FirstAsync(t => t.Id == tenant.Id);
        refreshed.ProvisioningState.Should().Be("deprovisioning");
    }

    [Test]
    public async Task DeprovisionAsync_AppendsReasonToDetail()
    {
        var tenant = await SeedAsync(state: "ready", projectId: "proj-1");

        await _provider.DeprovisionAsync(
            tenant.Id,
            new DeprovisioningRequest(DeprovisioningCleanupMode.Strict, "tenant_deleted"),
            CancellationToken.None);

        var refreshed = await _db.Tenants.IgnoreQueryFilters()
            .FirstAsync(t => t.Id == tenant.Id);
        refreshed.ProvisioningDetail.Should().Contain("tenant_deleted");
    }

    [Test]
    public async Task DeprovisionAsync_AlreadyDeprovisioning_DoesNotReEnqueue()
    {
        var tenant = await SeedAsync(state: "deprovisioning");

        await _provider.DeprovisionAsync(
            tenant.Id, new DeprovisioningRequest(), CancellationToken.None);

        _platformTasks.VerifyNoOtherCalls();
    }

    // ─── GetStatusAsync ──────────────────────────────────────────────────

    [Test]
    public async Task GetStatusAsync_PendingTenant_ReturnsSnapshotWithoutFailureReason()
    {
        var tenant = await SeedAsync(state: "pending");

        var snap = await _provider.GetStatusAsync(tenant.Id, CancellationToken.None);

        snap.State.Should().Be(ProvisioningState.Pending);
        snap.FailureReason.Should().BeNull();
    }

    [Test]
    public async Task GetStatusAsync_FailedTenant_SurfacesShadowFailureReason()
    {
        var tenant = await SeedAsync(
            state: "failed", failureReason: "cranl_db_create_failed");

        var snap = await _provider.GetStatusAsync(tenant.Id, CancellationToken.None);

        snap.State.Should().Be(ProvisioningState.Failed);
        snap.FailureReason.Should().Be("cranl_db_create_failed");
    }

    [Test]
    public async Task GetStatusAsync_NonFailedState_DoesNotLeakStaleFailureReason()
    {
        // FailureReason is only meaningful while State == Failed; a stale
        // value lingering from a prior failed run that was retried must not
        // surface to callers.
        var tenant = await SeedAsync(
            state: "ready", projectId: "proj-1",
            failureReason: "stale_from_previous_run");

        var snap = await _provider.GetStatusAsync(tenant.Id, CancellationToken.None);

        snap.State.Should().Be(ProvisioningState.Ready);
        snap.FailureReason.Should().BeNull();
    }

    // ─── ResolveEndpointsAsync ───────────────────────────────────────────

    [Test]
    public async Task ResolveEndpointsAsync_ReadyTenant_DecryptsAndAssembles()
    {
        var encrypted = _protector.Encrypt("postgresql://user:secret@cranl.example/db");
        var tenant = await SeedAsync(
            state: "ready",
            projectId: "proj-1",
            appUrl: "tamma-engine-y.cranl.net",
            dbUrlEncrypted: encrypted);

        var endpoints = await _provider.ResolveEndpointsAsync(
            tenant.Id, CancellationToken.None);

        endpoints.DatabaseUrl.Should().Be("postgresql://user:secret@cranl.example/db");
        endpoints.EngineHost.Should().Be("tamma-engine-y.cranl.net");
        endpoints.EngineUrl.Should().Be("https://tamma-engine-y.cranl.net");
        endpoints.CustomDomain.Should().BeNull();
    }

    [Test]
    public async Task ResolveEndpointsAsync_NoEncryptedUrl_Throws()
    {
        var tenant = await SeedAsync(state: "pending");

        var act = async () => await _provider.ResolveEndpointsAsync(
            tenant.Id, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ─── Tenant lookup ───────────────────────────────────────────────────

    [Test]
    public async Task GetStatusAsync_UnknownTenant_Throws()
    {
        var act = async () => await _provider.GetStatusAsync(
            Guid.NewGuid(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
