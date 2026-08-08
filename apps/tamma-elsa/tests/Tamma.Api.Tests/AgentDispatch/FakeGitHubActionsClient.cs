using Moq;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;

namespace Tamma.Api.Tests.AgentDispatch;

/// <summary>
/// Epic 31 P3 (seam 6) — in-memory <see cref="IGitPlatformActionsClient"/> +
/// driver for the server-side aggregator + mediation tests. Replaces the
/// pre-swap <c>FakeGitHubActionsClient</c> (the GitHub-only seam is gone):
/// tests seed platform-result state, run the mediation/aggregation, and assert
/// outputs + which calls fired — without any platform-specific client.
/// </summary>
internal sealed class FakePlatformActionsClient : IGitPlatformActionsClient
{
    public static WorkflowRun Run(
        string id = "55", string status = "completed", string? conclusion = "success",
        string url = "https://ci/run", DateTimeOffset? started = null) =>
        new(id, status, conclusion, url, started ?? DateTimeOffset.UtcNow,
            conclusion is null ? null : DateTimeOffset.UtcNow, null);

    public Func<string, string, WorkflowDispatchRequest, PlatformResult<WorkflowRun>> OnDispatch { get; set; } =
        (_, _, _) => PlatformResult<WorkflowRun>.FromOk(Run(id: "1", status: "queued", conclusion: null, url: "https://ci/run/1"));

    public List<(string Owner, string Repo, WorkflowDispatchRequest Request)> DispatchCalls { get; } = new();

    public Dictionary<string, WorkflowRun> RunsById { get; } = new();
    public IReadOnlyList<WorkflowRun> DefaultListRuns { get; set; } = Array.Empty<WorkflowRun>();

    public Func<string, PlatformResult<IReadOnlyList<Artifact>>> OnListArtifacts { get; set; } =
        _ => PlatformResult<IReadOnlyList<Artifact>>.FromOk(Array.Empty<Artifact>());

    public Dictionary<string, byte[]> ArtifactBytes { get; } = new();

    public int GetRunCalls { get; private set; }
    public int ListRunsCalls { get; private set; }

    public Task<PlatformResult<WorkflowRun>> DispatchWorkflowAsync(
        string owner, string repoName, WorkflowDispatchRequest request, CancellationToken ct = default)
    {
        DispatchCalls.Add((owner, repoName, request));
        return Task.FromResult(OnDispatch(owner, repoName, request));
    }

    public Task<PlatformResult<WorkflowRun>> GetRunStatusAsync(
        string owner, string repoName, string runId, CancellationToken ct = default)
    {
        GetRunCalls++;
        return Task.FromResult(RunsById.TryGetValue(runId, out var run)
            ? PlatformResult<WorkflowRun>.FromOk(run)
            : PlatformResult<WorkflowRun>.FromError(new PlatformError.NotFound()));
    }

    public Task<PlatformResult<IReadOnlyList<WorkflowRun>>> ListRunsAsync(
        string owner, string repoName, ListWorkflowRunsRequest request, CancellationToken ct = default)
    {
        ListRunsCalls++;
        return Task.FromResult(PlatformResult<IReadOnlyList<WorkflowRun>>.FromOk(DefaultListRuns));
    }

    public Task<PlatformResult<IReadOnlyList<WorkflowJob>>> ListRunJobsAsync(
        string owner, string repoName, string runId, CancellationToken ct = default) =>
        Task.FromResult(PlatformResult<IReadOnlyList<WorkflowJob>>.FromOk(
            (IReadOnlyList<WorkflowJob>)Array.Empty<WorkflowJob>()));

    public Task<PlatformResult<IReadOnlyList<Artifact>>> ListRunArtifactsAsync(
        string owner, string repoName, string runId, CancellationToken ct = default) =>
        Task.FromResult(OnListArtifacts(runId));

    public Task<PlatformResult<Stream>> DownloadArtifactAsync(
        string owner, string repoName, string artifactId, CancellationToken ct = default) =>
        Task.FromResult(ArtifactBytes.TryGetValue(artifactId, out var bytes)
            ? PlatformResult<Stream>.FromOk(new MemoryStream(bytes))
            : PlatformResult<Stream>.FromError(new PlatformError.NotFound()));

    public Task<PlatformResult<bool>> CancelRunAsync(
        string owner, string repoName, string runId, CancellationToken ct = default) =>
        Task.FromResult(PlatformResult<bool>.FromOk(true));
}

/// <summary>Composable fake driver over a (mockable) client + the fake actions.</summary>
internal sealed class FakePlatformDriver : IGitPlatformDriver
{
    public FakePlatformDriver(IGitPlatformClient? client = null, IGitPlatformActionsClient? actions = null)
    {
        Client = client ?? Mock.Of<IGitPlatformClient>();
        Actions = actions;
    }

    public PlatformKind Kind { get; init; } = PlatformKind.GitHub;
    public IGitPlatformClient Client { get; }
    public IGitPlatformActionsClient? Actions { get; }
    public IReadOnlySet<PlatformCapability> Capabilities { get; init; } =
        new HashSet<PlatformCapability> { PlatformCapability.Actions };
}
