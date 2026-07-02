using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tamma.Api.Services.Git;
using Tamma.Core.Interfaces;
using Tamma.Core.Logging;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Ci;

/// <summary>
/// Story 38 (Phase 1) — composes the CI-mediation sequence entirely inside
/// <c>Tamma.Api</c>: cross-tenant guard (reused from Story 38-1) → per-tenant token
/// (BYOK→platform, reusing <see cref="IGitTokenResolver"/> — CI is GitHub Actions on
/// the same git token) → CI call with the RESOLVED token via a token-bound
/// <see cref="ICIIntegrationService"/> minted by <see cref="ICiClientFactory"/> →
/// exactly-one terminal DCB event. The resolved token lives only on that
/// request-scoped service instance; it is NEVER logged, returned, or written to the
/// audit event (only the <c>credentialSource</c> LABEL is surfaced).
/// </summary>
public sealed class CiMediationService : ICiMediationService
{
    private readonly IGitRepoAuthorizer _authorizer;
    private readonly IGitTokenResolver _tokenResolver;
    private readonly ICiClientFactory _ciFactory;
    private readonly IEventRepository _events;
    private readonly ILogger<CiMediationService> _logger;

    public CiMediationService(
        IGitRepoAuthorizer authorizer,
        IGitTokenResolver tokenResolver,
        ICiClientFactory ciFactory,
        IEventRepository events,
        ILogger<CiMediationService> logger)
    {
        _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        _tokenResolver = tokenResolver ?? throw new ArgumentNullException(nameof(tokenResolver));
        _ciFactory = ciFactory ?? throw new ArgumentNullException(nameof(ciFactory));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
    // Trigger tests (the WRITE — dispatches an Actions workflow run)
    // ===================================================================

    private async Task<CiMediationResult> TriggerTestsCoreAsync(Guid? tenantId, string repo, TriggerTestsRequest body, CancellationToken ct)
    {
        var op = CiEventTypes.TestsTriggerOperation;

        var gate = await GuardOrDenyAsync(tenantId, repo, op, CiEventTypes.TestsTriggeredFailed, body.CorrelationId, ct).ConfigureAwait(false);
        if (gate is not null) return gate;

        var cred = await _tokenResolver.ResolveAsync(tenantId, repo, ct).ConfigureAwait(false);
        if (cred is null)
            return await TokenUnavailableAsync(tenantId, repo, op, CiEventTypes.TestsTriggeredFailed, body.CorrelationId, ct).ConfigureAwait(false);

        var ci = _ciFactory.Create(cred.Token);
        var res = await ci.TriggerTestsAsync(repo, body.Branch).ConfigureAwait(false);

        if (!res.Success)
            return await PlatformFailAsync(tenantId, repo, op, CiEventTypes.TestsTriggeredFailed, body.CorrelationId, cred.Source, res.Error, new { branch = body.Branch }, ct).ConfigureAwait(false);

        var data = res.Data!;
        var ok = new CiMediationResult
        {
            Success = true,
            CredentialSource = cred.Source,
            Outcome = "Triggered",
            TestRun = new CiTestRunDto
            {
                RunId = data.RunId,
                Status = data.Status,
                TotalTests = data.TotalTests,
                PassedTests = data.PassedTests,
                FailedTests = data.FailedTests,
                SkippedTests = data.SkippedTests,
                CoveragePercentage = data.CoveragePercentage,
            },
            CorrelationId = body.CorrelationId,
        };
        await EmitAsync(CiEventTypes.TestsTriggeredSuccess, op, tenantId, repo, body.CorrelationId, cred.Source, null,
            new { branch = body.Branch, runId = data.RunId, status = data.Status }, ct).ConfigureAwait(false);
        return ok;
    }

    // ===================================================================
    // Read build status
    // ===================================================================

    private async Task<CiMediationResult> GetBuildStatusCoreAsync(Guid? tenantId, string repo, string branch, string correlationId, CancellationToken ct)
    {
        var op = CiEventTypes.BuildStatusReadOperation;

        var gate = await GuardOrDenyAsync(tenantId, repo, op, CiEventTypes.BuildStatusReadFailed, correlationId, ct).ConfigureAwait(false);
        if (gate is not null) return gate;

        var cred = await _tokenResolver.ResolveAsync(tenantId, repo, ct).ConfigureAwait(false);
        if (cred is null)
            return await TokenUnavailableAsync(tenantId, repo, op, CiEventTypes.BuildStatusReadFailed, correlationId, ct).ConfigureAwait(false);

        var ci = _ciFactory.Create(cred.Token);
        var res = await ci.GetBuildStatusAsync(repo, branch).ConfigureAwait(false);

        if (!res.Success)
            return await PlatformFailAsync(tenantId, repo, op, CiEventTypes.BuildStatusReadFailed, correlationId, cred.Source, res.Error, new { branch }, ct).ConfigureAwait(false);

        var data = res.Data!;
        var ok = new CiMediationResult
        {
            Success = true,
            CredentialSource = cred.Source,
            Outcome = "Read",
            BuildStatus = new CiBuildStatusDto
            {
                Status = data.Status,
                BuildUrl = data.BuildUrl,
                StartedAt = data.StartedAt,
                FinishedAt = data.FinishedAt,
            },
            CorrelationId = correlationId,
        };
        await EmitAsync(CiEventTypes.BuildStatusReadSuccess, op, tenantId, repo, correlationId, cred.Source, null,
            new { branch, status = data.Status }, ct).ConfigureAwait(false);
        return ok;
    }

    // ===================================================================
    // Guard / token-unavailable / failure shared paths
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

    private async Task<CiMediationResult> PlatformFailAsync(
        Guid? tenantId, string repo, string operation, string failedEventType, string correlationId,
        string credentialSource, string? reason, object data, CancellationToken ct)
    {
        var fail = new CiMediationResult
        {
            Success = false,
            CredentialSource = credentialSource,
            Outcome = "Error",
            FailureCode = CiFailureCodes.PlatformError,
            FailureReason = reason,
            PlatformStatusCode = ParsePlatformStatus(reason),
            CorrelationId = correlationId,
        };
        await EmitAsync(failedEventType, operation, tenantId, repo, correlationId, credentialSource, CiFailureCodes.PlatformError, data, ct).ConfigureAwait(false);
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
