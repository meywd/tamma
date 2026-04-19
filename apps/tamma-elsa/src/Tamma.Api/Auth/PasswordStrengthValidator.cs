using System.Text.RegularExpressions;

namespace Tamma.Api.Auth;

/// <summary>
/// Mirror of TS <c>packages/api/src/auth/password.ts:73-112</c>
/// <c>validatePasswordStrength</c>. Story 18-1 AC 4 enumerates the criteria.
/// </summary>
public static class PasswordStrengthValidator
{
    public const int MinLength = 8;
    public const int MaxLength = 128;

    private static readonly Regex HasUpper = new("[A-Z]", RegexOptions.Compiled);
    private static readonly Regex HasLower = new("[a-z]", RegexOptions.Compiled);
    private static readonly Regex HasDigit = new(@"\d", RegexOptions.Compiled);

    // Subset of the OWASP / SecLists top-N — matches the 45 entries the TS
    // implementation shipped. Story 18-1 AC 4 calls for "top-1000"; expanding
    // is a follow-up. The list is lowercase-only because the lookup
    // lowercases the candidate first.
    private static readonly HashSet<string> CommonPasswords = new(StringComparer.Ordinal)
    {
        "password", "12345678", "123456789", "1234567890", "qwerty123",
        "qwerty1234", "qwertyuiop", "password1", "password12", "password123",
        "admin", "admin123", "administrator", "letmein", "welcome",
        "welcome1", "monkey", "dragon", "master", "passw0rd",
        "p@ssw0rd", "p@ssword", "iloveyou", "abc12345", "abcd1234",
        "12345678a", "11111111", "00000000", "qwertyui", "asdfghjk",
        "zxcvbnm", "1qaz2wsx", "1q2w3e4r", "test1234", "demo1234",
        "changeme", "default1", "trustno1", "sunshine", "princess",
        "starwars", "freedom", "football", "baseball", "shadow123"
    };

    public record Result(bool Valid, IReadOnlyList<string> Errors);

    public static Result Validate(string password)
    {
        var errors = new List<string>();
        if (string.IsNullOrEmpty(password))
        {
            errors.Add("Password is required");
            return new Result(false, errors);
        }
        if (password.Length < MinLength)
            errors.Add($"Password must be at least {MinLength} characters");
        if (password.Length > MaxLength)
            errors.Add($"Password must be at most {MaxLength} characters");
        if (!HasUpper.IsMatch(password))
            errors.Add("Password must contain at least one uppercase letter");
        if (!HasLower.IsMatch(password))
            errors.Add("Password must contain at least one lowercase letter");
        if (!HasDigit.IsMatch(password))
            errors.Add("Password must contain at least one digit");
        if (CommonPasswords.Contains(password.ToLowerInvariant()))
            errors.Add("Password is too common");

        return new Result(errors.Count == 0, errors);
    }
}
