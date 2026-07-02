namespace Tamma.Api.Dtos.Audit;

/// <summary>
/// Story 37-3 (AC11) — the wire projection of one curated
/// <c>audit_records</c> row. Mirrors the existing <c>AuditEventResponse</c>
/// precedent by exposing the JSON payload as a raw string. Story-37-2 chain
/// columns (<c>RecordHash</c>/<c>PrevRecordHash</c>) and raw DCB back-reference
/// metadata are deliberately NOT exposed.
/// </summary>
/// <param name="Id">Surrogate row id.</param>
/// <param name="ActionCategory">Compliance category (e.g. <c>secret</c>).</param>
/// <param name="ActionCode">Canonical action / event-type code (e.g. <c>SECRET.REVEAL</c>).</param>
/// <param name="ActorUserId">Acting user id, or null for system actions.</param>
/// <param name="ActorLabel">Point-in-time actor email snapshot, or null.</param>
/// <param name="TargetType">Target object type, or null.</param>
/// <param name="TargetId">Target object id, or null.</param>
/// <param name="Severity"><c>info</c> | <c>notice</c> | <c>warning</c> | <c>critical</c>.</param>
/// <param name="Outcome"><c>success</c> | <c>failure</c> | <c>denied</c>.</param>
/// <param name="IpAddress">Source IP, or null.</param>
/// <param name="OccurredAt">When the audited action occurred (UTC).</param>
/// <param name="Payload">Redacted JSON payload as a raw string.</param>
/// <param name="SourceSequenceNumber">Monotonic DCB sequence — also the keyset cursor source.</param>
public sealed record AuditRecordResponse(
    Guid Id,
    string ActionCategory,
    string ActionCode,
    Guid? ActorUserId,
    string? ActorLabel,
    string? TargetType,
    string? TargetId,
    string Severity,
    string Outcome,
    string? IpAddress,
    DateTime OccurredAt,
    string Payload,
    long SourceSequenceNumber);
