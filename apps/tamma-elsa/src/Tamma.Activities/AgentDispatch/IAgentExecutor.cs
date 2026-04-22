using Tamma.Activities.AgentDispatch.Models;

namespace Tamma.Activities.AgentDispatch;

/// <summary>
/// Abstraction for executing an AI agent to perform a development task.
/// Implementations handle the execution environment (local process,
/// GitHub Actions runner, etc.).
///
/// <para>Story 19-5 AC-1 — single-method interface, same input and output
/// regardless of mode. The implementation (Local vs GitHubActions) is a
/// configuration choice, not a code change for the workflow.</para>
/// </summary>
public interface IAgentExecutor
{
    /// <summary>
    /// Execute the agent and return the result. This is a potentially
    /// long-running operation (minutes). Implementations must honour
    /// <paramref name="cancellationToken"/>.
    /// </summary>
    Task<AgentExecutionResult> ExecuteAsync(
        AgentExecutionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The execution mode this executor operates in
    /// (<c>"local"</c> or <c>"github_actions"</c>).
    /// </summary>
    string Mode { get; }
}
