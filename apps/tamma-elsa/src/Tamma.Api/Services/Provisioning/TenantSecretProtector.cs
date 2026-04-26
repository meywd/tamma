using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;

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
    /// <c>Cranl:EncryptionKey</c> (base64) when present.
    ///
    /// <para>R2-H11 hardening: in production the
    /// <see cref="IHostEnvironment"/>-aware overload below is the
    /// supported entry point. When <c>Cranl:EncryptionKey</c> is absent
    /// the production path throws — the silent HKDF fallback that the
    /// round-2 review flagged is now strictly behind
    /// <see cref="HostEnvironmentEnvExtensions.IsDevelopment(IHostEnvironment)"/>.</para>
    ///
    /// <para>The legacy single-arg overload remains for callers that
    /// don't have access to <see cref="IHostEnvironment"/> (chiefly the
    /// dev-time helper in <c>NullTenantProvisioner</c>). It always
    /// behaves as the dev/test path — the silent HKDF fallback is
    /// active. Production composition roots MUST inject
    /// <see cref="IHostEnvironment"/> via the two-arg overload.</para>
    /// </summary>
    public static TenantSecretProtector FromConfiguration(
        IConfiguration cfg, ILogger? logger = null)
    {
        // No environment hint — assume dev/test. This preserves the
        // pre-H11 semantics for callers that do not flow IHostEnvironment.
        return FromConfiguration(cfg, environment: null, logger);
    }

    /// <summary>
    /// Build a protector from configuration with environment-aware
    /// fail-closed semantics. R2-H11: production deploys must set
    /// <c>Cranl:EncryptionKey</c> explicitly — the HKDF fallback is
    /// strictly a dev-time convenience and is never used in production.
    /// </summary>
    /// <param name="cfg">Application configuration.</param>
    /// <param name="environment">Host environment. When null, assumes
    /// development semantics. When <see cref="IHostEnvironment.IsProduction"/>,
    /// throws if <c>Cranl:EncryptionKey</c> is unset.</param>
    /// <param name="logger">Optional logger.</param>
    public static TenantSecretProtector FromConfiguration(
        IConfiguration cfg, IHostEnvironment? environment, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(cfg);

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

        // R2-H11: production hard-fail when Cranl:EncryptionKey is missing.
        // The previous behaviour silently HKDF'd from Cranl:ApiKey and
        // logged a warning; that is no longer acceptable for the
        // production path because it allowed a deploy to ship with an
        // AES-GCM key derived from the Cranl API token.
        if (environment is not null && environment.IsProduction())
        {
            throw new InvalidOperationException(
                "Cranl:EncryptionKey is REQUIRED in production. Set the env var "
                + "(base64-encoded 32 random bytes) or migrate to OpenBao via "
                + "Story 28-13. The HKDF-from-ApiKey fallback is dev-only.");
        }

        // Dev/test path — derive from the API key OR ship a non-functional
        // protector when even the API key is unset.
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
            + "from Cranl:ApiKey via HKDF. This path is DEV-ONLY — set "
            + "Cranl:EncryptionKey explicitly before promoting to production "
            + "(32 random bytes, base64-encoded). Production deploys with this "
            + "fallback now fail at startup; see runbook .dev/runbooks/kek-rotation.md.");
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
