using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

public class AgentConfigRepository(TammaDbContext db) : IAgentConfigRepository
{
    public async Task<AgentConfig?> GetAsync(Guid? tenantId)
        => await db.AgentConfigs.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId);

    public async Task<AgentConfig> UpsertAsync(Guid? tenantId, string configJson, Guid? userId)
    {
        var existing = await db.AgentConfigs.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId);
        if (existing is not null)
        {
            existing.Config = configJson;
            existing.Version++;
            existing.UpdatedAt = DateTime.UtcNow;
            existing.UpdatedBy = userId;
            await db.SaveChangesAsync();
            return existing;
        }
        var config = new AgentConfig
        {
            TenantId = tenantId,
            Config = configJson,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            CreatedBy = userId,
            UpdatedBy = userId
        };
        db.AgentConfigs.Add(config);
        await db.SaveChangesAsync();
        return config;
    }

    public async Task<bool> DeleteAsync(Guid tenantId)
    {
        var config = await db.AgentConfigs.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId);
        if (config is null) return false;
        db.AgentConfigs.Remove(config);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<(AgentConfig Config, string Source)> ResolveAsync(Guid tenantId)
    {
        // Try tenant-specific first
        var config = await db.AgentConfigs.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId);
        if (config is not null)
            return (config, "tenant");

        // Fall back to system default
        config = await db.AgentConfigs.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.TenantId == null);
        if (config is not null)
            return (config, "system");

        // Return empty default
        return (new AgentConfig { Config = "{}" }, "default");
    }

    /// <inheritdoc />
    public async Task<JsonDocument?> GetTenantConfigAsync(Guid? tenantId)
    {
        var row = await db.AgentConfigs.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId);
        if (row is null || string.IsNullOrWhiteSpace(row.Config))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(row.Config);
        }
        catch (JsonException)
        {
            // Corrupt JSON in DB — treat as "no override" and let the resolver
            // fall back to platform defaults.
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<AgentConfig> UpdateTenantConfigAsync(
        Guid tenantId, JsonDocument config, Guid? userId = null)
    {
        var json = JsonSerializer.Serialize(config.RootElement);
        return await UpsertAsync(tenantId, json, userId);
    }
}
