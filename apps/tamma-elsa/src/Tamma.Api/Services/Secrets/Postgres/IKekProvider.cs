namespace Tamma.Api.Services.Secrets.Postgres;

/// <summary>
/// Source of Key-Encryption-Keys (KEKs) for the Story 29-2
/// Postgres-backed secret store. Decoupled from the Epic 28
/// connection-string KEK
/// (<see cref="Tamma.Data.Abstractions.IConnectionStringDecryptor"/>)
/// so the secret-store KEK can rotate on its own cadence — a
/// connection-string envelope rewrap is cheap (one row per tenant);
/// a secret-store rewrap is expensive (every secret version) and
/// runs as a separate operational pass.
///
/// <para>The store identifies KEKs by a 1-byte slot id (0..255). The
/// envelope carries that byte at offset 1 so a future
/// <c>RewrapAllAsync</c> pass can <c>WHERE KekId = old</c> without
/// trial-decrypting every row. Slot exhaustion at one rotation per
/// month would take 21 years; rotation runbook calls for slot reuse
/// after a cooling period rather than a hard 256-cap.</para>
///
/// <para>Implementations:
/// <list type="bullet">
///   <item><description><see cref="EnvKekProvider"/> — reads
///     base64-encoded 32-byte KEKs from
///     <c>TAMMA_SECRET_STORE_KEK_PRIMARY</c> /
///     <c>TAMMA_SECRET_STORE_KEK_SECONDARY</c> env vars. Default for
///     the env-KEK decision (memory:
///     <c>project_epic28_kek_decision.md</c>).</description></item>
///   <item><description>Future: KMS-backed provider when Story 28-13
///     (OpenBao) lands.</description></item>
/// </list></para>
/// </summary>
public interface IKekProvider
{
    /// <summary>
    /// Slot id of the KEK that new envelopes should be wrapped under.
    /// All <see cref="ISecretStoreBackend.PutVersionAsync"/> calls use
    /// this slot.
    /// </summary>
    byte PrimaryKekId { get; }

    /// <summary>
    /// Look up a KEK by slot id. Returns the 32-byte AES-256 key
    /// material. Throws <see cref="KekNotAvailableException"/> when
    /// the slot is not loaded — typically when an envelope wrapped
    /// under an old KEK is being decrypted on a node that no longer
    /// carries the old key in its env.
    /// </summary>
    byte[] GetKek(byte kekId);

    /// <summary>
    /// True iff the slot is currently loaded. Lets the backend
    /// short-circuit a "rewrap candidate?" check without catching
    /// <see cref="KekNotAvailableException"/>.
    /// </summary>
    bool TryGetKek(byte kekId, out byte[]? key);
}

/// <summary>
/// Thrown when an envelope references a KEK slot that the running
/// process does not have loaded. Signals to the operator that the
/// node is missing a key (deployment / config mistake) rather than
/// that the data is corrupt.
/// </summary>
public sealed class KekNotAvailableException : Exception
{
    public byte KekId { get; }

    public KekNotAvailableException(byte kekId)
        : base($"KEK slot {kekId} is not loaded on this process. " +
               "Check TAMMA_SECRET_STORE_KEK_PRIMARY / SECONDARY env vars.")
    {
        KekId = kekId;
    }
}
