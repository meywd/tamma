using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Activities.SecretsRotation.Contracts;
using Tamma.Api.Services.PlatformTasks;
using Tamma.Api.Services.Secrets.Rotation;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Secrets.Rotation;

/// <summary>
/// Story 29-6 AC8 — tests for the <c>RETIRE_SECRET_VERSION</c> platform
/// task handler. The handler is the AC8-specified
/// <see cref="PlatformTaskWorker"/> route that finally drains the retire
/// tail (closing the type-blind dead-letter hazard). Failure semantics
/// are delegated to the worker via the handler's return / throw:
///
/// <list type="bullet">
///   <item><description>malformed payload ⇒ <see cref="PlatformTaskTerminalException"/>
///     (worker dead-letters, no retry budget burned).</description></item>
///   <item><description><c>runAfter &gt; now</c> ⇒ ordinary throw (worker
///     <c>FailAsync</c> → re-queue, NOT dead-letter).</description></item>
///   <item><description>retire throw ⇒ ordinary throw (worker retry budget).</description></item>
///   <item><description>happy path ⇒ returns, emits <c>SECRET.VERSION.RETIRED</c>,
///     invokes the handler's <c>RevokeOldAsync</c>, idempotent on
///     already-revoked.</description></item>
/// </list>
/// </summary>
[TestFixture]
public sealed class RetireSecretVersionTaskHandlerTests
{
    private static readonly Guid SecretA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static RetireSecretVersionTaskHandler BuildHandler(
        StubGateway gateway, StubRegistry registry, StubAuditor auditor,
        StubQueueRepo? repo = null)
    {
        var executor = new RetireTaskExecutor(
            gateway, registry, auditor,
            NullLogger<RetireTaskExecutor>.Instance);
        return new RetireSecretVersionTaskHandler(
            executor, repo ?? new StubQueueRepo(),
            NullLogger<RetireSecretVersionTaskHandler>.Instance);
    }

    private static PlatformQueuedTask BuildTask(RetireTaskPayload payload) => new()
    {
        Id = Guid.NewGuid(),
        Type = RetireScheduler.TaskType,
        Payload = JsonSerializer.Serialize(payload),
    };

    [Test]
    public void TaskType_IsRetireSecretVersion()
    {
        var handler = BuildHandler(new StubGateway(), new StubRegistry(), new StubAuditor());
        handler.TaskType.Should().Be("RETIRE_SECRET_VERSION");
        handler.TaskType.Should().Be(RetireScheduler.TaskType);
    }

    [Test]
    public async Task DueTask_Retires_RevokesOld_EmitsRetiredEvent()
    {
        var gateway = new StubGateway();
        gateway.Snapshots[SecretA] = new SecretRotationSnapshot(
            SecretA, "db/app-role", null, "postgres", "role=app", ActiveVersionNumber: 3);
        gateway.Plaintexts[(SecretA, 2)] = "old-pw";
        gateway.Versions[(SecretA, 2)] = "retired_grace";
        var handler = new StubHandler("postgres");
        var registry = new StubRegistry { ["postgres"] = handler };
        var auditor = new StubAuditor();
        var sut = BuildHandler(gateway, registry, auditor);

        await sut.HandleAsync(BuildTask(new RetireTaskPayload
        {
            SecretId = SecretA,
            VersionNumber = 2,
            RunAfter = DateTimeOffset.UtcNow.AddMinutes(-1),
            RotationCorrelationId = "rot_x",
        }), default);

        gateway.Versions[(SecretA, 2)].Should().Be("revoked");
        handler.RevokedOldPlaintext.Should().Be("old-pw");
        auditor.Events.Select(e => e.EventType)
            .Should().Contain(RotationAuditEvents.VersionRetired);
    }

    [Test]
    public async Task NotYetDue_Defers_NotDeadLettered_NoRetryBudgetBurn()
    {
        var gateway = new StubGateway();
        gateway.Snapshots[SecretA] = new SecretRotationSnapshot(
            SecretA, "db/app-role", null, "postgres", "role=app", ActiveVersionNumber: 3);
        var repo = new StubQueueRepo();
        var sut = BuildHandler(gateway, new StubRegistry(), new StubAuditor(), repo);

        var runAfter = DateTimeOffset.UtcNow.AddMinutes(30);
        var task = BuildTask(new RetireTaskPayload
        {
            SecretId = SecretA,
            VersionNumber = 2,
            RunAfter = runAfter,
            RotationCorrelationId = "rot_x",
        });

        var act = async () => await sut.HandleAsync(task, default);

        // Review fix: a not-yet-due retire is DEFERRED, not failed. The
        // handler throws PlatformTaskDeferredException (worker treats it as a
        // no-op: no CompleteAsync clobber, no FailAsync retry-budget burn /
        // dead-letter) AND it is explicitly NOT a terminal exception.
        var thrown = await Record(act);
        thrown.Should().BeOfType<PlatformTaskDeferredException>();
        thrown.Should().NotBeOfType<PlatformTaskTerminalException>();
        // The row was deferred until runAfter (VisibleAt pushed forward),
        // with the retry count untouched.
        repo.Deferred.Should().ContainSingle();
        repo.Deferred[0].Id.Should().Be(task.Id);
        repo.Deferred[0].VisibleAt.Should().BeCloseTo(runAfter.UtcDateTime, TimeSpan.FromSeconds(1));
        // No store mutation, no event.
        gateway.RetireCalls.Should().BeEmpty();
    }

    [Test]
    public async Task MalformedPayload_Throws_Terminal_DeadLetters()
    {
        var sut = BuildHandler(new StubGateway(), new StubRegistry(), new StubAuditor());
        var task = new PlatformQueuedTask
        {
            Id = Guid.NewGuid(),
            Type = RetireScheduler.TaskType,
            Payload = "not json {{{",
        };

        var act = async () => await sut.HandleAsync(task, default);

        var ex = await act.Should().ThrowAsync<PlatformTaskTerminalException>();
        ex.Which.Message.Should().Contain("malformed_payload");
    }

    [Test]
    public async Task EmptyPayload_Throws_Terminal()
    {
        var sut = BuildHandler(new StubGateway(), new StubRegistry(), new StubAuditor());
        var task = new PlatformQueuedTask
        {
            Id = Guid.NewGuid(),
            Type = RetireScheduler.TaskType,
            Payload = string.Empty,
        };

        await FluentActions.Awaiting(() => sut.HandleAsync(task, default))
            .Should().ThrowAsync<PlatformTaskTerminalException>();
    }

    [Test]
    public async Task RetireThrow_Bubbles_AsRetryable()
    {
        var gateway = new StubGateway { RetireException = new InvalidOperationException("db down") };
        gateway.Snapshots[SecretA] = new SecretRotationSnapshot(
            SecretA, "db/app-role", null, "postgres", "role=app", ActiveVersionNumber: 3);
        var sut = BuildHandler(gateway, new StubRegistry(), new StubAuditor());

        var act = async () => await sut.HandleAsync(BuildTask(new RetireTaskPayload
        {
            SecretId = SecretA,
            VersionNumber = 2,
            RunAfter = DateTimeOffset.UtcNow.AddMinutes(-1),
            RotationCorrelationId = "rot_x",
        }), default);

        var thrown = await Record(act);
        thrown.Should().NotBeNull();
        thrown.Should().NotBeOfType<PlatformTaskTerminalException>();
    }

    [Test]
    public async Task AlreadyRevoked_IsNoOp_StillEmitsAndCompletes()
    {
        var gateway = new StubGateway();
        gateway.Snapshots[SecretA] = new SecretRotationSnapshot(
            SecretA, "db/app-role", null, "postgres", "role=app", ActiveVersionNumber: 3);
        gateway.Versions[(SecretA, 2)] = "revoked"; // already terminal
        var sut = BuildHandler(gateway, new StubRegistry(), new StubAuditor());

        // No throw — handler returns so the worker marks completed.
        await sut.HandleAsync(BuildTask(new RetireTaskPayload
        {
            SecretId = SecretA,
            VersionNumber = 2,
            RunAfter = DateTimeOffset.UtcNow.AddMinutes(-1),
            RotationCorrelationId = "rot_x",
        }), default);

        gateway.Versions[(SecretA, 2)].Should().Be("revoked");
    }

    private static async Task<Exception?> Record(Func<Task> act)
    {
        try { await act(); return null; }
        catch (Exception ex) { return ex; }
    }

    // ─────────────────────────────────────────────────────────────────
    // Stubs

    private sealed class StubGateway : ISecretRotationGateway
    {
        public Dictionary<Guid, SecretRotationSnapshot> Snapshots { get; } = new();
        public Dictionary<(Guid, int), string> Versions { get; } = new();
        public Dictionary<(Guid, int), string> Plaintexts { get; } = new();
        public List<(Guid, int)> RetireCalls { get; } = new();
        public Exception? RetireException { get; set; }

        public Task<SecretRotationSnapshot?> GetSnapshotAsync(Guid secretId, CancellationToken ct) =>
            Task.FromResult(Snapshots.TryGetValue(secretId, out var s) ? s : null);

        public Task<int> MintPendingVersionAsync(Guid secretId, string newPlaintext, string rotationCorrelationId, Guid operatorUserId, CancellationToken ct) =>
            Task.FromResult(0);

        public Task DeleteVersionAsync(Guid secretId, int versionNumber, CancellationToken ct) => Task.CompletedTask;

        public Task ActivateVersionAsync(Guid secretId, int newVersionNumber, int previousVersionNumber, CancellationToken ct) => Task.CompletedTask;

        public Task RevertActivationAsync(Guid secretId, int newVersionNumber, int previousVersionNumber, CancellationToken ct) => Task.CompletedTask;

        public Task RetireVersionAsync(Guid secretId, int versionNumber, CancellationToken ct)
        {
            if (RetireException is not null) throw RetireException;
            RetireCalls.Add((secretId, versionNumber));
            if (Versions.TryGetValue((secretId, versionNumber), out var status) && status == "revoked")
                return Task.CompletedTask; // idempotent
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

    private sealed class StubHandler : IRotationHandler
    {
        public StubHandler(string system) => System = system;
        public string System { get; }
        public string? RevokedOldPlaintext;
        public Task PushAsync(RotationTarget t, string p, RotationContext c, CancellationToken ct) => Task.CompletedTask;
        public Task<ProbeResult> ProbeAsync(RotationTarget t, RotationContext c, CancellationToken ct) => Task.FromResult(ProbeResult.Healthy(1));
        public Task RollbackAsync(RotationTarget t, string p, RotationContext c, CancellationToken ct) => Task.CompletedTask;
        public Task RevokeOldAsync(RotationTarget t, string oldPlaintext, RotationContext c, CancellationToken ct)
        {
            RevokedOldPlaintext = oldPlaintext;
            return Task.CompletedTask;
        }
    }

    private sealed class StubRegistry : IRotationHandlerRegistry
    {
        private readonly Dictionary<string, IRotationHandler> _h = new();
        public IRotationHandler? Resolve(string system) => _h.TryGetValue(system, out var h) ? h : null;
        public IRotationHandler this[string key] { get => _h[key]; set => _h[key] = value; }
    }

    private sealed class StubAuditor : IRotationAuditEmitter
    {
        public ConcurrentBag<RotationAuditEvent> Events { get; } = new();
        public Task EmitAsync(RotationAuditEvent evt, CancellationToken ct) { Events.Add(evt); return Task.CompletedTask; }
    }

    /// <summary>
    /// Minimal queue-repo stub: records DeferAsync calls (the only method
    /// the handler invokes) and no-ops the rest.
    /// </summary>
    private sealed class StubQueueRepo : IPlatformQueuedTaskRepository
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
}
