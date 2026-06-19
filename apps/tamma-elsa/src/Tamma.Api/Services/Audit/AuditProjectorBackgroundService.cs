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
    /// curated rows inserted this tick.
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
        var cursor = await repo.LoadCursorAsync(cp, _options.ProjectorId, ct).ConfigureAwait(false);

        // ── READ-ONLY scan of both streams, ordered by SequenceNumber (AC15) ──
        //
        // Domain (tenant-scoped) events: per-tenant fan-out via the factory
        // mirrors AlertRuleEvaluator's PR-D shape — tenant domain_events live in
        // each tenant's schema. The domain cursor is shared across tenants
        // (per-stream-but-monotonic BIGSERIAL identity). When no factory is
        // wired (single-user / transitional shared-DB), fall back to cp.DomainEvents.
        var domain = await ReadDomainEventsAsync(cp, factory, cursor.LastDomainSequenceNumber, ct)
            .ConfigureAwait(false);

        // Platform (cross-tenant / platform-only) events: always CP-resident.
        var platform = await cp.PlatformEvents.AsNoTracking()
            .Where(e => e.SequenceNumber > cursor.LastPlatformSequenceNumber)
            .OrderBy(e => e.SequenceNumber)
            .Take(_options.BatchSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        Guid? singleUserOwnerId = mode == AuditOwnershipMode.SingleUser
            ? await ResolveSingleUserOwnerAsync(cp, ct).ConfigureAwait(false)
            : null;

        var inserted = 0;
        long maxDomainSeq = cursor.LastDomainSequenceNumber;
        long maxPlatformSeq = cursor.LastPlatformSequenceNumber;

        // Domain events first, in strict SequenceNumber order (37-2 needs it).
        foreach (var raw in domain)
        {
            if (ct.IsCancellationRequested) break;
            if (await ProjectOneAsync(
                    RawAuditEvent.From(raw), projector, repo, cp, factory,
                    mode, singleUserOwnerId, ct).ConfigureAwait(false))
            {
                inserted++;
            }
            if (raw.SequenceNumber > maxDomainSeq) maxDomainSeq = raw.SequenceNumber;
        }

        foreach (var raw in platform)
        {
            if (ct.IsCancellationRequested) break;
            if (await ProjectOneAsync(
                    RawAuditEvent.From(raw), projector, repo, cp, factory,
                    mode, singleUserOwnerId, ct).ConfigureAwait(false))
            {
                inserted++;
            }
            if (raw.SequenceNumber > maxPlatformSeq) maxPlatformSeq = raw.SequenceNumber;
        }

        var scanned = domain.Count + platform.Count;
        if (scanned > 0)
        {
            await repo.SaveCursorAsync(
                cp, _options.ProjectorId, maxDomainSeq, maxPlatformSeq,
                _timeProvider.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
        }

        await RecordLagAsync(cp, factory, maxDomainSeq, maxPlatformSeq, ct).ConfigureAwait(false);

        var durationMs = (_timeProvider.GetUtcNow().UtcDateTime - startedAt).TotalMilliseconds;
        _logger.LogInformation(
            "AuditProjector batch complete — projectorId={ProjectorId} domainCursor={DomainCursor} "
            + "platformCursor={PlatformCursor} eventsScanned={Scanned} recordsInserted={Inserted} "
            + "recordsSkipped={Skipped} batchDurationMs={DurationMs}",
            _options.ProjectorId, maxDomainSeq, maxPlatformSeq, scanned, inserted,
            scanned - inserted, (long)durationMs);

        return inserted;
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
            // AC10 / Logging — if classification or redaction throws, FAIL the
            // row and do NOT advance past it silently. We log ERROR; the row is
            // not persisted. (The cursor still advances over the batch's max
            // sequence; a re-run re-attempts via the un-inserted source id.)
            _logger.LogError(ex,
                "AuditProjector failed to build/redact record for event {SourceEventId} ({Type}); "
                + "row NOT persisted.", raw.Id, raw.Type);
            return false;
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
            if (mode == AuditOwnershipMode.SaaS && record.TenantId is Guid tenantId && factory is not null)
            {
                await using var tenantCtx = await factory.CreateAsync(tenantId, ct).ConfigureAwait(false);
                return await repo.InsertIfAbsentAsync(tenantCtx, record, ct).ConfigureAwait(false);
            }

            return await repo.InsertIfAbsentAsync(cp, record, ct).ConfigureAwait(false);
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
    /// Read new tenant-scoped domain events strictly after the shared domain
    /// cursor, ordered by <c>SequenceNumber</c>. Fans out across active tenants
    /// via the factory (PR-D shape); falls back to <c>cp.DomainEvents</c> when no
    /// factory is wired (single-user / transitional shared-DB). Read-only (AC15).
    /// </summary>
    private async Task<List<DomainEvent>> ReadDomainEventsAsync(
        ControlPlaneDbContext cp, ITenantDbContextFactory? factory, long afterSeq, CancellationToken ct)
    {
        if (factory is null)
        {
            return await cp.DomainEvents.AsNoTracking()
                .Where(e => e.SequenceNumber > afterSeq)
                .OrderBy(e => e.SequenceNumber)
                .Take(_options.BatchSize)
                .ToListAsync(ct)
                .ConfigureAwait(false);
        }

        var tenantIds = await cp.Tenants.AsNoTracking()
            .Where(t => t.DeletedAt == null)
            .OrderBy(t => t.Id)
            .Select(t => t.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var merged = new List<DomainEvent>();
        foreach (var tid in tenantIds)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                await using var tdb = await factory.CreateAsync(tid, ct).ConfigureAwait(false);
                var rows = await tdb.DomainEvents.AsNoTracking()
                    .Where(e => e.SequenceNumber > afterSeq)
                    .OrderBy(e => e.SequenceNumber)
                    .Take(_options.BatchSize)
                    .ToListAsync(ct)
                    .ConfigureAwait(false);
                merged.AddRange(rows);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "AuditProjector: tenant {TenantId} domain_events scan failed; "
                    + "continuing with the remaining tenants.", tid);
            }
        }

        // Global order across tenants: the BIGSERIAL identity is per-stream, so
        // sort by SequenceNumber then CreatedAt for a deterministic interleave.
        merged.Sort((a, b) =>
        {
            var c = a.SequenceNumber.CompareTo(b.SequenceNumber);
            return c != 0 ? c : a.CreatedAt.CompareTo(b.CreatedAt);
        });
        if (merged.Count > _options.BatchSize)
            merged.RemoveRange(_options.BatchSize, merged.Count - _options.BatchSize);
        return merged;
    }

    /// <summary>
    /// Resolve the sole user's id in single-user mode. The first (oldest)
    /// non-deleted user owns the instance. Returns null when no user exists yet
    /// (a fresh instance) — the projector then falls back to the event actor.
    /// </summary>
    private static async Task<Guid?> ResolveSingleUserOwnerAsync(
        ControlPlaneDbContext cp, CancellationToken ct)
    {
        return await cp.Users.AsNoTracking()
            .OrderBy(u => u.CreatedAt)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// AC9 — record the projection lag = (max raw SequenceNumber across both
    /// streams) − (last projected). Reads the current stream heads read-only.
    /// Domain head is the max across tenant schemas when fanning out.
    /// </summary>
    private async Task RecordLagAsync(
        ControlPlaneDbContext cp, ITenantDbContextFactory? factory,
        long projectedDomain, long projectedPlatform, CancellationToken ct)
    {
        long maxDomain;
        if (factory is null)
        {
            maxDomain = await cp.DomainEvents.AsNoTracking()
                .MaxAsync(e => (long?)e.SequenceNumber, ct).ConfigureAwait(false) ?? 0L;
        }
        else
        {
            maxDomain = 0L;
            var tenantIds = await cp.Tenants.AsNoTracking()
                .Where(t => t.DeletedAt == null).Select(t => t.Id)
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var tid in tenantIds)
            {
                try
                {
                    await using var tdb = await factory.CreateAsync(tid, ct).ConfigureAwait(false);
                    var head = await tdb.DomainEvents.AsNoTracking()
                        .MaxAsync(e => (long?)e.SequenceNumber, ct).ConfigureAwait(false) ?? 0L;
                    if (head > maxDomain) maxDomain = head;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex,
                        "AuditProjector: lag scan for tenant {TenantId} failed; continuing.", tid);
                }
            }
        }

        var maxPlatform = await cp.PlatformEvents.AsNoTracking()
            .MaxAsync(e => (long?)e.SequenceNumber, ct).ConfigureAwait(false) ?? 0L;

        var lag = Math.Max(0, maxDomain - projectedDomain)
            + Math.Max(0, maxPlatform - projectedPlatform);
        _metrics.RecordLag(lag);

        if (lag > _options.LagWarnThreshold)
        {
            _logger.LogWarning(
                "AuditProjector lag {Lag} exceeds threshold {Threshold}.",
                lag, _options.LagWarnThreshold);
        }
    }
}
