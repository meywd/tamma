using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tamma.Activities.ADL;
using Tamma.Core.Logging;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;
using Tamma.Platforms.Abstractions;
using PModels = Tamma.Platforms.Abstractions.Models;

namespace Tamma.Api.Services.Git;

/// <summary>
/// Story 38-1 / Epic 31 P2 — composes the git-mediation sequence entirely inside
/// <c>Tamma.Api</c>: cross-tenant guard → per-tenant DRIVER resolution
/// (tenant installation → <c>Platform:</c> config tier) → platform call through
/// the resolved driver's <see cref="IGitPlatformClient"/> → exactly-one terminal
/// DCB event.
///
/// <para><b>P2 swap.</b> The 17 op cores used to mint a token-bound GitHub
/// client per call (<c>IGitHubClientFactory</c> — deleted). They now resolve
/// <see cref="IPlatformResolver.ResolveForMediationAsync"/> and speak only the
/// platform abstraction; the driver owns credentials, base URL and platform
/// dialect. The mediation CONTRACT is unchanged: one terminal event, no-throw,
/// the same typed key-free failure taxonomy, and the same coarse wire strings
/// (<see cref="PlatformErrorText.ToLegacyString"/> reproduces the live path's
/// status-prefixed error shape so <see cref="ParsePlatformStatus"/> and the ADL
/// classifiers land in the same classes). The resolved credential lives only
/// inside the driver; it is NEVER logged, returned, or written to the audit
/// event (only the <c>credentialSource</c> LABEL is surfaced).</para>
///
/// <para><b>capability_unsupported (plan §4).</b> A driver's typed capability
/// refusal surfaces FIRST-CLASS: <c>failureCode = "capability_unsupported"</c>
/// (exact code, never coarsened into PLATFORM_ERROR) so the workflow's check
/// step / safety-net outcome can branch on it. No route or SiteKey changed.</para>
/// </summary>
public sealed class GitMediationService : IGitMediationService
{
    private readonly IGitRepoAuthorizer _authorizer;
    private readonly IPlatformResolver _platformResolver;
    private readonly IEventRepository _events;
    private readonly ILogger<GitMediationService> _logger;

    public GitMediationService(
        IGitRepoAuthorizer authorizer,
        IPlatformResolver platformResolver,
        IEventRepository events,
        ILogger<GitMediationService> logger)
    {
        _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        _platformResolver = platformResolver ?? throw new ArgumentNullException(nameof(platformResolver));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // ===================================================================
    // Public API — each op is wrapped so an unexpected exception between the
    // guard and the platform call becomes a typed PLATFORM_ERROR (never a raw
    // 5xx) with exactly one terminal GIT.* FAILED event (F3 / AC6 / AC7).
    // ===================================================================

    public Task<GitMediationResult> CreateBranchAsync(Guid? tenantId, string repo, CreateBranchRequest body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        return ExecuteGuardedAsync(tenantId, repo, GitEventTypes.BranchCreateOperation, GitEventTypes.BranchCreatedFailed, body.CorrelationId, ct,
            () => CreateBranchCoreAsync(tenantId, repo, body, ct));
    }

    public Task<GitMediationResult> CreatePullRequestAsync(Guid? tenantId, string repo, CreatePrRequest body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        return ExecuteGuardedAsync(tenantId, repo, GitEventTypes.PrOpenOperation, GitEventTypes.PrOpenedFailed, body.CorrelationId, ct,
            () => CreatePullRequestCoreAsync(tenantId, repo, body, ct));
    }

    public Task<GitMediationResult> MergePullRequestAsync(Guid? tenantId, string repo, int prNumber, MergePrRequest body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        return ExecuteGuardedAsync(tenantId, repo, GitEventTypes.PrMergeOperation, GitEventTypes.PrMergeFailed, body.CorrelationId, ct,
            () => MergePullRequestCoreAsync(tenantId, repo, prNumber, body, ct));
    }

    public Task<GitMediationResult> UpdateIssueAsync(Guid? tenantId, string repo, int issueNumber, UpdateIssueRequest body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        return ExecuteGuardedAsync(tenantId, repo, GitEventTypes.IssueUpdateOperation, GitEventTypes.IssueUpdatedFailed, body.CorrelationId, ct,
            () => UpdateIssueCoreAsync(tenantId, repo, issueNumber, body, ct));
    }

    public Task<GitMediationResult> GetPullRequestCommentsAsync(Guid? tenantId, string repo, int prNumber, string correlationId, CancellationToken ct = default)
        => ExecuteGuardedAsync(tenantId, repo, GitEventTypes.PrCommentsReadOperation, GitEventTypes.PrCommentsReadFailed, correlationId, ct,
            () => GetPullRequestCommentsCoreAsync(tenantId, repo, prNumber, correlationId, ct));

    public Task<GitMediationResult> GetPullRequestAsync(Guid? tenantId, string repo, int prNumber, string correlationId, CancellationToken ct = default)
        => ExecuteGuardedAsync(tenantId, repo, GitEventTypes.PrDetailsReadOperation, GitEventTypes.PrDetailsReadFailed, correlationId, ct,
            () => GetPullRequestCoreAsync(tenantId, repo, prNumber, correlationId, ct));

    public Task<GitMediationResult> GetCommitsAsync(Guid? tenantId, string repo, string branch, DateTime? since, string correlationId, CancellationToken ct = default)
        => ExecuteGuardedAsync(tenantId, repo, GitEventTypes.CommitsReadOperation, GitEventTypes.CommitsReadFailed, correlationId, ct,
            () => GetCommitsCoreAsync(tenantId, repo, branch, since, correlationId, ct));

    public Task<GitMediationResult> GetFileChangesAsync(Guid? tenantId, string repo, string branch, string correlationId, CancellationToken ct = default)
        => ExecuteGuardedAsync(tenantId, repo, GitEventTypes.FileChangesReadOperation, GitEventTypes.FileChangesReadFailed, correlationId, ct,
            () => GetFileChangesCoreAsync(tenantId, repo, branch, correlationId, ct));

    public Task<GitMediationResult> DeleteBranchAsync(Guid? tenantId, string repo, string branchName, string correlationId, CancellationToken ct = default)
        => ExecuteGuardedAsync(tenantId, repo, GitEventTypes.BranchDeleteOperation, GitEventTypes.BranchDeletedFailed, correlationId, ct,
            () => DeleteBranchCoreAsync(tenantId, repo, branchName, correlationId, ct));

    public Task<GitMediationResult> CreateReleaseAsync(Guid? tenantId, string repo, CreateReleaseRequest body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        return ExecuteGuardedAsync(tenantId, repo, GitEventTypes.ReleaseCreateOperation, GitEventTypes.ReleaseCreatedFailed, body.CorrelationId, ct,
            () => CreateReleaseCoreAsync(tenantId, repo, body, ct));
    }

    // ===================================================================
    // Story 31-13 — the 7 PR-lifecycle verbs (public wrappers)
    // ===================================================================

    public Task<GitMediationResult> ClosePullRequestAsync(Guid? tenantId, string repo, int prNumber, ClosePrRequest body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        return ExecuteGuardedAsync(tenantId, repo, GitEventTypes.PrCloseOperation, GitEventTypes.PrClosedFailed, body.CorrelationId, ct,
            () => ClosePullRequestCoreAsync(tenantId, repo, prNumber, body, ct));
    }

    public Task<GitMediationResult> ReopenPullRequestAsync(Guid? tenantId, string repo, int prNumber, ReopenPrRequest body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        return ExecuteGuardedAsync(tenantId, repo, GitEventTypes.PrReopenOperation, GitEventTypes.PrReopenedFailed, body.CorrelationId, ct,
            () => ReopenPullRequestCoreAsync(tenantId, repo, prNumber, body, ct));
    }

    public Task<GitMediationResult> CommentOnPullRequestAsync(Guid? tenantId, string repo, int prNumber, PrCommentRequest body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        return ExecuteGuardedAsync(tenantId, repo, GitEventTypes.PrCommentOperation, GitEventTypes.PrCommentedFailed, body.CorrelationId, ct,
            () => CommentOnPullRequestCoreAsync(tenantId, repo, prNumber, body, ct));
    }

    public Task<GitMediationResult> ReviewCommentOnPullRequestAsync(Guid? tenantId, string repo, int prNumber, PrReviewCommentRequest body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        return ExecuteGuardedAsync(tenantId, repo, GitEventTypes.PrReviewCommentOperation, GitEventTypes.PrReviewCommentedFailed, body.CorrelationId, ct,
            () => ReviewCommentOnPullRequestCoreAsync(tenantId, repo, prNumber, body, ct));
    }

    public Task<GitMediationResult> RequestPullRequestReviewersAsync(Guid? tenantId, string repo, int prNumber, PrReviewersRequest body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        return ExecuteGuardedAsync(tenantId, repo, GitEventTypes.PrReviewersRequestOperation, GitEventTypes.PrReviewersRequestedFailed, body.CorrelationId, ct,
            () => RequestPullRequestReviewersCoreAsync(tenantId, repo, prNumber, body, ct));
    }

    public Task<GitMediationResult> UpdatePullRequestLabelsAsync(Guid? tenantId, string repo, int prNumber, PrLabelsRequest body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        return ExecuteGuardedAsync(tenantId, repo, GitEventTypes.PrLabelsUpdateOperation, GitEventTypes.PrLabelsUpdatedFailed, body.CorrelationId, ct,
            () => UpdatePullRequestLabelsCoreAsync(tenantId, repo, prNumber, body, ct));
    }

    public Task<GitMediationResult> SetPullRequestDraftAsync(Guid? tenantId, string repo, int prNumber, PrDraftRequest body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        return ExecuteGuardedAsync(tenantId, repo, GitEventTypes.PrDraftSetOperation, GitEventTypes.PrDraftSetFailed, body.CorrelationId, ct,
            () => SetPullRequestDraftCoreAsync(tenantId, repo, prNumber, body, ct));
    }

    /// <summary>
    /// F3 — run one mediation op body; convert any unexpected exception (DB read,
    /// secret decrypt, driver compose, transport) into a typed key-free
    /// PLATFORM_ERROR result plus exactly one terminal GIT.* FAILED event. A
    /// cancellation is not a platform failure and propagates.
    /// </summary>
    private async Task<GitMediationResult> ExecuteGuardedAsync(
        Guid? tenantId, string repo, string operation, string failedEventType, string correlationId,
        CancellationToken ct, Func<Task<GitMediationResult>> body)
    {
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Genuine caller cancellation propagates; an HttpClient timeout
            // (TaskCanceledException with ct NOT requested) falls through to the
            // general catch → typed PLATFORM_ERROR envelope (never a raw 5xx),
            // matching the Ci/Jira mediation siblings.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "git-mediation op {Operation} threw; returning typed PLATFORM_ERROR (never a raw 5xx) with one FAILED event. correlationId={CorrelationId}, repo={Repo}, tenantId={TenantId}",
                operation, LogSanitizer.Clean(correlationId), LogSanitizer.Clean(repo), tenantId);

            await EmitAsync(failedEventType, operation, tenantId, repo, correlationId, credentialSource: null,
                GitFailureCodes.PlatformError, new { }, ct).ConfigureAwait(false);

            return new GitMediationResult
            {
                Success = false,
                Outcome = "Error",
                FailureCode = GitFailureCodes.PlatformError,
                FailureReason = "an unexpected error occurred processing the git operation",
                CorrelationId = correlationId,
            };
        }
    }

    // ===================================================================
    // Driver resolution (the P2 seam): tenant installation → Platform:
    // config tier → fail-closed GIT_TOKEN_UNAVAILABLE. The source LABEL
    // maps onto the pre-swap taxonomy: TenantInstallation ⇒ "byok",
    // PlatformDefault ⇒ "platform".
    // ===================================================================

    private sealed record ResolvedClient(
        IGitPlatformClient Client,
        string Source,
        IReadOnlySet<PlatformCapability> Capabilities);

    private async Task<ResolvedClient?> ResolveClientAsync(Guid? tenantId, CancellationToken ct)
    {
        var resolution = await _platformResolver
            .ResolveForMediationAsync(tenantId, ct)
            .ConfigureAwait(false);
        if (resolution is null) return null;

        var source = resolution.Source == MediationCredentialSource.TenantInstallation
            ? GitCredentialSources.Byok
            : GitCredentialSources.Platform;
        return new ResolvedClient(resolution.Driver.Client, source, resolution.Driver.Capabilities);
    }

    /// <summary>Legacy-string + capability projection of a non-Ok platform result.</summary>
    private readonly record struct PlatformFailure(string Reason, bool CapabilityUnsupported);

    private static PlatformFailure Describe<T>(PlatformResult<T> result) => result switch
    {
        PlatformResult<T>.Failed f => new(
            PlatformErrorText.ToLegacyString(f.Error),
            PlatformErrorText.IsCapabilityUnsupported(f.Error)),
        PlatformResult<T>.ServiceUnavailable => new("503: platform unavailable", false),
        _ => new("unknown platform result", false),
    };

    private static string PrStateWire(PModels.PullRequestState state) => state switch
    {
        PModels.PullRequestState.Closed => "closed",
        PModels.PullRequestState.Merged => "merged",
        _ => "open",
    };

    // ===================================================================
    // Create branch
    // ===================================================================

    private async Task<GitMediationResult> CreateBranchCoreAsync(Guid? tenantId, string repo, CreateBranchRequest body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        var op = GitEventTypes.BranchCreateOperation;

        var gate = await GuardOrDenyAsync(tenantId, repo, op, GitEventTypes.BranchCreatedFailed, body.CorrelationId, ct).ConfigureAwait(false);
        if (gate is not null) return gate;

        var cred = await ResolveClientAsync(tenantId, ct).ConfigureAwait(false);
        if (cred is null)
            return await TokenUnavailableAsync(tenantId, repo, op, GitEventTypes.BranchCreatedFailed, body.CorrelationId, ct).ConfigureAwait(false);

        var strategy = string.IsNullOrWhiteSpace(body.ConflictStrategy)
            ? CreateBranchActivity.DefaultConflictStrategy
            : body.ConflictStrategy!;

        var outcome = await CreateBranchActivity.ExecuteCoreAsync(
            cred.Client, repo, body.IssueNumber, body.BranchName, body.BaseRef, strategy, _logger).ConfigureAwait(false);

        if (outcome.Outcome == "Created")
        {
            var ok = new GitMediationResult
            {
                Success = true,
                CredentialSource = cred.Source,
                Outcome = "Created",
                BranchRef = outcome.BranchName,
                BaseSha = outcome.BaseSha,
                ConflictResolved = outcome.ConflictResolved,
                CorrelationId = body.CorrelationId,
            };
            await EmitAsync(GitEventTypes.BranchCreatedSuccess, op, tenantId, repo, body.CorrelationId, cred.Source, null,
                new { branchRef = outcome.BranchName, conflictResolved = outcome.ConflictResolved }, ct).ConfigureAwait(false);
            return ok;
        }

        var failCode = MapBranchFailure(outcome.ErrorCode);
        var fail = new GitMediationResult
        {
            Success = false,
            CredentialSource = cred.Source,
            Outcome = "Error",
            FailureCode = failCode,
            FailureReason = outcome.Error,
            PlatformStatusCode = ParsePlatformStatus(outcome.Error),
            CorrelationId = body.CorrelationId,
        };
        await EmitAsync(GitEventTypes.BranchCreatedFailed, op, tenantId, repo, body.CorrelationId, cred.Source, failCode,
            new { branchName = body.BranchName }, ct).ConfigureAwait(false);
        return fail;
    }

    // ===================================================================
    // Create / update pull request
    // ===================================================================

    private async Task<GitMediationResult> CreatePullRequestCoreAsync(Guid? tenantId, string repo, CreatePrRequest body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        var op = GitEventTypes.PrOpenOperation;

        var gate = await GuardOrDenyAsync(tenantId, repo, op, GitEventTypes.PrOpenedFailed, body.CorrelationId, ct).ConfigureAwait(false);
        if (gate is not null) return gate;

        var cred = await ResolveClientAsync(tenantId, ct).ConfigureAwait(false);
        if (cred is null)
            return await TokenUnavailableAsync(tenantId, repo, op, GitEventTypes.PrOpenedFailed, body.CorrelationId, ct).ConfigureAwait(false);

        var coreRequest = new Tamma.Core.Interfaces.CreatePullRequestRequest
        {
            Title = body.Title,
            Body = body.Body ?? string.Empty,
            Head = body.HeadRef,
            Base = body.BaseRef,
            Labels = body.Labels?.ToList() ?? new List<string>(),
            Reviewers = body.Reviewers?.ToList() ?? new List<string>(),
            IsDraft = body.IsDraft,
        };

        var outcome = await CreatePullRequestActivity.ExecuteCoreAsync(
            cred.Client, repo, body.HeadRef, body.BaseRef, body.IsDraft, coreRequest, _logger).ConfigureAwait(false);

        if (outcome.Outcome is "Created" or "Updated")
        {
            var ok = new GitMediationResult
            {
                Success = true,
                CredentialSource = cred.Source,
                Outcome = outcome.Outcome,
                PrNumber = outcome.PrNumber,
                PrUrl = outcome.PrUrl,
                Reused = outcome.Reused,
                IsDraft = outcome.IsDraft,
                ReviewersSkipped = outcome.ReviewersSkipped ? true : null,
                CorrelationId = body.CorrelationId,
            };

            // Epic 31 P5 M2 (DG-3) — a reviewer request the platform did not
            // perform is on the record: the core labeled the PR
            // (needs-reviewer) and this audit row carries the key-free
            // reason. The PR step itself is NOT failed (§4). Additional
            // audit event; the terminal PR_OPENED event below is unchanged.
            if (outcome.ReviewersSkipped)
            {
                await EmitAsync(GitEventTypes.PrReviewersSkipped, op, tenantId, repo, body.CorrelationId, cred.Source, null,
                    new
                    {
                        prNumber = outcome.PrNumber,
                        reason = outcome.ReviewersSkipReason ?? "unknown",
                        reviewerCount = body.Reviewers?.Count ?? 0,
                        label = CreatePullRequestActivity.ReviewersSkippedLabel,
                    }, ct).ConfigureAwait(false);
            }

            await EmitAsync(GitEventTypes.PrOpenedSuccess, op, tenantId, repo, body.CorrelationId, cred.Source, null,
                new { prNumber = outcome.PrNumber, reused = outcome.Reused, outcome = outcome.Outcome, reviewersSkipped = outcome.ReviewersSkipped }, ct).ConfigureAwait(false);
            return ok;
        }

        var failCode = MapPrFailure(outcome.ErrorCode);
        var fail = new GitMediationResult
        {
            Success = false,
            CredentialSource = cred.Source,
            Outcome = "Error",
            FailureCode = failCode,
            FailureReason = outcome.ErrorCode,
            CorrelationId = body.CorrelationId,
        };
        await EmitAsync(GitEventTypes.PrOpenedFailed, op, tenantId, repo, body.CorrelationId, cred.Source, failCode,
            new { head = body.HeadRef, @base = body.BaseRef }, ct).ConfigureAwait(false);
        return fail;
    }

    // ===================================================================
    // Merge pull request (highest-risk write)
    // ===================================================================

    private async Task<GitMediationResult> MergePullRequestCoreAsync(Guid? tenantId, string repo, int prNumber, MergePrRequest body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        var op = GitEventTypes.PrMergeOperation;

        var gate = await GuardOrDenyAsync(tenantId, repo, op, GitEventTypes.PrMergeFailed, body.CorrelationId, ct).ConfigureAwait(false);
        if (gate is not null) return gate;

        var cred = await ResolveClientAsync(tenantId, ct).ConfigureAwait(false);
        if (cred is null)
            return await TokenUnavailableAsync(tenantId, repo, op, GitEventTypes.PrMergeFailed, body.CorrelationId, ct).ConfigureAwait(false);

        var strategy = MergePullRequestActivity.NormalizeStrategy(body.MergeStrategy);

        var outcome = await MergePullRequestActivity.ExecuteCoreAsync(
            cred.Client, repo, prNumber, body.IssueNumber, body.BranchName ?? string.Empty, strategy,
            body.AutoDeleteBranch, body.CloseAssociatedIssue, _logger).ConfigureAwait(false);

        if (outcome.MergeSucceeded)
        {
            var ok = new GitMediationResult
            {
                Success = true,
                CredentialSource = cred.Source,
                Outcome = outcome.Outcome, // "Merged" | "MergedWithWarnings"
                Merged = true,
                MergeSha = outcome.MergeSha,
                IssueClosed = outcome.IssueClosed,
                BranchDeleted = outcome.BranchDeleted,
                AlreadyMerged = outcome.AlreadyMerged,
                AppliedMergeStrategy = outcome.AppliedStrategy,
                FailureReason = outcome.FailureReason, // warnings (partial), key-free
                CorrelationId = body.CorrelationId,
            };

            // Epic 31 P5 M2 (DG-4) — the fixed-order method fallback is on
            // the record (§4.4): which method was asked for, which one the
            // platform actually accepted. Additional audit row; the terminal
            // PR_MERGED event below is unchanged.
            if (outcome.MethodFallbackFrom is not null)
            {
                await EmitAsync(GitEventTypes.PrMergeMethodFallback, op, tenantId, repo, body.CorrelationId, cred.Source, null,
                    new
                    {
                        prNumber,
                        requestedMethod = outcome.MethodFallbackFrom,
                        appliedMethod = outcome.AppliedStrategy,
                        fallbackOrder = "rebase>squash>merge",
                    }, ct).ConfigureAwait(false);
            }

            await EmitAsync(GitEventTypes.PrMergedSuccess, op, tenantId, repo, body.CorrelationId, cred.Source, null,
                new { prNumber, issueNumber = body.IssueNumber, alreadyMerged = outcome.AlreadyMerged, outcome = outcome.Outcome, appliedStrategy = outcome.AppliedStrategy }, ct).ConfigureAwait(false);
            return ok;
        }

        var failCode = MapMergeFailure(outcome.FailureCode);
        var fail = new GitMediationResult
        {
            Success = false,
            CredentialSource = cred.Source,
            Outcome = "Error",
            Merged = false,
            FailureCode = failCode,
            FailureReason = outcome.FailureReason,
            PlatformStatusCode = ParsePlatformStatus(outcome.FailureReason),
            CorrelationId = body.CorrelationId,
        };
        await EmitAsync(GitEventTypes.PrMergeFailed, op, tenantId, repo, body.CorrelationId, cred.Source, failCode,
            new { prNumber, issueNumber = body.IssueNumber }, ct).ConfigureAwait(false);
        return fail;
    }

    // ===================================================================
    // Update issue (status comment + labels)
    // ===================================================================

    private async Task<GitMediationResult> UpdateIssueCoreAsync(Guid? tenantId, string repo, int issueNumber, UpdateIssueRequest body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        var op = GitEventTypes.IssueUpdateOperation;

        var gate = await GuardOrDenyAsync(tenantId, repo, op, GitEventTypes.IssueUpdatedFailed, body.CorrelationId, ct).ConfigureAwait(false);
        if (gate is not null) return gate;

        var cred = await ResolveClientAsync(tenantId, ct).ConfigureAwait(false);
        if (cred is null)
            return await TokenUnavailableAsync(tenantId, repo, op, GitEventTypes.IssueUpdatedFailed, body.CorrelationId, ct).ConfigureAwait(false);

        var (owner, repoName) = GitRepoName.Split(repo);
        var issueText = issueNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // Post the status comment, then add / remove labels. The first failure
        // surfaces a loud typed failure (no partial false success).
        if (!string.IsNullOrWhiteSpace(body.Body))
        {
            var comment = await cred.Client.CreateIssueCommentAsync(owner, repoName, issueText, body.Body!, ct).ConfigureAwait(false);
            if (comment is not PlatformResult<PModels.IssueComment>.Ok)
                return await IssueFailAsync(tenantId, repo, issueNumber, body.CorrelationId, cred.Source, Describe(comment), ct).ConfigureAwait(false);
        }

        if (body.AddLabels is { Count: > 0 })
        {
            var added = await cred.Client.AddIssueLabelsAsync(
                new PModels.AddIssueLabelsRequest(owner, repoName, issueText, body.AddLabels.ToList()), ct).ConfigureAwait(false);
            if (added is not PlatformResult<IReadOnlyList<string>>.Ok)
                return await IssueFailAsync(tenantId, repo, issueNumber, body.CorrelationId, cred.Source, Describe(added), ct).ConfigureAwait(false);
        }

        if (body.RemoveLabels is { Count: > 0 })
        {
            foreach (var label in body.RemoveLabels)
            {
                var removed = await cred.Client.RemoveIssueLabelAsync(owner, repoName, issueText, label, ct).ConfigureAwait(false);
                if (removed is not PlatformResult<IReadOnlyList<string>>.Ok)
                    return await IssueFailAsync(tenantId, repo, issueNumber, body.CorrelationId, cred.Source, Describe(removed), ct).ConfigureAwait(false);
            }
        }

        // Optional state transition (today the ADL activity never closes here, but
        // the wire supports it for completeness).
        if (string.Equals(body.Status, "closed", StringComparison.OrdinalIgnoreCase))
        {
            var closed = await cred.Client.CloseIssueAsync(owner, repoName, issueText, comment: null, ct).ConfigureAwait(false);
            if (closed is not PlatformResult<PModels.Issue>.Ok closedOk
                || closedOk.Value.State != PModels.IssueState.Closed)
            {
                var failure = closed is PlatformResult<PModels.Issue>.Ok
                    ? new PlatformFailure("issue close returned an unsuccessful result", false)
                    : Describe(closed);
                return await IssueFailAsync(tenantId, repo, issueNumber, body.CorrelationId, cred.Source, failure, ct).ConfigureAwait(false);
            }
        }

        var ok = new GitMediationResult
        {
            Success = true,
            CredentialSource = cred.Source,
            Outcome = "Updated",
            IssueStatus = string.IsNullOrWhiteSpace(body.Status) ? "updated" : body.Status,
            CorrelationId = body.CorrelationId,
        };
        await EmitAsync(GitEventTypes.IssueUpdatedSuccess, op, tenantId, repo, body.CorrelationId, cred.Source, null,
            new { issueNumber, status = ok.IssueStatus }, ct).ConfigureAwait(false);
        return ok;
    }

    private async Task<GitMediationResult> IssueFailAsync(
        Guid? tenantId, string repo, int issueNumber, string correlationId, string credentialSource, PlatformFailure failure, CancellationToken ct)
    {
        var failCode = failure.CapabilityUnsupported
            ? GitFailureCodes.CapabilityUnsupported
            : MapIssueFailure(failure.Reason);
        var fail = new GitMediationResult
        {
            Success = false,
            CredentialSource = credentialSource,
            Outcome = "Failed",
            FailureCode = failCode,
            FailureReason = failure.Reason,
            PlatformStatusCode = ParsePlatformStatus(failure.Reason),
            CorrelationId = correlationId,
        };
        await EmitAsync(GitEventTypes.IssueUpdatedFailed, GitEventTypes.IssueUpdateOperation, tenantId, repo, correlationId, credentialSource, failCode,
            new { issueNumber }, ct).ConfigureAwait(false);
        return fail;
    }

    // ===================================================================
    // Read PR review comments
    // ===================================================================

    private async Task<GitMediationResult> GetPullRequestCommentsCoreAsync(Guid? tenantId, string repo, int prNumber, string correlationId, CancellationToken ct = default)
    {
        var op = GitEventTypes.PrCommentsReadOperation;

        var gate = await GuardOrDenyAsync(tenantId, repo, op, GitEventTypes.PrCommentsReadFailed, correlationId, ct).ConfigureAwait(false);
        if (gate is not null) return gate;

        var cred = await ResolveClientAsync(tenantId, ct).ConfigureAwait(false);
        if (cred is null)
            return await TokenUnavailableAsync(tenantId, repo, op, GitEventTypes.PrCommentsReadFailed, correlationId, ct).ConfigureAwait(false);

        var (owner, repoName) = GitRepoName.Split(repo);
        var res = await cred.Client.ListPullRequestReviewCommentsAsync(
            owner, repoName, prNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), ct).ConfigureAwait(false);

        if (res is not PlatformResult<IReadOnlyList<PModels.PullRequestReviewComment>>.Ok resOk)
        {
            var failure = Describe(res);
            var failCode = failure.CapabilityUnsupported
                ? GitFailureCodes.CapabilityUnsupported
                : MapReadFailure(failure.Reason);
            var fail = new GitMediationResult
            {
                Success = false,
                CredentialSource = cred.Source,
                Outcome = "Error",
                FailureCode = failCode,
                FailureReason = failure.Reason,
                PlatformStatusCode = ParsePlatformStatus(failure.Reason),
                CorrelationId = correlationId,
            };
            await EmitAsync(GitEventTypes.PrCommentsReadFailed, op, tenantId, repo, correlationId, cred.Source, failCode,
                new { prNumber }, ct).ConfigureAwait(false);
            return fail;
        }

        var comments = resOk.Value
            .Select(c => new PrCommentDto
            {
                Id = long.TryParse(c.Id, out var id) ? id : 0,
                Body = c.Body,
                Path = c.Path,
                Line = c.Line,
                Author = c.AuthorLogin,
                CreatedAt = c.CreatedAt.UtcDateTime,
            })
            .ToList();

        var ok = new GitMediationResult
        {
            Success = true,
            CredentialSource = cred.Source,
            Outcome = "Done",
            Comments = comments,
            CorrelationId = correlationId,
        };
        await EmitAsync(GitEventTypes.PrCommentsReadSuccess, op, tenantId, repo, correlationId, cred.Source, null,
            new { prNumber, commentCount = comments.Count }, ct).ConfigureAwait(false);
        return ok;
    }

    // ===================================================================
    // Read PR details (Story 43-12) — the merge-target base-branch read
    // ===================================================================

    private async Task<GitMediationResult> GetPullRequestCoreAsync(Guid? tenantId, string repo, int prNumber, string correlationId, CancellationToken ct)
    {
        var op = GitEventTypes.PrDetailsReadOperation;

        var gate = await GuardOrDenyAsync(tenantId, repo, op, GitEventTypes.PrDetailsReadFailed, correlationId, ct).ConfigureAwait(false);
        if (gate is not null) return gate;

        var cred = await ResolveClientAsync(tenantId, ct).ConfigureAwait(false);
        if (cred is null)
            return await TokenUnavailableAsync(tenantId, repo, op, GitEventTypes.PrDetailsReadFailed, correlationId, ct).ConfigureAwait(false);

        var (owner, repoName) = GitRepoName.Split(repo);
        var res = await cred.Client.GetPullRequestAsync(
            owner, repoName, prNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), ct).ConfigureAwait(false);

        if (res is not PlatformResult<PModels.PullRequest>.Ok resOk)
            return await ReadFailAsync(tenantId, repo, op, GitEventTypes.PrDetailsReadFailed, correlationId, cred.Source, Describe(res), new { prNumber }, ct).ConfigureAwait(false);

        var ok = new GitMediationResult
        {
            Success = true,
            CredentialSource = cred.Source,
            Outcome = "Done",
            PrNumber = int.TryParse(resOk.Value.Number, out var n) ? n : prNumber,
            TargetBranch = resOk.Value.TargetBranch,
            CorrelationId = correlationId,
        };
        await EmitAsync(GitEventTypes.PrDetailsReadSuccess, op, tenantId, repo, correlationId, cred.Source, null,
            new { prNumber, baseBranch = resOk.Value.TargetBranch }, ct).ConfigureAwait(false);
        return ok;
    }

    // ===================================================================
    // Extra ops (Story 38 Phase 1) — commits / file-changes reads + delete
    // ===================================================================

    private async Task<GitMediationResult> GetCommitsCoreAsync(Guid? tenantId, string repo, string branch, DateTime? since, string correlationId, CancellationToken ct)
    {
        var op = GitEventTypes.CommitsReadOperation;

        var gate = await GuardOrDenyAsync(tenantId, repo, op, GitEventTypes.CommitsReadFailed, correlationId, ct).ConfigureAwait(false);
        if (gate is not null) return gate;

        var cred = await ResolveClientAsync(tenantId, ct).ConfigureAwait(false);
        if (cred is null)
            return await TokenUnavailableAsync(tenantId, repo, op, GitEventTypes.CommitsReadFailed, correlationId, ct).ConfigureAwait(false);

        var (owner, repoName) = GitRepoName.Split(repo);
        var res = await cred.Client.ListCommitsAsync(
            new PModels.ListCommitsRequest(owner, repoName, branch,
                since is { } s ? new DateTimeOffset(DateTime.SpecifyKind(s, DateTimeKind.Utc)) : null), ct).ConfigureAwait(false);

        if (res is not PlatformResult<IReadOnlyList<PModels.Commit>>.Ok resOk)
            return await ReadFailAsync(tenantId, repo, op, GitEventTypes.CommitsReadFailed, correlationId, cred.Source, Describe(res), new { branch }, ct).ConfigureAwait(false);

        var commits = resOk.Value
            .Select(c => new GitCommitDto
            {
                Sha = c.Sha,
                Message = c.Message,
                Author = c.AuthorName,
                Timestamp = c.Timestamp.UtcDateTime,
                Additions = 0,
                Deletions = 0,
                Files = new List<string>(),
            })
            .ToList();

        var ok = new GitMediationResult
        {
            Success = true,
            CredentialSource = cred.Source,
            Outcome = "Done",
            Commits = commits,
            CorrelationId = correlationId,
        };
        await EmitAsync(GitEventTypes.CommitsReadSuccess, op, tenantId, repo, correlationId, cred.Source, null,
            new { branch, commitCount = commits.Count }, ct).ConfigureAwait(false);
        return ok;
    }

    private async Task<GitMediationResult> GetFileChangesCoreAsync(Guid? tenantId, string repo, string branch, string correlationId, CancellationToken ct)
    {
        var op = GitEventTypes.FileChangesReadOperation;

        var gate = await GuardOrDenyAsync(tenantId, repo, op, GitEventTypes.FileChangesReadFailed, correlationId, ct).ConfigureAwait(false);
        if (gate is not null) return gate;

        var cred = await ResolveClientAsync(tenantId, ct).ConfigureAwait(false);
        if (cred is null)
            return await TokenUnavailableAsync(tenantId, repo, op, GitEventTypes.FileChangesReadFailed, correlationId, ct).ConfigureAwait(false);

        var (owner, repoName) = GitRepoName.Split(repo);
        var res = await cred.Client.ListBranchFileChangesAsync(
            new PModels.ListBranchFileChangesRequest(owner, repoName, branch), ct).ConfigureAwait(false);

        if (res is not PlatformResult<IReadOnlyList<PModels.PrFile>>.Ok resOk)
            return await ReadFailAsync(tenantId, repo, op, GitEventTypes.FileChangesReadFailed, correlationId, cred.Source, Describe(res), new { branch }, ct).ConfigureAwait(false);

        var changes = resOk.Value
            .Select(f => new GitFileChangeDto
            {
                FilePath = f.Path,
                ChangeType = FileStatusWire(f.Status),
                Additions = f.Additions,
                Deletions = f.Deletions,
            })
            .ToList();

        var ok = new GitMediationResult
        {
            Success = true,
            CredentialSource = cred.Source,
            Outcome = "Done",
            FileChanges = changes,
            CorrelationId = correlationId,
        };
        await EmitAsync(GitEventTypes.FileChangesReadSuccess, op, tenantId, repo, correlationId, cred.Source, null,
            new { branch, fileCount = changes.Count }, ct).ConfigureAwait(false);
        return ok;
    }

    private static string FileStatusWire(PModels.PrFileStatus status) => status switch
    {
        PModels.PrFileStatus.Added => "added",
        PModels.PrFileStatus.Modified => "modified",
        PModels.PrFileStatus.Removed => "removed",
        PModels.PrFileStatus.Renamed => "renamed",
        PModels.PrFileStatus.Copied => "copied",
        _ => "changed",
    };

    private async Task<GitMediationResult> DeleteBranchCoreAsync(Guid? tenantId, string repo, string branchName, string correlationId, CancellationToken ct)
    {
        var op = GitEventTypes.BranchDeleteOperation;

        var gate = await GuardOrDenyAsync(tenantId, repo, op, GitEventTypes.BranchDeletedFailed, correlationId, ct).ConfigureAwait(false);
        if (gate is not null) return gate;

        var cred = await ResolveClientAsync(tenantId, ct).ConfigureAwait(false);
        if (cred is null)
            return await TokenUnavailableAsync(tenantId, repo, op, GitEventTypes.BranchDeletedFailed, correlationId, ct).ConfigureAwait(false);

        var (owner, repoName) = GitRepoName.Split(repo);
        var res = await cred.Client.DeleteBranchAsync(owner, repoName, branchName, ct).ConfigureAwait(false);

        if (res is not PlatformResult<bool>.Ok)
            return await ReadFailAsync(tenantId, repo, op, GitEventTypes.BranchDeletedFailed, correlationId, cred.Source, Describe(res), new { branchName }, ct).ConfigureAwait(false);

        var ok = new GitMediationResult
        {
            Success = true,
            CredentialSource = cred.Source,
            Outcome = "Deleted",
            BranchRef = branchName,
            BranchDeleted = true,
            CorrelationId = correlationId,
        };
        await EmitAsync(GitEventTypes.BranchDeletedSuccess, op, tenantId, repo, correlationId, cred.Source, null,
            new { branchName }, ct).ConfigureAwait(false);
        return ok;
    }

    // ===================================================================
    // Create release (Epic 38 follow-up #21) — deployment-pipeline release step
    // ===================================================================

    private async Task<GitMediationResult> CreateReleaseCoreAsync(Guid? tenantId, string repo, CreateReleaseRequest body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        var op = GitEventTypes.ReleaseCreateOperation;

        var gate = await GuardOrDenyAsync(tenantId, repo, op, GitEventTypes.ReleaseCreatedFailed, body.CorrelationId, ct).ConfigureAwait(false);
        if (gate is not null) return gate;

        var cred = await ResolveClientAsync(tenantId, ct).ConfigureAwait(false);
        if (cred is null)
            return await TokenUnavailableAsync(tenantId, repo, op, GitEventTypes.ReleaseCreatedFailed, body.CorrelationId, ct).ConfigureAwait(false);

        var (owner, repoName) = GitRepoName.Split(repo);
        var res = await cred.Client.CreateReleaseAsync(
            new PModels.CreateReleaseRequest(
                Owner: owner,
                RepoName: repoName,
                TagName: body.TagName,
                Name: string.IsNullOrWhiteSpace(body.Name) ? body.TagName : body.Name,
                Body: body.Body,
                TargetCommitish: body.TargetRef,
                Draft: body.Draft,
                Prerelease: body.Prerelease), ct).ConfigureAwait(false);

        if (res is not PlatformResult<PModels.Release>.Ok resOk)
            return await ReadFailAsync(tenantId, repo, op, GitEventTypes.ReleaseCreatedFailed, body.CorrelationId, cred.Source, Describe(res), new { tag = body.TagName }, ct).ConfigureAwait(false);

        var releaseId = long.TryParse(resOk.Value.Id, out var rid) ? rid : 0;
        var ok = new GitMediationResult
        {
            Success = true,
            CredentialSource = cred.Source,
            Outcome = "Created",
            ReleaseId = releaseId,
            ReleaseUrl = resOk.Value.HtmlUrl,
            ReleaseTag = string.IsNullOrEmpty(resOk.Value.TagName) ? body.TagName : resOk.Value.TagName,
            CorrelationId = body.CorrelationId,
        };
        await EmitAsync(GitEventTypes.ReleaseCreatedSuccess, op, tenantId, repo, body.CorrelationId, cred.Source, null,
            new { tag = ok.ReleaseTag, releaseId }, ct).ConfigureAwait(false);
        return ok;
    }

    /// <summary>Shared typed-failure path for the read / delete / lifecycle ops —
    /// key-free NOT_FOUND / PLATFORM_ERROR (via the same 404 heuristic), or the
    /// first-class <c>capability_unsupported</c> code, + one FAILED event.</summary>
    private async Task<GitMediationResult> ReadFailAsync(
        Guid? tenantId, string repo, string operation, string failedEventType, string correlationId,
        string credentialSource, PlatformFailure failure, object data, CancellationToken ct)
    {
        var failCode = failure.CapabilityUnsupported
            ? GitFailureCodes.CapabilityUnsupported
            : MapReadFailure(failure.Reason);
        var fail = new GitMediationResult
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

    // ===================================================================
    // Story 31-13 — PR-lifecycle verb cores (guard → driver → platform →
    // exactly one terminal event). Failures route through the shared
    // ReadFailAsync helper (key-free NOT_FOUND / PLATFORM_ERROR /
    // capability_unsupported + one FAILED event).
    // ===================================================================

    private async Task<GitMediationResult> ClosePullRequestCoreAsync(Guid? tenantId, string repo, int prNumber, ClosePrRequest body, CancellationToken ct)
    {
        var op = GitEventTypes.PrCloseOperation;

        var gate = await GuardOrDenyAsync(tenantId, repo, op, GitEventTypes.PrClosedFailed, body.CorrelationId, ct).ConfigureAwait(false);
        if (gate is not null) return gate;

        var cred = await ResolveClientAsync(tenantId, ct).ConfigureAwait(false);
        if (cred is null)
            return await TokenUnavailableAsync(tenantId, repo, op, GitEventTypes.PrClosedFailed, body.CorrelationId, ct).ConfigureAwait(false);

        var (owner, repoName) = GitRepoName.Split(repo);
        var res = await cred.Client.ClosePullRequestAsync(
            owner, repoName, prNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), ct).ConfigureAwait(false);

        if (res is not PlatformResult<PModels.PullRequest>.Ok resOk)
            return await ReadFailAsync(tenantId, repo, op, GitEventTypes.PrClosedFailed, body.CorrelationId, cred.Source, Describe(res), new { prNumber }, ct).ConfigureAwait(false);

        var ok = new GitMediationResult
        {
            Success = true,
            CredentialSource = cred.Source,
            Outcome = "Closed",
            PrNumber = int.TryParse(resOk.Value.Number, out var n) ? n : prNumber,
            PrState = PrStateWire(resOk.Value.State),
            CorrelationId = body.CorrelationId,
        };
        await EmitAsync(GitEventTypes.PrClosedSuccess, op, tenantId, repo, body.CorrelationId, cred.Source, null,
            new { prNumber, state = ok.PrState }, ct).ConfigureAwait(false);
        return ok;
    }

    private async Task<GitMediationResult> ReopenPullRequestCoreAsync(Guid? tenantId, string repo, int prNumber, ReopenPrRequest body, CancellationToken ct)
    {
        var op = GitEventTypes.PrReopenOperation;

        var gate = await GuardOrDenyAsync(tenantId, repo, op, GitEventTypes.PrReopenedFailed, body.CorrelationId, ct).ConfigureAwait(false);
        if (gate is not null) return gate;

        var cred = await ResolveClientAsync(tenantId, ct).ConfigureAwait(false);
        if (cred is null)
            return await TokenUnavailableAsync(tenantId, repo, op, GitEventTypes.PrReopenedFailed, body.CorrelationId, ct).ConfigureAwait(false);

        var (owner, repoName) = GitRepoName.Split(repo);
        var res = await cred.Client.ReopenPullRequestAsync(
            owner, repoName, prNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), ct).ConfigureAwait(false);

        if (res is not PlatformResult<PModels.PullRequest>.Ok resOk)
            return await ReadFailAsync(tenantId, repo, op, GitEventTypes.PrReopenedFailed, body.CorrelationId, cred.Source, Describe(res), new { prNumber }, ct).ConfigureAwait(false);

        var ok = new GitMediationResult
        {
            Success = true,
            CredentialSource = cred.Source,
            Outcome = "Reopened",
            PrNumber = int.TryParse(resOk.Value.Number, out var n) ? n : prNumber,
            PrState = PrStateWire(resOk.Value.State),
            CorrelationId = body.CorrelationId,
        };
        await EmitAsync(GitEventTypes.PrReopenedSuccess, op, tenantId, repo, body.CorrelationId, cred.Source, null,
            new { prNumber, state = ok.PrState }, ct).ConfigureAwait(false);
        return ok;
    }

    private async Task<GitMediationResult> CommentOnPullRequestCoreAsync(Guid? tenantId, string repo, int prNumber, PrCommentRequest body, CancellationToken ct)
    {
        var op = GitEventTypes.PrCommentOperation;

        var gate = await GuardOrDenyAsync(tenantId, repo, op, GitEventTypes.PrCommentedFailed, body.CorrelationId, ct).ConfigureAwait(false);
        if (gate is not null) return gate;

        var cred = await ResolveClientAsync(tenantId, ct).ConfigureAwait(false);
        if (cred is null)
            return await TokenUnavailableAsync(tenantId, repo, op, GitEventTypes.PrCommentedFailed, body.CorrelationId, ct).ConfigureAwait(false);

        var (owner, repoName) = GitRepoName.Split(repo);
        // A PR IS an issue on every supported platform's comment surface —
        // reuse the issue-comment verb with the PR number.
        var res = await cred.Client.CreateIssueCommentAsync(
            owner, repoName, prNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), body.Body, ct).ConfigureAwait(false);

        if (res is not PlatformResult<PModels.IssueComment>.Ok)
            return await ReadFailAsync(tenantId, repo, op, GitEventTypes.PrCommentedFailed, body.CorrelationId, cred.Source, Describe(res), new { prNumber }, ct).ConfigureAwait(false);

        var ok = new GitMediationResult
        {
            Success = true,
            CredentialSource = cred.Source,
            Outcome = "Commented",
            PrNumber = prNumber,
            CorrelationId = body.CorrelationId,
        };
        await EmitAsync(GitEventTypes.PrCommentedSuccess, op, tenantId, repo, body.CorrelationId, cred.Source, null,
            new { prNumber }, ct).ConfigureAwait(false);
        return ok;
    }

    private async Task<GitMediationResult> ReviewCommentOnPullRequestCoreAsync(Guid? tenantId, string repo, int prNumber, PrReviewCommentRequest body, CancellationToken ct)
    {
        var op = GitEventTypes.PrReviewCommentOperation;

        var gate = await GuardOrDenyAsync(tenantId, repo, op, GitEventTypes.PrReviewCommentedFailed, body.CorrelationId, ct).ConfigureAwait(false);
        if (gate is not null) return gate;

        var cred = await ResolveClientAsync(tenantId, ct).ConfigureAwait(false);
        if (cred is null)
            return await TokenUnavailableAsync(tenantId, repo, op, GitEventTypes.PrReviewCommentedFailed, body.CorrelationId, ct).ConfigureAwait(false);

        var (owner, repoName) = GitRepoName.Split(repo);
        var prText = prNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // ── Epic 31 P5 M2 (DG-2) — the §4 check step at mediation altitude:
        // a driver that positively lacks PrFileReview never even attempts the
        // anchored post; the alternative step (plain PR comment carrying
        // file:line) runs directly. The feedback is NEVER dropped.
        if (!cred.Capabilities.Contains(PlatformCapability.PrFileReview))
        {
            return await DowngradeReviewCommentAsync(
                tenantId, repo, prNumber, body, cred, owner, repoName, prText,
                reason: "capability_unsupported", ct).ConfigureAwait(false);
        }

        // Anchor SHA: the caller's commit id, else the PR head branch tip (the
        // live path's head-SHA fallback, reproduced over the abstraction).
        var commitSha = body.CommitId;
        if (string.IsNullOrWhiteSpace(commitSha))
        {
            var prRead = await cred.Client.GetPullRequestAsync(owner, repoName, prText, ct).ConfigureAwait(false);
            if (prRead is not PlatformResult<PModels.PullRequest>.Ok prOk)
                return await ReadFailAsync(tenantId, repo, op, GitEventTypes.PrReviewCommentedFailed, body.CorrelationId, cred.Source, Describe(prRead), new { prNumber }, ct).ConfigureAwait(false);

            var headRead = await cred.Client.GetBranchAsync(owner, repoName, prOk.Value.SourceBranch, ct).ConfigureAwait(false);
            if (headRead is not PlatformResult<PModels.Branch>.Ok headOk)
                return await ReadFailAsync(tenantId, repo, op, GitEventTypes.PrReviewCommentedFailed, body.CorrelationId, cred.Source, Describe(headRead), new { prNumber }, ct).ConfigureAwait(false);
            commitSha = headOk.Value.Sha;
        }

        var res = await cred.Client.CreatePullRequestReviewCommentAsync(
            new PModels.CreatePullRequestReviewCommentRequest(
                owner, repoName, prText, body.Path, body.Line, body.Body, commitSha!, body.Side), ct).ConfigureAwait(false);

        if (res is not PlatformResult<PModels.IssueComment>.Ok resOk)
        {
            // §4.3 safety net + anchoring failure: the typed
            // capability_unsupported refusal (stale/lying probe) and the
            // platform's anchor rejection (InvalidRequest — e.g. line not in
            // the diff, GitLab position 400) DOWNGRADE to a plain comment.
            // Auth / not-found / rate-limit / transport failures stay REAL
            // failures (§4.5 — never mis-classify a real failure as
            // degradation).
            if (res is PlatformResult<PModels.IssueComment>.Failed anchoredFail
                && anchoredFail.Error is PlatformError.InvalidRequest)
            {
                var reason = PlatformErrorText.IsCapabilityUnsupported(anchoredFail.Error)
                    ? "capability_unsupported"
                    : "anchoring_failed";
                return await DowngradeReviewCommentAsync(
                    tenantId, repo, prNumber, body, cred, owner, repoName, prText,
                    reason, ct).ConfigureAwait(false);
            }
            return await ReadFailAsync(tenantId, repo, op, GitEventTypes.PrReviewCommentedFailed, body.CorrelationId, cred.Source, Describe(res), new { prNumber }, ct).ConfigureAwait(false);
        }

        var ok = new GitMediationResult
        {
            Success = true,
            CredentialSource = cred.Source,
            Outcome = "Commented",
            PrNumber = prNumber,
            CommentId = int.TryParse(resOk.Value.Id, out var cid) ? cid : 0,
            CorrelationId = body.CorrelationId,
        };
        await EmitAsync(GitEventTypes.PrReviewCommentedSuccess, op, tenantId, repo, body.CorrelationId, cred.Source, null,
            new { prNumber, commentId = ok.CommentId }, ct).ConfigureAwait(false);
        return ok;
    }

    /// <summary>
    /// Epic 31 P5 M2 (DG-2) — the review-comment alternative step: post the
    /// SAME feedback as a plain PR comment carrying <c>file:line</c> in the
    /// body, emit the GIT.PR_REVIEW_COMMENT.DOWNGRADED audit event (§4.4 —
    /// silent downgrades are forbidden), then the normal terminal success
    /// event. If even the plain comment fails, that is a REAL loud failure —
    /// the feedback is never silently dropped.
    /// </summary>
    private async Task<GitMediationResult> DowngradeReviewCommentAsync(
        Guid? tenantId, string repo, int prNumber, PrReviewCommentRequest body,
        ResolvedClient cred, string owner, string repoName, string prText,
        string reason, CancellationToken ct)
    {
        var op = GitEventTypes.PrReviewCommentOperation;
        var downgradedBody = BuildDowngradedReviewCommentBody(body.Path, body.Line, body.Body);

        var posted = await cred.Client.CreateIssueCommentAsync(
            owner, repoName, prText, downgradedBody, ct).ConfigureAwait(false);
        if (posted is not PlatformResult<PModels.IssueComment>.Ok postedOk)
            return await ReadFailAsync(tenantId, repo, op, GitEventTypes.PrReviewCommentedFailed, body.CorrelationId, cred.Source, Describe(posted), new { prNumber, downgraded = true }, ct).ConfigureAwait(false);

        await EmitAsync(GitEventTypes.PrReviewCommentDowngraded, op, tenantId, repo, body.CorrelationId, cred.Source, null,
            new { prNumber, reason, path = body.Path, line = body.Line }, ct).ConfigureAwait(false);

        var ok = new GitMediationResult
        {
            Success = true,
            CredentialSource = cred.Source,
            Outcome = "Commented",
            PrNumber = prNumber,
            CommentId = int.TryParse(postedOk.Value.Id, out var cid) ? cid : 0,
            ReviewCommentDowngraded = true,
            CorrelationId = body.CorrelationId,
        };
        await EmitAsync(GitEventTypes.PrReviewCommentedSuccess, op, tenantId, repo, body.CorrelationId, cred.Source, null,
            new { prNumber, commentId = ok.CommentId, downgraded = true }, ct).ConfigureAwait(false);
        return ok;
    }

    /// <summary>The downgraded body keeps the anchor visible to humans:
    /// <c>**Review note for `path:line`**</c> + the original feedback.
    /// Public for the degradation-pair tests.</summary>
    public static string BuildDowngradedReviewCommentBody(string path, int line, string body) =>
        $"**Review note for `{path}:{line}`** _(line-anchored comment unavailable; posted as a plain comment)_\n\n{body}";

    private async Task<GitMediationResult> RequestPullRequestReviewersCoreAsync(Guid? tenantId, string repo, int prNumber, PrReviewersRequest body, CancellationToken ct)
    {
        var op = GitEventTypes.PrReviewersRequestOperation;

        var gate = await GuardOrDenyAsync(tenantId, repo, op, GitEventTypes.PrReviewersRequestedFailed, body.CorrelationId, ct).ConfigureAwait(false);
        if (gate is not null) return gate;

        var cred = await ResolveClientAsync(tenantId, ct).ConfigureAwait(false);
        if (cred is null)
            return await TokenUnavailableAsync(tenantId, repo, op, GitEventTypes.PrReviewersRequestedFailed, body.CorrelationId, ct).ConfigureAwait(false);

        var (owner, repoName) = GitRepoName.Split(repo);
        var res = await cred.Client.RequestReviewersAsync(
            new PModels.RequestReviewersRequest(
                owner, repoName, prNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), body.Reviewers), ct).ConfigureAwait(false);

        if (res is not PlatformResult<PModels.PullRequest>.Ok)
            return await ReadFailAsync(tenantId, repo, op, GitEventTypes.PrReviewersRequestedFailed, body.CorrelationId, cred.Source, Describe(res), new { prNumber }, ct).ConfigureAwait(false);

        var ok = new GitMediationResult
        {
            Success = true,
            CredentialSource = cred.Source,
            Outcome = "ReviewersRequested",
            PrNumber = prNumber,
            CorrelationId = body.CorrelationId,
        };
        await EmitAsync(GitEventTypes.PrReviewersRequestedSuccess, op, tenantId, repo, body.CorrelationId, cred.Source, null,
            new { prNumber, reviewerCount = body.Reviewers.Count }, ct).ConfigureAwait(false);
        return ok;
    }

    private async Task<GitMediationResult> UpdatePullRequestLabelsCoreAsync(Guid? tenantId, string repo, int prNumber, PrLabelsRequest body, CancellationToken ct)
    {
        var op = GitEventTypes.PrLabelsUpdateOperation;

        var gate = await GuardOrDenyAsync(tenantId, repo, op, GitEventTypes.PrLabelsUpdatedFailed, body.CorrelationId, ct).ConfigureAwait(false);
        if (gate is not null) return gate;

        var cred = await ResolveClientAsync(tenantId, ct).ConfigureAwait(false);
        if (cred is null)
            return await TokenUnavailableAsync(tenantId, repo, op, GitEventTypes.PrLabelsUpdatedFailed, body.CorrelationId, ct).ConfigureAwait(false);

        var (owner, repoName) = GitRepoName.Split(repo);
        var prText = prNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // D2 — add then remove in ONE op; labels ride the issue side of a PR.
        // The first failure surfaces a loud typed failure (no partial success).
        if (body.AddLabels is { Count: > 0 })
        {
            var added = await cred.Client.AddPullRequestLabelsAsync(
                new PModels.AddPullRequestLabelsRequest(owner, repoName, prText, body.AddLabels), ct).ConfigureAwait(false);
            if (added is not PlatformResult<PModels.PullRequest>.Ok)
                return await ReadFailAsync(tenantId, repo, op, GitEventTypes.PrLabelsUpdatedFailed, body.CorrelationId, cred.Source, Describe(added), new { prNumber }, ct).ConfigureAwait(false);
        }

        if (body.RemoveLabels is { Count: > 0 })
        {
            foreach (var label in body.RemoveLabels)
            {
                var removed = await cred.Client.RemovePullRequestLabelAsync(owner, repoName, prText, label, ct).ConfigureAwait(false);
                if (removed is not PlatformResult<PModels.PullRequest>.Ok)
                    return await ReadFailAsync(tenantId, repo, op, GitEventTypes.PrLabelsUpdatedFailed, body.CorrelationId, cred.Source, Describe(removed), new { prNumber }, ct).ConfigureAwait(false);
            }
        }

        var ok = new GitMediationResult
        {
            Success = true,
            CredentialSource = cred.Source,
            Outcome = "LabelsUpdated",
            PrNumber = prNumber,
            CorrelationId = body.CorrelationId,
        };
        await EmitAsync(GitEventTypes.PrLabelsUpdatedSuccess, op, tenantId, repo, body.CorrelationId, cred.Source, null,
            new { prNumber }, ct).ConfigureAwait(false);
        return ok;
    }

    private async Task<GitMediationResult> SetPullRequestDraftCoreAsync(Guid? tenantId, string repo, int prNumber, PrDraftRequest body, CancellationToken ct)
    {
        var op = GitEventTypes.PrDraftSetOperation;

        var gate = await GuardOrDenyAsync(tenantId, repo, op, GitEventTypes.PrDraftSetFailed, body.CorrelationId, ct).ConfigureAwait(false);
        if (gate is not null) return gate;

        var cred = await ResolveClientAsync(tenantId, ct).ConfigureAwait(false);
        if (cred is null)
            return await TokenUnavailableAsync(tenantId, repo, op, GitEventTypes.PrDraftSetFailed, body.CorrelationId, ct).ConfigureAwait(false);

        var (owner, repoName) = GitRepoName.Split(repo);
        var res = await cred.Client.SetDraftAsync(
            new PModels.SetPullRequestDraftRequest(
                owner, repoName, prNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), body.Draft), ct).ConfigureAwait(false);

        if (res is not PlatformResult<PModels.PullRequest>.Ok resOk)
            return await ReadFailAsync(tenantId, repo, op, GitEventTypes.PrDraftSetFailed, body.CorrelationId, cred.Source, Describe(res), new { prNumber }, ct).ConfigureAwait(false);

        var ok = new GitMediationResult
        {
            Success = true,
            CredentialSource = cred.Source,
            Outcome = "DraftSet",
            PrNumber = int.TryParse(resOk.Value.Number, out var n) ? n : prNumber,
            IsDraft = resOk.Value.IsDraft,
            CorrelationId = body.CorrelationId,
        };
        await EmitAsync(GitEventTypes.PrDraftSetSuccess, op, tenantId, repo, body.CorrelationId, cred.Source, null,
            new { prNumber, isDraft = resOk.Value.IsDraft }, ct).ConfigureAwait(false);
        return ok;
    }

    // ===================================================================
    // Guard / token-unavailable shared paths
    // ===================================================================

    /// <summary>Run the cross-tenant guard. On deny, emit the terminal FAILED
    /// event and return the 403 result; the platform is NEVER called and no
    /// driver is resolved. On allow, returns null so the caller proceeds.</summary>
    private async Task<GitMediationResult?> GuardOrDenyAsync(
        Guid? tenantId, string repo, string operation, string failedEventType, string correlationId, CancellationToken ct)
    {
        var authz = await _authorizer.AuthorizeAsync(tenantId, repo, ct).ConfigureAwait(false);
        if (authz.Allowed) return null;

        var result = new GitMediationResult
        {
            Success = false,
            Outcome = "Error",
            FailureCode = GitFailureCodes.RepoNotAuthorized,
            FailureReason = authz.Reason,
            CorrelationId = correlationId,
        };
        // credentialSource is null — no driver was resolved (fail-closed).
        await EmitAsync(failedEventType, operation, tenantId, repo, correlationId, credentialSource: null,
            GitFailureCodes.RepoNotAuthorized, new { }, ct).ConfigureAwait(false);
        return result;
    }

    private async Task<GitMediationResult> TokenUnavailableAsync(
        Guid? tenantId, string repo, string operation, string failedEventType, string correlationId, CancellationToken ct)
    {
        var result = new GitMediationResult
        {
            Success = false,
            Outcome = "Error",
            FailureCode = GitFailureCodes.TokenUnavailable,
            FailureReason = "the per-tenant git token could not be resolved",
            CorrelationId = correlationId,
        };
        await EmitAsync(failedEventType, operation, tenantId, repo, correlationId, credentialSource: null,
            GitFailureCodes.TokenUnavailable, new { }, ct).ConfigureAwait(false);
        return result;
    }

    // ===================================================================
    // DCB audit (exactly one terminal GIT.* event per call)
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
            // AC7 / logging-requirements: an append failure is logged at ERROR, NOT
            // swallowed into a lost result — the mediation result still returns.
            _logger.LogError(ex,
                "GIT.* event append failed (type={Type}); the mediation result still returns. correlationId={CorrelationId}, repo={Repo}, tenantId={TenantId}",
                eventType, LogSanitizer.Clean(correlationId), LogSanitizer.Clean(repo), tenantId);
        }
    }

    // ===================================================================
    // Failure-code mapping (activity classification → coarse, key-free wire code)
    // ===================================================================

    private static string MapBranchFailure(string? code) => (code ?? string.Empty).ToLowerInvariant() switch
    {
        "branch_exists" or "conflict_exhausted" => GitFailureCodes.GitConflict,
        "base_branch_not_found" => GitFailureCodes.NotFound,
        _ => GitFailureCodes.PlatformError,
    };

    private static string MapPrFailure(string? code) => (code ?? string.Empty).ToLowerInvariant() switch
    {
        "merge-conflict" or "pr-already-exists" => GitFailureCodes.GitConflict,
        "not-found" => GitFailureCodes.NotFound,
        _ => GitFailureCodes.PlatformError,
    };

    private static string MapMergeFailure(string? code) => (code ?? string.Empty).ToLowerInvariant() switch
    {
        "merge_conflict" => GitFailureCodes.GitConflict,
        "not_mergeable" => GitFailureCodes.NotMergeable,
        _ => GitFailureCodes.PlatformError,
    };

    private static string MapIssueFailure(string? reason)
    {
        var code = UpdateIssueStatusActivity.ClassifyError(reason);
        return code switch
        {
            "issue-not-found" => GitFailureCodes.NotFound,
            _ => GitFailureCodes.PlatformError,
        };
    }

    private static string MapReadFailure(string? reason)
    {
        var status = ParsePlatformStatus(reason);
        return status == 404 ? GitFailureCodes.NotFound : GitFailureCodes.PlatformError;
    }

    /// <summary>Best-effort extraction of a leading numeric HTTP status from the
    /// legacy status-prefixed error (e.g. <c>"403: ..."</c>). Null
    /// when the message carries no numeric prefix (the coarse failureCode still
    /// classifies it).</summary>
    private static int? ParsePlatformStatus(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return null;
        var colon = reason.IndexOf(':');
        var head = colon > 0 ? reason[..colon] : reason;
        return int.TryParse(head.Trim(), out var status) && status is >= 100 and < 600 ? status : null;
    }
}
