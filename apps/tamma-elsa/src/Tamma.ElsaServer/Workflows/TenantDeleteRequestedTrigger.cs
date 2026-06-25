using Elsa.Workflows.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tamma.Data;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Options for <see cref="TenantDeleteRequestedTrigger"/>. Bound to the
/// <c>TenantDeleteTrigger</c> configuration section.
/// </summary>
public sealed class TenantDeleteRequestedTriggerOptions
{
    public const string SectionName = "TenantDeleteTrigger";

    /// <summary>
    /// When <c>false</c> the bridge does not start. Defaults to <c>true</c>
    /// so production picks up the delete workflow trigger automatically;
    /// tests + non-Elsa hosts flip this off.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How often the bridge polls <c>platform_events</c> for new
    /// <c>TENANT.DELETE.REQUESTED</c> rows. 2 seconds matches the admin SSE
    /// cadence; the cooling-off window (below) is what actually paces the
    /// destructive drop.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Story 28-5 AC4 — grace window between the admin DELETE flip and the
    /// destructive drop. The bridge will NOT dispatch the workflow until
    /// <c>now - tenants.DeleteRequestedAt &gt;= CoolingOff</c>, giving an
    /// operator time to cancel via
    /// <c>POST /api/admin/tenants/{id}/actions/cancel-delete</c>. Defaults
    /// to 5 minutes (Doc 04 §6.5 + Doc 01 §10.1).
    /// </summary>
    public TimeSpan CoolingOff { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Story 28-5 items #1, #2, #5 — bridge that closes the integration cliff
/// between the admin <c>POST /api/admin/tenants/{id}/actions/delete</c>
/// endpoint and <see cref="DeleteTenantWorkflow"/>.
///
/// <para>The endpoint emits a <c>TENANT.DELETE.REQUESTED</c> row into
/// <c>platform_events</c> + flips the tenant to <c>deleting</c>. Tamma.Api
/// and Tamma.ElsaServer run in separate processes, so this background
/// service polls the durable event log for new <c>TENANT.DELETE.REQUESTED</c>
/// rows and re-publishes the matching Elsa-side event via
/// <see cref="IEventPublisher.PublishAsync"/>, which fires the workflow's
/// <c>Event</c> starter trigger.</para>
///
/// <para><b>Cooling-off (item #2):</b> a row is NOT dispatched until
/// <c>now - tenants.DeleteRequestedAt &gt;= CoolingOff</c>. A row still
/// inside its window is DEFERRED for a later tick (the cursor is not advanced
/// past it) — but the bridge keeps scanning the rest of the batch so a ready
/// higher-sequence row is not starved behind it (head-of-line fix).
/// FORCE-DELETE waives the window entirely: a row whose payload carries
/// <c>data.source == "admin-force-delete"</c> dispatches immediately,
/// matching the force-delete contract.</para>
///
/// <para><b>Cancellation (item #5):</b> immediately before dispatch the
/// bridge re-reads the tenant. If <c>Status != 'deleting'</c> (an operator
/// cancelled during the window, flipping back to <c>active</c>) the row is
/// skipped and never dispatched. This is the EARLIEST of three cancellation
/// checkpoints; the in-flight delete workflow has two more (the mark step and
/// the cancellation guard immediately before <c>DROP SCHEMA</c>) so a cancel
/// that lands AFTER dispatch is still caught and the schema is never dropped.</para>
///
/// <para><b>Self re-dispatch / dedup</b>: once a tenant's teardown is
/// dispatched it is held in an in-process set until the bridge observes the
/// tenant is no longer <c>deleting</c>. A second <c>TENANT.DELETE.REQUESTED</c>
/// row for the same tenant (force-delete after delete, or a cooling-off
/// re-scan) does NOT re-dispatch. The workflow also no longer re-emits
/// <c>TENANT.DELETE.REQUESTED</c> (it emits <c>TENANT.DELETE.STARTED</c>), so
/// the bridge can never be fed its own output.</para>
///
/// <para><b>Idempotency</b>: the bridge tracks the last-seen
/// <c>SequenceNumber</c> in process memory and starts at the max sequence
/// at boot (not zero) so a redeploy doesn't replay historical requests.
/// The delete workflow itself is idempotent (probe-before-drop on every
/// step) so a duplicate dispatch is safe.</para>
///
/// <para><b>Failure isolation</b>: a tick failure (CP DB unreachable,
/// dispatch failure) is logged at WARN and the cursor is NOT advanced past
/// the offending row, so it is retried on the next tick.</para>
/// </summary>
public sealed class TenantDeleteRequestedTrigger : BackgroundService
{
    private const string EventType = "TENANT.DELETE.REQUESTED";

    private readonly IServiceProvider _services;
    private readonly IOptions<TenantDeleteRequestedTriggerOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TenantDeleteRequestedTrigger> _logger;

    private long _lastSequence;

    /// <summary>
    /// Per-tenant dispatch dedup (self re-dispatch guard). Once a tenant's
    /// delete workflow has been dispatched it is held here until we observe
    /// the tenant is no longer <c>deleting</c> (cancelled, or torn down). This
    /// prevents BOTH a second <c>TENANT.DELETE.REQUESTED</c> row for the same
    /// tenant (force-delete after delete, or a future workflow re-emit) AND a
    /// cooling-off head-of-line re-scan from re-dispatching an already-running
    /// teardown. The delete workflow itself is idempotent, so a missed dedup
    /// is safe — this just avoids redundant runs.
    /// </summary>
    private readonly HashSet<Guid> _dispatched = new();

    public TenantDeleteRequestedTrigger(
        IServiceProvider services,
        IOptions<TenantDeleteRequestedTriggerOptions> options,
        TimeProvider timeProvider,
        ILogger<TenantDeleteRequestedTrigger> logger)
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
        var opts = _options.Value;
        if (!opts.Enabled)
        {
            _logger.LogInformation(
                "TenantDeleteRequestedTrigger disabled — set " +
                "TenantDeleteTrigger:Enabled=true to opt in.");
            return;
        }

        try
        {
            _lastSequence = await ReadInitialCursorAsync(stoppingToken)
                .ConfigureAwait(false);
            _logger.LogInformation(
                "TenantDeleteRequestedTrigger starting cursor={Cursor} pollInterval={Poll}s coolingOff={Cooling}m",
                _lastSequence,
                opts.PollInterval.TotalSeconds,
                opts.CoolingOff.TotalMinutes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "TenantDeleteRequestedTrigger initial cursor read failed; starting at 0.");
            _lastSequence = 0;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "TenantDeleteRequestedTrigger tick threw; continuing.");
            }

            try
            {
                await Task.Delay(opts.PollInterval, _timeProvider, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("TenantDeleteRequestedTrigger shut down.");
    }

    private async Task<long> ReadInitialCursorAsync(CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var max = await db.PlatformEvents
            .AsNoTracking()
            .Where(e => e.Type == EventType)
            .Select(e => (long?)e.SequenceNumber)
            .MaxAsync(ct).ConfigureAwait(false);
        return max ?? 0L;
    }

    private async Task TickAsync(CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();

        var rows = await db.PlatformEvents
            .AsNoTracking()
            .Where(e => e.Type == EventType
                        && e.SequenceNumber > _lastSequence)
            .OrderBy(e => e.SequenceNumber)
            .Take(25)
            .Select(e => new { e.Id, e.SequenceNumber, e.TenantId, e.Data })
            .ToListAsync(ct).ConfigureAwait(false);

        if (rows.Count == 0) return;

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // Head-of-line fix — when a not-yet-due (cooling-off) row is hit we
        // must NOT advance the cursor PAST it (a later tick has to reconsider
        // it once its window elapses), but we MUST keep scanning the rest of
        // this batch so a ready higher-sequence row isn't starved behind it.
        // We therefore advance _lastSequence only across the leading run of
        // fully-handled rows; the moment we defer one, the cursor freezes at
        // the row just before it and every later row is processed in-place
        // (the per-tenant dedup below makes re-scanning those rows a no-op).
        var cursorAdvancing = true;

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();

            void AdvanceIfLeading()
            {
                if (cursorAdvancing) _lastSequence = row.SequenceNumber;
            }

            if (row.TenantId is null)
            {
                // Malformed row with no tenant binding — nothing to do; skip
                // it. Counts as fully-handled so the cursor can move past it.
                AdvanceIfLeading();
                continue;
            }

            var tenantId = row.TenantId.Value;

            // Re-read the tenant's live state: cooling-off window + the
            // operator-cancel check. Both read the authoritative shadow
            // columns, not the (possibly stale) event payload.
            var state = await db.Tenants
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(t => t.Id == tenantId)
                .Select(t => new
                {
                    Status = EF.Property<string?>(t, "Status"),
                    DeleteRequestedAt = EF.Property<DateTime?>(t, "DeleteRequestedAt"),
                    t.DeletedAt,
                })
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);

            // Item #5 — cancellation: tenant no longer in 'deleting' (an
            // operator cancelled, or it was already torn down). Skip; this row
            // is fully-handled (nothing to dispatch). Clear any in-flight dedup
            // so a fresh future delete of the same tenant can dispatch again.
            if (state is null
                || state.DeletedAt is not null
                || !string.Equals(state.Status, "deleting", StringComparison.OrdinalIgnoreCase))
            {
                _dispatched.Remove(tenantId);
                _logger.LogInformation(
                    "tenant.delete.dispatch_skipped reason=not_deleting tenantId={TenantId} status={Status} sequence={Sequence}",
                    tenantId, state?.Status, row.SequenceNumber);
                AdvanceIfLeading();
                continue;
            }

            // Self re-dispatch / duplicate-request dedup — the tenant is still
            // 'deleting' and we already dispatched its teardown. Don't dispatch
            // again (force-delete after delete; a cooling-off head-of-line
            // re-scan; a hypothetical workflow re-emit). Fully-handled row.
            if (_dispatched.Contains(tenantId))
            {
                _logger.LogDebug(
                    "tenant.delete.dispatch_skipped reason=already_in_flight tenantId={TenantId} sequence={Sequence}",
                    tenantId, row.SequenceNumber);
                AdvanceIfLeading();
                continue;
            }

            // Item #2 — cooling-off: hold the row until the grace window
            // elapses, UNLESS this is a force-delete (cooling-off waived per
            // the force-delete contract). A null DeleteRequestedAt is treated
            // as "just requested" → hold. Force-delete is detected from the
            // emitting endpoint's source marker in the event payload.
            var forced = IsForceDelete(row.Data);
            var requestedAt = state.DeleteRequestedAt ?? now;
            if (!forced && now - requestedAt < _options.Value.CoolingOff)
            {
                _logger.LogDebug(
                    "tenant.delete.cooling_off tenantId={TenantId} requestedAt={RequestedAt} remaining={Remaining}s",
                    tenantId, requestedAt,
                    (_options.Value.CoolingOff - (now - requestedAt)).TotalSeconds);
                // Head-of-line fix — DEFER this row (do not advance the cursor
                // past it) but keep scanning the rest of the batch. Freeze the
                // cursor here so a later tick reconsiders this row once its
                // window elapses.
                cursorAdvancing = false;
                continue;
            }

            try
            {
                var correlationId = tenantId.ToString("D");
                var payload = new Dictionary<string, object?>
                {
                    ["tenantId"] = tenantId.ToString("D"),
                    ["attempt"] = 1,
                };
                await publisher.PublishAsync(
                    DeleteTenantWorkflow.DeleteRequestedEventName,
                    correlationId,
                    null,
                    null,
                    payload,
                    false,
                    ct).ConfigureAwait(false);

                _dispatched.Add(tenantId);
                _logger.LogInformation(
                    "tenant.delete.dispatched eventId={EventId} tenantId={TenantId} sequence={Sequence} forced={Forced}",
                    row.Id, tenantId, row.SequenceNumber, forced);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "tenant.delete.dispatch_failed eventId={EventId} tenantId={TenantId} — " +
                    "will retry on next tick (cursor not advanced past this row).",
                    row.Id, tenantId);
                // Defer this row (do not advance past it) so the next tick
                // re-reads it; keep scanning the rest of the batch.
                cursorAdvancing = false;
                continue;
            }

            AdvanceIfLeading();
        }
    }

    /// <summary>
    /// Detects a force-delete request from the <c>TENANT.DELETE.REQUESTED</c>
    /// event payload (<c>data.source == "admin-force-delete"</c>). Force-delete
    /// waives the cooling-off window. Tolerant of malformed JSON — an
    /// unparseable payload is simply treated as a normal (cooling-off) delete.
    /// </summary>
    internal static bool IsForceDelete(string? data)
    {
        if (string.IsNullOrWhiteSpace(data)) return false;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(data);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && doc.RootElement.TryGetProperty("source", out var src)
                && src.ValueKind == System.Text.Json.JsonValueKind.String
                && string.Equals(src.GetString(), "admin-force-delete", StringComparison.Ordinal);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
}
