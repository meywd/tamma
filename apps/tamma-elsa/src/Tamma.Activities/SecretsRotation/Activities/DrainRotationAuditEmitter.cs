using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;
using Tamma.Activities.SecretsRotation.Contracts;

namespace Tamma.Activities.SecretsRotation.Activities;

/// <summary>
/// Engine-side implementation of <see cref="IRotationAuditEmitter"/>.
///
/// <para><b>Why this exists.</b> <c>RotateSecretWorkflow</c> runs inside
/// <c>Tamma.ElsaServer</c> (the engine). The concrete
/// <c>RotationAuditEmitter</c> lives in <c>Tamma.Api</c> (it forwards to
/// <c>IPlatformEventPublisher</c>), which the engine cannot reference — so
/// <c>IRotationAuditEmitter</c> was unregistered there and
/// <c>ctx.GetRequiredService&lt;IRotationAuditEmitter&gt;()</c> threw
/// <c>No service for type IRotationAuditEmitter</c> at runtime, crashing the
/// rotation saga and losing the audit trail entirely.</para>
///
/// <para><b>What it does.</b> Maps each <see cref="RotationAuditEvent"/> to a
/// <see cref="TammaEvent"/> and appends it to the workflow's <c>tamma:events</c>
/// list (reached via the ambient <see cref="RotationAuditDrainScope"/> that
/// <see cref="RotateSecretSagaActivity"/> opens around the saga run). The
/// merged <c>EventPersistenceMiddleware</c> then drains those events to
/// <c>POST /api/engine/events</c> → the tenant's <c>domain_events</c> — the
/// same durable path every other engine audit event rides. The produced events
/// carry the SAME <c>EventType</c> + tag keys (<c>secretId</c>,
/// <c>rotationCorrelationId</c>, <c>tenantId</c>, <c>versionNumber</c>) as the
/// Api-side emitter so dashboards/alert rules pattern-match identically.</para>
///
/// <para>Per the interface contract, <see cref="EmitAsync"/> never throws on a
/// persistence gap — if there is no ambient drain scope (e.g. resolved outside
/// a saga run) the emit is a logged no-op rather than a crash.</para>
/// </summary>
public sealed class DrainRotationAuditEmitter : IRotationAuditEmitter
{
    private readonly ILogger<DrainRotationAuditEmitter>? _logger;

    public DrainRotationAuditEmitter(ILogger<DrainRotationAuditEmitter>? logger = null)
    {
        _logger = logger;
    }

    public Task EmitAsync(RotationAuditEvent evt, CancellationToken ct)
    {
        try
        {
            var scope = RotationAuditDrainScope.Current;
            if (scope is null)
            {
                // No workflow drain context in scope — nothing to append to.
                // Must not throw (the saga has already mutated state); log so
                // a genuine wiring gap is visible.
                _logger?.LogWarning(
                    "Rotation audit event {EventType} for secret {SecretId} dropped — "
                    + "no RotationAuditDrainScope in scope (emitter resolved outside a saga run?)",
                    evt.EventType, evt.SecretId);
                return Task.CompletedTask;
            }

            var tammaEvent = MapToTammaEvent(evt, scope);
            scope.Events.Add(tammaEvent);

            _logger?.LogDebug(
                "Rotation audit event {EventType} queued for the DCB drain (secret {SecretId}, correlation {CorrelationId})",
                evt.EventType, evt.SecretId, evt.RotationCorrelationId);
        }
        catch (Exception ex)
        {
            // Contract: never throw on a persistence failure.
            _logger?.LogWarning(ex,
                "Failed to queue rotation audit event {EventType} for secret {SecretId}",
                evt.EventType, evt.SecretId);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Pure projection <see cref="RotationAuditEvent"/> → <see cref="TammaEvent"/>.
    /// Public + static so it's unit-testable without an Elsa runtime and so the
    /// tag/data shape can be asserted against the Api-side emitter's.
    /// </summary>
    public static TammaEvent MapToTammaEvent(RotationAuditEvent evt, RotationAuditDrainScope scope)
    {
        // Status mirrors the saga semantics the Api side encodes implicitly:
        // a *.FAILED event is an error; everything else is a success step.
        var isFailure =
            evt.EventType.EndsWith(".FAILED", StringComparison.Ordinal)
            || evt.EventType == RotationAuditEvents.Failed;

        // Tags = the queryable DCB index keys. Same key set the Api-side
        // RotationAuditEmitter.BuildTagsAndData writes.
        var tags = new Dictionary<string, object?>
        {
            ["secretId"] = evt.SecretId,
            ["rotationCorrelationId"] = evt.RotationCorrelationId,
            ["tenantId"] = evt.TenantId,
            ["versionNumber"] = evt.VersionNumber,
        };

        // Data = the structured payload; detail folded in when present (same as
        // the Api side).
        var data = new Dictionary<string, object?>(evt.Data);
        if (!string.IsNullOrEmpty(evt.Detail))
            data["detail"] = evt.Detail;

        return new TammaEvent
        {
            EventType = evt.EventType,
            Status = isFailure ? "error" : "success",
            Error = isFailure ? evt.Detail : null,
            Timestamp = evt.OccurredAt.UtcDateTime,
            ActivityId = scope.ActivityId,
            ActivityName = scope.ActivityName,
            WorkflowInstanceId = scope.WorkflowInstanceId,
            Tags = tags,
            Data = data,
        };
    }
}
