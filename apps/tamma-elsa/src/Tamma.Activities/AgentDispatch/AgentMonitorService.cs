using Microsoft.Extensions.Logging;
using Tamma.Activities.AgentDispatch.Models;

namespace Tamma.Activities.AgentDispatch;

/// <summary>
/// Default <see cref="IAgentMonitorService"/> — observes a dispatched
/// GitHub Actions workflow run until it reaches a terminal state
/// (story 19-3).
///
/// <para>Two modes:</para>
/// <list type="bullet">
///   <item>
///     <b>Poll</b> (default, AC 1-6 / 8) — the dispatch API returns 204 with
///     no run id, so the service queries <c>/actions/runs</c> for the
///     branch+event and picks the most recent run created after the
///     dispatch timestamp. Then polls <c>/actions/runs/{id}</c> every
///     <c>PollIntervalSeconds</c> until <c>status == "completed"</c> or
///     the overall timeout triggers.
///   </item>
///   <item>
///     <b>Webhook</b> (AC-7) — the service registers a wait with
///     <see cref="IWebhookSignalRegistry"/> keyed on the repository +
///     branch + session id. When the GitHub webhook receiver observes a
///     matching <c>workflow_run.completed</c> payload it publishes the
///     signal, the monitor wakes, and returns immediately. No polling
///     against GitHub at all.
///   </item>
///   <item>
///     <b>Auto</b> — webhook when the registry is wired, with a safety
///     window that falls back to poll if the webhook doesn't fire inside
///     <c>TimeoutMinutes * WebhookSafetyWindowMultiplier</c>. This is the
///     production-recommended setting for SaaS deployments.
///   </item>
/// </list>
/// </summary>
public sealed class AgentMonitorService : IAgentMonitorService
{
    private readonly IGitHubActionsClient _client;
    private readonly ILogger<AgentMonitorService>? _logger;
    private readonly IDelayProvider _delay;
    private readonly IWebhookSignalRegistry? _signals;

    // Discovery phase polls every 5s. Short enough to feel snappy;
    // long enough that we don't blow the rate-limit budget.
    private const int DiscoveryIntervalSeconds = 5;

    public AgentMonitorService(
        IGitHubActionsClient client,
        ILogger<AgentMonitorService>? logger = null,
        IDelayProvider? delay = null,
        IWebhookSignalRegistry? signals = null)
    {
        _client = client;
        _logger = logger;
        _delay = delay ?? new TaskDelayProvider();
        _signals = signals;
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

        // Resolve the effective mode. Auto falls back to Poll when no
        // signal registry is wired (e.g. the ElsaServer composition root
        // or a test harness without the webhook plumbing).
        var effectiveMode = options.Mode;
        if (effectiveMode == AgentMonitorMode.Auto && _signals is null)
        {
            _logger?.LogDebug(
                "AgentMonitor mode=Auto but IWebhookSignalRegistry not registered — falling back to Poll for {Repository}/{Branch}",
                request.Repository, request.BranchName);
            effectiveMode = AgentMonitorMode.Poll;
        }
        if (effectiveMode == AgentMonitorMode.Webhook && _signals is null)
        {
            _logger?.LogError(
                "AgentMonitor mode=Webhook but IWebhookSignalRegistry not registered — returning monitor_failed");
            return new AgentMonitorResult(
                WorkflowRunId: 0,
                Status: "error",
                Conclusion: "monitor_failed",
                WorkflowRunUrl: string.Empty,
                DurationSeconds: 0,
                ArtifactsUrl: string.Empty);
        }

        if (effectiveMode == AgentMonitorMode.Poll)
        {
            return await PollAsync(owner, repo, request, dispatchedAfter, options, cancellationToken)
                .ConfigureAwait(false);
        }

        // Webhook / Auto path.
        var webhookResult = await WaitForWebhookAsync(
            owner, repo, request, options, cancellationToken).ConfigureAwait(false);

        if (webhookResult is not null)
        {
            return webhookResult;
        }

        // Safety-window expired. Auto falls back to polling so the workflow
        // doesn't hang on a missed webhook delivery. Webhook (strict) bails
        // out with monitor_failed.
        if (effectiveMode == AgentMonitorMode.Webhook)
        {
            _logger?.LogWarning(
                "Webhook-mode safety window expired for {Repository}/{Branch} — webhook never arrived",
                request.Repository, request.BranchName);
            return new AgentMonitorResult(
                WorkflowRunId: 0,
                Status: "error",
                Conclusion: "monitor_failed",
                WorkflowRunUrl: string.Empty,
                DurationSeconds: 0,
                ArtifactsUrl: string.Empty);
        }

        _logger?.LogWarning(
            "Webhook-mode (Auto) safety window expired for {Repository}/{Branch} — falling back to poll",
            request.Repository, request.BranchName);
        return await PollAsync(owner, repo, request, dispatchedAfter, options, cancellationToken)
            .ConfigureAwait(false);
    }

    // ── Webhook path ──────────────────────────────────────────────────────

    private async Task<AgentMonitorResult?> WaitForWebhookAsync(
        string owner, string repo,
        AgentExecutionRequest request,
        AgentMonitorOptions options,
        CancellationToken cancellationToken)
    {
        if (_signals is null) return null; // unreachable; mode-check above.

        // Resolve the installation id so the wait key is scoped to this
        // tenant's GitHub App installation (review-session 2026-04-20
        // finding 5). Without the scope, two tenants on the same repo +
        // branch can cross-wake each other through the branch alias.
        // Resolution failure (e.g. dev mode with no GitHub App) leaves
        // installationId as null — the registry falls back to the
        // unscoped alias form, which keeps legacy back-compat but emits
        // a warning on the publish side.
        long? installationId = null;
        try
        {
            installationId = await _client.ResolveInstallationIdAsync(owner, repo, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex,
                "ResolveInstallationIdAsync failed for {Owner}/{Repo} — falling back to unscoped alias",
                owner, repo);
        }

        if (installationId is null)
        {
            _logger?.LogWarning(
                "AgentMonitor webhook wait: installation id not resolved for {Owner}/{Repo} — " +
                "falling back to unscoped key (cross-tenant risk, dev-mode only)",
                owner, repo);
        }

        // Pre-discovery bookmark: we don't have the run id yet, so the key
        // is (repo + branch + sessionId). The registry also registers an
        // alias under the branch-key so both the webhook side (which
        // *does* know the run id once it arrives) and the discovery side
        // can match.
        var key = new AgentWebhookSignalKey(
            Repository: $"{owner}/{repo}",
            HeadBranch: request.BranchName,
            SessionId: request.SessionId,
            WorkflowRunId: null,
            InstallationId: installationId);

        // Clamp to at least 1 second so an egregious 0-multiplier
        // misconfig doesn't immediately trip the fallback. The
        // AgentMonitorOptions default (35 * 1.5 = ~52 minutes) is the
        // production path; small values are test-mode.
        var safetyWindowMinutes = options.TimeoutMinutes * options.WebhookSafetyWindowMultiplier;
        var safetyWindow = safetyWindowMinutes <= 0
            ? TimeSpan.FromSeconds(1)
            : TimeSpan.FromMinutes(safetyWindowMinutes);

        _logger?.LogInformation(
            "AgentMonitor webhook wait: key={Key} safety={SafetyMinutes}m",
            key.ToKey(), safetyWindow.TotalMinutes);

        var signal = await _signals.WaitForSignalAsync(key, safetyWindow, cancellationToken)
            .ConfigureAwait(false);

        if (signal is null)
        {
            return null; // caller decides how to handle (Auto falls back, Webhook fails).
        }

        var durationSeconds = (int)Math.Max(0, (signal.UpdatedAt - signal.CreatedAt).TotalSeconds);
        return new AgentMonitorResult(
            WorkflowRunId: signal.WorkflowRunId,
            Status: signal.Status,
            Conclusion: signal.Conclusion,
            WorkflowRunUrl: signal.WorkflowRunUrl,
            DurationSeconds: durationSeconds,
            ArtifactsUrl: signal.ArtifactsUrl);
    }

    // ── Poll path (unchanged from story 19-3 v1) ──────────────────────────

    private async Task<AgentMonitorResult> PollAsync(
        string owner, string repo,
        AgentExecutionRequest request,
        DateTime dispatchedAfter,
        AgentMonitorOptions options,
        CancellationToken cancellationToken)
    {
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
