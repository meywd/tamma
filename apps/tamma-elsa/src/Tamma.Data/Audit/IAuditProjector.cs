using Tamma.Core.Audit;
using Tamma.Data.Entities;

namespace Tamma.Data.Audit;

/// <summary>
/// Story 37-1 — the operating mode that drives per-record ownership routing
/// (Story 37-1 AC11). Mirrors <c>Tamma.Api.Services.PromptStore.TammaMode</c>
/// without taking a dependency on the Api layer (the projector lives in
/// <c>Tamma.Data</c>).
/// </summary>
public enum AuditOwnershipMode
{
    /// <summary>Single-user — every curated row is keyed by <c>user_id</c>.</summary>
    SingleUser,

    /// <summary>SaaS — rows are keyed by <c>tenant_id</c> (platform-only rows by
    /// neither, i.e. <c>tenant_id = null</c> in the control plane).</summary>
    SaaS,
}

/// <summary>
/// Story 37-1 — projects raw DCB events into curated <see cref="AuditRecord"/>
/// rows. PURE classification + redaction + ownership routing — NO I/O. The
/// background host owns reading the event streams (read-only) and writing the
/// resulting rows into the correct (tenant vs CP) store; this keeps the
/// projector trivially unit-testable and guarantees it never touches the raw
/// event-store write path (AC15).
/// </summary>
public interface IAuditProjector
{
    /// <summary>
    /// Build a curated audit record from a raw event, or <c>null</c> when the
    /// event type is not in <see cref="SensitiveActionCatalog"/> (AC7 skip).
    /// The returned record's <c>PayloadJson</c> is already redacted (AC10) and
    /// its ownership column is set per <paramref name="mode"/> (AC11).
    /// </summary>
    /// <param name="rawEvent">The raw DCB event (a <see cref="DomainEvent"/> or
    /// a <see cref="PlatformEvent"/> projected into the same shape).</param>
    /// <param name="mode">Process ownership mode.</param>
    /// <param name="singleUserOwnerId">The sole user's id — required in
    /// <see cref="AuditOwnershipMode.SingleUser"/> so the row can be keyed.</param>
    AuditRecord? TryBuildRecord(
        RawAuditEvent rawEvent, AuditOwnershipMode mode, Guid? singleUserOwnerId);

    /// <summary>
    /// C2 — build a <b>quarantine</b> record for an event whose normal projection
    /// (redaction / build) FAILED. The row carries the known-safe classifiable
    /// fields (action_code / category / severity when resolvable, else a generic
    /// "unclassified" marker), the same <c>source_event_id</c> (so idempotency
    /// holds), <c>outcome = "failure"</c>, correct per-mode ownership, and a SAFE
    /// placeholder payload — NEVER the raw / un-redacted <c>Data</c>/<c>Tags</c>.
    /// Persisting this row lets the cursor advance past a poison-pill event so the
    /// audit trail progresses, while still recording that the action occurred.
    /// </summary>
    AuditRecord BuildQuarantineRecord(
        RawAuditEvent rawEvent, AuditOwnershipMode mode, Guid? singleUserOwnerId);
}

/// <summary>
/// Story 37-1 — a uniform, read-only view of a raw DCB event for the projector.
/// Carries exactly what the projector needs (id, type, tenant, JSONB tags/data,
/// occurred-at, sequence) so a <see cref="DomainEvent"/> and a
/// <see cref="PlatformEvent"/> classify through one code path.
/// </summary>
public sealed record RawAuditEvent(
    Guid Id,
    string Type,
    Guid? TenantId,
    string Tags,
    string Data,
    DateTime CreatedAt,
    long SequenceNumber)
{
    public static RawAuditEvent From(DomainEvent e) =>
        new(e.Id, e.Type, e.TenantId, e.Tags, e.Data, e.CreatedAt, e.SequenceNumber);

    public static RawAuditEvent From(PlatformEvent e) =>
        new(e.Id, e.Type, e.TenantId, e.Tags, e.Data, e.CreatedAt, e.SequenceNumber);
}
