using System.Diagnostics.Metrics;

namespace Tamma.Activities.Analytics;

/// <summary>
/// Story 36-2 (AC13) — OpenTelemetry surface for the dimensional analytics
/// projection SLO.
///
/// <list type="bullet">
///   <item><description><c>tamma.analytics.projection_lag_seconds</c> —
///     observable gauge of the wall-clock lag (seconds) between the rolled-up
///     hour bucket and the time the most recent fan-out pass completed.
///     A steady low value means the projection is keeping up; a value past the
///     SLO budget (default 2h) is the runbook's cue.</description></item>
/// </list>
///
/// <para>Self-registering meter (see <c>KekRotationMetrics</c> /
/// <c>AuditProjectionMetrics</c>): constructing a <see cref="Meter"/> with
/// <see cref="MeterName"/> makes the gauge discoverable; registering this class
/// as a singleton is sufficient. Thread-safe — the gauge reads a volatile
/// double.</para>
/// </summary>
public sealed class DimensionalProjectionMetrics : IDisposable
{
    /// <summary>Public meter name — pin so dashboards stay stable.</summary>
    public const string MeterName = "Tamma.AnalyticsProjection";

    private readonly Meter _meter;
    private long _lagSecondsBits;

    public DimensionalProjectionMetrics()
    {
        _meter = new Meter(MeterName, "1.0.0");
        _meter.CreateObservableGauge(
            "tamma.analytics.projection_lag_seconds",
            () => LagSeconds,
            unit: "s",
            description: "Wall-clock lag between the rolled-up analytics hour bucket "
                + "and the completion of the most recent dimensional fan-out pass.");
    }

    /// <summary>Last recorded projection lag in seconds.</summary>
    public double LagSeconds => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _lagSecondsBits));

    /// <summary>Record the projection lag after a fan-out pass (clamped at 0).</summary>
    public void RecordLag(double lagSeconds)
    {
        var value = lagSeconds < 0 ? 0d : lagSeconds;
        Interlocked.Exchange(ref _lagSecondsBits, BitConverter.DoubleToInt64Bits(value));
    }

    public void Dispose() => _meter.Dispose();
}
