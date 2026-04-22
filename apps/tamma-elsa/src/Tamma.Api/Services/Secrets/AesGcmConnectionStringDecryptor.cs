using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Tamma.Api.Services.Provisioning;
using Tamma.Data.Abstractions;

namespace Tamma.Api.Services.Secrets;

/// <summary>
/// Story 28-12 — production <see cref="IConnectionStringDecryptor"/>
/// adapter that wraps <see cref="TenantSecretProtector"/> for the
/// <see cref="Tamma.Data.Pooling.LruPooledTenantConnectionResolver"/>.
///
/// <para>The 28-4 resolver's seam is implementation-defined on both
/// the envelope layout and the slot indicator (<c>kekVersion</c>). This
/// adapter chooses the simplest interpretation that lines up with the
/// existing protector format
/// (<c>nonce ‖ ciphertext ‖ tag</c>):</para>
///
/// <list type="bullet">
///   <item><description><b>Steady state</b> — every envelope was
///     encrypted under the primary KEK exposed by
///     <see cref="KekProvider.GetPrimary"/>. Decrypt happens with the
///     primary; no fallback is needed.</description></item>
///   <item><description><b>Rotation window</b> — when the operator
///     stages a secondary KEK (Doc 01 §8.2 step 2), envelopes written
///     before the rotation kicked off still need the previous KEK to
///     decrypt. The adapter tries the primary first; on
///     <see cref="CryptographicException"/> (auth-tag failure) it
///     retries with the secondary. The adapter does NOT know which
///     KEK encrypted any given row at rest — it relies on the GCM tag
///     to fail-closed if both options are wrong.</description></item>
///   <item><description><b>Re-encrypt path</b> — the rotation worker
///     (<see cref="KekRotationCoordinator"/>) holds both KEKs at the
///     same time; it explicitly calls
///     <see cref="DecryptWithKey"/> + <see cref="EncryptWithKey"/> so
///     the fallback heuristic doesn't apply during the actual rotation
///     loop.</description></item>
/// </list>
///
/// <para>The <c>kekVersion</c> argument from the resolver is recorded
/// in structured logs but does NOT gate decryption — it is purely an
/// informational hint. This keeps the adapter forward-compatible with
/// future envelope versions (Story 28-13 OpenBao) without breaking the
/// 28-4 interface.</para>
///
/// <para>The adapter NEVER logs the envelope contents or the recovered
/// plaintext. On failure the wrapping
/// <see cref="TenantConnectionDecryptionException"/> carries the tenant
/// id and nothing else.</para>
/// </summary>
public sealed class AesGcmConnectionStringDecryptor : IConnectionStringDecryptor
{
    private readonly KekProvider _kekProvider;
    private readonly ILogger<AesGcmConnectionStringDecryptor> _logger;

    public AesGcmConnectionStringDecryptor(
        KekProvider kekProvider,
        ILogger<AesGcmConnectionStringDecryptor> logger)
    {
        ArgumentNullException.ThrowIfNull(kekProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _kekProvider = kekProvider;
        _logger = logger;
    }

    public string Decrypt(byte[] envelope, int? kekVersion)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Length == 0)
        {
            throw new ArgumentException(
                "Envelope is empty — AES-GCM decryptor requires nonce + ciphertext + tag.",
                nameof(envelope));
        }

        var primary = _kekProvider.GetPrimary();
        if (primary is null)
        {
            // No primary configured — surface a terse error. Production
            // deployments must set Cranl:EncryptionKey; the resolver
            // wraps this in TenantConnectionDecryptionException so the
            // envelope contents never leak.
            throw new InvalidOperationException(
                "No primary KEK is configured. Set "
                + KekProvider.PrimaryConfigKey
                + " to a 32-byte base64 value before serving tenant traffic.");
        }

        byte[]? secondary = null;
        try
        {
            try
            {
                return DecryptWithKey(envelope, primary);
            }
            catch (CryptographicException primaryFailure)
            {
                secondary = _kekProvider.GetSecondary();
                if (secondary is null)
                {
                    _logger.LogWarning(
                        "tenant.kek.decrypt_failed kekVersionHint={KekVersion} "
                        + "fallback=none reason=primary_only",
                        kekVersion);
                    throw;
                }

                try
                {
                    var plaintext = DecryptWithKey(envelope, secondary);
                    _logger.LogInformation(
                        "tenant.kek.decrypt_fallback kekVersionHint={KekVersion} "
                        + "slot=secondary — envelope predates the current rotation",
                        kekVersion);
                    return plaintext;
                }
                catch (CryptographicException secondaryFailure)
                {
                    _logger.LogWarning(
                        "tenant.kek.decrypt_failed kekVersionHint={KekVersion} "
                        + "fallback=secondary reason=auth_tag_mismatch primaryError={PrimaryError} "
                        + "secondaryError={SecondaryError}",
                        kekVersion,
                        primaryFailure.GetType().Name,
                        secondaryFailure.GetType().Name);
                    throw new CryptographicException(
                        "Both primary and secondary KEKs failed to decrypt the envelope.",
                        secondaryFailure);
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(primary);
            if (secondary is not null) CryptographicOperations.ZeroMemory(secondary);
        }
    }

    /// <summary>
    /// Decrypt a payload with a specific KEK — used by the rotation
    /// worker which holds both KEKs and knows exactly which one to try.
    /// Public so the worker can avoid the fallback heuristic.
    /// </summary>
    public static string DecryptWithKey(byte[] envelope, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(key);

        var protector = new TenantSecretProtector(key);
        return protector.Decrypt(envelope);
    }

    /// <summary>
    /// Encrypt a plaintext under a specific KEK — used by the rotation
    /// worker after a successful decrypt to write the new envelope back
    /// into <c>tenants.EncryptedConnectionString</c>.
    /// </summary>
    public static byte[] EncryptWithKey(string plaintext, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(key);

        var protector = new TenantSecretProtector(key);
        return protector.Encrypt(plaintext);
    }
}
