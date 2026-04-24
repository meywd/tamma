using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.Alerts;
using Tamma.Api.Services.Alerts.Rules;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Alerts.Rules;

/// <summary>
/// Story 5.6 (Wave C.2) — evaluator behavior:
/// <list type="bullet">
///   <item><description>fires sink when a rule matches</description></item>
///   <item><description>count_gte requires threshold events</description></item>
///   <item><description>throttle drops subsequent fires within window</description></item>
///   <item><description>cursor advances past processed batch</description></item>
///   <item><description>restart from cursor — no duplicate fires</description></item>
///   <item><description>ALERT.* events self-filter to avoid feedback</description></item>
///   <item><description>malformed predicate on one rule doesn't kill batch</description></item>
///   <item><description>platform_events are polled alongside domain_events</description></item>
///   <item><description>RULE.MATCHED event emitted on fire</description></item>
/// </list>
/// </summary>
[TestFixture]
public class AlertRuleEvaluatorTests
{
    private ServiceProvider _sp = null!;
    private RecordingAlertSink _sink = null!;
    private RecordingEventRepository _events = null!;
    private InMemoryRuleWindowStore _windowStore = null!;
    private AlertRuleRegistry _registry = null!;
    private TestTimeProvider _time = null!;
    private AlertRuleEvaluator _evaluator = null!;
    private AlertRuleEvaluatorOptions _options = null!;

    [SetUp]
    public void SetUp()
    {
        _sink = new RecordingAlertSink();
        _events = new RecordingEventRepository();
        _windowStore = new InMemoryRuleWindowStore();
        _time = new TestTimeProvider(DateTimeOffset.Parse("2026-04-23T12:00:00Z"));

        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId
                    .TransactionIgnoredWarning))
            .Options;

        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddScoped<ControlPlaneDbContext>(_ =>
            new ControlPlaneDbContext(options));
        services.AddSingleton<IAlertSink>(_sink);
        services.AddSingleton<IEventRepository>(_events);
        services.AddSingleton<IRuleWindowStore>(_windowStore);

        // Build once, then bolt-on the registry singleton that
        // references the same SP — AlertRuleRegistry resolves
        // scoped dependencies internally.
        var earlySp = services.BuildServiceProvider();
        _registry = new AlertRuleRegistry(
            earlySp, NullLogger<AlertRuleRegistry>.Instance);
        services.AddSingleton<IAlertRuleRegistry>(_registry);
        _sp = services.BuildServiceProvider();

        _options = new AlertRuleEvaluatorOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(10),
            RegistryRefreshInterval = TimeSpan.FromMinutes(10),
            BatchSize = 100,
            EvaluatorId = "test-eval",
        };
        _evaluator = new AlertRuleEvaluator(
            _sp, _options, _time,
            NullLogger<AlertRuleEvaluator>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _evaluator.Dispose();
        _sp.Dispose();
    }

    private async Task AddRuleAsync(AlertRule rule)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        db.AlertRules.Add(rule);
        await db.SaveChangesAsync();
    }

    private async Task AddEventAsync(DomainEvent evt)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        db.DomainEvents.Add(evt);
        await db.SaveChangesAsync();
    }

    private async Task AddPlatformEventAsync(PlatformEvent evt)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        db.PlatformEvents.Add(evt);
        await db.SaveChangesAsync();
    }

    private static AlertRule MakeRule(
        string eventType,
        string predicate = """{"op":"always"}""",
        int throttle = 0,
        string name = "r")
    {
        return new AlertRule
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = "d",
            IsEnabled = true,
            Severity = AlertSeverity.Warning,
            EventType = eventType,
            Predicate = predicate,
            ThrottleSeconds = throttle,
            ChannelIds = Array.Empty<Guid>(),
            IsBuiltIn = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
    }

    private static DomainEvent MakeEvent(
        string type, Guid? tenantId = null, DateTime? at = null)
    {
        return new DomainEvent
        {
            Id = Guid.NewGuid(),
            Type = type,
            TenantId = tenantId,
            Tags = "{}",
            Metadata = "{}",
            Data = "{}",
            CreatedAt = at ?? DateTime.UtcNow,
        };
    }

    [Test]
    public async Task ProcessOnce_AlwaysRuleFires_OnSink()
    {
        await AddRuleAsync(MakeRule("BUDGET.EXHAUSTED"));
        await _registry.RefreshAsync(default);

        await AddEventAsync(MakeEvent("BUDGET.EXHAUSTED", tenantId: Guid.NewGuid()));

        var processed = await _evaluator.ProcessOnceAsync(default);
        processed.Should().Be(1);

        _sink.Raised.Should().ContainSingle();
        _sink.Raised[0].Severity.Should().Be(AlertSeverity.Warning);
    }

    [Test]
    public async Task ProcessOnce_RuleMatchedEvent_Emitted()
    {
        await AddRuleAsync(MakeRule("BUDGET.EXHAUSTED", name: "budget-rule"));
        await _registry.RefreshAsync(default);
        await AddEventAsync(MakeEvent("BUDGET.EXHAUSTED", tenantId: Guid.NewGuid()));

        await _evaluator.ProcessOnceAsync(default);

        _events.Emitted.Should().ContainSingle(e => e.Type == "RULE.MATCHED");
        var matchedEvent = _events.Emitted.First(e => e.Type == "RULE.MATCHED");
        matchedEvent.Tags.Should().Contain("budget-rule");
    }

    [Test]
    public async Task ProcessOnce_CountGte_RequiresThresholdEvents()
    {
        await AddRuleAsync(MakeRule(
            "AGENT.DISPATCH.FAILED",
            predicate:
                """{"op":"count_gte","window_seconds":300,"threshold":3}"""));
        await _registry.RefreshAsync(default);

        var tenantId = Guid.NewGuid();
        var now = _time.GetUtcNow().UtcDateTime;
        await AddEventAsync(MakeEvent("AGENT.DISPATCH.FAILED", tenantId, at: now.AddSeconds(0)));
        await AddEventAsync(MakeEvent("AGENT.DISPATCH.FAILED", tenantId, at: now.AddSeconds(1)));
        // Only two events — below threshold, no fire.
        await _evaluator.ProcessOnceAsync(default);
        _sink.Raised.Should().BeEmpty();

        // Third event — now above threshold, fires.
        await AddEventAsync(MakeEvent("AGENT.DISPATCH.FAILED", tenantId, at: now.AddSeconds(2)));
        await _evaluator.ProcessOnceAsync(default);
        _sink.Raised.Should().ContainSingle();
    }

    [Test]
    public async Task ProcessOnce_Throttle_DropsSecondFireWithinWindow()
    {
        await AddRuleAsync(MakeRule("BUDGET.EXHAUSTED", throttle: 60));
        await _registry.RefreshAsync(default);

        var tenantId = Guid.NewGuid();
        var start = _time.GetUtcNow().UtcDateTime;
        await AddEventAsync(MakeEvent("BUDGET.EXHAUSTED", tenantId, at: start));
        await _evaluator.ProcessOnceAsync(default);
        _sink.Raised.Should().HaveCount(1);

        // Fast-forward 30s (still inside throttle).
        _time.Advance(TimeSpan.FromSeconds(30));
        await AddEventAsync(MakeEvent("BUDGET.EXHAUSTED", tenantId, at: start.AddSeconds(30)));
        await _evaluator.ProcessOnceAsync(default);
        _sink.Raised.Should().HaveCount(1, "throttled — second fire dropped");

        // Past the throttle window.
        _time.Advance(TimeSpan.FromSeconds(31));
        await AddEventAsync(MakeEvent("BUDGET.EXHAUSTED", tenantId, at: start.AddSeconds(65)));
        await _evaluator.ProcessOnceAsync(default);
        _sink.Raised.Should().HaveCount(2);
    }

    [Test]
    public async Task ProcessOnce_ThrottlePerTenant_DoesNotBlockOtherTenants()
    {
        await AddRuleAsync(MakeRule("BUDGET.EXHAUSTED", throttle: 60));
        await _registry.RefreshAsync(default);

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var now = _time.GetUtcNow().UtcDateTime;

        await AddEventAsync(MakeEvent("BUDGET.EXHAUSTED", tenantA, at: now));
        await AddEventAsync(MakeEvent("BUDGET.EXHAUSTED", tenantB, at: now.AddMilliseconds(1)));

        await _evaluator.ProcessOnceAsync(default);
        _sink.Raised.Should().HaveCount(2,
            "throttle is keyed per tenant");
    }

    [Test]
    public async Task ProcessOnce_CursorAdvances_SecondCallProcessesZero()
    {
        await AddRuleAsync(MakeRule("BUDGET.EXHAUSTED"));
        await _registry.RefreshAsync(default);
        await AddEventAsync(MakeEvent("BUDGET.EXHAUSTED", Guid.NewGuid()));

        await _evaluator.ProcessOnceAsync(default);
        _sink.Raised.Should().HaveCount(1);

        var secondBatch = await _evaluator.ProcessOnceAsync(default);
        secondBatch.Should().Be(0, "cursor advanced past the event");
    }

    [Test]
    public async Task ProcessOnce_CursorPersistedToDatabase()
    {
        await AddRuleAsync(MakeRule("BUDGET.EXHAUSTED"));
        await _registry.RefreshAsync(default);
        await AddEventAsync(MakeEvent("BUDGET.EXHAUSTED", Guid.NewGuid()));

        await _evaluator.ProcessOnceAsync(default);

        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var cursor = await db.AlertEvaluatorCursors.SingleAsync();
        cursor.EvaluatorId.Should().Be("test-eval");
        cursor.LastEventId.Should().NotBeNull();
    }

    [Test]
    public async Task ProcessOnce_CrashThenRestart_DoesNotDoubleFire()
    {
        await AddRuleAsync(MakeRule("BUDGET.EXHAUSTED"));
        await _registry.RefreshAsync(default);
        await AddEventAsync(MakeEvent("BUDGET.EXHAUSTED", Guid.NewGuid()));

        // First evaluator: processes the event, persists cursor.
        await _evaluator.ProcessOnceAsync(default);
        _sink.Raised.Should().HaveCount(1);

        // Simulate crash + restart by creating a second evaluator
        // against the same DB (the cursor row is already persisted).
        var newSink = new RecordingAlertSink();
        var newSp = BuildContainerWithSharedDb(newSink);
        var fresh = new AlertRuleEvaluator(
            newSp, _options, _time,
            NullLogger<AlertRuleEvaluator>.Instance);

        using (var scope = newSp.CreateScope())
        {
            var reg = scope.ServiceProvider
                .GetRequiredService<IAlertRuleRegistry>();
            await reg.RefreshAsync(default);
        }
        var processed = await fresh.ProcessOnceAsync(default);
        processed.Should().Be(0);
        newSink.Raised.Should().BeEmpty();

        newSp.Dispose();
    }

    private ServiceProvider BuildContainerWithSharedDb(
        RecordingAlertSink sink)
    {
        // Re-use the same DbContextOptions (registered as singleton)
        // so the cloned service-provider talks to the same InMemory
        // database root as the original.
        var srcOptions = _sp.GetRequiredService<
            DbContextOptions<ControlPlaneDbContext>>();

        var services = new ServiceCollection();
        services.AddSingleton(srcOptions);
        services.AddScoped<ControlPlaneDbContext>(_ =>
            new ControlPlaneDbContext(srcOptions));
        services.AddSingleton<IAlertSink>(sink);
        services.AddSingleton<IEventRepository>(_events);
        services.AddSingleton<IRuleWindowStore>(_windowStore);
        services.AddSingleton<IAlertRuleRegistry>(sp =>
            new AlertRuleRegistry(sp, NullLogger<AlertRuleRegistry>.Instance));
        return services.BuildServiceProvider();
    }

    [Test]
    public async Task ProcessOnce_AlertStarEvents_SelfFilteredToAvoidFeedback()
    {
        // A malicious / buggy rule that subscribes to ALERT.* itself.
        // The evaluator interlock MUST drop these events regardless.
        await AddRuleAsync(MakeRule("ALERT.RAISED"));
        await _registry.RefreshAsync(default);
        await AddEventAsync(MakeEvent("ALERT.RAISED", Guid.NewGuid()));

        await _evaluator.ProcessOnceAsync(default);
        _sink.Raised.Should().BeEmpty();
    }

    [Test]
    public async Task ProcessOnce_BadPredicateInOneRule_DoesNotKillBatch()
    {
        await AddRuleAsync(MakeRule("A.X", name: "good"));

        // Insert a row with a bad predicate directly, bypassing the
        // registry's parser step so the registry actually loads it
        // then skips. Alternatively, disable the bad rule before
        // registry refresh — but the point is to test the registry's
        // skip behavior under load.
        await AddRuleAsync(MakeRule(
            "A.X", predicate: """{"op":"unknown"}""", name: "bad"));

        await _registry.RefreshAsync(default);
        // Registry skips the bad row.
        _registry.Count.Should().Be(1);

        await AddEventAsync(MakeEvent("A.X"));
        await _evaluator.ProcessOnceAsync(default);
        _sink.Raised.Should().ContainSingle();
    }

    [Test]
    public async Task ProcessOnce_PlatformEventsPolledAlongsideDomain()
    {
        await AddRuleAsync(MakeRule("PLATFORM.API.UNHEALTHY"));
        await _registry.RefreshAsync(default);

        await AddPlatformEventAsync(new PlatformEvent
        {
            Id = Guid.NewGuid(),
            Type = "PLATFORM.API.UNHEALTHY",
            TenantId = null,
            Tags = "{}",
            Metadata = "{}",
            Data = "{}",
            CreatedAt = DateTime.UtcNow,
        });

        await _evaluator.ProcessOnceAsync(default);
        _sink.Raised.Should().ContainSingle();
    }

    [Test]
    public async Task ProcessOnce_SinkThrows_ContinuesProcessingBatch()
    {
        _sink.ShouldThrowOnNext = true;

        await AddRuleAsync(MakeRule("A.X", name: "r1"));
        await _registry.RefreshAsync(default);

        await AddEventAsync(MakeEvent("A.X"));
        await AddEventAsync(MakeEvent("A.X"));

        // The first event triggers a sink throw; the second must
        // still process after the interlock caught the first failure.
        await _evaluator.ProcessOnceAsync(default);
        _sink.Raised.Should().HaveCount(1);
    }

    // ── agent-dispatch-failed-3x-5min built-in rule coverage ─────
    //
    // The four tests below exercise the canonical
    // `agent-dispatch-failed-3x-5min` rule shape from
    // BuiltInAlertRules.cs:44-56 — predicate `count_gte`, window 300s,
    // threshold 3, throttle 300s, default group_by=["tenantId"]. They
    // cover single-pass batching, throttle/debounce on the 4th event,
    // cross-tenant partitioning, and a Postgres-backed end-to-end
    // sweep through the real ControlPlaneDbContext via ApiTestFixture.

    /// <summary>
    /// Three AGENT.DISPATCH.FAILED events for one tenant in a single
    /// batch (one ProcessOnceAsync call) must produce exactly one
    /// raised alert. The predicate counts each event in turn — the
    /// third pushes the rolling-window count to the threshold and
    /// fires; the first two stay below threshold and stay quiet.
    /// </summary>
    [Test]
    public async Task ProcessOnce_AgentDispatchFailed3x_SinglePassBatch_FiresOnce()
    {
        await AddRuleAsync(MakeRule(
            "AGENT.DISPATCH.FAILED",
            predicate:
                """{"op":"count_gte","window_seconds":300,"threshold":3}""",
            throttle: 300,
            name: "agent-dispatch-failed-3x-5min"));
        await _registry.RefreshAsync(default);

        var tenantId = Guid.NewGuid();
        var now = _time.GetUtcNow().UtcDateTime;

        // All three events seeded BEFORE the first ProcessOnceAsync —
        // exercises the batch-loop path that calls Evaluate() three
        // times within a single tick.
        await AddEventAsync(MakeEvent("AGENT.DISPATCH.FAILED", tenantId, at: now.AddSeconds(0)));
        await AddEventAsync(MakeEvent("AGENT.DISPATCH.FAILED", tenantId, at: now.AddSeconds(1)));
        await AddEventAsync(MakeEvent("AGENT.DISPATCH.FAILED", tenantId, at: now.AddSeconds(2)));

        var processed = await _evaluator.ProcessOnceAsync(default);
        processed.Should().Be(3, "all three events were in-batch");

        _sink.Raised.Should().ContainSingle(
            "only the third event crosses the count_gte threshold");
        _sink.Raised[0].TenantId.Should().Be(tenantId);
        _sink.Raised[0].Severity.Should().Be(AlertSeverity.Warning);
    }

    /// <summary>
    /// After the rule has fired once on three events, a fourth event
    /// 30s later (still inside the 300s throttle AND the 300s
    /// rolling-count window) MUST NOT produce a second alert. The
    /// <c>ShouldFireAfterThrottle</c> interlock keys on
    /// <c>(ruleId, tenantId)</c> and drops the second fire silently.
    /// This is the "debounce" semantic the alert plumbing relies on
    /// to avoid spamming an operator with one alert per matching
    /// event past the threshold.
    /// </summary>
    [Test]
    public async Task ProcessOnce_AgentDispatchFailed3x_FourthEventWithinThrottle_DoesNotRefire()
    {
        await AddRuleAsync(MakeRule(
            "AGENT.DISPATCH.FAILED",
            predicate:
                """{"op":"count_gte","window_seconds":300,"threshold":3}""",
            throttle: 300,
            name: "agent-dispatch-failed-3x-5min"));
        await _registry.RefreshAsync(default);

        var tenantId = Guid.NewGuid();
        var start = _time.GetUtcNow().UtcDateTime;

        await AddEventAsync(MakeEvent("AGENT.DISPATCH.FAILED", tenantId, at: start.AddSeconds(0)));
        await AddEventAsync(MakeEvent("AGENT.DISPATCH.FAILED", tenantId, at: start.AddSeconds(1)));
        await AddEventAsync(MakeEvent("AGENT.DISPATCH.FAILED", tenantId, at: start.AddSeconds(2)));
        await _evaluator.ProcessOnceAsync(default);
        _sink.Raised.Should().HaveCount(1, "scenario-1 fire");

        // Fourth event 30s later — well inside the 300s throttle.
        _time.Advance(TimeSpan.FromSeconds(30));
        await AddEventAsync(MakeEvent(
            "AGENT.DISPATCH.FAILED", tenantId, at: start.AddSeconds(30)));
        await _evaluator.ProcessOnceAsync(default);

        _sink.Raised.Should().HaveCount(1,
            "throttle gate at 300s blocks a second fire — operator " +
            "shouldn't get a fresh alert for every additional failed " +
            "dispatch in the same storm");
    }

    /// <summary>
    /// The default <c>group_by=["tenantId"]</c> partitions the
    /// rolling-window counter so one event each from three different
    /// tenants does NOT hit threshold=3. Once tenant A receives two
    /// more events (count = 3 in tenant A's bucket), exactly one
    /// alert fires — and it carries tenant A's id. Tenants B and C
    /// stay silent.
    /// </summary>
    [Test]
    public async Task ProcessOnce_AgentDispatchFailed3x_CrossTenantBucketsDoNotShare()
    {
        await AddRuleAsync(MakeRule(
            "AGENT.DISPATCH.FAILED",
            predicate:
                """{"op":"count_gte","window_seconds":300,"threshold":3}""",
            throttle: 300,
            name: "agent-dispatch-failed-3x-5min"));
        await _registry.RefreshAsync(default);

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var tenantC = Guid.NewGuid();
        var now = _time.GetUtcNow().UtcDateTime;

        // One event per tenant — three buckets, each at count=1.
        await AddEventAsync(MakeEvent("AGENT.DISPATCH.FAILED", tenantA, at: now.AddSeconds(0)));
        await AddEventAsync(MakeEvent("AGENT.DISPATCH.FAILED", tenantB, at: now.AddSeconds(1)));
        await AddEventAsync(MakeEvent("AGENT.DISPATCH.FAILED", tenantC, at: now.AddSeconds(2)));

        await _evaluator.ProcessOnceAsync(default);
        _sink.Raised.Should().BeEmpty(
            "each tenant has count=1 in its own bucket — below " +
            "threshold=3");

        // Push tenant A's bucket to count=3.
        await AddEventAsync(MakeEvent("AGENT.DISPATCH.FAILED", tenantA, at: now.AddSeconds(3)));
        await AddEventAsync(MakeEvent("AGENT.DISPATCH.FAILED", tenantA, at: now.AddSeconds(4)));

        await _evaluator.ProcessOnceAsync(default);
        _sink.Raised.Should().ContainSingle(
            "only tenant A crossed threshold; B/C still at count=1");
        _sink.Raised[0].TenantId.Should().Be(tenantA,
            "alert payload carries the firing tenant's id");
    }

    // ── Test doubles ────────────────────────────────────────

    private sealed class RecordingAlertSink : IAlertSink
    {
        public List<AlertPayload> Raised { get; } = new();
        public bool ShouldThrowOnNext { get; set; }

        public Task<AlertRaiseResult> RaiseAsync(
            AlertPayload payload, CancellationToken ct = default)
        {
            if (ShouldThrowOnNext)
            {
                ShouldThrowOnNext = false;
                throw new InvalidOperationException("sink under test");
            }
            Raised.Add(payload);
            return Task.FromResult(new AlertRaiseResult(
                AlertId: Guid.NewGuid(),
                Delivered: true,
                MatchedChannels: 0,
                DroppedByRateLimit: false));
        }
    }

    private sealed class RecordingEventRepository : IEventRepository
    {
        public List<DomainEvent> Emitted { get; } = new();

        public Task<DomainEvent> AppendAsync(DomainEvent evt)
        {
            Emitted.Add(evt);
            return Task.FromResult(evt);
        }

        public Task<DomainEvent?> GetByIdAsync(Guid id) =>
            Task.FromResult<DomainEvent?>(null);

        public Task<List<DomainEvent>> QueryAsync(
            Guid? tenantId, string? type, int? issueNumber, int limit) =>
            Task.FromResult(new List<DomainEvent>());

        public Task<DomainEvent?> GetLastByTypeAsync(Guid tenantId, string type) =>
            Task.FromResult<DomainEvent?>(null);

        public Task ClearAsync(Guid tenantId) => Task.CompletedTask;

        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> ListByTenantAsync(
            Guid tenantId, string? typePrefix, int limit, int offset) =>
            Task.FromResult<(IReadOnlyList<DomainEvent>, int)>(
                (Array.Empty<DomainEvent>(), 0));
    }
}

/// <summary>
/// Postgres-backed coverage for the
/// <c>agent-dispatch-failed-3x-5min</c> built-in. Mirrors scenario-1
/// from <see cref="AlertRuleEvaluatorTests"/> but seeds via the real
/// <c>domain_events</c> table on a Testcontainers Postgres instance,
/// so we exercise the cursor + EF query path that the InMemory
/// provider can't validate (e.g. the
/// <c>e.Id.ToString().CompareTo(...)</c> tie-break clause that ships
/// to Postgres).
///
/// <para>One test only — full evaluator semantics live in the in-
/// memory fixture; this is the smoke check that the SQL translation
/// finds events seeded directly into the table.</para>
/// </summary>
[TestFixture]
public class AlertRuleEvaluatorPostgresTests
{
    [SetUp]
    public async Task SetUp()
    {
        await ApiTestFixture.ResetDatabaseAsync();
    }

    [Test]
    public async Task ProcessOnce_AgentDispatchFailed3x_PostgresBacked_FiresOnce()
    {
        // Seed a custom rule that mirrors the built-in agent-dispatch
        // shape — a custom row (not relying on the seeder being re-run
        // post-respawn) so the test owns its rule lifecycle.
        var ruleId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        using (var scope = ApiTestFixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider
                .GetRequiredService<ControlPlaneDbContext>();
            db.AlertRules.Add(new AlertRule
            {
                Id = ruleId,
                Name = "pg-agent-dispatch-failed-3x-5min",
                Description = "pg-test rule",
                IsEnabled = true,
                Severity = AlertSeverity.Warning,
                EventType = "AGENT.DISPATCH.FAILED",
                Predicate =
                    """{"op":"count_gte","window_seconds":300,"threshold":3}""",
                ThrottleSeconds = 300,
                ChannelIds = Array.Empty<Guid>(),
                IsBuiltIn = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });

            // Seed three failed-dispatch events for the same tenant
            // directly into domain_events so the evaluator's batch
            // query has to find them via the (CreatedAt, Id) cursor.
            var baseTime = DateTime.UtcNow;
            for (var i = 0; i < 3; i++)
            {
                db.DomainEvents.Add(new DomainEvent
                {
                    Id = Guid.NewGuid(),
                    Type = "AGENT.DISPATCH.FAILED",
                    TenantId = tenantId,
                    Tags = "{}",
                    Metadata = "{}",
                    Data = "{}",
                    CreatedAt = baseTime.AddMilliseconds(i * 10),
                });
            }
            await db.SaveChangesAsync();
        }

        // Build a self-contained evaluator stack pointed at the same
        // Postgres but with a unique cursor id + a recording sink so
        // we don't trip the production AlertRuleEvaluator hosted
        // service that's also running inside the WAF.
        //
        // The evaluator itself only resolves ControlPlaneDbContext +
        // IAlertRuleRegistry + IAlertSink + IRuleWindowStore +
        // IEventRepository from each scope it creates. Wrap the WAF's
        // root provider so DbContext resolution still routes through
        // EF's pooled scoped lifetime (sharing the WAF's connection
        // string), while the alert-side dependencies come from our
        // test-owned recording instances.
        var sink = new PostgresRecordingAlertSink();
        var windowStore = new InMemoryRuleWindowStore();
        var registry = new AlertRuleRegistry(
            ApiTestFixture.Factory.Services,
            NullLogger<AlertRuleRegistry>.Instance);
        var eventRepo = new SilentEventRepository();

        var sp = new EvaluatorServiceProvider(
            ApiTestFixture.Factory.Services,
            sink, windowStore, registry, eventRepo);

        await registry.RefreshAsync(default);

        var options = new AlertRuleEvaluatorOptions
        {
            PollInterval = TimeSpan.FromSeconds(1),
            RegistryRefreshInterval = TimeSpan.FromMinutes(10),
            BatchSize = 100,
            // Unique cursor id so we don't collide with the WAF's
            // hosted evaluator on the alert_evaluator_cursor PK.
            EvaluatorId = "pg-test-" + Guid.NewGuid().ToString("N")[..8],
        };
        using var evaluator = new AlertRuleEvaluator(
            sp, options, TimeProvider.System,
            NullLogger<AlertRuleEvaluator>.Instance);

        var processed = await evaluator.ProcessOnceAsync(default);

        processed.Should().BeGreaterThanOrEqualTo(3,
            "the evaluator's batch query must find all three events " +
            "via the cursor / Postgres LINQ translation");

        // Restrict assertion to fires for our specific rule, since
        // the WAF's hosted evaluator may also have processed unrelated
        // events into our recording sink path is isolated by sink
        // identity — but defensively filter by ruleId anyway.
        sink.RaisedForRule(ruleId).Should().HaveCount(1,
            "only the third event in the rolling window crosses " +
            "threshold=3 — the prior two stay below");
        sink.RaisedForRule(ruleId)[0].TenantId.Should().Be(tenantId);
    }

    private sealed class PostgresRecordingAlertSink : IAlertSink
    {
        private readonly List<AlertPayload> _raised = new();
        private readonly object _lock = new();

        public IReadOnlyList<AlertPayload> RaisedForRule(Guid ruleId)
        {
            lock (_lock)
            {
                return _raised.Where(p => p.RuleId == ruleId).ToList();
            }
        }

        public Task<AlertRaiseResult> RaiseAsync(
            AlertPayload payload, CancellationToken ct = default)
        {
            lock (_lock) { _raised.Add(payload); }
            return Task.FromResult(new AlertRaiseResult(
                AlertId: Guid.NewGuid(),
                Delivered: true,
                MatchedChannels: 0,
                DroppedByRateLimit: false));
        }
    }

    private sealed class SilentEventRepository : IEventRepository
    {
        public Task<DomainEvent> AppendAsync(DomainEvent evt) =>
            Task.FromResult(evt);
        public Task<DomainEvent?> GetByIdAsync(Guid id) =>
            Task.FromResult<DomainEvent?>(null);
        public Task<List<DomainEvent>> QueryAsync(
            Guid? tenantId, string? type, int? issueNumber, int limit) =>
            Task.FromResult(new List<DomainEvent>());
        public Task<DomainEvent?> GetLastByTypeAsync(Guid tenantId, string type) =>
            Task.FromResult<DomainEvent?>(null);
        public Task ClearAsync(Guid tenantId) => Task.CompletedTask;
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> ListByTenantAsync(
            Guid tenantId, string? typePrefix, int limit, int offset) =>
            Task.FromResult<(IReadOnlyList<DomainEvent>, int)>(
                (Array.Empty<DomainEvent>(), 0));
    }

    /// <summary>
    /// Thin wrapper that forwards <see cref="ControlPlaneDbContext"/>
    /// resolution to the WAF's underlying provider (so EF's scoped
    /// lifetime + pool still apply) while substituting the alert-side
    /// dependencies with test-owned instances. Implements
    /// <see cref="IServiceProvider"/> + <see cref="IServiceScopeFactory"/>
    /// — the only two seams <see cref="AlertRuleEvaluator"/> uses.
    /// </summary>
    private sealed class EvaluatorServiceProvider : IServiceProvider, IServiceScopeFactory
    {
        private readonly IServiceProvider _inner;
        private readonly IAlertSink _sink;
        private readonly IRuleWindowStore _windowStore;
        private readonly IAlertRuleRegistry _registry;
        private readonly IEventRepository _events;

        public EvaluatorServiceProvider(
            IServiceProvider inner,
            IAlertSink sink,
            IRuleWindowStore windowStore,
            IAlertRuleRegistry registry,
            IEventRepository events)
        {
            _inner = inner;
            _sink = sink;
            _windowStore = windowStore;
            _registry = registry;
            _events = events;
        }

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(IServiceScopeFactory)) return this;
            if (serviceType == typeof(IAlertSink)) return _sink;
            if (serviceType == typeof(IRuleWindowStore)) return _windowStore;
            if (serviceType == typeof(IAlertRuleRegistry)) return _registry;
            if (serviceType == typeof(IEventRepository)) return _events;
            return _inner.GetService(serviceType);
        }

        public IServiceScope CreateScope() =>
            new EvaluatorScope(_inner.CreateScope(), this);

        private sealed class EvaluatorScope : IServiceScope
        {
            private readonly IServiceScope _innerScope;
            private readonly EvaluatorServiceProvider _outer;

            public EvaluatorScope(
                IServiceScope innerScope,
                EvaluatorServiceProvider outer)
            {
                _innerScope = innerScope;
                _outer = outer;
                ServiceProvider = new ScopeProvider(
                    innerScope.ServiceProvider, _outer);
            }

            public IServiceProvider ServiceProvider { get; }

            public void Dispose() => _innerScope.Dispose();
        }

        private sealed class ScopeProvider : IServiceProvider
        {
            private readonly IServiceProvider _innerScope;
            private readonly EvaluatorServiceProvider _outer;

            public ScopeProvider(
                IServiceProvider innerScope,
                EvaluatorServiceProvider outer)
            {
                _innerScope = innerScope;
                _outer = outer;
            }

            public object? GetService(Type serviceType)
            {
                if (serviceType == typeof(IAlertSink)) return _outer._sink;
                if (serviceType == typeof(IRuleWindowStore)) return _outer._windowStore;
                if (serviceType == typeof(IAlertRuleRegistry)) return _outer._registry;
                if (serviceType == typeof(IEventRepository)) return _outer._events;
                return _innerScope.GetService(serviceType);
            }
        }
    }
}
