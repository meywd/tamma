namespace Tamma.Api.Services.AgentDispatch;

// ============================================================
// Story 38-2 — server-side binding records for the agent-dispatch mediation
// endpoints. Bound from the engine client's camelCase JSON (Program.cs
// ConfigureHttpJsonOptions → JsonNamingPolicy.CamelCase applies to reads too),
// so the PascalCase property names below map from camelCase on the wire. The
// mirroring client-side records live in
// Tamma.Activities/LlmCall/Models/TammaApiModels.cs.
//
// NONE of these records carry a token — the API mints the per-tenant GitHub App
// installation token server-side (inside OctokitGitHubActionsClient); the engine
// holds no Actions token.
// ============================================================

/// <summary>
/// <c>POST /api/v1/agent-dispatch/{owner}/{repo}/runs</c>. The dispatch inputs
/// are composed engine-side (pure, token-free); the API validates the workflow
/// file exists and POSTs the <c>workflow_dispatch</c> with the minted
/// installation token.
/// </summary>
public sealed record DispatchAgentRunRequest
{
    /// <summary>The workflow file/id to dispatch (e.g. <c>tamma-agent.yml</c>).</summary>
    public string WorkflowFileName { get; init; } = "tamma-agent.yml";

    /// <summary>The branch/tag/sha to run against.</summary>
    public string Ref { get; init; } = string.Empty;

    /// <summary>The <c>workflow_dispatch</c> inputs (string-only, composed engine-side).</summary>
    public Dictionary<string, string> Inputs { get; init; } = new();

    /// <summary>The workflow instance / session id — ties the run to the audit trail.</summary>
    public string CorrelationId { get; init; } = string.Empty;
}

/// <summary>
/// <c>GET /api/v1/agent-dispatch/{owner}/{repo}/runs/{id}/results</c> query
/// parameters. The API aggregates the completed run's outputs (result artifact,
/// PR-for-head, base...head compare, check runs) server-side. The monitor's
/// conclusion + agent provider ride in the query so the aggregation reproduces
/// the exact same result the co-hosted collector produced.
/// </summary>
public sealed record CollectAgentRunRequest
{
    public string BranchName { get; init; } = string.Empty;
    public string Conclusion { get; init; } = string.Empty;
    public string AgentProvider { get; init; } = "claude-code";
    public int DurationSeconds { get; init; }
    public string CorrelationId { get; init; } = string.Empty;
}
