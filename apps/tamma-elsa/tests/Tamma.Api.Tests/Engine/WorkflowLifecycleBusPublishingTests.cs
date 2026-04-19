using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Engine.Lifecycle;
using Tamma.Api.Services.SaaS;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Engine;

/// <summary>
/// Verifies <see cref="WorkflowLifecycleService"/> publishes SSE lifecycle
/// frames when status transitions and terminal results land. Finding 012.
/// </summary>
[TestFixture]
public class WorkflowLifecycleBusPublishingTests
{
    private Mock<IWorkflowRepository> _workflowRepo = null!;
    private Mock<IEventRepository> _eventRepo = null!;
    private InMemoryEngineLifecycleBus _bus = null!;
    private WorkflowLifecycleService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _workflowRepo = new Mock<IWorkflowRepository>();
        _eventRepo = new Mock<IEventRepository>();
        _bus = new InMemoryEngineLifecycleBus();
        _service = new WorkflowLifecycleService(
            _workflowRepo.Object,
            _eventRepo.Object,
            NullLogger<WorkflowLifecycleService>.Instance,
            _bus);
    }

    [TearDown]
    public void TearDown() => _bus.Dispose();

    [Test]
    public async Task UpdateStatusAsync_PublishesWorkflowStatusEventToBus()
    {
        var instanceId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        _workflowRepo.Setup(r => r.UpdateInstanceAsync(instanceId, It.IsAny<Action<WorkflowInstance>>()))
            .ReturnsAsync((Guid _, Action<WorkflowInstance> mutate) =>
            {
                var inst = new WorkflowInstance
                {
                    Id = instanceId,
                    TenantId = tenantId,
                    Status = "pending",
                    Variables = "{}"
                };
                mutate(inst);
                return inst;
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new List<EngineLifecycleEvent>();
        var consumer = Task.Run(async () =>
        {
            await foreach (var evt in _bus.SubscribeAsync(tenantId, cts.Token))
            {
                received.Add(evt);
                if (received.Count >= 1) break;
            }
        });
        await WaitForSubscribersAsync(_bus, 1, cts.Token);

        var result = await _service.UpdateStatusAsync(
            instanceId, "running",
            JsonDocument.Parse("{}").RootElement,
            currentActivity: "PlanBuild");
        result.Success.Should().BeTrue();

        await consumer.WaitAsync(cts.Token);

        received.Should().HaveCount(1);
        received[0].Type.Should().Be("workflow.running");
        received[0].TenantId.Should().Be(tenantId);
    }

    [Test]
    public async Task RecordResultAsync_PublishesWorkflowCompletedToBus()
    {
        var instanceId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        _workflowRepo.Setup(r => r.UpdateInstanceAsync(instanceId, It.IsAny<Action<WorkflowInstance>>()))
            .ReturnsAsync((Guid _, Action<WorkflowInstance> mutate) =>
            {
                var inst = new WorkflowInstance
                {
                    Id = instanceId,
                    TenantId = tenantId,
                    Status = "running",
                    Variables = "{}"
                };
                mutate(inst);
                return inst;
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new List<EngineLifecycleEvent>();
        var consumer = Task.Run(async () =>
        {
            await foreach (var evt in _bus.SubscribeAsync(tenantId, cts.Token))
            {
                received.Add(evt);
                if (received.Count >= 1) break;
            }
        });
        await WaitForSubscribersAsync(_bus, 1, cts.Token);

        await _service.RecordResultAsync(
            instanceId,
            JsonDocument.Parse("{\"output\":\"ok\"}").RootElement,
            "completed");

        await consumer.WaitAsync(cts.Token);

        received.Should().HaveCount(1);
        received[0].Type.Should().Be("workflow.completed");
        received[0].TenantId.Should().Be(tenantId);
    }

    private static async Task WaitForSubscribersAsync(IEngineLifecycleBus bus, int expected, CancellationToken ct)
    {
        for (var i = 0; i < 100; i++)
        {
            if (bus.SubscriberCount >= expected) return;
            await Task.Delay(20, ct);
        }
        throw new TimeoutException();
    }
}
