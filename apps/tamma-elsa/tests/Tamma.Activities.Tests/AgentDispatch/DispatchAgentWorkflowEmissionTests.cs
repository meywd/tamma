using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.AgentDispatch;
using Tamma.Data.Abstractions;

namespace Tamma.Activities.Tests.AgentDispatch;

/// <summary>
/// Wave C.4 §2 — AGENT.DISPATCH.FAILED emission helper on
/// <see cref="DispatchAgentWorkflowActivity"/>. Integration test in
/// <c>E2EAlertFlowTests</c> covers the call-site round-trip.
/// </summary>
[TestFixture]
public class DispatchAgentWorkflowEmissionTests
{
    private sealed class RecordingEmitter : IAlertEventEmitter
    {
        public List<AgentDispatchFailedEvent> Events { get; } = new();

        public Task EmitAgentDispatchFailedAsync(
            AgentDispatchFailedEvent evt, CancellationToken ct)
        { Events.Add(evt); return Task.CompletedTask; }

        public Task EmitBudgetExhaustedAsync(BudgetExhaustedEvent evt, CancellationToken ct) => Task.CompletedTask;
        public Task EmitWorkflowRetryExceededAsync(WorkflowRetryExceededEvent evt, CancellationToken ct) => Task.CompletedTask;
        public Task EmitPlatformApiUnhealthyAsync(PlatformApiUnhealthyEvent evt, CancellationToken ct) => Task.CompletedTask;
        public Task EmitSecretRotationFailedAsync(SecretRotationFailedEvent evt, CancellationToken ct) => Task.CompletedTask;
    }

    [Test]
    public async Task EmitAgentDispatchFailed_WritesEvent_WithRequiredFields()
    {
        var emitter = new RecordingEmitter();
        var tenantId = Guid.NewGuid();

        await DispatchAgentWorkflowActivity.EmitAgentDispatchFailedAsync(
            emitter,
            tenantId: tenantId,
            correlationId: "sess-123",
            agentHandle: "claude-code",
            reason: "github_403",
            attemptNumber: 2,
            lastError: "GitHub returned 403",
            ct: default);

        emitter.Events.Should().ContainSingle();
        var evt = emitter.Events[0];
        evt.TenantId.Should().Be(tenantId);
        evt.CorrelationId.Should().Be("sess-123");
        evt.AgentHandle.Should().Be("claude-code");
        evt.Reason.Should().Be("github_403");
        evt.AttemptNumber.Should().Be(2);
        evt.LastError.Should().Contain("403");
    }

    [Test]
    public async Task EmitAgentDispatchFailed_NullEmitter_IsNoOp()
    {
        await DispatchAgentWorkflowActivity.EmitAgentDispatchFailedAsync(
            emitter: null,
            tenantId: Guid.NewGuid(),
            correlationId: "c",
            agentHandle: "h",
            reason: "r",
            attemptNumber: 1,
            lastError: "e",
            ct: default);
    }

    [Test]
    public async Task EmitAgentDispatchFailed_NullTenant_SkipsEmission()
    {
        // AGENT.DISPATCH.FAILED rule groups by tenantId. Emitting with
        // a null tenant would defeat the 3x/5min window rule.
        var emitter = new RecordingEmitter();
        await DispatchAgentWorkflowActivity.EmitAgentDispatchFailedAsync(
            emitter,
            tenantId: null,
            correlationId: "c",
            agentHandle: "h",
            reason: "r",
            attemptNumber: 1,
            lastError: "e",
            ct: default);
        emitter.Events.Should().BeEmpty();
    }
}
