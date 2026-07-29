using System.Text.Json;
using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Requests;
using Elsa.Workflows.Runtime.Responses;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Tamma.Activities.Scheduling;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Scheduling;

/// <summary>
/// Story 41-30 — behavioural coverage for
/// <see cref="TenantScheduledTriggerService"/> via the
/// <c>InvokeTickForTestsAsync</c> seam (the
/// <c>HourlyAnalyticsRollupScheduler</c> test shape): a fixed
/// <see cref="TimeProvider"/>, an in-memory
/// <see cref="IScheduledTriggerRepository"/> (whose claim map mimics the
/// <c>ON CONFLICT</c> ledger), a capturing <see cref="IWorkflowDispatcher"/>
/// and a deterministic leader lock. Covers AC2 / AC4 / AC6 / AC9 and the
/// failure-isolation + fire-budget behaviours.
/// </summary>
[TestFixture]
public class TenantScheduledTriggerServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 07, 27, 12, 30, 00, TimeSpan.Zero);

    // ── fakes ──

    private sealed class StubTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    /// <summary>
    /// In-memory ledger + registry. The claim map reproduces the ON CONFLICT
    /// semantics: first (trigger, window) wins, everyone else gets false —
    /// including a claim arriving AFTER the winner committed (the
    /// sequential-double-fire case).
    /// </summary>
    private sealed class FakeRepository : IScheduledTriggerRepository
    {
        public List<Guid> ActiveTenants { get; } = new();
        public List<ScheduledTrigger> Triggers { get; } = new();
        public Dictionary<(Guid, string), ScheduledTriggerFire> Fires { get; } = new();
        public int PruneCalls { get; private set; }

        public Task<IReadOnlyList<Guid>> SnapshotActiveTenantIdsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Guid>>(ActiveTenants.OrderBy(t => t).ToList());

        public Task<int> MaterialiseTemplatesAsync(
            IReadOnlyList<Guid> activeTenantIds, DateTime nowUtc, CancellationToken ct = default)
        {
            var created = 0;
            foreach (var template in Triggers.Where(t => t.TenantId is null && t.Enabled).ToList())
            {
                foreach (var tenant in activeTenantIds)
                {
                    if (Triggers.Any(t => t.TenantId == tenant
                        && t.DefinitionId == template.DefinitionId && t.Name == template.Name))
                        continue;
                    Triggers.Add(new ScheduledTrigger
                    {
                        Id = Guid.NewGuid(),
                        TenantId = tenant,
                        DefinitionId = template.DefinitionId,
                        Name = template.Name,
                        CronExpression = template.CronExpression,
                        Enabled = template.Enabled,
                        InputJson = template.InputJson,
                        CreatedAt = template.CreatedAt,
                        UpdatedAt = nowUtc,
                    });
                    created++;
                }
            }
            return Task.FromResult(created);
        }

        public Task<IReadOnlyList<ScheduledTrigger>> ListEnabledConcreteTriggersAsync(
            IReadOnlyList<Guid> activeTenantIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ScheduledTrigger>>(Triggers
                .Where(t => t.Enabled && t.TenantId is Guid tid && activeTenantIds.Contains(tid))
                .OrderBy(t => t.TenantId).ThenBy(t => t.Id)
                .ToList());

        private readonly object _claimGate = new();

        public Task<bool> TryClaimFireAsync(ScheduledTriggerFire fire, CancellationToken ct = default)
        {
            // Atomic first-writer-wins — the in-memory analogue of the
            // ON CONFLICT DO NOTHING unique-index arbitration (which the
            // Testcontainers ScheduledTriggerRepositoryTests prove against
            // real Postgres).
            lock (_claimGate)
            {
                var key = (fire.TriggerId, fire.WindowKey);
                if (Fires.ContainsKey(key)) return Task.FromResult(false);
                Fires[key] = fire;
                return Task.FromResult(true);
            }
        }

        public Task StampOutcomeAsync(
            Guid fireId, string outcome, string? workflowInstanceId, string? detail,
            DateTime? dispatchedAtUtc, CancellationToken ct = default)
        {
            var fire = Fires.Values.Single(f => f.Id == fireId);
            fire.Outcome = outcome;
            fire.WorkflowInstanceId = workflowInstanceId;
            fire.Detail = detail;
            fire.DispatchedAt = dispatchedAtUtc;
            return Task.CompletedTask;
        }

        public Task StampTriggerFiredAsync(
            Guid triggerId, string windowKey, DateTime firedAtUtc, DateTime? nextDueAtUtc,
            CancellationToken ct = default)
        {
            var trigger = Triggers.Single(t => t.Id == triggerId);
            trigger.LastWindowKey = windowKey;
            trigger.LastFiredAt = firedAtUtc;
            trigger.NextDueAt = nextDueAtUtc;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<(ScheduledTriggerFire Fire, ScheduledTrigger Trigger)>>
            ListPendingManualFiresAsync(int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(ScheduledTriggerFire, ScheduledTrigger)>>(Fires.Values
                .Where(f => f.Outcome == "claimed" && f.DispatchedAt == null
                    && f.WindowKey.StartsWith("manual:"))
                .OrderBy(f => f.ClaimedAt)
                .Take(limit)
                .Select(f => (f, Triggers.Single(t => t.Id == f.TriggerId)))
                .ToList());

        public Task<int> PruneLedgerAsync(
            DateTime olderThanUtc, int maxRows = 1000, CancellationToken ct = default)
        {
            PruneCalls++;
            var stale = Fires.Where(kv => kv.Value.ClaimedAt < olderThanUtc)
                .Select(kv => kv.Key).Take(maxRows).ToList();
            foreach (var key in stale) Fires.Remove(key);
            return Task.FromResult(stale.Count);
        }
    }

    private sealed class CapturingDispatcher : IWorkflowDispatcher
    {
        public List<DispatchWorkflowDefinitionRequest> Definitions { get; } = new();
        public Func<DispatchWorkflowDefinitionRequest, Exception?>? ThrowFor { get; set; }

        public Task<DispatchWorkflowResponse> DispatchAsync(
            DispatchWorkflowDefinitionRequest request, DispatchWorkflowOptions? options = default,
            CancellationToken cancellationToken = default)
        {
            if (ThrowFor?.Invoke(request) is { } ex) throw ex;
            Definitions.Add(request);
            return Task.FromResult(new DispatchWorkflowResponse(Fault: null));
        }

        public Task<DispatchWorkflowResponse> DispatchAsync(
            DispatchWorkflowInstanceRequest request, DispatchWorkflowOptions? options = default,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DispatchWorkflowResponse(Fault: null));

        public Task<DispatchWorkflowResponse> DispatchAsync(
            DispatchTriggerWorkflowsRequest request, DispatchWorkflowOptions? options = default,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DispatchWorkflowResponse(Fault: null));

        public Task<DispatchWorkflowResponse> DispatchAsync(
            DispatchResumeWorkflowsRequest request, DispatchWorkflowOptions? options = default,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DispatchWorkflowResponse(Fault: null));
    }

    private sealed class RecordingEventPublisher : IPlatformEventPublisher
    {
        public List<PlatformEvent> Events { get; } = new();

        public Task<PlatformEvent?> AppendAndPublishAsync(
            PlatformEvent evt, CancellationToken ct = default)
        {
            Events.Add(evt);
            return Task.FromResult<PlatformEvent?>(evt);
        }
    }

    private sealed class GrantingLease : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeLeaderLock : IRollupSchedulerLeaderLock
    {
        public List<long> Attempts { get; } = new();
        public bool Grant { get; set; } = true;

        public Task<IAsyncDisposable?> TryAcquireAsync(long lockKey, CancellationToken ct)
        {
            Attempts.Add(lockKey);
            return Task.FromResult<IAsyncDisposable?>(Grant ? new GrantingLease() : null);
        }
    }

    private sealed record Harness(
        TenantScheduledTriggerService Service,
        FakeRepository Repository,
        CapturingDispatcher Dispatcher,
        RecordingEventPublisher Events,
        FakeLeaderLock LeaderLock,
        StubTimeProvider Time);

    private static Harness Build(bool enabled = true, int maxFiresPerTick = 50)
    {
        var repository = new FakeRepository();
        var dispatcher = new CapturingDispatcher();
        var events = new RecordingEventPublisher();
        var leaderLock = new FakeLeaderLock();
        var time = new StubTimeProvider(Now);

        var services = new ServiceCollection()
            .AddSingleton<IScheduledTriggerRepository>(repository)
            .AddSingleton<IWorkflowDispatcher>(dispatcher)
            .AddSingleton<IPlatformEventPublisher>(events)
            .BuildServiceProvider();

        var service = new TenantScheduledTriggerService(
            services,
            Options.Create(new TenantScheduledTriggerOptions
            {
                Enabled = enabled,
                MaxFiresPerTick = maxFiresPerTick,
            }),
            time,
            NullLogger<TenantScheduledTriggerService>.Instance,
            configuration: null,
            leaderLock: leaderLock);

        return new Harness(service, repository, dispatcher, events, leaderLock, time);
    }

    private static ScheduledTrigger HourlyTrigger(
        Guid tenantId, string name = "nightly-audit", string inputJson = "{}") => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        DefinitionId = "test-noop-definition",
        Name = name,
        CronExpression = "0 * * * *",
        Enabled = true,
        InputJson = inputJson,
        // Last fired 61 minutes ago ⇒ exactly one due window (12:00Z).
        LastFiredAt = Now.AddMinutes(-61).UtcDateTime,
        CreatedAt = Now.AddDays(-10).UtcDateTime,
    };

    // ── (a) AC4 — one dispatch, with tenantId + windowKey + input_json ──

    [Test]
    public async Task DueTrigger_Dispatches_Once_With_TenantId_WindowKey_And_InputJson_AsInputs()
    {
        var h = Build();
        var tenant = Guid.NewGuid();
        h.Repository.ActiveTenants.Add(tenant);
        h.Repository.Triggers.Add(HourlyTrigger(tenant, inputJson: """{"repoFilter":"tamma/*"}"""));

        var dispatched = await h.Service.InvokeTickForTestsAsync(default);

        dispatched.Should().Be(1);
        h.Dispatcher.Definitions.Should().HaveCount(1);
        var request = h.Dispatcher.Definitions.Single();
        request.DefinitionVersionId.Should().Be("test-noop-definition", "the target is ROW DATA (AC3)");
        request.Input.Should().NotBeNull();
        request.Input!["tenantId"].Should().Be(tenant.ToString("D"), "AC4");
        request.Input["windowKey"].Should().Be("2026-07-27T12:00:00Z", "AC4");
        ((JsonElement)request.Input["repoFilter"]).GetString().Should().Be("tamma/*",
            "the row's input_json is merged into the dispatch inputs");

        var fire = h.Repository.Fires.Values.Single();
        fire.Outcome.Should().Be("dispatched");
        fire.WorkflowInstanceId.Should().NotBeNullOrEmpty();
        h.Events.Events.Should().ContainSingle(e => e.Type == ScheduleEvents.FireDispatched);
    }

    // ── (b) same window, second tick ⇒ nothing ──

    [Test]
    public async Task SecondTick_InTheSameWindow_Dispatches_Nothing()
    {
        var h = Build();
        var tenant = Guid.NewGuid();
        h.Repository.ActiveTenants.Add(tenant);
        var trigger = HourlyTrigger(tenant);
        h.Repository.Triggers.Add(trigger);

        (await h.Service.InvokeTickForTestsAsync(default)).Should().Be(1);

        // Simulate a pod restart losing the trigger-row bookkeeping: reset
        // LastFiredAt so the window computes as due again. Only the ledger
        // stands between us and a sequential double-fire (Correction 3).
        trigger.LastFiredAt = Now.AddMinutes(-61).UtcDateTime;
        trigger.LastWindowKey = null;

        (await h.Service.InvokeTickForTestsAsync(default)).Should().Be(0);
        h.Dispatcher.Definitions.Should().HaveCount(1, "the committed claim is the dedupe");
        h.Events.Events.Should().ContainSingle(e => e.Type == ScheduleEvents.FireSuppressed);
    }

    // ── (c) AC2 — three tenants, one schedule ⇒ three dispatches, three lock keys ──

    [Test]
    public async Task ThreeTenants_OneSchedule_ThreeDispatches_ThreeDistinctLockKeys()
    {
        var h = Build();
        var tenants = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        h.Repository.ActiveTenants.AddRange(tenants);
        foreach (var tenant in tenants)
            h.Repository.Triggers.Add(HourlyTrigger(tenant));

        var dispatched = await h.Service.InvokeTickForTestsAsync(default);

        dispatched.Should().Be(3, "tenant A's fire must never suppress tenant B's (AC2)");
        h.Dispatcher.Definitions.Select(d => d.Input!["tenantId"]).Should()
            .BeEquivalentTo(tenants.Select(t => (object)t.ToString("D")));
        h.LeaderLock.Attempts.Should().HaveCount(3).And.OnlyHaveUniqueItems(
            "same window, three tenants ⇒ three DIFFERENT advisory-lock keys");
    }

    // ── (d) AC9 — disabled by default ⇒ the loop exits without touching anything ──

    [Test]
    public async Task Disabled_Service_Returns_Immediately_And_Dispatches_Nothing()
    {
        var h = Build(enabled: false);
        var tenant = Guid.NewGuid();
        h.Repository.ActiveTenants.Add(tenant);
        h.Repository.Triggers.Add(HourlyTrigger(tenant));

        // Drive the REAL BackgroundService entry point — the Enabled guard
        // lives in ExecuteAsync, before any tick.
        await h.Service.StartAsync(default);
        await h.Service.ExecuteTask!;
        await h.Service.StopAsync(default);

        h.Dispatcher.Definitions.Should().BeEmpty();
        h.Repository.Fires.Should().BeEmpty();
        h.Repository.PruneCalls.Should().Be(0, "a disabled service must not even touch the DB");
    }

    [Test]
    public void Options_Default_Is_Disabled()
    {
        new TenantScheduledTriggerOptions().Enabled.Should().BeFalse(
            "AC9 — landing the seam must change no running deployment until an operator opts in");
    }

    // ── (e) failure isolation ──

    [Test]
    public async Task ADispatcherThrow_ForOneTenant_DoesNotAbort_TheOthers_AndEmits_FireFailed_Once()
    {
        var h = Build();
        var tenants = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() }
            .OrderBy(t => t).ToArray();
        h.Repository.ActiveTenants.AddRange(tenants);
        foreach (var tenant in tenants)
            h.Repository.Triggers.Add(HourlyTrigger(tenant));

        var poisoned = tenants[1].ToString("D");
        h.Dispatcher.ThrowFor = req =>
            Equals(req.Input?["tenantId"], poisoned)
                ? new InvalidOperationException("engine unavailable for tenant 2")
                : null;

        var dispatched = await h.Service.InvokeTickForTestsAsync(default);

        dispatched.Should().Be(2, "tenants 1 and 3 still dispatch");
        h.Events.Events.Where(e => e.Type == ScheduleEvents.FireFailed).Should().HaveCount(1);
        h.Repository.Fires.Values.Where(f => f.Outcome == "failed").Should().ContainSingle(
            "the poisoned window is stamped failed — the NEXT window is the recovery path, "
            + "never a same-window retry (Correction 4)");
    }

    // ── (f) AC6 — bounded catch-up ──

    [Test]
    public async Task After_A_24HourOutage_AnHourlySchedule_Fires_Once_And_Skips_23_Windows_Auditable()
    {
        var h = Build();
        var tenant = Guid.NewGuid();
        h.Repository.ActiveTenants.Add(tenant);
        var trigger = HourlyTrigger(tenant);
        trigger.LastFiredAt = Now.AddHours(-24).UtcDateTime; // 24 due windows
        h.Repository.Triggers.Add(trigger);

        var dispatched = await h.Service.InvokeTickForTestsAsync(default);

        dispatched.Should().Be(1, "AC6 — only the most recent missed window fires");
        h.Dispatcher.Definitions.Single().Input!["windowKey"].Should().Be("2026-07-27T12:00:00Z");

        var skipped = h.Events.Events.Single(e => e.Type == ScheduleEvents.WindowSkipped);
        using var data = JsonDocument.Parse(skipped.Data);
        data.RootElement.GetProperty("skippedCount").GetInt32().Should().Be(23);
        data.RootElement.GetProperty("firstSkippedWindowKey").GetString()
            .Should().Be("2026-07-26T13:00:00Z");
        data.RootElement.GetProperty("lastSkippedWindowKey").GetString()
            .Should().Be("2026-07-27T11:00:00Z");
    }

    // ── (g) MaxFiresPerTick bounds a cold start ──

    [Test]
    public async Task MaxFiresPerTick_Bounds_A_ColdStart()
    {
        var h = Build(maxFiresPerTick: 2);
        for (var i = 0; i < 5; i++)
        {
            var tenant = Guid.NewGuid();
            h.Repository.ActiveTenants.Add(tenant);
            h.Repository.Triggers.Add(HourlyTrigger(tenant));
        }

        (await h.Service.InvokeTickForTestsAsync(default)).Should().Be(2);
        h.Dispatcher.Definitions.Should().HaveCount(2,
            "the remaining due triggers roll to the next tick");
    }

    // ── templates materialise then fire (D6) ──

    [Test]
    public async Task APlatformTemplate_Materialises_PerTenant_AndFires_TheConcreteRows()
    {
        var h = Build();
        var tenants = new[] { Guid.NewGuid(), Guid.NewGuid() };
        h.Repository.ActiveTenants.AddRange(tenants);
        var template = HourlyTrigger(Guid.Empty);
        template.TenantId = null; // platform default template
        template.LastFiredAt = null;
        template.CreatedAt = Now.AddMinutes(-61).UtcDateTime;
        h.Repository.Triggers.Add(template);

        var dispatched = await h.Service.InvokeTickForTestsAsync(default);

        dispatched.Should().Be(2, "one fire per tenant, never one fire for the template");
        h.Repository.Triggers.Where(t => t.TenantId != null).Should().HaveCount(2);
        h.Repository.Fires.Values.Select(f => f.TenantId)
            .Should().BeEquivalentTo(tenants, "the ledger rows are per-tenant (D6)");
    }

    // ── the end-to-end at-most-once proof (AC1, service level) ──

    /// <summary>
    /// Two "pods" (two independent service instances — separate leader locks,
    /// separate DI scopes) sharing one ledger race the SAME due (tenant,
    /// trigger, window) concurrently: exactly ONE dispatch happens. A third,
    /// freshly-constructed "restarted pod" whose trigger-row bookkeeping was
    /// wiped (the crash case) then ticks and dispatches NOTHING — only the
    /// committed ledger row prevents that sequential double-fire
    /// (Correction 3). The Postgres half of the same proof (real ON CONFLICT
    /// arbitration) lives in ScheduledTriggerRepositoryTests.
    /// </summary>
    [Test]
    public async Task TwoConcurrentPods_ThenARestartedPod_DispatchExactlyOnce_PerWindow()
    {
        var repository = new FakeRepository();
        var tenant = Guid.NewGuid();
        repository.ActiveTenants.Add(tenant);
        var trigger = HourlyTrigger(tenant);
        repository.Triggers.Add(trigger);

        Harness Pod()
        {
            var dispatcher = new CapturingDispatcher();
            var events = new RecordingEventPublisher();
            var leaderLock = new FakeLeaderLock();
            var time = new StubTimeProvider(Now);
            var services = new ServiceCollection()
                .AddSingleton<IScheduledTriggerRepository>(repository)
                .AddSingleton<IWorkflowDispatcher>(dispatcher)
                .AddSingleton<IPlatformEventPublisher>(events)
                .BuildServiceProvider();
            var service = new TenantScheduledTriggerService(
                services,
                Options.Create(new TenantScheduledTriggerOptions { Enabled = true }),
                time,
                NullLogger<TenantScheduledTriggerService>.Instance,
                configuration: null,
                leaderLock: leaderLock);
            return new Harness(service, repository, dispatcher, events, leaderLock, time);
        }

        var podA = Pod();
        var podB = Pod();

        var results = await Task.WhenAll(
            Task.Run(() => podA.Service.InvokeTickForTestsAsync(default)),
            Task.Run(() => podB.Service.InvokeTickForTestsAsync(default)));

        results.Sum().Should().Be(1, "AC1 — at most one dispatch per (tenant, trigger, window) across the fleet");
        (podA.Dispatcher.Definitions.Count + podB.Dispatcher.Definitions.Count).Should().Be(1);

        // The crash-restart case: a new pod boots with NO in-process state and
        // the trigger row's bookkeeping lost (as if the pod died between
        // dispatch and the trigger-row stamp).
        trigger.LastFiredAt = Now.AddMinutes(-61).UtcDateTime;
        trigger.LastWindowKey = null;
        var podC = Pod();

        (await podC.Service.InvokeTickForTestsAsync(default)).Should().Be(0,
            "the committed ledger row — not the session-scoped lock, not process memory — "
            + "prevents the sequential double-fire");
        podC.Dispatcher.Definitions.Should().BeEmpty();
    }

    // ── manual run-now claims drain through the same path ──

    [Test]
    public async Task AManualRunNowClaim_IsDispatched_AndStamped()
    {
        var h = Build();
        var tenant = Guid.NewGuid();
        h.Repository.ActiveTenants.Add(tenant);
        var trigger = HourlyTrigger(tenant);
        trigger.LastFiredAt = Now.UtcDateTime; // no cron window due
        h.Repository.Triggers.Add(trigger);
        var manual = new ScheduledTriggerFire
        {
            Id = Guid.NewGuid(),
            TriggerId = trigger.Id,
            TenantId = tenant,
            DefinitionId = trigger.DefinitionId,
            WindowKey = "manual:20260727T121500.000Z",
            ClaimedAt = Now.AddMinutes(-15).UtcDateTime,
            Outcome = "claimed",
        };
        h.Repository.Fires[(trigger.Id, manual.WindowKey)] = manual;

        var dispatched = await h.Service.InvokeTickForTestsAsync(default);

        dispatched.Should().Be(1);
        h.Dispatcher.Definitions.Single().Input!["windowKey"].Should().Be(manual.WindowKey,
            "a manual window key is opaque to the consumer exactly like a cron one");
        manual.Outcome.Should().Be("dispatched");
    }
}
