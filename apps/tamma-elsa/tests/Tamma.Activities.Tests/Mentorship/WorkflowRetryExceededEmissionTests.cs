using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Mentorship;
using Tamma.Data.Abstractions;

namespace Tamma.Activities.Tests.Mentorship;

/// <summary>
/// Wave C.4 §3 — WORKFLOW.RETRY_EXCEEDED emission helper shared by every
/// retry-exhaustion site (mentorship FlowNodeActivities: Clarify /
/// ReExplain / AutoFix). The helper produces the alert-shaped payload;
/// integration tests cover the call sites.
/// </summary>
[TestFixture]
public class WorkflowRetryExceededEmissionTests
{
    private sealed class RecordingEmitter : IAlertEventEmitter
    {
        public List<WorkflowRetryExceededEvent> Events { get; } = new();

        public Task EmitWorkflowRetryExceededAsync(
            WorkflowRetryExceededEvent evt, CancellationToken ct)
        { Events.Add(evt); return Task.CompletedTask; }

        public Task EmitBudgetExhaustedAsync(BudgetExhaustedEvent evt, CancellationToken ct) => Task.CompletedTask;
        public Task EmitAgentDispatchFailedAsync(AgentDispatchFailedEvent evt, CancellationToken ct) => Task.CompletedTask;
        public Task EmitPlatformApiUnhealthyAsync(PlatformApiUnhealthyEvent evt, CancellationToken ct) => Task.CompletedTask;
        public Task EmitSecretRotationFailedAsync(SecretRotationFailedEvent evt, CancellationToken ct) => Task.CompletedTask;
    }

    [Test]
    public async Task Emit_WithAllFields_WritesExpectedPayload()
    {
        var emitter = new RecordingEmitter();
        var tenantId = Guid.NewGuid();
        var defId = Guid.NewGuid();
        var instId = Guid.NewGuid();

        await WorkflowRetryEmitter.EmitAsync(
            emitter,
            tenantId: tenantId,
            workflowDefinitionId: defId,
            workflowInstanceId: instId,
            attempts: 3,
            maxAttempts: 3,
            finalError: "repeated clarification rejected",
            activityId: "clarify-requirements",
            ct: default);

        var evt = emitter.Events.Should().ContainSingle().Subject;
        evt.TenantId.Should().Be(tenantId);
        evt.WorkflowDefinitionId.Should().Be(defId);
        evt.WorkflowInstanceId.Should().Be(instId);
        evt.Attempts.Should().Be(3);
        evt.MaxAttempts.Should().Be(3);
        evt.FinalError.Should().Contain("rejected");
        evt.ActivityId.Should().Be("clarify-requirements");
        // correlationId defaults to the instance id string for the
        // per-workflow rule-grouping.
        evt.CorrelationId.Should().Be(instId.ToString("N"));
    }

    [Test]
    public async Task Emit_NullEmitter_IsNoOp()
    {
        await WorkflowRetryEmitter.EmitAsync(
            emitter: null,
            tenantId: Guid.NewGuid(),
            workflowDefinitionId: Guid.NewGuid(),
            workflowInstanceId: Guid.NewGuid(),
            attempts: 3, maxAttempts: 3,
            finalError: null, activityId: null, ct: default);
    }

    [Test]
    public async Task Emit_NullTenant_SkipsEmission()
    {
        var emitter = new RecordingEmitter();
        await WorkflowRetryEmitter.EmitAsync(
            emitter,
            tenantId: null,
            workflowDefinitionId: Guid.NewGuid(),
            workflowInstanceId: Guid.NewGuid(),
            attempts: 3, maxAttempts: 3,
            finalError: null, activityId: null, ct: default);
        emitter.Events.Should().BeEmpty();
    }
}
