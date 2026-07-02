namespace Tamma.Api.Services.Jira;

/// <summary>
/// Story 38 (Phase 1) — the managed JIRA execution layer behind the
/// <c>/api/v1/jira/tickets/...</c> endpoints. Runs the JIRA call server-side (the
/// credential lives in Tamma.Api config, resolved inside
/// <c>IJiraIntegrationService</c>) under the caller's tenant context, then emits
/// exactly one terminal DCB audit event. ALWAYS returns a typed, key-free
/// <see cref="JiraMediationResult"/> — a failure never throws a raw 5xx. JIRA is not
/// repo-scoped, so there is no cross-tenant repo guard.
/// </summary>
public interface IJiraMediationService
{
    Task<JiraMediationResult> GetTicketAsync(Guid? tenantId, string ticketId, string correlationId, CancellationToken ct = default);
    Task<JiraMediationResult> UpdateTicketAsync(Guid? tenantId, string ticketId, UpdateTicketRequest body, CancellationToken ct = default);
}
