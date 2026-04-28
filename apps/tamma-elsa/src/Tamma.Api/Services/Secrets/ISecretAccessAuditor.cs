namespace Tamma.Api.Services.Secrets;

/// <summary>
/// Canonical event types emitted by the secret cabinet. Mirror the
/// Story 29-1 AC5 list verbatim so dashboards / alert rules can
/// pattern-match against the constants rather than ad-hoc string
/// literals.
/// </summary>
public static class SecretAuditEventTypes
{
    public const string Read = "SECRET.READ";
    public const string Write = "SECRET.WRITE";
    public const string RotateStarted = "SECRET.ROTATE.STARTED";
    public const string RotateSucceeded = "SECRET.ROTATE.SUCCESS";
    public const string RotateFailed = "SECRET.ROTATE.FAILED";
    public const string Reveal = "SECRET.REVEAL";
    public const string VersionRevoked = "SECRET.VERSION.REVOKED";

    /// <summary>
    /// Story 29-9 migration imported a stopgap secret into the cabinet.
    /// Emitted by <c>StopgapSecretMigrator</c> once per new row.
    /// </summary>
    public const string MigratedSuccess = "SECRET.MIGRATED.SUCCESS";

    /// <summary>
    /// Story 29-9 migration tried to import a stopgap secret and
    /// failed (no source value, backend put failure, etc.). Emitted
    /// by <c>StopgapSecretMigrator</c> per failed row.
    /// </summary>
    public const string MigratedFailed = "SECRET.MIGRATED.FAILED";

    /// <summary>
    /// Story 29-9 migration ran but found the cabinet row already
    /// present and therefore did nothing. Emitted so idempotent
    /// re-runs still show up in the audit trail.
    /// </summary>
    public const string MigratedSkipped = "SECRET.MIGRATED.SKIPPED";
}

/// <summary>
/// Outcome flag carried on rotation events so dashboards can group
/// "started" / "succeeded" / "failed" without parsing the event-type
/// string.
/// </summary>
public enum SecretAuditOutcome
{
    /// <summary>Operation succeeded (or "started" for the saga's
    /// first event).</summary>
    Success,

    /// <summary>Operation failed; the
    /// <see cref="SecretAuditEvent.Detail"/> field carries a short
    /// machine-readable reason code.</summary>
    Failure
}

/// <summary>
/// Typed payload for a single audit emission. Story 29-2 maps this
/// onto the <c>domain_events</c> table (tenant-scoped) or the future
/// <c>platform_events</c> table (platform-scoped); Story 29-1 keeps
/// the shape free of any storage coupling so the auditor can be
/// stubbed out in tests.
/// </summary>
/// <param name="EventType">One of the constants on
/// <see cref="SecretAuditEventTypes"/>.</param>
/// <param name="Reference">The secret being acted on. Carries the
/// scope + tenant id so the persistence layer can route the event to
/// the right stream.</param>
/// <param name="ActorUserId">User id of the operator that triggered
/// the action. <see cref="Guid.Empty"/> for system-initiated events
/// (e.g. scheduled auto-rotation).</param>
/// <param name="VersionNumber">Affected version number; null for
/// metadata-only events (LIST, etc.).</param>
/// <param name="Outcome">Coarse success / failure flag.</param>
/// <param name="Detail">Optional machine-readable reason code (e.g.
/// <c>backend_unavailable</c>, <c>handler_threw</c>). Free-form
/// string; never carries plaintext.</param>
/// <param name="OccurredAt">UTC timestamp of the event.</param>
public sealed record SecretAuditEvent(
    string EventType,
    SecretRef Reference,
    Guid ActorUserId,
    int? VersionNumber,
    SecretAuditOutcome Outcome,
    string? Detail,
    DateTimeOffset OccurredAt);

/// <summary>
/// Port for emitting <see cref="SecretAuditEvent"/>s
/// (Story 29-1 AC5). Story 29-2 wires the Postgres-backed
/// implementation that writes to <c>domain_events</c> /
/// <c>platform_events</c>; Story 29-1 ships a null implementation
/// (<see cref="NullSecretAccessAuditor"/>) so the
/// <see cref="ISecretStore"/> facade can be exercised in tests
/// without coupling to the persistence layer.
///
/// <para>Every <see cref="ISecretStoreBackend"/> method call must
/// emit exactly one event (AC5); the facade enforces this contract.</para>
/// </summary>
public interface ISecretAccessAuditor
{
    /// <summary>
    /// Persist the event. Must not throw on persistence failure — the
    /// caller has already mutated the secret state and an audit-log
    /// outage should not roll that back. Implementations log the
    /// failure to the application log instead.
    /// </summary>
    Task EmitAsync(SecretAuditEvent auditEvent, CancellationToken ct = default);
}

/// <summary>
/// Default <see cref="ISecretAccessAuditor"/> wired by
/// <c>AddTammaSecrets()</c> when no real auditor is registered. Drops
/// every event on the floor — adequate for unit tests and for the
/// interface-only Story 29-1 wiring; Story 29-2 swaps it for the
/// Postgres-backed real impl.
/// </summary>
public sealed class NullSecretAccessAuditor : ISecretAccessAuditor
{
    public Task EmitAsync(
        SecretAuditEvent auditEvent, CancellationToken ct = default) =>
        Task.CompletedTask;
}
