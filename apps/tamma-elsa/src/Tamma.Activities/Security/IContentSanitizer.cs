namespace Tamma.Activities.Security;

/// <summary>
/// Sanitizes content before it reaches the LLM (input) or after it returns (output).
/// All methods are thread-safe, idempotent, and never throw exceptions.
/// </summary>
public interface IContentSanitizer
{
    /// <summary>
    /// Sanitize input content before it reaches the LLM.
    /// Strips HTML, removes zero-width characters, normalizes Unicode (NFKD),
    /// and detects prompt injection patterns across 4+ categories.
    /// Never throws -- returns warnings for anything suspicious.
    /// </summary>
    /// <param name="input">The raw input text to sanitize.</param>
    /// <returns>A <see cref="SanitizationResult"/> containing the sanitized text and any warnings.</returns>
    SanitizationResult SanitizeInput(string input);

    /// <summary>
    /// Sanitize output content coming back from the LLM.
    /// Strips null bytes and zero-width chars, strips HTML while preserving code blocks.
    /// Less aggressive than input sanitization -- does not check for injection patterns.
    /// Never throws -- returns warnings for anything suspicious.
    /// </summary>
    /// <param name="output">The LLM output text to sanitize.</param>
    /// <returns>A <see cref="SanitizationResult"/> containing the sanitized text and any warnings.</returns>
    SanitizationResult SanitizeOutput(string output);
}
