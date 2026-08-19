using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Stores and verifies API keys on the control-plane <c>api_keys</c> table.
/// Epic-28 split: this row is CP-resident (see
/// <see cref="ControlPlaneDbContext.ApiKeys"/>), because platform-admin, user,
/// and service keys route through the CP before fanning out to per-tenant
/// DbContexts.
/// </summary>
public class ApiKeyRepository(ControlPlaneDbContext db) : IApiKeyRepository
{
    public async Task<ApiKey> CreateAsync(ApiKey apiKey)
    {
        apiKey.CreatedAt = DateTime.UtcNow;
        db.ApiKeys.Add(apiKey);
        await db.SaveChangesAsync();
        return apiKey;
    }

    public async Task<ApiKey?> GetByIdAsync(Guid id)
        => await db.ApiKeys.FindAsync(id);

    public async Task<ApiKey?> GetByHashAsync(string keyHash)
        => await db.ApiKeys.FirstOrDefaultAsync(k => k.KeyHash == keyHash);

    public async Task<List<ApiKey>> ListByScopeAsync(string scope)
        => await db.ApiKeys.Where(k => k.Scope == scope && k.RevokedAt == null).ToListAsync();

    /// <summary>
    /// Keys in <paramref name="scope"/> that are valid RIGHT NOW — which includes a key
    /// inside its rotation grace window, where <c>RevokedAt</c> is set but still in the
    /// future.
    ///
    /// <para>This exists because <see cref="ListByScopeAsync"/> filters
    /// <c>RevokedAt == null</c>, and it feeds the authentication candidate scan. A key's
    /// first successful auth rehashes its row to per-key-salted Argon2, after which no
    /// hash-equality lookup can find it and the prefix scan is the only path left — so a
    /// rotated-out key became invisible the moment <see cref="RotateAsync"/> stamped
    /// <c>RevokedAt = now + 24h</c>, and 401'd immediately. The documented 24-hour
    /// rolling cutover was unreachable for any key that had ever been used, which is
    /// every key that matters. The grace branch in the handler's revocation check now
    /// has candidates to act on.</para>
    ///
    /// <para>Deliberately a sibling rather than a change to
    /// <see cref="ListByScopeAsync"/>: that method also backs an admin listing, and
    /// widening it would quietly change what operators see.</para>
    /// </summary>
    public async Task<List<ApiKey>> ListValidByScopeAsync(string scope)
    {
        var now = DateTime.UtcNow;
        return await db.ApiKeys
            .Where(k => k.Scope == scope && (k.RevokedAt == null || k.RevokedAt > now))
            .ToListAsync();
    }

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

    public async Task RevokeAllByOwnerAsync(string ownerId)
    {
        var now = DateTime.UtcNow;
        // Sweep everything that can still AUTHENTICATE, not just never-revoked
        // rows — which means grace-window keys too (RevokedAt stamped in the
        // FUTURE by RotateAsync's 24h rolling cutover). This filtered
        // `RevokedAt == null` until 2026-08-19, which was harmless while a
        // future-RevokedAt key was invisible to the auth candidate scans anyway;
        // the moment ListValidByScopeAsync made the grace window real, this
        // sweep had to match it, or an admin deleting a user (AdminEndpoints
        // SoftDelete + this call) left the user's rotating key authenticating
        // for up to 24 more hours. Owner-wide revocation is the emergency stop:
        // it grants no grace.
        var keys = await db.ApiKeys
            .Where(k => k.OwnerId == ownerId && (k.RevokedAt == null || k.RevokedAt > now))
            .ToListAsync();
        foreach (var key in keys)
            key.RevokedAt = now;
        if (keys.Count > 0)
            await db.SaveChangesAsync();
    }

    public async Task<ApiKey> RotateAsync(Guid oldId, string newKeyHash, string newKeyPrefix)
    {
        var old = await db.ApiKeys.FindAsync(oldId)
            ?? throw new InvalidOperationException("API key not found");

        // 24h grace period: the old key continues to validate until this
        // moment so dependent services can roll over without an outage.
        // Matches the TS rotateApiKey behavior. Hash-lookup paths must
        // treat RevokedAt > NOW() as still-valid.
        old.RevokedAt = DateTime.UtcNow.AddHours(24);

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

    /// <summary>
    /// Story 28-7 deferred-item — rewrites <c>api_keys.KeyHash</c> in place
    /// (no new row, no <see cref="ApiKey.RotatedFromId"/> chain). Used by
    /// <c>ApiKeyAuthHandler</c> when a successful legacy-format verify hints
    /// that the row should be upgraded to Argon2id on next use.
    /// </summary>
    public async Task UpdateHashAsync(Guid id, string newKeyHash)
    {
        var key = await db.ApiKeys.FindAsync(id);
        if (key is null) return;
        key.KeyHash = newKeyHash;
        await db.SaveChangesAsync();
    }
}
