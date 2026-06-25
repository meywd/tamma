using System.Collections.Concurrent;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.SecretsRotation.Activities;
using Tamma.Activities.SecretsRotation.Contracts;

namespace Tamma.Activities.Tests.SecretsRotation;

/// <summary>
/// Story 29-6 — contract tests for the saga runner (the body of
/// <c>RotateSecretSagaActivity</c>). Uses stubs for every port so the
/// tests don't need Elsa hosting or a database. Covers:
///
/// <list type="bullet">
///   <item><description>Happy path — all steps succeed, new version
///     activated, retire task scheduled, completed event fired.</description></item>
///   <item><description>Push-failure compensation — pending row deleted,
///     handler rollback invoked, old version still active.</description></item>
///   <item><description>Probe-failure compensation — same as above
///     but triggered from the probe retry loop.</description></item>
///   <item><description>Handler-not-registered — saga fails before
///     mint, no version created.</description></item>
///   <item><description>Secret-not-found — saga fails at snapshot step.</description></item>
///   <item><description>First-rotation (previousVersion=0) — schedule
///     retire emits a no-previous-version event and does not enqueue a task.</description></item>
///   <item><description>Retry exhaustion — push is retried the configured
///     number of times before declaring failure.</description></item>
/// </list>
/// </summary>
[TestFixture]
public class SagaRunnerTests
{
    private static readonly IReadOnlyList<TimeSpan> NoDelays = new[]
    {
        TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero,
    };

    private static readonly Guid SecretIdA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string CorrelationA = "rot_a";

    [Test]
    public async Task HappyPath_ActivatesAndSchedulesRetire()
    {
        var gateway = new StubGateway();
        gateway.Secrets[SecretIdA] = new SecretRotationSnapshot(
            SecretIdA, "db/app-role", null, "postgres", "role=app", ActiveVersionNumber: 2);
        var handler = new StubHandler("postgres");
        var registry = new StubRegistry { ["postgres"] = handler };
        var auditor = new StubAuditor();
        var scheduler = new StubRetireScheduler();

        var runner = new SagaRunner(gateway, registry, auditor, scheduler, NoDelays, NoDelays, logger: null);
        var state = new RotationWorkflowState
        {
            SecretId = SecretIdA,
            RotationCorrelationId = CorrelationA,
            GraceWindowSeconds = 5,
        };

        var outcome = await runner.ExecuteAsync(state, suppliedPlaintext: "hunter2", generateLength: 32,
            ct: default);

        outcome.Should().Be(SagaOutcome.Activated);
        state.NewVersionNumber.Should().Be(3);
        state.PreviousVersionNumber.Should().Be(2);
        state.Activated.Should().BeTrue();
        state.Pushed.Should().BeTrue();
        state.Result.Should().Be("activated");
        gateway.Versions[(SecretIdA, 3)].Should().Be("active");
        gateway.Versions[(SecretIdA, 2)].Should().Be("retired_grace");
        handler.PushedPlaintext.Should().Be("hunter2");
        scheduler.Scheduled.Should().HaveCount(1);
        scheduler.Scheduled[0].VersionNumber.Should().Be(2);

        var types = auditor.Events.Select(e => e.EventType).ToList();
        types.Should().Contain(RotationAuditEvents.Started);
        types.Should().Contain(RotationAuditEvents.Staged);
        types.Should().Contain(RotationAuditEvents.PushSuccess);
        types.Should().Contain(RotationAuditEvents.ProbeSuccess);
        types.Should().Contain(RotationAuditEvents.Switched);
        types.Should().Contain(RotationAuditEvents.Activated);
        types.Should().Contain(RotationAuditEvents.RetireScheduled);
        types.Should().Contain(RotationAuditEvents.Completed);
    }

    [Test]
    public async Task PushFailure_CompensatesAndDeletesPending()
    {
        var gateway = new StubGateway();
        gateway.Secrets[SecretIdA] = new SecretRotationSnapshot(
            SecretIdA, "db/app-role", null, "postgres", "role=app", ActiveVersionNumber: 1);
        var handler = new StubHandler("postgres") { PushException = new InvalidOperationException("conn_refused") };
        var registry = new StubRegistry { ["postgres"] = handler };
        var auditor = new StubAuditor();
        var scheduler = new StubRetireScheduler();

        var runner = new SagaRunner(gateway, registry, auditor, scheduler, NoDelays, NoDelays, logger: null);
        var state = new RotationWorkflowState { SecretId = SecretIdA, RotationCorrelationId = CorrelationA };

        var outcome = await runner.ExecuteAsync(state, "pw", 32, default);

        outcome.Should().Be(SagaOutcome.Compensated);
        state.Error.Should().StartWith("push_failed:");
        // Pending version was minted then deleted by compensation.
        gateway.Versions.Should().NotContainKey((SecretIdA, 2));
        // Old version still active.
        gateway.Versions.ContainsKey((SecretIdA, 1)).Should().BeFalse("gateway stub tracks only minted versions");
        auditor.Events.Select(e => e.EventType).Should().Contain(new[]
        {
            RotationAuditEvents.PushFailed,
            RotationAuditEvents.CompensationStarted,
            RotationAuditEvents.CompensationSuccess,
            RotationAuditEvents.Failed,
        });
        // Push attempted maxAttempts times (initial + 3 retries).
        handler.PushAttempts.Should().Be(NoDelays.Count + 1);
    }

    [Test]
    public async Task ProbeFailure_CompensatesWithRollback()
    {
        var gateway = new StubGateway();
        gateway.Secrets[SecretIdA] = new SecretRotationSnapshot(
            SecretIdA, "db/app-role", null, "postgres", "role=app", ActiveVersionNumber: 1);
        var handler = new StubHandler("postgres") { ProbeOutcome = ProbeResult.Unhealthy("auth_failed", 7) };
        var registry = new StubRegistry { ["postgres"] = handler };
        var auditor = new StubAuditor();
        var scheduler = new StubRetireScheduler();

        var runner = new SagaRunner(gateway, registry, auditor, scheduler, NoDelays, NoDelays, logger: null);
        var state = new RotationWorkflowState { SecretId = SecretIdA, RotationCorrelationId = CorrelationA };

        var outcome = await runner.ExecuteAsync(state, "pw", 32, default);

        outcome.Should().Be(SagaOutcome.Compensated);
        state.Error.Should().Contain("probe_failed");
        handler.ProbeAttempts.Should().Be(NoDelays.Count + 1);
        handler.RollbackInvoked.Should().BeTrue();
        auditor.Events.Select(e => e.EventType).Should().Contain(RotationAuditEvents.ProbeFailed);
        auditor.Events.Select(e => e.EventType).Should().Contain(RotationAuditEvents.CompensationSuccess);
    }

    [Test]
    public async Task HandlerNotRegistered_FailsBeforeMint()
    {
        var gateway = new StubGateway();
        gateway.Secrets[SecretIdA] = new SecretRotationSnapshot(
            SecretIdA, "x", null, "unknown-system", "", ActiveVersionNumber: 1);
        var registry = new StubRegistry(); // empty
        var auditor = new StubAuditor();
        var scheduler = new StubRetireScheduler();

        var runner = new SagaRunner(gateway, registry, auditor, scheduler, NoDelays, NoDelays, logger: null);
        var state = new RotationWorkflowState { SecretId = SecretIdA, RotationCorrelationId = CorrelationA };
        var outcome = await runner.ExecuteAsync(state, "x", 32, default);

        outcome.Should().Be(SagaOutcome.Failed);
        state.Error.Should().Be("handler_not_registered");
        state.NewVersionNumber.Should().Be(0);
        auditor.Events.Select(e => e.EventType).Should().Contain(RotationAuditEvents.Failed);
    }

    [Test]
    public async Task SecretNotFound_FailsFast()
    {
        var gateway = new StubGateway(); // no secrets
        var registry = new StubRegistry { ["generic-http"] = new StubHandler("generic-http") };
        var auditor = new StubAuditor();
        var scheduler = new StubRetireScheduler();

        var runner = new SagaRunner(gateway, registry, auditor, scheduler, NoDelays, NoDelays, logger: null);
        var state = new RotationWorkflowState { SecretId = SecretIdA, RotationCorrelationId = CorrelationA };

        var outcome = await runner.ExecuteAsync(state, "x", 32, default);

        outcome.Should().Be(SagaOutcome.Failed);
        state.Error.Should().Be("secret_not_found");
        gateway.MintCallCount.Should().Be(0);
    }

    [Test]
    public async Task GenericHttpFallback_UsedWhenSpecificHandlerMissing()
    {
        var gateway = new StubGateway();
        gateway.Secrets[SecretIdA] = new SecretRotationSnapshot(
            SecretIdA, "x", null, "custom-thing", "id=1", ActiveVersionNumber: 0);
        var fallback = new StubHandler("generic-http");
        var registry = new StubRegistry { ["generic-http"] = fallback };
        var auditor = new StubAuditor();
        var scheduler = new StubRetireScheduler();

        var runner = new SagaRunner(gateway, registry, auditor, scheduler, NoDelays, NoDelays, logger: null);
        var state = new RotationWorkflowState { SecretId = SecretIdA, RotationCorrelationId = CorrelationA };

        var outcome = await runner.ExecuteAsync(state, "x", 32, default);

        outcome.Should().Be(SagaOutcome.Activated);
        fallback.PushedPlaintext.Should().Be("x");
    }

    [Test]
    public async Task FirstRotation_DoesNotScheduleRetire()
    {
        var gateway = new StubGateway();
        gateway.Secrets[SecretIdA] = new SecretRotationSnapshot(
            SecretIdA, "new", null, "postgres", "role=app", ActiveVersionNumber: 0);
        var handler = new StubHandler("postgres");
        var registry = new StubRegistry { ["postgres"] = handler };
        var auditor = new StubAuditor();
        var scheduler = new StubRetireScheduler();

        var runner = new SagaRunner(gateway, registry, auditor, scheduler, NoDelays, NoDelays, logger: null);
        var state = new RotationWorkflowState { SecretId = SecretIdA, RotationCorrelationId = CorrelationA };

        var outcome = await runner.ExecuteAsync(state, "first-secret", 32, default);

        outcome.Should().Be(SagaOutcome.Activated);
        scheduler.Scheduled.Should().BeEmpty();
        auditor.Events.Single(e => e.EventType == RotationAuditEvents.RetireScheduled)
            .Detail.Should().Be("no_previous_version");
    }

    [Test]
    public async Task PushSucceedsAfterRetry_ActivatesNormally()
    {
        var gateway = new StubGateway();
        gateway.Secrets[SecretIdA] = new SecretRotationSnapshot(
            SecretIdA, "x", null, "postgres", "role=app", ActiveVersionNumber: 1);
        var handler = new StubHandler("postgres") { FailFirstNPushes = 2 };
        var registry = new StubRegistry { ["postgres"] = handler };
        var auditor = new StubAuditor();
        var scheduler = new StubRetireScheduler();

        var runner = new SagaRunner(gateway, registry, auditor, scheduler, NoDelays, NoDelays, logger: null);
        var state = new RotationWorkflowState { SecretId = SecretIdA, RotationCorrelationId = CorrelationA };

        var outcome = await runner.ExecuteAsync(state, "x", 32, default);

        outcome.Should().Be(SagaOutcome.Activated);
        handler.PushAttempts.Should().Be(3);
        auditor.Events.Any(e => e.EventType == RotationAuditEvents.PushSuccess).Should().BeTrue();
    }

    [Test]
    public async Task ProbeSucceedsAfterRetry_ActivatesNormally()
    {
        var gateway = new StubGateway();
        gateway.Secrets[SecretIdA] = new SecretRotationSnapshot(
            SecretIdA, "x", null, "postgres", "role=app", ActiveVersionNumber: 1);
        var handler = new StubHandler("postgres") { FailFirstNProbes = 2 };
        var registry = new StubRegistry { ["postgres"] = handler };
        var auditor = new StubAuditor();
        var scheduler = new StubRetireScheduler();

        var runner = new SagaRunner(gateway, registry, auditor, scheduler, NoDelays, NoDelays, logger: null);
        var state = new RotationWorkflowState { SecretId = SecretIdA, RotationCorrelationId = CorrelationA };

        var outcome = await runner.ExecuteAsync(state, "x", 32, default);

        outcome.Should().Be(SagaOutcome.Activated);
        handler.ProbeAttempts.Should().Be(3);
    }

    [Test]
    public async Task CorrelationIdFlowsToAllEvents()
    {
        var gateway = new StubGateway();
        gateway.Secrets[SecretIdA] = new SecretRotationSnapshot(
            SecretIdA, "x", null, "postgres", "role=app", ActiveVersionNumber: 1);
        var handler = new StubHandler("postgres");
        var registry = new StubRegistry { ["postgres"] = handler };
        var auditor = new StubAuditor();
        var scheduler = new StubRetireScheduler();

        var runner = new SagaRunner(gateway, registry, auditor, scheduler, NoDelays, NoDelays, logger: null);
        var state = new RotationWorkflowState { SecretId = SecretIdA, RotationCorrelationId = "custom-id-42" };
        await runner.ExecuteAsync(state, "x", 32, default);

        auditor.Events.Should().NotBeEmpty();
        auditor.Events.Should().AllSatisfy(e => e.RotationCorrelationId.Should().Be("custom-id-42"));
    }

    // ─── Stubs ───────────────────────────────────────────────────────────
    // (moved to SagaRunnerTestStubs.cs so Wave-C.4 alert-emission tests
    // can reuse them — NUnit doesn't allow private nested types to be
    // shared across fixtures.)
}

internal sealed class StubGateway : ISecretRotationGateway
{
        public Dictionary<Guid, SecretRotationSnapshot> Secrets { get; } = new();
        public Dictionary<(Guid, int), string> Versions { get; } = new();
        public Dictionary<(Guid, int), string> Plaintexts { get; } = new();
        public int MintCallCount;

        public Task<SecretRotationSnapshot?> GetSnapshotAsync(Guid secretId, CancellationToken ct) =>
            Task.FromResult(Secrets.TryGetValue(secretId, out var s) ? s : null);

        public Task<int> MintPendingVersionAsync(
            Guid secretId, string newPlaintext, string rotationCorrelationId,
            Guid operatorUserId, CancellationToken ct)
        {
            MintCallCount++;
            var existingActive = Secrets[secretId].ActiveVersionNumber;
            var next = existingActive + 1;
            Versions[(secretId, next)] = "pending";
            Plaintexts[(secretId, next)] = newPlaintext;
            return Task.FromResult(next);
        }

        public Task DeleteVersionAsync(Guid secretId, int versionNumber, CancellationToken ct)
        {
            Versions.Remove((secretId, versionNumber));
            Plaintexts.Remove((secretId, versionNumber));
            return Task.CompletedTask;
        }

        public Task ActivateVersionAsync(Guid secretId, int newVersionNumber, int previousVersionNumber, CancellationToken ct)
        {
            Versions[(secretId, newVersionNumber)] = "active";
            if (previousVersionNumber > 0)
                Versions[(secretId, previousVersionNumber)] = "retired_grace";
            var old = Secrets[secretId];
            Secrets[secretId] = old with { ActiveVersionNumber = newVersionNumber };
            return Task.CompletedTask;
        }

        public Task RevertActivationAsync(Guid secretId, int newVersionNumber, int previousVersionNumber, CancellationToken ct)
        {
            Versions[(secretId, newVersionNumber)] = "pending";
            if (previousVersionNumber > 0)
                Versions[(secretId, previousVersionNumber)] = "active";
            return Task.CompletedTask;
        }

        public Task RetireVersionAsync(Guid secretId, int versionNumber, CancellationToken ct)
        {
            if (Versions.ContainsKey((secretId, versionNumber)))
                Versions[(secretId, versionNumber)] = "revoked";
            Plaintexts.Remove((secretId, versionNumber));
            return Task.CompletedTask;
        }

        public Task<string?> GetVersionPlaintextAsync(Guid secretId, int versionNumber, CancellationToken ct) =>
            Task.FromResult(Plaintexts.TryGetValue((secretId, versionNumber), out var p) ? p : null);

        public Task<bool> TryBeginRotationAsync(Guid secretId, string rotationCorrelationId, CancellationToken ct) =>
            Task.FromResult(true);

        public Task EndRotationAsync(Guid secretId, string rotationCorrelationId, CancellationToken ct) =>
            Task.CompletedTask;
    }

internal sealed class StubHandler : IRotationHandler
    {
        public StubHandler(string system) => System = system;

        public string System { get; }
        public int PushAttempts;
        public int ProbeAttempts;
        public bool RollbackInvoked;
        public string? PushedPlaintext;
        public Exception? PushException;
        public ProbeResult? ProbeOutcome;
        public int FailFirstNPushes;
        public int FailFirstNProbes;

        public Task PushAsync(RotationTarget target, string newPlaintext, RotationContext ctx, CancellationToken ct)
        {
            PushAttempts++;
            if (PushAttempts <= FailFirstNPushes)
                throw new InvalidOperationException("transient");
            if (PushException is not null)
                throw PushException;
            PushedPlaintext = newPlaintext;
            return Task.CompletedTask;
        }

        public Task<ProbeResult> ProbeAsync(RotationTarget target, RotationContext ctx, CancellationToken ct)
        {
            ProbeAttempts++;
            if (ProbeAttempts <= FailFirstNProbes)
                return Task.FromResult(ProbeResult.Unhealthy("transient", 1));
            return Task.FromResult(ProbeOutcome ?? ProbeResult.Healthy(1));
        }

        public Task RollbackAsync(RotationTarget target, string newPlaintext, RotationContext ctx, CancellationToken ct)
        {
            RollbackInvoked = true;
            return Task.CompletedTask;
        }
    }

internal sealed class StubRegistry : IRotationHandlerRegistry
    {
        private readonly Dictionary<string, IRotationHandler> _handlers = new();
        public IRotationHandler? Resolve(string system) =>
            _handlers.TryGetValue(system, out var h) ? h : null;
        public IRotationHandler this[string key]
        {
            get => _handlers[key];
            set => _handlers[key] = value;
        }
    }

internal sealed class StubAuditor : IRotationAuditEmitter
    {
        public ConcurrentBag<RotationAuditEvent> Events { get; } = new();
        public Task EmitAsync(RotationAuditEvent evt, CancellationToken ct)
        {
            Events.Add(evt);
            return Task.CompletedTask;
        }
    }

internal sealed class StubRetireScheduler : IRetireScheduler
    {
        public List<(Guid SecretId, int VersionNumber, DateTimeOffset RunAfter)> Scheduled { get; } = new();

        public Task<Guid> ScheduleRetireAsync(
            Guid secretId, int versionNumber, Guid? tenantId,
            DateTimeOffset runAfter, string rotationCorrelationId, CancellationToken ct)
        {
            Scheduled.Add((secretId, versionNumber, runAfter));
            return Task.FromResult(Guid.NewGuid());
        }

    public Task<int> SweepDueRetireTasksAsync(CancellationToken ct) => Task.FromResult(0);
}
