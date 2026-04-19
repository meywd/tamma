using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Org.BouncyCastle.Crypto.Generators;

namespace Tamma.Api.Auth;

public interface IPasswordService
{
    string HashPassword(string password);

    /// <summary>
    /// Verifies a presented password against a stored hash. Accepts two
    /// formats:
    /// <list type="bullet">
    ///   <item>Argon2id (current): <c>$argon2id$v=19$m=...,t=...,p=...$salt$hash</c></item>
    ///   <item>scrypt (legacy TS): <c>scrypt:N:r:p:keylen:saltHex:derivedHex</c></item>
    /// </list>
    /// The scrypt branch is a compatibility path for users whose
    /// <c>users.password_hash</c> was written by the deleted TypeScript API.
    /// Callers are expected to rehash to argon2id on successful scrypt-verify
    /// (see <see cref="NeedsRehash"/>).
    /// </summary>
    bool VerifyPassword(string password, string hash);

    /// <summary>
    /// Returns true when the stored hash uses a legacy format and should be
    /// rewritten with <see cref="HashPassword"/>. Used by the Login endpoint
    /// to migrate scrypt-format hashes to argon2id transparently.
    /// </summary>
    bool NeedsRehash(string hash);

    /// <summary>
    /// A constant-time dummy hash with the same parameters as live argon2id
    /// hashes. Login uses this when the user lookup misses, so an attacker
    /// cannot enumerate registered emails by timing the response. The dummy
    /// password is fixed at module-load time and the hash is precomputed.
    /// </summary>
    string DummyHash { get; }
}

public class PasswordService : IPasswordService
{
    // Argon2id parameters. Story 18-1 AC 2 specifies m=19456 t=2 p=1; the
    // C# port chose stronger params for headroom (m=64MiB t=3 p=4). Either
    // is OWASP-compliant; existing argon2id hashes use these stronger params
    // so they are kept here.
    private const int MemorySize = 65536; // 64 MB (KiB)
    private const int Iterations = 3;
    private const int Parallelism = 4;
    private const int SaltLength = 16;
    private const int HashLength = 32;

    // Legacy scrypt parameters from packages/api/src/auth/password.ts (TS):
    //   N=16384, r=8, p=1, keylen=32, salt=16
    // Stored format: "scrypt:N:r:p:keylen:saltHex:derivedHex"
    private const int ScryptN = 16384;
    private const int ScryptR = 8;
    private const int ScryptP = 1;
    private const int ScryptKeyLength = 32;

    private readonly Lazy<string> _dummyHash;

    public PasswordService()
    {
        // Precompute a real argon2id hash of a throwaway random password so
        // Login's null-user branch can still pay the same crypto cost as the
        // verified-user branch (constant-time anti-enumeration). The dummy
        // password value never matters — VerifyPassword(input, DummyHash)
        // always returns false unless the caller happens to know the random
        // bytes, which has 2^256 probability of guessing.
        _dummyHash = new Lazy<string>(() =>
        {
            var dummyPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            return HashPassword(dummyPassword);
        });
    }

    public string DummyHash => _dummyHash.Value;

    public string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var hash = ComputeArgon2Hash(password, salt);
        return $"$argon2id$v=19$m={MemorySize},t={Iterations},p={Parallelism}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool VerifyPassword(string password, string hash)
    {
        if (string.IsNullOrEmpty(hash))
            return false;

        try
        {
            // Branch by stored-hash format. Argon2id hashes start with '$';
            // scrypt hashes start with the literal "scrypt:". Anything else
            // is unverifiable and counts as a failure.
            if (hash.StartsWith("$argon2id$", StringComparison.Ordinal))
                return VerifyArgon2(password, hash);

            if (hash.StartsWith("scrypt:", StringComparison.Ordinal))
                return VerifyScrypt(password, hash);

            return false;
        }
        catch
        {
            return false;
        }
    }

    public bool NeedsRehash(string hash)
    {
        // Any non-argon2id stored hash should be rewritten on next login.
        return string.IsNullOrEmpty(hash)
            || !hash.StartsWith("$argon2id$", StringComparison.Ordinal);
    }

    private static bool VerifyArgon2(string password, string hash)
    {
        var parts = hash.Split('$');
        if (parts.Length != 6 || parts[1] != "argon2id") return false;

        var salt = Convert.FromBase64String(parts[4]);
        var expectedHash = Convert.FromBase64String(parts[5]);
        var computedHash = ComputeArgon2Hash(password, salt);

        return CryptographicOperations.FixedTimeEquals(computedHash, expectedHash);
    }

    private static bool VerifyScrypt(string password, string hash)
    {
        // Format: "scrypt:N:r:p:keylen:saltHex:derivedHex" — exactly 7 fields
        var parts = hash.Split(':');
        if (parts.Length != 7 || parts[0] != "scrypt")
            return false;

        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ||
            !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ||
            !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ||
            !int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var keylen))
        {
            return false;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromHexString(parts[5]);
            expected = Convert.FromHexString(parts[6]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (expected.Length != keylen)
            return false;

        var derived = SCrypt.Generate(
            Encoding.UTF8.GetBytes(password),
            salt,
            n, r, p,
            keylen);

        return CryptographicOperations.FixedTimeEquals(derived, expected);
    }

    private static byte[] ComputeArgon2Hash(string password, byte[] salt)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = MemorySize,
            Iterations = Iterations,
            DegreeOfParallelism = Parallelism
        };
        return argon2.GetBytes(HashLength);
    }
}
