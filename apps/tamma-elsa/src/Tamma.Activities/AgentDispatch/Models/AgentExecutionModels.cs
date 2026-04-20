namespace Tamma.Activities.AgentDispatch.Models;

// ================================================================
// Epic 19 — Agent Dispatch shared types
//
// These models flow through four activities / two executors:
//   DispatchAgentWorkflowActivity  (story 19-2)
//   MonitorAgentWorkflowActivity   (story 19-3)
//   CollectAgentResultsActivity    (story 19-4)
//   IAgentExecutor (Local / GHA)   (story 19-5)
//
// Kept in a single file so the contract surface is easy to grok.
// Records are used intentionally — these values are immutable once a
// pipeline step completes, and equality-by-value keeps tests simple.
// ================================================================

/// <summary>
/// Execution mode for an agent request. Resolved by
/// <c>AgentExecutorFactory.Create</c> at workflow runtime based on
/// configuration and installation availability.
/// </summary>
public enum ExecutionMode
{
    /// <summary>Run the agent in-process on the Tamma host.</summary>
    Local,

    /// <summary>Dispatch to the user's GitHub Actions runner.</summary>
    GitHubActions
}

/// <summary>
/// Stable string keys used in events + diagnostics to identify the
/// execution mode. Kept as string constants so they survive JSON
/// (de)serialization unchanged across the ELSA workflow boundary.
/// </summary>
public static class ExecutionModeNames
{
    public const string Local = "local";
    public const string GitHubActions = "github_actions";

    public static string From(ExecutionMode mode) => mode switch
    {
        ExecutionMode.Local => Local,
        ExecutionMode.GitHubActions => GitHubActions,
        _ => "unknown"
    };
}

/// <summary>
/// Input to <see cref="IAgentExecutor.ExecuteAsync"/>. Contains everything
/// an agent needs to know to perform its task on the target branch.
///
/// <para>Story 19-5 AC-7: shared between Local + GitHubActions executors and
/// the <c>ExecuteAgentActivity</c> ELSA wrapper.</para>
/// </summary>
public sealed record AgentExecutionRequest(
    string Repository,
    string BranchName,
    int IssueNumber,
    string IssueTitle,
    string Task,
    string PlanJson,
    string SessionId,
    string AgentProvider,
    string? AgentConfigJson,
    string? WorkflowFileName,
    int TimeoutMinutes);

/// <summary>
/// Unified result returned by every <see cref="IAgentExecutor"/>
/// implementation. Story 19-5 AC-7 / story 19-4 AC-4.
///
/// <para>Designed as a drop-in replacement for the existing TDD activity
/// outputs so <c>SingleIssueCycleWorkflow</c> can consume it without
/// branching on mode.</para>
/// </summary>
public sealed record AgentExecutionResult(
    bool Success,
    int? PrNumber,
    string? PrUrl,
    string CommitSha,
    string[] FilesChanged,
    int CommitsCount,
    bool? ChecksPassed,
    int TokensUsed,
    int DurationSeconds,
    string? ErrorMessage,
    string? AgentLogSummary,
    string AgentProvider,
    string? AgentVersion,
    string ExecutionMode)
{
    /// <summary>
    /// Factory for a failed result with minimal context (used when dispatch
    /// itself never succeeds or the GHA workflow_run concludes with
    /// non-success).
    /// </summary>
    public static AgentExecutionResult Failed(
        string errorMessage,
        string agentProvider,
        string executionMode) =>
        new(
            Success: false,
            PrNumber: null,
            PrUrl: null,
            CommitSha: string.Empty,
            FilesChanged: System.Array.Empty<string>(),
            CommitsCount: 0,
            ChecksPassed: null,
            TokensUsed: 0,
            DurationSeconds: 0,
            ErrorMessage: errorMessage,
            AgentLogSummary: null,
            AgentProvider: agentProvider,
            AgentVersion: null,
            ExecutionMode: executionMode);
}

/// <summary>
/// Story 19-2 output — outcome of dispatching a workflow_dispatch event.
/// The GitHub API returns 204 with no body on success; we carry an
/// <c>DispatchedAt</c> timestamp so the monitoring phase can filter
/// out pre-existing runs on the same branch.
/// </summary>
public sealed record AgentDispatchResult(
    bool Success,
    string? WorkflowRunUrl,
    string? ErrorMessage,
    DateTime DispatchedAt);

/// <summary>
/// Story 19-3 output — terminal state of the monitored workflow_run.
/// </summary>
public sealed record AgentMonitorResult(
    long WorkflowRunId,
    string Status,
    string Conclusion,
    string WorkflowRunUrl,
    int DurationSeconds,
    string ArtifactsUrl);

/// <summary>
/// Schema of the <c>.tamma/result.json</c> artifact produced by the
/// agent runner (story 19-1). Parsed by <c>CollectAgentResultsActivity</c>
/// and merged into <see cref="AgentExecutionResult"/>.
/// </summary>
public sealed record AgentResultArtifact(
    bool Success,
    string Task,
    int IssueNumber,
    string BranchName,
    string TammaSessionId,
    string[] FilesChanged,
    int? PrNumber,
    string CommitSha,
    string? ErrorMessage,
    string? AgentLogSummary,
    int TokensUsed,
    int DurationSeconds,
    string AgentProvider,
    string? AgentVersion);
