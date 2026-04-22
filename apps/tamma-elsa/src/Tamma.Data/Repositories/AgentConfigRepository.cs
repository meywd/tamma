using Tamma.Data.Abstractions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Tenant-scoped repo; uses <see cref="ITenantDbContextFactory"/> for
/// tenant-bound reads/writes. Platform-default rows (<c>TenantId IS NULL</c>
/// — the "system default" agent config row) are accessed through
/// <see cref="ControlPlaneDbContext"/> since they are cross-tenant by
/// definition and the tenant factory requires a tenant id.
/// </summary>
public class AgentConfigRepository(
    ITenantDbContextFactory tenantDbFactory,
    ControlPlaneDbContext cp) : IAgentConfigRepository
{
    public async Task<AgentConfig?> GetAsync(Guid? tenantId)
    {
        if (tenantId is Guid tid)
        {
            await using var db = await tenantDbFactory.CreateAsync(tid);
            return await db.AgentConfigs.IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.TenantId == tid);
        }
        // Platform-default row lives in the CP plane.
        return await cp.AgentConfigs.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.TenantId == null);
    }

    public async Task<AgentConfig> UpsertAsync(Guid? tenantId, string configJson, Guid? userId)
    {
        if (tenantId is Guid tid)
        {
            await using var db = await tenantDbFactory.CreateAsync(tid);
            var existing = await db.AgentConfigs.IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.TenantId == tid);
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
                TenantId = tid,
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
        else
        {
            // Platform default (TenantId == null) — CP plane.
            var existing = await cp.AgentConfigs.IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.TenantId == null);
            if (existing is not null)
            {
                existing.Config = configJson;
                existing.Version++;
                existing.UpdatedAt = DateTime.UtcNow;
                existing.UpdatedBy = userId;
                await cp.SaveChangesAsync();
                return existing;
            }
            var config = new AgentConfig
            {
                TenantId = null,
                Config = configJson,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                CreatedBy = userId,
                UpdatedBy = userId
            };
            cp.AgentConfigs.Add(config);
            await cp.SaveChangesAsync();
            return config;
        }
    }

    public async Task<bool> DeleteAsync(Guid tenantId)
    {
        await using var db = await tenantDbFactory.CreateAsync(tenantId);
        var config = await db.AgentConfigs.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId);
        if (config is null) return false;
        db.AgentConfigs.Remove(config);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<(AgentConfig Config, string Source)> ResolveAsync(Guid tenantId)
    {
        await using (var db = await tenantDbFactory.CreateAsync(tenantId))
        {
            var config = await db.AgentConfigs.IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.TenantId == tenantId);
            if (config is not null)
                return (config, "tenant");
        }

        var systemConfig = await cp.AgentConfigs.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.TenantId == null);
        if (systemConfig is not null)
            return (systemConfig, "system");

        return (new AgentConfig { Config = "{}" }, "default");
    }

    /// <inheritdoc />
    public async Task<JsonDocument?> GetTenantConfigAsync(Guid? tenantId)
    {
        AgentConfig? row;
        if (tenantId is Guid tid)
        {
            await using var db = await tenantDbFactory.CreateAsync(tid);
            row = await db.AgentConfigs.IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.TenantId == tid);
        }
        else
        {
            row = await cp.AgentConfigs.IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.TenantId == null);
        }
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
