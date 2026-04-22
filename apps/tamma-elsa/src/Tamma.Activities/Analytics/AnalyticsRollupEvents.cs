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
