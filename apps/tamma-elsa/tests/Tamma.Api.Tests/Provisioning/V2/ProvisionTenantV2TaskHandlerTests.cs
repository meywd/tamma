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
using Tamma.Data.Repositories;

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
        => BuildHandler(new RecordingQueueRepo(), providers);

    private ProvisionTenantV2TaskHandler BuildHandler(
        RecordingQueueRepo repo,
        params ITenantInfrastructureProvider[] providers)
    {
        var all = new List<ITenantInfrastructureProvider> { new NullTenantProvider() };
        all.AddRange(providers);
        var registry = new TenantProviderRegistry(all);
        var workflow = new ProvisionTenantV2Workflow(
            _db,
            registry,
            // Story 30-3 — a real registrar backed by a throwaway fake cabinet
            // so the DedicatedCompute paths reach Ready (Step 6 registers the
            // HMAC into the fake harmlessly). This handler suite asserts queue
            // routing, not secret registration.
            new ProvisioningSecretRegistrar(
                new FakeSecretStore(), NullLogger<ProvisioningSecretRegistrar>.Instance),
            Mock.Of<IPlatformEventPublisher>(),
            TimeProvider.System,
            NullLogger<ProvisionTenantV2Workflow>.Instance)
        {
            ProbeInterval = TimeSpan.FromMilliseconds(1),
            ProbeTimeout = TimeSpan.FromSeconds(5),
        };
        return new ProvisionTenantV2TaskHandler(
            workflow, repo, TimeProvider.System,
            NullLogger<ProvisionTenantV2TaskHandler>.Instance);
    }

    /// <summary>
    /// Minimal queue-repo test double: records <c>DeferAsync</c> calls (the
    /// only method the handler invokes) and no-ops the rest. Modelled on the
    /// <c>StubQueueRepo</c> in <c>RetireSecretVersionTaskHandlerTests</c>.
    /// </summary>
    private sealed class RecordingQueueRepo : IPlatformQueuedTaskRepository
    {
        public List<(Guid Id, DateTime VisibleAt)> Deferred { get; } = new();

        public Task<PlatformQueuedTask> EnqueueAsync(PlatformQueuedTask task, CancellationToken ct = default)
            => Task.FromResult(task);
        public Task<PlatformQueuedTask?> ReserveNextAsync(string workerId, CancellationToken ct = default)
            => Task.FromResult<PlatformQueuedTask?>(null);
        public Task CompleteAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<PlatformQueuedTask?> FailAsync(Guid id, string error, int maxRetries, CancellationToken ct = default)
            => Task.FromResult<PlatformQueuedTask?>(null);
        public Task DeadLetterAsync(Guid id, string error, CancellationToken ct = default) => Task.CompletedTask;
        public Task<PlatformQueuedTask?> ParkUnprocessableAsync(Guid id, string reason, int maxRetries, CancellationToken ct = default)
            => Task.FromResult<PlatformQueuedTask?>(null);
        public Task DeferAsync(Guid id, DateTime visibleAt, CancellationToken ct = default)
        {
            Deferred.Add((id, visibleAt));
            return Task.CompletedTask;
        }
        public Task<PlatformQueuedTask?> GetAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult<PlatformQueuedTask?>(null);
        public Task<int> ReapStaleProcessingAsync(TimeSpan visibilityTimeout, int maxRetries, CancellationToken ct = default)
            => Task.FromResult(0);
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

    [Test]
    public async Task HandleAsync_DeprovisionOperation_RoutesToWorkflowDeprovision()
    {
        // Arrange — build a mock workflow to verify routing without running
        // the real DB-backed implementation. ProvisionTenantV2Workflow is
        // unsealed + virtual-methods so Moq can proxy it.
        var registry = new TenantProviderRegistry(
            new ITenantInfrastructureProvider[] { new NullTenantProvider() });
        var workflowMock = new Mock<ProvisionTenantV2Workflow>(
            _db,
            registry,
            Mock.Of<IProvisioningSecretRegistrar>(),
            Mock.Of<IPlatformEventPublisher>(),
            TimeProvider.System,
            NullLogger<ProvisionTenantV2Workflow>.Instance);

        var deprovisionResult = new ProvisioningResult(
            new ProvisioningStatusSnapshot(
                ProvisioningState.Deprovisioned, "deprovision_complete", null, DateTimeOffset.UtcNow),
            new Dictionary<string, string>());
        var provisionOutcome = ProvisionTenantV2Outcome.Completed(new ProvisioningResult(
            new ProvisioningStatusSnapshot(
                ProvisioningState.Ready, "ready", null, DateTimeOffset.UtcNow),
            new Dictionary<string, string>()));

        workflowMock
            .Setup(w => w.DeprovisionAsync(
                It.IsAny<ProvisionTenantV2TaskPayload>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(deprovisionResult);
        workflowMock
            .Setup(w => w.ExecuteAsync(
                It.IsAny<ProvisionTenantV2TaskPayload>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(provisionOutcome);

        var handler = new ProvisionTenantV2TaskHandler(
            workflowMock.Object, new RecordingQueueRepo(), TimeProvider.System,
            NullLogger<ProvisionTenantV2TaskHandler>.Instance);

        var payload = new ProvisionTenantV2TaskPayload
        {
            TenantId = Guid.NewGuid(),
            ProviderKey = "cranl",
            Operation = ProvisioningOperation.Deprovision,
            Topology = ProvisioningTopology.DedicatedCompute,
        };
        var task = new PlatformQueuedTask
        {
            Type = ProvisionTenantV2TaskPayload.TaskType,
            Payload = JsonSerializer.Serialize(payload),
        };

        // Act
        await handler.HandleAsync(task, CancellationToken.None);

        // Assert — deprovision path called, provision path NOT called.
        workflowMock.Verify(w => w.DeprovisionAsync(
            It.IsAny<ProvisionTenantV2TaskPayload>(), It.IsAny<CancellationToken>()), Times.Once);
        workflowMock.Verify(w => w.ExecuteAsync(
            It.IsAny<ProvisionTenantV2TaskPayload>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task HandleAsync_TenantStillProvisioning_DefersAndThrowsDeferredException()
    {
        // Phase-B I1: a non-terminal probe within the budget makes the handler
        // return the row to the queue (DeferAsync, VisibleAt ≈ now + ProbeInterval)
        // and throw PlatformTaskDeferredException — the worker treats that as a
        // no-op ack (no Complete/Fail), releasing the single-worker slot so the
        // inner provisioning.tenant task can run before the next resume.
        var tenant = await SeedAsync();
        var fake = new FakeTenantInfrastructureProvider("cranl");
        fake.EnqueueDeploying(times: 1); // still provisioning on the single probe

        var repo = new RecordingQueueRepo();
        var handler = BuildHandler(repo, fake);
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
            // First-enqueue timestamp in the recent past ⇒ deadline (CreatedAt +
            // 5s ProbeTimeout) is still in the future ⇒ within budget ⇒ defer.
            CreatedAt = DateTime.UtcNow,
        };

        var before = DateTime.UtcNow;
        var thrown = await Record(() => handler.HandleAsync(task, CancellationToken.None));

        // Deferred, not a terminal failure.
        thrown.Should().BeOfType<PlatformTaskDeferredException>();
        thrown.Should().NotBeOfType<PlatformTaskTerminalException>();
        // Row was returned to the queue with a future VisibleAt (≈ now + 1ms
        // ProbeInterval), and only once.
        repo.Deferred.Should().ContainSingle();
        repo.Deferred[0].Id.Should().Be(task.Id);
        repo.Deferred[0].VisibleAt.Should().BeOnOrAfter(before);
        // No compensation on a defer — the tenant stays Pending for the resume.
        fake.DeprovisionCalls.Should().BeEmpty();
        var refreshed = await _db.Tenants.IgnoreQueryFilters()
            .FirstAsync(t => t.Id == tenant.Id);
        refreshed.ProvisioningState.Should().Be("pending");
    }

    [Test]
    public async Task HandleAsync_ProbeDeadlineExceeded_FailsWithoutDeferring()
    {
        // When the cross-resume budget (task.CreatedAt + ProbeTimeout) is
        // already blown, a still-provisioning probe drives Failed + compensation
        // — it must NOT defer (which would loop forever past the deadline).
        var tenant = await SeedAsync();
        var fake = new FakeTenantInfrastructureProvider("cranl");
        fake.EnqueueDeploying(times: 1);

        var repo = new RecordingQueueRepo();
        var handler = BuildHandler(repo, fake);
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
            // Enqueued an hour ago ⇒ CreatedAt + 5s ProbeTimeout is long past.
            CreatedAt = DateTime.UtcNow.AddHours(-1),
        };

        // Terminal (Failed) — the handler does NOT throw; the state is persisted.
        Func<Task> act = async () => await handler.HandleAsync(task, CancellationToken.None);
        await act.Should().NotThrowAsync();

        repo.Deferred.Should().BeEmpty("past the deadline the handler must not defer");
        fake.DeprovisionCalls.Should().HaveCount(1, "timeout runs compensation");
        var refreshed = await _db.Tenants.IgnoreQueryFilters()
            .FirstAsync(t => t.Id == tenant.Id);
        refreshed.ProvisioningState.Should().Be("failed");
    }

    private static async Task<Exception?> Record(Func<Task> act)
    {
        try { await act(); return null; }
        catch (Exception ex) { return ex; }
    }
}
