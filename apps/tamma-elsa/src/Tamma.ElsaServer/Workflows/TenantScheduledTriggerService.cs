using System.Text.Json;
using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Requests;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tamma.Activities.Scheduling;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.ElsaServer.Workflows.Helpers;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Options for <see cref="TenantScheduledTriggerService"/>. Bound to the
/// <c>ScheduledTriggers</c> configuration section.
/// </summary>
public sealed class TenantScheduledTriggerOptions
{
    public const string SectionName = "ScheduledTriggers";

    /// <summary>
    /// <b>Default <c>false</c></b> (AC9 — the
    /// <c>SecretAutoRotationScheduler</c> precedent, NOT the rollup
    /// scheduler's <c>true</c>): landing the seam changes no running
    /// deployment's behaviour until an operator opts in.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Tick cadence. Worst-case extra fire latency is one interval.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Bounds a cold start on a large fleet (D5).</summary>
    public int MaxFiresPerTick { get; set; } = 50;

    /// <summary>
    /// Per-dispatch timeout (MODERATE-3 fix, 2026-07-29). One hung
    /// <c>IWorkflowDispatcher.DispatchAsync</c> call would otherwise stall
    /// every remaining tenant on the pod for the rest of the tick while
    /// holding the advisory-lock connection. On timeout the fire is stamped
    /// <c>failed</c> (burn-the-window, Correction 4 — the NEXT window is the
    /// recovery path) and the loop continues. Non-positive disables the
    /// timeout.
    /// </summary>
    public TimeSpan DispatchTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Fire-ledger retention window (D2).</summary>
    public TimeSpan LedgerRetention { get; set; } = TimeSpan.FromDays(90);

    /// <summary>
    /// LOW-8 fix (2026-07-30) — how long a ledger row may sit in
    /// <c>Outcome = 'claimed'</c> before the tick's stale-claim sweep
    /// declares its owning pod dead, burns the row (<c>failed</c>) and emits
    /// <c>SCHEDULE.FIRE.ABANDONED</c>. Must comfortably exceed
    /// <see cref="DispatchTimeout"/> — an in-flight dispatch is legitimately
    /// <c>claimed</c> — hence the 15-minute default against a 30-second
    /// dispatch timeout. Non-positive disables the sweep.
    /// </summary>
    public TimeSpan StaleClaimThreshold { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// LOW-8 — per-tick bound on the stale-claim sweep, so a pathological
    /// backlog (a pod that died holding hundreds of claims) is announced over
    /// several ticks rather than in one unbounded burst. The sweep is NOT a
    /// retry: it only stamps and reports.
    /// </summary>
    public int MaxStaleClaimsSweptPerTick { get; set; } = 50;
}

/// <summary>
/// Story 41-30 — the tenant-aware scheduled-trigger seam: dispatches ANY
/// workflow definition, per tenant, per cron window, AT MOST ONCE across the
/// fleet, durably. One hosted service + two control-plane tables
/// (<c>scheduled_triggers</c> registry, <c>scheduled_trigger_fires</c>
/// ledger) + an admin API — deliberately NOT a workflow (a workflow cannot
/// start itself on a cadence, and a coordinator workflow doing the fan-out
/// would reproduce the single-slot <c>PlatformTaskWorker</c> block-poll
/// hazard, D11).
///
/// <para><b>Why not Elsa's own scheduling</b> (Correction 2): the
/// <c>Cron</c> trigger activity arms one trigger per workflow DEFINITION —
/// no tenant dimension, no runtime tenant fan-out — and the shipped
/// <c>IScheduler</c> is <c>LocalScheduler</c>, in-process, so an N-pod
/// deploy arms N copies. This seam uses Elsa's (transitive) cron PARSER via
/// <see cref="ScheduleWindowCalculator"/>, never Elsa's scheduler.</para>
///
/// <para><b>At-most-once, two mechanisms, both required</b> (Correction 3):
/// per due window the order is <i>advisory lock → ledger claim → dispatch →
/// stamp outcome → release</i>. The tenant-scoped advisory lock
/// (<see cref="ScheduleLockKey"/> — tenant + trigger + window, fixing the
/// tenant-less key at <c>HourlyAnalyticsRollupScheduler.cs:241</c>) keeps
/// concurrent pods off the same window cheaply; the committed
/// <c>ON CONFLICT DO NOTHING</c> ledger row is what survives a pod crash
/// (a session-scoped lock does not). Exactly-once is impossible across a
/// process boundary (Correction 4): a lost fire is surfaced — by the tick's
/// stale-claim sweep, since the dead process cannot surface itself — as
/// <c>SCHEDULE.FIRE.ABANDONED</c> over a burnt ledger row, and the NEXT
/// window is the recovery path, never a silent same-window retry.</para>
///
/// <para><b>The at-most-once unit is the TRIGGER ROW</b> (LOW-7, decided
/// 2026-07-30): <c>(triggerId, windowKey)</c> for both the ledger's unique
/// index and the advisory-lock key — NOT
/// <c>(tenantId, definitionId, windowKey)</c>. A trigger row is a schedule,
/// and one tenant may deliberately run one definition on two cadences with
/// two payloads; a definition-level key would make one of those silently
/// swallow the other. Consumers receive <c>triggerId</c> as an input and pick
/// their own idempotency scope — see
/// <see cref="Tamma.Data.Abstractions.IScheduledTriggerRepository.TryClaimFireAsync"/>.</para>
///
/// <para><b>Failure isolation:</b> one trigger's failure never aborts the
/// tick (per-row, the <c>ListPendingFromAnyTenantAsync</c> discipline).
/// <b>Bounded catch-up</b> (D7): after an outage only the MOST RECENT missed
/// window fires; the dropped windows are recorded with
/// <c>SCHEDULE.WINDOW.SKIPPED</c> so the gap is auditable.</para>
///
/// <para><b>One window, one audit row</b> (MODERATE-5 fix, 2026-07-30): every
/// <c>SCHEDULE.*</c> emission on the fire path hangs off a state transition
/// that can only happen once per <c>(trigger, window)</c> — the skip audit
/// off the won ledger claim, the terminal audit off the outcome stamp, the
/// abandonment audit off the sweep's CAS — and a window that already reached
/// a terminal ledger outcome is re-observed silently. A failed daily window
/// used to re-emit its skip + suppression on all ~1440 of that day's ticks,
/// per pod.</para>
/// </summary>
public sealed class TenantScheduledTriggerService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IOptions<TenantScheduledTriggerOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TenantScheduledTriggerService> _logger;
    private readonly IRollupSchedulerLeaderLock _leaderLock;

    public TenantScheduledTriggerService(
        IServiceProvider services,
        IOptions<TenantScheduledTriggerOptions> options,
        TimeProvider timeProvider,
        ILogger<TenantScheduledTriggerService> logger,
        IConfiguration? configuration = null,
        IRollupSchedulerLeaderLock? leaderLock = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _services = services;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        // Reuse the landed advisory-lock primitive verbatim (D4) — only the
        // KEY derivation was wrong for a tenant-aware seam, and that lives in
        // ScheduleLockKey. Tests inject a deterministic in-memory lock.
        _leaderLock = leaderLock ?? new PostgresAdvisoryLeaderLock(configuration);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = _options.Value;
        if (!opts.Enabled)
        {
            // AC9 — disabled by default; landing the story changes nothing
            // until an operator opts in (ScheduledTriggers:Enabled=true).
            _logger.LogInformation(
                "TenantScheduledTriggerService disabled (Enabled=false) — no schedules will fire.");
            return;
        }

        _logger.LogInformation(
            "TenantScheduledTriggerService running poll={PollSeconds}s maxFiresPerTick={MaxFires} retention={RetentionDays}d",
            opts.PollInterval.TotalSeconds, opts.MaxFiresPerTick, opts.LedgerRetention.TotalDays);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(opts, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TenantScheduledTriggerService tick threw; continuing.");
            }

            try
            {
                await Task.Delay(opts.PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("TenantScheduledTriggerService shut down.");
    }

    /// <summary>
    /// Test-only single-tick entry point (the exact seam
    /// <c>HourlyAnalyticsRollupScheduler</c> exposes;
    /// <c>InternalsVisibleTo("Tamma.Activities.Tests")</c> is already granted
    /// by the project). Returns the number of dispatches performed.
    /// </summary>
    internal Task<int> InvokeTickForTestsAsync(CancellationToken ct)
        => TickAsync(_options.Value, ct);

    private async Task<int> TickAsync(TenantScheduledTriggerOptions opts, CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetService<IScheduledTriggerRepository>();
        if (repository is null)
        {
            // No control plane wired (dev composition without
            // ConnectionStrings:ControlPlane) — nothing to schedule. Not an
            // error; mirrors SecretAutoRotationScheduler's null-dbFactory arm.
            return 0;
        }

        var dispatcher = scope.ServiceProvider.GetRequiredService<IWorkflowDispatcher>();
        // 2026-08-13 — resolves definition VERSION ids for dispatch (see
        // PublishedWorkflowDispatch; the request ctor takes the version id).
        var definitionService = scope.ServiceProvider
            .GetRequiredService<Elsa.Workflows.Management.IWorkflowDefinitionService>();
        var events = scope.ServiceProvider.GetService<IPlatformEventPublisher>();
        var now = _timeProvider.GetUtcNow();

        // 1–2. Active-tenant snapshot, then template materialisation (D6) so
        // a platform-default schedule reaches tenants created since the last
        // tick before the due computation below sees the concrete rows.
        var activeTenantIds = await repository.SnapshotActiveTenantIdsAsync(ct)
            .ConfigureAwait(false);
        try
        {
            await repository.MaterialiseTemplatesAsync(activeTenantIds, now.UtcDateTime, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // MODERATE-4 fix (2026-07-29): materialisation is INSIDE the
            // tick's failure isolation. A poison template row (the repository
            // additionally isolates per template) or a transient DB error here
            // must not abort the tick — the already-materialised concrete
            // triggers below still fire, and the next tick retries
            // materialisation.
            _logger.LogWarning(ex,
                "schedule.materialise.failed — template materialisation failed; continuing the tick with existing concrete triggers.");
        }

        var triggers = await repository
            .ListEnabledConcreteTriggersAsync(activeTenantIds, ct)
            .ConfigureAwait(false);

        var dispatched = 0;
        foreach (var trigger in triggers)
        {
            if (ct.IsCancellationRequested) break;
            if (dispatched >= opts.MaxFiresPerTick)
            {
                _logger.LogInformation(
                    "schedule.tick.fire_budget_exhausted maxFiresPerTick={Max} — remaining due triggers roll to the next tick.",
                    opts.MaxFiresPerTick);
                break;
            }

            try
            {
                if (await FireDueWindowAsync(repository, dispatcher, definitionService, events, trigger, now, ct)
                        .ConfigureAwait(false))
                {
                    dispatched++;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Failure isolation (D5 step 6): one trigger's failure never
                // aborts the tick — WARN + SCHEDULE.FIRE.FAILED, continue.
                _logger.LogWarning(ex,
                    "schedule.fire.trigger_failed trigger={TriggerId} tenant={TenantId} definition={DefinitionId}",
                    trigger.Id, trigger.TenantId, trigger.DefinitionId);
                await EmitAsync(events, ScheduleEvents.FireFailed, trigger.TenantId,
                    trigger, windowKey: null, data: new { error = ex.Message }, ct)
                    .ConfigureAwait(false);
            }
        }

        // Admin run-now claims (D8): drain manual:{timestamp} ledger rows the
        // API claimed, through the same dispatch + stamp path.
        dispatched += await DrainManualFiresAsync(repository, dispatcher, definitionService, events, opts, dispatched, ct)
            .ConfigureAwait(false);

        // LOW-8 — give the claim-then-crash contract a real surface.
        await SweepStaleClaimsAsync(repository, events, opts, now, ct).ConfigureAwait(false);

        // 7. Bounded ledger retention.
        await repository.PruneLedgerAsync(
                now.UtcDateTime - opts.LedgerRetention, maxRows: 1000, ct)
            .ConfigureAwait(false);

        return dispatched;
    }

    /// <summary>Returns true when a dispatch happened for this trigger.</summary>
    private async Task<bool> FireDueWindowAsync(
        IScheduledTriggerRepository repository,
        IWorkflowDispatcher dispatcher,
        Elsa.Workflows.Management.IWorkflowDefinitionService definitionService,
        IPlatformEventPublisher? events,
        ScheduledTrigger trigger,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var since = new DateTimeOffset(
            DateTime.SpecifyKind(trigger.LastFiredAt ?? trigger.CreatedAt, DateTimeKind.Utc));
        // MAJOR-2 fix (2026-07-29): ComputeDue guarantees LastWindow is the
        // TRUE most recent due occurrence even when the backlog exceeds the
        // counting cap (the old capped ascending list held the OLDEST 1000,
        // so a >16.7h minutely backlog fired a ~7h-stale window).
        var due = ScheduleWindowCalculator.ComputeDue(trigger.CronExpression, since, now);
        if (due.LastWindow is not { } window) return false;

        var windowKey = ScheduleWindowCalculator.WindowKey(window);
        var tenantId = trigger.TenantId!.Value;

        // MODERATE-5 fix (2026-07-30) — the SETTLED-WINDOW short circuit, and
        // the reason it has to come first.
        //
        // `since` is the trigger row's LastFiredAt, which only advances on a
        // SUCCESSFUL dispatch. So a window whose dispatch FAILED keeps
        // recomputing as the due window for the rest of its cadence (a whole
        // day, for a daily schedule). Pre-fix, each of those ~1440 ticks per
        // pod re-emitted the same SCHEDULE.WINDOW.SKIPPED, re-took the
        // advisory lock, lost the ledger claim and wrote yet another
        // SCHEDULE.FIRE.SUPPRESSED row. At-most-once was never in danger —
        // the committed ledger row is what refuses the re-dispatch — but the
        // audit trail degraded into noise, which is the one thing this seam's
        // event stream exists for.
        //
        // Asking the ledger for the window's outcome up front makes a settled
        // (dispatched / failed / abandoned-and-burnt) window SILENT: no lock,
        // no claim, no event. What remains reaches the lock only while a
        // claim is genuinely in flight, so a SUPPRESSED row now means real
        // concurrency instead of a stuck trigger's heartbeat.
        var settled = await repository.GetFireOutcomeAsync(trigger.Id, windowKey, ct)
            .ConfigureAwait(false);
        if (settled is "dispatched" or "failed")
        {
            _logger.LogDebug(
                "schedule.fire.window_settled trigger={TriggerId} tenant={TenantId} window={WindowKey} outcome={Outcome} — already terminal; the NEXT window is the recovery path",
                trigger.Id, tenantId, windowKey, settled);
            return false;
        }

        // D4 order of operations: lock → claim → dispatch → stamp → release.
        // The KEY carries the tenant (AC2) so tenant A's leader never
        // suppresses tenant B's fire on the same window.
        var lockKey = ScheduleLockKey.Compute(tenantId, trigger.Id, windowKey);
        await using var lease = await _leaderLock.TryAcquireAsync(lockKey, ct)
            .ConfigureAwait(false);
        if (lease is null)
        {
            // Another pod holds this (tenant, trigger, window) right now —
            // INFO, not an error: suppression is the contract working.
            _logger.LogInformation(
                "schedule.fire.suppressed_not_leader trigger={TriggerId} tenant={TenantId} window={WindowKey} lockKey={LockKey}",
                trigger.Id, tenantId, windowKey, lockKey);
            await EmitAsync(events, ScheduleEvents.FireSuppressed, tenantId, trigger, windowKey,
                data: new { reason = "advisory_lock_held" }, ct).ConfigureAwait(false);
            return false;
        }

        var fire = new ScheduledTriggerFire
        {
            Id = Guid.NewGuid(),
            TriggerId = trigger.Id,
            TenantId = tenantId,
            DefinitionId = trigger.DefinitionId,
            WindowKey = windowKey,
            ClaimedAt = now.UtcDateTime,
        };
        if (!await repository.TryClaimFireAsync(fire, ct).ConfigureAwait(false))
        {
            // The durable half of at-most-once (Correction 3): a committed
            // ledger row from another pod / a pre-crash run of this pod owns
            // the window. Sequential double-fire dies HERE, not at the lock.
            _logger.LogInformation(
                "schedule.fire.suppressed_already_claimed trigger={TriggerId} tenant={TenantId} window={WindowKey}",
                trigger.Id, tenantId, windowKey);
            await EmitAsync(events, ScheduleEvents.FireSuppressed, tenantId, trigger, windowKey,
                data: new { reason = "window_already_claimed" }, ct).ConfigureAwait(false);
            return false;
        }

        // D7 — bounded catch-up: fire ONLY the most recent window; record the
        // gap LOUDLY so it is auditable rather than invisible. When the count
        // saturated, skippedCount is a floor ("at least N") and the event says
        // so via skippedCountSaturated.
        //
        // MODERATE-5 fix (2026-07-30): emitted AFTER the claim is won, not
        // before the lock. The claim is this seam's at-most-once arbiter, so
        // hanging the emission off it gives the skip audit the same
        // guarantee the fire itself has — exactly one SCHEDULE.WINDOW.SKIPPED
        // per (trigger, window), fleet-wide, no matter how many pods tick or
        // how long the backlog persists.
        if (due.DueCount > 1)
        {
            var skipped = due.DueCount - 1;
            var firstSkippedKey = ScheduleWindowCalculator.WindowKey(due.FirstWindow!.Value);
            var lastSkippedKey = ScheduleWindowCalculator.WindowKey(due.PreviousWindow!.Value);
            _logger.LogWarning(
                "schedule.window.skipped trigger={TriggerId} tenant={TenantId} window={WindowKey} skippedCount={Skipped} saturated={Saturated} first={First} last={Last}",
                trigger.Id, tenantId, windowKey, skipped, due.CountSaturated,
                firstSkippedKey, lastSkippedKey);
            await EmitAsync(events, ScheduleEvents.WindowSkipped, tenantId, trigger,
                windowKey,
                data: new
                {
                    skippedCount = skipped,
                    skippedCountSaturated = due.CountSaturated,
                    firstSkippedWindowKey = firstSkippedKey,
                    lastSkippedWindowKey = lastSkippedKey,
                }, ct).ConfigureAwait(false);
        }

        return await DispatchAndStampAsync(
            repository, dispatcher, definitionService, events, trigger, fire, now, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// LOW-8 fix (2026-07-30) — the claim-then-crash surface.
    ///
    /// <para>The at-most-once contract has always said that a pod dying
    /// between the ledger claim and the dispatch LOSES that fire, and that the
    /// loss is "surfaced as a claimed row that never became dispatched". It
    /// was not: nothing looked. The crashing process cannot emit its own
    /// <c>SCHEDULE.FIRE.FAILED</c> (it is gone), and no sweep, metric, log or
    /// alert ever inspected stale <c>claimed</c> rows — the only way to find
    /// one was manual SQL against the ledger.</para>
    ///
    /// <para>This is that surface: bounded
    /// (<see cref="TenantScheduledTriggerOptions.MaxStaleClaimsSweptPerTick"/>
    /// rows per tick), thresholded
    /// (<see cref="TenantScheduledTriggerOptions.StaleClaimThreshold"/>, which
    /// must exceed the dispatch timeout so an in-flight claim is never
    /// mistaken for an abandoned one), and deliberately NOT a retry — it
    /// stamps the row <c>failed</c> and emits
    /// <c>SCHEDULE.FIRE.ABANDONED</c> + a WARN. At-most-once means the window
    /// is burnt; the NEXT window is the recovery path. Stamping the terminal
    /// outcome is also what keeps the sweep emit-once: a swept row is never
    /// seen again.</para>
    /// </summary>
    private async Task SweepStaleClaimsAsync(
        IScheduledTriggerRepository repository,
        IPlatformEventPublisher? events,
        TenantScheduledTriggerOptions opts,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (opts.StaleClaimThreshold <= TimeSpan.Zero) return;
        if (opts.MaxStaleClaimsSweptPerTick <= 0) return;

        var cutoff = now.UtcDateTime - opts.StaleClaimThreshold;
        IReadOnlyList<ScheduledTriggerFire> stale;
        try
        {
            stale = await repository
                .ListStaleClaimedFiresAsync(cutoff, opts.MaxStaleClaimsSweptPerTick, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "schedule.stale_claim.sweep_failed — continuing; the next tick retries.");
            return;
        }

        foreach (var fire in stale)
        {
            if (ct.IsCancellationRequested) break;
            var age = now.UtcDateTime - fire.ClaimedAt;
            var detail =
                $"abandoned: claimed {age.TotalMinutes:F0}m ago and never stamped "
                + $"(threshold {opts.StaleClaimThreshold}); the owning process did not survive "
                + "its dispatch. Window burnt — at-most-once forbids a retry.";
            try
            {
                if (!await repository.TryMarkFireAbandonedAsync(fire.Id, detail, ct)
                        .ConfigureAwait(false))
                {
                    // Another pod's sweep (or a late outcome stamp) got there
                    // first — it owns the announcement.
                    continue;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "schedule.stale_claim.stamp_failed fire={FireId} trigger={TriggerId}",
                    fire.Id, fire.TriggerId);
                continue;
            }

            _logger.LogWarning(
                "schedule.fire.abandoned fire={FireId} trigger={TriggerId} tenant={TenantId} definition={DefinitionId} window={WindowKey} claimedAt={ClaimedAt:O} ageMinutes={AgeMinutes:F0} — a pod claimed this window and died before dispatching; the window is BURNT, the next window is the recovery path",
                fire.Id, fire.TriggerId, fire.TenantId, fire.DefinitionId, fire.WindowKey,
                fire.ClaimedAt, age.TotalMinutes);

            await EmitAbandonedAsync(events, fire, age, opts, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// LOW-8 — the abandoned-fire audit row. Emitted straight off the ledger
    /// row (the trigger row may since have been edited or deleted), so this
    /// does not reuse <see cref="EmitAsync"/>'s trigger-shaped tags.
    /// </summary>
    private async Task EmitAbandonedAsync(
        IPlatformEventPublisher? events,
        ScheduledTriggerFire fire,
        TimeSpan age,
        TenantScheduledTriggerOptions opts,
        CancellationToken ct)
    {
        if (events is null) return;
        try
        {
            await events.AppendAndPublishAsync(new PlatformEvent
            {
                Type = ScheduleEvents.FireAbandoned,
                TenantId = fire.TenantId,
                Tags = JsonSerializer.Serialize(new
                {
                    tenantId = fire.TenantId.ToString("D"),
                    definitionId = fire.DefinitionId,
                    windowKey = fire.WindowKey,
                    triggerId = fire.TriggerId.ToString("D"),
                    status = ScheduleEvents.StatusForEvent(ScheduleEvents.FireAbandoned),
                }),
                Metadata = "{\"eventSource\":\"system\",\"emitter\":\"TenantScheduledTriggerService\"}",
                Data = JsonSerializer.Serialize(new
                {
                    fireId = fire.Id.ToString("D"),
                    claimedAt = fire.ClaimedAt.ToString("O"),
                    ageMinutes = Math.Round(age.TotalMinutes, 1),
                    thresholdMinutes = opts.StaleClaimThreshold.TotalMinutes,
                    // A manual fire that was CAS-marked and then lost its pod
                    // is distinguishable here (D8 / MAJOR-1): DispatchedAt is
                    // the drain's marker, not a dispatch time.
                    dispatchAttempted = fire.DispatchedAt is not null,
                    retried = false,
                }),
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "schedule.events.emit_failed type={Type}",
                ScheduleEvents.FireAbandoned);
        }
    }

    private async Task<int> DrainManualFiresAsync(
        IScheduledTriggerRepository repository,
        IWorkflowDispatcher dispatcher,
        Elsa.Workflows.Management.IWorkflowDefinitionService definitionService,
        IPlatformEventPublisher? events,
        TenantScheduledTriggerOptions opts,
        int alreadyDispatched,
        CancellationToken ct)
    {
        var budget = opts.MaxFiresPerTick - alreadyDispatched;
        if (budget <= 0) return 0;

        var manual = await repository.ListPendingManualFiresAsync(budget, ct)
            .ConfigureAwait(false);
        var dispatched = 0;
        foreach (var (fire, trigger) in manual)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                // MAJOR-1 fix (2026-07-29): the list above is an unclaimed
                // read — two pods ticking concurrently both see the same
                // pending row. This CAS is the arbiter: exactly one pod wins
                // the dispatch attempt (DispatchedAt stamped while Outcome is
                // still 'claimed'); losers skip. A crash — or a failed
                // outcome stamp — after a won CAS BURNS the fire (the
                // pending list filters DispatchedAt IS NULL), matching the
                // cron path's burn-the-window at-most-once semantics; a
                // pending manual row can never be dispatched twice nor loop.
                if (!await repository.TryClaimManualFireForDispatchAsync(
                        fire.Id, _timeProvider.GetUtcNow().UtcDateTime, ct)
                        .ConfigureAwait(false))
                {
                    continue;
                }

                if (await DispatchAndStampAsync(
                        repository, dispatcher, definitionService, events, trigger, fire,
                        _timeProvider.GetUtcNow(), ct).ConfigureAwait(false))
                {
                    dispatched++;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "schedule.fire.manual_failed fire={FireId} trigger={TriggerId}",
                    fire.Id, fire.TriggerId);
            }
        }

        return dispatched;
    }

    /// <summary>
    /// Dispatch the claimed fire and stamp its outcome. AC4: <c>tenantId</c>
    /// and <c>windowKey</c> reach the workflow as INPUTS, alongside the row's
    /// <c>input_json</c> (merged; the reserved keys win). The target's own
    /// idempotency under a replayed <c>windowKey</c> is the CONSUMER's
    /// contract (41-20 D3) — this seam only guarantees it is not CALLED twice.
    /// </summary>
    private async Task<bool> DispatchAndStampAsync(
        IScheduledTriggerRepository repository,
        IWorkflowDispatcher dispatcher,
        Elsa.Workflows.Management.IWorkflowDefinitionService definitionService,
        IPlatformEventPublisher? events,
        ScheduledTrigger trigger,
        ScheduledTriggerFire fire,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var input = BuildInput(trigger, fire);
        var instanceId = Guid.NewGuid().ToString();
        // The definition id is ROW DATA (AC3) — this service must never name
        // a consumer workflow's DefinitionId constant.
        // 2026-08-13 — the request ctor takes the VERSION id, not the definition
        // id (see PublishedWorkflowDispatch); resolve the published version first
        // or every fire dies WorkflowGraphNotFound in the dispatch queue.
        var definitionVersionId = await Tamma.Activities.Core.PublishedWorkflowDispatch
            .ResolvePublishedVersionIdAsync(definitionService, fire.DefinitionId, ct);
        var request = new DispatchWorkflowDefinitionRequest(definitionVersionId)
        {
            InstanceId = instanceId,
            Input = input,
        };

        // MODERATE-3 fix (2026-07-29): per-dispatch timeout. The linked CTS
        // cancels a cooperative dispatcher; WaitAsync additionally abandons a
        // dispatcher that IGNORES its token, so one hung dispatch cannot
        // stall the remaining tenants on the pod while holding the
        // advisory-lock connection. Timeout ⇒ stamp 'failed' (burn the
        // window) and continue the loop.
        var timeout = _options.Value.DispatchTimeout;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout > TimeSpan.Zero) timeoutCts.CancelAfter(timeout);
        try
        {
            var dispatchTask = dispatcher.DispatchAsync(
                request, new DispatchWorkflowOptions(), timeoutCts.Token);
            await (timeout > TimeSpan.Zero
                    ? dispatchTask.WaitAsync(timeout, ct)
                    : dispatchTask)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // service stop — propagate, never stamp
        }
        catch (Exception ex)
        {
            // Correction 4 — at-most-once: stamp 'failed' and let the NEXT
            // window recover; never re-dispatch this window. A timeout
            // (TimeoutException from WaitAsync, or the linked token's OCE)
            // is a failure like any other.
            timeoutCts.Cancel(); // tell an abandoned in-flight dispatch to stop
            var detail = ex is TimeoutException or OperationCanceledException
                ? $"dispatch timed out after {timeout}"
                : ex.Message;
            await repository.StampOutcomeAsync(
                    fire.Id, "failed", null, detail, null, ct)
                .ConfigureAwait(false);
            _logger.LogWarning(ex,
                "schedule.fire.dispatch_failed trigger={TriggerId} tenant={TenantId} window={WindowKey} detail={Detail} — next window is the recovery path",
                fire.TriggerId, fire.TenantId, fire.WindowKey, detail);
            await EmitAsync(events, ScheduleEvents.FireFailed, fire.TenantId, trigger,
                fire.WindowKey, data: new { error = detail }, ct).ConfigureAwait(false);
            return false;
        }

        await repository.StampOutcomeAsync(
                fire.Id, "dispatched", instanceId, null, now.UtcDateTime, ct)
            .ConfigureAwait(false);

        var nextDue = ScheduleWindowCalculator
            .DueWindows(trigger.CronExpression, now, now.AddYears(1), maxWindows: 1)
            .FirstOrDefault();
        await repository.StampTriggerFiredAsync(
                trigger.Id, fire.WindowKey, now.UtcDateTime,
                nextDue == default ? null : nextDue.UtcDateTime, ct)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "schedule.fire.dispatched trigger={TriggerId} tenant={TenantId} definition={DefinitionId} window={WindowKey} instance={InstanceId}",
            fire.TriggerId, fire.TenantId, fire.DefinitionId, fire.WindowKey, instanceId);
        await EmitAsync(events, ScheduleEvents.FireDispatched, fire.TenantId, trigger,
            fire.WindowKey, data: new { workflowInstanceId = instanceId }, ct)
            .ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Merge the row's <c>input_json</c> under the seam's reserved inputs.
    /// Reserved keys (<c>tenantId</c>, <c>windowKey</c>, <c>triggerId</c>,
    /// <c>definitionId</c>) always win — a row cannot spoof another tenant's
    /// id into its own dispatch.
    /// </summary>
    private static Dictionary<string, object> BuildInput(
        ScheduledTrigger trigger, ScheduledTriggerFire fire)
    {
        var input = new Dictionary<string, object>();
        try
        {
            using var doc = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(trigger.InputJson) ? "{}" : trigger.InputJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in doc.RootElement.EnumerateObject())
                    input[prop.Name] = prop.Value.Clone();
            }
        }
        catch (JsonException)
        {
            // Fail-open on row data: a malformed input_json (the admin API
            // rejects these, but raw SQL could smuggle one) must not kill the
            // fire — the reserved inputs below are what consumers require.
        }

        input["tenantId"] = fire.TenantId.ToString("D");
        input["windowKey"] = fire.WindowKey;
        input["triggerId"] = fire.TriggerId.ToString("D");
        input["definitionId"] = fire.DefinitionId;
        return input;
    }

    private async Task EmitAsync(
        IPlatformEventPublisher? events,
        string type,
        Guid? tenantId,
        ScheduledTrigger trigger,
        string? windowKey,
        object data,
        CancellationToken ct)
    {
        if (events is null) return;
        try
        {
            await events.AppendAndPublishAsync(new PlatformEvent
            {
                Type = type,
                TenantId = tenantId,
                Tags = JsonSerializer.Serialize(new
                {
                    tenantId = tenantId?.ToString("D"),
                    definitionId = trigger.DefinitionId,
                    windowKey,
                    triggerId = trigger.Id.ToString("D"),
                    status = ScheduleEvents.StatusForEvent(type),
                }),
                Metadata = "{\"eventSource\":\"system\",\"emitter\":\"TenantScheduledTriggerService\"}",
                Data = JsonSerializer.Serialize(data),
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Audit emission is best-effort here — the fire itself must not
            // die because the event callback hiccupped (the
            // EngineApiPlatformEventPublisher already degrades to WARN+null).
            _logger.LogWarning(ex, "schedule.events.emit_failed type={Type}", type);
        }
    }
}
