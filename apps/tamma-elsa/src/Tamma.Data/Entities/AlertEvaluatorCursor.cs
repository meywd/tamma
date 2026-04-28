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
    /// Last-processed <c>SequenceNumber</c> from the
    /// <c>domain_events</c> stream. The next fetch tick selects rows
    /// where <c>SequenceNumber &gt; LastDomainSequenceNumber</c>. Zero
    /// means "no events processed yet — start from the beginning".
    ///
    /// <para>Replaced the original <c>(LastEventAt, LastEventId)</c>
    /// composite cursor: timestamp + Guid lexicographic compare drops
    /// rows whose Guid string sorts ≤ the cursor on the same-tick
    /// boundary. Sequence numbers are monotonic per stream and immune
    /// to that bug.</para>
    /// </summary>
    public long LastDomainSequenceNumber { get; set; }

    /// <summary>
    /// Last-processed <c>SequenceNumber</c> from the
    /// <c>platform_events</c> stream. Tracked independently from
    /// <see cref="LastDomainSequenceNumber"/> because the two tables
    /// each have their own BIGSERIAL identity — there is no global
    /// ordering between them, only per-stream monotonicity.
    /// </summary>
    public long LastPlatformSequenceNumber { get; set; }

    public DateTime UpdatedAt { get; set; }
}
