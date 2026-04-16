namespace Tamma.Api.Dtos.Agents;

public record UpdateAgentConfigRequest(object Config);
public record ValidateConfigRequest(object Config);

/// <summary>
/// Resolve-for-phase request body. Accepts either <c>role</c> (new shape)
/// or <c>taskType</c> (legacy). One must be provided.
/// </summary>
public record ResolveForPhaseRequest(string Phase, string TaskType = "", string? Role = null);

public record AgentConfigResponse(object Config, string Source, int Version);
public record ResolvedAgentResponse(string Provider, string Model, object Config);
