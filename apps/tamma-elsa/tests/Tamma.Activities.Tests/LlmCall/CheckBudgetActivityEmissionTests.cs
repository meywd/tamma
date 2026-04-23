using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.LlmCall;
using Tamma.Data.Abstractions;

namespace Tamma.Activities.Tests.LlmCall;

/// <summary>
/// Wave C.4 §1 — verifies <see cref="CheckBudgetActivity"/>'s
/// BUDGET.EXHAUSTED emission helper is invoked with the correct payload
/// on both API-reported and local-budget exhaustion paths.
///
/// <para>The activity inlines the DI lookup + <c>ActivityExecutionContext</c>
/// interaction; Elsa's context can't be cheaply mocked. Per the existing
/// convention in <c>AgentDispatchActivitiesTests</c>, we test the extracted
/// static emission helper directly. The activity body calls the helper
/// once per exhaustion branch — integration tests in
/// <c>E2EAlertFlowTests</c> prove the call site round-trips.</para>
/// </summary>
[TestFixture]
public class CheckBudgetActivityEmissionTests
{
    private sealed class RecordingEmitter : IAlertEventEmitter
    {
        public List<BudgetExhaustedEvent> Budget { get; } = new();
        public List<AgentDispatchFailedEvent> AgentDispatch { get; } = new();
        public List<WorkflowRetryExceededEvent> Workflow { get; } = new();
        public List<PlatformApiUnhealthyEvent> Platform { get; } = new();
        public List<SecretRotationFailedEvent> SecretRotation { get; } = new();

        public Task EmitBudgetExhaustedAsync(BudgetExhaustedEvent evt, CancellationToken ct)
        { Budget.Add(evt); return Task.CompletedTask; }

        public Task EmitAgentDispatchFailedAsync(AgentDispatchFailedEvent evt, CancellationToken ct)
        { AgentDispatch.Add(evt); return Task.CompletedTask; }

        public Task EmitWorkflowRetryExceededAsync(WorkflowRetryExceededEvent evt, CancellationToken ct)
        { Workflow.Add(evt); return Task.CompletedTask; }

        public Task EmitPlatformApiUnhealthyAsync(PlatformApiUnhealthyEvent evt, CancellationToken ct)
        { Platform.Add(evt); return Task.CompletedTask; }

        public Task EmitSecretRotationFailedAsync(SecretRotationFailedEvent evt, CancellationToken ct)
        { SecretRotation.Add(evt); return Task.CompletedTask; }
    }

    [Test]
    public async Task EmitApiBudgetExhaustion_EmitsWithSourceApi()
    {
        var emitter = new RecordingEmitter();
        var tenantId = Guid.NewGuid();

        await CheckBudgetActivity.EmitBudgetExhaustedAsync(
            emitter,
            tenantId: tenantId,
            workflowInstanceId: "wf-1",
            source: "api",
            spent: 10.0m, limit: 10.0m,
            providerName: "anthropic",
            ct: default);

        emitter.Budget.Should().ContainSingle();
        var evt = emitter.Budget[0];
        evt.Source.Should().Be("api");
        evt.TenantId.Should().Be(tenantId);
        evt.Spent.Should().Be(10.0m);
        evt.Limit.Should().Be(10.0m);
        evt.ProviderName.Should().Be("anthropic");
        evt.WorkflowInstanceId.Should().Be("wf-1");
        evt.CorrelationId.Should().Be("wf-1");
    }

    [Test]
    public async Task EmitLocalBudgetExhaustion_EmitsWithSourceLocal()
    {
        var emitter = new RecordingEmitter();
        var tenantId = Guid.NewGuid();

        await CheckBudgetActivity.EmitBudgetExhaustedAsync(
            emitter,
            tenantId: tenantId,
            workflowInstanceId: "wf-2",
            source: "local",
            spent: 5.5m, limit: 5.0m,
            providerName: "openai",
            ct: default);

        emitter.Budget.Should().ContainSingle();
        var evt = emitter.Budget[0];
        evt.Source.Should().Be("local");
    }

    [Test]
    public async Task EmitWithNullTenant_Skips()
    {
        // Budget exhausted is tenant-scoped by definition — no emission
        // makes sense without a tenant context. The helper must be a
        // no-op rather than emitting a bogus Guid.Empty tenantId.
        var emitter = new RecordingEmitter();

        await CheckBudgetActivity.EmitBudgetExhaustedAsync(
            emitter,
            tenantId: null,
            workflowInstanceId: "wf-3",
            source: "api",
            spent: 1.0m, limit: 1.0m,
            providerName: "x",
            ct: default);

        emitter.Budget.Should().BeEmpty();
    }

    [Test]
    public async Task EmitWithNullEmitter_DoesNotThrow()
    {
        // DI may not wire IAlertEventEmitter in some test harnesses; the
        // helper must degrade gracefully.
        await CheckBudgetActivity.EmitBudgetExhaustedAsync(
            emitter: null,
            tenantId: Guid.NewGuid(),
            workflowInstanceId: "wf-4",
            source: "api",
            spent: 1.0m, limit: 1.0m,
            providerName: "x",
            ct: default);
        // passes by not throwing
    }
}
