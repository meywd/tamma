using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Generators;

namespace Tamma.Api.Auth;

/// <summary>
/// Centralized API-key generation and hash verification.
///
/// <para>Wire format: <c>tamma_sk_&lt;base64url-of-32-random-bytes&gt;</c> for
/// every scope (service, user, installation). Matches the deleted TS
/// <c>packages/api/src/auth/api-key.ts</c> for compatibility.</para>
///
/// <para>Hash storage: SHA-256 hex of the raw key, with a scrypt fallback so
/// rows written by the legacy TS API (<c>scrypt(key, "tamma-api-key-hash-v1",
/// {N=16384, r=8, p=1}, 32)</c> hex) still verify after cutover. Newly written
/// rows are SHA-256 only; <see cref="NeedsRehash"/> flags legacy rows so the
/// auth handler can transparently rewrite on next use.</para>
/// </summary>
public static class ApiKeyHasher
{
    public const string KeyPrefix = "tamma_sk_";
    public const int KeyBytes = 32;
    public const int DisplayPrefixLength = 12;
    private const string ScryptSalt = "tamma-api-key-hash-v1";
    private const int ScryptN = 16384;
    private const int ScryptR = 8;
    private const int ScryptP = 1;
    private const int ScryptKeyLength = 32;

    /// <summary>Generates a fresh API key (raw, not yet hashed).</summary>
    public static string NewKey()
    {
        var random = RandomNumberGenerator.GetBytes(KeyBytes);
        return KeyPrefix + Base64Url.Encode(random);
    }

    /// <summary>Computes the canonical SHA-256 hash for a raw key (lowercase hex).</summary>
    public static string Hash(string rawKey)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey))).ToLowerInvariant();

    /// <summary>
    /// Lookup-prefix for displaying a key in lists / logs. First 12 chars
    /// (e.g. <c>tamma_sk_abc</c>) — enough to disambiguate without exposing
    /// material entropy.
    /// </summary>
    public static string Prefix(string rawKey)
        => rawKey.Length >= DisplayPrefixLength ? rawKey[..DisplayPrefixLength] : rawKey;

    /// <summary>
    /// Returns the legacy scrypt-hex representation for a raw key. Used only
    /// by the auth handler's fallback lookup — normal writes go through
    /// <see cref="Hash"/>.
    /// </summary>
    public static string LegacyScryptHash(string rawKey)
    {
        var derived = SCrypt.Generate(
            Encoding.UTF8.GetBytes(rawKey),
            Encoding.UTF8.GetBytes(ScryptSalt),
            ScryptN, ScryptR, ScryptP,
            ScryptKeyLength);
        return Convert.ToHexString(derived).ToLowerInvariant();
    }

    /// <summary>
    /// Returns true when the stored hash format is legacy (scrypt-derived) and
    /// should be rewritten on next successful verification. Both modern and
    /// legacy hashes are 64-char lowercase hex, so we cannot disambiguate by
    /// shape alone — instead we treat any successful scrypt-fallback lookup
    /// as needing a rehash (by passing the key to <see cref="Hash"/>).
    /// </summary>
    public static bool NeedsRehash(string storedHash, string sha256Hash)
        => !string.Equals(storedHash, sha256Hash, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Base64url helpers (RFC 4648 §5) — no padding, URL-safe alphabet.</summary>
internal static class Base64Url
{
    public static string Encode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }
}
