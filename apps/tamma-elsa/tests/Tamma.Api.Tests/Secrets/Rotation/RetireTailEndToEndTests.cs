using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Tamma.Activities.SecretsRotation.Contracts;
using Tamma.Api.Services.PlatformTasks;
using Tamma.Api.Services.Secrets.Rotation;
using Tamma.Data;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Secrets.Rotation;

/// <summary>
/// Story 29-6 AC7/AC8 (audit gap #10) — durable end-to-end test of the
/// RETIRE TAIL. Proves the "old retired" leg that was previously
/// untestable end-to-end (the sweeper / handler was unwired):
///
/// <list type="number">
///   <item><description>A rotation has activated, leaving a previous
///     version in <c>RetiredGrace</c> and a due
///     <c>RETIRE_SECRET_VERSION</c> task enqueued (via the real
///     <see cref="RetireScheduler.ScheduleRetireAsync"/>).</description></item>
///   <item><description>The real <see cref="PlatformTaskWorker"/> reserves
///     the row and routes it to the real
///     <see cref="RetireSecretVersionTaskHandler"/>.</description></item>
///   <item><description>Assert the old version → <c>Revoked</c>,
///     <c>SECRET.VERSION.RETIRED</c> emitted, the queue row
///     <c>completed</c>, and the handler's <c>RevokeOldAsync</c> ran.</description></item>
/// </list>
///
/// <para>Also pins the grace-window edge: a NOT-yet-due retire row is
/// re-queued (retryable, NOT dead-lettered) so the secret keeps its old
/// credential until the window expires.</para>
/// </summary>
[TestFixture]
public sealed class RetireTailEndToEndTests
{
    private static readonly Guid SecretA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private string _dbName = null!;
    private StubGateway _gateway = null!;
    private StubRegistry _registry = null!;
    private StubAuditor _auditor = null!;

    [SetUp]
    public void SetUp()
    {
        _dbName = $"retire-e2e-{Guid.NewGuid():N}";
        _gateway = new StubGateway();
        _registry = new StubRegistry();
        _auditor = new StubAuditor();
    }

    /// <summary>
    /// Build a self-contained SP wiring the control-plane queue repo, the
    /// rotation ports (stubbed gateway/registry/auditor + real executor),
    /// and the real RetireSecretVersionTaskHandler in the platform-task
    /// registry — so the real PlatformTaskWorker drives the real handler.
    /// </summary>
    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ControlPlaneDbContext>(o => o.UseInMemoryDatabase(_dbName));
        services.AddScoped<IPlatformQueuedTaskRepository, PlatformQueuedTaskRepository>();

        services.AddSingleton<ISecretRotationGateway>(_gateway);
        services.AddSingleton<IRotationHandlerRegistry>(_registry);
        services.AddSingleton<IRotationAuditEmitter>(_auditor);
        services.AddScoped<IRetireTaskExecutor, RetireTaskExecutor>();
        services.AddScoped<IRetireScheduler, RetireScheduler>();

        // The retire handler in the platform-task registry.
        services.AddScoped<IPlatformTaskHandler, RetireSecretVersionTaskHandler>();
        services.AddScoped<IPlatformTaskHandlerRegistry, PlatformTaskHandlerRegistry>();

        return services.BuildServiceProvider();
    }

    private static PlatformTaskWorker NewWorker(IServiceProvider sp) =>
        new(sp,
            Options.Create(new PlatformTaskWorkerOptions { RunOnStartup = false, MaxRetries = 5 }),
            TimeProvider.System,
            NullLogger<PlatformTaskWorker>.Instance);

    [Test]
    public async Task ActivatedThenDrain_OldVersionRevoked_RetiredEventEmitted()
    {
        // Arrange: a secret with v3 active, v2 in retired_grace + handler.
        _gateway.Snapshots[SecretA] = new SecretRotationSnapshot(
            SecretA, "db/app-role", null, "postgres", "role=app", ActiveVersionNumber: 3);
        _gateway.Versions[(SecretA, 2)] = "retired_grace";
        _gateway.Plaintexts[(SecretA, 2)] = "old-pw";
        var rotationHandler = new StubHandler("postgres");
        _registry["postgres"] = rotationHandler;

        var sp = BuildProvider();

        // Enqueue a DUE retire task via the real scheduler.
        Guid taskId;
        await using (var scope = sp.CreateAsyncScope())
        {
            var scheduler = scope.ServiceProvider.GetRequiredService<IRetireScheduler>();
            taskId = await scheduler.ScheduleRetireAsync(
                SecretA, versionNumber: 2, tenantId: null,
                runAfter: DateTimeOffset.UtcNow.AddMinutes(-1),
                rotationCorrelationId: "rot_e2e", ct: default);
        }

        // Act: drive the real worker once → routes to the retire handler.
        var worker = NewWorker(sp);
        var processed = await worker.ProcessOnceAsync(default);

        // Assert.
        processed.Should().BeTrue();
        _gateway.Versions[(SecretA, 2)].Should().Be("revoked");
        rotationHandler.RevokedOldPlaintext.Should().Be("old-pw");
        _auditor.Events.Select(e => e.EventType)
            .Should().Contain(RotationAuditEvents.VersionRetired);

        await using var verifyScope = sp.CreateAsyncScope();
        var repo = verifyScope.ServiceProvider.GetRequiredService<IPlatformQueuedTaskRepository>();
        var row = await repo.GetAsync(taskId);
        row!.Status.Should().Be("completed");

        sp.Dispose();
    }

    [Test]
    public async Task NotYetDue_OverManyTicks_NeverReservedNeverDeadLettered()
    {
        // Review fix (lost-retire bug): a freshly-scheduled retire (default
        // grace 900s) must NOT be dead-lettered before it is due. The OLD
        // code re-delivered the row every ~5s poll and hit MaxRetries (5) in
        // ~25s → dead_letter → the old credential never reached Revoked. The
        // single-tick test gave false confidence; drive MANY ticks (more than
        // MaxRetries=5) and assert the row stays pending the whole time.
        _gateway.Snapshots[SecretA] = new SecretRotationSnapshot(
            SecretA, "db/app-role", null, "postgres", "role=app", ActiveVersionNumber: 3);
        _gateway.Versions[(SecretA, 2)] = "retired_grace";
        _gateway.Plaintexts[(SecretA, 2)] = "old-pw";

        var sp = BuildProvider();

        Guid taskId;
        await using (var scope = sp.CreateAsyncScope())
        {
            var scheduler = scope.ServiceProvider.GetRequiredService<IRetireScheduler>();
            taskId = await scheduler.ScheduleRetireAsync(
                SecretA, 2, null,
                runAfter: DateTimeOffset.UtcNow.AddHours(1),    // not due
                rotationCorrelationId: "rot_future", ct: default);
        }

        var worker = NewWorker(sp);

        // Far more ticks than MaxRetries (5) — the old code dead-lettered by
        // tick 5. With VisibleAt = runAfter the row is simply NOT reserved,
        // so every tick observes an empty queue (ProcessOnceAsync == false).
        for (var tick = 0; tick < 12; tick++)
        {
            var processed = await worker.ProcessOnceAsync(default);
            processed.Should().BeFalse(
                "a not-yet-due retire row must not be reserved (VisibleAt guard)");

            await using var checkScope = sp.CreateAsyncScope();
            var checkRepo = checkScope.ServiceProvider.GetRequiredService<IPlatformQueuedTaskRepository>();
            var current = await checkRepo.GetAsync(taskId);
            current!.Status.Should().Be("pending", $"tick {tick}: row stays pending until due");
            current.Status.Should().NotBe("dead_letter");
            current.RetryCount.Should().Be(0, $"tick {tick}: a deferred row never burns the retry budget");
        }

        // Old version untouched; row still pending, never dead-lettered.
        _gateway.Versions[(SecretA, 2)].Should().Be("retired_grace");

        sp.Dispose();
    }

    [Test]
    public async Task NotYetDue_BecomesDue_ThenDrains()
    {
        // The complement: once VisibleAt elapses, the SAME row is reserved
        // and drained normally (proves VisibleAt gates, not blocks forever).
        _gateway.Snapshots[SecretA] = new SecretRotationSnapshot(
            SecretA, "db/app-role", null, "postgres", "role=app", ActiveVersionNumber: 3);
        _gateway.Versions[(SecretA, 2)] = "retired_grace";
        _gateway.Plaintexts[(SecretA, 2)] = "old-pw";
        _registry["postgres"] = new StubHandler("postgres");

        var sp = BuildProvider();

        Guid taskId;
        await using (var scope = sp.CreateAsyncScope())
        {
            var scheduler = scope.ServiceProvider.GetRequiredService<IRetireScheduler>();
            // Already-due window so the very first tick reserves + drains it.
            taskId = await scheduler.ScheduleRetireAsync(
                SecretA, 2, null,
                runAfter: DateTimeOffset.UtcNow.AddSeconds(-1),
                rotationCorrelationId: "rot_due", ct: default);
        }

        var worker = NewWorker(sp);
        var processed = await worker.ProcessOnceAsync(default);

        processed.Should().BeTrue();
        _gateway.Versions[(SecretA, 2)].Should().Be("revoked");
        await using var verifyScope = sp.CreateAsyncScope();
        var repo = verifyScope.ServiceProvider.GetRequiredService<IPlatformQueuedTaskRepository>();
        var row = await repo.GetAsync(taskId);
        row!.Status.Should().Be("completed");

        sp.Dispose();
    }

    [Test]
    public async Task Sweeper_DrainsDue_LeavesNotDuePending()
    {
        // The ACTIVE drainer (RetireSweepHostedService calls this) must
        // drain a DUE row and leave a NOT-DUE row pending — proving retires
        // drain even with PlatformTaskWorker:RunOnStartup=false.
        _gateway.Snapshots[SecretA] = new SecretRotationSnapshot(
            SecretA, "db/app-role", null, "postgres", "role=app", ActiveVersionNumber: 3);
        _gateway.Versions[(SecretA, 2)] = "retired_grace";
        _gateway.Plaintexts[(SecretA, 2)] = "old-pw";
        _gateway.Versions[(SecretA, 1)] = "retired_grace";
        _gateway.Plaintexts[(SecretA, 1)] = "older-pw";
        _registry["postgres"] = new StubHandler("postgres");

        var sp = BuildProvider();

        Guid dueId, notDueId;
        await using (var scope = sp.CreateAsyncScope())
        {
            var scheduler = scope.ServiceProvider.GetRequiredService<IRetireScheduler>();
            dueId = await scheduler.ScheduleRetireAsync(
                SecretA, 2, null, DateTimeOffset.UtcNow.AddSeconds(-1), "rot_due", default);
            notDueId = await scheduler.ScheduleRetireAsync(
                SecretA, 1, null, DateTimeOffset.UtcNow.AddHours(1), "rot_future", default);
        }

        int drained;
        await using (var scope = sp.CreateAsyncScope())
        {
            var scheduler = scope.ServiceProvider.GetRequiredService<IRetireScheduler>();
            drained = await scheduler.SweepDueRetireTasksAsync(default);
        }

        drained.Should().Be(1, "only the due row is reservable (VisibleAt guard)");
        _gateway.Versions[(SecretA, 2)].Should().Be("revoked");
        _gateway.Versions[(SecretA, 1)].Should().Be("retired_grace", "not-due row untouched");

        await using var verify = sp.CreateAsyncScope();
        var repo = verify.ServiceProvider.GetRequiredService<IPlatformQueuedTaskRepository>();
        (await repo.GetAsync(dueId))!.Status.Should().Be("completed");
        var notDue = await repo.GetAsync(notDueId);
        notDue!.Status.Should().Be("pending");
        notDue.RetryCount.Should().Be(0);

        sp.Dispose();
    }

    // ── Stubs ────────────────────────────────────────────────────────

    private sealed class StubGateway : ISecretRotationGateway
    {
        public Dictionary<Guid, SecretRotationSnapshot> Snapshots { get; } = new();
        public Dictionary<(Guid, int), string> Versions { get; } = new();
        public Dictionary<(Guid, int), string> Plaintexts { get; } = new();

        public Task<SecretRotationSnapshot?> GetSnapshotAsync(Guid secretId, CancellationToken ct) =>
            Task.FromResult(Snapshots.TryGetValue(secretId, out var s) ? s : null);
        public Task<int> MintPendingVersionAsync(Guid s, string p, string c, Guid o, CancellationToken ct) => Task.FromResult(0);
        public Task DeleteVersionAsync(Guid s, int v, CancellationToken ct) => Task.CompletedTask;
        public Task ActivateVersionAsync(Guid s, int n, int p, CancellationToken ct) => Task.CompletedTask;
        public Task RevertActivationAsync(Guid s, int n, int p, CancellationToken ct) => Task.CompletedTask;
        public Task RetireVersionAsync(Guid secretId, int versionNumber, CancellationToken ct)
        {
            if (Versions.TryGetValue((secretId, versionNumber), out var status) && status == "revoked")
                return Task.CompletedTask;
            Versions[(secretId, versionNumber)] = "revoked";
            Plaintexts.Remove((secretId, versionNumber));
            return Task.CompletedTask;
        }
        public Task<string?> GetVersionPlaintextAsync(Guid secretId, int versionNumber, CancellationToken ct) =>
            Task.FromResult(Plaintexts.TryGetValue((secretId, versionNumber), out var p) ? p : null);
        public Task<bool> TryBeginRotationAsync(Guid s, string c, CancellationToken ct) => Task.FromResult(true);
        public Task EndRotationAsync(Guid s, string c, CancellationToken ct) => Task.CompletedTask;
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
}
