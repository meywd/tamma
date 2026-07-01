using Tamma.Activities.AgentDispatch;

namespace Tamma.Api.Tests.AgentDispatch;

/// <summary>
/// Story 38-2 — in-memory <see cref="IGitHubActionsClient"/> for the server-side
/// aggregator + mediation tests (the aggregation moved to Tamma.Api). Lets tests
/// seed state, run the aggregator/mediation service, and assert outputs + which
/// calls fired — without Octokit.
/// </summary>
internal sealed class FakeGitHubActionsClient : IGitHubActionsClient
{
    public Func<string, string, string, WorkflowFileCheck> CheckWorkflow { get; set; } =
        (_, _, _) => new WorkflowFileCheck(true, false, null);

    public Func<string, string, string, string, IReadOnlyDictionary<string, string>, DispatchApiResult> OnDispatch { get; set; } =
        (_, _, _, _, _) => new DispatchApiResult(204, null);

    public List<DispatchInvocation> DispatchCalls { get; } = new();

    public IReadOnlyList<WorkflowRunSummary> DefaultListRuns { get; set; } = Array.Empty<WorkflowRunSummary>();
    public Dictionary<long, WorkflowRunSummary> RunsById { get; } = new();
    public Dictionary<long, IReadOnlyList<WorkflowRunArtifact>> ArtifactsByRunId { get; } = new();
    public Dictionary<long, byte[]?> ArtifactBytes { get; } = new();
    public IReadOnlyList<PullRequestSummary> Pulls { get; set; } = Array.Empty<PullRequestSummary>();
    public BranchComparison? Comparison { get; set; }
    public IReadOnlyList<CheckRunSummary> CheckRuns { get; set; } = Array.Empty<CheckRunSummary>();
    public long? DefaultInstallationId { get; set; } = 100L;

    public int GetRunCalls { get; private set; }
    public int ListRunsCalls { get; private set; }
    public int CheckWorkflowCalls { get; private set; }

    public Task<WorkflowFileCheck> CheckWorkflowFileAsync(string owner, string repo, string workflowFileName, CancellationToken ct = default)
    {
        CheckWorkflowCalls++;
        return Task.FromResult(CheckWorkflow(owner, repo, workflowFileName));
    }

    public Task<DispatchApiResult> DispatchWorkflowAsync(
        string owner, string repo, string workflowFileName, string @ref,
        IReadOnlyDictionary<string, string> inputs, CancellationToken ct = default)
    {
        DispatchCalls.Add(new DispatchInvocation(owner, repo, workflowFileName, @ref, inputs));
        return Task.FromResult(OnDispatch(owner, repo, workflowFileName, @ref, inputs));
    }

    public Task<IReadOnlyList<WorkflowRunSummary>> ListWorkflowRunsAsync(
        string owner, string repo, string branch, DateTime createdAfter, int perPage = 5, CancellationToken ct = default)
    {
        ListRunsCalls++;
        return Task.FromResult(DefaultListRuns);
    }

    public Task<WorkflowRunSummary?> GetWorkflowRunAsync(string owner, string repo, long runId, CancellationToken ct = default)
    {
        GetRunCalls++;
        RunsById.TryGetValue(runId, out var v);
        return Task.FromResult<WorkflowRunSummary?>(v);
    }

    public Task<IReadOnlyList<WorkflowRunArtifact>> ListRunArtifactsAsync(string owner, string repo, long runId, CancellationToken ct = default)
    {
        ArtifactsByRunId.TryGetValue(runId, out var list);
        return Task.FromResult(list ?? Array.Empty<WorkflowRunArtifact>());
    }

    public Task<byte[]?> DownloadArtifactZipAsync(string owner, string repo, long artifactId, CancellationToken ct = default)
    {
        ArtifactBytes.TryGetValue(artifactId, out var bytes);
        return Task.FromResult(bytes);
    }

    public Task<IReadOnlyList<PullRequestSummary>> ListPullRequestsForHeadAsync(string owner, string repo, string headBranch, CancellationToken ct = default)
        => Task.FromResult(Pulls);

    public Task<BranchComparison?> CompareRefsAsync(string owner, string repo, string baseRef, string headRef, CancellationToken ct = default)
        => Task.FromResult(Comparison);

    public Task<IReadOnlyList<CheckRunSummary>> ListCheckRunsAsync(string owner, string repo, string commitSha, CancellationToken ct = default)
        => Task.FromResult(CheckRuns);

    public Task<long?> ResolveInstallationIdAsync(string owner, string repo, CancellationToken ct = default)
        => Task.FromResult(DefaultInstallationId);
}

internal sealed record DispatchInvocation(
    string Owner, string Repo, string WorkflowFile, string Ref, IReadOnlyDictionary<string, string> Inputs);
