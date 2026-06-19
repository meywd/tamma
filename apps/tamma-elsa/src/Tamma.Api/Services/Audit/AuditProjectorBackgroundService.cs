using Microsoft.EntityFrameworkCore;
using Tamma.Api.Services.PromptStore;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Audit;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Audit;

/// <summary>
/// Story 37-1 (AC7–AC11, AC15) — background host that materializes the curated
/// <c>audit_records</c> read-model from the immutable DCB stream. Structurally
/// a near-clone of <c>AlertRuleEvaluator</c>: a cursor-tracked poll loop with a
/// <see cref="AuditProjectorOptions.RunOnStartup"/> gate and per-tick
/// crash-isolation.
///
/// <para><b>Read-only against the event store (AC15):</b> the loop only READS
/// <c>cp.DomainEvents</c> / <c>cp.PlatformEvents</c> (and per-tenant
/// <c>domain_events</c> via the factory) ordered by <c>SequenceNumber</c>; it
/// NEVER appends, mutates, or deletes a raw event. The only writes are the
/// curated <c>audit_records</c> rows and the projector cursor.</para>
///
/// <para><b>Per-tenant domain cursor (C1):</b> each tenant's <c>domain_events</c>
/// is an INDEPENDENT per-schema BIGSERIAL stream, so each tenant is scanned with
/// <c>WHERE SequenceNumber &gt; &lt;that tenant's own last&gt;</c> and advances
/// ONLY that tenant's cursor row. The global CP <c>platform_events</c> stream
/// uses the distinguished <see cref="AuditProjectorCursor.PlatformSentinel"/>
/// row, which also carries the single-user / shared-DB <c>cp.domain_events</c>
/// fallback. The batch cap is applied PER TENANT (I1) so one busy tenant cannot
/// starve the others.</para>
///
/// <para><b>Failed-redaction quarantine (C2):</b> if classification/redaction
/// throws for one event, the host writes a minimal QUARANTINE row (safe
/// placeholder payload, <c>outcome = failure</c>, same <c>source_event_id</c>)
/// and emits a WARN + a failure counter — then, and only then, advances the
/// cursor past it. The action is still recorded (never silently dropped) and the
/// trail is never stalled forever on a poison-pill payload.</para>
///
/// <para><b>Scope routing (AC11):</b> SaaS tenant-scoped events (TenantId set)
/// materialize into the tenant schema keyed by <c>tenant_id</c>; SaaS
/// platform-only events (TenantId null) materialize into the CP
/// <c>audit_records</c> with <c>tenant_id</c> null. Single-user events all
/// materialize into the single CP store keyed by <c>user_id</c>.</para>
/// </summary>
public sealed class AuditProjectorBackgroundService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly AuditProjectorOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly AuditProjectionMetrics _metrics;
    private readonly ILogger<AuditProjectorBackgroundService> _logger;

    public AuditProjectorBackgroundService(
        IServiceProvider services,
        AuditProjectorOptions options,
        TimeProvider timeProvider,
        AuditProjectionMetrics metrics,
        ILogger<AuditProjectorBackgroundService> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(logger);
        _services = services;
        _options = options;
        _timeProvider = timeProvider;
        _metrics = metrics;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.RunOnStartup)
        {
            _logger.LogDebug(
                "AuditProjector gated off (RunOnStartup=false); polling loop will not start.");
            return;
        }

        _logger.LogInformation(
            "AuditProjector starting — poll every {Interval}s, batch {Batch}, projectorId '{Id}'.",
            _options.PollInterval.TotalSeconds, _options.BatchSize, _options.ProjectorId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Crash-isolate per tick — one bad batch logs and the loop survives.
                _logger.LogWarning(ex, "AuditProjector tick threw and was crash-isolated; continuing.");
            }

            try
            {
                await Task.Delay(_options.PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("AuditProjector shut down.");
    }

    /// <summary>
    /// Run a single projection tick. Public so tests can drive the projector
    /// deterministically without the background loop. Returns the number of
    /// curated rows inserted this tick (including quarantine rows).
    /// </summary>
    public async Task<int> ProcessOnceAsync(CancellationToken ct)
    {
        var startedAt = _timeProvider.GetUtcNow().UtcDateTime;
        using var scope = _services.CreateScope();
        var sp = scope.ServiceProvider;
        var cp = sp.GetRequiredService<ControlPlaneDbContext>();
        var projector = sp.GetRequiredService<IAuditProjector>();
        var repo = sp.GetRequiredService<IAuditRecordRepository>();
        var modeProvider = sp.GetRequiredService<ITammaModeProvider>();
        var mode = modeProvider.Mode == TammaMode.SaaS
            ? AuditOwnershipMode.SaaS
            : AuditOwnershipMode.SingleUser;

        var factory = sp.GetService<ITenantDbContextFactory>();

        Guid? singleUserOwnerId = mode == AuditOwnershipMode.SingleUser
            ? await ResolveSingleUserOwnerAsync(cp, ct).ConfigureAwait(false)
            : null;

        var inserted = 0;
        long totalScanned = 0;
        // Lag (I2): compute the domain head from the scan we already did, plus the
        // residual (events still beyond the per-tenant batch cap) so a long backlog
        // still reports lag without a second full MAX(SequenceNumber) fan-out.
        long domainHeadFromScan = 0;
        long domainResidual = 0;

        // ── Per-tenant domain projection (C1 + I1) ──────────────────────────────
        // Each tenant has an independent BIGSERIAL stream and its own cursor row;
        // the batch cap is applied PER TENANT so one busy tenant can't starve others.
        if (factory is null)
        {
            // Single-user / transitional shared-DB: one stream, tracked on the
            // platform-sentinel row's domain field. No tenant fan-out.
            var sentinel = AuditProjectorCursor.PlatformSentinel;
            var cursor = await repo.LoadCursorAsync(cp, _options.ProjectorId, sentinel, ct)
                .ConfigureAwait(false);
            var domain = await cp.DomainEvents.AsNoTracking()
                .Where(e => e.SequenceNumber > cursor.LastDomainSequenceNumber)
                .OrderBy(e => e.SequenceNumber)
                .Take(_options.BatchSize)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            var (insertedHere, maxSeq) = await ProjectStreamAsync(
                domain.Select(RawAuditEvent.From), projector, repo, cp, factory,
                mode, singleUserOwnerId, cursor.LastDomainSequenceNumber, ct)
                .ConfigureAwait(false);
            inserted += insertedHere;
            totalScanned += domain.Count;
            if (maxSeq > domainHeadFromScan) domainHeadFromScan = maxSeq;
            if (domain.Count >= _options.BatchSize)
            {
                // A full batch implies there may be more beyond the cap — sample
                // the head once so lag is not under-reported on a backlog.
                var head = await cp.DomainEvents.AsNoTracking()
                    .MaxAsync(e => (long?)e.SequenceNumber, ct).ConfigureAwait(false) ?? 0L;
                domainResidual += Math.Max(0, head - maxSeq);
                if (head > domainHeadFromScan) domainHeadFromScan = head;
            }

            // Persist the (single) domain cursor on the sentinel row.
            if (domain.Count > 0)
            {
                await repo.SaveCursorAsync(
                    cp, _options.ProjectorId, sentinel, maxSeq,
                    cursor.LastPlatformSequenceNumber,
                    _timeProvider.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            }
        }
        else
        {
            var tenantIds = await cp.Tenants.AsNoTracking()
                .Where(t => t.DeletedAt == null)
                .OrderBy(t => t.Id)
                .Select(t => t.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (var tid in tenantIds)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    var cursor = await repo.LoadCursorAsync(cp, _options.ProjectorId, tid, ct)
                        .ConfigureAwait(false);

                    await using var tdb = await factory.CreateAsync(tid, ct).ConfigureAwait(false);
                    var rows = await tdb.DomainEvents.AsNoTracking()
                        .Where(e => e.SequenceNumber > cursor.LastDomainSequenceNumber)
                        .OrderBy(e => e.SequenceNumber)
                        .Take(_options.BatchSize)
                        .ToListAsync(ct)
                        .ConfigureAwait(false);

                    var (insertedHere, maxSeq) = await ProjectStreamAsync(
                        rows.Select(RawAuditEvent.From), projector, repo, cp, factory,
                        mode, singleUserOwnerId, cursor.LastDomainSequenceNumber, ct)
                        .ConfigureAwait(false);
                    inserted += insertedHere;
                    totalScanned += rows.Count;
                    if (maxSeq > domainHeadFromScan) domainHeadFromScan = maxSeq;

                    if (rows.Count >= _options.BatchSize)
                    {
                        // This tenant has a backlog beyond the cap — sample its head
                        // so per-tenant lag is reflected without a blanket fan-out.
                        var head = await tdb.DomainEvents.AsNoTracking()
                            .MaxAsync(e => (long?)e.SequenceNumber, ct).ConfigureAwait(false) ?? 0L;
                        var residual = Math.Max(0, head - maxSeq);
                        domainResidual += residual;
                        if (head > domainHeadFromScan) domainHeadFromScan = head;
                        if (residual > 0)
                        {
                            _logger.LogInformation(
                                "AuditProjector: tenant {TenantId} has {Residual} domain events "
                                + "still beyond this tick's batch cap.", tid, residual);
                        }
                    }

                    // Advance ONLY this tenant's cursor row.
                    if (rows.Count > 0)
                    {
                        await repo.SaveCursorAsync(
                            cp, _options.ProjectorId, tid, maxSeq, 0L,
                            _timeProvider.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex,
                        "AuditProjector: tenant {TenantId} domain projection failed; "
                        + "continuing with the remaining tenants.", tid);
                }
            }
        }

        // ── Platform (cross-tenant / platform-only) projection — sentinel row ──
        var platformCursor = await repo.LoadCursorAsync(
            cp, _options.ProjectorId, AuditProjectorCursor.PlatformSentinel, ct).ConfigureAwait(false);
        var platform = await cp.PlatformEvents.AsNoTracking()
            .Where(e => e.SequenceNumber > platformCursor.LastPlatformSequenceNumber)
            .OrderBy(e => e.SequenceNumber)
            .Take(_options.BatchSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var (platInserted, maxPlatformSeq) = await ProjectStreamAsync(
            platform.Select(RawAuditEvent.From), projector, repo, cp, factory,
            mode, singleUserOwnerId, platformCursor.LastPlatformSequenceNumber, ct)
            .ConfigureAwait(false);
        inserted += platInserted;
        totalScanned += platform.Count;

        long platformResidual = 0;
        if (platform.Count >= _options.BatchSize)
        {
            var head = await cp.PlatformEvents.AsNoTracking()
                .MaxAsync(e => (long?)e.SequenceNumber, ct).ConfigureAwait(false) ?? 0L;
            platformResidual = Math.Max(0, head - maxPlatformSeq);
        }

        if (platform.Count > 0)
        {
            // Preserve the sentinel row's domain-fallback mark (set above for the
            // shared-DB path); only the platform-stream mark changes here.
            await repo.SaveCursorAsync(
                cp, _options.ProjectorId, AuditProjectorCursor.PlatformSentinel,
                platformCursor.LastDomainSequenceNumber, maxPlatformSeq,
                _timeProvider.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
        }

        // Lag (I2) — derived from the heads we already touched + the sampled
        // residual, with no second blanket MAX(SequenceNumber) fan-out on the
        // common (fully-caught-up) path.
        var lag = domainResidual + platformResidual;
        _metrics.RecordLag(lag);
        if (lag > _options.LagWarnThreshold)
        {
            _logger.LogWarning(
                "AuditProjector lag {Lag} exceeds threshold {Threshold}.",
                lag, _options.LagWarnThreshold);
        }

        var durationMs = (_timeProvider.GetUtcNow().UtcDateTime - startedAt).TotalMilliseconds;
        _logger.LogInformation(
            "AuditProjector batch complete — projectorId={ProjectorId} domainHead={DomainHead} "
            + "platformCursor={PlatformCursor} eventsScanned={Scanned} recordsInserted={Inserted} "
            + "lag={Lag} batchDurationMs={DurationMs}",
            _options.ProjectorId, domainHeadFromScan, maxPlatformSeq, totalScanned, inserted,
            lag, (long)durationMs);

        return inserted;
    }

    /// <summary>
    /// Project one ordered stream (a single tenant's domain events, or the
    /// platform stream). Returns the rows inserted and the max sequence number
    /// reached (the new high-water mark for THAT stream's cursor). Every scanned
    /// event advances the mark — including non-catalog skips (AC7) and quarantine
    /// rows (C2) — so the cursor never stalls and never re-scans a handled event.
    /// </summary>
    private async Task<(int Inserted, long MaxSeq)> ProjectStreamAsync(
        IEnumerable<RawAuditEvent> events,
        IAuditProjector projector,
        IAuditRecordRepository repo,
        ControlPlaneDbContext cp,
        ITenantDbContextFactory? factory,
        AuditOwnershipMode mode,
        Guid? singleUserOwnerId,
        long startSeq,
        CancellationToken ct)
    {
        var inserted = 0;
        var maxSeq = startSeq;
        foreach (var raw in events)
        {
            if (ct.IsCancellationRequested) break;
            if (await ProjectOneAsync(
                    raw, projector, repo, cp, factory, mode, singleUserOwnerId, ct)
                .ConfigureAwait(false))
            {
                inserted++;
            }
            if (raw.SequenceNumber > maxSeq) maxSeq = raw.SequenceNumber;
        }
        return (inserted, maxSeq);
    }

    private async Task<bool> ProjectOneAsync(
        RawAuditEvent raw,
        IAuditProjector projector,
        IAuditRecordRepository repo,
        ControlPlaneDbContext cp,
        ITenantDbContextFactory? factory,
        AuditOwnershipMode mode,
        Guid? singleUserOwnerId,
        CancellationToken ct)
    {
        AuditRecord? record;
        try
        {
            record = projector.TryBuildRecord(raw, mode, singleUserOwnerId);
        }
        catch (Exception ex)
        {
            // C2 — classification / redaction threw. QUARANTINE: do NOT drop and
            // do NOT halt. Build a minimal placeholder row keyed by the same
            // source_event_id (idempotency holds), with a SAFE payload + outcome
            // "failure", then let the cursor advance past it. The action stays
            // recorded; a poison-pill payload (e.g. RegexMatchTimeoutException
            // from a pathological string) cannot stall the whole trail.
            _logger.LogWarning(ex,
                "AuditProjector failed to build/redact record for event {SourceEventId} ({Type}); "
                + "writing a QUARANTINE row (safe placeholder payload) and advancing.",
                raw.Id, raw.Type);
            _metrics.RecordProjectionFailure();
            try
            {
                var quarantine = projector.BuildQuarantineRecord(raw, mode, singleUserOwnerId);
                return await InsertRoutedAsync(quarantine, mode, factory, repo, cp, raw, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception qex)
            {
                // The quarantine write itself failed. Returning false here means
                // the cursor still advances over the batch's max sequence (the
                // event is not re-scanned), but the failure is loudly recorded so
                // ops can replay it (truncate + reset cursor rebuilds the trail).
                _logger.LogError(qex,
                    "AuditProjector quarantine write FAILED for event {SourceEventId} ({Type}); "
                    + "the curated trail is missing this event until a rebuild.",
                    raw.Id, raw.Type);
                return false;
            }
        }

        if (record is null)
        {
            _logger.LogDebug("AuditProjector skipped non-catalog event {Type}.", raw.Type);
            return false; // AC7 — non-catalog skip.
        }

        // AC11 routing: SaaS tenant-scoped rows go to the tenant schema; SaaS
        // platform rows + all single-user rows go to the CP store.
        try
        {
            return await InsertRoutedAsync(record, mode, factory, repo, cp, raw, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "AuditProjector insert failed for event {SourceEventId} ({Type}); continuing.",
                raw.Id, raw.Type);
            return false;
        }
    }

    /// <summary>
    /// AC11 routing — write the record into the correct store. SaaS tenant-scoped
    /// rows (TenantId set) go to the tenant schema via the factory; SaaS platform
    /// rows + all single-user rows go to the CP store.
    /// </summary>
    private static async Task<bool> InsertRoutedAsync(
        AuditRecord record,
        AuditOwnershipMode mode,
        ITenantDbContextFactory? factory,
        IAuditRecordRepository repo,
        ControlPlaneDbContext cp,
        RawAuditEvent raw,
        CancellationToken ct)
    {
        if (mode == AuditOwnershipMode.SaaS && record.TenantId is Guid tenantId && factory is not null)
        {
            await using var tenantCtx = await factory.CreateAsync(tenantId, ct).ConfigureAwait(false);
            return await repo.InsertIfAbsentAsync(tenantCtx, record, ct).ConfigureAwait(false);
        }

        return await repo.InsertIfAbsentAsync(cp, record, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolve the sole user's id in single-user mode. The first (oldest)
    /// non-deleted user owns the instance. Returns null when no user exists yet
    /// (a fresh instance) — the projector then falls back to the event actor.
    /// </summary>
    private async Task<Guid?> ResolveSingleUserOwnerAsync(
        ControlPlaneDbContext cp, CancellationToken ct)
    {
        var owner = await cp.Users.AsNoTracking()
            .OrderBy(u => u.CreatedAt)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        // M3 — the "oldest user owns the instance" heuristic is only safe when
        // there is exactly one user. If somehow >1 exists in single-user mode,
        // ownership attribution may be wrong; surface it so ops can investigate.
        if (owner is not null)
        {
            var count = await cp.Users.AsNoTracking().CountAsync(ct).ConfigureAwait(false);
            if (count > 1)
            {
                _logger.LogWarning(
                    "AuditProjector single-user owner resolution found {Count} users; "
                    + "attributing audit rows to the oldest ({OwnerId}) may mis-attribute. "
                    + "single-user mode expects exactly one user.", count, owner);
            }
        }

        return owner;
    }
}
