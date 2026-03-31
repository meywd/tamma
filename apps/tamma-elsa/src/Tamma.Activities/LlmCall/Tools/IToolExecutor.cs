using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.LlmCall.Tools;

/// <summary>
/// Contract for a single tool that the LLM can invoke during the agentic tool loop.
/// Each implementation is registered in DI as IToolExecutor and discovered by the registry.
/// Implementations must never throw — all errors are returned as ToolExecutionResult with Success=false.
/// </summary>
public interface IToolExecutor
{
    /// <summary>
    /// Unique tool name matching what the LLM sees (e.g. "file_read", "shell_execute").
    /// </summary>
    string ToolName { get; }

    /// <summary>
    /// Human-readable description sent to the LLM as part of the tool definition.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// JSON Schema for the tool's input parameters (serialized as Dictionary for the LLM API).
    /// </summary>
    Dictionary<string, object> InputSchema { get; }

    /// <summary>
    /// Execute the tool with the given arguments.
    /// </summary>
    /// <param name="toolCallId">Provider-assigned tool call ID for correlation.</param>
    /// <param name="argumentsJson">JSON-serialized arguments from the LLM.</param>
    /// <param name="cancellationToken">Cancellation token (includes timeout).</param>
    /// <returns>Structured result with output text and timing. Never throws.</returns>
    Task<ToolExecutionResult> ExecuteAsync(
        string toolCallId,
        string argumentsJson,
        CancellationToken cancellationToken = default);
}
