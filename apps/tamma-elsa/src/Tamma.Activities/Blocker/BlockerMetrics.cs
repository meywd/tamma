using System.Diagnostics.Metrics;

namespace Tamma.Activities.Blocker;

/// <summary>
/// Completeness audit 2026-06-22 (<c>BlockerDiagnosis.md</c> §Missing #13, 7-1G AC9) —
/// OpenTelemetry metric surface for the <c>blocker-diagnosis</c> sub-workflow. The meter
/// is self-registering (constructing a <see cref="Meter"/> with <see cref="MeterName"/>
/// makes the instruments discoverable by any <c>MeterProvider</c> wired to that name —
/// the codebase keeps no explicit <c>AddMeter</c> allow-list, see
/// <see cref="Tamma.Data.Pooling.TenantConnectionPoolMetrics"/> /
/// <see cref="Tamma.Activities.ADL.EmitPrEventActivity"/>).
///
/// <para>The instruments below realise the AC9-named metrics. Counters are split by
/// terminal so the rate metrics the spec names — <c>blocker.resolved_rate</c> /
/// <c>blocker.escalation_rate</c> — are derived in the dashboard as
/// <c>blocker.resolved / blocker.total</c> etc. (we expose the raw numerators +
/// denominator rather than precomputed ratios, which is the correct OTel idiom);
/// <c>blocker.avg_resolution_time</c> is the mean of the
/// <c>blocker.resolution_time</c> histogram. Per-level resolution-rate is the
/// <c>blocker.resolved</c> counter sliced by its <c>level</c> tag; blocker-type
/// distribution is any counter sliced by its <c>blocker_type</c> tag. Everything is
/// tagged with <c>tenant</c> so per-tenant perf data stays tenant-scoped (Epic 32).</para>
/// </summary>
public static class BlockerMetrics
{
    /// <summary>Public meter name — pinned so dashboards stay stable.</summary>
    public const string MeterName = "Tamma.Blocker";

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    /// <summary>Total blockers diagnosed (denominator for the rate metrics).</summary>
    private static readonly Counter<long> Total = Meter.CreateCounter<long>(
        "blocker.total",
        unit: "{blocker}",
        description: "Total blockers diagnosed, tagged by blocker_type and tenant.");

    /// <summary>Blockers that reached a Resolved terminal (numerator for resolved_rate).</summary>
    private static readonly Counter<long> Resolved = Meter.CreateCounter<long>(
        "blocker.resolved",
        unit: "{blocker}",
        description: "Blockers resolved, tagged by the resolving level, blocker_type and tenant.");

    /// <summary>Blockers that reached an Escalated terminal (numerator for escalation_rate).</summary>
    private static readonly Counter<long> Escalated = Meter.CreateCounter<long>(
        "blocker.escalated",
        unit: "{blocker}",
        description: "Blockers escalated to a senior, tagged by blocker_type and tenant.");

    /// <summary>Blockers whose escalation SLA expired with no senior response.</summary>
    private static readonly Counter<long> TimedOut = Meter.CreateCounter<long>(
        "blocker.timed_out",
        unit: "{blocker}",
        description: "Blockers whose escalation SLA expired with no senior response, tagged by blocker_type and tenant.");

    /// <summary>Wall-clock resolution time (seconds) — the basis for avg_resolution_time.</summary>
    private static readonly Histogram<double> ResolutionTime = Meter.CreateHistogram<double>(
        "blocker.resolution_time",
        unit: "s",
        description: "Wall-clock time from blocker capture to terminal, tagged by terminal status, blocker_type and tenant.");

    // In-process running totals since process start (lets unit tests assert increments
    // without standing up a MeterListener — mirrors EmitPrEventActivity.PrsCreatedTotal).
    private static long _total;
    private static long _resolved;
    private static long _escalated;
    private static long _timedOut;

    public static long TotalCount => Interlocked.Read(ref _total);
    public static long ResolvedCount => Interlocked.Read(ref _resolved);
    public static long EscalatedCount => Interlocked.Read(ref _escalated);
    public static long TimedOutCount => Interlocked.Read(ref _timedOut);

    private static KeyValuePair<string, object?>[] Tags(string? blockerType, string? tenant, string? level = null)
    {
        var tags = new List<KeyValuePair<string, object?>>(3)
        {
            new("blocker_type", blockerType ?? "Unknown"),
            new("tenant", string.IsNullOrWhiteSpace(tenant) ? "platform" : tenant),
        };
        if (!string.IsNullOrWhiteSpace(level))
            tags.Add(new("level", level));
        return tags.ToArray();
    }

    /// <summary>Record a newly-diagnosed blocker (denominator).</summary>
    public static void RecordDiagnosed(string? blockerType, string? tenant)
    {
        Interlocked.Increment(ref _total);
        Total.Add(1, Tags(blockerType, tenant));
    }

    /// <summary>Record a Resolved terminal at <paramref name="level"/>.</summary>
    public static void RecordResolved(string? blockerType, string? tenant, string? level, TimeSpan resolutionTime)
    {
        Interlocked.Increment(ref _resolved);
        Resolved.Add(1, Tags(blockerType, tenant, level));
        ResolutionTime.Record(resolutionTime.TotalSeconds, Tags(blockerType, tenant, level: "Resolved"));
    }

    /// <summary>Record an Escalated terminal (senior notified, awaiting response).</summary>
    public static void RecordEscalated(string? blockerType, string? tenant, TimeSpan resolutionTime)
    {
        Interlocked.Increment(ref _escalated);
        Escalated.Add(1, Tags(blockerType, tenant));
        ResolutionTime.Record(resolutionTime.TotalSeconds, Tags(blockerType, tenant, level: "Escalated"));
    }

    /// <summary>Record a Timeout terminal (escalation SLA expired with no senior response).</summary>
    public static void RecordTimedOut(string? blockerType, string? tenant, TimeSpan resolutionTime)
    {
        Interlocked.Increment(ref _timedOut);
        TimedOut.Add(1, Tags(blockerType, tenant));
        ResolutionTime.Record(resolutionTime.TotalSeconds, Tags(blockerType, tenant, level: "Timeout"));
    }
}
