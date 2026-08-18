using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Org.BouncyCastle.Crypto.Generators;

namespace Tamma.Api.Auth;

/// <summary>
/// Centralized API-key generation and hash verification.
///
/// <para>Wire format: <c>tamma_sk_&lt;base64url-of-32-random-bytes&gt;</c> for
/// every scope (service, user, installation). Matches the deleted TS
/// <c>packages/api/src/auth/api-key.ts</c> for compatibility.</para>
///
/// <para>Hash storage — Story 28-7 deferred-items rollout:
/// <list type="bullet">
///   <item>New writes use Argon2id with the format
///         <c>argon2id$v=19$m=&lt;MemKiB&gt;,t=&lt;t&gt;,p=&lt;p&gt;$&lt;saltB64&gt;$&lt;hashB64&gt;</c>
///         (no leading <c>$</c>, to stay a plain DB column). Same KDF + params
///         as <see cref="PasswordService"/>, but the derived hash is stored
///         directly — prefix routing already provides O(1) lookup, so per-key
///         salts are fine for verification.</item>
///   <item>Legacy rows in the old formats are still verified:
///     <list type="bullet">
///       <item>SHA-256 hex (post-merge TS/C# bridge) — 64-char lowercase hex.</item>
///       <item>scrypt hex (pre-Epic-28 TS) — 64-char lowercase hex; distinguished
///             by a secondary scrypt-hash check in <see cref="Verify"/>.</item>
///     </list>
///   </item>
///   <item><see cref="NeedsRehash"/> returns <c>true</c> for any non-argon2 stored
///         hash so the auth handler can transparently rewrite the row on next
///         successful verify.</item>
/// </list></para>
/// </summary>
public static class ApiKeyHasher
{
    public const string KeyPrefix = "tamma_sk_";
    public const int KeyBytes = 32;
    public const int DisplayPrefixLength = 12;

    /// <summary>
    /// Marker prefix for Argon2id rows. Distinct from <see cref="PasswordService"/>
    /// which uses <c>$argon2id$</c> (leading <c>$</c>) — keeps the two code paths
    /// from accidentally sharing a hash even though the KDF params match.
    /// </summary>
    public const string Argon2Prefix = "argon2id$";

    // Argon2id parameters. Match PasswordService so ops has one knob to tune.
    // OWASP ASVS v4 recommends m >= 19456 KiB, t >= 2, p >= 1. These exceed it.
    private const int Argon2MemorySize = 65536; // 64 MiB
    private const int Argon2Iterations = 3;
    private const int Argon2Parallelism = 4;
    private const int Argon2SaltLength = 16;
    private const int Argon2HashLength = 32;

    private const string ScryptSalt = "tamma-api-key-hash-v1";
    private const int ScryptN = 16384;
    private const int ScryptR = 8;
    private const int ScryptP = 1;
    private const int ScryptKeyLength = 32;

    /// <summary>
    /// Generates a fresh un-prefixed (legacy-shape) API key, raw and not yet
    /// hashed.
    ///
    /// <para>2026-08-18 — the base64url alphabet includes <c>_</c>, so a random
    /// body can start <c>u_</c>, <c>t_</c> or <c>pl_</c>, and
    /// <see cref="ApiKeyPrefixParser"/> then reads that as a Story-28-7 SCOPE
    /// MARKER. The key routes to the prefixed lookup instead of the legacy one
    /// (and a <c>t_</c> collision decodes a garbage tenant segment), so it 401s
    /// for its whole life. Roughly 1 key in 4000 — far too often for a
    /// credential that cannot be made to work — so a colliding body is
    /// re-rolled. Entropy is unaffected: rejection sampling over a CSPRNG.</para>
    /// </summary>
    public static string NewKey()
    {
        while (true)
        {
            var candidate = KeyPrefix + Base64Url.Encode(RandomNumberGenerator.GetBytes(KeyBytes));
            if (!ApiKeyPrefixParser.StartsWithScopeMarker(candidate)) return candidate;
        }
    }

    /// <summary>
    /// Legacy canonical SHA-256 hash for a raw key (lowercase hex). Preserved
    /// for backward compat with pre-Story-28-7 code paths that still compute
    /// the SHA-256 directly for lookups; new write paths should go through
    /// <see cref="HashArgon2"/>.
    /// </summary>
    public static string Hash(string rawKey)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey))).ToLowerInvariant();

    /// <summary>
    /// Argon2id hash for a raw key, encoded as
    /// <c>argon2id$v=19$m=&lt;MemKiB&gt;,t=&lt;t&gt;,p=&lt;p&gt;$&lt;saltB64&gt;$&lt;hashB64&gt;</c>.
    /// Use for all new <c>api_keys.KeyHash</c> writes.
    /// </summary>
    public static string HashArgon2(string rawKey)
    {
        var salt = RandomNumberGenerator.GetBytes(Argon2SaltLength);
        var hash = ComputeArgon2(rawKey, salt);
        return $"{Argon2Prefix}v=19$m={Argon2MemorySize},t={Argon2Iterations},p={Argon2Parallelism}$"
               + $"{Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>
    /// Constant-time verification of <paramref name="rawKey"/> against a stored
    /// hash of any supported format:
    /// <list type="number">
    ///   <item>Argon2id (<see cref="Argon2Prefix"/>)</item>
    ///   <item>SHA-256 hex (new TS/C# bridge)</item>
    ///   <item>scrypt hex (pre-Epic-28 TS)</item>
    /// </list>
    /// Returns <c>true</c> when any of the formats matches.
    /// </summary>
    public static bool Verify(string rawKey, string storedHash)
    {
        if (string.IsNullOrEmpty(storedHash)) return false;

        try
        {
            if (storedHash.StartsWith(Argon2Prefix, StringComparison.Ordinal))
                return VerifyArgon2(rawKey, storedHash);

            // Legacy hashes — both SHA-256 and scrypt are 64-char lowercase
            // hex, indistinguishable by shape. Compute SHA-256 first (it's
            // cheap) and fall back to scrypt. FixedTimeEquals on every path
            // so success vs. failure latency is constant per-format.
            var sha = Hash(rawKey);
            if (CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(sha),
                Encoding.ASCII.GetBytes(storedHash)))
            {
                return true;
            }

            var scrypt = LegacyScryptHash(rawKey);
            return CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(scrypt),
                Encoding.ASCII.GetBytes(storedHash));
        }
        catch
        {
            return false;
        }
    }

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
    /// <see cref="HashArgon2"/> (preferred) or <see cref="Hash"/> (legacy).
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
    /// Returns <c>true</c> when <paramref name="storedHash"/> is NOT an
    /// Argon2id row and therefore should be rewritten on next successful
    /// <see cref="Verify"/>. Covers both SHA-256 and scrypt legacy shapes.
    /// </summary>
    public static bool NeedsRehash(string storedHash)
    {
        if (string.IsNullOrEmpty(storedHash)) return false;
        return !storedHash.StartsWith(Argon2Prefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Legacy <see cref="NeedsRehash"/> overload used by pre-Story-28-7 call
    /// sites that passed the pre-computed SHA-256 for comparison. Argon2id
    /// rows never need a rehash; for legacy SHA-256/scrypt rows, any mismatch
    /// against the caller-supplied SHA-256 also counts as "needs rehash".
    /// </summary>
    public static bool NeedsRehash(string storedHash, string sha256Hash)
    {
        if (string.IsNullOrEmpty(storedHash)) return false;
        if (storedHash.StartsWith(Argon2Prefix, StringComparison.Ordinal))
            return false;
        return !string.Equals(storedHash, sha256Hash, StringComparison.OrdinalIgnoreCase);
    }

    // ── Argon2 internals ─────────────────────────────────────────────

    /// <summary>
    /// Parses the <c>argon2id$...</c> storage form and verifies in constant
    /// time. Returns <c>false</c> on any structural malformation.
    /// </summary>
    private static bool VerifyArgon2(string rawKey, string storedHash)
    {
        // Format: argon2id$v=19$m=<mem>,t=<t>,p=<p>$<saltB64>$<hashB64>
        //         ^0      ^1    ^2              ^3          ^4
        var parts = storedHash.Split('$');
        if (parts.Length != 5) return false;
        if (parts[0] != "argon2id") return false;

        var paramParts = parts[2].Split(',');
        if (paramParts.Length != 3) return false;

        if (!TryParsePrefixed(paramParts[0], "m=", out var mem)) return false;
        if (!TryParsePrefixed(paramParts[1], "t=", out var iters)) return false;
        if (!TryParsePrefixed(paramParts[2], "p=", out var parallel)) return false;

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[3]);
            expected = Convert.FromBase64String(parts[4]);
        }
        catch (FormatException)
        {
            return false;
        }

        var computed = ComputeArgon2(rawKey, salt, mem, iters, parallel, expected.Length);
        return CryptographicOperations.FixedTimeEquals(computed, expected);
    }

    private static bool TryParsePrefixed(string s, string prefix, out int value)
    {
        value = 0;
        if (!s.StartsWith(prefix, StringComparison.Ordinal)) return false;
        return int.TryParse(
            s[prefix.Length..],
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out value);
    }

    private static byte[] ComputeArgon2(string rawKey, byte[] salt)
        => ComputeArgon2(rawKey, salt, Argon2MemorySize, Argon2Iterations, Argon2Parallelism, Argon2HashLength);

    private static byte[] ComputeArgon2(
        string rawKey,
        byte[] salt,
        int memorySize,
        int iterations,
        int parallelism,
        int hashLength)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(rawKey))
        {
            Salt = salt,
            MemorySize = memorySize,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return argon2.GetBytes(hashLength);
    }
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
