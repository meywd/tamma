namespace Tamma.Activities.SecretsRotation.Contracts;

/// <summary>
/// Story 29-6 — thin port the rotation activities use to talk to the
/// secret store without taking a dependency on
/// <c>Tamma.Api.Services.Secrets.ISecretStore</c>. The Api layer
/// registers a concrete bridge
/// (<c>SecretStoreRotationGateway</c>) that fans the calls out to the
/// real store + backend.
///
/// <para>All operations are idempotent on
/// <c>rotationCorrelationId</c> where it matters (mint, activate,
/// retire). Replayed activities see the existing state rather than
/// duplicate rows.</para>
/// </summary>
public interface ISecretRotationGateway
{
    /// <summary>
    /// Resolve a secret by id into the fields the rotation workflow
    /// needs. Returns null when the id is unknown — the activity turns
    /// that into <c>SECRET.ROTATE.FAILED</c>.
    /// </summary>
    Task<SecretRotationSnapshot?> GetSnapshotAsync(
        Guid secretId,
        CancellationToken ct);

    /// <summary>
    /// Mint a new version row in <c>Pending</c> status and persist its
    /// plaintext to the backend. Returns the version number. Throws
    /// when the secret does not exist.
    ///
    /// <para>Idempotent on <paramref name="rotationCorrelationId"/>:
    /// a second call with the same id returns the existing version
    /// number instead of creating a duplicate row. The gateway stores
    /// the correlation id on the version metadata or audit row.</para>
    /// </summary>
    Task<int> MintPendingVersionAsync(
        Guid secretId,
        string newPlaintext,
        string rotationCorrelationId,
        Guid operatorUserId,
        CancellationToken ct);

    /// <summary>
    /// Hard-delete the pending row + its backend bytes. Used by the
    /// workflow's compensation step when the rotation aborts before
    /// activation. No audit event on the row — the gateway emits a
    /// <c>SECRET.VERSION.DELETED</c> event on the audit bus so the
    /// removal is visible.
    /// </summary>
    Task DeleteVersionAsync(
        Guid secretId,
        int versionNumber,
        CancellationToken ct);

    /// <summary>
    /// Flip the new version <c>Pending → Active</c> and the previous
    /// active (if any) <c>Active → RetiredGrace</c> atomically.
    /// Records the activation timestamp.
    /// </summary>
    Task ActivateVersionAsync(
        Guid secretId,
        int newVersionNumber,
        int previousVersionNumber,
        CancellationToken ct);

    /// <summary>
    /// Revert an activation (compensation). Flips the new version back
    /// to <c>Pending</c> and the previous version back to <c>Active</c>.
    /// Used only when compensation follows a successful activate —
    /// normally the workflow compensates before reaching activate.
    /// </summary>
    Task RevertActivationAsync(
        Guid secretId,
        int newVersionNumber,
        int previousVersionNumber,
        CancellationToken ct);

    /// <summary>
    /// Terminal retirement — flip <c>RetiredGrace → Revoked</c> and
    /// scrub the ciphertext. Called by the sweeper after the grace
    /// window expires. Idempotent — calling on an already-revoked
    /// version is a no-op.
    /// </summary>
    Task RetireVersionAsync(
        Guid secretId,
        int versionNumber,
        CancellationToken ct);

    /// <summary>
    /// Read back a version's plaintext. Used by handlers on rollback
    /// (need the previous plaintext to push back) and by the sweeper
    /// (handler's RevokeOldAsync hook).
    /// </summary>
    Task<string?> GetVersionPlaintextAsync(
        Guid secretId,
        int versionNumber,
        CancellationToken ct);
}

/// <summary>
/// Snapshot of a secret's rotation-relevant fields. Carries enough for
/// the activities to build a <see cref="RotationTarget"/> without the
/// activity depending on the full <c>SecretMetadata</c> record.
/// </summary>
/// <param name="SecretId">Store id.</param>
/// <param name="Name">Slug for logs.</param>
/// <param name="TenantId">Owning tenant; null for platform scope.</param>
/// <param name="ConsumerSystem">First consumer-ref system.</param>
/// <param name="ConsumerIdentifier">First consumer-ref identifier.</param>
/// <param name="ActiveVersionNumber">Currently-active version; 0 when
/// this is the first rotation.</param>
public sealed record SecretRotationSnapshot(
    Guid SecretId,
    string Name,
    Guid? TenantId,
    string ConsumerSystem,
    string ConsumerIdentifier,
    int ActiveVersionNumber);
