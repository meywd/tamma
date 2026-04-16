namespace Tamma.Api.Dtos.Agents;

public record UpdateAgentConfigRequest(object Config);
public record ValidateConfigRequest(object Config);
public record ResolveForPhaseRequest(string Phase, string TaskType);
public record AgentConfigResponse(object Config, string Source, int Version);
public record ResolvedAgentResponse(string Provider, string Model, object Config);
