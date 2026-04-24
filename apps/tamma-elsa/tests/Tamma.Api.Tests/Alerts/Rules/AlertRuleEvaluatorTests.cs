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
        // Sequence-number cursor advanced past 0 (the initial state)
        // proves the evaluator persisted progress for the
        // domain_events stream.
        cursor.LastDomainSequenceNumber.Should().BeGreaterThan(0);
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

    [Test]
    public async Task ProcessOnce_SameTimestampBurst_NoEventSkippedOrDoubled()
    {
        // Regression: the previous (LastEventAt, LastEventId) cursor
        // tiebroke same-CreatedAt bursts via Guid.ToString() string
        // compare. String compare ≠ Guid binary order ≠ insertion
        // order, so events whose Guid string sorted ≤ the cursor
        // were permanently skipped after a crash + restart. With the
        // sequence-number cursor each event has a strictly-monotonic
        // BIGSERIAL key, immune to that bug.

        await AddRuleAsync(MakeRule("BURST.HAPPENED"));
        await _registry.RefreshAsync(default);

        var tenantId = Guid.NewGuid();
        var sameInstant = DateTime.SpecifyKind(
            new DateTime(2026, 4, 23, 12, 0, 0), DateTimeKind.Utc);

        // Three events with byte-identical CreatedAt — the bug
        // surfaces precisely on equal-timestamp inserts.
        await AddEventAsync(MakeEvent("BURST.HAPPENED", tenantId, at: sameInstant));
        await AddEventAsync(MakeEvent("BURST.HAPPENED", tenantId, at: sameInstant));
        await AddEventAsync(MakeEvent("BURST.HAPPENED", tenantId, at: sameInstant));

        // First evaluator pass: should see all three events. Throttle
        // is 0 on the rule so each match yields a sink fire.
        var firstPassProcessed = await _evaluator.ProcessOnceAsync(default);
        firstPassProcessed.Should().Be(3,
            "all three same-CreatedAt events must enter the batch");
        _sink.Raised.Should().HaveCount(3,
            "each event must fire the sink — none silently dropped");

        // Verify the cursor row was persisted with a monotonic seq#.
        long persistedSeq;
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider
                .GetRequiredService<ControlPlaneDbContext>();
            var cursor = await db.AlertEvaluatorCursors.SingleAsync();
            cursor.LastDomainSequenceNumber.Should().BeGreaterThan(0);
            persistedSeq = cursor.LastDomainSequenceNumber;
        }

        // Simulate crash + restart on the same database — a fresh
        // evaluator instance loads the persisted cursor and runs.
        var newSink = new RecordingAlertSink();
        var newSp = BuildContainerWithSharedDb(newSink);
        try
        {
            using (var scope = newSp.CreateScope())
            {
                var reg = scope.ServiceProvider
                    .GetRequiredService<IAlertRuleRegistry>();
                await reg.RefreshAsync(default);
            }
            var fresh = new AlertRuleEvaluator(
                newSp, _options, _time,
                NullLogger<AlertRuleEvaluator>.Instance);

            var secondPassProcessed = await fresh.ProcessOnceAsync(default);
            secondPassProcessed.Should().Be(0,
                "cursor advanced past every same-CreatedAt event — " +
                "no replay, no skip");
            newSink.Raised.Should().BeEmpty(
                "crash-restart must not double-fire any of the burst events");

            // And the cursor itself didn't drift.
            using (var scope = newSp.CreateScope())
            {
                var db = scope.ServiceProvider
                    .GetRequiredService<ControlPlaneDbContext>();
                var cursor = await db.AlertEvaluatorCursors.SingleAsync();
                cursor.LastDomainSequenceNumber.Should().Be(persistedSeq);
            }
        }
        finally
        {
            newSp.Dispose();
        }
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
