using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// CP-scoped implementation of <see cref="IPlatformApiKeyIndexRepository"/>.
/// Queries the <c>platform_api_key_index</c> table on
/// <see cref="ControlPlaneDbContext"/>.
/// </summary>
public class PlatformApiKeyIndexRepository(ControlPlaneDbContext db)
    : IPlatformApiKeyIndexRepository
{
    public async Task<PlatformApiKeyIndex> CreateAsync(PlatformApiKeyIndex row)
    {
        if (row.CreatedAt == default)
            row.CreatedAt = DateTime.UtcNow;
        db.PlatformApiKeyIndex.Add(row);
        await db.SaveChangesAsync();
        return row;
    }

    public async Task<PlatformApiKeyIndex?> GetByPrefixAsync(string keyPrefix)
        => await db.PlatformApiKeyIndex
            .FirstOrDefaultAsync(r => r.KeyPrefix == keyPrefix);

    public async Task<PlatformApiKeyIndex?> GetByPrefixAndSuffixAsync(
        string keyPrefix, string hashedSuffix)
        => await db.PlatformApiKeyIndex
            .FirstOrDefaultAsync(r =>
                r.KeyPrefix == keyPrefix && r.HashedSuffix == hashedSuffix);

    public async Task RevokeByApiKeyIdAsync(Guid apiKeyId, DateTime? revokedAt = null)
    {
        var when = revokedAt ?? DateTime.UtcNow;
        var rows = await db.PlatformApiKeyIndex
            .Where(r => r.ApiKeyId == apiKeyId && r.RevokedAt == null)
            .ToListAsync();
        foreach (var row in rows)
            row.RevokedAt = when;
        if (rows.Count > 0)
            await db.SaveChangesAsync();
    }

    public async Task DeleteByPrefixAsync(string keyPrefix)
    {
        var row = await db.PlatformApiKeyIndex
            .FirstOrDefaultAsync(r => r.KeyPrefix == keyPrefix);
        if (row is not null)
        {
            db.PlatformApiKeyIndex.Remove(row);
            await db.SaveChangesAsync();
        }
    }
}
