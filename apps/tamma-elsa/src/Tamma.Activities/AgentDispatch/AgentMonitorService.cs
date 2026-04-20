using Microsoft.Extensions.Logging;
using Tamma.Activities.AgentDispatch.Models;

namespace Tamma.Activities.AgentDispatch;

/// <summary>
/// Default <see cref="IAgentMonitorService"/> — polls the GitHub Actions
/// API for a dispatched workflow run until it reaches a terminal state
/// (story 19-3).
///
/// <para>Two phases:</para>
/// <list type="number">
///   <item>
///     <b>Discovery</b> — the dispatch API returns 204 with no run id, so
///     the service queries <c>/actions/runs</c> for the branch+event and
///     picks the most recent run created after the dispatch timestamp.
///     Poll every 5s up to <c>DiscoveryTimeoutSeconds</c>.
///   </item>
///   <item>
///     <b>Monitoring</b> — once the run id is known, poll
///     <c>/actions/runs/{id}</c> every <c>PollIntervalSeconds</c> until
///     <c>status == "completed"</c> or the overall timeout triggers.
///   </item>
/// </list>
///
/// <para>Webhook mode (AC-7) is intentionally deferred — see TODO below.
/// Poll-mode is sufficient for v1 and matches the scoping note in the
/// story.</para>
/// </summary>
public sealed class AgentMonitorService : IAgentMonitorService
{
    private readonly IGitHubActionsClient _client;
    private readonly ILogger<AgentMonitorService>? _logger;
    private readonly IDelayProvider _delay;

    // Discovery phase polls every 5s. Short enough to feel snappy;
    // long enough that we don't blow the rate-limit budget.
    private const int DiscoveryIntervalSeconds = 5;

    public AgentMonitorService(
        IGitHubActionsClient client,
        ILogger<AgentMonitorService>? logger = null,
        IDelayProvider? delay = null)
    {
        _client = client;
        _logger = logger;
        _delay = delay ?? new TaskDelayProvider();
    }

    public async Task<AgentMonitorResult> MonitorAsync(
        AgentExecutionRequest request,
        DateTime dispatchedAfter,
        AgentMonitorOptions options,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseRepository(request.Repository, out var owner, out var repo))
        {
            return NotFoundResult($"Invalid repository format '{request.Repository}'");
        }

        // ── Phase 1: discover the run ─────────────────────────────────
        var run = await DiscoverRunAsync(
            owner, repo, request.BranchName, dispatchedAfter,
            options.DiscoveryTimeoutSeconds, cancellationToken)
            .ConfigureAwait(false);

        if (run is null)
        {
            return NotFoundResult(
                $"Workflow run never appeared for branch '{request.BranchName}' within {options.DiscoveryTimeoutSeconds}s");
        }

        _logger?.LogInformation(
            "Discovered workflow run {RunId} for {Repository}/{Branch} (status={Status})",
            run.Id, request.Repository, request.BranchName, run.Status);

        // ── Phase 2: poll until terminal ──────────────────────────────
        var deadline = DateTime.UtcNow.AddMinutes(options.TimeoutMinutes);
        var consecutiveErrors = 0;
        var lastRun = run;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsTerminal(lastRun.Status))
            {
                var durationSeconds = (int)Math.Max(0,
                    (lastRun.UpdatedAt - lastRun.CreatedAt).TotalSeconds);
                return new AgentMonitorResult(
                    WorkflowRunId: lastRun.Id,
                    Status: lastRun.Status,
                    Conclusion: lastRun.Conclusion,
                    WorkflowRunUrl: lastRun.HtmlUrl,
                    DurationSeconds: durationSeconds,
                    ArtifactsUrl: lastRun.ArtifactsUrl);
            }

            try
            {
                await _delay.DelayAsync(TimeSpan.FromSeconds(options.PollIntervalSeconds), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }

            try
            {
                var next = await _client.GetWorkflowRunAsync(owner, repo, lastRun.Id, cancellationToken)
                    .ConfigureAwait(false);
                if (next is null)
                {
                    // Run disappeared (deleted?) — treat as a transient
                    // error so we don't immediately bail.
                    consecutiveErrors++;
                    _logger?.LogWarning(
                        "Workflow run {RunId} not found during poll (consecutive errors={Count})",
                        lastRun.Id, consecutiveErrors);
                }
                else
                {
                    consecutiveErrors = 0;
                    lastRun = next;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                consecutiveErrors++;
                _logger?.LogWarning(ex,
                    "GitHub API error polling run {RunId} (consecutive errors={Count})",
                    lastRun.Id, consecutiveErrors);
            }

            if (consecutiveErrors >= options.MaxConsecutiveErrors)
            {
                var finalDuration = (int)Math.Max(0,
                    (DateTime.UtcNow - lastRun.CreatedAt).TotalSeconds);
                return new AgentMonitorResult(
                    WorkflowRunId: lastRun.Id,
                    Status: "error",
                    Conclusion: "monitor_failed",
                    WorkflowRunUrl: lastRun.HtmlUrl,
                    DurationSeconds: finalDuration,
                    ArtifactsUrl: lastRun.ArtifactsUrl);
            }
        }

        // Timeout reached.
        var timeoutDuration = (int)Math.Max(0, (DateTime.UtcNow - lastRun.CreatedAt).TotalSeconds);
        _logger?.LogWarning(
            "Monitor timed out after {Minutes}m for workflow run {RunId}",
            options.TimeoutMinutes, lastRun.Id);
        return new AgentMonitorResult(
            WorkflowRunId: lastRun.Id,
            Status: "completed",
            Conclusion: "timed_out",
            WorkflowRunUrl: lastRun.HtmlUrl,
            DurationSeconds: timeoutDuration,
            ArtifactsUrl: lastRun.ArtifactsUrl);

        // TODO: Webhook-mode (story 19-3 AC-7). When the GitHub webhook
        // handler receives workflow_run.completed, it should resume an
        // ELSA bookmark keyed by the session id / run id, eliminating
        // the poll loop. Requires wiring on two sides: the webhook
        // handler must map workflow_run → bookmark, and this service
        // must create+await the bookmark. Defer until the poll path is
        // validated in production.
    }

    private async Task<WorkflowRunSummary?> DiscoverRunAsync(
        string owner, string repo, string branch, DateTime after,
        int timeoutSeconds, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var runs = await _client.ListWorkflowRunsAsync(
                    owner, repo, branch, after, perPage: 5, ct)
                    .ConfigureAwait(false);
                if (runs.Count > 0)
                {
                    // API returns sorted desc-by-created; take the first.
                    return runs[0];
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex,
                    "Discovery poll failed (will retry): {Repository}/{Branch}",
                    $"{owner}/{repo}", branch);
            }

            try
            {
                await _delay.DelayAsync(TimeSpan.FromSeconds(DiscoveryIntervalSeconds), ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        return null;
    }

    private static AgentMonitorResult NotFoundResult(string errorMessage) =>
        new(
            WorkflowRunId: 0,
            Status: "error",
            Conclusion: "not_found",
            WorkflowRunUrl: string.Empty,
            DurationSeconds: 0,
            ArtifactsUrl: string.Empty);

    private static bool IsTerminal(string status) =>
        string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseRepository(string? repository, out string owner, out string repo)
    {
        owner = string.Empty;
        repo = string.Empty;
        if (string.IsNullOrWhiteSpace(repository)) return false;
        var parts = repository.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return false;
        owner = parts[0];
        repo = parts[1];
        return !string.IsNullOrEmpty(owner) && !string.IsNullOrEmpty(repo);
    }
}

/// <summary>
/// Indirection over <c>Task.Delay</c> so tests can skip real time.
/// </summary>
public interface IDelayProvider
{
    Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken);
}

internal sealed class TaskDelayProvider : IDelayProvider
{
    public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
        => Task.Delay(duration, cancellationToken);
}
