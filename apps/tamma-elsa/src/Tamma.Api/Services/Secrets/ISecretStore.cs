namespace Tamma.Api.Services.Secrets;

/// <summary>
/// Typed read/write surface for the platform secret cabinet
/// (Story 29-1 AC1). Backed in production (Story 29-2) by a Postgres
/// envelope-encrypted store; in-memory test fixtures and a future
/// OpenBao driver (Story 28-13) plug in via
/// <see cref="ISecretStoreBackend"/>.
///
/// <para><b>Plaintext rule</b>: none of these methods returns
/// plaintext bytes through their public signature. Plaintext is only
/// ever returned to a registered rotation handler via the
/// out-of-band <see cref="RotateAsync"/> path (the handler receives
/// the freshly-minted value via callback so it can push it to the
/// downstream consumer). HTTP endpoints surfacing this interface must
/// rely on the reveal-once UX (Story 29-3).</para>
///
/// <para>All methods are async + cancellable. The store emits an
/// <see cref="ISecretAccessAuditor"/> event for every read /
/// write / rotate / retire / version probe call.</para>
/// </summary>
public interface ISecretStore
{
    /// <summary>
    /// Create a new secret row + (optionally) mint its first version
    /// from <see cref="CreateSecretRequest.InitialPlaintext"/>. Throws
    /// when the <c>(Scope, TenantId, Name)</c> tuple already exists.
    /// </summary>
    Task<SecretMetadata> CreateAsync(
        CreateSecretRequest request, CancellationToken ct = default);

    /// <summary>
    /// Look up a secret by ref. Returns null when the ref does not
    /// resolve to a row the caller is authorised to see — the store
    /// does not distinguish "not found" from "not authorised" in the
    /// return value to avoid leaking existence (the audit event still
    /// captures the attempt).
    /// </summary>
    Task<SecretMetadata?> GetAsync(
        SecretRef reference, CancellationToken ct = default);

    /// <summary>
    /// List secrets the caller can see, filtered per
    /// <paramref name="filter"/>. Empty list when no rows match.
    /// </summary>
    Task<IReadOnlyList<SecretMetadata>> ListAsync(
        SecretListFilter filter, CancellationToken ct = default);

    /// <summary>
    /// Rotate a secret to a new version. Per the rotation saga
    /// (research notes §3) the new version starts in
    /// <see cref="SecretVersionStatus.Pending"/>; the store flips it
    /// to <see cref="SecretVersionStatus.Active"/> after the rotation
    /// handler signals success. The previous active version moves to
    /// <see cref="SecretVersionStatus.RetiredGrace"/> for the grace
    /// window.
    /// </summary>
    Task<SecretMetadata> RotateAsync(
        SecretRef reference,
        RotateSecretRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Force-revoke a specific version: scrub the ciphertext, flip
    /// status to <see cref="SecretVersionStatus.Revoked"/>. Throws
    /// when called against the current active version (use
    /// <see cref="RotateAsync"/> first so there's a successor before
    /// the active row is taken away).
    /// </summary>
    Task<SecretMetadata> RetireVersionAsync(
        SecretRef reference,
        int versionNumber,
        CancellationToken ct = default);

    /// <summary>
    /// Read a single version's metadata. Plaintext is not surfaced —
    /// see the rotation-handler path on
    /// <see cref="RotateAsync"/>.
    /// </summary>
    Task<SecretVersion?> GetVersionAsync(
        SecretRef reference,
        int versionNumber,
        CancellationToken ct = default);

    /// <summary>
    /// List every version of a secret, newest first. Empty list when
    /// the secret does not exist or is not visible to the caller.
    /// </summary>
    Task<IReadOnlyList<SecretVersion>> ListVersionsAsync(
        SecretRef reference, CancellationToken ct = default);
}
