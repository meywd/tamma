namespace Tamma.Activities.Security;

/// <summary>
/// Static sanitization helpers for use in workflow lambda contexts
/// where DI is not available. Internally uses a static ContentSanitizer instance.
/// Thread-safe: ContentSanitizer has no mutable state and compiled regexes are thread-safe.
///
/// NOTE: This helper cannot log (no DI). Callers in workflow lambdas must log
/// before/after calling the helper if logging is required.
/// </summary>
public static class SecurityHelpers
{
    /// <summary>
    /// Static instance -- thread-safe because ContentSanitizer has no mutable state.
    /// Created without logger (static context has no DI).
    /// </summary>
    private static readonly ContentSanitizer Sanitizer = new();

    /// <summary>
    /// Sanitize a string for use in an LLM prompt. Convenience wrapper for
    /// <see cref="IContentSanitizer.SanitizeInput"/> in contexts without DI.
    /// Returns the sanitized string (warnings are discarded since we cannot log here).
    /// Returns empty string for null input, empty string for empty input.
    /// </summary>
    /// <param name="input">The raw input text to sanitize. May be null.</param>
    /// <returns>The sanitized text, or empty string if input was null/empty.</returns>
    public static string SanitizeForPrompt(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return input ?? string.Empty;

        return Sanitizer.SanitizeInput(input).Result;
    }
}
