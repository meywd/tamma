namespace Tamma.Data.Abstractions;

/// <summary>
/// Decrypts the per-tenant connection-string envelope persisted on
/// <c>tenants.EncryptedConnectionString</c> (Doc 01 §8.1).
///
/// <para>The interface exists so the <see cref="ITenantConnectionResolver"/>
/// implementation in <c>Tamma.Data</c> can stay independent of the AES-GCM
/// helper that currently lives in <c>Tamma.Api/Services/Provisioning/TenantSecretProtector.cs</c>.
/// Story 28-4 ships a passthrough default
/// (<see cref="Pooling.PassthroughConnectionStringDecryptor"/>) so the
/// resolver works in dev/local environments where the envelope column
/// holds the raw connection string. Story 28-12 wires the real AES-GCM
/// path by registering an adapter over <c>TenantSecretProtector</c> in
/// the API composition root.</para>
/// </summary>
public interface IConnectionStringDecryptor
{
    /// <summary>
    /// Decrypts an envelope. Implementations must throw
    /// <see cref="TenantConnectionDecryptionException"/> on tag mismatch
    /// or key-version mismatch — never return a partially-decoded string.
    /// </summary>
    /// <param name="envelope">Cipher payload exactly as stored in
    /// <c>tenants.EncryptedConnectionString</c>. Implementations MUST
    /// treat as opaque bytes (size, layout, version are
    /// implementation-defined per Doc 01 §8.1).</param>
    /// <param name="kekVersion">Optional KEK slot indicator from
    /// <c>tenants.KekVersion</c>. <c>null</c> means "use whatever default
    /// the implementation prefers" — the passthrough variant ignores it.</param>
    string Decrypt(byte[] envelope, int? kekVersion);
}
