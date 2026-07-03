using Tamma.Core.Interfaces;

namespace Tamma.Api.Services.Integrations;

/// <summary>
/// Credential-bound JIRA HTTP client — the JIRA analog of git BYOK's
/// <c>IGitHubClientFactory</c>-minted, token-bound client. Every call takes the
/// RESOLVED per-tenant <see cref="JiraCredential"/> and threads its
/// baseUrl/email/apiToken straight into the HTTP request, instead of reading a
/// process-global config. The token never leaves this call; it is never logged
/// or surfaced on the returned <see cref="IntegrationResult{T}"/>.
/// </summary>
public interface IJiraApiClient
{
    /// <summary>Fetch a ticket using the supplied per-tenant credential.</summary>
    Task<IntegrationResult<JiraTicket?>> GetTicketAsync(
        JiraCredential credential, string ticketId, CancellationToken ct = default);

    /// <summary>Update a ticket (comment/status) using the supplied credential.</summary>
    Task<IntegrationResult<JiraTicketResult>> UpdateTicketAsync(
        JiraCredential credential, string ticketId, JiraTicketUpdate update,
        CancellationToken ct = default);
}
