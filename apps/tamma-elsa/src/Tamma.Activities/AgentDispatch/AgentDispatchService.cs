using Microsoft.Extensions.Logging;
using Tamma.Activities.AgentDispatch.Models;

namespace Tamma.Activities.AgentDispatch;

/// <summary>
/// Default <see cref="IAgentDispatchService"/> — wraps
/// <see cref="IGitHubActionsClient"/> with the story 19-2 flow:
///
/// 1. Parse <c>owner/repo</c>.
/// 2. Verify the workflow file exists (AC-8).
/// 3. POST workflow_dispatch (AC-3).
/// 4. Map API errors to actionable messages (AC-5).
///
/// <para>Rate-limit / 5xx retries are handled here with a simple
/// exponential backoff (1s, 2s, 4s). A 404 on the workflow file or a
/// 403 permission denial is reported verbatim — those are operator
/// errors, not transient issues.</para>
/// </summary>
public sealed class AgentDispatchService : IAgentDispatchService
{
    private readonly IGitHubActionsClient _client;
    private readonly ILogger<AgentDispatchService>? _logger;

    // Retry budget for 429/5xx — aggregate wait ≤ 7s, fits within the
    // <2s-latency target for the happy path while giving one bounce for
    // transient rate-limit blips.
    private const int MaxRetries = 3;
    private static readonly int[] RetryDelaysMs = { 1000, 2000, 4000 };

    public AgentDispatchService(
        IGitHubActionsClient client,
        ILogger<AgentDispatchService>? logger = null)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<AgentDispatchResult> DispatchAsync(
        AgentExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseRepository(request.Repository, out var owner, out var repo))
        {
            return new AgentDispatchResult(
                Success: false,
                WorkflowRunUrl: null,
                ErrorMessage: $"Invalid repository format '{request.Repository}' (expected 'owner/repo')",
                DispatchedAt: DateTime.UtcNow);
        }

        var workflowFile = string.IsNullOrWhiteSpace(request.WorkflowFileName)
            ? "tamma-agent.yml"
            : request.WorkflowFileName;

        // AC-8: validate workflow file presence before dispatching.
        var check = await _client.CheckWorkflowFileAsync(owner, repo, workflowFile, cancellationToken)
            .ConfigureAwait(false);
        if (check.NotConfigured)
        {
            return new AgentDispatchResult(
                Success: false,
                WorkflowRunUrl: null,
                ErrorMessage: "GitHub App not configured on the Tamma server — cannot dispatch agent workflow.",
                DispatchedAt: DateTime.UtcNow);
        }
        if (!check.Exists)
        {
            return new AgentDispatchResult(
                Success: false,
                WorkflowRunUrl: null,
                ErrorMessage:
                    $"Workflow file '{workflowFile}' not found in {owner}/{repo}. " +
                    "Add the Tamma agent workflow template to .github/workflows/.",
                DispatchedAt: DateTime.UtcNow);
        }

        var inputs = BuildDispatchInputs(request);
        var dispatchedAt = DateTime.UtcNow;

        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new AgentDispatchResult(
                    Success: false,
                    WorkflowRunUrl: null,
                    ErrorMessage: "Dispatch cancelled by caller",
                    DispatchedAt: dispatchedAt);
            }

            var apiResult = await _client.DispatchWorkflowAsync(
                owner, repo, workflowFile, request.BranchName, inputs, cancellationToken)
                .ConfigureAwait(false);

            if (apiResult.NotConfigured)
            {
                return new AgentDispatchResult(
                    Success: false,
                    WorkflowRunUrl: null,
                    ErrorMessage: "GitHub App not configured — dispatch rejected.",
                    DispatchedAt: dispatchedAt);
            }

            if (apiResult.HttpStatusCode == 204)
            {
                // AC-4 — dispatch API returns 204 with no workflow_run URL.
                // Story 19-3 resolves the run URL by polling.
                _logger?.LogInformation(
                    "Dispatched agent workflow {Workflow} to {Repository} branch={Branch} session={SessionId}",
                    workflowFile, request.Repository, request.BranchName, request.SessionId);
                return new AgentDispatchResult(
                    Success: true,
                    WorkflowRunUrl: null,
                    ErrorMessage: null,
                    DispatchedAt: dispatchedAt);
            }

            if (apiResult.HttpStatusCode == 404)
            {
                return new AgentDispatchResult(
                    Success: false,
                    WorkflowRunUrl: null,
                    ErrorMessage:
                        $"GitHub returned 404 for dispatch — branch '{request.BranchName}' or workflow '{workflowFile}' may not exist.",
                    DispatchedAt: dispatchedAt);
            }

            if (apiResult.HttpStatusCode == 403)
            {
                return new AgentDispatchResult(
                    Success: false,
                    WorkflowRunUrl: null,
                    ErrorMessage:
                        "GitHub returned 403 for dispatch — Tamma App installation may be missing the 'actions: write' permission.",
                    DispatchedAt: dispatchedAt);
            }

            if (IsRetryable(apiResult.HttpStatusCode) && attempt < MaxRetries)
            {
                var delay = RetryDelaysMs[attempt];
                _logger?.LogWarning(
                    "Dispatch attempt {Attempt} returned {Status} ({Reason}); retrying in {DelayMs}ms",
                    attempt + 1, apiResult.HttpStatusCode, apiResult.ErrorReason, delay);
                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return new AgentDispatchResult(
                        Success: false,
                        WorkflowRunUrl: null,
                        ErrorMessage: "Dispatch cancelled during backoff",
                        DispatchedAt: dispatchedAt);
                }
                continue;
            }

            return new AgentDispatchResult(
                Success: false,
                WorkflowRunUrl: null,
                ErrorMessage:
                    $"GitHub dispatch failed with HTTP {apiResult.HttpStatusCode}: {apiResult.ErrorReason ?? "(no body)"}",
                DispatchedAt: dispatchedAt);
        }

        return new AgentDispatchResult(
            Success: false,
            WorkflowRunUrl: null,
            ErrorMessage: "Dispatch failed after retries",
            DispatchedAt: dispatchedAt);
    }

    private static IReadOnlyDictionary<string, string> BuildDispatchInputs(AgentExecutionRequest request)
    {
        // GitHub workflow_dispatch inputs are string-only and capped at
        // 10 entries / 65535 chars each. We keep the set minimal; larger
        // plans can be passed by reference (uploaded as a gist first) by
        // the workflow template — that's outside this dispatch call.
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["issue_number"] = request.IssueNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["task"] = request.Task ?? "implement",
            ["plan_json"] = request.PlanJson ?? "{}",
            ["branch_name"] = request.BranchName,
            ["tamma_session_id"] = request.SessionId,
            ["agent_provider"] = request.AgentProvider ?? "claude-code",
            ["agent_config_json"] = request.AgentConfigJson ?? "{}"
        };
    }

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

    private static bool IsRetryable(int statusCode)
    {
        return statusCode == 429
            || statusCode == 502
            || statusCode == 503
            || statusCode == 504
            || statusCode == 0; // connection failure
    }
}
