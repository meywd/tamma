namespace Tamma.Api.Services.Ci;

/// <summary>
/// Story 38 (Phase 1) — the terminal DCB event families the CI-mediation endpoints
/// emit (exactly one per call). Naming mirrors the Story 38-1 <c>GIT.*</c>
/// convention (<c>AGGREGATE.ACTION.STATUS</c>). Payloads + tags are KEY-FREE — they
/// reference the repo + branch, never the resolved git token or any Authorization
/// header.
/// </summary>
public static class CiEventTypes
{
    public const string TestsTriggerOperation = "tests_trigger";
    public const string BuildStatusReadOperation = "build_status_read";

    public const string TestsTriggeredSuccess = "CI.TESTS_TRIGGERED.SUCCESS";
    public const string TestsTriggeredFailed = "CI.TESTS_TRIGGERED.FAILED";

    public const string BuildStatusReadSuccess = "CI.BUILD_STATUS_READ.SUCCESS";
    public const string BuildStatusReadFailed = "CI.BUILD_STATUS_READ.FAILED";
}

/// <summary>
/// Story 38 (Phase 1) — the coarse, key-free CI failure taxonomy surfaced on the
/// wire so the workflow can branch on the outcome. Never a raw provider 5xx.
/// </summary>
public static class CiFailureCodes
{
    /// <summary>The tenant↔repo cross-tenant guard denied. Platform never called.</summary>
    public const string RepoNotAuthorized = "REPO_NOT_AUTHORIZED";

    /// <summary>The per-tenant git token could not be resolved (fail-closed).</summary>
    public const string TokenUnavailable = "CI_TOKEN_UNAVAILABLE";

    /// <summary>Any other expected platform failure (permission, rate-limit, transient).</summary>
    public const string PlatformError = "PLATFORM_ERROR";
}
