using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Activities.SecretsRotation.Contracts;
using Tamma.Api.Services;
using Tamma.Api.Services.Secrets.Rotation;

namespace Tamma.Api.Tests.Secrets.Rotation;

/// <summary>
/// Story 29-6 (audit gap #2 + #3) — tests for the
/// <see cref="RotationTriggerService"/>: mint a correlation id, take the
/// per-secret concurrency guard, dispatch <c>rotate-secret</c>, and emit
/// <c>SECRET.ROTATION.REQUESTED</c> / <c>SECRET.ROTATION.REJECTED</c>.
/// Plaintext is never placed on the audit event.
/// </summary>
[TestFixture]
public sealed class RotationTriggerServiceTests
{
    private static readonly Guid SecretA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid TenantA = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid Operator = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    private static RotationTriggerService Build(
        StubGateway gateway, StubAuditor auditor, StubWorkflows workflows) =>
        new(gateway, auditor, workflows, NullLogger<RotationTriggerService>.Instance);

    [Test]
    public async Task Trigger_WhenNotInFlight_Dispatches_And_EmitsRequested()
    {
        var gateway = new StubGateway { CanBegin = true };
        gateway.Snapshots[SecretA] = new SecretRotationSnapshot(
            SecretA, "db/app", TenantA, "postgres", "role=app", 1);
        var auditor = new StubAuditor();
        var workflows = new StubWorkflows();
        var sut = Build(gateway, auditor, workflows);

        var result = await sut.TriggerRotationAsync(
            SecretA, Operator, newPlaintext: "supplied-pw", generateLength: null,
            graceWindowSeconds: 60, ct: default);

        result.Accepted.Should().BeTrue();
        result.RotationCorrelationId.Should().StartWith("rot_");
        workflows.Dispatches.Should().HaveCount(1);
        workflows.Dispatches[0].WorkflowName.Should().Be("rotate-secret");
        workflows.Dispatches[0].Input["secretId"].Should().Be(SecretA.ToString());
        workflows.Dispatches[0].Input["rotationCorrelationId"].Should().Be(result.RotationCorrelationId);
        workflows.Dispatches[0].Input.Should().ContainKey("newPlaintext");

        var requested = auditor.Events.Single(e => e.EventType == RotationAuditEvents.Requested);
        requested.SecretId.Should().Be(SecretA);
        requested.TenantId.Should().Be(TenantA);
        // NEVER the plaintext on the audit event.
        requested.Data.Values
            .Where(v => v is string)
            .Cast<string>()
            .Should().NotContain(s => s.Contains("supplied-pw"));
        requested.Data["generated"].Should().Be(false);
    }

    [Test]
    public async Task Trigger_NoPlaintext_PassesGenerateLength()
    {
        var gateway = new StubGateway { CanBegin = true };
        var auditor = new StubAuditor();
        var workflows = new StubWorkflows();
        var sut = Build(gateway, auditor, workflows);

        var result = await sut.TriggerRotationAsync(
            SecretA, Guid.Empty, newPlaintext: null, generateLength: 48,
            graceWindowSeconds: 0, ct: default);

        result.Accepted.Should().BeTrue();
        workflows.Dispatches[0].Input.Should().NotContainKey("newPlaintext");
        workflows.Dispatches[0].Input["generateLength"].Should().Be(48);
        auditor.Events.Single(e => e.EventType == RotationAuditEvents.Requested)
            .Data["generated"].Should().Be(true);
    }

    [Test]
    public async Task Trigger_WhenInFlight_Rejects_EmitsRejected_NoDispatch()
    {
        var gateway = new StubGateway { CanBegin = false };
        var auditor = new StubAuditor();
        var workflows = new StubWorkflows();
        var sut = Build(gateway, auditor, workflows);

        var result = await sut.TriggerRotationAsync(
            SecretA, Operator, "pw", null, 0, default);

        result.Accepted.Should().BeFalse();
        result.Reason.Should().Be("rotation_in_progress");
        workflows.Dispatches.Should().BeEmpty();
        var rejected = auditor.Events.Single(e => e.EventType == RotationAuditEvents.Rejected);
        rejected.Detail.Should().Be("rotation_in_progress");
        auditor.Events.Should().NotContain(e => e.EventType == RotationAuditEvents.Requested);
    }

    [Test]
    public async Task Trigger_DispatchThrows_ReleasesGuard_AndPropagates()
    {
        var gateway = new StubGateway { CanBegin = true };
        var auditor = new StubAuditor();
        var workflows = new StubWorkflows { ThrowOnDispatch = new InvalidOperationException("engine down") };
        var sut = Build(gateway, auditor, workflows);

        var act = async () => await sut.TriggerRotationAsync(SecretA, Operator, "pw", null, 0, default);

        await act.Should().ThrowAsync<InvalidOperationException>();
        gateway.EndCalls.Should().Contain(SecretA);
        auditor.Events.Should().NotContain(e => e.EventType == RotationAuditEvents.Requested);
    }

    [Test]
    public async Task Trigger_EmptySecretId_Throws()
    {
        var sut = Build(new StubGateway(), new StubAuditor(), new StubWorkflows());
        await FluentActions
            .Awaiting(() => sut.TriggerRotationAsync(Guid.Empty, Operator, "pw", null, 0, default))
            .Should().ThrowAsync<ArgumentException>();
    }

    // ── Stubs ────────────────────────────────────────────────────────

    private sealed class StubGateway : ISecretRotationGateway
    {
        public bool CanBegin { get; set; } = true;
        public Dictionary<Guid, SecretRotationSnapshot> Snapshots { get; } = new();
        public List<Guid> EndCalls { get; } = new();

        public Task<SecretRotationSnapshot?> GetSnapshotAsync(Guid secretId, CancellationToken ct) =>
            Task.FromResult(Snapshots.TryGetValue(secretId, out var s) ? s : null);
        public Task<int> MintPendingVersionAsync(Guid s, string p, string c, Guid o, CancellationToken ct) => Task.FromResult(0);
        public Task DeleteVersionAsync(Guid s, int v, CancellationToken ct) => Task.CompletedTask;
        public Task ActivateVersionAsync(Guid s, int n, int p, CancellationToken ct) => Task.CompletedTask;
        public Task RevertActivationAsync(Guid s, int n, int p, CancellationToken ct) => Task.CompletedTask;
        public Task RetireVersionAsync(Guid s, int v, CancellationToken ct) => Task.CompletedTask;
        public Task<string?> GetVersionPlaintextAsync(Guid s, int v, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<bool> TryBeginRotationAsync(Guid secretId, string corr, CancellationToken ct) => Task.FromResult(CanBegin);
        public Task EndRotationAsync(Guid secretId, string corr, CancellationToken ct)
        {
            EndCalls.Add(secretId);
            return Task.CompletedTask;
        }
    }

    private sealed class StubAuditor : IRotationAuditEmitter
    {
        public ConcurrentBag<RotationAuditEvent> Events { get; } = new();
        public Task EmitAsync(RotationAuditEvent evt, CancellationToken ct) { Events.Add(evt); return Task.CompletedTask; }
    }

    private sealed class StubWorkflows : IElsaWorkflowService
    {
        public List<(string WorkflowName, Dictionary<string, object> Input)> Dispatches { get; } = new();
        public Exception? ThrowOnDispatch { get; set; }

        public Task<string> StartWorkflowAsync(string workflowName, Dictionary<string, object> input)
        {
            if (ThrowOnDispatch is not null) throw ThrowOnDispatch;
            Dispatches.Add((workflowName, input));
            return Task.FromResult(Guid.NewGuid().ToString());
        }

        public Task PauseWorkflowAsync(string instanceId) => Task.CompletedTask;
        public Task ResumeWorkflowAsync(string instanceId) => Task.CompletedTask;
        public Task CancelWorkflowAsync(string instanceId) => Task.CompletedTask;
        public Task<WorkflowStatus> GetWorkflowStatusAsync(string instanceId) => Task.FromResult(new WorkflowStatus());
        public Task SendSignalAsync(string instanceId, string signalName, object? payload = null) => Task.CompletedTask;
        public Task<MergeApprovalResumeResult> ResumeMergeApprovalAsync(int i, int p, string? t, string? r, string d, string? f, string? a) =>
            Task.FromResult(new MergeApprovalResumeResult(false, false, null));
        public Task<ApprovalGateLocation> LocateMergeApprovalGateAsync(int i, int p, string? t, string? r) =>
            Task.FromResult(new ApprovalGateLocation(false, null, null));
        public Task<ApprovalGateLocation> LocateDeploymentApprovalGateAsync(int i, string? t, string? r, string? sha) =>
            Task.FromResult(new ApprovalGateLocation(false, null, null));
        public Task<MergeApprovalResumeResult> ResumeDeploymentApprovalAsync(int i, string? t, string? r, string? sha, string d, string? f, string? a) =>
            Task.FromResult(new MergeApprovalResumeResult(false, false, null));
        public Task<MergeApprovalResumeResult> ResumeBlockerResolutionAsync(Guid sid, string k, string? l, bool res, string? pt, string? det, string? sr, string? resolver) =>
            Task.FromResult(new MergeApprovalResumeResult(false, false, null));
        public Task<MergeApprovalResumeResult> ResumeClarifyingQuestionsAsync(Guid sid, string? t, string answers, string? resolver) =>
            Task.FromResult(new MergeApprovalResumeResult(false, false, null));
        public Task<MergeApprovalResumeResult> ResumeDesignApprovalAsync(Guid sid, string? t, bool approved, string? feedback, string? reviewer) =>
            Task.FromResult(new MergeApprovalResumeResult(false, false, null));
        public Task<MergeApprovalResumeResult> ResumeDocumentDecisionAsync(Guid sid, string? t, string decisionJson, string? feedback, string? deciderId, string? deciderDisplay, string channel, string? rulesReference) =>
            Task.FromResult(new MergeApprovalResumeResult(false, false, null));
    }
}
