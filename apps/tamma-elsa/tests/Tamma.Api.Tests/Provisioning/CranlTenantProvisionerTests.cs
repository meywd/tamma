using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Provisioning;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Provisioning;

/// <summary>
/// Tests for <see cref="CranlTenantProvisioner"/>. Verifies the provisioner
/// is idempotent (already-provisioned tenants don't re-enqueue), correctly
/// transitions to Pending on a fresh tenant, and enqueues the right task
/// type for both provision + deprovision flows.
///
/// <para>Story 28-1 PR B — provisioning + deprovisioning tasks live on
/// the platform queue (<see cref="IPlatformQueuedTaskRepository"/>),
/// not the per-tenant queue. Reason: at provisioning time the tenant
/// DB doesn't exist yet (the task's whole job is to create it!); at
/// deprovisioning time the tenant DB is about to be torn down. Tests
/// here mock <see cref="IPlatformQueuedTaskRepository"/> instead of
/// <see cref="ITaskQueue"/>.</para>
/// </summary>
[TestFixture]
public class CranlTenantProvisionerTests
{
    private IServiceScope _scope = null!;
#pragma warning disable NUnit1032
    private ControlPlaneDbContext _db = null!;
#pragma warning restore NUnit1032
    private Mock<IPlatformQueuedTaskRepository> _platformTasks = null!;
    private CranlTenantProvisioner _provisioner = null!;

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

        _provisioner = new CranlTenantProvisioner(
            _db, _platformTasks.Object, NullLogger<CranlTenantProvisioner>.Instance);
    }

    [TearDown]
    public void TearDown() => _scope?.Dispose();

    private async Task<Tenant> SeedAsync(string state = "none", string? projectId = null)
    {
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "Acme",
            Slug = "acme-" + Guid.NewGuid().ToString("N").Substring(0, 6),
            ProvisioningState = state,
            CranlProjectId = projectId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.Tenants.Add(tenant);
        await _db.SaveChangesAsync();
        return tenant;
    }

    [Test]
    public async Task ProvisionAsync_FreshTenant_FlipsToPendingAndEnqueuesProvisioningTask()
    {
        var tenant = await SeedAsync();

        var status = await _provisioner.ProvisionAsync(
            tenant.Id, new ProvisioningOptions("germany-1"), CancellationToken.None);

        status.State.Should().Be(ProvisioningState.Pending);

        _platformTasks.Verify(q => q.EnqueueAsync(
            It.Is<PlatformQueuedTask>(t =>
                t.Type == CranlTenantProvisioner.ProvisioningTaskType
                && t.TenantId == tenant.Id
                && t.Payload != null
                && t.Payload.Contains(tenant.Id.ToString())
                && t.Payload.Contains("germany-1")),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task ProvisionAsync_AlreadyHasCranlProject_DoesNotEnqueueAgain()
    {
        // Tenant has a Cranl project already → idempotent return of current
        // status. The provisioner must not re-enqueue (which would leak a
        // second project on Cranl).
        var tenant = await SeedAsync(state: "database_provisioning", projectId: "proj-existing");

        var status = await _provisioner.ProvisionAsync(
            tenant.Id, new ProvisioningOptions("germany-1"), CancellationToken.None);

        status.State.Should().Be(ProvisioningState.DatabaseProvisioning);
        _platformTasks.Verify(q => q.EnqueueAsync(
            It.IsAny<PlatformQueuedTask>(),
            It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task ProvisionAsync_AlreadyReady_DoesNotReProvision()
    {
        var tenant = await SeedAsync(state: "ready", projectId: "proj-existing");

        var status = await _provisioner.ProvisionAsync(
            tenant.Id, new ProvisioningOptions("germany-1"), CancellationToken.None);

        status.State.Should().Be(ProvisioningState.Ready);
        _platformTasks.VerifyNoOtherCalls();
    }

    [Test]
    public async Task DeprovisionAsync_EnqueuesDeprovisioningTask()
    {
        var tenant = await SeedAsync(state: "ready", projectId: "proj-1");

        await _provisioner.DeprovisionAsync(tenant.Id, CancellationToken.None);

        _platformTasks.Verify(q => q.EnqueueAsync(
            It.Is<PlatformQueuedTask>(t =>
                t.Type == CranlTenantProvisioner.DeprovisioningTaskType
                && t.TenantId == tenant.Id),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task DeprovisionAsync_AlreadyDeprovisioning_DoesNotReEnqueue()
    {
        var tenant = await SeedAsync(state: "deprovisioning");

        await _provisioner.DeprovisionAsync(tenant.Id, CancellationToken.None);

        _platformTasks.VerifyNoOtherCalls();
    }
}
