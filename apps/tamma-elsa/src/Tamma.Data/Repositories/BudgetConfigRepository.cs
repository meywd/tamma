using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IBudgetConfigRepository"/>. Backs the
/// Postgres-persisted budget overrides (audit finding providers/005 follow-up).
/// </summary>
public class BudgetConfigRepository(TammaDbContext db) : IBudgetConfigRepository
{
    public async Task<BudgetConfig?> GetAsync(Guid? tenantId, string accountId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        return await db.BudgetConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(
                b => b.TenantId == tenantId && b.AccountId == accountId,
                ct);
    }

    public async Task<BudgetConfig> UpsertAsync(BudgetConfig config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.AccountId);

        var existing = await db.BudgetConfigs
            .FirstOrDefaultAsync(
                b => b.TenantId == config.TenantId && b.AccountId == config.AccountId,
                ct);

        if (existing is null)
        {
            var row = new BudgetConfig
            {
                // Id + CreatedAt + UpdatedAt are populated by Postgres defaults.
                TenantId = config.TenantId,
                AccountId = config.AccountId,
                LimitUsd = config.LimitUsd,
                AlertThreshold = config.AlertThreshold,
                PeriodDays = config.PeriodDays,
            };
            db.BudgetConfigs.Add(row);
            await db.SaveChangesAsync(ct);
            return row;
        }

        existing.LimitUsd = config.LimitUsd;
        existing.AlertThreshold = config.AlertThreshold;
        existing.PeriodDays = config.PeriodDays;
        existing.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task<bool> DeleteAsync(Guid? tenantId, string accountId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        var existing = await db.BudgetConfigs
            .FirstOrDefaultAsync(
                b => b.TenantId == tenantId && b.AccountId == accountId,
                ct);
        if (existing is null) return false;
        db.BudgetConfigs.Remove(existing);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
