using Microsoft.AspNetCore.Http;

namespace Tamma.Api.Services.Git;

/// <summary>
/// Story 38-1 (AC4/AC6) — the single normalized, KEY-FREE result the git
/// endpoints return. Only <see cref="CredentialSource"/> (the label
/// <c>byok</c>/<c>platform</c>) is ever present; the resolved token NEVER appears
/// here (nor in any log or DCB event). The mirroring engine-side wire type is
/// <c>Tamma.Activities.LlmCall.Models.GitCallResponse</c>.
/// </summary>
public sealed record GitMediationResult
{
    public bool Success { get; init; }

    /// <summary>"byok" | "platform" — the credential label, NEVER the token.</summary>
    public string? CredentialSource { get; init; }

    /// <summary>
    /// Operation-specific Elsa outcome the thin activity routes on
    /// (e.g. "Created" / "Updated" / "Merged" / "MergedWithWarnings" / "Done" /
    /// "Error"). Lets the activity reproduce its exact edge without re-deriving it.
    /// </summary>
    public string? Outcome { get; init; }

    // ── branch ──
    public string? BranchRef { get; init; }
    public string? BaseSha { get; init; }
    public bool? ConflictResolved { get; init; }

    // ── pull request ──
    public int? PrNumber { get; init; }
    public string? PrUrl { get; init; }
    public bool? Reused { get; init; }
    public bool? IsDraft { get; init; }

    // ── pr lifecycle read-backs (Story 31-13) ──
    /// <summary>The PR's state (open | closed) after a close/reopen verb.</summary>
    public string? PrState { get; init; }

    /// <summary>The id of a posted review comment (Story 31-13 review-comment verb).</summary>
    public int? CommentId { get; init; }

    // ── pr details read (Story 43-12 — merge-target key selection) ──
    /// <summary>The PR's base/target branch (the branch being merged into). Set by
    /// <see cref="IGitMediationService.GetPullRequestAsync"/>; null when unreadable
    /// (the selector then fails closed to git.merge.main).</summary>
    public string? TargetBranch { get; init; }

    // ── merge ──
    public bool? Merged { get; init; }
    public string? MergeSha { get; init; }
    public bool? IssueClosed { get; init; }
    public bool? BranchDeleted { get; init; }
    public bool? AlreadyMerged { get; init; }

    // ── issue ──
    public string? IssueStatus { get; init; }

    // ── comments ──
    public IReadOnlyList<PrCommentDto>? Comments { get; init; }

    // ── commits / file-changes reads (Story 38 Phase 1 — GitHub extra ops) ──
    public IReadOnlyList<GitCommitDto>? Commits { get; init; }
    public IReadOnlyList<GitFileChangeDto>? FileChanges { get; init; }

    // ── release (Epic 38 follow-up #21 — deployment-pipeline release step) ──
    public long? ReleaseId { get; init; }
    public string? ReleaseUrl { get; init; }
    public string? ReleaseTag { get; init; }

    // ── failure-only (key-free) ──
    public string? FailureCode { get; init; }   // REPO_NOT_AUTHORIZED | GIT_CONFLICT | NOT_MERGEABLE | NOT_FOUND | PLATFORM_ERROR | GIT_TOKEN_UNAVAILABLE
    public string? FailureReason { get; init; }  // key-free
    public int? PlatformStatusCode { get; init; }

    public string? CorrelationId { get; init; }
}

/// <summary>A key-free PR review comment projection.</summary>
public sealed record PrCommentDto
{
    public long Id { get; init; }
    public string Body { get; init; } = string.Empty;
    public string? Path { get; init; }
    public int? Line { get; init; }
    public string Author { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
}

/// <summary>A key-free commit projection (Story 38 Phase 1). Mirrors the engine
/// wire type <c>Tamma.Activities.LlmCall.Models.GitCommitSummaryDto</c>.</summary>
public sealed record GitCommitDto
{
    public string Sha { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; }
    public int Additions { get; init; }
    public int Deletions { get; init; }
    public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();
}

/// <summary>A key-free file-change projection (Story 38 Phase 1). Mirrors the engine
/// wire type <c>Tamma.Activities.LlmCall.Models.GitFileChangeDto</c>.</summary>
public sealed record GitFileChangeDto
{
    public string FilePath { get; init; } = string.Empty;
    public string ChangeType { get; init; } = string.Empty;
    public int Additions { get; init; }
    public int Deletions { get; init; }
}

/// <summary>
/// Story 38-1 (AC6) — the HTTP-status decision. Mirrors the Story 32-5 mapper's
/// discipline but with the git story's explicit real-status semantics:
/// <list type="bullet">
///   <item>success ⇒ 200</item>
///   <item>expected platform failure (200 <c>success:false</c> + preserved
///     <c>platformStatusCode</c>) ⇒ 200 so the workflow branches on the outcome</item>
///   <item><c>REPO_NOT_AUTHORIZED</c> ⇒ 403 (the cross-tenant guard, fail-closed)</item>
///   <item><c>GIT_TOKEN_UNAVAILABLE</c> ⇒ 503 (credential unresolvable, fail-closed)</item>
/// </list>
/// A raw 5xx is NEVER produced.
/// </summary>
public static class GitMediationResultExtensions
{
    public static IResult ToHttpResult(this GitMediationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Success)
        {
            return Results.Ok(result);
        }

        return result.FailureCode switch
        {
            GitFailureCodes.RepoNotAuthorized => Results.Json(result, statusCode: StatusCodes.Status403Forbidden),
            GitFailureCodes.TokenUnavailable => Results.Json(result, statusCode: StatusCodes.Status503ServiceUnavailable),
            // Expected platform failures ride inside 200 success:false so the ADL
            // workflow can branch on the outcome (preserved platformStatusCode).
            _ => Results.Ok(result),
        };
    }
}
