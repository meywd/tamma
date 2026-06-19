namespace Tamma.Data.Entities;

/// <summary>
/// Story 37-1 — one row per logical audit projector. Tracks the last DCB
/// event (per stream) the projector successfully materialized so a restart
/// resumes instead of re-scanning from scratch. Structurally a clone of
/// <see cref="AlertEvaluatorCursor"/> — same cursor mechanism, different sink.
///
/// <para><b>Crash safety</b>: the cursor is persisted after each batch. A
/// process kill mid-batch may re-scan a handful of events on restart; the
/// <c>audit_records.source_event_id</c> UNIQUE index makes the re-scan a
/// no-op (insert-if-absent), so the projection is idempotent (AC8).</para>
/// </summary>
public class AuditProjectorCursor
{
    /// <summary>Stable id for the logical projector. One row per id; the single
    /// <c>"default"</c> projector is used today.</summary>
    public string ProjectorId { get; set; } = "default";

    /// <summary>Last-processed <see cref="DomainEvent.SequenceNumber"/> from the
    /// tenant <c>domain_events</c> stream. Zero = start from the beginning.</summary>
    public long LastDomainSequenceNumber { get; set; }

    /// <summary>Last-processed <see cref="PlatformEvent.SequenceNumber"/> from the
    /// control-plane <c>platform_events</c> stream. Tracked independently because
    /// the two streams each have their own BIGSERIAL identity.</summary>
    public long LastPlatformSequenceNumber { get; set; }

    public DateTime UpdatedAt { get; set; }
}
