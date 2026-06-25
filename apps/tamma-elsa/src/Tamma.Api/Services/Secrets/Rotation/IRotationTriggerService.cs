namespace Tamma.Api.Services.Secrets.Rotation;

/// <summary>
/// Story 29-6 (audit gap #2 + #3) — the trigger surface for the
/// <c>rotate-secret</c> Elsa workflow. Used by the operator endpoint
/// (<c>POST /api/v1/secrets/{secretId}/rotate</c>) and the scheduled
/// auto-rotation hosted service. Owns the per-secret concurrency guard,
/// the correlation-id mint, the workflow dispatch, and the
/// <c>SECRET.ROTATION.REQUESTED</c> / <c>SECRET.ROTATION.REJECTED</c>
/// audit events.
/// </summary>
public interface IRotationTriggerService
{
    /// <summary>
    /// Mint a fresh <c>rotationCorrelationId</c>, take the per-secret
    /// concurrency guard, and (when acquired) dispatch the
    /// <c>rotate-secret</c> workflow with the supplied inputs. Emits
    /// <c>SECRET.ROTATION.REQUESTED</c> on dispatch or
    /// <c>SECRET.ROTATION.REJECTED(rotation_in_progress)</c> when a
    /// rotation is already in flight.
    /// </summary>
    /// <param name="secretId">Secret to rotate.</param>
    /// <param name="operatorUserId"><see cref="Guid.Empty"/> for
    /// scheduled / auto rotations.</param>
    /// <param name="newPlaintext">Operator-supplied new value, or null to
    /// have the saga CSPRNG-generate one of <paramref name="generateLength"/>
    /// bytes.</param>
    /// <param name="generateLength">Length (bytes) for generated plaintext
    /// when <paramref name="newPlaintext"/> is null.</param>
    /// <param name="graceWindowSeconds">Retire grace window; 0 ⇒ saga
    /// default (15 min).</param>
    Task<RotationTriggerResult> TriggerRotationAsync(
        Guid secretId,
        Guid operatorUserId,
        string? newPlaintext,
        int? generateLength,
        long graceWindowSeconds,
        CancellationToken ct);
}

/// <summary>
/// Outcome of <see cref="IRotationTriggerService.TriggerRotationAsync"/>.
/// </summary>
/// <param name="Accepted"><c>true</c> when the workflow was dispatched;
/// <c>false</c> when the concurrency guard rejected it.</param>
/// <param name="RotationCorrelationId">The minted correlation id (always
/// returned so the caller can surface it / log it).</param>
/// <param name="Reason">Non-null rejection reason when
/// <see cref="Accepted"/> is <c>false</c> (e.g.
/// <c>rotation_in_progress</c>).</param>
public sealed record RotationTriggerResult(
    bool Accepted,
    string RotationCorrelationId,
    string? Reason);
