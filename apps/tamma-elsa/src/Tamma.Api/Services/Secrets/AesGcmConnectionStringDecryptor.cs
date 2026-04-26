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
/// <para>R2-H13 fix: the adapter now consumes <c>kekVersion</c> when
/// the caller supplies it. The decryptor first asks the
/// <see cref="KekProvider"/> for the slot at that exact version (which
/// looks up active/secondary/retired in one step) and tries ONLY that
/// key. The two-key heuristic fallback is reserved for legacy rows
/// where <c>kekVersion</c> is null — those carry no version hint, so
/// trying primary then secondary is the only safe choice. After more
/// than one rotation, however, retired keys must be reachable by
/// version: <see cref="KekProvider.GetByVersion"/> returns the matching
/// retired slot when one is in the cabinet ring.</para>
///
/// <list type="bullet">
///   <item><description><b>Steady state</b> — every envelope was
///     encrypted under the active primary KEK. <c>kekVersion</c> on
///     the row matches <see cref="KekProvider.GetActiveVersion"/>.
///     <see cref="KekProvider.GetByVersion"/> returns the primary slot
///     and the decrypt succeeds in one shot.</description></item>
///   <item><description><b>Rotation window</b> — a row may carry the
///     previous primary's <c>KekVersion</c>. The cabinet still holds
///     the previous primary in the secondary slot (rotation step 2 in
///     the runbook). <see cref="KekProvider.GetByVersion"/> returns
///     the secondary slot and the decrypt succeeds.</description></item>
///   <item><description><b>Post-rotation, pre-cleanup</b> — a row was
///     added or unreachable during the previous rotation and is now
///     two versions behind. The cabinet keeps a small ring of retired
///     keys for exactly this case — see
///     <see cref="KekProvider.RetainedHistorySize"/>.</description></item>
///   <item><description><b>Legacy row (kekVersion=null)</b> — try
///     primary first; on auth-tag failure try secondary. This is the
///     pre-H13 heuristic; it stays for migration safety but logs a
///     warning so operators can see how many rows still lack a
///     version stamp.</description></item>
/// </list>
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

        // R2-H13: when the caller supplies a kekVersion, look it up
        // directly. This avoids the post-multi-rotation hole where a row
        // is two-versions-back and primary+secondary are both wrong.
        if (kekVersion is not null && kekVersion.Value > 0)
        {
            var slot = _kekProvider.GetByVersion(kekVersion.Value);
            if (slot is null)
            {
                _logger.LogWarning(
                    "tenant.kek.decrypt_failed kekVersion={KekVersion} reason=unknown_version "
                    + "activeVersion={ActiveVersion}",
                    kekVersion, _kekProvider.GetActiveVersion());
                throw new CryptographicException(
                    $"KEK version {kekVersion} is not present in the cabinet. "
                    + "The retired-keys ring may have been pruned past this version — "
                    + "operator must restore the historical key or re-encrypt the row.");
            }

            var keyCopy = slot.Material;
            try
            {
                return DecryptWithKey(envelope, keyCopy);
            }
            catch (CryptographicException primaryFailure)
            {
                _logger.LogWarning(
                    "tenant.kek.decrypt_failed kekVersion={KekVersion} slotKind={Kind} "
                    + "reason=auth_tag_mismatch errorType={ErrorType}",
                    kekVersion, slot.Kind, primaryFailure.GetType().Name);
                throw;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(keyCopy);
            }
        }

        // Legacy / passthrough path: kekVersion is null. We don't know
        // which slot encrypted the envelope, so fall back to primary
        // then secondary. This is the pre-H13 heuristic.
        return DecryptLegacyHeuristic(envelope);
    }

    private string DecryptLegacyHeuristic(byte[] envelope)
    {
        var primary = _kekProvider.GetPrimary();
        if (primary is null)
        {
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
                        "tenant.kek.decrypt_failed kekVersionHint=null "
                        + "fallback=none reason=primary_only");
                    throw;
                }

                try
                {
                    var plaintext = DecryptWithKey(envelope, secondary);
                    _logger.LogInformation(
                        "tenant.kek.decrypt_fallback kekVersionHint=null "
                        + "slot=secondary — envelope predates the current rotation");
                    return plaintext;
                }
                catch (CryptographicException secondaryFailure)
                {
                    _logger.LogWarning(
                        "tenant.kek.decrypt_failed kekVersionHint=null "
                        + "fallback=secondary reason=auth_tag_mismatch primaryError={PrimaryError} "
                        + "secondaryError={SecondaryError}",
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
