namespace Tamma.Activities.LlmCall.Tools;

/// <summary>
/// Registry for discovering and retrieving tool executors by name.
/// Supports allowlist filtering for workflow-level tool restriction.
/// </summary>
public interface IToolExecutorRegistry
{
    /// <summary>
    /// Get a tool executor by name. Returns null if no executor is registered for that name.
    /// </summary>
    IToolExecutor? GetExecutor(string toolName);

    /// <summary>
    /// Check if a tool is allowed given the current allowlist.
    /// Returns true if allowlist is null/empty (all tools allowed) or if the name is in the allowlist.
    /// </summary>
    bool IsAllowed(string toolName, string[]? allowlist);

    /// <summary>
    /// Get all registered tool executors (for building the tools array sent to the LLM).
    /// </summary>
    IReadOnlyList<IToolExecutor> GetAll();

    /// <summary>
    /// Get all registered tool executors filtered by an allowlist.
    /// </summary>
    IReadOnlyList<IToolExecutor> GetAllowed(string[]? allowlist);
}
