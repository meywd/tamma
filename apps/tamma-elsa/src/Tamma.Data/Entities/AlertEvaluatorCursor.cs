namespace Tamma.Data.Entities;

/// <summary>
/// Story 5.6 (Wave C.2) — one row per logical alert-rule evaluator.
/// Tracks the last DCB event the evaluator successfully processed so
/// that a process restart can resume from the same point instead of
/// re-evaluating from scratch.
///
/// <para>Crash safety is best-effort: the cursor is persisted
/// periodically (every N ticks), not after every event, so the
/// evaluator MAY re-process a handful of events on restart. The
/// rule fires themselves are de-duplicated via the in-memory
/// throttle map + sink-side rate limiter, so duplicate fires become
/// at-most-one delivery per throttle window. Losing a few seconds
/// of newly-arrived events on hard crash is acceptable per the
/// Wave C.2 brief.</para>
/// </summary>
public class AlertEvaluatorCursor
{
    /// <summary>
    /// Stable id for the logical evaluator. One physical process one
    /// row — multi-process deployments today use the same
    /// <see cref="EvaluatorId"/> (<c>"default"</c>) because only one
    /// evaluator per CP makes sense today. Future horizontal scale
    /// will partition events by rule id to multiple rows.
    /// </summary>
    public string EvaluatorId { get; set; } = "default";

    /// <summary>
    /// Timestamp of the last processed event. New events are fetched
    /// via <c>CreatedAt &gt; LastEventAt</c>. Combined with the
    /// secondary <see cref="LastEventId"/> tie-breaker so two events
    /// sharing a millisecond-precision timestamp don't get skipped.
    /// </summary>
    public DateTime LastEventAt { get; set; }

    /// <summary>Secondary cursor to tie-break equal timestamps.</summary>
    public Guid? LastEventId { get; set; }

    public DateTime UpdatedAt { get; set; }
}
