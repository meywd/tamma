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

    /// <summary>
    /// Story 32-18 — a selection of a public persona was BLOCKED because the
    /// persona is not enabled for the principal's catalog (per-tenant enablement
    /// gate, 32-16). Maps to 409 <c>agent_not_enabled</c> at the endpoint. Tags
    /// <c>{ agentId, personaName, role, mode, tenantId|userId }</c>.
    /// </summary>
    public const string SelectNotEnabled = "AGENT.SELECT.NOT_ENABLED";

    /// <summary>
    /// Story 32-18 — a stored selection pointed at a persona the principal has
    /// since DISABLED; resolution degraded to the enabled default rather than
    /// resolving the disabled persona. WARN-level audit event. Tags
    /// <c>{ role, staleAgentId, fallbackSource, mode }</c>.
    /// </summary>
    public const string ResolveDegraded = "AGENT.RESOLVE.DEGRADED";

    /// <summary>
    /// Story 32-18 — the principal has enabled NO persona (and has no own-private
    /// agent for the role), so there is no enabled default to resolve. The
    /// fail-loud code carried by the <see cref="Tamma.Core.TammaError"/> the
    /// resolver throws on this branch (alongside the mandatory
    /// <see cref="ResolveFailed"/> audit event).
    /// </summary>
    public const string NoEnabledDefault = "AGENT.RESOLVE.NO_ENABLED_DEFAULT";
}
