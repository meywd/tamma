using Microsoft.AspNetCore.Http;
using Tamma.Api.Services.Ci;
using Tamma.Data;

namespace Tamma.Api.Endpoints;

/// <summary>
/// Story 38 (Phase 1) — the internal, engine-only CI-mediation endpoints
/// (<c>/api/v1/ci/{owner}/{repo}/...</c>). They mirror the Story 38-1 git plane
/// exactly: <c>EngineServiceOnly</c> auth (a missing/invalid bearer ⇒ 401; a user JWT
/// ⇒ 403), the acting tenant is the auth-derived <see cref="ITenantContext"/>
/// (X-Tenant-Id, NEVER the body), and delegation to <see cref="ICiMediationService"/>
/// (guard → per-tenant token → CI call with the resolved token → one DCB event) with
/// the typed key-free result projected via <c>ToHttpResult()</c> (200 / 200
/// success:false + preserved platformStatusCode / 403 REPO_NOT_AUTHORIZED / 503
/// CI_TOKEN_UNAVAILABLE — never a raw 5xx).
///
/// <para><c>{owner}/{repo}</c> is bound as two route segments (an <c>owner/name</c>
/// full name carries a slash).</para>
/// </summary>
public static class CiEndpoints
{
    public static async Task<IResult> TriggerTests(
        string owner, string repo, TriggerTestsRequest body,
        ITenantContext tenantContext, ICiMediationService ci, CancellationToken ct)
    {
        var result = await ci.TriggerTestsAsync(tenantContext.TenantId, Repo(owner, repo), body, ct).ConfigureAwait(false);
        return result.ToHttpResult();
    }

    public static async Task<IResult> GetBuildStatus(
        string owner, string repo, string? branch, string? correlationId,
        ITenantContext tenantContext, ICiMediationService ci, CancellationToken ct)
    {
        var result = await ci.GetBuildStatusAsync(
            tenantContext.TenantId, Repo(owner, repo), branch ?? string.Empty, correlationId ?? string.Empty, ct).ConfigureAwait(false);
        return result.ToHttpResult();
    }

    private static string Repo(string owner, string repo) => $"{owner}/{repo}";
}
