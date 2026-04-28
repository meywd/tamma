using Tamma.Data.Abstractions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tamma.Data.Defaults;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Tenant-scoped repo; uses <see cref="ITenantDbContextFactory"/> for
/// tenant-bound reads/writes.
///
/// <para>
/// Story 28-1 PR A (Decision #1): the legacy
/// <c>agent_configs.tenant_id IS NULL</c> CP row no longer carries the
/// platform default. Reads with <c>tenantId == null</c> resolve to the
/// in-code default in <see cref="AgentConfigDefaults"/> (and downstream
/// <c>DefaultAgentConfig.ForRole</c> in Tamma.Api), matching the prompt-store
/// pattern documented in CLAUDE.md.
/// </para>
///
/// <para>
/// Writes with <c>tenantId == null</c> were previously how operators "edited
/// the platform default" via the SettingsEndpoints / AgentEndpoints surface.
/// Defaults now live in code, so those writes are no-ops with a structured
/// warning — the API surface is preserved (clients keep getting an
/// <see cref="AgentConfig"/> back) but the value never persists. To truly
/// change defaults, edit
/// <c>Tamma.Api.Services.Agents.DefaultAgentConfig.ForRole</c>.
/// </para>
/// </summary>
public class AgentConfigRepository(
    ITenantDbContextFactory tenantDbFactory,
    ILogger<AgentConfigRepository>? logger = null) : IAgentConfigRepository
{
    private readonly ILogger<AgentConfigRepository>? _logger = logger;

    public async Task<AgentConfig?> GetAsync(Guid? tenantId)
    {
        if (tenantId is Guid tid)
        {
            await using var db = await tenantDbFactory.CreateAsync(tid);
            return await db.AgentConfigs.IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.TenantId == tid);
        }
        // Story 28-1 PR A: platform default lives in code now, not in CP.
        // Returning null here (rather than the synthetic snapshot) preserves
        // the existing "no row found → caller falls back to platform-default
        // sentinel" contract used by SettingsEndpoints / AgentEndpoints.
        return null;
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

        // Story 28-1 PR A: platform default is code-resident; this write is
        // a no-op so the legacy admin endpoint surface keeps responding 200
        // (instead of breaking on a missing CP DbSet after PR D).
        _logger?.LogWarning(
            "AgentConfig.UpsertAsync called with tenantId=null — platform " +
            "defaults moved to code (DefaultAgentConfig.ForRole) per Story " +
            "28-1 Decision #1. Discarding the requested config (UpdatedBy={UserId}).",
            userId);

        return AgentConfigDefaults.Snapshot();
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

        // Story 28-1 PR A: skip the CP "system" lookup; defaults live in code.
        // The "default" branch is preserved as a stable sentinel for callers
        // (e.g. ProviderChainResolver) that still expect a non-null
        // AgentConfig back from the resolve API.
        return (AgentConfigDefaults.Snapshot(), "default");
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
            // Story 28-1 PR A: no platform-default row to read; return null
            // so the resolver layer falls through to its in-code defaults.
            row = null;
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
