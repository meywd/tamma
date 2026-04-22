using System.Text.RegularExpressions;

namespace Tamma.Api.Services.Security;

/// <summary>
/// Write-time heuristic check for catastrophic-backtracking regex patterns
/// (ReDoS). Ported from <c>NESTED_QUANTIFIER</c> in
/// <c>packages/api/src/services/sanitization-store.ts</c> at <c>9e9a57c~1</c>
/// — finding 014 / finding 025 (optional hardening).
///
/// <para>
/// The runtime sanitisation pipeline already enforces a 100ms
/// <see cref="Regex.MatchTimeout"/> so a pathological pattern cannot stall
/// requests indefinitely. <see cref="ReDosGuard"/> adds a write-time guard
/// so admins learn at PUT time that their pattern is unsafe, instead of
/// waking up to "every sanitization call burns 100ms" in production.
/// </para>
/// </summary>
public static class ReDosGuard
{
    /// <summary>
    /// Matches a quantifier (<c>* + ? {n,m}</c>) immediately following a
    /// closing parenthesis whose group also contains a quantifier — the
    /// classic <c>(a+)+</c> shape. Catches the common ReDoS patterns; will
    /// not catch lookahead-based ReDoS (<c>(?=(a+))+</c>) — that kind needs
    /// runtime guards.
    /// </summary>
    private static readonly Regex NestedQuantifier =
        new(@"\([^)]*[*+?{][^)]*\)[*+?{]",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Maximum permitted pattern length (chars).</summary>
    public const int MaxPatternLength = 500;

    /// <summary>Maximum number of patterns a tenant may install in one upsert.</summary>
    public const int MaxPatternCount = 100;

    /// <summary>
    /// Returns true if the pattern is suspected to be ReDoS-vulnerable.
    /// </summary>
    public static bool IsSuspectNestedQuantifier(string pattern)
        => !string.IsNullOrEmpty(pattern) && NestedQuantifier.IsMatch(pattern);

    /// <summary>
    /// Validate a single pattern: not empty, ≤ MaxPatternLength, compiles,
    /// no nested-quantifier shape. Throws <see cref="ArgumentException"/> on
    /// failure with a caller-friendly message.
    /// </summary>
    public static void Validate(string label, string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
            throw new ArgumentException($"{label}: pattern must not be empty.", nameof(pattern));
        if (pattern.Length > MaxPatternLength)
            throw new ArgumentException(
                $"{label}: pattern length {pattern.Length} exceeds max {MaxPatternLength}.",
                nameof(pattern));
        if (IsSuspectNestedQuantifier(pattern))
            throw new ArgumentException(
                $"{label}: unsafe nested-quantifier pattern \"{pattern}\".",
                nameof(pattern));
        try
        {
            _ = new Regex(pattern);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException(
                $"{label}: invalid regex pattern - {ex.Message}", nameof(pattern));
        }
    }
}
