using Tamma.Activities.AgentDispatch;

namespace Tamma.Activities.Tests.AgentDispatch;

/// <summary>
/// Hand-rolled in-memory <see cref="IGitHubActionsClient"/> used by all
/// agent-dispatch tests. Prefer this over Moq.Setup chains for each call
/// site — the fake lets tests write expressive "seed the state, run the
/// service, assert outputs" stories with minimal noise.
/// </summary>
internal sealed class FakeGitHubActionsClient : IGitHubActionsClient
{
    public Func<string, string, string, WorkflowFileCheck> CheckWorkflow { get; set; } =
        (_, _, _) => new WorkflowFileCheck(true, false, null);

    public Func<string, string, string, string, IReadOnlyDictionary<string, string>, DispatchApiResult> OnDispatch { get; set; } =
        (_, _, _, _, _) => new DispatchApiResult(204, null);

    public List<DispatchInvocation> DispatchCalls { get; } = new();

    public Queue<IReadOnlyList<WorkflowRunSummary>> ListRunsQueue { get; } = new();

    public IReadOnlyList<WorkflowRunSummary> DefaultListRuns { get; set; } =
        System.Array.Empty<WorkflowRunSummary>();

    public Dictionary<long, WorkflowRunSummary> RunsById { get; } = new();

    public Queue<WorkflowRunSummary?>? GetRunQueue { get; set; }

    public Dictionary<long, IReadOnlyList<WorkflowRunArtifact>> ArtifactsByRunId { get; } = new();

    public Dictionary<long, byte[]?> ArtifactBytes { get; } = new();

    public IReadOnlyList<PullRequestSummary> Pulls { get; set; } = System.Array.Empty<PullRequestSummary>();

    public BranchComparison? Comparison { get; set; }

    public IReadOnlyList<CheckRunSummary> CheckRuns { get; set; } = System.Array.Empty<CheckRunSummary>();

    public int GetRunCalls { get; private set; }
    public int ListRunsCalls { get; private set; }
    public int CheckWorkflowCalls { get; private set; }

    public Task<WorkflowFileCheck> CheckWorkflowFileAsync(
        string owner, string repo, string workflowFileName, CancellationToken ct = default)
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
        if (ListRunsQueue.Count > 0) return Task.FromResult(ListRunsQueue.Dequeue());
        return Task.FromResult(DefaultListRuns);
    }

    public Task<WorkflowRunSummary?> GetWorkflowRunAsync(
        string owner, string repo, long runId, CancellationToken ct = default)
    {
        GetRunCalls++;
        if (GetRunQueue is not null && GetRunQueue.Count > 0)
        {
            return Task.FromResult(GetRunQueue.Dequeue());
        }
        RunsById.TryGetValue(runId, out var v);
        return Task.FromResult<WorkflowRunSummary?>(v);
    }

    public Task<IReadOnlyList<WorkflowRunArtifact>> ListRunArtifactsAsync(
        string owner, string repo, long runId, CancellationToken ct = default)
    {
        ArtifactsByRunId.TryGetValue(runId, out var list);
        return Task.FromResult(list ?? System.Array.Empty<WorkflowRunArtifact>());
    }

    public Task<byte[]?> DownloadArtifactZipAsync(
        string owner, string repo, long artifactId, CancellationToken ct = default)
    {
        ArtifactBytes.TryGetValue(artifactId, out var bytes);
        return Task.FromResult(bytes);
    }

    public Task<IReadOnlyList<PullRequestSummary>> ListPullRequestsForHeadAsync(
        string owner, string repo, string headBranch, CancellationToken ct = default)
        => Task.FromResult(Pulls);

    public Task<BranchComparison?> CompareRefsAsync(
        string owner, string repo, string baseRef, string headRef, CancellationToken ct = default)
        => Task.FromResult(Comparison);

    public Task<IReadOnlyList<CheckRunSummary>> ListCheckRunsAsync(
        string owner, string repo, string commitSha, CancellationToken ct = default)
        => Task.FromResult(CheckRuns);
}

internal sealed record DispatchInvocation(
    string Owner, string Repo, string WorkflowFile, string Ref,
    IReadOnlyDictionary<string, string> Inputs);

internal sealed class ImmediateDelayProvider : IDelayProvider
{
    public int CallCount { get; private set; }
    public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        CallCount++;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
