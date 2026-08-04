namespace Tamma.Api.Services.Git;

// ============================================================
// Story 38-1 — server-side binding records for the git-mediation endpoints.
// Bound from the engine client's camelCase JSON (Program.cs
// ConfigureHttpJsonOptions → JsonNamingPolicy.CamelCase applies to reads too),
// so the PascalCase property names below map from camelCase on the wire. The
// mirroring client-side records live in
// Tamma.Activities/LlmCall/Models/TammaApiModels.cs.
//
// NONE of these records carry a token — the API resolves the per-tenant
// credential server-side (BYOK→platform); the engine holds no git token.
// ============================================================

/// <summary>
/// <c>POST /api/v1/git/{owner}/{repo}/branches</c>. The candidate branch name is
/// generated engine-side (a pure, token-free helper); the API performs the
/// idempotent conflict-resolution + create + validate with the resolved token.
/// </summary>
public sealed record CreateBranchRequest
{
    public string BranchName { get; init; } = string.Empty;
    public string BaseRef { get; init; } = "main";
    public string? ConflictStrategy { get; init; }
    public int IssueNumber { get; init; }
    public string CorrelationId { get; init; } = string.Empty;
}

/// <summary>
/// <c>POST /api/v1/git/{owner}/{repo}/pull-requests</c>. Title / body / labels
/// are composed engine-side (pure, token-free); the API opens or idempotently
/// updates the PR with the resolved token.
/// </summary>
public sealed record CreatePrRequest
{
    public string Title { get; init; } = string.Empty;
    public string? Body { get; init; }
    public string HeadRef { get; init; } = string.Empty;
    public string BaseRef { get; init; } = "main";
    public IReadOnlyList<string>? Labels { get; init; }
    public IReadOnlyList<string>? Reviewers { get; init; }
    public bool IsDraft { get; init; }
    public string CorrelationId { get; init; } = string.Empty;
}

/// <summary>
/// <c>PUT /api/v1/git/{owner}/{repo}/pull-requests/{n}/merge</c> — the
/// highest-risk write. The API performs the pre-merge readiness read, the merge,
/// and the verified post-merge close-issue + branch-delete with the resolved token.
/// </summary>
public sealed record MergePrRequest
{
    public string? MergeStrategy { get; init; } // merge | squash | rebase
    public int IssueNumber { get; init; }
    public string? BranchName { get; init; }
    public bool AutoDeleteBranch { get; init; } = true;
    public bool CloseAssociatedIssue { get; init; } = true;
    public string CorrelationId { get; init; } = string.Empty;
}

/// <summary>
/// <c>PATCH /api/v1/git/{owner}/{repo}/issues/{n}</c>. The status comment body is
/// composed engine-side; the API posts the comment + adds/removes labels with the
/// resolved token.
/// </summary>
public sealed record UpdateIssueRequest
{
    public string? Body { get; init; }
    public IReadOnlyList<string>? AddLabels { get; init; }
    public IReadOnlyList<string>? RemoveLabels { get; init; }
    public string? Status { get; init; } // open | closed (optional; today: no state change)
    public string CorrelationId { get; init; } = string.Empty;
}

/// <summary>
/// <c>POST /api/v1/git/{owner}/{repo}/releases</c> (Epic 38 follow-up #21 —
/// deployment-pipeline release step). The tag / title / notes are composed
/// engine-side (pure, token-free); the API creates the release with the resolved
/// per-tenant token. Carries NO token.
/// </summary>
public sealed record CreateReleaseRequest
{
    public string TagName { get; init; } = string.Empty;
    public string? TargetRef { get; init; }   // SHA / branch the tag is created from
    public string? Name { get; init; }         // release title (empty ⇒ TagName)
    public string? Body { get; init; }         // release notes (Markdown)
    public bool Draft { get; init; }
    public bool Prerelease { get; init; }
    public int IssueNumber { get; init; }
    public string CorrelationId { get; init; } = string.Empty;
}

// ============================================================
// Story 31-13 — the 7 PR-lifecycle verb request records. Each carries a
// CorrelationId; the PR number rides as a route/method arg (like
// MergePullRequestAsync's prNumber), never in the body. None carries a token —
// the API resolves the per-tenant credential server-side.
// ============================================================

/// <summary>
/// <c>POST /api/v1/git/{owner}/{repo}/pull-requests/{n}/close</c> — close an open
/// PR (PATCH state=closed). Reversible via reopen.
/// </summary>
public sealed record ClosePrRequest
{
    public string CorrelationId { get; init; } = string.Empty;
}

/// <summary>
/// <c>POST /api/v1/git/{owner}/{repo}/pull-requests/{n}/reopen</c> — reopen a
/// closed PR (PATCH state=open). The inverse of close.
/// </summary>
public sealed record ReopenPrRequest
{
    public string CorrelationId { get; init; } = string.Empty;
}

/// <summary>
/// <c>POST /api/v1/git/{owner}/{repo}/pull-requests/{n}/comments</c> — post an
/// issue-style comment on the PR (a PR IS an issue on GitHub).
/// </summary>
public sealed record PrCommentRequest
{
    public string Body { get; init; } = string.Empty;
    public string CorrelationId { get; init; } = string.Empty;
}

/// <summary>
/// <c>POST /api/v1/git/{owner}/{repo}/pull-requests/{n}/review-comments</c> — post
/// a review comment anchored to a diff line. When <see cref="CommitId"/> is
/// null/empty the API resolves the PR head SHA.
/// </summary>
public sealed record PrReviewCommentRequest
{
    public string Body { get; init; } = string.Empty;
    public string? CommitId { get; init; }
    public string Path { get; init; } = string.Empty;
    public int Line { get; init; }
    public string Side { get; init; } = "RIGHT";
    public string CorrelationId { get; init; } = string.Empty;
}

/// <summary>
/// <c>POST /api/v1/git/{owner}/{repo}/pull-requests/{n}/reviewers</c> — request
/// reviewers for the PR (failure surfaced, not swallowed).
/// </summary>
public sealed record PrReviewersRequest
{
    public IReadOnlyList<string> Reviewers { get; init; } = Array.Empty<string>();
    public string CorrelationId { get; init; } = string.Empty;
}

/// <summary>
/// <c>PUT /api/v1/git/{owner}/{repo}/pull-requests/{n}/labels</c> — add and/or
/// remove PR labels (a PR IS an issue on GitHub). Design Decision D2: ONE
/// mediation op carrying both arrays; add applies first, then remove.
/// </summary>
public sealed record PrLabelsRequest
{
    public IReadOnlyList<string>? AddLabels { get; init; }
    public IReadOnlyList<string>? RemoveLabels { get; init; }
    public string CorrelationId { get; init; } = string.Empty;
}

/// <summary>
/// <c>PUT /api/v1/git/{owner}/{repo}/pull-requests/{n}/draft</c> — toggle the PR's
/// draft state (GraphQL-backed on GitHub).
/// </summary>
public sealed record PrDraftRequest
{
    public bool Draft { get; init; }
    public string CorrelationId { get; init; } = string.Empty;
}
