namespace Tamma.Activities.SecretsRotation.Contracts;

/// <summary>
/// Story 29-6 AC3 — execution-time context threaded through every
/// <see cref="IRotationHandler"/> call. Separates the per-rotation
/// inputs (correlation id, operator id, dry-run flag, handler-specific
/// overrides) from the stable secret identity
/// (<see cref="RotationTarget"/>).
///
/// <para><see cref="RotationCorrelationId"/> is the primary idempotency
/// key. Handlers include it in out-of-band state (e.g. a Cranl
/// <c>x-tamma-rot-id</c> header) so a re-invocation from a replayed
/// Elsa activity can converge rather than duplicate the push.</para>
/// </summary>
/// <param name="RotationCorrelationId">Stable identifier for this one
/// rotation saga. Mirrors the Story 29-6 brief's
/// <c>rotationCorrelationId</c> input.</param>
/// <param name="OperatorUserId">User id of the caller that triggered
/// the rotation. <see cref="Guid.Empty"/> for scheduled / auto
/// rotations.</param>
/// <param name="DryRun">When true the handler validates the operation
/// (generate + check + render SQL) but never reaches out to the
/// downstream system. Used by the admin UI's "preview rotation"
/// button (Story 29-4 + 29-7 AC9).</param>
/// <param name="HandlerOptions">Free-form string→string map for
/// handler-specific overrides. <see cref="CranlEnvVarRotationHandler"/>
/// reads <c>CranlMode</c>, <c>ReloadTimeoutSeconds</c>;
/// <see cref="PostgresRoleRotationHandler"/> reads
/// <c>AdminConnectionString</c>. Never populated from an untrusted
/// source — only the workflow (server-side) can set entries here.</param>
public sealed record RotationContext(
    string RotationCorrelationId,
    Guid OperatorUserId,
    bool DryRun,
    IReadOnlyDictionary<string, string> HandlerOptions)
{
    /// <summary>Convenience: read a handler option or fall back to a default.</summary>
    public string GetOption(string key, string defaultValue) =>
        HandlerOptions.TryGetValue(key, out var v) ? v : defaultValue;

    /// <summary>Convenience factory — context without any handler options.</summary>
    public static RotationContext ForCorrelation(string correlationId, Guid? operatorUserId = null) =>
        new(
            correlationId,
            operatorUserId ?? Guid.Empty,
            DryRun: false,
            HandlerOptions: new Dictionary<string, string>());
}
