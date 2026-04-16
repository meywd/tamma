using System.Text.Json;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

public interface IAgentConfigRepository
{
    Task<AgentConfig?> GetAsync(Guid? tenantId);
    Task<AgentConfig> UpsertAsync(Guid? tenantId, string configJson, Guid? userId);
    Task<bool> DeleteAsync(Guid tenantId);
    Task<(AgentConfig Config, string Source)> ResolveAsync(Guid tenantId);

    /// <summary>
    /// Get the raw tenant-scoped agent config JSON as a parsed
    /// <see cref="JsonDocument"/>. Returns <c>null</c> when no override
    /// exists for the tenant (callers fall back to platform defaults).
    ///
    /// The returned <see cref="JsonDocument"/> is owned by the caller and
    /// must be disposed; for service-lifetime consumers the EF context will
    /// tear it down on scope disposal if unclaimed.
    /// </summary>
    Task<JsonDocument?> GetTenantConfigAsync(Guid? tenantId);

    /// <summary>
    /// Upsert a tenant-scoped agent config from a <see cref="JsonDocument"/>,
    /// bumping the <c>version</c> counter on update.
    /// </summary>
    Task<AgentConfig> UpdateTenantConfigAsync(
        Guid tenantId, JsonDocument config, Guid? userId = null);
}
