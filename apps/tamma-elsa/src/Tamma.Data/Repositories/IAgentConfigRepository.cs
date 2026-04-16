using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

public interface IAgentConfigRepository
{
    Task<AgentConfig?> GetAsync(Guid? tenantId);
    Task<AgentConfig> UpsertAsync(Guid? tenantId, string configJson, Guid? userId);
    Task<bool> DeleteAsync(Guid tenantId);
    Task<(AgentConfig Config, string Source)> ResolveAsync(Guid tenantId);
}
