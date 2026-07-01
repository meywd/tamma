namespace Tamma.Api.Services.Git;

/// <summary>
/// Story 38-1 — the managed git execution layer behind the
/// <c>/api/v1/git/{repo}/...</c> endpoints. Composes the rule-1 sequence ENTIRELY
/// inside <c>Tamma.Api</c> (the only place the git token lives): cross-tenant
/// guard → per-tenant token resolution (BYOK→platform) → platform call with the
/// RESOLVED token → exactly-one terminal DCB audit event. ALWAYS returns a typed,
/// key-free <see cref="GitMediationResult"/> — a failure never throws a raw 5xx.
/// </summary>
public interface IGitMediationService
{
    Task<GitMediationResult> CreateBranchAsync(Guid? tenantId, string repo, CreateBranchRequest body, CancellationToken ct = default);
    Task<GitMediationResult> CreatePullRequestAsync(Guid? tenantId, string repo, CreatePrRequest body, CancellationToken ct = default);
    Task<GitMediationResult> MergePullRequestAsync(Guid? tenantId, string repo, int prNumber, MergePrRequest body, CancellationToken ct = default);
    Task<GitMediationResult> UpdateIssueAsync(Guid? tenantId, string repo, int issueNumber, UpdateIssueRequest body, CancellationToken ct = default);
    Task<GitMediationResult> GetPullRequestCommentsAsync(Guid? tenantId, string repo, int prNumber, string correlationId, CancellationToken ct = default);
}
