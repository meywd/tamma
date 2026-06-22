namespace Tamma.Api.Services.Agents;

/// <summary>
/// Story 32-5 (AC8) — DCB event-type constants for a managed run, emitted from
/// <c>Tamma.Api</c> (where the tenant store + cabinet live) via the tenant
/// <c>IEventRepository</c>. Pattern <c>AGGREGATE.ACTION.STATUS</c> (per CLAUDE.md
/// "Event Types"), matching the neighbouring <see cref="AgentEventTypes"/> /
/// <see cref="AgentEnablementEventTypes"/> convention. Kept here as the single
/// source so composition/endpoint code never hand-types a literal that could
/// drift from the audit/projector expectations.
///
/// <para><see cref="Started"/> fires once before the loop; exactly one terminal
/// <see cref="Success"/> or <see cref="Failed"/> fires after. All are tagged
/// <c>{ agentId, version, provider, model, role, correlationId, credentialSource,
/// tenantId }</c>; <see cref="Failed"/> additionally tags <c>failureCode</c>.</para>
/// </summary>
public static class AgentRunEventTypes
{
    /// <summary>A managed run began (emitted before the tool loop).</summary>
    public const string Started = "AGENT.RUN.STARTED";

    /// <summary>A managed run produced a usable response (terminal).</summary>
    public const string Success = "AGENT.RUN.SUCCESS";

    /// <summary>A managed run failed (terminal); additionally tags
    /// <c>failureCode</c>.</summary>
    public const string Failed = "AGENT.RUN.FAILED";
}
