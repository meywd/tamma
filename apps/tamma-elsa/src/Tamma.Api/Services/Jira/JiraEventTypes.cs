namespace Tamma.Api.Services.Jira;

/// <summary>
/// Story 38 (Phase 1) — the terminal DCB event families the JIRA-mediation
/// endpoints emit (exactly one per call). Naming mirrors the Story 38-1 <c>GIT.*</c>
/// convention. Payloads + tags are KEY-FREE — they reference the ticket id, never
/// the JIRA API token / basic-auth header.
/// </summary>
public static class JiraEventTypes
{
    public const string TicketReadOperation = "ticket_read";
    public const string TicketUpdateOperation = "ticket_update";

    public const string TicketReadSuccess = "JIRA.TICKET_READ.SUCCESS";
    public const string TicketReadFailed = "JIRA.TICKET_READ.FAILED";

    public const string TicketUpdatedSuccess = "JIRA.TICKET_UPDATED.SUCCESS";
    public const string TicketUpdatedFailed = "JIRA.TICKET_UPDATED.FAILED";
}

/// <summary>
/// Story 38 (Phase 1) — the coarse, key-free JIRA failure taxonomy surfaced on the
/// wire so the workflow can branch on the outcome. Never a raw provider 5xx.
/// </summary>
public static class JiraFailureCodes
{
    /// <summary>JIRA is not configured on the Tamma server (no base URL / creds).</summary>
    public const string NotConfigured = "JIRA_NOT_CONFIGURED";

    /// <summary>The referenced ticket was not found.</summary>
    public const string NotFound = "NOT_FOUND";

    /// <summary>
    /// Per-tenant BYOK fail-loud: no JIRA credential resolved for the acting
    /// tenant (no tenant cabinet bundle in SaaS, and no single-user <c>Jira:*</c>
    /// config). The credential-bound JIRA client is
    /// NEVER called — the mediation fails loud rather than silently using a
    /// shared platform default. The tenant registers its own credential via
    /// <c>POST /api/v1/integrations/jira/credential</c>.
    /// </summary>
    public const string CredentialUnavailable = "JIRA_CREDENTIAL_UNAVAILABLE";

    /// <summary>Any other expected platform failure (permission, rate-limit, transient).</summary>
    public const string PlatformError = "PLATFORM_ERROR";
}
