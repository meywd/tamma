using Microsoft.AspNetCore.Http;

namespace Tamma.Api.Services.Jira;

/// <summary>
/// Story 38 (Phase 1) — the single normalized, KEY-FREE result the JIRA endpoints
/// return. The JIRA credential NEVER appears here (nor in any log or DCB event). The
/// mirroring engine-side wire type is
/// <c>Tamma.Activities.LlmCall.Models.JiraCallResponse</c>.
/// </summary>
public sealed record JiraMediationResult
{
    public bool Success { get; init; }

    /// <summary>Operation-specific outcome ("Read" / "Updated" / "Error").</summary>
    public string? Outcome { get; init; }

    // ── ticket read ──
    public JiraTicketDto? Ticket { get; init; }

    // ── ticket update ──
    public string? TicketKey { get; init; }

    // ── failure-only (key-free) ──
    public string? FailureCode { get; init; }
    public string? FailureReason { get; init; }

    public string? CorrelationId { get; init; }
}

/// <summary>A key-free JIRA ticket projection. Mirrors <c>Core.Interfaces.JiraTicket</c>.</summary>
public sealed record JiraTicketDto
{
    public string Id { get; init; } = string.Empty;
    public string Key { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string Status { get; init; } = string.Empty;
    public string? Assignee { get; init; }
    public string? Priority { get; init; }
    public IReadOnlyList<string> Labels { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Story 38 (Phase 1) — the HTTP-status decision. JIRA is not repo-scoped and has no
/// per-tenant token resolver, so every non-success rides inside a 200 success:false
/// envelope (the workflow branches on the outcome). A raw 5xx is NEVER produced.
/// </summary>
public static class JiraMediationResultExtensions
{
    public static IResult ToHttpResult(this JiraMediationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return Results.Ok(result);
    }
}
