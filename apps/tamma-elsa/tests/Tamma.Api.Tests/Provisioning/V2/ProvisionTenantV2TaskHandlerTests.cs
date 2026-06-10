using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.PlatformTasks;
using Tamma.Api.Services.Provisioning;
using Tamma.Api.Services.Provisioning.V2;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Provisioning.V2;

/// <summary>
/// Story 30-2 — tests for the platform-queue handler that drives the
/// v2 provisioning workflow.
///
/// <para>Covers:</para>
/// <list type="bullet">
///   <item><description>Task type matches
///     <c>provisioning.tenant.v2</c> so
///     <see cref="PlatformTaskHandlerRegistry"/> routes platform-queue
///     rows correctly.</description></item>
///   <item><description>Malformed payload → terminal exception (queue
///     row goes to dead-letter, not retry).</description></item>
///   <item><description>Empty / blank-key payloads → terminal exception
///     (configuration bug, retry won't help).</description></item>
///   <item><description>Happy path → workflow runs and tenant reaches
///     Ready.</description></item>
/// </list>
/// </summary>
[TestFixture]
public sealed class ProvisionTenantV2TaskHandlerTests
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

    private async Task<Tenant> SeedAsync()
    {
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "Acme",
            Slug = "acme-" + Guid.NewGuid().ToString("N").Substring(0, 6),
            ProvisioningState = "none",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Tenants.Add(tenant);
        await _db.SaveChangesAsync();
        return tenant;
    }

    private ProvisionTenantV2TaskHandler BuildHandler(
        params ITenantInfrastructureProvider[] providers)
    {
        var all = new List<ITenantInfrastructureProvider> { new NullTenantProvider() };
        all.AddRange(providers);
        var registry = new TenantProviderRegistry(all);
        var workflow = new ProvisionTenantV2Workflow(
            _db,
            registry,
            Mock.Of<IPlatformEventPublisher>(),
            TimeProvider.System,
            NullLogger<ProvisionTenantV2Workflow>.Instance)
        {
            ProbeInterval = TimeSpan.FromMilliseconds(1),
            ProbeTimeout = TimeSpan.FromSeconds(5),
        };
        return new ProvisionTenantV2TaskHandler(
            workflow, NullLogger<ProvisionTenantV2TaskHandler>.Instance);
    }

    [Test]
    public void TaskType_MatchesPlatformQueueIdentifier()
    {
        var handler = BuildHandler();
        handler.TaskType.Should().Be("provisioning.tenant.v2");
        handler.TaskType.Should().Be(ProvisionTenantV2TaskPayload.TaskType);
    }

    [Test]
    public async Task HandleAsync_MalformedJson_ThrowsTerminalException()
    {
        var handler = BuildHandler();
        var task = new PlatformQueuedTask
        {
            Id = Guid.NewGuid(),
            Type = ProvisionTenantV2TaskPayload.TaskType,
            Payload = "not-valid-json {",
        };

        Func<Task> act = async () => await handler.HandleAsync(task, CancellationToken.None);
        await act.Should().ThrowAsync<PlatformTaskTerminalException>();
    }

    [Test]
    public async Task HandleAsync_EmptyPayload_ThrowsTerminalException()
    {
        var handler = BuildHandler();
        var task = new PlatformQueuedTask
        {
            Id = Guid.NewGuid(),
            Type = ProvisionTenantV2TaskPayload.TaskType,
            Payload = "",
        };

        Func<Task> act = async () => await handler.HandleAsync(task, CancellationToken.None);
        await act.Should().ThrowAsync<PlatformTaskTerminalException>();
    }

    [Test]
    public async Task HandleAsync_BlankProviderKey_ThrowsTerminalException()
    {
        var handler = BuildHandler();
        var payload = new ProvisionTenantV2TaskPayload
        {
            TenantId = Guid.NewGuid(),
            ProviderKey = "",
            Topology = ProvisioningTopology.DatabaseOnly,
        };
        var task = new PlatformQueuedTask
        {
            Id = Guid.NewGuid(),
            Type = ProvisionTenantV2TaskPayload.TaskType,
            Payload = JsonSerializer.Serialize(payload),
        };

        Func<Task> act = async () => await handler.HandleAsync(task, CancellationToken.None);
        await act.Should().ThrowAsync<PlatformTaskTerminalException>();
    }

    [Test]
    public async Task HandleAsync_EmptyTenantId_ThrowsTerminalException()
    {
        var handler = BuildHandler();
        var payload = new ProvisionTenantV2TaskPayload
        {
            TenantId = Guid.Empty,
            ProviderKey = "cranl",
            Topology = ProvisioningTopology.DedicatedCompute,
        };
        var task = new PlatformQueuedTask
        {
            Id = Guid.NewGuid(),
            Type = ProvisionTenantV2TaskPayload.TaskType,
            Payload = JsonSerializer.Serialize(payload),
        };

        Func<Task> act = async () => await handler.HandleAsync(task, CancellationToken.None);
        await act.Should().ThrowAsync<PlatformTaskTerminalException>();
    }

    [Test]
    public async Task HandleAsync_HappyPath_DrivesWorkflowToReady()
    {
        var tenant = await SeedAsync();
        var fake = new FakeTenantInfrastructureProvider("cranl");
        fake.EnqueueReady();

        var handler = BuildHandler(fake);
        var payload = new ProvisionTenantV2TaskPayload
        {
            TenantId = tenant.Id,
            ProviderKey = "cranl",
            Topology = ProvisioningTopology.DedicatedCompute,
            Region = "germany-1",
        };
        var task = new PlatformQueuedTask
        {
            Id = Guid.NewGuid(),
            Type = ProvisionTenantV2TaskPayload.TaskType,
            TenantId = tenant.Id,
            Payload = JsonSerializer.Serialize(payload),
        };

        await handler.HandleAsync(task, CancellationToken.None);

        var refreshed = await _db.Tenants.IgnoreQueryFilters()
            .FirstAsync(t => t.Id == tenant.Id);
        refreshed.ProvisioningState.Should().Be("ready");
        fake.ProvisionCalls.Should().HaveCount(1);
    }

    [Test]
    public async Task HandleAsync_WorkflowReturnsFailedSnapshot_DoesNotThrow()
    {
        // Workflow-level failures should NOT throw out of the handler —
        // they're persisted on the tenant row + emitted as events. Throwing
        // would re-enqueue and trigger compensation again.
        var tenant = await SeedAsync();
        var fake = new FakeTenantInfrastructureProvider("cranl")
        {
            OnProvision = (_, _, _) => Task.FromResult(new ProvisioningResult(
                new ProvisioningStatusSnapshot(
                    ProvisioningState.Failed, "boom", "boom", DateTimeOffset.UtcNow),
                new Dictionary<string, string>())),
        };

        var handler = BuildHandler(fake);
        var payload = new ProvisionTenantV2TaskPayload
        {
            TenantId = tenant.Id,
            ProviderKey = "cranl",
            Topology = ProvisioningTopology.DedicatedCompute,
        };
        var task = new PlatformQueuedTask
        {
            Id = Guid.NewGuid(),
            Type = ProvisionTenantV2TaskPayload.TaskType,
            TenantId = tenant.Id,
            Payload = JsonSerializer.Serialize(payload),
        };

        Func<Task> act = async () => await handler.HandleAsync(task, CancellationToken.None);
        await act.Should().NotThrowAsync();

        var refreshed = await _db.Tenants.IgnoreQueryFilters()
            .FirstAsync(t => t.Id == tenant.Id);
        refreshed.ProvisioningState.Should().Be("failed");
    }

    [Test]
    public async Task HandleAsync_NullTask_Throws()
    {
        var handler = BuildHandler();
        Func<Task> act = async () => await handler.HandleAsync(null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
