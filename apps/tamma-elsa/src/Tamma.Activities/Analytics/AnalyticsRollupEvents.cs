using System.Text.Json;
using Tamma.Data.Entities;

namespace Tamma.Activities.Analytics;

/// <summary>
/// Story 28-10 — event-type catalogue and helpers for the hourly
/// <c>platform_analytics_hourly</c> rollup workflow.
///
/// <para>All events land on <c>platform_events</c> via
/// <see cref="Tamma.Data.Abstractions.IPlatformEventPublisher"/>; they
/// are platform-wide (not tenant-scoped) because the rollup is a CP-level
/// job — a partial failure or a skipped tenant are facts about the
/// platform as a whole.</para>
///
/// <para>Event names follow the <c>AGGREGATE.ACTION.STATUS</c> convention
/// (same shape as Story 28-5 TENANT.* events) so the step-dedup index
/// from Story 28-6 behaves identically — replays are swallowed by
/// <c>(tenant_id, type, tags-&gt;&gt;'step', tags-&gt;&gt;'attempt')</c>.</para>
/// </summary>
public static class AnalyticsRollupEvents
{
    public const string TenantRollupCompleted = "ANALYTICS.ROLLUP.TENANT_COMPLETED";
    public const string TenantRollupSkipped = "ANALYTICS.ROLLUP.TENANT_SKIPPED";
    public const string TenantRollupFailed = "ANALYTICS.ROLLUP.TENANT_FAILED";
    public const string PlatformRollupCompleted = "ANALYTICS.ROLLUP.PLATFORM_COMPLETED";
    public const string HourCompleted = "ANALYTICS.ROLLUP.HOUR_COMPLETED";

    /// <summary>
    /// Terminal event for the <c>PURGE_ANALYTICS_HOURLY</c> retention
    /// sweep — carries the cutoff timestamp and the number of stale
    /// <c>platform_analytics_hourly</c> rows deleted.
    /// </summary>
    public const string AnalyticsPurged = "ANALYTICS.PURGE.HOURLY";
    public const string AnalyticsPurgeFailed = "ANALYTICS.PURGE.FAILED";

    // ── Story 36-2 — per-tenant dimensional projection pipeline ──
    // Same AGGREGATE.ACTION.STATUS convention; the ANALYTICS.PROJECTION /
    // ANALYTICS.ROLLUP.TENANT_DIMENSIONAL_* family was reserved for this
    // story by Story 36-1 §"DCB events". Per-tenant events carry tenantId so
    // the Story 28-6 step-dedup index applies.

    /// <summary>Per tenant×hour — the dimensional projection wrote its rows.</summary>
    public const string TenantDimensionalRollupCompleted =
        "ANALYTICS.ROLLUP.TENANT_DIMENSIONAL_COMPLETED";

    /// <summary>Per tenant×hour — the dimensional projection threw (fan-out continues).</summary>
    public const string TenantDimensionalRollupFailed =
        "ANALYTICS.ROLLUP.TENANT_DIMENSIONAL_FAILED";

    /// <summary>Per tenant×day — hourly→daily lossless compaction completed.</summary>
    public const string DailyCompacted = "ANALYTICS.COMPACT.DAILY";

    /// <summary>Terminal event for the per-tenant analytics_usage_hourly retention sweep.</summary>
    public const string UsageHourlyPurged = "ANALYTICS.PURGE.USAGE_HOURLY";
    public const string UsageHourlyPurgeFailed = "ANALYTICS.PURGE.USAGE_HOURLY_FAILED";

    /// <summary>
    /// Per fan-out pass, emitted only when the wall-clock lag between the
    /// rolled-up hour and projection completion exceeds the SLO budget. A
    /// WARN-level observability signal, never a failure.
    /// </summary>
    public const string DimensionalLag = "ANALYTICS.ROLLUP.DIMENSIONAL_LAG";

    /// <summary>
    /// Build a <see cref="PlatformEvent"/> for an hourly-rollup milestone.
    /// <paramref name="tenantId"/> is null when the event is platform-wide
    /// (<c>PLATFORM_COMPLETED</c>, <c>HOUR_COMPLETED</c>); per-tenant events
    /// carry the tenant id so the step-dedup index applies.
    /// </summary>
    public static PlatformEvent BuildEvent(
        string type,
        DateTime hour,
        Guid? tenantId = null,
        IReadOnlyDictionary<string, object?>? data = null)
    {
        if (string.IsNullOrWhiteSpace(type))
            throw new ArgumentException("type must be supplied", nameof(type));

        var tags = new Dictionary<string, string?>
        {
            ["hour"] = hour.ToUniversalTime().ToString("O"),
        };
        if (tenantId is not null) tags["tenantId"] = tenantId.Value.ToString("D");

        return new PlatformEvent
        {
            Type = type,
            TenantId = tenantId,
            Tags = JsonSerializer.Serialize(tags),
            Metadata = """{"workflowVersion":"1.0.0","eventSource":"system"}""",
            Data = data is null ? "{}" : JsonSerializer.Serialize(data),
        };
    }

    /// <summary>
    /// Truncate <paramref name="instant"/> to the top-of-hour in UTC so
    /// every row the rollup writes aligns to the same bucket key. The
    /// workflow passes <c>DateTime.UtcNow.Hour - 1</c> for the bucket just
    /// completed; callers should run this helper to avoid drift.
    /// </summary>
    public static DateTime TruncateToHour(DateTime instant)
    {
        var utc = instant.Kind == DateTimeKind.Utc
            ? instant
            : instant.ToUniversalTime();
        return new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc);
    }
}
