using Microsoft.AspNetCore.Http;

namespace Tamma.Api.Services.AgentDispatch;

// ============================================================
// Story 38-2 (AC4/AC6) — the normalized, KEY-FREE result records the
// agent-dispatch endpoints return. Only CredentialSource (the constant label
// "installation") is ever present; the minted installation token NEVER appears
// here (nor in any log or DCB event). The mirroring engine-side wire types live
// in Tamma.Activities/LlmCall/Models/TammaApiModels.cs.
//
// The HTTP-status decision (ToHttpResult) is shared across all three run
// operations:
//   success                       -> 200
//   expected platform failure     -> 200 success:false (preserved platformStatusCode)
//   REPO_NOT_AUTHORIZED (guard)   -> 403 (fail-closed; platform never called)
// There is NO 503 token path: the installation token is minted internally, so a
// missing/unresolvable installation is an expected platform failure
// (ACTIONS_NOT_CONFIGURED) that rides inside 200 success:false. A raw 5xx is
// NEVER produced.
// ============================================================

/// <summary>Common shape shared by the three run-operation results so the HTTP
/// mapper can project any of them uniformly.</summary>
public interface IAgentDispatchResult
{
    bool Success { get; }
    string? FailureCode { get; }
}

/// <summary>Result of <c>POST .../runs</c> — a triggered <c>workflow_dispatch</c>.</summary>
public sealed record AgentDispatchRunResult : IAgentDispatchResult
{
    public bool Success { get; init; }
    public string? CredentialSource { get; init; }
    public string? WorkflowRunUrl { get; init; }
    public DateTime DispatchedAt { get; init; }
    public string? FailureCode { get; init; }
    public string? FailureReason { get; init; }
    public int? PlatformStatusCode { get; init; }
    public string? CorrelationId { get; init; }
}

/// <summary>Result of <c>GET .../runs</c> (discover) and <c>GET .../runs/{id}</c>
/// (poll). <see cref="Found"/> is false when the platform is reachable but the run
/// isn't visible yet (discovery still searching / run disappeared) — that is a
/// SUCCESSFUL poll (200) the monitor treats as "keep waiting", NOT a failure.</summary>
public sealed record AgentRunStatusResult : IAgentDispatchResult
{
    public bool Success { get; init; }
    public string? CredentialSource { get; init; }

    /// <summary>Whether a matching run was returned. False ⇒ no run yet (still 200/success).</summary>
    public bool Found { get; init; }

    public long? RunId { get; init; }
    public string? Status { get; init; }
    public string? Conclusion { get; init; }
    public string? WorkflowRunUrl { get; init; }
    public string? HeadBranch { get; init; }
    public DateTime? CreatedAt { get; init; }
    public DateTime? UpdatedAt { get; init; }
    public string? ArtifactsUrl { get; init; }

    public string? FailureCode { get; init; }
    public string? FailureReason { get; init; }
    public int? PlatformStatusCode { get; init; }
    public string? CorrelationId { get; init; }
}

/// <summary>Result of <c>GET .../runs/{id}/results</c> — the aggregated outputs of
/// a completed run. <see cref="Success"/> is the MEDIATION success (guard passed +
/// aggregation ran without throwing); <see cref="AgentSuccess"/> is the agent's
/// own task success (an agent can fail while collection succeeds).</summary>
public sealed record AgentRunResultsResult : IAgentDispatchResult
{
    public bool Success { get; init; }
    public string? CredentialSource { get; init; }

    public bool AgentSuccess { get; init; }
    public int? PrNumber { get; init; }
    public string? PrUrl { get; init; }
    public string CommitSha { get; init; } = string.Empty;
    public IReadOnlyList<string> FilesChanged { get; init; } = Array.Empty<string>();
    public int CommitsCount { get; init; }
    public bool? ChecksPassed { get; init; }
    public int TokensUsed { get; init; }
    public int DurationSeconds { get; init; }
    public string? ErrorMessage { get; init; }
    public string? AgentLogSummary { get; init; }
    public string? AgentProvider { get; init; }
    public string? AgentVersion { get; init; }

    public string? FailureCode { get; init; }
    public string? FailureReason { get; init; }
    public int? PlatformStatusCode { get; init; }
    public string? CorrelationId { get; init; }
}

/// <summary>Result of <c>GET .../installation</c> — resolves the GitHub App
/// installation id owning the repo, used ONLY to scope the inbound webhook-signal
/// wait key (finding 5). The id is not a secret; the guard still runs first.</summary>
public sealed record AgentInstallationResult : IAgentDispatchResult
{
    public bool Success { get; init; }
    public long? InstallationId { get; init; }
    public string? FailureCode { get; init; }
    public string? FailureReason { get; init; }
    public string? CorrelationId { get; init; }
}

/// <summary>
/// Story 38-2 (AC6) — the shared HTTP-status decision. A raw 5xx is NEVER
/// produced; expected platform failures ride inside 200 success:false so the
/// dispatch workflow branches on the outcome. Only the cross-tenant guard denial
/// (<c>REPO_NOT_AUTHORIZED</c>) maps to a non-200 (403, fail-closed).
/// </summary>
public static class AgentDispatchResultExtensions
{
    public static IResult ToHttpResult<T>(this T result) where T : IAgentDispatchResult
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Success)
        {
            return Results.Ok(result);
        }

        return result.FailureCode switch
        {
            AgentDispatchFailureCodes.RepoNotAuthorized =>
                Results.Json(result, statusCode: StatusCodes.Status403Forbidden),
            // Expected platform failures (workflow/run not found, dispatch rejected,
            // actions-not-configured, platform error) ride inside 200 success:false so
            // the dispatch workflow can branch on the outcome (preserved platformStatusCode).
            _ => Results.Ok(result),
        };
    }
}
