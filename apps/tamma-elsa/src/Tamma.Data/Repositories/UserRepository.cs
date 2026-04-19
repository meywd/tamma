using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

public class UserRepository(TammaDbContext db) : IUserRepository
{
    public async Task<User> CreateAsync(User user)
    {
        user.CreatedAt = DateTime.UtcNow;
        user.UpdatedAt = DateTime.UtcNow;
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    public async Task<User?> GetByIdAsync(Guid id)
        => await db.Users.FirstOrDefaultAsync(u => u.Id == id);

    public async Task<User?> GetByEmailAsync(string email)
        => await db.Users.FirstOrDefaultAsync(u => u.Email == email);

    public async Task<User?> GetByGitHubIdAsync(long githubId)
        => await db.Users.FirstOrDefaultAsync(u => u.GitHubId == githubId);

    public async Task<User?> GetByEmailVerificationTokenHashAsync(string tokenHash)
        => await db.Users.FirstOrDefaultAsync(u =>
            u.EmailVerificationTokenHash == tokenHash && u.DeletedAt == null);

    public async Task<(List<User> Users, int Total)> ListAsync(int limit, int offset, string? role)
    {
        var query = db.Users.AsQueryable();
        if (!string.IsNullOrEmpty(role))
            query = query.Where(u => u.Role == role);
        var total = await query.CountAsync();
        var users = await query.OrderByDescending(u => u.CreatedAt).Skip(offset).Take(limit).ToListAsync();
        return (users, total);
    }

    public async Task<User> UpdateAsync(User user)
    {
        user.UpdatedAt = DateTime.UtcNow;
        db.Users.Update(user);
        await db.SaveChangesAsync();
        return user;
    }

    public async Task SoftDeleteAsync(Guid id)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user is not null)
        {
            user.DeletedAt = DateTime.UtcNow;
            user.IsActive = false;
            await db.SaveChangesAsync();
        }
    }

    public async Task UpdateActiveTenantAsync(Guid userId, Guid tenantId)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is not null)
        {
            user.TenantId = tenantId;
            user.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
    }

    public async Task SetEmailVerifiedAsync(Guid id)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user is null) return;
        user.EmailVerified = true;
        user.EmailVerificationTokenHash = null;
        user.EmailVerificationExpiresAt = null;
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    public async Task UpdateVerificationTokenAsync(Guid id, string tokenHash, DateTime expiresAt)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user is null) return;
        user.EmailVerificationTokenHash = tokenHash;
        user.EmailVerificationExpiresAt = expiresAt;
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    public async Task UpdatePasswordHashAsync(Guid id, string passwordHash)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user is null) return;
        user.PasswordHash = passwordHash;
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    public async Task UpdateAuthMethodAsync(Guid id, string authMethod)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user is null) return;
        user.AuthMethod = authMethod;
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    public async Task SetGitHubIdAsync(Guid id, long githubId, string githubLogin)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user is null) return;
        user.GitHubId = githubId;
        user.GitHubLogin = githubLogin;
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    public async Task UpdateLastActiveAsync(Guid id)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user is null) return;
        user.LastActiveAt = DateTime.UtcNow;
        // Intentionally do NOT bump UpdatedAt — last-active is a soft signal.
        await db.SaveChangesAsync();
    }

    public async Task<string> GetUserSettingsAsync(Guid id)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id);
        return user?.Settings ?? "{}";
    }

    public async Task UpdateUserSettingsAsync(Guid id, string settingsJson)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user is null) return;
        user.Settings = settingsJson;
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }
}
