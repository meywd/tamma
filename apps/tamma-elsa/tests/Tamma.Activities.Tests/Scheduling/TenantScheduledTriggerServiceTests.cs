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

        /// <summary>MODERATE-4 — when set, MaterialiseTemplatesAsync throws
        /// this (the poison-template / transient-DB-error case).</summary>
        public Func<Exception>? MaterialiseFailure { get; set; }

        public Task<IReadOnlyList<Guid>> SnapshotActiveTenantIdsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Guid>>(ActiveTenants.OrderBy(t => t).ToList());

        public Task<int> MaterialiseTemplatesAsync(
            IReadOnlyList<Guid> activeTenantIds, DateTime nowUtc, CancellationToken ct = default)
        {
            if (MaterialiseFailure is { } failure) throw failure();

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
                        // The REAL repository stamps CreatedAt = now on a
                        // freshly materialised row (so its first fire is the
                        // NEXT cron occurrence) — the fake must match.
                        CreatedAt = nowUtc,
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
        {
            lock (_claimGate)
            {
                // Mirrors the real query: pending manual claims on ENABLED
                // triggers only (2026-07-29 contract).
                return Task.FromResult<IReadOnlyList<(ScheduledTriggerFire, ScheduledTrigger)>>(
                    Fires.Values
                        .Where(f => f.Outcome == "claimed" && f.DispatchedAt == null
                            && f.WindowKey.StartsWith("manual:"))
                        .Select(f => (Fire: f, Trigger: Triggers.Single(t => t.Id == f.TriggerId)))
                        .Where(p => p.Trigger.Enabled)
                        .OrderBy(p => p.Fire.ClaimedAt)
                        .Take(limit)
                        .Select(p => ((ScheduledTriggerFire, ScheduledTrigger))p)
                        .ToList());
            }
        }

        public Task<bool> TryClaimManualFireForDispatchAsync(
            Guid fireId, DateTime attemptAtUtc, CancellationToken ct = default)
        {
            // The in-memory analogue of the real repository's conditional
            // UPDATE … WHERE Outcome='claimed' AND DispatchedAt IS NULL CAS
            // (which the Testcontainers ScheduledTriggerRepositoryTests prove
            // against real Postgres, MAJOR-1).
            lock (_claimGate)
            {
                var fire = Fires.Values.SingleOrDefault(f => f.Id == fireId);
                if (fire is null || fire.Outcome != "claimed" || fire.DispatchedAt is not null)
                    return Task.FromResult(false);
                fire.DispatchedAt = attemptAtUtc;
                return Task.FromResult(true);
            }
        }

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

        /// <summary>MODERATE-3 — requests matching this predicate NEVER
        /// complete and IGNORE the cancellation token (the worst-case hung
        /// dispatcher the per-dispatch timeout must survive).</summary>
        public Func<DispatchWorkflowDefinitionRequest, bool>? HangFor { get; set; }

        public Task<DispatchWorkflowResponse> DispatchAsync(
            DispatchWorkflowDefinitionRequest request, DispatchWorkflowOptions? options = default,
            CancellationToken cancellationToken = default)
        {
            if (HangFor?.Invoke(request) == true)
                return new TaskCompletionSource<DispatchWorkflowResponse>().Task;
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

    private static Harness Build(
        bool enabled = true, int maxFiresPerTick = 50, TimeSpan? dispatchTimeout = null)
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

        var options = new TenantScheduledTriggerOptions
        {
            Enabled = enabled,
            MaxFiresPerTick = maxFiresPerTick,
        };
        if (dispatchTimeout is { } timeout) options.DispatchTimeout = timeout;

        var service = new TenantScheduledTriggerService(
            services,
            Options.Create(options),
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

    // ── templates materialise, then the concrete rows fire at the NEXT
    // cron occurrence (D6) ──

    [Test]
    public async Task APlatformTemplate_Materialises_PerTenant_AndTheConcreteRows_FireAtTheNextCronOccurrence()
    {
        var h = Build();
        var tenants = new[] { Guid.NewGuid(), Guid.NewGuid() };
        h.Repository.ActiveTenants.AddRange(tenants);
        var template = HourlyTrigger(Guid.Empty);
        template.TenantId = null; // platform default template
        template.LastFiredAt = null;
        template.CreatedAt = Now.AddMinutes(-61).UtcDateTime;
        h.Repository.Triggers.Add(template);

        // Tick 1 — materialisation only. The REAL repository stamps a
        // freshly materialised row's CreatedAt = now, so its first due
        // window is the NEXT cron occurrence — nothing fires on the
        // materialising tick itself (and the template NEVER fires).
        (await h.Service.InvokeTickForTestsAsync(default)).Should().Be(0,
            "a freshly materialised row first fires at the NEXT cron occurrence, not the same tick");
        h.Repository.Triggers.Where(t => t.TenantId != null).Should().HaveCount(2);
        h.Repository.Fires.Should().BeEmpty();

        // Tick 2 — past the next hourly occurrence (13:00Z): both concrete
        // rows fire; the template still does not.
        h.Time.UtcNow = Now.AddMinutes(31); // 13:01Z
        (await h.Service.InvokeTickForTestsAsync(default)).Should().Be(2,
            "one fire per tenant, never one fire for the template");
        h.Repository.Fires.Values.Select(f => f.TenantId)
            .Should().BeEquivalentTo(tenants, "the ledger rows are per-tenant (D6)");
        h.Repository.Fires.Values.Should().OnlyContain(
            f => f.WindowKey == "2026-07-27T13:00:00Z");
    }

    // ── MODERATE-4 — materialisation failure stays inside the tick's
    // failure isolation ──

    [Test]
    public async Task AMaterialisationFailure_DoesNotKillTheTick_ConcreteTriggersStillFire()
    {
        var h = Build();
        var tenant = Guid.NewGuid();
        h.Repository.ActiveTenants.Add(tenant);
        h.Repository.Triggers.Add(HourlyTrigger(tenant));
        h.Repository.MaterialiseFailure =
            () => new InvalidOperationException("poison template row");

        var dispatched = await h.Service.InvokeTickForTestsAsync(default);

        dispatched.Should().Be(1,
            "MODERATE-4 — a poison template must not take down the whole tick "
            + "(forever, for all tenants); existing concrete triggers still fire");
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

    private static ScheduledTriggerFire PendingManualFire(
        ScheduledTrigger trigger, string windowKey = "manual:20260727T121500.000Z") => new()
    {
        Id = Guid.NewGuid(),
        TriggerId = trigger.Id,
        TenantId = trigger.TenantId!.Value,
        DefinitionId = trigger.DefinitionId,
        WindowKey = windowKey,
        ClaimedAt = Now.AddMinutes(-15).UtcDateTime,
        Outcome = "claimed",
    };

    // ── MAJOR-1 — the manual drain is at-most-once ──

    /// <summary>
    /// The manual mirror of <c>TwoConcurrentPods_…_DispatchExactlyOnce</c>:
    /// eight independent "pods" sharing one ledger tick concurrently over a
    /// SINGLE pending manual fire. Pre-fix, every pod listed the row and
    /// dispatched it; the per-row CAS
    /// (<c>TryClaimManualFireForDispatchAsync</c>) lets exactly one win.
    /// The real-Postgres arbitration proof lives in
    /// ScheduledTriggerRepositoryTests.
    /// </summary>
    [Test]
    public async Task EightConcurrentPods_DrainingOnePendingManualFire_DispatchExactlyOnce()
    {
        var repository = new FakeRepository();
        var tenant = Guid.NewGuid();
        repository.ActiveTenants.Add(tenant);
        var trigger = HourlyTrigger(tenant);
        trigger.LastFiredAt = Now.UtcDateTime; // no cron window due
        repository.Triggers.Add(trigger);
        var manual = PendingManualFire(trigger);
        repository.Fires[(trigger.Id, manual.WindowKey)] = manual;

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

        var pods = Enumerable.Range(0, 8).Select(_ => Pod()).ToList();

        var results = await Task.WhenAll(
            pods.Select(p => Task.Run(() => p.Service.InvokeTickForTestsAsync(default))));

        results.Sum().Should().Be(1,
            "MAJOR-1 — concurrent drains must not double-dispatch a pending manual fire");
        pods.Sum(p => p.Dispatcher.Definitions.Count).Should().Be(1);
        manual.Outcome.Should().Be("dispatched");
    }

    [Test]
    public async Task AManualDispatchFailure_BurnsTheFire_NeverARedispatchLoop()
    {
        var h = Build();
        var tenant = Guid.NewGuid();
        h.Repository.ActiveTenants.Add(tenant);
        var trigger = HourlyTrigger(tenant);
        trigger.LastFiredAt = Now.UtcDateTime; // no cron window due
        h.Repository.Triggers.Add(trigger);
        var manual = PendingManualFire(trigger);
        h.Repository.Fires[(trigger.Id, manual.WindowKey)] = manual;

        var attempts = 0;
        h.Dispatcher.ThrowFor = _ =>
        {
            attempts++;
            return new InvalidOperationException("engine unavailable");
        };

        (await h.Service.InvokeTickForTestsAsync(default)).Should().Be(0);
        attempts.Should().Be(1);
        manual.Outcome.Should().Be("failed",
            "a manual dispatch failure burns the fire (at-most-once), like the cron path");

        // The pre-fix defect: an unstamped/failed row was re-listed and
        // re-dispatched on EVERY tick, forever.
        (await h.Service.InvokeTickForTestsAsync(default)).Should().Be(0);
        attempts.Should().Be(1, "MAJOR-1 — a burnt manual fire is never re-dispatched");
    }

    [Test]
    public async Task APendingManualFire_OnADisabledTrigger_IsNotDrained()
    {
        var h = Build();
        var tenant = Guid.NewGuid();
        h.Repository.ActiveTenants.Add(tenant);
        var trigger = HourlyTrigger(tenant);
        trigger.Enabled = false;
        h.Repository.Triggers.Add(trigger);
        var manual = PendingManualFire(trigger);
        h.Repository.Fires[(trigger.Id, manual.WindowKey)] = manual;

        (await h.Service.InvokeTickForTestsAsync(default)).Should().Be(0);
        h.Dispatcher.Definitions.Should().BeEmpty(
            "2026-07-29 contract — the drain only dispatches ENABLED triggers' claims");
        manual.Outcome.Should().Be("claimed", "the claim waits for re-enablement, un-burnt");
        manual.DispatchedAt.Should().BeNull();
    }

    // ── MODERATE-3 — per-dispatch timeout ──

    [Test]
    public async Task AHungDispatch_TimesOut_StampsFailed_AndDoesNotStallTheOtherTenants()
    {
        var h = Build(dispatchTimeout: TimeSpan.FromMilliseconds(200));
        var tenants = new[] { Guid.NewGuid(), Guid.NewGuid() }.OrderBy(t => t).ToArray();
        h.Repository.ActiveTenants.AddRange(tenants);
        foreach (var tenant in tenants)
            h.Repository.Triggers.Add(HourlyTrigger(tenant));

        // The FIRST tenant's dispatch hangs forever and ignores its token —
        // the worst case. Pre-fix this stalled the entire tick (and every
        // later tenant) while holding the advisory-lock connection.
        var hung = tenants[0].ToString("D");
        h.Dispatcher.HangFor = req => Equals(req.Input?["tenantId"], hung);

        var dispatched = await h.Service.InvokeTickForTestsAsync(default);

        dispatched.Should().Be(1, "the second tenant must still fire");
        var failed = h.Repository.Fires.Values.Single(f => f.Outcome == "failed");
        failed.TenantId.Should().Be(tenants[0]);
        failed.Detail.Should().Contain("timed out",
            "MODERATE-3 — a hung dispatch is stamped failed after the per-dispatch timeout");
        h.Events.Events.Should().ContainSingle(e => e.Type == ScheduleEvents.FireFailed);
    }

    // ── MAJOR-2 — a capped backlog still fires the MOST RECENT window ──

    [Test]
    public async Task AMinutelyTrigger_18HoursStale_Fires_TheMostRecentDueWindow_WithTheTrueSkipCount()
    {
        var h = Build();
        var tenant = Guid.NewGuid();
        h.Repository.ActiveTenants.Add(tenant);
        var trigger = HourlyTrigger(tenant);
        trigger.CronExpression = "* * * * *";
        trigger.LastFiredAt = Now.AddHours(-18).UtcDateTime; // 1080 due windows
        h.Repository.Triggers.Add(trigger);

        var dispatched = await h.Service.InvokeTickForTestsAsync(default);

        dispatched.Should().Be(1, "AC6 — bounded catch-up fires exactly once");
        h.Dispatcher.Definitions.Single().Input!["windowKey"].Should().Be(
            "2026-07-27T12:30:00Z",
            "MAJOR-2 — the fired window must be the MOST RECENT due occurrence; the pre-fix "
            + "capped list fired the 1000th-OLDEST one (~7h stale) and never surfaced the rest");

        var skipped = h.Events.Events.Single(e => e.Type == ScheduleEvents.WindowSkipped);
        using var data = JsonDocument.Parse(skipped.Data);
        data.RootElement.GetProperty("skippedCount").GetInt32().Should().Be(1079,
            "every one of the 1080 due windows except the fired one is accounted for");
        data.RootElement.GetProperty("skippedCountSaturated").GetBoolean().Should().BeFalse();
        data.RootElement.GetProperty("firstSkippedWindowKey").GetString()
            .Should().Be("2026-07-26T18:31:00Z");
        data.RootElement.GetProperty("lastSkippedWindowKey").GetString()
            .Should().Be("2026-07-27T12:29:00Z");
    }
}
