namespace Tamma.Activities.SecretsRotation.Contracts;

/// <summary>
/// Story 29-6 AC5 — typed audit port for the rotation saga. Activities
/// resolve one of these from DI and emit per-step events; the Api
/// layer wires a concrete impl that forwards to
/// <c>IPlatformEventPublisher</c> and/or the tenant's
/// <c>domain_events</c> log.
///
/// <para>The event-type constants live on <see cref="RotationAuditEvents"/>
/// so dashboards pattern-match against them instead of bare strings.</para>
/// </summary>
public interface IRotationAuditEmitter
{
    /// <summary>
    /// Persist + fan-out a rotation saga event. Must not throw on
    /// persistence failure — the caller has already mutated state.
    /// Implementations log the failure instead.
    /// </summary>
    Task EmitAsync(RotationAuditEvent evt, CancellationToken ct);
}

/// <summary>
/// Carries the rotation-saga event payload. The raw event gets written
/// to either <c>platform_events</c> (when <see cref="TenantId"/> is
/// null) or the tenant's <c>domain_events</c>.
/// </summary>
public sealed record RotationAuditEvent(
    string EventType,
    Guid SecretId,
    Guid? TenantId,
    string RotationCorrelationId,
    int? VersionNumber,
    string? Detail,
    IReadOnlyDictionary<string, object?> Data,
    DateTimeOffset OccurredAt)
{
    public static RotationAuditEvent Create(
        string eventType,
        Guid secretId,
        Guid? tenantId,
        string rotationCorrelationId,
        int? versionNumber = null,
        string? detail = null,
        IReadOnlyDictionary<string, object?>? data = null) =>
        new(
            eventType,
            secretId,
            tenantId,
            rotationCorrelationId,
            versionNumber,
            detail,
            data ?? new Dictionary<string, object?>(),
            DateTimeOffset.UtcNow);
}

/// <summary>
/// Canonical event-type constants emitted by the rotation saga. Match
/// the Story 29-6 AC5 list verbatim so alert rules can pattern-match.
/// </summary>
public static class RotationAuditEvents
{
    /// <summary>Emitted by the trigger surface (operator endpoint /
    /// scheduled auto-rotation) when a rotation is dispatched to the
    /// <c>rotate-secret</c> workflow — BEFORE the saga's STARTED.</summary>
    public const string Requested = "SECRET.ROTATION.REQUESTED";

    /// <summary>Emitted by the trigger surface when the per-secret
    /// concurrency guard refuses a rotation because one is already in
    /// flight (<c>rotation_in_progress</c>).</summary>
    public const string Rejected = "SECRET.ROTATION.REJECTED";

    public const string Started = "SECRET.ROTATION.STARTED";
    public const string Staged = "SECRET.ROTATION.STAGED";

    public const string PushSuccess = "SECRET.ROTATION.PUSH.SUCCESS";
    public const string PushFailed = "SECRET.ROTATION.PUSH.FAILED";

    public const string ProbeSuccess = "SECRET.ROTATION.PROBE.SUCCESS";
    public const string ProbeFailed = "SECRET.ROTATION.PROBE.FAILED";

    public const string Switched = "SECRET.ROTATION.SWITCHED";
    public const string Activated = "SECRET.ROTATION.ACTIVATED";
    public const string Completed = "SECRET.ROTATION.COMPLETED";
    public const string Failed = "SECRET.ROTATION.FAILED";

    public const string Retired = "SECRET.ROTATION.RETIRED";
    public const string VersionRetired = "SECRET.VERSION.RETIRED";

    public const string CompensationStarted = "SECRET.ROTATION.COMPENSATION.STARTED";
    public const string CompensationSuccess = "SECRET.ROTATION.COMPENSATION.SUCCESS";
    public const string CompensationFailed = "SECRET.ROTATION.COMPENSATION.FAILED";

    public const string RetireScheduled = "SECRET.ROTATION.RETIRE_SCHEDULED";
    public const string PoolDrained = "SECRET.ROTATION.POOL.DRAINED";
    public const string CranlEnvPushed = "SECRET.ROTATION.CRANL.ENV_PUSHED";
    public const string CranlReloadTriggered = "SECRET.ROTATION.CRANL.RELOAD_TRIGGERED";
    public const string CranlRateLimitHit = "SECRET.ROTATION.CRANL.RATE_LIMIT_HIT";
    public const string RollbackRoleDisabled = "SECRET.ROTATION.ROLLBACK.ROLE_DISABLED";
}
