namespace Tamma.Activities.Security;

/// <summary>
/// Result of a sanitization operation. Contains the sanitized text and any warnings
/// that were generated during the sanitization process.
/// </summary>
public sealed class SanitizationResult
{
    /// <summary>
    /// The sanitized text after all sanitization rules have been applied.
    /// </summary>
    public string Result { get; init; } = string.Empty;

    /// <summary>
    /// Warnings generated during sanitization, such as detected prompt injection patterns.
    /// Each warning includes the pattern category and matched pattern description.
    /// Never null; empty list when no warnings were generated.
    /// </summary>
    public List<string> Warnings { get; init; } = new();
}
