using Tamma.Activities.Core;

namespace Tamma.Activities.SecretsRotation.Activities;

/// <summary>
/// Ambient bridge from the rotation saga's <c>ActivityExecutionContext</c> to
/// the engine-side <see cref="Contracts.IRotationAuditEmitter"/>.
///
/// <para><b>Why an ambient?</b> The rotation audit-emit calls happen deep
/// inside <c>SagaRunner</c>, which (by design — it's unit-testable without
/// Elsa) holds no <c>ActivityExecutionContext</c>. The emitter interface
/// <c>EmitAsync(RotationAuditEvent, ct)</c> likewise carries no context. But
/// the durable DCB drain (<c>EventPersistenceMiddleware</c>) reads the
/// workflow's <c>tamma:events</c> transient list, which is only reachable via
/// the context. Rather than thread the context through ~20 emit call sites,
/// <see cref="RotateSecretSagaActivity"/> (which DOES hold the context) opens
/// a scope around the single saga run; the engine emitter reads the ambient
/// <c>tamma:events</c> list from it and appends mapped events that then ride
/// the drain to <c>domain_events</c>.</para>
///
/// <para>The scope is opened with a <c>using</c> tightly around the saga body
/// and restores the previous ambient on dispose, so it never leaks across runs
/// (and nests safely — an inner scope restores the outer on dispose).</para>
/// </summary>
public sealed class RotationAuditDrainScope : IDisposable
{
    private static readonly AsyncLocal<RotationAuditDrainScope?> AmbientScope = new();

    private readonly RotationAuditDrainScope? _previous;
    private bool _disposed;

    private RotationAuditDrainScope(
        List<TammaEvent> events,
        string? activityId,
        string? activityName,
        string? workflowInstanceId)
    {
        Events = events;
        ActivityId = activityId;
        ActivityName = activityName;
        WorkflowInstanceId = workflowInstanceId;
        _previous = AmbientScope.Value;
        AmbientScope.Value = this;
    }

    /// <summary>The workflow's <c>tamma:events</c> list the emitter appends to.</summary>
    public List<TammaEvent> Events { get; }

    /// <summary>Source activity id stamped onto each emitted event.</summary>
    public string? ActivityId { get; }

    /// <summary>Source activity name stamped onto each emitted event.</summary>
    public string? ActivityName { get; }

    /// <summary>Owning workflow-instance id stamped onto each emitted event.</summary>
    public string? WorkflowInstanceId { get; }

    /// <summary>The ambient scope for the current async flow, or <c>null</c>.</summary>
    public static RotationAuditDrainScope? Current => AmbientScope.Value;

    /// <summary>
    /// Open an ambient scope bound to <paramref name="events"/> (the workflow's
    /// <c>tamma:events</c> list). Dispose to restore the previous ambient.
    /// </summary>
    public static RotationAuditDrainScope Begin(
        List<TammaEvent> events,
        string? activityId,
        string? activityName,
        string? workflowInstanceId) =>
        new(events ?? throw new ArgumentNullException(nameof(events)),
            activityId, activityName, workflowInstanceId);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Restore only if we're still the active ambient (defensive against
        // out-of-order disposal); otherwise leave the current ambient alone.
        if (ReferenceEquals(AmbientScope.Value, this))
            AmbientScope.Value = _previous;
    }
}
