namespace Tamma.Api.Services.Agents;

/// <summary>
/// Story 32-16 — DCB event-type constants for the per-tenant agent/persona
/// enablement surface (pattern <c>AGGREGATE.ACTION.STATUS</c>, per CLAUDE.md
/// "Event Types"). Kept here as the single source so service code never
/// hand-types a literal that could drift from the audit/projector expectations.
/// </summary>
public static class AgentEnablementEventTypes
{
    /// <summary>A principal enabled a public persona for its tenant's catalog.</summary>
    public const string Enabled = "AGENT.ENABLED.SUCCESS";

    /// <summary>A principal disabled a public persona from its tenant's catalog.</summary>
    public const string Disabled = "AGENT.DISABLED.SUCCESS";
}
