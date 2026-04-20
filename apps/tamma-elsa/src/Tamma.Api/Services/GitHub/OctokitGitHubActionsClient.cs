using System.Net;
using Octokit;
using Tamma.Activities.AgentDispatch;
using Tamma.Api.Services.Engine;

namespace Tamma.Api.Services.GitHub;

/// <summary>
/// Octokit-backed <see cref="IGitHubActionsClient"/>. Bridges the
/// agent-dispatch activities (living in Tamma.Activities) with the
/// existing GitHub App infrastructure (<see cref="OctokitGitHubAppClient"/>
/// + <see cref="IRepoInstallationResolver"/>).
///
/// <para>The activities interface is intentionally narrow; this class
/// adapts it to Octokit's typed client + a few raw-connection GETs where
/// Octokit doesn't expose the endpoint we need.</para>
///
/// <para>Error handling: the activities want 0 vs 204 vs 4xx vs 5xx
/// status codes, so we catch <see cref="ApiException"/> and return the
/// status code + body verbatim. Unexpected exceptions still propagate —
/// those indicate a bug, not an operator error.</para>
/// </summary>
public sealed class OctokitGitHubActionsClient : IGitHubActionsClient
{
    private readonly OctokitGitHubAppClient _appClient;
    private readonly IRepoInstallationResolver _resolver;
    private readonly ILogger<OctokitGitHubActionsClient> _logger;

    public OctokitGitHubActionsClient(
        OctokitGitHubAppClient appClient,
        IRepoInstallationResolver resolver,
        ILogger<OctokitGitHubActionsClient> logger)
    {
        _appClient = appClient;
        _resolver = resolver;
        _logger = logger;
    }

    public async Task<WorkflowFileCheck> CheckWorkflowFileAsync(
        string owner, string repo, string workflowFileName, CancellationToken ct = default)
    {
        var installationId = await _resolver.ResolveInstallationIdAsync(owner, repo, ct).ConfigureAwait(false);
        if (installationId is null)
        {
            return new WorkflowFileCheck(false, true, "installation_not_found");
        }

        try
        {
            var client = await _appClient.GetInstallationClientAsync(installationId.Value, ct).ConfigureAwait(false);
            var workflow = await client.Actions.Workflows.Get(owner, repo, workflowFileName).WaitAsync(ct);
            return new WorkflowFileCheck(Exists: workflow is not null, NotConfigured: false, ErrorReason: null);
        }
        catch (NotFoundException)
        {
            return new WorkflowFileCheck(false, false, "workflow_not_found");
        }
        catch (ApiException ex)
        {
            _logger.LogWarning(ex,
                "CheckWorkflowFile error {Owner}/{Repo} workflow={Workflow}: {Status}",
                owner, repo, workflowFileName, (int)ex.StatusCode);
            return new WorkflowFileCheck(false, false, $"github_api_error_{(int)ex.StatusCode}");
        }
    }

    public async Task<DispatchApiResult> DispatchWorkflowAsync(
        string owner, string repo, string workflowFileName, string @ref,
        IReadOnlyDictionary<string, string> inputs, CancellationToken ct = default)
    {
        var installationId = await _resolver.ResolveInstallationIdAsync(owner, repo, ct).ConfigureAwait(false);
        if (installationId is null)
        {
            return new DispatchApiResult(0, "installation_not_found", NotConfigured: true);
        }

        try
        {
            var client = await _appClient.GetInstallationClientAsync(installationId.Value, ct).ConfigureAwait(false);
            var dispatch = new CreateWorkflowDispatch(@ref);
            foreach (var kv in inputs)
            {
                dispatch.Inputs[kv.Key] = kv.Value;
            }
            await client.Actions.Workflows.CreateDispatch(owner, repo, workflowFileName, dispatch).WaitAsync(ct);
            // Octokit throws on non-204; reaching here means success.
            return new DispatchApiResult(204, null);
        }
        catch (RateLimitExceededException ex)
        {
            _logger.LogWarning(ex,
                "Dispatch rate-limited for {Owner}/{Repo} resetAt={ResetAt:o}",
                owner, repo, ex.Reset);
            return new DispatchApiResult(429, "github_rate_limited");
        }
        catch (AuthorizationException)
        {
            _appClient.InvalidateInstallationToken(installationId.Value);
            return new DispatchApiResult(401, "github_unauthorized");
        }
        catch (ApiException ex)
        {
            var status = (int)ex.StatusCode;
            var body = ex.Message;
            _logger.LogWarning(ex,
                "Dispatch failed for {Owner}/{Repo} workflow={Workflow}: HTTP {Status}",
                owner, repo, workflowFileName, status);
            return new DispatchApiResult(status, body);
        }
    }

    public async Task<IReadOnlyList<WorkflowRunSummary>> ListWorkflowRunsAsync(
        string owner, string repo, string branch, DateTime createdAfter,
        int perPage = 5, CancellationToken ct = default)
    {
        var installationId = await _resolver.ResolveInstallationIdAsync(owner, repo, ct).ConfigureAwait(false);
        if (installationId is null) return Array.Empty<WorkflowRunSummary>();

        try
        {
            var client = await _appClient.GetInstallationClientAsync(installationId.Value, ct).ConfigureAwait(false);

            // Octokit's Actions.Workflows.Runs.ListByWorkflow requires a
            // workflow id, but we want all runs on the branch (the
            // workflow file we filter on is always the same one). Using
            // the repo-scope List with WorkflowRunsRequest covers it.
            var request = new WorkflowRunsRequest
            {
                Branch = branch,
                Event = "workflow_dispatch",
                Created = $">={createdAfter:yyyy-MM-ddTHH:mm:ssZ}"
            };
            var options = new ApiOptions { PageSize = perPage, PageCount = 1 };
            var response = await client.Actions.Workflows.Runs.List(owner, repo, request, options).WaitAsync(ct);

            return response.WorkflowRuns
                .OrderByDescending(r => r.CreatedAt)
                .Select(ToSummary)
                .ToList();
        }
        catch (ApiException ex)
        {
            _logger.LogWarning(ex,
                "ListWorkflowRuns error {Owner}/{Repo} branch={Branch}: {Status}",
                owner, repo, branch, (int)ex.StatusCode);
            return Array.Empty<WorkflowRunSummary>();
        }
    }

    public async Task<WorkflowRunSummary?> GetWorkflowRunAsync(
        string owner, string repo, long runId, CancellationToken ct = default)
    {
        var installationId = await _resolver.ResolveInstallationIdAsync(owner, repo, ct).ConfigureAwait(false);
        if (installationId is null) return null;

        try
        {
            var client = await _appClient.GetInstallationClientAsync(installationId.Value, ct).ConfigureAwait(false);
            var run = await client.Actions.Workflows.Runs.Get(owner, repo, runId).WaitAsync(ct);
            return run is null ? null : ToSummary(run);
        }
        catch (NotFoundException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<WorkflowRunArtifact>> ListRunArtifactsAsync(
        string owner, string repo, long runId, CancellationToken ct = default)
    {
        var installationId = await _resolver.ResolveInstallationIdAsync(owner, repo, ct).ConfigureAwait(false);
        if (installationId is null) return Array.Empty<WorkflowRunArtifact>();

        try
        {
            var client = await _appClient.GetInstallationClientAsync(installationId.Value, ct).ConfigureAwait(false);
            var response = await client.Actions.Artifacts.ListWorkflowArtifacts(owner, repo, runId).WaitAsync(ct);
            return response.Artifacts
                .Select(a => new WorkflowRunArtifact(a.Id, a.Name, a.SizeInBytes, a.Expired))
                .ToList();
        }
        catch (ApiException ex)
        {
            _logger.LogWarning(ex,
                "ListRunArtifacts error {Owner}/{Repo} run={RunId}: {Status}",
                owner, repo, runId, (int)ex.StatusCode);
            return Array.Empty<WorkflowRunArtifact>();
        }
    }

    public async Task<byte[]?> DownloadArtifactZipAsync(
        string owner, string repo, long artifactId, CancellationToken ct = default)
    {
        var installationId = await _resolver.ResolveInstallationIdAsync(owner, repo, ct).ConfigureAwait(false);
        if (installationId is null) return null;

        try
        {
            var client = await _appClient.GetInstallationClientAsync(installationId.Value, ct).ConfigureAwait(false);
            var stream = await client.Actions.Artifacts.DownloadArtifact(owner, repo, artifactId, "zip").WaitAsync(ct);
            if (stream is null) return null;
            await using (stream)
            {
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, ct);
                return ms.ToArray();
            }
        }
        catch (NotFoundException)
        {
            return null;
        }
        catch (ApiException ex)
        {
            _logger.LogWarning(ex,
                "DownloadArtifact error {Owner}/{Repo} artifact={ArtifactId}: {Status}",
                owner, repo, artifactId, (int)ex.StatusCode);
            return null;
        }
    }

    public async Task<IReadOnlyList<PullRequestSummary>> ListPullRequestsForHeadAsync(
        string owner, string repo, string headBranch, CancellationToken ct = default)
    {
        var installationId = await _resolver.ResolveInstallationIdAsync(owner, repo, ct).ConfigureAwait(false);
        if (installationId is null) return Array.Empty<PullRequestSummary>();

        try
        {
            var client = await _appClient.GetInstallationClientAsync(installationId.Value, ct).ConfigureAwait(false);
            var request = new PullRequestRequest
            {
                State = ItemStateFilter.All,
                Head = $"{owner}:{headBranch}"
            };
            var prs = await client.PullRequest.GetAllForRepository(owner, repo, request).WaitAsync(ct);
            return prs.Select(pr => new PullRequestSummary(
                Number: pr.Number,
                Title: pr.Title ?? string.Empty,
                Body: pr.Body,
                HtmlUrl: pr.HtmlUrl ?? string.Empty,
                HeadSha: pr.Head?.Sha ?? string.Empty,
                ChangedFiles: pr.ChangedFiles)).ToList();
        }
        catch (ApiException ex)
        {
            _logger.LogWarning(ex,
                "ListPullRequests error {Owner}/{Repo} head={Head}: {Status}",
                owner, repo, headBranch, (int)ex.StatusCode);
            return Array.Empty<PullRequestSummary>();
        }
    }

    public async Task<BranchComparison?> CompareRefsAsync(
        string owner, string repo, string baseRef, string headRef, CancellationToken ct = default)
    {
        var installationId = await _resolver.ResolveInstallationIdAsync(owner, repo, ct).ConfigureAwait(false);
        if (installationId is null) return null;

        try
        {
            var client = await _appClient.GetInstallationClientAsync(installationId.Value, ct).ConfigureAwait(false);
            var comparison = await client.Repository.Commit.Compare(owner, repo, baseRef, headRef).WaitAsync(ct);
            if (comparison is null) return null;

            var files = comparison.Files
                .Select(f => new CompareFileChange(
                    Filename: f.Filename ?? string.Empty,
                    Status: f.Status ?? string.Empty,
                    Additions: f.Additions,
                    Deletions: f.Deletions))
                .ToList();

            var commits = comparison.Commits
                .Select(c => new CompareCommit(
                    Sha: c.Sha ?? string.Empty,
                    Message: c.Commit?.Message ?? string.Empty))
                .ToList();

            return new BranchComparison(
                BaseSha: comparison.MergeBaseCommit?.Sha ?? string.Empty,
                HeadSha: comparison.Commits.LastOrDefault()?.Sha ?? string.Empty,
                Files: files,
                Commits: commits);
        }
        catch (NotFoundException)
        {
            return null;
        }
        catch (ApiException ex)
        {
            _logger.LogWarning(ex,
                "Compare error {Owner}/{Repo} {Base}...{Head}: {Status}",
                owner, repo, baseRef, headRef, (int)ex.StatusCode);
            return null;
        }
    }

    public async Task<IReadOnlyList<CheckRunSummary>> ListCheckRunsAsync(
        string owner, string repo, string commitSha, CancellationToken ct = default)
    {
        var installationId = await _resolver.ResolveInstallationIdAsync(owner, repo, ct).ConfigureAwait(false);
        if (installationId is null) return Array.Empty<CheckRunSummary>();

        try
        {
            var client = await _appClient.GetInstallationClientAsync(installationId.Value, ct).ConfigureAwait(false);
            var runs = await client.Check.Run.GetAllForReference(owner, repo, commitSha).WaitAsync(ct);
            return runs.CheckRuns
                .Select(r => new CheckRunSummary(
                    Name: r.Name ?? string.Empty,
                    Status: r.Status.StringValue ?? string.Empty,
                    Conclusion: r.Conclusion?.StringValue))
                .ToList();
        }
        catch (ApiException ex)
        {
            _logger.LogWarning(ex,
                "ListCheckRuns error {Owner}/{Repo} sha={Sha}: {Status}",
                owner, repo, commitSha, (int)ex.StatusCode);
            return Array.Empty<CheckRunSummary>();
        }
    }

    private static WorkflowRunSummary ToSummary(WorkflowRun r) =>
        new(
            Id: r.Id,
            Status: r.Status.StringValue ?? string.Empty,
            Conclusion: r.Conclusion?.StringValue ?? string.Empty,
            HtmlUrl: r.HtmlUrl ?? string.Empty,
            CreatedAt: r.CreatedAt.UtcDateTime,
            UpdatedAt: r.UpdatedAt.UtcDateTime,
            HeadBranch: r.HeadBranch ?? string.Empty,
            Event: r.Event ?? string.Empty,
            ArtifactsUrl: r.ArtifactsUrl ?? string.Empty);
}
