using System.Security.Cryptography;
using System.Text;

namespace Tamma.Api.Services.Secrets.Postgres;

/// <summary>
/// AES-256-GCM envelope encryption helper for the Story 29-2
/// Postgres-backed secret store. Each call to
/// <see cref="Encrypt"/> mints a fresh 32-byte Data Encryption Key
/// (DEK), encrypts the plaintext under it, then wraps the DEK under
/// the supplied KEK. <see cref="Decrypt"/> reverses the process.
///
/// <para><b>Wire format</b> (versioned for forward compat):
/// <code>
/// offset  bytes  field
/// ────── ────── ─────────────────────────────────────────────
/// 0      1      format_version (currently <see cref="CurrentFormatVersion"/>)
/// 1      1      kek_id           (which KEK slot wrapped the DEK)
/// 2      12     wrap_nonce       (AES-GCM nonce for the DEK wrap)
/// 14     32     wrapped_dek      (AES-256-GCM ciphertext of DEK)
/// 46     16     wrap_tag         (AES-GCM tag for the DEK wrap)
/// 62     12     value_nonce      (AES-GCM nonce for the value)
/// 74     N      value_ct         (AES-256-GCM ciphertext of plaintext)
/// 74+N   16     value_tag        (AES-GCM tag for the value)
/// ────── ────── ─────────────────────────────────────────────
/// total: 74 + N + 16 = 90 + plaintext_len
/// </code>
/// where <c>N</c> is the UTF-8 byte length of the plaintext.</para>
///
/// <para><b>Why a fresh DEK per row?</b> Bounds the blast radius of
/// a single-row compromise to that row's plaintext (the attacker
/// would also need the KEK to unwrap the DEK). A KEK rotation only
/// rewraps the DEKs — never the plaintext value — so the rotation
/// pass is O(rows) AES-GCM ops rather than O(plaintext bytes).</para>
///
/// <para>Both AES-GCM tags are constant-time-checked by
/// <see cref="AesGcm"/>; tampering with any field flips a tag
/// mismatch into a thrown <see cref="CryptographicException"/>.</para>
/// </summary>
public static class SecretEnvelope
{
    /// <summary>Current envelope format version. Bumped on
    /// schema-incompatible changes; old envelopes remain decryptable
    /// because the version byte tells the decoder which layout to
    /// use.</summary>
    public const byte CurrentFormatVersion = 1;

    /// <summary>AES-GCM nonce length (NIST SP 800-38D §5.2.1.1
    /// recommendation).</summary>
    public const int NonceSize = 12;

    /// <summary>AES-GCM tag length (max).</summary>
    public const int TagSize = 16;

    /// <summary>DEK / KEK length (AES-256).</summary>
    public const int KeySize = 32;

    private const int WrapNonceOffset = 2;
    private const int WrappedDekOffset = WrapNonceOffset + NonceSize;
    private const int WrapTagOffset = WrappedDekOffset + KeySize;
    private const int ValueNonceOffset = WrapTagOffset + TagSize;
    private const int ValueCtOffset = ValueNonceOffset + NonceSize;
    private const int HeaderSize = ValueCtOffset; // 74 bytes

    /// <summary>
    /// Encrypt <paramref name="plaintext"/> under a fresh DEK, then
    /// wrap the DEK under the supplied KEK. Returns the assembled
    /// envelope per the wire-format documented on the class.
    /// </summary>
    public static byte[] Encrypt(string plaintext, byte kekId, byte[] kek)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(kek);
        if (kek.Length != KeySize)
            throw new ArgumentException(
                $"KEK must be {KeySize} bytes; got {kek.Length}.",
                nameof(kek));

        var valueBytes = Encoding.UTF8.GetBytes(plaintext);

        // Mint a fresh DEK + nonces.
        var dek = RandomNumberGenerator.GetBytes(KeySize);
        var wrapNonce = RandomNumberGenerator.GetBytes(NonceSize);
        var valueNonce = RandomNumberGenerator.GetBytes(NonceSize);

        try
        {
            var envelope = new byte[HeaderSize + valueBytes.Length + TagSize];
            envelope[0] = CurrentFormatVersion;
            envelope[1] = kekId;
            Buffer.BlockCopy(wrapNonce, 0, envelope, WrapNonceOffset, NonceSize);
            Buffer.BlockCopy(valueNonce, 0, envelope, ValueNonceOffset, NonceSize);

            // Wrap the DEK under the KEK.
            using (var wrapAes = new AesGcm(kek, TagSize))
            {
                wrapAes.Encrypt(
                    nonce: wrapNonce,
                    plaintext: dek,
                    ciphertext: envelope.AsSpan(WrappedDekOffset, KeySize),
                    tag: envelope.AsSpan(WrapTagOffset, TagSize));
            }

            // Encrypt the value under the DEK.
            using (var valueAes = new AesGcm(dek, TagSize))
            {
                valueAes.Encrypt(
                    nonce: valueNonce,
                    plaintext: valueBytes,
                    ciphertext: envelope.AsSpan(ValueCtOffset, valueBytes.Length),
                    tag: envelope.AsSpan(ValueCtOffset + valueBytes.Length, TagSize));
            }

            return envelope;
        }
        finally
        {
            // Best-effort scrub of the in-process DEK + plaintext.
            // Doesn't help against a memory dump (the GC may have
            // copied them) but eliminates the most obvious sniffable
            // residue in the heap.
            CryptographicOperations.ZeroMemory(dek);
            CryptographicOperations.ZeroMemory(valueBytes);
        }
    }

    /// <summary>
    /// Reverse of <see cref="Encrypt"/>. Reads the
    /// <see cref="CurrentFormatVersion"/> byte, looks up the KEK for
    /// the slot id at offset 1 via the supplied
    /// <paramref name="kekProvider"/>, unwraps the DEK, then
    /// decrypts the value.
    /// </summary>
    /// <exception cref="SecretEnvelopeFormatException">
    /// The envelope is shorter than the minimum, or carries an
    /// unknown <see cref="CurrentFormatVersion"/> byte.
    /// </exception>
    /// <exception cref="KekNotAvailableException">
    /// The envelope references a KEK slot that the running process
    /// does not have loaded.
    /// </exception>
    /// <exception cref="CryptographicException">
    /// AES-GCM tag mismatch — the envelope was tampered with or the
    /// wrong KEK is loaded for the slot id.
    /// </exception>
    public static string Decrypt(byte[] envelope, IKekProvider kekProvider)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(kekProvider);

        if (envelope.Length < HeaderSize + TagSize)
        {
            throw new SecretEnvelopeFormatException(
                $"Envelope length {envelope.Length} is shorter than the " +
                $"minimum header + tag size ({HeaderSize + TagSize}).");
        }

        var formatVersion = envelope[0];
        if (formatVersion != CurrentFormatVersion)
        {
            throw new SecretEnvelopeFormatException(
                $"Unsupported envelope format version {formatVersion}; " +
                $"this build understands {CurrentFormatVersion}.");
        }

        var kekId = envelope[1];
        var kek = kekProvider.GetKek(kekId);

        var dek = new byte[KeySize];
        try
        {
            using (var wrapAes = new AesGcm(kek, TagSize))
            {
                wrapAes.Decrypt(
                    nonce: envelope.AsSpan(WrapNonceOffset, NonceSize),
                    ciphertext: envelope.AsSpan(WrappedDekOffset, KeySize),
                    tag: envelope.AsSpan(WrapTagOffset, TagSize),
                    plaintext: dek);
            }

            var valueCtLength = envelope.Length - ValueCtOffset - TagSize;
            var valueBytes = new byte[valueCtLength];
            try
            {
                using (var valueAes = new AesGcm(dek, TagSize))
                {
                    valueAes.Decrypt(
                        nonce: envelope.AsSpan(ValueNonceOffset, NonceSize),
                        ciphertext: envelope.AsSpan(ValueCtOffset, valueCtLength),
                        tag: envelope.AsSpan(ValueCtOffset + valueCtLength, TagSize),
                        plaintext: valueBytes);
                }
                return Encoding.UTF8.GetString(valueBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(valueBytes);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    /// <summary>
    /// Read the format version byte without decrypting. Used by the
    /// rewrap pass to skip envelopes that already use the latest
    /// format on the new KEK.
    /// </summary>
    public static byte ReadFormatVersion(byte[] envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Length < 1)
            throw new SecretEnvelopeFormatException(
                "Envelope is empty; no format byte to read.");
        return envelope[0];
    }

    /// <summary>
    /// Read the KEK slot id without decrypting. Used by the rewrap
    /// pass to filter rows by old KEK without paying the AES-GCM
    /// tax.
    /// </summary>
    public static byte ReadKekId(byte[] envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Length < 2)
            throw new SecretEnvelopeFormatException(
                "Envelope is shorter than 2 bytes; no kek_id byte to read.");
        return envelope[1];
    }
}

/// <summary>
/// Thrown when an envelope's wire format is malformed (truncated,
/// or carries an unsupported <see cref="SecretEnvelope.CurrentFormatVersion"/>
/// byte). Distinct from a tag mismatch (which surfaces as a
/// <see cref="System.Security.Cryptography.CryptographicException"/>) so
/// the operator can tell "schema drift" from "tamper / wrong key".
/// </summary>
public sealed class SecretEnvelopeFormatException : Exception
{
    public SecretEnvelopeFormatException(string message) : base(message)
    {
    }
}
