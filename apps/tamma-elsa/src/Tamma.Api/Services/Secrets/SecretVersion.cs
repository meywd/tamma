namespace Tamma.Api.Services.Secrets;

/// <summary>
/// Lifecycle status of a single <see cref="SecretVersion"/>.
/// Matches the rotation-saga vocabulary in Story 29-1 AC3 and the
/// research-notes saga sketch (research/secret-management-and-multi-
/// backend-provisioning-2026.md §3).
/// </summary>
public enum SecretVersionStatus
{
    /// <summary>
    /// Version has been minted but not yet pushed to the consumer.
    /// Set by <see cref="ISecretStore.RotateAsync"/> as the first step
    /// of the rotation saga.
    /// </summary>
    Pending,

    /// <summary>
    /// Version is the live one for reads. Exactly one version per
    /// secret is <see cref="Active"/> at any point in time
    /// (enforced by the backend driver in Story 29-2).
    /// </summary>
    Active,

    /// <summary>
    /// Previous active version is kept readable for a grace window so
    /// in-flight requests don't fail mid-rotation. Flips to
    /// <see cref="Revoked"/> when the grace timer expires.
    /// </summary>
    RetiredGrace,

    /// <summary>
    /// Plaintext for this version is no longer retrievable (the backend
    /// driver may scrub the row). Audit history is retained.
    /// </summary>
    Revoked
}

/// <summary>
/// Metadata for a single version of a secret. Plaintext bytes are
/// <b>not</b> in this record — they live in the backend driver's
/// storage and are only handed to a registered rotation handler via
/// the out-of-band <see cref="ISecretStore.RotateAsync"/> path
/// (Story 29-1 AC1, AC3).
///
/// <para>Versions are monotonic per secret and never re-used. The
/// caller queries <see cref="ISecretStore.ListVersionsAsync"/> to walk
/// history; the backend keeps revoked rows for the audit trail (Story
/// 29-2 AC) but scrubs the ciphertext columns.</para>
/// </summary>
/// <param name="SecretId">FK to <see cref="SecretMetadata.Id"/>.</param>
/// <param name="VersionNumber">Monotonic 1-based version number.</param>
/// <param name="Status">Lifecycle status — see <see cref="SecretVersionStatus"/>.</param>
/// <param name="CreatedAt">UTC timestamp when the version was minted.</param>
/// <param name="ActivatedAt">UTC timestamp when the version flipped to <see cref="SecretVersionStatus.Active"/>; null while still pending.</param>
/// <param name="RetiredAt">UTC timestamp when the version flipped to <see cref="SecretVersionStatus.RetiredGrace"/> or <see cref="SecretVersionStatus.Revoked"/>; null while still active or pending.</param>
/// <param name="CreatedByUserId">User id of the operator (or system user) that minted this version.</param>
public sealed record SecretVersion(
    Guid SecretId,
    int VersionNumber,
    SecretVersionStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ActivatedAt,
    DateTimeOffset? RetiredAt,
    Guid CreatedByUserId);
