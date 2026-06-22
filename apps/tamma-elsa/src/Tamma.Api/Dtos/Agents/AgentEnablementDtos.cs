using Tamma.Api.Services.Agents;

namespace Tamma.Api.Dtos.Agents;

// Story 32-16 — request/response DTOs for the per-tenant agent/persona
// enablement surface (/api/agents/.../enablement). Enablement = catalog
// membership (which PUBLIC personas a tenant exposes). Distinct from the 32-2
// role-selection DTOs in AgentRegistryDtos.cs.

/// <summary>
/// Body for <c>PUT /api/agents/{agentId}/enablement</c> — enable
/// (<c>true</c>) or disable (<c>false</c>) a public persona for the calling
/// principal's catalog.
/// </summary>
public sealed record SetEnablementRequest(bool Enabled);

/// <summary>
/// One enablement-catalog projection for <c>GET</c>/<c>PUT</c> responses.
/// <see cref="ImplicitlyEnabled"/> is true for own-private agents (cannot be
/// toggled here). <see cref="PersonaName"/> is the persona/agent handle.
/// </summary>
public sealed record AgentEnablementResponse(
    Guid AgentId,
    string? PersonaName,
    bool Enabled,
    bool ImplicitlyEnabled)
{
    /// <summary>Project an <see cref="AgentEnablementState"/> onto the wire DTO.</summary>
    public static AgentEnablementResponse From(AgentEnablementState state) => new(
        state.AgentId, state.PersonaName, state.Enabled, state.ImplicitlyEnabled);
}
