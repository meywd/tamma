namespace Tamma.Api.Services.Secrets.Reveal;

/// <summary>
/// Story 29-3 reveal-once UX service. Owns the full
/// create-secret-and-return-token / rotate-and-return-token /
/// consume-token flow. Composes the Story 29-1 abstractions
/// (<see cref="ISecretStoreBackend"/>, <see cref="ISecretAccessAuditor"/>,
/// <see cref="SecretMetadataFactory"/>) without modifying any of them.
///
/// <para><b>Single responsibility for plaintext reveal</b>: this is
/// the only service in the code base authorised to emit a
/// <c>SECRET.REVEAL</c> audit event. Every reveal attempt — success
/// or failure — flows through <see cref="ConsumeAsync"/> so the
/// auditor is the single choke point (Story 29-3 AC4, AC10).</para>
///
/// <para>The service is <c>scoped</c> — it carries no long-lived
/// state. A fresh instance per request lets the DbContext factories
/// (Story 29-2 <c>SecretsDbContext</c>, Story 29-3
/// <see cref="SecretRevealDbContext"/>) hand out short-lived contexts
/// without cross-request sharing.</para>
/// </summary>
public interface ISecretRevealService
{
    /// <summary>
    /// Create a brand-new secret row + first version and issue a
    /// reveal token for the plaintext. The plaintext is NOT returned
    /// in the result — it is only retrievable via
    /// <see cref="ConsumeAsync"/> with the returned token within 60
    /// seconds.
    ///
    /// <para>Emits a <c>SECRET.WRITE</c> audit event on success.</para>
    /// </summary>
    /// <param name="name">Slug per the Story 29-1 name grammar.</param>
    /// <param name="scope">Platform or Tenant.</param>
    /// <param name="tenantId">Required when
    /// <paramref name="scope"/> is <see cref="SecretScope.Tenant"/>.</param>
    /// <param name="purpose">Typed purpose.</param>
    /// <param name="initialPlaintext">Non-empty plaintext. A future
    /// iteration may accept a server-generated length instead, but for
    /// 29-3 the caller always supplies the value.</param>
    /// <param name="consumerRefs">Downstream consumers; may be
    /// empty.</param>
    /// <param name="ownerUserId">Operator user id. Attaches to the
    /// metadata row and to the reveal-token row so only the owner's
    /// session can poll status.</param>
    /// <param name="rotationSchedule">Cadence; defaults to
    /// <see cref="RotationSchedule.None"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The persisted metadata + the reveal token + its
    /// expiry.</returns>
    Task<RevealTokenIssueResult> IssueCreateAsync(
        string name,
        SecretScope scope,
        Guid? tenantId,
        SecretPurpose purpose,
        string initialPlaintext,
        IReadOnlyList<ConsumerRef>? consumerRefs,
        Guid ownerUserId,
        RotationSchedule? rotationSchedule,
        CancellationToken ct = default);

    /// <summary>
    /// Rotate an existing secret to a new version and issue a reveal
    /// token for the new plaintext. The old version is NOT revealable
    /// — it stays internal state for rotation handlers only (Story
    /// 29-3 AC6).
    ///
    /// <para>Emits <c>SECRET.ROTATE.STARTED</c> +
    /// <c>SECRET.ROTATE.SUCCESS</c> audit events on success.</para>
    /// </summary>
    /// <param name="secretId">Existing secret id.</param>
    /// <param name="newPlaintext">New plaintext to mint as the next
    /// version.</param>
    /// <param name="actorUserId">Operator user id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated metadata + the reveal token + its
    /// expiry.</returns>
    /// <exception cref="KeyNotFoundException">No secret matches
    /// <paramref name="secretId"/>.</exception>
    Task<RevealTokenIssueResult> IssueRotateAsync(
        Guid secretId,
        string newPlaintext,
        Guid actorUserId,
        CancellationToken ct = default);

    /// <summary>
    /// Consume a reveal token: returns the plaintext once, then flips
    /// the token to <c>consumed</c>. Subsequent calls against the same
    /// token return <see cref="RevealTokenConsumeOutcome.AlreadyConsumed"/>.
    ///
    /// <para>Emits <c>SECRET.REVEAL</c> on success only. A failed
    /// reveal — expired token, already-consumed, not-found — is
    /// auditable but does NOT emit a <c>SECRET.REVEAL</c> (the
    /// plaintext was not disclosed); the auditor emits
    /// <c>SECRET.READ</c> with <c>Outcome=Failure</c> instead so
    /// dashboards can graph attempted vs successful reveals
    /// separately.</para>
    /// </summary>
    /// <param name="rawToken">Raw base64url-encoded token as returned
    /// by <see cref="IssueCreateAsync"/> /
    /// <see cref="IssueRotateAsync"/>.</param>
    /// <param name="caller">Context captured by the endpoint layer
    /// (User-Agent, IP). Used only for the audit row.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<RevealTokenConsumeResult> ConsumeAsync(
        string rawToken,
        RevealCallerContext caller,
        CancellationToken ct = default);

    /// <summary>
    /// Background sweep helper: flips every <c>status='unused'</c> row
    /// whose <see cref="SecretRevealTokenRow.ExpiresAt"/> is in the
    /// past to <c>status='expired'</c>. Called by
    /// <see cref="RevealTokenSweeper"/> every 30 seconds. Exposed on
    /// the interface so tests can drive the sweep deterministically.
    /// </summary>
    /// <returns>Number of rows flipped.</returns>
    Task<int> SweepExpiredAsync(CancellationToken ct = default);
}
