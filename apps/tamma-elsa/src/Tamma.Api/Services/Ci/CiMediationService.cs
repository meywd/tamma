using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Activities.ADL;
using Tamma.Api.Services.Git;
using Tamma.Core.Logging;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;
using Tamma.Platforms.Abstractions;
using PModels = Tamma.Platforms.Abstractions.Models;

namespace Tamma.Api.Services.Ci;

/// <summary>
/// Story 38 (Phase 1) / Epic 31 P3 — composes the CI-mediation sequence entirely
/// inside <c>Tamma.Api</c>: cross-tenant guard (reused from Story 38-1) →
/// per-tenant DRIVER resolution (tenant installation → <c>Platform:</c> config
/// tier, the same <see cref="IPlatformResolver.ResolveForMediationAsync"/> path
/// the git plane took in P2) → the CI call through the resolved driver's
/// <see cref="IGitPlatformActionsClient"/> → exactly-one terminal DCB event.
///
/// <para><b>P3 swap (seam 3).</b> The op cores used to mint a token-bound
/// GitHub-only <c>CIIntegrationService</c> over the named "github" HttpClient
/// (<c>ICiClientFactory</c>). They now speak only the platform abstraction; the
/// driver owns credentials, base URL and platform dialect. The mediation
/// CONTRACT is unchanged: one terminal event, no-throw, the same typed key-free
/// failure taxonomy, the same trigger-then-poll-to-terminal semantics
/// (<c>CI:PollIntervalMs</c> / <c>CI:PollMaxAttempts</c>), and the same coarse
/// wire strings (<see cref="PlatformErrorText.ToLegacyString"/>). The resolved
/// credential lives only inside the driver; it is NEVER logged, returned, or
/// written to the audit event (only the <c>credentialSource</c> LABEL is
/// surfaced).</para>
///
/// <para><b>capability_unsupported (plan §4).</b> A driver without an Actions
/// surface — or a typed <c>capability_unsupported</c> refusal from the platform
/// — surfaces FIRST-CLASS as <c>failureCode = "capability_unsupported"</c>
/// (exact code, never coarsened into PLATFORM_ERROR) so the workflow's check
/// step / safety-net outcome can branch on it. No route or SiteKey changed.</para>
/// </summary>
public sealed class CiMediationService : ICiMediationService
{
    internal const string DefaultWorkflowFile = "test.yml";

    private readonly IGitRepoAuthorizer _authorizer;
    private readonly IPlatformResolver _platformResolver;
    private readonly IEventRepository _events;
    private readonly ILogger<CiMediationService> _logger;
    private readonly int _ciPollIntervalMs;
    private readonly int _ciPollMaxAttempts;
    private readonly string _workflowFile;

    public CiMediationService(
        IGitRepoAuthorizer authorizer,
        IPlatformResolver platformResolver,
        IEventRepository events,
        IConfiguration configuration,
        ILogger<CiMediationService> logger)
    {
        _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        _platformResolver = platformResolver ?? throw new ArgumentNullException(nameof(platformResolver));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        ArgumentNullException.ThrowIfNull(configuration);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // The absorbed CIIntegrationService poll knobs — semantics preserved.
        _ciPollIntervalMs = configuration.GetValue<int>("CI:PollIntervalMs", 5000);
        _ciPollMaxAttempts = configuration.GetValue<int>("CI:PollMaxAttempts", 10);
        _workflowFile = configuration["CI:WorkflowId"] ?? DefaultWorkflowFile;
    }

    public Task<CiMediationResult> TriggerTestsAsync(Guid? tenantId, string repo, TriggerTestsRequest body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        return ExecuteGuardedAsync(tenantId, repo, CiEventTypes.TestsTriggerOperation, CiEventTypes.TestsTriggeredFailed, body.CorrelationId, ct,
            () => TriggerTestsCoreAsync(tenantId, repo, body, ct));
    }

    public Task<CiMediationResult> GetBuildStatusAsync(Guid? tenantId, string repo, string branch, string correlationId, CancellationToken ct = default)
        => ExecuteGuardedAsync(tenantId, repo, CiEventTypes.BuildStatusReadOperation, CiEventTypes.BuildStatusReadFailed, correlationId, ct,
            () => GetBuildStatusCoreAsync(tenantId, repo, branch, correlationId, ct));

    // ===================================================================
    // Driver resolution (the P3 seam): tenant installation → Platform:
    // config tier → fail-closed CI_TOKEN_UNAVAILABLE. Source LABEL maps
    // onto the pre-swap taxonomy (TenantInstallation ⇒ "byok",
    // PlatformDefault ⇒ "platform"). A resolved driver WITHOUT an Actions
    // surface is a first-class capability_unsupported, not a token failure.
    // ===================================================================

    private sealed record ResolvedActions(IGitPlatformActionsClient? Actions, string Source);

    private async Task<ResolvedActions?> ResolveActionsAsync(Guid? tenantId, CancellationToken ct)
    {
        var resolution = await _platformResolver
            .ResolveForMediationAsync(tenantId, ct)
            .ConfigureAwait(false);
        if (resolution is null) return null;

        var source = resolution.Source == MediationCredentialSource.TenantInstallation
            ? GitCredentialSources.Byok
            : GitCredentialSources.Platform;
        return new ResolvedActions(resolution.Driver.Actions, source);
    }

    /// <summary>Legacy-string + capability projection of a non-Ok platform result
    /// (mirrors <c>GitMediationService.Describe</c>).</summary>
    private readonly record struct PlatformFailure(string Reason, bool CapabilityUnsupported);

    private static PlatformFailure Describe<T>(PlatformResult<T> result) => result switch
    {
        PlatformResult<T>.Failed f => new(
            PlatformErrorText.ToLegacyString(f.Error),
            PlatformErrorText.IsCapabilityUnsupported(f.Error)),
        PlatformResult<T>.ServiceUnavailable => new("503: platform unavailable", false),
        _ => new("unknown platform result", false),
    };

    // ===================================================================
    // Trigger tests (the WRITE — dispatches an Actions workflow run, then
    // polls the run to a terminal (or last-observed) state, preserving the
    // absorbed CIIntegrationService semantics: Status carries the platform
    // conclusion once terminal, else the platform's in-progress status).
    // ===================================================================

    private async Task<CiMediationResult> TriggerTestsCoreAsync(Guid? tenantId, string repo, TriggerTestsRequest body, CancellationToken ct)
    {
        var op = CiEventTypes.TestsTriggerOperation;

        var gate = await GuardOrDenyAsync(tenantId, repo, op, CiEventTypes.TestsTriggeredFailed, body.CorrelationId, ct).ConfigureAwait(false);
        if (gate is not null) return gate;

        var cred = await ResolveActionsAsync(tenantId, ct).ConfigureAwait(false);
        if (cred is null)
            return await TokenUnavailableAsync(tenantId, repo, op, CiEventTypes.TestsTriggeredFailed, body.CorrelationId, ct).ConfigureAwait(false);
        if (cred.Actions is null)
            return await CapabilityUnsupportedAsync(tenantId, repo, op, CiEventTypes.TestsTriggeredFailed, body.CorrelationId, cred.Source, new { branch = body.Branch }, ct).ConfigureAwait(false);

        var (owner, repoName) = GitRepoName.Split(repo);
        var workflowFile = string.IsNullOrWhiteSpace(body.WorkflowFile) ? _workflowFile : body.WorkflowFile!;

        var dispatch = await cred.Actions.DispatchWorkflowAsync(
            owner, repoName,
            new PModels.WorkflowDispatchRequest(
                Ref: body.Branch,
                WorkflowFileName: workflowFile,
                Inputs: (IReadOnlyDictionary<string, string>?)body.Inputs ?? new Dictionary<string, string>()),
            ct).ConfigureAwait(false);

        if (dispatch is not PlatformResult<PModels.WorkflowRun>.Ok dispatchOk)
            return await PlatformFailAsync(tenantId, repo, op, CiEventTypes.TestsTriggeredFailed, body.CorrelationId, cred.Source, Describe(dispatch), new { branch = body.Branch }, ct).ConfigureAwait(false);

        var run = dispatchOk.Value;

        // Poll the dispatched run to a terminal state (absorbed poll knobs).
        // A transient status-read failure is fatal like the pre-swap path
        // (EnsureSuccessStatusCode threw → typed PLATFORM_ERROR).
        for (var poll = 0; run.Conclusion is null && poll < _ciPollMaxAttempts; poll++)
        {
            await Task.Delay(_ciPollIntervalMs, ct).ConfigureAwait(false);

            var statusRes = await cred.Actions.GetRunStatusAsync(owner, repoName, run.RunId, ct).ConfigureAwait(false);
            if (statusRes is not PlatformResult<PModels.WorkflowRun>.Ok statusOk)
                return await PlatformFailAsync(tenantId, repo, op, CiEventTypes.TestsTriggeredFailed, body.CorrelationId, cred.Source, Describe(statusRes), new { branch = body.Branch, runId = run.RunId }, ct).ConfigureAwait(false);

            run = statusOk.Value;
        }

        // Terminal → the conclusion is the status (pre-swap contract); still
        // running after the poll budget → surface the in-progress status.
        var status = run.Conclusion ?? run.Status;

        var ok = new CiMediationResult
        {
            Success = true,
            CredentialSource = cred.Source,
            Outcome = "Triggered",
            TestRun = new CiTestRunDto
            {
                RunId = run.RunId,
                Status = status,
                TotalTests = 0,
            },
            CorrelationId = body.CorrelationId,
        };
        await EmitAsync(CiEventTypes.TestsTriggeredSuccess, op, tenantId, repo, body.CorrelationId, cred.Source, null,
            new { branch = body.Branch, runId = run.RunId, status }, ct).ConfigureAwait(false);
        return ok;
    }

    // ===================================================================
    // Read build status (latest run on the branch via ListRunsAsync)
    // ===================================================================

    private async Task<CiMediationResult> GetBuildStatusCoreAsync(Guid? tenantId, string repo, string branch, string correlationId, CancellationToken ct)
    {
        var op = CiEventTypes.BuildStatusReadOperation;

        var gate = await GuardOrDenyAsync(tenantId, repo, op, CiEventTypes.BuildStatusReadFailed, correlationId, ct).ConfigureAwait(false);
        if (gate is not null) return gate;

        var cred = await ResolveActionsAsync(tenantId, ct).ConfigureAwait(false);
        if (cred is null)
            return await TokenUnavailableAsync(tenantId, repo, op, CiEventTypes.BuildStatusReadFailed, correlationId, ct).ConfigureAwait(false);
        if (cred.Actions is null)
            return await CapabilityUnsupportedAsync(tenantId, repo, op, CiEventTypes.BuildStatusReadFailed, correlationId, cred.Source, new { branch }, ct).ConfigureAwait(false);

        var (owner, repoName) = GitRepoName.Split(repo);
        var res = await cred.Actions.ListRunsAsync(
            owner, repoName, new PModels.ListWorkflowRunsRequest(Branch: branch, PerPage: 1), ct).ConfigureAwait(false);

        if (res is not PlatformResult<IReadOnlyList<PModels.WorkflowRun>>.Ok resOk)
            return await PlatformFailAsync(tenantId, repo, op, CiEventTypes.BuildStatusReadFailed, correlationId, cred.Source, Describe(res), new { branch }, ct).ConfigureAwait(false);

        CiBuildStatusDto dto;
        string statusForEvent;
        if (resOk.Value.Count == 0)
        {
            // Pre-swap contract: no runs on the branch is a SUCCESSFUL read
            // with Status "NoRuns", not an error.
            dto = new CiBuildStatusDto { Status = "NoRuns" };
            statusForEvent = "NoRuns";
        }
        else
        {
            var run = resOk.Value[0];
            statusForEvent = run.Conclusion ?? run.Status;
            dto = new CiBuildStatusDto
            {
                Status = statusForEvent,
                BuildUrl = run.HtmlUrl,
                StartedAt = run.StartedAt.UtcDateTime,
                FinishedAt = run.CompletedAt?.UtcDateTime,
            };
        }

        var ok = new CiMediationResult
        {
            Success = true,
            CredentialSource = cred.Source,
            Outcome = "Read",
            BuildStatus = dto,
            CorrelationId = correlationId,
        };
        await EmitAsync(CiEventTypes.BuildStatusReadSuccess, op, tenantId, repo, correlationId, cred.Source, null,
            new { branch, status = statusForEvent }, ct).ConfigureAwait(false);
        return ok;
    }

    // ===================================================================
    // Guard / token-unavailable / capability / failure shared paths
    // ===================================================================

    private async Task<CiMediationResult?> GuardOrDenyAsync(
        Guid? tenantId, string repo, string operation, string failedEventType, string correlationId, CancellationToken ct)
    {
        var authz = await _authorizer.AuthorizeAsync(tenantId, repo, ct).ConfigureAwait(false);
        if (authz.Allowed) return null;

        var result = new CiMediationResult
        {
            Success = false,
            Outcome = "Error",
            FailureCode = CiFailureCodes.RepoNotAuthorized,
            FailureReason = authz.Reason,
            CorrelationId = correlationId,
        };
        await EmitAsync(failedEventType, operation, tenantId, repo, correlationId, credentialSource: null,
            CiFailureCodes.RepoNotAuthorized, new { }, ct).ConfigureAwait(false);
        return result;
    }

    private async Task<CiMediationResult> TokenUnavailableAsync(
        Guid? tenantId, string repo, string operation, string failedEventType, string correlationId, CancellationToken ct)
    {
        var result = new CiMediationResult
        {
            Success = false,
            Outcome = "Error",
            FailureCode = CiFailureCodes.TokenUnavailable,
            FailureReason = "the per-tenant CI token could not be resolved",
            CorrelationId = correlationId,
        };
        await EmitAsync(failedEventType, operation, tenantId, repo, correlationId, credentialSource: null,
            CiFailureCodes.TokenUnavailable, new { }, ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>Epic 31 P3 (plan §4) — the resolved driver has no Actions surface:
    /// a FIRST-CLASS typed capability refusal, not a token failure and not a
    /// coarse PLATFORM_ERROR.</summary>
    private async Task<CiMediationResult> CapabilityUnsupportedAsync(
        Guid? tenantId, string repo, string operation, string failedEventType, string correlationId,
        string credentialSource, object data, CancellationToken ct)
    {
        var result = new CiMediationResult
        {
            Success = false,
            CredentialSource = credentialSource,
            Outcome = "Error",
            FailureCode = CiFailureCodes.CapabilityUnsupported,
            FailureReason = "the resolved platform driver does not support CI dispatch (no Actions surface)",
            CorrelationId = correlationId,
        };
        await EmitAsync(failedEventType, operation, tenantId, repo, correlationId, credentialSource,
            CiFailureCodes.CapabilityUnsupported, data, ct).ConfigureAwait(false);
        return result;
    }

    private async Task<CiMediationResult> PlatformFailAsync(
        Guid? tenantId, string repo, string operation, string failedEventType, string correlationId,
        string credentialSource, PlatformFailure failure, object data, CancellationToken ct)
    {
        var failCode = failure.CapabilityUnsupported
            ? CiFailureCodes.CapabilityUnsupported
            : CiFailureCodes.PlatformError;
        var fail = new CiMediationResult
        {
            Success = false,
            CredentialSource = credentialSource,
            Outcome = "Error",
            FailureCode = failCode,
            FailureReason = failure.Reason,
            PlatformStatusCode = ParsePlatformStatus(failure.Reason),
            CorrelationId = correlationId,
        };
        await EmitAsync(failedEventType, operation, tenantId, repo, correlationId, credentialSource, failCode, data, ct).ConfigureAwait(false);
        return fail;
    }

    /// <summary>Run one mediation op body; convert any unexpected exception into a
    /// typed key-free PLATFORM_ERROR result plus exactly one terminal FAILED event.
    /// A cancellation is not a platform failure and propagates. Mirrors Story 38-1.</summary>
    private async Task<CiMediationResult> ExecuteGuardedAsync(
        Guid? tenantId, string repo, string operation, string failedEventType, string correlationId,
        CancellationToken ct, Func<Task<CiMediationResult>> body)
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
                "ci-mediation op {Operation} threw; returning typed PLATFORM_ERROR (never a raw 5xx) with one FAILED event. correlationId={CorrelationId}, repo={Repo}, tenantId={TenantId}",
                operation, LogSanitizer.Clean(correlationId), LogSanitizer.Clean(repo), tenantId);

            await EmitAsync(failedEventType, operation, tenantId, repo, correlationId, credentialSource: null,
                CiFailureCodes.PlatformError, new { }, ct).ConfigureAwait(false);

            return new CiMediationResult
            {
                Success = false,
                Outcome = "Error",
                FailureCode = CiFailureCodes.PlatformError,
                FailureReason = "an unexpected error occurred processing the CI operation",
                CorrelationId = correlationId,
            };
        }
    }

    // ===================================================================
    // DCB audit (exactly one terminal CI.* event per call)
    // ===================================================================

    private async Task EmitAsync(
        string eventType, string operation, Guid? tenantId, string repo, string correlationId,
        string? credentialSource, string? failureCode, object data, CancellationToken ct)
    {
        try
        {
            object tagsObj = failureCode is null
                ? new { tenantId = tenantId?.ToString(), repo, operation, credentialSource, correlationId }
                : new { tenantId = tenantId?.ToString(), repo, operation, credentialSource, correlationId, failureCode };

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
                "CI.* event append failed (type={Type}); the mediation result still returns. correlationId={CorrelationId}, repo={Repo}, tenantId={TenantId}",
                eventType, LogSanitizer.Clean(correlationId), LogSanitizer.Clean(repo), tenantId);
        }
    }

    private static int? ParsePlatformStatus(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return null;
        var colon = reason.IndexOf(':');
        var head = colon > 0 ? reason[..colon] : reason;
        return int.TryParse(head.Trim(), out var status) && status is >= 100 and < 600 ? status : null;
    }
}
