namespace Tamma.Api.Services.Integrations;

/// <summary>
/// A resolved JIRA credential bundle — the tuple threaded into the JIRA HTTP
/// client per request (the JIRA analog of git BYOK's resolved token). The
/// <see cref="ApiToken"/> is request-scoped: it is NEVER logged, emitted onto a
/// DCB event, or echoed on any HTTP response.
/// </summary>
/// <param name="BaseUrl">The JIRA Cloud/Server base URL (e.g.
/// <c>https://acme.atlassian.net</c>).</param>
/// <param name="Email">The account email used for JIRA basic auth.</param>
/// <param name="ApiToken">The JIRA API token (basic-auth password). Secret.</param>
public sealed record JiraCredential(string BaseUrl, string Email, string ApiToken);

/// <summary>
/// A resolved <see cref="JiraCredential"/> plus the tier that answered
/// (tenant BYOK vs single-user system config). Mirrors git BYOK's
/// <c>GitTokenResolution</c>.
/// </summary>
/// <param name="Credential">The resolved bundle (never null here — a null
/// resolution is represented by the resolver returning <c>null</c>).</param>
/// <param name="Source">The tier that answered.</param>
public sealed record JiraCredentialResolution(
    JiraCredential Credential, IntegrationCredentialSource Source);
