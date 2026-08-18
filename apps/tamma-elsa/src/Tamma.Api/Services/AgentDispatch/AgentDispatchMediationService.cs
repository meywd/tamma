using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tamma.Api.Services.Git;
using Tamma.Core.Logging;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;
using Tamma.Platforms.Abstractions;
using PModels = Tamma.Platforms.Abstractions.Models;

namespace Tamma.Api.Services.AgentDispatch;

/// <summary>
/// Story 38-2 / Epic 31 P3 (seam 6) — composes the agent-dispatch mediation
/// sequence entirely inside <c>Tamma.Api</c>: cross-tenant guard (reused from
/// Story 38-1, UNCHANGED) → per-tenant DRIVER resolution
/// (<see cref="IPlatformResolver.ResolveForMediationAsync"/>: tenant
/// installation → <c>Platform:</c> config tier) → the platform call through the
/// resolved driver's <see cref="IGitPlatformActionsClient"/> → the collect
/// aggregation (<see cref="IActionsResultAggregator"/>, handed the SAME
/// resolved driver) → exactly-one terminal DCB event.
///
/// <para><b>P3 swap.</b> The ops used to ride the GitHub-only
/// <c>IGitHubActionsClient</c> (Octokit App tokens, real only when the
/// process-level App was configured). They now speak only the platform
/// abstraction; the driver owns credentials, base URL and dialect. The
/// mediation CONTRACT is unchanged: one terminal event, no-throw, the same
/// typed key-free failure taxonomy (<c>ACTIONS_NOT_CONFIGURED</c> now means
/// "no platform driver / no Actions surface resolved"), the same
/// only-429-retries dispatch posture, and the same found=false "keep waiting"
/// poll semantics. The credential lives only inside the driver; only the
/// <c>credentialSource</c> LABEL (<c>installation</c>) is surfaced.</para>
/// </summary>
public sealed class AgentDispatchMediationService : IAgentDispatchMediationService
{
    private readonly IGitRepoAuthorizer _authorizer;
    private readonly IPlatformResolver _platformResolver;
    private readonly IInstallationRepository _installations;
    private readonly IActionsResultAggregator _aggregator;
    private readonly IEventRepository _events;
    private readonly ILogger<AgentDispatchMediationService> _logger;

    // Retry budget for rate-limited dispatch — aggregate wait ≤ 7s (1s, 2s, 4s),
    // ported verbatim from the former engine-side AgentDispatchService so the
    // dispatch semantics are unchanged after the cutover. ONLY a rate-limit
    // retries (a 5xx may mask an already-queued dispatch — review finding 4).
    private const int MaxRetries = 3;
    private static readonly int[] RetryDelaysMs = { 1000, 2000, 4000 };
    private const string DefaultWorkflowFile = "tamma-agent.yml";

    /// <summary>The Actions driver's typed "dispatch accepted but the run
    /// could not be correlated" message prefix — the dispatch itself
    /// SUCCEEDED, so it maps to a successful trigger with no run URL (the
    /// monitor discovers it), exactly like the pre-swap 204 path. Epic 31
    /// review (F-medium): now the SHARED abstraction constant so the CI
    /// mediation plane applies the same interpretation instead of
    /// coarsening the miss into a hard trigger failure.</summary>
    internal const string DispatchAcceptedPrefix = PlatformErrorText.DispatchAcceptedPrefix;

    public AgentDispatchMediationService(
        IGitRepoAuthorizer authorizer,
        IPlatformResolver platformResolver,
        IInstallationRepository installations,
        IActionsResultAggregator aggregator,
        IEventRepository events,
        ILogger<AgentDispatchMediationService> logger)
    {
        _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        _platformResolver = platformResolver ?? throw new ArgumentNullException(nameof(platformResolver));
        _installations = installations ?? throw new ArgumentNullException(nameof(installations));
        _aggregator = aggregator ?? throw new ArgumentNullException(nameof(aggregator));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // ===================================================================
    // Driver resolution
    // ===================================================================

    /// <summary>
    /// Epic 31 review (F-high) — PER-REPO installation resolution first
    /// (repo → App-plane installation row → that installation's driver,
    /// tenant-scoped), tenant-primary mediation resolution second. The
    /// pre-swap <c>OctokitGitHubActionsClient</c> resolved per repo; riding
    /// the tenant-primary driver 404s for repos of a sibling installation
    /// (a GitHub App installation token cannot see them).
    /// </summary>
    private async Task<IGitPlatformDriver?> ResolveDriverAsync(Guid? tenantId, string repo, CancellationToken ct)
    {
        if (tenantId is { } tid && tid != Guid.Empty)
        {
            try
            {
                var install = await _installations.GetByRepoFullNameAsync(repo).ConfigureAwait(false);
                if (install?.TenantId == tid)
                {
                    var perRepo = await _platformResolver.ResolveForRepoInstallationAsync(
                        tid, PlatformKind.GitHub,
                        install.InstallationId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ct).ConfigureAwait(false);
                    if (perRepo is not null) return perRepo;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Per-repo installation resolution failed for {Repo}; "
                    + "falling back to tenant-primary resolution", LogSanitizer.Clean(repo));
            }
        }

        var resolution = await _platformResolver.ResolveForMediationAsync(tenantId, ct).ConfigureAwait(false);
        return resolution?.Driver;
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

        var driver = await ResolveDriverAsync(tenantId, repo, ct).ConfigureAwait(false);
        if (driver?.Actions is null)
            return await DispatchFailAsync(tenantId, repo, body.CorrelationId, dispatchedAt,
                AgentDispatchFailureCodes.ActionsNotConfigured,
                "No platform driver with a CI/Actions surface resolved for this deployment/tenant — cannot dispatch agent workflow.", null, ct)
                .ConfigureAwait(false);

        var workflowFile = string.IsNullOrWhiteSpace(body.WorkflowFileName) ? DefaultWorkflowFile : body.WorkflowFileName;

        // AC-8 — workflow-file pre-check, expressed through the abstraction:
        // for platforms that dispatch BY FILE (everything but GitLab's
        // pipeline-per-ref model) probe the file on the dispatched ref. Only a
        // POSITIVE not-found blocks with the operator-actionable message; any
        // other probe failure defers to the dispatch itself.
        if (driver.Kind != PlatformKind.GitLab)
        {
            var probe = await driver.Client.GetFileContentAsync(
                new PModels.GetFileContentRequest(owner, name, $".github/workflows/{workflowFile}", body.Ref),
                ct).ConfigureAwait(false);
            if (probe is PlatformResult<byte[]>.Failed { Error: PlatformError.NotFound })
                return await DispatchFailAsync(tenantId, repo, body.CorrelationId, dispatchedAt,
                    AgentDispatchFailureCodes.WorkflowNotFound,
                    $"Workflow file '{workflowFile}' not found in {owner}/{name}. Install the Tamma agent runner: " +
                    "apps/tamma-elsa/runner/github-actions/install-runner.sh --repo <owner/repo> (see that directory's README).",
                    404, ct)
                    .ConfigureAwait(false);
        }

        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var api = await driver.Actions.DispatchWorkflowAsync(
                owner, name,
                new PModels.WorkflowDispatchRequest(body.Ref, workflowFile, body.Inputs),
                ct).ConfigureAwait(false);

            switch (api)
            {
                case PlatformResult<PModels.WorkflowRun>.Ok ok:
                {
                    var success = new AgentDispatchRunResult
                    {
                        Success = true,
                        CredentialSource = AgentDispatchCredentialSources.Installation,
                        // P1 made dispatch return a POLLABLE run — surface its URL
                        // (the pre-swap path always answered null and left the
                        // monitor to discover the run).
                        WorkflowRunUrl = string.IsNullOrWhiteSpace(ok.Value.HtmlUrl) ? null : ok.Value.HtmlUrl,
                        DispatchedAt = dispatchedAt,
                        CorrelationId = body.CorrelationId,
                    };
                    await EmitAsync(AgentDispatchEventTypes.RunTriggeredSuccess, op, tenantId, repo, body.CorrelationId,
                        AgentDispatchCredentialSources.Installation, runId: null, failureCode: null,
                        new { workflowFile, @ref = body.Ref }, ct).ConfigureAwait(false);
                    return success;
                }

                case PlatformResult<PModels.WorkflowRun>.Failed { Error: PlatformError.Unknown u }
                    when u.Reason.StartsWith(DispatchAcceptedPrefix, StringComparison.OrdinalIgnoreCase):
                {
                    // Dispatch WAS accepted; only the run correlation failed —
                    // success with no run URL, monitor discovers (pre-swap 204 parity).
                    var accepted = new AgentDispatchRunResult
                    {
                        Success = true,
                        CredentialSource = AgentDispatchCredentialSources.Installation,
                        WorkflowRunUrl = null,
                        DispatchedAt = dispatchedAt,
                        CorrelationId = body.CorrelationId,
                    };
                    await EmitAsync(AgentDispatchEventTypes.RunTriggeredSuccess, op, tenantId, repo, body.CorrelationId,
                        AgentDispatchCredentialSources.Installation, runId: null, failureCode: null,
                        new { workflowFile, @ref = body.Ref }, ct).ConfigureAwait(false);
                    return accepted;
                }

                case PlatformResult<PModels.WorkflowRun>.Failed { Error: PlatformError.NotFound }:
                    return await DispatchFailAsync(tenantId, repo, body.CorrelationId, dispatchedAt,
                        AgentDispatchFailureCodes.DispatchRejected,
                        $"The platform returned 404 for dispatch — branch '{body.Ref}' or workflow '{workflowFile}' may not exist.", 404, ct)
                        .ConfigureAwait(false);

                case PlatformResult<PModels.WorkflowRun>.Failed { Error: PlatformError.PermissionDenied }:
                    return await DispatchFailAsync(tenantId, repo, body.CorrelationId, dispatchedAt,
                        AgentDispatchFailureCodes.DispatchRejected,
                        "The platform returned 403 for dispatch — the installation/credential may be missing the workflow-dispatch (actions: write) permission.", 403, ct)
                        .ConfigureAwait(false);

                case PlatformResult<PModels.WorkflowRun>.Failed f
                    when PlatformErrorText.IsCapabilityUnsupported(f.Error):
                    // Typed capability refusal — surfaced EXACT (plan §4), so the
                    // workflow's safety-net outcome can branch on it.
                    return await DispatchFailAsync(tenantId, repo, body.CorrelationId, dispatchedAt,
                        AgentDispatchFailureCodes.CapabilityUnsupported,
                        PlatformErrorText.ToLegacyString(f.Error), null, ct)
                        .ConfigureAwait(false);

                case PlatformResult<PModels.WorkflowRun>.Failed { Error: PlatformError.RateLimited }
                    when attempt < MaxRetries:
                    _logger.LogWarning(
                        "Dispatch attempt {Attempt} was rate-limited; retrying in {DelayMs}ms",
                        attempt + 1, RetryDelaysMs[attempt]);
                    await Task.Delay(RetryDelaysMs[attempt], ct).ConfigureAwait(false);
                    continue;

                case PlatformResult<PModels.WorkflowRun>.Failed f:
                {
                    var reason = PlatformErrorText.ToLegacyString(f.Error);
                    return await DispatchFailAsync(tenantId, repo, body.CorrelationId, dispatchedAt,
                        AgentDispatchFailureCodes.PlatformError,
                        $"Workflow dispatch failed: {reason}", ParsePlatformStatus(reason), ct)
                        .ConfigureAwait(false);
                }

                default: // ServiceUnavailable
                    return await DispatchFailAsync(tenantId, repo, body.CorrelationId, dispatchedAt,
                        AgentDispatchFailureCodes.PlatformError,
                        "Workflow dispatch failed: 503: platform unavailable", 503, ct)
                        .ConfigureAwait(false);
            }
        }

        return await DispatchFailAsync(tenantId, repo, body.CorrelationId, dispatchedAt,
            AgentDispatchFailureCodes.PlatformError, "Dispatch failed after retries (rate-limited)", 429, ct).ConfigureAwait(false);
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

        var driver = await ResolveDriverAsync(tenantId, repo, ct).ConfigureAwait(false);
        if (driver?.Actions is null)
            return await PollNotFoundAsync(tenantId, repo, op, correlationId, null, ct).ConfigureAwait(false);

        var runsRes = await driver.Actions.ListRunsAsync(
            owner, name, new PModels.ListWorkflowRunsRequest(branch, PerPage: 5), ct).ConfigureAwait(false);
        if (runsRes is not PlatformResult<IReadOnlyList<PModels.WorkflowRun>>.Ok runsOk)
        {
            // Poll posture parity: a transient platform failure during discovery
            // is "not visible yet" — the monitor keeps waiting inside its own
            // bounded SLA (never an abort on a blip).
            _logger.LogDebug("Run discovery listing failed for {Repo}@{Branch}; treating as not-found", repo, branch);
            return await PollNotFoundAsync(tenantId, repo, op, correlationId, null, ct).ConfigureAwait(false);
        }

        // Runs are newest-first; take the newest at-or-after the dispatch
        // window (60s clock-skew allowance, mirroring the driver's own
        // dispatch correlation).
        var floor = createdAfter.ToUniversalTime().AddSeconds(-60);
        var run = runsOk.Value.FirstOrDefault(r => r.StartedAt.UtcDateTime >= floor);
        return await PollResultAsync(tenantId, repo, op, correlationId, run, branch, ct).ConfigureAwait(false);
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

        var driver = await ResolveDriverAsync(tenantId, repo, ct).ConfigureAwait(false);
        if (driver?.Actions is null)
            return await PollNotFoundAsync(tenantId, repo, op, correlationId, runId, ct).ConfigureAwait(false);

        var runRes = await driver.Actions.GetRunStatusAsync(
            owner, name, runId.ToString(System.Globalization.CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        if (runRes is not PlatformResult<PModels.WorkflowRun>.Ok runOk)
        {
            return await PollNotFoundAsync(tenantId, repo, op, correlationId, runId, ct).ConfigureAwait(false);
        }
        return await PollResultAsync(tenantId, repo, op, correlationId, runOk.Value, headBranch: null, ct).ConfigureAwait(false);
    }

    /// <summary>Map a discovered/polled run (or null) into a SUCCESSFUL status
    /// result — a null run means "not visible yet" (Found=false), still a
    /// successful poll (200) the monitor treats as keep-waiting.
    ///
    /// <para>AC7 — only a TERMINAL poll emits an audit event (the monitor polls
    /// every tick for ~35 minutes; per-poll events bloat the store).</para></summary>
    private async Task<AgentRunStatusResult> PollResultAsync(
        Guid? tenantId, string repo, string op, string correlationId, PModels.WorkflowRun? run,
        string? headBranch, CancellationToken ct)
    {
        if (run is null)
            return await PollNotFoundAsync(tenantId, repo, op, correlationId, null, ct).ConfigureAwait(false);

        var runId = long.TryParse(run.RunId, out var id) ? id : (long?)null;
        var result = new AgentRunStatusResult
        {
            Success = true,
            CredentialSource = AgentDispatchCredentialSources.Installation,
            Found = true,
            RunId = runId,
            Status = run.Status,
            Conclusion = run.Conclusion,
            WorkflowRunUrl = run.HtmlUrl,
            HeadBranch = headBranch,
            CreatedAt = run.StartedAt.UtcDateTime,
            UpdatedAt = (run.CompletedAt ?? run.StartedAt).UtcDateTime,
            ArtifactsUrl = null, // platform-neutral runs carry no artifacts URL; collect lists artifacts directly.
            CorrelationId = correlationId,
        };

        if (IsTerminalStatus(run.Status) || run.Conclusion is not null)
        {
            await EmitAsync(AgentDispatchEventTypes.RunPolledSuccess, op, tenantId, repo, correlationId,
                AgentDispatchCredentialSources.Installation, runId, failureCode: null,
                new { runId, status = run.Status, found = true }, ct).ConfigureAwait(false);
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

        var driver = await ResolveDriverAsync(tenantId, repo, ct).ConfigureAwait(false);
        if (driver is null)
        {
            var noDriver = new AgentRunResultsResult
            {
                Success = false,
                FailureCode = AgentDispatchFailureCodes.ActionsNotConfigured,
                FailureReason = "No platform driver resolved for this deployment/tenant.",
                CorrelationId = body.CorrelationId,
            };
            await EmitAsync(AgentDispatchEventTypes.ResultsCollectedFailed, op, tenantId, repo, body.CorrelationId,
                credentialSource: null, runId, AgentDispatchFailureCodes.ActionsNotConfigured, new { }, ct).ConfigureAwait(false);
            return noDriver;
        }

        var result = await _aggregator.AggregateAsync(driver, tenantId, owner, name, runId, body, ct).ConfigureAwait(false);
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

            // The wait-key scope: the App-plane installation row for the repo
            // (the same registry the guard consulted). No platform client is
            // involved — the id is registry data, and null (no row / non-App
            // platform) keeps the pre-swap null semantics.
            var installation = await _installations
                .GetByRepoFullNameAsync($"{owner}/{name}").ConfigureAwait(false);
            return new AgentInstallationResult { Success = true, InstallationId = installation?.InstallationId, CorrelationId = corr };
        }
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
    /// deny reason); the platform is NEVER called and no driver is resolved.
    /// On allow, returns null so the caller proceeds.</summary>
    private async Task<T?> GuardOrDenyRunAsync<T>(
        Guid? tenantId, string repo, string operation, string failedEventType, string correlationId, long? runId,
        Func<string?, T> makeDenied, CancellationToken ct) where T : class, IAgentDispatchResult
    {
        var authz = await _authorizer.AuthorizeAsync(tenantId, repo, ct).ConfigureAwait(false);
        if (authz.Allowed) return null;

        // credentialSource is null — no driver/credential was resolved (fail-closed).
        await EmitAsync(failedEventType, operation, tenantId, repo, correlationId, credentialSource: null,
            runId, AgentDispatchFailureCodes.RepoNotAuthorized, new { }, ct).ConfigureAwait(false);
        return makeDenied(authz.Reason);
    }

    /// <summary>Run one mediation op body; convert any unexpected exception (DB read,
    /// driver compose, transport) into a typed key-free PLATFORM_ERROR result plus
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

    private static int? ParsePlatformStatus(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return null;
        var colon = reason.IndexOf(':');
        var head = colon > 0 ? reason[..colon] : reason;
        return int.TryParse(head.Trim(), out var status) && status is >= 100 and < 600 ? status : null;
    }
}
