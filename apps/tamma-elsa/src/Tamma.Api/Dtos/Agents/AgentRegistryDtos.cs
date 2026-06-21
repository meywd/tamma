namespace Tamma.Api.Dtos.Agents;

// Story 32-2 — DTOs for the entity-aware registry/resolution surface
// (/api/agents). The first-class agent CRUD DTOs (CreateAgentRequest,
// PublishVersionRequest, AgentSummary, AgentDetail, ...) ship in
// AgentDtos.cs (Story 32-1) and are reused; these add only the role-selection
// + list-filter shapes 32-2 introduces.

/// <summary>
/// Filter for <c>GET /api/agents</c>. All optional; absent = no filter on that
/// dimension. Visibility/status are wire strings (<c>public</c>/<c>private</c>,
/// <c>active</c>/<c>archived</c>); role is a canonical
/// <c>RolePhaseMap.ValidRoles</c> wire string.
/// </summary>
public sealed record AgentListFilter(
    string? Role = null,
    string? Visibility = null,
    string? Status = null);

/// <summary>
/// Body for <c>PUT /api/agents/role-selections/{role}</c> — which agent (public
/// OR own private) serves the role for the calling principal.
/// </summary>
public sealed record SelectRoleRequest(Guid AgentId);

/// <summary>
/// Body for <c>POST /api/agents/{id}/rollback</c> — re-activate an EXISTING
/// prior version (repoint the active-version pointer; AC 13).
/// </summary>
public sealed record RollbackVersionRequest(int Version);

/// <summary>
/// One role→agent selection projection for <c>GET</c> responses. <c>Visibility</c>
/// is the provenance recomputed at read time.
/// </summary>
public sealed record AgentRoleSelectionResponse(
    string Role,
    Guid AgentId,
    string Visibility);
