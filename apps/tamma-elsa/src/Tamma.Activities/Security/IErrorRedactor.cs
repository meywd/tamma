namespace Tamma.Activities.Security;

/// <summary>
/// Redacts sensitive information (API keys, internal URLs, stack traces)
/// from error bodies before logging or storage.
/// Thread-safe and never throws exceptions.
/// </summary>
public interface IErrorRedactor
{
    /// <summary>
    /// Redact sensitive content from an error message or body.
    /// Removes Bearer tokens, OpenAI/Anthropic/generic API keys, internal URLs,
    /// and .NET stack traces. Never throws.
    /// </summary>
    /// <param name="errorBody">The raw error message or body to redact.</param>
    /// <returns>The redacted error body with sensitive information replaced by [REDACTED].</returns>
    string Redact(string errorBody);
}
