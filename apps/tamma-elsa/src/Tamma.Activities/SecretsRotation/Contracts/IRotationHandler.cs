namespace Tamma.Activities.SecretsRotation.Contracts;

/// <summary>
/// Story 29-6 AC3 — plug-in port for one secret-consumer class
/// (<c>postgres</c>, <c>cranl</c>, <c>hmac</c>, <c>generic-http</c>,
/// &#8230;). Implementations encapsulate the push / probe / rollback
/// semantics for one concrete downstream system; the
/// <c>RotateSecretWorkflow</c> (Story 29-6) resolves the right handler
/// by the secret's first
/// <c>ConsumerRef.System</c> via keyed DI.
///
/// <para>Each method is called out of band from the workflow. The
/// handler must be idempotent: if Elsa re-plays a step the handler
/// has to detect the partial work and converge rather than double-
/// push or double-rollback.</para>
///
/// <para>Handler implementations live in
/// <c>Tamma.Api.Services.Secrets.Handlers/</c> — this contract lives in
/// <c>Tamma.Activities</c> so the Elsa activities can reference it
/// without taking a dependency on <c>Tamma.Api</c>.</para>
/// </summary>
public interface IRotationHandler
{
    /// <summary>
    /// Stable key for keyed DI resolution. Matches a secret's
    /// <c>ConsumerRef[0].System</c>. Examples: <c>postgres</c>,
    /// <c>cranl</c>, <c>hmac</c>, <c>generic-http</c>.
    /// </summary>
    string System { get; }

    /// <summary>
    /// Push the freshly-minted plaintext to the downstream consumer.
    /// <para>In a DB-password handler this runs
    /// <c>ALTER ROLE &lt;role&gt; WITH PASSWORD '&lt;new&gt;'</c>. In a
    /// Cranl-env handler this fetches the current env, merges the
    /// new value, <c>PUT</c>s the result and triggers a reload.</para>
    /// <para>Must be idempotent: re-invoking against the same
    /// <paramref name="newPlaintext"/> / <paramref name="ctx"/>.RotationCorrelationId
    /// must be a no-op (or a converging retry).</para>
    /// </summary>
    Task PushAsync(
        RotationTarget target,
        string newPlaintext,
        RotationContext ctx,
        CancellationToken ct);

    /// <summary>
    /// Verify the new value is live — DB-handler opens a fresh pool
    /// and runs <c>SELECT 1</c>; Cranl-handler polls the app status +
    /// hits <c>/health</c>.
    /// </summary>
    Task<ProbeResult> ProbeAsync(
        RotationTarget target,
        RotationContext ctx,
        CancellationToken ct);

    /// <summary>
    /// Undo the push. Called by the workflow's compensation path
    /// when probe fails after all retries. Must recover the consumer
    /// to the previous working value — DB-handler ALTERs back with
    /// the previous plaintext; Cranl-handler <c>PUT</c>s the previous
    /// env + reloads.
    /// </summary>
    Task RollbackAsync(
        RotationTarget target,
        string newPlaintext,
        RotationContext ctx,
        CancellationToken ct);

    /// <summary>
    /// Optional hook called after the grace window expires on a
    /// successfully-rotated secret. Postgres returns the old version
    /// to a NULL password (effectively disabling the old credentials);
    /// Cranl is a no-op since the env-var is already replaced.
    /// Default implementation does nothing so most handlers skip.
    /// </summary>
    Task RevokeOldAsync(
        RotationTarget target,
        string oldPlaintext,
        RotationContext ctx,
        CancellationToken ct) => Task.CompletedTask;
}
