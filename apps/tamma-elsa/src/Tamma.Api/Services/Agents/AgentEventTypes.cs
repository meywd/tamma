namespace Tamma.Api.Services.Agents;

/// <summary>
/// Story 32-2 — DCB event-type constants for the agent registry/resolution
/// surface (pattern <c>AGGREGATE.ACTION.STATUS</c>, per CLAUDE.md "Event
/// Types"). The lifecycle events (<c>CREATED</c>/<c>VERSION_PUBLISHED</c>/
/// <c>ARCHIVED</c>) are emitted by <c>AgentRepository</c> (Story 32-1); 32-2
/// adds the selection + resolve-failure events. Kept here as the single source
/// so endpoint/service code never hand-types a literal that could drift from
/// the audit/projector expectations.
/// </summary>
public static class AgentEventTypes
{
    /// <summary>A principal chose which agent serves a role.</summary>
    public const string SelectedForRole = "AGENT.SELECTED_FOR_ROLE.SUCCESS";

    /// <summary>No agent resolvable for a taxonomy-valid role — the fail-loud
    /// branch (AC 9). Fires even when the missing-config recorder is absent.</summary>
    public const string ResolveFailed = "AGENT.RESOLVE.FAILED";
}
