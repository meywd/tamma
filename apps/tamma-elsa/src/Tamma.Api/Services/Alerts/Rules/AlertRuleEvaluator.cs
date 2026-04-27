using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Alerts.Rules;

/// <summary>
/// Options for <see cref="AlertRuleEvaluator"/>.
/// </summary>
public sealed class AlertRuleEvaluatorOptions
{
    /// <summary>
    /// How often the evaluator polls for new events. Default 1
    /// second per the Wave C.2 brief.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How often the registry refreshes from the DB so admin CRUD
    /// changes propagate without a process restart. Default 30s.
    /// </summary>
    public TimeSpan RegistryRefreshInterval { get; set; } =
        TimeSpan.FromSeconds(30);

    /// <summary>
    /// Rows per poll tick. Default 100 events; bounded to keep the
    /// tick latency under ~1s on typical hardware.
    /// </summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// Stable id for this logical evaluator. One row per id in
    /// <c>alert_evaluator_cursor</c>. Default <c>"default"</c>.
    /// </summary>
    public string EvaluatorId { get; set; } = "default";

    /// <summary>
    /// When <c>true</c> (default) the evaluator's
    /// <see cref="BackgroundService.ExecuteAsync"/> polling loop runs
    /// once the host starts. Tests that drive
    /// <see cref="AlertRuleEvaluator.ProcessOnceAsync"/> directly (or
    /// don't exercise the rule engine at all) override this to
    /// <c>false</c> to skip the once-per-second background tick + the
    /// startup registry refresh round-trip.
    /// </summary>
    public bool RunOnStartup { get; set; } = true;
}

/// <summary>
/// Story 5.6 (Wave C.2) — background service that polls the DCB
/// event store, matches events against enabled
/// <see cref="IAlertRuleRegistry"/> rules, and raises alerts through
/// <see cref="IAlertSink"/>.
///
/// <para><b>Cursor crash safety</b>: the evaluator persists its
/// progress into <c>alert_evaluator_cursor</c>. On startup it reads
/// the cursor + resumes. Process kill mid-batch may reprocess events
/// past the cursor; the sink-side rate limiter + in-process throttle
/// map de-duplicate to at-most-one delivery per throttle window.</para>
///
/// <para>Per the Wave C.2 brief the evaluator polls CP-resident
/// events today: <see cref="ControlPlaneDbContext.DomainEvents"/>
/// (transitional shared-DB topology) and
/// <see cref="ControlPlaneDbContext.PlatformEvents"/> (cross-tenant
/// lifecycle events). When Story 28-1's db-per-tenant rollout
/// completes, the evaluator expands to poll per-tenant DBs through
/// <c>ITenantDbContextFactory</c>; no rule-engine logic changes.</para>
///
/// <para><b>Story 28-1 PR C audit</b>: the <c>cp.DomainEvents</c> scan
/// at <see cref="FetchBatchAsync"/> is a cross-tenant tenant-scoped
/// scan today — built-in alert rules (BUDGET.EXHAUSTED,
/// AGENT.DISPATCH.FAILED, WORKFLOW.RETRY_EXCEEDED, etc.) subscribe to
/// tenant-scoped events. Per Decision #2
/// (<c>.dev/decisions/story-28-1-design-calls.md</c>) the evaluator's
/// final shape is a per-tenant fan-out via <c>ITenantDbContextFactory</c>
/// driven off the LRU pool's known-warm tenants. That cascade lands
/// with PR D — when the entity move forces the issue. The current
/// scan stays in place under PR C: <c>cp.DomainEvents</c> still
/// exists in PR C-and-before so rules keep firing against today's
/// shared-DB topology with no behavioural change. PR D will replace
/// the line with the fan-out implementation as part of the entity
/// move; the rule pipeline downstream is independent of the scan
/// shape so the migration is contained in <c>FetchBatchAsync</c>.</para>
/// </summary>
public sealed class AlertRuleEvaluator : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly AlertRuleEvaluatorOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AlertRuleEvaluator> _logger;

    // In-process throttle: last-fired timestamp per (ruleId, groupKey).
    private readonly Dictionary<string, DateTime> _throttle = new();
    private readonly object _throttleLock = new();

    private DateTime _lastRegistryRefresh = DateTime.MinValue;

    public AlertRuleEvaluator(
        IServiceProvider services,
        AlertRuleEvaluatorOptions options,
        TimeProvider timeProvider,
        ILogger<AlertRuleEvaluator> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _services = services;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.RunOnStartup)
        {
            _logger.LogDebug(
                "AlertRuleEvaluator gated off (RunOnStartup=false); " +
                "polling loop will not start.");
            return;
        }

        _logger.LogInformation(
            "AlertRuleEvaluator starting — poll every {Interval}s, " +
            "registry refresh every {Refresh}s, batch {Batch}, " +
            "evaluatorId '{Id}'.",
            _options.PollInterval.TotalSeconds,
            _options.RegistryRefreshInterval.TotalSeconds,
            _options.BatchSize, _options.EvaluatorId);

        // Prime the registry before the first tick so a startup burst
        // of events doesn't slip past unmatched.
        await RefreshRegistryIfDueAsync(stoppingToken, force: true)
            .ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshRegistryIfDueAsync(stoppingToken, force: false)
                    .ConfigureAwait(false);
                await ProcessOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "AlertRuleEvaluator tick threw; continuing.");
            }

            try
            {
                await Task.Delay(_options.PollInterval, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("AlertRuleEvaluator shut down.");
    }

    private async Task RefreshRegistryIfDueAsync(
        CancellationToken ct, bool force)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        if (!force && now - _lastRegistryRefresh <
            _options.RegistryRefreshInterval)
        {
            return;
        }
        using var scope = _services.CreateScope();
        var registry = scope.ServiceProvider
            .GetRequiredService<IAlertRuleRegistry>();
        await registry.RefreshAsync(ct).ConfigureAwait(false);
        _lastRegistryRefresh = now;
    }

    /// <summary>
    /// Run a single evaluation tick. Public for tests so they can
    /// drive the evaluator deterministically.
    /// </summary>
    public async Task<int> ProcessOnceAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<ControlPlaneDbContext>();
        var registry = scope.ServiceProvider
            .GetRequiredService<IAlertRuleRegistry>();
        var sink = scope.ServiceProvider.GetRequiredService<IAlertSink>();
        var windowStore = scope.ServiceProvider
            .GetRequiredService<IRuleWindowStore>();

        var cursor = await LoadCursorAsync(db, ct).ConfigureAwait(false);

        var batch = await FetchBatchAsync(db, cursor, ct).ConfigureAwait(false);
        if (batch.Count == 0) return 0;

        var processed = 0;
        // Track per-stream high-water sequence numbers. Each entry in
        // the batch carries its origin stream so the cursor can
        // advance both monotonic streams independently — there is no
        // global ordering between domain_events and platform_events.
        long maxDomainSeq = cursor.LastDomainSequenceNumber;
        long maxPlatformSeq = cursor.LastPlatformSequenceNumber;

        foreach (var item in batch)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                await ProcessEventAsync(
                        item.Event, registry, sink, windowStore, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Rule evaluation for event {EventId} ({Type}) threw; " +
                    "continuing.", item.Event.Id, item.Event.Type);
            }

            if (item.Source == EventSource.Domain &&
                item.Event.SequenceNumber > maxDomainSeq)
            {
                maxDomainSeq = item.Event.SequenceNumber;
            }
            else if (item.Source == EventSource.Platform &&
                item.Event.SequenceNumber > maxPlatformSeq)
            {
                maxPlatformSeq = item.Event.SequenceNumber;
            }
            processed++;
        }

        if (processed > 0)
        {
            await SaveCursorAsync(db, maxDomainSeq, maxPlatformSeq, ct)
                .ConfigureAwait(false);
        }
        return processed;
    }

    private async Task ProcessEventAsync(
        DomainEvent evt,
        IAlertRuleRegistry registry,
        IAlertSink sink,
        IRuleWindowStore windowStore,
        CancellationToken ct)
    {
        var rules = registry.GetRulesForEventType(evt.Type);
        if (rules.Count == 0) return;

        foreach (var rule in rules)
        {
            // Don't feed an alert lifecycle event back into a rule
            // that subscribes to it — that path self-excites. We
            // filter out ALERT.* events here as a safety interlock
            // regardless of what the rule claims to subscribe to.
            if (evt.Type.StartsWith("ALERT.", StringComparison.Ordinal))
                continue;

            AlertPayload? payload;
            try
            {
                var ctx = new AlertRuleContext(rule.Id, evt, windowStore);
                payload = rule.Evaluate(ctx);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Rule {RuleId} threw on event {EventId}; skipping.",
                    rule.Id, evt.Id);
                continue;
            }
            if (payload is null) continue;

            // Throttle check: drop the fire if we're still within the
            // rule's throttle window for this correlation group.
            if (!ShouldFireAfterThrottle(rule, payload))
            {
                _logger.LogDebug(
                    "Rule {RuleId} throttled — skipping fire.", rule.Id);
                continue;
            }

            try
            {
                await sink.RaiseAsync(payload, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "IAlertSink.RaiseAsync failed for rule {RuleId}.",
                    rule.Id);
                // Don't rethrow — one bad sink invocation shouldn't
                // halt the batch.
                continue;
            }

            // Emit a RULE.MATCHED DCB event for audit / drift
            // inspection. Fire-and-log-on-failure (we're already
            // inside the evaluator — an event-emit failure is a
            // secondary signal).
            await TryEmitMatchedAsync(rule, evt, ct).ConfigureAwait(false);
        }
    }

    private bool ShouldFireAfterThrottle(
        DatabaseBackedAlertRule rule, AlertPayload payload)
    {
        if (rule.ThrottleSeconds <= 0) return true;

        // Throttle key = rule + tenantId (so two tenants don't share
        // a single throttle; matches the count_gte default group_by).
        var key = $"{rule.Id:N}:{payload.TenantId?.ToString("N") ?? "(platform)"}";
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var throttleUntil = TimeSpan.FromSeconds(rule.ThrottleSeconds);

        lock (_throttleLock)
        {
            if (_throttle.TryGetValue(key, out var lastFire) &&
                now - lastFire < throttleUntil)
            {
                return false;
            }
            _throttle[key] = now;

            // Opportunistic cleanup of stale throttle entries. Drop
            // anything that hasn't fired in >1 hour.
            if (_throttle.Count > 512)
            {
                var cutoff = now - TimeSpan.FromHours(1);
                var toRemove = new List<string>();
                foreach (var kv in _throttle)
                {
                    if (kv.Value < cutoff) toRemove.Add(kv.Key);
                }
                foreach (var k in toRemove) _throttle.Remove(k);
            }
            return true;
        }
    }

    private async Task TryEmitMatchedAsync(
        DatabaseBackedAlertRule rule, DomainEvent evt, CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var events = scope.ServiceProvider
                .GetRequiredService<IEventRepository>();
            await events.AppendAsync(new DomainEvent
            {
                Type = "RULE.MATCHED",
                TenantId = evt.TenantId,
                Tags = JsonSerializer.Serialize(new Dictionary<string, string?>
                {
                    ["ruleId"] = rule.Id.ToString("N"),
                    ["ruleName"] = rule.Name,
                    ["sourceEventId"] = evt.Id.ToString("N"),
                    ["sourceEventType"] = evt.Type,
                }),
                Metadata = """{"eventSource":"system"}""",
                Data = JsonSerializer.Serialize(new
                {
                    severity = rule.Severity,
                }),
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "RULE.MATCHED emission failed for rule {RuleId}.",
                rule.Id);
        }
        _ = ct;  // accept ct parameter for future cancellation support
    }

    private async Task<CursorState> LoadCursorAsync(
        ControlPlaneDbContext db, CancellationToken ct)
    {
        var row = await db.AlertEvaluatorCursors
            .AsNoTracking()
            .FirstOrDefaultAsync(
                c => c.EvaluatorId == _options.EvaluatorId, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return new CursorState(0L, 0L);
        }
        return new CursorState(
            row.LastDomainSequenceNumber, row.LastPlatformSequenceNumber);
    }

    private async Task SaveCursorAsync(
        ControlPlaneDbContext db,
        long lastDomainSeq,
        long lastPlatformSeq,
        CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var existing = await db.AlertEvaluatorCursors
            .FirstOrDefaultAsync(
                c => c.EvaluatorId == _options.EvaluatorId, ct)
            .ConfigureAwait(false);
        if (existing is null)
        {
            db.AlertEvaluatorCursors.Add(new AlertEvaluatorCursor
            {
                EvaluatorId = _options.EvaluatorId,
                LastDomainSequenceNumber = lastDomainSeq,
                LastPlatformSequenceNumber = lastPlatformSeq,
                UpdatedAt = now,
            });
        }
        else
        {
            existing.LastDomainSequenceNumber = lastDomainSeq;
            existing.LastPlatformSequenceNumber = lastPlatformSeq;
            existing.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task<List<BatchItem>> FetchBatchAsync(
        ControlPlaneDbContext db, CursorState cursor, CancellationToken ct)
    {
        // Events strictly after the cursor in stream order. The
        // monotonic SequenceNumber column (BIGSERIAL identity, set
        // server-side on INSERT) is immune to same-millisecond
        // CreatedAt collisions that previously dropped events when
        // the tiebreak was a Guid lexicographic compare. Each stream
        // tracks its own cursor because their sequences are
        // independent — there is no global ordering between
        // domain_events and platform_events.
        //
        // Story 28-1 PR C — the DomainEvents scan below is a
        // cross-tenant tenant-scoped scan that PR D rewires into a
        // per-tenant fan-out via ITenantDbContextFactory (see class
        // doc comment). PR C deliberately leaves the line in place:
        // cp.DomainEvents still exists today, the scan still produces
        // the right rows, and replacing the scan + the cursor model
        // is non-trivial enough that bundling it with the entity move
        // (PR D) keeps the diff reviewable.
        var domain = await db.DomainEvents.AsNoTracking()
            .IgnoreQueryFilters()
            .Where(e => e.SequenceNumber > cursor.LastDomainSequenceNumber)
            .OrderBy(e => e.SequenceNumber)
            .Take(_options.BatchSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // PlatformEvents share the same shape — project into
        // DomainEvent for uniform rule evaluation.
        var platform = await db.PlatformEvents.AsNoTracking()
            .Where(e => e.SequenceNumber > cursor.LastPlatformSequenceNumber)
            .OrderBy(e => e.SequenceNumber)
            .Take(_options.BatchSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var items = new List<BatchItem>(domain.Count + platform.Count);
        foreach (var d in domain)
        {
            items.Add(new BatchItem(d, EventSource.Domain));
        }
        foreach (var p in platform)
        {
            // Project the platform-event row into a DomainEvent shape
            // so the rule pipeline stays uniform. SequenceNumber rides
            // along so the cursor can pick the right per-stream high
            // water mark after the rule loop.
            var projected = new DomainEvent
            {
                Id = p.Id,
                Type = p.Type,
                TenantId = p.TenantId,
                Tags = p.Tags,
                Metadata = p.Metadata,
                Data = p.Data,
                CreatedAt = p.CreatedAt,
                SequenceNumber = p.SequenceNumber,
            };
            items.Add(new BatchItem(projected, EventSource.Platform));
        }

        // Stable ordering across the two streams so equal-CreatedAt
        // bursts have a deterministic interleave; the cursor advance
        // is per-stream so the chosen interleave doesn't influence
        // resume correctness.
        items.Sort((a, b) =>
        {
            var c = a.Event.CreatedAt.CompareTo(b.Event.CreatedAt);
            if (c != 0) return c;
            // Within a same-CreatedAt tie, domain events come first;
            // within a single stream, the per-stream SequenceNumber
            // already gave us order from the DB query.
            c = ((int)a.Source).CompareTo((int)b.Source);
            if (c != 0) return c;
            return a.Event.SequenceNumber.CompareTo(b.Event.SequenceNumber);
        });

        if (items.Count > _options.BatchSize)
        {
            items.RemoveRange(
                _options.BatchSize, items.Count - _options.BatchSize);
        }
        return items;
    }

    private enum EventSource
    {
        Domain = 0,
        Platform = 1,
    }

    private readonly record struct BatchItem(
        DomainEvent Event, EventSource Source);

    private readonly record struct CursorState(
        long LastDomainSequenceNumber, long LastPlatformSequenceNumber);
}
