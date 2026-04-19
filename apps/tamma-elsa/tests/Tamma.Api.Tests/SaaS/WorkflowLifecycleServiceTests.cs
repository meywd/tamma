using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.SaaS;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.SaaS;

/// <summary>
/// Unit tests for <see cref="WorkflowLifecycleService"/>. Covers status
/// updates (variables merge + persist) and terminal result recording (emits
/// WORKFLOW.COMPLETED / WORKFLOW.FAILED events with payload).
/// </summary>
[TestFixture]
public class WorkflowLifecycleServiceTests
{
    private Mock<IWorkflowRepository> _workflowRepo = null!;
    private Mock<IEventRepository> _eventRepo = null!;
    private Mock<ILogger<WorkflowLifecycleService>> _logger = null!;
    private WorkflowLifecycleService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _workflowRepo = new Mock<IWorkflowRepository>();
        _eventRepo = new Mock<IEventRepository>();
        _logger = new Mock<ILogger<WorkflowLifecycleService>>();
        _service = new WorkflowLifecycleService(
            _workflowRepo.Object, _eventRepo.Object, _logger.Object);
    }

    // ─── Status updates ─────────────────────────────────────────────────────

    [Test]
    public async Task UpdateStatusAsync_HappyPath_PersistsVariablesAndStatus()
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
                    Variables = "{\"preExisting\":\"keep-me\"}"
                };
                mutate(inst);
                return inst;
            });

        var vars = JsonDocument.Parse("{\"progress\":42,\"message\":\"half-way\"}");
        var result = await _service.UpdateStatusAsync(instanceId, "running", vars.RootElement.Clone());

        result.Success.Should().BeTrue();
        _workflowRepo.Verify(r => r.UpdateInstanceAsync(instanceId, It.IsAny<Action<WorkflowInstance>>()), Times.Once);
    }

    [Test]
    public async Task UpdateStatusAsync_MergesExistingVariables()
    {
        var instanceId = Guid.NewGuid();
        WorkflowInstance? updated = null;

        _workflowRepo.Setup(r => r.UpdateInstanceAsync(instanceId, It.IsAny<Action<WorkflowInstance>>()))
            .ReturnsAsync((Guid _, Action<WorkflowInstance> mutate) =>
            {
                var inst = new WorkflowInstance
                {
                    Id = instanceId,
                    Status = "running",
                    Variables = "{\"keepMe\":\"original\",\"willOverride\":\"old\"}"
                };
                mutate(inst);
                updated = inst;
                return inst;
            });

        var vars = JsonDocument.Parse("{\"willOverride\":\"new\",\"freshKey\":\"value\"}");
        await _service.UpdateStatusAsync(instanceId, "running", vars.RootElement.Clone());

        updated.Should().NotBeNull();
        var merged = JsonDocument.Parse(updated!.Variables).RootElement;
        merged.GetProperty("keepMe").GetString().Should().Be("original");
        merged.GetProperty("willOverride").GetString().Should().Be("new");
        merged.GetProperty("freshKey").GetString().Should().Be("value");
    }

    [Test]
    public async Task UpdateStatusAsync_NullVariables_StillUpdatesStatus()
    {
        var instanceId = Guid.NewGuid();
        WorkflowInstance? captured = null;

        _workflowRepo.Setup(r => r.UpdateInstanceAsync(instanceId, It.IsAny<Action<WorkflowInstance>>()))
            .ReturnsAsync((Guid _, Action<WorkflowInstance> mutate) =>
            {
                var inst = new WorkflowInstance
                {
                    Id = instanceId,
                    Status = "pending",
                    Variables = "{\"keep\":\"yes\"}"
                };
                mutate(inst);
                captured = inst;
                return inst;
            });

        await _service.UpdateStatusAsync(instanceId, "running", null);

        captured.Should().NotBeNull();
        captured!.Status.Should().Be("running");
        JsonDocument.Parse(captured.Variables).RootElement.GetProperty("keep").GetString()
            .Should().Be("yes");
    }

    [Test]
    public async Task UpdateStatusAsync_UnknownInstance_ReturnsNotFound()
    {
        var instanceId = Guid.NewGuid();
        _workflowRepo.Setup(r => r.UpdateInstanceAsync(instanceId, It.IsAny<Action<WorkflowInstance>>()))
            .ReturnsAsync((WorkflowInstance?)null);

        var result = await _service.UpdateStatusAsync(instanceId, "running", null);
        result.Success.Should().BeFalse();
        result.ErrorReason.Should().Be("not_found");
    }

    // ─── Result recording ───────────────────────────────────────────────────

    [Test]
    public async Task RecordResultAsync_Success_MarksCompletedAndEmitsEvent()
    {
        var instanceId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        WorkflowInstance? captured = null;

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
                captured = inst;
                return inst;
            });

        var result = JsonDocument.Parse("{\"prNumber\":42,\"duration\":12345}");
        var outcome = await _service.RecordResultAsync(instanceId, result.RootElement.Clone(), terminalStatus: "completed");

        outcome.Success.Should().BeTrue();
        captured.Should().NotBeNull();
        captured!.Status.Should().Be("completed");
        captured.CompletedAt.Should().NotBeNull();
        captured.Result.Should().NotBeNull();
        JsonDocument.Parse(captured.Result!).RootElement.GetProperty("prNumber").GetInt32().Should().Be(42);

        _eventRepo.Verify(r => r.AppendAsync(It.Is<DomainEvent>(
            e => e.Type == "WORKFLOW.COMPLETED" && e.TenantId == tenantId)),
            Times.Once);
    }

    [Test]
    public async Task RecordResultAsync_Failed_MarksFailedAndEmitsFailedEvent()
    {
        var instanceId = Guid.NewGuid();
        WorkflowInstance? captured = null;

        _workflowRepo.Setup(r => r.UpdateInstanceAsync(instanceId, It.IsAny<Action<WorkflowInstance>>()))
            .ReturnsAsync((Guid _, Action<WorkflowInstance> mutate) =>
            {
                var inst = new WorkflowInstance
                {
                    Id = instanceId,
                    Status = "running",
                    Variables = "{}"
                };
                mutate(inst);
                captured = inst;
                return inst;
            });

        var result = JsonDocument.Parse("{\"error\":\"timed out\"}");
        var outcome = await _service.RecordResultAsync(instanceId, result.RootElement.Clone(), terminalStatus: "failed");

        outcome.Success.Should().BeTrue();
        captured!.Status.Should().Be("failed");
        captured.CompletedAt.Should().NotBeNull();

        _eventRepo.Verify(r => r.AppendAsync(It.Is<DomainEvent>(
            e => e.Type == "WORKFLOW.FAILED")), Times.Once);
    }

    [Test]
    public async Task RecordResultAsync_UnknownInstance_ReturnsNotFound()
    {
        var instanceId = Guid.NewGuid();
        _workflowRepo.Setup(r => r.UpdateInstanceAsync(instanceId, It.IsAny<Action<WorkflowInstance>>()))
            .ReturnsAsync((WorkflowInstance?)null);

        var result = await _service.RecordResultAsync(instanceId,
            JsonDocument.Parse("{}").RootElement.Clone(), terminalStatus: "completed");

        result.Success.Should().BeFalse();
        result.ErrorReason.Should().Be("not_found");
        _eventRepo.Verify(r => r.AppendAsync(It.IsAny<DomainEvent>()), Times.Never);
    }

    [Test]
    public async Task RecordResultAsync_EventDataIncludesResultPayload()
    {
        var instanceId = Guid.NewGuid();
        _workflowRepo.Setup(r => r.UpdateInstanceAsync(instanceId, It.IsAny<Action<WorkflowInstance>>()))
            .ReturnsAsync((Guid _, Action<WorkflowInstance> mutate) =>
            {
                var inst = new WorkflowInstance
                {
                    Id = instanceId,
                    Status = "running",
                    Variables = "{}"
                };
                mutate(inst);
                return inst;
            });

        DomainEvent? captured = null;
        _eventRepo.Setup(r => r.AppendAsync(It.IsAny<DomainEvent>()))
            .Callback<DomainEvent>(e => captured = e)
            .ReturnsAsync((DomainEvent e) => e);

        var result = JsonDocument.Parse("{\"x\":1}");
        await _service.RecordResultAsync(instanceId, result.RootElement.Clone(), terminalStatus: "completed");

        captured.Should().NotBeNull();
        var data = JsonDocument.Parse(captured!.Data).RootElement;
        data.GetProperty("instanceId").GetString().Should().Be(instanceId.ToString());
        data.GetProperty("result").GetProperty("x").GetInt32().Should().Be(1);
    }
}
