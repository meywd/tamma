using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.Security;

/// <summary>
/// Validates LLM-returned tool calls against an allowlist, format rules,
/// argument constraints, and blocked command patterns.
/// </summary>
public interface IToolCallValidator
{
    /// <summary>
    /// Validate a single tool call against the list of tools that were sent to the LLM.
    /// Returns a validation result. When invalid, ErrorMessage is populated and the
    /// caller should return this message to the LLM as a tool error (not crash the workflow).
    /// </summary>
    /// <param name="toolCall">The tool call from the LLM response.</param>
    /// <param name="allowedToolNames">Names of tools that were offered to the LLM.</param>
    /// <returns>Validation result with sanitized arguments when valid, or error message when invalid.</returns>
    ToolCallValidationResult Validate(LlmToolCall toolCall, IReadOnlyList<string> allowedToolNames);
}

/// <summary>
/// Result of tool call validation.
/// </summary>
public sealed class ToolCallValidationResult
{
    /// <summary>Whether the tool call passed all validation checks.</summary>
    public bool IsValid { get; init; }

    /// <summary>Human-readable error message when IsValid == false.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// The tool call arguments JSON after sanitization of string values.
    /// Same as original if no string values were modified.
    /// Only populated when IsValid == true.
    /// </summary>
    public string? SanitizedArgumentsJson { get; init; }
}
