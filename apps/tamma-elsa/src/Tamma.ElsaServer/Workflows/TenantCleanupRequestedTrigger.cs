using System.Text.Json;
using Elsa.Workflows.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tamma.Data;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Options for <see cref="TenantCleanupRequestedTrigger"/>. Bound to the
/// <c>TenantCleanupTrigger</c> configuration section.
/// </summary>
public sealed class TenantCleanupRequestedTriggerOptions
{
    public const string SectionName = "TenantCleanupTrigger";

    /// <summary>
    /// When <c>false</c> the bridge does not start. Defaults to
    /// <c>true</c> so production picks up the cleanup workflow trigger
    /// automatically; tests + non-Elsa hosts flip this off.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How often the bridge polls <c>platform_events</c> for new
    /// <c>TENANT.CLEANUP.REQUESTED</c> rows. 2 seconds matches the
    /// admin SSE cadence and is fast enough for an operator-driven
    /// recovery flow.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// Round-2 review M3 — bridge that closes the integration cliff between
/// the admin <c>POST /api/admin/tenants/{id}/cleanup</c> endpoint and
/// <see cref="CleanUpFailedTenantWorkflow"/>.
///
/// <para>The endpoint emits a <c>TENANT.CLEANUP.REQUESTED</c> row into
/// <c>platform_events</c>. Tamma.Api and Tamma.ElsaServer run in
/// separate processes, so the in-memory <c>IPlatformEventBus</c> in
/// Tamma.Api isn't visible to the Elsa runtime here. Instead, this
/// background service polls the durable event log for new
/// <c>TENANT.CLEANUP.REQUESTED</c> rows and re-publishes the matching
/// Elsa-side event via <see cref="IEventPublisher.PublishAsync"/>,
/// which fires the workflow's <c>Event</c> starter trigger.</para>
///
/// <para><b>Idempotency</b>: the bridge tracks the last-seen
/// <c>SequenceNumber</c> in process memory. A process restart re-reads
/// the latest cleanup-requested row, but the cleanup activity itself
/// is idempotent (probe-before-drop on every step) so a duplicate
/// dispatch is safe — at worst it logs "already deleted" on each step.
/// The on-startup behaviour deliberately starts at "now" (max sequence
/// at boot) rather than zero so we don't replay every historical
/// cleanup request after a redeploy.</para>
///
/// <para><b>Failure isolation</b>: a single tick failure (CP DB
/// unreachable, dispatch failure) is logged at WARN; the bridge
/// continues to the next interval. The cleanup endpoint returns 200 to
/// the operator either way; the bridge's job is to convert that
/// promise into actual workflow execution as soon as connectivity
/// recovers.</para>
/// </summary>
public sealed class TenantCleanupRequestedTrigger : BackgroundService
{
    private const string EventType = "TENANT.CLEANUP.REQUESTED";

    private readonly IServiceProvider _services;
    private readonly IOptions<TenantCleanupRequestedTriggerOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TenantCleanupRequestedTrigger> _logger;

    private long _lastSequence;

    public TenantCleanupRequestedTrigger(
        IServiceProvider services,
        IOptions<TenantCleanupRequestedTriggerOptions> options,
        TimeProvider timeProvider,
        ILogger<TenantCleanupRequestedTrigger> logger)
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
                "TenantCleanupRequestedTrigger disabled — set " +
                "TenantCleanupTrigger:Enabled=true to opt in.");
            return;
        }

        // Initial cursor: high-water-mark BEFORE we started. We don't
        // want to dispatch a flood of cleanup workflows for every
        // historical request after a redeploy.
        try
        {
            _lastSequence = await ReadInitialCursorAsync(stoppingToken)
                .ConfigureAwait(false);
            _logger.LogInformation(
                "TenantCleanupRequestedTrigger starting cursor={Cursor} pollInterval={Poll}s",
                _lastSequence,
                opts.PollInterval.TotalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "TenantCleanupRequestedTrigger initial cursor read failed; starting at 0.");
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
                    "TenantCleanupRequestedTrigger tick threw; continuing.");
            }

            try
            {
                await Task.Delay(opts.PollInterval, _timeProvider, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("TenantCleanupRequestedTrigger shut down.");
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

        // 25-row cap per tick keeps the bridge responsive even after a
        // long backlog. The cleanup activity is idempotent so dispatch
        // ordering relative to other cleanup requests doesn't matter.
        var rows = await db.PlatformEvents
            .AsNoTracking()
            .Where(e => e.Type == EventType
                        && e.SequenceNumber > _lastSequence)
            .OrderBy(e => e.SequenceNumber)
            .Take(25)
            .Select(e => new { e.Id, e.SequenceNumber, e.TenantId, e.Data })
            .ToListAsync(ct).ConfigureAwait(false);

        if (rows.Count == 0) return;

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var note = TryReadNote(row.Data);
                // Correlate by tenant id so concurrent cleanups for
                // different tenants don't share a workflow instance.
                var correlationId = row.TenantId?.ToString("D");
                var payload = new Dictionary<string, object?>
                {
                    ["tenantId"] = row.TenantId?.ToString("D"),
                    ["note"] = note,
                };
                // IEventPublisher.PublishAsync signature (Elsa 3.5.x):
                // PublishAsync(string eventName, string? correlationId,
                //   string? workflowInstanceId, string? activityInstanceId,
                //   object? payload, bool asynchronous,
                //   CancellationToken ct).
                await publisher.PublishAsync(
                    CleanUpFailedTenantWorkflow.CleanupRequestedEventName,
                    correlationId,
                    null,
                    null,
                    payload,
                    false,
                    ct).ConfigureAwait(false);

                _logger.LogInformation(
                    "tenant.cleanup.dispatched eventId={EventId} tenantId={TenantId} sequence={Sequence}",
                    row.Id,
                    row.TenantId,
                    row.SequenceNumber);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "tenant.cleanup.dispatch_failed eventId={EventId} tenantId={TenantId} — " +
                    "will retry on next tick (cursor not advanced).",
                    row.Id,
                    row.TenantId);
                // Cursor not advanced — the next tick re-reads this row.
                return;
            }

            _lastSequence = row.SequenceNumber;
        }
    }

    private static string? TryReadNote(string? dataJson)
    {
        if (string.IsNullOrWhiteSpace(dataJson) || dataJson == "{}")
            return null;
        try
        {
            using var doc = JsonDocument.Parse(dataJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            return doc.RootElement.TryGetProperty("note", out var v)
                && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
