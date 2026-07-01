namespace Tamma.Api.Services.AgentDispatch;

/// <summary>
/// Story 38-2 — the managed agent-dispatch execution layer behind the
/// <c>/api/v1/agent-dispatch/{owner}/{repo}/...</c> endpoints. Composes the
/// rule-1 sequence ENTIRELY inside <c>Tamma.Api</c> (the only place the GitHub
/// Actions installation token lives): cross-tenant guard (reuses Story 38-1's
/// <see cref="Tamma.Api.Services.Git.IGitRepoAuthorizer"/>) → platform call via
/// the existing <c>OctokitGitHubActionsClient</c> (which mints the per-repo
/// installation token internally) → exactly-one terminal DCB audit event. ALWAYS
/// returns a typed, key-free result — a failure never throws a raw 5xx.
///
/// <para>The outbound dispatch/poll/collect is mediated here; the INBOUND
/// <c>workflow_run.completed</c> webhook + <c>WebhookSignalRegistry</c> signalling
/// stay in-process and are out of scope (design §5.3, unchanged).</para>
/// </summary>
public interface IAgentDispatchMediationService
{
    /// <summary><c>POST .../runs</c> — trigger a <c>workflow_dispatch</c> run.</summary>
    Task<AgentDispatchRunResult> TriggerRunAsync(
        Guid? tenantId, string repo, DispatchAgentRunRequest body, CancellationToken ct = default);

    /// <summary><c>GET .../runs?branch=&amp;createdAfter=</c> — discover the latest
    /// dispatched run for a branch (the monitor's discovery phase, mediated).</summary>
    Task<AgentRunStatusResult> DiscoverRunAsync(
        Guid? tenantId, string repo, string branch, DateTime createdAfter, string? correlationId, CancellationToken ct = default);

    /// <summary><c>GET .../runs/{id}</c> — single-shot status of one run (the
    /// monitor's poll iteration, mediated). The poll LOOP stays engine-side.</summary>
    Task<AgentRunStatusResult> GetRunAsync(
        Guid? tenantId, string repo, long runId, string? correlationId, CancellationToken ct = default);

    /// <summary><c>GET .../runs/{id}/results</c> — aggregate a completed run's
    /// outputs (artifact + PR + compare + check runs) server-side.</summary>
    Task<AgentRunResultsResult> CollectResultsAsync(
        Guid? tenantId, string repo, long runId, CollectAgentRunRequest body, CancellationToken ct = default);

    /// <summary><c>GET .../installation</c> — resolve the GitHub App installation id
    /// owning the repo, used only to scope the inbound webhook-signal wait key.</summary>
    Task<AgentInstallationResult> ResolveInstallationAsync(
        Guid? tenantId, string repo, string? correlationId, CancellationToken ct = default);
}
