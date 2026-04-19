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
}
