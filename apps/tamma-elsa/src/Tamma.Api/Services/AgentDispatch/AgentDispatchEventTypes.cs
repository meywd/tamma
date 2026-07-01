namespace Tamma.Api.Services.AgentDispatch;

/// <summary>
/// Story 38-2 (AC7) — the terminal DCB event families the agent-dispatch
/// mediation endpoints emit (exactly one per endpoint call). Naming mirrors the
/// Story 38-1 <c>GIT.*</c> / Story 32-5 <c>AGENT.RUN.*</c> convention
/// (<c>AGGREGATE.ACTION.STATUS</c>). Payloads + tags are KEY-FREE — they
/// reference the repo + run id, never the resolved GitHub Actions installation
/// token or any Authorization header. Distinct from the inbound
/// <c>WebhookSignalRegistry</c> signalling (which this story leaves unchanged).
/// </summary>
public static class AgentDispatchEventTypes
{
    // Operation labels (the `operation` tag).
    public const string RunTriggerOperation = "run_trigger";
    public const string RunDiscoverOperation = "run_discover";
    public const string RunPollOperation = "run_poll";
    public const string ResultsCollectOperation = "results_collect";

    public const string RunTriggeredSuccess = "AGENT_DISPATCH.RUN_TRIGGERED.SUCCESS";
    public const string RunTriggeredFailed = "AGENT_DISPATCH.RUN_TRIGGERED.FAILED";

    public const string RunPolledSuccess = "AGENT_DISPATCH.RUN_POLLED.SUCCESS";
    public const string RunPolledFailed = "AGENT_DISPATCH.RUN_POLLED.FAILED";

    public const string ResultsCollectedSuccess = "AGENT_DISPATCH.RESULTS_COLLECTED.SUCCESS";
    public const string ResultsCollectedFailed = "AGENT_DISPATCH.RESULTS_COLLECTED.FAILED";
}

/// <summary>
/// Story 38-2 (AC6) — the coarse, key-free failure taxonomy surfaced on the wire
/// so the dispatch workflow can branch on the outcome exactly the way it does
/// today. Never a raw provider 5xx.
/// </summary>
public static class AgentDispatchFailureCodes
{
    /// <summary>The tenant↔repo cross-tenant guard denied (AC2). Platform never called.</summary>
    public const string RepoNotAuthorized = "REPO_NOT_AUTHORIZED";

    /// <summary>The workflow file was not found in the repo.</summary>
    public const string WorkflowNotFound = "WORKFLOW_NOT_FOUND";

    /// <summary>The referenced workflow run was not found.</summary>
    public const string RunNotFound = "RUN_NOT_FOUND";

    /// <summary>The dispatch was rejected by the platform (404 branch/workflow, 403 permission).</summary>
    public const string DispatchRejected = "DISPATCH_REJECTED";

    /// <summary>The GitHub App / installation is not configured for this repo — no Actions
    /// token can be minted. Distinct from a cross-tenant guard denial: the tenant may own
    /// the repo, but the platform integration is absent. Rides inside 200 success:false so
    /// the workflow branches (fail-closed, never a call with an empty token).</summary>
    public const string ActionsNotConfigured = "ACTIONS_NOT_CONFIGURED";

    /// <summary>Any other expected platform failure (permission, rate-limit, transient).</summary>
    public const string PlatformError = "PLATFORM_ERROR";
}

/// <summary>
/// The credential-source LABEL surfaced on the audit event + response — never the
/// token itself (AC3). Unlike the git story (BYOK→platform), the GitHub Actions
/// token is a GitHub App INSTALLATION token minted internally by
/// <c>OctokitGitHubActionsClient</c> from the repo's installation — so the source
/// is always <c>installation</c> (the guard already asserts the installation
/// belongs to the acting tenant, preventing cross-tenant token use).
/// </summary>
public static class AgentDispatchCredentialSources
{
    public const string Installation = "installation";
}
