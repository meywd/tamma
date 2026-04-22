namespace Tamma.Data.Abstractions;

/// <summary>
/// Story 28-5 — narrow port for AES-encrypting the per-tenant connection
/// string before it is written to <c>tenants.EncryptedConnectionString</c>.
/// The activity assembly references this contract (lives in
/// Tamma.Data.Abstractions); the production adapter wraps the existing
/// <c>TenantSecretProtector</c> in Tamma.Api so the cryptographic core
/// stays in one place. Tests inject a stub that round-trips through
/// base64 — sufficient for asserting the persistence path is wired
/// without standing up real key material.
///
/// <para>The KEK version is a slot for future rotation (Story 28-12 KEK
/// rotation). Today the protector returns 1; downstream readers
/// (LRU resolver) consult the slot when decrypting.</para>
/// </summary>
public interface ITenantConnectionStringProtector
{
    /// <summary>
    /// Encrypt a plaintext connection string. Returns the ciphertext
    /// envelope ready to drop into the bytea column.
    /// </summary>
    byte[] Encrypt(string plaintext);

    /// <summary>
    /// Current KEK slot. Persisted alongside the ciphertext so a future
    /// rotation can decrypt with the right key.
    /// </summary>
    int CurrentKekVersion { get; }
}
