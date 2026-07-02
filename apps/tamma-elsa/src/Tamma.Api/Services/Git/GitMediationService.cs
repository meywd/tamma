using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tamma.Activities.ADL;
using Tamma.Core.Interfaces;
using Tamma.Core.Logging;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Git;

/// <summary>
/// Story 38-1 — composes the git-mediation sequence entirely inside
/// <c>Tamma.Api</c>: cross-tenant guard → per-tenant token (BYOK→platform) →
/// platform call with the RESOLVED token → exactly-one terminal DCB event.
///
/// <para>The platform call reuses the existing, well-tested ADL orchestration
/// cores (<c>CreateBranchActivity.ExecuteCoreAsync</c> etc.) but against a
/// TOKEN-BOUND <see cref="IGitHubIntegrationService"/> minted by
/// <see cref="IGitHubClientFactory"/> — so "the token used == the token
/// resolved". The resolved token lives only on that request-scoped service
/// instance; it is NEVER logged, returned, or written to the audit event
/// (only the <c>credentialSource</c> LABEL is surfaced).</para>
/// </summary>
public sealed class GitMediationService : IGitMediationService
{
    private readonly IGitRepoAuthorizer _authorizer;
    private readonly IGitTokenResolver _tokenResolver;
    private readonly IGitHubClientFactory _githubFactory;
    private readonly IEventRepository _events;
    private readonly ILogger<GitMediationService> _logger;

    public GitMediationService(
        IGitRepoAuthorizer authorizer,
        IGitTokenResolver tokenResolver,
        IGitHubClientFactory githubFactory,
        IEventRepository events,
        ILogger<GitMediationService> logger)
    {
        _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        _tokenResolver = tokenResolver ?? throw new ArgumentNullException(nameof(tokenResolver));
        _githubFactory = githubFactory ?? throw new ArgumentNullException(nameof(githubFactory));
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

    public Task<GitMediationResult> GetCommitsAsync(Guid? tenantId, string repo, string branch, DateTime? since, string correlationId, CancellationToken ct = default)
        => ExecuteGuardedAsync(tenantId, repo, GitEventTypes.CommitsReadOperation, GitEventTypes.CommitsReadFailed, correlationId, ct,
            () => GetCommitsCoreAsync(tenantId, repo, branch, since, correlationId, ct));

    public Task<GitMediationResult> GetFileChangesAsync(Guid? tenantId, string repo, string branch, string correlationId, CancellationToken ct = default)
        => ExecuteGuardedAsync(tenantId, repo, GitEventTypes.FileChangesReadOperation, GitEventTypes.FileChangesReadFailed, correlationId, ct,
            () => GetFileChangesCoreAsync(tenantId, repo, branch, correlationId, ct));

    public Task<GitMediationResult> DeleteBranchAsync(Guid? tenantId, string repo, string branchName, string correlationId, CancellationToken ct = default)
        => ExecuteGuardedAsync(tenantId, repo, GitEventTypes.BranchDeleteOperation, GitEventTypes.BranchDeletedFailed, correlationId, ct,
            () => DeleteBranchCoreAsync(tenantId, repo, branchName, correlationId, ct));

    /// <summary>
    /// F3 — run one mediation op body; convert any unexpected exception (DB read,
    /// secret decrypt, client mint, transport) into a typed key-free
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
    // Create branch
    // ===================================================================

    private async Task<GitMediationResult> CreateBranchCoreAsync(Guid? tenantId, string repo, CreateBranchRequest body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        var op = GitEventTypes.BranchCreateOperation;

        var gate = await GuardOrDenyAsync(tenantId, repo, op, GitEventTypes.BranchCreatedFailed, body.CorrelationId, ct).ConfigureAwait(false);
        if (gate is not null) return gate;

        var cred = await _tokenResolver.ResolveAsync(tenantId, repo, ct).ConfigureAwait(false);
        if (cred is null)
            return await TokenUnavailableAsync(tenantId, repo, op, GitEventTypes.BranchCreatedFailed, body.CorrelationId, ct).ConfigureAwait(false);

        var github = _githubFactory.Create(cred.Token);
        var strategy = string.IsNullOrWhiteSpace(body.ConflictStrategy)
            ? CreateBranchActivity.DefaultConflictStrategy
            : body.ConflictStrategy!;

        var outcome = await CreateBranchActivity.ExecuteCoreAsync(
            github, repo, body.IssueNumber, body.BranchName, body.BaseRef, strategy, _logger).ConfigureAwait(false);

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

        var cred = await _tokenResolver.ResolveAsync(tenantId, repo, ct).ConfigureAwait(false);
        if (cred is null)
            return await TokenUnavailableAsync(tenantId, repo, op, GitEventTypes.PrOpenedFailed, body.CorrelationId, ct).ConfigureAwait(false);

        var github = _githubFactory.Create(cred.Token);
        var coreRequest = new CreatePullRequestRequest
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
            github, repo, body.HeadRef, body.BaseRef, body.IsDraft, coreRequest, _logger).ConfigureAwait(false);

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
                CorrelationId = body.CorrelationId,
            };
            await EmitAsync(GitEventTypes.PrOpenedSuccess, op, tenantId, repo, body.CorrelationId, cred.Source, null,
                new { prNumber = outcome.PrNumber, reused = outcome.Reused, outcome = outcome.Outcome }, ct).ConfigureAwait(false);
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

        var cred = await _tokenResolver.ResolveAsync(tenantId, repo, ct).ConfigureAwait(false);
        if (cred is null)
            return await TokenUnavailableAsync(tenantId, repo, op, GitEventTypes.PrMergeFailed, body.CorrelationId, ct).ConfigureAwait(false);

        var github = _githubFactory.Create(cred.Token);
        var strategy = MergePullRequestActivity.NormalizeStrategy(body.MergeStrategy);

        var outcome = await MergePullRequestActivity.ExecuteCoreAsync(
            github, repo, prNumber, body.IssueNumber, body.BranchName ?? string.Empty, strategy,
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
                FailureReason = outcome.FailureReason, // warnings (partial), key-free
                CorrelationId = body.CorrelationId,
            };
            await EmitAsync(GitEventTypes.PrMergedSuccess, op, tenantId, repo, body.CorrelationId, cred.Source, null,
                new { prNumber, issueNumber = body.IssueNumber, alreadyMerged = outcome.AlreadyMerged, outcome = outcome.Outcome }, ct).ConfigureAwait(false);
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

        var cred = await _tokenResolver.ResolveAsync(tenantId, repo, ct).ConfigureAwait(false);
        if (cred is null)
            return await TokenUnavailableAsync(tenantId, repo, op, GitEventTypes.IssueUpdatedFailed, body.CorrelationId, ct).ConfigureAwait(false);

        var github = _githubFactory.Create(cred.Token);

        // Post the status comment, then add / remove labels. The first failure
        // surfaces a loud typed failure (no partial false success).
        if (!string.IsNullOrWhiteSpace(body.Body))
        {
            var comment = await github.PostIssueCommentAsync(repo, issueNumber, body.Body!).ConfigureAwait(false);
            if (!comment.Success)
                return await IssueFailAsync(tenantId, repo, issueNumber, body.CorrelationId, cred.Source, comment.Error, ct).ConfigureAwait(false);
        }

        if (body.AddLabels is { Count: > 0 })
        {
            var added = await github.AddIssueLabelsAsync(repo, issueNumber, body.AddLabels.ToArray()).ConfigureAwait(false);
            if (!added.Success)
                return await IssueFailAsync(tenantId, repo, issueNumber, body.CorrelationId, cred.Source, added.Error, ct).ConfigureAwait(false);
        }

        if (body.RemoveLabels is { Count: > 0 })
        {
            foreach (var label in body.RemoveLabels)
            {
                var removed = await github.RemoveIssueLabelAsync(repo, issueNumber, label).ConfigureAwait(false);
                if (!removed.Success)
                    return await IssueFailAsync(tenantId, repo, issueNumber, body.CorrelationId, cred.Source, removed.Error, ct).ConfigureAwait(false);
            }
        }

        // Optional state transition (today the ADL activity never closes here, but
        // the wire supports it for completeness).
        if (string.Equals(body.Status, "closed", StringComparison.OrdinalIgnoreCase))
        {
            var closed = await github.CloseGitHubIssueAsync(repo, issueNumber).ConfigureAwait(false);
            if (!closed.Success || !closed.Data)
                return await IssueFailAsync(tenantId, repo, issueNumber, body.CorrelationId, cred.Source, closed.Error, ct).ConfigureAwait(false);
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
        Guid? tenantId, string repo, int issueNumber, string correlationId, string credentialSource, string? reason, CancellationToken ct)
    {
        var failCode = MapIssueFailure(reason);
        var fail = new GitMediationResult
        {
            Success = false,
            CredentialSource = credentialSource,
            Outcome = "Failed",
            FailureCode = failCode,
            FailureReason = reason,
            PlatformStatusCode = ParsePlatformStatus(reason),
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

        var cred = await _tokenResolver.ResolveAsync(tenantId, repo, ct).ConfigureAwait(false);
        if (cred is null)
            return await TokenUnavailableAsync(tenantId, repo, op, GitEventTypes.PrCommentsReadFailed, correlationId, ct).ConfigureAwait(false);

        var github = _githubFactory.Create(cred.Token);
        var res = await github.GetPullRequestReviewCommentsAsync(repo, prNumber).ConfigureAwait(false);

        if (!res.Success)
        {
            var failCode = MapReadFailure(res.Error);
            var fail = new GitMediationResult
            {
                Success = false,
                CredentialSource = cred.Source,
                Outcome = "Error",
                FailureCode = failCode,
                FailureReason = res.Error,
                PlatformStatusCode = ParsePlatformStatus(res.Error),
                CorrelationId = correlationId,
            };
            await EmitAsync(GitEventTypes.PrCommentsReadFailed, op, tenantId, repo, correlationId, cred.Source, failCode,
                new { prNumber }, ct).ConfigureAwait(false);
            return fail;
        }

        var comments = (res.Data ?? new List<GitHubReviewComment>())
            .Select(c => new PrCommentDto
            {
                Id = c.Id,
                Body = c.Body,
                Path = c.Path,
                Line = c.Line,
                Author = c.Author,
                CreatedAt = c.CreatedAt,
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
    // GitHub extra ops (Story 38 Phase 1) — commits / file-changes reads + delete
    // ===================================================================

    private async Task<GitMediationResult> GetCommitsCoreAsync(Guid? tenantId, string repo, string branch, DateTime? since, string correlationId, CancellationToken ct)
    {
        var op = GitEventTypes.CommitsReadOperation;

        var gate = await GuardOrDenyAsync(tenantId, repo, op, GitEventTypes.CommitsReadFailed, correlationId, ct).ConfigureAwait(false);
        if (gate is not null) return gate;

        var cred = await _tokenResolver.ResolveAsync(tenantId, repo, ct).ConfigureAwait(false);
        if (cred is null)
            return await TokenUnavailableAsync(tenantId, repo, op, GitEventTypes.CommitsReadFailed, correlationId, ct).ConfigureAwait(false);

        var github = _githubFactory.Create(cred.Token);
        var res = await github.GetGitHubCommitsAsync(repo, branch, since).ConfigureAwait(false);

        if (!res.Success)
            return await ReadFailAsync(tenantId, repo, op, GitEventTypes.CommitsReadFailed, correlationId, cred.Source, res.Error, new { branch }, ct).ConfigureAwait(false);

        var commits = (res.Data ?? new List<GitHubCommit>())
            .Select(c => new GitCommitDto
            {
                Sha = c.Sha,
                Message = c.Message,
                Author = c.Author,
                Timestamp = c.Timestamp,
                Additions = c.Additions,
                Deletions = c.Deletions,
                Files = c.Files.ToList(),
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

        var cred = await _tokenResolver.ResolveAsync(tenantId, repo, ct).ConfigureAwait(false);
        if (cred is null)
            return await TokenUnavailableAsync(tenantId, repo, op, GitEventTypes.FileChangesReadFailed, correlationId, ct).ConfigureAwait(false);

        var github = _githubFactory.Create(cred.Token);
        var res = await github.GetGitHubFileChangesAsync(repo, branch).ConfigureAwait(false);

        if (!res.Success)
            return await ReadFailAsync(tenantId, repo, op, GitEventTypes.FileChangesReadFailed, correlationId, cred.Source, res.Error, new { branch }, ct).ConfigureAwait(false);

        var changes = (res.Data ?? new List<GitHubFileChange>())
            .Select(f => new GitFileChangeDto
            {
                FilePath = f.FilePath,
                ChangeType = f.ChangeType,
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

    private async Task<GitMediationResult> DeleteBranchCoreAsync(Guid? tenantId, string repo, string branchName, string correlationId, CancellationToken ct)
    {
        var op = GitEventTypes.BranchDeleteOperation;

        var gate = await GuardOrDenyAsync(tenantId, repo, op, GitEventTypes.BranchDeletedFailed, correlationId, ct).ConfigureAwait(false);
        if (gate is not null) return gate;

        var cred = await _tokenResolver.ResolveAsync(tenantId, repo, ct).ConfigureAwait(false);
        if (cred is null)
            return await TokenUnavailableAsync(tenantId, repo, op, GitEventTypes.BranchDeletedFailed, correlationId, ct).ConfigureAwait(false);

        var github = _githubFactory.Create(cred.Token);
        var res = await github.DeleteGitHubBranchAsync(repo, branchName).ConfigureAwait(false);

        if (!res.Success)
            return await ReadFailAsync(tenantId, repo, op, GitEventTypes.BranchDeletedFailed, correlationId, cred.Source, res.Error, new { branchName }, ct).ConfigureAwait(false);

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

    /// <summary>Shared typed-failure path for the extra read/delete ops — key-free
    /// PLATFORM_ERROR / NOT_FOUND (via the same 404 heuristic) + one FAILED event.</summary>
    private async Task<GitMediationResult> ReadFailAsync(
        Guid? tenantId, string repo, string operation, string failedEventType, string correlationId,
        string credentialSource, string? reason, object data, CancellationToken ct)
    {
        var failCode = MapReadFailure(reason);
        var fail = new GitMediationResult
        {
            Success = false,
            CredentialSource = credentialSource,
            Outcome = "Error",
            FailureCode = failCode,
            FailureReason = reason,
            PlatformStatusCode = ParsePlatformStatus(reason),
            CorrelationId = correlationId,
        };
        await EmitAsync(failedEventType, operation, tenantId, repo, correlationId, credentialSource, failCode, data, ct).ConfigureAwait(false);
        return fail;
    }

    // ===================================================================
    // Guard / token-unavailable shared paths
    // ===================================================================

    /// <summary>Run the cross-tenant guard. On deny, emit the terminal FAILED
    /// event and return the 403 result; the platform is NEVER called and no token
    /// is resolved. On allow, returns null so the caller proceeds.</summary>
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
        // credentialSource is null — no token was resolved (fail-closed).
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
    /// integration layer's status-prefixed error (e.g. <c>"403: ..."</c>). Null
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
