using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.SecretsRotation.Activities;
using Tamma.Activities.SecretsRotation.Contracts;
using Tamma.Data.Abstractions;

namespace Tamma.Activities.Tests.SecretsRotation;

/// <summary>
/// Wave C.4 §5 — verifies the rotation saga fires the alert-shaped
/// SECRET.ROTATION.FAILED event through <see cref="IAlertEventEmitter"/>
/// at each terminal failure site, independent of the Story 29-6
/// rotation-audit event already fired at the same points.
/// </summary>
[TestFixture]
public class SagaRunnerAlertEmissionTests
{
    private static readonly IReadOnlyList<TimeSpan> NoDelays = new[]
    {
        TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero,
    };

    private static readonly Guid SecretIdA = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string CorrelationA = "rot_alert_a";

    private sealed class RecordingAlertEmitter : IAlertEventEmitter
    {
        public List<SecretRotationFailedEvent> Events { get; } = new();

        public Task EmitSecretRotationFailedAsync(
            SecretRotationFailedEvent evt, CancellationToken ct)
        {
            Events.Add(evt);
            return Task.CompletedTask;
        }

        public Task EmitBudgetExhaustedAsync(BudgetExhaustedEvent evt, CancellationToken ct) => Task.CompletedTask;
        public Task EmitAgentDispatchFailedAsync(AgentDispatchFailedEvent evt, CancellationToken ct) => Task.CompletedTask;
        public Task EmitWorkflowRetryExceededAsync(WorkflowRetryExceededEvent evt, CancellationToken ct) => Task.CompletedTask;
        public Task EmitPlatformApiUnhealthyAsync(PlatformApiUnhealthyEvent evt, CancellationToken ct) => Task.CompletedTask;
    }

    [Test]
    public async Task SecretNotFound_EmitsAlertWithMintStage_NoCompensation()
    {
        var gateway = new StubGateway(); // no secret
        var alert = new RecordingAlertEmitter();
        var runner = new SagaRunner(gateway, new StubRegistry(), new StubAuditor(),
            new StubRetireScheduler(), NoDelays, NoDelays, logger: null,
            alertEmitter: alert);

        var state = new RotationWorkflowState
        { SecretId = SecretIdA, RotationCorrelationId = CorrelationA };

        var outcome = await runner.ExecuteAsync(state, "x", 32, default);
        outcome.Should().Be(SagaOutcome.Failed);

        alert.Events.Should().ContainSingle();
        alert.Events[0].FailureStage.Should().Be("mint");
        alert.Events[0].CompensationApplied.Should().BeFalse();
        alert.Events[0].CorrelationId.Should().Be(CorrelationA);
        alert.Events[0].LastError.Should().Be("secret_not_found");
    }

    [Test]
    public async Task HandlerNotRegistered_EmitsAlertWithMintStage()
    {
        var gateway = new StubGateway();
        gateway.Secrets[SecretIdA] = new SecretRotationSnapshot(
            SecretIdA, "x", null, "unknown-system", "", ActiveVersionNumber: 1);

        var alert = new RecordingAlertEmitter();
        var runner = new SagaRunner(gateway, new StubRegistry(), new StubAuditor(),
            new StubRetireScheduler(), NoDelays, NoDelays, logger: null,
            alertEmitter: alert);

        await runner.ExecuteAsync(
            new RotationWorkflowState
            { SecretId = SecretIdA, RotationCorrelationId = CorrelationA },
            "x", 32, default);

        alert.Events.Should().ContainSingle();
        alert.Events[0].FailureStage.Should().Be("mint");
        alert.Events[0].CompensationApplied.Should().BeFalse();
        alert.Events[0].LastError.Should().Be("handler_not_registered");
    }

    [Test]
    public async Task PushFailure_EmitsAlertWithPushStage_CompensationApplied()
    {
        var tenantId = Guid.NewGuid();
        var gateway = new StubGateway();
        gateway.Secrets[SecretIdA] = new SecretRotationSnapshot(
            SecretIdA, "db/app-role", tenantId, "postgres", "role=app", ActiveVersionNumber: 1);
        var handler = new StubHandler("postgres")
        { PushException = new InvalidOperationException("conn_refused") };
        var registry = new StubRegistry { ["postgres"] = handler };

        var alert = new RecordingAlertEmitter();
        var runner = new SagaRunner(gateway, registry, new StubAuditor(),
            new StubRetireScheduler(), NoDelays, NoDelays, logger: null,
            alertEmitter: alert);

        var outcome = await runner.ExecuteAsync(
            new RotationWorkflowState
            { SecretId = SecretIdA, RotationCorrelationId = CorrelationA },
            "pw", 32, default);
        outcome.Should().Be(SagaOutcome.Compensated);

        alert.Events.Should().ContainSingle();
        var evt = alert.Events[0];
        evt.FailureStage.Should().Be("push");
        evt.CompensationApplied.Should().BeTrue();
        evt.TenantId.Should().Be(tenantId);
        evt.CabinetName.Should().Be("db/app-role");
        evt.HandlerType.Should().Be("postgres");
        evt.TargetKind.Should().Be("postgres");
        evt.LastError.Should().StartWith("push_failed:");
    }

    [Test]
    public async Task ProbeFailure_EmitsAlertWithProbeStage()
    {
        var gateway = new StubGateway();
        gateway.Secrets[SecretIdA] = new SecretRotationSnapshot(
            SecretIdA, "db/app-role", null, "postgres", "role=app", ActiveVersionNumber: 1);
        var handler = new StubHandler("postgres")
        { ProbeOutcome = ProbeResult.Unhealthy("auth_failed", 7) };
        var registry = new StubRegistry { ["postgres"] = handler };

        var alert = new RecordingAlertEmitter();
        var runner = new SagaRunner(gateway, registry, new StubAuditor(),
            new StubRetireScheduler(), NoDelays, NoDelays, logger: null,
            alertEmitter: alert);

        await runner.ExecuteAsync(
            new RotationWorkflowState
            { SecretId = SecretIdA, RotationCorrelationId = CorrelationA },
            "pw", 32, default);

        alert.Events.Should().ContainSingle();
        alert.Events[0].FailureStage.Should().Be("probe");
        alert.Events[0].CompensationApplied.Should().BeTrue();
    }

    [Test]
    public async Task HappyPath_NoAlertFired()
    {
        // Success path must NOT emit SECRET.ROTATION.FAILED.
        var gateway = new StubGateway();
        gateway.Secrets[SecretIdA] = new SecretRotationSnapshot(
            SecretIdA, "db/app", null, "postgres", "role=app", ActiveVersionNumber: 1);
        var registry = new StubRegistry { ["postgres"] = new StubHandler("postgres") };

        var alert = new RecordingAlertEmitter();
        var runner = new SagaRunner(gateway, registry, new StubAuditor(),
            new StubRetireScheduler(), NoDelays, NoDelays, logger: null,
            alertEmitter: alert);

        var outcome = await runner.ExecuteAsync(
            new RotationWorkflowState
            { SecretId = SecretIdA, RotationCorrelationId = CorrelationA, GraceWindowSeconds = 5 },
            "pw", 32, default);
        outcome.Should().Be(SagaOutcome.Activated);

        alert.Events.Should().BeEmpty();
    }

    [Test]
    public async Task NullEmitter_SagaRunsWithoutException()
    {
        // Absence of alert emitter must not break the existing saga
        // contract — the rotation-audit emitter stays the primary
        // audit-trail writer.
        var gateway = new StubGateway();
        gateway.Secrets[SecretIdA] = new SecretRotationSnapshot(
            SecretIdA, "db/app", null, "postgres", "role=app", ActiveVersionNumber: 1);
        var registry = new StubRegistry(); // no handler → handler_not_registered
        var runner = new SagaRunner(gateway, registry, new StubAuditor(),
            new StubRetireScheduler(), NoDelays, NoDelays, logger: null,
            alertEmitter: null);

        var outcome = await runner.ExecuteAsync(
            new RotationWorkflowState
            { SecretId = SecretIdA, RotationCorrelationId = CorrelationA },
            "x", 32, default);
        outcome.Should().Be(SagaOutcome.Failed);
    }
}
