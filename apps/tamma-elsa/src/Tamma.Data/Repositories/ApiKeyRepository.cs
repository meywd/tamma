using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

public class ApiKeyRepository(TammaDbContext db) : IApiKeyRepository
{
    public async Task<ApiKey> CreateAsync(ApiKey apiKey)
    {
        apiKey.CreatedAt = DateTime.UtcNow;
        db.ApiKeys.Add(apiKey);
        await db.SaveChangesAsync();
        return apiKey;
    }

    public async Task<ApiKey?> GetByHashAsync(string keyHash)
        => await db.ApiKeys.FirstOrDefaultAsync(k => k.KeyHash == keyHash);

    public async Task<List<ApiKey>> ListByScopeAsync(string scope)
        => await db.ApiKeys.Where(k => k.Scope == scope && k.RevokedAt == null).ToListAsync();

    public async Task<List<ApiKey>> ListByOwnerAsync(string ownerId)
        => await db.ApiKeys.Where(k => k.OwnerId == ownerId && k.RevokedAt == null).ToListAsync();

    public async Task RevokeAsync(Guid id)
    {
        var key = await db.ApiKeys.FindAsync(id);
        if (key is not null)
        {
            key.RevokedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
    }

    public async Task<ApiKey> RotateAsync(Guid oldId, string newKeyHash, string newKeyPrefix)
    {
        var old = await db.ApiKeys.FindAsync(oldId)
            ?? throw new InvalidOperationException("API key not found");
        old.RevokedAt = DateTime.UtcNow;

        var newKey = new ApiKey
        {
            Scope = old.Scope,
            OwnerId = old.OwnerId,
            KeyHash = newKeyHash,
            KeyPrefix = newKeyPrefix,
            Label = old.Label,
            Permissions = old.Permissions,
            TenantId = old.TenantId,
            CreatedAt = DateTime.UtcNow,
            RotatedFromId = old.Id
        };
        db.ApiKeys.Add(newKey);
        await db.SaveChangesAsync();
        return newKey;
    }

    public async Task UpdateLastUsedAsync(Guid id)
    {
        var key = await db.ApiKeys.FindAsync(id);
        if (key is not null)
        {
            key.LastUsedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
    }
}
