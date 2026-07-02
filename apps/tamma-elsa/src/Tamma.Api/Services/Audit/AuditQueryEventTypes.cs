namespace Tamma.Api.Services.Audit;

/// <summary>
/// Story 37-3 — DCB event types emitted by the audit query surface itself.
/// The meta-audit event (<see cref="Queried"/>) makes "who read the audit log,
/// with what filters, and how many rows came back" itself auditable (AC10).
///
/// <para><b>Not an <c>audit_records</c> row.</b> <see cref="Queried"/> is a raw
/// DCB event (<c>domain_events</c> / <c>platform_events</c>), NOT a curated
/// audit row, so it does not feed back into this query surface (no recursion).
/// Whether it is later PROJECTED into the curated trail is a Story 37-1 catalog
/// decision, independent of this story.</para>
/// </summary>
public static class AuditQueryEventTypes
{
    /// <summary>Emitted best-effort after every successful audit read.</summary>
    public const string Queried = "AUDIT.QUERIED";
}
