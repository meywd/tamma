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
/// inside its window is left for a later tick (the cursor is NOT advanced
/// past it) so the destructive drop fires only after the grace period.</para>
///
/// <para><b>Cancellation (item #5):</b> immediately before dispatch the
/// bridge re-reads the tenant. If <c>Status != 'deleting'</c> (an operator
/// cancelled during the window, flipping back to <c>active</c>) the row is
/// skipped — the cursor advances past it so it is never reconsidered, and
/// no workflow runs.</para>
///
/// <para><b>Idempotency</b>: the bridge tracks the last-seen
/// <c>SequenceNumber</c> in process memory and starts at the max sequence
/// at boot (not zero) so a redeploy doesn't replay historical requests.
/// The delete workflow itself is idempotent (probe-before-drop on every
/// step) so a duplicate dispatch is safe.</para>
///
/// <para><b>Failure isolation</b>: a tick failure (CP DB unreachable,
/// dispatch failure) is logged at WARN and the cursor is NOT advanced, so
/// the row is retried on the next tick.</para>
/// </summary>
public sealed class TenantDeleteRequestedTrigger : BackgroundService
{
    private const string EventType = "TENANT.DELETE.REQUESTED";

    private readonly IServiceProvider _services;
    private readonly IOptions<TenantDeleteRequestedTriggerOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TenantDeleteRequestedTrigger> _logger;

    private long _lastSequence;

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
            .Select(e => new { e.Id, e.SequenceNumber, e.TenantId })
            .ToListAsync(ct).ConfigureAwait(false);

        if (rows.Count == 0) return;

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();

            if (row.TenantId is null)
            {
                // Malformed row with no tenant binding — nothing to do; skip
                // it (advance cursor) so it doesn't wedge the bridge.
                _lastSequence = row.SequenceNumber;
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
            // operator cancelled, or it was already torn down). Skip and
            // advance the cursor — there is nothing to dispatch.
            if (state is null
                || state.DeletedAt is not null
                || !string.Equals(state.Status, "deleting", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation(
                    "tenant.delete.dispatch_skipped reason=not_deleting tenantId={TenantId} status={Status} sequence={Sequence}",
                    tenantId, state?.Status, row.SequenceNumber);
                _lastSequence = row.SequenceNumber;
                continue;
            }

            // Item #2 — cooling-off: hold the row until the grace window
            // elapses. Do NOT advance the cursor; a later tick reconsiders
            // it (and re-checks the cancel condition). A null
            // DeleteRequestedAt is treated as "just requested" → hold.
            var requestedAt = state.DeleteRequestedAt ?? now;
            if (now - requestedAt < _options.Value.CoolingOff)
            {
                _logger.LogDebug(
                    "tenant.delete.cooling_off tenantId={TenantId} requestedAt={RequestedAt} remaining={Remaining}s",
                    tenantId, requestedAt,
                    (_options.Value.CoolingOff - (now - requestedAt)).TotalSeconds);
                // Stop scanning here — rows are ordered by sequence and this
                // one isn't ready, so we leave it (and everything after it on
                // this tick) for a later poll. Cursor unchanged.
                return;
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

                _logger.LogInformation(
                    "tenant.delete.dispatched eventId={EventId} tenantId={TenantId} sequence={Sequence}",
                    row.Id, tenantId, row.SequenceNumber);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "tenant.delete.dispatch_failed eventId={EventId} tenantId={TenantId} — " +
                    "will retry on next tick (cursor not advanced).",
                    row.Id, tenantId);
                // Cursor not advanced — the next tick re-reads this row.
                return;
            }

            _lastSequence = row.SequenceNumber;
        }
    }
}
