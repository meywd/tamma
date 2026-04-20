namespace Tamma.Activities.AgentDispatch;

// ================================================================
// Epic 19 — GitHub Actions API surface
//
// The activities & executor only need a narrow slice of the GitHub
// REST API. Defining that slice here keeps Tamma.Activities free of
// an Octokit dependency and lets Tamma.Api adapt the real client
// (OctokitGitHubAppClient) on top of this contract.
//
// The implementation lives in Tamma.Api/Services/GitHub/ because it
// needs the GitHub App installation router (which itself lives in
// the Api assembly). See OctokitGitHubActionsClient for the bridge.
// ================================================================

/// <summary>
/// Minimal GitHub Actions REST surface needed by the agent dispatch
/// activities. Returns null / null-collections on "not found" rather
/// than throwing so callers can decide the failure mode.
///
/// <para>All methods take <c>owner</c> + <c>repo</c> explicitly — the
/// implementation resolves an installation token from the repo on each
/// call. Callers should reuse a single instance per activity.</para>
/// </summary>
public interface IGitHubActionsClient
{
    /// <summary>
    /// Verify the workflow file exists in the repo's default branch via
    /// <c>GET /repos/{owner}/{repo}/actions/workflows/{workflow_id}</c>.
    /// Returns <c>true</c> if present, <c>false</c> if the workflow is
    /// missing (404) or the installation has no access.
    /// </summary>
    Task<WorkflowFileCheck> CheckWorkflowFileAsync(
        string owner, string repo, string workflowFileName, CancellationToken ct = default);

    /// <summary>
    /// Dispatch a workflow_dispatch event via
    /// <c>POST /repos/{owner}/{repo}/actions/workflows/{workflow_id}/dispatches</c>.
    /// Returns the API status code and any error body; success is 204.
    /// </summary>
    Task<DispatchApiResult> DispatchWorkflowAsync(
        string owner,
        string repo,
        string workflowFileName,
        string @ref,
        IReadOnlyDictionary<string, string> inputs,
        CancellationToken ct = default);

    /// <summary>
    /// List workflow runs filtered by branch + event + creation time. The
    /// most recent matching run is first in the returned list.
    /// </summary>
    Task<IReadOnlyList<WorkflowRunSummary>> ListWorkflowRunsAsync(
        string owner,
        string repo,
        string branch,
        DateTime createdAfter,
        int perPage = 5,
        CancellationToken ct = default);

    /// <summary>
    /// Fetch a single workflow run by id. Returns null when the run is
    /// not found.
    /// </summary>
    Task<WorkflowRunSummary?> GetWorkflowRunAsync(
        string owner, string repo, long runId, CancellationToken ct = default);

    /// <summary>
    /// List artifacts produced by a workflow run.
    /// </summary>
    Task<IReadOnlyList<WorkflowRunArtifact>> ListRunArtifactsAsync(
        string owner, string repo, long runId, CancellationToken ct = default);

    /// <summary>
    /// Download a specific artifact as a zip byte array. Returns null when
    /// the artifact is expired / deleted.
    /// </summary>
    Task<byte[]?> DownloadArtifactZipAsync(
        string owner, string repo, long artifactId, CancellationToken ct = default);

    /// <summary>
    /// List pull requests matching the given head branch.
    /// </summary>
    Task<IReadOnlyList<PullRequestSummary>> ListPullRequestsForHeadAsync(
        string owner, string repo, string headBranch, CancellationToken ct = default);

    /// <summary>
    /// Compare two refs (base...head) and return commits + file changes.
    /// </summary>
    Task<BranchComparison?> CompareRefsAsync(
        string owner, string repo, string baseRef, string headRef, CancellationToken ct = default);

    /// <summary>
    /// List check-run conclusions for the given commit SHA.
    /// </summary>
    Task<IReadOnlyList<CheckRunSummary>> ListCheckRunsAsync(
        string owner, string repo, string commitSha, CancellationToken ct = default);
}

/// <summary>
/// Workflow file existence check. <c>NotConfigured</c> is returned when
/// the GitHub App client itself isn't wired (dev mode); callers should
/// surface a clear operator error rather than dispatching against a null
/// installation.
/// </summary>
public sealed record WorkflowFileCheck(
    bool Exists,
    bool NotConfigured,
    string? ErrorReason);

/// <summary>
/// Result of a POST /dispatches call.
/// </summary>
public sealed record DispatchApiResult(
    int HttpStatusCode,
    string? ErrorReason,
    bool NotConfigured = false);

public sealed record WorkflowRunSummary(
    long Id,
    string Status,
    string Conclusion,
    string HtmlUrl,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string HeadBranch,
    string Event,
    string ArtifactsUrl);

public sealed record WorkflowRunArtifact(
    long Id,
    string Name,
    long SizeInBytes,
    bool Expired);

public sealed record PullRequestSummary(
    int Number,
    string Title,
    string? Body,
    string HtmlUrl,
    string HeadSha,
    int ChangedFiles);

public sealed record BranchComparison(
    string BaseSha,
    string HeadSha,
    IReadOnlyList<CompareFileChange> Files,
    IReadOnlyList<CompareCommit> Commits);

public sealed record CompareFileChange(
    string Filename,
    string Status,
    int Additions,
    int Deletions);

public sealed record CompareCommit(
    string Sha,
    string Message);

public sealed record CheckRunSummary(
    string Name,
    string Status,
    string? Conclusion);
