using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Provisioning;
using Tamma.Api.Services.Provisioning.V2;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Provisioning.V2;

/// <summary>
/// Story 30-2 — behavioural tests for
/// <see cref="ProvisionTenantV2Dispatcher"/>. The dispatcher is the
/// entry-point service operators call to submit a provisioning request;
/// it short-circuits in single-user mode (null seam wins), validates the
/// request against provider capabilities, and enqueues onto the platform
/// queue when accepted.
/// </summary>
[TestFixture]
public sealed class ProvisionTenantV2DispatcherTests
{
    private IServiceScope _scope = null!;
#pragma warning disable NUnit1032
    private ControlPlaneDbContext _db = null!;
#pragma warning restore NUnit1032
    private Mock<IPlatformQueuedTaskRepository> _platformTasks = null!;

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

    private ProvisionTenantV2Dispatcher BuildDispatcher(
        params ITenantInfrastructureProvider[] providers)
    {
        var registry = new TenantProviderRegistry(providers);
        return new ProvisionTenantV2Dispatcher(
            _db,
            registry,
            _platformTasks.Object,
            TimeProvider.System,
            NullLogger<ProvisionTenantV2Dispatcher>.Instance);
    }

    private static ITenantInfrastructureProvider FakeProvider(
        string key,
        ProvisioningTopology supported,
        params string[] regions)
    {
        var mock = new Mock<ITenantInfrastructureProvider>(MockBehavior.Strict);
        mock.Setup(p => p.ProviderKey).Returns(key);
        mock.Setup(p => p.GetCapabilities()).Returns(
            new ProviderCapabilities(
                key, $"Fake {key}", supported, regions, ProviderFeatures.None));
        return mock.Object;
    }

    [Test]
    public async Task DispatchAsync_NullProviderKey_ShortCircuitsAsReadyNoBackend()
    {
        var tenant = await SeedAsync("none");
        var tenantId = tenant.Id;
        var dispatcher = BuildDispatcher(); // registry is empty; the null-key check short-circuits before the registry lookup

        var result = await dispatcher.DispatchAsync(
            tenantId,
            NullTenantProvider.Key,
            new ProvisioningRequest(ProvisioningTopology.DatabaseOnly, "germany-1"),
            invokingOrgId: null,
            CancellationToken.None);

        Assert.That(result.Status.State, Is.EqualTo(ProvisioningState.Ready));
        Assert.That(result.Status.Detail, Is.EqualTo("shared_infrastructure_no_backend_configured"));
        Assert.That(result.Status.FailureReason, Is.Null);
        _platformTasks.Verify(q => q.EnqueueAsync(It.IsAny<PlatformQueuedTask>(), It.IsAny<CancellationToken>()), Times.Never);

        var row = await _db.Tenants.IgnoreQueryFilters().FirstAsync(t => t.Id == tenantId);
        Assert.That(row.ProvisioningState, Is.EqualTo("ready"));
    }

    [Test]
    public async Task DispatchAsync_UnknownProviderKey_StampsProviderNotRegisteredFailure()
    {
        var tenant = await SeedAsync();
        var dispatcher = BuildDispatcher(
            new NullTenantProvider(),
            FakeProvider("cranl", ProvisioningTopology.DedicatedCompute, "germany-1"));

        var result = await dispatcher.DispatchAsync(
            tenant.Id,
            "totally-unknown",
            new ProvisioningRequest(ProvisioningTopology.DatabaseOnly),
            ct: CancellationToken.None);

        result.Status.State.Should().Be(ProvisioningState.Failed);
        result.Status.FailureReason.Should().Be(
            ProvisioningFailureReasons.ProviderNotRegistered);
        _platformTasks.Verify(q => q.EnqueueAsync(
            It.IsAny<PlatformQueuedTask>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task DispatchAsync_TopologyNotSupportedByProvider_FailsFast()
    {
        var tenant = await SeedAsync();
        var dispatcher = BuildDispatcher(
            new NullTenantProvider(),
            FakeProvider("cloudflare", ProvisioningTopology.Managed, "auto"));

        var result = await dispatcher.DispatchAsync(
            tenant.Id,
            "cloudflare",
            new ProvisioningRequest(ProvisioningTopology.DedicatedCompute),
            ct: CancellationToken.None);

        result.Status.FailureReason.Should().Be(
            ProvisioningFailureReasons.UnsupportedTopology);
        _platformTasks.Verify(q => q.EnqueueAsync(
            It.IsAny<PlatformQueuedTask>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task DispatchAsync_RegionNotInProviderList_FailsFast()
    {
        var tenant = await SeedAsync();
        var dispatcher = BuildDispatcher(
            new NullTenantProvider(),
            FakeProvider("hetzner", ProvisioningTopology.DedicatedCompute, "nbg1", "fsn1"));

        var result = await dispatcher.DispatchAsync(
            tenant.Id,
            "hetzner",
            new ProvisioningRequest(
                ProvisioningTopology.DedicatedCompute,
                Region: "us-west-2"),
            ct: CancellationToken.None);

        result.Status.FailureReason.Should().Be(
            ProvisioningFailureReasons.UnsupportedRegion);
        _platformTasks.Verify(q => q.EnqueueAsync(
            It.IsAny<PlatformQueuedTask>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task DispatchAsync_TenantNotFound_ReturnsSyntheticFailure()
    {
        var dispatcher = BuildDispatcher(
            new NullTenantProvider(),
            FakeProvider("cranl", ProvisioningTopology.DedicatedCompute, "germany-1"));

        var result = await dispatcher.DispatchAsync(
            Guid.NewGuid(),
            "cranl",
            new ProvisioningRequest(ProvisioningTopology.DedicatedCompute),
            ct: CancellationToken.None);

        result.Status.FailureReason.Should().Be(
            ProvisioningFailureReasons.TenantNotFound);
        _platformTasks.Verify(q => q.EnqueueAsync(
            It.IsAny<PlatformQueuedTask>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task DispatchAsync_HappyPath_FlipsTenantToPendingAndEnqueuesPlatformTask()
    {
        var tenant = await SeedAsync();
        var dispatcher = BuildDispatcher(
            new NullTenantProvider(),
            FakeProvider("cranl", ProvisioningTopology.DedicatedCompute, "germany-1"));

        var result = await dispatcher.DispatchAsync(
            tenant.Id,
            "cranl",
            new ProvisioningRequest(
                ProvisioningTopology.DedicatedCompute,
                Region: "germany-1",
                CustomName: "acme-prod"),
            ct: CancellationToken.None);

        result.Status.State.Should().Be(ProvisioningState.Pending);
        result.Status.FailureReason.Should().BeNull();

        // Confirms platform-queue dispatch (NOT per-tenant queue).
        _platformTasks.Verify(q => q.EnqueueAsync(
            It.Is<PlatformQueuedTask>(t =>
                t.Type == ProvisionTenantV2TaskPayload.TaskType
                && t.TenantId == tenant.Id
                && t.Payload != null
                && t.Payload.Contains("cranl")
                && t.Payload.Contains("germany-1")
                && t.Payload.Contains("acme-prod")),
            It.IsAny<CancellationToken>()),
            Times.Once);

        var refreshed = await _db.Tenants.IgnoreQueryFilters()
            .FirstAsync(t => t.Id == tenant.Id);
        refreshed.ProvisioningState.Should().Be("pending");
        refreshed.ProvisioningDetail.Should().Be("queued_for_v2_provisioning");
    }

    [Test]
    public async Task DispatchAsync_BlankProviderKey_RecordsFailure()
    {
        var tenant = await SeedAsync();
        var dispatcher = BuildDispatcher(new NullTenantProvider());

        var result = await dispatcher.DispatchAsync(
            tenant.Id,
            providerKey: "  ",
            new ProvisioningRequest(ProvisioningTopology.DatabaseOnly),
            ct: CancellationToken.None);

        result.Status.FailureReason.Should().Be(
            ProvisioningFailureReasons.ProviderNotRegistered);
        _platformTasks.Verify(q => q.EnqueueAsync(
            It.IsAny<PlatformQueuedTask>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Deprovision tests ───────────────────────────────────────────

    [Test]
    public async Task DispatchDeprovisionAsync_NullProviderKey_ShortCircuitsToDeprovisioned()
    {
        var tenant = await SeedAsync("ready");
        var dispatcher = BuildDispatcher(); // null-only (no real providers)

        var result = await dispatcher.DispatchDeprovisionAsync(
            tenant.Id, NullTenantProvider.Key, reason: "tenant_deleted", CancellationToken.None);

        Assert.That(result.Status.State, Is.EqualTo(ProvisioningState.Deprovisioned));
        Assert.That(result.Status.FailureReason, Is.Null);
        _platformTasks.Verify(q => q.EnqueueAsync(
            It.IsAny<PlatformQueuedTask>(), It.IsAny<CancellationToken>()), Times.Never);
        var row = await _db.Tenants.IgnoreQueryFilters().FirstAsync(t => t.Id == tenant.Id);
        Assert.That(row.ProvisioningState, Is.EqualTo("deprovisioned"));
    }

    [Test]
    public async Task DispatchDeprovisionAsync_RealProvider_FlipsToDeprovisioningAndEnqueues()
    {
        var tenant = await SeedAsync("ready");
        var dispatcher = BuildDispatcher(FakeProvider("cranl", ProvisioningTopology.DedicatedCompute));

        PlatformQueuedTask? captured = null;
        _platformTasks
            .Setup(q => q.EnqueueAsync(It.IsAny<PlatformQueuedTask>(), It.IsAny<CancellationToken>()))
            .Callback<PlatformQueuedTask, CancellationToken>((t, _) => captured = t)
            .ReturnsAsync((PlatformQueuedTask t, CancellationToken _) =>
            {
                t.Id = Guid.NewGuid();
                return t;
            });

        var result = await dispatcher.DispatchDeprovisionAsync(
            tenant.Id, "cranl", reason: "plan_downgrade", CancellationToken.None);

        Assert.That(result.Status.State, Is.EqualTo(ProvisioningState.Deprovisioning));
        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.Type, Is.EqualTo(ProvisionTenantV2TaskPayload.TaskType));
        var payload = System.Text.Json.JsonSerializer.Deserialize<ProvisionTenantV2TaskPayload>(captured.Payload!)!;
        Assert.That(payload.Operation, Is.EqualTo(ProvisioningOperation.Deprovision));
        Assert.That(payload.ProviderKey, Is.EqualTo("cranl"));
    }

    [Test]
    public async Task DispatchDeprovisionAsync_UnknownProviderKey_StampsProviderNotRegistered()
    {
        var tenant = await SeedAsync("ready");
        var dispatcher = BuildDispatcher(); // null-only, no "hetzner"

        var result = await dispatcher.DispatchDeprovisionAsync(
            tenant.Id, "hetzner", reason: null, CancellationToken.None);

        Assert.That(result.Status.State, Is.EqualTo(ProvisioningState.Failed));
        Assert.That(result.Status.FailureReason, Is.EqualTo(ProvisioningFailureReasons.ProviderNotRegistered));
        _platformTasks.Verify(q => q.EnqueueAsync(
            It.IsAny<PlatformQueuedTask>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
