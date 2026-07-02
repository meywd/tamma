using Microsoft.AspNetCore.Http;
using Tamma.Api.Services.Jira;
using Tamma.Data;

namespace Tamma.Api.Endpoints;

/// <summary>
/// Story 38 (Phase 1) — the internal, engine-only JIRA-mediation endpoints
/// (<c>/api/v1/jira/tickets/...</c>). Same engine-only plane as <c>/api/v1/llm/call</c>
/// / <c>/api/v1/git/...</c>: <c>EngineServiceOnly</c> auth (missing/invalid bearer ⇒
/// 401; user JWT ⇒ 403), the acting tenant is the auth-derived
/// <see cref="ITenantContext"/> (X-Tenant-Id, NEVER the body). JIRA is not
/// repo-scoped, so there is no tenant↔repo guard — the tenant scopes the audit event
/// only. Delegates to <see cref="IJiraMediationService"/> and projects the typed
/// key-free result (every non-success rides inside 200 success:false — never a raw
/// 5xx).
/// </summary>
public static class JiraEndpoints
{
    public static async Task<IResult> GetTicket(
        string ticketId, string? correlationId,
        ITenantContext tenantContext, IJiraMediationService jira, CancellationToken ct)
    {
        var result = await jira.GetTicketAsync(tenantContext.TenantId, ticketId, correlationId ?? string.Empty, ct).ConfigureAwait(false);
        return result.ToHttpResult();
    }

    public static async Task<IResult> UpdateTicket(
        string ticketId, UpdateTicketRequest body,
        ITenantContext tenantContext, IJiraMediationService jira, CancellationToken ct)
    {
        var result = await jira.UpdateTicketAsync(tenantContext.TenantId, ticketId, body, ct).ConfigureAwait(false);
        return result.ToHttpResult();
    }
}
