using System.Diagnostics.Metrics;

namespace Tamma.Api.Services.Audit;

/// <summary>
/// Story 37-1 (AC9) — OpenTelemetry surface for the audit projector.
///
/// <list type="bullet">
///   <item><description><c>tamma.audit.projection_lag</c> — observable gauge of
///     how many raw DCB events are still un-projected (max raw
///     <c>SequenceNumber</c> − last projected <c>SequenceNumber</c>, summed
///     across the two streams). Reports the last value the projector recorded
///     after its most recent batch; <c>0</c> means the curated trail is fully
///     caught up.</description></item>
/// </list>
///
/// <para>Self-registering meter (see <c>KekRotationMetrics</c> /
/// <c>TenantConnectionPoolMetrics</c>): constructing a <see cref="Meter"/> with
/// <see cref="MeterName"/> makes the gauge discoverable; registering this class
/// as a singleton is sufficient. Thread-safe — the gauge reads a volatile long.</para>
/// </summary>
public sealed class AuditProjectionMetrics : IDisposable
{
    /// <summary>Public meter name — pin so dashboards stay stable.</summary>
    public const string MeterName = "Tamma.AuditProjection";

    private readonly Meter _meter;
    private readonly Counter<long> _projectionFailures;
    private long _lag;

    public AuditProjectionMetrics()
    {
        _meter = new Meter(MeterName, "1.0.0");
        _meter.CreateObservableGauge(
            "tamma.audit.projection_lag",
            () => Interlocked.Read(ref _lag),
            unit: "{event}",
            description: "Raw DCB events not yet materialized into audit_records "
                + "(0 when the curated trail is fully caught up).");

        // C2 — counts events whose normal projection (redaction / build) failed
        // and were QUARANTINED (a safe placeholder row written, cursor advanced).
        // A non-zero rate means ops should inspect a poison-pill / pathological
        // payload — the action was still recorded, never silently dropped.
        _projectionFailures = _meter.CreateCounter<long>(
            "tamma.audit.projection_failures",
            unit: "{event}",
            description: "Raw DCB events that failed normal projection and were "
                + "quarantined (safe placeholder row written; action still audited).");
    }

    /// <summary>Current lag (raw events still awaiting projection).</summary>
    public long Lag => Interlocked.Read(ref _lag);

    /// <summary>Record the projector's latest lag after a batch.</summary>
    public void RecordLag(long lag) => Interlocked.Exchange(ref _lag, lag < 0 ? 0 : lag);

    /// <summary>C2 — count one quarantined (failed-redaction) event.</summary>
    public void RecordProjectionFailure() => _projectionFailures.Add(1);

    public void Dispose() => _meter.Dispose();
}
