using Microsoft.Extensions.Logging;
using Tamma.Activities.AgentDispatch.Models;
using Tamma.Activities.LlmCall;
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.AgentDispatch;

/// <summary>
/// Story 38-2 (Class-C cutover) — the <see cref="IAgentMonitorService"/> KEEPS its
/// discover→poll loop (timeout, backoff, consecutive-error cap, webhook/Auto/Poll
/// modes) ENTIRELY engine-side, but no longer injects the credential-holding
/// <see cref="IGitHubActionsClient"/>. Each read that used to hit GitHub directly
/// now hits <c>Tamma.Api</c> over the wire via <see cref="TammaApiClient"/>
/// (single-shot status GETs), so the ~35-minute loop never holds an HTTP request
/// open on the API and the engine holds no Actions token.
///
/// <para>The INBOUND <c>workflow_run.completed</c> webhook + the in-process
/// <see cref="IWebhookSignalRegistry"/> signalling are unchanged (design §5.3);
/// only the installation-id lookup used to scope the wait key is now mediated.</para>
/// </summary>
public sealed class AgentMonitorService : IAgentMonitorService
{
    private readonly TammaApiClient _api;
    private readonly ILogger<AgentMonitorService>? _logger;
    private readonly IDelayProvider _delay;
    private readonly IWebhookSignalRegistry? _signals;

    // Discovery phase polls every 5s.
    private const int DiscoveryIntervalSeconds = 5;

    public AgentMonitorService(
        TammaApiClient api,
        ILogger<AgentMonitorService>? logger = null,
        IDelayProvider? delay = null,
        IWebhookSignalRegistry? signals = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
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
        if (!TryParseRepository(request.Repository))
        {
            return NotFoundResult($"Invalid repository format '{request.Repository}'");
        }

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
            return new AgentMonitorResult(0, "error", "monitor_failed", string.Empty, 0, string.Empty);
        }

        if (effectiveMode == AgentMonitorMode.Poll)
        {
            return await PollAsync(request, dispatchedAfter, options, cancellationToken).ConfigureAwait(false);
        }

        // Webhook / Auto path.
        var webhookResult = await WaitForWebhookAsync(request, options, cancellationToken).ConfigureAwait(false);
        if (webhookResult is not null)
        {
            return webhookResult;
        }

        if (effectiveMode == AgentMonitorMode.Webhook)
        {
            _logger?.LogWarning(
                "Webhook-mode safety window expired for {Repository}/{Branch} — webhook never arrived",
                request.Repository, request.BranchName);
            return new AgentMonitorResult(0, "error", "monitor_failed", string.Empty, 0, string.Empty);
        }

        _logger?.LogWarning(
            "Webhook-mode (Auto) safety window expired for {Repository}/{Branch} — falling back to poll",
            request.Repository, request.BranchName);
        return await PollAsync(request, dispatchedAfter, options, cancellationToken).ConfigureAwait(false);
    }

    // ── Webhook path ──────────────────────────────────────────────────────

    private async Task<AgentMonitorResult?> WaitForWebhookAsync(
        AgentExecutionRequest request,
        AgentMonitorOptions options,
        CancellationToken cancellationToken)
    {
        if (_signals is null) return null; // unreachable; mode-check above.

        var tenantId = request.TenantId?.ToString();

        // Resolve the installation id (mediated) so the wait key is scoped to this
        // tenant's GitHub App installation (finding 5). Resolution failure leaves it
        // null — the registry falls back to the unscoped alias with a warning.
        long? installationId = null;
        try
        {
            var resolved = await _api.ResolveAgentInstallationIdAsync(request.Repository, tenantId, cancellationToken)
                .ConfigureAwait(false);
            installationId = resolved?.InstallationId;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex,
                "ResolveAgentInstallationIdAsync failed for {Repo} — falling back to unscoped alias", request.Repository);
        }

        if (installationId is null)
        {
            _logger?.LogWarning(
                "AgentMonitor webhook wait: installation id not resolved for {Repo} — " +
                "falling back to unscoped key (cross-tenant risk, dev-mode only)", request.Repository);
        }

        var key = new AgentWebhookSignalKey(
            Repository: request.Repository,
            HeadBranch: request.BranchName,
            SessionId: request.SessionId,
            WorkflowRunId: null,
            InstallationId: installationId);

        var safetyWindowMinutes = options.TimeoutMinutes * options.WebhookSafetyWindowMultiplier;
        var safetyWindow = safetyWindowMinutes <= 0
            ? TimeSpan.FromSeconds(1)
            : TimeSpan.FromMinutes(safetyWindowMinutes);

        _logger?.LogInformation(
            "AgentMonitor webhook wait: key={Key} safety={SafetyMinutes}m", key.ToKey(), safetyWindow.TotalMinutes);

        var signal = await _signals.WaitForSignalAsync(key, safetyWindow, cancellationToken).ConfigureAwait(false);
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

    // ── Poll path (unchanged loop; reads are now mediated) ─────────────────

    private async Task<AgentMonitorResult> PollAsync(
        AgentExecutionRequest request,
        DateTime dispatchedAfter,
        AgentMonitorOptions options,
        CancellationToken cancellationToken)
    {
        var tenantId = request.TenantId?.ToString();

        // ── Phase 1: discover the run ─────────────────────────────────
        var run = await DiscoverRunAsync(
            request, dispatchedAfter, options.DiscoveryTimeoutSeconds, tenantId, cancellationToken).ConfigureAwait(false);

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
                var durationSeconds = (int)Math.Max(0, (lastRun.UpdatedAt - lastRun.CreatedAt).TotalSeconds);
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
                var response = await _api.GetAgentRunAsync(request.Repository, lastRun.Id, request.SessionId, tenantId, cancellationToken)
                    .ConfigureAwait(false);
                var next = ToSummary(response, lastRun.HeadBranch);
                if (next is null)
                {
                    // Run not visible / mediation transient — treat as a transient error.
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
                    "Mediated poll error for run {RunId} (consecutive errors={Count})", lastRun.Id, consecutiveErrors);
            }

            if (consecutiveErrors >= options.MaxConsecutiveErrors)
            {
                var finalDuration = (int)Math.Max(0, (DateTime.UtcNow - lastRun.CreatedAt).TotalSeconds);
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
            "Monitor timed out after {Minutes}m for workflow run {RunId}", options.TimeoutMinutes, lastRun.Id);
        return new AgentMonitorResult(
            WorkflowRunId: lastRun.Id,
            Status: "completed",
            Conclusion: "timed_out",
            WorkflowRunUrl: lastRun.HtmlUrl,
            DurationSeconds: timeoutDuration,
            ArtifactsUrl: lastRun.ArtifactsUrl);
    }

    private async Task<WorkflowRunSummary?> DiscoverRunAsync(
        AgentExecutionRequest request, DateTime after, int timeoutSeconds, string? tenantId, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var response = await _api.DiscoverAgentRunAsync(
                    request.Repository, request.BranchName, after, request.SessionId, tenantId, ct).ConfigureAwait(false);
                var run = ToSummary(response, request.BranchName);
                if (run is not null)
                {
                    return run;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Discovery poll failed (will retry): {Repository}/{Branch}", request.Repository, request.BranchName);
            }

            try
            {
                await _delay.DelayAsync(TimeSpan.FromSeconds(DiscoveryIntervalSeconds), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        return null;
    }

    /// <summary>
    /// Story 38-2 (AC5) — pure map of the mediated status response → a
    /// <see cref="WorkflowRunSummary"/> the loop operates on. Returns null when the
    /// mediation failed (guard/platform/transport) OR the run isn't visible yet
    /// (<c>found:false</c>) — the loop treats both as "keep waiting / transient",
    /// exactly as the pre-cutover null-run path did.
    /// </summary>
    public static WorkflowRunSummary? ToSummary(AgentRunStatusApiResponse? response, string fallbackBranch)
    {
        if (response is null || !response.Success || !response.Found || response.RunId is not long runId)
        {
            return null;
        }

        return new WorkflowRunSummary(
            Id: runId,
            Status: response.Status ?? string.Empty,
            Conclusion: response.Conclusion ?? string.Empty,
            HtmlUrl: response.WorkflowRunUrl ?? string.Empty,
            CreatedAt: response.CreatedAt ?? default,
            UpdatedAt: response.UpdatedAt ?? default,
            HeadBranch: string.IsNullOrEmpty(response.HeadBranch) ? fallbackBranch : response.HeadBranch!,
            Event: "workflow_dispatch",
            ArtifactsUrl: response.ArtifactsUrl ?? string.Empty);
    }

    private static AgentMonitorResult NotFoundResult(string errorMessage) =>
        new(WorkflowRunId: 0, Status: "error", Conclusion: "not_found", WorkflowRunUrl: string.Empty, DurationSeconds: 0, ArtifactsUrl: string.Empty);

    private static bool IsTerminal(string status) =>
        string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseRepository(string? repository)
    {
        if (string.IsNullOrWhiteSpace(repository)) return false;
        var parts = repository.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && !string.IsNullOrEmpty(parts[0]) && !string.IsNullOrEmpty(parts[1]);
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
