namespace Tamma.Api.Services.Jira;

// ============================================================
// Story 38 (Phase 1) — server-side binding records for the JIRA-mediation
// endpoints. Bound from the engine client's camelCase JSON. JIRA is not
// repo-scoped (like Slack) — there is no tenant↔repo guard; the acting tenant
// scopes the audit event only. The JIRA credential lives in Tamma.Api config; the
// engine holds nothing.
// ============================================================

/// <summary>
/// <c>PATCH /api/v1/jira/tickets/{ticketId}</c>. The status/comment are composed
/// engine-side; the API applies them with the server-side JIRA credential.
/// </summary>
public sealed record UpdateTicketRequest
{
    public string? Status { get; init; }
    public string? Comment { get; init; }
    public Dictionary<string, object>? CustomFields { get; init; }
    public string CorrelationId { get; init; } = string.Empty;
}
