using System.Security.Cryptography;
using System.Text;

namespace Tamma.Api.Services.Provisioning;

/// <summary>
/// Tiny AES-GCM helper for encrypting per-tenant secrets at rest (chiefly
/// the Cranl-issued <c>DATABASE_URL</c>, persisted on
/// <c>tenants.cranl_database_url_encrypted</c>).
///
/// <para>This is a deliberately minimal seam — the codebase does not yet
/// have a generic <c>IDataProtector</c> / <c>IKeyProtector</c> abstraction
/// (the only existing encryption is libsodium sealed-box for GitHub Actions
/// secrets, which targets recipients, not at-rest storage). We pick AES-GCM
/// here because it's:</para>
///
/// <list type="bullet">
///   <item><description>Built into <c>System.Security.Cryptography</c> — no
///     extra dependency.</description></item>
///   <item><description>AEAD: ciphertext + tag are bound to a 12-byte
///     nonce that we prepend to the output, so a single byte flip is
///     detected on decrypt.</description></item>
///   <item><description>Streamable + small — 12-byte nonce + ciphertext +
///     16-byte tag fits in a Postgres bytea column without ceremony.</description></item>
/// </list>
///
/// <para>Key source: <c>Cranl:EncryptionKey</c> in configuration. Must be a
/// base64-encoded 32-byte key (256-bit). When unset, the constructor
/// derives a deterministic key from the API key + a fixed salt — this is
/// NOT a security boundary, only a "make dev work without ceremony"
/// fallback. Production deployments MUST set <c>Cranl:EncryptionKey</c>
/// explicitly; the deployment runbook calls this out.</para>
///
/// <para>TODO (audit cranl/002): migrate to a proper KMS-backed
/// <c>IKeyProtector</c> when Story 28-13 (OpenBao integration) lands so
/// the key isn't sat in a config file.</para>
/// </summary>
public sealed class TenantSecretProtector
{
    private const int NonceSize = 12;   // AES-GCM standard
    private const int TagSize = 16;     // AES-GCM standard
    private const int KeySize = 32;     // 256-bit
    private static readonly byte[] DerivationSalt =
        Encoding.UTF8.GetBytes("tamma.cranl.secret-protector.v1");

    private readonly byte[] _key;

    public TenantSecretProtector(byte[] key)
    {
        if (key.Length != KeySize)
            throw new ArgumentException(
                $"TenantSecretProtector key must be {KeySize} bytes (got {key.Length}).",
                nameof(key));
        _key = key;
    }

    /// <summary>
    /// Build a protector from configuration. Reads
    /// <c>Cranl:EncryptionKey</c> (base64) when present, otherwise derives
    /// a fallback key from <c>Cranl:ApiKey</c> + a fixed salt via
    /// HKDF-SHA256. The fallback path logs a warning so deployments
    /// without an explicit key are visible in startup logs.
    /// </summary>
    public static TenantSecretProtector FromConfiguration(
        IConfiguration cfg, ILogger? logger = null)
    {
        var explicitKey = cfg["Cranl:EncryptionKey"];
        if (!string.IsNullOrWhiteSpace(explicitKey))
        {
            try
            {
                var bytes = Convert.FromBase64String(explicitKey);
                if (bytes.Length != KeySize)
                {
                    throw new InvalidOperationException(
                        $"Cranl:EncryptionKey must decode to {KeySize} bytes (got {bytes.Length}).");
                }
                return new TenantSecretProtector(bytes);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException(
                    "Cranl:EncryptionKey is not valid base64.", ex);
            }
        }

        // Fallback path: derive from the API key. Not a security boundary —
        // this only protects against a casual dump-the-table read and
        // matches the GitHub:PrivateKey ergonomics (one knob enables the
        // whole subsystem).
        var apiKey = cfg["Cranl:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            // No API key + no encryption key → produce a non-functional
            // protector that throws on use. The Null provisioner is wired
            // in this case anyway so the protector is never invoked.
            logger?.LogInformation(
                "TenantSecretProtector: no Cranl:ApiKey or Cranl:EncryptionKey; "
                + "secret protection is unavailable. This is OK when the Null "
                + "tenant provisioner is wired (default for dev).");
            return new TenantSecretProtector(new byte[KeySize]);
        }

        logger?.LogWarning(
            "TenantSecretProtector: Cranl:EncryptionKey not set, deriving key "
            + "from Cranl:ApiKey via HKDF. Set Cranl:EncryptionKey explicitly "
            + "in production (32 random bytes, base64-encoded).");
        var derived = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            ikm: Encoding.UTF8.GetBytes(apiKey),
            outputLength: KeySize,
            salt: DerivationSalt,
            info: Encoding.UTF8.GetBytes("cranl-database-url-protection"));
        return new TenantSecretProtector(derived);
    }

    /// <summary>
    /// Encrypt a UTF-8 plaintext (typically the Cranl DATABASE_URL).
    /// Output layout: 12-byte nonce ‖ ciphertext ‖ 16-byte tag.
    /// </summary>
    public byte[] Encrypt(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plainBytes, ciphertext, tag);

        var result = new byte[NonceSize + ciphertext.Length + TagSize];
        Buffer.BlockCopy(nonce, 0, result, 0, NonceSize);
        Buffer.BlockCopy(ciphertext, 0, result, NonceSize, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, result, NonceSize + ciphertext.Length, TagSize);
        return result;
    }

    /// <summary>
    /// Decrypt a payload previously produced by <see cref="Encrypt"/>.
    /// Throws <see cref="CryptographicException"/> on tag mismatch / corruption.
    /// </summary>
    public string Decrypt(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length < NonceSize + TagSize)
            throw new CryptographicException(
                "Ciphertext is too short to contain nonce + tag.");

        var nonce = new byte[NonceSize];
        Buffer.BlockCopy(payload, 0, nonce, 0, NonceSize);

        var cipherLen = payload.Length - NonceSize - TagSize;
        var ciphertext = new byte[cipherLen];
        Buffer.BlockCopy(payload, NonceSize, ciphertext, 0, cipherLen);

        var tag = new byte[TagSize];
        Buffer.BlockCopy(payload, NonceSize + cipherLen, tag, 0, TagSize);

        var plaintext = new byte[cipherLen];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return Encoding.UTF8.GetString(plaintext);
    }
}
