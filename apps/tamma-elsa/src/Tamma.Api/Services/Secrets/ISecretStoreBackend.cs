namespace Tamma.Api.Services.Secrets;

/// <summary>
/// Driver port for the secret store (Story 29-1 AC4). The
/// <see cref="ISecretStore"/> facade owns metadata + the audit
/// pipeline; <see cref="ISecretStoreBackend"/> owns the byte-oriented
/// plaintext storage. Implementations:
/// <list type="bullet">
///   <item><description><c>PostgresSecretStoreBackend</c> — Story
///     29-2; envelope-encrypted with the env-KEK per the 2026-04-17
///     decision (memory:
///     <c>project_epic28_kek_decision.md</c>).</description></item>
///   <item><description><c>OpenBaoSecretStoreBackend</c> — Story
///     28-13 (deferred until a trigger fires).</description></item>
///   <item><description><c>InMemorySecretStoreBackend</c> — Story
///     29-1; ships in tests so subsequent stories don't need a
///     Postgres container to exercise admin / rotation flows.</description></item>
/// </list>
///
/// <para>The backend never sees a <see cref="SecretMetadata"/> — only
/// the storage tuple <c>(secretId, versionNumber)</c> and the byte
/// payload. This separation keeps the audit + invariant enforcement
/// in <see cref="ISecretStore"/> and lets a future OpenBao driver
/// stay narrowly scoped to "put / get / delete a wrapped value".</para>
/// </summary>
public interface ISecretStoreBackend
{
    /// <summary>
    /// Persist a new version's plaintext. The backend wraps the
    /// payload (envelope encryption for Postgres; KMS-managed key for
    /// OpenBao) and stores it indexed by
    /// <c>(secretId, versionNumber)</c>.
    /// </summary>
    Task PutVersionAsync(
        Guid secretId,
        int versionNumber,
        string plaintext,
        CancellationToken ct = default);

    /// <summary>
    /// Read back the plaintext for a specific version. Returns null
    /// when the version row exists but its ciphertext has been
    /// scrubbed (revoked versions; see
    /// <see cref="DeleteVersionAsync"/>). Throws
    /// <see cref="KeyNotFoundException"/> when the version row does
    /// not exist at all.
    /// </summary>
    Task<string?> GetVersionPlaintextAsync(
        Guid secretId,
        int versionNumber,
        CancellationToken ct = default);

    /// <summary>
    /// Scrub the ciphertext for a version (the version row is kept
    /// for audit history; only the bytes are zeroed). Idempotent —
    /// calling on an already-scrubbed row is a no-op.
    /// </summary>
    Task DeleteVersionAsync(
        Guid secretId,
        int versionNumber,
        CancellationToken ct = default);
}
