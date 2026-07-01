using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tamma.Activities.AgentDispatch;
using Tamma.Api.Services.Git;
using Tamma.Core.Interfaces;
using Tamma.Core.Logging;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.AgentDispatch;

/// <summary>
/// Story 38-2 — composes the agent-dispatch mediation sequence entirely inside
/// <c>Tamma.Api</c>: cross-tenant guard (reused from Story 38-1) → platform call
/// via <see cref="IGitHubActionsClient"/> (<c>OctokitGitHubActionsClient</c>,
/// which mints the per-repo GitHub App INSTALLATION token internally) → the
/// collect aggregation (<see cref="IActionsResultAggregator"/>) → exactly-one
/// terminal DCB event.
///
/// <para>Unlike the git story there is no BYOK→platform token resolver: the
/// Actions token is inherently the tenant's App installation, resolved by repo
/// inside the Octokit client — the guard already asserts that installation
/// belongs to the acting tenant, so <c>credentialSource</c> is always the
/// constant <c>installation</c>. The token lives only inside the Octokit client
/// for the one call; it is NEVER logged, returned, or written to the audit event
/// (only the <c>credentialSource</c> LABEL is surfaced).</para>
/// </summary>
public sealed class AgentDispatchMediationService : IAgentDispatchMediationService
{
    private readonly IGitRepoAuthorizer _authorizer;
    private readonly IGitHubActionsClient _actions;
    private readonly IActionsResultAggregator _aggregator;
    private readonly IEventRepository _events;
    private readonly ILogger<AgentDispatchMediationService> _logger;

    // Retry budget for 429/5xx dispatch — aggregate wait ≤ 7s (1s, 2s, 4s),
    // ported verbatim from the former engine-side AgentDispatchService so the
    // dispatch semantics are unchanged after the cutover.
    private const int MaxRetries = 3;
    private static readonly int[] RetryDelaysMs = { 1000, 2000, 4000 };
    private const string DefaultWorkflowFile = "tamma-agent.yml";

    public AgentDispatchMediationService(
        IGitRepoAuthorizer authorizer,
        IGitHubActionsClient actions,
        IActionsResultAggregator aggregator,
        IEventRepository events,
        ILogger<AgentDispatchMediationService> logger)
    {
        _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _aggregator = aggregator ?? throw new ArgumentNullException(nameof(aggregator));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // ===================================================================
    // Trigger run (the WRITE — triggers Actions code execution)
    // ===================================================================

    public Task<AgentDispatchRunResult> TriggerRunAsync(
        Guid? tenantId, string repo, DispatchAgentRunRequest body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        return ExecuteGuardedAsync(
            () => TriggerRunCoreAsync(tenantId, repo, body, ct),
            tenantId, repo, AgentDispatchEventTypes.RunTriggerOperation, AgentDispatchEventTypes.RunTriggeredFailed,
            body.CorrelationId, runId: null,
            fail: (code, reason) => new AgentDispatchRunResult
            {
                Success = false,
                FailureCode = code, FailureReason = reason, CorrelationId = body.CorrelationId,
                DispatchedAt = DateTime.UtcNow,
            }, ct);
    }

    private async Task<AgentDispatchRunResult> TriggerRunCoreAsync(
        Guid? tenantId, string repo, DispatchAgentRunRequest body, CancellationToken ct)
    {
        const string op = AgentDispatchEventTypes.RunTriggerOperation;
        var (owner, name, parsed) = ParseRepo(repo);
        var dispatchedAt = DateTime.UtcNow;

        var gate = await GuardOrDenyRunAsync(tenantId, repo, op, AgentDispatchEventTypes.RunTriggeredFailed,
            body.CorrelationId, null,
            reason => new AgentDispatchRunResult
            {
                Success = false, DispatchedAt = dispatchedAt,
                FailureCode = AgentDispatchFailureCodes.RepoNotAuthorized, FailureReason = reason, CorrelationId = body.CorrelationId,
            }, ct).ConfigureAwait(false);
        if (gate is not null) return gate;

        if (!parsed)
            return await DispatchFailAsync(tenantId, repo, body.CorrelationId, dispatchedAt,
                AgentDispatchFailureCodes.PlatformError, $"Invalid repository format '{repo}' (expected 'owner/repo')", null, ct)
                .ConfigureAwait(false);

        var workflowFile = string.IsNullOrWhiteSpace(body.WorkflowFileName) ? DefaultWorkflowFile : body.WorkflowFileName;

        // AC-8: validate workflow file presence before dispatching.
        var check = await _actions.CheckWorkflowFileAsync(owner, name, workflowFile, ct).ConfigureAwait(false);
        if (check.NotConfigured)
            return await DispatchFailAsync(tenantId, repo, body.CorrelationId, dispatchedAt,
                AgentDispatchFailureCodes.ActionsNotConfigured,
                "GitHub App not configured on the Tamma server — cannot dispatch agent workflow.", null, ct)
                .ConfigureAwait(false);
        if (!check.Exists)
            return await DispatchFailAsync(tenantId, repo, body.CorrelationId, dispatchedAt,
                AgentDispatchFailureCodes.WorkflowNotFound,
                $"Workflow file '{workflowFile}' not found in {owner}/{name}. Add the Tamma agent workflow template to .github/workflows/.",
                404, ct)
                .ConfigureAwait(false);

        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var api = await _actions.DispatchWorkflowAsync(owner, name, workflowFile, body.Ref, body.Inputs, ct)
                .ConfigureAwait(false);

            if (api.NotConfigured)
                return await DispatchFailAsync(tenantId, repo, body.CorrelationId, dispatchedAt,
                    AgentDispatchFailureCodes.ActionsNotConfigured, "GitHub App not configured — dispatch rejected.", null, ct)
                    .ConfigureAwait(false);

            if (api.HttpStatusCode == 204)
            {
                var ok = new AgentDispatchRunResult
                {
                    Success = true,
                    CredentialSource = AgentDispatchCredentialSources.Installation,
                    WorkflowRunUrl = null, // dispatch API returns 204 with no run URL — the monitor discovers it.
                    DispatchedAt = dispatchedAt,
                    CorrelationId = body.CorrelationId,
                };
                await EmitAsync(AgentDispatchEventTypes.RunTriggeredSuccess, op, tenantId, repo, body.CorrelationId,
                    AgentDispatchCredentialSources.Installation, runId: null, failureCode: null,
                    new { workflowFile, @ref = body.Ref }, ct).ConfigureAwait(false);
                return ok;
            }

            if (api.HttpStatusCode == 404)
                return await DispatchFailAsync(tenantId, repo, body.CorrelationId, dispatchedAt,
                    AgentDispatchFailureCodes.DispatchRejected,
                    $"GitHub returned 404 for dispatch — branch '{body.Ref}' or workflow '{workflowFile}' may not exist.", 404, ct)
                    .ConfigureAwait(false);

            if (api.HttpStatusCode == 403)
                return await DispatchFailAsync(tenantId, repo, body.CorrelationId, dispatchedAt,
                    AgentDispatchFailureCodes.DispatchRejected,
                    "GitHub returned 403 for dispatch — Tamma App installation may be missing the 'actions: write' permission.", 403, ct)
                    .ConfigureAwait(false);

            if (IsDispatchRetryable(api.HttpStatusCode) && attempt < MaxRetries)
            {
                _logger.LogWarning(
                    "Dispatch attempt {Attempt} returned {Status} ({Reason}); retrying in {DelayMs}ms",
                    attempt + 1, api.HttpStatusCode, api.ErrorReason, RetryDelaysMs[attempt]);
                await Task.Delay(RetryDelaysMs[attempt], ct).ConfigureAwait(false);
                continue;
            }

            return await DispatchFailAsync(tenantId, repo, body.CorrelationId, dispatchedAt,
                AgentDispatchFailureCodes.PlatformError,
                $"GitHub dispatch failed with HTTP {api.HttpStatusCode}: {api.ErrorReason ?? "(no body)"}",
                api.HttpStatusCode == 0 ? null : api.HttpStatusCode, ct)
                .ConfigureAwait(false);
        }

        return await DispatchFailAsync(tenantId, repo, body.CorrelationId, dispatchedAt,
            AgentDispatchFailureCodes.PlatformError, "Dispatch failed after retries", null, ct).ConfigureAwait(false);
    }

    private async Task<AgentDispatchRunResult> DispatchFailAsync(
        Guid? tenantId, string repo, string correlationId, DateTime dispatchedAt,
        string failureCode, string reason, int? platformStatusCode, CancellationToken ct)
    {
        var fail = new AgentDispatchRunResult
        {
            Success = false,
            CredentialSource = AgentDispatchCredentialSources.Installation,
            DispatchedAt = dispatchedAt,
            FailureCode = failureCode,
            FailureReason = reason,
            PlatformStatusCode = platformStatusCode,
            CorrelationId = correlationId,
        };
        await EmitAsync(AgentDispatchEventTypes.RunTriggeredFailed, AgentDispatchEventTypes.RunTriggerOperation,
            tenantId, repo, correlationId, AgentDispatchCredentialSources.Installation, runId: null, failureCode,
            new { }, ct).ConfigureAwait(false);
        return fail;
    }

    // ===================================================================
    // Discover / poll run status
    // ===================================================================

    public Task<AgentRunStatusResult> DiscoverRunAsync(
        Guid? tenantId, string repo, string branch, DateTime createdAfter, string? correlationId, CancellationToken ct = default)
    {
        var corr = correlationId ?? string.Empty;
        return ExecuteGuardedAsync(
            () => DiscoverRunCoreAsync(tenantId, repo, branch, createdAfter, corr, ct),
            tenantId, repo, AgentDispatchEventTypes.RunDiscoverOperation, AgentDispatchEventTypes.RunPolledFailed,
            corr, runId: null,
            fail: (code, reason) => new AgentRunStatusResult
            { Success = false, Found = false, FailureCode = code, FailureReason = reason, CorrelationId = corr }, ct);
    }

    private async Task<AgentRunStatusResult> DiscoverRunCoreAsync(
        Guid? tenantId, string repo, string branch, DateTime createdAfter, string correlationId, CancellationToken ct)
    {
        const string op = AgentDispatchEventTypes.RunDiscoverOperation;
        var (owner, name, parsed) = ParseRepo(repo);

        var gate = await GuardOrDenyRunAsync(tenantId, repo, op, AgentDispatchEventTypes.RunPolledFailed,
            correlationId, null,
            reason => new AgentRunStatusResult
            {
                Success = false, Found = false,
                FailureCode = AgentDispatchFailureCodes.RepoNotAuthorized, FailureReason = reason, CorrelationId = correlationId,
            }, ct).ConfigureAwait(false);
        if (gate is not null) return gate;

        if (!parsed)
            return await PollNotFoundAsync(tenantId, repo, op, correlationId, null, ct).ConfigureAwait(false);

        var runs = await _actions.ListWorkflowRunsAsync(owner, name, branch, createdAfter, perPage: 5, ct).ConfigureAwait(false);
        var run = runs.Count > 0 ? runs[0] : null;
        return await PollResultAsync(tenantId, repo, op, correlationId, run, ct).ConfigureAwait(false);
    }

    public Task<AgentRunStatusResult> GetRunAsync(
        Guid? tenantId, string repo, long runId, string? correlationId, CancellationToken ct = default)
    {
        var corr = correlationId ?? string.Empty;
        return ExecuteGuardedAsync(
            () => GetRunCoreAsync(tenantId, repo, runId, corr, ct),
            tenantId, repo, AgentDispatchEventTypes.RunPollOperation, AgentDispatchEventTypes.RunPolledFailed,
            corr, runId,
            fail: (code, reason) => new AgentRunStatusResult
            { Success = false, Found = false, RunId = runId, FailureCode = code, FailureReason = reason, CorrelationId = corr }, ct);
    }

    private async Task<AgentRunStatusResult> GetRunCoreAsync(
        Guid? tenantId, string repo, long runId, string correlationId, CancellationToken ct)
    {
        const string op = AgentDispatchEventTypes.RunPollOperation;
        var (owner, name, parsed) = ParseRepo(repo);

        var gate = await GuardOrDenyRunAsync(tenantId, repo, op, AgentDispatchEventTypes.RunPolledFailed,
            correlationId, runId,
            reason => new AgentRunStatusResult
            {
                Success = false, Found = false, RunId = runId,
                FailureCode = AgentDispatchFailureCodes.RepoNotAuthorized, FailureReason = reason, CorrelationId = correlationId,
            }, ct).ConfigureAwait(false);
        if (gate is not null) return gate;

        if (!parsed)
            return await PollNotFoundAsync(tenantId, repo, op, correlationId, runId, ct).ConfigureAwait(false);

        var run = await _actions.GetWorkflowRunAsync(owner, name, runId, ct).ConfigureAwait(false);
        return await PollResultAsync(tenantId, repo, op, correlationId, run, ct).ConfigureAwait(false);
    }

    /// <summary>Map a discovered/polled run summary (or null) into a SUCCESSFUL
    /// status result — a null run means "not visible yet" (Found=false), still a
    /// successful poll (200) the monitor treats as keep-waiting.
    ///
    /// <para>AC7 (review finding 3) — the monitor polls <c>GET runs/{id}</c> every tick
    /// for the ~35-minute run, so emitting a <c>RUN_POLLED</c> DCB event per poll bloats
    /// the event store (~400/run) and the wiki time-travel view. We emit ONLY when the
    /// observed run status is TERMINAL (<c>completed</c>); routine in-progress/queued
    /// polls emit nothing. Combined with <c>RUN_TRIGGERED</c> (dispatch) +
    /// <c>RESULTS_COLLECTED</c> (collect), that's ~3 audit events per run capturing the
    /// meaningful bookends. The poll's functional response is unchanged — only the DCB
    /// emit is suppressed for non-terminal polls.</para></summary>
    private async Task<AgentRunStatusResult> PollResultAsync(
        Guid? tenantId, string repo, string op, string correlationId, WorkflowRunSummary? run, CancellationToken ct)
    {
        if (run is null)
            return await PollNotFoundAsync(tenantId, repo, op, correlationId, null, ct).ConfigureAwait(false);

        var result = new AgentRunStatusResult
        {
            Success = true,
            CredentialSource = AgentDispatchCredentialSources.Installation,
            Found = true,
            RunId = run.Id,
            Status = run.Status,
            Conclusion = run.Conclusion,
            WorkflowRunUrl = run.HtmlUrl,
            HeadBranch = run.HeadBranch,
            CreatedAt = run.CreatedAt,
            UpdatedAt = run.UpdatedAt,
            ArtifactsUrl = run.ArtifactsUrl,
            CorrelationId = correlationId,
        };

        // AC7 — only a TERMINAL poll emits an audit event (see method remarks).
        if (IsTerminalStatus(run.Status))
        {
            await EmitAsync(AgentDispatchEventTypes.RunPolledSuccess, op, tenantId, repo, correlationId,
                AgentDispatchCredentialSources.Installation, run.Id, failureCode: null,
                new { runId = run.Id, status = run.Status, found = true }, ct).ConfigureAwait(false);
        }
        return result;
    }

    private Task<AgentRunStatusResult> PollNotFoundAsync(
        Guid? tenantId, string repo, string op, string correlationId, long? runId, CancellationToken ct)
    {
        // AC7 — a not-yet-visible run is a non-terminal poll: emit NO audit event.
        var result = new AgentRunStatusResult
        {
            Success = true,
            CredentialSource = AgentDispatchCredentialSources.Installation,
            Found = false,
            RunId = runId,
            CorrelationId = correlationId,
        };
        return Task.FromResult(result);
    }

    // ===================================================================
    // Collect results
    // ===================================================================

    public Task<AgentRunResultsResult> CollectResultsAsync(
        Guid? tenantId, string repo, long runId, CollectAgentRunRequest body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        return ExecuteGuardedAsync(
            () => CollectResultsCoreAsync(tenantId, repo, runId, body, ct),
            tenantId, repo, AgentDispatchEventTypes.ResultsCollectOperation, AgentDispatchEventTypes.ResultsCollectedFailed,
            body.CorrelationId, runId,
            fail: (code, reason) => new AgentRunResultsResult
            { Success = false, FailureCode = code, FailureReason = reason, CorrelationId = body.CorrelationId }, ct);
    }

    private async Task<AgentRunResultsResult> CollectResultsCoreAsync(
        Guid? tenantId, string repo, long runId, CollectAgentRunRequest body, CancellationToken ct)
    {
        const string op = AgentDispatchEventTypes.ResultsCollectOperation;
        var (owner, name, parsed) = ParseRepo(repo);

        var gate = await GuardOrDenyRunAsync(tenantId, repo, op, AgentDispatchEventTypes.ResultsCollectedFailed,
            body.CorrelationId, runId,
            reason => new AgentRunResultsResult
            {
                Success = false,
                FailureCode = AgentDispatchFailureCodes.RepoNotAuthorized, FailureReason = reason, CorrelationId = body.CorrelationId,
            }, ct).ConfigureAwait(false);
        if (gate is not null) return gate;

        if (!parsed)
        {
            var badRepo = new AgentRunResultsResult
            {
                Success = false,
                FailureCode = AgentDispatchFailureCodes.PlatformError,
                FailureReason = $"Invalid repository format '{repo}' (expected 'owner/repo')",
                CorrelationId = body.CorrelationId,
            };
            await EmitAsync(AgentDispatchEventTypes.ResultsCollectedFailed, op, tenantId, repo, body.CorrelationId,
                credentialSource: null, runId, AgentDispatchFailureCodes.PlatformError, new { }, ct).ConfigureAwait(false);
            return badRepo;
        }

        var result = await _aggregator.AggregateAsync(owner, name, runId, body, ct).ConfigureAwait(false);
        await EmitAsync(AgentDispatchEventTypes.ResultsCollectedSuccess, op, tenantId, repo, body.CorrelationId,
            AgentDispatchCredentialSources.Installation, runId, failureCode: null,
            new { runId, agentSuccess = result.AgentSuccess, prNumber = result.PrNumber }, ct).ConfigureAwait(false);
        return result;
    }

    // ===================================================================
    // Resolve installation id (webhook wait-key scoping — NO DCB event)
    // ===================================================================

    public async Task<AgentInstallationResult> ResolveInstallationAsync(
        Guid? tenantId, string repo, string? correlationId, CancellationToken ct = default)
    {
        var corr = correlationId ?? string.Empty;
        try
        {
            var authz = await _authorizer.AuthorizeAsync(tenantId, repo, ct).ConfigureAwait(false);
            if (!authz.Allowed)
                return new AgentInstallationResult
                { Success = false, FailureCode = AgentDispatchFailureCodes.RepoNotAuthorized, FailureReason = authz.Reason, CorrelationId = corr };

            var (owner, name, parsed) = ParseRepo(repo);
            if (!parsed)
                return new AgentInstallationResult
                { Success = false, FailureCode = AgentDispatchFailureCodes.PlatformError, FailureReason = "invalid repo", CorrelationId = corr };

            var id = await _actions.ResolveInstallationIdAsync(owner, name, ct).ConfigureAwait(false);
            return new AgentInstallationResult { Success = true, InstallationId = id, CorrelationId = corr };
        }
        // Review finding 5 — only a caller cancellation propagates; an internal
        // resolution/HTTP timeout (TaskCanceledException, token != ct) is a typed failure.
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "installation resolution threw for repo {Repo}", LogSanitizer.Clean(repo));
            return new AgentInstallationResult
            { Success = false, FailureCode = AgentDispatchFailureCodes.PlatformError, FailureReason = "installation resolution failed", CorrelationId = corr };
        }
    }

    // ===================================================================
    // Guard / guarded-envelope shared paths
    // ===================================================================

    /// <summary>Run the cross-tenant guard. On deny, emit the terminal FAILED event
    /// and return the 403 result (built by <paramref name="makeDenied"/> from the
    /// deny reason); the platform is NEVER called and no installation is resolved.
    /// On allow, returns null so the caller proceeds.</summary>
    private async Task<T?> GuardOrDenyRunAsync<T>(
        Guid? tenantId, string repo, string operation, string failedEventType, string correlationId, long? runId,
        Func<string?, T> makeDenied, CancellationToken ct) where T : class, IAgentDispatchResult
    {
        var authz = await _authorizer.AuthorizeAsync(tenantId, repo, ct).ConfigureAwait(false);
        if (authz.Allowed) return null;

        // credentialSource is null — no token/installation was resolved (fail-closed).
        await EmitAsync(failedEventType, operation, tenantId, repo, correlationId, credentialSource: null,
            runId, AgentDispatchFailureCodes.RepoNotAuthorized, new { }, ct).ConfigureAwait(false);
        return makeDenied(authz.Reason);
    }

    /// <summary>Run one mediation op body; convert any unexpected exception (DB read,
    /// client mint, transport) into a typed key-free PLATFORM_ERROR result plus
    /// exactly one terminal FAILED event. A cancellation is not a platform failure
    /// and propagates. Mirrors Story 38-1's ExecuteGuardedAsync.</summary>
    private async Task<T> ExecuteGuardedAsync<T>(
        Func<Task<T>> body, Guid? tenantId, string repo, string operation, string failedEventType,
        string correlationId, long? runId, Func<string, string, T> fail, CancellationToken ct)
        where T : IAgentDispatchResult
    {
        try
        {
            return await body().ConfigureAwait(false);
        }
        // Review finding 5 — only a CALLER cancellation propagates. An HttpClient /
        // dispatch TIMEOUT surfaces as a TaskCanceledException whose token is NOT the
        // caller's ct; rethrowing it would leak a raw 500 and skip the FAILED event
        // (violating "never a raw 5xx" + "exactly one event"). Treat it as PLATFORM_ERROR.
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "agent-dispatch op {Operation} threw; returning typed PLATFORM_ERROR (never a raw 5xx) with one FAILED event. correlationId={CorrelationId}, repo={Repo}, tenantId={TenantId}",
                operation, LogSanitizer.Clean(correlationId), LogSanitizer.Clean(repo), tenantId);

            await EmitAsync(failedEventType, operation, tenantId, repo, correlationId, credentialSource: null,
                runId, AgentDispatchFailureCodes.PlatformError, new { }, ct).ConfigureAwait(false);

            return fail(AgentDispatchFailureCodes.PlatformError, "an unexpected error occurred processing the agent-dispatch operation");
        }
    }

    // ===================================================================
    // DCB audit (exactly one terminal AGENT_DISPATCH.* event per call)
    // ===================================================================

    private async Task EmitAsync(
        string eventType, string operation, Guid? tenantId, string repo, string correlationId,
        string? credentialSource, long? runId, string? failureCode, object data, CancellationToken ct)
    {
        try
        {
            object tagsObj = (runId, failureCode) switch
            {
                (null, null) => new { tenantId = tenantId?.ToString(), repo, operation, credentialSource, correlationId },
                (not null, null) => new { tenantId = tenantId?.ToString(), repo, operation, credentialSource, correlationId, runId = runId!.Value.ToString() },
                (null, not null) => new { tenantId = tenantId?.ToString(), repo, operation, credentialSource, correlationId, failureCode },
                _ => new { tenantId = tenantId?.ToString(), repo, operation, credentialSource, correlationId, runId = runId!.Value.ToString(), failureCode },
            };

            await _events.AppendAsync(new DomainEvent
            {
                Id = Guid.NewGuid(),
                Type = eventType,
                TenantId = tenantId,
                Tags = JsonSerializer.Serialize(tagsObj),
                Metadata = JsonSerializer.Serialize(new { workflowVersion = "1.0.0", eventSource = "system" }),
                Data = JsonSerializer.Serialize(data),
                CreatedAt = DateTime.UtcNow,
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // An append failure is logged at ERROR, NOT swallowed into a lost result —
            // the mediation result still returns.
            _logger.LogError(ex,
                "AGENT_DISPATCH.* event append failed (type={Type}); the mediation result still returns. correlationId={CorrelationId}, repo={Repo}, tenantId={TenantId}",
                eventType, LogSanitizer.Clean(correlationId), LogSanitizer.Clean(repo), tenantId);
        }
    }

    // ===================================================================
    // Helpers
    // ===================================================================

    private static (string Owner, string Name, bool Parsed) ParseRepo(string? repo)
    {
        if (string.IsNullOrWhiteSpace(repo)) return (string.Empty, string.Empty, false);
        var parts = repo.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return (string.Empty, string.Empty, false);
        return (parts[0], parts[1], true);
    }

    private static bool IsTerminalStatus(string? status) =>
        string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Review finding 4 — the dispatch POST is NON-idempotent: a 5xx (502/503/504) or a
    /// transport error (0) may arrive AFTER GitHub already queued the <c>workflow_dispatch</c>,
    /// so retrying would spawn a SECOND agent run for one issue (double LLM cost / PR
    /// conflicts) while a single <c>RUN_TRIGGERED.SUCCESS</c> event masks the orphan.
    /// We therefore auto-retry the dispatch ONLY on <c>429</c> (rate-limited ⇒ definitely
    /// not queued). An ambiguous 5xx that masked a successful queue is reconciled by the
    /// Monitor's discover phase, which finds the run created in the dispatch window.
    /// (The idempotent READ ops — discover / get-run / collect — are looped by the
    /// engine-side monitor and are safe to re-issue there.)
    /// </summary>
    private static bool IsDispatchRetryable(int statusCode) => statusCode == 429;
}
