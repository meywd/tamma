namespace Tamma.Data.Entities;

/// <summary>
/// Story 37-1 — one row per <c>(ProjectorId, TenantId)</c> high-water mark.
/// Tracks the last DCB event the projector successfully materialized so a
/// restart resumes instead of re-scanning from scratch.
///
/// <para><b>Why a composite key (C1 fix)</b>: each tenant's
/// <c>domain_events.SequenceNumber</c> is an INDEPENDENT per-schema BIGSERIAL —
/// tenant B's stream starts at 1 regardless of how far tenant A has advanced. A
/// single shared domain cursor therefore loses tenant B's audit data the moment
/// the shared cursor passes tenant A's high-water mark (B is read with
/// <c>WHERE SequenceNumber &gt; &lt;A's max&gt;</c> and never projected). So the
/// domain high-water mark is tracked PER TENANT, keyed by <see cref="TenantId"/>.</para>
///
/// <para>The control-plane <c>platform_events</c> stream is a single global
/// BIGSERIAL stream, so it is tracked on one distinguished row whose
/// <see cref="TenantId"/> = <see cref="PlatformSentinel"/> (a Postgres primary
/// key column cannot be NULL, so the all-zero Guid is the "platform" sentinel).
/// That same sentinel row also tracks the single-user / transitional shared-DB
/// <c>cp.domain_events</c> fallback (one stream, no tenant fan-out).</para>
///
/// <para><b>Crash safety</b>: each tenant's cursor is persisted after its batch.
/// A process kill mid-batch may re-scan a handful of events on restart; the
/// <c>audit_records.source_event_id</c> UNIQUE index makes the re-scan a no-op
/// (insert-if-absent), so the projection is idempotent (AC8).</para>
/// </summary>
public class AuditProjectorCursor
{
    /// <summary>The distinguished <see cref="TenantId"/> value for the row that
    /// tracks the global CP <c>platform_events</c> stream (and the shared-DB
    /// <c>cp.domain_events</c> fallback). A real tenant id is never the all-zero
    /// Guid, so this never collides with a per-tenant row.</summary>
    public static readonly Guid PlatformSentinel = Guid.Empty;

    /// <summary>Stable id for the logical projector. The single
    /// <c>"default"</c> projector is used today.</summary>
    public string ProjectorId { get; set; } = "default";

    /// <summary>
    /// The tenant this cursor row tracks. <see cref="PlatformSentinel"/>
    /// (all-zero Guid) is the distinguished "platform" row that tracks the CP
    /// <c>platform_events</c> stream (and the single-user / transitional shared-DB
    /// <c>cp.domain_events</c> fallback). A real tenant id tracks one tenant's
    /// per-schema <c>domain_events</c> stream. Part of the composite primary key
    /// together with <see cref="ProjectorId"/>.
    /// </summary>
    public Guid TenantId { get; set; } = PlatformSentinel;

    /// <summary>Last-processed <see cref="DomainEvent.SequenceNumber"/> from THIS
    /// row's <c>domain_events</c> stream (this tenant's per-schema stream, or the
    /// shared-DB <c>cp.domain_events</c> on the sentinel/platform row). Zero =
    /// start from the beginning.</summary>
    public long LastDomainSequenceNumber { get; set; }

    /// <summary>Last-processed <see cref="PlatformEvent.SequenceNumber"/> from the
    /// control-plane <c>platform_events</c> stream. Only meaningful on the
    /// distinguished <see cref="PlatformSentinel"/> platform row (the platform
    /// stream is global, not per-tenant). Stays zero on per-tenant rows.</summary>
    public long LastPlatformSequenceNumber { get; set; }

    public DateTime UpdatedAt { get; set; }
}
