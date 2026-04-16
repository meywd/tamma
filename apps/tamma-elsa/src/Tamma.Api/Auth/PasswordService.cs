using System.Security.Cryptography;
using Konscious.Security.Cryptography;

namespace Tamma.Api.Auth;

public interface IPasswordService
{
    string HashPassword(string password);
    bool VerifyPassword(string password, string hash);
}

public class PasswordService : IPasswordService
{
    private const int MemorySize = 65536; // 64 MB
    private const int Iterations = 3;
    private const int Parallelism = 4;
    private const int SaltLength = 16;
    private const int HashLength = 32;

    public string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var hash = ComputeHash(password, salt);
        return $"$argon2id$v=19$m={MemorySize},t={Iterations},p={Parallelism}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool VerifyPassword(string password, string hash)
    {
        try
        {
            var parts = hash.Split('$');
            if (parts.Length != 6 || parts[1] != "argon2id") return false;

            var salt = Convert.FromBase64String(parts[4]);
            var expectedHash = Convert.FromBase64String(parts[5]);
            var computedHash = ComputeHash(password, salt);

            return CryptographicOperations.FixedTimeEquals(computedHash, expectedHash);
        }
        catch
        {
            return false;
        }
    }

    private static byte[] ComputeHash(string password, byte[] salt)
    {
        using var argon2 = new Argon2id(System.Text.Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = MemorySize,
            Iterations = Iterations,
            DegreeOfParallelism = Parallelism
        };
        return argon2.GetBytes(HashLength);
    }
}
