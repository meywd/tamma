using System.Text.Json.Serialization;

namespace Tamma.Api.Services.Analytics;

/// <summary>
/// Story 36-3 — request/response contract + validation for the tenant usage
/// analytics API (<c>GET /api/v1/orgs/{tenantId}/analytics/usage[/breakdown]</c>).
///
/// <para>These types are the stable, additive-only contract the tenant
/// dashboard (Story 36-6) and the CSV/JSON exporter (Story 36-8) consume. The
/// wire names are pinned: <c>period_start</c>/<c>period_end</c> are snake_case
/// (the effective, clamped UTC window echoes); every other field rides the
/// app-wide camelCase JSON policy (Program.cs
/// <c>ConfigureHttpJsonOptions</c>).</para>
/// </summary>

/// <summary>Roll-up grain for a usage query.</summary>
public enum AnalyticsGranularity
{
    /// <summary>Top-of-hour buckets — reads <c>analytics_usage_hourly</c>.</summary>
    Hour,

    /// <summary>UTC-midnight buckets — reads <c>analytics_usage_daily</c> (default).</summary>
    Day,
}

/// <summary>Dimension a usage query groups by / a breakdown ranks over.</summary>
public enum AnalyticsDimension
{
    Provider,
    Agent,
    Workflow,
    Repo,
}

/// <summary>The measure a breakdown ranks its top-N rows by.</summary>
public enum AnalyticsMetric
{
    /// <summary><c>TokensIn + TokensOut</c>.</summary>
    Tokens,

    /// <summary><c>WorkflowsStarted</c>.</summary>
    Runs,

    /// <summary><c>AgentDispatches</c>.</summary>
    Dispatches,

    /// <summary><c>CostUsd</c>.</summary>
    Cost,
}

/// <summary>
/// Parse helpers for the string query params. Absent (null/empty) is treated
/// as "use the default" for granularity/metric; an unknown non-empty value is
/// a hard parse failure (the endpoint maps it to 400).
/// </summary>
public static class AnalyticsEnums
{
    /// <summary>
    /// Parse <c>granularity</c>. Absent → <see cref="AnalyticsGranularity.Day"/>
    /// (the default grain). Returns <c>false</c> for an unknown non-empty value.
    /// </summary>
    public static bool TryParseGranularity(string? raw, out AnalyticsGranularity value)
    {
        value = AnalyticsGranularity.Day;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        switch (raw.Trim().ToLowerInvariant())
        {
            case "hour":
                value = AnalyticsGranularity.Hour;
                return true;
            case "day":
                value = AnalyticsGranularity.Day;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Parse a dimension (<c>groupBy</c> / breakdown <c>dimension</c>). There is
    /// no default — an absent or unknown value returns <c>false</c>; the caller
    /// decides whether "absent" means "no grouping" (usage) or 400 (breakdown).
    /// </summary>
    public static bool TryParseDimension(string? raw, out AnalyticsDimension value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        switch (raw.Trim().ToLowerInvariant())
        {
            case "provider":
                value = AnalyticsDimension.Provider;
                return true;
            case "agent":
                value = AnalyticsDimension.Agent;
                return true;
            case "workflow":
                value = AnalyticsDimension.Workflow;
                return true;
            case "repo":
                value = AnalyticsDimension.Repo;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Parse the breakdown <c>metric</c>. Absent → <see cref="AnalyticsMetric.Tokens"/>
    /// (the default). Returns <c>false</c> for an unknown non-empty value.
    /// </summary>
    public static bool TryParseMetric(string? raw, out AnalyticsMetric value)
    {
        value = AnalyticsMetric.Tokens;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        switch (raw.Trim().ToLowerInvariant())
        {
            case "tokens":
                value = AnalyticsMetric.Tokens;
                return true;
            case "runs":
                value = AnalyticsMetric.Runs;
                return true;
            case "dispatches":
                value = AnalyticsMetric.Dispatches;
                return true;
            case "cost":
                value = AnalyticsMetric.Cost;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Lowercase wire token for a granularity (echoed in the response).</summary>
    public static string ToWire(this AnalyticsGranularity g) =>
        g == AnalyticsGranularity.Hour ? "hour" : "day";

    /// <summary>Lowercase wire token for a dimension (echoed in the response).</summary>
    public static string ToWire(this AnalyticsDimension d) => d switch
    {
        AnalyticsDimension.Provider => "provider",
        AnalyticsDimension.Agent => "agent",
        AnalyticsDimension.Workflow => "workflow",
        AnalyticsDimension.Repo => "repo",
        _ => d.ToString().ToLowerInvariant(),
    };

    /// <summary>Lowercase wire token for a metric (echoed in the response).</summary>
    public static string ToWire(this AnalyticsMetric m) => m switch
    {
        AnalyticsMetric.Tokens => "tokens",
        AnalyticsMetric.Runs => "runs",
        AnalyticsMetric.Dispatches => "dispatches",
        AnalyticsMetric.Cost => "cost",
        _ => m.ToString().ToLowerInvariant(),
    };
}

/// <summary>
/// The <b>effective, clamped, UTC</b> query window (Story 36-3 AC7/AC9). Built
/// by <see cref="Resolve"/> from the raw <c>from</c>/<c>to</c>/<c>granularity</c>
/// query params:
///
/// <list type="bullet">
///   <item><description><b>Forced-UTC</b> — <c>from</c>/<c>to</c> are normalized
///     via <c>DateTime.SpecifyKind(v.ToUniversalTime(), DateTimeKind.Utc)</c>
///     (the exact <c>AdminAnalyticsEndpoints.GetEventHistogram</c> fix) so the
///     window lands on the stored top-of-hour / midnight-UTC buckets, not a
///     Local shift (AC9).</description></item>
///   <item><description><b>Defaults</b> — <c>to</c>→now, <c>from</c>→30 days
///     before <c>to</c> when omitted.</description></item>
///   <item><description><b>365-day max range</b> — a wider window is truncated to
///     the most-recent 365 days (clamped, not rejected — the dashboard stays
///     forgiving); the effective window is echoed.</description></item>
///   <item><description><b>Hour-granularity cap</b> — an <c>hour</c> query over a
///     window wider than 31 days is <b>rejected</b> (400); an unbounded hourly
///     scan is the anti-pattern.</description></item>
/// </list>
/// </summary>
public readonly record struct AnalyticsWindow(
    DateTime From,
    DateTime To,
    AnalyticsGranularity Granularity,
    bool IsValid,
    string? Error)
{
    /// <summary>Hard ceiling on the <c>from..to</c> span (clamped, not rejected).</summary>
    public const int MaxRangeDays = 365;

    /// <summary>Widest window an <c>hour</c>-granularity query may scan (rejected past this).</summary>
    public const int HourGranularityMaxDays = 31;

    /// <summary>Window applied when <c>from</c> is omitted.</summary>
    public const int DefaultWindowDays = 30;

    private static AnalyticsWindow Invalid(string error) =>
        new(default, default, default, false, error);

    /// <summary>
    /// Resolve the raw query params into a validated, clamped, UTC window.
    /// Never throws — an invalid request yields <c>IsValid == false</c> and an
    /// <see cref="Error"/> the endpoint returns as 400.
    /// </summary>
    public static AnalyticsWindow Resolve(DateTime? from, DateTime? to, string? granularity)
    {
        if (!AnalyticsEnums.TryParseGranularity(granularity, out var gran))
        {
            return Invalid("granularity must be one of: hour, day");
        }

        var toUtc = ToUtc(to) ?? DateTime.UtcNow;
        var fromUtc = ToUtc(from) ?? toUtc.AddDays(-DefaultWindowDays);

        if (fromUtc >= toUtc)
        {
            return Invalid("from must precede to");
        }

        // 365-day max range — truncate to the most-recent 365 days, echo the
        // effective window (clamp, don't reject).
        if ((toUtc - fromUtc).TotalDays > MaxRangeDays)
        {
            fromUtc = toUtc.AddDays(-MaxRangeDays);
        }

        // Hourly scans are capped — an hour query over a window wider than the
        // cap is rejected (an unbounded hourly scan is the anti-pattern).
        if (gran == AnalyticsGranularity.Hour
            && (toUtc - fromUtc).TotalDays > HourGranularityMaxDays)
        {
            return Invalid($"granularity=hour requires a window <= {HourGranularityMaxDays} days");
        }

        return new AnalyticsWindow(fromUtc, toUtc, gran, true, null);
    }

    // Forced-UTC binding — mirror AdminAnalyticsEndpoints.GetEventHistogram.
    private static DateTime? ToUtc(DateTime? v) =>
        v is null ? null : DateTime.SpecifyKind(v.Value.ToUniversalTime(), DateTimeKind.Utc);
}

/// <summary>
/// One time-bucket row of a usage response. When the query is ungrouped there
/// is one row per bucket (<see cref="Key"/> <c>null</c>); when grouped there is
/// one row per <c>(bucket, dimension-value)</c> — and the <c>NULL</c>
/// "unattributed" dimension bucket is surfaced with a <c>null</c> key, never
/// dropped or coerced to a sentinel (preserving 36-2's reconciliation).
/// </summary>
public sealed record UsageBucketRow(
    DateTime Period,
    string? Key,
    long WorkflowsStarted,
    long WorkflowsCompleted,
    long WorkflowsFailed,
    long AgentDispatches,
    long TokensIn,
    long TokensOut,
    decimal CostUsd,
    decimal PlatformBilledUsd);

/// <summary>Response envelope for <c>GET …/analytics/usage</c>.</summary>
public sealed record UsageResponse(
    Guid TenantId,
    [property: JsonPropertyName("period_start")] DateTime PeriodStart,
    [property: JsonPropertyName("period_end")] DateTime PeriodEnd,
    string Granularity,
    string? GroupBy,
    IReadOnlyList<UsageBucketRow> Rows);

/// <summary>
/// One top-N row of a breakdown response: the dimension <see cref="Key"/>
/// (<c>null</c> = unattributed), the ranked <see cref="Value"/> (the selected
/// metric's aggregate), and the full measure set for context.
/// </summary>
public sealed record BreakdownRow(
    string? Key,
    decimal Value,
    long TokensIn,
    long TokensOut,
    decimal CostUsd,
    decimal PlatformBilledUsd,
    long WorkflowsStarted,
    long WorkflowsCompleted,
    long WorkflowsFailed,
    long AgentDispatches);

/// <summary>Response envelope for <c>GET …/analytics/usage/breakdown</c>.</summary>
public sealed record BreakdownResponse(
    Guid TenantId,
    [property: JsonPropertyName("period_start")] DateTime PeriodStart,
    [property: JsonPropertyName("period_end")] DateTime PeriodEnd,
    string Dimension,
    string Metric,
    int Limit,
    IReadOnlyList<BreakdownRow> Rows);
