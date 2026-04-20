namespace Tamma.Activities.AgentDispatch;

/// <summary>
/// Fallback <see cref="IGitHubActionsClient"/> used when the GitHub App
/// isn't configured (dev mode, CLI-only deployments). Every call reports
/// <c>NotConfigured = true</c> so the activities surface a clear
/// operator-facing error rather than silently succeeding.
///
/// <para>Mirrors the <c>NullGitHubAppClient</c> pattern already used by
/// <see cref="Tamma.Activities.AgentDispatch"/> callers on the Api side.</para>
/// </summary>
public sealed class NullGitHubActionsClient : IGitHubActionsClient
{
    private const string NotConfiguredReason = "github_client_not_configured";

    public Task<WorkflowFileCheck> CheckWorkflowFileAsync(
        string owner, string repo, string workflowFileName, CancellationToken ct = default)
        => Task.FromResult(new WorkflowFileCheck(false, true, NotConfiguredReason));

    public Task<DispatchApiResult> DispatchWorkflowAsync(
        string owner, string repo, string workflowFileName, string @ref,
        IReadOnlyDictionary<string, string> inputs, CancellationToken ct = default)
        => Task.FromResult(new DispatchApiResult(0, NotConfiguredReason, NotConfigured: true));

    public Task<IReadOnlyList<WorkflowRunSummary>> ListWorkflowRunsAsync(
        string owner, string repo, string branch, DateTime createdAfter,
        int perPage = 5, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<WorkflowRunSummary>>(System.Array.Empty<WorkflowRunSummary>());

    public Task<WorkflowRunSummary?> GetWorkflowRunAsync(
        string owner, string repo, long runId, CancellationToken ct = default)
        => Task.FromResult<WorkflowRunSummary?>(null);

    public Task<IReadOnlyList<WorkflowRunArtifact>> ListRunArtifactsAsync(
        string owner, string repo, long runId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<WorkflowRunArtifact>>(System.Array.Empty<WorkflowRunArtifact>());

    public Task<byte[]?> DownloadArtifactZipAsync(
        string owner, string repo, long artifactId, CancellationToken ct = default)
        => Task.FromResult<byte[]?>(null);

    public Task<IReadOnlyList<PullRequestSummary>> ListPullRequestsForHeadAsync(
        string owner, string repo, string headBranch, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PullRequestSummary>>(System.Array.Empty<PullRequestSummary>());

    public Task<BranchComparison?> CompareRefsAsync(
        string owner, string repo, string baseRef, string headRef, CancellationToken ct = default)
        => Task.FromResult<BranchComparison?>(null);

    public Task<IReadOnlyList<CheckRunSummary>> ListCheckRunsAsync(
        string owner, string repo, string commitSha, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<CheckRunSummary>>(System.Array.Empty<CheckRunSummary>());

    public Task<long?> ResolveInstallationIdAsync(
        string owner, string repo, CancellationToken ct = default)
        => Task.FromResult<long?>(null);
}
