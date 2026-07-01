using Microsoft.AspNetCore.Http;
using Tamma.Api.Services.Git;
using Tamma.Data;

namespace Tamma.Api.Endpoints;

/// <summary>
/// Story 38-1 (AC1) — the internal, engine-only git-mediation endpoints
/// (<c>/api/v1/git/{owner}/{repo}/...</c>). They mirror the Story 32-5
/// <c>/api/v1/llm/call</c> plane exactly:
/// <list type="bullet">
///   <item><b>Auth</b> — the <c>EngineServiceOnly</c> policy (the engine posts the
///     service-scope <c>Tamma:ApiToken</c> Bearer via <c>TammaEngineAuthHandler</c>).
///     A missing/invalid bearer ⇒ 401; a user JWT ⇒ 403 — both BEFORE the handler.</item>
///   <item><b>Tenant scope</b> — the acting tenant is the auth-derived
///     <see cref="ITenantContext"/> (X-Tenant-Id), NEVER the request body.</item>
///   <item><b>Composition</b> — delegates to <see cref="IGitMediationService"/>
///     (cross-tenant guard → per-tenant token → platform call with the resolved
///     token → one DCB audit event), then projects the typed key-free
///     <see cref="GitMediationResult"/> via <c>ToHttpResult()</c> (200 / 200
///     success:false + preserved platformStatusCode / 403 REPO_NOT_AUTHORIZED /
///     503 GIT_TOKEN_UNAVAILABLE — never a raw 5xx).</item>
/// </list>
///
/// <para><c>{owner}/{repo}</c> is bound as two route segments (an
/// <c>owner/name</c> full name carries a slash, which a single path segment
/// cannot capture); the endpoints reconstruct the <c>owner/name</c> repo string
/// the guard/token/platform layer expects.</para>
/// </summary>
public static class GitEndpoints
{
    public static async Task<IResult> CreateBranch(
        string owner, string repo, CreateBranchRequest body,
        ITenantContext tenantContext, IGitMediationService git, CancellationToken ct)
    {
        var result = await git.CreateBranchAsync(tenantContext.TenantId, Repo(owner, repo), body, ct).ConfigureAwait(false);
        return result.ToHttpResult();
    }

    public static async Task<IResult> CreatePullRequest(
        string owner, string repo, CreatePrRequest body,
        ITenantContext tenantContext, IGitMediationService git, CancellationToken ct)
    {
        var result = await git.CreatePullRequestAsync(tenantContext.TenantId, Repo(owner, repo), body, ct).ConfigureAwait(false);
        return result.ToHttpResult();
    }

    public static async Task<IResult> MergePullRequest(
        string owner, string repo, int n, MergePrRequest body,
        ITenantContext tenantContext, IGitMediationService git, CancellationToken ct)
    {
        var result = await git.MergePullRequestAsync(tenantContext.TenantId, Repo(owner, repo), n, body, ct).ConfigureAwait(false);
        return result.ToHttpResult();
    }

    public static async Task<IResult> GetPullRequestComments(
        string owner, string repo, int n, string? correlationId,
        ITenantContext tenantContext, IGitMediationService git, CancellationToken ct)
    {
        var result = await git.GetPullRequestCommentsAsync(
            tenantContext.TenantId, Repo(owner, repo), n, correlationId ?? string.Empty, ct).ConfigureAwait(false);
        return result.ToHttpResult();
    }

    public static async Task<IResult> UpdateIssue(
        string owner, string repo, int n, UpdateIssueRequest body,
        ITenantContext tenantContext, IGitMediationService git, CancellationToken ct)
    {
        var result = await git.UpdateIssueAsync(tenantContext.TenantId, Repo(owner, repo), n, body, ct).ConfigureAwait(false);
        return result.ToHttpResult();
    }

    private static string Repo(string owner, string repo) => $"{owner}/{repo}";
}
