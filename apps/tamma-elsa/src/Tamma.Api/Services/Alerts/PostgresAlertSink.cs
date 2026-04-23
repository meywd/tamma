using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Alerts;

/// <summary>
/// Story 5.6 (Wave C.1) — default <see cref="IAlertSink"/> backed by
/// the CP Postgres. Writes the <c>alerts</c> row, fans out pending
/// <c>alert_delivery_attempts</c> rows per matching enabled channel,
/// and emits an <c>ALERT.RAISED</c> DCB event. A rate-limiter drop
/// records a single <c>dropped_rate_limit</c> audit row (no alert
/// row, no channel fan-out, no raised event) and returns early.
///
/// <para>All three writes (alert + attempts + event) land in a single
/// <see cref="DbContext.SaveChangesAsync(CancellationToken)"/> call
/// so partial-failure leaves zero orphan rows. The event emission
/// routes through <see cref="IEventRepository"/> which handles the
/// CP-vs-tenant routing (platform-scoped alerts emit onto the CP
/// stream; tenant-scoped alerts emit into the tenant stream). The
/// event emit is NOT inside the same SaveChanges — it would couple
/// the CP alerts write to the tenant DomainEvents write and
/// complicate error recovery; event emission is fire-and-audit-log on
/// failure.</para>
/// </summary>
public sealed class PostgresAlertSink : IAlertSink
{
    private readonly ControlPlaneDbContext _db;
    private readonly IAlertRateLimiter _rateLimiter;
    private readonly IEventRepository _events;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PostgresAlertSink> _logger;

    public PostgresAlertSink(
        ControlPlaneDbContext db,
        IAlertRateLimiter rateLimiter,
        IEventRepository events,
        TimeProvider timeProvider,
        ILogger<PostgresAlertSink> logger)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(rateLimiter);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _db = db;
        _rateLimiter = rateLimiter;
        _events = events;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<AlertRaiseResult> RaiseAsync(
        AlertPayload payload, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (!AlertSeverity.IsValid(payload.Severity))
            throw new ArgumentException(
                $"Invalid severity '{payload.Severity}'. " +
                $"Expected one of: {string.Join(", ", AlertSeverity.All)}.",
                nameof(payload));

        if (string.IsNullOrWhiteSpace(payload.Title))
            throw new ArgumentException("Title is required.", nameof(payload));
        if (payload.Title.Length > 512)
            throw new ArgumentException(
                "Title must be <= 512 characters.", nameof(payload));
        if (string.IsNullOrWhiteSpace(payload.Description))
            throw new ArgumentException(
                "Description is required.", nameof(payload));

        // Rate-limit gate. A rejected bucket writes a single
        // dropped_rate_limit row so the drop is auditable and returns
        // early — no alert row, no event, no channel fan-out.
        if (!_rateLimiter.TryConsume(payload.RuleId))
        {
            return await RecordDropAsync(payload, ct).ConfigureAwait(false);
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var alert = new Alert
        {
            // Generate the id client-side so the child delivery-attempt
            // rows can reference it in the same SaveChanges. Otherwise
            // the Postgres server-side gen_random_uuid() default only
            // fires during INSERT, leaving alert.Id as Guid.Empty at
            // attempt-row build time and violating the FK.
            Id = Guid.NewGuid(),
            RuleId = payload.RuleId,
            Severity = payload.Severity,
            Title = payload.Title,
            Description = payload.Description,
            CorrelationId = payload.CorrelationId,
            TenantId = payload.TenantId,
            Metadata = SerializeMetadata(payload.Metadata),
            Status = AlertStatus.Active,
            CreatedAt = now,
        };
        _db.Alerts.Add(alert);

        // Find every enabled channel that matches the tenant scope
        // of this alert. Platform-wide alerts (TenantId null) fan
        // out to platform channels (TenantId null); tenant-scoped
        // alerts fan out to tenant channels PLUS any platform
        // channels (platform channels carry cross-tenant severity
        // escalations like PagerDuty paging a platform oncall).
        var channels = await ResolveMatchingChannelsAsync(
            payload.TenantId, ct).ConfigureAwait(false);

        foreach (var channel in channels)
        {
            _db.AlertDeliveryAttempts.Add(new AlertDeliveryAttempt
            {
                AlertId = alert.Id,
                ChannelId = channel.Id,
                AttemptNumber = 1,
                Status = AlertDeliveryStatus.Pending,
                NextAttemptAt = null,
                CreatedAt = now,
            });
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Audit event — fire-and-log-on-failure so a transient
        // tenant-DB outage doesn't block the alert from being
        // persisted + dispatched.
        try
        {
            await _events.AppendAsync(new DomainEvent
            {
                Type = AlertEventTypes.Raised,
                TenantId = payload.TenantId,
                Tags = JsonSerializer.Serialize(new Dictionary<string, string?>
                {
                    ["alertId"] = alert.Id.ToString("N"),
                    ["severity"] = payload.Severity,
                    ["ruleId"] = payload.RuleId?.ToString("N"),
                    ["correlationId"] = payload.CorrelationId,
                }),
                Metadata = """{"eventSource":"system"}""",
                Data = JsonSerializer.Serialize(new
                {
                    title = payload.Title,
                    matchedChannels = channels.Count,
                }),
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ALERT.RAISED event emission failed for alertId {AlertId}; " +
                "alert + delivery rows persisted OK.",
                alert.Id);
        }

        return new AlertRaiseResult(
            AlertId: alert.Id,
            Delivered: true,
            MatchedChannels: channels.Count,
            DroppedByRateLimit: false);
    }

    private async Task<AlertRaiseResult> RecordDropAsync(
        AlertPayload payload, CancellationToken ct)
    {
        // Drop-audit flow: we want the drop visible in the admin
        // feed (so operators can see rate-limited bursts), but we
        // do NOT write an alert row — the bucket is supposed to
        // suppress the alert. We DO emit a DCB ALERT.DELIVERY_DROPPED
        // event tagged with the rule id. No delivery-attempt row is
        // written here because there's no alert id to reference as
        // the FK parent; the event IS the audit record for the drop.
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        try
        {
            await _events.AppendAsync(new DomainEvent
            {
                Type = AlertEventTypes.DeliveryDropped,
                TenantId = payload.TenantId,
                Tags = JsonSerializer.Serialize(new Dictionary<string, string?>
                {
                    ["severity"] = payload.Severity,
                    ["ruleId"] = payload.RuleId?.ToString("N"),
                    ["reason"] = "rate_limit",
                    ["correlationId"] = payload.CorrelationId,
                }),
                Metadata = """{"eventSource":"system"}""",
                Data = JsonSerializer.Serialize(new
                {
                    title = payload.Title,
                    droppedAt = now,
                }),
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ALERT.DELIVERY_DROPPED event emission failed for " +
                "ruleId {RuleId}; drop proceeds.",
                payload.RuleId);
        }

        return new AlertRaiseResult(
            AlertId: Guid.Empty,
            Delivered: false,
            MatchedChannels: 0,
            DroppedByRateLimit: true);
    }

    private async Task<IReadOnlyList<AlertChannel>> ResolveMatchingChannelsAsync(
        Guid? tenantId, CancellationToken ct)
    {
        // Fetch all enabled channels that match the alert scope:
        // - platform-scoped alerts (tenantId null) → platform-scoped channels
        //   (channel.TenantId null) only
        // - tenant-scoped alerts → tenant-scoped channels for that tenant
        //   plus platform-scoped channels (so platform-level oncall still
        //   gets paged on tenant-originated critical alerts)
        var query = _db.AlertChannels.AsNoTracking()
            .Where(c => c.IsEnabled);

        if (tenantId is null)
        {
            query = query.Where(c => c.TenantId == null);
        }
        else
        {
            var tid = tenantId.Value;
            query = query.Where(c => c.TenantId == null || c.TenantId == tid);
        }

        return await query.ToListAsync(ct).ConfigureAwait(false);
    }

    private static string SerializeMetadata(
        IReadOnlyDictionary<string, object?>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
            return "{}";
        return JsonSerializer.Serialize(metadata);
    }
}
