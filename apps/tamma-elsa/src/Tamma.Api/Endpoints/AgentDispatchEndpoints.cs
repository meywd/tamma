using System.Globalization;
using Microsoft.AspNetCore.Http;
using Tamma.Api.Services.AgentDispatch;
using Tamma.Data;

namespace Tamma.Api.Endpoints;

/// <summary>
/// Story 38-2 (AC1) — the internal, engine-only agent-dispatch mediation
/// endpoints (<c>/api/v1/agent-dispatch/{owner}/{repo}/...</c>). They mirror the
/// Story 38-1 git plane and the Story 32-5 <c>/api/v1/llm/call</c> plane exactly:
/// <list type="bullet">
///   <item><b>Auth</b> — the <c>EngineServiceOnly</c> policy (the engine posts the
///     service-scope <c>Tamma:ApiToken</c> Bearer via <c>TammaEngineAuthHandler</c>).
///     A missing/invalid bearer ⇒ 401; a user JWT ⇒ 403 — both BEFORE the handler.</item>
///   <item><b>Tenant scope</b> — the acting tenant is the auth-derived
///     <see cref="ITenantContext"/> (X-Tenant-Id), NEVER the request body.</item>
///   <item><b>Composition</b> — delegates to <see cref="IAgentDispatchMediationService"/>
///     (cross-tenant guard → platform call via the Octokit client, which mints the
///     per-repo installation token internally → one DCB audit event), then projects
///     the typed key-free result via <c>ToHttpResult()</c> (200 / 200 success:false
///     with a preserved platformStatusCode / 403 REPO_NOT_AUTHORIZED — never a raw
///     5xx; there is no 503 token path because the token is minted internally).</item>
/// </list>
///
/// <para><c>{owner}/{repo}</c> is bound as two route segments (an
/// <c>owner/name</c> full name carries a slash); the endpoints reconstruct the
/// <c>owner/name</c> repo string the guard/platform layer expects.</para>
///
/// <para>The monitor's poll LOOP stays engine-side: <c>GET .../runs</c> (discover)
/// and <c>GET .../runs/{id}</c> (poll) are single-shot status reads the engine
/// loops over; the endpoints never block for the ~35-minute run. The inbound
/// <c>workflow_run.completed</c> webhook is out of scope (design §5.3, unchanged).</para>
/// </summary>
public static class AgentDispatchEndpoints
{
    public static async Task<IResult> TriggerRun(
        string owner, string repo, DispatchAgentRunRequest body,
        ITenantContext tenantContext, IAgentDispatchMediationService dispatch, CancellationToken ct)
    {
        var result = await dispatch.TriggerRunAsync(tenantContext.TenantId, Repo(owner, repo), body, ct).ConfigureAwait(false);
        return result.ToHttpResult();
    }

    public static async Task<IResult> DiscoverRun(
        string owner, string repo, string branch, string? createdAfter, string? correlationId,
        ITenantContext tenantContext, IAgentDispatchMediationService dispatch, CancellationToken ct)
    {
        var result = await dispatch.DiscoverRunAsync(
            tenantContext.TenantId, Repo(owner, repo), branch, ParseCreatedAfterUtc(createdAfter), correlationId, ct)
            .ConfigureAwait(false);
        return result.ToHttpResult();
    }

    /// <summary>
    /// Review-session 2026-06-30 finding 1 (TZ bug): bind <c>createdAfter</c> as a raw
    /// string and parse it to a <see cref="DateTimeKind.Utc"/> instant end-to-end. The
    /// default minimal-API <c>DateTime</c> binding parses a <c>Z</c>-suffixed value to
    /// Kind=Local, which the downstream GitHub <c>created:&gt;=</c> formatter then stamps
    /// with a literal <c>Z</c> — shifting the window into the future on a non-UTC host.
    /// <see cref="DateTimeStyles.AssumeUniversal"/> (no-offset ⇒ UTC) +
    /// <see cref="DateTimeStyles.AdjustToUniversal"/> (offset/Z ⇒ convert to UTC) yields
    /// the correct instant with Kind=Utc regardless of host TZ. A missing/unparseable
    /// value falls back to the Unix epoch — the filter then excludes nothing, so
    /// discovery still finds the just-dispatched run (the most recent on the branch);
    /// it must NEVER over-filter into the future.
    /// </summary>
    internal static DateTime ParseCreatedAfterUtc(string? raw)
    {
        if (!string.IsNullOrWhiteSpace(raw)
            && DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return parsed;
        }
        return DateTime.UnixEpoch;
    }

    public static async Task<IResult> GetRun(
        string owner, string repo, long id, string? correlationId,
        ITenantContext tenantContext, IAgentDispatchMediationService dispatch, CancellationToken ct)
    {
        var result = await dispatch.GetRunAsync(
            tenantContext.TenantId, Repo(owner, repo), id, correlationId, ct).ConfigureAwait(false);
        return result.ToHttpResult();
    }

    public static async Task<IResult> CollectResults(
        string owner, string repo, long id, string? branch, string? conclusion, string? agentProvider,
        int? durationSeconds, string? correlationId,
        ITenantContext tenantContext, IAgentDispatchMediationService dispatch, CancellationToken ct)
    {
        var body = new CollectAgentRunRequest
        {
            BranchName = branch ?? string.Empty,
            Conclusion = conclusion ?? string.Empty,
            AgentProvider = string.IsNullOrWhiteSpace(agentProvider) ? "claude-code" : agentProvider,
            DurationSeconds = durationSeconds ?? 0,
            CorrelationId = correlationId ?? string.Empty,
        };
        var result = await dispatch.CollectResultsAsync(
            tenantContext.TenantId, Repo(owner, repo), id, body, ct).ConfigureAwait(false);
        return result.ToHttpResult();
    }

    public static async Task<IResult> ResolveInstallation(
        string owner, string repo, string? correlationId,
        ITenantContext tenantContext, IAgentDispatchMediationService dispatch, CancellationToken ct)
    {
        var result = await dispatch.ResolveInstallationAsync(
            tenantContext.TenantId, Repo(owner, repo), correlationId, ct).ConfigureAwait(false);
        return result.ToHttpResult();
    }

    private static string Repo(string owner, string repo) => $"{owner}/{repo}";
}
