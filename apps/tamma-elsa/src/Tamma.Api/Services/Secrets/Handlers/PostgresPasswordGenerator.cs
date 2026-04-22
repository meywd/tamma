using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Tamma.Api.Services.Secrets.Handlers;

/// <summary>
/// Story 29-7 AC2 + AC7 — generates a Postgres-safe password from a
/// fixed character set that:
///
/// <list type="bullet">
///   <item><description>Contains no single quote, no backslash, no
///     semicolon — so the SQL-literal escaper is a safety-net rather
///     than load-bearing.</description></item>
///   <item><description>Is ASCII only — avoids Postgres's inconsistent
///     Unicode password handling across server encodings.</description></item>
///   <item><description>Defaults to 64 characters (~380 bits of
///     effective entropy given the ~70-char alphabet).</description></item>
/// </list>
///
/// <para>The class is static-factory shaped so it composes cleanly
/// with DI and tests.</para>
/// </summary>
public static class PostgresPasswordGenerator
{
    public const int DefaultLength = 64;

    // AC2 regex — safe characters only, no single quote or backslash.
    private const string Alphabet =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789" +
        "!@#$%^&*()_+-=[]{}|:,.<>?";

    private static readonly Regex SafePattern = new(
        @"^[A-Za-z0-9!@#$%^&*()_+\-=\[\]{}|:,.<>?]+$",
        RegexOptions.Compiled);

    /// <summary>
    /// Generate a password of length <paramref name="length"/> from the
    /// safe alphabet. Uses rejection sampling on
    /// <see cref="RandomNumberGenerator"/> output so the distribution
    /// over the alphabet is uniform.
    /// </summary>
    public static string Generate(int length = DefaultLength)
    {
        if (length < 16 || length > 256)
            throw new ArgumentOutOfRangeException(nameof(length),
                "Password length must be in [16, 256].");

        var alphabet = Alphabet;
        var result = new char[length];
        Span<byte> buffer = stackalloc byte[1];

        // Rejection-sample — modulo bias is negligible at 70/256 but
        // doing it right keeps the audit log clean.
        var threshold = (byte)(256 - (256 % alphabet.Length));
        var i = 0;
        while (i < length)
        {
            RandomNumberGenerator.Fill(buffer);
            if (buffer[0] >= threshold) continue;
            result[i++] = alphabet[buffer[0] % alphabet.Length];
        }

        var pwd = new string(result);
        if (!SafePattern.IsMatch(pwd))
            throw new InvalidOperationException(
                "Generated password failed the safe-character regex check. " +
                "This should be impossible and indicates an internal bug.");
        return pwd;
    }

    /// <summary>
    /// Validate that <paramref name="candidate"/> matches the safe
    /// character set. Used by the SQL-literal escaper as a guard rail
    /// against malformed operator-supplied passwords.
    /// </summary>
    public static bool IsSafe(string candidate) =>
        !string.IsNullOrEmpty(candidate) && SafePattern.IsMatch(candidate);
}
