using System.Collections.Frozen;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Tamma.Api.Auth;

/// <summary>
/// Mirror of TS <c>packages/api/src/auth/password.ts:73-112</c>
/// <c>validatePasswordStrength</c>. Story 18-1 AC 4 enumerates the criteria.
///
/// <para>
/// Audit finding auth/013: the common-password list is the top-1000 from
/// SecLists (Passwords/Common-Credentials/xato-net-10-million-passwords-1000.txt,
/// MIT-licensed, Daniel Miessler 2018). Embedded as a resource and loaded into
/// a <see cref="FrozenSet{T}"/> at class initialization for O(1) case-insensitive
/// lookup.
/// </para>
/// </summary>
public static class PasswordStrengthValidator
{
    public const int MinLength = 8;
    public const int MaxLength = 128;

    private const string CommonPasswordsResourceName = "Tamma.Api.Auth.common-passwords.txt";

    private static readonly Regex HasUpper = new("[A-Z]", RegexOptions.Compiled);
    private static readonly Regex HasLower = new("[a-z]", RegexOptions.Compiled);
    private static readonly Regex HasDigit = new(@"\d", RegexOptions.Compiled);

    /// <summary>
    /// Top-1000 common passwords (lowercase, case-insensitive lookup). Loaded
    /// once at type-init from the embedded SecLists resource. See
    /// <c>Auth/common-passwords.txt</c> + LICENSE in the project root.
    /// </summary>
    private static readonly FrozenSet<string> CommonPasswords = LoadCommonPasswords();

    /// <summary>Number of entries loaded from the embedded common-passwords list.</summary>
    public static int CommonPasswordCount => CommonPasswords.Count;

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
        if (CommonPasswords.Contains(password))
            errors.Add("Password is too common");

        return new Result(errors.Count == 0, errors);
    }

    private static FrozenSet<string> LoadCommonPasswords()
    {
        var asm = typeof(PasswordStrengthValidator).Assembly;
        using var stream = asm.GetManifestResourceStream(CommonPasswordsResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{CommonPasswordsResourceName}' not found. " +
                "Check the EmbeddedResource entry in Tamma.Api.csproj.");

        using var reader = new StreamReader(stream);
        var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            entries.Add(trimmed);
        }

        // FrozenSet with OrdinalIgnoreCase → case-insensitive O(1) lookup; the
        // source file is already lowercase so this degenerates to plain
        // ordinal equality but remains correct if the list is ever re-synced
        // from a mixed-case source.
        return entries.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }
}
